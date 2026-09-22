using System.ComponentModel;
using System.Runtime.CompilerServices;
using BIMDesigner.Core;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Parameters;

namespace BIMDesigner.UI.ViewModels;

/// <summary>One corner of a wall profile being edited: how far along the wall, how high.</summary>
public sealed class ProfileCornerRow : INotifyPropertyChanged
{
    private (double Value, string Text) _along;
    private (double Value, string Text) _height;

    public ProfileCornerRow(int number, Point2D corner)
    {
        Number = number;
        Set(corner);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised when a value is typed in.</summary>
    public event EventHandler? Edited;

    public int Number { get; }

    public string AlongText
    {
        get => _along.Text;
        set { _along = Parse(value); OnPropertyChanged(); Edited?.Invoke(this, EventArgs.Empty); }
    }

    public string HeightText
    {
        get => _height.Text;
        set { _height = Parse(value); OnPropertyChanged(); Edited?.Invoke(this, EventArgs.Empty); }
    }

    /// <summary>The corner, or null while either value is not a length.</summary>
    public Point2D? Corner =>
        double.IsNaN(_along.Value) || double.IsNaN(_height.Value) ? null : new Point2D(_along.Value, _height.Value);

    /// <summary>Shows a corner moved on the drawing, without raising <see cref="Edited"/>.</summary>
    public void Set(Point2D corner)
    {
        _along = (corner.X, Units.FormatLength(corner.X));
        _height = (corner.Y, Units.FormatLength(corner.Y));
        OnPropertyChanged(nameof(AlongText));
        OnPropertyChanged(nameof(HeightText));
    }

    private static (double, string) Parse(string text) =>
        ParameterFormatter.TryParse(ParameterDataType.Length, text, out var parsed) && parsed is double millimetres
            ? (millimetres, Units.FormatLength(millimetres))
            : (double.NaN, text);

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
