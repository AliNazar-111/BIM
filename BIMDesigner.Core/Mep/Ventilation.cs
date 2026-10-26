using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Mep;

/// <summary>What a room is for, as its ventilation goes: a wet room extracted from, a habitable room supplied, or neither.</summary>
public enum RoomUse
{
    Kitchen,
    Utility,
    Bathroom,
    Wc,
    Habitable,
    Other
}

/// <summary>A room's ventilation at the design: the air supplied to it and extracted from it, l/s - running continuously, and at its high rate - and the rate its use asks.</summary>
public sealed record RoomAir(Room Room, RoomUse Use, double Volume, double Supply, double Extract, double SupplyHigh, double ExtractHigh, double ExtractAsked, IReadOnlyList<Component> Supplies, IReadOnlyList<Component> Extracts)
{
    /// <summary>The fans in it running continuously.</summary>
    public IReadOnlyList<Component> Fans { get; init; } = Array.Empty<Component>();
}

/// <summary>How a home is ventilated, as Approved Document F's Table 1.6 has the systems.</summary>
public enum VentilationType
{
    Natural,
    ContinuousExtract,
    HeatRecovery
}

/// <summary>
/// A home's ventilation as Approved Document F (2021, Volume 1) sets it - and in the US the IRC.
///
/// A home with a heat recovery unit on its supply and extract is ventilated mechanically with heat
/// recovery: each habitable room supplied, the supply shared out in proportion to the rooms' volumes
/// (1.67); each kitchen, utility room, bathroom and WC extracted from at least at Table 1.2's high
/// rate - a kitchen 13 l/s, a utility room or bathroom 8, a WC 6 (1.70); and the whole of it running
/// continuously at least at the whole dwelling rate - 0.3 l/s a square metre of floor, or Table 1.3's
/// by the bedrooms (19 l/s for one, 25 for two, 31 for three, 6 more for each more), whichever is
/// more (1.24, 1.69) - extract and supply balanced. In the US, the whole-house rate is 0.01 cfm a
/// square foot and 7.5 cfm for each of the bedrooms and one more (IRC M1505.4.3, Equation 15-1),
/// and a kitchen extracted at 25 cfm and a bathroom at 20 continuously (Table M1505.4.4).
/// </summary>
public static class Ventilation
{
    /// <summary>Table 1.2's high rates, l/s: a kitchen's, a utility room's, a bathroom's, a WC's.</summary>
    public const double KitchenHigh = 13, UtilityHigh = 8, BathroomHigh = 8, WcHigh = 6;

    /// <summary>Litres a second in a cubic foot a minute.</summary>
    public const double LitresPerCfm = 0.4719;

    private static readonly string[] NotHabitable =
        { "hall", "landing", "corridor", "lobby", "porch", "stair", "store", "storage", "cupboard", "airing", "garage", "loft", "plant", "void", "boiler", "cellar", "shed", "bin", "cycle" };

    /// <summary>What a room is for: a kitchen by its sink or cooker, a bathroom by its bath or shower, a WC, a utility room by its machines; otherwise habitable unless its name says it is a hall, a store or the like.</summary>
    public static RoomUse UseOf(BimDocument document, Room room, IReadOnlyList<Point2D> outline)
    {
        var forms = document.All<Component>()
            .Where(piece => piece.LevelId == room.LevelId && Polygon2D.Contains(outline, piece.Location))
            .Select(piece => document.FindType<ComponentType>(piece.TypeId)).OfType<ComponentType>().ToList();
        if (forms.Any(type => type.Form is ComponentForm.Sink or ComponentForm.Cooker)) return RoomUse.Kitchen;
        if (forms.Any(type => type.Form is ComponentForm.Bath or ComponentForm.Shower)) return RoomUse.Bathroom;
        if (forms.Any(Bathrooms.IsWc)) return RoomUse.Wc;
        if (forms.Any(type => type.Form is ComponentForm.WashingMachine or ComponentForm.TumbleDryer)) return RoomUse.Utility;

        var name = room.Name.ToLowerInvariant();
        if (name.Contains("kitchen")) return RoomUse.Kitchen;
        if (name.Contains("utility")) return RoomUse.Utility;
        if (name.Contains("bath") || name.Contains("shower") || name.Contains("en-suite") || name.Contains("ensuite")) return RoomUse.Bathroom;
        if (name.Contains("wc") || name.Contains("toilet") || name.Contains("cloak")) return RoomUse.Wc;
        return NotHabitable.Any(name.Contains) ? RoomUse.Other : RoomUse.Habitable;
    }

    /// <summary>The rate a wet room is extracted at, l/s, running continuously: Table 1.2's high rate - or in the US Table M1505.4.4's continuous one.</summary>
    public static double ExtractAsked(RoomUse use, bool us) => use switch
    {
        RoomUse.Kitchen => us ? 25 * LitresPerCfm : KitchenHigh,
        RoomUse.Utility => us ? 0 : UtilityHigh,
        RoomUse.Bathroom => us ? 20 * LitresPerCfm : BathroomHigh,
        RoomUse.Wc => us ? 20 * LitresPerCfm : WcHigh,
        _ => 0
    };

    /// <summary>
    /// The whole dwelling ventilation rate, l/s, and what it is reckoned from: the floor area (m²) and
    /// the bedrooms - Approved Document F's 0.3 l/s a m² or Table 1.3's, whichever is more; the IRC's
    /// Equation 15-1 in the US.
    /// </summary>
    public static (double Rate, double Area, int Bedrooms) WholeDwelling(BimDocument document)
    {
        var rooms = document.All<Room>().Select(room => (Room: room, Boundary: room.GetBoundary(document))).Where(entry => entry.Boundary.IsEnclosed).ToList();
        var area = rooms.Sum(entry => entry.Boundary.Area) / 1e6;
        var bedrooms = rooms.Count(entry => Bedrooms.Of(document, entry.Room, entry.Boundary.Polygon) is not null || entry.Room.Name.Contains("bedroom", StringComparison.OrdinalIgnoreCase));
        if (document.ProjectInformation.ClearanceStandard.IsUs())
            return ((0.01 * area * 10.7639 + 7.5 * (bedrooms + 1)) * LitresPerCfm, area, bedrooms);

        var habitable = rooms.Count(entry => UseOf(document, entry.Room, entry.Boundary.Polygon) == RoomUse.Habitable);
        var byBedrooms = bedrooms == 0 ? habitable <= 1 ? 13 : 19 : 19 + 6 * (bedrooms - 1);
        return (Math.Max(0.3 * area, byBedrooms), area, bedrooms);
    }

    /// <summary>The heat recovery units in the model.</summary>
    public static IReadOnlyList<Component> Units(BimDocument document) => Of(document, ComponentForm.HeatRecoveryUnit);

    /// <summary>The central extract units in the model.</summary>
    public static IReadOnlyList<Component> ExtractUnits(BimDocument document) => Of(document, ComponentForm.ExtractUnit);

    private static IReadOnlyList<Component> Of(BimDocument document, ComponentForm form) =>
        document.All<Component>().Where(component => document.FindType<ComponentType>(component.TypeId)?.Form == form).ToList();

    /// <summary>Whether the home is ventilated mechanically with heat recovery: a heat recovery unit feeding supply valves.</summary>
    public static bool WithHeatRecovery(BimDocument document) =>
        Units(document).Any(unit => MepSystems.FedBy(document, unit, SystemKind.SupplyAir).Any(system => MepSystems.MembersOf(document, system).Count > 0));

    /// <summary>
    /// How the home is ventilated, as Approved Document F's Table 1.6 has the systems: with heat recovery,
    /// by continuous mechanical extract - a central extract unit drawing from valves, or fans running all the
    /// time in the wet rooms - or naturally, with background ventilators and intermittent extract fans.
    /// </summary>
    public static VentilationType TypeOf(BimDocument document)
    {
        if (WithHeatRecovery(document)) return VentilationType.HeatRecovery;
        var central = ExtractUnits(document).Any(unit => MepSystems.FedBy(document, unit, SystemKind.ExtractAir).Any(system => MepSystems.MembersOf(document, system).Count > 0));
        var continuous = document.All<Component>().Any(component => document.FindType<ComponentType>(component.TypeId) is { Form: ComponentForm.ExtractFan, ContinuousRate: > 0 });
        return central || continuous ? VentilationType.ContinuousExtract : VentilationType.Natural;
    }

    /// <summary>Whether the ventilation is being designed: anything to ventilate with is in the model - a fan, a hood, a unit, a valve or a ventilator.</summary>
    public static bool Designed(BimDocument document) =>
        document.All<Component>().Any(component => document.FindType<ComponentType>(component.TypeId)?.Form is ComponentForm.ExtractFan or ComponentForm.CookerHood
            or ComponentForm.HeatRecoveryUnit or ComponentForm.ExtractUnit or ComponentForm.SupplyTerminal or ComponentForm.ExtractTerminal or ComponentForm.BackgroundVentilator);

    /// <summary>A room's background ventilators: the trickle vents in its windows and the ventilators on its walls, and their equivalent area together, mm².</summary>
    public static (double Area, int Count, IReadOnlyList<Element> Ventilators) BackgroundVentilators(BimDocument document, Room room, IReadOnlyList<Point2D> outline)
    {
        var found = new List<(Element Element, double Area)>();
        foreach (var (window, type, _) in WindowRules.Of(document, room, outline).Windows.Where(entry => entry.Type.TrickleVent > 0))
            found.Add((window, type.TrickleVent));
        foreach (var vent in document.All<Component>().Where(component => component.LevelId == room.LevelId))
            if (document.FindType<ComponentType>(vent.TypeId) is { Form: ComponentForm.BackgroundVentilator } type
                && (RoomsOf.Holds(document, room, outline, vent) || Edges(outline).Any(edge => Line2D.DistanceFromSegment(vent.Location, edge.A, edge.B) <= 100)))
                found.Add((vent, type.VentArea));
        return (found.Sum(entry => entry.Area), found.Count, found.Select(entry => entry.Element).ToList());
    }

    private static IEnumerable<(Point2D A, Point2D B)> Edges(IReadOnlyList<Point2D> outline) =>
        outline.Select((point, i) => (point, outline[(i + 1) % outline.Count]));

    /// <summary>
    /// The equivalent area of background ventilators Approved Document F's Table 1.7 asks of a room in a
    /// naturally ventilated home, mm²: a habitable room or a kitchen 8000 where the home has more than one
    /// storey, 10,000 where it has one; a bathroom 4000; a utility room or a WC none.
    /// </summary>
    public static double VentilatorsAsked(RoomUse use, bool oneStorey) => use switch
    {
        RoomUse.Habitable or RoomUse.Kitchen => oneStorey ? 10_000 : 8000,
        RoomUse.Bathroom => 4000,
        _ => 0
    };

    /// <summary>
    /// Every room's air at the design: what its valves and its continuous fans supply and extract,
    /// continuously and at the high rate, the extract it is asked for, and its valves and fans. The extract
    /// valves of a wet room share its asked rate; the supply valves share the supply, the habitable rooms'
    /// by their volumes; continuously the valves run at the whole dwelling rate, at the high rate at the wet
    /// rooms' rates, balanced. A continuous fan runs at its continuous rate and boosts to its extract rate.
    /// </summary>
    public static IReadOnlyList<RoomAir> Rooms(BimDocument document) =>
        ModelCache.Of(document) is { } kept ? kept.Keep("room air", document, null, null, () => Work(document)) : Work(document);

    private static IReadOnlyList<RoomAir> Work(BimDocument document)
    {
        var us = document.ProjectInformation.ClearanceStandard.IsUs();
        var pieces = document.All<Component>()
            .Select(component => (Component: component, Type: document.FindType<ComponentType>(component.TypeId)))
            .Where(entry => entry.Type is { Form: ComponentForm.SupplyTerminal or ComponentForm.ExtractTerminal } || entry.Type is { Form: ComponentForm.ExtractFan, ContinuousRate: > 0 })
            .ToList();
        var rooms = document.All<Room>().Select(room => (Room: room, Boundary: room.GetBoundary(document)))
            .Where(entry => entry.Boundary.IsEnclosed && entry.Boundary.Polygon.Count >= 3)
            .Select(entry =>
            {
                bool Inside(Component piece) => piece.LevelId == entry.Room.LevelId && (RoomsOf.Holds(document, entry.Room, entry.Boundary.Polygon, piece)
                    || Edges(entry.Boundary.Polygon).Any(edge => Line2D.DistanceFromSegment(piece.Location, edge.A, edge.B) <= 100));
                var inside = pieces.Where(piece => Inside(piece.Component)).ToList();
                var use = UseOf(document, entry.Room, entry.Boundary.Polygon);
                var volume = (RoomHeadroom.Measure(document, entry.Room)?.Volume ?? entry.Boundary.Area * entry.Room.Height) / 1e9;
                return (entry.Room, Use: use, Volume: volume,
                    Supplies: inside.Where(piece => piece.Type!.Form == ComponentForm.SupplyTerminal).Select(piece => piece.Component).ToList(),
                    Extracts: inside.Where(piece => piece.Type!.Form == ComponentForm.ExtractTerminal).Select(piece => piece.Component).ToList(),
                    Fans: inside.Where(piece => piece.Type!.Form == ComponentForm.ExtractFan).ToList());
            }).ToList();

        var asked = rooms.ToDictionary(room => room.Room, room => room.Extracts.Count == 0 ? 0 : Math.Max(ExtractAsked(room.Use, us), room.Use is RoomUse.Habitable or RoomUse.Other ? WcHigh : 0));
        var whole = WholeDwelling(document).Rate;
        var totalAsked = asked.Values.Sum();
        double Continuous(Room room) => totalAsked > 0 ? asked[room] * whole / totalAsked : 0;
        var totalHigh = rooms.Sum(room => Math.Max(asked[room.Room], Continuous(room.Room)));
        var supplyVolume = rooms.Where(room => room.Supplies.Count > 0).Sum(room => room.Volume);
        return rooms.Select(room =>
        {
            var share = supplyVolume > 0 && room.Supplies.Count > 0 ? room.Volume / supplyVolume : 0;
            var continuous = Continuous(room.Room);
            var fansContinuous = room.Fans.Sum(fan => fan.Type!.ContinuousRate);
            var fansHigh = room.Fans.Sum(fan => Math.Max(fan.Type!.ExtractRate, fan.Type.ContinuousRate));
            return new RoomAir(room.Room, room.Use, room.Volume, share * whole, continuous + fansContinuous, share * Math.Max(totalHigh, whole),
                Math.Max(asked[room.Room], continuous) + fansHigh, ExtractAsked(room.Use, us), room.Supplies, room.Extracts)
            {
                Fans = room.Fans.Select(fan => fan.Component).ToList()
            };
        }).ToList();
    }

    /// <summary>Each valve's airflow, l/s - at the high rate, or continuously: its room's shared among its valves.</summary>
    public static IReadOnlyDictionary<Component, double> TerminalFlows(BimDocument document, bool high = true)
    {
        var flows = new Dictionary<Component, double>();
        foreach (var room in Rooms(document))
        {
            var fansHigh = room.Fans.Sum(fan => document.FindType<ComponentType>(fan.TypeId) is { } type ? Math.Max(type.ExtractRate, type.ContinuousRate) : 0);
            var fansContinuous = room.Fans.Sum(fan => document.FindType<ComponentType>(fan.TypeId)?.ContinuousRate ?? 0);
            foreach (var supply in room.Supplies) flows[supply] = (high ? room.SupplyHigh : room.Supply) / room.Supplies.Count;
            foreach (var extract in room.Extracts) flows[extract] = ((high ? room.ExtractHigh - fansHigh : room.Extract - fansContinuous)) / room.Extracts.Count;
        }

        return flows;
    }

    /// <summary>
    /// What the heat loss takes of the ventilation: a heat recovery unit's efficiency (0 for extract alone),
    /// and each room's air supplied and extracted continuously, m³/h; none for natural ventilation.
    /// </summary>
    public static (double Efficiency, IReadOnlyDictionary<Room, (double Supply, double Extract)> Rooms)? ForHeatLoss(BimDocument document)
    {
        var type = TypeOf(document);
        if (type == VentilationType.Natural) return null;
        var efficiency = type == VentilationType.HeatRecovery ? Units(document).Select(unit => document.FindType<ComponentType>(unit.TypeId)?.HeatRecovery ?? 0).DefaultIfEmpty(0).Max() : 0;
        return (efficiency, Rooms(document).ToDictionary(room => room.Room, room => (type == VentilationType.HeatRecovery ? room.Supply * 3.6 : 0, room.Extract * 3.6)));
    }

    /// <summary>
    /// What is wanting in the ventilation by Approved Document F: as the system is - natural, continuous
    /// extract, or heat recovery - rooms not supplied or extracted from as it asks, its units too small, the
    /// whole short of the whole dwelling rate; and the background ventilators each asks for, or not.
    /// </summary>
    public static IEnumerable<MepProblem> Problems(BimDocument document)
    {
        if (!Designed(document)) yield break;
        var us = document.ProjectInformation.ClearanceStandard.IsUs();
        var type = TypeOf(document);
        var rooms = Rooms(document);
        var (whole, area, bedrooms) = WholeDwelling(document);

        if (type == VentilationType.HeatRecovery)
        {
            foreach (var room in rooms)
            {
                if (room.Use == RoomUse.Habitable && room.Supplies.Count == 0 && !us)
                    yield return new MepProblem(room.Room, "no supply valve - Approved Document F (1.67) supplies each habitable room of a home ventilated with heat recovery");
                if (room.ExtractAsked > 0 && room.Extracts.Count == 0 && room.Fans.Count == 0)
                    yield return new MepProblem(room.Room, us
                        ? $"no extract valve - IRC Table M1505.4.4 asks for {room.ExtractAsked / LitresPerCfm:0} cfm extracted from it continuously"
                        : $"no extract valve - Approved Document F (1.70) extracts at least {room.ExtractAsked:0} l/s from a {Describe(room.Use)} (Table 1.2)");
            }

            foreach (var unit in Units(document))
            {
                if (document.FindType<ComponentType>(unit.TypeId) is not { } unitType) continue;
                // What the unit draws through its valves - not what fans in the same rooms take.
                double Fans(RoomAir room) => room.Fans.Sum(fan => document.FindType<ComponentType>(fan.TypeId) is { } fanType ? Math.Max(fanType.ExtractRate, fanType.ContinuousRate) : 0);
                var high = Math.Max(rooms.Where(room => room.Extracts.Count > 0).Sum(room => Math.Max(0, room.ExtractHigh - Fans(room))), rooms.Sum(room => room.SupplyHigh));
                if (unitType.Airflow > 0 && unitType.Airflow < high - 0.05)
                    yield return new MepProblem(unit, $"it moves {unitType.Airflow:0} l/s at most - its rooms want {high:0} l/s at the high rate");
                if (rooms.Sum(room => room.Supplies.Count) == 0) continue;
                var supplied = rooms.Sum(room => room.Supply);
                if (supplied < whole - 0.05)
                    yield return new MepProblem(unit, us
                        ? $"it supplies {supplied / LitresPerCfm:0} cfm - IRC M1505.4.3 asks {whole / LitresPerCfm:0} cfm for {area * 10.7639:0} sq ft and {bedrooms} bedroom{(bedrooms == 1 ? string.Empty : "s")}"
                        : $"it supplies {supplied:0.0} l/s - Approved Document F (1.24, 1.69) asks {whole:0.0} l/s of {area:0} m² and {bedrooms} bedroom{(bedrooms == 1 ? string.Empty : "s")}");
            }
        }
        else if (type == VentilationType.ContinuousExtract)
        {
            foreach (var room in rooms.Where(room => room.ExtractAsked > 0))
            {
                if (room.Extracts.Count == 0 && room.Fans.Count == 0)
                    yield return new MepProblem(room.Room, us
                        ? $"no continuous extract - IRC Table M1505.4.4 asks for {room.ExtractAsked / LitresPerCfm:0} cfm from it continuously"
                        : $"no continuous extract - Approved Document F (1.63) extracts at least {room.ExtractAsked:0} l/s from a {Describe(room.Use)} at the high rate (Table 1.2)");
                else if (room.ExtractHigh < room.ExtractAsked - 0.05)
                    yield return new MepProblem(room.Room, $"its extract reaches {room.ExtractHigh:0.#} l/s at the high rate - Approved Document F (1.63, Table 1.2) asks {room.ExtractAsked:0} l/s of a {Describe(room.Use)}");
            }

            var continuous = rooms.Sum(room => room.Extract);
            var first = (Element?)ExtractUnits(document).FirstOrDefault() ?? rooms.SelectMany(room => room.Fans).FirstOrDefault()
                        ?? document.All<Component>().FirstOrDefault(component => document.FindType<ComponentType>(component.TypeId) is { Form: ComponentForm.ExtractFan, ContinuousRate: > 0 });
            if (first is not null && continuous < whole - 0.05)
                yield return new MepProblem(first, us
                    ? $"the extract runs at {continuous / LitresPerCfm:0} cfm continuously - IRC M1505.4.3 asks {whole / LitresPerCfm:0} cfm"
                    : $"the extract runs at {continuous:0.0} l/s continuously - Approved Document F (1.62) asks at least the whole dwelling rate, {whole:0.0} l/s");
            var valves = TerminalFlows(document).Where(entry => document.FindType<ComponentType>(entry.Key.TypeId)?.Form == ComponentForm.ExtractTerminal).Sum(entry => entry.Value);
            foreach (var unit in ExtractUnits(document))
                if (document.FindType<ComponentType>(unit.TypeId) is { Airflow: > 0 } unitType && unitType.Airflow < valves - 0.05)
                    yield return new MepProblem(unit, $"it moves {unitType.Airflow:0} l/s at most - its valves want {valves:0} l/s at the high rate");
        }

        // Background ventilators: none with heat recovery; with continuous extract none in the wet rooms, 4000 mm² in each habitable
        // room and as many as the bedrooms and two; naturally, Table 1.7's in each room with an outside wall, five at least.
        if (us) yield break;
        var oneStorey = rooms.Select(room => room.Room.LevelId).Distinct().Count() <= 1;
        var counted = 0;
        foreach (var room in rooms)
        {
            var outline = room.Room.GetBoundary(document);
            var (vents, count, ventilators) = BackgroundVentilators(document, room.Room, outline.Polygon);
            if (room.Use == RoomUse.Habitable || room.Use == RoomUse.Kitchen && type == VentilationType.Natural) counted += count;
            var outside = outline.Sides.Any(wall => wall is not null && document.GetWallType(wall)?.Function == WallFunction.Exterior);
            switch (type)
            {
                case VentilationType.HeatRecovery when count > 0:
                    yield return new MepProblem(room.Room, "background ventilators with heat recovery - Approved Document F (1.72) leaves them out, so air does not find its own way in");
                    break;
                case VentilationType.ContinuousExtract when room.Use is RoomUse.Kitchen or RoomUse.Utility or RoomUse.Bathroom or RoomUse.Wc && count > 0:
                    yield return new MepProblem(room.Room, "background ventilators in a wet room with continuous extract - Approved Document F (1.64 a) keeps them out of the wet rooms");
                    break;
                case VentilationType.ContinuousExtract when room.Use == RoomUse.Habitable && outside && vents < 4000 - 0.5:
                    yield return new MepProblem(room.Room, $"background ventilators of {vents:0} mm² - Approved Document F (1.64 b) asks 4000 mm² for each habitable room with continuous extract");
                    break;
                case VentilationType.Natural when outside && vents < VentilatorsAsked(room.Use, oneStorey) - 0.5:
                    yield return new MepProblem(room.Room, $"background ventilators of {vents:0} mm² - Approved Document F (1.52, Table 1.7) asks {VentilatorsAsked(room.Use, oneStorey):0} mm² for a {Describe(room.Use)} in a home of {(oneStorey ? "one storey" : "more than one storey")}");
                    break;
            }

            // Fans and background ventilators in the same room 500 mm apart at least (1.55).
            if (type == VentilationType.Natural)
                foreach (var fan in document.All<Component>().Where(component => component.LevelId == room.Room.LevelId
                             && document.FindType<ComponentType>(component.TypeId)?.Form == ComponentForm.ExtractFan
                             && (RoomsOf.Holds(document, room.Room, outline.Polygon, component) || Edges(outline.Polygon).Any(edge => Line2D.DistanceFromSegment(component.Location, edge.A, edge.B) <= 100))))
                    if (ventilators.Select(Where).Any(at => at.DistanceTo(fan.Location) < 500 - 0.5))
                        yield return new MepProblem(fan, "a background ventilator within 500 mm of it - Approved Document F (1.55) keeps a fan and a ventilator in the same room 500 mm apart, so the fan does not draw straight from it");
        }

        var wanted = type switch
        {
            VentilationType.Natural => bedrooms <= 1 ? 4 : 5,
            VentilationType.ContinuousExtract when rooms.Any(room => room.Use == RoomUse.Habitable) => bedrooms + 2,
            _ => 0
        };
        var anyRoom = rooms.FirstOrDefault(room => room.Use == RoomUse.Habitable)?.Room ?? rooms.FirstOrDefault()?.Room;
        if (wanted > 0 && counted < wanted && anyRoom is not null)
            yield return new MepProblem(anyRoom, type == VentilationType.Natural
                ? $"the home has {counted} background ventilator{(counted == 1 ? string.Empty : "s")} in its habitable rooms and kitchen - Approved Document F (1.57) asks {wanted} at least"
                : $"the home has {counted} background ventilator{(counted == 1 ? string.Empty : "s")} in its habitable rooms - Approved Document F (1.64 c) asks one for each bedroom and two more, {wanted}");

        Point2D Where(Element element) => element switch
        {
            Component component => component.Location,
            Opening opening when document.Find<Wall>(opening.HostWallId) is { } wall && document.GetWallType(wall) is { } wallType => wall.PointAt(wallType.Structure, opening.DistanceAlongWall, 0),
            _ => new Point2D(double.MaxValue, double.MaxValue)
        };
    }

    /// <summary>The whole-house rate ASHRAE 62.2 (2016 and 2019) asks of a US home, l/s: 0.03 cfm a square foot and 7.5 for each of the bedrooms and one more - as it is reported.</summary>
    public static double Ashrae622(BimDocument document)
    {
        var (_, area, bedrooms) = WholeDwelling(document);
        return (0.03 * area * 10.7639 + 7.5 * (Math.Max(1, bedrooms) + 1)) * LitresPerCfm;
    }

    public static string Describe(RoomUse use) => use switch
    {
        RoomUse.Wc => "WC",
        RoomUse.Utility => "utility room",
        _ => use.ToString().ToLowerInvariant()
    };
}
