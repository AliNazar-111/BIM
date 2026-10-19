using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;
using BIMDesigner.Core.Parameters;

namespace BIMDesigner.Core.Architecture;

/// <summary>What a mass is topped with.</summary>
public enum MassTop
{
    /// <summary>Flat: a prism, or a cylinder.</summary>
    Flat,

    /// <summary>Its sides drawn in as they rise, to a smaller top or to a point: a frustum, a pyramid, a cone.</summary>
    Tapered,

    /// <summary>Sloping up from its two long sides to a ridge, gable ends between.</summary>
    Gable,

    /// <summary>Sloping up from every side.</summary>
    Hip,

    /// <summary>Sloping up from its longest side only.</summary>
    Shed
}

/// <summary>What a mass's face was made into, so it can be brought back to the face when the mass changes.</summary>
public enum MassLinkKind
{
    Wall,
    CurtainWall,
    Floor,
    Roof
}

/// <summary>An element made from a mass's face: what it is, the face (a side's index; -1 for its top), and for a floor its level.</summary>
public sealed record MassLink(Guid ElementId, MassLinkKind Kind, int Face, Guid? LevelId);

/// <summary>A face of a mass: a side (by the index of its edge round the footprint), its top, or its bottom.</summary>
public enum MassFaceKind
{
    Side,
    Top,
    Bottom
}

/// <summary>One flat face of a mass, as a polygon in space.</summary>
public sealed record MassFace(MassFaceKind Kind, int Index, IReadOnlyList<Point3D> Points);

/// <summary>
/// A mass: Revit's conceptual mass - the shape of a building before it is a building. A footprint
/// - drawn round, a box, or a circle - rising so high, flat-topped, drawn in to a smaller top or a
/// point, or roofed with a gable, a hip or a shed. Cut by mass floors at the levels asked for, it
/// gives the building's gross floor area and volume; its faces are made into walls, curtain
/// walls, floors and a roof by face, and brought back to them when it changes.
/// </summary>
public sealed class Mass : Element
{
    private readonly List<Point2D> _boundary = new();

    public override BuiltInCategory Category => BuiltInCategory.Masses;

    /// <summary>Its footprint, anticlockwise.</summary>
    public IReadOnlyList<Point2D> Boundary => _boundary;

    public void SetBoundary(IEnumerable<Point2D> points)
    {
        var replacement = points.ToList();
        _boundary.Clear();
        _boundary.AddRange(replacement);
        if (Polygon2D.SignedArea(_boundary) < 0) _boundary.Reverse();
    }

    /// <summary>Whether it is round - a cylinder or a cone - its footprint a circle about its centre.</summary>
    public bool Round { get; private set; }

    public Point2D Centre { get; private set; }

    public double Radius { get; private set; }

    /// <summary>How many straight pieces a round mass's circle is made of.</summary>
    public const int CircleSides = 48;

    /// <summary>Makes it round: a circle of so many straight pieces about a centre.</summary>
    public void SetCircle(Point2D centre, double radius)
    {
        (Round, Centre, Radius) = (true, centre, Math.Max(100, radius));
        SetBoundary(Enumerable.Range(0, CircleSides).Select(i => 2 * Math.PI * i / CircleSides)
            .Select(angle => centre + new Vector2D(Math.Cos(angle), Math.Sin(angle)) * Radius));
    }

    /// <summary>Its round footprint moved as the mass is: by a move, a turn, a mirror or a scale.</summary>
    public void TransformCircle(Func<Point2D, Point2D> map)
    {
        if (!Round) return;
        var edge = map(Centre + new Vector2D(Radius, 0));
        Centre = map(Centre);
        Radius = Math.Max(100, Centre.DistanceTo(edge));
    }

    /// <summary>How far above its level it stands, mm.</summary>
    public double BaseOffset { get; set; }

    /// <summary>How high it rises - to its flat or tapered top, or to its eaves under a sloping top, mm.</summary>
    public double Height { get; set; } = 9000;

    public MassTop Top { get; set; } = MassTop.Flat;

    /// <summary>The pitch of a gable, hip or shed top, degrees.</summary>
    public double Pitch { get; set; } = 35;

    /// <summary>How big a tapered top is beside its footprint: 0 to a point, 1 not tapered.</summary>
    public double TopScale { get; set; } = 0.5;

    /// <summary>The levels it is cut by mass floors at.</summary>
    public List<Guid> FloorLevels { get; } = new();

    /// <summary>The walls, floors and roof made from its faces.</summary>
    public List<MassLink> Links { get; } = new();

    public override IEnumerable<ParameterValue> GetInstanceParameters(BimDocument document)
    {
        yield return ParameterValue.ReadOnly(MassParameters.Level, () => document.FindLevel(LevelId)?.Name ?? string.Empty);
        yield return ParameterValue.Bind(MassParameters.BaseOffset, () => BaseOffset, v => { if (double.IsFinite(v)) BaseOffset = v; });
        yield return ParameterValue.BindValidated(MassParameters.Height, () => Height, (double v) =>
        {
            if (!double.IsFinite(v) || v < 300 || v > 500000) return false;
            Height = v;
            return true;
        });
        var tops = Round ? new[] { MassTop.Flat, MassTop.Tapered } : Enum.GetValues<MassTop>();
        yield return ParameterValue.BindChoice(MassParameters.Top,
            () => EnumText.Humanise(Top),
            v => { if (EnumText.TryParse<MassTop>(v, out var top) && tops.Contains(top)) Top = top; },
            tops.Select(top => EnumText.Humanise(top)).ToArray());
        if (Top is MassTop.Gable or MassTop.Hip or MassTop.Shed)
            yield return ParameterValue.BindValidated(MassParameters.Pitch, () => Pitch, (double v) =>
            {
                if (!double.IsFinite(v) || v < 1 || v > 75) return false;
                Pitch = v;
                return true;
            });
        if (Top == MassTop.Tapered)
            yield return ParameterValue.BindValidated(MassParameters.TopScale, () => TopScale, (double v) =>
            {
                if (!double.IsFinite(v) || v < 0 || v >= 1) return false;
                TopScale = v;
                return true;
            });
        yield return ParameterValue.Bind(MassParameters.FloorsAtEveryLevel,
            () => FloorLevels.Count > 0 && Masses.LevelsWithin(document, this).All(FloorLevels.Contains),
            v =>
            {
                FloorLevels.Clear();
                if (v) FloorLevels.AddRange(Masses.LevelsWithin(document, this));
            });
        yield return ParameterValue.ReadOnly(MassParameters.MassFloors, () => FloorLevels.Count == 0 ? "None"
            : string.Join(", ", FloorLevels.Select(id => document.FindLevel(id)).OfType<Level>().OrderBy(level => level.Elevation).Select(level => level.Name)));
        yield return ParameterValue.ReadOnly(MassParameters.GrossFloorArea, () => Masses.GrossFloorArea(document, this));
        yield return ParameterValue.ReadOnly(MassParameters.GrossVolume, () => Masses.Volume(this));
        yield return ParameterValue.ReadOnly(MassParameters.GrossSurfaceArea, () => Masses.SurfaceArea(document, this));
        yield return ParameterValue.ReadOnly(MassParameters.MadeByFace, () => Links.Count(link => document.FindElement(link.ElementId) is not null));

        foreach (var parameter in GetCommonParameters(document)) yield return parameter;
    }
}

public static class MassParameters
{
    public static readonly ParameterDefinition Level = new("Level", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition BaseOffset = new("Base Offset", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition Height = new("Height (to eaves or top)", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Top = new("Top", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Pitch = new("Pitch", ParameterDataType.Angle, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition TopScale = new("Top Scale (0 to a point)", ParameterDataType.Number, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition FloorsAtEveryLevel = new("Mass Floors at Every Level", ParameterDataType.YesNo, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition MassFloors = new("Mass Floors", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition GrossFloorArea = new("Gross Floor Area", ParameterDataType.Area, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition GrossVolume = new("Gross Volume", ParameterDataType.Volume, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition GrossSurfaceArea = new("Gross Surface Area", ParameterDataType.Area, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition MadeByFace = new("Elements Made by Face", ParameterDataType.Integer, ParameterBinding.Instance, ParameterGroup.IdentityData);
}

/// <summary>A mass's shape - its faces, its floors, its quantities - and the walls, floors and roof made from its faces.</summary>
public static class Masses
{
    /// <summary>The height of its foot.</summary>
    public static double Base(BimDocument document, Mass mass) => (document.FindLevel(mass.LevelId)?.Elevation ?? 0) + mass.BaseOffset;

    private static bool Roofed(Mass mass) => !mass.Round && mass.Top is MassTop.Gable or MassTop.Hip or MassTop.Shed;

    /// <summary>The scale of a tapered mass at a height up it: 1 at its foot, its top scale at its top.</summary>
    private static double ScaleAt(Mass mass, double up) =>
        mass.Top == MassTop.Tapered ? 1 - (1 - Math.Clamp(mass.TopScale, 0, 1)) * Math.Clamp(up / Math.Max(1, mass.Height), 0, 1) : 1;

    /// <summary>Its footprint drawn in about its middle by a scale.</summary>
    private static IReadOnlyList<Point2D> Scaled(Mass mass, double scale)
    {
        var middle = mass.Round ? mass.Centre : Polygon2D.Centroid(mass.Boundary, mass.Boundary[0]);
        return mass.Boundary.Select(point => middle + (point - middle) * scale).ToList();
    }

    /// <summary>
    /// The edges a sloping top rises from, as a roof's: every one for a hip; those running the way
    /// its longest side does for a gable; its longest side alone for a shed.
    /// </summary>
    public static IReadOnlyList<RoofEdge> TopEdges(Mass mass)
    {
        var boundary = mass.Boundary;
        var count = boundary.Count;
        var longest = Enumerable.Range(0, count).MaxBy(i => boundary[i].DistanceTo(boundary[(i + 1) % count]));
        var way = (boundary[(longest + 1) % count] - boundary[longest]).NormalisedOrDefault(Vector2D.UnitX);
        return Enumerable.Range(0, count).Select(i =>
        {
            var along = (boundary[(i + 1) % count] - boundary[i]).NormalisedOrDefault(Vector2D.UnitX);
            var slopes = mass.Top switch
            {
                MassTop.Hip => true,
                MassTop.Shed => i == longest,
                MassTop.Gable => Math.Abs(along.Cross(way)) < Math.Sin(20 * Math.PI / 180),
                _ => false
            };
            return new RoofEdge { DefinesSlope = slopes, SlopeDegrees = mass.Pitch };
        }).ToList();
    }

    /// <summary>A gable, hip or shed top's surface, its eaves at the mass's height.</summary>
    public static RoofSurface? TopSurface(BimDocument document, Mass mass) =>
        Roofed(mass) && mass.Boundary.Count >= 3 ? RoofShape.Build(mass.Boundary, TopEdges(mass), Base(document, mass) + mass.Height) : null;

    /// <summary>How high the mass's top is over a point of its footprint.</summary>
    private static double TopAt(BimDocument document, Mass mass, RoofSurface? surface, Point2D at) =>
        surface?.HeightAt(at) ?? Base(document, mass) + mass.Height;

    /// <summary>Its highest point.</summary>
    public static double Peak(BimDocument document, Mass mass) =>
        TopSurface(document, mass) is { } surface ? surface.RidgeElevation : Base(document, mass) + mass.Height;

    /// <summary>The points along a side's top, from its far end back to its near one, where a sloping top bends.</summary>
    private static IReadOnlyList<Point2D> SideTopPoints(Mass mass, RoofSurface? surface, Point2D a, Point2D b)
    {
        var stations = new List<double> { 1, 0 };
        if (surface is not null)
            foreach (var (from, to) in surface.BreakLines)
            {
                var r = b - a;
                var s = to - from;
                var denominator = r.X * s.Y - r.Y * s.X;
                if (Math.Abs(denominator) < 1e-9) continue;
                var t = ((from.X - a.X) * s.Y - (from.Y - a.Y) * s.X) / denominator;
                var u = ((from.X - a.X) * r.Y - (from.Y - a.Y) * r.X) / denominator;
                if (t > 1e-6 && t < 1 - 1e-6 && u >= -1e-6 && u <= 1 + 1e-6) stations.Add(t);
            }

        return stations.Distinct().OrderByDescending(t => t).Select(t => a + (b - a) * t).ToList();
    }

    /// <summary>Every face of the mass: its sides in order round it, its top - the faces of a sloping one - and its bottom.</summary>
    public static IReadOnlyList<MassFace> Faces(BimDocument document, Mass mass)
    {
        var boundary = mass.Boundary;
        if (boundary.Count < 3) return Array.Empty<MassFace>();
        var count = boundary.Count;
        var z0 = Base(document, mass);
        var z1 = z0 + mass.Height;
        var surface = TopSurface(document, mass);
        var top = mass.Top == MassTop.Tapered ? Scaled(mass, Math.Clamp(mass.TopScale, 0, 1)) : boundary;
        var faces = new List<MassFace>();

        for (var i = 0; i < count; i++)
        {
            var (a, b) = (boundary[i], boundary[(i + 1) % count]);
            var points = new List<Point3D> { Point3D.On(a, z0), Point3D.On(b, z0) };
            if (mass.Top == MassTop.Tapered)
            {
                points.Add(Point3D.On(top[(i + 1) % count], z1));
                if (mass.TopScale > 1e-6) points.Add(Point3D.On(top[i], z1));
            }
            else points.AddRange(SideTopPoints(mass, surface, a, b).Select(point => Point3D.On(point, TopAt(document, mass, surface, point))));

            faces.Add(new MassFace(MassFaceKind.Side, i, points));
        }

        if (surface is not null)
            faces.AddRange(surface.Facets.Select((facet, i) => new MassFace(MassFaceKind.Top, i, facet.Points3D)));
        else if (mass.Top != MassTop.Tapered || mass.TopScale > 1e-6)
            faces.Add(new MassFace(MassFaceKind.Top, 0, top.Select(point => Point3D.On(point, z1)).ToList()));

        faces.Add(new MassFace(MassFaceKind.Bottom, 0, boundary.Select(point => Point3D.On(point, z0)).Reverse().ToList()));
        return faces;
    }

    /// <summary>A flat polygon's area in space, mm².</summary>
    private static double Area(IReadOnlyList<Point3D> points)
    {
        double x = 0, y = 0, z = 0;
        for (var i = 0; i < points.Count; i++)
        {
            var (p, q) = (points[i], points[(i + 1) % points.Count]);
            x += (p.Y - q.Y) * (p.Z + q.Z);
            y += (p.Z - q.Z) * (p.X + q.X);
            z += (p.X - q.X) * (p.Y + q.Y);
        }

        return Math.Sqrt(x * x + y * y + z * z) / 2;
    }

    public static double SurfaceArea(BimDocument document, Mass mass) => Faces(document, mass).Sum(face => Area(face.Points));

    /// <summary>Its volume, mm³: a prism's, a frustum's, or a prism's with its sloping top on it.</summary>
    public static double Volume(Mass mass)
    {
        if (mass.Boundary.Count < 3) return 0;
        var area = Polygon2D.Area(mass.Boundary);
        var prism = area * mass.Height;
        if (mass.Top == MassTop.Tapered)
        {
            var s = Math.Clamp(mass.TopScale, 0, 1);
            return prism * (1 + s + s * s) / 3;
        }

        if (!Roofed(mass)) return prism;
        var surface = RoofShape.Build(mass.Boundary, TopEdges(mass), mass.Height);
        return prism + surface.Facets.Sum(facet => facet.Area * (facet.Plane.HeightAt(Polygon2D.Centroid(facet.Outline, facet.Outline[0])) - mass.Height));
    }

    /// <summary>The levels the mass rises through - at or above its foot, below its top.</summary>
    public static IReadOnlyList<Guid> LevelsWithin(BimDocument document, Mass mass)
    {
        var (bottom, top) = (Base(document, mass), Peak(document, mass));
        return document.Levels.Where(level => level.Elevation >= bottom - 1 && level.Elevation < top - 1).OrderBy(level => level.Elevation).Select(level => level.Id).ToList();
    }

    /// <summary>The mass cut level at a height: its footprint, drawn in for a taper, or as far as its sloping top reaches.</summary>
    public static IReadOnlyList<IReadOnlyList<Point2D>> Section(BimDocument document, Mass mass, double z)
    {
        if (mass.Boundary.Count < 3) return Array.Empty<IReadOnlyList<Point2D>>();
        var z0 = Base(document, mass);
        var up = z - z0;
        if (up < -1 || z >= Peak(document, mass) - 1) return Array.Empty<IReadOnlyList<Point2D>>();
        if (mass.Top == MassTop.Tapered)
        {
            var scale = ScaleAt(mass, up);
            return scale > 1e-3 ? new[] { Scaled(mass, scale) } : Array.Empty<IReadOnlyList<Point2D>>();
        }

        if (up <= mass.Height + 1e-6 || TopSurface(document, mass) is not { } surface) return new[] { mass.Boundary };

        // Above the eaves: where the sloping top is still over the cut - each face's part higher than it.
        IReadOnlyList<PolygonBoolean.Region> kept = Array.Empty<PolygonBoolean.Region>();
        foreach (var facet in surface.Facets)
        {
            var above = Above(facet.Outline, facet.Plane, z);
            foreach (var piece in above)
                kept = kept.Count == 0 ? new[] { piece } : PolygonBoolean.Combine(kept.Select(region => region.Outer).ToList(), new[] { piece.Outer }, BooleanOperation.Union);
        }

        return kept.Select(region => region.Outer).Where(outline => Polygon2D.Area(outline) > 1e4).ToList();
    }

    /// <summary>The part of a face's outline where its plane is at least a height.</summary>
    private static IReadOnlyList<PolygonBoolean.Region> Above(IReadOnlyList<Point2D> outline, RoofPlane plane, double z)
    {
        var (a, b) = (plane.A, plane.B);
        var length = Math.Sqrt(a * a + b * b);
        if (length < 1e-12) return plane.C >= z ? new[] { new PolygonBoolean.Region(outline, Array.Empty<IReadOnlyList<Point2D>>()) } : Array.Empty<PolygonBoolean.Region>();

        // a·x + b·y + c ≥ z: the half-plane past the line where the plane is at z, as a rectangle far bigger than the face.
        var normal = new Vector2D(a / length, b / length);
        var on = new Point2D(normal.X * (z - plane.C) / length, normal.Y * (z - plane.C) / length);
        var reach = outline.Max(point => point.DistanceTo(on)) * 4 + 1e6;
        var side = normal.PerpendicularLeft();
        var rectangle = new[] { on - side * reach, on + side * reach, on + side * reach + normal * reach, on - side * reach + normal * reach };
        return PolygonBoolean.Combine(outline, rectangle, BooleanOperation.Intersection);
    }

    /// <summary>The floor area of the mass at each of its mass floors, and their total, mm².</summary>
    public static double GrossFloorArea(BimDocument document, Mass mass) =>
        mass.FloorLevels.Select(id => document.FindLevel(id)).OfType<Level>()
            .Sum(level => Section(document, mass, level.Elevation).Sum(Polygon2D.Area));

    /// <summary>The mass as a see-through solid in 3D, face by face.</summary>
    public static Mesh3D Mesh(BimDocument document, Mass mass)
    {
        var mesh = new Mesh3D(mass.Id, mass.LevelId, MeshKind.Wall, new ColourRgb(0xB8, 0x9B, 0xD9), "Mass") { Opacity = 0.35 };
        var centre = Polygon2D.Centroid(mass.Boundary, mass.Boundary.Count > 0 ? mass.Boundary[0] : default);
        var middle = Point3D.On(centre, Base(document, mass) + mass.Height / 2);
        foreach (var face in Faces(document, mass))
        {
            if (face.Points.Count < 3) continue;

            // Laid out flat in the face's own plane, to be cut into triangles.
            var flat = Flatten(face.Points);
            var triangles = Polygon2D.Triangulate(flat);
            var normal = Normal(face.Points);
            var at = face.Points.Aggregate((p, q) => new Point3D(p.X + q.X, p.Y + q.Y, p.Z + q.Z));
            var faceMiddle = new Point3D(at.X / face.Points.Count, at.Y / face.Points.Count, at.Z / face.Points.Count);
            var outward = new Vector3(faceMiddle.X - middle.X, faceMiddle.Y - middle.Y, faceMiddle.Z - middle.Z).Dot(normal) >= 0 ? normal : -normal;
            mesh.AddFace(face.Points, triangles, new Point3D(faceMiddle.X + outward.X * 1000, faceMiddle.Y + outward.Y * 1000, faceMiddle.Z + outward.Z * 1000));
            if (!mass.Round || face.Kind != MassFaceKind.Side)
                for (var i = 0; i < face.Points.Count; i++) mesh.AddEdge(face.Points[i], face.Points[(i + 1) % face.Points.Count]);
        }

        return mesh;
    }

    private static Vector3 Normal(IReadOnlyList<Point3D> points)
    {
        double x = 0, y = 0, z = 0;
        for (var i = 0; i < points.Count; i++)
        {
            var (p, q) = (points[i], points[(i + 1) % points.Count]);
            x += (p.Y - q.Y) * (p.Z + q.Z);
            y += (p.Z - q.Z) * (p.X + q.X);
            z += (p.X - q.X) * (p.Y + q.Y);
        }

        return new Vector3(x, y, z).Normalised;
    }

    /// <summary>A flat polygon in space laid out in its own plane.</summary>
    private static IReadOnlyList<Point2D> Flatten(IReadOnlyList<Point3D> points)
    {
        var normal = Normal(points);
        var u = Math.Abs(normal.Z) > 0.9 ? new Vector3(1, 0, 0) : new Vector3(0, 0, 1).Cross(normal).Normalised;
        var v = normal.Cross(u);
        var origin = points[0];
        return points.Select(point => new Vector3(point.X - origin.X, point.Y - origin.Y, point.Z - origin.Z))
            .Select(d => new Point2D(d.Dot(u), d.Dot(v))).ToList();
    }

    /// <summary>The side of a mass a point in plan is on or just beside, within a reach, or -1.</summary>
    public static int SideAt(Mass mass, Point2D at, double reach) => SideAt(mass.Boundary, at, reach);

    /// <summary>The side of an outline a point is on or just beside, within a reach, or -1.</summary>
    public static int SideAt(IReadOnlyList<Point2D> outline, Point2D at, double reach)
    {
        var best = -1;
        for (var i = 0; i < outline.Count; i++)
        {
            var distance = Line2D.DistanceFromSegment(at, outline[i], outline[(i + 1) % outline.Count]);
            if (distance >= reach) continue;
            (best, reach) = (i, distance);
        }

        return best;
    }

    /// <summary>The face of a mass nearest a point in space - where a click in 3D landed on it.</summary>
    public static MassFace? FaceAt(BimDocument document, Mass mass, Point3D at)
    {
        MassFace? best = null;
        var nearest = double.MaxValue;
        foreach (var face in Faces(document, mass))
        {
            var normal = Normal(face.Points);
            var p = face.Points[0];
            var distance = Math.Abs(new Vector3(at.X - p.X, at.Y - p.Y, at.Z - p.Z).Dot(normal));
            if (distance >= nearest) continue;
            (best, nearest) = (face, distance);
        }

        return best;
    }

    // ---- by face ----------------------------------------------------------------------

    /// <summary>
    /// The wall a side of a mass is made into - or, round, the two half circles round it - its
    /// outside face on the mass's face: square up a plain side, leaning in up a tapered one,
    /// its gable shaped to the top over a gable end. Null, with why, where the face cannot carry one.
    /// </summary>
    public static IReadOnlyList<Wall>? WallsByFace(BimDocument document, Mass mass, int side, Guid typeId, out string? why)
    {
        why = null;
        if (mass.Boundary.Count < 3) return null;
        if (mass.Top == MassTop.Tapered && mass.TopScale < 1e-6)
        {
            why = "Its sides come to a point: they cannot carry walls. Roof by Face makes it a roof.";
            return null;
        }

        if (mass.Round)
        {
            if (mass.Top == MassTop.Tapered)
            {
                why = "A cone's sloping side cannot carry a curved wall. Roof by Face makes it a conical roof.";
                return null;
            }

            // Two half circles, drawn clockwise so their outsides face out.
            var (east, west) = (mass.Centre + new Vector2D(mass.Radius, 0), mass.Centre - new Vector2D(mass.Radius, 0));
            return new[] { Halves(east, west), Halves(west, east) };

            Wall Halves(Point2D from, Point2D to) => new()
            {
                Start = from, End = to, Bulge = -1, TypeId = typeId, LevelId = mass.LevelId, BaseOffset = mass.BaseOffset,
                UnconnectedHeight = mass.Height, LocationLine = WallLocationLine.FinishFaceExterior
            };
        }

        var count = mass.Boundary.Count;
        var (a, b) = (mass.Boundary[side % count], mass.Boundary[(side + 1) % count]);

        // From its far end back, so its outside is on the outside of the mass.
        var wall = new Wall
        {
            Start = b, End = a, TypeId = typeId, LevelId = mass.LevelId, BaseOffset = mass.BaseOffset,
            UnconnectedHeight = mass.Height, LocationLine = WallLocationLine.FinishFaceExterior
        };

        if (mass.Top == MassTop.Tapered)
        {
            // Leaning in as the side does: in by as much as the top is drawn in, over its height.
            var top = Scaled(mass, mass.TopScale);
            var inward = (b - a).NormalisedOrDefault(Vector2D.UnitX).PerpendicularLeft();
            var drawnIn = (top[side % count] - a).Dot(inward);
            var lean = Math.Atan2(drawnIn, mass.Height) * 180 / Math.PI;
            if (Math.Abs(lean) > WallLean.MaxAngle)
            {
                why = $"That side leans {Math.Abs(lean):0}°: a wall leans {WallLean.MaxAngle:0}° at most.";
                return null;
            }

            wall.CrossSection = WallCrossSection.Slanted;
            wall.SlantAngle = -lean;
            return new[] { wall };
        }

        // Under a sloping top, a side that is not an eave rises to it: its gable, in the wall's profile.
        if (TopSurface(document, mass) is { } surface && !TopEdges(mass)[side % count].DefinesSlope)
        {
            var z0 = Base(document, mass);
            var length = a.DistanceTo(b);
            var tops = SideTopPoints(mass, surface, a, b).Reverse().Select(point => new Point2D(point.DistanceTo(b), surface.HeightAt(point) - z0)).ToList();
            var profile = new List<Point2D> { new(0, 0), new(length, 0) };
            profile.AddRange(tops.OrderByDescending(point => point.X));
            if (WallProfile.Problem(profile, length) is null)
            {
                wall.UnconnectedHeight = Math.Max(mass.Height, profile.Max(point => point.Y));
                wall.Profile = profile;
                wall.ProfileLength = length;
            }
        }

        return new[] { wall };
    }

    /// <summary>The roof a mass's top is made into: flat on a flat top; pitched as its sloping top, or from its foot up a taper - a pyramid or a cone, cut off flat at a smaller top.</summary>
    public static Roof? RoofByFace(BimDocument document, Mass mass, Guid typeId, out string? why)
    {
        why = null;
        if (mass.Boundary.Count < 3) return null;
        var roof = new Roof { TypeId = typeId, LevelId = mass.LevelId };
        roof.SetBoundary(mass.Boundary);

        switch (mass.Top)
        {
            case MassTop.Flat:
                roof.HeightOffset = mass.BaseOffset + mass.Height;
                roof.SetEdges(mass.Boundary.Select(_ => new RoofEdge { DefinesSlope = false }));
                break;

            case MassTop.Tapered:
            {
                // Each side's pitch: up its height, in as far as its top is drawn in.
                roof.HeightOffset = mass.BaseOffset;
                var top = Scaled(mass, mass.TopScale);
                var arc = mass.Round ? Guid.NewGuid() : (Guid?)null;
                var count = mass.Boundary.Count;
                roof.SetEdges(Enumerable.Range(0, count).Select(i =>
                {
                    var (a, b) = (mass.Boundary[i], mass.Boundary[(i + 1) % count]);
                    var inward = (b - a).NormalisedOrDefault(Vector2D.UnitX).PerpendicularLeft();
                    var run = Math.Max(1, (top[i] - a).Dot(inward));
                    return new RoofEdge { DefinesSlope = true, SlopeDegrees = Math.Atan2(mass.Height, run) * 180 / Math.PI, ArcId = arc };
                }));
                if (mass.TopScale > 1e-6) roof.CutoffOffset = mass.Height;
                break;
            }

            default:
                roof.HeightOffset = mass.BaseOffset + mass.Height;
                roof.SetEdges(TopEdges(mass));
                break;
        }

        return roof;
    }

    /// <summary>The floors a mass's floors are made into, one at each of its mass floors, its outline cut there.</summary>
    public static IReadOnlyList<Floor> FloorsByFace(BimDocument document, Mass mass, Guid typeId)
    {
        var floors = new List<Floor>();
        foreach (var level in mass.FloorLevels.Select(id => document.FindLevel(id)).OfType<Level>().OrderBy(level => level.Elevation))
        foreach (var outline in Section(document, mass, level.Elevation))
        {
            var floor = new Floor { TypeId = typeId, LevelId = level.Id };
            floor.SetBoundary(outline);
            floors.Add(floor);
        }

        return floors;
    }

    /// <summary>The changes that bring what was made from a mass's faces back to them, as one step to undo; null with nothing to change.</summary>
    public static IUndoableCommand? UpdateToFace(BimDocument document, Mass mass, out int changed)
    {
        var steps = new List<IUndoableCommand>();
        foreach (var link in mass.Links.ToList())
        {
            if (document.FindElement(link.ElementId) is not { } element) continue;
            switch (element)
            {
                case Wall wall when link.Kind is MassLinkKind.Wall or MassLinkKind.CurtainWall:
                    if (WallsByFace(document, mass, link.Face, wall.TypeId, out _) is { Count: > 0 } walls)
                    {
                        // A round mass's two halves: each the one it was.
                        var made = walls[Math.Clamp(mass.Round ? mass.Links.Where(other => other.Face == link.Face && other.Kind == link.Kind).ToList().IndexOf(link) : 0, 0, walls.Count - 1)];
                        steps.Add(new WallShapeCommand(wall, made));
                    }

                    break;

                case Floor floor when link.Kind == MassLinkKind.Floor && link.LevelId is { } levelId && document.FindLevel(levelId) is { } level:
                    if (Section(document, mass, level.Elevation).OrderByDescending(Polygon2D.Area).FirstOrDefault() is { } outline)
                        steps.Add(new SetSlabSketchCommand(floor, outline, floor.Holes, floor.SpanDirection));
                    break;

                case Roof roof when link.Kind == MassLinkKind.Roof:
                    if (RoofByFace(document, mass, roof.TypeId, out _) is { } shaped)
                        steps.Add(new RoofShapeCommand(roof, shaped));
                    break;
            }
        }

        changed = steps.Count;
        return steps.Count == 0 ? null : new CompositeCommand("Update to Face", steps);
    }

    /// <summary>A wall given another's line, height, lean and profile - and back.</summary>
    private sealed class WallShapeCommand : IUndoableCommand
    {
        private readonly Wall _wall;
        private readonly (Point2D Start, Point2D End, double Bulge, double BaseOffset, double Height, WallCrossSection Section, double Slant, IReadOnlyList<Point2D>? Profile, double ProfileLength) _before, _after;

        public WallShapeCommand(Wall wall, Wall shape)
        {
            _wall = wall;
            _before = Of(wall);
            _after = Of(shape);
        }

        private static (Point2D, Point2D, double, double, double, WallCrossSection, double, IReadOnlyList<Point2D>?, double) Of(Wall wall) =>
            (wall.Start, wall.End, wall.Bulge, wall.BaseOffset, wall.UnconnectedHeight, wall.CrossSection, wall.SlantAngle, wall.Profile, wall.ProfileLength);

        public string Name => "Update to Face";

        public void Redo() => Apply(_after);

        public void Undo() => Apply(_before);

        private void Apply((Point2D Start, Point2D End, double Bulge, double BaseOffset, double Height, WallCrossSection Section, double Slant, IReadOnlyList<Point2D>? Profile, double ProfileLength) shape)
        {
            (_wall.Start, _wall.End, _wall.Bulge, _wall.BaseOffset, _wall.UnconnectedHeight) = (shape.Start, shape.End, shape.Bulge, shape.BaseOffset, shape.Height);
            (_wall.CrossSection, _wall.SlantAngle) = (shape.Section, shape.Slant);
            (_wall.Profile, _wall.ProfileLength) = (shape.Profile, shape.ProfileLength);
        }
    }

    /// <summary>A roof given another's outline, edges, height and cut-off - and back.</summary>
    private sealed class RoofShapeCommand : IUndoableCommand
    {
        private readonly Roof _roof;
        private readonly (List<Point2D> Boundary, List<RoofEdge> Edges, double Height, double Cutoff) _before, _after;

        public RoofShapeCommand(Roof roof, Roof shape)
        {
            _roof = roof;
            _before = (roof.Boundary.ToList(), roof.Edges.Select(edge => edge.Copy()).ToList(), roof.HeightOffset, roof.CutoffOffset);
            _after = (shape.Boundary.ToList(), shape.Edges.Select(edge => edge.Copy()).ToList(), shape.HeightOffset, shape.CutoffOffset);
        }

        public string Name => "Update to Face";

        public void Redo() => Apply(_after);

        public void Undo() => Apply(_before);

        private void Apply((List<Point2D> Boundary, List<RoofEdge> Edges, double Height, double Cutoff) shape)
        {
            _roof.SetBoundary(shape.Boundary);
            _roof.SetEdges(shape.Edges.Select(edge => edge.Copy()));
            (_roof.HeightOffset, _roof.CutoffOffset) = (shape.Height, shape.Cutoff);
        }
    }
}

/// <summary>Elements made from a mass's faces put in the model, and remembered by the mass so they can be brought back to it - as one step to undo.</summary>
public sealed class MakeByFaceCommand : IUndoableCommand
{
    private readonly BimDocument _document;
    private readonly Mass _mass;
    private readonly List<(Element Element, MassLink Link)> _made;

    public MakeByFaceCommand(BimDocument document, Mass mass, IEnumerable<(Element Element, MassLink Link)> made, string name)
    {
        (_document, _mass, _made, Name) = (document, mass, made.ToList(), name);
    }

    public string Name { get; }

    public void Redo()
    {
        foreach (var (element, link) in _made)
        {
            _document.Add(element);
            _mass.Links.Add(link);
        }
    }

    public void Undo()
    {
        foreach (var (element, link) in _made)
        {
            _mass.Links.Remove(link);
            _document.Remove(element);
        }
    }
}

/// <summary>A mass's mass floors set afresh - and back.</summary>
public sealed class SetMassFloorsCommand : IUndoableCommand
{
    private readonly Mass _mass;
    private readonly List<Guid> _before, _after;

    public SetMassFloorsCommand(Mass mass, IEnumerable<Guid> levels)
    {
        _mass = mass;
        _before = mass.FloorLevels.ToList();
        _after = levels.ToList();
    }

    public string Name => "Mass Floors";

    public void Redo() => Set(_after);

    public void Undo() => Set(_before);

    private void Set(List<Guid> levels)
    {
        _mass.FloorLevels.Clear();
        _mass.FloorLevels.AddRange(levels);
    }
}
