using System.Windows.Media;
using System.Windows.Media.Media3D;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Geometry;
using ColourRgb = BIMDesigner.Core.Materials.ColourRgb;
using CorePoint3D = BIMDesigner.Core.Geometry.Point3D;
using Mesh3D = BIMDesigner.Core.Geometry.Mesh3D;

namespace BIMDesigner.UI.Controls;

/// <summary>
/// The analytical model in 3D, over the building ghosted behind it: each member a line - orange -
/// each plate a pane of colour with its edges, blue for walls, green for floors and roofs, brown on
/// the ground; each node a small block, each support a green pyramid under its node or a green
/// strip along the foot of its wall.
/// </summary>
public partial class ModelView
{
    private AnalyticalModel? _analytical;

    /// <summary>The analytical model shown, or none.</summary>
    public AnalyticalModel? Analytical
    {
        get => _analytical;
        set
        {
            _analytical = value;
            Compose();
        }
    }

    private static readonly Color MemberColour = Color.FromRgb(0xE8, 0x59, 0x0C);
    private static readonly Color SupportColour = Color.FromRgb(0x2B, 0x8A, 0x3E);

    private static Color PanelColour(AnalyticalPanelKind kind) => kind switch
    {
        AnalyticalPanelKind.Wall => Color.FromRgb(0x19, 0x71, 0xC2),
        AnalyticalPanelKind.Raft or AnalyticalPanelKind.GroundSlab => Color.FromRgb(0x9C, 0x6B, 0x30),
        _ => Color.FromRgb(0x2F, 0x9E, 0x44)
    };

    /// <summary>The building drawn faint behind the analytical model.</summary>
    private static Material Ghost(Mesh3D mesh)
    {
        var brush = new SolidColorBrush(Color.FromArgb(0x14, mesh.Colour.R, mesh.Colour.G, mesh.Colour.B));
        brush.Freeze();
        var material = new DiffuseMaterial(brush);
        material.Freeze();
        return material;
    }

    private static Material Lit(Color colour, byte alpha)
    {
        var brush = new SolidColorBrush(Color.FromArgb(alpha, colour.R, colour.G, colour.B));
        brush.Freeze();
        var material = new MaterialGroup { Children = { new DiffuseMaterial(brush), new EmissiveMaterial(new SolidColorBrush(Color.FromArgb((byte)(alpha / 3), colour.R, colour.G, colour.B))) } };
        material.Freeze();
        return material;
    }

    /// <summary>The plates, nodes and supports - what has faces - to go in after everything solid.</summary>
    private Model3DGroup AnalyticalScene()
    {
        var group = new Model3DGroup();
        if (_analytical is not { } model) return group;

        foreach (var panel in model.Panels.Where(panel => panel.Outline.Count >= 3))
        {
            var mesh = new Mesh3D(panel.Element.Id, panel.Element.LevelId, MeshKind.Wall, new ColourRgb(0, 0, 0), panel.Name);
            var polygon = panel.Outline.Select(node => node.At).ToList();
            var triangles = Polygon2D.Triangulate(panel.FlatOutline);
            var middle = polygon[0];
            mesh.AddFace(polygon, triangles, new CorePoint3D(middle.X + panel.Normal.X * 1000, middle.Y + panel.Normal.Y * 1000, middle.Z + panel.Normal.Z * 1000));
            if (mesh.IsEmpty) continue;
            var material = Lit(PanelColour(panel.Kind), 0x58);
            group.Children.Add(new GeometryModel3D(ToGeometry(mesh), material) { BackMaterial = material });
        }

        var blocks = new Mesh3D(Guid.Empty, Guid.Empty, MeshKind.Beam, new ColourRgb(0, 0, 0), "Nodes");
        foreach (var node in model.Nodes)
        {
            const double half = 45;
            var at = node.At;
            blocks.AddExtrusion(new[] { new Point2D(at.X - half, at.Y - half), new Point2D(at.X + half, at.Y - half), new Point2D(at.X + half, at.Y + half), new Point2D(at.X - half, at.Y + half) }, at.Z - half, at.Z + half);
        }

        if (!blocks.IsEmpty) group.Children.Add(new GeometryModel3D(ToGeometry(blocks), Lit(Color.FromRgb(0x30, 0x34, 0x3C), 0xFF)));

        var supports = new Mesh3D(Guid.Empty, Guid.Empty, MeshKind.Beam, new ColourRgb(0, 0, 0), "Supports");
        foreach (var support in model.Supports)
        {
            if (support.Node is { } node)
            {
                // A pyramid under the node.
                var (apex, size, depth) = (node.At, 220.0, 320.0);
                var corners = new[] { (-1, -1), (1, -1), (1, 1), (-1, 1) }.Select(c => new CorePoint3D(apex.X + c.Item1 * size, apex.Y + c.Item2 * size, apex.Z - depth)).ToList();
                for (var i = 0; i < 4; i++) supports.AddTriangle(apex, corners[i], corners[(i + 1) % 4]);
                supports.AddTriangle(corners[0], corners[2], corners[1]);
                supports.AddTriangle(corners[0], corners[3], corners[2]);
            }
            else if (support is { From: { } from, To: { } to })
            {
                // A strip along the wall's foot.
                var (a, b) = (from.At, to.At);
                supports.AddFace(a, b, new CorePoint3D(b.X, b.Y, b.Z - 200), new CorePoint3D(a.X, a.Y, a.Z - 200), new CorePoint3D((a.X + b.X) / 2 + (b.Y - a.Y), (a.Y + b.Y) / 2 - (b.X - a.X), a.Z));
            }
        }

        if (!supports.IsEmpty)
        {
            var material = Lit(SupportColour, 0xD0);
            group.Children.Add(new GeometryModel3D(ToGeometry(supports), material) { BackMaterial = material });
        }

        return group;
    }

    /// <summary>The members' lines and the plates' edges, as wide as the view's edges are at this distance and wider.</summary>
    private void AddAnalyticalEdges()
    {
        if (_analytical is not { } model) return;
        var half = EdgeHalfWidth;
        var members = new EdgeMesh();
        foreach (var member in model.Members)
            for (var i = 0; i < member.Nodes.Count - 1; i++)
                members.Add(ToUnits(member.Nodes[i].At), ToUnits(member.Nodes[i + 1].At), half * (member.Kind == AnalyticalMemberKind.Joist ? 1.6 : 2.8));
        AddEdgeModel(members, MemberColour);

        foreach (var kind in model.Panels.Select(panel => panel.Kind).Distinct())
        {
            var edges = new EdgeMesh();
            foreach (var panel in model.Panels.Where(panel => panel.Kind == kind))
            {
                foreach (var (from, to) in panel.Edges) edges.Add(ToUnits(from.At), ToUnits(to.At), half * 1.6);
                foreach (var opening in panel.Openings)
                    for (var i = 0; i < opening.Count; i++)
                        edges.Add(ToUnits(opening[i]), ToUnits(opening[(i + 1) % opening.Count]), half);
            }

            var colour = PanelColour(kind);
            AddEdgeModel(edges, Color.FromRgb((byte)(colour.R * 0.7), (byte)(colour.G * 0.7), (byte)(colour.B * 0.7)));
        }
    }
}
