using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Tests;

/// <summary>
/// A wheelchair user dwelling's storage and transfer space, Approved Document M Volume 1 3.25 and Diagram 3.6 (round 160): 1700 wide and
/// 1100 deep on the entrance storey, opening off a space 1200 clear across its width, a power socket in it - a component of its own.
/// </summary>
public class WheelchairStorageTests
{
    private const string Store = "Wheelchair Storage and Transfer Space - 1700 x 1100";

    /// <summary>A house 8 m by 6 m checked to M4(3), its front door in its south wall into a hall 2.7 m wide on the west, a living room east of it.</summary>
    private static (BimDocument Document, Door Front, Level Ground) House()
    {
        var document = BimDocument.CreateDefault();
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.ApprovedDocumentM3;
        var ground = document.Levels.OrderBy(level => level.Elevation).First();
        var outside = document.TypesOf<WallType>().First(type => type.Name == "Exterior - Brick on Block 330mm");
        var inside = document.TypesOf<WallType>().First(type => type.Name == "Interior - Partition 125mm");
        Wall Wall(double x1, double y1, double x2, double y2, WallType type)
        {
            var wall = new Wall { Start = new Point2D(x1, y1), End = new Point2D(x2, y2), TypeId = type.Id, LevelId = ground.Id, UnconnectedHeight = 3000 };
            document.Add(wall);
            return wall;
        }

        Wall(0, 0, 0, 6000, outside);
        Wall(0, 6000, 8000, 6000, outside);
        Wall(8000, 6000, 8000, 0, outside);
        var south = Wall(8000, 0, 0, 0, outside);
        Wall(2700, 0, 2700, 6000, inside);
        var front = new Door { TypeId = document.TypesOf<DoorType>().First(type => type.Name == "Front Door - 1000 x 2100").Id, LevelId = ground.Id, HostWallId = south.Id, DistanceAlongWall = 6750 };
        document.Add(front);
        document.Add(new Room { LevelId = ground.Id, Location = new Point2D(1250, 3000), Name = "Hall" });
        document.Add(new Room { LevelId = ground.Id, Location = new Point2D(5000, 3000), Name = "Living Room" });
        return (document, front, ground);
    }

    /// <summary>The storage space against the hall's west wall, its back to it, facing east (rotation 90): 1700 along the wall, 1100 out from its face.</summary>
    private static Component Put(BimDocument document, Level ground, double y = 3500)
    {
        var type = document.TypesOf<ComponentType>().Single(each => each.Name == Store);
        var piece = new Component { TypeId = type.Id, TypeKind = type.Kind, LevelId = ground.Id, Location = new Point2D(165 + type.Depth / 2, y), Rotation = 90 };
        document.Add(piece);
        return piece;
    }

    [Fact]
    public void AWheelchairUserDwellingHasAStorageAndTransferSpaceOnItsEntranceStorey()
    {
        // Offered in an English project, not a US one; 1700 by 1100.
        var england = ProjectTemplates.Create(new NewProjectSpec { Template = ProjectTemplate.EnglandHouse });
        var us = ProjectTemplates.Create(new NewProjectSpec { Template = ProjectTemplate.UsHouse });
        var type = england.TypesOf<ComponentType>().Single(each => each.Name == Store);
        Assert.Equal((1700.0, 1100.0, ComponentForm.WheelchairStorage), (type.Width, type.Depth, type.Form));
        Assert.True(ProjectTemplates.Fits(england, type));
        Assert.False(ProjectTemplates.Fits(us, us.TypesOf<ComponentType>().Single(each => each.Name == Store)));

        // None on the entrance storey: said of the front door (3.25a).
        var (document, front, ground) = House();
        var said = Assert.Single(Visitable.Problems(document));
        Assert.Same(front, said.Element);
        Assert.Contains("M4(3) 3.25a", said.Said);

        // Put down: none said - and under M4(2), none asked at all.
        Put(document, ground);
        Assert.Empty(Visitable.Problems(document));
        document.All<Component>().ToList().ForEach(piece => document.Remove(piece));
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.ApprovedDocumentM;
        Assert.DoesNotContain(Visitable.Problems(document), problem => problem.Said.Contains("3.25"));
    }

    [Fact]
    public void ItHasASocketInItAndASpace1200ClearInFrontOfIt()
    {
        var (document, _, ground) = House();
        var store = Put(document, ground);

        // No socket in it (3.25c); one on the wall behind it, within its width: met.
        bool Uncharged() => Services.Problems(document, store, ClearanceStandard.ApprovedDocumentM3).Any(problem => problem.Rule.Contains("3.25c"));
        Assert.True(Uncharged());
        var socket = document.TypesOf<ComponentType>().First(each => each.Name == "Socket - Double");
        document.Add(new Component { TypeId = socket.Id, TypeKind = socket.Kind, LevelId = ground.Id, Location = new Point2D(165 + 20, 3900), Rotation = 270, Elevation = 600 });
        Assert.False(Uncharged());

        // Its two zones: itself, and 1200 in front of it across its width - clear in a hall 2.7 m wide (3.25a, b).
        var zones = Clearances.Of(document, store, ClearanceStandard.ApprovedDocumentM3).ToList();
        Assert.Equal(2, zones.Count);
        Assert.All(zones, zone => Assert.True(zone.In.Count == 0, $"{zone.Zone.What}: {string.Join(", ", zone.In)}"));

        // A chest of drawers standing in front of it: the space it opens off is blocked.
        var chest = document.TypesOf<ComponentType>().First(each => each.Name == "Chest of Drawers - 750");
        document.Add(new Component { TypeId = chest.Id, TypeKind = chest.Kind, LevelId = ground.Id, Location = new Point2D(165 + 1100 + 400, 3500), Rotation = 270 });
        Assert.Contains(Clearances.Of(document, store, ClearanceStandard.ApprovedDocumentM3), zone => zone.In.Count > 0 && zone.Zone.Rule.Contains("3.25b"));
    }
}
