using BIMDesigner.Core;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Tests;

public class WallTests
{
    private static (BimDocument Document, Wall Wall, WallType Type) Scenario(string typeName = "Generic - 200mm")
    {
        var document = BimDocument.CreateDefault();
        var type = document.TypesOf<WallType>().Single(t => t.Name == typeName);
        var level = document.Levels.First();

        var wall = new Wall
        {
            Start = new Point2D(0, 0),
            End = new Point2D(5250, 0),
            TypeId = type.Id,
            LevelId = level.Id
        };

        document.Add(wall);
        return (document, wall, type);
    }

    [Fact]
    public void Length_IsDerivedFromEndPoints()
    {
        var (_, wall, _) = Scenario();

        Assert.Equal(5250, wall.Length, precision: 6);
    }

    [Fact]
    public void Length_HandlesDiagonalWalls()
    {
        var (_, wall, _) = Scenario();
        wall.End = new Point2D(3000, 4000);

        Assert.Equal(5000, wall.Length, precision: 6);
    }

    [Fact]
    public void Volume_UsesTheTypeWidth_NotAStoredThickness()
    {
        var (document, wall, type) = Scenario();
        wall.End = new Point2D(5000, 0);
        wall.UnconnectedHeight = 3000;

        Assert.Equal(5000d * 3000 * type.Width, wall.GetVolume(document), precision: 6);
    }

    [Fact]
    public void Volume_FollowsTheTypeWhenTheAssemblyChanges()
    {
        var (document, wall, _) = Scenario();
        var before = wall.GetVolume(document);

        // Re-typing the wall must change its quantities, because quantities are never stored.
        wall.TypeId = document.TypesOf<WallType>().Single(t => t.Name.Contains("Partition")).Id;

        Assert.NotEqual(before, wall.GetVolume(document));
    }

    [Fact]
    public void Height_FollowsTheTopLevelWhenConstrained()
    {
        var (document, wall, _) = Scenario();
        var first = document.Levels.Single(l => l.Name == "First Floor");
        wall.TopLevelId = first.Id;

        Assert.Equal(3000, wall.GetHeight(document), precision: 6);

        // Raising the level must raise every wall constrained to it.
        first.Elevation = 3600;
        Assert.Equal(3600, wall.GetHeight(document), precision: 6);
    }

    [Fact]
    public void Height_FallsBackToUnconnectedHeightWithoutATopLevel()
    {
        var (document, wall, _) = Scenario();
        wall.UnconnectedHeight = 2700;

        Assert.Equal(2700, wall.GetHeight(document), precision: 6);
    }

    [Fact]
    public void UnconnectedHeight_RejectsNonPositiveValues()
    {
        var (_, wall, _) = Scenario();

        Assert.Throws<ArgumentOutOfRangeException>(() => wall.UnconnectedHeight = 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => wall.UnconnectedHeight = -100);
    }

    [Fact]
    public void Area_IsLengthTimesHeight()
    {
        var (document, wall, _) = Scenario();
        wall.UnconnectedHeight = 3000;

        Assert.Equal(5250d * 3000, wall.GetArea(document), precision: 6);
    }
}
