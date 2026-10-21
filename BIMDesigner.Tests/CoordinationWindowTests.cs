using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Coordination;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;
using BIMDesigner.UI;
using BIMDesigner.UI.Controls;

namespace BIMDesigner.Tests;

/// <summary>The coordination windows: Clash Detective, the construction programme and the cost plan, beside the model.</summary>
[Collection("Wpf")]
public class CoordinationWindowTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

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

    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, Hidden)!.GetValue(owner)!;

    private static void Call(object owner, string name, params object?[] arguments) =>
        owner.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(owner, arguments);

    /// <summary>The house with its desk pushed into the front wall, open in the main window.</summary>
    private static (MainWindow Window, BimDocument Document) Opened()
    {
        var document = ConstructionTests.House();
        document.Elements.OfType<Component>().Single().Location = new Point2D(3000, 200);
        var window = new MainWindow();
        Call(window, "LoadDocument", document, null, false);
        return (window, Field<PlanView>(window, "Plan").Document!);
    }

    [Fact]
    public void ClashDetectiveRunsTheTestsAndEachClashIsAnIssueToUndo()
    {
        OnUiThread(() =>
        {
            var (main, document) = Opened();
            Call(main, "OnClashDetective", main, new RoutedEventArgs());
            var window = Field<System.Windows.Window>(main, "_clashWindow");
            Call(window, "Run", true);

            var test = document.ClashTests.Single(each => each.Name.Contains("Fittings and furniture through walls"));
            var clash = Assert.Single(test.Results);
            var history = Field<UndoStack>(main, "_history");
            Assert.True(history.IsModified);

            // Picked in the list, given a status: one step, undone.
            Field<ListBox>(window, "_tests").SelectedIndex = document.ClashTests.IndexOf(test);
            var results = Field<DataGrid>(window, "_results");
            results.SelectedIndex = 0;
            var issue = Field<StackPanel>(window, "_issue");
            var status = issue.Children.OfType<WrapPanel>().First().Children.OfType<ComboBox>().First();
            status.SelectedItem = "Approved";
            Assert.Equal(ClashStatus.Approved, document.ClashTests.Single(each => each.Id == test.Id).Results.Single().Status);
            history.Undo();
            Assert.Equal(ClashStatus.New, document.ClashTests.Single(each => each.Id == test.Id).Results.Single().Status);

            // Shown: both elements selected.
            var host = typeof(MainWindow).GetInterfaces().Single(each => each.Name == "ICoordinationHost");
            host.GetMethod("ShowClash")!.Invoke(main, new object[] { clash });
            Assert.Equal(new[] { clash.ElementA, clash.ElementB }.OrderBy(id => id), Field<PlanView>(main, "Plan").SelectedElements.Select(element => element.Id).OrderBy(id => id));
            window.Close();
        });
    }

    [Fact]
    public void TheProgrammeIsMadeFromTheModelAndPlayedInThe3DView()
    {
        OnUiThread(() =>
        {
            var (main, document) = Opened();
            Call(main, "OnConstructionProgramme", main, new RoutedEventArgs());
            var window = Field<System.Windows.Window>(main, "_programmeWindow");
            Call(window, "Generate");
            Assert.NotEmpty(document.Construction.Tasks);
            Assert.Equal(document.Construction.Tasks.Count, ((System.Collections.IList)Field<DataGrid>(window, "_grid").ItemsSource).Count);

            // The second day: the 3D view shows the building as it stands then.
            var slider = Field<Slider>(window, "_slider");
            slider.Value = 1;
            var model = Field<ModelView>(main, "Model3D");
            Assert.Equal(ConstructionPlanning.DateOf(document.Construction, 1), model.ConstructionDate);

            // Closed: the whole model again.
            window.Close();
            Assert.Null(model.ConstructionDate);
        });
    }

    [Fact]
    public void TheCostPlanShowsTheModelCostedAndItsAdditionsChange()
    {
        OnUiThread(() =>
        {
            var (main, document) = Opened();
            Call(main, "OnCostPlan", main, new RoutedEventArgs());
            var window = Field<System.Windows.Window>(main, "_costWindow");
            Assert.True(((System.Collections.IList)Field<DataGrid>(window, "_plan").ItemsSource).Count > 5);

            var settings = Field<StackPanel>(window, "_settings");
            var prelims = settings.Children.OfType<TextBox>().First();
            prelims.Text = "15";
            prelims.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
            Assert.Equal(15, document.CostSettings.Preliminaries);
            Field<UndoStack>(main, "_history").Undo();
            Assert.Equal(12, document.CostSettings.Preliminaries);
            window.Close();
        });
    }

    [Fact]
    public void OnADateTheModelShowsWhatIsBuiltAndWhatIsGoingUp()
    {
        OnUiThread(() =>
        {
            var document = ConstructionTests.House();
            ConstructionPlanning.Generate(document, new DateOnly(2026, 11, 2));
            var view = new ModelView { Width = 400, Height = 300, Document = document };
            view.Measure(new Size(400, 300));
            view.Arrange(new Rect(0, 0, 400, 300));

            IReadOnlyList<Mesh3D> Meshes() => Field<IReadOnlyList<Mesh3D>>(view, "_allMeshes");
            var walls = document.Walls.Where(wall => wall.LevelId == document.Levels[1].Id).Select(wall => wall.Id).ToHashSet();
            Assert.Contains(Meshes(), mesh => walls.Contains(mesh.OwnerId));

            // The first day: only the footings and slab, going up green.
            var slab = document.Construction.Tasks.Single(task => task.Name.EndsWith("Ground slab"));
            view.ConstructionDate = ConstructionPlanning.Time(document.Construction).Single(timed => timed.Task == slab).Start;
            Assert.DoesNotContain(Meshes(), mesh => walls.Contains(mesh.OwnerId));
            var floor = document.Elements.OfType<Floor>().Single(each => each.LevelId == document.Levels[0].Id);
            Assert.All(Meshes().Where(mesh => mesh.OwnerId == floor.Id), mesh => Assert.Equal(ModelView.BuildingColour, mesh.Colour));

            view.ConstructionDate = null;
            Assert.Contains(Meshes(), mesh => walls.Contains(mesh.OwnerId));
        });
    }
}
