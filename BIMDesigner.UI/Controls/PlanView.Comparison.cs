using System.Windows.Media;
using BIMDesigner.Core.Sheets;
using BIMDesigner.Core.Views;
using BIMDesigner.UI.Rendering;

namespace BIMDesigner.UI.Controls;

/// <summary>
/// A comparison on the plan: this project's elements coloured by how they have changed, and what was
/// removed drawn from the issue before, red and dashed, where it stood.
/// </summary>
public partial class PlanView
{
    private ComparisonOverlay? _comparison;
    private readonly PlanRenderer _removedRenderer = new();

    /// <summary>The comparison shown, or none.</summary>
    public ComparisonOverlay? Comparison
    {
        get => _comparison;
        set
        {
            _comparison = value;
            RefreshModel();
        }
    }

    /// <summary>What this project has lost since the issue it is compared with, on this storey, drawn from that issue.</summary>
    private void DrawRemoved(DrawingContext dc)
    {
        if (Document is null || Comparison is not { Link: null } comparing || comparing.Removed.Count == 0 || !comparing.Kinds.Contains(Core.Coordination.ChangeKind.Removed)) return;
        if (comparing.LevelBefore(Document, ActiveLevelId) is not { } level) return;

        _removedRenderer.Document = comparing.Before;
        _removedRenderer.DetailLevel = EffectiveDetailLevel;
        _removedRenderer.View = ViewReference.FloorPlan(level);
        _removedRenderer.SetSelection(null);
        _removedRenderer.ActiveLevelId = level;
        _removedRenderer.Filter = element => comparing.Removed.Contains(element.Id);
        _removedRenderer.Style = _ => new ElementStyle(Colour: ComparisonOverlay.RemovedColour, Dashed: true);
        _removedRenderer.PixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        _removedRenderer.PixelsPerPaperMm = PlanRenderer.ScreenPixelsPerPaperMm;
        _removedRenderer.SetTransform(ModelToScreen, PixelsPerMm);
        _removedRenderer.DrawContent(dc, underlay: true);
    }
}

public partial class PlanView
{
    /// <summary>The plan turned to a part of it: centred on it, with room round it to see where it is.</summary>
    public void ZoomTo(Core.Geometry.Point2D min, Core.Geometry.Point2D max)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        const double marginMm = 3000;
        var width = Math.Max(max.X - min.X, 1000) + marginMm * 2;
        var height = Math.Max(max.Y - min.Y, 1000) + marginMm * 2;
        PixelsPerMm = Clamp(Math.Min(ActualWidth / width, ActualHeight / height));
        ViewCentre = new Core.Geometry.Point2D((min.X + max.X) / 2, (min.Y + max.Y) / 2);
        RaiseViewChanged();
    }
}
