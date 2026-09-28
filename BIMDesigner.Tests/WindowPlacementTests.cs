using System.Windows;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.UI;
using BIMDesigner.UI.Controls;

namespace BIMDesigner.Tests;

/// <summary>
/// Windows as Revit adds them: hosted in any wall, placed from a 3D view as readily as from a
/// plan, and moved to another wall with Pick New Host.
/// </summary>
[Collection("Wpf")]
public class WindowPlacementTests
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

    private static (BimDocument Document, Wall Wall, PlanView Plan) Wall6m()
    {
        var document = BimDocument.CreateDefault();
        var type = document.TypesOf<WallType>().First(t => t.Name.StartsWith("Exterior"));
        var wall = new Wall
        {
            Start = new Point2D(0, 0), End = new Point2D(6000, 0),
            TypeId = type.Id, LevelId = document.Levels[0].Id, UnconnectedHeight = 3000
        };
        document.Add(wall);

        var plan = new PlanView { Document = document, ActiveLevelId = wall.LevelId };
        return (document, wall, plan);
    }

    [Fact]
    public void AWindowIsPlacedWhereTheWallWasClickedIn3D()
    {
        OnUiThread(() =>
        {
            var (document, wall, plan) = Wall6m();
            var type = document.TypesOf<WindowType>().First();
            plan.ActiveWindowTypeId = type.Id;
            plan.SetTool(PlanTool.Window);

            // A click on the wall's face, 2 m along it and 1.6 m up.
            Assert.True(plan.PlaceOpeningIn3D(wall.Id, new Point3D(2000, 100, 1600)));

            var window = document.Elements.OfType<BIMDesigner.Core.Architecture.Window>().Single();
            Assert.Equal(wall.Id, window.HostWallId);
            Assert.Equal(2000, window.DistanceAlongWall, precision: 6);

            // Centred on the height clicked, rather than dropped to the type's usual sill.
            Assert.Equal(1600 - type.Height / 2, window.SillHeight, precision: 6);
            Assert.Equal("1", window.Mark);
        });
    }

    [Fact]
    public void AWindowStaysInsideTheWallHoweverHighItIsClicked()
    {
        OnUiThread(() =>
        {
            var (document, wall, plan) = Wall6m();
            var type = document.TypesOf<WindowType>().First();
            plan.ActiveWindowTypeId = type.Id;
            plan.SetTool(PlanTool.Window);

            // Clicked at the very top of a 3 m wall: it sits under the top, not through it, with a
            // little wall left over its head.
            plan.PlaceOpeningIn3D(wall.Id, new Point3D(3000, 100, 2980));

            var window = document.Elements.OfType<BIMDesigner.Core.Architecture.Window>().Single();
            Assert.Equal(3000 - WallOpenings.FitMargin - type.Height, window.SillHeight, precision: 6);
        });
    }

    [Fact]
    public void ADoorPlacedIn3DStandsOnTheFloor()
    {
        OnUiThread(() =>
        {
            var (document, wall, plan) = Wall6m();
            plan.ActiveDoorTypeId = document.TypesOf<DoorType>().First(t => !t.CurtainPanel).Id;
            plan.SetTool(PlanTool.Door);

            plan.PlaceOpeningIn3D(wall.Id, new Point3D(1500, 100, 1900));

            var door = document.Elements.OfType<Door>().Single();
            Assert.Equal(0, door.SillHeight, precision: 6);
            Assert.Equal(1500, door.DistanceAlongWall, precision: 6);
        });
    }

    [Fact]
    public void AClickOnACurtainWallsPanelPutsTheDoorInThatWall()
    {
        OnUiThread(() =>
        {
            var document = BimDocument.CreateDefault();
            var curtain = document.TypesOf<CurtainWallType>().First(t => t.Name.Contains("Storefront"));
            var wall = new Wall
            {
                Start = new Point2D(0, 0), End = new Point2D(6000, 0),
                TypeId = curtain.Id, LevelId = document.Levels[0].Id, UnconnectedHeight = 3000
            };
            document.Add(wall);

            var plan = new PlanView { Document = document, ActiveLevelId = wall.LevelId };
            plan.ActiveDoorTypeId = CurtainDoors.TypesFor(document)[0].Id;
            plan.SetTool(PlanTool.Door);

            // The click lands on a panel, not on the wall itself, and the panel belongs to it.
            var panel = CurtainPanel.At(document, wall, 1, 0)!;
            Assert.True(plan.PlaceOpeningIn3D(panel.Id, new Point3D(2000, 0, 900)));

            Assert.Empty(document.Elements.OfType<Door>());
            Assert.Contains(CurtainLayout.Of(document, wall)!.Cells, c => c.Kind == CurtainPanelKind.Door);
        });
    }

    [Fact]
    public void AClickOnSomethingThatIsNotAWallPlacesNothing()
    {
        OnUiThread(() =>
        {
            var (document, _, plan) = Wall6m();
            plan.ActiveWindowTypeId = document.TypesOf<WindowType>().First().Id;
            plan.SetTool(PlanTool.Window);

            Assert.False(plan.PlaceOpeningIn3D(Guid.NewGuid(), new Point3D(2000, 100, 1600)));
            Assert.Empty(document.Elements.OfType<BIMDesigner.Core.Architecture.Window>());
        });
    }
}
