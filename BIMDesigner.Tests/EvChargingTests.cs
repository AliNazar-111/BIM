using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Mep;

namespace BIMDesigner.Tests;

/// <summary>
/// Charging electric vehicles by Approved Document S (round 98): a new home's parking spaces given charge points (S1, 1.1, 1.4), cable routes past
/// 10 spaces and in a covered car park (S1(3), 44D(4), 1.2, 1.3), a building other than a residential one (S4, 3.1), and the charge points
/// themselves (6.2, 6.4).
/// </summary>
[Collection("Wpf")]
public class EvChargingTests
{
    /// <summary>A house 8 m by 6 m of brick on block, a room in it, its site's boundary 20 m round it - or none.</summary>
    private static BimDocument House(bool boundary = true)
    {
        var document = BimDocument.CreateDefault();
        var level = document.Levels[0].Id;
        var brick = document.TypesOf<WallType>().First(type => type.Name == "Exterior - Brick on Block 330mm");
        var corners = new[] { new Point2D(0, 0), new Point2D(0, 6000), new Point2D(8000, 6000), new Point2D(8000, 0) };
        for (var i = 0; i < 4; i++) document.Add(new Wall { Start = corners[i], End = corners[(i + 1) % 4], TypeId = brick.Id, LevelId = level, UnconnectedHeight = 3000 });
        document.Add(new Room { LevelId = level, Location = new Point2D(4000, 3000), Name = "Living Room" });
        if (boundary)
        {
            var line = new PropertyLine { LevelId = level };
            line.SetBoundary(new[] { new Point2D(-20000, -20000), new Point2D(28000, -20000), new Point2D(28000, 26000), new Point2D(-20000, 26000) });
            document.Add(line);
        }

        return document;
    }

    private static Component Place(BimDocument document, string type, double x, double y)
    {
        var kind = document.TypesOf<ComponentType>().First(each => each.Name == type);
        var piece = new Component { TypeId = kind.Id, TypeKind = kind.Kind, LevelId = document.Levels[0].Id, Location = new Point2D(x, y), Elevation = 0 };
        document.Add(piece);
        return piece;
    }

    /// <summary>Bays 2.4 by 4.8 m side by side along x from so far, their middles at y -10 m - their heads at -7.6 m.</summary>
    private static List<Component> Bays(BimDocument document, int count, double from = 1200, string type = "Parking Bay - 2400 x 4800") =>
        Enumerable.Range(0, count).Select(i => Place(document, type, from + 2400 * i, -10000)).ToList();

    private static List<string> S(BimDocument document) => EvCharging.Problems(document).Select(problem => problem.Text).ToList();

    [Fact]
    public void ANewHomesParkingSpaceHasAChargePoint()
    {
        // A house with a space on its drive and no charge point: one asked.
        var document = House();
        var bay = Bays(document, 1).Single();
        Assert.Equal("0 of the 1 associated parking spaces outside a covered car park have a charge point - Approved Document S (S1(2), 1.1, 1.4) asks 1: as many as there are dwellings, 1, or as there are spaces where there are fewer, outside a covered car park first",
            Assert.Single(S(document)));

        // A pedestal at its head, 300 mm off it: met.
        var pedestal = Place(document, "EV Charge Point - 7.4 kW Pedestal", 1200, -7300);
        Assert.Empty(S(document));

        // 3 m off it: too far to serve it (1 m, taken so).
        pedestal.Location = new Point2D(1200, -4600);
        Assert.Single(S(document));
        pedestal.Location = new Point2D(1200, -7300);

        // The space outside the site's boundary: not associated with the house - nothing asked.
        bay.Location = new Point2D(40000, -10000);
        Assert.Empty(S(document));
    }

    [Fact]
    public void AChargePointIs7kWWithAUniversalSocket()
    {
        var document = House();
        Bays(document, 1);
        var kind = (ComponentType)document.TypesOf<ComponentType>().First(each => each.Name == "EV Charge Point - 7.4 kW Pedestal").Duplicate("EV Charge Point - 3.6 kW Tethered");
        (kind.Load, kind.Tethered) = (3600, true);
        document.AddType(kind);
        Place(document, kind.Name, 1200, -7300);
        var said = S(document);
        Assert.Equal(2, said.Count);
        Assert.Contains("it charges at 3.6 kW an outlet - Approved Document S (6.2b) asks a charge point of 7 kW nominal rated output at least", said);
        Assert.Contains(said, each => each.StartsWith("its cable is tethered - Approved Document S (6.2c)", StringComparison.Ordinal));
    }

    [Fact]
    public void FlatsPast10SpacesHaveCableRoutesToEverySpaceWithoutACharger()
    {
        // Four flats named in the house's rooms and 12 spaces: four charge points (S1(2)), and cable routes to the other eight (S1(3), 1.3).
        var document = House();
        var level = document.Levels[0].Id;
        document.Remove(document.All<Room>().Single());
        var partition = document.TypesOf<WallType>().First(type => type.Name == "Interior - Partition 125mm");
        foreach (var x in new[] { 2000.0, 4000, 6000 })
            document.Add(new Wall { Start = new Point2D(x, 0), End = new Point2D(x, 6000), TypeId = partition.Id, LevelId = level, UnconnectedHeight = 3000 });
        for (var i = 0; i < 4; i++) document.Add(new Room { LevelId = level, Location = new Point2D(1000 + 2000 * i, 3000), Name = "Living Room", Dwelling = $"Flat {i + 1}" });
        Bays(document, 12);
        Assert.Contains(S(document), said => said.StartsWith("0 of the 12 associated parking spaces outside a covered car park have a charge point - Approved Document S (S1(2), 1.1, 1.4) asks 4", StringComparison.Ordinal));

        // Twin pedestals between the first four spaces: four served - met; the other eight bare.
        Place(document, "EV Charge Point - 2 x 7.4 kW Pedestal", 2400, -7300);
        Place(document, "EV Charge Point - 2 x 7.4 kW Pedestal", 7200, -7300);
        Assert.Equal("8 of the 12 associated parking spaces have neither a charge point nor cable routes - Approved Document S (S1(3), 1.3) asks cable routes to every space without a charge point where there are more than 10 spaces and more spaces than dwellings, 4",
            Assert.Single(S(document)));

        // Twin future connection locations between the other eight: met.
        foreach (var x in new[] { 12000.0, 16800, 21600, 26400 }) Place(document, "EV Future Connection Location - 2 Spaces", x, -7300);
        Assert.Empty(S(document));
    }

    [Fact]
    public void SpacesInACoveredCarParkHaveCableRoutes()
    {
        // Two flats over a car park of two spaces, enclosed: none outside it, so no charge point asked - cable routes to both (44D(4)(b), 1.2).
        var document = House(boundary: false);
        var level = document.Levels[0].Id;
        var room = document.All<Room>().Single();
        room.Name = "Car Park";
        var upper = document.Levels[1].Id;
        var brick = document.TypesOf<WallType>().First(type => type.Name == "Exterior - Brick on Block 330mm");
        var corners = new[] { new Point2D(0, 0), new Point2D(0, 6000), new Point2D(8000, 6000), new Point2D(8000, 0) };
        for (var i = 0; i < 4; i++) document.Add(new Wall { Start = corners[i], End = corners[(i + 1) % 4], TypeId = brick.Id, LevelId = upper, UnconnectedHeight = 3000 });
        document.Add(new Wall { Start = new Point2D(4000, 0), End = new Point2D(4000, 6000), TypeId = document.TypesOf<WallType>().First(type => type.Name == "Interior - Partition 125mm").Id, LevelId = upper, UnconnectedHeight = 3000 });
        document.Add(new Room { LevelId = upper, Location = new Point2D(2000, 3000), Name = "Living Room", Dwelling = "Flat 1" });
        document.Add(new Room { LevelId = upper, Location = new Point2D(6000, 3000), Name = "Living Room", Dwelling = "Flat 2" });
        Place(document, "Parking Bay - 2400 x 4800", 2000, 3000);
        Place(document, "Parking Bay - 2400 x 4800", 5000, 3000);
        Assert.Equal("0 of the 2 spaces in the covered car park have cable routes or a charge point - Approved Document S (44D(4), 1.2) asks 2, to make up one a dwelling with the spaces outside it",
            Assert.Single(S(document)));

        // A twin future connection location on the wall between their heads: met.
        Place(document, "EV Future Connection Location - 2 Spaces", 3500, 5600);
        Assert.Empty(S(document));
        _ = level;
    }

    [Fact]
    public void AnOfficeWithMoreThan10SpacesHasOneChargePointAndCableRoutesToOneInFive()
    {
        // An office and 11 spaces: one charge point, and cable routes to two of the other ten (3.1, its note).
        var document = House();
        document.ProjectInformation.BuildingType = "Office";
        Bays(document, 11);
        var said = S(document);
        Assert.Contains(said, each => each.StartsWith("none of its 11 parking spaces has a charge point - Approved Document S (S4, 3.1a, 3.3)", StringComparison.Ordinal));
        Assert.Contains("0 of its other parking spaces have cable routes - Approved Document S (S4, 3.1b, 3.4) asks at least one in five of them, 2", said);

        // A pedestal at the first, a twin future connection location between the next two: met.
        Place(document, "EV Charge Point - 7.4 kW Pedestal", 1200, -7300);
        Place(document, "EV Future Connection Location - 2 Spaces", 4800, -7300);
        Assert.Empty(S(document));

        // An accessible space among them, none of the accessible ones served (6.4).
        var accessible = document.All<Component>().Where(piece => document.FindType<ComponentType>(piece.TypeId)?.Form == ComponentForm.ParkingBay).OrderBy(piece => piece.Location.X).Last();
        accessible.TypeId = document.TypesOf<ComponentType>().First(type => type.Form == ComponentForm.AccessibleParkingBay).Id;
        Assert.Equal("none of the accessible parking spaces has a charge point or a future connection location - Approved Document S (6.4, Diagram 6.1) asks at least one of them to have one, where they are given",
            Assert.Single(S(document)));

        // Saved and opened again; in IFC a charge point is an outlet of its own kind.
        var twin = document.TypesOf<ComponentType>().First(type => type.Name == "EV Charge Point - 2 x 7.4 kW Pedestal");
        twin.Tethered = true;
        var opened = BIMDesigner.Infrastructure.Serialization.ProjectFile.FromJson(BIMDesigner.Infrastructure.Serialization.ProjectFile.ToJson(document));
        var back = opened.TypesOf<ComponentType>().Single(type => type.Name == twin.Name);
        Assert.Equal((2, true, 14800.0), (back.Outlets, back.Tethered, back.Load));
        using var model = BIMDesigner.Infrastructure.Interoperability.IfcExport.Build(document);
        Assert.Contains(model.Instances.OfType<Xbim.Ifc4.Interfaces.IIfcOutlet>(), outlet => outlet.PredefinedType == Xbim.Ifc4.Interfaces.IfcOutletTypeEnum.USERDEFINED && outlet.ObjectType.ToString() == "Electric vehicle charge point");
    }
}
