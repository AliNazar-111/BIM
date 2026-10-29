using System.Globalization;
using BIMDesigner.Core;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Mep;
using BIMDesigner.Core.Parameters;

using CoreWindow = BIMDesigner.Core.Architecture.Window;

namespace BIMDesigner.Infrastructure.Interoperability;

/// <summary>Who the COBie is from, and what it is priced in: the Contact sheet's one row, and the facility's currency.</summary>
public sealed class CobieOptions
{
    public string ContactEmail { get; init; } = "info@example.com";

    public string Company { get; init; } = string.Empty;

    public string Phone { get; init; } = string.Empty;

    public string GivenName { get; init; } = string.Empty;

    public string FamilyName { get; init; } = string.Empty;

    /// <summary>The currency costs are in, as COBie names it: GBP, USD, EUR.</summary>
    public string Currency { get; init; } = "GBP";

    public DateTime CreatedOn { get; init; } = DateTime.Now;
}

/// <summary>
/// The project as a COBie spreadsheet (COBie 2.4, as BS 1192-4 puts it to use): what a building's
/// owner takes over to run it. The facility, its floors and spaces, the spaces grouped into zones
/// by department; the types of the things in it that are kept up - doors, windows, roof windows,
/// fittings, equipment and furniture - and every one of them as a component in its space, grouped
/// into systems; their parameters as attributes and where each is as a coordinate. Its sheets and
/// columns are COBie's, in COBie's order, headings coloured as COBie colours them; a required
/// field with nothing to say says "n/a". Doors' and windows' categories are their types'
/// classification - Uniclass, to start with.
/// </summary>
public static class CobieExport
{
    private const string None = "n/a";
    private const string System = "BIMDesigner";

    public static void Save(BimDocument document, string path, CobieOptions? options = null)
    {
        using var file = File.Create(path);
        XlsxWriter.Write(file, Build(document, options ?? new CobieOptions()));
    }

    // ---- columns, as COBie 2.4 has them -------------------------------------------------

    private static (string, HeadingKind)[] Columns(params string[] spec) => spec.Select(column =>
    {
        var kind = column[0] switch { '*' => HeadingKind.Required, '>' => HeadingKind.Reference, '#' => HeadingKind.External, _ => HeadingKind.Optional };
        return (kind == HeadingKind.Optional ? column : column[1..], kind);
    }).ToArray();

    private static readonly string[] Common = { "*Name", ">CreatedBy", "*CreatedOn", "*Category" };

    private static XlsxSheet Sheet(string name, params string[] rest) =>
        new(name, Columns(Common.Concat(rest).ToArray()));

    public static IReadOnlyList<XlsxSheet> Build(BimDocument document, CobieOptions options)
    {
        using var cache = ModelCache.Begin(document);
        var by = string.IsNullOrWhiteSpace(options.ContactEmail) ? "info@example.com" : options.ContactEmail.Trim();
        var on = options.CreatedOn.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
        string Text(string? value) => string.IsNullOrWhiteSpace(value) ? None : value.Trim();
        string Id(Guid id) => Xbim.Ifc4.UtilityResource.IfcGloballyUniqueId.ConvertToBase64(id);
        string Category(string code) => string.IsNullOrWhiteSpace(code) ? None : document.Classification.Describe(code);

        var information = document.ProjectInformation;
        var sheets = new List<XlsxSheet>();

        var instruction = new XlsxSheet("Instruction", Columns(" Instruction", " Value"));
        instruction.Add("COBie version", "2.4");
        instruction.Add("Produced by", $"{System} for {Text(information.Name)}");
        instruction.Add("Produced on", on);
        instruction.Add("Units", "Lengths in millimetres, areas in square metres, volumes in cubic metres");
        instruction.Add("Colours", "Yellow: required. Salmon: refers to another sheet. Purple: from the authoring system. Green: if known.");
        sheets.Add(instruction);

        // ---- Contact ----
        var contact = new XlsxSheet("Contact", Columns("*Email", ">CreatedBy", "*CreatedOn", "*Category", "*Company", "*Phone", "#ExternalSystem", "#ExternalObject",
            "#ExternalIdentifier", "Department", "OrganizationCode", "GivenName", "FamilyName", "Street", "PostalBox", "Town", "StateRegion", "PostalCode", "Country"));
        contact.Add(by, by, on, None, Text(options.Company), Text(options.Phone), System, "IfcPersonAndOrganization", None, None, None,
            Text(options.GivenName), Text(options.FamilyName), Text(information.Address), None, None, None, None, None);
        sheets.Add(contact);

        // ---- Facility ----
        var facility = Sheet("Facility", "*ProjectName", "*SiteName", "*LinearUnits", "*AreaUnits", "*VolumeUnits", "*CurrencyUnit", "*AreaMeasurement",
            "#ExternalSystem", "#ExternalProjectObject", "#ExternalProjectIdentifier", "#ExternalSiteObject", "#ExternalSiteIdentifier", "#ExternalFacilityObject",
            "#ExternalFacilityIdentifier", "Description", "ProjectDescription", "SiteDescription", "Phase");
        facility.Add(Text(information.Name), by, on, Text(information.BuildingType), Text(string.IsNullOrWhiteSpace(information.Number) ? information.Name : information.Number),
            Text(information.Address), "millimeters", "square meters", "cubic meters", Text(options.Currency), "Gross internal area",
            System, "IfcProject", None, "IfcSite", None, "IfcBuilding", None, Text(information.Name), Text(information.Client), Text(information.Address), "New construction");
        sheets.Add(facility);

        // ---- Floor ----
        var levels = document.Levels.OrderBy(level => level.Elevation).ToList();
        var floor = Sheet("Floor", "#ExternalSystem", "#ExternalObject", "#ExternalIdentifier", "Description", "Elevation", "Height");
        for (var i = 0; i < levels.Count; i++)
        {
            object height = i + 1 < levels.Count ? levels[i + 1].Elevation - levels[i].Elevation : None;
            floor.Add(levels[i].Name, by, on, "Floor", System, "IfcBuildingStorey", Id(levels[i].Id), levels[i].Name, levels[i].Elevation, height);
        }

        sheets.Add(floor);

        // ---- Space ----
        var rooms = document.All<Room>()
            .Select(room => (Room: room, Boundary: room.GetBoundary(document)))
            .Where(entry => entry.Boundary.IsEnclosed)
            .ToList();
        var roomNames = UniqueNames(rooms.Select(entry => string.IsNullOrWhiteSpace(entry.Room.Number) ? entry.Room.Name : entry.Room.Number).ToList());
        var space = Sheet("Space", ">FloorName", "*Description", "#ExternalSystem", "#ExternalObject", "#ExternalIdentifier", "RoomTag", "UsableHeight", "GrossArea", "NetArea");
        for (var i = 0; i < rooms.Count; i++)
        {
            var (room, boundary) = rooms[i];
            var area = Math.Round(boundary.Area / Units.SquareMillimetresPerSquareMetre, 3);
            space.Add(roomNames[i], by, on, Category(room.ClassificationCode), document.FindLevel(room.LevelId)?.Name ?? None, Text(room.Name),
                System, "IfcSpace", Id(room.Id), Text(room.Number), room.UpperLimitOffset - room.BaseOffset, area, area);
        }

        sheets.Add(space);

        // ---- Zone: the spaces of each department ----
        var zone = Sheet("Zone", ">SpaceNames", "#ExternalSystem", "#ExternalObject", "#ExternalIdentifier", "Description");
        foreach (var department in rooms.Select((entry, i) => (entry.Room.Department, Name: roomNames[i]))
                     .Where(entry => !string.IsNullOrWhiteSpace(entry.Department))
                     .GroupBy(entry => entry.Department.Trim(), StringComparer.OrdinalIgnoreCase)
                     .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
            zone.Add(department.Key, by, on, "Department", string.Join(",", department.Select(entry => entry.Name)), System, "IfcZone", None, department.Key);
        sheets.Add(zone);

        // ---- Type and Component: what is kept up ----
        var assets = document.Elements.Where(element => element is Door or CoreWindow or RoofWindow or Component).ToList();
        var types = assets.Select(element => document.ElementTypes.FirstOrDefault(type => type.Id == element.TypeId)).OfType<ElementType>().Distinct().ToList();
        var typeNames = UniqueNames(types.Select(type => type.Name).ToList()).Zip(types).ToDictionary(entry => entry.Second.Id, entry => entry.First);

        var typeSheet = Sheet("Type", "*Description", "*AssetType", ">Manufacturer", "*ModelNumber", ">WarrantyGuarantorParts", "*WarrantyDurationParts",
            ">WarrantyGuarantorLabor", "*WarrantyDurationLabor", "*WarrantyDurationUnit", "#ExternalSystem", "#ExternalObject", "#ExternalIdentifier",
            "ReplacementCost", "ExpectedLife", "DurationUnit", "WarrantyDescription", "NominalLength", "NominalWidth", "NominalHeight", "ModelReference",
            "Shape", "Size", "Color", "Finish", "Grade", "Material", "Constituents", "Features", "AccessibilityPerformance", "CodePerformance", "SustainabilityPerformance");
        foreach (var type in types)
        {
            var (length, width, height) = type switch
            {
                OpeningType opening => ((object)opening.Thickness, (object)opening.Width, (object)opening.Height),
                ComponentType fitting => (fitting.Depth, fitting.Width, fitting.Height),
                _ => ((object)None, (object)None, (object)None)
            };
            var manufacturer = type.Manufacturer.Contains('@') ? type.Manufacturer.Trim() : None;
            var fire = type switch { OpeningType opening => opening.FireRating, _ => string.Empty };
            typeSheet.Add(typeNames[type.Id], by, on, Category(type.ClassificationCode), Text(string.IsNullOrWhiteSpace(type.Description) ? type.Name : type.Description),
                IsMovable(type) ? "Movable" : "Fixed", manufacturer, None, None, None, None, None, "year",
                System, IfcClass(type) + "Type", Id(type.Id), type.Cost > 0 ? (object)(double)type.Cost : None, None, "year", None,
                length, width, height, Text(type.Url), None, None, None, None, None, None, None, None, None, Text(fire), None);
        }

        sheets.Add(typeSheet);

        var componentNames = UniqueNames(assets.Select(element =>
            !string.IsNullOrWhiteSpace(element.Mark) ? element.Mark : document.ElementTypes.FirstOrDefault(type => type.Id == element.TypeId)?.Name ?? element.Category.ToString()).ToList(), numberAll: true);
        var names = assets.Zip(componentNames).ToDictionary(entry => entry.First.Id, entry => entry.Second);
        var component = Sheet("Component", ">TypeName", ">Space", "*Description", "#ExternalSystem", "#ExternalObject", "#ExternalIdentifier",
            "SerialNumber", "InstallationDate", "WarrantyStartDate", "TagNumber", "BarCode", "AssetIdentifier");
        var coordinates = new List<(string Name, Point3D At, double Rotation)>();
        foreach (var element in assets)
        {
            var type = document.ElementTypes.FirstOrDefault(candidate => candidate.Id == element.TypeId);
            var (at, rotation) = Position(document, element);
            var spaces = rooms.Select((entry, i) => (entry, i))
                .Where(pair => pair.entry.Room.LevelId == element.LevelId && Touches(pair.entry.Boundary.Polygon, at, element is Opening ? 400 : 0))
                .Select(pair => roomNames[pair.i]).ToList();
            component.Add(names[element.Id], by, on, None, type is null ? None : typeNames[type.Id], spaces.Count == 0 ? None : string.Join(",", spaces),
                Text(type?.Name), System, IfcClass(type), Id(element.Id), None, None, None, Text(element.Mark), None, None);
            coordinates.Add((names[element.Id], at, rotation));
        }

        sheets.Add(component);

        // ---- System: the services' systems, a row for each component on each as COBie 2.4 has it (4.2.3.2.2.98) ----
        var systemSheet = Sheet("System", ">ComponentNames", "#ExternalSystem", "#ExternalObject", "#ExternalIdentifier", "Description");
        var onSystems = new HashSet<Guid>();
        foreach (var system in document.All<Core.Mep.MepSystem>().OrderBy(system => system.Name, StringComparer.OrdinalIgnoreCase))
        {
            var (code, title) = Uniclass(system.Kind);
            var members = (Core.Mep.MepSystems.SourceOf(document, system) is { } source ? new[] { source } : Array.Empty<Component>())
                .Concat(Core.Mep.MepSystems.MembersOf(document, system)).Where(member => names.ContainsKey(member.Id)).Distinct().ToList();
            foreach (var member in members)
            {
                systemSheet.Add(system.Name, by, on, document.Classification.Find(code) is not null ? Category(code) : $"{code} : {title}", names[member.Id],
                    System, system.Kind == Core.Mep.SystemKind.Power ? "IfcDistributionCircuit" : "IfcDistributionSystem", Id(system.Id), system.Kind.Title());
                onSystems.Add(member.Id);
            }
        }

        // Fittings on no system yet, by what they serve - every component in a system, as BS 1192-4 asks (6.3.1).
        foreach (var group in assets.OfType<Component>().Where(item => !onSystems.Contains(item.Id))
                     .Select(item => (Component: item, System: SystemOf(document.FindType<ComponentType>(item.TypeId)?.Kind)))
                     .Where(entry => entry.System is not null)
                     .GroupBy(entry => entry.System!.Value)
                     .OrderBy(group => group.Key.Name, StringComparer.Ordinal))
            foreach (var entry in group)
                systemSheet.Add(group.Key.Name, by, on, Category(group.Key.Code), names[entry.Component.Id], System, "IfcSystem", None, group.Key.Name);
        sheets.Add(systemSheet);

        // ---- the sheets a designer leaves for the contractor and the owner ----
        sheets.Add(new XlsxSheet("Assembly", Columns("*Name", ">CreatedBy", "*CreatedOn", "*SheetName", "*ParentName", "*ChildNames", "*AssemblyType",
            "#ExternalSystem", "#ExternalObject", "#ExternalIdentifier", "Description")));

        // Connections: each system's source to each fitting on it - upstream first - or its fittings in turn where nothing feeds it, as a control link.
        var connection = new XlsxSheet("Connection", Columns("*Name", ">CreatedBy", "*CreatedOn", "*ConnectionType", "*SheetName", "*RowName1", "*RowName2",
            "*RealizingElement", "*PortName1", "*PortName2", "#ExternalSystem", "#ExternalObject", "#ExternalIdentifier", "Description"));
        string PortOf(Component component, Core.Mep.SystemKind kind) =>
            Core.Mep.MepSystems.ConnectorsOf(document, component).FirstOrDefault(connector => connector.Kind == kind)?.Name ?? None;
        foreach (var system in document.All<Core.Mep.MepSystem>().OrderBy(system => system.Name, StringComparer.OrdinalIgnoreCase))
        {
            var members = Core.Mep.MepSystems.MembersOf(document, system).Where(member => names.ContainsKey(member.Id)).ToList();
            var source = Core.Mep.MepSystems.SourceOf(document, system) is { } feeding && names.ContainsKey(feeding.Id) ? feeding : null;
            var drained = Core.Mep.Drainage.IsDrainage(system.Kind) || system.Kind == Core.Mep.SystemKind.ExtractAir;
            var links = source is not null
                ? members.Select(member => drained ? (Up: member, Down: source) : (Up: source, Down: member)).ToList()
                : members.Zip(members.Skip(1), (up, down) => (Up: up, Down: down)).ToList();
            var type = source is null ? "Control" : drained ? "Flow" : system.Kind == Core.Mep.SystemKind.Heating ? "Flow" : "Supply";
            foreach (var (up, down) in links)
                connection.Add($"{system.Name}: {names[up.Id]} to {names[down.Id]}", by, on, type, "Component", names[up.Id], names[down.Id],
                    None, PortOf(up, system.Kind), PortOf(down, system.Kind), System, "IfcRelConnectsPorts", None, $"{system.Kind.Title()} from {names[up.Id]} to {names[down.Id]} on {system.Name}");
        }

        sheets.Add(connection);
        sheets.Add(Sheet("Spare", ">TypeName", "*Suppliers", "#ExternalSystem", "#ExternalObject", "#ExternalIdentifier", "*Description", "SetNumber", "PartNumber"));
        sheets.Add(Sheet("Resource", "#ExternalSystem", "#ExternalObject", "#ExternalIdentifier", "*Description"));
        sheets.Add(Sheet("Job", "*Status", ">TypeName", "*Description", "*Duration", "*DurationUnit", "*Start", "*TaskStartUnit", "*Frequency", "*FrequencyUnit",
            "#ExternalSystem", "#ExternalObject", "#ExternalIdentifier", "TaskNumber", "Priors", "ResourceNames"));
        sheets.Add(Sheet("Impact", "*ImpactType", "*ImpactStage", "*SheetName", "*RowName", "*Value", "*ImpactUnit", "LeadInTime", "Duration", "LeadOutTime",
            "#ExternalSystem", "#ExternalObject", "#ExternalIdentifier", "Description"));
        sheets.Add(Sheet("Document", "*ApprovalBy", "*Stage", "*SheetName", "*RowName", "*Directory", "*File", "#ExternalSystem", "#ExternalObject",
            "#ExternalIdentifier", "Description", "Reference"));

        // ---- Attribute: each type's, component's and space's parameters ----
        var attribute = Sheet("Attribute", "*SheetName", "*RowName", "*Value", "*Unit", "#ExternalSystem", "#ExternalObject", "#ExternalIdentifier", "Description", "AllowedValues");
        void Attributes(string sheetName, string row, IEnumerable<ParameterValue> parameters, string set)
        {
            foreach (var parameter in parameters)
            {
                if (parameter.Name is "Type Name" or "Mark" or "Classification" or "Classification Title") continue;
                var value = parameter.DisplayValue;
                if (string.IsNullOrWhiteSpace(value) || value == ParameterFormatter.NoValue) continue;
                attribute.Add(parameter.Name, by, on, "Submitted", sheetName, row, value, None, System, "IfcPropertySingleValue", set, None,
                    parameter.AllowedValues is { Count: > 0 } allowed ? string.Join(",", allowed) : None);
            }
        }

        foreach (var type in types) Attributes("Type", typeNames[type.Id], type.AllTypeParameters(document), "BIMDesigner_Type");
        foreach (var element in assets) Attributes("Component", names[element.Id], element.GetInstanceParameters(document), "BIMDesigner_Parameters");
        for (var i = 0; i < rooms.Count; i++) Attributes("Space", roomNames[i], rooms[i].Room.GetInstanceParameters(document), "Pset_SpaceCommon");
        sheets.Add(attribute);

        // ---- Coordinate: where each component is ----
        var coordinate = Sheet("Coordinate", "*SheetName", "*RowName", "*CoordinateXAxis", "*CoordinateYAxis", "*CoordinateZAxis", "#ExternalSystem",
            "#ExternalObject", "#ExternalIdentifier", "ClockwiseRotation", "ElevationalRotation", "YawRotation");
        foreach (var (name, at, rotation) in coordinates)
            coordinate.Add(name, by, on, "point", "Component", name, Math.Round(at.X, 1), Math.Round(at.Y, 1), Math.Round(at.Z, 1),
                System, "IfcCartesianPoint", None, Math.Round(-rotation, 3), 0.0, 0.0);
        sheets.Add(coordinate);

        sheets.Add(Sheet("Issue", "*Type", "*Risk", "*Chance", "*Impact", "*SheetName1", "*RowName1", "*SheetName2", "*RowName2", "*Description",
            "*Owner", "*Mitigation", "#ExternalSystem", "#ExternalObject", "#ExternalIdentifier"));
        sheets.Add(new XlsxSheet("PickLists", Columns("CategoryFacility", "CategorySpace", "CategoryElement", "CategoryProduct", "CategoryRole")));
        return sheets;
    }

    /// <summary>Names made unique, as COBie's must be: a second "Kitchen" is "Kitchen (2)" - or, numbering all, each "Chair-01", "Chair-02".</summary>
    private static List<string> UniqueNames(IReadOnlyList<string> names, bool numberAll = false)
    {
        var counts = names.GroupBy(name => name, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var raw in names)
        {
            var name = string.IsNullOrWhiteSpace(raw) ? "Unnamed" : raw.Trim();
            var n = seen[name] = seen.GetValueOrDefault(name) + 1;
            result.Add(counts[raw] <= 1 ? name : numberAll ? $"{name}-{n:00}" : n == 1 ? name : $"{name} ({n})");
        }

        return result;
    }

    private static bool IsMovable(ElementType type) =>
        type is ComponentType { Kind: BuiltInCategory.Furniture or BuiltInCategory.Entourage or BuiltInCategory.Planting } component
        && component.Placement == ComponentPlacement.Freestanding;

    /// <summary>A system's Uniclass 2015 Elements/functions group, by what it carries.</summary>
    private static (string Code, string Title) Uniclass(Core.Mep.SystemKind kind) => kind switch
    {
        Core.Mep.SystemKind.Foul or Core.Mep.SystemKind.Rainwater => ("EF_50", "Waste disposal functions"),
        Core.Mep.SystemKind.ColdWater or Core.Mep.SystemKind.HotWater or Core.Mep.SystemKind.Gas or Core.Mep.SystemKind.Sprinkler => ("EF_55", "Piped supply functions"),
        Core.Mep.SystemKind.Heating => ("EF_60", "Heating, cooling and refrigeration functions"),
        Core.Mep.SystemKind.ExtractAir or Core.Mep.SystemKind.SupplyAir or Core.Mep.SystemKind.OutsideAir => ("EF_65", "Ventilation and air conditioning functions"),
        Core.Mep.SystemKind.FireAlarm => ("EF_75", "Communications, security, safety, control and protection functions"),
        _ => ("EF_70", "Electrical power and lighting functions")
    };

    private static string IfcClass(ElementType? type) => type switch
    {
        ComponentType fitting when fitting.Form != ComponentForm.Custom && fitting.Form != ComponentForm.Box => IfcExport.ClassName(fitting),
        DoorType => "IfcDoor",
        WindowType or RoofWindowType => "IfcWindow",
        ComponentType { Kind: BuiltInCategory.Furniture or BuiltInCategory.Casework } => "IfcFurniture",
        ComponentType { Kind: BuiltInCategory.PlumbingFixtures } => "IfcSanitaryTerminal",
        ComponentType { Kind: BuiltInCategory.LightingFixtures } => "IfcLightFixture",
        ComponentType { Kind: BuiltInCategory.MechanicalEquipment } => "IfcSpaceHeater",
        ComponentType { Kind: BuiltInCategory.ElectricalFixtures } => "IfcOutlet",
        ComponentType { Kind: BuiltInCategory.LightingDevices } => "IfcSwitchingDevice",
        ComponentType { Kind: BuiltInCategory.FireAlarmDevices } => "IfcSensor",
        ComponentType { Kind: BuiltInCategory.ElectricalEquipment } => "IfcElectricDistributionBoard",
        _ => "IfcBuildingElementProxy"
    };

    /// <summary>The system a kind of fitting is part of, and its Uniclass function.</summary>
    private static (string Name, string Code)? SystemOf(BuiltInCategory? kind) => kind switch
    {
        BuiltInCategory.LightingFixtures or BuiltInCategory.LightingDevices => ("Lighting", "EF_70"),
        BuiltInCategory.ElectricalFixtures => ("Small power", "EF_70"),
        BuiltInCategory.ElectricalEquipment => ("Electrical distribution", "EF_70"),
        BuiltInCategory.MechanicalEquipment => ("Heating", "EF_60"),
        BuiltInCategory.FireAlarmDevices => ("Fire detection and alarm", "EF_75"),
        BuiltInCategory.Sprinklers => ("Fire sprinklers", "EF_55"),
        BuiltInCategory.PlumbingFixtures => ("Sanitary", "EF_55"),
        _ => null
    };

    /// <summary>Where a component is, in plan and height, and which way it is turned.</summary>
    private static (Point3D At, double Rotation) Position(BimDocument document, Element element)
    {
        var level = document.FindLevel(element.LevelId)?.Elevation ?? 0;
        switch (element)
        {
            case Component component:
                return (Point3D.On(component.Location, level + component.Elevation), component.Rotation);

            case Opening opening when document.Find<Wall>(opening.HostWallId) is { } wall:
                var centre = opening.GetCentre(wall);
                var direction = wall.Direction;
                return (Point3D.On(centre, level + wall.BaseOffset + opening.SillHeight), Math.Atan2(direction.Y, direction.X) * 180 / Math.PI);

            case RoofWindow window:
                return (Point3D.On(window.Location, level), 0);

            default:
                return (new Point3D(0, 0, level), 0);
        }
    }

    /// <summary>Whether a point is in a room's outline, or - for a door or window in its wall - within reach of it.</summary>
    private static bool Touches(IReadOnlyList<Point2D> polygon, Point3D at, double reach)
    {
        var point = new Point2D(at.X, at.Y);
        if (Polygon2D.Contains(polygon, point)) return true;
        if (reach <= 0) return false;
        for (var i = 0; i < polygon.Count; i++)
            if (Line2D.DistanceFromSegment(point, polygon[i], polygon[(i + 1) % polygon.Count]) <= reach) return true;
        return false;
    }
}
