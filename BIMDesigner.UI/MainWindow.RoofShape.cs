using System.Windows;
using System.Windows.Input;
using BIMDesigner.Core;

namespace BIMDesigner.UI;

/// <summary>Shape Editing on a flat roof: started from the roof's context tab, its rise typed on the options bar, its shape reset.</summary>
public partial class MainWindow
{
    private void OnRoofShapeEditing(object sender, RoutedEventArgs e)
    {
        if (!Plan.BeginShapeEditing())
        {
            StatusHint.Text = "Select a flat roof drawn by footprint first: shape editing is for a flat roof's falls.";
            return;
        }

        RoofShapeRiseBox.Text = Units.FormatLength(Plan.ShapeRise);
        Plan.Focus();
    }

    private void OnRoofShapeRiseChanged(object sender, RoutedEventArgs e)
    {
        if (Units.TryParseLength(RoofShapeRiseBox.Text.Trim().TrimStart('+'), out var rise) && double.IsFinite(rise) && Math.Abs(rise) <= 2000) Plan.ShapeRise = rise;
        RoofShapeRiseBox.Text = Units.FormatLength(Plan.ShapeRise);
    }

    private void OnRoofShapeRiseKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        OnRoofShapeRiseChanged(sender, e);
        Plan.Focus();
    }

    private void OnResetRoofShape(object sender, RoutedEventArgs e)
    {
        StatusHint.Text = Plan.ResetShape() ? "The roof's top is back as built." : "The roof's shape has not been edited.";
        Plan.Focus();
    }
}
