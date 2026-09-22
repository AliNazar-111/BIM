using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.UI;
using BIMDesigner.UI.Controls;
using BIMDesigner.UI.ViewModels;

using Window = System.Windows.Window;

namespace BIMDesigner.Tests;

/// <summary>
/// The profile editor window builds, its presets and corners table reach the drawing, and a
/// bad outline keeps OK disabled.
/// </summary>
public class EditProfileWindowTests
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
    public void PresetsAndTypedCornersReachTheDrawing()
    {
        OnUiThread(() =>
        {
            var document = BimDocument.CreateDefault();
            var wall = new Wall
            {
                Start = new Point2D(0, 0), End = new Point2D(6000, 0),
                TypeId = document.TypesOf<WallType>().First().Id, LevelId = document.Levels[0].Id, UnconnectedHeight = 3000
            };
            document.Add(wall);

            var window = new EditProfileWindow(document, wall);
            var editor = (ProfileEditor)window.FindName("Editor");
            var ok = (Button)window.FindName("OkButton");
            var rows = (IList<ProfileCornerRow>)((DataGrid)window.FindName("CornerGrid")).ItemsSource;

            // It opens on the wall's rectangle.
            Assert.Equal(4, editor.Corners.Count);
            Assert.True(ok.IsEnabled);

            Click(window, "OnGable");
            Assert.Equal(5, editor.Corners.Count);
            Assert.Equal(5, rows.Count);
            Assert.Equal(6000, editor.Corners.Max(c => c.Y), precision: 9);

            // A typed height moves the corner; a corner past the end of the wall is refused.
            rows[3].HeightText = "5000";
            Assert.Equal(5000, editor.Corners[3].Y, precision: 9);
            Assert.True(ok.IsEnabled);

            rows[1].AlongText = "7000";
            Assert.False(ok.IsEnabled);

            rows[1].AlongText = "not a length";
            Assert.False(ok.IsEnabled);

            // Placed exactly: the third corner 2 m straight up from the second.
            Click(window, "OnRectangle");
            editor.Select(2);
            ((TextBox)window.FindName("PlaceLengthBox")).Text = "2000";
            ((TextBox)window.FindName("PlaceAngleBox")).Text = "90";
            Click(window, "OnPlaceCorner");
            Assert.Equal(6000, editor.Corners[2].X, precision: 9);
            Assert.Equal(2000, editor.Corners[2].Y, precision: 9);
            Assert.Equal(new Point2D(6000, 2000), rows[2].Corner);
            Assert.Equal(90, ProfileEditor.AngleBetween(editor.Corners[1], editor.Corners[2]), precision: 9);

            window.Close();
        });
    }
}
