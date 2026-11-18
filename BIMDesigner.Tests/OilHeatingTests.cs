using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Mep;

namespace BIMDesigner.Tests;

/// <summary>
/// Heating with oil (round 91), as Approved Document J has it: an open-flued oil boiler out of bathrooms and bedrooms (4.2) and its air,
/// 550 mm² a kW (Diagram 40); an oil tank kept from the building and the boundary (Table 10), on its hard base (5.4) and bunded over 2500
/// litres (5.8 a).
/// </summary>
[Collection("Wpf")]
public class OilHeatingTests
{
    /// <summary>A house 8 m by 6 m of brick on block - 120 minutes - a room in it, a window 1.2 m wide in the middle of its south wall.</summary>
    private static (BimDocument Document, Room Room, Wall South) House(string name = "Utility")
    {
        var document = BimDocument.CreateDefault();
        var level = document.Levels[0].Id;
        var brick = document.TypesOf<WallType>().First(type => type.Name == "Exterior - Brick on Block 330mm");
        var corners = new[] { new Point2D(0, 0), new Point2D(0, 6000), new Point2D(8000, 6000), new Point2D(8000, 0) };
        var walls = Enumerable.Range(0, 4).Select(i => new Wall { Start = corners[i], End = corners[(i + 1) % 4], TypeId = brick.Id, LevelId = level, UnconnectedHeight = 3000 }).ToList();
        foreach (var wall in walls) document.Add(wall);
        var room = new Room { LevelId = level, Location = new Point2D(4000, 3000), Name = name };
        document.Add(room);
        document.Add(new Window { HostWallId = walls[3].Id, TypeId = document.TypesOf<WindowType>().First(type => type.Name == "Casement - 1200 x 1200").Id, LevelId = level, DistanceAlongWall = 4000, SillHeight = 900 });
        return (document, room, walls[3]);
    }

    private static Component Place(BimDocument document, string type, double x, double y)
    {
        var kind = document.TypesOf<ComponentType>().First(each => each.Name == type);
        var piece = new Component { TypeId = kind.Id, TypeKind = kind.Kind, LevelId = document.Levels[0].Id, Location = new Point2D(x, y), Elevation = kind.DefaultElevation };
        document.Add(piece);
        return piece;
    }

    private static GroundRegion Paving(BimDocument document, double x0, double y0, double x1, double y1)
    {
        var region = new GroundRegion { LevelId = document.Levels[0].Id, Surface = GroundSurface.Paving };
        region.SetBoundary(new[] { new Point2D(x0, y0), new Point2D(x1, y0), new Point2D(x1, y1), new Point2D(x0, y1) });
        document.Add(region);
        return region;
    }

    private static List<string> Of(BimDocument document, Core.Elements.Element element) =>
        OilHeating.Problems(document).Where(problem => problem.Element == element).Select(problem => problem.Text).ToList();

    [Fact]
    public void AnOilBoilerIsAGasBoilerNot()
    {
        var document = BimDocument.CreateDefault();
        var oil = document.TypesOf<ComponentType>().First(type => type.Name == "Boiler - Oil System, Open-Flued");
        Assert.True(oil.BurnsOil);
        Assert.False(oil.BurnsGas);
        Assert.Equal(0, GasPipes.InputOf(oil));
        Assert.DoesNotContain(Connectors.Of(oil), connector => connector.Kind == SystemKind.Gas);
        Assert.Contains(Connectors.Of(document.TypesOf<ComponentType>().First(type => type.Name == "Boiler - Gas Combi")), connector => connector.Kind == SystemKind.Gas);
        Assert.True(document.TypesOf<ComponentType>().First(type => type.Name == "Boiler - Gas Combi").BurnsGas);

        // Its fuel, and a tank's being bunded, kept when the project is saved and opened again; the tank sent to IFC as a storage tank.
        var unbunded = (ComponentType)document.TypesOf<ComponentType>().First(type => type.Name == "Oil Tank - Bunded 1225 l").Duplicate("Oil Tank - 1225 l, Unbunded");
        unbunded.Bunded = false;
        document.AddType(unbunded);
        var opened = BIMDesigner.Infrastructure.Serialization.ProjectFile.FromJson(BIMDesigner.Infrastructure.Serialization.ProjectFile.ToJson(document));
        Assert.Equal(ApplianceFuel.Oil, opened.TypesOf<ComponentType>().First(type => type.Name == "Boiler - Oil System, Open-Flued").Fuel);
        Assert.True(opened.TypesOf<ComponentType>().First(type => type.Name == "Oil Tank - Bunded 1225 l").Bunded);
        Assert.False(opened.TypesOf<ComponentType>().First(type => type.Name == "Oil Tank - 1225 l, Unbunded").Bunded);
        Assert.Equal(1225, opened.TypesOf<ComponentType>().First(type => type.Name == "Oil Tank - 1225 l, Unbunded").Capacity);
        Place(document, "Oil Tank - Bunded 1225 l", 0, -5000);
        using (var model = BIMDesigner.Infrastructure.Interoperability.IfcExport.Build(document))
            Assert.Equal(Xbim.Ifc4.Interfaces.IfcTankTypeEnum.STORAGE, Assert.Single(model.Instances.OfType<Xbim.Ifc4.Interfaces.IIfcTank>()).PredefinedType);
    }

    [Fact]
    public void AnOpenFluedOilBoilerKeepsOutOfBedroomsAndHasItsAir()
    {
        // 21 kW open-flued in a utility room: 21 x 550 = 11,550 mm² of vent wanted, none there.
        var (document, _, _) = House();
        var boiler = Place(document, "Boiler - Oil System, Open-Flued", 2000, 5500);
        Assert.Equal("open-flued at 21.0 kW, it wants 11,550 mm² of permanent vent to outside in Utility - 550 mm² a kW of its output (Approved Document J, Diagram 40) - and has 0; a window's trickle vent closes and does not count",
            Assert.Single(Of(document, boiler)));

        // Three 5000 mm² ventilators: 15,000, enough.
        foreach (var x in new[] { 1000.0, 3000.0, 5000.0 }) Place(document, "Background Ventilator - 5000 mm²", x, 5800);
        Assert.Empty(Of(document, boiler));

        // A 60 l/s extract fan in its room: past 1.20 b's 40 l/s.
        var fan = Place(document, "Extract Fan - 150 (60 l/s)", 6000, 5800);
        Assert.StartsWith("open-flued in Utility, whose extract draws 60 l/s - Approved Document J (1.20 b)", Assert.Single(Of(document, boiler)));
        document.Remove(fan);

        // In a bedroom: a room-sealed one there (4.2) - and a room-sealed one asks nothing.
        (document, _, _) = House("Bedroom");
        boiler = Place(document, "Boiler - Oil System, Open-Flued", 2000, 5500);
        Assert.Contains(Of(document, boiler), said => said.StartsWith("open-flued, in Bedroom, a bedroom - Approved Document J (4.2)", StringComparison.Ordinal));
        var sealedBoiler = Place(document, "Boiler - Oil Combi, Room-Sealed", 6000, 5500);
        Assert.Empty(Of(document, sealedBoiler));
    }

    [Fact]
    public void AnOilTankIsKeptFromTheBuildingAndTheBoundaryOnItsBase()
    {
        // A bunded 1225 l tank 1.85 by 1.15 m, its near side 2.76 m from the house's south face: no hard base under it.
        var (document, _, _) = House();
        var tank = Place(document, "Oil Tank - Bunded 1225 l", 4000, -3500);
        Assert.StartsWith("no hard base under it reaching 300 mm past it all round - Approved Document J (5.4)", Assert.Single(Of(document, tank)));

        // Paving 300 mm past it: met.
        Paving(document, 2700, -4500, 5300, -2500);
        Assert.Empty(Of(document, tank));

        // Moved to 1 m from the face, the window over it 1.0 m off: the wall within 1.8 m is not imperforate (Table 10).
        tank.Location = new Point2D(4000, -1740);
        Paving(document, 2700, -2700, 5300, -700);
        var near = Assert.Single(Of(document, tank));
        Assert.StartsWith("it stands 1.0 m from the building - Approved Document J (Table 10) asks the walls within 1.8 m of a tank imperforate and resisting fire 30 minutes", near);
        Assert.EndsWith("; here a window 1.0 m from it", near);

        // A fire wall between, 2 m high - the tank 1.55 m and 300 mm more - and 3 m long, 300 mm past it either side: met.
        var block = document.TypesOf<WallType>().First(type => type.Name == "Exterior - Brick on Block 330mm");
        document.Add(new Wall { Start = new Point2D(2500, -900), End = new Point2D(5500, -900), TypeId = block.Id, LevelId = document.Levels[0].Id, UnconnectedHeight = 2000 });
        Assert.Empty(Of(document, tank));

        // The site's boundary 0.5 m south of it: 760 mm asked, or a fire wall or a 30 minute boundary wall.
        var line = new PropertyLine { LevelId = document.Levels[0].Id };
        line.SetBoundary(new[] { new Point2D(-10000, -2815), new Point2D(20000, -2815), new Point2D(20000, 20000), new Point2D(-10000, 20000) });
        document.Add(line);
        Assert.StartsWith("it stands 0.50 m from the boundary - Approved Document J (Table 10)", Assert.Single(Of(document, tank)));

        // A brick boundary wall between, 2 m high, past it either side: met.
        document.Add(new Wall { Start = new Point2D(2500, -2650), End = new Point2D(5500, -2650), TypeId = block.Id, LevelId = document.Levels[0].Id, UnconnectedHeight = 2000 });
        Assert.Empty(Of(document, tank));
    }

    [Fact]
    public void ABigTankIsBundedAndOneIndoorsIsSaidSo()
    {
        // A single-skinned 2700 l tank on its base: more than 2500 litres, unbunded (5.8 a).
        var (document, room, _) = House();
        var tank = Place(document, "Oil Tank - Single Skin 2700 l", 4000, -4000);
        Paving(document, 2400, -5200, 5600, -2800);
        Assert.Equal("it holds 2,700 litres and is not bunded - Approved Document J (5.8 a, 5.10) asks secondary containment, a bunded tank or a bund holding 110% of it, where a tank holds more than 2500 litres",
            Assert.Single(Of(document, tank)));

        // Indoors, in the utility room: a place of special fire hazard, which is not looked into here.
        var indoors = Place(document, "Oil Tank - Bunded 1225 l", 4000, 3000);
        Assert.StartsWith($"it is inside the building, in {RoomsOf.Label(room)} - Approved Document J (Table 10)", Assert.Single(Of(document, indoors)));
    }
}
