using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;
using BIMDesigner.Core.Parameters;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// A pad graded into the ground: an area made level - or falling one way, as a patio or a drive
/// does - at a height of its own, the ground cut down or made up to it, and battered back to the
/// ground round it at a side slope of so many to one, or held sheer at its edge: Revit's building
/// pad, and a pad within grade limits as Vectorworks has it. What is dug out and made up for it is
/// counted in the ground's cut and fill, and what stands on it stands on it.
/// </summary>
public sealed class SitePad : Element
{
    private readonly List<Point2D> _boundary = new();

    public override BuiltInCategory Category => BuiltInCategory.Pads;

    /// <summary>Its outline in plan, anticlockwise.</summary>
    public IReadOnlyList<Point2D> Boundary => _boundary;

    public void SetBoundary(IEnumerable<Point2D> points)
    {
        var replacement = points.ToList();
        _boundary.Clear();
        _boundary.AddRange(replacement);
        if (Polygon2D.SignedArea(_boundary) < 0) _boundary.Reverse();
    }

    /// <summary>How far above its level it is at its middle, mm - below, for minus.</summary>
    public double HeightOffset { get; set; } = -150;

    /// <summary>How steeply it falls, as one in so many; none for level.</summary>
    public double Fall { get; set; }

    /// <summary>Which way it falls - downhill - anticlockwise from the X axis, degrees.</summary>
    public double FallDirection { get; set; } = 270;

    /// <summary>The side slope it is battered back to the ground at, as so many along to one up; none for a sheer edge, held by a wall.</summary>
    public double Batter { get; set; } = 2;

    /// <summary>What it is laid as.</summary>
    public GroundSurface Surface { get; set; } = GroundSurface.Paving;

    /// <summary>A material of the project's for it; none for how its surface usually reads.</summary>
    public Guid MaterialId { get; set; }

    public override IEnumerable<ParameterValue> GetInstanceParameters(BimDocument document)
    {
        yield return ParameterValue.BindChoice(SitePadParameters.Level,
            () => document.FindLevel(LevelId)?.Name ?? "<none>",
            v => { if (document.Levels.FirstOrDefault(level => level.Name == v) is { } level) LevelId = level.Id; },
            document.Levels.Select(level => level.Name).ToArray());
        yield return ParameterValue.BindValidated(SitePadParameters.HeightOffset, () => HeightOffset, (double v) =>
        {
            if (!double.IsFinite(v) || Math.Abs(v) > 20000) return false;
            HeightOffset = v;
            return true;
        });
        yield return ParameterValue.BindValidated(SitePadParameters.Fall, () => Fall, (double v) =>
        {
            if (!double.IsFinite(v) || (v != 0 && v < 1) || v > 1000) return false;
            Fall = v;
            return true;
        });
        yield return ParameterValue.BindValidated(SitePadParameters.FallDirection, () => FallDirection, (double v) =>
        {
            if (!double.IsFinite(v)) return false;
            FallDirection = (v % 360 + 360) % 360;
            return true;
        });
        yield return ParameterValue.BindValidated(SitePadParameters.Batter, () => Batter, (double v) =>
        {
            if (!double.IsFinite(v) || v < 0 || v > 100) return false;
            Batter = v;
            return true;
        });
        yield return ParameterValue.BindChoice(SitePadParameters.Surface,
            () => EnumText.Humanise(Surface),
            v => { if (EnumText.TryParse<GroundSurface>(v, out var surface)) Surface = surface; },
            EnumText.Choices<GroundSurface>());
        var names = document.Materials.Select(material => material.Name).OrderBy(name => name).ToArray();
        yield return ParameterValue.BindChoice(SitePadParameters.Material,
            () => document.FindMaterial(MaterialId)?.Name ?? "As its surface",
            v => MaterialId = document.Materials.FirstOrDefault(material => material.Name == v)?.Id ?? Guid.Empty,
            names.Prepend("As its surface").ToArray());
        yield return ParameterValue.ReadOnly(SitePadParameters.Area, () => Polygon2D.Area(Boundary));
        yield return ParameterValue.ReadOnly(SitePadParameters.Perimeter, () => Polygon2D.Perimeter(Boundary));
        yield return ParameterValue.ReadOnly(SitePadParameters.Height, () => Math.Round(SitePads.HeightAt(document, this, Polygon2D.Centroid(Boundary, default))));
        yield return ParameterValue.ReadOnly(SitePadParameters.Rule, () => SitePads.Verdict(document, this));

        foreach (var parameter in GetCommonParameters(document)) yield return parameter;
    }
}

public static class SitePadParameters
{
    public static readonly ParameterDefinition Level = new("Level", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition HeightOffset = new("Height Offset From Level", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition Fall = new("Fall (1 in, 0 for level)", ParameterDataType.Number, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition FallDirection = new("Falls Toward", ParameterDataType.Angle, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Batter = new("Batter (1 in, 0 for a sheer edge)", ParameterDataType.Number, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Surface = new("Surface", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Construction);
    public static readonly ParameterDefinition Material = new("Material", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.MaterialsAndFinishes);
    public static readonly ParameterDefinition Area = new("Area", ParameterDataType.Area, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Perimeter = new("Perimeter", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Height = new("Height at Middle", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Rule = new("Regulations", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.IdentityData);
}

/// <summary>
/// Pads: how high one is at a point, and whether it sits right against the building - kept down
/// from the damp-proof course, and falling away from the walls so water runs off, not to them.
/// </summary>
public static class SitePads
{
    /// <summary>How far from the walls a pad's fall is checked, mm: 10 ft (3048), as the IRC and the IBC measure it.</summary>
    public const double NearWalls = 3048;

    /// <summary>The pads graded into the ground on a level.</summary>
    public static IReadOnlyList<SitePad> On(BimDocument document, Guid levelId) =>
        document.All<SitePad>().Where(pad => pad.LevelId == levelId && pad.Boundary.Count >= 3).ToList();

    /// <summary>Which way it runs down, in plan; none for a level pad.</summary>
    public static Vector2D? Downhill(SitePad pad) =>
        pad.Fall >= 1 ? new Vector2D(Math.Cos(pad.FallDirection * Math.PI / 180), Math.Sin(pad.FallDirection * Math.PI / 180)) : null;

    /// <summary>Its height over a point: its level and offset at its middle, falling the way it falls.</summary>
    public static double HeightAt(BimDocument document, SitePad pad, Point2D at)
    {
        var middle = Polygon2D.Centroid(pad.Boundary, pad.Boundary.Count > 0 ? pad.Boundary[0] : at);
        var height = (document.FindLevel(pad.LevelId)?.Elevation ?? 0) + pad.HeightOffset;
        return Downhill(pad) is { } down ? height - (at - middle).Dot(down) / pad.Fall : height;
    }

    /// <summary>
    /// What about a pad falls short. Beside the building: kept 150 below the floor inside -
    /// Approved Document C's damp-proof course - or 6 in (152) under the IRC; and, a hard surface
    /// within 10 ft (3048) of the walls, falling away from them so water runs off: 1 in 48 (2%)
    /// at least, the IRC and the IBC say, and not toward them at all. Battered steeper than 1 in
    /// 1, the earth will not stand: hold it with a retaining wall.
    /// </summary>
    public static IReadOnlyList<string> Problems(BimDocument document, SitePad pad)
    {
        var problems = new List<string>();
        if (pad.Boundary.Count < 3) return problems;
        var us = document.ProjectInformation.ClearanceStandard.IsUs();
        var hard = pad.Surface is GroundSurface.Paving or GroundSurface.Path;

        // Its edges near the building's walls, and the floor inside them.
        var (worstKept, toward, tooFlat) = ((double?)null, false, false);
        var down = Downhill(pad);
        foreach (var wall in document.Walls.Where(wall => wall.LevelId == pad.LevelId && wall.Length >= 1 && document.GetWallType(wall) is { Function: WallFunction.Exterior }))
        {
            var type = document.GetWallType(wall)!;
            var half = type.Structure.TotalWidth / 2;
            var count = Math.Max(1, (int)Math.Floor(wall.Length / 500));
            for (var k = 0; k <= count; k++)
            {
                var along = wall.Length * k / count;
                foreach (var side in new[] { 1.0, -1.0 })
                {
                    var outside = wall.PointAt(type.Structure, along, side * (half + 100));
                    if (!Polygon2D.Contains(pad.Boundary, outside)) continue;
                    var inside = wall.PointAt(type.Structure, along, -side * (half + 300));
                    var floors = document.All<Floor>().Where(floor => floor.LevelId == wall.LevelId && floor.Boundary.Count >= 3 && floor.Contains(inside))
                        .Select(floor => floor.GetTopElevation(document)).ToList();
                    if (floors.Count > 0)
                    {
                        var kept = floors.Max() - HeightAt(document, pad, outside);
                        if (worstKept is not { } known || kept < known) worstKept = kept;
                    }

                    // Which way away from the wall is, here: does it run down to the wall, or away?
                    var away = (outside - inside).NormalisedOrDefault(Vector2D.UnitX);
                    // Under the US codes, 2% away at least; here, some fall away, not none.
                    var awayFall = down is { } fall ? fall.Dot(away) / pad.Fall : 0;
                    if (down is { } falls && falls.Dot(away) < -0.2) toward = true;
                    else if (us ? awayFall < 1 / 48.0 - 1e-6 : awayFall < 1e-6) tooFlat = true;
                }
            }
        }

        if (worstKept is { } least && least < (us ? 152 : 150) - 0.5)
            problems.Add(us
                ? $"Against the walls it is {Units.Bare(least)} below the floor: the IRC asks for 6 in{Units.MetricAside("152")} of foundation above the finished grade."
                : $"Against the walls it is {least:0} below the floor: Approved Document C asks for the damp-proof course 150 above it - keep it 150 down, or make it a level threshold drained at the door.");
        if (hard && toward)
            problems.Add("It falls toward the building: rain runs to the walls. Make it fall away from them.");
        else if (hard && tooFlat)
            problems.Add(us
                ? $"Against the walls it is too flat: the IRC and the IBC ask a hard surface within 10 ft{Units.MetricAside("3048")} of them to fall 1 in 48 (2%) away."
                : "Against the walls it is level: give it a fall away from them - 1 in 48 (2%) or so - so rain runs off.");
        if (pad.Batter > 0 && pad.Batter < 1 - 1e-9)
            problems.Add($"Battered at 1 in {pad.Batter:0.##}, steeper than earth will stand: hold its edge with a retaining wall (a Batter of 0), or batter it 1 in 1 or gentler.");
        return problems;
    }

    public static string Verdict(BimDocument document, SitePad pad)
    {
        var problems = Problems(document, pad);
        return problems.Count > 0 ? string.Join(" ", problems) : "Clear of the walls, and falling right";
    }
}

/// <summary>
/// The line round a property - the plot's legal boundary - drawn by its corners: its area and
/// perimeter, each side's length and bearing in plan, and a setback inside it that the building
/// is kept behind, as a planning condition or a covenant sets it.
/// </summary>
public sealed class PropertyLine : Element
{
    private readonly List<Point2D> _boundary = new();

    public override BuiltInCategory Category => BuiltInCategory.PropertyLines;

    /// <summary>Its corners, anticlockwise.</summary>
    public IReadOnlyList<Point2D> Boundary => _boundary;

    public void SetBoundary(IEnumerable<Point2D> points)
    {
        var replacement = points.ToList();
        _boundary.Clear();
        _boundary.AddRange(replacement);
        if (Polygon2D.SignedArea(_boundary) < 0) _boundary.Reverse();
    }

    /// <summary>How far in from it the building is to be kept, mm; none for no setback.</summary>
    public double Setback { get; set; }

    /// <summary>How it is edged on the planning drawings: red round the application site, blue round other land the applicant owns.</summary>
    public Views.PlanningEdge Planning { get; set; }

    public override IEnumerable<ParameterValue> GetInstanceParameters(BimDocument document)
    {
        yield return ParameterValue.BindChoice(PropertyLineParameters.Planning,
            () => PropertyLineParameters.PlanningEdges[(int)Planning],
            value => Planning = (Views.PlanningEdge)Math.Max(0, Array.IndexOf(PropertyLineParameters.PlanningEdges, value)),
            PropertyLineParameters.PlanningEdges);
        yield return ParameterValue.BindValidated(PropertyLineParameters.Setback, () => Setback, (double v) =>
        {
            if (!double.IsFinite(v) || v < 0 || v > 100000) return false;
            Setback = v;
            return true;
        });
        yield return ParameterValue.ReadOnly(PropertyLineParameters.Area, () => Polygon2D.Area(Boundary));
        yield return ParameterValue.ReadOnly(PropertyLineParameters.Perimeter, () => Polygon2D.Perimeter(Boundary));
        yield return ParameterValue.ReadOnly(PropertyLineParameters.Coverage, () => $"{PropertyLines.Coverage(document, this) * 100:0.#}%");
        yield return ParameterValue.ReadOnly(PropertyLineParameters.Rule, () => PropertyLines.Verdict(document, this));
        if (!document.ProjectInformation.ClearanceStandard.IsUs())
            yield return ParameterValue.ReadOnly(PropertyLineParameters.SpaceSeparation, () => SpaceSeparation.Summary(document));

        foreach (var parameter in GetCommonParameters(document)) yield return parameter;
    }
}

public static class PropertyLineParameters
{
    public static readonly ParameterDefinition Setback = new("Setback (0 for none)", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition Area = new("Area", ParameterDataType.Area, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Perimeter = new("Perimeter", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Coverage = new("Plot Covered by the Building", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Rule = new("Building Within It", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.IdentityData);
    public static readonly ParameterDefinition SpaceSeparation = new("Space Separation (Approved Document B)", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.IdentityData);
    public static readonly ParameterDefinition Planning = new("Planning Edge", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.IdentityData);

    /// <summary>The planning edges as Properties offers them, in the order of <see cref="Views.PlanningEdge"/>.</summary>
    public static readonly string[] PlanningEdges = { "None", "Red line - the application site", "Blue line - other land owned" };
}

/// <summary>Property lines: each side's length and bearing, how much of the plot the building covers, and whether the building is inside it and behind its setback.</summary>
public static class PropertyLines
{
    /// <summary>A side's bearing in plan, as a surveyor writes it: "N 45°30' E" - from north or south, toward east or west - with the project's north up the page.</summary>
    public static string Bearing(Point2D from, Point2D to)
    {
        var way = to - from;
        if (way.Length < 1e-9) return "-";
        var azimuth = (Math.Atan2(way.X, way.Y) * 180 / Math.PI + 360) % 360;
        var (start, angle, end) = azimuth switch
        {
            <= 90 => ("N", azimuth, "E"),
            <= 180 => ("S", 180 - azimuth, "E"),
            <= 270 => ("S", azimuth - 180, "W"),
            _ => ("N", 360 - azimuth, "W")
        };
        var degrees = (int)Math.Floor(angle + 1e-9);
        var minutes = (int)Math.Round((angle - degrees) * 60);
        if (minutes == 60) (degrees, minutes) = (degrees + 1, 0);
        if (degrees == 0 && minutes == 0) return start == "N" ? "N" : "S";
        if (degrees == 90 && minutes == 0) return end;
        return $"{start} {degrees}°{minutes:00}' {end}";
    }

    /// <summary>The building's footprint in plan: its ground storey's floors and the outlines its outside walls stand on, as one.</summary>
    public static double Footprint(BimDocument document)
    {
        // The lowest storey the building stands on: of its floors, and of its walls - a garden's retaining walls are not the building.
        double Elevation(Element element) => document.FindLevel(element.LevelId)?.Elevation ?? 0;
        var floors = document.All<Floor>().Where(floor => floor.Boundary.Count >= 3).ToList();
        var walls = document.Walls.Where(wall => wall.Length >= 1 && document.GetWallType(wall) is { Function: not WallFunction.Retaining }).ToList();
        if (floors.Count + walls.Count == 0) return 0;
        var lowest = floors.Cast<Element>().Concat(walls).Min(Elevation);

        // Its floors there and the bodies of its walls, as one - all inside the outside of them,
        // so a house drawn without its floor yet still covers what its walls stand round.
        var outlines = floors.Where(floor => Math.Abs(Elevation(floor) - lowest) < 1).Select(floor => floor.Boundary).ToList();
        foreach (var wall in walls.Where(wall => Math.Abs(Elevation(wall) - lowest) < 1))
        {
            var half = document.GetWallType(wall)!.Structure.TotalWidth / 2;
            if (half > 0) outlines.Add(CurtainGeometry.Band(wall, document.GetWallType(wall)!, 0, wall.Length, half, -half));
        }

        if (outlines.Count == 0) return 0;
        if (outlines.Count == 1) return Polygon2D.Area(outlines[0]);
        var union = new List<IReadOnlyList<Point2D>> { outlines[0] };
        foreach (var more in outlines.Skip(1))
        {
            // Merged with each piece it overlaps; kept on its own where it meets none.
            var touching = union.Where(piece => PolygonBoolean.Combine(piece, more, BooleanOperation.Intersection).Count > 0).ToList();
            var merged = touching.Aggregate((IReadOnlyList<Point2D>)more, (sum, piece) =>
                PolygonBoolean.Combine(sum, piece, BooleanOperation.Union).OrderByDescending(region => Polygon2D.Area(region.Outer)).First().Outer);
            union = union.Except(touching).Append(merged).ToList();
        }

        return union.Sum(Polygon2D.Area);
    }

    /// <summary>How much of the plot the building covers: its footprint over the plot's area.</summary>
    public static double Coverage(BimDocument document, PropertyLine line)
    {
        var area = Polygon2D.Area(line.Boundary);
        return area <= 1 || Elsewhere(document, line) ? 0 : Math.Min(1, Footprint(document) / area);
    }

    /// <summary>
    /// Whether any of the building stands on a plot - a wall of it inside the line. Land beside it,
    /// the applicant's other land edged in blue, has none of it: not covered, and not stood over.
    /// </summary>
    /// <summary>Whether the building is on another plot and none of it on this one: land beside it, not the building's.</summary>
    public static bool Elsewhere(BimDocument document, PropertyLine line) =>
        !Holds(document, line) && document.All<PropertyLine>().Any(other => !ReferenceEquals(other, line) && Holds(document, other));

    public static bool Holds(BimDocument document, PropertyLine line) =>
        line.Boundary.Count >= 3 && document.Walls.Any(wall => wall.Length >= 1 && document.GetWallType(wall) is { Function: not WallFunction.Retaining }
            && Enumerable.Range(0, Math.Max(1, (int)Math.Ceiling(wall.Length / 500)) + 1).Any(k => Polygon2D.Contains(line.Boundary, wall.LocationCurve.PointAt(wall.Length * k / Math.Max(1, (int)Math.Ceiling(wall.Length / 500))))));

    /// <summary>The line inside it the building is kept behind, its setback in from each side - none with no setback.</summary>
    public static IReadOnlyList<Point2D> SetbackLine(PropertyLine line) =>
        line.Setback <= 0 || line.Boundary.Count < 3 ? Array.Empty<Point2D>() : Railings.SetIn(line.Boundary, true, line.Setback);

    /// <summary>What about the building and the plot falls short: walls standing over the property line, or nearer it than its setback.</summary>
    public static IReadOnlyList<string> Problems(BimDocument document, PropertyLine line)
    {
        var problems = new List<string>();
        if (line.Boundary.Count < 3) return problems;
        if (Elsewhere(document, line)) return problems;
        var (outside, nearest) = (0, double.PositiveInfinity);
        foreach (var wall in document.Walls.Where(wall => wall.Length >= 1 && document.GetWallType(wall) is { Function: not WallFunction.Retaining }))
        {
            var half = (document.GetWallType(wall)?.Structure.TotalWidth ?? 0) / 2;
            var count = Math.Max(1, (int)Math.Ceiling(wall.Length / 500));
            for (var k = 0; k <= count; k++)
            {
                var at = wall.LocationCurve.PointAt(wall.Length * k / count);
                if (!Polygon2D.Contains(line.Boundary, at)) outside++;
                var gap = Enumerable.Range(0, line.Boundary.Count)
                    .Min(i => Line2D.DistanceFromSegment(at, line.Boundary[i], line.Boundary[(i + 1) % line.Boundary.Count])) - half;
                nearest = Math.Min(nearest, Polygon2D.Contains(line.Boundary, at) ? gap : -gap);
            }
        }

        if (outside > 0) problems.Add("Walls of the building stand over the property line.");
        else if (line.Setback > 0 && nearest < line.Setback - 0.5)
            problems.Add($"The building comes within {Units.Metres(nearest, "0.00")} of the property line: its setback is {Units.Metres(line.Setback, "0.00")}.");
        return problems;
    }

    public static string Verdict(BimDocument document, PropertyLine line)
    {
        var problems = Problems(document, line);
        if (problems.Count > 0) return string.Join(" ", problems);
        return !document.Walls.Any() ? "No building yet" : Elsewhere(document, line) ? "No building on it" : line.Setback > 0 ? "Within it, behind its setback" : "Within it";
    }
}

/// <summary>
/// A spot elevation on the plan: the height of what is at a point - a floor, a ramp, a pad, the
/// ground - read off the model and written beside a mark there, as "FFL +0.000" or "GL -0.450";
/// it reads afresh when what is under it moves.
/// </summary>
public sealed class SpotElevation : Element
{
    public override BuiltInCategory Category => BuiltInCategory.SpotElevations;

    /// <summary>The point whose height it reads.</summary>
    public Point2D Location { get; set; }

    public override IEnumerable<ParameterValue> GetInstanceParameters(BimDocument document)
    {
        var (height, what) = SpotElevations.Reading(document, this);
        yield return ParameterValue.ReadOnly(SpotElevationParameters.Height, () => Math.Round(height, 1));
        yield return ParameterValue.ReadOnly(SpotElevationParameters.Reads, () => what);
        yield return ParameterValue.ReadOnly(SpotElevationParameters.Label, () => SpotElevations.Label(document, this));

        foreach (var parameter in GetCommonParameters(document)) yield return parameter;
    }
}

public static class SpotElevationParameters
{
    public static readonly ParameterDefinition Height = new("Elevation", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Reads = new("Reads", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.IdentityData);
    public static readonly ParameterDefinition Label = new("Label", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.IdentityData);
}

/// <summary>What a spot elevation reads: the height of the top of whatever is at its point on its storey, and what that is.</summary>
public static class SpotElevations
{
    public static (double Height, string What) Reading(BimDocument document, SpotElevation spot)
    {
        var level = document.FindLevel(spot.LevelId)?.Elevation ?? 0;
        var at = spot.Location;
        if (Ramps.SurfaceOn(document, spot.LevelId, at) is { } ramp) return (ramp, "Ramp");
        if (FloorSurfaces.StoodOnNear(document, level, at, Terrains.Reach) is { } floor) return (floor.Top, floor.Slab is Roof ? "Roof" : "Floor");
        if (SitePads.On(document, spot.LevelId).FirstOrDefault(pad => Polygon2D.Contains(pad.Boundary, at)) is { } pad) return (SitePads.HeightAt(document, pad, at), "Pad");
        if (Terrains.GroundNear(document, level, at) is { } ground) return (ground, "Ground");
        return (level, "Level");
    }

    /// <summary>Its label: what it reads, and the height signed - in metres, "FFL +0.000", "GL -0.450"; in a US project "GL -0'-6"".</summary>
    public static string Label(BimDocument document, SpotElevation spot)
    {
        var (height, what) = Reading(document, spot);
        var prefix = what switch { "Floor" => "FFL", "Ground" => "GL", "Pad" => "PL", "Ramp" => "RL", "Roof" => "RL", _ => "LVL" };
        return $"{prefix} {Units.Elevation(height)}";
    }
}
