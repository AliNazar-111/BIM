using System.Windows;

namespace BIMDesigner.UI;

/// <summary>
/// Keeps a dialog on the screen it opens on. A window sized for a large monitor would
/// otherwise hang off the top and bottom of a laptop's, with its title bar and buttons out of
/// reach; here it shrinks to the working area (the screen less the taskbar) and is moved
/// fully onto it.
/// </summary>
public static class ScreenFit
{
    public static void Apply(Window window)
    {
        var area = SystemParameters.WorkArea;

        window.MinWidth = Math.Min(window.MinWidth, area.Width);
        window.MinHeight = Math.Min(window.MinHeight, area.Height);
        window.MaxWidth = area.Width;
        window.MaxHeight = area.Height;
        if (window.Width > area.Width) window.Width = area.Width;
        if (window.Height > area.Height) window.Height = area.Height;

        // Centring on the owner can still push an edge off the screen.
        window.Loaded += (_, _) =>
        {
            window.Left = Math.Clamp(window.Left, area.Left, Math.Max(area.Left, area.Right - window.ActualWidth));
            window.Top = Math.Clamp(window.Top, area.Top, Math.Max(area.Top, area.Bottom - window.ActualHeight));
        };
    }
}
