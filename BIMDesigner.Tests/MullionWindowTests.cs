using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;
using BIMDesigner.UI;
using BIMDesigner.UI.Controls;

using Window = System.Windows.Window;

namespace BIMDesigner.Tests;

/// <summary>Mullions in the editors: a stretch of a line given its own in the grid editor, the slots in Wall Types, a mullion type's own properties.</summary>
[Collection("Wpf")]
public class MullionWindowTests
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

        if (failure is not null) throw new Exception("The editor failed on the UI thread.", failure);
    }

    private static void Click(Window window, string handler) =>
        window.GetType()
            .GetMethod(handler, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, new object?[] { window, new RoutedEventArgs() });

    private static (BimDocument Document, Wall Wall) Storefront()
    {
        var document = BimDocument.CreateDefault();
        var wall = new Wall
        {
            Start = new Point2D(0, 0), End = new Point2D(6000, 0), UnconnectedHeight = 3000,
            TypeId = document.TypesOf<CurtainWallType>().Single(t => t.Name.Contains("Storefront")).Id, LevelId = document.Levels[0].Id
        };
        document.Add(wall);
        return (document, wall);
    }

    [Fact]
    public void InTheGridEditorAStretchOfALineTakesAnotherMullionOrNone()
    {
        OnUiThread(() =>
        {
            var (document, wall) = Storefront();
            var window = new EditCurtainGridWindow(document, wall);
            var editor = (CurtainGridEditor)window.FindName("Editor");
            var picker = (ComboBox)window.FindName("MullionBox");
            Assert.False(picker.IsEnabled);

            // The lower stretch of the middle line: none, the glass butted.
            editor.Select(true, 1, 0);
            Assert.True(picker.IsEnabled);
            Assert.Equal("As the type", picker.SelectedItem);
            picker.SelectedItem = "None - the panels butted";
            Assert.Equal(new CurtainMullionOverride(true, 3000, 750, Guid.Empty), Assert.Single(editor.Mullions));

            // All of the line after it - double-clicked - the heavy box.
            var heavy = document.TypesOf<MullionType>().Single(t => t.Name.Contains("65 x 200"));
            editor.Select(true, 1, null);
            picker.SelectedItem = heavy.Name;
            Assert.Equal(2, editor.Mullions.Count);
            Assert.All(editor.Mullions, m => Assert.Equal(heavy.Id, m.TypeId));
            Assert.Equal(heavy.Id, editor.SelectedMullion);

            // Back to the type on its lower stretch.
            editor.Select(true, 1, 0);
            picker.SelectedItem = "As the type";
            Assert.Single(editor.Mullions);

            // OK sets the result before closing a dialog - which, not shown as one here, it cannot.
            Assert.Throws<TargetInvocationException>(() => Click(window, "OnOk"));
            Assert.Single(window.ResultMullions!);
            window.Close();
        });
    }

    [Fact]
    public void InWallTypesEachLineIsGivenAMullionType()
    {
        OnUiThread(() =>
        {
            var document = BimDocument.CreateDefault();
            var history = new UndoStack();
            var storefront = document.TypesOf<CurtainWallType>().Single(t => t.Name.Contains("Storefront"));
            var capped = document.TypesOf<MullionType>().Single(t => t.Name.Contains("Capped"));

            var window = new WallTypesWindow(document, history, storefront);
            var interior = (ComboBox)window.FindName("InteriorVerticalPicker");
            Assert.Equal("The mullion above", interior.SelectedItem);

            interior.SelectedItem = capped.Name;
            ((ComboBox)window.FindName("BorderHorizontalTopPicker")).SelectedItem = "None";
            ((ComboBox)window.FindName("JoinPicker")).SelectedItem = BIMDesigner.Core.Parameters.EnumText.Humanise(CurtainJoin.HorizontalContinuous);
            Click(window, "OnApply");

            Assert.Equal(capped.Id, storefront.InteriorVertical);
            Assert.Equal(Guid.Empty, storefront.BorderHorizontalTop);
            Assert.Equal(CurtainJoin.HorizontalContinuous, storefront.JoinCondition);

            history.Undo();
            Assert.Null(storefront.InteriorVertical);
            Assert.Equal(CurtainJoin.VerticalContinuous, storefront.JoinCondition);
            window.Close();
        });
    }

    [Fact]
    public void AMullionTypeShowsItsSizesAndStiffnessAndIsKeptWhileFramedIn()
    {
        OnUiThread(() =>
        {
            var document = BimDocument.CreateDefault();
            var capped = document.TypesOf<MullionType>().Single(t => t.Name.Contains("Capped"));
            var round = document.TypesOf<MullionType>().Single(t => t.Name.Contains("Circular"));

            // Framing the stick system, it cannot be deleted; the round one frames nothing.
            var window = new TypePropertiesWindow(document, new UndoStack(), capped, Array.Empty<BIMDesigner.Core.Elements.Element>());
            Assert.False(((Button)window.FindName("DeleteButton")).IsEnabled);
            window.Close();

            window = new TypePropertiesWindow(document, new UndoStack(), round, Array.Empty<BIMDesigner.Core.Elements.Element>());
            Assert.True(((Button)window.FindName("DeleteButton")).IsEnabled);
            window.Close();

            var names = capped.AllTypeParameters(document).Select(p => p.Definition.Name).ToList();
            Assert.Contains("Cover Cap Depth (0 = none)", names);
            Assert.Contains("Second Moment Across the Wall (cm⁴)", names);
            Assert.DoesNotContain("Leg 1", names);
        });
    }
}
