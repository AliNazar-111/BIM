using BIMDesigner.Core.Parameters;
using BIMDesigner.Core;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.UI.Controls;

/// <summary>
/// Join/Unjoin Roof and Dormer Opening on the plan (Revit's tools of the same names): how a
/// dormer with walls is married to the roof it comes out of.
/// </summary>
public partial class PlanView
{
    /// <summary>The roof, and the edge of it, clicked first by Join Roof.</summary>
    private (Roof Roof, int Edge)? _roofJoinFirst;

    /// <summary>The roof Dormer Opening is cutting, while it waits for the dormer to be clicked.</summary>
    private Roof? _dormerHost;

    /// <summary>The roofs on this storey drawn by footprint, top first where they overlap.</summary>
    private IEnumerable<Roof> FootprintRoofs() =>
        OnActiveLevel<Roof>().Where(roof => !roof.IsExtrusion && roof.Boundary.Count >= 3)
            .OrderByDescending(roof => Document is null ? 0 : roof.GetBottomElevation(Document));

    /// <summary>The roof edge nearest a click, within a few pixels.</summary>
    private (Roof Roof, int Edge)? RoofEdgeAt(Point2D raw)
    {
        var reach = 10 / PixelsPerMm;
        (Roof, int)? best = null;

        foreach (var roof in FootprintRoofs())
        {
            var boundary = roof.Boundary;
            for (var i = 0; i < boundary.Count; i++)
            {
                var distance = Line2D.DistanceFromSegment(raw, boundary[i], boundary[(i + 1) % boundary.Count]);
                if (distance >= reach) continue;

                reach = distance;
                best = (roof, i);
            }
        }

        return best;
    }

    /// <summary>The roof over a point, other than one to leave out - the highest where several are.</summary>
    private Roof? RoofOver(Point2D raw, Roof? except) =>
        OnActiveLevel<Roof>()
            .Where(roof => !ReferenceEquals(roof, except) && roof.Contains(raw))
            .OrderByDescending(roof => Document is null ? 0 : roof.TopAt(Document, raw))
            .FirstOrDefault();

    /// <summary>
    /// A click with Join/Unjoin Roof: first the edge of the roof to carry back - a dormer's back
    /// edge - then the roof it runs into. A roof already joined, clicked, is unjoined. Public so
    /// it can be driven from tests.
    /// </summary>
    public bool JoinRoofAt(Point2D raw)
    {
        if (Document is null) return false;

        if (_roofJoinFirst is not var (first, edge))
        {
            if (RoofEdgeAt(raw) is not var (roof, picked))
            {
                HintChanged?.Invoke(this, "Click an edge of the roof to join - the back edge of a dormer's roof, the one facing the roof it runs into.");
                return false;
            }

            if (roof.JoinedTo is not null)
            {
                Apply(new SetRoofJoinCommand(roof, null));
                HintChanged?.Invoke(this, "Unjoined: the roof stands on its own again, as far back as it was carried.");
                ModelChanged?.Invoke(this, EventArgs.Empty);
                InvalidateVisual();
                return true;
            }

            _roofJoinFirst = (roof, picked);
            HintChanged?.Invoke(this, "Now click the roof it should run back into.");
            InvalidateVisual();
            return true;
        }

        if (RoofOver(raw, first) is not { } target)
        {
            HintChanged?.Invoke(this, "Click inside the roof it should run into.");
            return false;
        }

        _roofJoinFirst = null;

        if (RoofJoin.Join(Document, first, edge, target, out var problem) is not { } command)
        {
            HintChanged?.Invoke(this, problem!);
            InvalidateVisual();
            return false;
        }

        Apply(command);
        HintChanged?.Invoke(this, "Joined: carried back into the roof and trimmed where it meets it, in valleys. " +
                                  "Select that roof and use Dormer Opening to cut the hole under it.");
        ModelChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
        return true;
    }

    /// <summary>
    /// Starts Dormer Opening on the selected roof - Revit's Openings > Dormer. The next click is
    /// on the dormer's roof; clicking a dormer whose opening is already cut closes it again.
    /// </summary>
    public bool BeginDormerOpening()
    {
        if (Document is null || _selection is not [Roof host]) return false;

        SetTool(PlanTool.DormerOpening);
        _dormerHost = host;
        HintChanged?.Invoke(this, "Click the dormer's roof - joined to this one, on its walls. The roof is cut away under it, between the walls.");
        return true;
    }

    /// <summary>A click with Dormer Opening: the dormer to open the roof for. Public so it can be driven from tests.</summary>
    public bool DormerOpeningAt(Point2D raw)
    {
        if (Document is null || _dormerHost is not { } host) return false;

        var dormer = OnActiveLevel<Roof>()
            .Where(roof => !ReferenceEquals(roof, host) && roof.Contains(raw))
            .OrderByDescending(roof => roof.JoinedTo == host.Id)
            .FirstOrDefault();

        if (dormer is null)
        {
            HintChanged?.Invoke(this, "Click inside the dormer's roof.");
            return false;
        }

        var open = !host.DormerOpenings.Contains(dormer.Id);
        Apply(new SetDormerOpeningCommand(host, dormer.Id, open));

        var hole = RoofJoin.Openings(Document, host).Sum(Polygon2D.Area);
        HintChanged?.Invoke(this, open
            ? hole > 0
                ? $"Opened: {Units.FormatArea(hole)} of the roof cut away under the dormer, between its walls."
                : "Marked for the dormer, but there is nothing under it to cut yet: join the dormer's roof to this one and attach its walls' tops to it."
            : "The opening is closed again.");

        _dormerHost = null;
        SetTool(PlanTool.Select);
        Select(host);
        ModelChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private bool CancelRoofJoin()
    {
        var pending = _roofJoinFirst is not null || _dormerHost is not null;
        _roofJoinFirst = null;
        _dormerHost = null;
        return pending;
    }
}

/// <summary>A dormer click the roof could not take: where, why, and a dormer that would fit there instead, if one would.</summary>
public sealed class DormerRefusedEventArgs : EventArgs
{
    public DormerRefusedEventArgs(Point2D at, string problem, DormerSettings? instead, Point2D? fitsAt = null)
    {
        At = at;
        Problem = problem;
        Instead = instead;
        FitsAt = fitsAt;
    }

    public Point2D At { get; }

    /// <summary>The nearest place along the slope where the dormer asked for does fit, if there is one.</summary>
    public Point2D? FitsAt { get; }

    public string Problem { get; }

    public DormerSettings? Instead { get; }
}

/// <summary>The Dormer tool on the plan: a whole dormer, clicked onto a roof's slope.</summary>
public partial class PlanView
{
    private Point2D? _dormerHover;

    /// <summary>The dormer the next click makes - shape and size, from the options bar.</summary>
    public DormerSettings Dormer { get; set; } = DormerSettings.Default;

    /// <summary>
    /// Raised when a dormer click is refused - so the window can say why, out loud rather than
    /// on the status bar, and offer one that fits.
    /// </summary>
    public event EventHandler<DormerRefusedEventArgs>? DormerRefused;

    /// <summary>The roof a dormer would go on at a point: the topmost there not already joined to another.</summary>
    private Roof? DormerRoofAt(Point2D raw) =>
        Document is null
            ? null
            : OnActiveLevel<Roof>()
                .Where(candidate => candidate.Contains(raw) && candidate.JoinedTo is null)
                .OrderByDescending(candidate => candidate.TopAt(Document, raw))
                .FirstOrDefault();

    private WallType? DormerWallType() =>
        Document is null
            ? null
            : Document.FindType<WallType>(ActiveWallTypeId)
              ?? Document.TypesOf<WallType>().FirstOrDefault(type => type.Name.StartsWith("Exterior"))
              ?? Document.TypesOf<WallType>().FirstOrDefault();

    /// <summary>
    /// Where a click puts the dormer's front wall: on the drawing grid, unless that would take
    /// it off the roof - a click just inside the eave stays on the roof.
    /// </summary>
    private Point2D DormerPoint(Roof roof, Point2D raw)
    {
        var snapped = SnapToGrid(raw);
        return roof.Contains(snapped) ? snapped : raw;
    }

    /// <summary>
    /// A click with the Dormer tool: a dormer on the roof under it, its front wall across the
    /// slope there - walls, roof, join and opening in one step. Public so it can be driven from
    /// tests.
    /// </summary>
    public bool DormerAt(Point2D raw)
    {
        if (Document is null) return false;

        if (DormerRoofAt(raw) is not { } roof)
        {
            Refuse(raw, "Click on a roof's slope, where the dormer's front wall should be.", null);
            return false;
        }

        var wallType = DormerWallType();
        var at = DormerPoint(roof, raw);

        var command = Dormers.Add(Document, roof, at, Dormer, wallType?.Id ?? Guid.Empty, out var problem, out var used);
        if (command is null)
        {
            var instead = Dormer.Shape == DormerShape.Shed || wallType is null
                ? null
                : Dormers.InsteadAt(Document, roof, at, Dormer, wallType.Structure.TotalWidth);
            var fitsAt = wallType is null ? null : Dormers.NearestThatFits(Document, roof, at, Dormer, wallType.Structure.TotalWidth);

            HintChanged?.Invoke(this, problem!);
            DormerRefused?.Invoke(this, new DormerRefusedEventArgs(raw, problem!, instead, fitsAt));
            return false;
        }

        // Already done: recorded as one step, not done again.
        History?.Record(command);

        var shallower = used.Slope < Dormer.Slope - 0.01
            ? $"Its slope is {used.Slope:0.#}° - shallower than asked, so it stands higher under the ridge. "
            : "";
        HintChanged?.Invoke(this, (used.Height < Dormer.Height - 1
                ? $"{EnumText.Humanise(used.Shape)} dormer added, {Units.FormatLength(used.Height)} high - as high as fits under the ridge here. "
                : $"{EnumText.Humanise(used.Shape)} dormer added. ") + shallower +
            "Select it to change its shape and size in Properties - its walls and roof follow together; TAB picks one part.");

        ModelChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
        return true;
    }

    private void Refuse(Point2D at, string problem, DormerSettings? instead)
    {
        HintChanged?.Invoke(this, problem);
        DormerRefused?.Invoke(this, new DormerRefusedEventArgs(at, problem, instead));
    }

    private void DormerHover(Point2D raw)
    {
        _dormerHover = raw;
        InvalidateVisual();
    }

    private static readonly System.Windows.Media.Pen DormerMissPen =
        Frozen(new System.Windows.Media.Pen(Frozen(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE5, 0x3E, 0x3E))), 1.6)
        {
            DashStyle = new System.Windows.Media.DashStyle(new[] { 4.0, 3.0 }, 0)
        });

    /// <summary>
    /// Where the dormer would go - its front wall across the slope, its sides running up it -
    /// and whether it fits there: as high as it would be made, or in red when the roof is too
    /// low for it, before anything is clicked.
    /// </summary>
    private void DrawDormerPreview(System.Windows.Media.DrawingContext dc)
    {
        if (ActiveTool != PlanTool.Dormer || _dormerHover is not { } hover || Document is null) return;
        if (DormerRoofAt(hover) is not { } roof) return;

        var at = DormerPoint(roof, hover);
        if (roof.Surface(Document).FaceAt(at) is not { } face || face.Plane.Rise < 0.05) return;

        var thickness = DormerWallType()?.Structure.TotalWidth ?? 300;
        var size = Dormers.SizeAt(Document, roof, at, Dormer, thickness, out _);
        var fitting = size is not null;
        var height = size?.Height ?? Dormer.Height;
        var eased = size is { } made && made.Slope < Dormer.Slope - 0.01;

        var uphill = new Vector2D(face.Plane.A, face.Plane.B) / face.Plane.Rise;
        var across = uphill.PerpendicularLeft();
        var left = at - across * (Dormer.Width / 2);
        var right = at + across * (Dormer.Width / 2);
        var run = height / face.Plane.Rise;

        var pen = fitting ? SketchPreviewPen : DormerMissPen;
        foreach (var (from, to) in new[] { (left + uphill * run, left), (left, right), (right, right + uphill * run) })
            dc.DrawLine(pen, ModelToScreen(from), ModelToScreen(to));

        var slope = eased ? $" at {size!.Slope:0.#}°" : "";
        var label = fitting
            ? height < Dormer.Height - 1
                ? $"{EnumText.Humanise(Dormer.Shape)}, {Units.FormatLength(height)} high{slope} - all that fits here"
                : $"{EnumText.Humanise(Dormer.Shape)}, {Units.FormatLength(height)} high{slope}"
            : Dormer.Shape != DormerShape.Shed && Dormers.InsteadAt(Document, roof, at, Dormer, thickness) is { } shed
                ? $"Too low here for a {EnumText.Humanise(Dormer.Shape).ToLowerInvariant()} dormer - a shed {Units.FormatLength(shed.Height)} high fits"
                : "Too low here for a dormer - a steeper roof would take one";

        var text = new System.Windows.Media.FormattedText(
            label,
            System.Globalization.CultureInfo.CurrentCulture,
            System.Windows.FlowDirection.LeftToRight,
            new System.Windows.Media.Typeface("Segoe UI"),
            11,
            pen.Brush,
            System.Windows.Media.VisualTreeHelper.GetDpi(this).PixelsPerDip);

        dc.DrawText(text, ModelToScreen(hover) + new System.Windows.Vector(14, 10));
    }
}
