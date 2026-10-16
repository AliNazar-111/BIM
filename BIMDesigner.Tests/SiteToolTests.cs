using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.UI;
using BIMDesigner.UI.Controls;

namespace BIMDesigner.Tests;

/// <summary>
/// The Site tab's tools on the plan: the ground put round the building at a click, 150 under its
/// floor; pads and the property line drawn by their corners; spot elevations; a spot level taken
/// out; and the ground coloured by its slope.
/// </summary>
[Collection("Wpf")]
public class SiteToolTests
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
        thread.Start();
        thread.Join();
        if (failure is not null) throw failure;
    }

    private static (BimDocument Document, PlanView Plan) House()
    {
        var (document, _) = StairTests.House();
        var outside = document.TypesOf<WallType>().First(type => type.Function == WallFunction.Exterior);
        foreach (var wall in document.Walls) wall.TypeId = outside.Id;
        var floor = new Floor { LevelId = document.Levels[0].Id, TypeId = document.TypesOf<FloorType>().First().Id };
        floor.SetBoundary(new[] { new Point2D(0, 0), new Point2D(6000, 0), new Point2D(6000, 5000), new Point2D(0, 5000) });
        document.Add(floor);
        var plan = new PlanView { Document = document, ActiveLevelId = document.Levels[0].Id, Width = 600, Height = 500, ShowUnderlay = false };
        return (document, plan);
    }

    [Fact]
    public void AroundBuildingPutsTheGroundRoundItUnderTheDampProofCourseOnce() => OnUi(() =>
    {
        var (document, plan) = House();
        var said = new List<string>();
        plan.HintChanged += (_, hint) => said.Add(hint);

        Assert.True(plan.GroundAroundBuilding());
        var ground = Assert.Single(document.Elements.OfType<Toposurface>());
        Assert.All(ground.Points, point => Assert.Equal(-150, point.Z, 6));
        Assert.True(ground.Points.Min(point => point.X) <= -PlanView.GroundMargin + 1);
        Assert.Equal("Clear of the walls, and falling away", Terrains.Verdict(document, ground));

        // Once: there is ground there now.
        Assert.False(plan.GroundAroundBuilding());
        Assert.Contains("already", said[^1]);
    });

    [Fact]
    public void PadsAndThePropertyLineAreDrawnByTheirCornersAndSpotElevationsReadTheGround() => OnUi(() =>
    {
        var (document, plan) = House();
        plan.ActiveGroundElevation = -450;
        plan.GroundAroundBuilding();

        // A patio south of the house, 200 under the floor.
        plan.SetTool(PlanTool.Pad);
        plan.ActivePadOffset = -200;
        foreach (var corner in new[] { new Point2D(0, -4000), new Point2D(6000, -4000), new Point2D(6000, -500), new Point2D(0, -500) }) plan.ClickOutline(corner);
        Assert.True(plan.FinishOutline());
        var pad = Assert.Single(document.Elements.OfType<SitePad>());
        Assert.Equal(-200, Terrains.GroundAt(document, new Point2D(3000, -2000))!.Value, 3);

        plan.SetTool(PlanTool.PropertyLine);
        plan.ActiveSetback = 3000;
        foreach (var corner in new[] { new Point2D(-8000, -10000), new Point2D(14000, -10000), new Point2D(14000, 12000), new Point2D(-8000, 12000) }) plan.ClickOutline(corner);
        Assert.True(plan.FinishOutline());
        var line = Assert.Single(document.Elements.OfType<PropertyLine>());
        Assert.Equal(3000, line.Setback);
        Assert.Equal("Within it, behind its setback", PropertyLines.Verdict(document, line));

        plan.SetTool(PlanTool.SpotElevation);
        Assert.True(plan.PlaceSpotElevation(new Point2D(3000, -2000)));
        Assert.True(plan.PlaceSpotElevation(new Point2D(-6000, 8000)));
        Assert.Equal(new[] { "PL -0.200", "GL -0.450" }, document.Elements.OfType<SpotElevation>().Select(spot => SpotElevations.Label(document, spot)));
        Assert.NotNull(pad);
    });

    [Fact]
    public void ASpotLevelIsTakenOutButThreeAreKept() => OnUi(() =>
    {
        var (document, plan) = House();
        plan.GroundAroundBuilding();
        var ground = document.Elements.OfType<Toposurface>().Single();
        plan.SetTool(PlanTool.Terrain);
        plan.ClickGround(new Point2D(3000, -8000));
        Assert.Equal(5, ground.Points.Count);

        Assert.True(plan.DeleteGroundPoint(ground, 4));
        Assert.Equal(4, ground.Points.Count);
        Assert.True(plan.DeleteGroundPoint(ground, 0));
        Assert.False(plan.DeleteGroundPoint(ground, 0));
        Assert.Equal(3, ground.Points.Count);
    });

    [Fact]
    public void TheGroundColouredBySlopeIsDrawnSo() => OnUi(() =>
    {
        var (document, plan) = House();
        var ground = new Toposurface { LevelId = document.Levels[0].Id };
        ground.SetPoints(new[] { new Point3D(-15000, -15000, -2000), new Point3D(20000, -15000, -2000), new Point3D(20000, 20000, 600), new Point3D(-15000, 20000, 600) });
        document.Add(ground);
        plan.Measure(new Size(600, 500));
        plan.Arrange(new Rect(0, 0, 600, 500));
        plan.ZoomToFit();

        byte[] Pixels()
        {
            plan.InvalidateVisual();
            plan.UpdateLayout();
            var bitmap = new RenderTargetBitmap(600, 500, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(plan);
            var pixels = new byte[600 * 500 * 4];
            bitmap.CopyPixels(pixels, 600 * 4, 0);
            return pixels;
        }

        var plain = Pixels();
        plan.GroundColouring = GroundColouring.Slope;
        Assert.NotEqual(plain, Pixels());
        Assert.Equal(6, GroundColours.Bands(document, ground, GroundColouring.Slope).Count);
        Assert.All(GroundColours.Shade(document, ground, GroundColouring.Slope), shaded => Assert.Equal(2, shaded.Band));
    });
}
