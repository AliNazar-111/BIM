using System.Reflection;
using System.Windows;
using System.Windows.Media;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Parameters;
using BIMDesigner.Core.Schedules;
using BIMDesigner.Core.Sheets;
using BIMDesigner.Core.Views;
using BIMDesigner.UI;
using BIMDesigner.UI.Controls;
using BIMDesigner.UI.Rendering;

namespace BIMDesigner.Tests;

/// <summary>Phasing, filters, design options, parameters and classification as they are used in the window - and drawn.</summary>
[Collection("Wpf")]
public class ProjectDataWindowTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

    private static object? Call(MainWindow window, string name, params object?[] arguments) =>
        typeof(MainWindow).GetMethods(Hidden).First(method => method.Name == name && method.GetParameters().Length == arguments.Length).Invoke(window, arguments);

    private static T Field<T>(MainWindow window, string name) => (T)typeof(MainWindow).GetField(name, Hidden)!.GetValue(window)!;

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
        if (failure is not null) throw new Exception("The window failed on the UI thread.", failure);
    }

    /// <summary>Every drawing in a tree, with the opacity it is drawn at.</summary>
    private static IEnumerable<(Drawing Drawing, double Opacity)> Walk(Drawing drawing, double opacity = 1)
    {
        yield return (drawing, opacity);
        if (drawing is DrawingGroup group)
            foreach (var child in group.Children)
                foreach (var inner in Walk(child, opacity * group.Opacity)) yield return inner;
    }

    [Fact]
    public void ThePlanDrawsWhatIsThereFadedWhatGoesRedAndDashed()
    {
        OnUiThread(() =>
        {
            var document = BimDocument.CreateDefault();
            var type = document.TypesOf<WallType>().First(t => t.Function == WallFunction.Exterior);
            Wall Wall(double y, DesignPhase created, DesignPhase? gone = null)
            {
                var wall = new Wall { Start = new Point2D(0, y), End = new Point2D(5000, y), TypeId = type.Id, LevelId = document.Levels[0].Id, UnconnectedHeight = 3000, PhaseCreated = created, PhaseDemolished = gone };
                document.Add(wall);
                return wall;
            }

            Wall(0, DesignPhase.Existing);
            Wall(3000, DesignPhase.Existing, DesignPhase.New);
            Wall(6000, DesignPhase.New);
            var plan = ViewReference.FloorPlan(document.Levels[0].Id);
            var renderer = new PlanRenderer(DrawingPalette.Paper)
            {
                Document = document, ActiveLevelId = document.Levels[0].Id, View = plan,
                Filter = document.ViewSettings.FilterFor(document, plan), Style = document.ViewSettings.StyleFor(document, plan)
            };
            renderer.SetTransform(p => new Point(p.X / 20, -p.Y / 20), 1 / 20.0);
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen()) renderer.DrawContent(dc);
            var drawn = Walk(visual.Drawing).ToList();

            Assert.Contains(drawn, entry => entry.Drawing is DrawingGroup { Opacity: < 0.5 });
            Assert.Contains(drawn, entry => entry.Drawing is GeometryDrawing { Pen: { DashStyle.Dashes.Count: > 0, Brush: SolidColorBrush red } } && red.Color.R == Phasing.DemolishedColour.R && red.Color.G == Phasing.DemolishedColour.G);

            // A demolition plan draws no new work.
            document.ViewSettings.SetPhaseFilter(plan, PhaseFilter.ShowPreviousAndDemolition);
            renderer.Filter = document.ViewSettings.FilterFor(document, plan);
            var before = drawn.Count;
            using (var dc = visual.RenderOpen()) renderer.DrawContent(dc);
            Assert.True(Walk(visual.Drawing).Count() < before);
        });
    }

    [Fact]
    public void FiltersOptionsParametersAndClassificationAreEditedInTheirWindows()
    {
        OnUiThread(() =>
        {
            var (document, _, _) = DormerToolTests.House(10000, 8000, 40);
            var window = new MainWindow();
            Call(window, "LoadDocument", document, null, false);
            var history = Field<UndoStack>(window, "_history");
            var plan = Field<PlanView>(window, "Plan");
            var view = ViewReference.FloorPlan(document.Levels[0].Id);

            // A filter, made and added to the plan in red.
            Assert.True((bool)Call(window, "EditViewFilters", (Func<ViewFiltersWindow, bool>)(editor =>
            {
                var filter = editor.Add();
                filter.Categories.Add(BuiltInCategory.Walls);
                filter.Rules.Add(new ScheduleFilter("Function", ScheduleOperator.Equals, "Exterior"));
                return true;
            }))!);
            var made = Assert.Single(document.ParameterFilters);
            Assert.Equal(made.Id, Assert.Single(document.ViewSettings.FiltersOf(view)).FilterId);
            Assert.NotNull(document.ViewSettings.StyleFor(document, view)(document.Walls.First()).Colour);
            history.Undo();
            Assert.Empty(document.ParameterFilters);
            Assert.Empty(document.ViewSettings.FiltersOf(view));

            // A set of options, the second worked on: a wall drawn now is in it, and not shown till the plan shows it.
            Assert.True((bool)Call(window, "EditDesignOptions", (Func<DesignOptionsWindow, bool>)(editor =>
            {
                var set = editor.AddSet("Extension");
                editor.Edit(editor.AddOption(set).Id);
                return true;
            }))!);
            var options = Assert.Single(document.DesignOptionSets);
            Assert.Equal(options.Options[1].Id, document.EditingDesignOption);
            plan.SetTool(PlanTool.Wall);
            var type = document.TypesOf<WallType>().First();
            var wall = new Wall { Start = new Point2D(0, 12000), End = new Point2D(4000, 12000), TypeId = type.Id, LevelId = document.Levels[0].Id, UnconnectedHeight = 3000 };
            document.Add(wall);
            Assert.Equal(options.Options[1].Id, wall.DesignOptionId);
            Assert.False(document.ViewSettings.FilterFor(document, view)(wall));

            // Shown in the plan, picked in its Properties.
            var phasing = (IEnumerable<ParameterValue>)Call(window, "PhasingParameters", view)!;
            var shown = phasing.Single(p => p.Name == "Design Option: Extension 1");
            Assert.True(shown.TrySetFromText("Option 2"));
            Assert.True(document.ViewSettings.FilterFor(document, view)(wall));
            var filter = ((IEnumerable<ParameterValue>)Call(window, "PhasingParameters", view)!).Single(p => p.Name == "Phase Filter");
            Assert.True(filter.TrySetFromText("Show Complete"));
            Assert.Equal(PhaseFilter.ShowComplete, document.ViewSettings.PhaseFilterOf(view));

            // A parameter of the project's own, on walls; then the classification, from a file.
            Assert.True((bool)Call(window, "EditProjectParameters", (Func<ProjectParametersWindow, bool>)(editor =>
            {
                editor.Add("Acoustic Note").Categories.Add(BuiltInCategory.Walls);
                return editor.Problems() is null;
            }))!);
            Assert.Contains(document.Walls.First().GetInstanceParameters(document), p => p.Name == "Acoustic Note");
            Assert.True((bool)Call(window, "EditClassification", (Func<ClassificationWindow, bool>)(editor =>
            {
                editor.Load("Code\tTitle\nPr_20_93_96\tTimber windows\n");
                return true;
            }))!);
            Assert.Equal("Pr_20_93_96", Assert.Single(document.Classification.Entries).Code);
            history.Undo();
            Assert.Contains(document.Classification.Entries, entry => entry.Code == "EF_25_10_25");
            history.Undo();
            Assert.Empty(document.ProjectParameters);
        });
    }
}
