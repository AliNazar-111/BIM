using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;
using BIMDesigner.Core.Parameters;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// A downpipe (a downspout): what takes the water down from a gutter. A drop outlet in the
/// gutter's bottom, an elbow and an offset back to the wall - the swan neck - the pipe down the
/// wall, and a shoe at its foot turning the water away from it. Hosted by its gutter: deleted
/// and copied with it, carried when its roof moves. Set where it is along the gutter, so it
/// slides along it when moved, and follows the gutter's sections and sizes from its type.
/// </summary>
public sealed class Downpipe : Element, IHostedElement
{
    public override BuiltInCategory Category => BuiltInCategory.Downpipes;

    /// <summary>The gutter it takes the water from.</summary>
    public Guid GutterId { get; set; }

    public Guid HostId => GutterId;

    /// <summary>Where along its gutter its outlet is, in plan: the outlet is at the nearest point of the gutter to this.</summary>
    public Point2D Location { get; set; }

    public override IEnumerable<ParameterValue> GetInstanceParameters(BimDocument document)
    {
        var gutter = document.Elements.OfType<Gutter>().FirstOrDefault(candidate => candidate.Id == GutterId);
        var type = gutter is null ? null : document.FindType<GutterType>(gutter.TypeId);

        yield return ParameterValue.ReadOnly(DownpipeParameters.Gutter, () => type?.Name ?? "<none>");
        yield return ParameterValue.ReadOnly(DownpipeParameters.Shape, () => type is null ? string.Empty : EnumText.Humanise(type.DownpipeShape));
        yield return ParameterValue.ReadOnly(DownpipeParameters.Size, () => type is null ? string.Empty : Downpipes.Size(type));
        yield return ParameterValue.ReadOnly(DownpipeParameters.Height, () => Downpipes.Path(document, this) is { } path ? path.Points[0].Z - path.Bottom : 0);
        yield return ParameterValue.ReadOnly(DownpipeParameters.Length, () => Downpipes.Path(document, this)?.Length ?? 0);
        yield return ParameterValue.ReadOnly(DownpipeParameters.Discharge,
            () => Downpipes.Path(document, this) is { OntoRoof: true } ? "Onto the roof below" : "To the ground");

        foreach (var parameter in GetCommonParameters(document)) yield return parameter;
    }
}

/// <summary>
/// Where a downpipe runs: the centreline from the gutter's outlet down to its shoe, the way along
/// the wall and out from it, its section, and where it ends - at the ground or on a roof below.
/// </summary>
public sealed record DownpipePath(
    IReadOnlyList<Vector3> Points, Vector3 Across, Vector3 Outward, IReadOnlyList<Point2D> Section, Point2D Foot, double Bottom, bool OntoRoof)
{
    public double Length => Points.Zip(Points.Skip(1), (a, b) => (b - a).Length).Sum();

    /// <summary>Where the pipe stands in plan, down the wall: its section there.</summary>
    public IReadOnlyList<Point2D> Footprint =>
        Section.Select(point => Foot + new Vector2D(Across.X, Across.Y) * point.X + new Vector2D(Outward.X, Outward.Y) * point.Y).ToList();
}

public static class Downpipes
{
    /// <summary>How far a downpipe stands off its wall, mm, for the brackets holding it.</summary>
    public const double Clearance = 35;

    /// <summary>How far the outlet drops out of the gutter's bottom before the pipe turns, mm.</summary>
    public const double OutletDrop = 60;

    /// <summary>The shoe at the foot: how high above the ground it turns out, and how far out it reaches, mm.</summary>
    public const double ShoeRise = 150, ShoeReach = 100;

    /// <summary>How far below the underside of the roof's edge a downpipe turns back to the wall, mm: clear of the fascia's drip and the soffit.</summary>
    public const double EaveClearance = 80;

    /// <summary>How near a run's end an outlet may come, mm, clear of its end cap.</summary>
    public const double EndMargin = 100;

    /// <summary>How far above the roof below a dormer's downpipe turns down to it at the least, mm: room for the pipe to drop.</summary>
    public const double AboveRoofBelow = 50;

    public static IEnumerable<Downpipe> On(BimDocument document, RoofEdgeSweep gutter) =>
        document.Elements.OfType<Downpipe>().Where(pipe => pipe.GutterId == gutter.Id);

    /// <summary>A downpipe's size as it is written: "68" for a round one, "65 x 65", "65 x 100".</summary>
    public static string Size(GutterType type) => type.DownpipeShape switch
    {
        DownpipeShape.Round => $"Ø{type.DownpipeWidth:0}",
        DownpipeShape.Square => $"{type.DownpipeWidth:0} x {type.DownpipeWidth:0}",
        _ => $"{type.DownpipeWidth:0} x {type.DownpipeDepth:0}"
    };

    /// <summary>The section of a gutter type's downpipes: across the wall, then out from it.</summary>
    public static IReadOnlyList<Point2D> Section(GutterType type)
    {
        var width = type.DownpipeWidth;
        if (type.DownpipeShape == DownpipeShape.Round)
        {
            const int sides = 16;
            return Enumerable.Range(0, sides)
                .Select(i => 2 * Math.PI * i / sides)
                .Select(angle => new Point2D(width / 2 * Math.Cos(angle), width / 2 * Math.Sin(angle)))
                .ToList();
        }

        var depth = type.DownpipeShape == DownpipeShape.Square ? width : type.DownpipeDepth;
        return new[] { new Point2D(-width / 2, -depth / 2), new Point2D(width / 2, -depth / 2), new Point2D(width / 2, depth / 2), new Point2D(-width / 2, depth / 2) };
    }

    /// <summary>
    /// The place along a gutter nearest a point in plan: the stretch of it, and how far along it -
    /// kept clear of the ends of each stretch, where its cap or a corner is.
    /// </summary>
    public static (RoofEdgeSegment Segment, double Along, double Distance)? Nearest(BimDocument document, RoofEdgeSweep gutter, Point2D near)
    {
        (RoofEdgeSegment, double, double)? best = null;
        foreach (var segment in RoofEdgeSweeps.Runs(document, gutter).SelectMany(run => run.Segments))
        {
            var (from, to) = (segment.From.Plan, segment.To.Plan);
            var length = segment.Length;
            if (length < 1e-6) continue;

            var direction = (to - from).NormalisedOrDefault(Vector2D.UnitX);
            var planLength = from.DistanceTo(to);
            var margin = Math.Min(EndMargin, planLength / 2);
            var share = Math.Clamp((near - from).Dot(direction), margin, planLength - margin) / Math.Max(planLength, 1e-9);
            var distance = near.DistanceTo(from + (to - from) * share);

            if (best is null || distance < best.Value.Item3) best = (segment, share * length, distance);
        }

        return best;
    }

    /// <summary>Where a downpipe runs, or null where its gutter or roof is gone.</summary>
    public static DownpipePath? Path(BimDocument document, Downpipe pipe)
    {
        if (document.Elements.OfType<Gutter>().FirstOrDefault(candidate => candidate.Id == pipe.GutterId) is not { } gutter) return null;
        if (document.FindType<GutterType>(gutter.TypeId) is not { } type) return null;
        if (document.Elements.OfType<Roof>().FirstOrDefault(roof => roof.Id == gutter.RoofId) is not { } roof) return null;
        if (Nearest(document, gutter, pipe.Location) is not var (segment, along, _)) return null;

        // Where a dormer's gutter runs back into the main roof, a downpipe would come out of it
        // inside that roof and down into the room under it: it goes along the gutter to the
        // nearest place that is out over the roof.
        return At(document, gutter, type, roof, segment, along) ??
               Places(document, gutter, pipe.Location).Select(place => At(document, gutter, type, roof, place.Segment, place.Along)).FirstOrDefault(path => path is not null);
    }

    /// <summary>Places along a gutter, kept clear of the ends of each stretch, the nearest a point first.</summary>
    private static IEnumerable<(RoofEdgeSegment Segment, double Along)> Places(BimDocument document, RoofEdgeSweep gutter, Point2D near)
    {
        var places = new List<(RoofEdgeSegment Segment, double Along, double Distance)>();
        foreach (var segment in RoofEdgeSweeps.Runs(document, gutter).SelectMany(run => run.Segments))
        {
            var (from, to) = (segment.From.Plan, segment.To.Plan);
            var planLength = from.DistanceTo(to);
            if (segment.Length < 1e-6 || planLength < 1e-6) continue;

            var margin = Math.Min(EndMargin, planLength / 2);
            for (var s = margin; s <= planLength - margin + 1e-6; s += 25)
                places.Add((segment, s / planLength * segment.Length, near.DistanceTo(from + (to - from) * (s / planLength))));
        }

        return places.OrderBy(place => place.Distance).Select(place => (place.Segment, place.Along));
    }

    /// <summary>
    /// The roof a gutter's roof is carried back into - a dormer's main roof - which its downpipes
    /// let their water onto, if it is.
    /// </summary>
    private static Roof? RoofBelow(BimDocument document, Roof roof) =>
        roof.JoinedTo is { } joined
            ? document.Elements.OfType<Roof>().FirstOrDefault(other => other.Id == joined && !ReferenceEquals(other, roof) && !other.IsExtrusion)
            : null;

    /// <summary>
    /// A downpipe with its outlet at a place along a gutter - or null where it would not come out
    /// over the roof below: the outlet, and the swan neck back to the wall, inside that roof.
    /// </summary>
    private static DownpipePath? At(BimDocument document, RoofEdgeSweep gutter, GutterType type, Roof roof, RoofEdgeSegment segment, double along)
    {
        var form = Gutters.Form(type);
        var at = segment.From + segment.Along * along;
        var outlet = segment.Place(at, new Point2D(form.Outlet + gutter.HorizontalOffset, form.Bottom + gutter.VerticalOffset));
        var outward = new Vector3(segment.Out.X, segment.Out.Y, 0).Normalised;
        var across = new Vector3(segment.Along.X, segment.Along.Y, 0).Normalised;
        var down = new Vector3(0, 0, 1);
        var section = Section(type);
        var standOff = section.Max(point => point.Y);

        // Back to the wall under the eave, standing off it; straight down where there is none.
        var outletOut = (outlet - at).Dot(outward);
        var below = RoofBelow(document, roof);
        var (faceOut, wall) = WallFace(document, at.Plan, new Vector2D(outward.X, outward.Y), new Vector2D(across.X, across.Y), below: below);
        var pipeOut = faceOut is { } face ? face + Clearance + standOff : outletOut;
        var back = outletOut - pipeOut;

        // Down out of the gutter, and on past the roof's eave - its edge, and any fascia and
        // soffit under it - before turning back under the overhang to the wall.
        var inside = at.Plan - new Vector2D(outward.X, outward.Y) * 1;
        var eave = roof.Contains(inside) ? roof.UndersideAt(document, inside) : at.Z;
        var turn = Math.Min(outlet.Z - OutletDrop, eave - EaveClearance);
        var points = new List<Vector3> { outlet, new(outlet.X, outlet.Y, turn) };

        // The swan neck: two elbows and an offset at 45° between them.
        if (Math.Abs(back) > 5) points.Add(points[^1] - outward * back - down * Math.Abs(back));

        // All of that out over the roof below, with room under it for the pipe to drop.
        if (below is not null && points.Any(point => below.Contains(point.Plan) && point.Z < below.TopAt(document, point.Plan) + AboveRoofBelow)) return null;

        var foot = points[^1].Plan;
        var (bottom, ontoRoof) = Ground(document, roof, wall, foot);
        var top = points[^1].Z;

        if (top - bottom > ShoeRise + 50)
        {
            // Down the wall, and out at the foot through a shoe - which stays on top of a roof it
            // lets its water onto, where that roof rises the way it points.
            points.Add(new Vector3(foot.X, foot.Y, bottom + ShoeRise));
            var shoe = new Vector3(foot.X, foot.Y, bottom + ShoeRise) + outward * ShoeReach - down * ShoeReach;
            if (ontoRoof && below is not null && below.Contains(shoe.Plan))
                shoe = new Vector3(shoe.X, shoe.Y, Math.Max(shoe.Z, below.TopAt(document, shoe.Plan) + 10));
            points.Add(shoe);
        }
        else if (top - bottom > 10)
        {
            points.Add(new Vector3(foot.X, foot.Y, bottom + 10));
        }

        return new DownpipePath(points, across, outward, section, foot, bottom, ontoRoof);
    }

    /// <summary>
    /// How far out from the roof's edge the face of the wall under it is - the outermost wall
    /// running along the eave, under it - and that wall; nothing where no wall is there. Over a
    /// roof below, only a wall coming up through it: one under it is inside the house.
    /// </summary>
    internal static (double? FaceOut, Wall? Wall) WallFace(BimDocument document, Point2D edge, Vector2D outward, Vector2D along, double beyond = 50, Roof? below = null)
    {
        var roofTop = below is not null && below.Contains(edge) ? below.TopAt(document, edge) : double.NegativeInfinity;

        (double, Wall)? best = null;
        foreach (var wall in document.Walls.Where(wall => !wall.IsCurved))
        {
            if (wall.GetTopElevation(document) <= roofTop) continue;
            if (Math.Abs(wall.Direction.Cross(along)) > 0.05) continue;
            if (document.GetWallType(wall) is not { } type) continue;

            // Along the wall, the pipe is on it.
            var onWall = (edge - wall.Start).Dot(wall.Direction);
            if (onWall < -1 || onWall > wall.Length + 1) continue;

            var centreOut = -(edge - wall.Start).Dot(outward);
            var faceOut = centreOut + type.Structure.TotalWidth / 2;
            if (centreOut < -2500 || faceOut > beyond) continue;

            if (best is null || faceOut > best.Value.Item1) best = (faceOut, wall);
        }

        return best is { } found ? (found.Item1, found.Item2) : (null, null);
    }

    /// <summary>
    /// Where a downpipe ends: on the roof below, for a dormer's, which lets its water onto the roof
    /// it comes out of; otherwise at the foot of the wall it runs down, or the lowest level.
    /// </summary>
    internal static (double Bottom, bool OntoRoof) Ground(BimDocument document, Roof roof, Wall? wall, Point2D foot)
    {
        if (RoofBelow(document, roof) is { } below && below.Contains(foot))
            return (below.TopAt(document, foot), true);

        if (wall is not null) return (wall.GetBaseElevation(document), false);

        return (document.Levels.Count > 0 ? document.Levels.Min(level => level.Elevation) : 0, false);
    }

    /// <summary>
    /// Where downpipes go on a gutter by themselves: at each end of each run, and at each corner
    /// of one that goes all round - brought back along the gutter, past the overhang, to where
    /// there is wall under it to run down, near its corner. An end that runs back into the roof
    /// below - the top end of a dormer's side gutter, in the main roof - is stopped, not an
    /// outlet: the water runs the other way, to the first end or corner out over that roof.
    /// </summary>
    public static IReadOnlyList<Point2D> AtEnds(BimDocument document, RoofEdgeSweep gutter)
    {
        var type = document.FindType<GutterType>(gutter.TypeId);
        var roof = document.Elements.OfType<Roof>().FirstOrDefault(candidate => candidate.Id == gutter.RoofId);
        var half = type is not null ? type.DownpipeWidth / 2 : 35;
        var below = roof is null ? null : RoofBelow(document, roof);
        bool Clear(RoofEdgeSegment segment, double along) => type is null || roof is null || At(document, gutter, type, roof, segment, along) is not null;

        var places = new List<Point2D>();
        foreach (var run in RoofEdgeSweeps.Runs(document, gutter))
        {
            if (run.Segments.Count == 0) continue;

            if (run.Closed)
            {
                // Each corner of a run all round, looking back along the stretch from it.
                foreach (var segment in run.Segments)
                    if (NearCorner(document, segment, fromStart: true, half, below, Clear) is { } corner) places.Add(corner);
                continue;
            }

            // Each end of a run - or, where it is buried, the first end of a stretch along from it that is not.
            var fromFirst = run.Segments.SelectMany(segment => new[] { (Segment: segment, FromStart: true), (Segment: segment, FromStart: false) });
            var fromLast = run.Segments.Reverse().SelectMany(segment => new[] { (Segment: segment, FromStart: false), (Segment: segment, FromStart: true) });
            foreach (var ends in new[] { fromFirst, fromLast })
                if (ends.Select(end => NearCorner(document, end.Segment, end.FromStart, half, below, Clear)).FirstOrDefault(place => place is not null) is { } place)
                    places.Add(place);
        }

        // Not two in one place: a short run gets one.
        return places.Where((place, i) => places.Take(i).All(other => other.DistanceTo(place) > 300)).ToList();
    }

    /// <summary>
    /// The first place back from one end of a stretch of gutter with wall under it, clear of the
    /// wall's corner - or just in from the end, where no wall is found. None where the end is
    /// buried in the roof below.
    /// </summary>
    private static Point2D? NearCorner(BimDocument document, RoofEdgeSegment segment, bool fromStart, double half, Roof? below, Func<RoofEdgeSegment, double, bool> clear)
    {
        var length = segment.Length;
        var outward = new Vector2D(segment.Out.X, segment.Out.Y).NormalisedOrDefault(Vector2D.UnitY);
        var along = new Vector2D(segment.Along.X, segment.Along.Y).NormalisedOrDefault(Vector2D.UnitX);
        Point2D At(double s) => (fromStart ? segment.From + segment.Along * s : segment.To - segment.Along * s).Plan;
        bool ClearAt(double s) => clear(segment, fromStart ? s : length - s);

        var nearEnd = Math.Min(EndMargin + 50, length / 2);
        if (!ClearAt(nearEnd)) return null;

        for (var s = EndMargin + 50; s <= Math.Min(length / 2, 3000); s += 25)
        {
            var at = At(s);
            if (WallFace(document, at, outward, along, below: below) is { Wall: { } wall } &&
                Math.Min((at - wall.Start).Dot(wall.Direction), wall.Length - (at - wall.Start).Dot(wall.Direction)) >= half + 200 &&
                ClearAt(s))
                return at;
        }

        return At(nearEnd);
    }

    /// <summary>
    /// The downpipes on the gutters of roofs being moved that are not moving already: a gutter
    /// goes with its roof, and its downpipes with it.
    /// </summary>
    public static IReadOnlyList<Element> Following(BimDocument document, IReadOnlyList<Element> moving)
    {
        var roofs = moving.OfType<Roof>().Select(roof => roof.Id).ToHashSet();
        if (roofs.Count == 0) return Array.Empty<Element>();

        var gutters = document.Elements.OfType<Gutter>().Where(gutter => roofs.Contains(gutter.RoofId)).Select(gutter => gutter.Id).ToHashSet();
        return document.Elements.OfType<Downpipe>().Where(pipe => gutters.Contains(pipe.GutterId) && !moving.Contains(pipe)).ToList();
    }

    private static readonly ColourRgb PipeColour = ColourRgb.FromHex("2E3136");

    /// <summary>The downpipe as a solid: its section swept down its path, mitred at each elbow, in its gutter's material.</summary>
    public static Mesh3D? Mesh(BimDocument document, Downpipe pipe)
    {
        if (Path(document, pipe) is not { } path || path.Points.Count < 2) return null;

        var gutter = document.Elements.OfType<Gutter>().First(candidate => candidate.Id == pipe.GutterId);
        var material = document.FindMaterial(document.FindType<GutterType>(gutter.TypeId)?.MaterialId ?? Guid.Empty);
        var mesh = new Mesh3D(pipe.Id, gutter.LevelId, MeshKind.Sweep, material?.SurfaceColour ?? PipeColour, material?.Name ?? "Downpipe");

        var segments = new List<RoofEdgeSegment>();
        for (var i = 0; i + 1 < path.Points.Count; i++)
        {
            var (from, to) = (path.Points[i], path.Points[i + 1]);
            if ((to - from).Length < 1e-6) continue;

            var along = (to - from).Normalised;
            segments.Add(new RoofEdgeSegment(from, to, path.Across, along.Cross(path.Across).Normalised, path.Section));
        }

        RoofEdgeSweeps.AddRun(mesh, new RoofEdgeRun(segments, false));
        return mesh.IsEmpty ? null : mesh;
    }
}

public static class DownpipeParameters
{
    public static readonly ParameterDefinition Gutter = new("Gutter", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition Shape = new("Shape", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Size = new("Size", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Height = new("Height", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Length = new("Length", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Discharge = new("Discharges", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Other);
}
