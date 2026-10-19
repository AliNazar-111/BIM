using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Tests;

/// <summary>
/// A house's sides against the property line, as Approved Document B has it: how much of each
/// may be windows for how far it stands from the boundary, and a wall within a metre of it.
/// </summary>
public class SpaceSeparationTests
{
    /// <summary>A house 8 m by 6 m of outside walls on a plot 1.5 m clear of its east side and 0.8 m clear of its west side.</summary>
    private static (BimDocument Document, List<Wall> Walls) Plot()
    {
        var document = BimDocument.CreateDefault();
        var level = document.Levels[0].Id;
        var type = document.TypesOf<WallType>().First(candidate => candidate.Function == WallFunction.Exterior && candidate.Log is null);
        var half = type.Structure.TotalWidth / 2;
        var corners = new[] { new Point2D(0, 0), new Point2D(8000, 0), new Point2D(8000, 6000), new Point2D(0, 6000) };
        var walls = new List<Wall>();
        for (var i = 0; i < 4; i++)
        {
            var wall = new Wall { Start = corners[i], End = corners[(i + 1) % 4], TypeId = type.Id, LevelId = level, UnconnectedHeight = 2700 };
            document.Add(wall);
            walls.Add(wall);
        }

        var line = new PropertyLine { LevelId = level };
        line.SetBoundary(new[] { new Point2D(-half - 800, -10000), new Point2D(8000 + half + 1500, -10000), new Point2D(8000 + half + 1500, 16000), new Point2D(-half - 800, 16000) });
        document.Add(line);
        return (document, walls);
    }

    private static void Windows(BimDocument document, Wall wall, int count)
    {
        var type = document.TypesOf<WindowType>().Single(candidate => candidate.Name == "Casement - 1200 x 1200");
        for (var i = 0; i < count; i++)
            document.Add(new Window { TypeId = type.Id, LevelId = wall.LevelId, HostWallId = wall.Id, DistanceAlongWall = 800 + i * 1400, SillHeight = 900, Mark = $"W{i}" });
    }

    [Fact]
    public void DiagramElevenSevenAllowsMoreWindowsTheFurtherASideIsFromTheBoundary()
    {
        Assert.Equal(5.6, SpaceSeparation.Allowed(1.5));
        Assert.Equal(18, SpaceSeparation.Allowed(3.2));
        Assert.Null(SpaceSeparation.Allowed(6));

        var (document, walls) = Plot();
        var east = walls[1];
        var standard = document.ProjectInformation.ClearanceStandard;
        Assert.Equal(1500, SpaceSeparation.DistanceToBoundary(document, east)!.Value, 1);

        // Three windows of 1.44 m² - 4.3 m² - is within the 5.6 m² allowed 1.5 m from the boundary; four, 5.8 m², is not.
        Windows(document, east, 3);
        Assert.Empty(SpaceSeparation.Problems(document, east, standard));
        Windows(document, east, 1);
        var problem = Assert.Single(SpaceSeparation.Problems(document, east, standard));
        Assert.Contains("facing east 1.5 m from the property line, has 5.8 m²", problem.Said);
        Assert.Contains(WallRules.Problems(document, east, standard), wallProblem => wallProblem.Rule.Contains("Diagram 11.7"));
        Assert.Contains("East 1.5 m: 5.8 m² open, 5.6 m² allowed", SpaceSeparation.Summary(document));

        // The US codes measure fire separation their own way.
        Assert.Empty(SpaceSeparation.Problems(document, east, ClearanceStandard.Irc));
    }

    [Fact]
    public void AWallWithinAMetreOfTheBoundaryHasOnlySmallOpenings()
    {
        var (document, walls) = Plot();
        var west = walls[3];
        var standard = document.ProjectInformation.ClearanceStandard;
        Assert.Equal(800, SpaceSeparation.DistanceToBoundary(document, west)!.Value, 1);

        Assert.DoesNotContain(SpaceSeparation.Problems(document, west, standard), problem => problem.Rule.Contains("Diagram 11.5"));
        Windows(document, west, 1);
        Assert.Contains(SpaceSeparation.Problems(document, west, standard), problem => problem.Rule.Contains("Diagram 11.5"));

        // A small fixed light, 0.36 m², may stay.
        document.Remove(document.Elements.OfType<Window>().Single());
        var small = document.TypesOf<WindowType>().Single(candidate => candidate.Name == "Fixed Light - 600 x 600");
        document.Add(new Window { TypeId = small.Id, LevelId = west.LevelId, HostWallId = west.Id, DistanceAlongWall = 3000, SillHeight = 1500, Mark = "W9" });
        Assert.DoesNotContain(SpaceSeparation.Problems(document, west, standard), problem => problem.Rule.Contains("Diagram 11.5"));
    }
}
