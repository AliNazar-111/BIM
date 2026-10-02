using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// A gutter's section, out from the roof's edge and up from its top: the sheet it is formed
/// from, the channel that sheet holds, and where its back, front, rim and bottom are - and the
/// outlet in its bottom, where a downpipe takes the water.
/// </summary>
public sealed record GutterForm(
    IReadOnlyList<Point2D> Sheet, IReadOnlyList<Point2D> Channel, double Back, double Front, double Rim, double Bottom, double Outlet);

/// <summary>
/// Gutters as they are made: the four sections - K-style, half round, box and fascia gutter - and
/// the parts along one besides the channel itself: end caps closing its ends, hangers across it,
/// and a leaf screen over it.
/// </summary>
public static class Gutters
{
    /// <summary>How far apart hangers are when a type says nothing, mm.</summary>
    public const double HangerSpacing = 600;

    /// <summary>A hanger strap: how wide along the gutter, and how thick, mm.</summary>
    private const double HangerWidth = 25, HangerThickness = 3;

    /// <summary>How far apart the wires of a leaf screen are drawn, mm.</summary>
    private const double ScreenPitch = 40;

    /// <summary>A gutter type's section.</summary>
    public static GutterForm Form(GutterType gutter)
    {
        var width = Math.Max(gutter.Width, 20);
        var depth = Math.Max(gutter.Depth, 20);
        var sheet = Math.Clamp(gutter.WallThickness, 0.3, width / 4);

        // The outer face, from the top of the back down round the bottom and up the front, and
        // any hem turned in along the front's top edge.
        IReadOnlyList<Point2D> face;
        IReadOnlyList<Point2D> hem = Array.Empty<Point2D>();
        double outlet;

        switch (gutter.Shape)
        {
            case GutterShape.Box:
                face = new[] { new Point2D(0, 0), new Point2D(0, -depth), new Point2D(width, -depth), new Point2D(width, 0) };
                outlet = width / 2;
                break;

            case GutterShape.KStyle:
            {
                // A flat back and bottom, and a front moulded like a crown moulding: a cove
                // rising out from the bottom, a bead, a smaller cove, and a hemmed rim.
                (double U, double V)[] moulding =
                {
                    (0, 0), (0, -1), (0.60, -1), (0.64, -0.86), (0.70, -0.74), (0.78, -0.68), (0.84, -0.60),
                    (0.86, -0.48), (0.84, -0.38), (0.90, -0.30), (0.97, -0.24), (1, -0.14), (1, 0)
                };
                face = moulding.Select(p => new Point2D(p.U * width, p.V * depth)).ToList();
                hem = new[] { new Point2D(width - Math.Min(15, width / 8), 0) };
                outlet = 0.30 * width;
                break;
            }

            case GutterShape.Fascia:
                // Deep and square-fronted, its back against the rafter ends in place of a fascia
                // board: a plain front, a bevel along its bottom and a hem along its top.
                var bevel = Math.Min(12, depth / 6);
                face = new[]
                {
                    new Point2D(0, 0), new Point2D(0, -depth), new Point2D(width - bevel, -depth),
                    new Point2D(width, -depth + bevel), new Point2D(width, 0)
                };
                hem = new[] { new Point2D(width - Math.Min(18, width / 6), 0) };
                outlet = (width - bevel) / 2;
                depth = Math.Max(depth, 20);
                break;

            default:
            {
                // A half round, from the back of its rim down round the bottom and up to the front.
                const int pieces = 12;
                var radius = width / 2;
                face = Enumerable.Range(0, pieces + 1)
                    .Select(i => Math.PI * i / pieces)
                    .Select(angle => new Point2D(radius - radius * Math.Cos(angle), -radius * Math.Sin(angle)))
                    .ToList();
                outlet = radius;
                depth = radius;
                break;
            }
        }

        var line = face.Concat(hem).ToList();
        return new GutterForm(Sheet(line, sheet), face, 0, width, 0, -depth, outlet);
    }

    /// <summary>
    /// A sheet of a thickness formed along a line, as a closed outline: the line, then back along
    /// it on its inner side. The line runs down the back, across the bottom and up the front, so
    /// the inside of the channel is on its left.
    /// </summary>
    private static IReadOnlyList<Point2D> Sheet(IReadOnlyList<Point2D> line, double thickness)
    {
        static Vector2D Left(Point2D from, Point2D to)
        {
            var along = (to - from).NormalisedOrDefault(Vector2D.UnitX);
            return new Vector2D(-along.Y, along.X);
        }

        var inner = new List<Point2D>();
        for (var i = 0; i < line.Count; i++)
        {
            var before = i > 0 ? Left(line[i - 1], line[i]) : (Vector2D?)null;
            var after = i + 1 < line.Count ? Left(line[i], line[i + 1]) : (Vector2D?)null;
            var normal = (before, after) switch
            {
                ({ } b, { } a) => (b + a).NormalisedOrDefault(a),
                ({ } b, null) => b,
                (null, { } a) => a,
                _ => Vector2D.UnitY
            };

            // Where the sheet ends on a side, it is cut level with the rim rather than on the slant.
            if ((before is null || after is null) && Math.Abs(normal.Y) < 0.5)
                normal = new Vector2D(Math.Sign(normal.X), 0);

            // Mitred at a turn, so the sheet keeps its thickness round the corner.
            var reach = before is { } first ? thickness / Math.Max(0.35, normal.Dot(first)) : thickness;
            inner.Add(line[i] + normal * reach);
        }

        var outline = line.Concat(Enumerable.Reverse(inner)).ToList();
        if (Polygon2D.SignedArea(outline) < 0) outline.Reverse();
        return outline;
    }

    /// <summary>
    /// The parts of a gutter besides its channel, onto its mesh along one run: a cap closing each
    /// open end, a hanger across its rim every so often, and a leaf screen over it if it has one.
    /// </summary>
    public static void AddParts(Mesh3D mesh, GutterType type, RoofEdgeSweep gutter, RoofEdgeRun run)
    {
        if (run.Segments.Count == 0) return;

        var form = Form(type);
        Point2D Shifted(Point2D point) => new(point.X + gutter.HorizontalOffset, point.Y + gutter.VerticalOffset);
        var channel = form.Channel.Select(Shifted).ToList();

        // End caps: the channel's outline, closed flat across each open end.
        if (!run.Closed)
        {
            var first = run.Segments[0];
            var last = run.Segments[^1];
            Plate(mesh, channel.Select(point => first.Place(first.From, point)).ToList(), -first.Along);
            Plate(mesh, channel.Select(point => last.Place(last.To, point)).ToList(), last.Along);
        }

        var (back, front) = (form.Back + gutter.HorizontalOffset, form.Front + gutter.HorizontalOffset);
        var rim = form.Rim + gutter.VerticalOffset;

        // Hangers: a strap across the rim, from the back to the front, every so often.
        var spacing = type.HangerSpacing;
        if (spacing > 0)
        {
            foreach (var segment in run.Segments)
            {
                var length = segment.Length;
                for (var s = Math.Min(spacing / 2, length / 2); s < length; s += spacing)
                {
                    var at = segment.From + segment.Along * s;
                    var start = segment.Place(at, new Point2D(back, rim)) - segment.Along * (HangerWidth / 2);
                    Box(mesh, start, segment.Out * (front - back), segment.Along * HangerWidth, segment.Up * HangerThickness);
                }
            }
        }

        // A leaf screen: a mesh over the channel's top, all along it.
        if (type.LeafGuard)
        {
            var screen = new[] { new Point2D(back, rim), new Point2D(front, rim), new Point2D(front, rim + 2), new Point2D(back, rim + 2) };
            var covered = new RoofEdgeRun(run.Segments.Select(segment => segment with { Profile = screen }).ToList(), run.Closed);
            RoofEdgeSweeps.AddRun(mesh, covered);

            foreach (var segment in run.Segments)
            {
                for (var s = ScreenPitch; s < segment.Length; s += ScreenPitch)
                {
                    var at = segment.From + segment.Along * s;
                    mesh.AddEdge(segment.Place(at, new Point2D(back, rim + 2.5)).ToPoint(), segment.Place(at, new Point2D(front, rim + 2.5)).ToPoint());
                }
            }
        }
    }

    /// <summary>A flat plate over an outline standing in space, seen from both sides, with its edge drawn.</summary>
    private static void Plate(Mesh3D mesh, IReadOnlyList<Vector3> outline, Vector3 facing)
    {
        if (outline.Count < 3) return;

        // Triangulated in its own plane.
        var u = (outline[1] - outline[0]).Normalised;
        var v = facing.Normalised.Cross(u).Normalised;
        var flat = outline.Select(point => new Point2D((point - outline[0]).Dot(u), (point - outline[0]).Dot(v))).ToList();
        var triangles = Polygon2D.Triangulate(flat).ToList();
        var points = outline.Select(point => point.ToPoint()).ToList();

        mesh.AddFace(points, triangles, facing.ToPoint());
        mesh.AddFace(points, triangles, (-facing).ToPoint());
        for (var i = 0; i < outline.Count; i++) mesh.AddEdge(points[i], points[(i + 1) % points.Count]);
    }

    /// <summary>A box from a corner along three edges.</summary>
    private static void Box(Mesh3D mesh, Vector3 corner, Vector3 a, Vector3 b, Vector3 c)
    {
        Point3D P(double i, double j, double k) => (corner + a * i + b * j + c * k).ToPoint();

        mesh.AddQuad(P(0, 0, 0), P(0, 1, 0), P(1, 1, 0), P(1, 0, 0));
        mesh.AddQuad(P(0, 0, 1), P(1, 0, 1), P(1, 1, 1), P(0, 1, 1));
        mesh.AddQuad(P(0, 0, 0), P(1, 0, 0), P(1, 0, 1), P(0, 0, 1));
        mesh.AddQuad(P(0, 1, 0), P(0, 1, 1), P(1, 1, 1), P(1, 1, 0));
        mesh.AddQuad(P(0, 0, 0), P(0, 0, 1), P(0, 1, 1), P(0, 1, 0));
        mesh.AddQuad(P(1, 0, 0), P(1, 1, 0), P(1, 1, 1), P(1, 0, 1));
    }
}
