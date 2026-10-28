using System.Windows;

namespace BIMDesigner.UI;

/// <summary>The project's materials and their faces, and renderings of the 3D view.</summary>
public partial class MainWindow
{
    private MaterialsWindow? _materialsWindow;
    private RenderWindow? _renderWindow;

    private void OnMaterials(object sender, RoutedEventArgs e) => OpenMaterials();

    /// <summary>The Materials window: each material's colours and the pattern its face is laid in.</summary>
    internal MaterialsWindow OpenMaterials()
    {
        if (_materialsWindow is { IsLoaded: true })
        {
            _materialsWindow.Activate();
            return _materialsWindow;
        }

        _materialsWindow = new MaterialsWindow(this);
        if (IsLoaded) _materialsWindow.Owner = this;
        _materialsWindow.Show();
        return _materialsWindow;
    }

    private void OnRender(object sender, RoutedEventArgs e) => OpenRender();

    /// <summary>The Render window, for the 3D view as it looks - opened first if it is not.</summary>
    internal RenderWindow OpenRender()
    {
        if (ModelPanel.Visibility != Visibility.Visible || Model3D.Elevation is not null) Show3D();
        if (_renderWindow is { IsLoaded: true })
        {
            _renderWindow.Activate();
            return _renderWindow;
        }

        _renderWindow = new RenderWindow(this, Model3D);
        if (IsLoaded) _renderWindow.Owner = this;
        _renderWindow.Show();
        return _renderWindow;
    }
}
