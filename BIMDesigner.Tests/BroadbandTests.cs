using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Mep;

namespace BIMDesigner.Tests;

/// <summary>
/// Infrastructure for electronic communications (round 126), by Approved Document R (2022): a network termination point in each new home
/// with a supply at it and its controls within reach, an access point - a common one for a building of several homes - and a building
/// other than a home's termination point and access point by Volume 2.
/// </summary>
public class BroadbandTests
{
    private const string Brick = "Exterior - Brick on Block 330mm";

    /// <summary>A building of a use 12 m by 8 m: a partition at x 6 m, two rooms, named as given.</summary>
    private static (BimDocument Document, Room West, Room East, Wall South) Building(string use, string west = "Living Room", string east = "Kitchen")
    {
        var document = BimDocument.CreateDefault();
        document.ProjectInformation.BuildingType = use;
        var level = document.Levels[0].Id;
        var corners = new[] { new Point2D(0, 0), new Point2D(12000, 0), new Point2D(12000, 8000), new Point2D(0, 8000) };
        var walls = Enumerable.Range(0, 4).Select(i =>
        {
            var wall = new Wall { Start = corners[i], End = corners[(i + 1) % 4], TypeId = document.TypesOf<WallType>().First(each => each.Name == Brick).Id, LevelId = level, UnconnectedHeight = 3000 };
            document.Add(wall);
            return wall;
        }).ToList();
        document.Add(new Wall { Start = new Point2D(6000, 0), End = new Point2D(6000, 8000), TypeId = document.TypesOf<WallType>().First(each => each.Name == "Interior - Partition 125mm").Id, LevelId = level, UnconnectedHeight = 3000 });
        var a = new Room { LevelId = level, Location = new Point2D(3000, 4000), Name = west, Number = "1" };
        var b = new Room { LevelId = level, Location = new Point2D(9000, 4000), Name = east, Number = "2" };
        document.Add(a);
        document.Add(b);
        return (document, a, b, walls[0]);
    }

    private static Component Put(BimDocument document, ComponentForm form, double x, double y, double? elevation = null, string? name = null)
    {
        var type = document.TypesOf<ComponentType>().First(each => each.Form == form && (name is null || each.Name == name));
        var piece = new Component { TypeId = type.Id, TypeKind = type.Kind, LevelId = document.Levels[0].Id, Location = new Point2D(x, y), Elevation = elevation ?? type.DefaultElevation };
        document.Add(piece);
        return piece;
    }

    private static List<string> Said(BimDocument document) => Broadband.Problems(document).Select(problem => problem.Text).ToList();

    [Fact]
    public void ANewHomeHasANetworkTerminationPointAndAnAccessPoint()
    {
        // A house with nothing: no termination point (RA1, 2.6), no access point (RA1(1)(d)).
        var (house, living, _, _) = Building("House");
        var said = Said(house);
        Assert.Equal(2, said.Count);
        Assert.StartsWith("the home has no network termination point - Approved Document R (Volume 1, RA1, 2.6)", said[0], StringComparison.Ordinal);
        Assert.StartsWith("no access point for electronic communications networks - Approved Document R (Volume 1, RA1(1)(d), 1.5, Diagram 5)", said[1], StringComparison.Ordinal);

        // A termination point in the living room, 450 up - its middle 575 - a socket 600 from it, and an access point on the south wall: met.
        var point = Put(house, ComponentForm.NetworkTerminationPoint, 3000, 7800);
        Put(house, ComponentForm.Socket, 3600, 7800);
        Put(house, ComponentForm.CommsAccessPoint, 2000, -200, name: "Comms Access Point - External Box");
        Assert.Empty(Said(house));

        // 1300 up - its middle 1425: beyond Part M's 1200 (2.8).
        point.Elevation = 1300;
        Assert.Contains(Said(house), text => text.StartsWith("its middle is 1425 above the floor - Approved Document R (Volume 1, 2.8)", StringComparison.Ordinal));

        // Its socket 1.5 m off: no supply at it (2.7).
        point.Elevation = 450;
        house.All<Component>().Single(piece => house.FindType<ComponentType>(piece.TypeId)!.Form == ComponentForm.Socket).Location = new Point2D(4500, 7800);
        Assert.Contains(Said(house), text => text.Contains("(Volume 1, 2.7)"));
    }

    [Fact]
    public void FlatsHaveATerminationPointEachAndACommonAccessPoint()
    {
        // Two flats side by side, each with its termination point and socket; the access point inside Flat 1 - not a common one.
        var (block, west, east, _) = Building("Flat");
        (west.Dwelling, east.Dwelling) = ("Flat 1", "Flat 2");
        foreach (var x in new[] { 3000.0, 9000 })
        {
            Put(block, ComponentForm.NetworkTerminationPoint, x, 7800);
            Put(block, ComponentForm.Socket, x + 500, 7800);
        }

        var cabinet = Put(block, ComponentForm.CommsAccessPoint, 1000, 200, name: "Common Access Point - Comms Cabinet");
        Assert.Contains(Said(block), text => text.StartsWith("no common access point for the building's 2 dwellings - Approved Document R (Volume 1, RA1(2), 1.5, 1.26, Diagram 6)", StringComparison.Ordinal));

        // Moved outside both, onto the south wall's face: met.
        cabinet.Location = new Point2D(6000, -400);
        Assert.Empty(Said(block));

        // Flat 2's termination point taken away: Flat 2 has none.
        block.Remove(block.All<Component>().First(piece => block.FindType<ComponentType>(piece.TypeId)!.Form == ComponentForm.NetworkTerminationPoint && piece.Location.X > 6000));
        Assert.Contains(Said(block), text => text.StartsWith("Flat 2 has no network termination point", StringComparison.Ordinal));
    }

    [Fact]
    public void ABuildingOtherThanAHomeHasATerminationPointDuctedToAnAccessPoint()
    {
        // An office: Volume 2's R1 - one termination point at least and an access point (1.6). A hotel's rooms are no dwellings (Volume 1 1.6c):
        // the same.
        foreach (var use in new[] { "Office", "Hotel" })
        {
            var (office, _, _, _) = Building(use, "Office", "Store");
            var said = Said(office);
            Assert.Contains(said, text => text.StartsWith("no network termination point - Approved Document R (Volume 2, R1, 1.6)", StringComparison.Ordinal));
            Assert.Contains(said, text => text.StartsWith("no access point for electronic communications networks - Approved Document R (Volume 2, R1, 1.6, 1.7, Diagram 1)", StringComparison.Ordinal));
            Put(office, ComponentForm.NetworkTerminationPoint, 3000, 7800);
            Put(office, ComponentForm.Socket, 3500, 7800);
            Put(office, ComponentForm.CommsAccessPoint, 2000, -200, name: "Comms Access Point - External Box");
            Assert.Empty(Said(office));
        }
    }

    [Fact]
    public void TheTerminationPointAndAccessPointGoToIfcAsCommunicationsAppliances()
    {
        var (house, _, _, _) = Building("House");
        Put(house, ComponentForm.NetworkTerminationPoint, 3000, 7800);
        Put(house, ComponentForm.CommsAccessPoint, 2000, -200, name: "Comms Access Point - External Box");
        using var model = BIMDesigner.Infrastructure.Interoperability.IfcExport.Build(house);
        var appliances = model.Instances.OfType<Xbim.Ifc4.Interfaces.IIfcCommunicationsAppliance>().ToList();
        Assert.Equal(2, appliances.Count);
        Assert.Contains(appliances, each => each.PredefinedType == Xbim.Ifc4.Interfaces.IfcCommunicationsApplianceTypeEnum.MODEM && each.ObjectType == "Network termination point");
        Assert.Contains(appliances, each => each.PredefinedType == Xbim.Ifc4.Interfaces.IfcCommunicationsApplianceTypeEnum.USERDEFINED && each.ObjectType == "Access point for electronic communications networks");
    }
}
