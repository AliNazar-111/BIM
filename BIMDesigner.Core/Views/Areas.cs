using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Parameters;

namespace BIMDesigner.Core.Views;

/// <summary>
/// What an area on a rentable area plan is used for. Each falls in BOMA 2017's categories and in an
/// IPMS component area: an office or shop is an occupant's (BOMA occupant area; IPMS F, measured as
/// IPMS 3.2); a tenant's store is left out of the rentable area (occupant storage; G2); an amenity is
/// the building's (building amenity; H3); a main lobby and plant serve the building (building
/// service; E and C); toilets and corridors serve the floor (floor service; D and E - a corridor is
/// base building circulation in Method B); stairs, lifts and shafts are major vertical penetrations
/// (B1, B2); parking is left out (H3).
/// </summary>
public enum AreaUse
{
    None,
    Office,
    Retail,
    TenantStorage,
    Amenity,
    Lobby,
    Plant,
    Toilets,
    Corridor,
    Stair,
    Lift,
    Shaft,
    Parking
}

/// <summary>A line splitting one area plan by hand - a storey measured in one scheme - and drawn on that plan only.</summary>
public sealed class AreaBoundary : Element
{
    public override BuiltInCategory Category => BuiltInCategory.AreaBoundaries;

    public Point2D Start { get; set; }

    public Point2D End { get; set; }

    /// <summary>The scheme of the plan it splits.</summary>
    public AreaScheme Scheme { get; set; } = AreaScheme.GrossInternal;

    /// <summary>The wall it was picked from, whose middle it follows; none for one drawn, or one whose ends were moved by hand.</summary>
    public Guid? WallId { get; set; }

    /// <summary>How far it bows, as a wall does: the tangent of a quarter of the angle its arc turns through, positive turning left - bowing out to the right of the way it runs; none for a straight one.</summary>
    public double Bulge { get; set; }

    /// <summary>The line or arc it runs along.</summary>
    public WallCurve Curve => WallCurve.Of(Start, End, Bulge);

    public double Length => Math.Abs(Bulge) < WallCurve.StraightBulge ? Start.DistanceTo(End) : Curve.Length;

    /// <summary>Its line, or its arc as short straight pieces: what it splits the plan by.</summary>
    public IReadOnlyList<(Point2D From, Point2D To)> Pieces()
    {
        if (Math.Abs(Bulge) < WallCurve.StraightBulge) return new[] { (Start, End) };
        var points = Curve.Points();
        return points.Zip(points.Skip(1)).ToList();
    }

    public override IEnumerable<ParameterValue> GetInstanceParameters(BimDocument document)
    {
        yield return ParameterValue.ReadOnly(AreaParameters.Scheme, () => AreaPlans.Title(document, Scheme));
        yield return ParameterValue.ReadOnly(AreaParameters.Length, () => Length);
        foreach (var common in GetCommonParameters(document)) yield return common;
    }
}

/// <summary>
/// One area of an area plan: named and numbered, what it is used for, and measured as the space its
/// area boundaries and the storey's outline close round where it is placed - less what the scheme
/// leaves out there.
/// </summary>
public sealed class Area : Element
{
    public override BuiltInCategory Category => BuiltInCategory.Areas;

    public Point2D Location { get; set; }

    public string Name { get; set; } = "Area";

    public string Number { get; set; } = string.Empty;

    public AreaScheme Scheme { get; set; } = AreaScheme.GrossInternal;

    public AreaUse Use { get; set; }

    public override IEnumerable<ParameterValue> GetInstanceParameters(BimDocument document)
    {
        var shape = new Lazy<AreaShape>(() => Areas.Shape(document, this));
        yield return ParameterValue.Bind(AreaParameters.Name, () => Name, value => { if (!string.IsNullOrWhiteSpace(value)) Name = value.Trim(); });
        yield return ParameterValue.Bind(AreaParameters.Number, () => Number, value => Number = value.Trim());
        if (Scheme == AreaScheme.Rentable)
            yield return ParameterValue.BindChoice(AreaParameters.Use, () => Areas.Name(Use),
                value => Use = Enum.GetValues<AreaUse>().FirstOrDefault(use => Areas.Name(use) == value), Enum.GetValues<AreaUse>().Select(Areas.Name).ToList());
        yield return ParameterValue.ReadOnly(AreaParameters.Scheme, () => AreaPlans.Title(document, Scheme));
        yield return ParameterValue.ReadOnly(AreaParameters.Area, () => shape.Value.Polygon is null ? (double?)null : shape.Value.Area);
        yield return ParameterValue.ReadOnly(AreaParameters.Enclosed, () => shape.Value.Polygon is not null && shape.Value.Shared is null);
        if ((shape.Value.Why ?? shape.Value.Shared) is { } said)
            yield return ParameterValue.ReadOnly(AreaParameters.Status, () => said);
        if (Scheme == AreaScheme.Rentable)
        {
            yield return ParameterValue.ReadOnly(AreaParameters.Boma, () => Areas.BomaClass(Use));
            yield return ParameterValue.ReadOnly(AreaParameters.Ipms, () => Areas.IpmsClass(Use));
        }

        foreach (var common in GetCommonParameters(document)) yield return common;
    }
}

public static class AreaParameters
{
    public static readonly ParameterDefinition Name = new("Name", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.IdentityData);
    public static readonly ParameterDefinition Number = new("Number", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.IdentityData);
    public static readonly ParameterDefinition Use = new("Use", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.IdentityData);
    public static readonly ParameterDefinition Scheme = new("Area Scheme", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Other);
    public static readonly ParameterDefinition Area = new("Area", ParameterDataType.Area, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Enclosed = new("Enclosed", ParameterDataType.YesNo, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Status = new("Status", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Length = new("Length", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Boma = new("BOMA 2017", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Other);
    public static readonly ParameterDefinition Ipms = new("IPMS Component", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Other);
}

/// <summary>
/// An area as it is measured: the space it is placed in - null where nothing closes round it, and
/// why - its area, and the earlier area it shares that space with, where it does.
/// </summary>
public sealed record AreaShape(IReadOnlyList<Point2D>? Polygon, double Area, string? Why, string? Shared = null);

/// <summary>
/// Areas drawn by hand on an area plan: the space each is closed in by the plan's area boundaries
/// and the storey's outline, measured as its scheme measures the storey - what the scheme leaves
/// out there taken out of it - and the uses a rentable area's areas are put to.
/// </summary>
public static class Areas
{
    public static IEnumerable<Area> On(BimDocument document, Guid levelId, AreaScheme scheme) =>
        document.All<Area>().Where(area => area.LevelId == levelId && area.Scheme == scheme);

    public static IEnumerable<AreaBoundary> Boundaries(BimDocument document, Guid levelId, AreaScheme scheme)
    {
        FollowWalls(document);
        return document.All<AreaBoundary>().Where(line => line.LevelId == levelId && line.Scheme == scheme);
    }

    /// <summary>
    /// Each boundary picked from a wall taken to where the wall's middle now is - the wall moved, lengthened, made thicker or
    /// bowed - as it is measured and drawn, along the same arc for a curved wall; a wall gone, elliptical or a spline leaves it where it was.
    /// </summary>
    public static void FollowWalls(BimDocument document)
    {
        foreach (var line in document.All<AreaBoundary>())
            if (line.WallId is { } id && document.Find<Wall>(id) is { IsElliptical: false, IsSpline: false } wall && document.GetWallType(wall) is { } type)
                (line.Start, line.End, line.Bulge) = Along(wall, type.Structure);
    }

    /// <summary>The middle of a straight or arc wall's body: its ends, and how far it bows - an arc moved sideways keeps its bow.</summary>
    public static (Point2D Start, Point2D End, double Bulge) Along(Wall wall, Materials.CompoundStructure structure)
    {
        var (start, end) = wall.GetBodyCentreline(structure);
        return (start, end, wall.IsCurved ? wall.Bulge : 0);
    }

    /// <summary>The number the next area on a plan takes: one more than the highest that is a number.</summary>
    public static string NextNumber(BimDocument document, Guid levelId, AreaScheme scheme) =>
        (On(document, levelId, scheme).Select(area => int.TryParse(area.Number, out var n) ? n : 0).DefaultIfEmpty().Max() + 1).ToString();

    public static AreaShape Shape(BimDocument document, Area area) =>
        ModelCache.Of(document) is { } cache
            ? cache.Keep("area shape", document, area.Id, null, () => Shaped(document, area))
            : Shaped(document, area);

    private static AreaShape Shaped(BimDocument document, Area area)
    {
        var frame = AreaPlans.Frame(document, area.LevelId, area.Scheme);
        if (!frame.Any(outline => Polygon2D.Contains(outline, area.Location)))
            return new AreaShape(null, 0, "Not inside the storey's outline: it is measured within the walls that close round the building.");

        var lines = Boundaries(document, area.LevelId, area.Scheme).SelectMany(line => line.Pieces())
            .Concat(frame.SelectMany(outline => outline.Select((point, i) => (point, outline[(i + 1) % outline.Count]))));
        if (RoomBoundary.Face(lines, area.Location) is not { } face)
            return new AreaShape(null, 0, "Not enclosed: its area boundaries do not close round it.");

        // Two areas in one space: the first, by number, is measured; the other says so and counts nothing.
        var first = On(document, area.LevelId, area.Scheme)
            .Where(other => Polygon2D.Contains(face, other.Location))
            .OrderBy(other => int.TryParse(other.Number, out var n) ? n : int.MaxValue).ThenBy(other => other.Number).ThenBy(other => other.Id)
            .First();
        if (!ReferenceEquals(first, area))
            return new AreaShape(face, 0, null, $"Shares its space with {Said(first)}: draw an area boundary between them.");

        // What the scheme measures there: the storey's outlines over it, less what is left out of them.
        var measure = AreaPlans.Measure(document, area.LevelId, area.Scheme);
        var counted = measure.Outlines.Sum(outline => Overlap(face, outline));
        var excluded = measure.Parts
            .Where(part => part.Kind == AreaPartKind.Excluded && part.Outline is { Count: >= 3 } outline
                           && measure.Outlines.Any(within => Polygon2D.Contains(within, Polygon2D.Centroid(outline, outline[0]))))
            .Sum(part => Overlap(face, part.Outline!));
        return new AreaShape(face, Math.Max(0, counted - excluded), null);
    }

    /// <summary>How much of one outline lies over another, mm².</summary>
    private static double Overlap(IReadOnlyList<Point2D> a, IReadOnlyList<Point2D> b) =>
        PolygonBoolean.Combine(a, b, BooleanOperation.Intersection).Sum(region => Polygon2D.Area(region.Outer) - region.Holes.Sum(Polygon2D.Area));

    public static string Said(Area area) => area.Number.Length > 0 ? $"{area.Name} ({area.Number})" : area.Name;

    public static string Name(AreaUse use) => use switch
    {
        AreaUse.None => "Not set",
        AreaUse.TenantStorage => "Tenant storage",
        _ => use.ToString()
    };

    /// <summary>The BOMA 2017 category an area's use puts it in.</summary>
    public static string BomaClass(AreaUse use) => use switch
    {
        AreaUse.Office or AreaUse.Retail => "Occupant area",
        AreaUse.TenantStorage => "Rentable exclusion (occupant storage)",
        AreaUse.Amenity => "Building amenity area",
        AreaUse.Lobby or AreaUse.Plant => "Building service area",
        AreaUse.Toilets => "Floor service area",
        AreaUse.Corridor => "Floor service area (base building circulation, Method B)",
        AreaUse.Stair or AreaUse.Lift or AreaUse.Shaft => "Major vertical penetration",
        AreaUse.Parking => "Rentable exclusion (parking)",
        _ => "Floor service area, until its use is set"
    };

    /// <summary>The IPMS component area an area's use puts it in (IPMS: All Buildings, Table 1).</summary>
    public static string IpmsClass(AreaUse use) => use switch
    {
        AreaUse.Office or AreaUse.Retail => "F Primary area",
        AreaUse.TenantStorage => "G2 Ancillary area",
        AreaUse.Amenity => "H3 Standard Facilities",
        AreaUse.Lobby or AreaUse.Corridor => "E Horizontal circulation",
        AreaUse.Plant => "C Technical area",
        AreaUse.Toilets => "D Sanitary area",
        AreaUse.Stair or AreaUse.Lift => "B1 Vertical circulation",
        AreaUse.Shaft => "B2 Vertical technical area",
        AreaUse.Parking => "H3 Standard Facilities",
        _ => "H1 Other area"
    };

    /// <summary>The component letter an area's use counts under.</summary>
    public static char Component(AreaUse use) => IpmsClass(use)[0];

    public static bool Occupant(AreaUse use) => use is AreaUse.Office or AreaUse.Retail;

    public static bool Penetration(AreaUse use) => use is AreaUse.Stair or AreaUse.Lift or AreaUse.Shaft;
}

/// <summary>A storey's rentable area by BOMA 2017 Method A, and the figures it is worked from, mm².</summary>
public sealed record RentableFloor(
    Level Level, double Boundary, double Penetrations, double Exclusions, double Occupant, double Amenity,
    double BuildingService, double Circulation, double? AllocationRatio)
{
    /// <summary>Boundary area less major vertical penetrations and the other exclusions.</summary>
    public double FloorRentable => Boundary - Penetrations - Exclusions;

    /// <summary>Occupant and building amenity areas.</summary>
    public double Usable => Occupant + Amenity;

    /// <summary>What is left of the floor rentable area: what serves the floor.</summary>
    public double FloorService => Math.Max(0, FloorRentable - Usable - BuildingService);

    /// <summary>The building's amenity and service on this storey, as Method A shares them across the building.</summary>
    public double AmenityAndService => AllocationRatio is { } ratio ? BuildingService + Amenity * ratio : FloorRentable;
}

/// <summary>An occupant's area and its rentable area by Methods A and B, mm².</summary>
public sealed record RentableShare(Area Area, Level Level, double Occupant, double RentableA, double RentableB)
{
    public double LoadFactorA => Occupant > 0 ? RentableA / Occupant : 0;

    public double LoadFactorB => Occupant > 0 ? RentableB / Occupant : 0;
}

/// <summary>A storey's IPMS figures: IPMS 1 and 2, the exclusive areas (IPMS 3.2) and each component area, mm².</summary>
public sealed record IpmsFloor(Level Level, double Ipms1, double Ipms2, double Exclusive, IReadOnlyDictionary<char, double> Components);

/// <summary>
/// Office space let by its rentable area. Each storey's boundary area is measured to the inside faces
/// of its outside walls, and split by hand into areas of each use. By ANSI/BOMA Z65.1-2017 (Office):
/// the floor rentable area is the boundary area less major vertical penetrations - not at the lowest
/// storey they are on, where they have a floor - and other exclusions (occupant storage, parking);
/// Method A gives each occupant its floor's service in proportion (the floor allocation ratio) and the
/// building's amenity and service across the building (the building allocation ratio); Method B one
/// load factor for every occupant, the building's floor rentable area over its occupant area. By IPMS:
/// All Buildings (2023), IPMS 1 is taken as the gross external area - both to the outside of the
/// outside walls and the middle of one shared with the next building, voids out above their base -
/// IPMS 2 as the boundary area, each occupant's area as its IPMS 3.2, and the component areas from the
/// uses: A, the walls, is what IPMS 1 has over IPMS 2; H1, what no area covers.
/// </summary>
public static class RentableAreas
{
    public static IReadOnlyList<RentableFloor> Floors(BimDocument document) =>
        Worked(document).Floors;

    public static IReadOnlyList<RentableShare> Shares(BimDocument document) =>
        Worked(document).Shares;

    /// <summary>The building allocation ratio of Method A; null with no occupant to allocate to.</summary>
    public static double? BuildingRatio(BimDocument document) => Worked(document).BuildingRatio;

    /// <summary>Method B's load factor; null with no occupant area.</summary>
    public static double? LoadFactorB(BimDocument document) => Worked(document).LoadFactorB;

    private sealed record Work(IReadOnlyList<RentableFloor> Floors, IReadOnlyList<RentableShare> Shares, double? BuildingRatio, double? LoadFactorB);

    private static Work Worked(BimDocument document) =>
        ModelCache.Of(document) is { } cache ? cache.Keep("rentable areas", document, null, null, () => Working(document)) : Working(document);

    /// <summary>Each storey with a boundary area, lowest first, and its areas with what they measure.</summary>
    private static IEnumerable<(Level Level, AreaMeasure Measure, List<(Area Area, double Measured)> Areas)> Storeys(BimDocument document) =>
        AreaPlans.Storeys(document, AreaScheme.Rentable).Select(entry => (entry.Level, entry.Measure,
            Areas.On(document, entry.Level.Id, AreaScheme.Rentable)
                .Select(area => (Area: area, Shape: Areas.Shape(document, area)))
                .Where(pair => pair.Shape.Polygon is not null && pair.Shape.Shared is null)
                .Select(pair => (pair.Area, pair.Shape.Area))
                .ToList()));

    private static Work Working(BimDocument document)
    {
        var storeys = Storeys(document).ToList();
        var floors = new List<RentableFloor>();
        var below = new List<IReadOnlyList<Point2D>>();
        foreach (var (level, measure, areas) in storeys)
        {
            double Sum(Func<AreaUse, bool> which) => areas.Where(entry => which(entry.Area.Use)).Sum(entry => entry.Measured);

            // A vertical penetration is left out where it passes through; at the lowest storey it is on, it stands on a floor.
            var penetrations = areas.Where(entry => Areas.Penetration(entry.Area.Use) && below.Any(polygon => Polygon2D.Contains(polygon, entry.Area.Location))).Sum(entry => entry.Measured);
            below = areas.Where(entry => Areas.Penetration(entry.Area.Use)).Select(entry => Areas.Shape(document, entry.Area).Polygon!).ToList();

            var occupant = Sum(Areas.Occupant);
            var amenity = Sum(use => use == AreaUse.Amenity);
            floors.Add(new RentableFloor(level, measure.Area, penetrations, Sum(use => use is AreaUse.TenantStorage or AreaUse.Parking),
                occupant, amenity, Sum(use => use is AreaUse.Lobby or AreaUse.Plant), Sum(use => use == AreaUse.Corridor), null));
        }

        // Method A: each storey's floor service shared by what is usable on it; a storey with nothing usable is shared by the building.
        floors = floors.Select(floor => floor.Usable > 0
            ? floor with { AllocationRatio = (floor.Usable + floor.FloorService) / floor.Usable }
            : floor).ToList();
        var rentable = floors.Sum(floor => floor.FloorRentable);
        var allocated = rentable - floors.Sum(floor => floor.AmenityAndService);
        double? buildingRatio = allocated > 1e-6 && floors.Any(floor => floor.Occupant > 0) ? rentable / allocated : null;

        // Method B: one load factor for the building.
        var occupants = floors.Sum(floor => floor.Occupant);
        double? loadFactorB = occupants > 1e-6 ? rentable / occupants : null;

        var shares = new List<RentableShare>();
        foreach (var ((level, _, areas), floor) in storeys.Zip(floors))
            foreach (var (area, measured) in areas.Where(entry => Areas.Occupant(entry.Area.Use)))
                shares.Add(new RentableShare(area, level, measured,
                    buildingRatio is { } ratio && floor.AllocationRatio is { } floorRatio ? measured * floorRatio * ratio : 0,
                    loadFactorB is { } factor ? measured * factor : 0));

        return new Work(floors, shares, buildingRatio, loadFactorB);
    }

    /// <summary>Each storey's IPMS figures, lowest first.</summary>
    public static IReadOnlyList<IpmsFloor> Ipms(BimDocument document) =>
        Storeys(document).Select(storey =>
        {
            var (level, measure, areas) = storey;
            var ipms1 = AreaPlans.Measure(document, level.Id, AreaScheme.GrossExternal).Area;
            var components = "ABCDEFGH".ToDictionary(letter => letter, _ => 0.0);
            foreach (var (area, measured) in areas) components[Areas.Component(area.Use)] += measured;
            components['A'] = Math.Max(0, ipms1 - measure.Area);
            components['H'] += Math.Max(0, measure.Area - areas.Sum(entry => entry.Measured));
            return new IpmsFloor(level, ipms1, measure.Area, areas.Where(entry => Areas.Occupant(entry.Area.Use) || entry.Area.Use == AreaUse.TenantStorage).Sum(entry => entry.Measured), components);
        }).ToList();
}
