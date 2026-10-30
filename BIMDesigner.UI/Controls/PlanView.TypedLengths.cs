using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using BIMDesigner.Core;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.UI.Controls;

/// <summary>
/// The temporary dimension along a line being drawn - a wall, a gridline, a section, a beam, a
/// room separator, a railing, an outline, a sketch line - its length, and its angle where it is
/// not square, beside it as the cursor moves; and a length typed while it is drawn, as on any
/// drawing board: the first figure typed opens the dimension to type into, and Enter puts the
/// next point that far along, the way the cursor points - square, or at 45°, where it is within
/// a few degrees of it. The next line carries on from there, to be typed again.
/// </summary>
public partial class PlanView
{
    /// <summary>The length being typed; null when none is.</summary>
    private string? _typedLength;

    /// <summary>How near to square, or to 45°, a line's direction is taken to be meant as it.</summary>
    private const double SquareWithin = 3;

    /// <summary>A line being drawn: where it starts, where the cursor has it going, and what a click at its end does.</summary>
    private readonly record struct DrawnLine(Point2D From, Point2D Toward, Func<Point2D, bool> Place);

    /// <summary>Whether a length is being typed for the line being drawn.</summary>
    public bool TypingLength => _typedLength is not null;

    /// <summary>What has been typed so far, or null.</summary>
    public string? TypedLength => _typedLength;

    /// <summary>The line the active tool is drawing, if it is drawing one from a point already placed.</summary>
    private DrawnLine? LineBeingDrawn()
    {
        if (Document is null) return null;

        if (IsSketching)
            return _sketchTool == RoofSketchTool.Line && _sketchStart is { } begun ? new DrawnLine(begun, SketchSnap(_sketchCursor), DrawLineTo) : null;

        return ActiveTool switch
        {
            PlanTool.Wall when _drawShape == WallShape.Line && _pendingWallStart is { } from =>
                new DrawnLine(from, _cursorModel, point => { PlaceWallPoint(point); return true; }),
            PlanTool.Grid when _pendingWallStart is { } from => new DrawnLine(from, _cursorModel, point => { PlaceGridPoint(point); return true; }),
            PlanTool.Section when _pendingWallStart is { } from => new DrawnLine(from, _cursorModel, point => { PlaceSectionPoint(point); return true; }),
            PlanTool.Beam when _beamStart is { } from => new DrawnLine(from, _beamHover ?? _cursorModel, ClickBeam),
            PlanTool.RoomSeparator when _separatorStart is { } from => new DrawnLine(from, _separatorHover ?? _cursorModel, ClickRoomSeparator),
            PlanTool.Railing when !PickingEdges && !RailingArc && _railingArcEnd is null && _railingPoints.Count > 0 =>
                new DrawnLine(_railingPoints[^1], _cursorModel, ClickRailing),
            PlanTool.Region or PlanTool.Joists or PlanTool.Pad or PlanTool.PropertyLine when _outlinePoints.Count > 0 =>
                new DrawnLine(_outlinePoints[^1], _outlineHover ?? _cursorModel, ClickOutline),
            PlanTool.Mass when ActiveMassFootprint == MassFootprint.Outline && _outlinePoints.Count > 0 =>
                new DrawnLine(_outlinePoints[^1], _outlineHover ?? _cursorModel, ClickOutline),
            _ => null
        };
    }

    /// <summary>
    /// Which way a line goes from its start toward the cursor: square, or at 45°, where it is
    /// within <see cref="SquareWithin"/> degrees of it - as near as a hand puts it - or exactly
    /// the way the cursor is. Null with the cursor on the start.
    /// </summary>
    private static Vector2D? Heading(Point2D from, Point2D toward)
    {
        var along = toward - from;
        if (along.Length < 1) return null;

        var degrees = Math.Atan2(along.Y, along.X) * 180 / Math.PI;
        var square = Math.Round(degrees / 45) * 45;
        if (Math.Abs(degrees - square) <= SquareWithin) degrees = square;
        var radians = degrees * Math.PI / 180;
        return new Vector2D(Math.Cos(radians), Math.Sin(radians));
    }

    /// <summary>A length as typed, in mm; null where it cannot be read or is not a length at all.</summary>
    private static double? LengthOf(string? typed) =>
        Units.TryParseLength(typed, out var mm) && mm >= 1 ? mm : null;

    /// <summary>
    /// Starts typing a length for the line being drawn, with what has been typed so far. False
    /// where no line is being drawn. Public so tests can drive it.
    /// </summary>
    public bool BeginTypedLength(string typed = "")
    {
        if (LineBeingDrawn() is null) return false;

        _typedLength = typed;
        HintChanged?.Invoke(this, $"Type its length - {(Units.Imperial ? "12'6\" or 12.5" : "4200 or 4.2 m")} - then Enter: it goes the way the cursor points. Esc leaves it.");
        InvalidateVisual();
        return true;
    }

    /// <summary>More typed into the length. Public so tests can drive it.</summary>
    public void TypeIntoLength(string text)
    {
        if (_typedLength is null) return;
        _typedLength += text;
        InvalidateVisual();
    }

    /// <summary>
    /// The next point put that far along the line being drawn, the way the cursor points - as
    /// a click there would put it, without snapping it to anything nearby. False, with the
    /// length still there to correct, where it cannot be read or the cursor points nowhere.
    /// <paramref name="toward"/> stands for the cursor where tests drive it. Public so tests can drive it.
    /// </summary>
    public bool PlaceTypedLength(string? typed = null, Point2D? toward = null)
    {
        typed ??= _typedLength;
        if (LineBeingDrawn() is not { } drawn)
        {
            CloseTypedLength();
            return false;
        }

        var line = toward is { } pointed ? drawn with { Toward = pointed } : drawn;

        if (LengthOf(typed) is not { } length)
        {
            HintChanged?.Invoke(this, $"\"{typed?.Trim()}\" is not a length: type one such as {(Units.Imperial ? "12'6\" or 12.5" : "4200 or 4.2 m")}, then Enter.");
            InvalidateVisual();
            return false;
        }

        if (Heading(line.From, line.Toward) is not { } heading)
        {
            HintChanged?.Invoke(this, "Point the cursor the way it goes, then press Enter.");
            return false;
        }

        _typedLength = null;
        var end = line.From + heading * length;
        var placed = line.Place(end);
        if (placed)
            HintChanged?.Invoke(this, $"{Units.FormatLength(length)} along. Type the next length, or click; Esc to stop.");
        InvalidateVisual();
        return placed;
    }

    /// <summary>The length being typed let go, the line left as it was. Whether there was one.</summary>
    private bool CloseTypedLength()
    {
        if (_typedLength is null) return false;
        _typedLength = null;
        InvalidateVisual();
        return true;
    }

    /// <summary>A figure typed while a line is drawn opens its length to type into; what follows goes into it.</summary>
    protected override void OnTextInput(TextCompositionEventArgs e)
    {
        base.OnTextInput(e);
        if (e.Handled || string.IsNullOrEmpty(e.Text) || (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) != 0) return;
        if (e.Text.Any(char.IsControl)) return;

        if (_typedLength is not null)
        {
            TypeIntoLength(e.Text);
            e.Handled = true;
            return;
        }

        if ((char.IsDigit(e.Text[0]) || e.Text[0] == '.') && BeginTypedLength(e.Text)) e.Handled = true;
    }

    /// <summary>While a length is typed: Enter puts it down, Backspace takes the last of it back, Esc leaves it.</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || _typedLength is null) return;

        switch (e.Key)
        {
            case Key.Enter:
                PlaceTypedLength();
                e.Handled = true;
                break;
            case Key.Back:
                _typedLength = _typedLength.Length > 0 ? _typedLength[..^1] : _typedLength;
                InvalidateVisual();
                e.Handled = true;
                break;
            case Key.Escape:
                CloseTypedLength();
                HintChanged?.Invoke(this, "Left. Click where it ends, or type a length.");
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// The temporary dimension beside the line being drawn: its length, and its angle where it is
    /// not square - or the length being typed, boxed, with where it would end ringed.
    /// </summary>
    private void DrawTemporaryDimension(DrawingContext dc)
    {
        if (LineBeingDrawn() is not { } line)
        {
            _typedLength = null;
            return;
        }

        var typed = LengthOf(_typedLength);
        var heading = Heading(line.From, line.Toward);
        var end = typed is { } length && heading is { } way ? line.From + way * length : line.Toward;
        var span = end - line.From;
        if (_typedLength is null && span.Length * PixelsPerMm < 24) return;

        var ink = _selectedPen.Brush;
        if (_typedLength is not null && typed is not null && heading is not null)
        {
            dc.DrawLine(_previewPen, ModelToScreen(line.From), ModelToScreen(end));
            dc.DrawEllipse(null, _selectedPen, ModelToScreen(end), 5, 5);
        }

        var degrees = heading is { } turned ? Math.Atan2(turned.Y, turned.X) * 180 / Math.PI : 0;
        var said = _typedLength is not null
            ? _typedLength + "▏"
            : $"{Units.FormatLength(span.Length)}{(Math.Abs(degrees / 90 - Math.Round(degrees / 90)) * 90 < 0.05 ? string.Empty : $" at {(degrees < 0 ? degrees + 360 : degrees):0.#}°")}";
        var text = new FormattedText(said, CultureInfo.CurrentCulture, System.Windows.FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, ink,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

        // Beside the middle of the line, clear of the wall itself: above a line drawn across, to
        // the left of one drawn up - or the other side, where that would be off the plan.
        var a = ModelToScreen(line.From);
        var b = ModelToScreen(end);
        var middle = new Point((a.X + b.X) / 2, (a.Y + b.Y) / 2);
        var across = new Vector(b.Y - a.Y, -(b.X - a.X));
        if (across.Length > 0) across.Normalize();
        else across = new Vector(0, -1);
        // Out past the wall's flip arrows, which stand off its middle.
        var reach = 30 + Math.Abs(across.X) * text.Width / 2 + Math.Abs(across.Y) * text.Height / 2;
        var width = Math.Max(text.Width + 8, _typedLength is null ? 0 : 56);
        Rect BoxAt(Vector side)
        {
            var centre = middle + side * reach;
            return new Rect(centre.X - width / 2, centre.Y - text.Height / 2 - 1, width, text.Height + 2);
        }

        var box = BoxAt(across);
        if (box.Left < 0 || box.Top < 0 || box.Right > ActualWidth || box.Bottom > ActualHeight) box = BoxAt(-across);

        _readoutFill ??= Frozen(new SolidColorBrush(AppTheme.Pick(Color.FromArgb(0xEE, 0xFF, 0xFF, 0xFF), Color.FromArgb(0xEE, 0x1E, 0x22, 0x28))));
        dc.DrawRoundedRectangle(_readoutFill, _typedLength is null ? null : _selectedPen, box, 3, 3);
        dc.DrawText(text, new Point(box.X + 4, box.Y + 1));
    }

    private Brush? _readoutFill;
}
