using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.UI;
using BIMDesigner.UI.ViewModels;

using Window = System.Windows.Window;

namespace BIMDesigner.Tests;

/// <summary>
/// Drives the wall type editor the way a user would: duplicate a type, rename it, add a layer,
/// apply. Mostly this proves the window builds at all - a XAML mistake only shows when the
/// window is created - and that its buttons reach the model through the undo stack.
/// </summary>
[Collection("Wpf")]
public class WallTypesWindowTests
{
    private static void OnUiThread(Action action)
    {
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                // The window's colours and control templates come from the application's theme.
                if (Application.Current is null) new App().InitializeComponent();
                action();
            }
            catch (Exception exception) { failure = exception; }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null) throw new Exception("The editor failed on the UI thread.", failure);
    }

    private static void Click(Window window, string handler) =>
        window.GetType()
            .GetMethod(handler, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, new object?[] { window, new RoutedEventArgs() });

    [Fact]
    public void DuplicatingRenamingAndAddingALayerReachesTheModelAsUndoableSteps()
    {
        OnUiThread(() =>
        {
            var document = BimDocument.CreateDefault();
            var history = new UndoStack();
            var exterior = document.TypesOf<WallType>().Single(t => t.Name.StartsWith("Exterior"));
            var before = document.TypesOf<WallType>().Count();

            var window = new WallTypesWindow(document, history, exterior);

            Click(window, "OnDuplicate");
            Assert.Equal(before + 1, document.TypesOf<WallType>().Count());

            var copy = document.TypesOf<WallType>().Single(t => t.Name == $"{exterior.Name} 2");

            ((TextBox)window.FindName("NameBox")).Text = "Exterior - Thick Plaster";

            var grid = (DataGrid)window.FindName("LayerGrid");
            grid.SelectedIndex = grid.Items.Count - 1;
            Click(window, "OnAddLayer");

            var added = (LayerDraftRow)grid.SelectedItem;
            added.ThicknessText = "25";

            Click(window, "OnApply");

            Assert.Equal("Exterior - Thick Plaster", copy.Name);
            Assert.Equal(exterior.Width + 25, copy.Width, precision: 6);
            Assert.Equal(exterior.Structure.Layers.Count + 1, copy.Structure.Layers.Count);

            // Two steps, and undoing both leaves the project as it was.
            history.Undo();
            Assert.Equal(exterior.Width, copy.Width, precision: 6);
            history.Undo();
            Assert.Equal(before, document.TypesOf<WallType>().Count());

            window.Close();
        });
    }

    [Fact]
    public void MakingAStackedTypeInTheEditor()
    {
        OnUiThread(() =>
        {
            var document = BimDocument.CreateDefault();
            var history = new UndoStack();
            var window = new WallTypesWindow(document, history, null);

            Click(window, "OnNewStacked");
            var stacked = Assert.Single(document.TypesOf<StackedWallType>());
            Assert.Equal(2, stacked.Tiers.Count);

            ((TextBox)window.FindName("StackNameBox")).Text = "Plinth and Render";

            // Top first on screen: the second row is the bottom tier.
            var grid = (DataGrid)window.FindName("TierGrid");
            var bottom = (TierDraftRow)grid.Items[1];
            bottom.HeightText = "1200";

            Click(window, "OnApply");

            Assert.Equal("Plinth and Render", stacked.Name);
            Assert.Equal(1200, stacked.Tiers[0].Height);
            Assert.True(stacked.Tiers[1].IsVariable);

            window.Close();
        });
    }
}
