using System.Globalization;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Parameters;

namespace BIMDesigner.Core.Handover;

/// <summary>
/// What is known of one asset as it was put in: its serial number and bar code, the day it was
/// installed and the day its warranty began, where that is not the day it was installed. What its
/// type has in common - maker, model, warranty, life - is on the type.
/// </summary>
public sealed record AssetRecord(string SerialNumber = "", string BarCode = "", DateOnly? InstalledOn = null, DateOnly? WarrantyStart = null)
{
    public static readonly AssetRecord Empty = new();

    public bool IsEmpty => SerialNumber.Length == 0 && BarCode.Length == 0 && InstalledOn is null && WarrantyStart is null;
}

/// <summary>The kinds of thing kept up in a home, each with its own maintenance, life and tag.</summary>
public enum AssetKind
{
    GasBoiler,
    OilBoiler,
    ElectricBoiler,
    HeatPump,
    HotWaterCylinder,
    Radiator,
    TowelRail,
    UnderfloorHeating,
    Thermostat,
    HeatRecoveryUnit,
    ExtractUnit,
    ExtractFan,
    CookerHood,
    BackgroundVentilator,
    SmokeAlarm,
    HeatAlarm,
    CarbonMonoxideAlarm,
    DuctSmokeDetector,
    ConsumerUnit,
    SprinklerValve,
    Sprinkler,
    GasMeter,
    StopValve,
    GasFire,
    Cooker,
    Fridge,
    Dishwasher,
    WashingMachine,
    TumbleDryer,
    Toilet,
    Basin,
    Sink,
    Bath,
    Shower,
    DrainAccess,
    Soakaway,
    AirAdmittanceValve,
    Door,
    FireDoor,
    Window,
    RoofWindow,
    Gutter,
    Chimney,

    /// <summary>Equipment of a family of one's own: kept up as its maker says.</summary>
    Equipment
}

/// <summary>How long a kind of thing lasts before it is replaced: years, or the life of the building, or not known.</summary>
public sealed record ServiceLife(double? Years, bool Lifetime, string Said, string Source)
{
    public static readonly ServiceLife NotGiven = new(null, false, "not given", string.Empty);

    public static ServiceLife For(double years, string said, string source) => new(years, false, said, source);

    public static ServiceLife ForLife(string source) => new(null, true, "the life of the building", source);
}

/// <summary>
/// One asset as the handover sees it: the element, its type and kind, the tag it is known by, where it
/// is - its storey, the room it is in, its plan position - and the systems it is on.
/// </summary>
public sealed record Asset(Element Element, ElementType? Type, AssetKind Kind, string Tag, string Name, Level? Level, string Room, Point2D At, IReadOnlyList<string> Systems)
{
    public string Maker => Type?.Manufacturer.Trim() ?? string.Empty;

    public string Model => Type?.ModelNumber.Trim() ?? string.Empty;

    public AssetRecord Record => Element.Asset;
}

/// <summary>
/// The home's assets: what is kept up in it, found from the model - its boilers, heat pumps, cylinders,
/// radiators and controls; its fans, ventilation units and vents; its alarms; its consumer unit or panel;
/// its sprinklers; its meters and stop valve; its gas fires and kitchen appliances; its sanitaryware; its
/// drains' access points, soakaways and air admittance valves; its doors, fire doors, windows and roof
/// windows; its gutters and chimneys - each tagged by its mark, or by its kind and a number where it has
/// none, as an asset register lists them. Furniture, planting, people, cars and the like are not assets
/// to keep up; sockets, switches and lights are not listed either, as nothing is done to them in turn.
///
/// How long each lasts is its type's Expected Life where one is given, else the National Association of
/// Home Builders' Study of Life Expectancy of Home Components (February 2007), Table 1 - the one such
/// table published openly; the UK's (BCIS's, CIBSE Guide M's) are sold. Where the study gives a range,
/// the shorter end is planned for.
/// </summary>
public static class Assets
{
    /// <summary>The kind of asset an element is, or null where it is not one kept up.</summary>
    public static AssetKind? KindOf(BimDocument document, Element element)
    {
        switch (element)
        {
            case Component component when document.FindType<ComponentType>(component.TypeId) is { } type:
                return KindOf(type);
            case Door door:
                return document.FindType<DoorType>(door.TypeId) is { } doorType && IsFireRated(doorType.FireRating) ? AssetKind.FireDoor : AssetKind.Door;
            case Window:
                return AssetKind.Window;
            case RoofWindow:
                return AssetKind.RoofWindow;
            case Gutter:
                return AssetKind.Gutter;
            case Chimney:
                return AssetKind.Chimney;
            default:
                return null;
        }
    }

    /// <summary>The kind of asset a fitting of this type is, or null where it is not one kept up.</summary>
    public static AssetKind? KindOf(ComponentType type) => type.Form switch
    {
        ComponentForm.Boiler when Says(type, "oil") => AssetKind.OilBoiler,
        ComponentForm.Boiler when Says(type, "electric") => AssetKind.ElectricBoiler,
        ComponentForm.Boiler => AssetKind.GasBoiler,
        ComponentForm.HeatPump => AssetKind.HeatPump,
        ComponentForm.HotWaterCylinder => AssetKind.HotWaterCylinder,
        ComponentForm.Radiator => AssetKind.Radiator,
        ComponentForm.TowelRail => AssetKind.TowelRail,
        ComponentForm.UnderfloorHeating => AssetKind.UnderfloorHeating,
        ComponentForm.Thermostat => AssetKind.Thermostat,
        ComponentForm.HeatRecoveryUnit => AssetKind.HeatRecoveryUnit,
        ComponentForm.ExtractUnit => AssetKind.ExtractUnit,
        ComponentForm.ExtractFan => AssetKind.ExtractFan,
        ComponentForm.CookerHood => AssetKind.CookerHood,
        ComponentForm.BackgroundVentilator => AssetKind.BackgroundVentilator,
        ComponentForm.SmokeAlarm => AssetKind.SmokeAlarm,
        ComponentForm.HeatAlarm => AssetKind.HeatAlarm,
        ComponentForm.CarbonMonoxideAlarm => AssetKind.CarbonMonoxideAlarm,
        ComponentForm.DuctSmokeDetector => AssetKind.DuctSmokeDetector,
        ComponentForm.ConsumerUnit => AssetKind.ConsumerUnit,
        ComponentForm.SprinklerValve => AssetKind.SprinklerValve,
        ComponentForm.Sprinkler => AssetKind.Sprinkler,
        ComponentForm.GasMeter => AssetKind.GasMeter,
        ComponentForm.StopValve => AssetKind.StopValve,
        ComponentForm.GasFire => AssetKind.GasFire,
        ComponentForm.Cooker => AssetKind.Cooker,
        ComponentForm.Fridge => AssetKind.Fridge,
        ComponentForm.Dishwasher => AssetKind.Dishwasher,
        ComponentForm.WashingMachine => AssetKind.WashingMachine,
        ComponentForm.TumbleDryer => AssetKind.TumbleDryer,
        ComponentForm.Toilet or ComponentForm.WallHungToilet => AssetKind.Toilet,
        ComponentForm.Basin => AssetKind.Basin,
        ComponentForm.Sink => AssetKind.Sink,
        ComponentForm.Bath => AssetKind.Bath,
        ComponentForm.Shower => AssetKind.Shower,
        ComponentForm.DrainAccess or ComponentForm.Gully => AssetKind.DrainAccess,
        ComponentForm.Soakaway => AssetKind.Soakaway,
        ComponentForm.AirAdmittanceValve => AssetKind.AirAdmittanceValve,
        ComponentForm.Custom or ComponentForm.Box when type.Kind is BuiltInCategory.MechanicalEquipment or BuiltInCategory.ElectricalEquipment
            or BuiltInCategory.SpecialtyEquipment or BuiltInCategory.PlumbingFixtures => AssetKind.Equipment,
        _ => null
    };

    /// <summary>Whether a fire rating says the door resists fire: "FD30", "30 min", "20-minute" - not "None" or blank.</summary>
    public static bool IsFireRated(string rating)
    {
        var said = rating.Trim();
        return said.Length > 0 && !said.Equals("none", StringComparison.OrdinalIgnoreCase) && !said.Equals("-", StringComparison.Ordinal) && said.Any(char.IsDigit);
    }

    /// <summary>Whether its name or description has the word, whole: "oil", not the "oil" in "boiler".</summary>
    private static bool Says(ElementType type, string word) =>
        System.Text.RegularExpressions.Regex.IsMatch($"{type.Name} {type.Description}", @"\b" + word + @"\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    public static bool IsAsset(BimDocument document, Element element) => KindOf(document, element) is not null;

    /// <summary>Whether things of this type are assets to keep up: what its type properties give a maker, model, warranty and life for.</summary>
    public static bool IsAssetType(ElementType type) => type switch
    {
        ComponentType component => KindOf(component) is not null,
        DoorType or WindowType or RoofWindowType or GutterType => true,
        _ => false
    };

    /// <summary>A kind as the register says it.</summary>
    public static string Describe(AssetKind kind, bool us) => kind switch
    {
        AssetKind.GasBoiler => us ? "Gas boiler" : "Gas boiler",
        AssetKind.OilBoiler => "Oil boiler",
        AssetKind.ElectricBoiler => "Electric boiler",
        AssetKind.HeatPump => "Heat pump",
        AssetKind.HotWaterCylinder => us ? "Water heater" : "Hot water cylinder",
        AssetKind.Radiator => "Radiator",
        AssetKind.TowelRail => "Towel rail",
        AssetKind.UnderfloorHeating => "Underfloor heating",
        AssetKind.Thermostat => "Thermostat",
        AssetKind.HeatRecoveryUnit => us ? "Heat recovery ventilator" : "Heat recovery unit (MVHR)",
        AssetKind.ExtractUnit => "Central extract unit",
        AssetKind.ExtractFan => us ? "Exhaust fan" : "Extract fan",
        AssetKind.CookerHood => us ? "Range hood" : "Cooker hood",
        AssetKind.BackgroundVentilator => us ? "Trickle vent" : "Background ventilator",
        AssetKind.SmokeAlarm => "Smoke alarm",
        AssetKind.HeatAlarm => "Heat alarm",
        AssetKind.CarbonMonoxideAlarm => "Carbon monoxide alarm",
        AssetKind.DuctSmokeDetector => "Duct smoke detector",
        AssetKind.ConsumerUnit => us ? "Panelboard" : "Consumer unit",
        AssetKind.SprinklerValve => us ? "Sprinkler riser" : "Sprinkler valve set",
        AssetKind.Sprinkler => "Sprinkler",
        AssetKind.GasMeter => "Gas meter",
        AssetKind.StopValve => us ? "Main shutoff valve" : "Stop valve",
        AssetKind.GasFire => us ? "Gas fireplace" : "Gas fire",
        AssetKind.Cooker => us ? "Range" : "Cooker",
        AssetKind.Fridge => us ? "Refrigerator" : "Fridge",
        AssetKind.Dishwasher => "Dishwasher",
        AssetKind.WashingMachine => us ? "Washer" : "Washing machine",
        AssetKind.TumbleDryer => us ? "Dryer" : "Tumble dryer",
        AssetKind.Toilet => us ? "Toilet" : "WC",
        AssetKind.Basin => us ? "Lavatory" : "Basin",
        AssetKind.Sink => "Sink",
        AssetKind.Bath => us ? "Bathtub" : "Bath",
        AssetKind.Shower => "Shower",
        AssetKind.DrainAccess => us ? "Cleanout" : "Drain access",
        AssetKind.Soakaway => us ? "Dry well" : "Soakaway",
        AssetKind.AirAdmittanceValve => "Air admittance valve",
        AssetKind.Door => "Door",
        AssetKind.FireDoor => "Fire door",
        AssetKind.Window => "Window",
        AssetKind.RoofWindow => us ? "Skylight" : "Roof window",
        AssetKind.Gutter => "Gutter",
        AssetKind.Chimney => "Chimney",
        _ => "Equipment"
    };

    /// <summary>The letters an asset of a kind is tagged by where it has no mark of its own.</summary>
    public static string Prefix(AssetKind kind, bool us) => kind switch
    {
        AssetKind.GasBoiler or AssetKind.OilBoiler or AssetKind.ElectricBoiler => "BLR",
        AssetKind.HeatPump => "HP",
        AssetKind.HotWaterCylinder => us ? "WH" : "CYL",
        AssetKind.Radiator => "RAD",
        AssetKind.TowelRail => "TR",
        AssetKind.UnderfloorHeating => "UFH",
        AssetKind.Thermostat => "TS",
        AssetKind.HeatRecoveryUnit => us ? "HRV" : "MVHR",
        AssetKind.ExtractUnit => "MEV",
        AssetKind.ExtractFan => "EF",
        AssetKind.CookerHood => "CH",
        AssetKind.BackgroundVentilator => "BV",
        AssetKind.SmokeAlarm => "SA",
        AssetKind.HeatAlarm => "HA",
        AssetKind.CarbonMonoxideAlarm => "CO",
        AssetKind.DuctSmokeDetector => "DSD",
        AssetKind.ConsumerUnit => us ? "PNL" : "CU",
        AssetKind.SprinklerValve => "SV",
        AssetKind.Sprinkler => "SPK",
        AssetKind.GasMeter => "GM",
        AssetKind.StopValve => "STV",
        AssetKind.GasFire => "GF",
        AssetKind.Cooker => "CKR",
        AssetKind.Fridge => "FR",
        AssetKind.Dishwasher => "DW",
        AssetKind.WashingMachine => "WM",
        AssetKind.TumbleDryer => "TD",
        AssetKind.Toilet => "WC",
        AssetKind.Basin => us ? "LAV" : "WHB",
        AssetKind.Sink => "SNK",
        AssetKind.Bath => "BTH",
        AssetKind.Shower => "SHR",
        AssetKind.DrainAccess => us ? "CO" : "DA",
        AssetKind.Soakaway => "SK",
        AssetKind.AirAdmittanceValve => "AAV",
        AssetKind.Door => "D",
        AssetKind.FireDoor => "FD",
        AssetKind.Window => "W",
        AssetKind.RoofWindow => "RW",
        AssetKind.Gutter => "GUT",
        AssetKind.Chimney => "CHY",
        _ => "EQ"
    };

    /// <summary>
    /// The home's assets, kind by kind, storey by storey from the lowest, then along each storey from
    /// the back left - each tagged by its mark, or by its kind's letters and its number among those of
    /// its kind with no mark.
    /// </summary>
    public static IReadOnlyList<Asset> Of(BimDocument document) =>
        ModelCache.Of(document) is { } kept ? kept.Keep("handover assets", document, null, null, () => Work(document)) : Work(document);

    /// <summary>The assets found, in the register's order, each with its tag: no rooms or systems looked for.</summary>
    private static List<(Element Element, AssetKind Kind, Level? Level, Point2D At, string Tag)> Found(BimDocument document)
    {
        var us = document.ProjectInformation.ClearanceStandard.IsUs();
        var found = document.Elements
            .Select(element => (Element: element, Kind: KindOf(document, element)))
            .Where(entry => entry.Kind is not null && entry.Element.PhaseDemolished is null && entry.Element.DesignOptionId is null)
            .Select(entry => (entry.Element, Kind: entry.Kind!.Value, Level: document.FindLevel(entry.Element.LevelId), At: PositionOf(document, entry.Element)))
            .OrderBy(entry => entry.Kind)
            .ThenBy(entry => entry.Level?.Elevation ?? 0)
            .ThenByDescending(entry => Math.Round(entry.At.Y / 500))
            .ThenBy(entry => entry.At.X)
            .ToList();

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var tagged = new List<(Element, AssetKind, Level?, Point2D, string)>();
        foreach (var (element, kind, level, at) in found)
        {
            string tag;
            // A mark that is only a number - doors and windows are numbered each on their own - takes its kind's letters, so door 1 and window 1 are told apart.
            if (!string.IsNullOrWhiteSpace(element.Mark)) tag = element.Mark.Trim().All(char.IsDigit) ? $"{Prefix(kind, us)}-{element.Mark.Trim()}" : element.Mark.Trim();
            else
            {
                var prefix = Prefix(kind, us);
                counts[prefix] = counts.GetValueOrDefault(prefix) + 1;
                tag = $"{prefix}-{counts[prefix]:00}";
            }

            tagged.Add((element, kind, level, at, tag));
        }

        return tagged;
    }

    /// <summary>The tag an asset is known by in the register, or its mark where it is not one.</summary>
    public static string TagOf(BimDocument document, Element element)
    {
        var tags = ModelCache.Of(document) is { } kept
            ? kept.Keep("handover tags", document, null, null, () => Found(document).ToDictionary(entry => entry.Element.Id, entry => entry.Tag))
            : Found(document).ToDictionary(entry => entry.Element.Id, entry => entry.Tag);
        return tags.GetValueOrDefault(element.Id) ?? element.Mark;
    }

    private static IReadOnlyList<Asset> Work(BimDocument document)
    {
        var us = document.ProjectInformation.ClearanceStandard.IsUs();
        var found = Found(document);
        var systems = SystemsOf(document);
        var rooms = document.All<Room>()
            .Select(room => (Room: room, Boundary: room.GetBoundary(document)))
            .Where(entry => entry.Boundary.IsEnclosed)
            .ToList();
        string RoomOf(Element element, Point2D at)
        {
            var reach = element is Opening or RoofWindow ? 400 : 0;
            var here = rooms.Where(entry => entry.Room.LevelId == element.LevelId).ToList();
            var room = here.FirstOrDefault(entry => Polygon2D.Contains(entry.Boundary.Polygon, at)).Room
                       ?? (reach > 0 ? here.FirstOrDefault(entry => Near(entry.Boundary.Polygon, at, reach)).Room : null);
            return room is null ? string.Empty : string.IsNullOrWhiteSpace(room.Number) ? room.Name : $"{room.Number} {room.Name}";
        }

        var assets = new List<Asset>();
        foreach (var (element, kind, level, at, tag) in found)
        {
            var type = document.ElementTypes.FirstOrDefault(candidate => candidate.Id == element.TypeId);
            assets.Add(new Asset(element, type, kind, tag, type?.Name ?? Describe(kind, us), level, RoomOf(element, at), at,
                systems.TryGetValue(element.Id, out var on) ? on : Array.Empty<string>()));
        }

        return assets;
    }

    private static bool Near(IReadOnlyList<Point2D> polygon, Point2D point, double reach)
    {
        for (var i = 0; i < polygon.Count; i++)
            if (Line2D.DistanceFromSegment(point, polygon[i], polygon[(i + 1) % polygon.Count]) <= reach) return true;
        return false;
    }

    /// <summary>Where an element stands on its storey's plan: a fitting's insertion point, an opening's middle in its wall, else the middle of what it is drawn by.</summary>
    public static Point2D PositionOf(BimDocument document, Element element)
    {
        switch (element)
        {
            case Component component:
                return component.Location;
            case Opening opening when document.Find<Wall>(opening.HostWallId) is { } wall:
                return opening.GetCentre(wall);
            case RoofWindow window:
                return window.Location;
            case Chimney chimney:
                return chimney.Location;
            default:
                var points = ElementTransforms.PlanPoints(element);
                return points.Count == 0 ? default : new Point2D(points.Average(point => point.X), points.Average(point => point.Y));
        }
    }

    /// <summary>The systems each fitting is on, by its id: its source's and its members'.</summary>
    private static Dictionary<Guid, IReadOnlyList<string>> SystemsOf(BimDocument document)
    {
        var on = new Dictionary<Guid, List<string>>();
        foreach (var system in document.All<Mep.MepSystem>().OrderBy(system => system.Name, StringComparer.OrdinalIgnoreCase))
        {
            var members = Mep.MepSystems.MembersOf(document, system).Select(member => member.Id).ToList();
            if (Mep.MepSystems.SourceOf(document, system) is { } source) members.Insert(0, source.Id);
            foreach (var id in members.Distinct())
            {
                if (!on.TryGetValue(id, out var names)) on[id] = names = new List<string>();
                if (!names.Contains(system.Name)) names.Add(system.Name);
            }
        }

        return on.ToDictionary(entry => entry.Key, entry => (IReadOnlyList<string>)entry.Value);
    }

    /// <summary>The day an asset was installed: as recorded, else the day the home was handed over, else not known.</summary>
    public static DateOnly? InstalledOn(BimDocument document, Asset asset) => asset.Record.InstalledOn ?? document.Handover.HandedOver;

    /// <summary>The day an asset's parts warranty runs out, where its type gives how long it is for.</summary>
    public static DateOnly? WarrantyEnds(BimDocument document, Asset asset)
    {
        var years = asset.Type?.PartsWarranty ?? 0;
        if (years <= 0 || (asset.Record.WarrantyStart ?? InstalledOn(document, asset)) is not { } start) return null;
        return start.AddMonths((int)Math.Round(years * 12));
    }

    /// <summary>How long an asset lasts: its type's Expected Life where given, else the study's figure for its kind.</summary>
    public static ServiceLife LifeOf(BimDocument document, Asset asset)
    {
        if (asset.Type is { ExpectedLife: > 0 } type)
            return ServiceLife.For(type.ExpectedLife, $"{type.ExpectedLife.ToString("0.#", CultureInfo.CurrentCulture)} years", "its type's Expected Life");
        return StandardLife(document, asset);
    }

    private const string Nahb = "NAHB, Study of Life Expectancy of Home Components (2007), Table 1";

    /// <summary>The study's life for an asset's kind, or none where it gives none.</summary>
    public static ServiceLife StandardLife(BimDocument document, Asset asset)
    {
        var name = asset.Type?.Name ?? string.Empty;
        bool Named(string word) => name.Contains(word, StringComparison.OrdinalIgnoreCase);
        ServiceLife Years(double years, string said) => ServiceLife.For(years, said, Nahb);
        switch (asset.Kind)
        {
            case AssetKind.GasBoiler: return Years(21, "21 years (gas boilers)");
            case AssetKind.OilBoiler: return Years(13, "13-21 years (boilers)");
            case AssetKind.ElectricBoiler: return Years(13, "13 years (electric boilers)");
            case AssetKind.HeatPump: return Years(16, "16 years (heat pumps)");
            case AssetKind.HotWaterCylinder:
                return asset.Type is ComponentType { GasInput: > 0 } ? Years(10, "10 years (gas water heaters)") : Years(11, "11 years (electric water heaters)");
            case AssetKind.Radiator or AssetKind.TowelRail: return Years(15, "15+ years (hot water radiant heaters)");
            case AssetKind.Thermostat: return Years(35, "35 years (thermostats)");
            case AssetKind.HeatRecoveryUnit: return Years(20, "20+ years (heat recovery ventilators)");
            case AssetKind.ExtractUnit or AssetKind.ExtractFan: return Years(10, "10 years (exhaust fans)");
            case AssetKind.CookerHood: return Years(14, "14 years (range and oven hoods)");
            case AssetKind.SmokeAlarm or AssetKind.HeatAlarm or AssetKind.DuctSmokeDetector:
                return ServiceLife.For(10, "10 years from manufacture", document.ProjectInformation.ClearanceStandard.IsUs()
                    ? "NFPA 72 10.4.6; NAHB (2007), Table 1: under 10 years" : "the makers' replace-by date; NAHB (2007), Table 1: under 10 years");
            case AssetKind.CarbonMonoxideAlarm:
                return ServiceLife.For(7, "7 or 10 years, by the unit", "BS EN 50291-1: an end-of-life warning, 7- and 10-year units made");
            case AssetKind.Cooker:
                return asset.Type is ComponentType { GasInput: > 0 } ? Years(15, "15 years (gas ranges)") : Years(13, "13 years (electric ranges)");
            case AssetKind.Fridge: return Years(13, "13 years (refrigerators)");
            case AssetKind.Dishwasher: return Years(9, "9 years (dishwashers)");
            case AssetKind.WashingMachine: return Years(10, "10 years (washers)");
            case AssetKind.TumbleDryer: return Years(13, "13 years (dryers)");
            case AssetKind.Toilet: return ServiceLife.ForLife(Nahb + ": toilets lifetime; the cistern's fittings maintained");
            case AssetKind.Basin: return Years(20, "20+ years (lavatory faucets)");
            case AssetKind.Sink: return Years(15, "15 years (kitchen sink faucets)");
            case AssetKind.Bath: return Years(20, "20+ years (tub and shower faucets)");
            case AssetKind.Shower: return Years(20, "20+ years (shower doors)");
            case AssetKind.Door:
                return Named("upvc") || Named("vinyl") ? Years(20, "20 years (vinyl doors)")
                    : Named("french") ? Years(30, "30-50 years (French doors)")
                    : ServiceLife.ForLife(Nahb + ": exterior fibreglass, steel and wood doors and closet doors lifetime");
            case AssetKind.FireDoor: return ServiceLife.ForLife(Nahb + ": fire-rated steel doors lifetime");
            case AssetKind.Window or AssetKind.RoofWindow:
                var frame = asset.Type is OpeningType opening ? opening.FrameMaterial : string.Empty;
                bool Frame(string word) => frame.Contains(word, StringComparison.OrdinalIgnoreCase) || Named(word);
                return Frame("alumin") ? Years(15, "15-20 years (aluminium windows)")
                    : Frame("timber") || Frame("wood") || asset.Kind == AssetKind.RoofWindow ? Years(30, "30+ years (wood windows)")
                    : ServiceLife.NotGiven with { Said = "not given (the study has no uPVC windows)" };
            case AssetKind.Gutter:
                return Named("copper") ? Years(50, "50+ years (copper gutters)") : Named("alumin") ? Years(20, "20 years (aluminium gutters)") : ServiceLife.NotGiven;
            case AssetKind.Chimney: return ServiceLife.ForLife(Nahb + ": chimneys lifetime");
            default: return ServiceLife.NotGiven;
        }
    }

    /// <summary>The year an asset is due to be replaced: installed and its life on, or none for one that lasts the building or has no life given.</summary>
    public static DateOnly? ReplaceBy(BimDocument document, Asset asset) =>
        LifeOf(document, asset) is { Years: { } years } && InstalledOn(document, asset) is { } installed ? installed.AddMonths((int)Math.Round(years * 12)) : null;

    /// <summary>Whether an asset is one a serial number is looked for on: equipment, appliances and alarms - not a door, a window, a gutter or a basin.</summary>
    public static bool HasSerial(AssetKind kind) => kind is not (AssetKind.Door or AssetKind.FireDoor or AssetKind.Window or AssetKind.RoofWindow or AssetKind.Gutter
        or AssetKind.Chimney or AssetKind.Toilet or AssetKind.Basin or AssetKind.Sink or AssetKind.Bath or AssetKind.Radiator or AssetKind.TowelRail
        or AssetKind.DrainAccess or AssetKind.Soakaway or AssetKind.AirAdmittanceValve or AssetKind.BackgroundVentilator or AssetKind.StopValve or AssetKind.Sprinkler);

    /// <summary>What a handover still wants of an asset: its maker, its model, its serial number, the day it was installed.</summary>
    public static IReadOnlyList<string> Missing(BimDocument document, Asset asset)
    {
        var missing = new List<string>();
        if (asset.Maker.Length == 0) missing.Add("maker");
        if (asset.Model.Length == 0) missing.Add("model");
        if (HasSerial(asset.Kind) && asset.Record.SerialNumber.Trim().Length == 0) missing.Add("serial number");
        if (InstalledOn(document, asset) is null) missing.Add("installation date");
        return missing;
    }

    /// <summary>
    /// Writes each asset's tag into its mark where it has none, as one step: so the tag on the label,
    /// on the drawings and in the register is the same. Null where every asset has a mark already.
    /// </summary>
    public static IUndoableCommand? TagCommand(BimDocument document)
    {
        var untagged = Of(document).Where(asset => string.IsNullOrWhiteSpace(asset.Element.Mark)).ToList();
        if (untagged.Count == 0) return null;
        return new CompositeCommand($"Tag {untagged.Count} Asset{(untagged.Count == 1 ? string.Empty : "s")}",
            untagged.Select(asset => (IUndoableCommand)new MarkCommand(asset.Element, asset.Tag)).ToList());
    }

    private sealed class MarkCommand(Element element, string mark) : IUndoableCommand
    {
        private readonly string _was = element.Mark;

        public string Name => "Tag Asset";

        public void Redo() => element.Mark = mark;

        public void Undo() => element.Mark = _was;
    }

    /// <summary>A date as typed - "2026-11-03", "3/11/2026", "3 Nov 2026" - or null for nothing, or what is not one.</summary>
    public static DateOnly? ParseDate(string? text)
    {
        var said = (text ?? string.Empty).Trim();
        if (said.Length == 0) return null;
        if (DateOnly.TryParseExact(said, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var iso)) return iso;
        if (DateOnly.TryParse(said, CultureInfo.CurrentCulture, DateTimeStyles.None, out var date)) return date;

        // As the project's own country writes it, where this computer's does not read it: "15/01/2026" on a US-set machine.
        foreach (var culture in new[] { "en-GB", "en-US" })
            if (DateOnly.TryParse(said, CultureInfo.GetCultureInfo(culture), DateTimeStyles.None, out var other)) return other;
        return null;
    }

    /// <summary>A date as the handover writes it: "3 Nov 2026".</summary>
    public static string Said(DateOnly? date) => date?.ToString("d MMM yyyy", CultureInfo.CurrentCulture) ?? string.Empty;
}

/// <summary>The handover's parameters: each asset's own, and its type's.</summary>
public static class AssetParameters
{
    public static readonly ParameterDefinition SerialNumber = new("Serial Number", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Handover);
    public static readonly ParameterDefinition BarCode = new("Bar Code", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Handover);
    public static readonly ParameterDefinition InstalledOn = new("Installation Date", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Handover);
    public static readonly ParameterDefinition WarrantyStart = new("Warranty Start Date", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Handover);
    public static readonly ParameterDefinition Tag = new("Asset Tag", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Handover);
    public static readonly ParameterDefinition Kind = new("Asset Kind", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Handover);

    public static readonly ParameterDefinition ModelNumber = new("Model Number", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.Handover);
    public static readonly ParameterDefinition PartsWarranty = new("Warranty, Parts (years)", ParameterDataType.Number, ParameterBinding.Type, ParameterGroup.Handover);
    public static readonly ParameterDefinition LabourWarranty = new("Warranty, Labour (years)", ParameterDataType.Number, ParameterBinding.Type, ParameterGroup.Handover);
    public static readonly ParameterDefinition Guarantor = new("Warranty Guarantor (email)", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.Handover);
    public static readonly ParameterDefinition ExpectedLife = new("Expected Life (years, 0 = standard)", ParameterDataType.Number, ParameterBinding.Type, ParameterGroup.Handover);

    /// <summary>An asset's own: its serial number, bar code and dates, editable; its tag and kind, said.</summary>
    public static IEnumerable<ParameterValue> Of(BimDocument document, Element element)
    {
        if (Assets.KindOf(document, element) is not { } kind) yield break;
        var us = document.ProjectInformation.ClearanceStandard.IsUs();
        yield return ParameterValue.ReadOnly(Kind, () => Assets.Describe(kind, us));
        if (Assets.HasSerial(kind))
            yield return ParameterValue.Bind(SerialNumber, () => element.Asset.SerialNumber, v => element.Asset = element.Asset with { SerialNumber = (v ?? string.Empty).Trim() });
        yield return ParameterValue.Bind(BarCode, () => element.Asset.BarCode, v => element.Asset = element.Asset with { BarCode = (v ?? string.Empty).Trim() });
        yield return ParameterValue.Bind(InstalledOn, () => Assets.Said(element.Asset.InstalledOn), v => element.Asset = element.Asset with { InstalledOn = Assets.ParseDate(v) });
        yield return ParameterValue.Bind(WarrantyStart, () => Assets.Said(element.Asset.WarrantyStart), v => element.Asset = element.Asset with { WarrantyStart = Assets.ParseDate(v) });
    }

    /// <summary>A type's: its model, warranty and life.</summary>
    public static IEnumerable<ParameterValue> Of(ElementType type)
    {
        if (!Assets.IsAssetType(type)) yield break;
        yield return ParameterValue.Bind(ModelNumber, () => type.ModelNumber, v => type.ModelNumber = (v ?? string.Empty).Trim());
        yield return ParameterValue.Bind(PartsWarranty, () => type.PartsWarranty, v => type.PartsWarranty = Math.Clamp(v, 0, 50));
        yield return ParameterValue.Bind(LabourWarranty, () => type.LabourWarranty, v => type.LabourWarranty = Math.Clamp(v, 0, 50));
        yield return ParameterValue.Bind(Guarantor, () => type.WarrantyGuarantor, v => type.WarrantyGuarantor = (v ?? string.Empty).Trim());
        yield return ParameterValue.Bind(ExpectedLife, () => type.ExpectedLife, v => type.ExpectedLife = Math.Clamp(v, 0, 200));
    }
}
