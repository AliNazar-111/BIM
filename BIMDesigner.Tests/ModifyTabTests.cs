using System.Windows;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.UI;
using BIMDesigner.UI.Controls;
using BimWindow = BIMDesigner.Core.Architecture.Window;

namespace BIMDesigner.Tests;

/// <summary>
/// The Modify tab's tools on what a house is made of: a window repeated along a front goes into
/// it, one pasted over another wall goes into that wall and a door mirrored on its own is hung
/// the other way; a copy pastes where the cursor is; a band dragged across a room picks up its
/// furniture, columns and beams; and a wall trimmed where another crosses it keeps the part clicked.
/// </summary>
[Collection("Wpf")]
public class ModifyTabTests
{
    private static void OnUi(Action act)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (Application.Current is null) new App().InitializeComponent();
                act();
            }
            catch (Exception e) { failure = e; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        if (failure is not null) throw failure;
    }

    /// <summary>
    /// A 10 m x 7 m box of walls drawn clockwise, so their outsides face out: west, north (left
    /// to right), east, then the front (right to left), on a plan with an undo history.
    /// </summary>
    private static (BimDocument Document, List<Wall> Walls, PlanView Plan, UndoStack History) Box()
    {
        var document = BimDocument.CreateDefault();
        var level = document.Levels[0].Id;
        var type = document.TypesOf<WallType>().First(t => t.Name.StartsWith("Exterior"));
        var corners = new[] { new Point2D(0, 0), new Point2D(0, 7000), new Point2D(10000, 7000), new Point2D(10000, 0) };
        var walls = Enumerable.Range(0, 4).Select(i => new Wall
        {
            Start = corners[i], End = corners[(i + 1) % 4], TypeId = type.Id, LevelId = level, UnconnectedHeight = 3000
        }).ToList();
        foreach (var wall in walls) document.Add(wall);

        var history = new UndoStack();
        return (document, walls, new PlanView { Document = document, ActiveLevelId = level, History = history }, history);
    }

    private static BimWindow AddWindow(BimDocument document, Wall wall, double along)
    {
        var window = new BimWindow
        {
            HostWallId = wall.Id, LevelId = wall.LevelId, TypeId = document.TypesOf<WindowType>().First(t => t.Width == 1200).Id,
            DistanceAlongWall = along, SillHeight = 900
        };
        document.Add(window);
        return window;
    }

    private static List<BimWindow> Windows(BimDocument document) => document.Elements.OfType<BimWindow>().ToList();

    [Fact]
    public void AWindowArrayedAlongAFrontGoesIntoIt()
    {
        OnUi(() =>
        {
            var (document, walls, plan, _) = Box();
            var front = walls[3];
            var window = AddWindow(document, front, 7500);
            window.FlipHand = true;

            // Three, 3 m apart along the front: x = 2500, 5500 and 8500.
            plan.Select(window);
            plan.ArrayCount = 3;
            plan.PlaceArrayPointAt(new Point2D(2500, 0));
            plan.PlaceArrayPointAt(new Point2D(5500, 0));

            var windows = Windows(document).OrderByDescending(w => w.DistanceAlongWall).ToList();
            Assert.Equal(3, windows.Count);
            Assert.All(windows, w => Assert.Equal(front.Id, w.HostWallId));
            Assert.Equal(new[] { 7500.0, 4500, 1500 }, windows.Select(w => Math.Round(w.DistanceAlongWall)));
            Assert.All(windows, w => Assert.Equal((window.FlipFacing, window.FlipHand), (w.FlipFacing, w.FlipHand)));
        });
    }

    [Fact]
    public void CopiesThatLandPastAWallsEndAreLeftOutAndSaid()
    {
        OnUi(() =>
        {
            var (document, walls, plan, _) = Box();
            var window = AddWindow(document, walls[3], 7500);
            string? said = null;
            plan.HintChanged += (_, hint) => said = hint;

            // 6 m apart: the second at x = 8500 is in the front; the third, at 14500, is in nothing.
            plan.Select(window);
            plan.ArrayCount = 3;
            plan.PlaceArrayPointAt(new Point2D(2500, 0));
            plan.PlaceArrayPointAt(new Point2D(8500, 0));

            Assert.Equal(2, Windows(document).Count);
            Assert.Contains("1 opening not placed", said);
        });
    }

    [Fact]
    public void APastedWindowGoesIntoTheWallUnderTheCursorNotOnTopOfItself()
    {
        OnUi(() =>
        {
            var (document, walls, plan, _) = Box();
            var window = AddWindow(document, walls[3], 7500);
            window.FlipHand = true;

            plan.Select(window);
            plan.CopySelection();

            // Over the back wall, 4 m along it: in it there, facing out and hung as the front one is.
            plan.PasteAt(new Point2D(4000, 7000));
            var pasted = Assert.Single(Windows(document), w => !ReferenceEquals(w, window));
            Assert.Equal(walls[1].Id, pasted.HostWallId);
            Assert.Equal(4000, pasted.DistanceAlongWall, precision: 0);
            Assert.Equal((window.FlipFacing, window.FlipHand), (pasted.FlipFacing, pasted.FlipHand));

            // Over the middle of the room, where there is no wall, or in place on the same storey,
            // over itself: nothing pasted.
            plan.PasteAt(new Point2D(5000, 3500));
            plan.Paste(inPlace: true);
            Assert.Equal(2, Windows(document).Count);
        });
    }

    [Fact]
    public void ADoorMirroredOnItsOwnIsHungFromTheOtherHand()
    {
        OnUi(() =>
        {
            var (document, walls, plan, history) = Box();
            var front = walls[3];
            var door = new Door { HostWallId = front.Id, LevelId = front.LevelId, TypeId = document.TypesOf<DoorType>().First(t => t.Name.StartsWith("Single -")).Id, DistanceAlongWall = 3000 };
            document.Add(door);

            // About a line across the front at x = 5000: the copy at x = 3000, in the front, hinged the other side.
            plan.Select(door);
            plan.MirrorKeepsOriginal = true;
            plan.PlaceMirrorPointAt(new Point2D(5000, -1000));
            plan.PlaceMirrorPointAt(new Point2D(5000, 1000));
            var copy = Assert.Single(document.Elements.OfType<Door>(), d => !ReferenceEquals(d, door));
            Assert.Equal(front.Id, copy.HostWallId);
            Assert.Equal(7000, copy.DistanceAlongWall, precision: 0);
            Assert.Equal(door.FlipFacing, copy.FlipFacing);
            Assert.NotEqual(door.FlipHand, copy.FlipHand);

            // Across the middle of the house, onto the back wall: opening out of it as the front
            // door opens out of the front - its mirror image - and hung from the other hand.
            plan.Select(door);
            plan.PlaceMirrorPointAt(new Point2D(0, 3500));
            plan.PlaceMirrorPointAt(new Point2D(10000, 3500));
            var back = Assert.Single(document.Elements.OfType<Door>(), d => d.HostWallId == walls[1].Id);
            Assert.Equal(7000, back.DistanceAlongWall, precision: 0);
            Assert.Equal(door.FlipFacing, back.FlipFacing);
            Assert.True(back.FlipHand);

            // Mirrored rather than copied, the door itself goes - and comes back on undo.
            plan.MirrorKeepsOriginal = false;
            plan.Select(door);
            plan.PlaceMirrorPointAt(new Point2D(6000, -1000));
            plan.PlaceMirrorPointAt(new Point2D(6000, 1000));
            Assert.Equal(5000, door.DistanceAlongWall, precision: 0);
            Assert.True(door.FlipHand);
            history.Undo();
            Assert.Equal(3000, door.DistanceAlongWall, precision: 0);
            Assert.False(door.FlipHand);
        });
    }

    [Fact]
    public void ACopiedSofaPastesWhereTheCursorIs()
    {
        OnUi(() =>
        {
            var (document, _, plan, _) = Box();
            var type = document.TypesOf<ComponentType>().First(t => t.Name == "Sofa - Three Seat");
            var sofa = new Component { TypeId = type.Id, TypeKind = type.Kind, LevelId = document.Levels[0].Id, Location = new Point2D(3000, 3000) };
            document.Add(sofa);

            plan.Select(sofa);
            plan.CopySelection();

            // Paste waits for a click to say where; Esc lets it go without pasting anything.
            plan.BeginPaste();
            Assert.True(plan.PastePending);
            Assert.True(plan.CancelPendingOperation());
            Assert.False(plan.PastePending);
            Assert.Single(document.Elements.OfType<Component>());

            plan.PasteAt(new Point2D(7000, 5000));

            var pasted = Assert.Single(document.Elements.OfType<Component>(), c => !ReferenceEquals(c, sofa));
            Assert.Equal(7000, pasted.Location.X, precision: 6);
            Assert.Equal(5000, pasted.Location.Y, precision: 6);
        });
    }

    [Fact]
    public void ABandPicksUpFurnitureColumnsAndBeams()
    {
        OnUi(() =>
        {
            var (document, _, plan, _) = Box();
            var level = document.Levels[0].Id;
            var sofaType = document.TypesOf<ComponentType>().First(t => t.Name == "Sofa - Three Seat");
            var sofa = new Component { TypeId = sofaType.Id, TypeKind = sofaType.Kind, LevelId = level, Location = new Point2D(3000, 3000) };
            var column = new Column { TypeId = document.TypesOf<ColumnType>().First().Id, LevelId = level, Location = new Point2D(6500, 3000), UnconnectedHeight = 3000 };
            var beam = new Beam { TypeId = document.TypesOf<BeamType>().First().Id, LevelId = level, Start = new Point2D(500, 5000), End = new Point2D(9500, 5000) };
            foreach (var element in new Element[] { sofa, column, beam }) document.Add(element);

            // A band over the middle of the sofa, its edge only in it; one round the column; one the beam runs through.
            plan.SelectInBand(new Point2D(2500, 2700), new Point2D(3500, 3700));
            Assert.Equal(new Element[] { sofa }, plan.SelectedElements);
            plan.SelectInBand(new Point2D(6000, 2500), new Point2D(7000, 3500));
            Assert.Equal(new Element[] { column }, plan.SelectedElements);
            plan.SelectInBand(new Point2D(4000, 4500), new Point2D(5000, 5500));
            Assert.Equal(new Element[] { beam }, plan.SelectedElements);
        });
    }
}
