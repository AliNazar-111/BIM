using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Parameters;

namespace BIMDesigner.Core.Coordination;

/// <summary>What a type's cost is for: each one, each metre, each square metre or each cubic metre of it.</summary>
public enum CostUnit
{
    Item,
    Length,
    Area,
    Volume
}

/// <summary>One element costed: its cost element, how much of it there is, its rate and what it comes to.</summary>
public sealed record CostLine(Element Element, ElementType? Type, string Code, string Title, double Quantity, CostUnit Unit, decimal Rate, decimal Total);

/// <summary>A line of a cost plan: a cost element with its lines, a group's subtotal, or an addition on what comes before.</summary>
public sealed record CostRow(string Code, string Title, decimal Amount, IReadOnlyList<CostLine> Lines, bool IsTotal = false, double? Percent = null);

/// <summary>A cost plan: its rows in order, its total, the floor area it is spread over and so its cost a square metre.</summary>
public sealed record CostSummary(IReadOnlyList<CostRow> Rows, decimal Total, double FloorArea, string Currency)
{
    public decimal PerSquareMetre => FloorArea > 0 ? Total / (decimal)FloorArea : 0;
}

/// <summary>Earned value at a date: what was planned to be done by then, what has been, and what it has cost - and what follows.</summary>
public sealed record EarnedValue(DateOnly StatusDate, decimal Budget, decimal Planned, decimal Earned, decimal Actual)
{
    public decimal ScheduleVariance => Earned - Planned;

    public decimal CostVariance => Earned - Actual;

    public double SchedulePerformance => Planned > 0 ? (double)(Earned / Planned) : 0;

    public double CostPerformance => Actual > 0 ? (double)(Earned / Actual) : 0;

    public decimal EstimateAtCompletion => CostPerformance > 0 ? Budget / (decimal)CostPerformance : Budget;

    public decimal VarianceAtCompletion => Budget - EstimateAtCompletion;
}

/// <summary>The additions a cost plan carries on its building works, in percent, and what it is counted in.</summary>
public sealed class CostSettings
{
    public double Preliminaries { get; set; } = 12;

    public double OverheadsAndProfit { get; set; } = 5;

    public double DesignFees { get; set; }

    public decimal OtherCosts { get; set; }

    public double Risk { get; set; } = 5;

    public double Inflation { get; set; }

    public double Tax { get; set; }

    /// <summary>The currency's symbol; none set, the project's standard's - £ or $.</summary>
    public string Currency { get; set; } = string.Empty;
}

/// <summary>
/// The model costed (5D): each element measured as its type's rate is given - by the item, the
/// metre, the square metre or the cubic metre - and set out by element as the project's standard
/// has a cost plan: in England the RICS's New Rules of Measurement (NRM1) - 0 facilitating works,
/// 1 substructure, 2 superstructure with its frame, upper floors, roof, stairs, external walls,
/// windows and external doors, internal walls and internal doors, 3 internal finishes, 4
/// fittings, 5 services, 8 external works - then the main contractor's preliminaries, overheads
/// and profit, fees, other costs, risk and inflation, VAT outside it; in the US ASTM E1557's
/// UniFormat II - A substructure, B shell, C interiors, D services, E equipment and furnishings,
/// F demolition, G sitework - then general conditions, overhead and profit, fees, contingency,
/// escalation and tax. A wall is measured net of its doors and windows, which are counted
/// themselves; what stays of an existing building is not costed, what is demolished is.
/// </summary>
public static class CostPlanning
{
    /// <summary>What a category's cost is for: walls, floors, roofs and ceilings by the square metre; beams, railings and what runs along an edge by the metre; the rest each.</summary>
    public static CostUnit UnitOf(Element element) => element.Category switch
    {
        BuiltInCategory.Walls or BuiltInCategory.Floors or BuiltInCategory.Roofs or BuiltInCategory.Ceilings => CostUnit.Area,
        BuiltInCategory.StructuralFraming or BuiltInCategory.Railings or BuiltInCategory.WallSweeps or BuiltInCategory.WallReveals or
            BuiltInCategory.Fascias or BuiltInCategory.Gutters or BuiltInCategory.RoofSoffits or BuiltInCategory.Eaves or
            BuiltInCategory.SlabEdges or BuiltInCategory.Downpipes or BuiltInCategory.Ducts or BuiltInCategory.Pipes => CostUnit.Length,
        BuiltInCategory.StructuralFoundations when element is WallFooting => CostUnit.Length,
        _ => CostUnit.Item
    };

    /// <summary>The unit's short name: nr, m, m², m³.</summary>
    public static string Abbreviation(CostUnit unit) => unit switch
    {
        CostUnit.Length => "m",
        CostUnit.Area => "m²",
        CostUnit.Volume => "m³",
        _ => "nr"
    };

    /// <summary>How much of an element there is in a unit, mm, mm² or mm³ - or one.</summary>
    public static double Measure(BimDocument document, Element element, CostUnit unit)
    {
        if (unit == CostUnit.Item) return 1;

        // A wall net of the doors and windows in it - they are costed themselves.
        if (unit == CostUnit.Area && element is Wall wall)
        {
            var gross = wall.GetArea(document);
            if (document.IsCurtainWall(wall)) return gross;
            var holes = WallOpenings.Of(document, wall)
                .Select(opening => (opening, type: document.FindType<OpeningType>(opening.TypeId)))
                .Where(entry => entry.type is not null)
                .Sum(entry => entry.opening.WidthOf(entry.type!) * entry.type!.Height);
            return Math.Max(0, gross - holes);
        }

        if (unit == CostUnit.Length && element is WallFooting footing && document.Find<Wall>(footing.WallId) is { } under)
            return under.Length;

        var name = unit switch { CostUnit.Length => "Length", CostUnit.Area => "Area", _ => "Volume" };
        var dataType = unit switch { CostUnit.Length => ParameterDataType.Length, CostUnit.Area => ParameterDataType.Area, _ => ParameterDataType.Volume };
        foreach (var parameter in element.GetInstanceParameters(document))
            if (parameter.Definition.DataType == dataType && parameter.Definition.Name == name && parameter.Value is { } value)
                return Convert.ToDouble(value);
        return 0;
    }

    /// <summary>A measured quantity in its unit's metres: m, m², m³ - or a count.</summary>
    public static double InUnits(double measured, CostUnit unit) => unit switch
    {
        CostUnit.Length => measured / 1000,
        CostUnit.Area => measured / 1e6,
        CostUnit.Volume => measured / 1e9,
        _ => measured
    };

    /// <summary>Whether an element is built - or taken down - in this work: new, or existing and demolished. Not what stays, nor what is to come later.</summary>
    public static bool Costed(Element element) =>
        element.PhaseCreated is DesignPhase.New or DesignPhase.Temporary || element.PhaseCreated == DesignPhase.Existing && element.PhaseDemolished is not null;

    /// <summary>What an element costs: its quantity at its type's rate.</summary>
    public static decimal CostOf(BimDocument document, Element element)
    {
        if (document.FindType<ElementType>(element.TypeId) is not { } type || type.Cost == 0) return 0;
        var unit = UnitOf(element);
        return (decimal)InUnits(Measure(document, element, unit), unit) * type.Cost;
    }

    // ---- cost elements ---------------------------------------------------------------

    private static bool OnLowest(BimDocument document, Element element) =>
        document.Levels.Count > 0 && element.LevelId == document.Levels.OrderBy(level => level.Elevation).First().Id;

    private static bool Outside(BimDocument document, Element element)
    {
        var wall = element as Wall ?? (element is Opening opening ? document.Find<Wall>(opening.HostWallId) : null);
        if (wall is null) return false;
        return document.IsCurtainWall(wall)
            ? document.FindType<CurtainWallType>(wall.TypeId)?.Function != WallFunction.Interior
            : document.GetWallType(wall)?.Function is WallFunction.Exterior or WallFunction.Retaining or WallFunction.Foundation;
    }

    /// <summary>NRM1's element for an element: its number and title.</summary>
    public static (string Code, string Title) Nrm1(BimDocument document, Element element)
    {
        if (element.PhaseCreated == DesignPhase.Existing) return ("0.2", "Major demolition works");
        if (element.PhaseCreated == DesignPhase.Temporary) return ("9.1", "Temporary works");
        return element.Category switch
        {
            BuiltInCategory.StructuralFoundations or BuiltInCategory.Pads => ("1.1", "Substructure"),
            BuiltInCategory.Floors when OnLowest(document, element) => ("1.1", "Substructure"),
            BuiltInCategory.SlabEdges when OnLowest(document, element) => ("1.1", "Substructure"),
            BuiltInCategory.Columns or BuiltInCategory.StructuralColumns or BuiltInCategory.StructuralFraming => ("2.1", "Frame"),
            BuiltInCategory.Floors or BuiltInCategory.SlabEdges => ("2.2", "Upper floors"),
            BuiltInCategory.Roofs or BuiltInCategory.Fascias or BuiltInCategory.Gutters or BuiltInCategory.RoofSoffits or BuiltInCategory.Eaves or
                BuiltInCategory.Downpipes or BuiltInCategory.RoofDrains or BuiltInCategory.RoofWindows or BuiltInCategory.Chimneys => ("2.3", "Roof"),
            BuiltInCategory.Stairs or BuiltInCategory.Ramps or BuiltInCategory.Railings => ("2.4", "Stairs and ramps"),
            BuiltInCategory.Walls when Outside(document, element) => ("2.5", "External walls"),
            BuiltInCategory.WallSweeps or BuiltInCategory.WallReveals => ("2.5", "External walls"),
            BuiltInCategory.Windows or BuiltInCategory.CurtainPanels => ("2.6", "Windows and external doors"),
            BuiltInCategory.Doors when Outside(document, element) => ("2.6", "Windows and external doors"),
            BuiltInCategory.Walls => ("2.7", "Internal walls and partitions"),
            BuiltInCategory.Doors => ("2.8", "Internal doors"),
            BuiltInCategory.Ceilings => ("3.3", "Ceiling finishes"),
            BuiltInCategory.Furniture or BuiltInCategory.Casework or BuiltInCategory.SpecialtyEquipment or BuiltInCategory.GenericModels => ("4.1", "Fittings, furnishings and equipment"),
            BuiltInCategory.PlumbingFixtures => ("5.1", "Sanitary installations"),
            BuiltInCategory.Pipes when element is Mep.Pipe drain && Mep.Drainage.IsDrainage(drain.Kind) && Mep.Drainage.BelowGround(document, drain) => ("8.6", "External drainage"),
            BuiltInCategory.Pipes when element is Mep.Pipe { Kind: Mep.SystemKind.Foul or Mep.SystemKind.Rainwater } => ("5.3", "Disposal installations"),
            BuiltInCategory.Pipes when element is Mep.Pipe { Kind: Mep.SystemKind.Heating } => ("5.6", "Space heating and air conditioning"),
            BuiltInCategory.Pipes when element is Mep.Pipe { Kind: Mep.SystemKind.Gas } => ("5.9", "Fuel installations"),
            BuiltInCategory.Pipes => ("5.4", "Water installations"),
            BuiltInCategory.MechanicalEquipment when element is Component unit && document.FindType<ComponentType>(unit.TypeId)?.Form == ComponentForm.HeatRecoveryUnit => ("5.7", "Ventilation"),
            BuiltInCategory.MechanicalEquipment when element is Component source && document.FindType<ComponentType>(source.TypeId)?.Form is ComponentForm.Boiler or ComponentForm.HeatPump => ("5.5", "Heat source"),
            BuiltInCategory.MechanicalEquipment when element is Component cylinder && document.FindType<ComponentType>(cylinder.TypeId)?.Form == ComponentForm.HotWaterCylinder => ("5.4", "Water installations"),
            BuiltInCategory.WallOpenings or BuiltInCategory.ShaftOpenings => ("5.14", "Builder's work in connection with services"),
            BuiltInCategory.MechanicalEquipment => ("5.6", "Space heating and air conditioning"),
            BuiltInCategory.Ducts => ("5.7", "Ventilation"),
            BuiltInCategory.AirTerminals => ("5.7", "Ventilation"),
            BuiltInCategory.ElectricalEquipment or BuiltInCategory.ElectricalFixtures or BuiltInCategory.LightingFixtures or BuiltInCategory.LightingDevices
                or BuiltInCategory.ElectricalCircuits => ("5.8", "Electrical installations"),
            BuiltInCategory.FireAlarmDevices => ("5.12", "Communication, security and control systems"),
            BuiltInCategory.Drainage => ("8.6", "External drainage"),
            BuiltInCategory.Parking => ("8.2", "Roads, paths, pavings and surfacings"),
            BuiltInCategory.Planting => ("8.3", "Soft landscaping, planting and irrigation systems"),
            _ => ("4.1", "Fittings, furnishings and equipment")
        };
    }

    /// <summary>UniFormat II's element for an element: its code and title - from its type's assembly code where it has one.</summary>
    public static (string Code, string Title) UniFormat(BimDocument document, Element element)
    {
        if (element.PhaseCreated == DesignPhase.Existing) return ("F2010", "Building Elements Demolition");
        if (element.PhaseCreated == DesignPhase.Temporary) return ("Z1010", "General Requirements");
        var code = element.Category switch
        {
            BuiltInCategory.StructuralFoundations => "A1010",
            BuiltInCategory.Floors when OnLowest(document, element) => "A1030",
            BuiltInCategory.SlabEdges when OnLowest(document, element) => "A1030",
            BuiltInCategory.Pads => "G1030",
            BuiltInCategory.Columns or BuiltInCategory.StructuralColumns or BuiltInCategory.StructuralFraming or BuiltInCategory.Floors or BuiltInCategory.SlabEdges => "B1010",
            BuiltInCategory.Roofs or BuiltInCategory.Fascias or BuiltInCategory.Gutters or BuiltInCategory.RoofSoffits or BuiltInCategory.Eaves or
                BuiltInCategory.Downpipes or BuiltInCategory.RoofDrains => "B3010",
            BuiltInCategory.RoofWindows => "B3020",
            BuiltInCategory.Chimneys or BuiltInCategory.WallSweeps or BuiltInCategory.WallReveals => "B2010",
            BuiltInCategory.Walls when element is Wall wall && document.IsCurtainWall(wall) && Outside(document, element) => "B2020",
            BuiltInCategory.Walls when Outside(document, element) => "B2010",
            BuiltInCategory.Windows or BuiltInCategory.CurtainPanels => "B2020",
            BuiltInCategory.Doors when Outside(document, element) => "B2030",
            BuiltInCategory.Walls => "C1010",
            BuiltInCategory.Doors => "C1020",
            BuiltInCategory.Stairs or BuiltInCategory.Ramps or BuiltInCategory.Railings => "C2010",
            BuiltInCategory.Ceilings => "C3030",
            BuiltInCategory.PlumbingFixtures => "D2010",
            BuiltInCategory.Pipes when element is Mep.Pipe { Kind: Mep.SystemKind.Rainwater } => "G3030",
            BuiltInCategory.Pipes when element is Mep.Pipe { Kind: Mep.SystemKind.Foul } => "D2030",
            BuiltInCategory.Pipes when element is Mep.Pipe { Kind: Mep.SystemKind.Heating } => "D3040",
            BuiltInCategory.Pipes when element is Mep.Pipe { Kind: Mep.SystemKind.Gas } => "D2090",
            BuiltInCategory.Pipes => "D2020",
            BuiltInCategory.Ducts => "D3040",
            BuiltInCategory.AirTerminals => "D3040",
            BuiltInCategory.MechanicalEquipment when element is Component source && document.FindType<ComponentType>(source.TypeId)?.Form is ComponentForm.Boiler or ComponentForm.HeatPump => "D3020",
            BuiltInCategory.MechanicalEquipment => "D3050",
            BuiltInCategory.WallOpenings or BuiltInCategory.ShaftOpenings => "D3090",
            BuiltInCategory.ElectricalEquipment => "D5010",
            BuiltInCategory.ElectricalFixtures or BuiltInCategory.LightingFixtures or BuiltInCategory.LightingDevices or BuiltInCategory.ElectricalCircuits => "D5020",
            BuiltInCategory.FireAlarmDevices => "D5030",
            BuiltInCategory.Drainage => "G3020",
            BuiltInCategory.Casework => "E2010",
            BuiltInCategory.Furniture => "E2020",
            BuiltInCategory.SpecialtyEquipment or BuiltInCategory.GenericModels => "E1090",
            BuiltInCategory.Parking => "G2020",
            BuiltInCategory.Planting => "G2050",
            _ => "E1090"
        };

        // A type's own UniFormat code where it gives one - a letter and four figures - within the
        // same group: a roof's covering or its structure, not a ground slab's type taken for a floor's.
        if (document.FindType<ElementType>(element.TypeId)?.AssemblyCode is { Length: 5 } given && char.IsLetter(given[0]) && given[1..].All(char.IsDigit) &&
            UniFormatTitles.ContainsKey(given.ToUpperInvariant()) && char.ToUpperInvariant(given[0]) == code[0])
            code = given.ToUpperInvariant();
        return (code, UniFormatTitles.GetValueOrDefault(code, code));
    }

    /// <summary>UniFormat II's level 3 elements used here, and its level 1 groups.</summary>
    public static readonly IReadOnlyDictionary<string, string> UniFormatTitles = new Dictionary<string, string>
    {
        ["A1010"] = "Standard Foundations", ["A1020"] = "Special Foundations", ["A1030"] = "Slab on Grade",
        ["B1010"] = "Floor Construction", ["B1020"] = "Roof Construction", ["B2010"] = "Exterior Walls", ["B2020"] = "Exterior Windows",
        ["B2030"] = "Exterior Doors", ["B3010"] = "Roof Coverings", ["B3020"] = "Roof Openings",
        ["C1010"] = "Partitions", ["C1020"] = "Interior Doors", ["C1030"] = "Fittings", ["C2010"] = "Stair Construction", ["C2020"] = "Stair Finishes",
        ["C3010"] = "Wall Finishes", ["C3020"] = "Floor Finishes", ["C3030"] = "Ceiling Finishes",
        ["D2010"] = "Plumbing Fixtures", ["D2020"] = "Domestic Water Distribution", ["D2030"] = "Sanitary Waste", ["D2090"] = "Other Plumbing Systems",
        ["D3020"] = "Heat Generating Systems", ["D3040"] = "Distribution Systems", ["D3050"] = "Terminal and Package Units", ["D3090"] = "Other HVAC Systems and Equipment",
        ["G3020"] = "Sanitary Sewer", ["G3030"] = "Storm Sewer",
        ["D5010"] = "Electrical Service and Distribution", ["D5020"] = "Lighting and Branch Wiring", ["D5030"] = "Communications and Security",
        ["E1090"] = "Other Equipment", ["E2010"] = "Fixed Furnishings", ["E2020"] = "Movable Furnishings",
        ["F2010"] = "Building Elements Demolition", ["G1030"] = "Site Earthwork", ["G2020"] = "Parking Lots", ["G2050"] = "Landscaping",
        ["Z1010"] = "General Requirements"
    };

    private static readonly IReadOnlyDictionary<string, string> Nrm1Groups = new Dictionary<string, string>
    {
        ["0"] = "Facilitating works", ["1"] = "Substructure", ["2"] = "Superstructure", ["3"] = "Internal finishes",
        ["4"] = "Fittings, furnishings and equipment", ["5"] = "Services", ["6"] = "Prefabricated buildings and building units",
        ["7"] = "Work to existing buildings", ["8"] = "External works", ["9"] = "Main contractor's preliminaries"
    };

    private static readonly IReadOnlyDictionary<string, string> UniFormatGroups = new Dictionary<string, string>
    {
        ["A"] = "Substructure", ["B"] = "Shell", ["C"] = "Interiors", ["D"] = "Services", ["E"] = "Equipment and Furnishings",
        ["F"] = "Special Construction and Demolition", ["G"] = "Building Sitework", ["Z"] = "General"
    };

    /// <summary>Every element built or taken down, costed and set out by its cost element.</summary>
    public static IReadOnlyList<CostLine> Lines(BimDocument document)
    {
        using var cache = ModelCache.Begin(document);
        var us = document.ProjectInformation.ClearanceStandard.IsUs();
        var lines = new List<CostLine>();
        foreach (var element in document.Elements.Where(element => ClashDetective.Solidly(element.Category) && Costed(element)))
        {
            var type = document.FindType<ElementType>(element.TypeId);
            var unit = UnitOf(element);
            var quantity = InUnits(Measure(document, element, unit), unit);
            var rate = type?.Cost ?? 0;
            var (code, title) = us ? UniFormat(document, element) : Nrm1(document, element);
            lines.Add(new CostLine(element, type, code, title, quantity, unit, rate, (decimal)quantity * rate));
        }

        // Each pipe's clips and each duct's hangers, with the pipe or duct.
        foreach (var service in document.All<Mep.Pipe>().Cast<Element>().Concat(document.All<Mep.Duct>()).Where(Costed))
        {
            var count = Mep.Supports.Of(document, service).Count;
            if (count == 0) continue;
            var (code, title) = us ? UniFormat(document, service) : Nrm1(document, service);
            var rate = Mep.Supports.Rate(service, us);
            lines.Add(new CostLine(service, null, code, title, count, CostUnit.Item, rate, count * rate));
        }

        // Each opening cut for the services, and its fire-stopping: builder's work in connection with them.
        foreach (var opening in document.Elements.Where(element => element is WallOpening { ForServices: true } or ShaftOpening { ForServices: true }).Where(Costed))
        {
            var (code, title) = us ? UniFormat(document, opening) : Nrm1(document, opening);
            var rate = opening is ShaftOpening ? (us ? 85m : 75m) : (us ? 65m : 55m);
            lines.Add(new CostLine(opening, null, code, title, 1, CostUnit.Item, rate, rate));
        }

        // Each circuit: its cable by the metre, and its breaker.
        foreach (var circuit in Mep.Circuits.Of(document).Where(Costed))
        {
            if (Mep.Circuits.Design(document, circuit) is not { } design) continue;
            var (code, title) = us ? UniFormat(document, circuit) : Nrm1(document, circuit);
            var (cable, breaker) = Mep.Circuits.Rates(us, design);
            lines.Add(new CostLine(circuit, null, code, title, design.Length, CostUnit.Length, cable, (decimal)design.Length * cable));
            lines.Add(new CostLine(circuit, null, code, title, 1, CostUnit.Item, breaker, breaker));
        }

        return lines;
    }

    /// <summary>The project's currency's symbol.</summary>
    public static string Currency(BimDocument document) =>
        !string.IsNullOrWhiteSpace(document.CostSettings.Currency) ? document.CostSettings.Currency
        : document.ProjectInformation.ClearanceStandard.IsUs() ? "$" : "£";

    /// <summary>The floor area the cost is spread over, m²: every floor's.</summary>
    public static double FloorArea(BimDocument document) =>
        document.All<Floor>().Where(Costed).Sum(floor => InUnits(Measure(document, floor, CostUnit.Area), CostUnit.Area));

    /// <summary>
    /// The cost plan: each cost element and its lines under its group, each group's subtotal, the
    /// building works, then each addition on what comes before it, to the total.
    /// </summary>
    public static CostSummary Summary(BimDocument document)
    {
        using var cache = ModelCache.Begin(document);
        var us = document.ProjectInformation.ClearanceStandard.IsUs();
        var settings = document.CostSettings;
        var lines = Lines(document);
        var rows = new List<CostRow>();
        string GroupOf(string code) => us ? code[..1] : code.Split('.')[0];
        int Order(string code) => us ? code[0] == 'Z' ? 99 : code[0] - 'A' : int.Parse(GroupOf(code));

        decimal works = 0, preliminaries = 0;
        foreach (var group in lines.GroupBy(line => GroupOf(line.Code)).OrderBy(group => Order(group.First().Code)))
        {
            var groupTitle = us ? UniFormatGroups.GetValueOrDefault(group.Key, group.Key) : Nrm1Groups.GetValueOrDefault(group.Key, group.Key);
            foreach (var element in group.GroupBy(line => (line.Code, line.Title)).OrderBy(entry => entry.Key.Code, StringComparer.Ordinal))
                rows.Add(new CostRow(element.Key.Code, element.Key.Title, element.Sum(line => line.Total), element.ToList()));
            var subtotal = group.Sum(line => line.Total);
            rows.Add(new CostRow(group.Key, groupTitle, subtotal, Array.Empty<CostLine>(), IsTotal: true));
            if (group.Key is "9" or "Z") preliminaries += subtotal;
            else works += subtotal;
        }

        CostRow Total(string title, decimal amount) => new(string.Empty, title, amount, Array.Empty<CostLine>(), IsTotal: true);
        CostRow Addition(string code, string title, double percent, decimal on) => new(code, title, Math.Round(on * (decimal)percent / 100, 2), Array.Empty<CostLine>(), Percent: percent);

        rows.Add(Total(us ? "Building construction subtotal" : "Building works estimate (with facilitating works)", works));
        var prelims = Addition(us ? "Z1010" : "9", us ? "General conditions" : "Main contractor's preliminaries", settings.Preliminaries, works);
        rows.Add(prelims);
        var withPrelims = works + preliminaries + prelims.Amount;
        var profit = Addition(us ? "Z1020" : "10", us ? "Contractor's overhead and profit" : "Main contractor's overheads and profit", settings.OverheadsAndProfit, withPrelims);
        rows.Add(profit);
        var worksCost = withPrelims + profit.Amount;
        rows.Add(Total(us ? "Construction cost" : "Works cost estimate", worksCost));
        var fees = Addition(us ? "Z1030" : "11", us ? "Design fees" : "Project and design team fees", settings.DesignFees, worksCost);
        rows.Add(fees);
        rows.Add(new CostRow(us ? "Z1040" : "12", us ? "Other project costs" : "Other development and project costs", settings.OtherCosts, Array.Empty<CostLine>()));
        var baseCost = worksCost + fees.Amount + settings.OtherCosts;
        rows.Add(Total(us ? "Project cost before contingency" : "Base cost estimate", baseCost));
        var risk = Addition(us ? "Z1050" : "13", us ? "Contingency" : "Risk allowances", settings.Risk, baseCost);
        rows.Add(risk);
        var limit = baseCost + risk.Amount;
        rows.Add(Total(us ? "Project cost" : "Cost limit (excluding inflation)", limit));
        var inflation = Addition(us ? "Z1060" : "14", us ? "Escalation" : "Inflation", settings.Inflation, limit);
        rows.Add(inflation);
        var total = limit + inflation.Amount;
        rows.Add(Total(us ? "Total project cost" : "Cost limit (including inflation)", total));
        if (settings.Tax > 0)
        {
            var tax = Addition(us ? "Z7010" : "VAT", us ? "Sales tax" : "VAT (outside NRM1's cost limit)", settings.Tax, total);
            rows.Add(tax);
            total += tax.Amount;
            rows.Add(Total("Total with tax", total));
        }

        return new CostSummary(rows, total, FloorArea(document), Currency(document));
    }

    // ---- cost over time ----------------------------------------------------------------

    /// <summary>What each task costs: its elements' - each element counted with the first task that builds it.</summary>
    public static Dictionary<Guid, decimal> TaskBudgets(BimDocument document)
    {
        var counted = new HashSet<Guid>();
        var budgets = new Dictionary<Guid, decimal>();
        foreach (var task in document.Construction.Tasks)
        {
            decimal sum = 0;
            foreach (var element in ConstructionPlanning.Elements(document, task))
                if (counted.Add(element.Id)) sum += CostOf(document, element);
            budgets[task.Id] = sum;
        }

        return budgets;
    }

    /// <summary>The planned spend week by week - each task's cost spread evenly over its working days - and its running total.</summary>
    public static IReadOnlyList<(DateOnly Week, decimal Spend, decimal Cumulative)> CashFlow(BimDocument document)
    {
        var schedule = document.Construction;
        var budgets = TaskBudgets(document);
        var byWeek = new SortedDictionary<DateOnly, decimal>();
        DateOnly WeekOf(DateOnly date) => date.AddDays(-(((int)date.DayOfWeek + 6) % 7));
        foreach (var times in ConstructionPlanning.Time(schedule))
        {
            if (!budgets.TryGetValue(times.Task.Id, out var budget) || budget == 0) continue;
            var days = Math.Max(1, times.EarlyFinish - times.EarlyStart);
            for (var day = 0; day < days; day++)
            {
                var week = WeekOf(ConstructionPlanning.DateOf(schedule, times.EarlyStart + day));
                byWeek[week] = byWeek.GetValueOrDefault(week) + budget / days;
            }
        }

        decimal running = 0;
        return byWeek.Select(entry => (entry.Key, entry.Value, running += entry.Value)).ToList();
    }

    /// <summary>
    /// Earned value at a date: planned value - each task's budget for the share of its planned
    /// days by then, by its baseline where it has one; earned value - its budget for the share of
    /// it done; actual cost - what it has cost.
    /// </summary>
    public static EarnedValue Earned(BimDocument document, DateOnly statusDate)
    {
        var schedule = document.Construction;
        var budgets = TaskBudgets(document);
        decimal planned = 0, earned = 0, actual = 0;
        foreach (var times in ConstructionPlanning.Time(schedule))
        {
            var task = times.Task;
            var budget = budgets.GetValueOrDefault(task.Id);
            var (start, finish) = (task.BaselineStart ?? times.Start, task.BaselineFinish ?? times.Finish);
            var fraction = statusDate < start ? 0
                : statusDate >= finish ? 1
                : (double)(ConstructionPlanning.DayOf(schedule, statusDate) - ConstructionPlanning.DayOf(schedule, start) + 1) /
                  Math.Max(1, ConstructionPlanning.DayOf(schedule, finish) - ConstructionPlanning.DayOf(schedule, start) + 1);
            planned += budget * (decimal)Math.Clamp(fraction, 0, 1);
            earned += budget * (decimal)Math.Clamp(task.PercentComplete / 100, 0, 1);
            actual += task.ActualCost;
        }

        return new EarnedValue(statusDate, budgets.Values.Sum(), planned, earned, actual);
    }
}
