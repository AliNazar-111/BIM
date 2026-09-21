using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;

namespace BIMDesigner.Core.Views;

/// <summary>
/// Builds the 3D form of the model (specification section 6.1).
///
/// Like the section cut, nothing here is stored: the meshes are produced from the same walls,
/// levels and slabs every other view reads, whenever the 3D view asks. So the 3D view cannot
/// show a building the plan does not, and every height it draws is the height a schedule
/// would report.
///
/// It lives in Core, with no reference to any 3D library, so the same geometry can back an
/// IFC export or a web viewer without being worked out a second time.
/// </summary>
public static class ModelMeshBuilder
{
    private static readonly ColourRgb DefaultSurface = new(0xB0, 0xB0, 0xB0);
    private static readonly ColourRgb GlazingColour = new(0x8C, 0xC4, 0xE0);
    private static readonly ColourRgb LeafColour = new(0xA8, 0x84, 0x5C);

    /// <summary>Thickness of a door leaf and a pane of glass, in millimetres.</summary>
    private const double LeafThickness = 40;
    private const double PaneThickness = 12;

    public static IReadOnlyList<Mesh3D> Build(BimDocument document, Func<Element, bool>? shows = null)
    {
        shows ??= _ => true;

        var meshes = new List<Mesh3D>();

        foreach (var wall in document.Walls.Where(wall => shows(wall))) AddWall(document, wall, meshes);
        foreach (var slab in document.Elements.OfType<Slab>()) AddSlab(document, slab, meshes);

        return meshes.Where(mesh => !mesh.IsEmpty).ToList();
    }

    // ---- walls -----------------------------------------------------------------

    /// <summary>
    /// A wall as its real layers, each extruded between the wall's base and top, with the
    /// holes its doors and windows leave.
    ///
    /// Each layer is its own mesh in its own material, for the same reason the plan draws
    /// them separately: an exterior wall is brick outside and plaster inside, and a 3D view
    /// that showed it as one grey block would be hiding the most useful thing about it.
    /// </summary>
    private static void AddWall(BimDocument document, Wall wall, List<Mesh3D> meshes)
    {
        var type = document.GetWallType(wall);
        if (type is null || wall.Length <= WallJoins.JoinTolerance) return;

        var structure = type.Structure;
        if (structure.TotalWidth <= 0) return;

        var bottom = (document.FindLevel(wall.LevelId)?.Elevation ?? 0) + wall.BaseOffset;
        var top = bottom + wall.GetHeight(document);
        if (top <= bottom) return;

        var (startCut, endCut) = WallJoins.GetEndCuts(document, wall, type);
        var half = structure.TotalWidth / 2;

        // The pieces of wall, with the cut at each end: full height where it is solid, and
        // below the sill and above the head wherever something is hosted in it. The cutting is
        // worked out in one place so the plan and the 3D model cannot disagree about where a
        // wall starts and stops.
        var pieces = new List<(WallSlice Slice, double Bottom, double Top)>();

        foreach (var slice in WallSlices.Solid(document, wall, type))
            pieces.Add((slice, bottom, top));

        var openings = new List<(Opening Opening, OpeningType Type, double From, double To, double Sill, double Head)>();

        foreach (var opening in WallOpenings.Of(document, wall))
        {
            if (document.FindType<OpeningType>(opening.TypeId) is not { } openingType) continue;

            var (from, to) = opening.GetSpan(openingType);
            from = Math.Max(0, from);
            to = Math.Min(wall.Length, to);
            if (to - from <= WallJoins.JoinTolerance) continue;

            var sill = Math.Clamp(bottom + opening.SillHeight, bottom, top);
            var head = Math.Clamp(bottom + opening.SillHeight + openingType.Height, bottom, top);

            if (WallSlices.Between(document, wall, type, from, to, startCut, endCut) is { } over)
            {
                if (sill > bottom) pieces.Add((over, bottom, sill));
                if (head < top) pieces.Add((over, head, top));
            }

            openings.Add((opening, openingType, from, to, sill, head));
        }

        foreach (var (layer, start, end) in structure.GetLayerOffsets())
        {
            var material = document.FindMaterial(layer.MaterialId);
            var mesh = new Mesh3D(
                wall.Id, wall.LevelId, MeshKind.Wall,
                material?.SurfaceColour ?? DefaultSurface,
                material?.Name ?? layer.Function.ToString());

            foreach (var (slice, pieceBottom, pieceTop) in pieces)
            {
                var outline = WallJoins.GetBandOutline(
                    wall, type, half - start, half - end, Unwrapped(slice.CutFrom), Unwrapped(slice.CutTo));

                mesh.AddExtrusion(outline, pieceBottom, pieceTop);
            }

            meshes.Add(mesh);
        }

        foreach (var (opening, openingType, from, to, sill, head) in openings)
            AddOpeningInfill(wall, type, opening, from, to, sill, head, meshes);
    }

    /// <summary>
    /// A jamb without its wrapping. The finishes return into a reveal only as high as the
    /// opening; above it the wall carries on unbroken, and a solid built from the plan
    /// cut would run the return the full height of the wall.
    /// </summary>
    private static WallCut Unwrapped(WallCut cut) =>
        cut.Condition == WallEndCondition.Jamb ? new WallCut(cut.Points, cut.Condition) : cut;

    /// <summary>
    /// What fills a hole: a pane of glass for a window, a closed leaf for a door. Both sit in
    /// the middle of the wall, which is where a frame is fixed.
    /// </summary>
    private static void AddOpeningInfill(
        Wall wall, WallType type, Opening opening, double from, double to, double sill, double head,
        List<Mesh3D> meshes)
    {
        if (head <= sill) return;

        var (bodyStart, _) = wall.GetBodyCentreline(type.Structure);
        var along = wall.Direction;
        var across = wall.Direction.PerpendicularLeft();

        var isWindow = opening is Window;
        var half = (isWindow ? PaneThickness : LeafThickness) / 2;

        var outline = new[]
        {
            bodyStart + along * from + across * half,
            bodyStart + along * to + across * half,
            bodyStart + along * to - across * half,
            bodyStart + along * from - across * half
        };

        var mesh = new Mesh3D(
            opening.Id, opening.LevelId,
            isWindow ? MeshKind.Glazing : MeshKind.DoorLeaf,
            isWindow ? GlazingColour : LeafColour,
            isWindow ? "Glazing" : "Door leaf");

        mesh.AddExtrusion(outline, sill, head);
        meshes.Add(mesh);
    }

    // ---- slabs -----------------------------------------------------------------

    /// <summary>
    /// A floor, ceiling or roof as its layers, hanging down from its upper surface - the same
    /// convention the section uses, so the two views cannot disagree about where it sits.
    /// </summary>
    private static void AddSlab(BimDocument document, Slab slab, List<Mesh3D> meshes)
    {
        if (slab.Boundary.Count < 3) return;
        if (document.FindType<SlabType>(slab.TypeId) is not { } type) return;

        var surface = (document.FindLevel(slab.LevelId)?.Elevation ?? 0) + slab.HeightOffset;

        foreach (var (layer, start, end) in type.Structure.GetLayerOffsets())
        {
            var material = document.FindMaterial(layer.MaterialId);
            var mesh = new Mesh3D(
                slab.Id, slab.LevelId,

                // Kept apart so a viewer can lift the roof off without losing the floors.
                slab switch
                {
                    Roof => MeshKind.Roof,
                    Ceiling => MeshKind.Ceiling,
                    _ => MeshKind.Floor
                },
                material?.SurfaceColour ?? DefaultSurface,
                material?.Name ?? layer.Function.ToString());

            mesh.AddExtrusion(slab.Boundary, surface - end, surface - start);
            meshes.Add(mesh);
        }
    }

    /// <summary>The box around the whole model, or null when there is nothing to draw.</summary>
    public static (Point3D Min, Point3D Max)? Bounds(IEnumerable<Mesh3D> meshes)
    {
        (Point3D Min, Point3D Max)? total = null;

        foreach (var bounds in meshes.Select(mesh => mesh.Bounds()))
        {
            if (bounds is not { } b) continue;

            total = total is not { } t
                ? b
                : (new Point3D(Math.Min(t.Min.X, b.Min.X), Math.Min(t.Min.Y, b.Min.Y), Math.Min(t.Min.Z, b.Min.Z)),
                   new Point3D(Math.Max(t.Max.X, b.Max.X), Math.Max(t.Max.Y, b.Max.Y), Math.Max(t.Max.Z, b.Max.Z)));
        }

        return total;
    }
}
