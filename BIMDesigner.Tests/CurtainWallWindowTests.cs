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

/// <summary>The curtain wall editors: the type panel in Wall Types, and the grid editor for one wall.</summary>
[Collection("Wpf")]
public class CurtainWallWindowTests
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

    [Fact]
    public void ACurtainTypeIsEditedAndAppliedAsOneStep()
    {
        OnUiThread(() =>
        {
            var document = BimDocument.CreateDefault();
            var history = new UndoStack();
            var storefront = document.TypesOf<CurtainWallType>().Single(t => t.Name.Contains("Storefront"));

            var window = new WallTypesWindow(document, history, storefront);
            Assert.Equal(Visibility.Visible, ((FrameworkElement)window.FindName("CurtainPanel")).Visibility);
            Assert.Equal(Visibility.Collapsed, ((FrameworkElement)window.FindName("LayeredPanel")).Visibility);

            ((TextBox)window.FindName("VerticalSpacingBox")).Text = "1000";
            ((ComboBox)window.FindName("MullionProfilePicker")).SelectedItem = "Circular";
            Click(window, "OnApply");

            Assert.Equal(1000, storefront.VerticalSpacing);
            Assert.Equal(MullionProfile.Circular, storefront.MullionProfile);

            history.Undo();
            Assert.Equal(1500, storefront.VerticalSpacing);

            // A new curtain type starts from the first one and is listed straight away.
            var before = document.TypesOf<CurtainWallType>().Count();
            Click(window, "OnNewCurtain");
            Assert.Equal(before + 1, document.TypesOf<CurtainWallType>().Count());

            window.Close();
        });
    }

    [Fact]
    public void AddingAndDeletingLinesKeepsPanelChoicesWithTheirCells()
    {
        OnUiThread(() =>
        {
            var editor = new CurtainGridEditor();
            editor.Show(new CurtainWallType("test"), 6000, 3000, new[] { 1500.0, 3000, 4500 }, new[] { 1500.0 },
                new[] { new CurtainPanelOverride(1, 0, CurtainPanelKind.Door), new CurtainPanelOverride(3, 1, CurtainPanelKind.Solid) });

            // A line through the door's bay: both halves are doors, and the solid panel moves along one.
            Assert.True(editor.AddLine(vertical: true, at: 2000));
            Assert.Equal(new[] { 1500.0, 2000, 3000, 4500 }, editor.Verticals);
            Assert.Contains(new CurtainPanelOverride(1, 0, CurtainPanelKind.Door), editor.Panels);
            Assert.Contains(new CurtainPanelOverride(2, 0, CurtainPanelKind.Door), editor.Panels);
            Assert.Contains(new CurtainPanelOverride(4, 1, CurtainPanelKind.Solid), editor.Panels);
            Assert.True(editor.GridEdited);

            // Deleting it puts things back.
            editor.RemoveSelected();
            Assert.Equal(new[] { 1500.0, 3000, 4500 }, editor.Verticals);
            Assert.Equal(new[] { new CurtainPanelOverride(1, 0, CurtainPanelKind.Door), new CurtainPanelOverride(3, 1, CurtainPanelKind.Solid) }, editor.Panels);

            // A horizontal line through the doors does not lift a door off the floor.
            Assert.True(editor.AddLine(vertical: false, at: 700));
            Assert.Contains(new CurtainPanelOverride(1, 0, CurtainPanelKind.Door), editor.Panels);
            Assert.DoesNotContain(editor.Panels, p => p.Kind == CurtainPanelKind.Door && p.Row > 0);

            // Too close to another line is refused.
            Assert.False(editor.AddLine(vertical: true, at: 1550));
        });
    }

    [Fact]
    public void TheGridWindowOpensOnACurtainWall()
    {
        OnUiThread(() =>
        {
            var document = BimDocument.CreateDefault();
            var wall = new Wall
            {
                Start = new Point2D(0, 0), End = new Point2D(6000, 0),
                TypeId = document.TypesOf<CurtainWallType>().First().Id, LevelId = document.Levels[0].Id
            };
            document.Add(wall);

            var window = new EditCurtainGridWindow(document, wall);
            var editor = (CurtainGridEditor)window.FindName("Editor");
            Assert.Equal(CurtainLayout.Of(document, wall)!.Verticals.Count - 2, editor.Verticals.Count);
            window.Close();
        });
    }
}
