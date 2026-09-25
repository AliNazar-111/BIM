using System.Windows;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;
using BIMDesigner.UI;
using BIMDesigner.UI.Controls;

namespace BIMDesigner.Tests;

/// <summary>
/// What a right click offers, and that the entries do what they say. A door is opened and shut
/// from here because that is what one does with a door.
/// </summary>
[Collection("Wpf")]
public class OpeningMenuTests
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

    [Fact]
    public void ADoorsMenuOpensAndShutsIt()
    {
        OnUiThread(() =>
        {
            var document = BimDocument.CreateDefault();
            var wallType = document.TypesOf<WallType>().First(t => t.Name.StartsWith("Exterior"));
            var wall = new Wall
            {
                Start = new Point2D(0, 0), End = new Point2D(6000, 0),
                TypeId = wallType.Id, LevelId = document.Levels[0].Id, UnconnectedHeight = 3000
            };
            document.Add(wall);

            var door = new Door
            {
                TypeId = document.TypesOf<DoorType>().First(t => t.Name.StartsWith("Single -")).Id,
                LevelId = wall.LevelId, HostWallId = wall.Id, DistanceAlongWall = 3000
            };
            document.Add(door);

            var plan = new PlanView { Document = document, ActiveLevelId = wall.LevelId };
            var menu = plan.MenuFor(door);

            // What a door offers, and nothing that needs a menu it cannot act on.
            Assert.Equal("Open", menu[0].Header);
            Assert.Contains(menu, entry => entry.Header == "Flip Facing");
            Assert.Contains(menu, entry => entry.Header == "Pick New Host");
            Assert.Contains(menu, entry => entry.Header == "Delete");

            // The entry does what it says, and says the other thing next time.
            menu[0].Do!();
            Assert.True(door.IsOpen);
            Assert.Equal("Close", plan.MenuFor(door)[0].Header);

            plan.MenuFor(door)[0].Do!();
            Assert.False(door.IsOpen);

            // A wall is a different thing with different answers.
            Assert.Contains(plan.MenuFor(wall), entry => entry.Header == "Flip");
            Assert.DoesNotContain(plan.MenuFor(wall), entry => entry.Header == "Open");
        });
    }

    [Fact]
    public void ACurtainWallsDoorPanelOpensFromItsMenuToo()
    {
        OnUiThread(() =>
        {
            var document = BimDocument.CreateDefault();
            var type = document.TypesOf<CurtainWallType>().Single(t => t.Name.Contains("Storefront"));
            var wall = new Wall
            {
                Start = new Point2D(0, 0), End = new Point2D(6000, 0),
                TypeId = type.Id, LevelId = document.Levels[0].Id, UnconnectedHeight = 3000
            };
            document.Add(wall);

            var placement = CurtainDoors.Place(document, wall, CurtainDoors.TypesFor(document)[0], along: 3000)!;
            new SetCurtainLayoutCommand(wall, placement.Grid, placement.Panels).Redo();

            var doorPanel = CurtainLayout.Of(document, wall)!.Cells
                .Where(c => c.Kind == CurtainPanelKind.Door)
                .Select(c => CurtainPanel.At(document, wall, c.Column, c.Row)!)
                .Single();

            double LeafAcross() => ModelMeshBuilder.BuildWall(document, wall)
                .Single(m => m.ElementId == doorPanel.Id && m.Description == "Door leaf")
                .Bounds()!.Value is var leaf ? leaf.Max.Y - leaf.Min.Y : 0;

            var plan = new PlanView { Document = document, ActiveLevelId = wall.LevelId };
            var menu = plan.MenuFor(doorPanel);

            // A panel says which one it is, and a door panel opens like any other door.
            Assert.StartsWith("Panel:", menu[0].Header);
            Assert.Equal("Open", menu[1].Header);
            Assert.Contains(menu, entry => entry.Header == "Mirror");

            // Shut, the leaf lies in its bay; open, it has swung out across the wall.
            var shut = LeafAcross();
            Assert.True(shut < 100, "the leaf is not lying in its doorway");

            menu[1].Do!();
            Assert.True(LeafAcross() > 500, "the panel door did not swing open");
            Assert.Equal("Close", plan.MenuFor(doorPanel)[1].Header);
            Assert.True((bool)doorPanel.GetInstanceParameters(document).Single(p => p.Name == "Open").Value!);

            // Shut again from the same entry, it is back where it was.
            plan.MenuFor(doorPanel)[1].Do!();
            Assert.Equal(shut, LeafAcross(), precision: 6);

            // A plain glass panel has nothing to swing, so it is not offered.
            var glazed = CurtainLayout.Of(document, wall)!.Cells.First(c => c.Kind != CurtainPanelKind.Door);
            var pane = CurtainPanel.At(document, wall, glazed.Column, glazed.Row)!;
            Assert.DoesNotContain(plan.MenuFor(pane), entry => entry.Header is "Open" or "Close");
        });
    }

    [Fact]
    public void RightClickingFindsTheDoorUnderTheCursor()
    {
        OnUiThread(() =>
        {
            var document = BimDocument.CreateDefault();
            var wallType = document.TypesOf<WallType>().First(t => t.Name.StartsWith("Exterior"));
            var wall = new Wall
            {
                Start = new Point2D(0, 0), End = new Point2D(6000, 0),
                TypeId = wallType.Id, LevelId = document.Levels[0].Id, UnconnectedHeight = 3000
            };
            document.Add(wall);

            var door = new Door
            {
                TypeId = document.TypesOf<DoorType>().First(t => t.Name.StartsWith("Single -")).Id,
                LevelId = wall.LevelId, HostWallId = wall.Id, DistanceAlongWall = 3000
            };
            document.Add(door);

            var plan = new PlanView { Document = document, ActiveLevelId = wall.LevelId, Width = 800, Height = 600 };
            plan.Measure(new Size(800, 600));
            plan.Arrange(new Rect(0, 0, 800, 600));
            plan.ZoomToFit();
            plan.UpdateLayout();

            // On the door, the door; on the wall away from it, the wall; off the building, nothing.
            Assert.IsType<Door>(plan.PickAt(plan.ScreenFor(new Point2D(3000, 0))));
            Assert.IsType<Wall>(plan.PickAt(plan.ScreenFor(new Point2D(800, 0))));
            Assert.Null(plan.PickAt(plan.ScreenFor(new Point2D(3000, 4000))));
        });
    }
}
