using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Sheets;
using BIMDesigner.Core.Views;
using BIMDesigner.Infrastructure.Serialization;

namespace BIMDesigner.Tests;

/// <summary>
/// View templates: made from a view, applied to another once, or governing views - what they
/// include set by them alone, and a change to them reaching every view they govern.
/// </summary>
public class ViewTemplateTests
{
    [Fact]
    public void ATemplateIsMadeFromAViewAndAppliedToAnother()
    {
        var document = BimDocument.CreateDefault();
        var (ground, first) = (ViewReference.FloorPlan(document.Levels[0].Id), ViewReference.FloorPlan(document.Levels[1].Id));
        document.ViewSettings.SetScale(ground, 50);
        document.ViewSettings.SetDetailLevel(ground, DetailLevel.Coarse);
        document.ViewSettings.SetWallFunctionVisible(ground, WallFunction.Interior, false);

        var template = ViewTemplates.FromView(document, ground)!;
        Assert.Equal((ViewTemplateKind.Plan, 50.0, DetailLevel.Coarse), (template.For, template.Scale, template.DetailLevel));
        Assert.Contains(WallFunction.Interior, template.HiddenWallFunctions);
        document.Add(template);

        // Applied once: the first floor is set like the ground, and governed by nothing.
        var history = new UndoStack();
        history.Execute(ViewTemplates.Apply(document, template, first, govern: false));
        Assert.Equal(50, document.ViewSettings.ScaleOf(first));
        Assert.Equal(DetailLevel.Coarse, document.ViewSettings.DetailLevelOf(first));
        Assert.False(document.ViewSettings.IsWallFunctionVisible(first, WallFunction.Interior));
        Assert.Null(ViewTemplates.Of(document, first));

        history.Undo();
        Assert.Equal(ViewSettings.DefaultScale, document.ViewSettings.ScaleOf(first));
        Assert.True(document.ViewSettings.IsWallFunctionVisible(first, WallFunction.Interior));
        Assert.Empty(ViewTemplates.For(document, ViewReference.Model3D));
    }

    [Fact]
    public void ATemplateGovernsItsViewsAndAChangeToItReachesThemAll()
    {
        var document = BimDocument.CreateDefault();
        var template = new ViewTemplate { Name = "Working plans", For = ViewTemplateKind.Plan, Scale = 100, DetailLevel = DetailLevel.Medium, IncludeDetailLevel = false };
        document.Add(template);
        var views = document.Levels.Select(level => ViewReference.FloorPlan(level.Id)).ToList();
        var history = new UndoStack();
        foreach (var view in views) history.Execute(ViewTemplates.Apply(document, template, view, govern: true));

        // What it includes is locked in the views; what it leaves out is theirs.
        Assert.All(views, view => Assert.True(ViewTemplates.Locks(document, view, ViewTemplateSetting.Scale)));
        Assert.False(ViewTemplates.Locks(document, views[0], ViewTemplateSetting.DetailLevel));
        Assert.Equal(views.Count, ViewTemplates.Governed(document, template).Count());

        // A change to it reaches every view - and is undone in them all with it.
        history.Execute(ViewTemplates.Change(document, template, t => t.Scale = 200, "View Template Scale"));
        Assert.All(views, view => Assert.Equal(200, document.ViewSettings.ScaleOf(view)));
        history.Undo();
        Assert.Equal(100, template.Scale);
        Assert.All(views, view => Assert.Equal(100, document.ViewSettings.ScaleOf(view)));

        // Saved, it governs them still.
        var reopened = ProjectFile.FromJson(ProjectFile.ToJson(document));
        var kept = Assert.Single(reopened.Elements.OfType<ViewTemplate>());
        Assert.Equal(("Working plans", false), (kept.Name, kept.IncludeDetailLevel));
        Assert.Equal(kept.Id, reopened.ViewSettings.TemplateOf(views[0]));

        // Let go: governed by nothing, its settings kept.
        history.Execute(ViewTemplates.Apply(document, null, views[0], govern: true));
        Assert.Null(ViewTemplates.Of(document, views[0]));
        Assert.Equal(100, document.ViewSettings.ScaleOf(views[0]));
    }

    [Fact]
    public void A3DTemplateSetsItsStyleAndWhatItLeavesOut()
    {
        var document = BimDocument.CreateDefault();
        var template = new ViewTemplate { For = ViewTemplateKind.ThreeD, VisualStyle = VisualStyle.Wireframe };
        template.HiddenKinds.Add(MeshKind.Roof);
        document.Add(template);
        Assert.False(template.Includes(ViewTemplateSetting.Scale));

        var camera = Views3D.Camera(document, document.Levels[0].Id, new Point2D(0, 0), new Point2D(0, 5000));
        document.Add(camera);
        var view = ViewReference.View3D(camera.Id);
        Assert.Same(template, Assert.Single(ViewTemplates.For(document, view)));

        var history = new UndoStack();
        history.Execute(ViewTemplates.Apply(document, template, view, govern: true));
        Assert.Equal(VisualStyle.Wireframe, camera.Style);
        Assert.Contains(MeshKind.Roof, camera.HiddenKinds);
        Assert.True(ViewTemplates.Locks(document, view, ViewTemplateSetting.VisualStyle));

        history.Undo();
        Assert.Equal(VisualStyle.Shaded, camera.Style);
        Assert.Empty(camera.HiddenKinds);
    }
}
