using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Sheets;
using BIMDesigner.Core.Views;
using BIMDesigner.Infrastructure.Serialization;

namespace BIMDesigner.Tests;

/// <summary>
/// Callouts: part of a plan or section drawn larger as a view of its own, cropped to its box, at
/// its own scale and detail - its tag quoting the sheet it is placed on.
/// </summary>
public class CalloutTests
{
    [Fact]
    public void ACalloutIsAViewOfItsBoxAtItsOwnScaleAndDetail()
    {
        var document = BimDocument.CreateDefault();
        var plan = ViewReference.FloorPlan(document.Levels[0].Id);
        var (callout, command) = Callouts.Draw(document, plan, document.Levels[0].Id, new Point2D(4000, 3000), new Point2D(1000, 1000));
        var history = new UndoStack();
        history.Execute(command);

        var view = Callouts.Reference(callout);
        Assert.Equal(new BoundingBox2D(1000, 1000, 4000, 3000), callout.Region);
        Assert.Equal("Callout 1", view.TitleIn(document));
        Assert.True(view.ExistsIn(document));
        Assert.Equal(Callouts.PlanScale, document.ViewSettings.ScaleOf(view));
        Assert.Equal(DetailLevel.Fine, document.ViewSettings.DetailLevelOf(view));
        Assert.Equal(callout.Region, ViewExtent.Of(document, view));
        Assert.Single(Callouts.On(document, plan));

        // On a sheet, it is numbered there, and its tag quotes it.
        Assert.Null(Callouts.PlacedOn(document, view));
        var sheet = new Sheet { Number = "A-201" };
        sheet.Add(new Viewport { View = ViewReference.FloorPlan(document.Levels[0].Id) });
        sheet.Add(new Viewport { View = view });
        document.Add(sheet);
        Assert.Equal(("2", "A-201"), Callouts.PlacedOn(document, view));

        // Undone, the callout and its view's settings go together.
        document.Remove(sheet);
        history.Undo();
        Assert.Empty(document.Elements.OfType<Callout>());
        Assert.Null(document.ViewSettings.DetailLevelOf(view));
    }

    [Fact]
    public void ItsBoxIsCroppedByItsCornersAndKept()
    {
        var document = BimDocument.CreateDefault();
        var level = document.Levels[0].Id;
        var (callout, command) = Callouts.Draw(document, ViewReference.FloorPlan(level), level, new Point2D(0, 0), new Point2D(3000, 2000));
        command.Redo();

        // Its top right corner dragged out; dragged past the corner across, it stops a callout's least short of it.
        var handle = ElementHandles.Of(document, callout).Single(h => h.At == new Point2D(3000, 2000));
        ElementHandles.Drag(document, callout, handle, new Point2D(5000, 4000), 0);
        Assert.Equal(new BoundingBox2D(0, 0, 5000, 4000), callout.Region);
        handle = ElementHandles.Of(document, callout).Single(h => h.At == new Point2D(5000, 4000));
        ElementHandles.Drag(document, callout, handle, new Point2D(50, 50), 0);
        Assert.Equal(new BoundingBox2D(0, 0, Callouts.Smallest, Callouts.Smallest), callout.Region);

        // Moved with the plan.
        ElementTransforms.Move(callout, new Vector2D(1000, 0));
        Assert.Equal(1000, callout.Region.MinX);

        // A section's callout is a detail, at 1:20.
        var marker = new SectionMarker { LevelId = level, Start = new Point2D(0, -1000), End = new Point2D(0, 6000) };
        document.Add(marker);
        var (detail, made) = Callouts.Draw(document, ViewReference.Section(marker.Id), level, new Point2D(0, 2500), new Point2D(1500, 3500));
        made.Redo();
        Assert.Equal(Callouts.SectionScale, document.ViewSettings.ScaleOf(Callouts.Reference(detail)));

        var reopened = ProjectFile.FromJson(ProjectFile.ToJson(document));
        var kept = reopened.Elements.OfType<Callout>().OrderBy(c => c.Name).ToList();
        Assert.Equal(2, kept.Count);
        Assert.Equal((callout.Name, callout.Region, callout.Parent), (kept[0].Name, kept[0].Region, kept[0].Parent));
        Assert.Equal(detail.Parent, kept[1].Parent);
        Assert.Equal(DetailLevel.Fine, reopened.ViewSettings.DetailLevelOf(Callouts.Reference(kept[1])));
        Assert.Equal(Callouts.SectionScale, reopened.ViewSettings.ScaleOf(Callouts.Reference(kept[1])));
    }
}

/// <summary>Callouts drawn and opened on the plan, as a user does it.</summary>
[Collection("Wpf")]
public class CalloutPlanTests
{
    [Fact]
    public void TwoClicksDrawACalloutAndThePlanShowsItCropped()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (System.Windows.Application.Current is null) new BIMDesigner.UI.App().InitializeComponent();
                var document = BimDocument.CreateDefault();
                var plan = new BIMDesigner.UI.Controls.PlanView { Document = document, ActiveLevelId = document.Levels[0].Id, History = new UndoStack() };

                plan.SetTool(BIMDesigner.UI.Controls.PlanTool.Callout);
                Assert.True(plan.ClickCallout(new Point2D(0, 0)));
                Assert.False(plan.ClickCallout(new Point2D(50, 50)));
                Assert.True(plan.ClickCallout(new Point2D(4000, 3000)));
                var callout = Assert.Single(document.Elements.OfType<Callout>());

                // Opened, the plan is the callout's view, in its own detail; its own detail level set, the plan's is not.
                plan.ActiveCallout = callout;
                Assert.Equal(Callouts.Reference(callout), plan.CurrentView);
                document.ViewSettings.SetDetailLevel(Callouts.Reference(callout), DetailLevel.Medium);
                Assert.Equal(DetailLevel.Medium, plan.EffectiveDetailLevel);
                plan.ActiveCallout = null;
                Assert.Equal(DetailLevel.Fine, plan.EffectiveDetailLevel);

                // Another storey is another plan: the callout is put away.
                plan.ActiveCallout = callout;
                plan.ActiveLevelId = document.Levels[1].Id;
                Assert.Null(plan.ActiveCallout);
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Exception("The plan failed on the UI thread.", failure);
    }
}
