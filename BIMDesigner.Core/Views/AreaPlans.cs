using System.Text.RegularExpressions;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;
using BIMDesigner.Core.Schedules;
using BIMDesigner.Core.Sheets;

namespace BIMDesigner.Core.Views;

/// <summary>How a storey's floor area is measured: an area plan's scheme.</summary>
public enum AreaScheme
{
    /// <summary>Gross External Area: measured externally at each floor level (RICS Code of Measuring Practice, 1.0) - in the US the gross area to the outside faces.</summary>
    GrossExternal = 1,

    /// <summary>Gross Internal Area: to the internal face of the perimeter walls at each floor level (2.0).</summary>
    GrossInternal = 2,

    /// <summary>Net Internal Area: the usable area within the perimeter walls (3.0).</summary>
    NetInternal = 3,

    /// <summary>A US home's finished area by ANSI Z765-2021: to the outside faces, finished and tall enough, above grade and below it apart.</summary>
    Finished = 4
}

/// <summary>What a part of a storey's measure does to its area: taken out, put in, or in it and said separately.</summary>
public enum AreaPartKind
{
    Excluded,
    Added,
    Stated
}

/// <summary>A part of a storey's measure: what it is and why, by which clause; its area; and where it is - an outline, or lines - where it can be drawn.</summary>
public sealed record AreaPart(AreaPartKind Kind, string Said, double Area, IReadOnlyList<Point2D>? Outline, IReadOnlyList<(Point2D From, Point2D To)> Lines)
{
    /// <summary>What it is, short enough to write in it on a plan; empty to take it from what is said.</summary>
    public string Label { get; init; } = string.Empty;
}

/// <summary>A storey measured in a scheme: the outlines measured, what is taken out, put in or said beside them, the area, and whether it is below grade.</summary>
public sealed record AreaMeasure(AreaScheme Scheme, Guid LevelId, IReadOnlyList<IReadOnlyList<Point2D>> Outlines, IReadOnlyList<AreaPart> Parts, double Area, bool BelowGrade, string? Note)
{
    public bool IsEmpty => Outlines.Count == 0 && Parts.Count == 0;
}

/// <summary>
/// Area plans: each storey measured as surveyors measure it, from the model as it stands. In
/// England by the RICS Code of Measuring Practice (6th edition) - Gross External Area to the
/// outside faces of the outside walls and the middle of a party wall; Gross Internal Area to
/// their inside faces, taking in the walls between rooms, stairwells and the floor under 1.5 m of
/// headroom (stated beside it), leaving out voids over the storey below and a home's garden
/// stores; Net Internal Area by the rooms, leaving out toilets and bathrooms, stairwells, plant,
/// parking and floor under 1.5 m, with the non-structural partitions between the rooms kept. In
/// the US a home's finished area by ANSI Z765-2021 - to the outside faces, leaving out garages,
/// unfinished space, openings to the floor below, a room with less than half of it at 7 ft and
/// floor under 5 ft - above grade and below grade apart, to the nearest square foot.
/// </summary>
public static class AreaPlans
{
    /// <summary>An area plan's index on its storey's plan reference: past the services plans' disciplines.</summary>
    private const int First = 10;

    /// <summary>The least headroom counted in a net internal area, and stated under a gross one, mm (GIA 2.14, NIA 3.20).</summary>
    public const double CountedHeadroom = 1500;

    /// <summary>ANSI Z765's ceiling: 7 ft over at least half a room; nothing under 5 ft counted, mm.</summary>
    public const double FinishedCeiling = 2133.6;

    public const double FinishedLeast = 1524;

    public static ViewReference View(Guid levelId, AreaScheme scheme) => ViewReference.FloorPlan(levelId) with { Index = First + (int)scheme };

    /// <summary>The scheme a plan is measured in, or null for a plan that is not an area plan.</summary>
    public static AreaScheme? Of(ViewReference view) =>
        view.Kind == ViewKind.FloorPlan && Enum.IsDefined(typeof(AreaScheme), view.Index - First) ? (AreaScheme)(view.Index - First) : null;

    /// <summary>The schemes a project is measured in: GEA, GIA and NIA; in the US the gross and the finished area.</summary>
    public static IReadOnlyList<AreaScheme> Schemes(BimDocument document) => document.ProjectInformation.ClearanceStandard.IsUs()
        ? new[] { AreaScheme.GrossExternal, AreaScheme.Finished }
        : new[] { AreaScheme.GrossExternal, AreaScheme.GrossInternal, AreaScheme.NetInternal };

    public static string Title(BimDocument document, AreaScheme scheme) => scheme switch
    {
        AreaScheme.GrossExternal when document.ProjectInformation.ClearanceStandard.IsUs() => "Gross Area",
        AreaScheme.GrossExternal => "Gross External Area (GEA)",
        AreaScheme.GrossInternal => "Gross Internal Area (GIA)",
        AreaScheme.NetInternal => "Net Internal Area (NIA)",
        _ => "Finished Area (ANSI Z765)"
    };

    public static string Short(BimDocument document, AreaScheme scheme) => scheme switch
    {
        AreaScheme.GrossExternal when document.ProjectInformation.ClearanceStandard.IsUs() => "Gross",
        AreaScheme.GrossExternal => "GEA",
        AreaScheme.GrossInternal => "GIA",
        AreaScheme.NetInternal => "NIA",
        _ => "Finished"
    };

    /// <summary>The code a scheme measures by, which the RICS asks be said beside any area given.</summary>
    public static string Code(AreaScheme scheme) => scheme == AreaScheme.Finished
        ? "Measured by ANSI Z765-2021, to the outside finished surfaces; reported to the nearest square foot."
        : "Measured by the RICS Code of Measuring Practice, 6th edition.";

    /// <summary>An area as its scheme says it: m² to a tenth, a US home's finished area to the whole square foot.</summary>
    public static string Said(AreaScheme scheme, double area) => scheme == AreaScheme.Finished || Units.Imperial
        ? $"{Math.Round(area / 92903.04):#,0} sq ft"
        : Units.SquareMetres(area, "0.0");

    // ---- what is measured to -------------------------------------------------------------

    /// <summary>
    /// A storey's outside walls by their type - outside, party and curtain walls: what a courtyard
    /// is closed round. Its outline is traced round whatever walls close it, typed as they may be.
    /// </summary>
    public static bool Perimeter(BimDocument document, Wall wall) =>
        document.FindType<CurtainWallType>(wall.TypeId) is not null
        || document.GetWallType(wall) is { } type && (type.Function == WallFunction.Exterior || type.Separating);

    private static double Half(BimDocument document, Wall wall) => (document.GetWallType(wall)?.Width ?? 0) / 2;

    /// <summary>To a party wall's middle from outside (GEA's Diagram A); to every other wall's face.</summary>
    private static double ToOutside(BimDocument document, Wall wall) => document.GetWallType(wall) is { Separating: true } ? 0 : Half(document, wall);

    private static readonly Regex Stores = new(@"\b(greenhouse|glasshouse|garden store|garden shed|shed|fuel store|bin store|log store|coal store)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Toilets = new(@"\b(wc|w\.c\.|toilet|bathroom|bath|shower|shower room|en-?suite|cloakroom|washroom|lavatory|cleaner'?s?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Plant = new(@"\b(plant|plant room|boiler room|tank room|tank|meter|riser)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Circulation = new(@"\b(stair|stairs|stairwell|stair core|lift|lift lobby|common)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Parking = new(@"\b(garage|car port|carport|parking)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Unfinished = new(@"\b(unfinished|garage|car port|carport|porch)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static string Called(Room room) => $"{room.Name} {room.Occupancy}";

    /// <summary>Why a room is left out of a net internal area, by the clause that leaves it out; null where it counts.</summary>
    public static string? NetExclusion(Room room)
    {
        var called = Called(room);
        if (Toilets.IsMatch(called)) return "toilets and bathrooms (NIA 3.13)";
        if (Plant.IsMatch(called)) return "plant, tank rooms and meters (NIA 3.14, 3.17)";
        if (Circulation.IsMatch(called)) return "stairwells, lift wells and common parts (NIA 3.12, 3.15, 3.16)";
        if (Parking.IsMatch(called)) return "parking (NIA 3.22)";
        return null;
    }

    /// <summary>A storey measured in a scheme. Kept while the model stands as it is.</summary>
    public static AreaMeasure Measure(BimDocument document, Guid levelId, AreaScheme scheme) =>
        ModelCache.Of(document) is { } cache
            ? cache.Keep($"area {scheme}", document, levelId, null, () => Measured(document, levelId, scheme))
            : Measured(document, levelId, scheme);

    private static AreaMeasure Measured(BimDocument document, Guid levelId, AreaScheme scheme)
    {
        if (document.FindLevel(levelId) is not { } level) return new AreaMeasure(scheme, levelId, Array.Empty<IReadOnlyList<Point2D>>(), Array.Empty<AreaPart>(), 0, false, null);
        if (scheme == AreaScheme.NetInternal) return Net(document, level);

        var shape = Shape(document, level);
        var parts = new List<AreaPart>();
        var outlines = new List<IReadOnlyList<Point2D>>();
        double area;

        var inside = scheme == AreaScheme.GrossInternal;
        IReadOnlyList<Point2D> Of(Outline outline) => inside ? outline.Inner : outline.Gross;
        var none = Array.Empty<(Point2D, Point2D)>();

        outlines.AddRange(shape.Buildings.Select(Of));
        area = outlines.Sum(Polygon2D.Area);
        foreach (var store in shape.DetachedStores)
            parts.Add(new AreaPart(AreaPartKind.Excluded,
                scheme == AreaScheme.Finished ? "an outbuilding, not the house"
                : inside ? "a home's garden store, greenhouse or fuel store (GIA 2.22)"
                : "a home's garden store, greenhouse or fuel store standing apart (GEA 1.20)", Polygon2D.Area(Of(store)), Of(store), none) { Label = scheme == AreaScheme.Finished ? "Outbuilding" : "Garden store" });
        foreach (var court in shape.Courts)
        {
            parts.Add(new AreaPart(AreaPartKind.Excluded, "a courtyard, open to the sky", Polygon2D.Area(Of(court)), Of(court), none) { Label = "Courtyard" });
            area -= Polygon2D.Area(Of(court));
        }

        // A garden store built onto the house: in its gross external area (GEA 1.10), not its gross internal (GIA 2.22).
        if (inside)
            foreach (var store in shape.StoreRooms)
            {
                parts.Add(new AreaPart(AreaPartKind.Excluded, $"{store.Room.Name}: a home's garden store, greenhouse or fuel store (GIA 2.22) - its walls left in", Polygon2D.Area(store.Polygon), store.Polygon, none) { Label = "Garden store" });
                area -= Polygon2D.Area(store.Polygon);
            }

        // Voids over the storey below - an atrium, a double-height room - counted at their base only; stairwells are not voids.
        foreach (var hole in Voids(document, level.Id).Where(hole => outlines.Any(outline => Polygon2D.Contains(outline, Inside(hole)))))
        {
            var said = scheme switch
            {
                AreaScheme.GrossInternal => "a void over the storey below, measured at its base (GIA 2.3, 2.21)",
                AreaScheme.GrossExternal when !document.ProjectInformation.ClearanceStandard.IsUs() => "a void over the storey below, measured at its base (GEA 1.4, 1.19)",
                _ => "an opening to the floor below"
            };
            parts.Add(new AreaPart(AreaPartKind.Excluded, said, Polygon2D.Area(hole), hole, Array.Empty<(Point2D, Point2D)>()) { Label = scheme == AreaScheme.Finished ? "Open to below" : "Void" });
            area -= Polygon2D.Area(hole);
        }

        var rooms = Rooms(document, level.Id, outlines);
        if (scheme == AreaScheme.Finished)
        {
            foreach (var (room, polygon) in rooms)
            {
                if (Unfinished.IsMatch(Called(room)))
                {
                    var outline = room.GetBoundary(document);
                    parts.Add(new AreaPart(AreaPartKind.Excluded, $"{room.Name}: a garage, porch or unfinished space - its walls left in", outline.Area, polygon, Array.Empty<(Point2D, Point2D)>()));
                    area -= outline.Area;
                    continue;
                }

                if (RoomHeadroom.Measure(document, room, FinishedLeast, FinishedCeiling) is not { UnderRoof: true } space) continue;
                if (space.FullHeightShare < 0.5)
                {
                    parts.Add(new AreaPart(AreaPartKind.Excluded, $"{room.Name}: less than half of it 7 ft high", space.Area, polygon, Array.Empty<(Point2D, Point2D)>()));
                    area -= space.Area;
                }
                else if (space.Area - space.AreaCounted > 1e4)
                {
                    parts.Add(new AreaPart(AreaPartKind.Excluded, $"{room.Name}: under 5 ft of headroom", space.Area - space.AreaCounted, null, RoomHeadroom.Contour(document, room, FinishedLeast)));
                    area -= space.Area - space.AreaCounted;
                }
            }
        }
        else
        {
            // Floor under 1.5 m of headroom is in a gross area, and said beside it (GEA 1.12, GIA 2.14).
            foreach (var (room, _) in rooms)
                if (RoomHeadroom.Measure(document, room) is { UnderRoof: true } space && space.Area - space.AreaCounted > 1e4)
                    parts.Add(new AreaPart(AreaPartKind.Stated, $"{room.Name}: under 1.5 m of headroom{(scheme == AreaScheme.GrossInternal ? " (GIA 2.14)" : scheme == AreaScheme.GrossExternal && !document.ProjectInformation.ClearanceStandard.IsUs() ? " (GEA 1.12)" : string.Empty)}",
                        space.Area - space.AreaCounted, null, RoomHeadroom.Contour(document, room, CountedHeadroom)));
        }

        return new AreaMeasure(scheme, level.Id, outlines, parts, Math.Max(0, area), BelowGrade(document, level, shape.Buildings.Select(building => building.Gross).ToList()), Note(outlines, document, level.Id));
    }

    /// <summary>What to say of a storey with walls on it and nothing measured: they close round nothing yet.</summary>
    private static string? Note(IReadOnlyList<IReadOnlyList<Point2D>> outlines, BimDocument document, Guid levelId) =>
        outlines.Count == 0 && document.Walls.Any(wall => wall.LevelId == levelId)
            ? "Its walls close round nothing yet: an area is measured round walls that meet end to end."
            : null;

    /// <summary>A point inside an outline: its centre, or where that falls outside it, a point just in from its first side.</summary>
    private static Point2D Inside(IReadOnlyList<Point2D> polygon)
    {
        var centre = Polygon2D.Centroid(polygon, polygon[0]);
        if (Polygon2D.Contains(polygon, centre)) return centre;
        for (var i = 0; i < polygon.Count; i++)
        {
            var (a, b) = (polygon[i], polygon[(i + 1) % polygon.Count]);
            var along = (b - a).NormalisedOrDefault(Vector2D.UnitX);
            foreach (var side in new[] { 1.0, -1.0 })
            {
                var probe = a.MidpointTo(b) + along.PerpendicularLeft() * (10 * side);
                if (Polygon2D.Contains(polygon, probe)) return probe;
            }
        }

        return centre;
    }

    /// <summary>The storey's rooms inside what is measured, with their outlines.</summary>
    private static List<(Room Room, IReadOnlyList<Point2D> Polygon)> Rooms(BimDocument document, Guid levelId, IReadOnlyList<IReadOnlyList<Point2D>> outlines) =>
        document.All<Room>().Where(room => room.LevelId == levelId)
            .Select(room => (Room: room, Boundary: room.GetBoundary(document)))
            .Where(entry => entry.Boundary.IsEnclosed && outlines.Any(outline => Polygon2D.Contains(outline, entry.Room.Location)))
            .Select(entry => (entry.Room, entry.Boundary.Polygon))
            .ToList();

    /// <summary>
    /// The holes in a storey's floors that no stair comes up through: voids over the storey below.
    /// A stairwell is floor (GIA 2.12) - and in the US the stair is counted on the level it comes down from.
    /// </summary>
    private static List<IReadOnlyList<Point2D>> Voids(BimDocument document, Guid levelId)
    {
        var stairs = document.All<Stair>().SelectMany(stair => Stairs.Flights(document, stair))
            .Where(flight => flight.TopLevelId == levelId)
            .Select(flight => Stairs.Footprint(flight.Layout))
            .ToList();
        return document.All<Floor>().Where(floor => floor.LevelId == levelId)
            .SelectMany(floor => floor.Holes)
            .Where(hole => hole.Count >= 3 && !stairs.Any(footprint => footprint.Any(point => Polygon2D.Contains(hole, point)) || hole.Any(point => Polygon2D.Contains(footprint, point))))
            .ToList();
    }

    /// <summary>Whether a storey is below grade, as ANSI Z765 has it: any of it below the ground round it. With no ground drawn, a storey below the project's zero.</summary>
    private static bool BelowGrade(BimDocument document, Level level, IReadOnlyList<IReadOnlyList<Point2D>> outsides)
    {
        var ground = outsides.SelectMany(outline => outline).Select(point => Terrains.GroundAt(document, point)).OfType<double>().ToList();
        return ground.Count > 0 ? ground.Max() > level.Elevation + 1 : level.Elevation < -1;
    }

    // ---- the storey's shape ---------------------------------------------------------------

    /// <summary>One outline of a storey two ways: to the outside faces of the walls round it and a party wall's middle (gross external), and to their inside faces (gross internal).</summary>
    private sealed record Outline(IReadOnlyList<Point2D> Gross, IReadOnlyList<Point2D> Inner);

    /// <summary>
    /// A storey's shape as its walls make it: each building on it, round the outside of the walls
    /// that close round it - whatever they are typed as - two ways; the courtyards its outside walls
    /// close round, open to the sky; a home's garden stores and greenhouses standing apart; and
    /// those built onto it, by their rooms.
    /// </summary>
    private sealed record StoreyShape(List<Outline> Buildings, List<Outline> DetachedStores, List<Outline> Courts, List<(Room Room, IReadOnlyList<Point2D> Polygon)> StoreRooms);

    private static StoreyShape Shape(BimDocument document, Level level) =>
        ModelCache.Of(document) is { } cache
            ? cache.Keep("storey shape", document, level.Id, null, () => Shaped(document, level))
            : Shaped(document, level);

    /// <summary>The walls a storey's outline is traced round: any but a retaining wall, a foundation wall or a soffit, that bounds rooms.</summary>
    private static bool Enclosing(BimDocument document, Wall wall) =>
        wall.RoomBounding && document.GetWallType(wall) is not { Function: WallFunction.Retaining or WallFunction.Foundation or WallFunction.Soffit };

    private static StoreyShape Shaped(BimDocument document, Level level)
    {
        var walls = document.Walls.Where(wall => wall.LevelId == level.Id && wall.Length >= 1 && Enclosing(document, wall)).ToList();
        var rooms = document.All<Room>().Where(room => room.LevelId == level.Id).ToList();
        var shape = new StoreyShape(new(), new(), new(), new());
        if (walls.Count == 0) return shape;

        bool Bounding(Wall wall) => Enclosing(document, wall);

        // Round the outside of each building: from over the top of the storey looking down at each
        // wall, and from beside each - a seed already inside a building, or by a wall already walked
        // round, is not tried again.
        var top = walls.SelectMany(wall => wall.LocationCurve.Points()).Max(point => point.Y) + 1000;
        var walked = new HashSet<Wall>();
        var buildings = new List<Outline>();
        foreach (var wall in walls)
        {
            var middle = wall.Length / 2;
            var at = wall.LocationCurve.PointAt(middle);
            var across = wall.ExteriorNormalAt(middle) * (Half(document, wall) + 20);
            foreach (var seed in new[] { new Point2D(at.X + 0.37, top), at + across, at - across })
            {
                if (walked.Contains(wall) || buildings.Any(known => Polygon2D.Contains(known.Gross, seed))) break;
                if (RoomBoundary.Loop(document, level.Id, seed, Bounding, each => ToOutside(document, each)) is not { Outside: true } outer) continue;
                var gross = outer.Polygon.Reverse().ToList();
                if (Polygon2D.Area(gross) < 1e4 || buildings.Any(known => Same(known.Gross, gross))) continue;
                if (RoomBoundary.Loop(document, level.Id, seed, Bounding, each => -Half(document, each)) is not { Outside: true } inner) continue;
                walked.UnionWith(outer.Walls);
                buildings.Add(new Outline(gross, inner.Polygon.Reverse().ToList()));
            }
        }

        // A courtyard: a space the outside walls close round, from their outside, with no room or
        // floor in it - where the storey has either; with neither drawn yet, none is told apart.
        var floors = document.All<Floor>().Where(floor => floor.LevelId == level.Id && floor.Boundary.Count >= 3).ToList();
        if (rooms.Count + floors.Count > 0)
        {
            var seen = new List<IReadOnlyList<Point2D>>();
            foreach (var wall in walls.Where(wall => Perimeter(document, wall)))
            {
                var middle = wall.Length / 2;
                var seed = wall.LocationCurve.PointAt(middle) + wall.ExteriorNormalAt(middle) * (Half(document, wall) + 20);
                if (!buildings.Any(known => Polygon2D.Contains(known.Inner, seed)) || seen.Any(known => Polygon2D.Contains(known, seed))) continue;
                if (RoomBoundary.Loop(document, level.Id, seed, each => Perimeter(document, each), each => Half(document, each)) is not { Outside: false } court) continue;
                seen.Add(court.Polygon);
                if (rooms.Any(room => Polygon2D.Contains(court.Polygon, room.Location)) || floors.Any(floor => Polygon2D.Contains(floor.Boundary, seed))) continue;
                if (RoomBoundary.Loop(document, level.Id, seed, each => Perimeter(document, each), each => -Half(document, each)) is not { Outside: false } far) continue;
                shape.Courts.Add(new Outline(court.Polygon, far.Polygon));
            }
        }

        // A home's garden stores, greenhouses and fuel stores: standing apart where every room in a building is one; built on, by their rooms.
        foreach (var building in buildings)
        {
            var within = rooms.Where(room => Polygon2D.Contains(building.Inner, room.Location)).ToList();
            if (within.Count > 0 && within.All(room => Stores.IsMatch(Called(room)))) shape.DetachedStores.Add(building);
            else
            {
                shape.Buildings.Add(building);
                shape.StoreRooms.AddRange(within.Where(room => Stores.IsMatch(Called(room)))
                    .Select(room => (Room: room, Boundary: room.GetBoundary(document)))
                    .Where(entry => entry.Boundary.IsEnclosed)
                    .Select(entry => (entry.Room, entry.Boundary.Polygon)));
            }
        }

        return shape;
    }

    private static bool Same(IReadOnlyList<Point2D> a, IReadOnlyList<Point2D> b) =>
        Math.Abs(Polygon2D.Area(a) - Polygon2D.Area(b)) < 1e3 && Polygon2D.Centroid(a, a[0]).DistanceTo(Polygon2D.Centroid(b, b[0])) < 1;

    // ---- net internal area ----------------------------------------------------------------

    /// <summary>
    /// A storey's net internal area, by its rooms inside its outside walls: each room that counts at
    /// 1.5 m of headroom and over, less the stairs in it, with the non-structural partitions between
    /// two that count (NIA 3.9) - the toilets and bathrooms, plant, stairwells, lifts and common
    /// parts, and parking left out, and the walls round them and every structural wall with them.
    /// </summary>
    private static AreaMeasure Net(BimDocument document, Level level)
    {
        var shape = Shape(document, level);
        var parts = new List<AreaPart>();
        var counted = new List<(Room Room, IReadOnlyList<Point2D> Polygon)>();
        double area = 0;

        foreach (var (room, polygon) in Rooms(document, level.Id, shape.Buildings.Select(building => building.Inner).ToList()))
        {
            if (NetExclusion(room) is { } why)
            {
                parts.Add(new AreaPart(AreaPartKind.Excluded, $"{room.Name}: {why}", room.GetBoundary(document).Area, polygon, Array.Empty<(Point2D, Point2D)>()));
                continue;
            }

            var space = RoomHeadroom.Measure(document, room);
            var full = room.GetBoundary(document).Area;
            var counts = space is { UnderRoof: true } ? space.AreaCounted : full;
            if (full - counts > 1e4)
                parts.Add(new AreaPart(AreaPartKind.Excluded, $"{room.Name}: under 1.5 m of headroom (NIA 3.20)", full - counts, null, RoomHeadroom.Contour(document, room, CountedHeadroom)));
            counted.Add((room, polygon));
            area += counts;
        }

        // The stairs in the rooms that count: a stairwell is left out (NIA 3.15).
        var flights = document.All<Stair>().SelectMany(stair => Stairs.Flights(document, stair))
            .Where(flight => flight.LevelId == level.Id || flight.TopLevelId == level.Id)
            .Select(flight => Stairs.Footprint(flight.Layout))
            .Where(footprint => footprint.Count >= 3)
            .ToList();
        foreach (var footprint in flights)
        {
            var within = counted.SelectMany(entry => PolygonBoolean.Combine(entry.Polygon, footprint, BooleanOperation.Intersection))
                .Sum(region => Polygon2D.Area(region.Outer) - region.Holes.Sum(Polygon2D.Area));
            if (within <= 1e4) continue;
            parts.Add(new AreaPart(AreaPartKind.Excluded, "a stairwell (NIA 3.15)", within, footprint, Array.Empty<(Point2D, Point2D)>()));
            area -= within;
        }

        var partitions = Partitions(document, level.Id, counted.Select(entry => entry.Polygon).ToList());
        if (partitions > 1e4)
        {
            parts.Add(new AreaPart(AreaPartKind.Added, "non-structural partitions between rooms that count (NIA 3.9)", partitions, null, Array.Empty<(Point2D, Point2D)>()));
            area += partitions;
        }

        var note = shape.Buildings.Count == 0 ? Note(Array.Empty<IReadOnlyList<Point2D>>(), document, level.Id)
            : counted.Count == 0 && parts.Count == 0 ? "No rooms in it: a net internal area is measured by the rooms."
            : null;
        return new AreaMeasure(AreaScheme.NetInternal, level.Id, counted.Select(entry => entry.Polygon).ToList(), parts, Math.Max(0, area), false, note);
    }

    /// <summary>
    /// The floor under the non-structural partitions with a room that counts on both sides, mm²:
    /// each such wall's thickness along as much of it as has. A bearing or shear wall, an outside
    /// or party wall, is structure, and stays out (NIA 3.18).
    /// </summary>
    private static double Partitions(BimDocument document, Guid levelId, IReadOnlyList<IReadOnlyList<Point2D>> rooms)
    {
        double area = 0;
        foreach (var wall in document.Walls.Where(wall => wall.LevelId == levelId && wall.Length >= 1 && wall.StructuralUsage == StructuralUsage.NonBearing))
        {
            if (document.GetWallType(wall) is not { Function: WallFunction.Interior, Separating: false } type) continue;
            bool In(Point2D point) => rooms.Any(polygon => Polygon2D.Contains(polygon, point));

            var steps = Math.Max(4, (int)(wall.Length / 100));
            var between = 0;
            for (var i = 0; i < steps; i++)
            {
                var along = wall.Length * (i + 0.5) / steps;
                var at = wall.LocationCurve.PointAt(along);
                var across = wall.ExteriorNormalAt(along) * (type.Width / 2 + 100);
                if (In(at + across) && In(at - across)) between++;
            }

            area += wall.Length * between / steps * type.Width;
        }

        return area;
    }

    // ---- the schedule ---------------------------------------------------------------------

    /// <summary>A scheme's area schedule, as a viewport refers to it.</summary>
    public static ViewReference Schedule(AreaScheme scheme) => new(ViewKind.AreaSchedule, Guid.Empty, string.Empty) { Index = (int)scheme };

    /// <summary>The scheme a schedule is of.</summary>
    public static AreaScheme SchemeOfSchedule(ViewReference schedule) =>
        Enum.IsDefined(typeof(AreaScheme), schedule.Index) ? (AreaScheme)schedule.Index : AreaScheme.GrossInternal;

    public static string ScheduleTitle(BimDocument document, ViewReference schedule) => $"Area Schedule - {Title(document, SchemeOfSchedule(schedule))}";

    /// <summary>Every storey measured in a scheme, lowest first: those with anything measured on them.</summary>
    public static IReadOnlyList<(Level Level, AreaMeasure Measure)> Storeys(BimDocument document, AreaScheme scheme) =>
        document.Levels.OrderBy(level => level.Elevation)
            .Select(level => (Level: level, Measure: Measure(document, level.Id, scheme)))
            .Where(entry => entry.Measure.Outlines.Count > 0)
            .ToList();

    /// <summary>What a storey's measure leaves out, puts in or says beside it, in short.</summary>
    public static string Notes(AreaMeasure measure) => string.Join("; ", measure.Parts
        .GroupBy(part => (part.Kind, Clause: Clause(part.Said)))
        .Select(group => $"{group.Key.Kind switch { AreaPartKind.Excluded => "less", AreaPartKind.Added => "with", _ => "of which" }} {group.Key.Clause}: {Said(measure.Scheme, group.Sum(part => part.Area))}"));

    /// <summary>A part's reason without the room it is in: "toilets and bathrooms (NIA 3.13)".</summary>
    private static string Clause(string said) => said.Contains(": ") ? said[(said.IndexOf(": ", StringComparison.Ordinal) + 2)..] : said;

    /// <summary>
    /// A scheme's schedule as a table on a sheet: each storey, its area and what is left out of it,
    /// then the total - a US home's finished area above grade and below it apart - and the code it is measured by.
    /// </summary>
    public static ScheduleTable Table(BimDocument document, ViewReference schedule)
    {
        var scheme = SchemeOfSchedule(schedule);
        var storeys = Storeys(document, scheme);
        var rows = new List<(ScheduleLineKind Kind, IReadOnlyList<string> Cells)>();
        void Total(string name, IEnumerable<AreaMeasure> measures, ScheduleLineKind kind) =>
            rows.Add((kind, new[] { name, Said(scheme, measures.Sum(measure => measure.Area)), string.Empty }));

        if (scheme == AreaScheme.Finished)
        {
            foreach (var (heading, below) in new[] { ("Above grade", false), ("Below grade", true) })
            {
                var these = storeys.Where(entry => entry.Measure.BelowGrade == below).ToList();
                if (these.Count == 0) continue;
                rows.Add((ScheduleLineKind.GroupHeading, new[] { heading }));
                rows.AddRange(these.Select(entry => (ScheduleLineKind.Row, (IReadOnlyList<string>)new[] { entry.Level.Name, Said(scheme, entry.Measure.Area), Notes(entry.Measure) })));
                Total($"{heading}, finished", these.Select(entry => entry.Measure), ScheduleLineKind.Subtotal);
            }
        }
        else rows.AddRange(storeys.Select(entry => (ScheduleLineKind.Row, (IReadOnlyList<string>)new[] { entry.Level.Name, Said(scheme, entry.Measure.Area), Notes(entry.Measure) })));

        Total("Total", storeys.Select(entry => entry.Measure), ScheduleLineKind.Total);
        rows.Add((ScheduleLineKind.GroupHeading, new[] { Code(scheme) }));

        var headings = new[] { "Storey", "Area", "Left out and stated" };
        var table = ScheduleTable.Of(ScheduleTitle(document, schedule), headings, rows.Where(row => row.Kind != ScheduleLineKind.GroupHeading).Select(row => row.Cells));
        var none = headings.Select(_ => (ColourRgb?)null).ToList();
        var lines = new List<ScheduleTableLine> { table.Lines[0], table.Lines[1] };
        lines.AddRange(rows.Select(row => new ScheduleTableLine(row.Kind, row.Cells, none, ScheduleTable.TextSize * (row.Kind == ScheduleLineKind.GroupHeading ? 2.2 : 2))));

        // Wide enough for the code it is measured by, across it.
        var widths = table.ColumnWidths.ToList();
        var code = ScheduleTable.TextWidth(Code(scheme), ScheduleTable.TextSize) + ScheduleTable.Padding * 2;
        if (widths.Sum() < code) widths[^1] += code - widths.Sum();
        return new ScheduleTable { Title = table.Title, ColumnWidths = widths, Numeric = new[] { false, true, false }, Lines = lines };
    }
}
