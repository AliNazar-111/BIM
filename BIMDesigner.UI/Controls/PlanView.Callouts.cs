using System.Windows;
using System.Windows.Media;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Sheets;
using BIMDesigner.Core.Views;

namespace BIMDesigner.UI.Controls;

/// <summary>
/// Callouts on the plan: the Callout tool takes two corners of the part to call out; the plan
/// shows a callout's view cropped to its box, in its own detail level; and a double-click on a
/// callout's box opens its view.
/// </summary>
public partial class PlanView
{
    private Callout? _activeCallout;
    private Point2D? _calloutCorner;

    private static readonly Pen CropPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0x9A, 0xA4, 0xB2))), 1) { DashStyle = DashStyles.Dash });

    /// <summary>Raised when a callout is double-clicked: its view is to be opened.</summary>
    public event EventHandler<Callout>? CalloutPicked;

    /// <summary>
    /// The callout whose view the plan shows, cropped to its box; null for the whole plan. Its
    /// storey is the plan's while it is shown.
    /// </summary>
    public Callout? ActiveCallout
    {
        get => _activeCallout;
        set
        {
            if (ReferenceEquals(_activeCallout, value)) return;
            _activeCallout = value;
            if (value is not null) ActiveLevelId = value.LevelId;
            CancelPendingOperation();
            InvalidateVisual();
        }
    }

    /// <summary>The detail the view shown is drawn in: its own where it has one, otherwise the plan's.</summary>
    public DetailLevel EffectiveDetailLevel => Document?.ViewSettings.DetailLevelOf(CurrentView) ?? DetailLevel;

    private Rect CropRect(Callout callout) =>
        new(ModelToScreen(new Point2D(callout.Region.MinX, callout.Region.MaxY)), ModelToScreen(new Point2D(callout.Region.MaxX, callout.Region.MinY)));

    private void DrawCrop(DrawingContext dc)
    {
        if (ActiveCallout is { } callout) dc.DrawRectangle(null, CropPen, CropRect(callout));
    }

    /// <summary>The callouts drawn on this plan whose box's edge passes near a point.</summary>
    private IEnumerable<Callout> CalloutsAt(Point2D model)
    {
        if (Document is null || ActiveCallout is not null) yield break;
        var reach = 5 / PixelsPerMm;
        foreach (var callout in Callouts.On(Document, ViewReference.FloorPlan(ActiveLevelId)))
        {
            var r = callout.Region;
            var corners = new[] { new Point2D(r.MinX, r.MinY), new Point2D(r.MaxX, r.MinY), new Point2D(r.MaxX, r.MaxY), new Point2D(r.MinX, r.MaxY) };
            if (Enumerable.Range(0, 4).Any(i => DistanceToSegment(model, corners[i], corners[(i + 1) % 4]) <= reach)) yield return callout;
        }
    }

    /// <summary>A double-click on a callout's box: its view opened.</summary>
    private bool OpenCalloutAt(Point2D model)
    {
        if (CalloutsAt(model).FirstOrDefault() is not { } callout) return false;
        CalloutPicked?.Invoke(this, callout);
        return true;
    }

    /// <summary>A click with the Callout tool: one corner of its box, then the other. Public so it can be driven from tests.</summary>
    public bool ClickCallout(Point2D at)
    {
        if (Document is null || Document.FindLevel(ActiveLevelId) is null) return false;
        if (_calloutCorner is not { } first)
        {
            _calloutCorner = at;
            HintChanged?.Invoke(this, "Now click the corner across from it.");
            InvalidateVisual();
            return true;
        }

        if (Math.Abs(at.X - first.X) < Callouts.Smallest || Math.Abs(at.Y - first.Y) < Callouts.Smallest)
        {
            HintChanged?.Invoke(this, $"A callout is at least {Core.Units.Millimetres(Callouts.Smallest, "0")} each way: click further from the first corner.");
            return false;
        }

        _calloutCorner = null;
        var parent = ActiveCallout is { } open ? open.Parent : ViewReference.FloorPlan(ActiveLevelId);
        var (callout, command) = Callouts.Draw(Document, parent, ActiveLevelId, first, at);
        Apply(command);
        HintChanged?.Invoke(this, $"{callout.Name}: a view of its own at 1:50, under Callouts. Double-click its box to open it; drag its corners to crop it.");
        ModelChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
        return true;
    }

    private bool CancelCallout()
    {
        var was = _calloutCorner is not null;
        _calloutCorner = null;
        if (was) InvalidateVisual();
        return was;
    }

    private void DrawCalloutPreview(DrawingContext dc)
    {
        if (ActiveTool != PlanTool.Callout || _calloutCorner is not { } first || _cameraHover is not { } at) return;
        dc.DrawRoundedRectangle(null, SketchPreviewPen, new Rect(ModelToScreen(first), ModelToScreen(at)), 6, 6);
    }
}
