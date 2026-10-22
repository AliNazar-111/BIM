using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Sheets;
using BIMDesigner.Core.Views;
using BIMDesigner.UI;
using BIMDesigner.UI.Controls;

namespace BIMDesigner.Tests;

/// <summary>
/// What a person meets in their first hour with the app, found by using it as they would: the
/// 3D view showing the whole building, the first view on a sheet in the middle of it, a room drawn
/// round staying together when a wall of it is moved, taking hold of a wall moving it rather than
/// bending it, a new project not showing the last one's section, the version in the title, and a
/// sloping roof starting out with a pitched roof type.
/// </summary>
[Collection("Wpf")]
public class FirstHourTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

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
        if (failure is not null) throw new Exception("Failed on the UI thread.", failure);
    }

    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, Hidden)!.GetValue(owner)!;

    /// <summary>A pointer at a point of the model: moved there, pressed and let go, or dragged on from there.</summary>
    private static void Press(PlanView plan, Point2D at, Point2D? dragTo = null)
    {
        void Move(Point2D model)
        {
            plan.PointerAt = plan.ScreenPointOf(model);
            plan.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseMoveEvent });
        }

        Move(at);
        plan.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.MouseDownEvent });
        if (dragTo is { } to)
            for (var i = 1; i <= 4; i++) Move(at + (to - at) * (i / 4.0));
        plan.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.MouseUpEvent });
    }

    private static PlanView Plan(BimDocument document, UndoStack history)
    {
        var plan = new PlanView { Width = 1000, Height = 800, Document = document, History = history };
        plan.Measure(new Size(1000, 800));
        plan.Arrange(new Rect(0, 0, 1000, 800));
        plan.ActiveLevelId = document.Levels[0].Id;
        return plan;
    }

    [Fact]
    public void TheWholeBuildingFitsAThreeDViewWiderThanItIsTall()
    {
        OnUiThread(() =>
        {
            var document = ConstructionTests.House();
            var view = new ModelView { Width = 900, Height = 300, Document = document };
            view.Measure(new Size(900, 300));
            view.Arrange(new Rect(0, 0, 900, 300));
            view.ZoomToFit();

            // Far enough back that the sphere round it fits the view's height - the narrower way, the field of view being across it.
            var (min, max) = ModelMeshBuilder.Bounds(Field<IReadOnlyList<Mesh3D>>(view, "_allMeshes"))!.Value;
            var radius = Math.Sqrt(Math.Pow(max.X - min.X, 2) + Math.Pow(max.Y - min.Y, 2) + Math.Pow(max.Z - min.Z, 2)) / 2 / 1000;
            var upAndDown = Math.Atan(Math.Tan(45.0 / 2 * Math.PI / 180) / 3);
            Assert.True(Field<double>(view, "_distance") >= radius / Math.Sin(upAndDown));
        });
    }

    [Fact]
    public void TheFirstViewOnASheetGoesInTheMiddleOfItAndTheNextBesideIt()
    {
        var document = BimDocument.CreateDefault();
        var sheet = new Sheet { Number = "A-101" };
        var area = sheet.DrawingArea;
        var first = sheet.NextFreePosition(document, 200, 150);
        Assert.Equal(area.Centre.X, first.X, 6);
        Assert.Equal(area.Centre.Y + Viewport.TitleBand / 2, first.Y, 6);

        sheet.Add(new Viewport { View = ViewReference.FloorPlan(document.Levels[0].Id), Scale = ViewScale.OneToOneHundred, Centre = first });
        Assert.NotEqual(first, sheet.NextFreePosition(document, 200, 150));
    }

    [Fact]
    public void ARoomDrawnRoundStaysTogetherWhenAWallOfItIsMoved()
    {
        OnUiThread(() =>
        {
            var document = BimDocument.CreateDefault();
            var history = new UndoStack();
            var plan = Plan(document, history);
            plan.SetTool(PlanTool.Wall);
            foreach (var corner in new[] { new Point2D(0, 0), new Point2D(6000, 0), new Point2D(6000, 4000), new Point2D(0, 4000), new Point2D(0, 0) })
                Press(plan, corner);
            plan.CancelPendingOperation();
            plan.SetTool(PlanTool.Select);

            var walls = document.Walls.ToList();
            Assert.Equal(4, walls.Count);

            // Drawn round anticlockwise their outsides would face in: they were turned to face out as the room closed.
            var loop = walls.Select(wall => wall.Start).ToList();
            Assert.All(walls, wall => Assert.False(Polygon2D.Contains(loop,
                wall.PointAt(document.GetWallType(wall)!.Structure, wall.Length / 2, document.GetWallType(wall)!.Width / 2 + 1))));
            Assert.Equal("Flip 4 Walls", history.UndoName);
            Assert.All(walls, wall => Assert.True(WallJointLock.IsLocked(document, wall.LevelId, wall.Start)));

            // The front wall taken by its middle and pulled out a metre: moved, not bent, the sides stretching after it.
            var front = walls[0];
            Press(plan, new Point2D(3000, 0));
            Assert.Equal(new[] { front }, plan.SelectedElements);
            Press(plan, new Point2D(3000, 0), new Point2D(3000, -1000));
            Assert.Equal(0, front.Bulge);
            Assert.Equal(-1000, front.Start.Y, 3);
            Assert.Equal(-1000, walls[1].Start.Y, 3);
            Assert.Equal(-1000, walls[3].End.Y, 3);

            // Its bend grip is off it, beyond its inside face: taken there, it curves the wall.
            var type = document.GetWallType(front)!;
            var grip = front.PointAt(type.Structure, front.Length / 2, -(type.Width / 2 + 16 / plan.PixelsPerMm));
            Press(plan, grip, grip + new Vector2D(0, 600));
            Assert.NotEqual(0, front.Bulge);
            history.Undo();
            Assert.Equal(0, front.Bulge);
        });
    }

    [Fact]
    public void OpeningAProjectShowsItsVersionAndNoneOfTheLastOnesSectionsOrSheets()
    {
        OnUiThread(() =>
        {
            var window = new MainWindow();
            var load = typeof(MainWindow).GetMethod("LoadDocument", Hidden)!;
            load.Invoke(window, new object?[] { ConstructionTests.House(), null, false });
            var section = Field<FrameworkElement>(window, "SectionPanel");
            var sheet = Field<FrameworkElement>(window, "SheetPanel");
            section.Visibility = sheet.Visibility = Visibility.Visible;

            load.Invoke(window, new object?[] { BimDocument.CreateDefault(), null, false });
            Assert.Equal(Visibility.Collapsed, section.Visibility);
            Assert.Equal(Visibility.Collapsed, sheet.Visibility);
            var version = typeof(MainWindow).Assembly.GetName().Version!;
            Assert.EndsWith($"BIMDesigner {version.Major}.{version.Minor}.{version.Build}", window.Title);
        });
    }

    [Fact]
    public void TheRoofToolSlopesItsSidesWithAPitchedRoofType()
    {
        OnUiThread(() =>
        {
            var window = new MainWindow();
            var document = BimDocument.CreateDefault();
            typeof(MainWindow).GetMethod("LoadDocument", Hidden)!.Invoke(window, new object?[] { document, null, false });
            var plan = Field<PlanView>(window, "Plan");

            Assert.True(plan.SketchDefinesSlope);
            var type = document.FindType<RoofType>(plan.ActiveRoofTypeId)!;
            Assert.DoesNotContain("Flat", type.Name);
        });
    }

    [Fact]
    public void AnElevationOffersNoWalkAndTheThreeDViewAfterItLooksFromTheCorner()
    {
        OnUiThread(() =>
        {
            var window = new MainWindow();
            typeof(MainWindow).GetMethod("LoadDocument", Hidden)!.Invoke(window, new object?[] { ConstructionTests.House(), null, false });
            var walkAs = Field<ComboBox>(window, "WalkAsPicker");
            var model = Field<ModelView>(window, "Model3D");

            typeof(MainWindow).GetMethod("ShowElevation", Hidden)!.Invoke(window, new object[] { ElevationSide.South });
            Assert.Equal(Visibility.Collapsed, walkAs.Visibility);
            Assert.Equal(0, model.Pitch, 6);

            typeof(MainWindow).GetMethod("Show3D", Hidden)!.Invoke(window, new object?[] { null });
            Assert.Equal(Visibility.Visible, walkAs.Visibility);
            Assert.Equal((-135.0, 28.0), (model.Yaw, model.Pitch));
        });
    }
}
