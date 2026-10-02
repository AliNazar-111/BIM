using System.Windows;
using BIMDesigner.Core.Annotation;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Infrastructure.Serialization;
using BIMDesigner.UI;
using BIMDesigner.UI.Controls;

namespace BIMDesigner.Tests;

/// <summary>
/// Rotate turns elements about a point, keeping what they carry and how they meet; Scale makes
/// their setting out bigger or smaller, keeping thicknesses and sizes; Pin holds them where they are.
/// </summary>
[Collection("Wpf")]
public class RotateScalePinTests
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

    private static void Near(Point2D expected, Point2D actual) =>
        Assert.True(expected.DistanceTo(actual) < 1e-6, $"Expected {expected}, was {actual}.");

    [Fact]
    public void ARoomOfWallsTurnsAboutAPointStillMeetingAtItsCorners()
    {
        var (document, roof, _) = DormerToolTests.House(6000, 4000, 40);
        var walls = document.Walls.ToList();
        var lengths = walls.Select(wall => wall.Length).ToList();

        var command = new RotateElementsCommand(walls.Cast<Core.Elements.Element>().Append(roof), new Point2D(3000, 2000), 90, document);
        command.Redo();

        // Turned a quarter: the corner at the origin goes to (5000, -1000).
        Near(new Point2D(5000, -1000), walls[0].Start);
        Assert.Equal(lengths, walls.Select(wall => wall.Length).ToList());
        for (var i = 0; i < 4; i++) Near(walls[i].End, walls[(i + 1) % 4].Start);
        Near(new Point2D(5000, -1000), roof.Boundary.OrderBy(p => p.DistanceTo(new Point2D(5000, -1000))).First());

        command.Undo();
        Near(new Point2D(0, 0), walls[0].Start);
        Near(new Point2D(6000, 0), walls[0].End);
    }

    [Fact]
    public void WhatItCarriesAndWhichWayThingsFaceTurnWithIt()
    {
        var (document, roof, _) = DormerToolTests.House(6000, 4000, 40);
        var wall = document.Walls.First();
        var door = new Core.Architecture.Door { TypeId = document.TypesOf<DoorType>().First().Id, LevelId = wall.LevelId, HostWallId = wall.Id, DistanceAlongWall = 2000 };
        document.Add(door);

        var family = document.TypesOf<ComponentType>().First(type => type.Name.StartsWith("Basin"));
        var basin = new Component { TypeId = family.Id, TypeKind = family.Kind };
        document.Add(basin);
        Assert.True(basin.HostOn(document, wall, new Point2D(4000, 300)));
        var basinRotation = basin.Rotation;

        var column = new Column { TypeId = document.TypesOf<ColumnType>().First().Id, LevelId = wall.LevelId, Location = new Point2D(1000, 1000), Rotation = 10 };
        var chimney = new Chimney { LevelId = wall.LevelId, Location = new Point2D(3000, 3000), Angle = 5 };
        document.Add(column);
        document.Add(chimney);

        var arrow = new RoofSlopeArrow { Tail = new Point2D(0, 2000), Head = new Point2D(3000, 2000) };
        roof.SetSlopeArrows(new[] { arrow });

        var centre = new Point2D(0, 0);
        new RotateElementsCommand(new Core.Elements.Element[] { wall, roof, column, chimney }, centre, 90, document).Redo();

        // The door stays the same way along its wall, which now runs up the Y axis.
        Assert.Equal(2000, door.DistanceAlongWall);
        Near(new Point2D(0, 2000), door.GetCentre(wall));

        // The basin fixed to the wall went round with it; the column and chimney turned as they moved.
        Assert.Equal(basinRotation + 90, basin.Rotation, 6);
        Assert.Equal(100, column.Rotation, 6);
        Near(new Point2D(-1000, 1000), column.Location);
        Assert.Equal(95, chimney.Angle, 6);

        // The roof's slope arrow went round with the roof.
        Near(new Point2D(-2000, 0), arrow.Tail);
        Near(new Point2D(-2000, 3000), arrow.Head);
    }

    [Fact]
    public void AMovedRoofTakesItsSlopeArrowsWithIt()
    {
        var (document, roof, _) = DormerToolTests.House(6000, 4000, 40);
        var arrow = new RoofSlopeArrow { Tail = new Point2D(0, 2000), Head = new Point2D(3000, 2000) };
        roof.SetSlopeArrows(new[] { arrow });

        new MoveElementsCommand(new[] { roof }, new Vector2D(500, 0), document: document).Redo();
        Near(new Point2D(500, 2000), arrow.Tail);
    }

    [Fact]
    public void ScaledWallsGrowLongerButNoThicker()
    {
        var (document, roof, _) = DormerToolTests.House(6000, 4000, 40);
        var walls = document.Walls.ToList();
        var types = walls.Select(wall => wall.TypeId).ToList();
        var area = Polygon2D.Area(roof.Boundary);
        var column = new Column { TypeId = document.TypesOf<ColumnType>().First().Id, LevelId = walls[0].LevelId, Location = new Point2D(1000, 1000) };
        document.Add(column);

        var command = new ScaleElementsCommand(walls.Cast<Core.Elements.Element>().Append(roof).Append(column), new Point2D(0, 0), 2, document);
        command.Redo();

        Near(new Point2D(12000, 0), walls[0].End);
        Assert.Equal(12000, walls[0].Length, 6);
        Assert.Equal(types, walls.Select(wall => wall.TypeId).ToList());
        Assert.Equal(area * 4, Polygon2D.Area(roof.Boundary), 3);
        Near(new Point2D(2000, 2000), column.Location);

        command.Undo();
        Assert.Equal(6000, walls[0].Length, 6);
    }

    [Fact]
    public void ARoofByExtrusionIsNotScaled()
    {
        var roof = new Roof();
        roof.SetExtrusion(new RoofExtrusion(new Point2D(0, 0), new Vector2D(1, 0),
            new RoofProfile(new[] { new Point2D(0, 0), new Point2D(3000, 1500), new Point2D(6000, 0) }, new double[] { 0, 0 }), 0, 8000));
        Assert.False(ElementTransforms.CanScale(roof));
        Assert.True(ElementTransforms.CanMove(roof));
    }

    [Fact]
    public void APinnedElementStaysPutUntilUnpinned()
    {
        OnUiThread(() =>
        {
            var (document, _, _) = DormerToolTests.House(6000, 4000, 40);
            var plan = new PlanView { Document = document, ActiveLevelId = document.Levels[0].Id, History = new UndoStack() };
            var wall = document.Walls.First();
            var refusals = new List<string>();
            plan.PinnedRefused += (_, why) => refusals.Add(why);

            plan.Select(wall);
            Assert.True(plan.PinSelected(true));
            Assert.True(wall.Pinned);

            // Not moved, turned, scaled or deleted - and it says why each time.
            Assert.False(plan.Nudge(new Vector2D(1, 0), far: false, continuing: false));
            Assert.False(plan.RotateBy(30));
            Assert.False(plan.ScaleAbout(new Point2D(0, 0), 2));
            plan.DeleteSelected();
            Assert.Contains(wall, document.Walls);
            Near(new Point2D(0, 0), wall.Start);
            Assert.Equal(4, refusals.Count);
            Assert.All(refusals, why => Assert.Contains("Unpin", why));

            // Turned copies leave it where it is, and are not pinned themselves.
            plan.RotateCopies = true;
            Assert.True(plan.RotateBy(90));
            var copy = document.Walls.Last();
            Assert.NotSame(wall, copy);
            Assert.False(copy.Pinned);
            Near(new Point2D(0, 0), wall.Start);

            // Unpinned, it moves.
            plan.Select(wall);
            Assert.True(plan.PinSelected(false));
            Assert.True(plan.Nudge(new Vector2D(1, 0), far: false, continuing: false));
            Near(new Point2D(100, 0), wall.Start);
        });
    }

    [Fact]
    public void PinsAreSavedAndUndone()
    {
        var (document, _, _) = DormerToolTests.House(6000, 4000, 40);
        var wall = document.Walls.First();
        var command = new PinElementsCommand(new[] { wall }, true);
        command.Redo();

        var loaded = ProjectFile.FromJson(ProjectFile.ToJson(document));
        Assert.True(loaded.Walls.Single(w => w.Id == wall.Id).Pinned);
        Assert.Equal(1, loaded.Elements.Count(element => element.Pinned));

        command.Undo();
        Assert.False(wall.Pinned);
    }

    [Fact]
    public void RotatingTheSelectionTurnsItAboutItsMiddle()
    {
        OnUiThread(() =>
        {
            var document = BimDocument.CreateDefault();
            var type = document.TypesOf<WallType>().First();
            var wall = new Wall { Start = new Point2D(0, 0), End = new Point2D(4000, 0), TypeId = type.Id, LevelId = document.Levels[0].Id, UnconnectedHeight = 3000 };
            document.Add(wall);
            var plan = new PlanView { Document = document, ActiveLevelId = document.Levels[0].Id, History = new UndoStack() };
            plan.SetTool(PlanTool.Rotate);
            plan.Select(wall);

            Assert.True(plan.RotateBy(90));
            Near(new Point2D(2000, -2000), wall.Start);
            Near(new Point2D(2000, 2000), wall.End);

            plan.History!.Undo();
            Near(new Point2D(0, 0), wall.Start);
        });
    }

    [Fact]
    public void RotateAndScaleGoBackToModifyWhenDoneOrWhenModifyIsPressed()
    {
        OnUiThread(() =>
        {
            var window = new MainWindow();
            T Field<T>(string name) => (T)typeof(MainWindow).GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)!.GetValue(window)!;
            void Modify() => typeof(MainWindow).GetMethod("OnSelectModify", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(window, new object[] { window, new RoutedEventArgs() });

            typeof(MainWindow).GetMethod("LoadDocument", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(window, new object?[] { BimDocument.CreateDefault(), null, false });
            var plan = Field<PlanView>("Plan");
            var document = plan.Document!;
            var wall = new Wall { Start = new Point2D(0, 0), End = new Point2D(4000, 0), TypeId = document.TypesOf<WallType>().First().Id, LevelId = plan.ActiveLevelId };
            document.Add(wall);
            plan.Select(wall);

            // Modify leaves Rotate: it stayed on before, the tool switch not knowing it was there.
            Field<System.Windows.Controls.RadioButton>("RotateTool").IsChecked = true;
            Assert.Equal(PlanTool.Rotate, plan.ActiveTool);
            Modify();
            Assert.Equal(PlanTool.Select, plan.ActiveTool);
            Assert.False(Field<System.Windows.Controls.RadioButton>("RotateTool").IsChecked);

            // And Rotate is done with once it has turned it - still selected, to go again.
            Field<System.Windows.Controls.RadioButton>("RotateTool").IsChecked = true;
            Assert.True(plan.RotateBy(90));
            Assert.Equal(PlanTool.Select, plan.ActiveTool);
            Assert.Contains(wall, plan.SelectedElements);

            Field<System.Windows.Controls.RadioButton>("ScaleTool").IsChecked = true;
            Assert.Equal(PlanTool.Scale, plan.ActiveTool);
            Assert.True(plan.ScaleAbout(new Point2D(0, 0), 2));
            Assert.Equal(PlanTool.Select, plan.ActiveTool);
            Assert.False(Field<System.Windows.Controls.RadioButton>("ScaleTool").IsChecked);

            // A selected wall's own tab has them all.
            foreach (var name in new[] { "ContextSplitGap", "ContextScale", "ContextPin" })
                Assert.Equal(Visibility.Visible, Field<System.Windows.Controls.Button>(name).Visibility);
            Assert.Equal(Visibility.Collapsed, Field<System.Windows.Controls.Button>("ContextUnpin").Visibility);
            window.Close();
        });
    }
}
