using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Parameters;

namespace BIMDesigner.Tests;

/// <summary>
/// The wall's top constraint, and changing its location line without moving it.
/// </summary>
public class WallConstraintTests
{
    private static (BimDocument Document, WallType Thick) Project()
    {
        var document = BimDocument.CreateDefault();
        return (document, document.TypesOf<WallType>().Single(t => t.Name.StartsWith("Exterior")));
    }

    private static Wall Add(BimDocument document, WallType type, Point2D start, Point2D end)
    {
        var wall = new Wall { Start = start, End = end, TypeId = type.Id, LevelId = document.Levels[0].Id };
        document.Add(wall);
        return wall;
    }

    private static ParameterValue Parameter(BimDocument document, Wall wall, string name) =>
        wall.GetInstanceParameters(document).Single(parameter => parameter.Name == name);

    /// <summary>Sets a parameter the way the property panel does, and returns its undo.</summary>
    private static ParameterChangeCommand Edit(BimDocument document, Wall wall, string name, object value)
    {
        var parameter = Parameter(document, wall, name);
        var before = parameter.Value;

        Assert.True(parameter.TrySet(value), $"{name} refused {value}.");
        return new ParameterChangeCommand(parameter, before, parameter.Value);
    }

    private static Point2D[] Outline(BimDocument document, Wall wall)
    {
        var type = document.GetWallType(wall)!;
        return WallJoins.GetBandOutline(document, wall, type, type.Width / 2, -type.Width / 2);
    }

    private static void AssertSameOutline(Point2D[] expected, Point2D[] actual, string what)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (var i = 0; i < expected.Length; i++)
            Assert.True(expected[i].DistanceTo(actual[i]) < 1e-6,
                $"{what}: corner {i} moved from {expected[i]} to {actual[i]}.");
    }

    // ---- top constraint ---------------------------------------------------------

    [Fact]
    public void TheTopCanBeUnconnectedItsOwnLevelOrAnyLevelAboveIt()
    {
        var (document, thick) = Project();
        var wall = Add(document, thick, new Point2D(0, 0), new Point2D(5000, 0));

        var top = Parameter(document, wall, "Top Constraint");

        Assert.False(top.IsReadOnly);
        Assert.Equal("Unconnected", top.Value);
        Assert.Equal(new[] { "Unconnected", "Up to level: Ground Floor", "Up to level: First Floor" }, top.AllowedValues);
    }

    [Fact]
    public void AWallUpToALevelFollowsItWhenItMoves()
    {
        var (document, thick) = Project();
        var wall = Add(document, thick, new Point2D(0, 0), new Point2D(5000, 0));
        var first = document.Levels[1];

        Edit(document, wall, "Top Constraint", "Up to level: First Floor");
        Assert.Equal(first.Id, wall.TopLevelId);
        Assert.Equal(3000, wall.GetHeight(document), precision: 6);

        Edit(document, wall, "Top Offset", -200d);
        Assert.Equal(2800, wall.GetHeight(document), precision: 6);

        new MoveLevelCommand(document, first, 3500).Redo();
        Assert.Equal(3300, wall.GetHeight(document), precision: 6);
    }

    [Fact]
    public void LettingGoOfTheTopLevelKeepsTheHeightTheWallHasNow()
    {
        var (document, thick) = Project();
        var wall = Add(document, thick, new Point2D(0, 0), new Point2D(5000, 0));
        wall.UnconnectedHeight = 2400;

        Edit(document, wall, "Top Constraint", "Up to level: First Floor");
        Edit(document, wall, "Top Offset", 250d);
        Edit(document, wall, "Top Constraint", "Unconnected");

        Assert.Null(wall.TopLevelId);
        Assert.Equal(3250, wall.UnconnectedHeight, precision: 6);
        Assert.Equal(3250, wall.GetHeight(document), precision: 6);
    }

    [Fact]
    public void UndoingATopConstraintPutsBackTheWallExactly()
    {
        var (document, thick) = Project();
        var wall = Add(document, thick, new Point2D(0, 0), new Point2D(5000, 0));
        wall.UnconnectedHeight = 2400;

        var attach = Edit(document, wall, "Top Constraint", "Up to level: First Floor");
        var detach = Edit(document, wall, "Top Constraint", "Unconnected");

        detach.Undo();
        Assert.NotNull(wall.TopLevelId);
        Assert.Equal(2400, wall.UnconnectedHeight);

        attach.Undo();
        Assert.Null(wall.TopLevelId);
        Assert.Equal(2400, wall.UnconnectedHeight);

        attach.Redo();
        Assert.Equal(3000, wall.GetHeight(document), precision: 6);
    }

    [Fact]
    public void TheTopOffsetAndUnconnectedHeightTakeTurnsBeingEditable()
    {
        var (document, thick) = Project();
        var wall = Add(document, thick, new Point2D(0, 0), new Point2D(5000, 0));

        Assert.True(Parameter(document, wall, "Top Offset").IsReadOnly);
        Assert.False(Parameter(document, wall, "Unconnected Height").IsReadOnly);

        Edit(document, wall, "Top Constraint", "Up to level: First Floor");

        Assert.False(Parameter(document, wall, "Top Offset").IsReadOnly);
        Assert.True(Parameter(document, wall, "Unconnected Height").IsReadOnly);
    }

    [Fact]
    public void AWallCannotBeGivenATopBelowItsBase()
    {
        var (document, thick) = Project();
        var wall = Add(document, thick, new Point2D(0, 0), new Point2D(5000, 0));
        Edit(document, wall, "Top Constraint", "Up to level: First Floor");

        Assert.False(Parameter(document, wall, "Top Offset").TrySet(-3000d));
        Assert.False(Parameter(document, wall, "Base Offset").TrySet(3000d));
        Assert.False(Parameter(document, wall, "Base Constraint").TrySet("First Floor"));

        Assert.Equal(0, wall.TopOffset);
        Assert.Equal(0, wall.BaseOffset);
        Assert.Equal(document.Levels[0].Id, wall.LevelId);
    }

    // ---- location line ----------------------------------------------------------

    [Theory]
    [InlineData("Finish Face Exterior")]
    [InlineData("Finish Face Interior")]
    [InlineData("Core Face Exterior")]
    [InlineData("Core Centreline")]
    public void ChangingTheLocationLineLeavesTheWallWhereItIs(string line)
    {
        var (document, thick) = Project();
        var wall = Add(document, thick, new Point2D(0, 0), new Point2D(5000, 0));
        var before = Outline(document, wall);

        Edit(document, wall, "Location Line", line);

        AssertSameOutline(before, Outline(document, wall), line);
        Assert.NotEqual(0, wall.Start.Y);
    }

    [Fact]
    public void TheDrawnLineMovesToTheChosenFace()
    {
        var (document, thick) = Project();
        var wall = Add(document, thick, new Point2D(0, 0), new Point2D(5000, 0));

        Edit(document, wall, "Location Line", "Finish Face Exterior");

        // Drawn left to right, the exterior is on the left - up the page.
        Assert.Equal(thick.Width / 2, wall.Start.Y, precision: 6);
        Assert.Equal(thick.Width / 2, wall.End.Y, precision: 6);
    }

    [Theory]
    [InlineData(90)]
    [InlineData(60)]
    [InlineData(135)]
    public void ACornerStaysJoinedWhenEitherWallChangesItsLine(int degrees)
    {
        foreach (var changeFirst in new[] { true, false })
        {
            var (document, thick) = Project();
            var radians = degrees * Math.PI / 180;

            var a = Add(document, thick, new Point2D(0, 0), new Point2D(5000, 0));
            var b = Add(document, thick, new Point2D(5000, 0),
                new Point2D(5000 + Math.Cos(radians) * 4000, Math.Sin(radians) * 4000));

            var beforeA = Outline(document, a);
            var beforeB = Outline(document, b);

            Edit(document, changeFirst ? a : b, "Location Line", "Finish Face Interior");

            AssertSameOutline(beforeA, Outline(document, a), $"{degrees}deg, first wall");
            AssertSameOutline(beforeB, Outline(document, b), $"{degrees}deg, second wall");
            Assert.True(a.End.DistanceTo(b.Start) < 1e-6, "The corner came apart.");
        }
    }

    [Fact]
    public void AStemTeeingInFollowsTheWallItRunsInto()
    {
        var (document, thick) = Project();
        var run = Add(document, thick, new Point2D(0, 0), new Point2D(10000, 0));
        var stem = Add(document, thick, new Point2D(5000, 0), new Point2D(5000, 4000));

        var beforeRun = Outline(document, run);
        var beforeStem = Outline(document, stem);

        Edit(document, run, "Location Line", "Finish Face Exterior");

        AssertSameOutline(beforeRun, Outline(document, run), "run");
        AssertSameOutline(beforeStem, Outline(document, stem), "stem");
        Assert.Equal(run.Start.Y, stem.Start.Y, precision: 6);
    }

    [Fact]
    public void AStemChangingItsLineStaysOnTheWallItRunsInto()
    {
        var (document, thick) = Project();
        var run = Add(document, thick, new Point2D(0, 0), new Point2D(10000, 0));
        var stem = Add(document, thick, new Point2D(5000, 0), new Point2D(7000, 4000));

        var beforeRun = Outline(document, run);
        var beforeStem = Outline(document, stem);

        Edit(document, stem, "Location Line", "Finish Face Exterior");

        AssertSameOutline(beforeRun, Outline(document, run), "run");
        AssertSameOutline(beforeStem, Outline(document, stem), "stem");
        Assert.Equal(0, stem.Start.Y, precision: 6);
    }

    [Fact]
    public void DoorsStayWhereTheyAre()
    {
        var (document, thick) = Project();
        var a = Add(document, thick, new Point2D(0, 0), new Point2D(5000, 0));
        var b = Add(document, thick, new Point2D(0, 0), new Point2D(-3000, 3000));

        var door = new Door
        {
            TypeId = document.TypesOf<DoorType>().First().Id,
            LevelId = a.LevelId,
            HostWallId = a.Id,
            DistanceAlongWall = 2000
        };
        document.Add(door);

        var before = door.GetCentre(a) - a.ExteriorNormal * a.GetLocationLineOffset(thick.Structure);

        Edit(document, a, "Location Line", "Finish Face Exterior");

        var after = door.GetCentre(a) - a.ExteriorNormal * a.GetLocationLineOffset(thick.Structure);
        Assert.True(before.DistanceTo(after) < 1e-6, $"The door moved from {before} to {after}.");
        _ = b;
    }

    [Fact]
    public void UndoPutsEveryWallBackExactly()
    {
        var (document, thick) = Project();
        var a = Add(document, thick, new Point2D(0, 0), new Point2D(5000, 0));
        var b = Add(document, thick, new Point2D(5000, 0), new Point2D(7123.4, 3456.7));
        var stem = Add(document, thick, new Point2D(2500, 0), new Point2D(1111.1, -4000));

        var positions = document.Walls.Select(w => (w.Start, w.End)).ToList();

        var change = Edit(document, a, "Location Line", "Core Face Interior");
        Assert.NotEqual(positions, document.Walls.Select(w => (w.Start, w.End)).ToList());

        change.Undo();
        Assert.Equal(positions, document.Walls.Select(w => (w.Start, w.End)).ToList());
        Assert.Equal(WallLocationLine.WallCentreline, a.LocationLine);

        change.Redo();
        Assert.Equal(WallLocationLine.CoreFaceInterior, a.LocationLine);
        _ = (b, stem);
    }
}
