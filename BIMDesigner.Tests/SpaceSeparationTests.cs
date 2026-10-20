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

        // Sprinklered throughout, the more of double the area or the area twice as far off (11.21): 18 m² at 1.5 m.
        Assert.Equal(18, SpaceSeparation.Allowed(1.5, sprinklered: true));
        Assert.Equal(30, SpaceSeparation.Allowed(2.5, sprinklered: true));
        Assert.Null(SpaceSeparation.Allowed(3, sprinklered: true));
        Assert.Equal(0, SpaceSeparation.Allowed(0.8, sprinklered: true));
        document.ProjectInformation.Sprinklered = true;
        Assert.Empty(SpaceSeparation.Problems(document, east, standard));
        Assert.Contains("East 1.5 m: 5.8 m² open, 18 m² allowed", SpaceSeparation.Summary(document));
    }

    [Fact]
    public void TheIrcHoldsAWallNearTheLotLineToItsRatingAndItsOpenings()
    {
        var (document, walls) = Plot();
        var (south, east, west) = (walls[0], walls[1], walls[3]);
        var half = document.GetWallType(east)!.Structure.TotalWidth / 2;
        Assert.Equal(1500, SpaceSeparation.FireSeparationDistance(document, east)!.Value, 1);

        // The south wall runs square to the side lot lines: it is measured to the one it faces.
        Assert.Equal(10000 - half, SpaceSeparation.FireSeparationDistance(document, south)!.Value, 1);

        // 4.9 ft from the line: rated two hours, and openings up to a quarter of the wall - 4.05 m² of its 16.2.
        Windows(document, east, 2);
        Assert.Empty(SpaceSeparation.Problems(document, east, ClearanceStandard.Irc));
        Windows(document, east, 1);
        var problem = Assert.Single(SpaceSeparation.Problems(document, east, ClearanceStandard.Irc));
        Assert.Contains("its openings 4.3 m² of its 16.2 m²", problem.Said);
        Assert.Contains("25% of its area", problem.Rule);

        // Not rated, it wants an hour, from both sides.
        var unrated = document.GetWallType(east)!.Duplicate("Exterior - Unrated");
        unrated.FireRating = string.Empty;
        document.AddType(unrated);
        east.TypeId = unrated.Id;
        Assert.Contains(SpaceSeparation.Problems(document, east, ClearanceStandard.Irc), found => found.Rule.Contains("rated 1 hour, tested with exposure from both sides"));

        // Sprinklered throughout, 3 ft is enough: neither the rating nor the share of openings is asked.
        document.ProjectInformation.Sprinklered = true;
        Assert.Empty(SpaceSeparation.Problems(document, east, ClearanceStandard.Irc));

        // 2.6 ft from the line: no openings at all, sprinklers or not.
        Windows(document, west, 1);
        Assert.Contains(SpaceSeparation.Problems(document, west, ClearanceStandard.Irc), found => found.Rule.Contains("no openings"));
        document.ProjectInformation.Sprinklered = false;
        Assert.Contains(SpaceSeparation.Problems(document, west, ClearanceStandard.Irc), found => found.Rule.Contains("no openings"));

        document.ProjectInformation.ClearanceStandard = ClearanceStandard.Irc;
        Assert.Contains("East 4.9 ft: 1-hour walls, openings up to 25%", SpaceSeparation.Summary(document));
        Assert.Contains("West 2.6 ft: 1-hour walls, no openings", SpaceSeparation.Summary(document));
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
