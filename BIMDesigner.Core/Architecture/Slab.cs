using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Parameters;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// A horizontal element with a sketched outline: a floor, a ceiling or a flat roof
/// (specification sections 3.2, 3.3 and 3.4).
///
/// Unlike a room, a slab's outline is <em>stored</em>. A room is a space that exists because
/// walls enclose it; a floor is a thing someone decided to build, and it does not vanish or
/// change shape because a wall moved. Picking an enclosed space is offered as a convenient
/// way to sketch that outline, not as a live link to it.
/// </summary>
public abstract class Slab : Element
{
    private readonly List<Point2D> _boundary = new();
    private readonly List<IReadOnlyList<Point2D>> _holes = new();

    /// <summary>The sketched outline, anticlockwise. Millimetres.</summary>
    public IReadOnlyList<Point2D> Boundary => _boundary;

    /// <summary>
    /// The holes sketched inside a floor's or ceiling's own outline - a void, a light well, a
    /// stair drawn round rather than cut by a shaft - each anticlockwise. A roof keeps its own
    /// as openings.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<Point2D>> Holes => _holes;

    public void SetHoles(IEnumerable<IEnumerable<Point2D>> holes)
    {
        // Read before clearing, as with the outline.
        var replacement = holes
            .Select(hole => hole.ToList())
            .Where(hole => hole.Count >= 3)
            .Select(hole =>
            {
                if (Polygon2D.SignedArea(hole) < 0) hole.Reverse();
                return (IReadOnlyList<Point2D>)hole;
            })
            .ToList();
        _holes.Clear();
        _holes.AddRange(replacement);
    }

    /// <summary>
    /// Height of the slab's reference surface above its level, in millimetres. A floor sits
    /// at its level, so 0; a ceiling sits up at the underside of the storey above.
    /// </summary>
    public double HeightOffset { get; set; }

    public virtual void SetBoundary(IEnumerable<Point2D> points)
    {
        // Read before clearing: the points may be worked out from this slab's own.
        var replacement = points.ToList();
        _boundary.Clear();
        _boundary.AddRange(replacement);

        // Stored anticlockwise so area is positive and every slab winds the same way.
        if (Polygon2D.SignedArea(_boundary) < 0) _boundary.Reverse();
    }

    /// <summary>The slab's upper surface, in project elevation: what a wall stands on.</summary>
    public virtual double GetTopElevation(BimDocument document) =>
        (document.FindLevel(LevelId)?.Elevation ?? 0) + HeightOffset;

    /// <summary>The slab's underside, in project elevation: what a wall reaches up to.</summary>
    public virtual double GetBottomElevation(BimDocument document) =>
        GetTopElevation(document) - (document.FindType<SlabType>(TypeId)?.Thickness ?? 0);

    /// <summary>Plan area in mm², less its own holes. The quantity a finishes or takeoff schedule reports.</summary>
    public double Area => Polygon2D.Area(_boundary) - _holes.Sum(hole => Polygon2D.Area(hole));

    public double Perimeter => Polygon2D.Perimeter(_boundary);

    public Point2D Centroid => Polygon2D.Centroid(_boundary, default);

    /// <summary>Whether a point is on the slab: inside its outline, and not in one of its own holes.</summary>
    public bool Contains(Point2D point) => Polygon2D.Contains(_boundary, point) && !_holes.Any(hole => Polygon2D.Contains(hole, point));

    /// <summary>Volume in mm³, from the plan area less the shafts through it and the type's build-up thickness.</summary>
    public virtual double GetVolume(BimDocument document) =>
        Shafts.NetArea(document, this) * (document.FindType<SlabType>(TypeId)?.Thickness ?? 0);

    public override IEnumerable<ParameterValue> GetInstanceParameters(BimDocument document)
    {
        var type = document.FindType<SlabType>(TypeId);

        yield return ParameterValue.BindChoice(
            SlabParameters.Level,
            () => document.FindLevel(LevelId)?.Name ?? string.Empty,
            v =>
            {
                var level = document.Levels.FirstOrDefault(l => l.Name == v);
                if (level is not null) LevelId = level.Id;
            },
            document.Levels.Select(l => l.Name).ToArray());

        yield return ParameterValue.Bind(
            SlabParameters.HeightOffset, () => HeightOffset, v => HeightOffset = v);

        // Dimensions, all from the sketched outline and the type - never stored.
        // Less the shafts through it, as a floor's area is measured - a roof's is its footprint.
        yield return ParameterValue.ReadOnly(SlabParameters.Area, () => this is Roof ? Area : Shafts.NetArea(document, this));
        yield return ParameterValue.ReadOnly(SlabParameters.Perimeter, () => Perimeter);
        yield return ParameterValue.ReadOnly(SlabParameters.Thickness, () => type?.Thickness ?? 0);
        yield return ParameterValue.ReadOnly(SlabParameters.Volume, () => GetVolume(document));
        yield return ParameterValue.ReadOnly(SlabParameters.Vertices, () => Boundary.Count);

        // A floor or a flat roof walked on: whether its edges are guarded where they drop too far.
        if (FloorSurfaces.IsStoodOn(document, this))
            yield return ParameterValue.ReadOnly(SlabParameters.EdgeGuarding, () => Railings.EdgeVerdict(document, this));

        // A floor or a roof: the heat it lets out, the sound and fire it holds back, the pitch its covering wants.
        if (this is Floor or Roof)
            yield return ParameterValue.ReadOnly(SlabParameters.Regulations, () => SlabRules.Verdict(document, this));

        foreach (var parameter in ExtraParameters(document)) yield return parameter;
        foreach (var parameter in GetCommonParameters(document)) yield return parameter;
    }

    /// <summary>What a particular kind of slab adds to the panel - for a roof, its shape and pitch.</summary>
    protected virtual IEnumerable<ParameterValue> ExtraParameters(BimDocument document) =>
        Array.Empty<ParameterValue>();
}

/// <summary>A floor slab (specification section 3.2).</summary>
public sealed class Floor : Slab
{
    public override BuiltInCategory Category => BuiltInCategory.Floors;

    /// <summary>
    /// The way the floor spans - its joists or its deck run - as degrees anticlockwise from the
    /// X axis; null where none is given. Drawn in plan as Revit's span direction symbol.
    /// </summary>
    public double? SpanDirection { get; set; }

    protected override IEnumerable<ParameterValue> ExtraParameters(BimDocument document)
    {
        yield return ParameterValue.BindValidated(SlabParameters.SpanDirection,
            () => SpanDirection is { } span ? $"{span:0.##}°" : "None",
            (string text) =>
            {
                var trimmed = text.Trim().TrimEnd('°').Trim();
                if (trimmed.Length == 0 || trimmed.Equals("None", StringComparison.OrdinalIgnoreCase))
                {
                    SpanDirection = null;
                    return true;
                }

                if (!double.TryParse(trimmed, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var degrees) || !double.IsFinite(degrees))
                    return false;
                SpanDirection = Floors.Normalised(degrees);
                return true;
            });
    }
}

/// <summary>Floors' span directions: kept between 0° and 180°, since a span runs both ways.</summary>
public static class Floors
{
    public static double Normalised(double degrees) => ((degrees % 180) + 180) % 180;

    /// <summary>
    /// Where a floor's span symbol goes: its middle - or, where that is in a hole, the nearest
    /// place to it on the floor clear of its holes.
    /// </summary>
    public static Point2D SymbolPlace(Slab slab)
    {
        var middle = Polygon2D.Centroid(slab.Boundary, slab.Boundary[0]);
        double Clear(Point2D at) => slab.Holes.Count == 0 ? double.MaxValue
            : slab.Holes.Min(hole => Enumerable.Range(0, hole.Count).Min(i => Line2D.DistanceFromSegment(at, hole[i], hole[(i + 1) % hole.Count])));
        if (slab.Contains(middle) && Clear(middle) > 1000) return middle;

        // Out from the middle toward each corner and the middle of each side, a little at a time.
        var toward = slab.Boundary.Concat(slab.Boundary.Select((point, i) => point.MidpointTo(slab.Boundary[(i + 1) % slab.Boundary.Count])));
        return toward
            .SelectMany(target => Enumerable.Range(1, 9).Select(step => middle + (target - middle) * (step / 10.0)))
            .Where(slab.Contains)
            .OrderByDescending(at => Math.Min(Clear(at), 1000))
            .ThenBy(at => at.DistanceTo(middle))
            .DefaultIfEmpty(middle)
            .First();
    }

    /// <summary>A span direction turned by an angle - as its floor is rotated.</summary>
    public static double Turned(double degrees, double by) => Normalised(degrees + by);

    /// <summary>A span direction as a reflection leaves it: the way a line along it ends up.</summary>
    public static double Reflected(double degrees, Func<Point2D, Point2D> reflect)
    {
        var radians = degrees * Math.PI / 180;
        var from = reflect(new Point2D(0, 0));
        var to = reflect(new Point2D(Math.Cos(radians), Math.Sin(radians)));
        return Normalised(Math.Atan2(to.Y - from.Y, to.X - from.X) * 180 / Math.PI);
    }
}

/// <summary>A ceiling (specification section 3.4).</summary>
public sealed class Ceiling : Slab
{
    public override BuiltInCategory Category => BuiltInCategory.Ceilings;
}


public static class SlabParameters
{
    public static readonly ParameterDefinition Level =
        new("Level", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Constraints);

    public static readonly ParameterDefinition HeightOffset =
        new("Height Offset From Level", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Constraints);

    public static readonly ParameterDefinition Area =
        new("Area", ParameterDataType.Area, ParameterBinding.Instance, ParameterGroup.Dimensions);

    public static readonly ParameterDefinition Perimeter =
        new("Perimeter", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);

    public static readonly ParameterDefinition Thickness =
        new("Thickness", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);

    public static readonly ParameterDefinition Volume =
        new("Volume", ParameterDataType.Volume, ParameterBinding.Instance, ParameterGroup.Dimensions);

    public static readonly ParameterDefinition Vertices =
        new("Boundary Points", ParameterDataType.Integer, ParameterBinding.Instance, ParameterGroup.Dimensions);

    public static readonly ParameterDefinition EdgeGuarding =
        new("Edge Guarding", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.IdentityData);

    public static readonly ParameterDefinition SpanDirection =
        new("Span Direction", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Construction);

    public static readonly ParameterDefinition Regulations =
        new("Regulations", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.IdentityData);
}
