using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Mep;

/// <summary>Where the heat through a surface goes: outside, into the ground, into a space nobody heats, or into a room kept at another temperature.</summary>
public enum HeatPath
{
    Outside,
    Ground,
    Unheated,
    Room
}

/// <summary>What a surface of a room is: what its gains in summer go by as much as its losses in winter.</summary>
public enum HeatSurface
{
    Wall,
    Window,
    Door,

    /// <summary>A roof over the room itself - flat, or sloping as a cathedral ceiling.</summary>
    Roof,

    /// <summary>A ceiling with a loft or an attic between it and the roof.</summary>
    CeilingUnderLoft,

    /// <summary>A ground floor on the ground, and one suspended over a void.</summary>
    GroundFloor,
    SuspendedFloor,

    /// <summary>A floor over, or a ceiling under, another space - a room, a space not heated, or the open air.</summary>
    Floor,
    Ceiling,

    /// <summary>A US slab on the ground, losing its heat at its exposed edge: its "area" the edge's length, m, its "U" W/m·K.</summary>
    SlabEdge
}

/// <summary>
/// One surface of a room and the heat through it: its area (m²), its U-value with the allowance for
/// thermal bridges, what is beyond and at what temperature - and what it is, and for a wall, a window
/// or a door the way it faces on the plan, degrees clockwise from up the plan.
/// </summary>
public sealed record HeatLossLine(string What, double Area, double U, HeatPath Path, double Beyond, double Watts, Element? Through, Room? Next = null,
    HeatSurface Kind = HeatSurface.Wall, double? Facing = null);

/// <summary>
/// One room's heat loss at the design conditions: through each surface, and with the air coming in.
/// Volume in m³; envelope - the area of it facing outside or the ground - in m²; air in m³/h.
/// </summary>
public sealed record RoomHeatLoss(
    Room Room, string Name, double? Temperature, IReadOnlyList<HeatLossLine> Lines, double Area, double Volume, double Envelope,
    double Leakage, double Minimum, double AirFlow, double Ventilation, IReadOnlyList<string> Notes)
{
    public bool Heated => Temperature is not null;

    /// <summary>Through its surfaces, W.</summary>
    public double Fabric => Lines.Sum(line => line.Watts);

    /// <summary>What its heating has to make up, W: its surfaces and its air.</summary>
    public double Total => Fabric + Ventilation;

    /// <summary>What it loses out of the heated building - outside, to the ground and to unheated spaces, not to the rooms round it - W.</summary>
    public double Lost => Lines.Where(line => line.Path != HeatPath.Room).Sum(line => line.Watts);

    /// <summary>Air supplied to it by a heat recovery unit, m³/h, and how warm it comes in, °C.</summary>
    public double Supply { get; init; }

    public double SupplyTemperature { get; init; }
}

/// <summary>The whole building's heat loss: each room's, and what the building loses all together.</summary>
public sealed record BuildingHeatLoss(double Outside, double Ground, double AirPermeability, IReadOnlyList<RoomHeatLoss> Rooms, double Ventilation)
{
    /// <summary>Ventilated with heat recovery: the unit's efficiency, and how warm its supply comes in at the design conditions, °C.</summary>
    public double? Recovery { get; init; }

    public double? SupplyTemperature { get; init; }

    /// <summary>The air extracted mechanically that no supply makes up, drawn in through the envelope instead, m³/h.</summary>
    public double Extracted { get; init; }

    /// <summary>What leaves the heated building through its surfaces, W.</summary>
    public double Fabric => Rooms.Where(room => room.Heated).Sum(room => room.Lost);

    /// <summary>What a US house's ducts lose outside what is heated, as a share of the rest (ASHRAE Table 6).</summary>
    public double DuctFactor { get; init; }

    /// <summary>What its ducts lose, W.</summary>
    public double Distribution => DuctFactor * (Fabric + Ventilation);

    /// <summary>A US house's air: its infiltration, with its ventilation, as ASHRAE's residential method takes it; none in England.</summary>
    public ResidentialAir.Flow? Air { get; init; }

    /// <summary>The building's design heat load, W: what its heat sources have to give.</summary>
    public double Total => Fabric + Ventilation + Distribution;

    public RoomHeatLoss? Of(Room room) => Rooms.FirstOrDefault(each => ReferenceEquals(each.Room, room));
}

/// <summary>
/// Heat loss room by room, as BS EN 12831-1:2017 works it and MCS MIS 3005-D asks.
///
/// Each room at its design temperature: in the UK a new home's - 21 °C, a bathroom or shower room
/// 22 - as CIBSE's Domestic Heating Design Guide (2026, Table 2-2) gives them for age band K on and
/// MCS's heat load calculator uses (at least MIS 3005-D Table 1's), and in the US 68 °F (IRC R303.10);
/// a garage, a loft or a porch not heated. Through each surface, its area times its U-value - with
/// the thermal bridges allowed for as MCS's calculator does: 0.08 W/m²K on what faces outside or
/// the ground (RdSAP 10 Table 21), 0.05 on what faces another space (the Guide's Table 2-9) - times
/// the difference to what is beyond: outside, the design temperature; under a solid ground floor,
/// the ground at the annual mean (MIS 3005-D 5.5.1 c) and under a suspended one, outside (d); a
/// neighbour's or a space nobody heats at the annual mean too, as the Guide takes an adjoining
/// dwelling unheated; another room, its own temperature.
///
/// The air, by the standard's own method (6.3.3): the envelope's air permeability at 50 Pa - the
/// project's, 8 m³/h·m² at most for a new home (Approved Document L Table 4.1) - times 0.05 for
/// the ordinary flow (Table B.8) over the room's share of the envelope, doubled for a room facing
/// the wind (f_dir, B.2.14) but never more than the whole home's; at least half an air change an
/// hour where the room has an outside wall (the Guide's Table 2-18). The home's own total takes
/// each room's leakage undoubled, and half its least air change (f_i-z). Each m³/h of air at
/// 0.34 W a kelvin. Ventilated with heat recovery, each room's supply and extract run continuously
/// as Approved Document F shares them out, the least air change made up by them first, and the air
/// supplied comes in at the outside temperature raised by the unit's efficiency of the way to the
/// extract's (6.3.3.7) - the extract and the supply taken as balanced.
/// </summary>
public static class HeatLoss
{
    /// <summary>Thermal bridges, W/m²K added to the U-value: of what faces outside or the ground, and of what faces another space.</summary>
    public const double ExternalBridging = 0.08, InternalBridging = 0.05;

    /// <summary>What a cubic metre of air an hour carries a kelvin, W (ρc_p).</summary>
    public const double Air = 0.34;

    /// <summary>The ordinary flow through the envelope as a share of the flow at 50 Pa (f_qv,z); and how much more a room facing the wind takes (f_dir).</summary>
    public const double FlowFactor = 0.05, Orientation = 2;

    /// <summary>How much an unbalanced ventilation's pressure cuts the ordinary leakage (f_fac), for a home with more than one exposed facade.</summary>
    public const double UnbalancedFactor = 8;

    /// <summary>The least air change an hour of a room with an outside wall, and the share of it the whole home counts (f_i-z).</summary>
    public const double LeastAirChange = 0.5, ZoneShare = 0.5;

    /// <summary>The most air permeability Approved Document L lets a new dwelling have, m³/h·m² at 50 Pa (Table 4.1).</summary>
    public const double LimitingAirPermeability = 8.0;

    private static readonly string[] UnheatedWords = { "garage", "carport", "porch", "shed", "outhouse", "unheated", "bin store", "cycle store", "bike store" };
    private static readonly string[] UnheatedNames = { "loft", "attic", "loft space", "roof space", "void" };
    private static readonly string[] WetWords = { "bath", "shower", "en-suite", "ensuite", "en suite", "wet room" };

    /// <summary>The temperature a room is kept at by its use, °C - null for one not heated.</summary>
    public static double? ByUse(BimDocument document, Room room)
    {
        // Whole words: a shed, not a furnished room.
        var name = room.Name.Trim().ToLowerInvariant();
        if (UnheatedWords.Any(word => System.Text.RegularExpressions.Regex.IsMatch(name, $@"\b{System.Text.RegularExpressions.Regex.Escape(word)}\b")) || UnheatedNames.Contains(name)) return null;
        if (document.ProjectInformation.ClearanceStandard.IsUs()) return 20;
        return WetWords.Any(name.Contains) ? 22 : 21;
    }

    /// <summary>The temperature a room is heated to, °C: what is set for it, or what its use asks; null for one not heated.</summary>
    public static double? Temperature(BimDocument document, Room room) =>
        room.Unheated ? null : room.DesignTemperature ?? ByUse(document, room);

    /// <summary>The building's heat loss, room by room; null where there is no design temperature outside to work to.</summary>
    public static BuildingHeatLoss? Of(BimDocument document) =>
        ModelCache.Of(document) is { } kept ? kept.Keep("heat loss", document, null, null, () => Work(document)) : Work(document);

    /// <summary>
    /// The building's rooms and their surfaces as the heat loss finds them, with or without a winter
    /// design temperature set - worked to freezing where there is none: what a cooling load starts from.
    /// </summary>
    public static BuildingHeatLoss? Surveyed(BimDocument document) =>
        Climate.DesignOutside(document) is not null ? Of(document)
        : ModelCache.Of(document) is { } kept ? kept.Keep("heat loss survey", document, null, null, () => Work(document, 0)) : Work(document, 0);

    /// <summary>A slab's heat loss a metre of its exposed edge a kelvin, W/m·K: ASHRAE's F_p of 0.68 Btu/h·ft·°F uninsulated and 0.50 insulated (Fundamentals chapter 18, Table 24, a block wall).</summary>
    public const double SlabEdgeUninsulated = 0.68 * CoolingLoads.WattsPerMetreKelvinPerBtuFootF, SlabEdgeInsulated = 0.50 * CoolingLoads.WattsPerMetreKelvinPerBtuFootF;

    private static BuildingHeatLoss? Work(BimDocument document, double? survey = null)
    {
        if ((Climate.DesignOutside(document) ?? survey) is not { } outside) return null;
        var us = document.ProjectInformation.ClearanceStandard.IsUs();
        var ground = Climate.Ground(document) ?? outside;
        var permeability = document.ProjectInformation.AirPermeability;

        using var cache = ModelCache.Begin(document);
        var rooms = document.All<Room>()
            .Select(room => (Room: room, Boundary: room.GetBoundary(document)))
            .Where(entry => entry.Boundary.IsEnclosed && entry.Boundary.Polygon.Count >= 3)
            .ToList();
        var temperatures = rooms.ToDictionary(entry => entry.Room, entry => Temperature(document, entry.Room));
        var levels = document.Levels.OrderBy(level => level.Elevation).ToList();

        Room? RoomAt(Guid level, Point2D at, Room except) =>
            rooms.FirstOrDefault(entry => entry.Room.LevelId == level && !ReferenceEquals(entry.Room, except) && Polygon2D.Contains(entry.Boundary.Polygon, at)).Room;

        var surveyed = new List<(Room Room, RoomBoundaryResult Boundary, List<HeatLossLine> Lines, List<string> Notes, double Volume)>();
        foreach (var (room, boundary) in rooms)
        {
            var lines = new List<HeatLossLine>();
            var notes = new List<string>();
            var volume = (RoomHeadroom.Measure(document, room)?.Volume ?? boundary.Area * room.Height) / 1e9;
            if (temperatures[room] is { } inside)
                Surfaces(document, room, boundary, inside, outside, ground, volume, levels, temperatures, RoomAt, lines, notes, us);
            surveyed.Add((room, boundary, lines, notes, volume));
        }

        // The air: each room's share of the leakage through the envelope, the whole home's at the most.
        double Envelope(List<HeatLossLine> lines) => lines.Where(line => line.Path is HeatPath.Outside or HeatPath.Ground).Sum(line => line.Area);
        var heated = surveyed.Where(entry => temperatures[entry.Room] is not null).ToList();
        var envelopeZone = heated.Sum(entry => Envelope(entry.Lines));
        var share = heated.Count > 1 ? ZoneShare : 1;

        // Ventilated with heat recovery: each room's air supplied and extracted continuously, the supply coming
        // in at the outside temperature raised by the unit's share of the way to the extract's (6.3.3.7).
        var recovery = Ventilation.ForHeatLoss(document);
        var extracted = recovery?.Rooms.Where(entry => entry.Value.Extract > 0 && temperatures.GetValueOrDefault(entry.Key) is not null).ToList();
        var exhaust = extracted is { Count: > 0 } ? extracted.Sum(entry => entry.Value.Extract * temperatures[entry.Key]!.Value) / extracted.Sum(entry => entry.Value.Extract) : (double?)null;
        var supplied = recovery is { } unit && exhaust is { } warm ? outside + unit.Efficiency * (warm - outside) : outside;

        // The air through the envelope (6.3.3.3): the extract the supply does not make up drawn in through it, and the
        // ordinary leakage - less the more unbalanced the ventilation (f_e, with f_fac 8 for more than one exposed facade).
        var extractZone = recovery?.Rooms.Where(entry => temperatures.GetValueOrDefault(entry.Key) is not null).Sum(entry => entry.Value.Extract) ?? 0;
        var supplyZone = recovery?.Rooms.Where(entry => temperatures.GetValueOrDefault(entry.Key) is not null).Sum(entry => entry.Value.Supply) ?? 0;
        var leakage50 = permeability * envelopeZone;
        var unbalanced = leakage50 > 0 ? (extractZone - supplyZone) / leakage50 : 0;
        var additional = leakage50 * FlowFactor / (1 + UnbalancedFactor / FlowFactor * unbalanced * unbalanced);
        var zone = Math.Max(extractZone - supplyZone, 0) + additional;

        double building = 0;
        var results = new List<RoomHeatLoss>();
        foreach (var (room, boundary, lines, notes, volume) in surveyed)
        {
            var envelope = Envelope(lines);
            var temperature = temperatures[room];
            var leakage = envelopeZone > 0 ? zone * envelope / envelopeZone : 0;
            var minimum = envelope > 0 ? LeastAirChange * volume : 0;
            var (supply, extract) = recovery?.Rooms.GetValueOrDefault(room) ?? (0, 0);
            var technical = Math.Max(supply, extract);
            var drawn = zone > 0 ? additional / zone * Math.Min(zone, Orientation * leakage) + (zone - additional) / zone * leakage : 0;
            var flow = Math.Max(drawn, minimum - technical);
            var difference = temperature is { } t ? t - outside : 0;
            var warmed = temperature is { } inside ? supply * (inside - supplied) : 0;
            if (temperature is not null) building += Air * (Math.Max(leakage, share * minimum - technical) * difference + warmed);
            results.Add(new RoomHeatLoss(room, room.ToString(), temperature, lines, boundary.Area / 1e6, volume, envelope,
                leakage, minimum, temperature is null ? 0 : flow, temperature is null ? 0 : Air * (flow * difference + warmed), notes)
            {
                Supply = temperature is null ? 0 : supply, SupplyTemperature = supplied
            });
        }

        var balanced = recovery is not null && supplyZone > 0;
        if (us) return UsAir(document, surveyed.Select(entry => (entry.Room, entry.Boundary, entry.Lines, entry.Notes, entry.Volume)).ToList(), temperatures, outside, ground, recovery, balanced, supplied);
        return new BuildingHeatLoss(outside, ground, permeability, results, building)
        {
            Recovery = balanced ? recovery!.Value.Efficiency : null, SupplyTemperature = balanced ? supplied : null, Extracted = Math.Max(extractZone - supplyZone, 0)
        };
    }

    /// <summary>
    /// A US house's air, as ASHRAE's residential method takes it: its infiltration through the
    /// leakage area its air leakage gives, at the heating's driving force, with its ventilation - each
    /// room the house's air change, its share by its volume; and its ducts' losses outside what is heated.
    /// </summary>
    private static BuildingHeatLoss UsAir(
        BimDocument document, List<(Room Room, RoomBoundaryResult Boundary, List<HeatLossLine> Lines, List<string> Notes, double Volume)> surveyed,
        IReadOnlyDictionary<Room, double?> temperatures, double outside, double ground,
        (double Efficiency, IReadOnlyDictionary<Room, (double Supply, double Extract)> Rooms)? recovery, bool balanced, double supplied)
    {
        var heated = surveyed.Where(entry => temperatures[entry.Room] is not null).ToList();
        var inside = heated.Count == 0 ? 20 : heated.Average(entry => temperatures[entry.Room]!.Value);
        var stack = ResidentialAir.StackFeet(document, heated.Select(entry => entry.Room));
        var air = ResidentialAir.Of(document, heated.SelectMany(entry => entry.Lines), stack, inside - outside, cooling: false, recovery,
            room => temperatures.GetValueOrDefault(room) is not null);
        var factor = ResidentialAir.Sensible * ResidentialAir.AltitudeFactor(document);
        var volume = heated.Sum(entry => entry.Volume);

        var results = new List<RoomHeatLoss>();
        double building = 0;
        foreach (var (room, boundary, lines, notes, roomVolume) in surveyed)
        {
            var temperature = temperatures[room];
            var share = temperature is not null && volume > 0 ? roomVolume / volume : 0;
            var watts = temperature is { } t ? factor * share * air.SensibleFlow * (t - outside) : 0;
            building += watts;
            results.Add(new RoomHeatLoss(room, room.ToString(), temperature, lines, boundary.Area / 1e6, roomVolume,
                lines.Where(line => line.Path is HeatPath.Outside or HeatPath.Ground).Sum(line => line.Kind == HeatSurface.SlabEdge ? 0 : line.Area),
                share * air.Infiltration, 0, share * air.SensibleFlow, watts, notes)
            {
                Supply = temperature is null ? 0 : recovery?.Rooms.GetValueOrDefault(room).Supply ?? 0, SupplyTemperature = supplied
            });
        }

        var storeys = heated.Select(entry => entry.Room.LevelId).Distinct().Count();
        return new BuildingHeatLoss(outside, ground, document.ProjectInformation.AirPermeability, results, building)
        {
            Recovery = balanced ? recovery!.Value.Efficiency : null, SupplyTemperature = balanced ? supplied : null, Air = air,
            DuctFactor = ResidentialAir.DuctFactor(document, cooling: false, storeys)
        };
    }

    /// <summary>A room's heat loss on its own - the building's worked out for it.</summary>
    public static RoomHeatLoss? OfRoom(BimDocument document, Room room) => Of(document)?.Of(room);

    private static void Surfaces(
        BimDocument document, Room room, RoomBoundaryResult boundary, double inside, double outside, double ground, double volume,
        IReadOnlyList<Datums.Level> levels, IReadOnlyDictionary<Room, double?> temperatures, Func<Guid, Point2D, Room, Room?> roomAt,
        List<HeatLossLine> lines, List<string> notes, bool us)
    {
        // How much of its floor's edge is exposed: along outside walls and walls to spaces not heated, as ASHRAE counts it for a slab.
        var edge = 0.0;
        var polygon = boundary.Polygon;
        var area = boundary.Area / 1e6;
        var centre = boundary.Centroid;
        var level = levels.FirstOrDefault(each => each.Id == room.LevelId);
        var floorLevel = (level?.Elevation ?? 0) + room.BaseOffset;
        var above = level is null ? null : levels.FirstOrDefault(each => each.Elevation > level.Elevation + 1);
        var below = level is null ? null : levels.LastOrDefault(each => each.Elevation < level.Elevation - 1);

        // The walls' height: storey to storey, as a net internal height with the floor between; under the roof, the room's own.
        var storey = above is not null && level is not null ? above.Elevation - level.Elevation : area > 0 ? volume / area * 1000 : room.Height;

        void Add(string what, double square, double u, HeatPath path, double beyond, Element? through, Room? next = null, HeatSurface kind = HeatSurface.Wall, double? facing = null)
        {
            if (square <= 1e-6) return;
            // A US window's or door's rated U-factor is its whole product's, frame and all; a slab's edge is reckoned on its own.
            var bridged = us && kind is HeatSurface.Window or HeatSurface.Door or HeatSurface.SlabEdge ? u
                : u + (path is HeatPath.Outside or HeatPath.Ground ? ExternalBridging : InternalBridging);
            lines.Add(new HeatLossLine(what, square, bridged, path, beyond, square * bridged * (inside - beyond), through, next, kind, facing));
        }

        // What is beyond a surface: another room at its temperature, or a space nobody heats.
        (HeatPath Path, double Beyond, Room? Next)? Across(Room? next, HeatPath otherwise)
        {
            if (next is null) return (otherwise, otherwise == HeatPath.Outside ? outside : ground, null);
            if (temperatures.GetValueOrDefault(next) is not { } theirs) return (HeatPath.Unheated, ground, next);
            return Math.Abs(theirs - inside) < 0.05 ? null : (HeatPath.Room, theirs, next);
        }

        // Its walls, side by side round it: what each faces, and the windows and doors in it.
        for (var i = 0; i < polygon.Count && i < boundary.Sides.Count; i++)
        {
            if (boundary.Sides[i] is not { } wall) continue;
            var from = polygon[i];
            var to = polygon[(i + 1) % polygon.Count];
            var length = from.DistanceTo(to);
            if (length < 1) continue;

            var along = (to - from) * (1 / length);
            var outward = new Vector2D(along.Y, -along.X);
            var facing = (Math.Atan2(outward.X, outward.Y) * 180 / Math.PI + 360) % 360;
            var curtain = document.FindType<CurtainWallType>(wall.TypeId);
            var type = document.GetWallType(wall);
            var width = type?.Width ?? 0;
            var middle = new Point2D((from.X + to.X) / 2, (from.Y + to.Y) / 2);
            var exterior = curtain is not null || type?.Function == WallFunction.Exterior;
            var next = roomAt(room.LevelId, middle + outward * (width + 150), room);
            if (Across(next, exterior ? HeatPath.Outside : HeatPath.Unheated) is not { } faces) continue;
            var (path, beyond, neighbour) = faces;
            if (path is HeatPath.Outside or HeatPath.Unheated) edge += length;

            var gross = length * storey / 1e6;
            double? wallU = curtain is not null ? curtain.HeatTransferCoefficient : type is null ? null : WallRules.UValue(document, type);
            var openings = 0.0;
            if (curtain is null && type is not null)
                foreach (var opening in WallOpenings.Of(document, wall))
                {
                    if (document.FindType<OpeningType>(opening.TypeId) is not { } openingType) continue;
                    var (spanFrom, spanTo) = opening.GetSpan(openingType);
                    var at = wall.PointAt(type.Structure, (spanFrom + spanTo) / 2, 0);
                    var offset = (at - from).Dot(along);
                    if (offset < -50 || offset > length + 50 || Math.Abs((at - from).Dot(outward)) > width / 2 + 100) continue;
                    if (openingType.HeatTransferCoefficient <= 0)
                    {
                        var unknown = $"{openingType.Name}'s U-value is not given - taken as the wall it is in";
                        if (!notes.Contains(unknown)) notes.Add(unknown);
                        continue;
                    }

                    var square = opening.WidthOf(openingType) * opening.HeightOf(openingType) / 1e6;
                    openings += square;
                    Add($"{(opening is Door ? "Door" : "Window")} {openingType.Name}", square, openingType.HeatTransferCoefficient, path, beyond, opening, neighbour,
                        opening is Door && !Glazed(openingType) ? HeatSurface.Door : HeatSurface.Window, facing);
                }

            var what = path switch { HeatPath.Outside => "Wall", HeatPath.Room => $"Wall to {neighbour}", _ => neighbour is null ? "Wall to a space not heated" : $"Wall to {neighbour}, not heated" };
            if (wallU is { } u) Add($"{what} ({curtain?.Name ?? type!.Name})", Math.Max(0, gross - openings), u, path, beyond, wall, neighbour,
                curtain is not null ? HeatSurface.Window : HeatSurface.Wall, facing);
            else notes.Add($"{curtain?.Name ?? type?.Name ?? "a wall"}'s U-value is not known - its layers' materials want their conductivity");
        }

        // Whether the storey below has nothing under this room's middle - no floor, no room: it overhangs, the air under it outside.
        bool Exposed(Guid levelBelow) =>
            roomAt(levelBelow, centre, room) is null && !document.All<Floor>().Any(slab => slab.LevelId == levelBelow && slab.Contains(centre));

        // Its floor: the ground under the lowest storey, or what is under it.
        var floor = document.All<Floor>().FirstOrDefault(slab => slab.LevelId == room.LevelId && slab.Contains(centre));
        var floorType = floor is null ? null : document.FindType<SlabType>(floor.TypeId);
        if (below is null)
        {
            if (floor is null) notes.Add("no floor under it - nothing lost to the ground counted");
            else if (us && !Suspended(document, floorType))
            {
                // A US slab on the ground: lost at its exposed edge (ASHRAE Fundamentals chapter 18, eq. 41).
                var insulated = floorType is not null && Thermal.UValue(document, floorType.Structure, Thermal.Flow.Downward) is < 1.0;
                Add($"Slab edge, {(insulated ? "insulated" : "uninsulated")} ({floorType?.Name})", edge / 1000, insulated ? SlabEdgeInsulated : SlabEdgeUninsulated,
                    HeatPath.Ground, outside, floor, kind: HeatSurface.SlabEdge);
            }
            else if (SlabRules.UValue(document, floor) is { } u)
            {
                var suspended = Suspended(document, floorType);
                Add($"Ground floor, {(suspended ? "suspended" : "solid")} ({floorType?.Name})", area, u, HeatPath.Ground, suspended ? outside : ground, floor,
                    kind: suspended ? HeatSurface.SuspendedFloor : HeatSurface.GroundFloor);
            }
            else notes.Add($"{floorType?.Name ?? "its floor"}'s U-value is not known");
        }
        else if (Across(roomAt(below.Id, centre, room), Exposed(below.Id) ? HeatPath.Outside : HeatPath.Unheated) is { } under)
        {
            var (path, beyond, neighbour) = under;
            if (floorType is not null && Thermal.UValue(document, floorType.Structure, Thermal.Flow.Downward) is { } u)
                Add(path == HeatPath.Room ? $"Floor over {neighbour}" : neighbour is null ? "Floor over a space not heated" : $"Floor over {neighbour}, not heated", area, u, path, beyond, floor, neighbour, HeatSurface.Floor);
            else notes.Add("the U-value of the floor under it is not known");
        }

        // What is over it: the roof - with any ceiling and its insulation - or the storey above.
        var covers = RoomHeadroom.Covers(document, room, polygon);
        var over = RoomHeadroom.Over(document, floorLevel, centre, covers);
        // A ceiling is under the roof only where no floor comes between them.
        var roof = over?.By as Roof ?? (over?.By is Ceiling
            ? covers.OfType<Roof>().Where(each => each.Contains(centre)).OrderBy(each => each.GetBottomElevation(document)).FirstOrDefault() is { } lowest
              && !covers.OfType<Floor>().Any(each => each.Contains(centre) && each.GetBottomElevation(document) < lowest.GetBottomElevation(document) - 1)
                ? lowest
                : null
            : null);
        if (roof is not null)
        {
            var sloped = over?.By is Roof && !roof.Surface(document).IsFlat;
            var square = sloped ? area / Math.Max(0.2, Math.Cos(roof.SlopeDegrees * Math.PI / 180)) : area;
            if (SlabRules.UValue(document, roof) is { } u)
                Add($"{(over?.By is Roof ? "Roof" : "Ceiling under the roof")} ({document.FindType<SlabType>(roof.TypeId)?.Name})", square, u, HeatPath.Outside, outside, roof,
                    kind: over?.By is Roof ? HeatSurface.Roof : HeatSurface.CeilingUnderLoft);
            else notes.Add("the roof's U-value is not known");
        }
        else if (covers.OfType<Floor>().Where(each => each.Contains(centre)).OrderBy(each => each.GetBottomElevation(document)).FirstOrDefault() is { } upper)
        {
            // A floor over it with no roof over that is open to the sky - a terrace, a balcony.
            var open = !document.All<Roof>().Any(each => each.Contains(centre) && each.Surface(document).HeightAt(centre) > upper.GetTopElevation(document) + 1);
            if (Across(roomAt(upper.LevelId, centre, room), open ? HeatPath.Outside : HeatPath.Unheated) is { } overhead)
            {
                var (path, beyond, neighbour) = overhead;
                if (document.FindType<SlabType>(upper.TypeId) is { } upperType && Thermal.UValue(document, upperType.Structure, Thermal.Flow.Upward) is { } u)
                    Add(path == HeatPath.Room ? $"Ceiling under {neighbour}" : neighbour is null ? "Ceiling under a space not heated" : $"Ceiling under {neighbour}, not heated", area, u, path, beyond, upper, neighbour, HeatSurface.Ceiling);
                else notes.Add("the U-value of the floor over it is not known");
            }
        }
        else notes.Add("nothing over it - no roof or floor found, so nothing lost upward is counted");
    }

    /// <summary>Whether a door is mostly glass - its gains in summer a window's.</summary>
    private static bool Glazed(OpeningType type) => type is DoorType { LeafDesign: DoorLeafDesign.Glazed or DoorLeafDesign.FrenchGlazed or DoorLeafDesign.FullGlass };

    /// <summary>
    /// Whether a ground floor is suspended over a void - timber, or beam and block - rather than
    /// solid on the ground: what MIS 3005-D works its loss to the outside for, not to the ground.
    /// </summary>
    public static bool Suspended(BimDocument document, SlabType? type)
    {
        if (type is null) return false;
        bool Says(string text, params string[] words) => words.Any(word => text.Contains(word, StringComparison.OrdinalIgnoreCase));
        if (Says(type.Name, "suspended", "timber", "joist", "beam and block", "beam & block")) return true;
        return !type.Structure.Layers.Any(layer => document.FindMaterial(layer.MaterialId)?.Name is { } name && Says(name, "concrete", "screed", "slab"));
    }
}
