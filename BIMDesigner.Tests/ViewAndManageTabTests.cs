using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using BIMDesigner.Core.Documents;
using BIMDesigner.UI;

namespace BIMDesigner.Tests;

/// <summary>
/// The View and Manage tabs as they are first used: a new sheet offers the plan being worked
/// on to place, not an empty storey listed first; and lists in the windows those tabs open are
/// in the theme's colours rather than white.
/// </summary>
[Collection("Wpf")]
public class ViewAndManageTabTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

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
        thread.Start(); thread.Join();
        if (failure is not null) throw failure;
    }

    private static object? Call(MainWindow window, string name, params object?[] arguments) =>
        typeof(MainWindow).GetMethod(name, Hidden)!.Invoke(window, arguments);

    private static T Field<T>(MainWindow window, string name) => (T)typeof(MainWindow).GetField(name, Hidden)!.GetValue(window)!;

    [Fact]
    public void ANewSheetOffersThePlanBeingWorkedOn()
    {
        OnUi(() =>
        {
            var (document, _, _) = DormerToolTests.House(10000, 8000, 40);
            var window = new MainWindow();
            Call(window, "LoadDocument", document, null, false);

            Call(window, "OnNewSheet", window, new RoutedEventArgs());
            var picked = Field<ComboBox>(window, "SheetViewPicker").SelectedItem!;
            Assert.Equal($"{document.Levels[0].Name} Plan", picked.GetType().GetProperty("Display")!.GetValue(picked));
        });
    }

    [Fact]
    public void ListsAreInTheThemesColours()
    {
        OnUi(() =>
        {
            var list = new ListBox();
            var style = Assert.IsType<Style>(Application.Current.TryFindResource(typeof(ListBox)));
            Assert.Contains(style.Setters.OfType<Setter>(), setter => setter.Property == Control.BackgroundProperty);
            Assert.IsType<Style>(Application.Current.TryFindResource(typeof(ListBoxItem)));
        });
    }
}
