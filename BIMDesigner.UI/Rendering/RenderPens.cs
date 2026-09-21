using System.Windows;
using System.Windows.Media;
using BIMDesigner.Core.Materials;

namespace BIMDesigner.UI.Rendering;

/// <summary>
/// Pen and brush construction shared by every drawing surface.
///
/// Everything here is frozen. A frozen <see cref="Freezable"/> can be reused across renders
/// without WPF re-validating it, which matters when a single repaint of a sheet draws several
/// thousand geometries.
/// </summary>
public static class RenderPens
{
    public static Pen Solid(Color colour, double thickness) =>
        Freeze(new Pen(Freeze(new SolidColorBrush(colour)), thickness));

    public static Pen Dashed(Color colour, double thickness, double dash, double gap) =>
        Freeze(new Pen(Freeze(new SolidColorBrush(colour)), thickness)
        {
            DashStyle = new DashStyle(new[] { dash, gap }, 0)
        });

    public static Pen Chain(Color colour, double thickness, params double[] pattern) =>
        Freeze(new Pen(Freeze(new SolidColorBrush(colour)), thickness)
        {
            DashStyle = new DashStyle(pattern, 0)
        });

    public static SolidColorBrush Fill(Color colour) => Freeze(new SolidColorBrush(colour));

    /// <summary>The model's own colour type to WPF's. Core knows nothing about WPF.</summary>
    public static Color ToMediaColor(ColourRgb colour) => Color.FromRgb(colour.R, colour.G, colour.B);

    public static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
