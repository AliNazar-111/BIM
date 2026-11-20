using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// Vehicle access, fire mains and hydrants for a building other than a dwelling (round 115), as Approved Document B Volume 2 sets them out in
/// Sections 15 and 16 - in place of Volume 1's 45 m to every point inside a house.
///
/// A building not fitted with fire mains: vehicle access to the share of its perimeter Table 15.1 asks by its floor area - every storey's but a
/// basement's, measured to the inner face of its enclosing walls (Diagram D3), its gross internal area - and the height of its top storey, for a
/// pump appliance or a high reach one; a small building - 2000 m² at most, its top storey 11 m up at most - to 15% of its perimeter or within 45
/// m of every point of its footprint, whichever is less onerous (15.1, Diagram 15.1). Its perimeter the ground storey's outer face (Diagram 15.1);
/// a stretch of it reached where the drive lies straight out from it, its near edge within 4.9 m - for a pump "adjacent" read so, from Diagram
/// 15.2's turntable ladder - and for a high reach appliance its hardstanding running on to 10 m out at least and 5 m wide, or from within 2 m to
/// 7.5 m out and 5.5 m wide for a hydraulic platform (15.8, Diagram 15.2). A door 750 wide at least in every elevation reached, 60 m apart at
/// most (15.3). The route's turning circle 26 m between kerbs and 4 m clear overhead for a high reach appliance, else a pump's 16.8 m and 3.7
/// m (Table 15.2).
///
/// A building fitted with fire mains: the pump within 18 m of each inlet and in sight of it (15.4); every point within 45 m along the hose from
/// a fire main outlet (16.3), the outlets in a protected stairway or protected lobby (16.4); wet mains, not dry, with a storey more than 50 m
/// up (16.6).
///
/// Hydrants: a building with a compartment of more than 280 m² a hydrant within 100 m - an existing one, its phase Existing - or hydrants of its
/// own within 90 m of an entrance, or of its dry fire mains' inlets, and 90 m apart at most (16.8, 16.9).
///
/// Not done: walls in common with other buildings left out of the perimeter (Diagram 15.1); overhanging storeys; storage buildings' mean roof
/// height (Table 15.1 note 2); wet mains' suction tank inlets (15.5); overhead obstructions over high reach hardstandings (15.9) and their
/// gradient (Diagram 15.2 note 1); hydrants' plates (16.10); alternative water supplies (16.12, 16.13); firefighting shafts (Section 17).
/// </summary>
public static partial class FireAccess
{
    /// <summary>Table 15.2 for a high reach appliance: a turning circle 26 m between kerbs, 4 m clear overhead, mm.</summary>
    public const double HighTurningCircle = 26000, HighClearance = 4000;

    /// <summary>
    /// Diagram 15.2: a turntable ladder's hardstanding, its near edge 4.9 m from the building at most and its further edge 10 m at least; a
    /// hydraulic platform's, 2 m and 7.5 m; their widths 5 m and 5.5 m - and "adjacent" for a pump read as within 4.9 m, mm.
    /// </summary>
    public const double LadderNear = 4900, LadderFar = 10000, LadderWidth = 5000, PlatformNear = 2000, PlatformFar = 7500, PlatformWidth = 5500;

    /// <summary>How far a hydrant may be from an entrance or an inlet, and from the next, mm (16.9); and an existing one from the building (16.8).</summary>
    public const double HydrantReach = 90000, ExistingHydrant = 100000;

    /// <summary>Whether Volume 2's Section 15 reaches the building: one other than a dwelling, no dwelling named in it.</summary>
    public static bool Volume2(BimDocument document) =>
        HorizontalEscape.Applies(document) && !document.All<Room>().Any(room => Dwellings.Of(room).Length > 0);

    /// <summary>
    /// Table 15.1 for a building other than a dwelling: its floor area, every storey's but a basement's, m²; its top storey's height, m; the share
    /// of its perimeter asked - null for a small building, 15.1's - and whether for a high reach appliance.
    /// </summary>
    public static (double Area, double Top, double? Share, bool HighReach) Table151(BimDocument document)
    {
        var ground = Escape.Ground(document);
        var area = document.Levels.Where(level => level.Elevation >= ground - Escape.Basement - 0.5)
            .Sum(level => AreaPlans.Measure(document, level.Id, AreaScheme.GrossInternal).Area) / 1e6;
        var (_, top) = FireDesign.HeightOf(document);
        var high = top > 11 + 1e-6;
        double? share = area <= 2000 + 1e-6 ? high ? 0.15 : null
            : area <= 8000 + 1e-6 ? high ? 0.5 : 0.15
            : area <= 16000 + 1e-6 ? 0.5
            : area <= 24000 + 1e-6 ? 0.75
            : 1.0;
        return (area, top, share, high);
    }

    /// <summary>The ground storey's outline, to the outer face of its walls - the building's footprint (Diagram 15.1).</summary>
    private static IReadOnlyList<IReadOnlyList<Point2D>> Footprint(BimDocument document, HashSet<Guid> groundLevels) =>
        groundLevels.SelectMany(level => AreaPlans.Frame(document, level, AreaScheme.GrossExternal)).Where(outline => outline.Count >= 3).ToList();

    /// <summary>
    /// The building's perimeter, mm, and how much of it the drive reaches: straight out from each 250 of its face, the drive's near edge within
    /// 4.9 m - and for a high reach appliance its hardstanding going on to 10 m out and 5 m wide, or from within 2 m to 7.5 m and 5.5 m wide.
    /// </summary>
    public static (double Perimeter, double Reached) Coverage(IReadOnlyList<IReadOnlyList<Point2D>> footprint, IReadOnlyList<GroundRegion> drives, bool highReach)
    {
        bool In(Point2D point) => drives.Any(drive => Polygon2D.Contains(drive.Boundary, point));
        bool Reaches(Point2D face, Vector2D outward)
        {
            double? near = null, far = null;
            for (var t = 50.0; t <= LadderFar + 2000; t += 100)
            {
                var inside = In(face + outward * t);
                if (near is null && inside) near = t;
                else if (near is not null && !inside)
                {
                    far = t;
                    break;
                }
            }

            if (near is not { } n || n > LadderNear + 50) return false;
            if (!highReach) return true;
            var f = far ?? double.PositiveInfinity;
            return f >= Math.Max(LadderFar, n + LadderWidth) - 50 || n <= PlatformNear + 50 && f >= Math.Max(PlatformFar, n + PlatformWidth) - 50;
        }

        var (perimeter, reached) = (0.0, 0.0);
        foreach (var outline in footprint)
        {
            var ccw = Polygon2D.SignedArea(outline) > 0;
            for (var i = 0; i < outline.Count; i++)
            {
                var (p, q) = (outline[i], outline[(i + 1) % outline.Count]);
                var length = p.DistanceTo(q);
                if (length < 1) continue;
                var along = (q - p) * (1 / length);
                var outward = ccw ? new Vector2D(along.Y, -along.X) : new Vector2D(-along.Y, along.X);
                perimeter += length;
                var count = Math.Max(1, (int)Math.Ceiling(length / 250));
                for (var k = 0; k < count; k++)
                    if (Reaches(p + along * ((k + 0.5) * length / count), outward)) reached += length / count;
            }
        }

        return (perimeter, reached);
    }

    /// <summary>What the drive falls short of in reaching a building not fitted with fire mains: Table 15.1's share of its perimeter, or 15.1's for a small building.</summary>
    private static void VehicleAccess(BimDocument document, List<GroundRegion> drives, HashSet<Guid> groundLevels, List<AccessProblem> problems)
    {
        var footprint = Footprint(document, groundLevels);
        if (footprint.Count == 0) return;
        var (area, top, share, high) = Table151(document);
        var (perimeter, reached) = Coverage(footprint, drives, high);
        if (perimeter < 1) return;
        var part = reached / perimeter;
        var said = $"the drive reaches {part * 100:0}% of the building's perimeter - {Units.Metres(reached, "0.0")} of {Units.Metres(perimeter, "0.0")}";
        if (share is null)
        {
            if (part >= 0.15 - 1e-9) return;
            var points = footprint.SelectMany(outline => outline.Concat(Energy.Daylight.Grid(outline, 1000))).ToList();
            var furthest = points.Count == 0 ? 0 : points.Max(point => Nearest(drives, point));
            if (furthest <= Hose + 0.5) return;
            problems.Add(new AccessProblem(drives[0], $"{said}, within 4.9 m of its face, and the furthest point of its footprint is {Units.Metres(furthest, "0.0")} from it - Approved Document B Volume 2 (15.1, Diagram 15.1) asks a building of {area:0} m², its top storey {top:0.0} m up, access for a pump appliance to 15% of its perimeter or within 45 m of every point of its footprint, whichever is less onerous - or fire mains (16.3)"));
            return;
        }

        if (part >= share.Value - 1e-9) return;
        problems.Add(new AccessProblem(drives[0], high
            ? $"{said}, a hardstanding within 4.9 m of its face running on to 10 m out, or within 2 m to 7.5 m - Approved Document B Volume 2 (15.2, 15.8b, Table 15.1, Diagram 15.2) asks {share.Value * 100:0}% for a building of {area:0} m², its top storey {top:0.0} m up, for a high reach appliance - or fire mains (16.3)"
            : $"{said}, within 4.9 m of its face - Approved Document B Volume 2 (15.2, 15.8a, Table 15.1) asks {share.Value * 100:0}% for a building of {area:0} m², its top storey {top:0.0} m up, a pump appliance's access alongside it - or fire mains (16.3)"));
    }

    /// <summary>A building's fire mains (16.3, 16.6): every point of every room within 45 m along the hose from an outlet; wet mains, not dry, with a storey more than 50 m up.</summary>
    private static void Mains(BimDocument document, IReadOnlyDictionary<Room, IReadOnlyList<Point2D>> rooms, List<Escape.Link> links, List<Escape.Flight> flights,
        List<Component> inlets, List<Component> outlets, List<AccessProblem> problems)
    {
        var (_, top) = FireDesign.HeightOf(document);
        if (top > 50 + 1e-6)
            foreach (var inlet in inlets.Where(inlet => document.FindType<ComponentType>(inlet.TypeId)?.Name.Contains("dry", StringComparison.OrdinalIgnoreCase) == true))
                problems.Add(new AccessProblem(inlet, $"it is a dry fire main's inlet where the top storey is {top:0.0} m up - Approved Document B Volume 2 (16.6) asks wet fire mains in a building with a storey more than 50 m above the fire and rescue service's access"));

        var sources = outlets.Select(outlet => (Outlet: outlet, Room: rooms.FirstOrDefault(entry => entry.Key.LevelId == outlet.LevelId && Polygon2D.Contains(entry.Value, outlet.Location)).Key))
            .Where(entry => entry.Room is not null).Select(entry => (entry.Room!, entry.Outlet.Location, 0.0)).ToList();
        if (sources.Count == 0) return;
        var walk = new Escape.Walk(rooms, links, flights.Where(flight => flight.Foot is not null && flight.Head is not null)
            .Select(flight => (flight.Foot!, flight.FootAt, flight.Head!, flight.HeadAt, flight.Length)));
        var from = walk.From(sources);
        foreach (var (room, outline) in rooms)
        {
            var furthest = Escape.Points(outline).Select(point => from(room, point)).OfType<double>().DefaultIfEmpty(double.NaN).Max();
            if (double.IsNaN(furthest) || furthest <= Hose + 0.5) continue;
            problems.Add(new AccessProblem(room, $"its furthest point is {Units.Metres(furthest, "0.0")} along the hose's route from the nearest fire main outlet - Approved Document B Volume 2 (16.3) asks 45 m at most from a fire main outlet to the furthest point"));
        }
    }

    /// <summary>
    /// Hydrants (16.8, 16.9): a building with a compartment of more than 280 m², a hydrant within 100 m of it - one whose phase is Existing - or
    /// hydrants of its own within 90 m of an entrance, or of its dry fire mains' inlets, and within 90 m of each other.
    /// </summary>
    private static void Hydrants(BimDocument document, List<GroundRegion> drives, HashSet<Guid> groundLevels, List<Component> inlets, List<AccessProblem> problems)
    {
        var minutes = Math.Max(30, FireCompartments.Minutes(document));
        var largest = HorizontalEscape.Storeys(document).Keys.SelectMany(level => FireCompartments.Compartments(document, level, minutes)).Select(compartment => compartment.Area).DefaultIfEmpty(0).Max();
        if (largest <= 280 + 1e-6) return;
        var footprint = Footprint(document, groundLevels);
        if (footprint.Count == 0) return;
        double FromBuilding(Point2D at) => footprint.Min(outline => Polygon2D.Contains(outline, at) ? 0
            : outline.Select((point, i) => Line2D.DistanceFromSegment(at, point, outline[(i + 1) % outline.Count])).Min());

        var hydrants = Of(document, ComponentForm.FireHydrant);
        if (hydrants.Any(hydrant => hydrant.PhaseCreated == DesignPhase.Existing && FromBuilding(hydrant.Location) <= ExistingHydrant + 0.5)) return;

        // Hydrants of its own: within 90 m of an entrance, or of a dry fire main's inlet where it has fire mains (16.9a, b).
        var mains = inlets.Count > 0;
        var entrances = mains
            ? inlets.Select(inlet => inlet.Location).ToList()
            : document.All<Door>().Where(door => document.Find<Wall>(door.HostWallId) is { } wall && groundLevels.Contains(wall.LevelId)
                                                 && document.FindType<DoorType>(door.TypeId) is { } type && Doors.External(document, door, type))
                .Select(door => document.Find<Wall>(door.HostWallId)!.PointAt(document.GetWallType(document.Find<Wall>(door.HostWallId)!)!.Structure, door.DistanceAlongWall, 0)).ToList();
        var own = hydrants.Where(hydrant => hydrant.PhaseCreated != DesignPhase.Existing).ToList();
        var near = own.Where(hydrant => entrances.Any(at => at.DistanceTo(hydrant.Location) <= HydrantReach + 0.5)).ToList();
        var what = mains ? "a dry fire main's inlet" : "an entrance";
        if (near.Count == 0)
        {
            problems.Add(new AccessProblem(drives[0], $"the building has a compartment of {largest:0} m², more than 280 m², and no fire hydrant is drawn within 100 m of it, nor one of its own within 90 m of {what} - Approved Document B Volume 2 (16.8, 16.9) asks hydrants for a building more than 100 m from an existing one: draw the nearest existing hydrant, its phase Existing, or put one in"));
            return;
        }

        foreach (var hydrant in own.Except(near))
            problems.Add(new AccessProblem(hydrant, $"it is {Units.Metres(entrances.Min(at => at.DistanceTo(hydrant.Location)), "0.0")} from {what} - Approved Document B Volume 2 ({(mains ? "16.9a" : "16.9b i")}) asks the building's own hydrants within 90 m of {(mains ? "its dry fire mains' inlets" : "an entrance to it")}"));
        if (!mains && own.Count >= 2)
            foreach (var hydrant in own)
                if (own.Where(other => other != hydrant).Min(other => other.Location.DistanceTo(hydrant.Location)) is var gap && gap > HydrantReach + 0.5)
                    problems.Add(new AccessProblem(hydrant, $"it is {Units.Metres(gap, "0.0")} from the next hydrant - Approved Document B Volume 2 (16.9b ii) asks the building's own hydrants 90 m apart at most"));
    }
}
