using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Tests;

/// <summary>
/// A building other than a dwelling judged by the rules for it, not a home's (round 118). Its fire detection and alarm system by Approved
/// Document B Volume 2 Section 1 - an electrically operated system to BS 5839-1 (1.3, 1.8), automatic detection in a hotel (1.4), in a store
/// opening onto the escape route (1.5) and to open an automatic smoke vent (1.6) - and no BS 5839-6 smoke alarms, no carbon monoxide alarm, which
/// Approved Document J asks only in a dwelling (2.34, 3.43). Its air by Approved Document F Volume 2: sanitary accommodation 6 l/s a WC or urinal
/// and 15 a shower, whatever its windows (1.26, Table 1.1); an office's kitchen 60, 30 with a hood, 15 for drinks (1.28); each office purged
/// (1.35); a common space opening a fiftieth of its floor (1.33). Its U-values by Approved Document L Volume 2's Table 4.1: a flat roof 0.18, a
/// fire doorset 1.8, a vehicle door 1.3, a high-usage entrance door 3.0, a display window none. M4(2)'s kitchen space and a curtain wall door's
/// 850, the NKBA's kitchen planning, Volume 1's purge and escape windows - a home's, kept to homes; a flat over a shop still judged as one.
/// </summary>
[Collection("Wpf")]
public class NonDomesticRulesTests
{
    private const string Brick = "Exterior - Brick on Block 330mm", Partition = "Interior - Partition 125mm", Wide = "Single - 1000 x 2100";

    private sealed class Building
    {
        public readonly BimDocument Document = BimDocument.CreateDefault();
        public readonly Dictionary<string, Room> Rooms = new();
        public readonly Dictionary<string, Wall> Walls = new();
        public readonly Dictionary<string, Door> Doors = new();
        public readonly Dictionary<string, Window> Windows = new();
        public Guid Level => Document.Levels.Where(level => level.Elevation >= 0).OrderBy(level => level.Elevation).First().Id;

        public Wall Wall(string name, double x0, double y0, double x1, double y1, string type = Partition)
        {
            var wall = new Wall { Start = new Point2D(x0, y0), End = new Point2D(x1, y1), TypeId = Document.TypesOf<WallType>().First(each => each.Name == type).Id, LevelId = Level, UnconnectedHeight = 3000 };
            Document.Add(wall);
            Walls[name] = wall;
            return wall;
        }

        public Component Put(ComponentForm form, double x, double y, Func<ComponentType, bool>? which = null)
        {
            var type = Document.TypesOf<ComponentType>().First(each => each.Form == form && (which is null || which(each)));
            var piece = new Component { TypeId = type.Id, TypeKind = type.Kind, LevelId = Level, Location = new Point2D(x, y), Elevation = type.DefaultElevation };
            Document.Add(piece);
            return piece;
        }

        public Building(string use)
        {
            Document.ProjectInformation.BuildingType = use;
            Wall("south", 0, 0, 20000, 0, Brick);
            Wall("east", 20000, 0, 20000, 12000, Brick);
            Wall("north", 20000, 12000, 0, 12000, Brick);
            Wall("west", 0, 12000, 0, 0, Brick);
            Wall("corridor south", 0, 5000, 20000, 5000);
            Wall("corridor north", 0, 7000, 20000, 7000);
            foreach (var (x, y0, y1) in new[] { (8000.0, 0.0, 5000.0), (12000, 0, 5000), (15000, 0, 5000), (8000, 7000, 12000), (14000, 7000, 12000) })
                Wall($"partition {x} {y0}", x, y0, x, y1);
            void Door(string name, string wall, double along)
            {
                var door = new Door { HostWallId = Walls[wall].Id, TypeId = Document.TypesOf<DoorType>().First(each => each.Name == Wide).Id, LevelId = Level, DistanceAlongWall = along };
                Document.Add(door);
                Doors[name] = door;
            }

            // The west wall runs north to south: 6 m along it is 6 m up from the south.
            Door("west", "west", 6000);
            Door("east", "east", 6000);
            foreach (var (name, wall, along) in new[] { ("office", "corridor south", 4000.0), ("kitchen", "corridor south", 10000), ("wc", "corridor south", 13500), ("store", "corridor south", 17500),
                         ("reception", "corridor north", 4000), ("meeting", "corridor north", 11000), ("plant", "corridor north", 17000) })
                Door(name, wall, along);
            void Window(string name, string wall, double along)
            {
                var window = new Window { HostWallId = Walls[wall].Id, TypeId = Document.TypesOf<WindowType>().First().Id, LevelId = Level, DistanceAlongWall = along };
                Document.Add(window);
                Windows[name] = window;
            }

            // The north wall runs east to west: 16 m along it is 4 m in from the west.
            Window("office", "south", 4000);
            Window("kitchen", "south", 10000);
            Window("reception", "north", 16000);
            Window("meeting", "north", 9000);
            foreach (var (name, x, y) in new[] { ("Office", 4000.0, 2500.0), ("Kitchen", 10000, 2500), ("WC", 13500, 2500), ("Store", 17500, 2500), ("Corridor", 10000, 6000),
                         ("Reception", 4000, 9500), ("Meeting Room", 11000, 9500), ("Plant Room", 17000, 9500) })
            {
                var room = new Room { LevelId = Level, Location = new Point2D(x, y), Name = name };
                Document.Add(room);
                Rooms[name] = room;
            }
        }

        public IReadOnlyList<string> Said() => Warnings.Of(Document).Select(warning => $"{warning.Heading}: {warning.Said}").ToList();
    }

    [Theory]
    [InlineData("Office")]
    [InlineData("Shop")]
    [InlineData("Hotel")]
    public void TheRulesForHomesStayInHomes(string use)
    {
        var building = new Building(use);
        building.Put(ComponentForm.Cooker, 10000, 800);
        building.Put(ComponentForm.Sofa, 3000, 10500);
        building.Put(ComponentForm.TumbleDryer, 17000, 11500, type => type.BurnsGas);
        building.Put(ComponentForm.Toilet, 13500, 800);

        var said = building.Said();
        foreach (var home in new[] { "BS 5839-6 asks", "BS 5839-6, which", "Approved Document J", "Table 1.4", "M4(2)", "NKBA", "housing standards", "utility room", "or a window to open" })
            Assert.DoesNotContain(said, line => line.Contains(home, StringComparison.Ordinal));

        // The same building as a house: a home's rules, as before.
        building.Document.ProjectInformation.BuildingType = "House";
        said = building.Said();
        foreach (var home in new[] { "BS 5839-6, which Approved Document B (1.1) follows, asks for one on every storey", "Approved Document J (2.34, 3.43)", "Approved Document F (Table 1.4)", "M4(2)) 2.24b",
                     "Approved Document F (Table 1.1) asks for 30 l/s from a utility room" })
            Assert.Contains(said, line => line.Contains(home, StringComparison.Ordinal));
    }

    [Fact]
    public void AFlatOverAShopIsJudgedAsAHome()
    {
        // The reception named into a flat: its window, opening 1.17 m² against a twentieth of its 37.1 m², asked as a home's (Table 1.4); the office not.
        var building = new Building("Shop");
        var flat = building.Rooms["Reception"];
        flat.Dwelling = "Flat 1";
        Assert.True(Domestic.Rules(building.Document, flat));
        Assert.False(Domestic.Rules(building.Document, building.Rooms["Office"]));
        Assert.Contains("Approved Document F (Table 1.4)", WindowRules.Verdict(building.Document, flat));
        Assert.Null(WindowRules.Verdict(building.Document, building.Rooms["Office"]));
        Assert.True(Domestic.Rules(building.Document, building.Windows["reception"]));
        Assert.False(Domestic.Rules(building.Document, building.Windows["office"]));
    }

    [Fact]
    public void ABuildingOtherThanAHomeHasAFireAlarmSystem()
    {
        // Said once, on the largest room of the lowest storey (1.3, 1.8); gone with a call point, a sounder and the panel.
        var building = new Building("Office");
        var document = building.Document;
        var saying = building.Rooms.Values.Where(room => Alarms.Verdict(document, room)?.Contains("no fire alarm system in the building - Approved Document B Volume 2 (1.3, 1.8)") == true).ToList();
        var largest = building.Rooms.Values.MaxBy(room => room.GetBoundary(document).Area);
        Assert.Equal(largest, Assert.Single(saying));

        building.Put(ComponentForm.FireAlarmCallPoint, 300, 6000);
        building.Put(ComponentForm.FireAlarmSounder, 10000, 6900);
        building.Put(ComponentForm.FireAlarmPanel, 300, 5500);
        Assert.DoesNotContain(building.Rooms.Values, room => Alarms.Verdict(document, room)?.Contains("(1.3, 1.8)") == true);
    }

    [Fact]
    public void AHotelHasAutomaticDetectionOnEveryStorey()
    {
        // 1.4: residential (other), automatic detection asked on the storey - on its largest room - and gone with a detector anywhere on it; a
        // store not asked separately (1.5 is for the other occupancies, 1.4 covering the hotel throughout).
        var building = new Building("Hotel");
        var document = building.Document;
        Assert.Single(building.Rooms.Values, room => Alarms.Verdict(document, room)?.Contains("no automatic fire detector on this storey - Approved Document B Volume 2 (1.4) asks automatic fire detection and alarms in residential (other), purpose group 2(b)") == true);
        Assert.DoesNotContain("(1.5)", Alarms.Verdict(document, building.Rooms["Store"]) ?? string.Empty);

        building.Put(ComponentForm.SmokeDetector, 10000, 6000);
        Assert.DoesNotContain(building.Rooms.Values, room => Alarms.Verdict(document, room)?.Contains("(1.4)") == true);
    }

    [Fact]
    public void AStoreOpeningOntoTheEscapeRouteHasAnAutomaticDetector()
    {
        // 1.5: the store and the plant room open onto the corridor - asked a detector each; the store given a heat detector, the plant room left
        // to the sprinklers, which can work the alarm (1.3, note).
        var building = new Building("Office");
        var document = building.Document;
        foreach (var name in new[] { "Store", "Plant Room" })
            Assert.Contains("no automatic fire detector in it - a fire could break out unseen here, a part of the building not visited regularly, and fill the Corridor the escape goes along: Approved Document B Volume 2 (1.5)",
                Alarms.Verdict(document, building.Rooms[name]));
        Assert.DoesNotContain("(1.5)", Alarms.Verdict(document, building.Rooms["Office"]) ?? string.Empty);

        building.Put(ComponentForm.HeatDetector, 17500, 2500);
        Assert.DoesNotContain("(1.5)", Alarms.Verdict(document, building.Rooms["Store"]) ?? string.Empty);
        Assert.Contains("(1.5)", Alarms.Verdict(document, building.Rooms["Plant Room"]));
        document.ProjectInformation.Sprinklered = true;
        Assert.DoesNotContain("(1.5)", Alarms.Verdict(document, building.Rooms["Plant Room"]) ?? string.Empty);
    }

    [Fact]
    public void AnAutomaticSmokeVentIsOpenedByAutomaticDetection()
    {
        // 1.6: a window type made an automatic opening vent - and nothing to detect the smoke it opens on; a smoke detector put up.
        var building = new Building("Office");
        var document = building.Document;
        document.FindType<WindowType>(building.Windows["meeting"].TypeId)!.SmokeVent = SmokeVentKind.Automatic;
        Assert.Contains("automatic opening vents open on detecting smoke, and nothing detects it - Approved Document B Volume 2 (1.6)",
            string.Join(" ", building.Rooms.Values.Select(room => Alarms.Verdict(document, room))));
        building.Put(ComponentForm.SmokeDetector, 10000, 6000);
        Assert.DoesNotContain("(1.6)", string.Join(" ", building.Rooms.Values.Select(room => Alarms.Verdict(document, room))));
    }

    [Fact]
    public void AHomesAlarmInAnOfficeIsTheWrongAlarm()
    {
        // A BS 5839-6 smoke alarm in the corridor: the wrong kind, not wired as a home's; a carbon monoxide alarm by the gas dryer not asked (2.34, 3.43).
        var building = new Building("Office");
        var document = building.Document;
        var alarm = building.Put(ComponentForm.SmokeAlarm, 10000, 6000);
        building.Put(ComponentForm.TumbleDryer, 17000, 11500, type => type.BurnsGas);
        var problem = Assert.Single(Alarms.Problems(document, alarm, document.ProjectInformation.ClearanceStandard));
        Assert.Equal("it is a home's smoke alarm, to BS 5839-6.", problem.Said);
        Assert.StartsWith("Approved Document B Volume 2 (1.8) asks the fire detection and alarm system of a building other than a dwelling to comply with BS 5839-1", problem.Rule);
        Assert.DoesNotContain(Core.Mep.MepSystems.Problems(document), each => each.Element == alarm);
        Assert.DoesNotContain("carbon monoxide", Alarms.Verdict(document, building.Rooms["Plant Room"]) ?? string.Empty);
    }

    [Fact]
    public void SanitaryAccommodationIsExtractedWhateverItsWindows()
    {
        // Two WCs and a urinal: 2 x 6 + 6 = 18 l/s (1.26), with a window or without; a 60 l/s fan meets it.
        var building = new Building("Office");
        var document = building.Document;
        building.Put(ComponentForm.Toilet, 12800, 800);
        building.Put(ComponentForm.Toilet, 14200, 800);
        building.Put(ComponentForm.Urinal, 13500, 4500);
        var wc = building.Rooms["WC"];
        Assert.Contains("extract 0 l/s - Approved Document F Volume 2 (1.26) asks sanitary accommodation and bathrooms for 18 l/s, 6 a WC pan or urinal and 15 a shower or bath - a window to open does not stand in for it",
            Services.Verdict(document, wc));
        document.Add(new Window { HostWallId = building.Walls["south"].Id, TypeId = document.TypesOf<WindowType>().First().Id, LevelId = building.Level, DistanceAlongWall = 13500 });
        Assert.Contains("(1.26) asks sanitary accommodation and bathrooms for 18 l/s", Services.Verdict(document, wc));
        building.Put(ComponentForm.ExtractFan, 13500, 200, type => type.Name.Contains("60 l/s"));
        Assert.Contains("extract 60 l/s", Services.Verdict(document, wc));
        Assert.DoesNotContain("asks", Services.Verdict(document, wc));

        // In a shop, the same by Table 1.1's row for sanitary accommodation.
        document.ProjectInformation.BuildingType = "Shop";
        document.Remove(document.All<Component>().Single(piece => document.FindType<ComponentType>(piece.TypeId)!.Form == ComponentForm.ExtractFan));
        Assert.Contains("Approved Document F Volume 2 (Table 1.1) asks sanitary accommodation and bathrooms for 18 l/s", Services.Verdict(document, wc));
    }

    [Fact]
    public void AnOfficesKitchenIsExtractedAs128Asks()
    {
        // A cooker with no hood: 60 l/s from an extract remote from it (1.28b); its 600 hood, 110 l/s: 30 met; a sink and a fridge only: 15 (1.28a).
        var building = new Building("Office");
        var document = building.Document;
        var kitchen = building.Rooms["Kitchen"];
        var cooker = building.Put(ComponentForm.Cooker, 10000, 800);
        Assert.Contains("extract 0 l/s - Approved Document F Volume 2 (1.28b) asks 60 l/s from an extract remote from the hob or cooker, 30 from a hood beside it", Services.Verdict(document, kitchen));
        Assert.Null(Kitchens.Verdict(document, kitchen));
        var hood = building.Put(ComponentForm.CookerHood, 10000, 800, type => type.Name.Contains("600"));
        Assert.Contains("extract 110 l/s", Services.Verdict(document, kitchen));
        document.Remove(cooker);
        document.Remove(hood);
        building.Put(ComponentForm.Sink, 9000, 800);
        building.Put(ComponentForm.Fridge, 11000, 800);
        Assert.Contains("extract 0 l/s - Approved Document F Volume 2 (1.28a) asks 15 l/s where only a microwave and drinks are made", Services.Verdict(document, kitchen));

        // A shop's kitchen: left to CIBSE's guidance (1.41, Table 1.1).
        document.ProjectInformation.BuildingType = "Shop";
        Assert.Null(Services.Verdict(document, kitchen));
    }

    [Fact]
    public void EachOfficeHasAMeansOfPurgeVentilation()
    {
        // 1.35: the office's window made fixed - nothing to purge its air with; a fan in it; in a shop not asked; the corridor, a common space,
        // opening 4.2 m² through its doors out against a fiftieth of its 36.9 m² - and, its doors made internal, nothing (1.33).
        var building = new Building("Office");
        var document = building.Document;
        var fixedType = document.TypesOf<WindowType>().First(type => type.Operation == WindowOperation.Fixed);
        building.Windows["office"].TypeId = fixedType.Id;
        Assert.Equal("Windows: nothing to purge its air with - Approved Document F Volume 2 (1.35) asks each office for a means of purge ventilation taken directly outside: a window or a door that opens, or a fan",
            WindowRules.Verdict(document, building.Rooms["Office"]));
        var fan = building.Put(ComponentForm.ExtractFan, 4000, 200);
        Assert.Null(WindowRules.Verdict(document, building.Rooms["Office"]));
        document.Remove(fan);
        document.ProjectInformation.BuildingType = "Shop";
        Assert.Null(WindowRules.Verdict(document, building.Rooms["Office"]));

        var corridor = building.Rooms["Corridor"];
        Assert.Null(WindowRules.Verdict(document, corridor));
        document.Remove(building.Doors["west"]);
        document.Remove(building.Doors["east"]);
        var floor = corridor.GetBoundary(document).Area / 1e6;
        Assert.Equal($"Windows: opens 0.00 m² - Approved Document F Volume 2 (Table 1.1) asks a common space for openings to the outside of a fiftieth of its floor, {floor / 50:0.00} m², or 0.5 l/s of outdoor air a square metre supplied",
            WindowRules.Verdict(document, corridor));
        document.ProjectInformation.BuildingType = "Office";
        Assert.Contains("Approved Document F Volume 2 (1.33)", WindowRules.Verdict(document, corridor));
        document.Add(new Window { HostWallId = building.Walls["west"].Id, TypeId = document.TypesOf<WindowType>().First().Id, LevelId = building.Level, DistanceAlongWall = 6000 });
        Assert.Null(WindowRules.Verdict(document, corridor));
    }

    [Fact]
    public void ABuildingOtherThanAHomesOutsideDoorsAreLimitedByVolume2sTable41()
    {
        var building = new Building("Office");
        var document = building.Document;
        var door = building.Doors["west"];
        var type = document.FindType<DoorType>(door.TypeId)!;
        string? Heat() => Doors.Problems(document, door, document.ProjectInformation.ClearanceStandard).FirstOrDefault(problem => problem.Said.StartsWith("its U-value", StringComparison.Ordinal))?.Rule;

        // A pedestrian door at 1.7, its fire rating taken off: over 1.6; a fire doorset at 1.7 within its 1.8 (note 14), at 1.9 over it.
        type.FireRating = string.Empty;
        type.HeatTransferCoefficient = 1.7;
        Assert.Equal("Approved Document L Volume 2 (Table 4.1) limits a pedestrian door of a new building other than a dwelling to a U-value of 1.6 W/m²K.", Heat());
        type.FireRating = "FD30S";
        Assert.Null(Heat());
        type.HeatTransferCoefficient = 1.9;
        Assert.Equal("Approved Document L Volume 2 (Table 4.1) limits an external fire doorset (note 14) of a new building other than a dwelling to a U-value of 1.8 W/m²K.", Heat());

        // Power-operated, a high-usage entrance door: 3.0; overhead, a vehicle door: 1.3.
        type.FireRating = string.Empty;
        type.Automatic = true;
        type.HeatTransferCoefficient = 2.9;
        Assert.Null(Heat());
        type.HeatTransferCoefficient = 3.1;
        Assert.Equal("Approved Document L Volume 2 (Table 4.1) limits a high-usage entrance door of a new building other than a dwelling to a U-value of 3.0 W/m²K.", Heat());
        type.Automatic = false;
        type.Operation = DoorOperation.Overhead;
        type.HeatTransferCoefficient = 1.5;
        Assert.Equal("Approved Document L Volume 2 (Table 4.1) limits a vehicle access door, or a similar large door, of a new building other than a dwelling to a U-value of 1.3 W/m²K.", Heat());

        // A house's: 1.6 whatever it is.
        document.ProjectInformation.BuildingType = "House";
        type.HeatTransferCoefficient = 1.7;
        Assert.Equal("Approved Document L (Table 4.1) limits an external door in a new dwelling to a U-value of 1.6 W/m²K.", Heat());
    }

    [Fact]
    public void AFlatRoofOfABuildingOtherThanAHomeIs018()
    {
        // The flat roof's insulation thinned to a U-value of 0.17: within an office's 0.18 for a flat roof, over a house's 0.16 for any; pitched, 0.16.
        var building = new Building("Office");
        var document = building.Document;
        var type = document.TypesOf<RoofType>().First(each => each.Name.Contains("Flat"));
        var roof = new Roof { LevelId = building.Level, HeightOffset = 3000, TypeId = type.Id };
        roof.SetBoundary(new[] { new Point2D(0, 0), new Point2D(20000, 0), new Point2D(20000, 12000), new Point2D(0, 12000) });
        document.Add(roof);
        var insulation = type.Structure.Layers.MaxBy(layer => layer.Thickness / Math.Max(1e-9, document.Materials.FirstOrDefault(material => material.Id == layer.MaterialId)?.ThermalConductivity ?? 1e9))!;
        var (thin, thick) = (1.0, insulation.Thickness * 3);
        for (var i = 0; i < 60; i++)
        {
            insulation.Thickness = (thin + thick) / 2;
            if (SlabRules.UValue(document, roof) > 0.17) thin = insulation.Thickness;
            else thick = insulation.Thickness;
        }

        Assert.Equal(0.17, SlabRules.UValue(document, roof)!.Value, 3);
        string? Heat() => SlabRules.Problems(document, roof, document.ProjectInformation.ClearanceStandard).FirstOrDefault(problem => problem.Said.StartsWith("its U-value", StringComparison.Ordinal))?.Rule;
        Assert.Equal(RoofForm.Flat, roof.Form);
        Assert.Null(Heat());
        foreach (var edge in roof.Edges)
        {
            edge.DefinesSlope = true;
            edge.SlopeDegrees = 30;
        }

        Assert.StartsWith("Approved Document L Volume 2 (Table 4.1) limits a pitched roof of a new building other than a dwelling to a U-value of 0.16 W/m²K", Heat());
        document.ProjectInformation.BuildingType = "House";
        foreach (var edge in roof.Edges) edge.DefinesSlope = false;
        Assert.StartsWith("Approved Document L (Table 4.1) limits a new dwelling's roof to a U-value of 0.16 W/m²K", Heat());
    }

    [Fact]
    public void AShopsDisplayWindowHasNoLimit()
    {
        // A window at 2.5: over 1.6 - a display window none (Table 4.1, note 10).
        var building = new Building("Shop");
        var document = building.Document;
        var window = building.Windows["office"];
        var type = document.FindType<WindowType>(window.TypeId)!;
        type.HeatTransferCoefficient = 2.5;
        string? Heat() => WindowRules.Problems(document, window, document.ProjectInformation.ClearanceStandard).FirstOrDefault(problem => problem.Said.StartsWith("its U-value", StringComparison.Ordinal))?.Rule;
        Assert.Equal("Approved Document L Volume 2 (Table 4.1) limits a window of a new building other than a dwelling to a U-value of 1.6 W/m²K - a display window to none.", Heat());
        type.Name = "Shopfront Display - 1200 x 1200";
        Assert.Null(Heat());
        document.ProjectInformation.BuildingType = "House";
        Assert.Equal("Approved Document L (Table 4.1) limits a window in a new dwelling to a U-value of 1.6 W/m²K.", Heat());
    }

    [Fact]
    public void ACurtainWallsDoorInAnOfficeIsVolume2s()
    {
        // A storefront's door cell between 800 and 850 clear: short of a house's 850 (M4(2)), within an office's 800 straight on (Table 2).
        var building = new Building("Office");
        var document = building.Document;
        var storefront = document.TypesOf<CurtainWallType>().Single(type => type.Name == "Curtain Wall - Storefront 1500");
        storefront.VerticalSpacing = 920;
        var wall = new Wall { Start = new Point2D(0, -3000), End = new Point2D(6000, -3000), TypeId = storefront.Id, LevelId = building.Level, UnconnectedHeight = 3000 };
        document.Add(wall);
        wall.CurtainPanels = new[] { new CurtainPanelOverride(1, 0, CurtainPanelKind.Door, document.TypesOf<DoorType>().First(t => t.CurtainPanel && t.Operation == DoorOperation.Swing).Id) };
        var cell = CurtainLayout.Of(document, wall)!.Cells.Single(each => each.Kind == CurtainPanelKind.Door);
        var clear = cell.ClearTo - cell.ClearFrom - Doors.Leaf;
        Assert.InRange(clear, 800, 850);
        IEnumerable<string> Rules() => WallRules.Problems(document, wall, document.ProjectInformation.ClearanceStandard).Where(problem => problem.Said.StartsWith("its door gives", StringComparison.Ordinal)).Select(problem => problem.Rule);
        Assert.Empty(Rules());
        document.ProjectInformation.BuildingType = "House";
        Assert.Contains("M4(2) 2.20d-e, 2.21", Assert.Single(Rules()));
    }

    [Fact]
    public void AHomesVentilationDesignAndWaterUseAreNotAskedOfAnOffice()
    {
        // A fan in the WC makes the ventilation a design to check: a dwelling's background ventilators (Table 1.7) and its water use (regulation
        // 36) asked of a house, not of an office - whose extract is said room by room by Volume 2.
        var building = new Building("Office");
        var document = building.Document;
        building.Put(ComponentForm.ExtractFan, 13500, 200, type => type.Name.Contains("60 l/s"));
        building.Put(ComponentForm.Toilet, 13500, 800);
        building.Put(ComponentForm.Basin, 12800, 800);
        Assert.DoesNotContain(Core.Mep.Ventilation.Problems(document), problem => problem.Text.Contains("Approved Document F (", StringComparison.Ordinal));
        Assert.Empty(Core.Mep.WaterUse.Problems(document));
        document.ProjectInformation.BuildingType = "House";
        Assert.Contains(Core.Mep.Ventilation.Problems(document), problem => problem.Text.Contains("Approved Document F (1.52, Table 1.7)", StringComparison.Ordinal));
    }

    [Fact]
    public void TheFireAlarmSystemsPartsGoToIfcAsAlarmsSensorsAndItsPanel()
    {
        var building = new Building("Office");
        foreach (var form in new[] { ComponentForm.FireAlarmCallPoint, ComponentForm.FireAlarmSounder, ComponentForm.VisualAlarm, ComponentForm.SmokeDetector, ComponentForm.HeatDetector, ComponentForm.FireAlarmPanel })
            building.Put(form, 10000, 6000);
        using var model = Infrastructure.Interoperability.IfcExport.Build(building.Document);
        Assert.Equal(new[] { Xbim.Ifc4.Interfaces.IfcAlarmTypeEnum.BREAKGLASSBUTTON, Xbim.Ifc4.Interfaces.IfcAlarmTypeEnum.LIGHT, Xbim.Ifc4.Interfaces.IfcAlarmTypeEnum.SIREN },
            model.Instances.OfType<Xbim.Ifc4.Interfaces.IIfcAlarm>().Select(alarm => alarm.PredefinedType!.Value).Order());
        Assert.Equal(new[] { Xbim.Ifc4.Interfaces.IfcSensorTypeEnum.HEATSENSOR, Xbim.Ifc4.Interfaces.IfcSensorTypeEnum.SMOKESENSOR },
            model.Instances.OfType<Xbim.Ifc4.Interfaces.IIfcSensor>().Select(sensor => sensor.PredefinedType!.Value).Order());
        Assert.Equal(Xbim.Ifc4.Interfaces.IfcUnitaryControlElementTypeEnum.ALARMPANEL, Assert.Single(model.Instances.OfType<Xbim.Ifc4.Interfaces.IIfcUnitaryControlElement>()).PredefinedType);
    }
}
