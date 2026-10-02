using System.Windows;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;
using BIMDesigner.UI;
using BIMDesigner.UI.Controls;

namespace BIMDesigner.Tests;

/// <summary>
/// A chimney stands in the room, never in a wall: put down on a wall, by one, or in a corner, it
/// goes against the wall's face with its fireplace to the room; dragged or nudged into a wall,
/// it stays out of it.
/// </summary>
public class ChimneyWallTests
{
    private static void OnUiThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (Application.Current is null) new App().InitializeComponent();
                action();
            }
            catch (Exception exception) { failure = exception; }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Exception("The plan failed on the UI thread.", failure);
    }

    /// <summary>A 6 m by 4 m house, and its plan with the Chimney tool picked up.</summary>
    private static (BimDocument Document, PlanView Plan, double Half) House()
    {
        var (document, _, wallType) = DormerToolTests.House(6000, 4000, 40);
        var plan = new PlanView { Document = document, ActiveLevelId = document.Levels[0].Id, History = new UndoStack() };
        plan.SetTool(PlanTool.Chimney);
        return (document, plan, wallType.Structure.TotalWidth / 2);
    }

    private static Chimney PutAt(BimDocument document, PlanView plan, double x, double y)
    {
        Assert.True(plan.PlaceChimneyAt(new Point2D(x, y)));
        return document.Elements.OfType<Chimney>().Last();
    }

    [Theory]
    [InlineData(4800, 3500)]
    [InlineData(4800, 3900)]
    [InlineData(4800, 4100)]
    [InlineData(2000, 3700)]
    public void PutDownOnOrByAWallItGoesAgainstItsFaceInTheRoom(double x, double y)
    {
        OnUiThread(() =>
        {
            var (document, plan, half) = House();
            var chimney = PutAt(document, plan, x, y);

            Assert.Empty(Chimneys.WallsItIsIn(document, chimney));
            Assert.Equal(-1, Chimneys.Front(chimney).Y, 6);

            // Its back - the breast's - is on the top wall's inside face.
            var footprint = Chimneys.Footprint(document, chimney, chimney.LevelId);
            Assert.Equal(4000 - half, footprint.Max(point => point.Y), 6);
            Assert.InRange(footprint.Min(point => point.X), half, 6000 - half);
            Assert.InRange(footprint.Max(point => point.X), half, 6000 - half);
        });
    }

    [Theory]
    [InlineData(5700, 3600)]
    [InlineData(5400, 3650)]
    [InlineData(300, 300)]
    public void InACornerItBacksOntoOneWallAndIsClearOfTheOther(double x, double y)
    {
        OnUiThread(() =>
        {
            var (document, plan, half) = House();
            var chimney = PutAt(document, plan, x, y);

            Assert.Empty(Chimneys.WallsItIsIn(document, chimney));
            var footprint = Chimneys.Footprint(document, chimney, chimney.LevelId);
            var (minX, maxX) = (footprint.Min(point => point.X), footprint.Max(point => point.X));
            var (minY, maxY) = (footprint.Min(point => point.Y), footprint.Max(point => point.Y));

            // Snug in the corner: against both faces, inside the room.
            Assert.True(minX >= half - 1e-6 && maxX <= 6000 - half + 1e-6 && minY >= half - 1e-6 && maxY <= 4000 - half + 1e-6);
            var nearX = x > 3000 ? 6000 - half - maxX : minX - half;
            var nearY = y > 2000 ? 4000 - half - maxY : minY - half;
            Assert.Equal(0, nearX, 6);
            Assert.Equal(0, nearY, 6);
        });
    }

    [Fact]
    public void ItKeepsOutOfTheWallWhateverLineTheWallIsDrawnOn()
    {
        OnUiThread(() =>
        {
            var (document, plan, _) = House();
            foreach (var wall in document.Walls) wall.LocationLine = WallLocationLine.FinishFaceExterior;

            var chimney = PutAt(document, plan, 3000, 3700);
            Assert.Empty(Chimneys.WallsItIsIn(document, chimney));

            // Right against it: a couple of millimetres further back and it would be in it.
            var front = Chimneys.Front(chimney);
            chimney.Location -= front * 2;
            Assert.NotEmpty(Chimneys.WallsItIsIn(document, chimney));
        });
    }

    [Fact]
    public void WellOutInTheRoomItStaysWhereItIsPutTurnedAwayFromTheNearestWall()
    {
        OnUiThread(() =>
        {
            var (document, plan, _) = House();
            var chimney = PutAt(document, plan, 3000, 1300);

            Assert.Equal(new Point2D(3000, 1300), chimney.Location);
            Assert.Equal(1, Chimneys.Front(chimney).Y, 6);
            Assert.Empty(Chimneys.WallsItIsIn(document, chimney));
        });
    }

    [Fact]
    public void AStoveOnAMetalFlueStandsOnItsPlateAgainstTheWall()
    {
        OnUiThread(() =>
        {
            var (document, plan, half) = House();
            plan.ActiveChimneyTypeId = document.TypesOf<ChimneyType>().First(type => type.Construction == ChimneyConstruction.TwinWallSteel).Id;
            var chimney = PutAt(document, plan, 3000, 3900);

            Assert.Equal(ChimneyFireplace.Stove, chimney.Fireplace);
            Assert.Empty(Chimneys.WallsItIsIn(document, chimney));
            Assert.Equal(4000 - half - (Chimneys.StovePlate / 2 - Chimneys.StovePlateForward), chimney.Location.Y, 6);
        });
    }

    [Fact]
    public void DraggedIntoAWallItComesBackOutAndUndoTakesItBack()
    {
        var (document, _, _) = DormerToolTests.House(6000, 4000, 40);
        var chimney = new Chimney { LevelId = document.Levels[0].Id, Location = new Point2D(3000, 3500), Fireplace = ChimneyFireplace.Open };
        document.Add(chimney);
        Chimneys.FitToWalls(document, chimney);
        var fitted = chimney.Location;

        chimney.Location += new Vector2D(0, 300);
        Assert.NotEmpty(Chimneys.WallsItIsIn(document, chimney));
        var command = FitChimneysCommand.For(document, new[] { chimney });
        Assert.NotNull(command);
        Assert.Equal(fitted, chimney.Location);

        command!.Undo();
        Assert.Equal(fitted + new Vector2D(0, 300), chimney.Location);
        command.Redo();
        Assert.Equal(fitted, chimney.Location);

        // One clear of the walls is left alone.
        Assert.Null(FitChimneysCommand.For(document, new[] { chimney }));
    }

    [Theory]
    [InlineData(180)]
    [InlineData(150)]
    [InlineData(215)]
    public void TurnedToFaceTheWallItTurnsRoundToFaceTheRoom(double by)
    {
        OnUiThread(() =>
        {
            var (document, plan, half) = House();
            var chimney = PutAt(document, plan, 3000, 3500);
            var (at, angle) = (chimney.Location, chimney.Angle);

            var rotation = chimney.GetInstanceParameters(document).Single(parameter => parameter.Definition == ChimneyParameters.Rotation);
            Assert.True(rotation.TrySet(angle + by));
            Assert.NotNull(rotation.Message);

            // Facing the room again, its back still on the wall's face.
            Assert.Null(Chimneys.WallInFront(document, chimney));
            Assert.Empty(Chimneys.WallsItIsIn(document, chimney));
            Assert.True(Chimneys.Front(chimney).Y < -0.5);
            var footprint = Chimneys.Footprint(document, chimney, chimney.LevelId);
            Assert.Equal(4000 - half, footprint.Max(point => point.Y), 6);

            // One step to undo.
            rotation.TakeAppliedChange()!.Undo();
            Assert.Equal(at, chimney.Location);
            Assert.Equal(angle, chimney.Angle);
        });
    }

    [Fact]
    public void TurnedWhereItFacesNoWallItTurnsAsAsked()
    {
        OnUiThread(() =>
        {
            var (document, plan, _) = House();
            var chimney = PutAt(document, plan, 3000, 1800);

            var rotation = chimney.GetInstanceParameters(document).Single(parameter => parameter.Definition == ChimneyParameters.Rotation);
            Assert.True(rotation.TrySet(90.0));
            Assert.Null(rotation.Message);
            Assert.Equal(90, chimney.Angle, 6);
            Assert.Equal(new Point2D(3000, 1800), chimney.Location);
        });
    }

    [Fact]
    public void MovedWithItsFireplaceUpToAWallItTurnsRoundAgainstIt()
    {
        var (document, _, wallType) = DormerToolTests.House(6000, 4000, 40);
        var half = wallType.Structure.TotalWidth / 2;

        // Out in the room, its fireplace toward the top wall; moved up until the hearth would hit it.
        var chimney = new Chimney { LevelId = document.Levels[0].Id, Location = new Point2D(3000, 2000), Fireplace = ChimneyFireplace.Open };
        document.Add(chimney);
        Assert.Equal(1, Chimneys.Front(chimney).Y, 6);
        Assert.Null(Chimneys.WallInFront(document, chimney));

        chimney.Location += new Vector2D(0, 1100);
        Assert.Same(document.Walls.ElementAt(2), Chimneys.WallInFront(document, chimney));

        var command = FitChimneysCommand.For(document, new[] { chimney });
        Assert.NotNull(command);
        Assert.Equal(-1, Chimneys.Front(chimney).Y, 6);
        Assert.Equal(4000 - half, Chimneys.Footprint(document, chimney, chimney.LevelId).Max(point => point.Y), 6);
        Assert.Null(Chimneys.WallInFront(document, chimney));

        command!.Undo();
        Assert.Equal(new Point2D(3000, 3100), chimney.Location);
        Assert.Equal(1, Chimneys.Front(chimney).Y, 6);
    }

    [Fact]
    public void InAPassageTooNarrowToTurnItIsLeftAsItIs()
    {
        var (document, _, _) = DormerToolTests.House(6000, 1300, 40);
        var chimney = new Chimney { LevelId = document.Levels[0].Id, Location = new Point2D(3000, 500), Fireplace = ChimneyFireplace.Open };
        document.Add(chimney);
        Assert.NotNull(Chimneys.WallInFront(document, chimney));

        Assert.False(Chimneys.TurnFromWallInFront(document, chimney));
        Assert.Equal(new Point2D(3000, 500), chimney.Location);
        Assert.Equal(0, chimney.Angle);
    }

    [Fact]
    public void NudgedTowardAWallItStopsAtTheFace()
    {
        OnUiThread(() =>
        {
            var (document, plan, _) = House();
            var chimney = PutAt(document, plan, 3000, 3500);
            plan.SetTool(PlanTool.Select);
            plan.Select(chimney);
            var at = chimney.Location;

            Assert.False(plan.Nudge(new Vector2D(0, 1), far: false, continuing: false));
            Assert.Equal(at, chimney.Location);

            Assert.True(plan.Nudge(new Vector2D(0, -1), far: false, continuing: false));
            Assert.Equal(at.Y - 100, chimney.Location.Y, 6);
        });
    }
}
