using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// Smoke, heat and carbon monoxide alarms, where the rules want them.
///
/// In England, Approved Document B asks for at least a Grade D2, Category LD3 system to BS 5839-6
/// (1.1) - LD2, a heat alarm in the kitchen with it, in a house of three storeys or more (1.7) -
/// and BS 5839-6 sites it: a smoke alarm on every storey, within 3 m of every bedroom's door and
/// 7.5 m of every other room's, 300 from walls and lights on the ceiling, and none in a kitchen or
/// bathroom, where cooking and steam set them off. Approved Document J asks for a carbon monoxide
/// alarm in the room with a stove, an open fire or a boiler, 1 to 3 m from it (2.36, 3.45).
///
/// In the US, the IRC (R314.3): a smoke alarm in every bedroom, one by the bedrooms outside them,
/// and one on every storey; 3 ft (914) at least from the door of a bathroom with a tub or shower,
/// and 6 ft (1828) from a cooking appliance - a photoelectric alarm's, R314.3.1. A carbon monoxide
/// alarm by the bedrooms where there is a fuel-fired appliance, and in a bedroom with one in it (R315).
/// </summary>
public static class Alarms
{
    /// <summary>Whether a piece is one of the alarms.</summary>
    private static bool IsAlarm(ComponentType type) =>
        type.Form is ComponentForm.SmokeAlarm or ComponentForm.HeatAlarm or ComponentForm.CarbonMonoxideAlarm;

    /// <summary>The pieces of one form on a storey, where they are.</summary>
    private static List<(Component Piece, ComponentType Type)> All(BimDocument document, Guid levelId, Func<ComponentType, bool> which) =>
        document.All<Component>()
            .Where(piece => piece.LevelId == levelId)
            .Select(piece => (Piece: piece, Type: document.FindType<ComponentType>(piece.TypeId)))
            .Where(entry => entry.Type is not null && which(entry.Type))
            .Select(entry => (entry.Piece, entry.Type!))
            .ToList();

    /// <summary>
    /// The appliances on a storey that burn a fuel, by name and where they stand: a boiler, and a
    /// chimney's stove or open fire on that storey - in front of its breast.
    /// </summary>
    public static IReadOnlyList<(string Name, Point2D At)> Appliances(BimDocument document, Guid levelId)
    {
        var found = All(document, levelId, type => type.Form == ComponentForm.Boiler)
            .Select(entry => (entry.Type.Name, entry.Piece.Location))
            .ToList();

        foreach (var chimney in document.All<Chimney>())
        {
            var fireplace = chimney.LevelId == levelId ? chimney.Fireplace : chimney.FireplaceOn(levelId);
            if (fireplace == ChimneyFireplace.None) continue;

            var front = new Vector2D(-Math.Sin(chimney.Angle * Math.PI / 180), Math.Cos(chimney.Angle * Math.PI / 180));
            found.Add((fireplace == ChimneyFireplace.Stove ? "the stove" : "the open fire", chimney.Location + front * (chimney.Depth / 2)));
        }

        return found;
    }

    /// <summary>A room's doors, by a point just inside it at each.</summary>
    public static IReadOnlyList<Point2D> Doors(BimDocument document, Room room, IReadOnlyList<Point2D> outline)
    {
        var doors = new List<Point2D>();
        foreach (var door in document.All<Door>())
        {
            if (document.Find<Wall>(door.HostWallId) is not { } wall || wall.LevelId != room.LevelId) continue;
            if (document.GetWallType(wall) is not { } type) continue;

            var reach = type.Structure.TotalWidth / 2 + 150;
            foreach (var side in new[] { reach, -reach })
            {
                var at = wall.PointAt(type.Structure, door.DistanceAlongWall, side);
                if (!Polygon2D.Contains(outline, at)) continue;
                doors.Add(at);
                break;
            }
        }

        return doors;
    }

    /// <summary>What is wrong with where one alarm is, as the project's standard asks.</summary>
    public static IReadOnlyList<PlacementProblem> Problems(BimDocument document, Component piece, ClearanceStandard standard)
    {
        if (document.FindType<ComponentType>(piece.TypeId) is not { } type || !IsAlarm(type)) return Array.Empty<PlacementProblem>();

        var us = standard.IsUs();
        var footprint = ComponentModel.Footprint(type, ComponentModel.FrameOf(piece, 0));
        var problems = new List<PlacementProblem>();
        var room = RoomsOf.Containing(document, piece.LevelId, piece.Location);
        var outline = room?.GetBoundary(document) is { IsEnclosed: true } boundary ? boundary.Polygon : null;
        var inRoom = outline is null
            ? new List<(Component Piece, ComponentType Type)>()
            : All(document, piece.LevelId, _ => true).Where(entry => Polygon2D.Contains(outline, entry.Piece.Location)).ToList();

        // 300 from the walls on the ceiling, and from the lights.
        if (!us)
        {
            var wall = document.Walls
                .Where(other => other.LevelId == piece.LevelId && document.GetWallType(other) is { } t && t.Structure.TotalWidth > 0)
                .Select(other => Line2D.DistanceFromSegment(piece.Location, other.Start, other.End) - document.GetWallType(other)!.Structure.TotalWidth / 2 - type.Width / 2)
                .DefaultIfEmpty(double.MaxValue)
                .Min();
            if (wall < 300 - 0.5)
                problems.Add(new PlacementProblem(piece, $"it is {wall:0} from the wall.",
                    type.Form == ComponentForm.CarbonMonoxideAlarm
                        ? "Approved Document J (2.36, 3.45) puts a carbon monoxide alarm on the ceiling 300 at least from any wall."
                        : "BS 5839-6, which Approved Document B (1.1) follows, puts an alarm on the ceiling 300 at least from the walls and the lights.",
                    footprint));

            var light = All(document, piece.LevelId, other => other.Kind == Elements.BuiltInCategory.LightingFixtures)
                .Select(entry => entry.Piece.Location.DistanceTo(piece.Location) - (entry.Type.Width + type.Width) / 2)
                .DefaultIfEmpty(double.MaxValue)
                .Min();
            if (type.Form != ComponentForm.CarbonMonoxideAlarm && light < 300 - 0.5)
                problems.Add(new PlacementProblem(piece, $"it is {light:0} from a light.",
                    "BS 5839-6, which Approved Document B (1.1) follows, puts an alarm on the ceiling 300 at least from the walls and the lights.", footprint));
        }

        switch (type.Form)
        {
            case ComponentForm.SmokeAlarm when !us && inRoom.Any(entry => entry.Type.Form is ComponentForm.Cooker or ComponentForm.Bath or ComponentForm.Shower):
                problems.Add(new PlacementProblem(piece, "it is in a kitchen or a bathroom, where cooking or steam will set it off.",
                    "BS 5839-6 keeps smoke alarms out of kitchens and bathrooms; a heat alarm goes in a kitchen.", footprint));
                break;

            case ComponentForm.SmokeAlarm when us:
            {
                var cooker = All(document, piece.LevelId, other => other.Form == ComponentForm.Cooker)
                    .Select(entry => Kitchens.Apart(ComponentModel.Footprint(entry.Type, ComponentModel.FrameOf(entry.Piece, 0)), footprint))
                    .DefaultIfEmpty(double.MaxValue)
                    .Min();
                if (cooker < 1828 - 0.5)
                    problems.Add(new PlacementProblem(piece, $"it is {Units.Bare(cooker)} from the cooker.",
                        $"IRC R314.3.1 keeps a smoke alarm 6 ft{Units.MetricAside("1828")} from a cooking appliance - a photoelectric one; an ionization one 20 ft.", footprint));

                var bathroomDoor = document.All<Room>()
                    .Where(other => other.LevelId == piece.LevelId)
                    .Select(other => (Room: other, Boundary: other.GetBoundary(document)))
                    .Where(other => other.Boundary.IsEnclosed && All(document, piece.LevelId, t => t.Form is ComponentForm.Bath or ComponentForm.Shower)
                        .Any(entry => Polygon2D.Contains(other.Boundary.Polygon, entry.Piece.Location)))
                    .SelectMany(other => Doors(document, other.Room, other.Boundary.Polygon))
                    .Select(door => door.DistanceTo(piece.Location))
                    .DefaultIfEmpty(double.MaxValue)
                    .Min();
                if (bathroomDoor < 914 - 0.5)
                    problems.Add(new PlacementProblem(piece, $"it is {Units.Bare(bathroomDoor)} from a bathroom's door.",
                        $"IRC R314.3 keeps a smoke alarm 3 ft{Units.MetricAside("914")} from the door of a bathroom with a tub or shower, out of its steam.", footprint));
                break;
            }

            case ComponentForm.CarbonMonoxideAlarm when !us:
            {
                var near = Appliances(document, piece.LevelId)
                    .Where(appliance => outline is null || Polygon2D.Contains(outline, appliance.At))
                    .Select(appliance => (appliance.Name, Apart: appliance.At.DistanceTo(piece.Location)))
                    .OrderBy(appliance => appliance.Apart)
                    .FirstOrDefault();
                if (near.Name is not null && (near.Apart < 1000 - 0.5 || near.Apart > 3000 + 0.5))
                    problems.Add(new PlacementProblem(piece, $"it is {Units.Metres(near.Apart, "0.0")} from {near.Name}.",
                        "Approved Document J (2.36, 3.45) puts a carbon monoxide alarm 1 to 3 m from the appliance, in its room.", footprint));
                break;
            }
        }

        return problems;
    }

    /// <summary>Every alarm's problems on a storey, as warnings among the clearances.</summary>
    public static IReadOnlyList<BlockedClearance> Blocked(BimDocument document, Guid levelId, ClearanceStandard standard) =>
        document.All<Component>()
            .Where(piece => piece.LevelId == levelId)
            .SelectMany(piece => Problems(document, piece, standard)
                .Select(problem => new BlockedClearance(new ClearanceZone(piece, "its place", problem.Outline, problem.Rule), string.Empty,
                    document.FindType<ComponentType>(piece.TypeId)?.Name ?? "An alarm", problem.Said)))
            .ToList();

    /// <summary>
    /// What a room lacks for its alarms - a smoke alarm on its storey, near its door or in it, a
    /// heat alarm, a carbon monoxide alarm by its stove or boiler - or null where it lacks nothing.
    /// </summary>
    public static string? Verdict(BimDocument document, Room room)
    {
        var boundary = room.GetBoundary(document);
        if (!boundary.IsEnclosed || boundary.Polygon.Count < 3) return null;

        var us = document.ProjectInformation.ClearanceStandard.IsUs();
        var everything = All(document, room.LevelId, _ => true);
        var inRoom = everything.Where(entry => Polygon2D.Contains(boundary.Polygon, entry.Piece.Location)).ToList();
        var smoke = everything.Where(entry => entry.Type.Form == ComponentForm.SmokeAlarm).Select(entry => entry.Piece.Location).ToList();
        bool Has(ComponentForm form) => inRoom.Any(entry => entry.Type.Form == form);

        var bedroom = Has(ComponentForm.Bed) || Has(ComponentForm.BunkBed);
        var wet = Has(ComponentForm.Bath) || Has(ComponentForm.Shower) || inRoom.Any(entry => Bathrooms.IsWc(entry.Type));
        var kitchen = Has(ComponentForm.Cooker);
        var doors = Doors(document, room, boundary.Polygon);
        double NearestSmoke(Point2D at) => smoke.Count == 0 ? double.MaxValue : smoke.Min(alarm => alarm.DistanceTo(at));

        var wanting = new List<string>();
        if (smoke.Count == 0)
            wanting.Add(us
                ? "no smoke alarm on this storey - IRC R314.3 asks for one on every storey"
                : "no smoke alarm on this storey - BS 5839-6, which Approved Document B (1.1) follows, asks for one on every storey");
        else if (us)
        {
            if (bedroom && !Has(ComponentForm.SmokeAlarm)) wanting.Add("no smoke alarm in this bedroom - IRC R314.3 asks for one in every bedroom");

            var outside = smoke.Where(alarm => !Polygon2D.Contains(boundary.Polygon, alarm)).ToList();
            if (bedroom && doors.Count > 0 && doors.Min(door => outside.Count == 0 ? double.MaxValue : outside.Min(alarm => alarm.DistanceTo(door))) > 3000 + 0.5)
                wanting.Add("no smoke alarm by its door outside it - IRC R314.3 asks for one outside every sleeping area, by the bedrooms");
        }
        else if (!wet && !kitchen && doors.Count > 0)
        {
            var reach = bedroom ? 3000 : 7500;
            var nearest = doors.Min(NearestSmoke);
            if (nearest > reach + 0.5)
                wanting.Add($"its door is {Units.Metres(nearest, "0.0")} from a smoke alarm - BS 5839-6 asks for one within {(bedroom ? "3 m of a bedroom's door" : "7.5 m of a room's door")}");
        }

        // A heat alarm in the kitchen of a house of three storeys or more: LD2.
        var storeys = document.Levels.Count(level => document.All<Room>().Any(other => other.LevelId == level.Id));
        if (!us && kitchen && storeys >= 3 && !Has(ComponentForm.HeatAlarm))
            wanting.Add("no heat alarm in the kitchen - Approved Document B (1.7) asks for an LD2 system, with one, in a house of three storeys or more");

        // A carbon monoxide alarm by a stove, a fire or a boiler.
        var appliances = Appliances(document, room.LevelId).Where(appliance => Polygon2D.Contains(boundary.Polygon, appliance.At)).ToList();
        if (appliances.Count > 0 && !Has(ComponentForm.CarbonMonoxideAlarm) && (!us || bedroom))
            wanting.Add(us
                ? $"no carbon monoxide alarm in this bedroom with {appliances[0].Name} in it - IRC R315.3 asks for one"
                : $"no carbon monoxide alarm by {appliances[0].Name} - Approved Document J (2.34, 3.43) asks for one in its room");

        return wanting.Count == 0 ? null : $"Alarms: {string.Join("; ", wanting)}";
    }
}
