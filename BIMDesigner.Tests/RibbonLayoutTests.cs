using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.UI;
using BIMDesigner.UI.Controls;

namespace BIMDesigner.Tests;

/// <summary>
/// The ribbon on a laptop's 1366-wide screen: the main tabs fit it whole, a selected wall's tab
/// has its framing within it, and a tab too wide for the window scrolls instead of being cut off.
/// </summary>
[Collection("Wpf")]
public class RibbonLayoutTests
{
    private const double Laptop = 1366;

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

    [Fact]
    public void EveryMainTabFitsALaptopScreenAndAWallsTabHasItsFramingOnIt()
    {
        OnUiThread(() =>
        {
            var window = new MainWindow();
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(MainWindow).GetMethod("LoadDocument", flags)!.Invoke(window, new object?[] { BimDocument.CreateDefault(), null, false });
            var root = (FrameworkElement)window.Content;
            var ribbon = (TabControl)typeof(MainWindow).GetField("Ribbon", flags)!.GetValue(window)!;
            var plan = (PlanView)typeof(MainWindow).GetField("Plan", flags)!.GetValue(window)!;

            void Layout()
            {
                root.Measure(new Size(Laptop, 740));
                root.Arrange(new Rect(0, 0, Laptop, 740));
                root.UpdateLayout();
            }

            foreach (var tab in ribbon.Items.OfType<TabItem>().Where(tab => tab.Visibility == Visibility.Visible))
            {
                ribbon.SelectedItem = tab;
                Layout();
                var panels = (FrameworkElement)tab.Content;
                panels.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Assert.True(panels.DesiredSize.Width <= Laptop, $"{tab.Header} needs {panels.DesiredSize.Width:0} px of the {Laptop}");

                // Every tool on it there to be seen: none stacked below where its panel is cut off.
                var host = (FrameworkElement)ribbon.Template.FindName("RibbonScroll", ribbon);
                var bottom = host.TransformToAncestor(ribbon).Transform(new Point(0, host.ActualHeight)).Y;
                foreach (var tool in Descendants(panels).OfType<System.Windows.Controls.Primitives.ButtonBase>().Where(tool => Shown(tool, panels)))
                {
                    var below = tool.TransformToAncestor(ribbon).Transform(new Point(0, tool.ActualHeight)).Y;
                    Assert.True(below <= bottom + 0.5, $"{tab.Header}: {tool.Content} is cut off, {below - bottom:0} px below the ribbon");
                }
            }

            // A wall picked: its own tab comes forward, Frame Walls on it within the screen.
            var document = plan.Document!;
            var wall = new Wall { Start = new Point2D(0, 0), End = new Point2D(4000, 0), TypeId = document.TypesOf<WallType>().First().Id, LevelId = plan.ActiveLevelId };
            document.Add(wall);
            plan.Select(wall);
            Layout();
            var context = (TabItem)ribbon.SelectedItem;
            Assert.StartsWith("Modify | Walls", (string)context.Header);
            var frameWalls = (Button)typeof(MainWindow).GetField("ContextFrameWalls", flags)!.GetValue(window)!;
            var right = frameWalls.TransformToAncestor((System.Windows.Media.Visual)context.Content).Transform(new Point(frameWalls.ActualWidth, 0)).X;
            Assert.True(right <= Laptop - 30, $"Frame Walls ends {right:0} px along the tab");

            // It is wider than the screen: it scrolls, rather than losing what is past the edge.
            var scroller = (RibbonScroller)ribbon.Template.FindName("RibbonScroll", ribbon);
            Assert.True(scroller.Overflows);
        });
    }

    /// <summary>Whether something is meant to be seen: it and all round it on the tab not hidden.</summary>
    private static bool Shown(DependencyObject element, DependencyObject tab)
    {
        for (var at = element; at is not null && !ReferenceEquals(at, tab); at = System.Windows.Media.VisualTreeHelper.GetParent(at))
            if (at is UIElement { Visibility: not Visibility.Visible }) return false;
        return true;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var below in Descendants(child)) yield return below;
        }
    }
}
