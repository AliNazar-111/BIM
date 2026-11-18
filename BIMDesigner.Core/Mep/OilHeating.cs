using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Mep;

/// <summary>
/// Heating with oil, as Approved Document J (2010, with the 2010 amendments) has it - in England, not under the US's codes:
///
/// An oil boiler (Section 4): open-flued, not in a bathroom, a shower room or a bedroom (4.2); its air through permanent vents to outside,
/// 550 mm² for each kW of its output (Diagram 40) - room-sealed, none; its room's extract 40 l/s at most, its burner a pressure jet (1.20 b).
///
/// An oil tank outside, 3500 litres or less (Section 5): 1.8 m at least from the building, else the building's walls within 1.8 m of it
/// imperforate and resisting fire 30 minutes - or a fire wall between, 300 mm higher and wider than the tank (Table 10); 760 mm at least from
/// the boundary, else a fire wall between or a boundary wall resisting fire 30 minutes, 300 mm higher and wider than it; on a hard base -
/// concrete or paving slabs - 300 mm wider than it all round (5.4); and bunded, with its own secondary containment, where it holds more than
/// 2500 litres (5.8 a, 5.10). Over 3500 litres, Approved Document J's guidance ends (5.2, 5.3).
/// </summary>
public static class OilHeating
{
    /// <summary>Diagram 40: permanent vent to outside for an open-flued oil appliance in a room, mm² for each kW of its output.</summary>
    public const double VentPerKw = 550;

    /// <summary>1.20 b: the fastest a room's extract may draw with an open-flued oil appliance in it - a pressure jet burner's, l/s.</summary>
    public const double ExtractLimit = 40;

    /// <summary>Table 10: a tank this near the building or the boundary wants protecting, mm.</summary>
    public const double FromBuilding = 1800, FromBoundary = 760;

    /// <summary>5.4's hard base, and Table 10's fire wall, so much past the tank, mm.</summary>
    public const double Margin = 300;

    /// <summary>5.8 a: a tank holding more than this wants secondary containment; 5.2: past this, the guidance ends, litres.</summary>
    public const double BundOver = 2500, MostHeld = 3500;

    /// <summary>What is wanting in a building's oil heating.</summary>
    public static IEnumerable<MepProblem> Problems(BimDocument document)
    {
        if (document.ProjectInformation.ClearanceStandard.IsUs()) yield break;
        var components = document.All<Component>().ToList();
        foreach (var piece in components)
        {
            if (document.FindType<ComponentType>(piece.TypeId) is not { } type) continue;
            if (type.BurnsOil)
                foreach (var problem in Appliance(document, piece, type, components)) yield return problem;
            else if (type.Form == ComponentForm.OilTank)
                foreach (var problem in Tank(document, piece, type)) yield return problem;
        }
    }

    /// <summary>An oil boiler: open-flued, not in a bathroom or bedroom (4.2), and its air from outside (Diagram 40).</summary>
    private static IEnumerable<MepProblem> Appliance(BimDocument document, Component boiler, ComponentType type, List<Component> components)
    {
        if (type.FlueOf != ApplianceFlue.OpenFlued) yield break;
        var room = RoomsOf.Of(document, boiler);
        if (room is not null)
        {
            var outline = room.GetBoundary(document).Polygon;
            var wet = outline.Count >= 3 && Lighting.KindOf(document, room, outline) == LitRoom.Bathroom
                      || new[] { "bath", "shower", "wc", "toilet" }.Any(word => room.Name.Contains(word, StringComparison.OrdinalIgnoreCase));
            var bedroom = room.Name.Contains("bedroom", StringComparison.OrdinalIgnoreCase)
                          || components.Any(each => ReferenceEquals(RoomsOf.Of(document, each), room) && document.FindType<ComponentType>(each.TypeId)?.Form is ComponentForm.Bed or ComponentForm.BunkBed);
            if (wet || bedroom)
                yield return new MepProblem(boiler, $"open-flued, in {RoomsOf.Label(room)}, a {(wet ? "bathroom" : "bedroom")} - Approved Document J (4.2) keeps an open-flued oil appliance out of bathrooms, shower rooms and bedrooms: a room-sealed one there");
        }

        // Its room's extract no faster than 40 l/s - a boiler's burner taken as a pressure jet - or it draws the flue's gases back in (1.20 b).
        if (room is not null)
        {
            var fastest = components.Where(each => ReferenceEquals(RoomsOf.Of(document, each), room)).Select(each => document.FindType<ComponentType>(each.TypeId))
                .Where(each => each is { Form: ComponentForm.ExtractFan or ComponentForm.CookerHood }).Select(each => each!.ExtractRate).DefaultIfEmpty(0).Max();
            if (fastest > ExtractLimit + 1e-6)
                yield return new MepProblem(boiler, $"open-flued in {RoomsOf.Label(room)}, whose extract draws {fastest:0} l/s - Approved Document J (1.20 b) keeps a room's extract to 40 l/s where an open-flued oil appliance with a pressure jet burner is in it, 20 l/s with a vaporising one: a slower fan, or a room-sealed appliance; and a spillage test with the fans running (1.21)");
        }

        var wanted = type.HeatOutput / 1000 * VentPerKw;
        var vents = room is null ? 0 : components.Where(each => document.FindType<ComponentType>(each.TypeId) is { Form: ComponentForm.BackgroundVentilator } && ReferenceEquals(RoomsOf.Of(document, each), room))
            .Sum(each => document.FindType<ComponentType>(each.TypeId)!.VentArea);
        if (wanted > 0 && vents < wanted - 0.5)
            yield return new MepProblem(boiler,
                $"open-flued at {type.HeatOutput / 1000:0.0} kW, it wants {wanted:#,0} mm² of permanent vent to outside in {(room is null ? "the room it is in" : RoomsOf.Label(room))} - 550 mm² a kW of its output (Approved Document J, Diagram 40) - and has {vents:#,0}; a window's trickle vent closes and does not count");
    }

    /// <summary>A tank's outline in plan: its width across it, its depth along the way it faces.</summary>
    public static IReadOnlyList<Point2D> Footprint(Component tank, ComponentType type)
    {
        var (across, facing) = (tank.Across * (type.Width / 2), tank.Facing * (type.Depth / 2));
        return new[] { tank.Location - across - facing, tank.Location + across - facing, tank.Location + across + facing, tank.Location - across + facing };
    }

    /// <summary>An oil tank: inside or outside, kept from the building and the boundary (Table 10), on its hard base (5.4), bunded (5.8 a), and not past 3500 litres (5.2).</summary>
    private static IEnumerable<MepProblem> Tank(BimDocument document, Component tank, ComponentType type)
    {
        var footprint = Footprint(tank, type);
        var top = tank.Elevation + type.Height;
        if (RoomsOf.Of(document, tank) is { } room)
        {
            yield return new MepProblem(tank, $"it is inside the building, in {RoomsOf.Label(room)} - Approved Document J (Table 10) asks a tank indoors to be in a place of special fire hazard, vented straight to outside, as Approved Document B has one");
            yield break;
        }

        // The building: its walls - some room's outline runs along them - and the doors and windows in them.
        var walls = document.Walls.Where(wall => wall.LevelId == tank.LevelId && document.GetWallType(wall) is { } wallType && wallType.Structure.TotalWidth > 0 && Combustibility.BoundsARoom(document, wall)).ToList();
        double Face(Wall wall) => Distance(footprint, new[] { wall.Start, wall.End }, open: true) - document.GetWallType(wall)!.Structure.TotalWidth / 2;
        var near = walls.Select(wall => (Wall: wall, Gap: Face(wall))).Where(entry => entry.Gap < FromBuilding - 0.5).OrderBy(entry => entry.Gap).ToList();
        var screened = near.Where(entry => Shielded(document, tank, type, footprint, Line2D.ClosestPointOnSegment(tank.Location, entry.Wall.Start, entry.Wall.End), top)).Select(entry => entry.Wall).ToHashSet();
        var bare = near.Where(entry => !screened.Contains(entry.Wall)).ToList();
        if (bare.Count > 0)
        {
            var wanting = new List<string>();
            foreach (var (wall, _) in bare)
            {
                var wallType = document.GetWallType(wall)!;
                if (Doors.FireMinutes(wallType.FireRating) < 30)
                    wanting.Add($"its {wallType.Name} {(Doors.FireMinutes(wallType.FireRating) is > 0 and var minutes ? $"rated {minutes} minutes" : "not fire-rated")}");
                foreach (var opening in Openings(document, wall))
                    if (Distance(footprint, opening.Span, open: true) - wallType.Structure.TotalWidth / 2 is var gap && gap < FromBuilding - 0.5)
                        wanting.Add($"a {opening.Kind} {Units.Metres(gap, "0.0")} from it");
            }

            if (wanting.Count > 0)
                yield return new MepProblem(tank,
                    $"it stands {Units.Metres(Math.Max(0, bare[0].Gap), "0.0")} from the building - Approved Document J (Table 10) asks the walls within 1.8 m of a tank imperforate and resisting fire 30 minutes, and the eaves over them, or a fire wall between 300 mm higher and wider than it; here {string.Join(", ", wanting.Distinct())}");
        }

        // The boundary: 760 mm, else a fire wall or a boundary wall of 30 minutes between, 300 mm higher and wider than the tank.
        var lines = document.All<PropertyLine>().Where(line => line.Boundary.Count >= 2).ToList();
        if (lines.Count > 0)
        {
            var (gap, at) = lines.SelectMany(line => line.Boundary.Select((point, i) => (point, next: line.Boundary[(i + 1) % line.Boundary.Count])))
                .Select(edge => (Gap: Distance(footprint, new[] { edge.point, edge.next }, open: true), At: Line2D.ClosestPointOnSegment(tank.Location, edge.point, edge.next)))
                .MinBy(entry => entry.Gap);
            if (gap < FromBoundary - 0.5 && !Shielded(document, tank, type, footprint, at, top, rated: true))
                yield return new MepProblem(tank, $"it stands {Units.Metres(gap, "0.00")} from the boundary - Approved Document J (Table 10) asks 760 mm at least, or a fire wall or a boundary wall resisting fire 30 minutes between, 300 mm higher and wider than the tank");
        }

        // Its hard base: concrete or paving slabs 300 mm wider than it all round (5.4).
        var spread = Spread(tank, type, Margin);
        var paved = document.All<GroundRegion>().Where(region => region.Surface is GroundSurface.Paving or GroundSurface.Path or GroundSurface.Driveway && region.Boundary.Count >= 3)
                        .Any(region => spread.All(corner => Polygon2D.Contains(region.Boundary, corner)))
                    || document.All<Floor>().Where(floor => floor.LevelId == tank.LevelId && floor.Boundary.Count >= 3).Any(floor => spread.All(floor.Contains))
                    || document.All<SitePad>().Any(pad => pad.Boundary.Count >= 3 && spread.All(corner => Polygon2D.Contains(pad.Boundary, corner)));
        if (!paved)
            yield return new MepProblem(tank, "no hard base under it reaching 300 mm past it all round - Approved Document J (5.4) asks concrete, or paving slabs 42 mm thick at least, 300 mm wider than the tank: draw a region of the ground laid as paving under it");

        // Bunded where it holds more than 2500 litres (5.8 a); and AD J's guidance only to 3500 litres (5.2).
        if (type.Capacity > BundOver + 1e-6 && !type.Bunded)
            yield return new MepProblem(tank, $"it holds {type.Capacity:#,0} litres and is not bunded - Approved Document J (5.8 a, 5.10) asks secondary containment, a bunded tank or a bund holding 110% of it, where a tank holds more than 2500 litres");
        if (type.Capacity > MostHeld + 1e-6)
            yield return new MepProblem(tank, $"it holds {type.Capacity:#,0} litres - Approved Document J's guidance (5.2, 5.3) goes to 3500 litres; past that, the fire and rescue authority's advice, and the Control of Pollution (Oil Storage) Regulations where it serves other than homes");
    }

    /// <summary>The tank's outline grown by so much all round.</summary>
    private static IReadOnlyList<Point2D> Spread(Component tank, ComponentType type, double by)
    {
        var (across, facing) = (tank.Across * (type.Width / 2 + by), tank.Facing * (type.Depth / 2 + by));
        return new[] { tank.Location - across - facing, tank.Location + across - facing, tank.Location + across + facing, tank.Location - across + facing };
    }

    /// <summary>
    /// Whether a fire wall stands between a tank and a point - the building or the boundary: a wall, not one of the building's, across the line
    /// from the tank to it, as high as the tank's top and 300 mm more, and reaching 300 mm past the tank either side; where it is to be rated,
    /// one resisting fire 30 minutes or of masonry or concrete.
    /// </summary>
    private static bool Shielded(BimDocument document, Component tank, ComponentType type, IReadOnlyList<Point2D> footprint, Point2D toward, double top, bool rated = false)
    {
        foreach (var wall in document.Walls.Where(wall => wall.LevelId == tank.LevelId && wall.Length > 1 && !Combustibility.BoundsARoom(document, wall)))
        {
            if (!Crosses(tank.Location, toward, wall.Start, wall.End)) continue;
            if (wall.GetTopElevation(document) - (document.FindLevel(tank.LevelId)?.Elevation ?? 0) < top + Margin - 0.5) continue;
            var along = (wall.End - wall.Start).NormalisedOrDefault(Vector2D.UnitX);
            var spans = footprint.Select(corner => (corner - wall.Start).Dot(along)).ToList();
            if (spans.Min() - Margin < -0.5 || spans.Max() + Margin > wall.Length + 0.5) continue;
            if (rated && document.GetWallType(wall) is { } wallType && Doors.FireMinutes(wallType.FireRating) < 30
                && !wallType.Structure.Layers.Any(layer => document.FindMaterial(layer.MaterialId)?.Name is { } name
                                                           && new[] { "brick", "block", "concrete", "stone" }.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase))))
                continue;
            return true;
        }

        return false;
    }

    /// <summary>The doors and windows in a wall, each as its span along the wall's line.</summary>
    private static IEnumerable<(string Kind, Point2D[] Span)> Openings(BimDocument document, Wall wall)
    {
        var along = (wall.End - wall.Start).NormalisedOrDefault(Vector2D.UnitX);
        foreach (var door in document.All<Door>().Where(door => door.HostWallId == wall.Id))
            if (document.FindType<DoorType>(door.TypeId) is { } type && door.GetSpan(type) is var (from, to))
                yield return ("door", new[] { wall.Start + along * from, wall.Start + along * to });
        foreach (var window in document.All<Window>().Where(window => window.HostWallId == wall.Id))
            if (document.FindType<WindowType>(window.TypeId) is { } type && window.GetSpan(type) is var (from, to))
                yield return ("window", new[] { wall.Start + along * from, wall.Start + along * to });
    }

    /// <summary>The least distance in plan from an outline to a line - a segment, open at its ends.</summary>
    private static double Distance(IReadOnlyList<Point2D> outline, IReadOnlyList<Point2D> line, bool open)
    {
        var least = double.PositiveInfinity;
        for (var i = 0; i < outline.Count; i++)
        {
            var (a, b) = (outline[i], outline[(i + 1) % outline.Count]);
            for (var j = 0; j + 1 < line.Count; j++)
            {
                var (c, d) = (line[j], line[j + 1]);
                if (Crosses(a, b, c, d)) return 0;
                least = Math.Min(least, new[] { Line2D.DistanceFromSegment(a, c, d), Line2D.DistanceFromSegment(b, c, d), Line2D.DistanceFromSegment(c, a, b), Line2D.DistanceFromSegment(d, a, b) }.Min());
            }
        }

        return least;
    }

    private static bool Crosses(Point2D p, Point2D q, Point2D r, Point2D s)
    {
        static double Cross(Point2D o, Point2D a, Point2D b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
        var (d1, d2, d3, d4) = (Cross(r, s, p), Cross(r, s, q), Cross(p, q, r), Cross(p, q, s));
        return (d1 > 1e-6 && d2 < -1e-6 || d1 < -1e-6 && d2 > 1e-6) && (d3 > 1e-6 && d4 < -1e-6 || d3 < -1e-6 && d4 > 1e-6);
    }
}
