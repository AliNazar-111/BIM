using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;

namespace BIMDesigner.Core.Mep;

/// <summary>What one radiator, towel rail or underfloor loop gives its room at its zone's temperatures, W - and for a floor, its area (m²), what it gives a square metre and how warm its surface is.</summary>
public sealed record EmitterOutput(Component Component, ComponentType Type, MepSystem? Zone, double Flow, double Return, double Watts)
{
    public double FloorArea { get; init; }

    public double Flux { get; init; }

    public double SurfaceTemperature { get; init; }

    public bool Floor => Type.Form == ComponentForm.UnderfloorHeating;
}

/// <summary>
/// The heating: what each room's radiators and floors give at its zone's water temperatures, what
/// each heat source gives against the heat load, and the rules round them.
///
/// A radiator's output is rated at a mean water temperature 50 K over the room's - 75/65/20 °C
/// (BS EN 442) - and falls as the difference to the power of its exponent n, from its test.
/// Underfloor heating gives what its maker's tables give for its pipe spacing, screed and floor
/// covering - so many W/m² for each kelvin its mean water temperature is over the room's - up to
/// the most BS EN 1264 lets a floor's surface be warm: 9 K over the room where people are (29 °C at
/// 20, 33 in a bathroom at 24); its surface temperature is the standard's basic characteristic, q =
/// 8.92 (θF - θi)^1.1. A loop is kept to 100 m of pipe and its flow to 55 °C over a cement screed
/// (the Danfoss underfloor handbook).
///
/// A zone runs at its source's usual temperatures unless they are set: a boiler 55 °C, a heat pump
/// 45 - Approved Document L's notional dwelling's (Table 1.1) - coming back 10 and 5 K cooler. A
/// new wet heating system is sized to run at 55 °C or less (Approved Document L 5.10); its heat
/// sources on a heat loss (5.8), a heat pump giving the whole of it at the design temperature with
/// no electric heater (5.12, and MCS MIS 3005-D 5.5.1 e); and a home's hot water reckoned 45 litres
/// a day for each of the bedrooms and one more, or the occupants if more (MIS 3005-D 5.6.2).
/// </summary>
public static class Heating
{
    /// <summary>The highest flow temperature a new wet heating system is sized to run at, °C (Approved Document L 5.10).</summary>
    public const double FlowLimit = 55;

    /// <summary>The usual flow temperatures, °C: a boiler's and a heat pump's, as Approved Document L's notional dwelling has them (Table 1.1); and how much cooler each comes back, K.</summary>
    public const double BoilerFlow = 55, HeatPumpFlow = 45, BoilerDrop = 10, HeatPumpDrop = 5;

    /// <summary>How much warmer than the room a floor's surface may be where people are, K (BS EN 1264).</summary>
    public const double FloorExcess = 9;

    /// <summary>The longest underfloor loop, mm, and the warmest flow over a cement screed, °C (the Danfoss underfloor handbook).</summary>
    public const double LongestLoop = 100_000, ScreedFlow = 55;

    /// <summary>A home's hot water a day for each person, litres (MIS 3005-D 5.6.2).</summary>
    public const double HotWaterPerPerson = 45;

    /// <summary>
    /// Whether a fitting's heating connector is on the return - the water going back to the heat
    /// source - rather than the flow: a boiler's or a heat pump's way in, a radiator's, a floor's or
    /// a coil's way out. Null for a connector that is not the heating's.
    /// </summary>
    public static bool? ReturnSide(BimDocument document, Component fitting, Connector connector)
    {
        if (connector.Kind != SystemKind.Heating || connector.Domain != ConnectorDomain.Pipe || document.FindType<ComponentType>(fitting.TypeId) is not { } type) return null;
        var source = Connectors.Of(type).Any(each => each.Kind == SystemKind.Heating && each.Feeds);
        return source ? connector.Direction == ConnectorFlow.In : connector.Direction == ConnectorFlow.Out;
    }

    /// <summary>Whether a fitting heats a room.</summary>
    public static bool IsEmitter(ComponentForm form) => form is ComponentForm.Radiator or ComponentForm.TowelRail or ComponentForm.UnderfloorHeating;

    /// <summary>Whether a heating zone runs from a heat pump.</summary>
    public static bool FromHeatPump(BimDocument document, MepSystem system) =>
        MepSystems.SourceOf(document, system) is { } source && document.FindType<ComponentType>(source.TypeId)?.Form == ComponentForm.HeatPump;

    /// <summary>A heating zone's flow and return temperatures, °C: as set, or its source's usual.</summary>
    public static (double Flow, double Return) Design(BimDocument document, MepSystem system)
    {
        var pump = FromHeatPump(document, system);
        var flow = system.FlowTemperature > 0 ? system.FlowTemperature : pump ? HeatPumpFlow : BoilerFlow;
        var back = system.ReturnTemperature > 0 && system.ReturnTemperature < flow ? system.ReturnTemperature : flow - (pump ? HeatPumpDrop : BoilerDrop);
        return (flow, back);
    }

    /// <summary>A radiator's output, W, at these water temperatures in a room this warm (°C).</summary>
    public static double RadiatorOutput(ComponentType type, double flow, double back, double room) =>
        type.HeatOutput <= 0 ? 0 : type.HeatOutput * Math.Pow(Math.Max(0, (flow + back) / 2 - room) / 50, type.HeatExponent);

    /// <summary>What an underfloor loop's floor gives a square metre, W/m², at these water temperatures in a room this warm.</summary>
    public static double FloorFlux(ComponentType type, double flow, double back, double room) =>
        Math.Min(type.FloorOutput * Math.Max(0, (flow + back) / 2 - room), FloorLimit(type));

    /// <summary>The most a floor gives, W/m²: its maker's limit, or BS EN 1264's where none is given.</summary>
    public static double FloorLimit(ComponentType type) => type.FloorOutputLimit > 0 ? type.FloorOutputLimit : MostFlux;

    /// <summary>How warm a floor's surface is giving this much, °C: BS EN 1264's basic characteristic, q = 8.92 (θF - θi)^1.1.</summary>
    public static double SurfaceTemperature(double flux, double room) => room + Math.Pow(Math.Max(0, flux) / 8.92, 1 / 1.1);

    /// <summary>The most a floor gives at BS EN 1264's limit to its warmth, W/m²: its surface 9 K over the room.</summary>
    public static double MostFlux => 8.92 * Math.Pow(FloorExcess, 1.1);

    /// <summary>The radiators, towel rails and underfloor loops in a room, and what each gives it.</summary>
    public static IReadOnlyList<EmitterOutput> In(BimDocument document, Room room, IReadOnlyList<Point2D> outline, double temperature)
    {
        var found = document.All<Component>()
            .Where(component => component.LevelId == room.LevelId && Polygon2D.Contains(outline, component.Location))
            .Select(component => (Component: component, Type: document.FindType<ComponentType>(component.TypeId)))
            .Where(entry => entry.Type is not null && IsEmitter(entry.Type.Form))
            .ToList();
        var floors = found.Count(entry => entry.Type!.Form == ComponentForm.UnderfloorHeating);
        var area = Polygon2D.Area(outline) / 1e6;

        return found.Select(entry =>
        {
            var zone = MepSystems.MemberOf(document, entry.Component, SystemKind.Heating);
            var (flow, back) = zone is null ? (BoilerFlow, BoilerFlow - BoilerDrop) : Design(document, zone);
            if (entry.Type!.Form != ComponentForm.UnderfloorHeating)
                return new EmitterOutput(entry.Component, entry.Type, zone, flow, back, RadiatorOutput(entry.Type, flow, back, temperature));

            var share = area / floors;
            var flux = FloorFlux(entry.Type, flow, back, temperature);
            return new EmitterOutput(entry.Component, entry.Type, zone, flow, back, flux * share)
            {
                FloorArea = share, Flux = flux, SurfaceTemperature = SurfaceTemperature(flux, temperature)
            };
        }).ToList();
    }

    /// <summary>What a radiator or a loop gives at its zone's temperatures, for its Properties; null for one in no room or no heated one.</summary>
    public static EmitterOutput? OutputOf(BimDocument document, Component component)
    {
        var room = document.All<Room>().FirstOrDefault(each => each.LevelId == component.LevelId
            && each.GetBoundary(document) is { IsEnclosed: true } boundary && Polygon2D.Contains(boundary.Polygon, component.Location));
        if (room is null || HeatLoss.Temperature(document, room) is not { } temperature) return null;
        return In(document, room, room.GetBoundary(document).Polygon, temperature).FirstOrDefault(output => ReferenceEquals(output.Component, component));
    }

    /// <summary>What heats a room, against what it loses - as its Properties say it.</summary>
    public static string Verdict(BimDocument document, Room room, RoomHeatLoss loss)
    {
        if (loss.Temperature is not { } temperature) return "Not heated";
        var emitters = In(document, room, room.GetBoundary(document).Polygon, temperature);
        if (emitters.Count == 0) return $"Nothing - it loses {loss.Total:#,0} W";

        var given = emitters.Sum(emitter => emitter.Watts);
        var at = string.Join(", ", emitters.Select(emitter => $"{emitter.Flow:0}/{emitter.Return:0}").Distinct());
        var said = $"{Count(emitters)} giving {given:#,0} W at {at} °C";
        return given >= loss.Total - 0.5 ? $"{said} - enough for its {loss.Total:#,0} W" : $"{said} - {loss.Total - given:#,0} W short of its {loss.Total:#,0} W";
    }

    private static string Count(IReadOnlyList<EmitterOutput> emitters)
    {
        var parts = emitters.GroupBy(emitter => emitter.Type.Form switch
        {
            ComponentForm.UnderfloorHeating => "underfloor loop",
            ComponentForm.TowelRail => "towel rail",
            _ => "radiator"
        }).Select(group => $"{group.Count()} {group.Key}{(group.Count() == 1 ? string.Empty : "s")}");
        return string.Join(" and ", parts);
    }

    /// <summary>The heat a source has to give, W, and the rooms it heats: the whole building's load where it heats every room that is heated, else its rooms' own.</summary>
    public static (double Load, IReadOnlyList<RoomHeatLoss> Rooms) LoadOn(BimDocument document, Component source, BuildingHeatLoss building)
    {
        var zones = MepSystems.FedBy(document, source, SystemKind.Heating);
        var rooms = building.Rooms.Where(loss => loss.Heated && In(document, loss.Room, loss.Room.GetBoundary(document).Polygon, loss.Temperature!.Value)
            .Any(emitter => emitter.Zone is { } zone && zones.Contains(zone))).ToList();
        var heated = building.Rooms.Where(loss => loss.Heated && loss.Total > 0).ToList();
        var all = heated.Count > 0 && heated.All(loss => rooms.Contains(loss));
        return (all ? building.Total : rooms.Sum(loss => loss.Total), rooms);
    }

    /// <summary>A heating zone's heat load, as its Properties say it.</summary>
    public static string Describe(BimDocument document, MepSystem zone)
    {
        var (flow, back) = Design(document, zone);
        var emitters = MepSystems.MembersOf(document, zone).Select(member => OutputOf(document, member)).OfType<EmitterOutput>().ToList();
        return emitters.Count == 0
            ? $"{flow:0}/{back:0} °C - nothing on it heating a room yet"
            : $"{flow:0}/{back:0} °C - {Count(emitters)} giving {emitters.Sum(emitter => emitter.Watts):#,0} W";
    }

    /// <summary>The home's hot water a day, litres, and the people it is reckoned for: the bedrooms and one more, or the occupants if more (MIS 3005-D 5.6.2).</summary>
    public static (double Litres, int People) HotWaterDemand(BimDocument document)
    {
        var rooms = document.All<Room>().Select(room => (Room: room, Boundary: room.GetBoundary(document))).Where(entry => entry.Boundary.IsEnclosed).ToList();
        var bedrooms = rooms.Count(entry => Bedrooms.Of(document, entry.Room, entry.Boundary.Polygon) is not null || entry.Room.Name.Contains("bedroom", StringComparison.OrdinalIgnoreCase));
        var people = Math.Max(bedrooms + 1, rooms.Sum(entry => entry.Room.OccupantCount));
        return (HotWaterPerPerson * people, people);
    }

    private const string NoDesignTemperature = "no winter design temperature is set for the project - IRC R303.10 has the heating hold 68 °F at the design temperature of Table R301.2";

    /// <summary>What is wanting in one heating zone itself, as the whole heating's problems say it - in the US no design temperature to hold to, said of the first zone; or designed to flow too hot - without working out the heat loss.</summary>
    public static IEnumerable<MepProblem> ZoneProblems(BimDocument document, MepSystem zone)
    {
        var heatingZones = document.All<MepSystem>().Where(system => system.Kind == SystemKind.Heating).ToList();
        if (!heatingZones.Contains(zone)) yield break;
        if (Climate.DesignOutside(document) is null)
        {
            if (document.ProjectInformation.ClearanceStandard.IsUs() && ReferenceEquals(heatingZones[0], zone)) yield return new MepProblem(zone, NoDesignTemperature);
            yield break;
        }

        if (!document.ProjectInformation.ClearanceStandard.IsUs() && TooHot(document, zone) is { } said) yield return new MepProblem(zone, said);
    }

    /// <summary>A zone heating rooms designed to flow over 55 °C, as Approved Document L says it; null where it is not.</summary>
    private static string? TooHot(BimDocument document, MepSystem zone) =>
        MepSystems.MembersOf(document, zone).Any(member => document.FindType<ComponentType>(member.TypeId) is { } type && IsEmitter(type.Form)) && Design(document, zone).Flow > FlowLimit + 0.01
            ? $"it is designed to flow at {Design(document, zone).Flow:0} °C - Approved Document L (5.10) sizes a new wet heating system's pipes and emitters to run at 55 °C or lower"
            : null;

    /// <summary>What is wanting in the heating: rooms short of heat or with none, zones run too hot, heat sources too small, floors asked too much of.</summary>
    public static IEnumerable<MepProblem> Problems(BimDocument document)
    {
        var heatingZones = document.All<MepSystem>().Where(system => system.Kind == SystemKind.Heating).ToList();
        var emitting = document.All<Component>().Any(component => document.FindType<ComponentType>(component.TypeId) is { } type && IsEmitter(type.Form));
        if (heatingZones.Count == 0 && !emitting) yield break;

        var us = document.ProjectInformation.ClearanceStandard.IsUs();
        var building = HeatLoss.Of(document);
        if (building is null)
        {
            if (us) yield return new MepProblem(heatingZones.FirstOrDefault() as Element ?? document.All<Component>().First(), NoDesignTemperature);
            yield break;
        }

        var rule = us
            ? "IRC R303.10 has the heating hold 68 °F in each habitable room at the winter design temperature"
            : "BS EN 12831-1 works out each room's heat loss at the design conditions, and MCS MIS 3005-D sizes its emitters to it";
        foreach (var loss in building.Rooms.Where(loss => loss.Heated && loss.Total > 0.5))
        {
            var emitters = In(document, loss.Room, loss.Room.GetBoundary(document).Polygon, loss.Temperature!.Value);
            if (emitters.Count == 0)
            {
                if (heatingZones.Count > 0) yield return new MepProblem(loss.Room, $"nothing heats it - it loses {loss.Total:#,0} W at the design conditions; {rule}");
                continue;
            }

            var given = emitters.Sum(emitter => emitter.Watts);
            if (given < loss.Total - 0.5)
                yield return new MepProblem(loss.Room,
                    $"what heats it gives {given:#,0} W at {string.Join(", ", emitters.Select(emitter => $"{emitter.Flow:0}/{emitter.Return:0}").Distinct())} °C - it loses {loss.Total:#,0} W; {rule}");

            foreach (var floor in emitters.Where(emitter => emitter.Floor))
            {
                if (floor.Flux >= FloorLimit(floor.Type) - 0.01 && given < loss.Total - 0.5)
                    yield return new MepProblem(floor.Component,
                        $"its floor gives {floor.Flux:0} W/m² at most - BS EN 1264 keeps a floor's surface within 9 K of the room where people are - so the rest of the room's heat wants another emitter");
                if (floor.Type.PipeSpacing > 0 && floor.FloorArea * 1e6 / floor.Type.PipeSpacing > LongestLoop)
                    yield return new MepProblem(floor.Component,
                        $"its {floor.FloorArea:0.0} m² takes about {floor.FloorArea / (floor.Type.PipeSpacing / 1000):0} m of pipe at {floor.Type.PipeSpacing:0} mm centres - more than the 100 m a loop is kept to; it wants {Math.Ceiling(floor.FloorArea * 1e6 / floor.Type.PipeSpacing / LongestLoop):0} loops");
                if (floor.Flow > ScreedFlow + 0.01)
                    yield return new MepProblem(floor.Component, $"its water flows at {floor.Flow:0} °C - over a cement screed, the flow is kept to 55 °C at most");
            }
        }

        // The zones' temperatures: 55 °C at most for a new system.
        if (!us)
            foreach (var zone in heatingZones)
                if (TooHot(document, zone) is { } said)
                    yield return new MepProblem(zone, said);

        // Each heat source against what it heats.
        foreach (var source in heatingZones.Select(zone => MepSystems.SourceOf(document, zone)).OfType<Component>().Distinct())
        {
            if (document.FindType<ComponentType>(source.TypeId) is not { } type || type.Form is not (ComponentForm.Boiler or ComponentForm.HeatPump)) continue;
            var (load, rooms) = LoadOn(document, source, building);
            if (rooms.Count == 0) continue;
            if (type.HeatOutput <= 0)
            {
                yield return new MepProblem(source, $"its heat output is not set - it heats rooms losing {load:#,0} W at the design conditions");
                continue;
            }

            if (type.HeatOutput < load - 0.5)
                yield return new MepProblem(source, type.Form == ComponentForm.HeatPump
                    ? $"it gives {type.HeatOutput:#,0} W against a heat load of {load:#,0} W - MCS MIS 3005-D (5.5.1 e) and Approved Document L (5.12) select a heat pump to give the whole of the heat load at the design temperature, with no supplementary electric heater"
                    : $"it gives {type.HeatOutput:#,0} W against a heat load of {load:#,0} W - Approved Document L (5.8) bases a heating system on the dwelling's heat loss");
        }
    }

    // ---- sizing radiators ------------------------------------------------------

    /// <summary>
    /// The panel radiators Size Radiators chooses from: Stelrad's Classic Compact, single (type 11,
    /// K1) and double panel double convector (type 22, K2), 450 and 600 high, at their BS EN 442
    /// outputs and exponents as tested by CETIAT.
    /// </summary>
    public static IReadOnlyList<ComponentType> RadiatorRange()
    {
        var range = new List<ComponentType>();
        void Add(int panels, double height, double exponent, double depth, params (double Length, double Watts)[] sizes)
        {
            foreach (var (length, watts) in sizes)
                range.Add(new ComponentType($"Radiator - Type {panels} {height:0} x {length:0}", BuiltInCategory.MechanicalEquipment, ComponentForm.Radiator, length, depth, height)
                {
                    TypeMark = $"R{panels}", Colour = new ColourRgb(0xF4, 0xF4, 0xF2), Cost = Math.Round((panels == 22 ? 120m : 85m) * (decimal)Math.Max(0.5, length / 1000)),
                    Placement = ComponentPlacement.FaceBased, DefaultElevation = 100, HeatOutput = watts, HeatExponent = exponent
                });
        }

        Add(11, 450, 1.31, 61, (400, 302), (600, 454), (800, 605), (1000, 756));
        Add(11, 600, 1.29, 61, (400, 392), (600, 588), (800, 784), (1000, 980));
        Add(22, 450, 1.33, 100, (400, 548), (600, 823), (800, 1097), (1000, 1371), (1200, 1645), (1400, 1919), (1600, 2194), (1800, 2468));
        Add(22, 600, 1.33, 100, (400, 693), (600, 1039), (800, 1386), (1000, 1732), (1200, 2078), (1400, 2425), (1600, 2771), (1800, 3118));
        return range;
    }

    /// <summary>
    /// Each room's radiators sized to its heat loss at its zone's temperatures: what its floors do
    /// not give shared among them as they share it now, each made the shortest radiator of the range
    /// as high as it is that gives its share - a single panel where one does, else a double. Towel
    /// rails stay as they are and count. A type the range has that the project has not is added.
    /// </summary>
    public static (IUndoableCommand? Command, int Changed, IReadOnlyList<string> Short) SizeRadiators(BimDocument document)
    {
        var building = HeatLoss.Of(document);
        if (building is null) return (null, 0, new[] { "No design temperature outside to size to" });

        var range = RadiatorRange();
        var chosen = new Dictionary<Component, ComponentType>();
        var unmet = new List<string>();
        foreach (var loss in building.Rooms.Where(loss => loss.Heated && loss.Total > 0.5))
        {
            var emitters = In(document, loss.Room, loss.Room.GetBoundary(document).Polygon, loss.Temperature!.Value);
            var radiators = emitters.Where(emitter => emitter.Type.Form == ComponentForm.Radiator).ToList();
            if (radiators.Count == 0) continue;

            var needed = loss.Total - emitters.Where(emitter => emitter.Type.Form != ComponentForm.Radiator).Sum(emitter => emitter.Watts);
            if (needed <= 0) continue;
            var now = radiators.Sum(radiator => radiator.Watts);
            foreach (var radiator in radiators)
            {
                var share = now > 0 ? needed * radiator.Watts / now : needed / radiators.Count;
                var tall = range.Select(type => type.Height).Distinct().OrderBy(height => Math.Abs(height - radiator.Type.Height)).First();
                var fits = range.Where(type => type.Height == tall)
                    .Select(type => (Type: type, Watts: RadiatorOutput(type, radiator.Flow, radiator.Return, loss.Temperature!.Value)))
                    .Where(entry => entry.Watts >= share - 0.5)
                    .OrderBy(entry => entry.Type.Width).ThenBy(entry => entry.Type.Depth)
                    .FirstOrDefault();
                if (fits.Type is null)
                {
                    unmet.Add($"{loss.Name}: {share:#,0} W is more than one radiator {tall:0} high gives at {radiator.Flow:0}/{radiator.Return:0} °C");
                    continue;
                }

                if (Same(fits.Type, radiator.Type)) continue;
                chosen[radiator.Component] = fits.Type;
            }
        }

        if (chosen.Count == 0) return (null, 0, unmet);

        // The range's types the project has already - by what they are, whatever they are called - else added.
        var steps = new List<IUndoableCommand>();
        var moves = new List<(Point3D From, Point3D To)>();
        foreach (var group in chosen.GroupBy(entry => entry.Value.Name))
        {
            var wanted = group.First().Value;
            var type = document.TypesOf<ComponentType>().FirstOrDefault(existing => Same(existing, wanted));
            if (type is null)
            {
                type = wanted;
                var add = new AddTypeCommand(document, type);
                add.Redo();
                steps.Add(add);
            }

            var radiators = group.Select(entry => entry.Key).ToList();
            var before = radiators.ToDictionary(radiator => radiator, radiator => Connectors.Placed(document, radiator).Select(placed => placed.At).ToList());
            var set = new SetElementsTypeCommand(radiators.Cast<Element>(), type);
            set.Redo();
            steps.Add(set);

            // A wider radiator's tappings are further apart: where they now are.
            foreach (var radiator in radiators)
            {
                var now = Connectors.Placed(document, radiator).Select(placed => placed.At).ToList();
                for (var i = 0; i < Math.Min(now.Count, before[radiator].Count); i++)
                    if (Math.Abs(now[i].X - before[radiator][i].X) + Math.Abs(now[i].Y - before[radiator][i].Y) + Math.Abs(now[i].Z - before[radiator][i].Z) > 0.5)
                        moves.Add((before[radiator][i], now[i]));
            }
        }

        // The pipes to them carried with their tappings.
        if (PipeNetworks.Follow(document, moves) is { Count: > 0 } followed)
        {
            var carry = new MovePipesCommand(followed);
            carry.Redo();
            steps.Add(carry);
        }

        return (new CompositeCommand("Size Radiators", steps), chosen.Count, unmet);
    }

    private static bool Same(ComponentType a, ComponentType b) =>
        a.Form == b.Form && Math.Abs(a.Width - b.Width) < 0.5 && Math.Abs(a.Depth - b.Depth) < 0.5 && Math.Abs(a.HeatOutput - b.HeatOutput) < 0.5 && Math.Abs(a.HeatExponent - b.HeatExponent) < 0.005;
}
