using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;
using BIMDesigner.Core.Parameters;

namespace BIMDesigner.Core.Architecture;

/// <summary>The section a gutter is made in.</summary>
public enum GutterShape
{
    /// <summary>A half round, open at the top.</summary>
    HalfRound,

    /// <summary>A square channel, open at the top.</summary>
    Box
}

/// <summary>
/// A fascia board's type (specification section 3.3, "fascia"): how thick it is and how deep,
/// and what it is made of. Deep enough by default to cover the roof's edge wherever it runs:
/// the cut ends of the build-up at an eave, its full thickness up a verge.
/// </summary>
public sealed class FasciaType : ElementType
{
    public FasciaType(string name) : base(name)
    {
    }

    public override BuiltInCategory Category => BuiltInCategory.Fascias;

    /// <summary>How far it stands out from the roof's edge.</summary>
    public double Thickness { get; set; } = 25;

    /// <summary>How deep it is, down the roof's edge; nothing to cover the edge, however deep that is.</summary>
    public double Depth { get; set; }

    public Guid MaterialId { get; set; }

    public FasciaType Duplicate(string name) => new(name)
    {
        Thickness = Thickness, Depth = Depth, MaterialId = MaterialId, Description = Description, Cost = Cost
    };

    public override IEnumerable<ParameterValue> GetTypeParameters(BimDocument? document = null)
    {
        foreach (var parameter in GetCommonTypeParameters()) yield return parameter;

        yield return ParameterValue.Bind(RoofEdgeSweepParameters.Thickness, () => Thickness, v => { if (v > 0) Thickness = v; });
        yield return ParameterValue.Bind(RoofEdgeSweepParameters.FasciaDepth, () => Depth, v => { if (v >= 0) Depth = v; });
        if (RoofEdgeSweepParameters.MaterialOf(document, () => MaterialId, id => MaterialId = id) is { } material) yield return material;
    }
}

/// <summary>A gutter's type: its section, how big, the sheet it is made of, and the material.</summary>
public sealed class GutterType : ElementType
{
    public GutterType(string name) : base(name)
    {
    }

    public override BuiltInCategory Category => BuiltInCategory.Gutters;

    public GutterShape Shape { get; set; } = GutterShape.HalfRound;

    /// <summary>Across its top, out from the roof.</summary>
    public double Width { get; set; } = 125;

    /// <summary>How deep a box gutter is; a half round is as deep as half its width.</summary>
    public double Depth { get; set; } = 75;

    /// <summary>The thickness of the sheet it is formed from.</summary>
    public double WallThickness { get; set; } = 4;

    public Guid MaterialId { get; set; }

    public GutterType Duplicate(string name) => new(name)
    {
        Shape = Shape, Width = Width, Depth = Depth, WallThickness = WallThickness, MaterialId = MaterialId,
        Description = Description, Cost = Cost
    };

    public override IEnumerable<ParameterValue> GetTypeParameters(BimDocument? document = null)
    {
        foreach (var parameter in GetCommonTypeParameters()) yield return parameter;

        yield return ParameterValue.BindChoice(RoofEdgeSweepParameters.GutterShape,
            () => EnumText.Humanise(Shape),
            v => { if (EnumText.TryParse<GutterShape>(v, out var shape)) Shape = shape; },
            EnumText.Choices<GutterShape>());
        yield return ParameterValue.Bind(RoofEdgeSweepParameters.Width, () => Width, v => { if (v > 2 * WallThickness) Width = v; });
        yield return ParameterValue.Bind(RoofEdgeSweepParameters.Depth, () => Depth, v => { if (v > WallThickness) Depth = v; });
        yield return ParameterValue.Bind(RoofEdgeSweepParameters.WallThickness, () => WallThickness, v => { if (v > 0 && 2 * v < Width) WallThickness = v; });
        if (RoofEdgeSweepParameters.MaterialOf(document, () => MaterialId, id => MaterialId = id) is { } material) yield return material;
    }
}

/// <summary>
/// Something run along the edges of a roof - a fascia board, a gutter - as Revit's Roof:
/// Fascia and Roof: Gutter are: a profile swept along the edges picked, round the corners
/// between them mitred, and up and over a verge as the roof goes. It follows the roof: the
/// edges are found by what they are, not where they come in the roof's outline, so it stays
/// on them as the roof's sketch is edited, and goes with the roof when it moves or is deleted.
/// </summary>
public abstract class RoofEdgeSweep : Element, IHostedElement
{
    /// <summary>The roof whose edges it runs along.</summary>
    public Guid RoofId { get; set; }

    public Guid HostId => RoofId;

    /// <summary>The edges it runs along, by <see cref="RoofEdge.Id"/>.</summary>
    public List<Guid> EdgeIds { get; } = new();

    /// <summary>How far out from the roof's edge it is moved, beyond where its profile puts it.</summary>
    public double HorizontalOffset { get; set; }

    /// <summary>How far up from the roof's top edge it is moved; down is negative.</summary>
    public double VerticalOffset { get; set; }

    /// <summary>How long it is, along every run of it, up the slopes too.</summary>
    public double Length(BimDocument document) => this is Soffit
        ? RoofEdgeSweeps.SoffitPieces(document, this).Sum(piece => piece.Length)
        : RoofEdgeSweeps.Runs(document, this).Sum(run => run.Segments.Sum(segment => segment.Length));

    public override IEnumerable<ParameterValue> GetInstanceParameters(BimDocument document)
    {
        yield return ParameterValue.ReadOnly(RoofEdgeSweepParameters.Roof,
            () => document.Elements.OfType<Roof>().FirstOrDefault(roof => roof.Id == RoofId) is { } roof
                ? document.FindType<SlabType>(roof.TypeId)?.Name ?? "Roof"
                : "<none>");
        yield return ParameterValue.ReadOnly(RoofEdgeSweepParameters.Edges, () => EdgeIds.Count);
        yield return ParameterValue.Bind(RoofEdgeSweepParameters.HorizontalOffset, () => HorizontalOffset, v => HorizontalOffset = v);
        yield return ParameterValue.Bind(RoofEdgeSweepParameters.VerticalOffset, () => VerticalOffset, v => VerticalOffset = v);
        yield return ParameterValue.ReadOnly(RoofEdgeSweepParameters.Length, () => Length(document));

        foreach (var parameter in GetCommonParameters(document)) yield return parameter;
    }
}

/// <summary>A fascia: a board along the roof's edge, covering the ends of its build-up.</summary>
public sealed class Fascia : RoofEdgeSweep
{
    public override BuiltInCategory Category => BuiltInCategory.Fascias;
}

/// <summary>A gutter, hung at the roof's edge to take the water off it.</summary>
public sealed class Gutter : RoofEdgeSweep
{
    public override BuiltInCategory Category => BuiltInCategory.Gutters;
}

/// <summary>
/// A soffit: the board closing the underside of the roof's overhang along its eaves, from the
/// back of the fascia to the face of the wall - Revit's Roof: Soffit, as a boxed eave has one.
/// Level, at the underside of the roof's edge, so the fascia covers its front.
/// </summary>
public sealed class Soffit : RoofEdgeSweep
{
    public override BuiltInCategory Category => BuiltInCategory.RoofSoffits;
}

/// <summary>A soffit's type: how thick the board is, and what it is made of.</summary>
public sealed class SoffitType : ElementType
{
    public SoffitType(string name) : base(name)
    {
    }

    public override BuiltInCategory Category => BuiltInCategory.RoofSoffits;

    public double Thickness { get; set; } = 12;

    public Guid MaterialId { get; set; }

    public SoffitType Duplicate(string name) => new(name)
    {
        Thickness = Thickness, MaterialId = MaterialId, Description = Description, Cost = Cost
    };

    public override IEnumerable<ParameterValue> GetTypeParameters(BimDocument? document = null)
    {
        foreach (var parameter in GetCommonTypeParameters()) yield return parameter;
        yield return ParameterValue.Bind(RoofEdgeSweepParameters.Thickness, () => Thickness, v => { if (v > 0) Thickness = v; });
        if (RoofEdgeSweepParameters.MaterialOf(document, () => MaterialId, id => MaterialId = id) is { } material) yield return material;
    }
}

/// <summary>One strip of a soffit: under one eave, its outline in plan, its top, and how long the eave is.</summary>
public sealed record SoffitPiece(IReadOnlyList<Point2D> Outline, double Top, double Bottom, double Length);

public static class RoofEdgeSweepParameters
{
    public static readonly ParameterDefinition Roof = new("Roof", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition Edges = new("Edges", ParameterDataType.Integer, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition HorizontalOffset = new("Horizontal Profile Offset", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition VerticalOffset = new("Vertical Profile Offset", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition Length = new("Length", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);

    public static readonly ParameterDefinition Thickness = new("Thickness", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition FasciaDepth = new("Depth (0 = the roof's edge)", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition GutterShape = new("Shape", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.Construction);
    public static readonly ParameterDefinition Width = new("Width", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Depth = new("Depth", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition WallThickness = new("Sheet Thickness", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Material = new("Material", ParameterDataType.Material, ParameterBinding.Type, ParameterGroup.MaterialsAndFinishes);

    /// <summary>What a fascia, gutter or soffit type is made of, chosen from the project's materials.</summary>
    public static ParameterValue? MaterialOf(BimDocument? document, Func<Guid> get, Action<Guid> set) =>
        document is null
            ? null
            : ParameterValue.BindChoice(
                Material,
                () => document.FindMaterial(get())?.Name ?? "<by category>",
                v => set(document.Materials.FirstOrDefault(m => m.Name == v)?.Id ?? Guid.Empty),
                new[] { "<by category>" }.Concat(document.Materials.Select(m => m.Name).OrderBy(name => name)).ToArray());
}

/// <summary>A point in space as a direction and distance, for working a sweep out along a sloping path.</summary>
public readonly record struct Vector3(double X, double Y, double Z)
{
    public static Vector3 operator +(Vector3 a, Vector3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vector3 operator -(Vector3 a, Vector3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vector3 operator *(Vector3 v, double s) => new(v.X * s, v.Y * s, v.Z * s);
    public static Vector3 operator -(Vector3 v) => new(-v.X, -v.Y, -v.Z);

    public double Dot(Vector3 other) => X * other.X + Y * other.Y + Z * other.Z;
    public Vector3 Cross(Vector3 other) => new(Y * other.Z - Z * other.Y, Z * other.X - X * other.Z, X * other.Y - Y * other.X);
    public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);
    public Vector3 Normalised => Length < 1e-12 ? this : this * (1 / Length);

    public static Vector3 Of(Point3D point) => new(point.X, point.Y, point.Z);
    public Point3D ToPoint() => new(X, Y, Z);
    public Point2D Plan => new(X, Y);
}

/// <summary>
/// One straight stretch of a sweep: from one point to the next along the roof's top outer
/// edge, the way out from the roof there, and the profile's section as it stands on it.
/// </summary>
public sealed record RoofEdgeSegment(Vector3 From, Vector3 To, Vector3 Out, Vector3 Up, IReadOnlyList<Point2D> Profile)
{
    public Vector3 Along => (To - From).Normalised;

    public double Length => (To - From).Length;

    /// <summary>A profile point, as it stands at a point of this stretch.</summary>
    public Vector3 Place(Vector3 at, Point2D profile) => at + Out * profile.X + Up * profile.Y;
}

/// <summary>One unbroken run of a sweep: stretches following on round the roof, closed if it goes all the way round.</summary>
public sealed record RoofEdgeRun(IReadOnlyList<RoofEdgeSegment> Segments, bool Closed);

/// <summary>
/// Where fascias and gutters run and the shapes they make - worked out from the roof each time,
/// so they follow it.
/// </summary>
public static class RoofEdgeSweeps
{
    /// <summary>The fascias and gutters along a roof's edges.</summary>
    public static IEnumerable<RoofEdgeSweep> Of(BimDocument document, Roof roof) =>
        document.Elements.OfType<RoofEdgeSweep>().Where(sweep => sweep.RoofId == roof.Id);

    /// <summary>
    /// Every run of a fascia or gutter: the edges it is on, taken in order round the roof and
    /// joined where they follow on from each other, each stretch at the height of the roof's
    /// top outer edge - level along an eave, up and over a verge - with its profile sized there.
    /// </summary>
    public static IReadOnlyList<RoofEdgeRun> Runs(BimDocument document, RoofEdgeSweep sweep)
    {
        if (sweep is Soffit) return Array.Empty<RoofEdgeRun>();
        if (document.Elements.OfType<Roof>().FirstOrDefault(roof => roof.Id == sweep.RoofId) is not { } roof) return Array.Empty<RoofEdgeRun>();
        if (roof.IsExtrusion || roof.Boundary.Count < 3 || roof.Edges.Count != roof.Boundary.Count) return Array.Empty<RoofEdgeRun>();

        var count = roof.Boundary.Count;
        var wanted = sweep.EdgeIds.ToHashSet();
        var on = Enumerable.Range(0, count).Select(i => wanted.Contains(roof.Edges[i].Id)).ToArray();
        if (!on.Any(picked => picked)) return Array.Empty<RoofEdgeRun>();

        var runs = new List<RoofEdgeRun>();
        if (on.All(picked => picked))
        {
            runs.Add(Run(document, roof, sweep, Enumerable.Range(0, count).ToList(), closed: true));
            return runs;
        }

        for (var start = 0; start < count; start++)
        {
            if (!on[start] || on[(start - 1 + count) % count]) continue;

            var edges = new List<int>();
            for (var i = start; on[i % count] && edges.Count < count; i++) edges.Add(i % count);
            runs.Add(Run(document, roof, sweep, edges, closed: false));
        }

        return runs;
    }

    private static RoofEdgeRun Run(BimDocument document, Roof roof, RoofEdgeSweep sweep, IReadOnlyList<int> edges, bool closed)
    {
        var surface = roof.Surface(document);
        var thickness = document.FindType<SlabType>(roof.TypeId)?.Thickness ?? 0;
        var boundary = roof.Boundary;
        var count = boundary.Count;
        var anticlockwise = Polygon2D.SignedArea(boundary) >= 0;
        var segments = new List<RoofEdgeSegment>();

        foreach (var edge in edges)
        {
            var (a, b) = (boundary[edge], boundary[(edge + 1) % count]);
            var direction = (b - a).NormalisedOrDefault(Vector2D.UnitX);
            var outward = direction.PerpendicularLeft() * (anticlockwise ? -1 : 1);

            // Along the edge, broken wherever a ridge, hip or valley meets it: the roof's top
            // changes direction there.
            var stations = new List<double> { 0, 1 };
            foreach (var (from, to) in surface.BreakLines)
                if (Crossing(a, b, from, to) is { } t && t > 1e-6 && t < 1 - 1e-6) stations.Add(t);
            stations = stations.Distinct().OrderBy(t => t).ToList();

            for (var i = 0; i + 1 < stations.Count; i++)
            {
                var (p, q) = (a + (b - a) * stations[i], a + (b - a) * stations[i + 1]);
                var inside = p.MidpointTo(q) - outward * 1;
                var face = surface.FaceAt(inside);
                var stretch = face?.Plane.VerticalStretch ?? 1;
                double Top(Point2D at) => (face?.Plane.HeightAt(at) ?? surface.HeightAt(inside)) + thickness * stretch;

                var from = new Vector3(p.X, p.Y, Top(p));
                var to = new Vector3(q.X, q.Y, Top(q));
                var along = (to - from).Normalised;
                var outVector = new Vector3(outward.X, outward.Y, 0);
                var up = along.Cross(outVector);
                if (up.Z < 0) up = -up;
                up = up.Normalised;

                // How deep the roof's edge is here, square to the run: the cut ends of the build-up.
                var rise = Math.Abs(along.Z) / Math.Max(1e-9, Math.Sqrt(along.X * along.X + along.Y * along.Y));
                var edgeDepth = thickness * stretch / Math.Sqrt(1 + rise * rise);
                if (roof.Edges[edge].DefinesSlope && roof.RafterCut != RafterCut.PlumbCut && (face?.Plane.Rise ?? 0) > 1e-6)
                    edgeDepth = Math.Min(roof.FasciaDepth, edgeDepth);

                var profile = Profile(document, sweep, edgeDepth)
                    .Select(point => new Point2D(point.X + sweep.HorizontalOffset, point.Y + sweep.VerticalOffset))
                    .ToList();
                segments.Add(new RoofEdgeSegment(from, to, outVector, up, profile));
            }
        }

        return new RoofEdgeRun(segments, closed);
    }

    /// <summary>Where along a to b a line from one point to another crosses it, as a share of a to b; null if it does not.</summary>
    private static double? Crossing(Point2D a, Point2D b, Point2D c, Point2D d)
    {
        var r = b - a;
        var s = d - c;
        var denominator = r.X * s.Y - r.Y * s.X;
        if (Math.Abs(denominator) < 1e-9) return null;

        var t = ((c.X - a.X) * s.Y - (c.Y - a.Y) * s.X) / denominator;
        var u = ((c.X - a.X) * r.Y - (c.Y - a.Y) * r.X) / denominator;
        return u >= -1e-6 && u <= 1 + 1e-6 ? t : null;
    }

    /// <summary>How far a fascia's capping stands out past its face, how deep the capping is, and how far the board runs down past the roof's edge.</summary>
    public const double FasciaNose = 20, FasciaCap = 40, FasciaDrip = 25;

    /// <summary>
    /// The section of a fascia or gutter, in millimetres out from the roof's edge and up from its
    /// top, wound anticlockwise: a board down the edge, or a channel hung off it.
    /// </summary>
    public static IReadOnlyList<Point2D> Profile(BimDocument document, RoofEdgeSweep sweep, double edgeDepth)
    {
        switch (sweep)
        {
            case Fascia when document.FindType<FasciaType>(sweep.TypeId) is { } fascia:
            {
                // A moulded board, as a fascia is made: a capping nose standing out along its
                // top and a drip lip below the roof's edge, which hides the soffit's front
                // edge. The steps are what make it read along an eave - faces turned up, out
                // and down, each lit differently, and a line at each - rather than a flat band
                // the colour of whatever is next to it. As deep as its type says, if it says.
                var thickness = fascia.Thickness;
                var depth = fascia.Depth > 0 ? fascia.Depth : Math.Max(edgeDepth, 50) + FasciaDrip;
                var nose = Math.Min(FasciaNose, thickness);
                var cap = Math.Min(FasciaCap, depth / 3);
                return new[]
                {
                    new Point2D(0, -depth), new Point2D(thickness, -depth), new Point2D(thickness, -cap),
                    new Point2D(thickness + nose, -cap), new Point2D(thickness + nose, 0), new Point2D(0, 0)
                };
            }

            case Gutter when document.FindType<GutterType>(sweep.TypeId) is { } gutter:
                return GutterSection(gutter);

            default:
                return new[] { new Point2D(0, -150), new Point2D(25, -150), new Point2D(25, 0), new Point2D(0, 0) };
        }
    }

    private static IReadOnlyList<Point2D> GutterSection(GutterType gutter)
    {
        var (width, sheet) = (gutter.Width, Math.Min(gutter.WallThickness, gutter.Width / 4));
        List<Point2D> points;

        if (gutter.Shape == GutterShape.Box)
        {
            var depth = gutter.Depth;
            points = new List<Point2D>
            {
                new(0, 0), new(0, -depth), new(width, -depth), new(width, 0),
                new(width - sheet, 0), new(width - sheet, -depth + sheet), new(sheet, -depth + sheet), new(sheet, 0)
            };
        }
        else
        {
            // Round the bottom from the inner rim to the outer, then back inside it.
            const int pieces = 12;
            var (centre, outer, inner) = (width / 2, width / 2, width / 2 - sheet);
            points = new List<Point2D>();
            for (var i = 0; i <= pieces; i++)
            {
                var angle = Math.PI * i / pieces;
                points.Add(new Point2D(centre - outer * Math.Cos(angle), -outer * Math.Sin(angle)));
            }

            for (var i = pieces; i >= 0; i--)
            {
                var angle = Math.PI * i / pieces;
                points.Add(new Point2D(centre - inner * Math.Cos(angle), -inner * Math.Sin(angle)));
            }
        }

        if (Polygon2D.SignedArea(points) < 0) points.Reverse();
        return points;
    }

    /// <summary>
    /// The rings of a run: at each end of each stretch, the profile where it stands - cut on the
    /// plane halving the turn where one stretch meets the next, so the two meet in a mitre, and
    /// square across at a run's open ends.
    /// </summary>
    public static IReadOnlyList<(IReadOnlyList<Vector3> Start, IReadOnlyList<Vector3> End)> Rings(RoofEdgeRun run)
    {
        var segments = run.Segments;
        var rings = new List<(IReadOnlyList<Vector3>, IReadOnlyList<Vector3>)>();

        for (var s = 0; s < segments.Count; s++)
        {
            var segment = segments[s];
            var previous = s > 0 ? segments[s - 1] : run.Closed ? segments[^1] : null;
            var next = s + 1 < segments.Count ? segments[s + 1] : run.Closed ? segments[0] : null;

            rings.Add((
                segment.Profile.Select(point => Joined(segment, segment.From, point, previous)).ToList(),
                segment.Profile.Select(point => Joined(segment, segment.To, point, next)).ToList()));
        }

        return rings;
    }

    /// <summary>
    /// A profile point where one stretch meets the next. Round a hip or over a ridge the two are
    /// cut on the plane halving the turn, and meet in a mitre. Where a level eave meets a sloping
    /// verge they are butted, as a carpenter joins a fascia to a barge board: the eave's board
    /// runs on past the corner as far as the verge's stands out, and the verge's is cut plumb
    /// against it - a mitre between a level board and a sloping one would leave the roof's edge
    /// showing between them.
    /// </summary>
    private static Vector3 Joined(RoofEdgeSegment segment, Vector3 at, Point2D profile, RoofEdgeSegment? other)
    {
        var placed = segment.Place(at, profile);
        if (other is null) return placed;

        var along = segment.Along;
        var turn = other.Along;
        var level = Math.Abs(along.Z) < 1e-6;
        var otherLevel = Math.Abs(turn.Z) < 1e-6;
        var turnsInPlan = Math.Abs(along.X * turn.Y - along.Y * turn.X) > 1e-3;

        // Onto a plane, carried along this stretch.
        Vector3 Onto(Vector3 through, Vector3 normal)
        {
            var across = normal.Dot(along);
            return Math.Abs(across) < 1e-6 ? placed : placed - along * (normal.Dot(placed - through) / across);
        }

        if (turnsInPlan && level != otherLevel)
        {
            if (level)
            {
                var reach = other.Profile.Max(point => point.X);
                return Onto(at + other.Out * reach, other.Out);
            }

            return Onto(at, other.Out);
        }

        if ((along + turn).Length < 1e-6) return placed;
        return Onto(at, (along + turn).Normalised);
    }

    /// <summary>A fascia or gutter as a solid: each stretch a mitred prism of its profile, capped where a run ends.</summary>
    public static Mesh3D Mesh(BimDocument document, RoofEdgeSweep sweep, Guid levelId)
    {
        if (sweep is Soffit)
        {
            var board = document.FindMaterial(document.FindType<SoffitType>(sweep.TypeId)?.MaterialId ?? Guid.Empty);
            var soffit = new Mesh3D(sweep.Id, levelId, MeshKind.Ceiling, board?.SurfaceColour ?? ColourRgb.FromHex("E8E6E0"),
                board?.Name ?? "Soffit");
            foreach (var piece in SoffitPieces(document, sweep)) soffit.AddExtrusion(piece.Outline, piece.Bottom, piece.Top);
            return soffit;
        }

        var material = document.FindMaterial(sweep switch
        {
            Fascia => document.FindType<FasciaType>(sweep.TypeId)?.MaterialId ?? Guid.Empty,
            Gutter => document.FindType<GutterType>(sweep.TypeId)?.MaterialId ?? Guid.Empty,
            _ => Guid.Empty
        });
        var mesh = new Mesh3D(sweep.Id, levelId, MeshKind.Sweep, material?.SurfaceColour ?? ColourRgb.FromHex("E8E6E0"),
            material?.Name ?? sweep.Category.ToString());

        foreach (var run in Runs(document, sweep))
        {
            var rings = Rings(run);
            for (var s = 0; s < run.Segments.Count; s++)
            {
                var profile = run.Segments[s].Profile;
                var (start, end) = rings[s];
                var count = profile.Count;

                for (var k = 0; k < count; k++)
                {
                    var j = (k + 1) % count;
                    mesh.AddQuad(start[k].ToPoint(), end[k].ToPoint(), end[j].ToPoint(), start[j].ToPoint());
                    if (IsCorner(profile, k)) mesh.AddEdge(start[k].ToPoint(), end[k].ToPoint());
                }

                // Where a stretch meets the next the mitre is drawn; where the run stops it is closed.
                var opens = !run.Closed && s == 0;
                var closes = !run.Closed && s == run.Segments.Count - 1;
                if (opens) Cap(mesh, profile, start);
                if (closes) Cap(mesh, profile, end);
                for (var k = 0; k < count; k++)
                {
                    mesh.AddEdge(end[k].ToPoint(), end[(k + 1) % count].ToPoint());
                    if (opens) mesh.AddEdge(start[k].ToPoint(), start[(k + 1) % count].ToPoint());
                }
            }
        }

        return mesh;
    }

    /// <summary>Whether the profile turns at a point enough to be a line along the sweep, rather than part of a curve.</summary>
    private static bool IsCorner(IReadOnlyList<Point2D> profile, int k)
    {
        var count = profile.Count;
        var into = (profile[k] - profile[(k - 1 + count) % count]).NormalisedOrDefault(Vector2D.UnitX);
        var outOf = (profile[(k + 1) % count] - profile[k]).NormalisedOrDefault(Vector2D.UnitX);
        return into.Dot(outOf) < Math.Cos(30 * Math.PI / 180);
    }

    private static void Cap(Mesh3D mesh, IReadOnlyList<Point2D> profile, IReadOnlyList<Vector3> ring)
    {
        foreach (var (a, b, c) in Polygon2D.Triangulate(profile))
            mesh.AddTriangle(ring[a].ToPoint(), ring[b].ToPoint(), ring[c].ToPoint());
    }

    /// <summary>
    /// Where a section's cut crosses a fascia or gutter: the profile as the cut plane slices it,
    /// in distance along the cut and height - one outline for each stretch the cut goes through.
    /// </summary>
    public static IEnumerable<IReadOnlyList<(double X, double Y)>> Cut(
        BimDocument document, RoofEdgeSweep sweep, Func<Point2D, double> depthOf, Func<Point2D, double> alongCut)
    {
        // A soffit is level: where the cut crosses a strip of it, a band of its thickness.
        foreach (var piece in SoffitPieces(document, sweep))
        {
            var crossings = new List<double>();
            var outline = piece.Outline;
            for (var i = 0; i < outline.Count; i++)
            {
                var (p, q) = (outline[i], outline[(i + 1) % outline.Count]);
                var (dp, dq) = (depthOf(p), depthOf(q));
                if (dp * dq > 0 || Math.Abs(dp - dq) < 1e-9) continue;
                crossings.Add(alongCut(p + (q - p) * (dp / (dp - dq))));
            }

            if (crossings.Count < 2) continue;
            var (from, to) = (crossings.Min(), crossings.Max());
            if (to - from < 1) continue;
            yield return new[] { (from, piece.Bottom), (to, piece.Bottom), (to, piece.Top), (from, piece.Top) };
        }

        foreach (var run in Runs(document, sweep))
        foreach (var segment in run.Segments)
        {
            var (from, to) = (depthOf(segment.From.Plan), depthOf(segment.To.Plan));
            if (from * to > 0 || Math.Abs(from - to) < 1e-9) continue;

            // The cut goes through this stretch here; each profile point is carried along the
            // stretch onto the cut plane - which is where the plane slices the prism.
            var at = segment.From + (segment.To - segment.From) * (from / (from - to));
            var along = segment.Along;
            var rate = depthOf(new Point2D(along.X, along.Y)) - depthOf(new Point2D(0, 0));
            if (Math.Abs(rate) < 1e-9) continue;

            yield return segment.Profile.Select(point =>
            {
                var placed = segment.Place(at, point);
                var onPlane = placed - along * (depthOf(placed.Plan) / rate);
                return (alongCut(onPlane.Plan), onPlane.Z);
            }).ToList();
        }
    }

    /// <summary>A fascia or gutter in plan: the ground each stretch of it covers, seen from above.</summary>
    public static IEnumerable<IReadOnlyList<Point2D>> Footprints(BimDocument document, RoofEdgeSweep sweep)
    {
        foreach (var piece in SoffitPieces(document, sweep)) yield return piece.Outline;

        foreach (var run in Runs(document, sweep))
        {
            var rings = Rings(run);
            foreach (var (start, end) in rings)
                yield return Hull(start.Concat(end).Select(point => point.Plan).ToList());
        }
    }

    /// <summary>The convex hull, anticlockwise.</summary>
    private static IReadOnlyList<Point2D> Hull(List<Point2D> points)
    {
        var sorted = points.Distinct().OrderBy(p => p.X).ThenBy(p => p.Y).ToList();
        if (sorted.Count < 3) return sorted;

        static double Turn(Point2D o, Point2D a, Point2D b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);

        var hull = new List<Point2D>();
        foreach (var pass in new[] { sorted, Enumerable.Reverse(sorted).ToList() })
        {
            var start = hull.Count;
            foreach (var point in pass)
            {
                while (hull.Count >= start + 2 && Turn(hull[^2], hull[^1], point) <= 0) hull.RemoveAt(hull.Count - 1);
                hull.Add(point);
            }

            hull.RemoveAt(hull.Count - 1);
        }

        return hull;
    }

    /// <summary>
    /// A soffit's strips: under each of its eaves, from the roof's edge in to the wall's face -
    /// as far as the eave overhangs it - mitred where two eaves meet at a hip and square where
    /// one meets a verge; level, at the underside of the roof's edge there. An eave with no
    /// overhang has nothing under it to close.
    /// </summary>
    public static IReadOnlyList<SoffitPiece> SoffitPieces(BimDocument document, RoofEdgeSweep sweep)
    {
        if (sweep is not Soffit) return Array.Empty<SoffitPiece>();
        if (document.Elements.OfType<Roof>().FirstOrDefault(roof => roof.Id == sweep.RoofId) is not { } roof) return Array.Empty<SoffitPiece>();
        if (roof.IsExtrusion || roof.Boundary.Count < 3 || roof.Edges.Count != roof.Boundary.Count) return Array.Empty<SoffitPiece>();

        var boundary = roof.Boundary;
        var count = boundary.Count;
        var anticlockwise = Polygon2D.SignedArea(boundary) >= 0;
        var surface = roof.Surface(document);
        var thickness = document.FindType<SlabType>(roof.TypeId)?.Thickness ?? 0;
        var board = Math.Max(1, document.FindType<SoffitType>(sweep.TypeId)?.Thickness ?? 12);
        var wanted = sweep.EdgeIds.ToHashSet();

        // Under a level eave that overhangs its wall.
        bool Under(int i) => wanted.Contains(roof.Edges[i].Id) && roof.Edges[i].DefinesSlope && roof.Edges[i].Overhang > 1;
        Vector2D Inward(int i) =>
            (boundary[(i + 1) % count] - boundary[i]).NormalisedOrDefault(Vector2D.UnitX).PerpendicularLeft() * (anticlockwise ? 1 : -1);
        (Point2D, Point2D) Inset(int i) =>
            (boundary[i] + Inward(i) * roof.Edges[i].Overhang, boundary[(i + 1) % count] + Inward(i) * roof.Edges[i].Overhang);

        var pieces = new List<SoffitPiece>();
        for (var i = 0; i < count; i++)
        {
            if (!Under(i)) continue;

            var (a, b) = (boundary[i], boundary[(i + 1) % count]);
            var (inA, inB) = Inset(i);
            var previous = (i - 1 + count) % count;
            var next = (i + 1) % count;
            if (Under(previous) && Meet(Inset(previous), (inA, inB)) is { } corner) inA = corner;
            if (Under(next) && Meet((inA, inB), Inset(next)) is { } far) inB = far;

            // At the underside of the roof's edge here: its top less the depth of its edge.
            var middle = a.MidpointTo(b);
            var inside = middle + Inward(i) * 1;
            var face = surface.FaceAt(inside);
            var stretch = face?.Plane.VerticalStretch ?? 1;
            var top = (face?.Plane.HeightAt(middle) ?? surface.HeightAt(inside)) + thickness * stretch;
            var depth = thickness * stretch;
            if (roof.RafterCut != RafterCut.PlumbCut && (face?.Plane.Rise ?? 0) > 1e-6) depth = Math.Min(roof.FasciaDepth, depth);
            var underside = top - depth + sweep.VerticalOffset;

            var outline = new List<Point2D> { a, b, inB, inA };
            if (Polygon2D.SignedArea(outline) < 0) outline.Reverse();
            pieces.Add(new SoffitPiece(outline, underside, underside - board, a.DistanceTo(b)));
        }

        return pieces;
    }

    /// <summary>Where two lines, each through two points, cross; null if they run parallel.</summary>
    private static Point2D? Meet((Point2D A, Point2D B) first, (Point2D A, Point2D B) second)
    {
        var r = first.B - first.A;
        var s = second.B - second.A;
        var denominator = r.X * s.Y - r.Y * s.X;
        if (Math.Abs(denominator) < 1e-9) return null;

        var t = ((second.A.X - first.A.X) * s.Y - (second.A.Y - first.A.Y) * s.X) / denominator;
        return first.A + r * t;
    }

    /// <summary>The eaves a soffit goes under by itself: those that overhang their walls.</summary>
    public static IReadOnlyList<Guid> SoffitEdges(BimDocument document, Roof roof) =>
        OpenEdges(document, roof).Where(i => roof.Edges[i].DefinesSlope && roof.Edges[i].Overhang > 1).Select(i => roof.Edges[i].Id).ToList();

    /// <summary>
    /// The edges a fascia goes round by itself: every edge of the roof, but not one carried back
    /// into another roof it is joined to - a dormer's back edge, buried in the main roof.
    /// </summary>
    /// <summary>
    /// The fascia type that stands out most on a roof: the one whose colour is furthest from
    /// both the walls it runs above and the roof it finishes - a dark board under a light roof on
    /// pale render, a white one on brick. A fascia the colour of its surroundings is a band no
    /// one can see. Null when the project has no fascia types.
    /// </summary>
    public static FasciaType? ContrastingFascia(BimDocument document, Roof roof)
    {
        // How different two colours look, hue as well as lightness: red brick is as far from a
        // grey board as white is, though they are about as light (the "redmean" weighting).
        static double Apart(ColourRgb a, ColourRgb b)
        {
            var mean = (a.R + b.R) / 2.0;
            var (r, g, bl) = ((double)a.R - b.R, (double)a.G - b.G, (double)a.B - b.B);
            return Math.Sqrt((2 + mean / 256) * r * r + 4 * g * g + (2 + (255 - mean) / 256) * bl * bl) / 765;
        }

        ColourRgb? Of(Guid materialId) => document.FindMaterial(materialId)?.SurfaceColour;

        var types = document.TypesOf<FasciaType>().ToList();
        if (types.Count == 0) return null;

        // The roof's finish, and the faces of the walls its edges were picked off.
        var roofLayers = document.FindType<SlabType>(roof.TypeId)?.Structure.Layers;
        var roofTop = roofLayers is { Count: > 0 } ? Of(roofLayers[0].MaterialId) : null;

        var walls = new List<ColourRgb>();
        foreach (var edge in roof.Edges.Where(edge => edge.WallId is not null))
        {
            if (document.Walls.FirstOrDefault(wall => wall.Id == edge.WallId) is not { } wall || document.GetWallType(wall) is not { } type) continue;
            var layers = type.Structure.Layers;
            if (layers.Count == 0) continue;

            // The face the edge was picked off: the exterior one unless the wall was picked from inside.
            var outside = edge.OnLeftOfWall == !wall.Flipped;
            if (Of((outside ? layers[0] : layers[^1]).MaterialId) is { } colour) walls.Add(colour);
        }

        return types
            .OrderByDescending(type =>
            {
                if (Of(type.MaterialId) is not { } own) return 0;
                var fromWalls = walls.Count > 0 ? walls.Min(wall => Apart(own, wall)) : 1;
                return Math.Min(fromWalls, roofTop is { } top ? Apart(own, top) : 1);
            })
            .ThenBy(type => type.Name, StringComparer.OrdinalIgnoreCase)
            .First();
    }

    public static IReadOnlyList<Guid> FasciaEdges(BimDocument document, Roof roof) =>
        OpenEdges(document, roof).Select(i => roof.Edges[i].Id).ToList();

    /// <summary>The edges a gutter goes along by itself: the eaves, where the water runs off.</summary>
    public static IReadOnlyList<Guid> GutterEdges(BimDocument document, Roof roof) =>
        OpenEdges(document, roof).Where(i => roof.Edges[i].DefinesSlope).Select(i => roof.Edges[i].Id).ToList();

    private static IEnumerable<int> OpenEdges(BimDocument document, Roof roof)
    {
        var count = roof.Boundary.Count;
        var target = document.Elements.OfType<Roof>().FirstOrDefault(other => other.Id == roof.JoinedTo);

        for (var i = 0; i < count && i < roof.Edges.Count; i++)
        {
            var middle = roof.Boundary[i].MidpointTo(roof.Boundary[(i + 1) % count]);
            if (target is not null && target.Contains(middle) && roof.TopAt(document, middle) <= target.TopAt(document, middle) + 1) continue;
            yield return i;
        }
    }
}

/// <summary>Changes which roof edges a fascia or gutter runs along, as one undoable step.</summary>
public sealed class SetRoofEdgeSweepEdgesCommand : IUndoableCommand
{
    private readonly RoofEdgeSweep _sweep;
    private readonly List<Guid> _before;
    private readonly List<Guid> _after;

    public SetRoofEdgeSweepEdgesCommand(RoofEdgeSweep sweep, IEnumerable<Guid> edges, string name)
    {
        _sweep = sweep;
        _before = sweep.EdgeIds.ToList();
        _after = edges.Distinct().ToList();
        Name = name;
    }

    public string Name { get; }

    public void Redo() => Set(_after);

    public void Undo() => Set(_before);

    private void Set(IEnumerable<Guid> edges)
    {
        _sweep.EdgeIds.Clear();
        _sweep.EdgeIds.AddRange(edges);
    }
}
