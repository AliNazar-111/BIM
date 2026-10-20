using System.Reflection;
using System.Windows;
using System.Windows.Controls.Primitives;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Sheets;
using BIMDesigner.Core.Views;
using BIMDesigner.UI;
using BIMDesigner.UI.Controls;

namespace BIMDesigner.Tests;

/// <summary>
/// The views in the main window, as a user goes through them: a camera's 3D view with its section
/// box, walking, a walkthrough, an interior elevation, a callout, each placed on a sheet, and a
/// template governing a plan.
/// </summary>
[Collection("Wpf")]
public class ViewsInTheWindowTests
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

    private static T Field<T>(MainWindow window, string name) => (T)typeof(MainWindow).GetField(name, Hidden)!.GetValue(window)!;

    private static object? Call(MainWindow window, string name, params object?[] arguments) =>
        typeof(MainWindow).GetMethods(Hidden).First(method => method.Name == name && method.GetParameters().Length == arguments.Length).Invoke(window, arguments);

    [Fact]
    public void EveryKindOfViewOpensAndGoesOnASheet()
    {
        OnUiThread(() =>
        {
            var (document, _, _) = DormerToolTests.House(10000, 8000, 40);
            var window = new MainWindow();
            Call(window, "LoadDocument", document, null, false);
            var root = (FrameworkElement)window.Content;
            root.Measure(new Size(1400, 900));
            root.Arrange(new Rect(0, 0, 1400, 900));
            root.UpdateLayout();
            var plan = Field<PlanView>(window, "Plan");
            var model = Field<ModelView>(window, "Model3D");

            // A camera, opened in 3D; its section box turned on round the model.
            plan.SetTool(PlanTool.Camera);
            Assert.True(plan.ClickCamera(new Point2D(-6000, -6000)));
            Assert.True(plan.ClickCamera(new Point2D(5000, 4000)));
            var camera = Assert.Single(document.Elements.OfType<View3D>());
            Assert.Same(camera, model.View);
            Field<ToggleButton>(window, "SectionBoxToggle").IsChecked = true;
            Call(window, "OnToggleSectionBox", window, new RoutedEventArgs());
            Assert.True(camera.SectionBoxOn);
            Assert.NotNull(camera.SectionBox);

            // Walking from there, and back.
            Assert.Null(model.StartWalking());
            Assert.True(model.IsWalking);
            model.StopWalking();

            // A walkthrough, opened on its first frame.
            plan.SetTool(PlanTool.Walkthrough);
            plan.ClickWalkthrough(new Point2D(2000, 2000));
            plan.ClickWalkthrough(new Point2D(8000, 2000));
            Assert.True(plan.FinishWalkthrough());
            Assert.Equal(Visibility.Visible, Field<FrameworkElement>(window, "WalkthroughBar").Visibility);

            // Saved as a video, every frame of it.
            var walk = Assert.Single(document.Elements.OfType<Walkthrough>());
            walk.Frames = 4;
            var path = Path.Combine(Path.GetTempPath(), $"walk-{Guid.NewGuid():N}.avi");
            var export = (Task)Call(window, "ExportWalkthrough", walk, path)!;
            while (!export.IsCompleted)
            {
                var frame = new System.Windows.Threading.DispatcherFrame();
                System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () => frame.Continue = false);
                System.Windows.Threading.Dispatcher.PushFrame(frame);
            }

            var video = File.ReadAllBytes(path);
            File.Delete(path);
            Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(video, 0, 4));
            Assert.Equal(4, BitConverter.ToInt32(video, 48));

            // An interior elevation, opened under the plan.
            plan.SetTool(PlanTool.InteriorElevation);
            Assert.True(plan.PlaceInteriorElevation(new Point2D(5000, 4000)));
            var marker = Assert.Single(document.Elements.OfType<InteriorElevationMarker>());
            Call(window, "ShowInteriorElevation", ViewReference.InteriorElevation(marker.Id, 0));
            Assert.Equal(Visibility.Visible, Field<FrameworkElement>(window, "Projected").Visibility);

            // A callout, opened in the plan.
            plan.SetTool(PlanTool.Callout);
            plan.ClickCallout(new Point2D(0, 0));
            plan.ClickCallout(new Point2D(4000, 3000));
            var callout = Assert.Single(document.Elements.OfType<Callout>());
            Call(window, "ShowCallout", callout);
            Assert.Same(callout, plan.ActiveCallout);

            // Each on a sheet, numbered there.
            var sheet = new Sheet { Number = "A-301" };
            document.Add(sheet);
            Call(window, "ShowSheet", sheet);
            foreach (var view in new[] { ViewReference.View3D(camera.Id), ViewReference.InteriorElevation(marker.Id, 0), Callouts.Reference(callout), ViewReference.Elevation(ElevationSide.South) })
            {
                Call(window, "RefreshSheetViewOptions");
                var picker = Field<System.Windows.Controls.ComboBox>(window, "SheetViewPicker");
                picker.SelectedItem = picker.Items.Cast<object>().First(option => option.GetType().GetProperty("View")!.GetValue(option) is ViewReference reference && reference == view);
                Call(window, "OnPlaceViewOnSheet", window, new RoutedEventArgs());
            }

            Assert.Equal(new[] { "1", "2", "3", "4" }, sheet.Viewports.Select(viewport => viewport.DetailNumber));
            root.UpdateLayout();
            Assert.Equal(("3", "A-301"), Callouts.PlacedOn(document, Callouts.Reference(callout)));
        });
    }
}
