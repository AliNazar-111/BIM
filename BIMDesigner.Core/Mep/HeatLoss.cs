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

/// <summary>One surface of a room and the heat through it: its area (m²), its U-value with the allowance for thermal bridges, what is beyond and at what temperature.</summary>
public sealed record HeatLossLine(string What, double Area, double U, HeatPath Path, double Beyond, double Watts, Element? Through, Room? Next = null);

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
}

/// <summary>The whole building's heat loss: each room's, and what the building loses all together.</summary>
public sealed record BuildingHeatLoss(double Outside, double Ground, double AirPermeability, IReadOnlyList<RoomHeatLoss> Rooms, double Ventilation)
{
    /// <summary>What leaves the heated building through its surfaces, W.</summary>
    public double Fabric => Rooms.Where(room => room.Heated).Sum(room => room.Lost);

    /// <summary>The building's design heat load, W: what its heat sources have to give.</summary>
    public double Total => Fabric + Ventilation;

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
/// 0.34 W a kelvin.
/// </summary>
public static class HeatLoss
{
    /// <summary>Thermal bridges, W/m²K added to the U-value: of what faces outside or the ground, and of what faces another space.</summary>
    public const double ExternalBridging = 0.08, InternalBridging = 0.05;

    /// <summary>What a cubic metre of air an hour carries a kelvin, W (ρc_p).</summary>
    public const double Air = 0.34;

    /// <summary>The ordinary flow through the envelope as a share of the flow at 50 Pa (f_qv,z); and how much more a room facing the wind takes (f_dir).</summary>
    public const double FlowFactor = 0.05, Orientation = 2;

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
        var name = room.Name.Trim().ToLowerInvariant();
        if (UnheatedWords.Any(name.Contains) || UnheatedNames.Contains(name)) return null;
        if (document.ProjectInformation.ClearanceStandard.IsUs()) return 20;
        return WetWords.Any(name.Contains) ? 22 : 21;
    }

    /// <summary>The temperature a room is heated to, °C: what is set for it, or what its use asks; null for one not heated.</summary>
    public static double? Temperature(BimDocument document, Room room) =>
        room.Unheated ? null : room.DesignTemperature ?? ByUse(document, room);

    /// <summary>The building's heat loss, room by room; null where there is no design temperature outside to work to.</summary>
    public static BuildingHeatLoss? Of(BimDocument document)
    {
        if (Climate.DesignOutside(document) is not { } outside) return null;
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
                Surfaces(document, room, boundary, inside, outside, ground, volume, levels, temperatures, RoomAt, lines, notes);
            surveyed.Add((room, boundary, lines, notes, volume));
        }

        // The air: each room's share of the leakage through the envelope, the whole home's at the most.
        double Envelope(List<HeatLossLine> lines) => lines.Where(line => line.Path is HeatPath.Outside or HeatPath.Ground).Sum(line => line.Area);
        var heated = surveyed.Where(entry => temperatures[entry.Room] is not null).ToList();
        var zone = FlowFactor * permeability * heated.Sum(entry => Envelope(entry.Lines));
        var share = heated.Count > 1 ? ZoneShare : 1;
        double building = 0;
        var results = new List<RoomHeatLoss>();
        foreach (var (room, boundary, lines, notes, volume) in surveyed)
        {
            var envelope = Envelope(lines);
            var temperature = temperatures[room];
            var leakage = FlowFactor * permeability * envelope;
            var minimum = envelope > 0 ? LeastAirChange * volume : 0;
            var flow = Math.Max(Math.Min(zone, Orientation * leakage), minimum);
            var difference = temperature is { } t ? t - outside : 0;
            if (temperature is not null) building += Air * Math.Max(leakage, share * minimum) * difference;
            results.Add(new RoomHeatLoss(room, room.ToString(), temperature, lines, boundary.Area / 1e6, volume, envelope,
                leakage, minimum, temperature is null ? 0 : flow, temperature is null ? 0 : Air * flow * difference, notes));
        }

        return new BuildingHeatLoss(outside, ground, permeability, results, building);
    }

    /// <summary>A room's heat loss on its own - the building's worked out for it.</summary>
    public static RoomHeatLoss? OfRoom(BimDocument document, Room room) => Of(document)?.Of(room);

    private static void Surfaces(
        BimDocument document, Room room, RoomBoundaryResult boundary, double inside, double outside, double ground, double volume,
        IReadOnlyList<Datums.Level> levels, IReadOnlyDictionary<Room, double?> temperatures, Func<Guid, Point2D, Room, Room?> roomAt,
        List<HeatLossLine> lines, List<string> notes)
    {
        var polygon = boundary.Polygon;
        var area = boundary.Area / 1e6;
        var centre = boundary.Centroid;
        var level = levels.FirstOrDefault(each => each.Id == room.LevelId);
        var floorLevel = (level?.Elevation ?? 0) + room.BaseOffset;
        var above = level is null ? null : levels.FirstOrDefault(each => each.Elevation > level.Elevation + 1);
        var below = level is null ? null : levels.LastOrDefault(each => each.Elevation < level.Elevation - 1);

        // The walls' height: storey to storey, as a net internal height with the floor between; under the roof, the room's own.
        var storey = above is not null && level is not null ? above.Elevation - level.Elevation : area > 0 ? volume / area * 1000 : room.Height;

        void Add(string what, double square, double u, HeatPath path, double beyond, Element? through, Room? next = null)
        {
            if (square <= 1e-6) return;
            var bridged = u + (path is HeatPath.Outside or HeatPath.Ground ? ExternalBridging : InternalBridging);
            lines.Add(new HeatLossLine(what, square, bridged, path, beyond, square * bridged * (inside - beyond), through, next));
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
            var curtain = document.FindType<CurtainWallType>(wall.TypeId);
            var type = document.GetWallType(wall);
            var width = type?.Width ?? 0;
            var middle = new Point2D((from.X + to.X) / 2, (from.Y + to.Y) / 2);
            var exterior = curtain is not null || type?.Function == WallFunction.Exterior;
            var next = roomAt(room.LevelId, middle + outward * (width + 150), room);
            if (Across(next, exterior ? HeatPath.Outside : HeatPath.Unheated) is not { } faces) continue;
            var (path, beyond, neighbour) = faces;

            var gross = length * storey / 1e6;
            double? wallU = curtain is not null ? curtain.HeatTransferCoefficient : type is null ? null : WallRules.UValue(document, type);
            var openings = 0.0;
            if (curtain is null && type is not null)
                foreach (var opening in WallOpenings.Of(document, wall))
                {
                    if (document.FindType<OpeningType>(opening.TypeId) is not { HeatTransferCoefficient: > 0 } openingType) continue;
                    var (spanFrom, spanTo) = opening.GetSpan(openingType);
                    var at = wall.PointAt(type.Structure, (spanFrom + spanTo) / 2, 0);
                    var offset = (at - from).Dot(along);
                    if (offset < -50 || offset > length + 50 || Math.Abs((at - from).Dot(outward)) > width / 2 + 100) continue;

                    var square = opening.WidthOf(openingType) * opening.HeightOf(openingType) / 1e6;
                    openings += square;
                    Add($"{(opening is Door ? "Door" : "Window")} {openingType.Name}", square, openingType.HeatTransferCoefficient, path, beyond, opening, neighbour);
                }

            var what = path switch { HeatPath.Outside => "Wall", HeatPath.Room => $"Wall to {neighbour}", _ => neighbour is null ? "Wall to a space not heated" : $"Wall to {neighbour}, not heated" };
            if (wallU is { } u) Add($"{what} ({curtain?.Name ?? type!.Name})", Math.Max(0, gross - openings), u, path, beyond, wall, neighbour);
            else notes.Add($"{curtain?.Name ?? type?.Name ?? "a wall"}'s U-value is not known - its layers' materials want their conductivity");
        }

        // Its floor: the ground under the lowest storey, or what is under it.
        var floor = document.All<Floor>().FirstOrDefault(slab => slab.LevelId == room.LevelId && slab.Contains(centre));
        var floorType = floor is null ? null : document.FindType<SlabType>(floor.TypeId);
        if (below is null)
        {
            if (floor is null) notes.Add("no floor under it - nothing lost to the ground counted");
            else if (SlabRules.UValue(document, floor) is { } u)
            {
                var suspended = Suspended(document, floorType);
                Add($"Ground floor, {(suspended ? "suspended" : "solid")} ({floorType?.Name})", area, u, HeatPath.Ground, suspended ? outside : ground, floor);
            }
            else notes.Add($"{floorType?.Name ?? "its floor"}'s U-value is not known");
        }
        else if (Across(roomAt(below.Id, centre, room), HeatPath.Unheated) is { } under)
        {
            var (path, beyond, neighbour) = under;
            if (floorType is not null && Thermal.UValue(document, floorType.Structure, Thermal.Flow.Downward) is { } u)
                Add(path == HeatPath.Room ? $"Floor over {neighbour}" : neighbour is null ? "Floor over a space not heated" : $"Floor over {neighbour}, not heated", area, u, path, beyond, floor, neighbour);
            else notes.Add("the U-value of the floor under it is not known");
        }

        // What is over it: the roof - with any ceiling and its insulation - or the storey above.
        var covers = RoomHeadroom.Covers(document, room, polygon);
        var over = RoomHeadroom.Over(document, floorLevel, centre, covers);
        var roof = over?.By as Roof ?? (over?.By is Ceiling
            ? covers.OfType<Roof>().Where(each => each.Contains(centre)).OrderBy(each => each.GetBottomElevation(document)).FirstOrDefault()
            : null);
        if (roof is not null)
        {
            var sloped = over?.By is Roof && !roof.Surface(document).IsFlat;
            var square = sloped ? area / Math.Max(0.2, Math.Cos(roof.SlopeDegrees * Math.PI / 180)) : area;
            if (SlabRules.UValue(document, roof) is { } u)
                Add($"{(over?.By is Roof ? "Roof" : "Ceiling under the roof")} ({document.FindType<SlabType>(roof.TypeId)?.Name})", square, u, HeatPath.Outside, outside, roof);
            else notes.Add("the roof's U-value is not known");
        }
        else if (covers.OfType<Floor>().Where(each => each.Contains(centre)).OrderBy(each => each.GetBottomElevation(document)).FirstOrDefault() is { } upper)
        {
            if (Across(roomAt(upper.LevelId, centre, room), HeatPath.Unheated) is { } overhead)
            {
                var (path, beyond, neighbour) = overhead;
                if (document.FindType<SlabType>(upper.TypeId) is { } upperType && Thermal.UValue(document, upperType.Structure, Thermal.Flow.Upward) is { } u)
                    Add(path == HeatPath.Room ? $"Ceiling under {neighbour}" : neighbour is null ? "Ceiling under a space not heated" : $"Ceiling under {neighbour}, not heated", area, u, path, beyond, upper, neighbour);
                else notes.Add("the U-value of the floor over it is not known");
            }
        }
        else notes.Add("nothing over it - no roof or floor found, so nothing lost upward is counted");
    }

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
