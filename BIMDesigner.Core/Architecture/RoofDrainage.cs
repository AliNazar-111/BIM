using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;
using BIMDesigner.Core.Parameters;

namespace BIMDesigner.Core.Architecture;

/// <summary>Where a roof drain's water goes.</summary>
public enum DrainDischarge
{
    /// <summary>Down a rainwater pipe inside the building, through the floors, to the drain under the ground floor.</summary>
    Internal,

    /// <summary>Out through the roof's edge into a hopper head on the outside wall, and down a downpipe to the ground.</summary>
    ThroughEdge
}

/// <summary>
/// A roof drain - a rainwater outlet - in a flat roof. The roof's insulation is tapered to fall
/// to its drains: thinnest at each drain, rising away from it at the roof's fall, so water runs
/// down each face to a valley and along the valley into the drain; where two drains' falls meet
/// there is a ridge. Hosted by its roof: moved, copied and deleted with it.
/// </summary>
public sealed class RoofDrain : Element, IHostedElement
{
    public override BuiltInCategory Category => BuiltInCategory.RoofDrains;

    /// <summary>The flat roof it drains.</summary>
    public Guid RoofId { get; set; }

    public Guid HostId => RoofId;

    /// <summary>Where it is, in plan.</summary>
    public Point2D Location { get; set; }

    /// <summary>The size of its outlet, mm.</summary>
    public double OutletDiameter { get; set; } = 100;

    /// <summary>Where its water goes: down inside the building, or out through the roof's edge.</summary>
    public DrainDischarge Discharge { get; set; } = DrainDischarge.Internal;

    public override IEnumerable<ParameterValue> GetInstanceParameters(BimDocument document)
    {
        var roof = document.Elements.OfType<Roof>().FirstOrDefault(candidate => candidate.Id == RoofId);

        yield return ParameterValue.ReadOnly(RoofDrainParameters.Roof,
            () => roof is null ? "<none>" : document.FindType<SlabType>(roof.TypeId)?.Name ?? "Roof");
        yield return ParameterValue.BindValidated(RoofDrainParameters.OutletDiameter, () => OutletDiameter, (double v) =>
        {
            if (v < 40 || v > 300) return false;
            OutletDiameter = v;
            return true;
        });
        yield return ParameterValue.BindChoiceValidated(RoofDrainParameters.Discharge,
            () => RoofDrainage.DischargeName(Discharge),
            name =>
            {
                var discharge = name == RoofDrainage.DischargeName(DrainDischarge.ThroughEdge) ? DrainDischarge.ThroughEdge : DrainDischarge.Internal;

                // An outlet through the edge sits at the edge.
                if (discharge == DrainDischarge.ThroughEdge && roof is not null && RoofDrainage.EdgeDistance(roof, Location) > RoofDrainage.EdgeOutletReach) return false;
                Discharge = discharge;
                return true;
            },
            new[] { RoofDrainage.DischargeName(DrainDischarge.Internal), RoofDrainage.DischargeName(DrainDischarge.ThroughEdge) });
        yield return ParameterValue.ReadOnly(RoofDrainParameters.PipeLength, () => RoofDrainage.Pipe(document, this)?.Length ?? 0);
        yield return ParameterValue.ReadOnly(RoofDrainParameters.CatchmentArea,
            () => roof is null ? 0 : RoofDrainage.CatchmentArea(document, roof, this));
        yield return ParameterValue.ReadOnly(RoofDrainParameters.HighestFall,
            () => roof is null ? 0 : RoofDrainage.Regions(document, roof).Where(region => ReferenceEquals(region.Drain, this))
                .SelectMany(region => region.Outline.Select(region.Rise)).DefaultIfEmpty(0).Max());

        foreach (var parameter in GetCommonParameters(document)) yield return parameter;
    }
}

/// <summary>
/// One face of a flat roof's falls: where it is in plan, the drain it runs to, the way down it,
/// and how far its top rises above the drain's - a plane, rise = A x + B y + C.
/// </summary>
public sealed record DrainageRegion(IReadOnlyList<Point2D> Outline, RoofDrain Drain, Vector2D Downhill, double A, double B, double C)
{
    public double Rise(Point2D point) => A * point.X + B * point.Y + C;
}

/// <summary>
/// Flat roofs that drain: falls to their drains, made by tapering the insulation.
///
/// Each drain's falls are an upside-down pyramid set square to the roof: four faces falling
/// straight at the drain, meeting in valleys along the diagonals - which is how tapered
/// insulation boards are laid out, and why the fall along a valley is shallower than the faces'.
/// Where two drains' pyramids meet they make a ridge between them. The deck stays flat; the
/// insulation gets thicker away from the drains, and what is on top of it rises with it.
/// </summary>
public static class RoofDrainage
{
    /// <summary>The fall a flat roof is designed to, one in this many: 1 in 40, so that 1 in 80 is left once it is built (BS 6229).</summary>
    public const double DesignFall = 40;

    /// <summary>The least fall a finished flat roof may have, one in this many.</summary>
    public const double FinishedFall = 80;

    /// <summary>How near a roof's edge, or another drain, a drain may go, mm.</summary>
    public const double EdgeClearance = 150, DrainSpacing = 500;

    public static IEnumerable<RoofDrain> On(BimDocument document, Roof roof) =>
        document.Elements.OfType<RoofDrain>().Where(drain => drain.RoofId == roof.Id);

    /// <summary>Whether a roof is one that drains to outlets: flat, drawn by footprint, with a drain in it.</summary>
    public static bool Drains(BimDocument document, Roof roof) =>
        CanDrain(document, roof) && On(document, roof).Any();

    /// <summary>Whether a roof can take drains: one drawn by footprint, and flat.</summary>
    public static bool CanDrain(BimDocument document, Roof roof) =>
        !roof.IsExtrusion && roof.Boundary.Count >= 3 && roof.Surface(document).IsFlat;

    /// <summary>How much a roof falls, as a fraction: 1 in 40 is 0.025.</summary>
    public static double Fall(Roof roof) => 1 / Math.Max(roof.DrainageFall, 1);

    /// <summary>The roof's own square: along its longest edge, and across it.</summary>
    public static (Vector2D Along, Vector2D Across) Axes(Roof roof)
    {
        var boundary = roof.Boundary;
        var longest = Enumerable.Range(0, boundary.Count)
            .Select(i => boundary[(i + 1) % boundary.Count] - boundary[i])
            .OrderByDescending(edge => edge.Length)
            .FirstOrDefault();

        var along = longest.NormalisedOrDefault(Vector2D.UnitX);
        return (along, along.PerpendicularLeft());
    }

    /// <summary>The four ways a drain's falls rise away from it.</summary>
    private static Vector2D[] Ways(Roof roof)
    {
        var (along, across) = Axes(roof);
        return new[] { along, -along, across, -across };
    }

    /// <summary>
    /// How far a roof's top rises over a point above where it is at its drains: the fall times the
    /// distance to the nearest drain, measured square to the roof - the way tapered boards fall.
    /// </summary>
    public static double Rise(BimDocument document, Roof roof, Point2D at)
    {
        if (!Drains(document, roof)) return 0;

        var ways = Ways(roof);
        var nearest = On(document, roof).Min(drain => ways.Max(way => (at - drain.Location).Dot(way)));
        return Fall(roof) * Math.Max(0, nearest);
    }

    /// <summary>The faces of a flat roof's falls, each falling to one drain in one direction.</summary>
    public static IReadOnlyList<DrainageRegion> Regions(BimDocument document, Roof roof)
    {
        if (!Drains(document, roof)) return Array.Empty<DrainageRegion>();

        var drains = On(document, roof).ToList();
        var ways = Ways(roof);
        var fall = Fall(roof);
        var reach = roof.Boundary.Concat(drains.Select(drain => drain.Location))
            .SelectMany(point => roof.Boundary.Select(other => other.DistanceTo(point)))
            .Max() * 3 + 1000;
        var centre = new Point2D(roof.Boundary.Average(point => point.X), roof.Boundary.Average(point => point.Y));
        var box = new List<Point2D>
        {
            centre + new Vector2D(-reach, -reach), centre + new Vector2D(reach, -reach),
            centre + new Vector2D(reach, reach), centre + new Vector2D(-reach, reach)
        };

        var regions = new List<DrainageRegion>();
        foreach (var drain in drains)
        foreach (var way in ways)
        {
            var q = drain.Location;

            // Where this way is the one it rises fastest: between the diagonals.
            var wedge = (IReadOnlyList<Point2D>)box;
            foreach (var other in ways.Where(other => other != way))
                wedge = Clip(wedge, way - other, (way - other).Dot(new Vector2D(q.X, q.Y)));
            if (wedge.Count < 3) continue;

            IReadOnlyList<PolygonBoolean.Region> parts = PolygonBoolean.Combine(roof.Boundary, wedge, BooleanOperation.Intersection);

            // Less where another drain is nearer. Where two are as near - the roof is the same
            // height either way - it goes to the nearer as the crow flies, so two drains side by
            // side share the roof between them evenly.
            foreach (var rival in drains.Where(rival => !ReferenceEquals(rival, drain)))
            {
                var r = rival.Location;
                var nearer = Nearer(box, ways, way, q, r, strictly: true);
                var asNear = Nearer(box, ways, way, q, r, strictly: false);
                if (asNear.Count >= 3)
                    asNear = Clip(asNear, r - q, ((r.X * r.X + r.Y * r.Y) - (q.X * q.X + q.Y * q.Y)) / 2);

                foreach (var taken in new[] { nearer, asNear }.Where(taken => taken.Count >= 3))
                    parts = parts.SelectMany(part => PolygonBoolean.Combine(Shafts.Loops(part), new[] { taken }, BooleanOperation.Difference)).ToList();
            }

            foreach (var part in parts.Where(part => part.Area > 100))
            foreach (var outline in PolygonBoolean.WithoutHoles(part))
                regions.Add(new DrainageRegion(outline, drain, -way, fall * way.X, fall * way.Y, -fall * way.Dot(new Vector2D(q.X, q.Y))));
        }

        return regions;
    }

    /// <summary>
    /// Where rising one way from a drain at q is further than - or, not strictly, as far as -
    /// the nearest a rival drain at r is, measured square to the roof: where the rival is nearer.
    /// </summary>
    private static IReadOnlyList<Point2D> Nearer(IReadOnlyList<Point2D> box, Vector2D[] ways, Vector2D way, Point2D q, Point2D r, bool strictly)
    {
        var nearer = box;
        var nudge = strictly ? 1e-9 : -1e-9;
        foreach (var rivalWay in ways)
        {
            var normal = way - rivalWay;
            var limit = way.Dot(new Vector2D(q.X, q.Y)) - rivalWay.Dot(new Vector2D(r.X, r.Y));
            nearer = normal.Length < 1e-9
                ? (0 >= limit + nudge ? nearer : Array.Empty<Point2D>())
                : Clip(nearer, normal, limit + nudge);
            if (nearer.Count < 3) return Array.Empty<Point2D>();
        }

        return nearer;
    }

    /// <summary>A convex outline cut to where normal · point is at least a value.</summary>
    private static IReadOnlyList<Point2D> Clip(IReadOnlyList<Point2D> polygon, Vector2D normal, double atLeast)
    {
        var kept = new List<Point2D>();
        for (var i = 0; i < polygon.Count; i++)
        {
            var (a, b) = (polygon[i], polygon[(i + 1) % polygon.Count]);
            var (da, db) = (normal.Dot(new Vector2D(a.X, a.Y)) - atLeast, normal.Dot(new Vector2D(b.X, b.Y)) - atLeast);
            if (da >= 0) kept.Add(a);
            if ((da >= 0) != (db >= 0)) kept.Add(a + (b - a) * (da / (da - db)));
        }

        return kept;
    }

    /// <summary>How much roof a drain takes the water from, in plan, mm².</summary>
    public static double CatchmentArea(BimDocument document, Roof roof, RoofDrain drain) =>
        Regions(document, roof).Where(region => ReferenceEquals(region.Drain, drain)).Sum(region => Polygon2D.Area(region.Outline));

    /// <summary>The fall along a valley, one in this many: the faces' fall, made shallower by running at 45° across them.</summary>
    public static double ValleyFall(Roof roof) => roof.DrainageFall * Math.Sqrt(2);

    /// <summary>
    /// The layer that is tapered to make the falls: the insulation, counting from the top; or,
    /// where there is none, the first layer under the finish. Its index in the build-up, or -1.
    /// </summary>
    public static int TaperedLayer(CompoundStructure structure)
    {
        var layers = structure.Layers;
        for (var i = 0; i < layers.Count; i++)
            if (layers[i].Function == LayerFunction.ThermalAir && layers[i].Thickness > 0) return i;

        for (var i = 0; i < layers.Count; i++)
            if (layers[i].Function is not (LayerFunction.Membrane or LayerFunction.Finish1) && layers[i].Thickness > 0) return i;

        return layers.Count > 0 ? 0 : -1;
    }

    /// <summary>
    /// A flat roof's pieces with its falls in them: the tapered layer's top, and everything on
    /// it, raised over each face of the falls by that face's rise; the deck under it untouched.
    /// </summary>
    public static IReadOnlyList<RoofPiece> Taper(BimDocument document, Roof roof, IReadOnlyList<RoofPiece> pieces)
    {
        if (!Drains(document, roof) || document.FindType<SlabType>(roof.TypeId) is not { } type) return pieces;

        var tapered = TaperedLayer(type.Structure);
        if (tapered < 0) return pieces;

        // The tapered layer's underside, above the roof's base: everything from there up rises.
        var offsets = type.Structure.GetLayerOffsets().ToList();
        var total = type.Structure.TotalWidth;
        var taperBottom = roof.BaseElevation(document) + (total - offsets[tapered].End);
        var taperTop = roof.BaseElevation(document) + (total - offsets[tapered].Start);

        var regions = Regions(document, roof);
        var result = new List<RoofPiece>();
        foreach (var piece in pieces)
        {
            // Under the tapered layer: the deck, flat.
            if (piece.Top.C <= taperBottom + 1e-6)
            {
                result.Add(piece);
                continue;
            }

            var isTapered = Math.Abs(piece.Bottom.C - taperBottom) < 1e-6 && Math.Abs(piece.Top.C - taperTop) < 1e-6;
            foreach (var region in regions)
            foreach (var part in PolygonBoolean.Combine(piece.Outline, region.Outline, BooleanOperation.Intersection))
            foreach (var outline in PolygonBoolean.WithoutHoles(part))
            {
                if (Polygon2D.Area(outline) <= 100) continue;

                var top = Raise(piece.Top, region);
                var bottom = isTapered ? piece.Bottom : Raise(piece.Bottom, region);
                result.Add(piece with { Outline = outline, Bottom = bottom, Top = top });
            }
        }

        return result;
    }

    private static RoofPlane Raise(RoofPlane plane, DrainageRegion region) =>
        new(plane.A + region.A, plane.B + region.B, plane.C + region.C, plane.Eave);

    /// <summary>
    /// The roof drains of roofs being moved that are not moving already: a drain goes with its
    /// roof, as a roof window does.
    /// </summary>
    public static IReadOnlyList<Element> Following(BimDocument document, IReadOnlyList<Element> moving)
    {
        var roofs = moving.OfType<Roof>().Select(roof => roof.Id).ToHashSet();
        return roofs.Count == 0
            ? Array.Empty<Element>()
            : document.Elements.OfType<RoofDrain>().Where(drain => roofs.Contains(drain.RoofId) && !moving.Contains(drain)).ToList();
    }

    /// <summary>
    /// Why a drain cannot go at a point of a roof - not a flat roof, off it or too near its edge,
    /// too near another drain - or null where it can.
    /// </summary>
    public static string? Problem(BimDocument document, Roof roof, Point2D at, RoofDrain? except = null)
    {
        if (!CanDrain(document, roof)) return "A roof drain goes in a flat roof: a pitched roof drains to its gutters.";
        if (!roof.Contains(at)) return "Click on the roof: the drain goes in it there.";

        var boundary = roof.Boundary;
        var edge = Enumerable.Range(0, boundary.Count).Min(i => Line2D.DistanceFromSegment(at, boundary[i], boundary[(i + 1) % boundary.Count]));
        if (edge < EdgeClearance) return $"Too near the roof's edge: keep a drain {EdgeClearance:0} mm in from it.";

        if (On(document, roof).Any(drain => !ReferenceEquals(drain, except) && drain.Location.DistanceTo(at) < DrainSpacing))
            return "There is a drain there already.";

        return null;
    }

    /// <summary>How near the roof's edge a drain must be to let its water out through it, mm.</summary>
    public const double EdgeOutletReach = 1000;

    /// <summary>How far below the bottom level an internal rainwater pipe goes, into the drain under the ground floor, mm; and how far it then runs off.</summary>
    public const double BelowGround = 300, RunOff = 400;

    /// <summary>A hopper head on the outside wall: how wide, deep and tall, mm.</summary>
    public const double HopperWidth = 250, HopperDepth = 200, HopperHeight = 250;

    public static string DischargeName(DrainDischarge discharge) =>
        discharge == DrainDischarge.ThroughEdge ? "Out through the roof's edge to a downpipe" : "Down inside the building";

    /// <summary>How far a point is from the nearest edge of a roof, mm.</summary>
    public static double EdgeDistance(Roof roof, Point2D at)
    {
        var boundary = roof.Boundary;
        return Enumerable.Range(0, boundary.Count).Min(i => Line2D.DistanceFromSegment(at, boundary[i], boundary[(i + 1) % boundary.Count]));
    }

    /// <summary>The nearest point of a roof's edge to a point, and the way out of the roof there.</summary>
    private static (Point2D At, Vector2D Out, Vector2D Along) NearestEdge(Roof roof, Point2D at)
    {
        var boundary = roof.Boundary;
        var anticlockwise = Polygon2D.SignedArea(boundary) >= 0;
        var best = (At: boundary[0], Out: Vector2D.UnitY, Along: Vector2D.UnitX, Distance: double.MaxValue);
        for (var i = 0; i < boundary.Count; i++)
        {
            var (a, b) = (boundary[i], boundary[(i + 1) % boundary.Count]);
            var along = (b - a).NormalisedOrDefault(Vector2D.UnitX);
            var share = Math.Clamp((at - a).Dot(along), 0, a.DistanceTo(b));
            var foot = a + along * share;
            var distance = foot.DistanceTo(at);
            if (distance >= best.Distance) continue;

            var outward = along.PerpendicularLeft() * (anticlockwise ? -1 : 1);
            best = (foot, outward, along, distance);
        }

        return (best.At, best.Out, best.Along);
    }

    /// <summary>
    /// The pipe a drain's water runs down, as a path and its section; and, for one let out
    /// through the edge, the hopper head on the wall at its top. Null where its roof is gone.
    /// </summary>
    public static DrainPipe? Pipe(BimDocument document, RoofDrain drain)
    {
        if (document.Elements.OfType<Roof>().FirstOrDefault(roof => roof.Id == drain.RoofId) is not { } roof) return null;

        var down = new Vector3(0, 0, 1);
        var start = new Vector3(drain.Location.X, drain.Location.Y, roof.TopAt(document, drain.Location) - 40);
        var (edge, outward, along) = NearestEdge(roof, drain.Location);
        var outward3 = new Vector3(outward.X, outward.Y, 0);
        var radius = drain.OutletDiameter / 2;
        var section = Enumerable.Range(0, 16).Select(i => 2 * Math.PI * i / 16)
            .Select(a => new Point2D(radius * Math.Cos(a), radius * Math.Sin(a))).ToList();

        if (drain.Discharge == DrainDischarge.Internal)
        {
            // Straight down through the building, under the ground floor, and off to the drain.
            var bottom = (document.Levels.Count > 0 ? document.Levels.Min(level => level.Elevation) : 0) - BelowGround;
            var foot = new Vector3(start.X, start.Y, bottom);
            return new DrainPipe(new[] { start, foot, foot + outward3 * RunOff }, section, new Vector3(along.X, along.Y, 0), null, bottom);
        }

        // Out through the edge, into a hopper head standing off the wall under it, and down.
        var (faceOut, wall) = Downpipes.WallFace(document, edge, outward, along, beyond: 600);
        var pipeOut = (faceOut ?? 0) + Downpipes.Clearance + HopperDepth / 2;
        var centre = edge + outward * pipeOut;
        var outlet = new Vector3(centre.X, centre.Y, start.Z);
        var hopperBottom = start.Z + 50 - HopperHeight;
        var (ground, _) = Downpipes.Ground(document, roof, wall, centre);

        var points = new List<Vector3> { start, outlet, new(centre.X, centre.Y, hopperBottom) };
        if (hopperBottom - ground > Downpipes.ShoeRise + 50)
        {
            points.Add(new Vector3(centre.X, centre.Y, ground + Downpipes.ShoeRise));
            points.Add(new Vector3(centre.X, centre.Y, ground + Downpipes.ShoeRise) + outward3 * Downpipes.ShoeReach - down * Downpipes.ShoeReach);
        }

        var hopper = new[]
        {
            centre - along * (HopperWidth / 2) - outward * (HopperDepth / 2), centre + along * (HopperWidth / 2) - outward * (HopperDepth / 2),
            centre + along * (HopperWidth / 2) + outward * (HopperDepth / 2), centre - along * (HopperWidth / 2) + outward * (HopperDepth / 2)
        };

        return new DrainPipe(points, section, new Vector3(along.X, along.Y, 0), (hopper, hopperBottom, start.Z + 50), ground);
    }

    /// <summary>
    /// The holes the rainwater pipes from drains cut in the floors and ceilings they go down
    /// through: each a sleeve a little bigger than the pipe.
    /// </summary>
    public static IEnumerable<IReadOnlyList<Point2D>> PipeHoles(BimDocument document, Slab slab)
    {
        if (slab is Roof) yield break;

        foreach (var drain in document.Elements.OfType<RoofDrain>().Where(drain => drain.Discharge == DrainDischarge.Internal))
        {
            if (!slab.Contains(drain.Location)) continue;
            if (document.Elements.OfType<Roof>().FirstOrDefault(roof => roof.Id == drain.RoofId) is not { } roof) continue;

            // Under the roof, and above the foot of the pipe.
            var bottom = (document.Levels.Count > 0 ? document.Levels.Min(level => level.Elevation) : 0) - BelowGround;
            if (slab.GetTopElevation(document) > roof.GetBottomElevation(document) - 1 || slab.GetBottomElevation(document) < bottom) continue;

            var radius = drain.OutletDiameter / 2 + 25;
            yield return Enumerable.Range(0, 16).Select(i => 2 * Math.PI * i / 16)
                .Select(a => drain.Location + new Vector2D(radius * Math.Cos(a), radius * Math.Sin(a)))
                .ToList();
        }
    }

    /// <summary>A drain's pipe as the 3D view draws it: the pipe swept down its path, and its hopper head.</summary>
    public static Mesh3D? PipeMesh(BimDocument document, RoofDrain drain)
    {
        if (Pipe(document, drain) is not { } pipe) return null;

        var mesh = new Mesh3D(drain.Id, drain.LevelId, MeshKind.Sweep, PipeColour, "Rainwater Pipe");
        var segments = new List<RoofEdgeSegment>();
        for (var i = 0; i + 1 < pipe.Points.Count; i++)
        {
            var (from, to) = (pipe.Points[i], pipe.Points[i + 1]);
            if ((to - from).Length < 1e-6) continue;

            var direction = (to - from).Normalised;
            var side = Math.Abs(direction.Dot(pipe.Across)) > 0.9 ? new Vector3(0, 0, 1) : pipe.Across;
            segments.Add(new RoofEdgeSegment(from, to, side, direction.Cross(side).Normalised, pipe.Section));
        }

        RoofEdgeSweeps.AddRun(mesh, new RoofEdgeRun(segments, false));
        if (pipe.Hopper is var (outline, bottom, top)) mesh.AddExtrusion(outline, bottom, top);
        return mesh.IsEmpty ? null : mesh;
    }

    private static readonly ColourRgb PipeColour = ColourRgb.FromHex("3A3E44");

    private static readonly ColourRgb Grate = ColourRgb.FromHex("2B2E33");

    /// <summary>A drain as the 3D view draws it: a flange on the roof's top and a domed grate over the outlet.</summary>
    public static Mesh3D? Mesh(BimDocument document, RoofDrain drain)
    {
        if (document.Elements.OfType<Roof>().FirstOrDefault(roof => roof.Id == drain.RoofId) is not { } roof) return null;

        var top = roof.TopAt(document, drain.Location);
        var mesh = new Mesh3D(drain.Id, roof.LevelId, MeshKind.Sweep, Grate, "Roof Drain");
        mesh.AddExtrusion(Circle(drain.Location, drain.OutletDiameter / 2 + 60), top, top + 6);
        mesh.AddExtrusion(Circle(drain.Location, drain.OutletDiameter / 2), top + 6, top + 60);
        mesh.AddExtrusion(Circle(drain.Location, drain.OutletDiameter / 4), top + 60, top + 80);
        return mesh;
    }

    private static IReadOnlyList<Point2D> Circle(Point2D centre, double radius) =>
        Enumerable.Range(0, 20).Select(i => 2 * Math.PI * i / 20).Select(a => centre + new Vector2D(radius * Math.Cos(a), radius * Math.Sin(a))).ToList();
}

/// <summary>A roof drain's pipe: its centreline, its section, the way across it, the hopper head at its top if it goes out through the edge, and where it ends.</summary>
public sealed record DrainPipe(
    IReadOnlyList<Vector3> Points, IReadOnlyList<Point2D> Section, Vector3 Across,
    (IReadOnlyList<Point2D> Outline, double Bottom, double Top)? Hopper, double Bottom)
{
    public double Length => Points.Zip(Points.Skip(1), (a, b) => (b - a).Length).Sum();
}

public static class RoofDrainParameters
{
    public static readonly ParameterDefinition Roof = new("Roof", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition OutletDiameter = new("Outlet Diameter", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition CatchmentArea = new("Catchment Area", ParameterDataType.Area, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition HighestFall = new("Rise to Its Furthest Point", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Discharge = new("Water Goes", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition PipeLength = new("Pipe Length", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);
}
