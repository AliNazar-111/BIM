using System.Windows;
using System.Windows.Controls;

namespace BIMDesigner.UI;

/// <summary>Synchronize with Central asked about: a comment for the others to read, and whether to let go of what was borrowed.</summary>
internal sealed class SynchronizeWindow : System.Windows.Window
{
    private readonly TextBox _comment = new() { Margin = new Thickness(16, 0, 16, 8), MinHeight = 60, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
    private readonly CheckBox _relinquish = new() { Content = "Let go of what I borrowed", IsChecked = true, Margin = new Thickness(16, 0, 16, 8) };

    public SynchronizeWindow()
    {
        DialogLook.Apply(this, "Synchronize with Central", 460, 260);
        ResizeMode = ResizeMode.NoResize;

        var go = new Button { Content = "Synchronize", IsDefault = true };
        go.Click += (_, _) => DialogResult = true;
        var cancel = new Button { Content = "Cancel", IsCancel = true };

        var root = new DockPanel();
        root.Children.Add(DialogLook.Footer(new TextBlock(), go, cancel));
        root.Children.Add(DialogLook.Intro("What you have changed goes to the central model, and everyone else's comes back. Say what you did, for the others:"));
        DockPanel.SetDock(_relinquish, Dock.Bottom);
        root.Children.Add(_relinquish);
        root.Children.Add(_comment);
        Content = root;
        Loaded += (_, _) => _comment.Focus();
    }

    public string Comment => _comment.Text;

    public bool Relinquish => _relinquish.IsChecked == true;
}
