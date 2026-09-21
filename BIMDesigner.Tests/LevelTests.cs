using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;
using BIMDesigner.Infrastructure.Serialization;

namespace BIMDesigner.Tests;

/// <summary>
/// Tests over adding, moving and removing storeys (specification section 2.3).
///
/// A level is the datum everything else hangs from, so these are mostly about reach: what
/// else changes when a level does, and what must not be left behind when one goes.
/// </summary>
public class LevelTests
{
    private const double Tolerance = 1e-6;

    private static BimDocument Project() => BimDocument.CreateDefault();

    private static Wall AddWall(BimDocument document, Guid levelId, Guid? topLevelId = null)
    {
        var wall = new Wall
        {
            Start = new Point2D(0, 0),
            End = new Point2D(6000, 0),
            TypeId = document.TypesOf<WallType>().First().Id,
            LevelId = levelId,
            TopLevelId = topLevelId
        };

        document.Add(wall);
        return wall;
    }

    // ---- naming and placing -----------------------------------------------------

    [Fact]
    public void ANewLevelGetsAnUnusedName()
    {
        var document = Project();

        // The template already has a ground and a first floor.
        Assert.Equal("Level 3", Levels.NextName(document));

        document.AddLevel(new Level { Name = "Level 3", Elevation = 6000 });
        Assert.Equal("Level 4", Levels.NextName(document));
    }

    [Fact]
    public void ANewLevelSitsAStoreyAboveTheHighest()
    {
        var document = Project();

        // Ground at 0 and first at 3000, so the storey height is 3 m.
        Assert.Equal(6000, Levels.NextElevation(document), Tolerance);

        document.Levels[1].Elevation = 2800;
        Assert.Equal(5600, Levels.NextElevation(document), Tolerance);
    }

    [Fact]
    public void ASingleLevelProjectStillKnowsWhereTheNextStoreyGoes()
    {
        var document = new BimDocument();
        document.AddLevel(new Level { Name = "Ground", Elevation = 0 });

        Assert.Equal(Levels.DefaultStoreyHeight, Levels.NextElevation(document), Tolerance);
    }

    [Fact]
    public void LevelsAreKeptInHeightOrder()
    {
        var document = Project();
        var history = new UndoStack();

        // A basement, added last but belonging first.
        var basement = new Level { Name = "Basement", Elevation = -3000 };
        history.Execute(new AddLevelCommand(document, basement));

        Assert.Equal(
            new[] { -3000d, 0, 3000 },
            document.Levels.Select(level => level.Elevation));

        Assert.Equal("Basement", document.Levels[0].Name);
    }

    [Fact]
    public void TwoLevelsMayNotShareAName()
    {
        var document = Project();

        Assert.False(Levels.IsNameAvailable(document, "Ground Floor"));
        Assert.False(Levels.IsNameAvailable(document, "  ground floor  "));
        Assert.True(Levels.IsNameAvailable(document, "Roof"));

        // Renaming a level to what it is already called is not a clash with itself.
        Assert.True(Levels.IsNameAvailable(document, "Ground Floor", document.Levels[0]));
    }

    [Fact]
    public void AnUnnamedLevelIsRefused()
    {
        var document = Project();

        Assert.False(Levels.IsNameAvailable(document, ""));
        Assert.False(Levels.IsNameAvailable(document, "   "));
    }

    // ---- what is above and below ------------------------------------------------

    [Fact]
    public void AStoreyKnowsTheOneUnderIt()
    {
        var document = Project();
        var ground = document.Levels[0];
        var first = document.Levels[1];

        Assert.Equal(ground.Id, Levels.Below(document, first.Id)?.Id);
        Assert.Null(Levels.Below(document, ground.Id));

        Assert.Equal(first.Id, Levels.Above(document, ground.Id)?.Id);
        Assert.Null(Levels.Above(document, first.Id));
    }

    // ---- moving -----------------------------------------------------------------

    [Fact]
    public void RaisingALevelRaisesTheWallsThatReachIt()
    {
        var document = Project();
        var history = new UndoStack();

        var ground = document.Levels[0];
        var first = document.Levels[1];
        var wall = AddWall(document, ground.Id, first.Id);

        Assert.Equal(3000, wall.GetHeight(document), Tolerance);

        history.Execute(new MoveLevelCommand(document, first, 3600));

        // Nothing had to notice: the wall works its height out from the datum every time.
        Assert.Equal(3600, wall.GetHeight(document), Tolerance);

        history.Undo();
        Assert.Equal(3000, wall.GetHeight(document), Tolerance);
    }

    [Fact]
    public void AMovedLevelTakesItsPlaceInTheOrder()
    {
        var document = Project();
        var history = new UndoStack();

        var first = document.Levels[1];

        // Push the first floor below the ground floor.
        history.Execute(new MoveLevelCommand(document, first, -2000));

        Assert.Equal(first.Id, document.Levels[0].Id);
        Assert.Equal(new[] { -2000d, 0 }, document.Levels.Select(level => level.Elevation));

        history.Undo();

        Assert.Equal(new[] { 0d, 3000 }, document.Levels.Select(level => level.Elevation));
        Assert.Equal(first.Id, document.Levels[1].Id);
    }

    [Fact]
    public void MovingALevelMovesTheSlabsOnIt()
    {
        var document = Project();
        var history = new UndoStack();
        var first = document.Levels[1];

        var floor = new Floor { TypeId = document.TypesOf<FloorType>().First().Id, LevelId = first.Id };
        floor.SetBoundary(new[]
        {
            new Point2D(0, -2000), new Point2D(6000, -2000),
            new Point2D(6000, 2000), new Point2D(0, 2000)
        });
        document.Add(floor);

        var marker = new SectionMarker
        {
            Start = new Point2D(3000, -4000),
            End = new Point2D(3000, 4000),
            LevelId = document.Levels[0].Id
        };
        document.Add(marker);

        var before = SectionProjection.Build(document, marker).Pieces
            .Where(piece => piece.ElementId == floor.Id)
            .Max(piece => piece.Bounds.Top);

        Assert.Equal(3000, before, precision: 6);

        history.Execute(new MoveLevelCommand(document, first, 3600));

        var after = SectionProjection.Build(document, marker).Pieces
            .Where(piece => piece.ElementId == floor.Id)
            .Max(piece => piece.Bounds.Top);

        Assert.Equal(3600, after, precision: 6);
    }

    // ---- renaming ---------------------------------------------------------------

    [Fact]
    public void RenamingALevelRetitlesEverythingThatNamesIt()
    {
        var document = Project();
        var history = new UndoStack();
        var ground = document.Levels[0];

        history.Execute(new RenameLevelCommand(ground, "Lower Ground"));

        Assert.Equal("Lower Ground", ground.Name);

        // A wall reports its base constraint by reading the level, not by storing its name.
        var wall = AddWall(document, ground.Id);
        var constraint = wall.GetInstanceParameters(document)
            .First(parameter => parameter.Name == "Base Constraint");

        Assert.Equal("Lower Ground", constraint.DisplayValue);

        history.Undo();
        Assert.Equal("Ground Floor", ground.Name);
    }

    // ---- deleting ---------------------------------------------------------------

    [Fact]
    public void DeletingALevelTakesWhatStandsOnItAndOneUndoBringsItAllBack()
    {
        var document = Project();
        var history = new UndoStack();

        var ground = document.Levels[0];
        var first = document.Levels[1];

        var downstairs = AddWall(document, ground.Id);
        var upstairs = AddWall(document, first.Id);

        var command = new DeleteLevelCommand(document, first);
        Assert.Single(command.Hosted);

        history.Execute(command);

        Assert.Single(document.Levels);
        Assert.DoesNotContain(upstairs, document.Elements);
        Assert.Contains(downstairs, document.Elements);

        // A storey coming back in pieces, over several undos, would be unusable.
        history.Undo();

        Assert.Equal(2, document.Levels.Count);
        Assert.Contains(upstairs, document.Elements);
        Assert.Equal(first.Id, document.Levels[1].Id);
    }

    [Fact]
    public void DeletingALevelTakesTheDoorsInItsWallsToo()
    {
        var document = Project();
        var history = new UndoStack();
        var first = document.Levels[1];

        var wall = AddWall(document, first.Id);
        var door = new Door
        {
            TypeId = document.TypesOf<DoorType>().First().Id,
            LevelId = first.Id,
            HostWallId = wall.Id,
            DistanceAlongWall = 3000
        };
        document.Add(door);

        history.Execute(new DeleteLevelCommand(document, first));

        // A door whose wall has gone would be counted by every schedule that reads doors.
        Assert.Empty(document.Elements.OfType<Door>());

        history.Undo();
        Assert.Single(document.Elements.OfType<Door>());
    }

    [Fact]
    public void AWallReachingUpToADeletedLevelKeepsItsHeightInsteadOfVanishing()
    {
        var document = Project();
        var history = new UndoStack();

        var ground = document.Levels[0];
        var first = document.Levels[1];

        var wall = AddWall(document, ground.Id, first.Id);
        Assert.Equal(3000, wall.GetHeight(document), Tolerance);

        var command = new DeleteLevelCommand(document, first);
        Assert.Contains(wall, command.Released);
        Assert.DoesNotContain(wall, command.Hosted);

        history.Execute(command);

        // It stands on a level that still exists, so it is a real wall - it simply stops
        // following a datum that is no longer there.
        Assert.Contains(wall, document.Elements);
        Assert.Null(wall.TopLevelId);
        Assert.Equal(3000, wall.GetHeight(document), Tolerance);

        history.Undo();
        Assert.Equal(first.Id, wall.TopLevelId);
    }

    [Fact]
    public void ADeletedLevelComesBackWhereItWas()
    {
        var document = Project();
        var history = new UndoStack();

        document.InsertLevel(new Level { Name = "Basement", Elevation = -3000 });
        var ground = document.Levels[1];

        history.Execute(new DeleteLevelCommand(document, ground));
        Assert.Equal(new[] { -3000d, 3000 }, document.Levels.Select(level => level.Elevation));

        history.Undo();
        Assert.Equal(new[] { -3000d, 0, 3000 }, document.Levels.Select(level => level.Elevation));
    }

    // ---- persistence ------------------------------------------------------------

    [Fact]
    public void LevelsAddedAndMovedSurviveSavingAndReopening()
    {
        var document = Project();
        var history = new UndoStack();

        var second = new Level { Name = "Second Floor", Elevation = 6000 };
        history.Execute(new AddLevelCommand(document, second));
        history.Execute(new MoveLevelCommand(document, second, 6600));

        var wall = AddWall(document, document.Levels[0].Id, second.Id);

        var reloaded = ProjectFile.FromJson(ProjectFile.ToJson(document));

        Assert.Equal(3, reloaded.Levels.Count);
        Assert.Equal(new[] { 0d, 3000, 6600 }, reloaded.Levels.Select(level => level.Elevation));

        var reloadedWall = reloaded.Walls.Single(w => w.Id == wall.Id);
        Assert.Equal(6600, reloadedWall.GetHeight(reloaded), Tolerance);
    }
}
