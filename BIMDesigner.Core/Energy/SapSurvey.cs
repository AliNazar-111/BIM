using System.Text.RegularExpressions;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;
using BIMDesigner.Core.Mep;

namespace BIMDesigner.Core.Energy;

/// <summary>The home as the model gives it to SAP, and what had to be taken that the model does not say.</summary>
public sealed record SapModel(SapDwelling Home, IReadOnlyList<string> Notes)
{
    /// <summary>Whether there is a home to rate at all - a heated room enclosed.</summary>
    public bool Empty => Home.FloorArea <= 0;
}

/// <summary>
/// The home read off the model for SAP 10.3: its heated rooms' floor area and volume, each of their
/// surfaces from the heat loss - with its U-value as its layers or its maker give it, the thermal
/// bridges counted on their own - and its junctions measured round the rooms: lintels, sills and
/// jambs, the ground and intermediate floors, eaves and gables, corners and party walls, each by
/// its length; the heat its surfaces hold; its living room; how it is ventilated and heated, its
/// hot water, showers and baths, its lights and the solar arrays on its roofs.
/// </summary>
public static class SapSurvey
{
    /// <summary>
    /// The thermal resistance of an unheated space beside the home, m²K/W - an integral garage's with the
    /// walls between it and the home its outside walls, one wall and its floor (Table 3.1): the least.
    /// </summary>
    public const double UnheatedResistance = 0.25;

    /// <summary>What a room is called that is the living room (section 7: the lounge, or the biggest room for sitting in).</summary>
    private static readonly string[] LivingWords = { "living", "lounge", "sitting", "family", "reception", "snug", "drawing" };
    private static readonly string[] NotLivingWords = { "bed", "bath", "shower", "en-suite", "ensuite", "wc", "toilet", "cloak", "utility", "kitchen", "hall", "landing", "store", "cupboard", "study" };

    /// <summary>Where a room's walls meet a floor or a roof, as its junctions are counted.</summary>
    private enum Edge
    {
        Within,
        Ground,
        Exposed,
        Loft,
        Rafter,
        Flat
    }

    /// <summary>The home as the model has it, for SAP.</summary>
    public static SapModel Of(BimDocument document)
    {
        var notes = new List<string>();
        var project = document.ProjectInformation;
        var settings = project.Energy;
        var north = project.TrueNorth;
        var building = HeatLoss.Surveyed(document);
        var heated = building?.Rooms.Where(room => room.Heated && room.Area > 0).ToList() ?? new List<RoomHeatLoss>();
        if (building is null || heated.Count == 0)
            return new SapModel(new SapDwelling(), new[] { "No heated room is enclosed - the home's rooms are wanted, bounded by its walls." });

        using var cache = ModelCache.Begin(document);
        var levels = document.Levels.OrderBy(level => level.Elevation).ToList();
        Datums.Level? LevelOf(Guid id) => levels.FirstOrDefault(level => level.Id == id);
        double StoreyHeight(RoomHeatLoss room) =>
            LevelOf(room.Room.LevelId) is { } level && levels.FirstOrDefault(each => each.Elevation > level.Elevation + 1) is { } above
                ? (above.Elevation - level.Elevation) / 1000
                : room.Volume / room.Area;

        // Its size: the heated rooms' floors, and the volume storey by storey.
        // Its size to the inside of its outside walls (box 4): its rooms and the floor under the partitions between them.
        var floorArea = heated.Sum(room => room.Area) + Partitions(document, heated);
        var volume = heated.Sum(room => room.Area * StoreyHeight(room))
                     + heated.GroupBy(room => room.Room.LevelId).Sum(storey => Partitions(document, heated, storey.Key) * StoreyHeight(storey.First()));
        var storeys = heated.Select(room => room.Room.LevelId).Distinct().ToList();
        var lowest = levels.FirstOrDefault(level => storeys.Contains(level.Id));
        var groundFloor = building.Rooms.Where(room => room.Room.LevelId == lowest?.Id).Sum(room => room.Area) + (lowest is null ? 0 : Partitions(document, heated, lowest.Id));

        // Its living room: one called so, or the biggest room for sitting in.
        bool Says(Room room, IEnumerable<string> words) => words.Any(word => room.Name.Contains(word, StringComparison.OrdinalIgnoreCase));
        var living = heated.Where(room => Says(room.Room, LivingWords)).OrderByDescending(room => room.Area).FirstOrDefault();
        if (living is null)
        {
            living = heated.Where(room => !Says(room.Room, NotLivingWords)).OrderByDescending(room => room.Area).FirstOrDefault()
                     ?? heated.OrderByDescending(room => room.Area).First();
            notes.Add($"No room is called a living room or lounge: {living.Name}, the biggest room for sitting in, is taken as the living area.");
        }

        // Its surfaces, from the heat loss: what faces outside, the ground, an unheated space or a neighbour.
        var elements = new List<SapElement>();
        foreach (var room in heated)
        foreach (var line in room.Lines.Where(line => line.Path != HeatPath.Room && line.Kind != HeatSurface.SlabEdge))
        {
            var u = line.Bare;
            if (line.Path == HeatPath.Unheated && line.Next is not null) u = 1 / (1 / u + UnheatedResistance);
            var orientation = line.Facing is { } facing ? ((facing - north) % 360 + 360) % 360 : 180;
            var through = line.Through;
            switch (line.Kind)
            {
                case HeatSurface.Wall when through is Wall wall && document.GetWallType(wall) is { Separating: true } party:
                    elements.Add(new SapElement(line.What, SapSurface.PartyWall, line.Area, PartyU(document, party)) { Source = through });
                    break;
                case HeatSurface.Wall:
                    elements.Add(new SapElement(line.What, SapSurface.Wall, line.Area, u) { Source = through, Orientation = orientation });
                    break;
                case HeatSurface.Door:
                    elements.Add(new SapElement(line.What, SapSurface.Door, line.Area, u) { Source = through, Orientation = orientation });
                    break;
                case HeatSurface.Window:
                    var (g, frame, light, triple, curtain) = GlazingOf(document, through);
                    var kind = through is RoofWindow ? line.Tilt is < 1 ? SapSurface.Rooflight : SapSurface.RoofWindow : SapSurface.Window;
                    elements.Add(new SapElement(line.What, kind, line.Area, u)
                    {
                        Source = through, Orientation = orientation, Tilt = line.Tilt ?? 90, SolarTransmittance = g, FrameFactor = frame,
                        LightTransmittance = light, Triple = triple, CurtainWall = curtain
                    });
                    break;
                case HeatSurface.Roof or HeatSurface.CeilingUnderLoft or HeatSurface.Ceiling:
                    elements.Add(new SapElement(line.What, SapSurface.Roof, line.Area, u) { Source = through, Tilt = 0 });
                    break;
                default:
                    elements.Add(new SapElement(line.What, SapSurface.Floor, line.Area, u) { Source = through, Tilt = 0 });
                    break;
            }
        }

        // Its junctions and the heat its surfaces hold, room by room round each one's walls.
        var lengths = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        void Count(string code, double metres)
        {
            if (metres > 1e-6) lengths[code] = lengths.GetValueOrDefault(code) + metres;
        }

        var heatedRooms = heated.Select(room => room.Room).ToHashSet();
        var boundaries = building.Rooms.ToDictionary(room => room.Room, room => room.Room.GetBoundary(document));
        Room? RoomAt(Guid level, Point2D at, Room except) =>
            boundaries.FirstOrDefault(entry => entry.Key.LevelId == level && !ReferenceEquals(entry.Key, except) && Polygon2D.Contains(entry.Value.Polygon, at)).Key;

        double capacity = 0;
        foreach (var room in heated)
        {
            var boundary = boundaries[room.Room];
            var polygon = boundary.Polygon;
            var turn = Polygon2D.SignedArea(polygon) >= 0 ? 1 : -1;
            var height = StoreyHeight(room);
            var level = LevelOf(room.Room.LevelId);
            var below = level is null ? null : levels.LastOrDefault(each => each.Elevation < level.Elevation - 1);
            var bottom = below is null ? Edge.Ground : room.Lines.Any(line => line.Kind == HeatSurface.Floor && line.Path != HeatPath.Room) ? Edge.Exposed : Edge.Within;
            var roofLine = room.Lines.FirstOrDefault(line => line.Kind is HeatSurface.Roof or HeatSurface.CeilingUnderLoft);
            var roof = roofLine?.Through as Roof;
            var top = roofLine?.Kind == HeatSurface.CeilingUnderLoft ? Edge.Loft
                : roofLine is not null ? roof is not null && roof.Surface(document).IsFlat ? Edge.Flat : Edge.Rafter
                : room.Lines.Any(line => line.Kind == HeatSurface.Ceiling && line.Path != HeatPath.Room) ? Edge.Flat
                : Edge.Within;

            // What each side is: a wall to a heated room, a party wall, a wall of the envelope - or no wall.
            var sides = new (bool Envelope, bool Party, Point2D From, Point2D To)[polygon.Count];
            for (var i = 0; i < polygon.Count && i < boundary.Sides.Count; i++)
            {
                var from = polygon[i];
                var to = polygon[(i + 1) % polygon.Count];
                if (boundary.Sides[i] is not { } wall || from.DistanceTo(to) < 1) continue;
                var length = from.DistanceTo(to) / 1000;
                var along = (to - from) * (1 / (length * 1000));
                var outward = new Vector2D(along.Y, -along.X) * turn;
                var curtain = document.FindType<CurtainWallType>(wall.TypeId);
                var type = document.GetWallType(wall);
                var width = type?.Width ?? 0;
                var middle = new Point2D((from.X + to.X) / 2, (from.Y + to.Y) / 2);
                var next = RoomAt(room.Room.LevelId, middle + outward * (width + 150), room.Room);
                var party = type?.Separating == true;
                var envelope = !party && (next is null || !heatedRooms.Contains(next));
                sides[i] = (envelope, party, from, to);

                // The heat it holds: an inside wall from both faces, as each room sees its own; the envelope's from inside.
                var openings = OpeningsIn(document, wall, type, from, to);
                var net = Math.Max(0, length * height - openings.Sum(opening => opening.Width * opening.Height));
                if (curtain is null && type is not null)
                    capacity += net * (envelope || party ? Kappa(document, type.Structure, fromStart: false)
                        : (Kappa(document, type.Structure, true) + Kappa(document, type.Structure, false)) / 2);

                if (party)
                {
                    Count(bottom switch { Edge.Ground => "P1", Edge.Exposed => "P7", _ => "P2" }, length);
                    if (top is Edge.Loft or Edge.Flat) Count("P4", length);
                    else if (top == Edge.Rafter) Count("P5", roof is null ? length : SlopedLength(document, roof, from, to, outward * -50));
                    continue;
                }

                if (!envelope) continue;

                // Round its windows and doors - none counted in curtain walling, whose U-value has them.
                foreach (var (width2, height2, window) in openings)
                {
                    Count("E2", width2);
                    if (window) Count("E3", width2);
                    Count("E4", 2 * height2);
                }

                Count(bottom switch { Edge.Ground => "E5", Edge.Exposed => "E20", _ => "E6" }, length);
                switch (top)
                {
                    case Edge.Loft or Edge.Rafter when roof is not null && Gable(document, roof, from, to, outward * -50):
                        Count(top == Edge.Loft ? "E12" : "E13", top == Edge.Loft ? length : SlopedLength(document, roof, from, to, outward * -50));
                        break;
                    case Edge.Loft:
                        Count("E10", length);
                        break;
                    case Edge.Rafter:
                        Count("E11", length);
                        break;
                    case Edge.Flat:
                        Count("E14", length);
                        break;
                }
            }

            // Its corners: two walls of the envelope meeting - outward, or turned in - and a party wall meeting the envelope.
            for (var i = 0; i < sides.Length; i++)
            {
                var before = sides[(i - 1 + sides.Length) % sides.Length];
                var after = sides[i];
                if (before.From == before.To || after.From == after.To) continue;
                if (before.Envelope && after.Envelope)
                {
                    var a = before.To - before.From;
                    var b = after.To - after.From;
                    var cross = turn * (a.X * b.Y - a.Y * b.X) / (a.Length * b.Length);
                    if (Math.Abs(cross) < 0.1) continue;
                    Count(cross > 0 ? "E16" : "E17", height);
                }
                else if (before.Envelope && after.Party || before.Party && after.Envelope)
                    Count("E18", height);
            }

            // Its floor's top and its ceiling's underside.
            var centre = boundary.Centroid;
            if (document.All<Floor>().FirstOrDefault(slab => slab.LevelId == room.Room.LevelId && slab.Contains(centre)) is { } floor
                && document.FindType<SlabType>(floor.TypeId) is { } floorType)
                capacity += room.Area * Kappa(document, floorType.Structure, fromStart: true);
            var floorLevel = (level?.Elevation ?? 0) + room.Room.BaseOffset;
            if (RoomHeadroom.Over(document, floorLevel, centre, RoomHeadroom.Covers(document, room.Room, polygon))?.By is { } over
                && document.FindType<SlabType>(over.TypeId) is { } overType)
                capacity += room.Area * Kappa(document, overType.Structure, fromStart: false);

            // Round its roof windows, and a rooflight's kerb.
            foreach (var line in room.Lines.Where(line => line.Through is RoofWindow))
            {
                var window = (RoofWindow)line.Through!;
                if (document.FindType<RoofWindowType>(window.TypeId) is not { } windowType) continue;
                var built = window.Built(windowType);
                if (line.Tilt is < 1) Count("R11", 2 * (built.Width + built.Height) / 1000);
                else
                {
                    Count("R1", built.Width / 1000);
                    Count("R2", built.Width / 1000);
                    Count("R3", 2 * built.Height / 1000);
                }
            }
        }

        // What the heat loss could not find out - a U-value not given, a floor missing - said once each.
        foreach (var note in heated.SelectMany(room => room.Notes.Select(note => $"{room.Name}: {note}")).Distinct().Take(12))
            notes.Add(char.ToUpperInvariant(note[0]) + note[1..] + ".");

        var thermalMass = capacity > 0 ? capacity / floorArea : 250;
        if (capacity <= 0) notes.Add("The heat the walls and floors hold could not be worked out from their layers: a medium thermal mass, 250 kJ/m²K, is taken.");

        double Psi(string code) =>
            settings.Psi.TryGetValue(code, out var own) ? own
            : SapJunctions.All.TryGetValue(code, out var junction) ? settings.Bridging == ThermalBridging.Defaults ? junction.Default : junction.Reference
            : 0;
        var junctions = settings.Bridging == ThermalBridging.YValue
            ? new List<SapJunction>()
            : lengths.OrderBy(entry => entry.Key).Select(entry => new SapJunction(entry.Key, SapJunctions.All.TryGetValue(entry.Key, out var junction) ? junction.What : entry.Key, entry.Value, Psi(entry.Key))).ToList();
        notes.Add(settings.Bridging switch
        {
            ThermalBridging.ReferenceDetails => "Each junction is taken at the notional home's ψ (Table R2): the details are to be designed and calculated to achieve them, or their own ψ given.",
            ThermalBridging.Defaults => "Each junction is taken at SAP's default ψ (Table K1), as for details not calculated.",
            _ => $"The thermal bridges are taken as a y-value of {settings.YValue:0.00} W/m²K over the area facing outside."
        });

        var (ventilation, fanPower, recovery, fans, central) = VentilationOf(document, notes);
        var chimneys = document.All<Chimney>().Count();
        if (chimneys > 0) notes.Add($"{chimneys} chimney{(chimneys == 1 ? " is" : "s are")} taken as open, 80 m³/h each - Approved Document L 2026 asks for no chimney or flue without an appliance.");

        var (heating, source) = HeatingOf(document, heated, boundaries, notes);
        var showers = new List<SapShower>();
        var baths = 0;
        double lumens = 0, watts = 0;
        foreach (var component in document.All<Component>())
        {
            if (document.FindType<ComponentType>(component.TypeId) is not { } type) continue;
            if (!heated.Any(room => RoomsOf.Holds(document, room.Room, boundaries[room.Room].Polygon, component))) continue;
            if (type.Form == ComponentForm.Shower) showers.Add(new SapShower(type.FlowRate > 0 ? type.FlowRate : DefaultShowerFlow(document, source), type.Load > 0 ? type.Load / 1000 : 0));
            else if (type.Form == ComponentForm.Bath) baths++;
            else if (type.Kind == BuiltInCategory.LightingFixtures)
            {
                lumens += Lighting.LumensOf(type);
                watts += Circuits.LoadOf(type);
            }
        }

        if (lumens <= 0) notes.Add("No fixed lights are drawn: SAP's allowance for a home without them is taken (L5b), at 21.3 lm/W.");
        var hotWater = HotWaterOf(document, heating, source, notes);

        // Its solar arrays: on each roof that has one, the face turned most to the south.
        var arrays = new List<SapPhotovoltaic>();
        foreach (var roof in document.All<Roof>().Where(roof => roof.SolarPeakPower > 0))
        {
            var (orientation, tilt, area) = ArrayOn(document, roof, north);
            var shading = roof.SolarOvershading switch
            {
                PvOvershading.Modest => 0.8,
                PvOvershading.Significant => 0.5,
                PvOvershading.Heavy => 0.35,
                PvOvershading.Severe => 0.2,
                _ => 1.0
            };
            arrays.Add(new SapPhotovoltaic(document.FindType<SlabType>(roof.TypeId)?.Name ?? "Roof", roof.SolarPeakPower, orientation, tilt, shading) { Source = roof });
            if (area > 0 && roof.SolarPeakPower / 0.22 > area)
                notes.Add($"{roof.SolarPeakPower:0.##} kWp wants about {roof.SolarPeakPower / 0.22:0} m² of panels at 0.22 kWp/m², more than the {area:0} m² of the roof's face it is on.");
        }

        var cooling = source is { } cooler && document.FindType<ComponentType>(cooler.TypeId) is { CoolingOutput: > 0 };
        if (cooling) notes.Add("The heat pump cools as well: the whole home is taken as cooled, at SAP's SEER for an unknown class, 4.3 (Table 10c).");

        var home = new SapDwelling
        {
            FloorArea = floorArea, Volume = volume, Storeys = Math.Max(1, storeys.Count), Flat = settings.Flat, LivingArea = living.Area, GroundFloorArea = groundFloor,
            Elements = elements, Junctions = junctions, YValue = settings.YValue, ThermalMass = thermalMass,
            ShelteredSides = settings.ShelteredSides, Overshading = settings.Overshading, AirPermeability = project.AirPermeability,
            Ventilation = ventilation, SpecificFanPower = fanPower, HeatRecovery = recovery, CentralExtract = central, OpenChimneys = chimneys, IntermittentFans = fans,
            Heating = heating, HotWater = hotWater, Showers = showers, Baths = baths, WaterTarget = true,
            WasteWaterRecovery = settings.WasteWaterRecovery, WasteWaterUtilisation = settings.WasteWaterUtilisation,
            LightingLumens = lumens, LightingWatts = watts, Photovoltaics = arrays, Battery = settings.Battery,
            CooledFraction = cooling ? 1 : 0, Seer = cooling ? 4.3 : 0
        };

        // A heat pump with no cylinder drawn: one sized to the hot water used, its loss as for one not specified.
        if (heating.Source == SapHeatSource.HeatPump && hotWater.CylinderVolume <= 0)
        {
            var sized = Math.Round(SapCalculation.HotWaterDemand(home).OrderBy(litres => litres).ElementAt(8));
            home = home with { HotWater = hotWater with { CylinderVolume = sized, HeatPumpInUse = 0.60 } };
            notes.Add($"No hot water cylinder is drawn for the heat pump: one of {sized:0} litres, the hot water used on a busy day, is taken, with SAP's in-use factor for a cylinder not specified (Table N8).");
        }

        return new SapModel(home, notes);
    }

    /// <summary>
    /// The floor under the home's own partitions, m² - what its rooms leave out of the floor measured to the
    /// inside of its outside walls (SAP's total floor area, Approved Document O's floor area): each wall
    /// with one of its rooms on both sides, by its thickness, along as much of it as has. On one storey, or all.
    /// </summary>
    public static double Partitions(BimDocument document, IReadOnlyCollection<RoomHeatLoss> rooms, Guid? level = null)
    {
        var outlines = rooms.Where(room => level is null || room.Room.LevelId == level)
            .GroupBy(room => room.Room.LevelId)
            .ToDictionary(storey => storey.Key, storey => storey.Select(room => room.Room.GetBoundary(document).Polygon).ToList());
        double area = 0;
        foreach (var wall in document.Walls.Where(wall => outlines.ContainsKey(wall.LevelId) && wall.Length >= 1))
        {
            if (document.FindType<CurtainWallType>(wall.TypeId) is not null || document.GetWallType(wall) is not { } type) continue;
            if (type.Function == WallFunction.Exterior || type.Separating) continue;
            bool In(Point2D point) => outlines[wall.LevelId].Any(polygon => Polygon2D.Contains(polygon, point));

            // Along it a step at a time: the share of it with a room either side.
            var steps = Math.Max(4, (int)(wall.Length / 100));
            var between = 0;
            for (var i = 0; i < steps; i++)
            {
                var along = wall.Length * (i + 0.5) / steps;
                var at = wall.LocationCurve.PointAt(along);
                var ahead = wall.LocationCurve.PointAt(Math.Min(wall.Length, along + 1)) - wall.LocationCurve.PointAt(Math.Max(0, along - 1));
                if (ahead.Length < 1e-9) continue;
                var across = new Vector2D(ahead.Y, -ahead.X) * (1 / ahead.Length) * (type.Width / 2 + 100);
                if (In(at + across) && In(at - across)) between++;
            }

            area += wall.Length * between / steps * type.Width / 1e6;
        }

        return area;
    }

    /// <summary>The windows and doors in a wall along a side of a room: each one's width and height, m, and whether it is a window.</summary>
    private static List<(double Width, double Height, bool Window)> OpeningsIn(BimDocument document, Wall wall, WallType? type, Point2D from, Point2D to)
    {
        var found = new List<(double, double, bool)>();
        if (type is null || document.FindType<CurtainWallType>(wall.TypeId) is not null) return found;
        var length = from.DistanceTo(to);
        var along = (to - from) * (1 / length);
        var outward = new Vector2D(along.Y, -along.X);
        foreach (var opening in WallOpenings.Of(document, wall))
        {
            if (document.FindType<OpeningType>(opening.TypeId) is not { } openingType || openingType.HeatTransferCoefficient <= 0) continue;
            var (spanFrom, spanTo) = opening.GetSpan(openingType);
            var at = wall.PointAt(type.Structure, (spanFrom + spanTo) / 2, 0);
            var offset = (at - from).Dot(along);
            if (offset < -50 || offset > length + 50 || Math.Abs((at - from).Dot(outward)) > type.Width / 2 + 100) continue;
            found.Add((opening.WidthOf(openingType) / 1000, opening.HeightOf(openingType) / 1000, opening is Window));
        }

        return found;
    }

    /// <summary>Whether the roof over a wall rises along it - a gable - rather than keeping level along it, an eaves.</summary>
    private static bool Gable(BimDocument document, Roof roof, Point2D from, Point2D to, Vector2D inward)
    {
        var surface = roof.Surface(document);
        var heights = Enumerable.Range(0, 5).Select(i => surface.HeightAt(from + (to - from) * (i / 4.0) + inward)).ToList();
        return heights.Max() - heights.Min() > 100;
    }

    /// <summary>A wall's length along the roof over it, m: up a gable's rake as well as along it.</summary>
    private static double SlopedLength(BimDocument document, Roof roof, Point2D from, Point2D to, Vector2D inward)
    {
        var surface = roof.Surface(document);
        double total = 0;
        for (var i = 0; i < 8; i++)
        {
            var a = from + (to - from) * (i / 8.0) + inward;
            var b = from + (to - from) * ((i + 1) / 8.0) + inward;
            var rise = surface.HeightAt(b) - surface.HeightAt(a);
            total += Math.Sqrt(a.DistanceTo(b) * a.DistanceTo(b) + rise * rise);
        }

        return total / 1000;
    }

    /// <summary>
    /// The heat a face of an element holds, kJ/m²K (box 35's κ): its layers' thickness, density and
    /// specific heat from that face in, as far as half its thickness, an insulating layer or 100 mm,
    /// whichever comes first (BS EN ISO 13786's simple method, as SAP's Table 1e takes it).
    /// </summary>
    public static double Kappa(BimDocument document, CompoundStructure structure, bool fromStart)
    {
        var layers = fromStart ? structure.Layers.ToList() : structure.Layers.Reverse().ToList();
        var limit = Math.Min(structure.Layers.Sum(layer => layer.Thickness) / 2, 100);
        double depth = 0, kappa = 0;
        foreach (var layer in layers.Where(layer => layer.Thickness > 0))
        {
            if (document.FindMaterial(layer.MaterialId) is not { } material) break;
            if (material.Density >= 5 && material.ThermalConductivity is > 0 and <= 0.08) break;
            var taken = Math.Min(layer.Thickness, limit - depth);
            if (taken <= 0) break;
            kappa += taken / 1000 * material.Density * SpecificHeat(material) / 1000;
            depth += taken;
        }

        return kappa;
    }

    /// <summary>A material's specific heat, J/kgK, by what it is: masonry, concrete and plaster 840, timber 1600, insulation 1450, glass 750, steel 450.</summary>
    private static double SpecificHeat(Material material)
    {
        bool Is(params string[] words) => words.Any(word => material.Name.Contains(word, StringComparison.OrdinalIgnoreCase));
        if (material.Density < 5) return 1000;
        if (Is("plywood")) return 1700;
        if (Is("timber", "wood", "osb", "chipboard", "softwood", "hardwood", "oak", "pine", "joist", "stud")) return 1600;
        if (Is("wool", "eps", "xps", "polystyrene", "foam", "pir", "pur", "phenolic", "insulation", "polyiso")) return 1450;
        if (Is("glass")) return 750;
        if (Is("steel", "alumin", "metal", "iron", "copper", "zinc")) return 450;
        return 840;
    }

    /// <summary>A party wall's U-value as SAP has it (Table 3.10): none for a solid or fully filled one, 0.2 for an unfilled cavity sealed at its edges.</summary>
    private static double PartyU(BimDocument document, WallType type) =>
        type.Structure.Layers.Any(layer => layer.Function == LayerFunction.ThermalAir && document.FindMaterial(layer.MaterialId) is { Density: < 5 }) ? 0.2 : 0;

    /// <summary>A glazed opening's solar transmittance, frame factor and light transmittance (Tables 6b and 6c), whether triple glazed, and whether curtain walling.</summary>
    private static (double G, double Frame, double Light, bool Triple, bool Curtain) GlazingOf(BimDocument document, Element? through)
    {
        static double FrameFactor(string material) =>
            new[] { "alumin", "steel", "metal", "bronze" }.Any(word => material.Contains(word, StringComparison.OrdinalIgnoreCase)) ? 0.8 : 0.7;
        switch (through)
        {
            case Window window when document.FindType<WindowType>(window.TypeId) is { } type:
                var glazing = type.GlazingType;
                var triple = glazing.Contains("triple", StringComparison.OrdinalIgnoreCase);
                var single = glazing.Contains("single", StringComparison.OrdinalIgnoreCase);
                var g = type.SolarHeatGainCoefficient > 0 ? type.SolarHeatGainCoefficient : triple ? 0.57 : single ? 0.85 : 0.63;
                return (g, FrameFactor(type.FrameMaterial), triple ? 0.70 : single ? 0.90 : 0.80, triple, false);
            case RoofWindow roofWindow when document.FindType<RoofWindowType>(roofWindow.TypeId) is { } type:
                return (type.SolarHeatGainCoefficient, 0.7, 0.80, false, false);
            case Door door when document.FindType<DoorType>(door.TypeId) is { } type:
                return (0.63, FrameFactor(type.FrameMaterial), 0.80, false, false);
            case Wall:
                return (0.63, 0.8, 0.80, false, true);
            default:
                return (0.63, 0.7, 0.80, false, false);
        }
    }

    /// <summary>How the home is ventilated, its fans' specific fan power and a heat recovery unit's efficiency, and the intermittent fans and hoods extracting outside.</summary>
    private static (SapVentilation Kind, double FanPower, double Recovery, int Fans, bool Central) VentilationOf(BimDocument document, List<string> notes)
    {
        var types = document.All<Component>().Select(component => document.FindType<ComponentType>(component.TypeId)).OfType<ComponentType>().ToList();
        var intermittent = types.Count(type => type.Form == ComponentForm.ExtractFan && type.ContinuousRate <= 0 || type.Form == ComponentForm.CookerHood && type.ExtractRate > 0);
        switch (Ventilation.TypeOf(document))
        {
            case VentilationType.HeatRecovery:
                var unit = Ventilation.Units(document).Select(each => document.FindType<ComponentType>(each.TypeId)).OfType<ComponentType>().FirstOrDefault();
                if (unit is null || unit.SpecificFanPower <= 0) notes.Add("The heat recovery unit's specific fan power is not given: SAP's default, 2.0 W/(l/s), is taken (Table 4g).");
                return (SapVentilation.HeatRecovery, unit?.SpecificFanPower ?? 0, unit?.HeatRecovery ?? 0, intermittent, false);
            case VentilationType.ContinuousExtract:
                var central = Ventilation.ExtractUnits(document).Select(each => document.FindType<ComponentType>(each.TypeId)).OfType<ComponentType>().FirstOrDefault();
                var decentralised = types.Where(type => type is { Form: ComponentForm.ExtractFan, ContinuousRate: > 0, SpecificFanPower: > 0 }).Select(type => type.SpecificFanPower).DefaultIfEmpty(0).Average();
                var power = central?.SpecificFanPower > 0 ? central.SpecificFanPower : decentralised;
                if (power <= 0) notes.Add("The extract fans' specific fan power is not given: SAP's default, 0.8 W/(l/s), is taken (Table 4g).");
                return (SapVentilation.Extract, power, 0, intermittent, central is not null);
            default:
                return (SapVentilation.Natural, 0, 0, intermittent, false);
        }
    }

    /// <summary>What heats the home - its heat pump or boiler, or electric heaters where none is drawn - with its efficiencies, emitters and controls.</summary>
    private static (SapHeating Heating, Component? Source) HeatingOf(BimDocument document, List<RoomHeatLoss> heated, IReadOnlyDictionary<Room, RoomBoundaryResult> boundaries, List<string> notes)
    {
        var sources = document.All<MepSystem>().Where(system => system.Kind == SystemKind.Heating)
            .Select(zone => MepSystems.SourceOf(document, zone)).OfType<Component>().Distinct()
            .Concat(document.All<Component>().Where(component => document.FindType<ComponentType>(component.TypeId)?.Form is ComponentForm.HeatPump or ComponentForm.Boiler))
            .Distinct().ToList();
        var source = sources.FirstOrDefault(each => document.FindType<ComponentType>(each.TypeId)?.Form == ComponentForm.HeatPump)
                     ?? sources.FirstOrDefault(each => document.FindType<ComponentType>(each.TypeId)?.Form == ComponentForm.Boiler);
        var type = source is null ? null : document.FindType<ComponentType>(source.TypeId);
        if (type is null)
        {
            notes.Add("No heat pump or boiler is drawn: direct-acting electric heaters with their own thermostats are taken, as SAP does for a new home with no heating given (9.1).");
            return (new SapHeating(SapHeatSource.DirectElectric, "Direct-acting electric heaters") { ControlType = 3, Controls = "Appliance thermostats" }, null);
        }

        // Its emitters and controls: thermostats in its rooms, its zones, radiators with TRVs or floors heated.
        var components = document.All<Component>().Select(component => (Component: component, Type: document.FindType<ComponentType>(component.TypeId))).Where(entry => entry.Type is not null).ToList();
        bool In(Component component) => heated.Any(room => RoomsOf.Holds(document, room.Room, boundaries[room.Room].Polygon, component));
        var thermostats = components.Count(entry => entry.Type!.Form == ComponentForm.Thermostat && In(entry.Component));
        var zones = document.All<MepSystem>().Count(system => system.Kind == SystemKind.Heating);
        var radiators = components.Any(entry => entry.Type!.Form is ComponentForm.Radiator or ComponentForm.TowelRail);
        var floorHeated = heated.Where(room => components.Any(entry => entry.Type!.Form == ComponentForm.UnderfloorHeating && RoomsOf.Holds(document, room.Room, boundaries[room.Room].Polygon, entry.Component))).Sum(room => room.Area);
        var heatPump = type.Form == ComponentForm.HeatPump;
        var (control, controls) = thermostats >= 2 && zones >= 2 ? (3, "Time and temperature zone control")
            : thermostats >= 1 && radiators ? (2, "Programmer, room thermostat and TRVs")
            : thermostats >= 2 ? (2, "Programmer and at least two room thermostats")
            : thermostats == 1 ? (1, "Programmer and room thermostat")
            : (1, "Programmer, no room thermostat");
        if (thermostats == 0) notes.Add("No room thermostat is drawn in the heated rooms: the heating is taken as timed only, its rooms warmer for it (Table 4e).");
        var responsiveness = floorHeated > heated.Sum(room => room.Area) / 2 ? 0.75 : 1.0;

        var heating = new SapHeating(heatPump ? SapHeatSource.HeatPump : SapHeatSource.GasBoiler, type.Name)
        {
            Output = type.HeatOutput, Combi = !heatPump && type.HotWater == BoilerHotWater.Combi, ControlType = control, Controls = controls,
            Responsiveness = responsiveness, RoomThermostat = thermostats > 0, TemperatureAdjustment = thermostats > 0 ? 0 : heatPump ? 0.3 : 0.6,
            SpaceEfficiency = type.SpaceEfficiency > 0 ? type.SpaceEfficiency : heatPump ? 170 : 84,
            WaterEfficiency = type.WaterEfficiency > 0 ? type.WaterEfficiency : heatPump ? 170 : type.HotWater == BoilerHotWater.Combi ? 75 : 74
        };
        if (type.SpaceEfficiency <= 0 || type.WaterEfficiency <= 0)
            notes.Add(heatPump
                ? $"{type.Name}'s seasonal efficiencies are not given: SAP's default for an air source heat pump, 170%, is taken (Table 4a) - its PCDB figures are wanted."
                : $"{type.Name}'s seasonal efficiencies are not given: SAP's defaults for a condensing gas boiler, 84% heating and {heating.WaterEfficiency:0}% hot water, are taken (Table 4b).");
        if (!heatPump) notes.Add($"{type.Name} is taken to burn mains gas.");
        return (heating, source);
    }

    /// <summary>A shower's flow where its own is not given (Table J4): 11 l/min from a combi or an unvented cylinder, 9 otherwise.</summary>
    private static double DefaultShowerFlow(BimDocument document, Component? source) =>
        source is not null || document.All<Component>().Any(component => document.FindType<ComponentType>(component.TypeId)?.Form == ComponentForm.HotWaterCylinder) ? 11 : 9;

    /// <summary>What heats the hot water, and its cylinder: its volume and its maker's declared loss.</summary>
    private static SapHotWater HotWaterOf(BimDocument document, SapHeating heating, Component? source, List<string> notes)
    {
        var cylinder = document.All<Component>().Select(component => document.FindType<ComponentType>(component.TypeId)).OfType<ComponentType>()
            .FirstOrDefault(type => type.Form == ComponentForm.HotWaterCylinder);
        var volume = cylinder is null ? 0 : cylinder.Capacity > 0 ? cylinder.Capacity
            : Regex.Match(cylinder.Name, @"(\d+(?:\.\d+)?)\s*(l|litre|litres)\b", RegexOptions.IgnoreCase) is { Success: true } said ? double.Parse(said.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)
            : Regex.Match(cylinder.Name, @"(\d+(?:\.\d+)?)\s*gal", RegexOptions.IgnoreCase) is { Success: true } gallons ? double.Parse(gallons.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) * Units.LitresPerGallon
            : 0;
        if (cylinder is not null && volume <= 0)
        {
            volume = 150;
            notes.Add($"{cylinder.Name}'s volume is not given: 150 litres is taken.");
        }

        if (cylinder is not null && cylinder.StandingLoss <= 0)
            notes.Add($"{cylinder.Name}'s declared heat loss is not given: its loss is worked out for 50 mm of factory insulation (Table 2).");
        double? declared = cylinder is { StandingLoss: > 0 } ? cylinder.StandingLoss : null;

        switch (heating.Source)
        {
            case SapHeatSource.HeatPump:
                return new SapHotWater(SapWaterHeater.MainSystem, volume) { DeclaredLoss = declared };
            case SapHeatSource.GasBoiler when heating.Combi && cylinder is null:
                return new SapHotWater(SapWaterHeater.MainSystem, 0);
            case SapHeatSource.GasBoiler:
                if (cylinder is null)
                {
                    notes.Add("The system boiler has no hot water cylinder drawn: one of 150 litres is taken.");
                    volume = 150;
                }

                return new SapHotWater(SapWaterHeater.MainSystem, volume) { DeclaredLoss = declared };
            default:
                if (cylinder is null) notes.Add("No cylinder or water heater is drawn: the hot water is taken as heated by electricity where it is drawn.");
                return cylinder is null ? new SapHotWater(SapWaterHeater.PointOfUse, 0) : new SapHotWater(SapWaterHeater.Immersion, volume) { DeclaredLoss = declared };
        }
    }

    /// <summary>
    /// Where a roof's solar array lies: the way the face turned most to the south faces (degrees clockwise
    /// from true north) and its pitch - or on a flat roof on frames facing south at 30° - and that face's area, m².
    /// </summary>
    public static (double Orientation, double Tilt, double Area) ArrayOn(BimDocument document, Roof roof, double north)
    {
        var surface = roof.Surface(document);
        var best = (Orientation: 180.0, Tilt: 30.0, Area: Polygon2D.Area(roof.Boundary) / 1e6, Sun: double.MinValue);
        if (surface.IsFlat || surface.Facets.Count == 0) return (best.Orientation, best.Tilt, best.Area);
        foreach (var facet in surface.Facets)
        {
            var plane = facet.Plane;
            if (plane.Rise < 0.01) continue;
            var orientation = ((Math.Atan2(-plane.A, -plane.B) * 180 / Math.PI - north) % 360 + 360) % 360;
            var sun = SapClimate.Annual(orientation, plane.SlopeDegrees);
            if (sun > best.Sun) best = (orientation, plane.SlopeDegrees, facet.SlopingArea / 1e6, sun);
        }

        return (best.Orientation, best.Tilt, best.Area);
    }
}
