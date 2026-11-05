using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using BIMDesigner.Core;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.UI.Controls;

/// <summary>
/// Shape Editing on a flat roof (Revit's Modify Sub Elements, Add Point, Add Split Line): a click on a
/// corner or a point raises it to the rise on the options bar, a click inside the roof away from them
/// adds a point there at that rise, and Ctrl and a click on each of two points puts a split line
/// between them - the ridge or valley its faces meet along.
/// </summary>
public partial class PlanView
{
    /// <summary>The flat roof whose shape is being edited.</summary>
    private Roof? _shapeRoof;

    /// <summary>The first end of a split line, while the second is waited for.</summary>
    private Point2D? _splitFrom;

    /// <summary>How far a point clicked is raised - lowered where less than none - mm.</summary>
    public double ShapeRise { get; set; } = 50;

    /// <summary>Starts Shape Editing on the selected roof, where it is a flat one drawn by footprint.</summary>
    public bool BeginShapeEditing()
    {
        if (Document is null || _selection is not [Roof roof] || !RoofShapeEditing.CanShape(Document, roof)) return false;

        SetTool(PlanTool.RoofShape);
        _shapeRoof = roof;
        _splitFrom = null;
        HintChanged?.Invoke(this, "Click a corner or a point to raise it to the rise on the options bar; inside the roof to add a point; Ctrl+click two points for a split line between them.");
        InvalidateVisual();
        return true;
    }

    /// <summary>The roof whose shape is being edited, for the options bar.</summary>
    public Roof? ShapeRoof => ActiveTool == PlanTool.RoofShape ? _shapeRoof : null;

    /// <summary>A click while editing a roof's shape. Public so it can be driven from tests.</summary>
    public bool RoofShapeAt(Point2D raw, bool split = false)
    {
        if (Document is null || _shapeRoof is not { } roof) return false;

        var reach = 12 / PixelsPerMm;
        var point = RoofShapeEditing.PointNear(roof, raw, reach);
        if (split || Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            if (point is null && !roof.Contains(raw) && !RoofShapeEditing.OnOutline(roof, raw))
            {
                HintChanged?.Invoke(this, "A split line runs across the roof: Ctrl+click its ends on it - corners, points, or anywhere on it.");
                return false;
            }

            var end = point?.At ?? raw;
            if (_splitFrom is not { } from)
            {
                _splitFrom = end;
                HintChanged?.Invoke(this, "Ctrl+click the point at the line's other end.");
                InvalidateVisual();
                return true;
            }

            _splitFrom = null;
            if (RoofShapeEditing.Split(roof, from, end) is not { } line)
            {
                HintChanged?.Invoke(this, "There is a split line there already.");
                return false;
            }

            Apply(line);
            HintChanged?.Invoke(this, "Split line added: the faces either side meet along it.");
            ModelChanged?.Invoke(this, EventArgs.Empty);
            InvalidateVisual();
            return true;
        }

        if (RoofShapeEditing.Raise(roof, point?.At ?? raw, ShapeRise) is not { } raise)
        {
            HintChanged?.Invoke(this, "Click on the roof: a corner, a point, or inside it.");
            return false;
        }

        Apply(raise);
        HintChanged?.Invoke(this, $"{(point is null ? "Point added" : "Point")} at {(ShapeRise >= 0 ? "+" : "")}{Units.FormatLength(ShapeRise)}: the insulation is laid to it.");
        ModelChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
        return true;
    }

    /// <summary>The roof's shape put back as built.</summary>
    public bool ResetShape()
    {
        var roof = _shapeRoof ?? (_selection is [Roof selected] ? selected : null);
        if (roof is null || RoofShapeEditing.Reset(roof) is not { } reset) return false;
        Apply(reset);
        ModelChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
        return true;
    }

    /// <summary>A shaped roof's points in plan, each marked with how far it is raised, and the split line begun.</summary>
    private void DrawRoofShape(DrawingContext dc)
    {
        if (Document is null || ActiveTool != PlanTool.RoofShape || _shapeRoof is not { } roof) return;

        var pen = new Pen(new SolidColorBrush(Color.FromRgb(0xD0, 0x6A, 0x1E)), 1.5);
        pen.Freeze();
        foreach (var vertex in RoofShapeEditing.Vertices(roof))
        {
            var at = ModelToScreen(vertex.At);
            dc.DrawEllipse(Math.Abs(vertex.Rise) > 1e-6 ? pen.Brush : null, pen, at, 4, 4);
            if (Math.Abs(vertex.Rise) <= 1e-6) continue;
            var said = new FormattedText($"{(vertex.Rise > 0 ? "+" : "")}{vertex.Rise.ToString("0", CultureInfo.CurrentCulture)}", CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, pen.Brush, 1);
            dc.DrawText(said, new Point(at.X + 6, at.Y - said.Height - 2));
        }

        foreach (var line in roof.SplitLines)
            dc.DrawLine(pen, ModelToScreen(line.From), ModelToScreen(line.To));
        if (_splitFrom is { } from)
            dc.DrawLine(pen, ModelToScreen(from), ModelToScreen(_cursorModel));
    }
}
