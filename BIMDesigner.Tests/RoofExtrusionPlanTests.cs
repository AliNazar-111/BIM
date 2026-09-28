using System.Windows;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;
using BIMDesigner.UI;
using BIMDesigner.UI.Controls;

namespace BIMDesigner.Tests;

/// <summary>
/// Roof by Extrusion on the plan, driven the way a user drives it: three clicks say where the
/// profile's line runs and how far the roof runs back from it, the profile drawn makes the
/// roof, and Edit Profile changes it - each one step to undo.
/// </summary>
[Collection("Wpf")]
public class RoofExtrusionPlanTests
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

    /// <summary>A 10 m x 6 m box of exterior walls reaching the first floor, and a plan on it.</summary>
    private static (BimDocument Document, PlanView Plan) Box()
    {
        var document = BimDocument.CreateDefault();
        var type = document.TypesOf<WallType>().First(t => t.Name.StartsWith("Exterior"));
        var ground = document.Levels[0];
        var first = document.Levels[1];

        var corners = new[]
        {
            new Point2D(0, 0), new Point2D(10000, 0), new Point2D(10000, 6000), new Point2D(0, 6000)
        };

        for (var i = 0; i < corners.Length; i++)
        {
            document.Add(new Wall
            {
                Start = corners[i], End = corners[(i + 1) % corners.Length],
                TypeId = type.Id, LevelId = ground.Id, TopLevelId = first.Id
            });
        }

        var plan = new PlanView
        {
            Document = document,
            ActiveLevelId = ground.Id,
            History = new UndoStack(),
            ActiveRoofTypeId = document.TypesOf<RoofType>().First().Id
        };

        plan.SetTool(PlanTool.RoofExtrusion);
        return (document, plan);
    }

    /// <summary>
    /// The profile's line up the 6 m east wall, and the roof run back 10 m over the box to
    /// the west wall.
    /// </summary>
    private static RoofExtrusionRequest? ClickOut(PlanView plan)
    {
        RoofExtrusionRequest? placed = null;
        plan.ExtrusionPlaced += (_, request) => placed = request;

        Assert.True(plan.ExtrusionClick(new Point2D(10000, 0)));
        Assert.True(plan.ExtrusionClick(new Point2D(10000, 6000)));
        Assert.Null(placed);
        Assert.True(plan.ExtrusionClick(new Point2D(0, 3000)));

        return placed;
    }

    [Fact]
    public void ThreeClicksSayWhereTheProfileGoesAndHowFarTheRoofRuns()
    {
        OnUiThread(() =>
        {
            var (_, plan) = Box();
            var request = ClickOut(plan);

            Assert.NotNull(request);
            Assert.Equal(new Point2D(10000, 0), request!.Origin);
            Assert.Equal(6000, request.Width, precision: 6);
            Assert.Equal(10000, request.End - request.Start, precision: 6);

            // The roof run back is the box: its far side lands on the west wall.
            var across = request.Direction.PerpendicularLeft();
            var far = request.Start < 0 ? request.Start : request.End;
            Assert.Equal(0, (request.Origin + across * far).X, precision: 6);

            // With nothing said, it would sit on the tops of the walls it covers.
            Assert.Equal(3000, request.BaseOffset, precision: 6);
        });
    }

    [Fact]
    public void ALineTooShortOrARoofWithNoDepthIsRefused()
    {
        OnUiThread(() =>
        {
            var (_, plan) = Box();
            RoofExtrusionRequest? placed = null;
            plan.ExtrusionPlaced += (_, request) => placed = request;

            Assert.True(plan.ExtrusionClick(new Point2D(10000, 0)));
            Assert.False(plan.ExtrusionClick(new Point2D(10000, 0)));
            Assert.True(plan.ExtrusionClick(new Point2D(10000, 6000)));

            // A third click on the line itself gives the roof nowhere to run.
            Assert.False(plan.ExtrusionClick(new Point2D(10000, 3000)));
            Assert.Null(placed);
        });
    }

    [Fact]
    public void TheDrawnProfileMakesTheRoofAsOneStep()
    {
        OnUiThread(() =>
        {
            var (document, plan) = Box();
            var request = ClickOut(plan)!;

            var roof = plan.PlaceExtrusionRoof(
                new RoofExtrusion(request.Origin, request.Direction,
                    RoofExtrusion.Preset(RoofForm.Gambrel, request.Width, 2500), request.Start, request.End),
                request.BaseOffset);

            Assert.NotNull(roof);
            Assert.Contains(roof, document.Elements);
            Assert.Equal(roof, Assert.Single(plan.SelectedElements));
            Assert.True(roof!.IsExtrusion);
            Assert.Equal(RoofForm.Gambrel, roof.Form);
            Assert.Equal(60e6, roof.Area, precision: 0);

            // Its eaves stand on the long walls, at their tops; the crown is the rise above.
            Assert.Equal(3000, roof.Surface(document).HeightAt(new Point2D(5000, 0)), precision: 3);
            Assert.Equal(3000, roof.Surface(document).HeightAt(new Point2D(5000, 6000)), precision: 3);
            Assert.Equal(5500, roof.Surface(document).HeightAt(new Point2D(5000, 3000)), precision: 3);

            plan.History!.Undo();
            Assert.DoesNotContain(roof, document.Elements);
        });
    }

    [Fact]
    public void AProfileThatCannotRoofAnythingMakesNothing()
    {
        OnUiThread(() =>
        {
            var (document, plan) = Box();
            var request = ClickOut(plan)!;

            var upright = new[] { new Point2D(0, 0), new Point2D(0, 2000), new Point2D(6000, 2000) };
            Assert.Null(plan.PlaceExtrusionRoof(
                new RoofExtrusion(request.Origin, request.Direction, upright, request.Start, request.End), 3000));
            Assert.Empty(document.Elements.OfType<Roof>());
        });
    }

    [Fact]
    public void EditingTheProfileAndBaseIsOneStepToUndo()
    {
        OnUiThread(() =>
        {
            var (document, plan) = Box();
            var request = ClickOut(plan)!;
            var gable = new RoofExtrusion(request.Origin, request.Direction,
                RoofExtrusion.Preset(RoofForm.Gable, request.Width, 1700), request.Start, request.End);
            var roof = plan.PlaceExtrusionRoof(gable, request.BaseOffset)!;

            Assert.True(plan.ChangeExtrusion(roof, gable.With(profile: RoofExtrusion.Preset(RoofForm.Barrel, 6000, 2000)), 3200));
            Assert.Equal(RoofForm.Barrel, roof.Form);
            Assert.Equal(3200, roof.HeightOffset, precision: 6);

            plan.History!.Undo();
            Assert.Equal(RoofForm.Gable, roof.Form);
            Assert.Equal(3000, roof.HeightOffset, precision: 6);
            Assert.Contains(roof, document.Elements);
        });
    }

    [Fact]
    public void EditProfileRedrawsOverTheBuildingNotTheEaves()
    {
        OnUiThread(() =>
        {
            var (_, plan) = Box();
            var request = ClickOut(plan)!;

            // Eaves carried 600 mm past the walls each side: the profile runs from -600 to
            // 6600, but the building under it is still the 6 m between the walls.
            var roof = plan.PlaceExtrusionRoof(
                new RoofExtrusion(request.Origin, request.Direction,
                    RoofExtrusion.Preset(RoofForm.Gable, 6000, 1700, overhang: 600), request.Start, request.End),
                request.BaseOffset)!;

            var (start, width) = plan.SpanUnder(roof);
            Assert.Equal(0, start, precision: 6);
            Assert.Equal(6000, width, precision: 6);
        });
    }
}
