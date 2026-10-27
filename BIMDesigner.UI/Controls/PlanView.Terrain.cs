using System.Windows.Media;
using BIMDesigner.Core;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.UI.Controls;

/// <summary>
/// The ground on the plan: each click puts a spot level down at the height the option bar
/// says - three make the surface, and every one after adds to it - or a points file is read in.
/// </summary>
public partial class PlanView
{
    /// <summary>
    /// How high the next spot level is above this level, mm: 150 below it to start with, so the
    /// ground outside sits under the damp-proof course, as Approved Document C asks.
    /// </summary>
    public double ActiveGroundElevation { get; set; } = -150;

    /// <summary>The spot levels clicked before there are three to make the ground of.</summary>
    private readonly List<Point3D> _groundPoints = new();

    private Point2D? _groundHover;

    private void GroundHover(Point2D at)
    {
        _groundHover = at;
        InvalidateVisual();
    }

    /// <summary>The ground on this level, where there is one.</summary>
    private Toposurface? GroundOnLevel => OnActiveLevel<Toposurface>().FirstOrDefault();

    /// <summary>
    /// A click with the Toposurface tool: a spot level there, at the height the option bar says -
    /// added to the ground on this level, or, once there are three, making it. Public so it can
    /// be driven from tests.
    /// </summary>
    public bool ClickGround(Point2D at)
    {
        if (Document?.FindLevel(ActiveLevelId) is not { } level) return false;

        var point = new Point3D(at.X, at.Y, level.Elevation + ActiveGroundElevation);

        // Not under the damp-proof course: said, so it is not found out later in the checks.
        var warning = ActiveGroundElevation > -150 + 0.5
            ? $" That is {(ActiveGroundElevation >= 0 ? "level with or over" : "within 150 of")} {level.Name}'s floor: the ground outside wants to be 150 below it at least."
            : "";
        if (GroundOnLevel is { } ground)
        {
            Apply(new SetToposurfacePointsCommand(ground, ground.Points.Append(point), "Ground Point"));
            HintChanged?.Invoke(this, $"Spot level at {Units.FormatLength(point.Z)}: {ground.Points.Count} on the ground.{warning} Click for another; Esc to stop.");
        }
        else
        {
            _groundPoints.Add(point);
            if (_groundPoints.Count < 3)
            {
                HintChanged?.Invoke(this, $"Spot level at {Units.FormatLength(point.Z)}. {3 - _groundPoints.Count} more to make the ground; change the elevation on the option bar between clicks.{warning}");
                InvalidateVisual();
                return true;
            }

            var made = new Toposurface { LevelId = ActiveLevelId };
            made.SetPoints(_groundPoints);
            _groundPoints.Clear();
            Apply(new AddElementCommand(Document, made, "Toposurface"));
            HintChanged?.Invoke(this, $"Ground made through its three spot levels.{warning} Click for more; Esc to stop.");
        }

        ModelChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
        return true;
    }

    /// <summary>
    /// Spot levels read from a points file - x, y and height on each line, in millimetres, or
    /// metres at a scale of 1000 - added to the ground on this level, or making it. How many were read.
    /// </summary>
    public int ImportGround(string text, double scale)
    {
        if (Document is null || Document.FindLevel(ActiveLevelId) is null) return 0;

        var read = Terrains.ReadPoints(text, scale);
        if (read.Count == 0)
        {
            HintChanged?.Invoke(this, "No points read: each line wants an x, a y and a height.");
            return 0;
        }

        if (GroundOnLevel is { } ground) Apply(new SetToposurfacePointsCommand(ground, ground.Points.Concat(read), "Import Ground Points"));
        else if (read.Count >= 3)
        {
            var made = new Toposurface { LevelId = ActiveLevelId };
            made.SetPoints(read);
            Apply(new AddElementCommand(Document, made, "Toposurface"));
        }
        else
        {
            HintChanged?.Invoke(this, "Three points at least make the ground.");
            return 0;
        }

        HintChanged?.Invoke(this, $"{read.Count} spot levels read in, {Units.FormatLength(read.Min(point => point.Z))} to {Units.FormatLength(read.Max(point => point.Z))}.");
        ModelChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
        return read.Count;
    }

    /// <summary>Esc with spot levels clicked but no ground made of them yet: forgotten.</summary>
    private bool CancelGround()
    {
        if (_groundPoints.Count == 0) return false;
        _groundPoints.Clear();
        InvalidateVisual();
        return true;
    }

    /// <summary>The spot levels clicked so far, and where the next would go.</summary>
    private void DrawGroundPreview(DrawingContext dc)
    {
        if (ActiveTool != PlanTool.Terrain) return;

        foreach (var at in _groundPoints.Select(point => new Point2D(point.X, point.Y)).Concat(_groundHover is { } hover ? new[] { hover } : Array.Empty<Point2D>()))
        {
            var screen = ModelToScreen(at);
            dc.DrawLine(SketchPreviewPen, new System.Windows.Point(screen.X - 6, screen.Y), new System.Windows.Point(screen.X + 6, screen.Y));
            dc.DrawLine(SketchPreviewPen, new System.Windows.Point(screen.X, screen.Y - 6), new System.Windows.Point(screen.X, screen.Y + 6));
        }
    }

    /// <summary>
    /// The ground recorded as it is now as the existing ground - its grading measured from it,
    /// its old contours shown dashed - or the record let go. Public so it can be driven from tests.
    /// </summary>
    public void SetExistingGround(Toposurface ground, bool record)
    {
        if (Document is null) return;
        Apply(new SetExistingGroundCommand(ground, record));
        var (cut, fill) = Terrains.CutAndFill(Document, ground);
        HintChanged?.Invoke(this, record
            ? "Existing ground recorded: drag its points in plan, raise them in 3D, and its Cut and Fill are measured from this."
            : $"Existing ground let go: cut {Core.Units.CubicMetres(cut, "0.0")}, fill {Core.Units.CubicMetres(fill, "0.0")} - the building's dig only.");
        ModelChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    /// <summary>The ground under a point of this plan.</summary>
    private IEnumerable<Toposurface> GroundsAt(Point2D model) =>
        OnActiveLevel<Toposurface>().Where(ground => Terrains.HeightAt(ground, model) is not null);
}
