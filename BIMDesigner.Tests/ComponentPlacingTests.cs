using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Schedules;
using BIMDesigner.Infrastructure.Serialization;

namespace BIMDesigner.Tests;

/// <summary>
/// Furniture set against the walls - backing onto the nearest, facing the room, clear of a
/// corner; kitchen units closed up into a run and wall units hung over the worktop - and every
/// piece knowing the room it is in, so it can be listed room by room.
/// </summary>
public class ComponentPlacingTests
{
    /// <summary>A 10 x 8 m house; the wall along y = 0 is the one things are put against.</summary>
    private static (BimDocument Document, double Face) House()
    {
        var (document, _, wallType) = DormerToolTests.House(10000, 8000, 40);
        return (document, wallType.Structure.TotalWidth / 2);
    }

    private static Component Put(BimDocument document, string typeName, double x, double y, double rotation = 0)
    {
        var type = document.TypesOf<ComponentType>().First(t => t.Name.StartsWith(typeName));
        return new Component { TypeId = type.Id, TypeKind = type.Kind, LevelId = document.Levels[0].Id, Location = new Point2D(x, y), Rotation = rotation };
    }

    private static ComponentType TypeOf(BimDocument document, Component component) => document.FindType<ComponentType>(component.TypeId)!;

    [Fact]
    public void ABedPutDownNearAWallBacksOntoItAndFacesTheRoom()
    {
        var (document, face) = House();
        var bed = Put(document, "Bed - Double", 5000, 1200, rotation: 90);
        Assert.True(ComponentPlacing.FitToWalls(document, bed));

        var type = TypeOf(document, bed);
        Assert.Equal(1, bed.Facing.Y, 6);
        Assert.Equal(face + type.Depth / 2, bed.Location.Y, 6);
        Assert.Equal(5000, bed.Location.X, 6);
    }

    [Fact]
    public void ABedOutInTheRoomAndATableNearAWallStayWhereTheyArePut()
    {
        var (document, _) = House();
        var bed = Put(document, "Bed - Double", 5000, 4000, rotation: 30);
        Assert.False(ComponentPlacing.FitToWalls(document, bed));
        Assert.Equal(30, bed.Rotation, 6);

        var table = Put(document, "Table - Dining", 5000, 700);
        Assert.False(ComponentPlacing.FitToWalls(document, table));
        Assert.Equal(new Point2D(5000, 700), table.Location);
    }

    [Fact]
    public void AWcPutDownInACornerIsSlidClearOfTheSideWall()
    {
        var (document, face) = House();
        var wc = Put(document, "WC", 150, 400);
        Assert.True(ComponentPlacing.FitToWalls(document, wc));

        // Its back on the wall along y = 0, its side clear of the one along x = 0.
        var type = TypeOf(document, wc);
        var footprint = Core.Views.ComponentModel.Footprint(type, Core.Views.ComponentModel.FrameOf(wc, 0));
        Assert.True(footprint.Min(point => point.X) >= face - 1e-6, "It stands in the side wall.");
        Assert.True(footprint.Min(point => point.Y) >= face - 1e-6, "It stands in the wall behind it.");
    }

    [Fact]
    public void KitchenUnitsCloseUpIntoARunAndAWallUnitHangsOverTheWorktop()
    {
        var (document, face) = House();
        var first = Put(document, "Base Unit - 600", 2000, 500);
        ComponentPlacing.FitToWalls(document, first);
        document.Add(first);

        // Put down a little way off: closed up to the first, side to side.
        var second = Put(document, "Base Unit - 600", 2750, 500);
        ComponentPlacing.FitToWalls(document, second);
        document.Add(second);
        Assert.Equal(600, Math.Abs(second.Location.X - first.Location.X), 6);
        Assert.Equal(first.Location.Y, second.Location.Y, 6);

        // A wall unit on the wall over them, its underside 500 above the worktop.
        var wall = document.Walls.First(w => Math.Abs(w.Start.Y) < 1 && Math.Abs(w.End.Y) < 1);
        var unit = Put(document, "Wall Unit - 600", 2000, 100);
        Assert.True(unit.HostOn(document, wall, new Point2D(2000, 100)));
        var unitType = TypeOf(document, unit);
        unit.Elevation = ComponentPlacing.OverWorktop(document, unit, unitType)!.Value;

        var worktop = ComponentHosting.BaseElevation(document, first, TypeOf(document, first)) + TypeOf(document, first).Height;
        Assert.Equal(worktop + ComponentPlacing.AboveWorktop, ComponentHosting.BaseElevation(document, unit, unitType), 6);
    }

    [Fact]
    public void EachPieceKnowsItsRoomAndTheFurnitureIsListedRoomByRoom()
    {
        var (document, _) = House();
        var level = document.Levels[0].Id;
        document.Add(new Wall { Start = new Point2D(5000, 0), End = new Point2D(5000, 8000), TypeId = document.TypesOf<WallType>().First().Id, LevelId = level, UnconnectedHeight = 3000 });
        document.Add(new Room { LevelId = level, Location = new Point2D(2500, 4000), Name = "Kitchen", Number = "1" });
        document.Add(new Room { LevelId = level, Location = new Point2D(7500, 4000), Name = "Bedroom", Number = "2" });

        var chair = Put(document, "Chair - Dining", 2500, 3000);
        var bed = Put(document, "Bed - Double", 7500, 4000);
        document.Add(chair);
        document.Add(bed);

        string RoomOf(Component piece) => (string)piece.GetInstanceParameters(document).Single(p => p.Name == "Room").Value!;
        Assert.Equal("1 Kitchen", RoomOf(chair));
        Assert.Equal("2 Bedroom", RoomOf(bed));

        var furniture = ScheduleDefinition.Defaults().Single(definition => definition.Name == "Furniture Schedule");
        Assert.Equal("Room", furniture.GroupBy);
        var result = Schedule.Run(document, furniture);
        Assert.Equal(new[] { "1 Kitchen", "2 Bedroom" }, result.Rows.Select(row => row.Cells["Room"].DisplayValue).ToArray());
    }

    [Fact]
    public void WhetherATypeBacksOntoAWallIsSavedAndAnOldFileGoesByItsShape()
    {
        var document = BimDocument.CreateDefault();
        Assert.True(document.TypesOf<ComponentType>().Single(t => t.Name == "Wardrobe - 1000 x 600").AgainstWall);
        Assert.False(document.TypesOf<ComponentType>().First(t => t.Form == ComponentForm.Table).AgainstWall);

        var island = document.TypesOf<ComponentType>().First(t => t.Name.StartsWith("Counter"));
        island.AgainstWall = false;
        var loaded = ProjectFile.FromJson(ProjectFile.ToJson(document));
        Assert.False(loaded.TypesOf<ComponentType>().Single(t => t.Id == island.Id).AgainstWall);

        // A file from before has nothing to say: a counter backs onto a wall, a table does not.
        var old = ProjectFile.FromJson(ProjectFile.ToJson(document).Replace("\"againstWall\": false,", string.Empty).Replace("\"againstWall\": true,", string.Empty));
        Assert.True(old.TypesOf<ComponentType>().Single(t => t.Id == island.Id).AgainstWall);
    }
}
