using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Tests;

/// <summary>
/// Rooms under roofs: a room is as tall, at each point, as the lowest thing over it - a ceiling,
/// the floor above, a sloping roof - and its volume, headroom and the area that counts follow:
/// floor under 1.5 m of headroom is not counted, as surveyors measure, and the space standard
/// wants 2.3 m over three quarters of a room.
/// </summary>
public class RoomUnderRoofTests
{
    private static readonly double Pitch = Math.Tan(40 * Math.PI / 180);

    /// <summary>An attic 10 m by 8 m on the first floor, under a 40° gable roof springing from the floor, eaves along y = 0 and y = 8000.</summary>
    private static (BimDocument Document, Room Room, Roof Roof, double Half) Attic()
    {
        var document = BimDocument.CreateDefault();
        var first = document.Levels[1];
        var type = document.TypesOf<WallType>().First(t => t.Name.StartsWith("Exterior"));
        var corners = new[] { new Point2D(0, 0), new Point2D(10000, 0), new Point2D(10000, 8000), new Point2D(0, 8000) };
        for (var i = 0; i < 4; i++)
            document.Add(new Wall { Start = corners[i], End = corners[(i + 1) % 4], TypeId = type.Id, LevelId = first.Id, UnconnectedHeight = 1200 });

        var roof = new Roof { TypeId = document.TypesOf<RoofType>().First().Id, LevelId = first.Id, HeightOffset = 0 };
        roof.SetBoundary(corners);
        roof.SetEdges(new[]
        {
            new RoofEdge { DefinesSlope = true, SlopeDegrees = 40 }, new RoofEdge(),
            new RoofEdge { DefinesSlope = true, SlopeDegrees = 40 }, new RoofEdge()
        });
        document.Add(roof);

        var room = new Room { Location = new Point2D(5000, 4000), LevelId = first.Id, Name = "Attic" };
        document.Add(room);
        return (document, room, roof, type.Structure.TotalWidth / 2);
    }

    [Fact]
    public void UnderASlopingRoofItsVolumeAndHeadroomFollowTheSlope()
    {
        var (document, room, _, half) = Attic();
        var space = RoomHeadroom.Measure(document, room)!;

        Assert.True(space.UnderRoof);
        var length = 10000 - 2 * half;

        // The roof's underside rises from the eaves at the floor: the volume under it, ridge to eaves.
        var volume = Pitch * length * (4000 * 4000 - half * half);
        Assert.InRange(space.Volume, volume * 0.99, volume * 1.01);
        Assert.InRange(space.Lowest, 0, Pitch * (half + 100));
        Assert.InRange(space.Highest, Pitch * 3900, Pitch * 4000);

        // Only floor with 1.5 m of headroom over it counts: a strip down the middle.
        var counted = length * (8000 - 2 * RoomHeadroom.CountedHeadroom / Pitch);
        Assert.InRange(space.AreaCounted, counted * 0.98, counted * 1.02);
        Assert.True(space.AreaCounted < space.Area);

        // And too little of it is full height for the space standard.
        Assert.True(space.FullHeightShare < RoomHeadroom.FullHeightShare);
        Assert.StartsWith("Short", (string)room.GetInstanceParameters(document).Single(p => p.Name == "Space Standard (75% at 2.3 m)").Value!);
    }

    [Fact]
    public void ThePlanShowsWhereTheHeadroomComesDownTo1500()
    {
        var (document, room, _, half) = Attic();

        var lines = RoomHeadroom.Contour(document, room, RoomHeadroom.CountedHeadroom);
        Assert.Equal(2, lines.Count);

        var y = RoomHeadroom.CountedHeadroom / Pitch;
        Assert.Contains(lines, line => Math.Abs(line.From.Y - y) < 1 && Math.Abs(line.To.Y - y) < 1);
        Assert.Contains(lines, line => Math.Abs(line.From.Y - (8000 - y)) < 1);
        Assert.All(lines, line => Assert.Equal(10000 - 2 * half, line.From.DistanceTo(line.To), 1));
    }

    [Fact]
    public void UnderACeilingItIsAsTallAsTheCeilingWhereTheRoofIsHigher()
    {
        var (document, room, _, _) = Attic();
        var ceiling = new Ceiling { LevelId = room.LevelId, TypeId = document.TypesOf<CeilingType>().First().Id, HeightOffset = 2400 };
        ceiling.SetBoundary(new[] { new Point2D(0, 0), new Point2D(10000, 0), new Point2D(10000, 8000), new Point2D(0, 8000) });
        document.Add(ceiling);

        var space = RoomHeadroom.Measure(document, room)!;
        Assert.Equal(ceiling.GetBottomElevation(document) - 3000, space.Highest, 3);

        // Its 1.5 m line is still where the roof comes down below the ceiling.
        Assert.Equal(2, RoomHeadroom.Contour(document, room, RoomHeadroom.CountedHeadroom).Count);
    }

    [Fact]
    public void UnderTheFloorAboveItIsAsTallAsTheFloorsUnderside()
    {
        var document = BimDocument.CreateDefault();
        var (ground, first) = (document.Levels[0], document.Levels[1]);
        var type = document.TypesOf<WallType>().First(t => t.Name.StartsWith("Exterior"));
        var corners = new[] { new Point2D(0, 0), new Point2D(6000, 0), new Point2D(6000, 4000), new Point2D(0, 4000) };
        for (var i = 0; i < 4; i++)
            document.Add(new Wall { Start = corners[i], End = corners[(i + 1) % 4], TypeId = type.Id, LevelId = ground.Id, TopLevelId = first.Id });
        var floor = new Floor { LevelId = first.Id, TypeId = document.TypesOf<FloorType>().First().Id };
        floor.SetBoundary(corners);
        document.Add(floor);
        var room = new Room { Location = new Point2D(3000, 2000), LevelId = ground.Id };
        document.Add(room);

        var space = RoomHeadroom.Measure(document, room)!;
        var clear = floor.GetBottomElevation(document);
        Assert.False(space.UnderRoof);
        Assert.Equal(clear, space.Highest, 3);
        Assert.Equal(space.Area * clear, space.Volume, 0);
        Assert.Equal(space.Area * clear, (double)room.GetInstanceParameters(document).Single(p => p.Name == "Volume").Value!, 0);
        Assert.Equal(space.Area, space.AreaCounted, 0);
    }

    [Fact]
    public void AFlatRoofItStandsOnIsUnderItNotOverIt()
    {
        var document = BimDocument.CreateDefault();
        var (ground, first) = (document.Levels[0], document.Levels[1]);
        var type = document.TypesOf<WallType>().First(t => t.Name.StartsWith("Exterior"));
        var corners = new[] { new Point2D(0, 0), new Point2D(6000, 0), new Point2D(6000, 4000), new Point2D(0, 4000) };
        for (var i = 0; i < 4; i++)
            document.Add(new Wall { Start = corners[i], End = corners[(i + 1) % 4], TypeId = type.Id, LevelId = first.Id, UnconnectedHeight = 3000 });

        // A flat roof doing the first floor's job, at 3000 and up.
        var slab = new Roof { TypeId = document.TypesOf<RoofType>().First().Id, LevelId = ground.Id, HeightOffset = first.Elevation };
        slab.SetBoundary(corners);
        document.Add(slab);
        var room = new Room { Location = new Point2D(3000, 2000), LevelId = first.Id, UpperLimitOffset = 2700 };
        document.Add(room);

        var space = RoomHeadroom.Measure(document, room)!;
        Assert.False(space.Bounded);
        Assert.Equal(2700, space.Lowest, 6);
    }

    [Fact]
    public void WithNothingOverItItIsItsUpperLimitTall()
    {
        var (document, room, roof, _) = Attic();
        document.Remove(roof);
        room.UpperLimitOffset = 2600;

        var space = RoomHeadroom.Measure(document, room)!;
        Assert.False(space.Bounded);
        Assert.Equal(space.Area * 2600, space.Volume, 0);
        Assert.Empty(RoomHeadroom.Contour(document, room, RoomHeadroom.CountedHeadroom));
    }
}
