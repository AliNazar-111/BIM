using System.Globalization;
using System.Windows.Data;

namespace BIMDesigner.UI.ViewModels;

/// <summary>
/// Which groups of the Properties palette are folded shut. Kept for the session, so a group
/// closed for one wall stays closed for the next thing selected, as Revit's do.
/// </summary>
public sealed class PaletteGroupState : IValueConverter
{
    public static HashSet<string> Collapsed { get; } = new();

    /// <summary>A group's name to whether it is open.</summary>
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is not string name || !Collapsed.Contains(name);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
