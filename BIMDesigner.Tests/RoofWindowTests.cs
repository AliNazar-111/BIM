using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Parameters;
using BIMDesigner.Core.Views;
using BIMDesigner.Infrastructure.Serialization;

namespace BIMDesigner.Tests;

/// <summary>
/// Roof windows: clicked onto a roof's slope, lying in it and lined up with it, the roof cut away
/// under them - and carried, copied and deleted with their roof.
/// </summary>
public class RoofWindowTests
{
    /// <summary>A 10 m by 8 m house under a 40° gable roof, eaves along the long sides, ridge along the middle.</summary>
    private static (BimDocument Document, Roof Roof, RoofWindowType Type) House()
    {
        var (document, roof, _) = DormerToolTests.House(10000, 8000, 40);
        return (document, roof, document.TypesOf<RoofWindowType>().First(t => t.Name.Contains("780 x 980")));
    }

    private static RoofWindow Put(BimDocument document, Roof roof, RoofWindowType type, Point2D at)
    {
        var window = new RoofWindow { RoofId = roof.Id, LevelId = roof.LevelId, TypeId = type.Id, Location = at };
        document.Add(window);
        return window;
    }

    [Fact]
    public void ItLiesInTheSlopeLinedUpWithItAndTheRoofIsCutAwayUnderIt()
    {
        var (document, roof, type) = House();
        var window = Put(document, roof, type, new Point2D(5000, 2000));
        var frame = RoofWindows.Frame(document, window)!;

        // Up the slope from the eave at y = 0 toward the ridge; as long up it as the type, seen in plan shorter.
        Assert.Equal(1, frame.Uphill.Y, 6);
        Assert.Equal(type.Height * Math.Cos(40 * Math.PI / 180), frame.Run, 3);
        Assert.Null(RoofWindows.Problem(document, frame, window));

        // The roof has a hole there: nothing of it over the window's middle.
        Assert.Contains(RoofJoin.Openings(document, roof), hole => Polygon2D.Contains(hole, window.Location));
        Assert.DoesNotContain(RoofSolid.Pieces(document, roof), piece => Polygon2D.Contains(piece.Outline, window.Location));

        // And less roof to buy tiles for.
        var whole = roof.Surface(document).SlopingArea;
        Assert.Equal(whole - type.Width * type.Height, roof.SlopingArea(document), 0);
    }

    [Fact]
    public void ItStandsUpOutOfTheRoofWithItsPaneInTheSlope()
    {
        var (document, roof, type) = House();
        var window = Put(document, roof, type, new Point2D(5000, 2000));
        var frame = RoofWindows.Frame(document, window)!;
        var meshes = RoofWindows.Meshes(document, window);

        Assert.Contains(meshes, mesh => mesh.Kind == MeshKind.Glazing);
        var highest = meshes.SelectMany(mesh => mesh.Positions).Where(p => Math.Abs(p.X - frame.Corners[0].X) < 1 && Math.Abs(p.Y - frame.Corners[0].Y) < 1).Max(p => p.Z);
        Assert.Equal(frame.Top(frame.Corners[0]) + type.Upstand / Math.Cos(40 * Math.PI / 180), highest, 3);
    }

    [Theory]
    [InlineData(5000, 3900, "ridge")]
    [InlineData(5000, 250, "off the roof")]
    [InlineData(9800, 2000, "off the roof")]
    public void WhereItWouldNotLieInOneSlopeItSaysWhy(double x, double y, string why)
    {
        var (document, roof, type) = House();
        var frame = RoofWindows.FrameOn(document, roof, type, new Point2D(x, y))!;

        Assert.Contains(why, RoofWindows.Problem(document, frame));
    }

    [Fact]
    public void NotOverAnotherOne()
    {
        var (document, roof, type) = House();
        Put(document, roof, type, new Point2D(5000, 2000));

        var frame = RoofWindows.FrameOn(document, roof, type, new Point2D(5300, 2200))!;
        Assert.Contains("already an opening", RoofWindows.Problem(document, frame));
    }

    [Fact]
    public void ItGoesWithItsRoofMovedCopiedAndDeleted()
    {
        var (document, roof, type) = House();
        var window = Put(document, roof, type, new Point2D(5000, 2000));

        var move = new MoveElementsCommand(new[] { roof }, new Vector2D(3000, 0), document: document);
        move.Redo();
        Assert.Equal(8000, window.Location.X, 6);
        move.Undo();
        Assert.Equal(5000, window.Location.X, 6);

        var copies = ElementCopy.Duplicate(document, new[] { roof });
        var copiedRoof = copies.OfType<Roof>().Single();
        Assert.Contains(copies.OfType<RoofWindow>(), copy => copy.RoofId == copiedRoof.Id);

        new DeleteElementCommand(document, roof).Redo();
        Assert.DoesNotContain(document.Elements, element => ReferenceEquals(element, window));
    }

    [Fact]
    public void SavedAndOpenedItIsStillThere()
    {
        var (document, roof, type) = House();
        var window = Put(document, roof, type, new Point2D(5000, 2000));

        var reloaded = ProjectFile.FromJson(ProjectFile.ToJson(document));
        var again = reloaded.Elements.OfType<RoofWindow>().Single();

        Assert.Equal(window.Id, again.Id);
        Assert.Equal(window.Location, again.Location);
        Assert.Equal(type.Name, reloaded.FindType<RoofWindowType>(again.TypeId)!.Name);
        Assert.Contains(RoofJoin.Openings(reloaded, reloaded.Elements.OfType<Roof>().Single()), hole => Polygon2D.Contains(hole, again.Location));
    }

    [Fact]
    public void ASectionThroughItCutsItsFrameAndPane()
    {
        var (document, roof, type) = House();
        var window = Put(document, roof, type, new Point2D(5000, 2000));
        var marker = new SectionMarker { Name = "A", Start = new Point2D(5000, -2000), End = new Point2D(5000, 10000), LevelId = roof.LevelId, ViewDepth = 3000 };
        document.Add(marker);

        var cut = SectionProjection.Build(document, marker).Pieces.Where(piece => piece.ElementId == window.Id).ToList();
        Assert.Equal(2, cut.Count(piece => piece.Part == SectionPart.Frame));
        Assert.Single(cut, piece => piece.Part == SectionPart.Glazing);
    }

    private static ParameterValue Parameter(BimDocument document, RoofWindow window, string name) =>
        window.GetInstanceParameters(document).Single(parameter => parameter.Name == name);

    [Fact]
    public void ChangingOneWindowChangesThatOneAloneAndNotItsTypeOrTheNextOnePutIn()
    {
        var (document, roof, type) = House();
        var first = Put(document, roof, type, new Point2D(3000, 2000));
        var second = Put(document, roof, type, new Point2D(7000, 2000));

        var width = Parameter(document, first, "Width");
        Assert.True(width.TrySet(1000.0));
        Assert.True(Parameter(document, first, "Upstand").TrySet(150.0));

        Assert.Equal(1000, RoofWindows.Frame(document, first)!.Type.Width);
        var corners = RoofWindows.Frame(document, first)!.Corners;
        Assert.Equal(1000, corners[0].DistanceTo(corners[1]), 6);
        Assert.Equal(type.Width, RoofWindows.Frame(document, second)!.Type.Width);
        Assert.Equal(780, type.Width);
        Assert.Equal(90, type.Upstand);

        // The next one put in is the type's size.
        var next = Put(document, roof, type, new Point2D(5000, 6000));
        Assert.Equal(780, RoofWindows.Frame(document, next)!.Type.Width);

        // One step back, and it is its type's size again.
        width.TakeAppliedChange()!.Undo();
        Assert.Null(first.Width);
        Assert.Equal(780, RoofWindows.Frame(document, first)!.Type.Width);
    }

    [Fact]
    public void ASizeThatWouldNotFitWhereItIsIsRefusedWithWhy()
    {
        var (document, roof, type) = House();
        var window = Put(document, roof, type, new Point2D(5000, 3000));

        // Up the slope into the ridge.
        var height = Parameter(document, window, "Height (up the slope)");
        Assert.False(height.TrySet(2400.0));
        Assert.Contains("ridge", height.Message);
        Assert.Null(window.Height);

        // All frame and no glass.
        var frame = Parameter(document, window, "Frame Width");
        Assert.False(frame.TrySet(400.0));
        Assert.Contains("no glass", frame.Message);
    }

    [Fact]
    public void ItsOwnMaterialsAndSizeGoWithItWhenCopiedSavedAndOpened()
    {
        var (document, roof, type) = House();
        var window = Put(document, roof, type, new Point2D(5000, 2000));
        var timber = document.Materials.First(material => material.Id != type.FrameMaterialId);

        Assert.True(Parameter(document, window, "Frame Material").TrySet(timber.Name));
        Assert.True(Parameter(document, window, "Width").TrySet(940.0));
        Assert.Equal(timber.Id, window.FrameMaterialId);
        Assert.Contains(RoofWindows.Meshes(document, window), mesh => mesh.Description == timber.Name);

        var copy = ElementCopy.Duplicate(document, new[] { window }).OfType<RoofWindow>().Single();
        Assert.Equal(window.Own, copy.Own);

        var reloaded = ProjectFile.FromJson(ProjectFile.ToJson(document)).Elements.OfType<RoofWindow>().Single(again => again.Id == window.Id);
        Assert.Equal(window.Own, reloaded.Own);

        // Back to its type's.
        Assert.True(Parameter(document, window, "Frame Material").TrySet(RoofWindowOwn.ByType));
        Assert.Null(window.FrameMaterialId);
    }

    [Fact]
    public void ASightLineFindsTheRoofTopEvenOverTheHoleTheWindowCut()
    {
        var (document, roof, type) = House();
        var window = Put(document, roof, type, new Point2D(5000, 2000));
        var top = RoofWindows.Frame(document, window)!.Top(window.Location);

        // From off to one side and above, looking down at the window's middle, where its own hole is.
        var eye = new Point3D(-5000, -8000, top + 10000);
        var at = RoofWindows.Along(document, roof, eye, new Point3D(5000, 2000, top))!.Value;
        Assert.Equal(5000, at.X, 3);
        Assert.Equal(2000, at.Y, 3);

        // Looking away from the house, nothing.
        Assert.Null(RoofWindows.Along(document, roof, eye, new Point3D(-6000, -9000, top + 11000)));
    }

    [Fact]
    public void MovedWithoutItsRoofOverARidgeOrOffTheRoofItDoesNotFit()
    {
        var (document, roof, type) = House();
        var window = Put(document, roof, type, new Point2D(5000, 2000));
        Assert.Null(RoofWindows.Misplaced(document, new Element[] { window }));

        window.Location = new Point2D(5000, 3900);
        Assert.Contains("ridge", RoofWindows.Misplaced(document, new Element[] { window }));

        window.Location = new Point2D(20000, 2000);
        Assert.Contains("stay on its roof", RoofWindows.Misplaced(document, new Element[] { window }));

        // Moving with its roof it is carried, wherever the roof goes.
        window.Location = new Point2D(5000, 2000);
        var move = new MoveElementsCommand(new[] { roof }, new Vector2D(0, 30000), document: document);
        move.Redo();
        Assert.Null(RoofWindows.Misplaced(document, new Element[] { roof, window }));
    }

    [Fact]
    public void DraggedIn3DItFollowsTheRoofUnderTheCursorAndIsOneMoveToUndo()
    {
        OnUiThread(() =>
        {
            var (document, roof, type) = House();
            var window = Put(document, roof, type, new Point2D(5000, 2000));
            var plan = new BIMDesigner.UI.Controls.PlanView
            {
                Document = document, ActiveLevelId = roof.LevelId, History = new UndoStack()
            };

            // Looking straight down on the roof through a point.
            Point3D Above(double x, double y) => new(x, y, 50000);
            Point3D Below(double x, double y) => new(x, y, 0);

            Assert.True(plan.CanDragIn3D(window.Id));
            Assert.False(plan.CanDragIn3D(roof.Id));
            Assert.True(plan.DragRoofWindowIn3D(window.Id, Above(4000, 2100), Below(4000, 2100)));
            Assert.True(plan.DragRoofWindowIn3D(window.Id, Above(3000, 2200), Below(3000, 2200)));

            // Over the ridge and off the roof it does not go: it waits where it last fitted.
            Assert.False(plan.DragRoofWindowIn3D(window.Id, Above(3000, 3900), Below(3000, 3900)));
            Assert.False(plan.DragRoofWindowIn3D(window.Id, Above(30000, 2000), Below(30000, 2000)));
            Assert.Equal(new Point2D(3000, 2200), window.Location);

            plan.EndRoofWindowDrag();
            Assert.Equal(new Point2D(3000, 2200), window.Location);

            plan.History!.Undo();
            Assert.Equal(new Point2D(5000, 2000), window.Location);
            plan.History.Redo();
            Assert.Equal(new Point2D(3000, 2200), window.Location);
        });
    }

    [Fact]
    public void NudgedUpTheSlopeItStopsShortOfTheRidge()
    {
        OnUiThread(() =>
        {
            var (document, roof, type) = House();
            var window = Put(document, roof, type, new Point2D(5000, 2000));
            var plan = new BIMDesigner.UI.Controls.PlanView
            {
                Document = document, ActiveLevelId = roof.LevelId, History = new UndoStack()
            };
            plan.Select(window);

            Assert.True(plan.Nudge(new Vector2D(0, 1), far: true, continuing: false));
            Assert.Equal(3000, window.Location.Y, 6);

            Assert.False(plan.Nudge(new Vector2D(0, 1), far: true, continuing: true));
            Assert.Equal(3000, window.Location.Y, 6);
        });
    }

    [Fact]
    public void TheSightLineThroughThe3DViewMeetsWhatIsUnderTheCursor()
    {
        OnUiThread(() =>
        {
            var (document, _, _) = House();
            var view = new BIMDesigner.UI.Controls.ModelView { Document = document };
            view.Measure(new System.Windows.Size(800, 600));
            view.Arrange(new System.Windows.Rect(0, 0, 800, 600));
            view.UpdateLayout();

            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly;
            var hitTest = typeof(BIMDesigner.UI.Controls.ModelView).GetMethod("HitTest", flags)!;
            var rayThrough = typeof(BIMDesigner.UI.Controls.ModelView).GetMethod("RayThrough", flags)!;

            var checkedPoints = 0;
            foreach (var point in new[] { new System.Windows.Point(400, 300), new System.Windows.Point(330, 260), new System.Windows.Point(470, 340) })
            {
                if (hitTest.Invoke(view, new object[] { point }) is not ValueTuple<Guid, Point3D> hit) continue;
                var (from, through) = ((Point3D, Point3D))rayThrough.Invoke(view, new object[] { point })!;

                // The point hit lies on the line of sight.
                var (dx, dy, dz) = (through.X - from.X, through.Y - from.Y, through.Z - from.Z);
                var length = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                var (hx, hy, hz) = (hit.Item2.X - from.X, hit.Item2.Y - from.Y, hit.Item2.Z - from.Z);
                var along = (hx * dx + hy * dy + hz * dz) / length;
                var off = Math.Sqrt(Math.Max(0, hx * hx + hy * hy + hz * hz - along * along));

                Assert.True(off < 5, $"The hit is {off:0.0} mm off the line of sight.");
                checkedPoints++;
            }

            Assert.True(checkedPoints > 0, "Nothing was under the middle of the view.");
        });
    }

    private static void OnUiThread(Action test)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { test(); }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Exception("The view failed on the UI thread.", failure);
    }

    [Fact]
    public void TheToolPutsOneInTheSlopeClickedAndNotOffTheRoof()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var (document, roof, _) = House();
                var plan = new BIMDesigner.UI.Controls.PlanView
                {
                    Document = document, ActiveLevelId = roof.LevelId, History = new UndoStack()
                };
                plan.SetTool(BIMDesigner.UI.Controls.PlanTool.RoofWindow);

                Assert.False(plan.PlaceRoofWindowAt(new Point2D(20000, 2000)));
                Assert.True(plan.PlaceRoofWindowAt(new Point2D(3000, 6000)));

                var window = document.Elements.OfType<RoofWindow>().Single();
                Assert.Equal(roof.Id, window.RoofId);
                Assert.Equal(-1, RoofWindows.Frame(document, window)!.Uphill.Y, 6);

                plan.History!.Undo();
                Assert.Empty(document.Elements.OfType<RoofWindow>());
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Exception("The plan failed on the UI thread.", failure);
    }
}
