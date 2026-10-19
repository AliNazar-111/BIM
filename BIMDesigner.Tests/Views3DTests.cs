using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;
using BIMDesigner.Core.Sheets;
using BIMDesigner.Core.Views;
using BIMDesigner.Infrastructure.Serialization;

namespace BIMDesigner.Tests;

/// <summary>
/// 3D views kept by name - the {3D} view, cameras, copies - with their section boxes, styles
/// and what they show; walking through the model, and walkthroughs.
/// </summary>
public class Views3DTests
{
    /// <summary>A block 1000 long, 200 thick and 3000 high.</summary>
    private static Mesh3D Block()
    {
        var mesh = new Mesh3D(Guid.NewGuid(), Guid.Empty, MeshKind.Wall, new ColourRgb(200, 200, 200), "Block");
        mesh.AddExtrusion(new[] { new Point2D(0, 0), new Point2D(1000, 0), new Point2D(1000, 200), new Point2D(0, 200) }, 0, 3000);
        return mesh;
    }

    private static double Area(Mesh3D mesh, Func<Point3D, bool> on)
    {
        var total = 0.0;
        for (var i = 0; i + 2 < mesh.Indices.Count; i += 3)
        {
            var (a, b, c) = (mesh.Positions[mesh.Indices[i]], mesh.Positions[mesh.Indices[i + 1]], mesh.Positions[mesh.Indices[i + 2]]);
            if (!on(a) || !on(b) || !on(c)) continue;
            var (u, v) = ((b.X - a.X, b.Y - a.Y, b.Z - a.Z), (c.X - a.X, c.Y - a.Y, c.Z - a.Z));
            var cross = (u.Item2 * v.Item3 - u.Item3 * v.Item2, u.Item3 * v.Item1 - u.Item1 * v.Item3, u.Item1 * v.Item2 - u.Item2 * v.Item1);
            total += Math.Sqrt(cross.Item1 * cross.Item1 + cross.Item2 * cross.Item2 + cross.Item3 * cross.Item3) / 2;
        }

        return total;
    }

    [Fact]
    public void ASectionBoxCutsASolidAndClosesItWhereItIsCut()
    {
        var box = new Box3D(new Point3D(-100, -100, -100), new Point3D(500, 300, 4000));
        var kept = SectionBoxes.Clip(new[] { Block() }, box);

        // What is left is inside the box; its cut face, 200 by 3000, is closed at x = 500.
        Assert.Equal(2, kept.Count);
        Assert.All(kept.SelectMany(mesh => mesh.Positions), point => Assert.InRange(point.X, -1e-6, 500 + 1e-6));
        var cap = kept[1];
        Assert.Equal(200 * 3000, Area(cap, point => Math.Abs(point.X - 500) < 1e-6), 0);
        Assert.True(cap.Colour.R < 200);
        Assert.NotEmpty(cap.Edges);

        // Wholly inside, it is left as it is; wholly outside, it is gone.
        var block = Block();
        Assert.Same(block, SectionBoxes.Clip(new[] { block }, box.Expand(5000))[0]);
        Assert.Empty(SectionBoxes.Clip(new[] { Block() }, new Box3D(new Point3D(2000, 0, 0), new Point3D(3000, 100, 100))));
    }

    [Fact]
    public void ACameraSeesWhatIsAheadInTheMiddleAndOrbitsBack()
    {
        var camera = new Camera3D(new Point3D(0, 0, 1750), new Point3D(0, 10000, 1750)) { FieldOfView = 50 };
        var (x, y, depth) = camera.Project(new Point3D(0, 5000, 1750));
        Assert.Equal((0.0, 0.0, 5000.0), (Math.Round(x, 6), Math.Round(y, 6), Math.Round(depth, 6)));

        // To the right of the way it looks is to the right on the view.
        Assert.True(camera.Project(new Point3D(1000, 5000, 1750)).X > 0);
        Assert.True(camera.Project(new Point3D(0, -100, 1750)).Depth < 0);

        var (yaw, pitch) = camera.Orbit();
        var again = Camera3D.FromOrbit(camera.Target, yaw, pitch, camera.Distance, perspective: true, 50);
        Assert.Equal(camera.Eye.X, again.Eye.X, 6);
        Assert.Equal(camera.Eye.Y, again.Eye.Y, 6);
        Assert.Equal(camera.Eye.Z, again.Eye.Z, 6);
    }

    [Fact]
    public void ViewsAreKeptWithTheirCameraBoxStyleAndWhatTheyLeaveOut()
    {
        var document = BimDocument.CreateDefault();
        document.Default3D.Camera = new Camera3D(new Point3D(-20000, -20000, 15000), new Point3D(5000, 4000, 1500));
        document.Default3D.Style = VisualStyle.HiddenLine;
        document.Default3D.SectionBox = new Box3D(new Point3D(0, 0, 0), new Point3D(10000, 8000, 3000));
        document.Default3D.SectionBoxOn = true;
        document.Default3D.HiddenKinds.Add(MeshKind.Roof);

        var camera = Views3D.Camera(document, document.Levels[0].Id, new Point2D(0, -5000), new Point2D(5000, 4000));
        document.Add(camera);
        var walk = new Walkthrough { LevelId = document.Levels[0].Id, Name = "Round the back", Frames = 150 };
        walk.SetPath(new[] { new Point2D(0, 0), new Point2D(10000, 0), new Point2D(10000, 8000) });
        document.Add(walk);

        Assert.Same(document.Default3D, Views3D.Find(document, ViewReference.Model3D));
        Assert.Same(camera, Views3D.Find(document, ViewReference.View3D(camera.Id)));
        Assert.Equal(camera.Name, ViewReference.View3D(camera.Id).TitleIn(document));
        Assert.True(camera.Perspective);
        Assert.Equal(document.Levels[0].Elevation + Views3D.EyeHeight, camera.Camera!.Eye.Z, 6);

        var reopened = ProjectFile.FromJson(ProjectFile.ToJson(document));
        Assert.Equal(VisualStyle.HiddenLine, reopened.Default3D.Style);
        Assert.Equal(document.Default3D.SectionBox, reopened.Default3D.SectionBox);
        Assert.True(reopened.Default3D.SectionBoxOn);
        Assert.Contains(MeshKind.Roof, reopened.Default3D.HiddenKinds);
        Assert.Equal(document.Default3D.Camera, reopened.Default3D.Camera);
        var named = Assert.Single(reopened.Elements.OfType<View3D>());
        Assert.Equal((camera.Name, camera.Camera), (named.Name, named.Camera));
        var path = Assert.Single(reopened.Elements.OfType<Walkthrough>());
        Assert.Equal((walk.Name, 150), (path.Name, path.Frames));
        Assert.Equal(walk.Path, path.Path);
    }

    [Fact]
    public void AChangeToAViewIsUndoneWithoutMovingItsCamera()
    {
        var view = new View3D { Camera = new Camera3D(new Point3D(0, 0, 0), new Point3D(1, 1, 1)) };
        var command = new SetView3DCommand(view, v => v.Style = VisualStyle.Wireframe, "Visual Style");
        Assert.Equal(VisualStyle.Shaded, view.Style);
        command.Redo();
        Assert.Equal(VisualStyle.Wireframe, view.Style);

        // Looked round since: undoing the style leaves the camera where it is now.
        var moved = new Camera3D(new Point3D(5, 5, 5), new Point3D(1, 1, 1));
        view.Camera = moved;
        command.Undo();
        Assert.Equal(VisualStyle.Shaded, view.Style);
        Assert.Equal(moved, view.Camera);
        Assert.False(new SetView3DCommand(view, _ => { }, "Nothing").Changes);
    }

    /// <summary>A room 6 m by 4 m: walls round it, a floor in it.</summary>
    private static (BimDocument Document, IReadOnlyList<Mesh3D> Meshes, double Floor) Room()
    {
        var document = BimDocument.CreateDefault();
        var level = document.Levels[0];
        var type = document.TypesOf<WallType>().First(t => t.Function == WallFunction.Exterior);
        var corners = new[] { new Point2D(0, 0), new Point2D(6000, 0), new Point2D(6000, 4000), new Point2D(0, 4000) };
        for (var i = 0; i < 4; i++)
            document.Add(new Wall { Start = corners[i], End = corners[(i + 1) % 4], TypeId = type.Id, LevelId = level.Id, UnconnectedHeight = 2700 });

        var floor = new Floor { TypeId = document.TypesOf<FloorType>().First().Id, LevelId = level.Id };
        floor.SetBoundary(corners);
        document.Add(floor);
        return (document, ModelMeshBuilder.Build(document), floor.GetTopElevation(document));
    }

    [Fact]
    public void AWalkerStandsOnTheFloorAndIsStoppedByTheWalls()
    {
        var (_, meshes, top) = Room();
        Assert.Equal(top, Walking.FloorUnder(meshes, new Point2D(3000, 2000), top + 10)!.Value, 3);

        // Across the room is clear; into the wall is not.
        Assert.False(Walking.Blocked(meshes, new Point2D(1500, 2000), new Point2D(4000, 2000), top, Views3D.EyeHeight));
        Assert.True(Walking.Blocked(meshes, new Point2D(5000, 2000), new Point2D(5800, 2000), top, Views3D.EyeHeight));
    }

    [Fact]
    public void AWalkthroughGoesAlongItsPathAtEyeHeightLookingAhead()
    {
        var document = BimDocument.CreateDefault();
        var walk = new Walkthrough { LevelId = document.Levels[0].Id };
        walk.SetPath(new[] { new Point2D(0, 0), new Point2D(10000, 0), new Point2D(10000, 10000) });
        Assert.Equal(20, walk.Duration, 6);

        var first = walk.FrameAt(document, 0);
        Assert.Equal(new Point3D(0, 0, document.Levels[0].Elevation + walk.EyeHeight), first.Eye);
        Assert.True(first.Look.X > 0.99);

        var last = walk.FrameAt(document, walk.Frames - 1);
        Assert.Equal(10000, last.Eye.X, 6);
        Assert.Equal(10000, last.Eye.Y, 6);
        Assert.True(last.Look.Y > 0.99);

        // Halfway by distance is at the corner.
        var middle = walk.FrameAt(document, (walk.Frames - 1) / 2);
        Assert.InRange(middle.Eye.X, 9900, 10000);
    }
}

/// <summary>Cameras made on the plan, and 3D views shown, as a user does it.</summary>
[Collection("Wpf")]
public class Views3DPlanTests
{
    private static void OnUi(Action test)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (System.Windows.Application.Current is null) new BIMDesigner.UI.App().InitializeComponent();
                test();
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Exception("The view failed on the UI thread.", failure);
    }

    [Fact]
    public void TwoClicksMakeACameraAndADrawnPathAWalkthrough() => OnUi(() =>
    {
        var document = BimDocument.CreateDefault();
        var plan = new BIMDesigner.UI.Controls.PlanView { Document = document, ActiveLevelId = document.Levels[0].Id, History = new UndoStack() };
        View3D? opened = null;
        plan.CameraPlaced += (_, view) => opened = view;

        plan.SetTool(BIMDesigner.UI.Controls.PlanTool.Camera);
        Assert.True(plan.ClickCamera(new Point2D(0, 0)));
        Assert.True(plan.ClickCamera(new Point2D(0, 8000)));
        var camera = Assert.Single(document.Elements.OfType<View3D>());
        Assert.Same(camera, opened);
        Assert.True(camera.Camera!.Look.Y > 0.99);

        plan.SetTool(BIMDesigner.UI.Controls.PlanTool.Walkthrough);
        Assert.True(plan.ClickWalkthrough(new Point2D(0, 0)));
        Assert.True(plan.ClickWalkthrough(new Point2D(5000, 0)));
        Assert.True(plan.ClickWalkthrough(new Point2D(5000, 5000)));
        Assert.True(plan.FinishWalkthrough());
        Assert.Equal(3, Assert.Single(document.Elements.OfType<Walkthrough>()).Path.Count);

        plan.History!.Undo();
        Assert.Empty(document.Elements.OfType<Walkthrough>());
    });

    [Fact]
    public void TheViewShownKeepsItsCameraAndIsCutByItsBox() => OnUi(() =>
    {
        var (document, roof, _) = DormerToolTests.House(10000, 8000, 40);
        var model = new BIMDesigner.UI.Controls.ModelView { Width = 600, Height = 400 };
        model.Measure(new System.Windows.Size(600, 400));
        model.Arrange(new System.Windows.Rect(0, 0, 600, 400));
        model.Document = document;
        model.ZoomToFit();

        // Looked through, the view keeps where it looks from.
        Assert.NotNull(document.Default3D.Camera);
        model.Orbit(30, 0);
        var camera = document.Default3D.Camera!;

        // A section box round half the house: what is shown is inside it.
        document.Default3D.SectionBox = new Box3D(new Point3D(-1000, -1000, -1000), new Point3D(5000, 9000, 20000));
        document.Default3D.SectionBoxOn = true;
        model.Rebuild();
        Assert.Equal(camera, document.Default3D.Camera);
        var shown = (IReadOnlyList<Mesh3D>)typeof(BIMDesigner.UI.Controls.ModelView)
            .GetField("_meshes", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(model)!;
        Assert.All(shown.SelectMany(mesh => mesh.Positions), point => Assert.True(point.X <= 5000 + 1e-6));

        // Wireframe and hidden line draw without complaint.
        model.RenderStyle = VisualStyle.Wireframe;
        model.RenderStyle = VisualStyle.HiddenLine;
        Assert.Equal(VisualStyle.HiddenLine, document.Default3D.Style);
    });
}
