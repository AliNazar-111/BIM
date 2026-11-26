using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;
using BIMDesigner.Core.Mep;

namespace BIMDesigner.Tests;

/// <summary>
/// A building converted into homes in England (round 145): regulation 6's requirements for a material change of use; Approved Document L
/// 11.7 - retained walls, roofs and floors to Table 4.3, retained windows and doors replaced over 3.3, new ones to Table 4.2, openings a
/// quarter of the floor; Approved Document E Section 4's wall treatment 1; Approved Document S's S2; and Parts K, M and O left out.
/// </summary>
[Collection("Wpf")]
public class ConversionTests
{
    private const string Stone = "Stone Wall - Rubble 500mm", Lined = "Stone Wall - Rubble 500mm, Insulated Lining";

    private static Guid TypeOf<T>(BimDocument document, string name) where T : ElementType => document.TypesOf<T>().First(each => each.Name == name).Id;

    /// <summary>A barn 12 m by 6 m of rubble stone walls, drawn clockwise, of a phase; a room in it; converted to a dwelling or not.</summary>
    private static (BimDocument Document, List<Wall> Walls, Room Room) Barn(DesignPhase phase = DesignPhase.Existing, bool converted = true)
    {
        var document = BimDocument.CreateDefault();
        document.ProjectInformation.BuildingType = "Detached house";
        if (converted) (document.ProjectInformation.MaterialChange, document.ProjectInformation.PreviousUse) = (MaterialChange.ToDwelling, "Agricultural barn");
        var level = document.Levels.OrderBy(each => each.Elevation).First().Id;
        var corners = new[] { new Point2D(0, 0), new Point2D(0, 6000), new Point2D(12000, 6000), new Point2D(12000, 0) };
        var walls = new List<Wall>();
        for (var i = 0; i < 4; i++)
        {
            var wall = new Wall { Start = corners[i], End = corners[(i + 1) % 4], TypeId = TypeOf<WallType>(document, Stone), LevelId = level, UnconnectedHeight = 3000, PhaseCreated = phase };
            document.Add(wall);
            walls.Add(wall);
        }

        var room = new Room { LevelId = level, Location = new Point2D(6000, 3000), Name = "Living" };
        document.Add(room);
        return (document, walls, room);
    }

    private static IReadOnlyList<PlacementProblem> Problems(BimDocument document, Wall wall) => WallRules.Problems(document, wall, document.ProjectInformation.ClearanceStandard);

    [Fact]
    public void Regulation6SaysWhatAConversionIsAsked()
    {
        var (document, _, _) = Barn();
        var said = Conversion.Requirements(document);
        Assert.StartsWith("Regulation 6(1), a material change of use under regulation 5(a) from agricultural barn: B1, B2, B3, B4(2), B5, C1(2), C2, E1 to E3", said);
        Assert.EndsWith("Not A, K, M or O - what is retained needs no work for them; new work meets them as building work", said);
        document.ProjectInformation.MaterialChange = MaterialChange.None;
        Assert.Equal("A new building: all of Schedule 1", Conversion.Requirements(document));
        Assert.Equal("To flats (regulation 5(b))", Conversion.Said(MaterialChange.ToFlats));
    }

    [Fact]
    public void RetainedWallsAreUpgradedPastTable43AndNewOnesMeetTable42()
    {
        // The stone wall: 1 / (0.13 + 0.5/2.3 + 0.02/0.72 + 0.04), about 2.4 W/m²K - over 0.70: upgraded to 0.30 (Table 4.3).
        var (document, walls, _) = Barn();
        Assert.InRange(WallRules.UValue(document, document.GetWallType(walls[0])!)!.Value, 2.3, 2.5);
        var said = Assert.Single(Problems(document, walls[0]), problem => problem.Rule.Contains("Table 4.3"));
        Assert.StartsWith("it is a retained wall of U-value 2.", said.Said);
        Assert.EndsWith("over Table 4.3's threshold of 0.70 W/m²K.", said.Said);
        Assert.Contains("upgraded to 0.30 W/m²K - 0.55 where its cavity is filled", said.Rule);

        // Lined with 100 of rigid insulation: about 0.2, nothing asked.
        walls[0].TypeId = TypeOf<WallType>(document, Lined);
        Assert.True(WallRules.UValue(document, document.GetWallType(walls[0])!) < 0.30);
        Assert.DoesNotContain(Problems(document, walls[0]), problem => problem.Rule.Contains("Approved Document L"));

        // The brick and block wall built new in the conversion, about 0.25: over Table 4.2's 0.18 - where a new house's 0.26 would do.
        walls[1].TypeId = TypeOf<WallType>(document, "Exterior - Brick on Block 330mm");
        walls[1].PhaseCreated = DesignPhase.New;
        Assert.Contains(Problems(document, walls[1]), problem => problem.Said.StartsWith("it is a new wall of U-value") && problem.Rule.Contains("Table 4.2") && problem.Rule.Contains("0.18"));

        // Not converted: the stone wall a new house's, Table 4.1's 0.26.
        var (plain, plainWalls, _) = Barn(converted: false);
        Assert.Contains(Problems(plain, plainWalls[0]), problem => problem.Rule == "Approved Document L (Table 4.1) limits an outside wall of a new dwelling to a U-value of 0.26 W/m²K.");
    }

    [Fact]
    public void RetainedWindowsOver33AreReplacedNewOnesMeet14AndOpeningsAQuarterOfTheFloor()
    {
        var (document, walls, room) = Barn();
        var casement = document.TypesOf<WindowType>().First(each => each.Name == "Casement - 1200 x 1200");
        var single = casement.Duplicate("Casement - Single Glazed");
        single.HeatTransferCoefficient = 4.8;
        var double16 = casement.Duplicate("Casement - 1.6");
        double16.HeatTransferCoefficient = 1.6;
        document.AddType(single);
        document.AddType(double16);
        Window Put(WindowType type, double along, DesignPhase phase)
        {
            var window = new Window { HostWallId = walls[3].Id, LevelId = walls[3].LevelId, DistanceAlongWall = along, TypeId = type.Id, SillHeight = 900, PhaseCreated = phase };
            document.Add(window);
            return window;
        }

        var old = Put(single, 2000, DesignPhase.Existing);
        Assert.Contains("it is a retained window of U-value 4.8, over 3.3 W/m²K.", WindowRules.Problems(document, old, ClearanceStandard.ApprovedDocumentM).Select(problem => problem.Said));
        var fresh = Put(double16, 5000, DesignPhase.New);
        Assert.Contains(WindowRules.Problems(document, fresh, ClearanceStandard.ApprovedDocumentM), problem => problem.Said == "it is a new window of U-value 1.60." && problem.Rule.Contains("1.40"));
        var good = Put(casement, 8000, DesignPhase.New);
        Assert.DoesNotContain(WindowRules.Problems(document, good, ClearanceStandard.ApprovedDocumentM), problem => problem.Rule.Contains("Approved Document L"));
        Assert.Empty(Conversion.Problems(document));

        // Eight more 1200 squares: 11 of them, 15.8 m², past a quarter of the room's 11 by 5 m.
        for (var i = 0; i < 8; i++) Put(casement, 1000 + 1300 * i, DesignPhase.New).HostWallId = walls[i % 2 == 0 ? 1 : 3].Id;
        var openings = Assert.Single(Conversion.Problems(document));
        Assert.Same(room, openings.Room);
        Assert.Contains("Approved Document L (11.7c) keeps the openings of a dwelling formed by a material change of use to 25% of its floor area at most", openings.Said);
    }

    [Fact]
    public void WallTreatmentOneLinesARetainedSeparatingWall()
    {
        var document = BimDocument.CreateDefault();
        Material Find(string name) => document.Materials.First(material => material.Name == name);
        MaterialLayer Layer(string material, double thickness, LayerFunction function = LayerFunction.Finish2) => new(function, Find(material).Id, thickness);
        var board = "Gypsum Plasterboard";

        // Two layers of plasterboard each side over 50 of mineral wool, a 100 brick core: treated on both faces.
        var both = new WallType("Treated both sides", new CompoundStructure(Layer(board, 12.5, LayerFunction.Finish1), Layer(board, 12.5, LayerFunction.Finish1),
            Layer("Mineral Wool Insulation", 50, LayerFunction.ThermalAir), Layer("Brick, Common", 100, LayerFunction.Structure),
            Layer("Mineral Wool Insulation", 50, LayerFunction.ThermalAir), Layer(board, 12.5), Layer(board, 12.5)));
        Assert.True(Conversion.TreatmentOne(document, both));

        // One side only: a 100 brick core plastered both faces will do; a core of timber studs will not.
        var one = new WallType("Treated one side", new CompoundStructure(Layer(board, 12.5, LayerFunction.Finish1), Layer(board, 12.5, LayerFunction.Finish1),
            Layer("Mineral Wool Insulation", 50, LayerFunction.ThermalAir), Layer("Cement Plaster", 13), Layer("Brick, Common", 100, LayerFunction.Structure), Layer("Cement Plaster", 13)));
        Assert.True(Conversion.TreatmentOne(document, one));
        var studs = new WallType("Studs one side", new CompoundStructure(Layer(board, 12.5, LayerFunction.Finish1), Layer(board, 12.5, LayerFunction.Finish1),
            Layer("Mineral Wool Insulation", 50, LayerFunction.ThermalAir), Layer("Timber, Softwood", 100, LayerFunction.Structure), Layer(board, 12.5)));
        Assert.False(Conversion.TreatmentOne(document, studs));

        // One board only: not 20 kg/m².
        var thin = new WallType("One board", new CompoundStructure(Layer(board, 12.5, LayerFunction.Finish1), Layer("Mineral Wool Insulation", 50, LayerFunction.ThermalAir),
            Layer("Brick, Common", 100, LayerFunction.Structure), Layer("Mineral Wool Insulation", 50, LayerFunction.ThermalAir), Layer(board, 12.5)));
        Assert.False(Conversion.TreatmentOne(document, thin));
    }

    [Fact]
    public void PartsKAndMAreLeftOutAndS2AsksAChargePointForEachDwelling()
    {
        // An existing stair with no handrail: Approved Document K's - said in a new house, and of a new stair in a conversion; left out of a
        // retained one's warnings.
        var (document, walls, _) = Barn();
        var levels = document.Levels.OrderBy(each => each.Elevation).ToList();
        var stair = new Stair
        {
            TypeId = document.TypesOf<StairType>().First().Id, LevelId = levels[0].Id, TopLevelId = levels[1].Id, Start = new Point2D(2000, 1000), Angle = 90, Width = 900,
            Railings = StairRailings.None, PhaseCreated = DesignPhase.Existing
        };
        document.Add(stair);
        Assert.Contains(Stairs.Problems(document, stair), problem => problem.Contains("Approved Document K"));
        bool Said(Element element, string what) => Warnings.Of(document).Any(warning => warning.Elements.Contains(element) && warning.Said.Contains(what));
        Assert.False(Said(stair, "Approved Document K"));
        stair.PhaseCreated = DesignPhase.New;
        Assert.True(Said(stair, "Approved Document K"));
        stair.PhaseCreated = DesignPhase.Existing;
        document.ProjectInformation.MaterialChange = MaterialChange.None;
        Assert.True(Said(stair, "Approved Document K"));

        // Part M: an entrance door too narrow for M4(2) said of a new house, not of a conversion.
        var door = new Door { HostWallId = walls[3].Id, LevelId = walls[3].LevelId, DistanceAlongWall = 6000, TypeId = TypeOf<DoorType>(document, "Single - 900 x 2100") };
        document.Add(door);
        bool PartM() => Warnings.Of(document).Any(warning => warning.Kind == WarningKind.Regulations && warning.Said.Contains("Approved Document M"));
        Assert.True(PartM());
        document.ProjectInformation.MaterialChange = MaterialChange.ToDwelling;
        Assert.False(PartM());

        // S2: one dwelling, two parking bays outside, no charge point - one asked, by S2 and regulation 44E.
        var bay = TypeOf<ComponentType>(document, "Parking Bay - 2400 x 4800");
        foreach (var x in new[] { 16000.0, 19000 }) document.Add(new Component { LevelId = levels[0].Id, TypeId = bay, Location = new Point2D(x, 3000) });
        var charging = Assert.Single(EvCharging.Problems(document));
        Assert.Equal("0 of the 2 associated parking spaces have a charge point - Approved Document S (S2, regulation 44E) asks one for each dwelling formed by a material change of use, 1, where the car park or its electrics are worked on and the incoming supply takes them", charging.Text);
        document.ProjectInformation.MaterialChange = MaterialChange.None;
        Assert.Contains(EvCharging.Problems(document), problem => problem.Text.Contains("(S1(2), 1.1, 1.4)"));
    }
}
