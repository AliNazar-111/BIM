using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;

namespace BIMDesigner.Core.Mep;

/// <summary>
/// Where a home's smoke and carbon monoxide alarms go.
///
/// In the UK, Approved Document B (Volume 1, 1.1) asks at least Grade D2 Category LD3 of BS 5839-6: a
/// smoke alarm in the circulation spaces - the hall and each landing - on every storey. In the US, the
/// IRC (R314.3, 2021; R310 in 2024): a smoke alarm in each sleeping room, outside each sleeping area,
/// and on each storey; and a carbon monoxide alarm outside each sleeping area where the home has a
/// fuel-burning appliance or an attached garage (R315.3).
/// </summary>
public static class AlarmSiting
{
    private static readonly string[] Circulation = { "hall", "landing", "corridor", "lobby", "foyer", "entry", "stair" };

    private static bool Is(BimDocument document, Component component, params ComponentForm[] forms) =>
        document.FindType<ComponentType>(component.TypeId) is { } type && forms.Contains(type.Form);

    /// <summary>Whether the alarms are being designed: an alarm, a board or a circuit is in the model.</summary>
    private static bool Designed(BimDocument document) =>
        document.All<Component>().Any(component => Is(document, component, ComponentForm.SmokeAlarm, ComponentForm.HeatAlarm, ComponentForm.CarbonMonoxideAlarm, ComponentForm.ConsumerUnit));

    public static IEnumerable<MepProblem> Problems(BimDocument document)
    {
        if (!Designed(document)) yield break;
        var us = document.ProjectInformation.ClearanceStandard.IsUs();
        var smoke = document.All<Component>().Where(component => Is(document, component, ComponentForm.SmokeAlarm)).ToList();
        var carbon = document.All<Component>().Where(component => Is(document, component, ComponentForm.CarbonMonoxideAlarm)).ToList();
        var rooms = document.All<Room>().Select(room => (Room: room, Boundary: room.GetBoundary(document))).Where(entry => entry.Boundary.IsEnclosed).ToList();
        bool Has(Room room, IReadOnlyList<Component> alarms) => alarms.Any(alarm => RoomsOf.Holds(document, room, rooms.First(entry => entry.Room == room).Boundary.Polygon, alarm));
        bool Circulating(Room room) => Circulation.Any(room.Name.ToLowerInvariant().Contains);
        bool Bedroom(Room room, IReadOnlyList<Geometry.Point2D> outline) => room.Name.Contains("bed", StringComparison.OrdinalIgnoreCase) || Bedrooms.Of(document, room, outline) is not null;

        foreach (var storey in rooms.GroupBy(entry => entry.Room.LevelId))
        {
            var halls = storey.Where(entry => Circulating(entry.Room)).ToList();
            var level = document.FindLevel(storey.Key)?.Name ?? "a storey";
            if (!us)
            {
                foreach (var (room, _) in halls.Where(entry => !Has(entry.Room, smoke)))
                    yield return new MepProblem(room, "no smoke alarm - Approved Document B (1.1) asks at least Grade D2 Category LD3: a smoke alarm in the circulation spaces on every storey");
                if (halls.Count == 0 && !storey.Any(entry => Has(entry.Room, smoke)))
                    yield return new MepProblem(storey.First().Room, $"no smoke alarm on {level} - Approved Document B (1.1) asks a smoke alarm on every storey");
                continue;
            }

            var bedrooms = storey.Where(entry => Bedroom(entry.Room, entry.Boundary.Polygon)).ToList();
            foreach (var (room, _) in bedrooms.Where(entry => !Has(entry.Room, smoke)))
                yield return new MepProblem(room, "no smoke alarm - IRC R314.3 puts a smoke alarm in each sleeping room");
            if (bedrooms.Count > 0)
                foreach (var (room, _) in halls.Where(entry => !Has(entry.Room, smoke)))
                    yield return new MepProblem(room, "no smoke alarm - IRC R314.3 puts one outside each sleeping area, in the vicinity of the bedrooms");
            if (!storey.Any(entry => Has(entry.Room, smoke)))
                yield return new MepProblem(storey.First().Room, $"no smoke alarm on {level} - IRC R314.3 puts one on each storey");

            // Carbon monoxide: with a fuel-burning appliance or an attached garage.
            var fuel = document.All<Component>().Any(component => MepSystems.ConnectorsOf(document, component).Any(connector => connector.Kind == SystemKind.Gas && !connector.Feeds))
                       || rooms.Any(entry => entry.Room.Name.Contains("garage", StringComparison.OrdinalIgnoreCase));
            if (fuel && bedrooms.Count > 0 && !storey.Any(entry => !Bedroom(entry.Room, entry.Boundary.Polygon) && Has(entry.Room, carbon)))
                yield return new MepProblem(halls.FirstOrDefault().Room ?? bedrooms[0].Room,
                    "no carbon monoxide alarm outside the bedrooms - IRC R315.3 asks one outside each sleeping area of a home with a fuel-burning appliance or an attached garage");
        }
    }
}
