using BIMDesigner.Core;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.UI.Controls;

/// <summary>
/// A picture under the plan set to its true size: two points clicked on it that are a known distance apart - the ends of a
/// scale bar, a building's front - and the picture scaled about the first so they are that far apart.
/// </summary>
public partial class PlanView
{
    private (ImageUnderlay Picture, Point2D? First)? _imageScale;

    /// <summary>Whether the two points to scale a picture by are being clicked.</summary>
    public bool IsScalingImage => _imageScale is not null;

    /// <summary>The length the two points are to be apart, mm; none to be asked once they are clicked.</summary>
    public double? ImageScaleLength { get; set; }

    /// <summary>Raised when both points are clicked and no length is set: the window asks it, then calls <see cref="FinishImageScale"/>.</summary>
    public event EventHandler<double>? ImageScaleMeasured;

    private (Point2D First, Point2D Second)? _imageScalePoints;

    /// <summary>Starts scaling a picture - the one selected, or the only one on the storey. False where there is none.</summary>
    public bool BeginImageScale(ImageUnderlay? picture = null)
    {
        picture ??= _selection.OfType<ImageUnderlay>().FirstOrDefault() ?? (OnActiveLevel<ImageUnderlay>().Take(2).ToList() is { Count: 1 } only ? only[0] : null);
        if (picture is null)
        {
            HintChanged?.Invoke(this, "Select the picture to scale first.");
            return false;
        }

        Select(picture);
        _imageScale = (picture, null);
        _imageScalePoints = null;
        HintChanged?.Invoke(this, "Click a point on the picture whose distance to another you know - one end of its scale bar, a corner of a building. Esc stops.");
        return true;
    }

    /// <summary>A click while scaling a picture - public so tests can drive it.</summary>
    public bool ImageScaleAt(Point2D raw)
    {
        if (_imageScale is not var (picture, first)) return false;
        if (first is not { } from)
        {
            _imageScale = (picture, raw);
            HintChanged?.Invoke(this, "Now click the other point.");
            InvalidateVisual();
            return true;
        }

        if (from.DistanceTo(raw) < 1)
        {
            HintChanged?.Invoke(this, "Click a second point away from the first.");
            return true;
        }

        _imageScalePoints = (from, raw);
        if (ImageScaleLength is { } length) return FinishImageScale(length);
        ImageScaleMeasured?.Invoke(this, from.DistanceTo(raw));
        return true;
    }

    /// <summary>The picture scaled about the first point clicked so the two are a length apart, mm, as one step to undo.</summary>
    public bool FinishImageScale(double length)
    {
        if (_imageScale is not var (picture, _) || _imageScalePoints is not var (first, second)) return false;
        _imageScale = null;
        _imageScalePoints = null;
        if (ImageUnderlays.Calibration(first, second, length) is not { } factor)
        {
            HintChanged?.Invoke(this, "Give the distance between the two points: more than nothing.");
            return false;
        }

        Select(picture);
        if (Math.Abs(factor - 1) > 1e-9 && !ScaleAbout(first, factor)) return false;
        HintChanged?.Invoke(this, $"Scaled so the two points are {Units.FormatLength(length)} apart: a pixel is {Units.FormatLength(picture.Scale)}, the picture {Units.FormatLength(picture.Width)} wide.");
        InvalidateVisual();
        return true;
    }

    private bool CancelImageScale()
    {
        if (_imageScale is null) return false;
        (_imageScale, _imageScalePoints) = (null, null);
        HintChanged?.Invoke(this, "Scaling the picture stopped.");
        InvalidateVisual();
        return true;
    }
}
