using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// The handles on a selected stair: its width, from either side; where it turns; the well
/// between a half turn's flights; a spiral or curved stair's inside and outside; which way it
/// goes; and in 3D, its top and bottom.
/// </summary>
public enum StairGripKind
{
    WidthLeft,
    WidthRight,
    Turn,
    Well,
    Inside,
    Outside,
    Flip,
    Rotate,
    Top,
    Bottom,
    PathPoint
}

/// <summary>
/// One handle on a stair: what it does, where it is in plan and how high, and the way it is
/// dragged - straight up and down for its top and bottom.
/// </summary>
public sealed record StairGrip(StairGripKind Kind, Point2D At, double Height, Vector2D Axis, int Index = 0)
{
    public bool Vertical => Kind is StairGripKind.Top or StairGripKind.Bottom;

    /// <summary>Clicked rather than dragged.</summary>
    public bool Clicked => Kind == StairGripKind.Flip;
}

/// <summary>Everything about a stair its handles change, to put back or put again - its walking line by what is in it.</summary>
public sealed record StairState(
    Point2D Start, double Angle, double Width, int? RisersBeforeTurn, double Well, double InnerRadius, bool TurnsLeft,
    double BaseOffset, double TopOffset, double UnconnectedHeight, StairShape Shape, double[] PathLengths, double[] PathTurns)
{
    public static StairState Of(Stair stair) => new(stair.Start, stair.Angle, stair.Width, stair.RisersBeforeTurn, stair.Well, stair.InnerRadius,
        stair.TurnsLeft, stair.BaseOffset, stair.TopOffset, stair.UnconnectedHeight, stair.Shape, stair.PathLengths.ToArray(), stair.PathTurns.ToArray());

    public void ApplyTo(Stair stair)
    {
        (stair.Start, stair.Angle, stair.Width, stair.RisersBeforeTurn, stair.Well, stair.InnerRadius, stair.TurnsLeft) =
            (Start, Angle, Width, RisersBeforeTurn, Well, InnerRadius, TurnsLeft);
        (stair.BaseOffset, stair.TopOffset, stair.UnconnectedHeight) = (BaseOffset, TopOffset, UnconnectedHeight);
        (stair.Shape, stair.PathLengths, stair.PathTurns) = (Shape, PathLengths.ToList(), PathTurns.ToList());
    }

    public bool Equals(StairState? other) =>
        other is not null && (Start, Angle, Width, RisersBeforeTurn, Well, InnerRadius, TurnsLeft, BaseOffset, TopOffset, UnconnectedHeight, Shape) ==
        (other.Start, other.Angle, other.Width, other.RisersBeforeTurn, other.Well, other.InnerRadius, other.TurnsLeft, other.BaseOffset, other.TopOffset, other.UnconnectedHeight, other.Shape)
        && PathLengths.SequenceEqual(other.PathLengths) && PathTurns.SequenceEqual(other.PathTurns);

    public override int GetHashCode() => HashCode.Combine(Start, Angle, Width, Shape, PathLengths.Length, PathTurns.Length);
}

/// <summary>A stair changed by its handles, as one step to undo.</summary>
public sealed class StairEditCommand : IUndoableCommand
{
    private readonly Stair _stair;
    private readonly StairState _before;
    private readonly StairState _after;

    public StairEditCommand(Stair stair, StairState before, StairState after, string name)
    {
        (_stair, _before, _after, Name) = (stair, before, after, name);
    }

    public string Name { get; }

    public void Redo() => _after.ApplyTo(_stair);

    public void Undo() => _before.ApplyTo(_stair);
}

/// <summary>
/// A stair reshaped by dragging its handles, as Revit's, Archicad's and Vectorworks' are: a
/// side pulled out to widen it, the other side staying put; the end of its first flight
/// dragged up or down it to turn a riser sooner or later; a half turn's second flight pulled
/// away to widen the well; a spiral's or curve's inside or outside edge pulled in or out round
/// the same centre; turned round its foot; and in 3D, its top or bottom pulled up or down.
/// It re-plans as it goes - risers, going, opening - and says what it has become.
/// </summary>
public static class StairGrips
{
    private const double Ahead = 400;

    public static IReadOnlyList<StairGrip> Of(BimDocument document, Stair stair)
    {
        var layout = Stairs.Layout(document, stair);
        var grips = new List<StairGrip>();
        var up = layout.Axis;
        var left = up.PerpendicularLeft();
        var half = layout.WidestFlight / 2;
        double HeightAt(double along) => Math.Min(layout.Top, layout.Bottom + (along / layout.Going + 1) * layout.Rise);

        if (layout.Centre is { } centre && layout.WalkingLine.Count > 1)
        {
            // A quarter of the way round, or less: on its inside and outside edges.
            var walk = layout.WalkingLine;
            var k = Math.Max(1, walk.Count / 4);
            var outward = (walk[k] - centre).NormalisedOrDefault(Vector2D.UnitX);
            var height = layout.Bottom + (layout.Top - layout.Bottom) * k / (walk.Count - 1.0);
            grips.Add(new StairGrip(StairGripKind.Inside, centre + outward * layout.InnerRadius, height, outward * -1));
            grips.Add(new StairGrip(StairGripKind.Outside, centre + outward * layout.OuterRadius, height, outward));
        }
        else
        {
            // Halfway up its first flight, each side.
            var (from, to) = layout.TopEdge;
            var reach = layout.TurnAt is { } turnAt ? (turnAt - layout.Origin).Dot(up) : (from.MidpointTo(to) - layout.Origin).Dot(up);
            var along = Math.Max(0, reach) / 2;
            grips.Add(new StairGrip(StairGripKind.WidthLeft, layout.Origin + up * along + left * half, HeightAt(along), left));
            grips.Add(new StairGrip(StairGripKind.WidthRight, layout.Origin + up * along - left * half, HeightAt(along), left * -1));

            if (layout.TurnAt is { } turn && layout.TurnRisers is { } risers)
                grips.Add(new StairGrip(StairGripKind.Turn, turn, layout.Bottom + risers * layout.Rise, up));
            if (layout.WellAt is { } well && layout.WellAxis is { } away)
                grips.Add(new StairGrip(StairGripKind.Well, well, (layout.Bottom + layout.Top) / 2 + layout.Rise, away));
        }

        // A drawn stair's walking line: a handle at each turn and at its head.
        for (var i = 1; i < layout.PathPoints.Count; i++)
            grips.Add(new StairGrip(StairGripKind.PathPoint, layout.PathPoints[i], layout.Bottom + (layout.Top - layout.Bottom) * i / (layout.PathPoints.Count - 1.0),
                default, i));

        grips.Add(new StairGrip(StairGripKind.Rotate, layout.Origin - up * Ahead, layout.Bottom, up * -1));
        if (stair.Shape != StairShape.Bifurcated)
            grips.Add(new StairGrip(StairGripKind.Flip, layout.Origin - up * (Ahead / 2) + left * (half + 250), layout.Bottom, left));

        var (head, foot) = layout.TopEdge;
        grips.Add(new StairGrip(StairGripKind.Top, head.MidpointTo(foot), layout.Top, up));
        grips.Add(new StairGrip(StairGripKind.Bottom, layout.Origin, layout.Bottom, up));
        return grips;
    }

    /// <summary>What dragging a handle is called, for the undo list.</summary>
    public static string Name(StairGripKind kind) => kind switch
    {
        StairGripKind.WidthLeft or StairGripKind.WidthRight or StairGripKind.Outside => "Widen Stair",
        StairGripKind.Turn => "Move Stair Turn",
        StairGripKind.Well => "Widen Stair Well",
        StairGripKind.Inside => "Change Stair Radius",
        StairGripKind.Flip => "Flip Stair",
        StairGripKind.Rotate => "Rotate Stair",
        StairGripKind.PathPoint => "Reshape Stair",
        _ => "Change Stair Height"
    };

    /// <summary>
    /// A handle dragged to a point in plan - or, for its top and bottom, to a height. The stair
    /// changes as far as it can and says what it now is, or why it went no further - and, unless
    /// told not to while the mouse is still moving, whether it meets its regulations.
    /// </summary>
    public static string Drag(BimDocument document, Stair stair, StairGrip grip, Point2D to, double height, bool check = true)
    {
        var layout = Stairs.Layout(document, stair);
        var up = layout.Axis;
        var left = up.PerpendicularLeft();
        string said;

        // Widening it or its well leaves its turn where it is, rather than re-centring it - as long as it does widen.
        var (risersBefore, widthBefore, wellBefore) = (stair.RisersBeforeTurn, stair.Width, stair.Well);
        if (grip.Kind is StairGripKind.WidthLeft or StairGripKind.WidthRight or StairGripKind.Well && stair.RisersBeforeTurn is null)
            stair.RisersBeforeTurn = layout.TurnRisers;

        switch (grip.Kind)
        {
            case StairGripKind.WidthLeft or StairGripKind.WidthRight:
            {
                // The other side stays where it is; a split stair widens both ways about its middle.
                var side = grip.Kind == StairGripKind.WidthLeft ? left : left * -1;
                var ratio = layout.WidestFlight / layout.Width;
                var widest = stair.Shape == StairShape.Bifurcated
                    ? 2 * Math.Abs((to - layout.Origin).Dot(left))
                    : (to - (layout.Origin - side * (layout.WidestFlight / 2))).Dot(side);
                var width = Math.Clamp(Math.Round(widest / ratio / 10) * 10, 500, 5000);
                if (stair.Shape != StairShape.Bifurcated) stair.Start += side * ((width - layout.Width) * ratio / 2);
                stair.Width = width;
                said = width <= 500 ? "500 wide: it is no narrower" : $"{width:0} wide";
                break;
            }

            case StairGripKind.Turn:
            {
                // Balanced winders that would have too few treads to share the turn among stay where they were.
                var was = stair.RisersBeforeTurn;
                var wanted = Math.Max(1, (layout.TurnRisers ?? 1) + (int)Math.Round((to - grip.At).Dot(up) / layout.Going));
                stair.RisersBeforeTurn = wanted;
                var next = Stairs.Layout(document, stair);
                var got = next.TurnRisers ?? wanted;
                if (next.Newels.Count != layout.Newels.Count)
                {
                    stair.RisersBeforeTurn = was;
                    got = layout.TurnRisers ?? wanted;
                }
                else if (got != wanted) stair.RisersBeforeTurn = got;
                said = got == wanted
                    ? $"It turns after {got} risers"
                    : $"It turns after {got} risers: it can turn no {(wanted < got ? "sooner" : "later")}, with the treads it has";
                break;
            }

            case StairGripKind.Well:
            {
                var well = Math.Clamp(Math.Round((stair.Well + (to - grip.At).Dot(grip.Axis)) / 10) * 10, 0, 3000);
                stair.Well = well;
                said = $"A well of {well:0} between its flights";
                break;
            }

            case StairGripKind.Inside or StairGripKind.Outside when layout.Centre is { } centre:
            {
                // Round the same centre: its start slides along to keep it there.
                var reach = (to - centre).Length;
                var spiral = stair.Shape == StairShape.Spiral;
                var (inner, width) = grip.Kind == StairGripKind.Inside
                    ? (Math.Clamp(Math.Round(reach / 10) * 10, spiral ? 40 : 200, 10000), layout.Width)
                    : (layout.InnerRadius, Math.Clamp(Math.Round((reach - layout.InnerRadius) / 10) * 10, 500, 5000));
                var towards = (centre - stair.Start).NormalisedOrDefault(left * -1);
                stair.Start = centre - towards * (inner + width / 2);
                (stair.InnerRadius, stair.Width) = (inner, width);
                said = grip.Kind == StairGripKind.Inside ? $"{inner:0} from its centre to its inside" : $"{width:0} wide";
                break;
            }

            case StairGripKind.Rotate:
            {
                // Its way up points away from the handle, in whole degrees - to 15° when near one.
                var way = stair.Start - to;
                if (way.Length < 1) return "Drag round its foot to turn it.";
                var angle = Math.Atan2(way.Y, way.X) * 180 / Math.PI;
                var fifteen = Math.Round(angle / 15) * 15;
                angle = Math.Abs(angle - fifteen) < 3 ? fifteen : Math.Round(angle);
                stair.Angle = (angle % 360 + 360) % 360;
                said = $"Going up at {stair.Angle:0}°";
                break;
            }

            case StairGripKind.PathPoint when grip.Index < layout.PathPoints.Count:
            {
                var points = layout.PathPoints.ToList();
                points[grip.Index] = to;
                SetPath(stair, points);
                said = stair.PathTurns.Count == 0 ? "One straight flight" : $"Turning {string.Join(", then ", stair.PathTurns.Select(turn => Turning(turn * (stair.TurnsLeft ? -1 : 1))))}";
                break;
            }

            case StairGripKind.Top:
            {
                var top = Math.Round(height / 10) * 10;
                if (top - layout.Bottom < 300) top = layout.Bottom + 300;
                if (stair.TopLevelId is not null && document.FindLevel(stair.TopLevelId.Value) is not null)
                    stair.TopOffset += top - layout.Top;
                else
                    stair.UnconnectedHeight = top - layout.Bottom;
                said = $"{top - layout.Bottom:0} high";
                break;
            }

            case StairGripKind.Bottom:
            {
                var bottom = Math.Round(height / 10) * 10;
                if (layout.Top - bottom < 300) bottom = layout.Top - 300;
                stair.BaseOffset += bottom - layout.Bottom;
                said = $"Its foot {stair.BaseOffset:+0;-0;0} off its floor";
                break;
            }

            default:
                return string.Empty;
        }

        if (grip.Kind is StairGripKind.WidthLeft or StairGripKind.WidthRight or StairGripKind.Well && stair.Width == widthBefore && stair.Well == wellBefore)
            stair.RisersBeforeTurn = risersBefore;

        var now = Stairs.Layout(document, stair);
        return $"{said}: {now.Risers} risers of {now.Rise:0.#}, going {now.Going:0}." + (check ? $" {Stairs.Verdict(document, stair)}." : "");
    }

    /// <summary>What a stair is, in a line: its risers and going, and whether it meets its regulations.</summary>
    public static string Summary(BimDocument document, Stair stair)
    {
        var layout = Stairs.Layout(document, stair);
        return $"{layout.Risers} risers of {layout.Rise:0.#}, going {layout.Going:0}, {layout.Width:0} wide. {Stairs.Verdict(document, stair)}.";
    }

    /// <summary>A turn, in words: "90° right", "45° left", "straight on".</summary>
    private static string Turning(double degrees) =>
        Math.Abs(degrees) < 0.5 ? "straight on" : $"{Math.Abs(degrees):0}° {(degrees > 0 ? "right" : "left")}";

    /// <summary>
    /// A drawn stair's walking line set from points - its foot, each turn, its head: where it
    /// starts and which way it goes up, how long each stretch is, and how far it turns at each
    /// corner, to the nearest 15° and no more than a right angle either way. Points nearer than
    /// 10 to the one before are the same point. It becomes a drawn stair, if it was not.
    /// </summary>
    public static void SetPath(Stair stair, IReadOnlyList<Point2D> points)
    {
        var kept = new List<Point2D>();
        foreach (var point in points)
            if (kept.Count == 0 || (point - kept[^1]).Length > 10) kept.Add(point);
        if (kept.Count < 2) return;

        // Each turn snapped, and the next measured from the way it then goes.
        var mirror = stair.TurnsLeft ? -1.0 : 1.0;
        var first = kept[1] - kept[0];
        var heading = Math.Atan2(first.Y, first.X);
        var (lengths, turns) = (new List<double> { first.Length }, new List<double>());
        for (var j = 1; j + 1 < kept.Count; j++)
        {
            var stretch = kept[j + 1] - kept[j];
            var bent = Math.Atan2(stretch.Y, stretch.X) - heading;
            bent = Math.Atan2(Math.Sin(bent), Math.Cos(bent));
            var turn = Math.Clamp(Math.Round(-bent * 180 / Math.PI / 15) * 15, -90, 90);
            heading -= turn * Math.PI / 180;
            turns.Add(turn * mirror);
            lengths.Add(stretch.Length);
        }

        stair.Start = kept[0];
        stair.Angle = (Math.Atan2(first.Y, first.X) * 180 / Math.PI % 360 + 360) % 360;
        (stair.PathLengths, stair.PathTurns, stair.Shape) = (lengths, turns, StairShape.Custom);
    }

    /// <summary>
    /// A stair turned the other way: a turning, spiral or curved stair mirrored across its first
    /// flight, to turn left rather than right; a straight one reversed, its head now its foot.
    /// </summary>
    public static string Flip(BimDocument document, Stair stair)
    {
        if (stair.Shape == StairShape.Straight)
        {
            var (from, to) = Stairs.Layout(document, stair).TopEdge;
            stair.Start = from.MidpointTo(to);
            stair.Angle = ((stair.Angle + 180) % 360 + 360) % 360;
            Stairs.SwapSides(stair);
            return "Reversed: it now goes up the other way.";
        }

        stair.TurnsLeft = !stair.TurnsLeft;
        return stair.TurnsLeft ? "It now turns left going up." : "It now turns right going up.";
    }
}
