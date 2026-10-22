using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Sheets;
using BIMDesigner.Core.Views;

namespace BIMDesigner.Core.Architecture;

/// <summary>How a handle is drawn: a corner or a point to move, a "+" midway along to add one, an end, or an arrow up to raise it.</summary>
public enum HandleShape
{
    Corner,
    Insert,
    End,
    Raise,

    /// <summary>The middle of an arc, dragged to bow it more or less.</summary>
    Bend
}

/// <summary>
/// A handle on a selected railing, beam or piece of ground: what it is ("corner:2"), where it
/// is in plan and how high, whether it is dragged up and down rather than across, and how it
/// is drawn. Those dragged up and down are shown in 3D only.
/// </summary>
public sealed record ElementHandle(string Key, Point2D At, double Height, bool Vertical, HandleShape Shape);

/// <summary>
/// The handles on what is set out by points - a railing's corners, a beam's ends, the ground's
/// spot levels, a region's or a joist layout's corners - and what dragging each does: in plan
/// across, in 3D up and down too. Each drag is one step to undo, as a snapshot of what it moved.
/// </summary>
public static class ElementHandles
{
    /// <summary>Whether an element has handles of this kind.</summary>
    public static bool Has(Element element) =>
        element is Railing or Beam or Toposurface or GroundRegion or BeamSystem or Ramp or SitePad or PropertyLine or RoomSeparator or Callout { Parent.Kind: ViewKind.FloorPlan };

    /// <summary>The most spot levels shown with handles: past that the ground is reshaped by reading its points in again.</summary>
    public const int MostPoints = 2000;

    public static IReadOnlyList<ElementHandle> Of(BimDocument document, Element element)
    {
        var handles = new List<ElementHandle>();
        switch (element)
        {
            case Railing railing:
            {
                var type = Railings.TypeOf(document, railing);
                double Top(Point2D at) => Railings.Base(document, railing, at) + type.Height;
                for (var i = 0; i < railing.Path.Count; i++)
                    handles.Add(new ElementHandle($"corner:{i}", railing.Path[i], Top(railing.Path[i]), false, HandleShape.Corner));
                // Midway along each length: a "+" to add a corner to a straight one, the middle of an arc to bow it.
                var count = railing.Path.Count - (railing.Closed && railing.Path.Count >= 3 ? 0 : 1);
                for (var i = 0; i < count; i++)
                {
                    var curve = WallCurve.Of(railing.Path[i], railing.Path[(i + 1) % railing.Path.Count], railing.BulgeAt(i));
                    if (curve.Length < 1) continue;
                    var middle = curve.PointAt(curve.Length / 2);
                    handles.Add(curve.IsArc
                        ? new ElementHandle($"bend:{i}", middle, Top(middle), false, HandleShape.Bend)
                        : new ElementHandle($"insert:{i}", middle, Top(middle), false, HandleShape.Insert));
                }

                break;
            }

            case RoomSeparator separator:
            {
                var level = document.FindLevel(separator.LevelId)?.Elevation ?? 0;
                handles.Add(new ElementHandle("start", separator.Start, level, false, HandleShape.End));
                handles.Add(new ElementHandle("end", separator.End, level, false, HandleShape.End));
                break;
            }

            case Beam beam:
                handles.Add(new ElementHandle("start", beam.Start, Beams.Top(document, beam, 0), false, HandleShape.End));
                handles.Add(new ElementHandle("end", beam.End, Beams.Top(document, beam, 1), false, HandleShape.End));
                handles.Add(new ElementHandle("raise:start", beam.Start, Beams.Top(document, beam, 0) + 300, true, HandleShape.Raise));
                handles.Add(new ElementHandle("raise:end", beam.End, Beams.Top(document, beam, 1) + 300, true, HandleShape.Raise));
                break;

            case Toposurface ground when ground.Points.Count <= MostPoints:
                for (var i = 0; i < ground.Points.Count; i++)
                {
                    var point = ground.Points[i];
                    // The arrow raising it over it, as a beam's is, so it is not lost under the point.
                    handles.Add(new ElementHandle($"point:{i}", new Point2D(point.X, point.Y), point.Z, false, HandleShape.Corner));
                    handles.Add(new ElementHandle($"raise:{i}", new Point2D(point.X, point.Y), point.Z + RaiseOver, true, HandleShape.Raise));
                }

                break;

            case GroundRegion region:
                Outline(handles, region.Boundary, at => GroundRegions.HeightAt(document, region, at));
                break;

            case SitePad pad:
                Outline(handles, pad.Boundary, at => SitePads.HeightAt(document, pad, at));
                break;

            case PropertyLine line:
                Outline(handles, line.Boundary, _ => document.FindLevel(line.LevelId)?.Elevation ?? 0);
                break;

            // A plan callout's box: its corners, to crop it larger or smaller.
            case Callout { Parent.Kind: ViewKind.FloorPlan } callout:
            {
                var floor = document.FindLevel(callout.LevelId)?.Elevation ?? 0;
                var corners = Corners(callout.Region);
                for (var i = 0; i < 4; i++) handles.Add(new ElementHandle($"corner:{i}", corners[i], floor, false, HandleShape.Corner));
                break;
            }

            case BeamSystem system:
                Outline(handles, system.Boundary, _ => BeamSystems.Top(document, system));
                break;

            case Ramp ramp:
            {
                // Its anchored end, to move it; a point up it from there - or down it, from its head -
                // to turn it round that end; its side by that end, to widen it; a drawn ramp's
                // turns; and, in 3D, its head and its foot raised and lowered.
                var layout = Ramps.Layout(document, ramp);
                var foot = ramp.Anchor == RampAnchor.Foot;
                var (end, up, height) = foot ? (layout.Foot, layout.FootUp, layout.Bottom) : (layout.Head, layout.HeadUp * -1, layout.Top);
                var reach = Math.Min(1500, layout.Length / 2);
                double On(Point2D at) => layout.PieceAt(at)?.HeightAt(at) ?? height;
                handles.Add(new ElementHandle("start", end, height, false, HandleShape.Corner));
                handles.Add(new ElementHandle("turn", end + up * reach, On(end + up * reach), false, HandleShape.End));
                var side = end + up * Math.Min(300, reach / 2) + up.PerpendicularLeft() * (layout.Width / 2);
                handles.Add(new ElementHandle("width", side, On(side), false, HandleShape.End));
                if (ramp.Shape == RampShape.Custom)
                    for (var i = 1; i + 1 < layout.Walk.Count; i++)
                        handles.Add(new ElementHandle($"corner:{i}", layout.Walk[i], On(layout.Walk[i]), false, HandleShape.Corner));
                handles.Add(new ElementHandle("raise:head", layout.Head, layout.Top + RaiseOver, true, HandleShape.Raise));
                handles.Add(new ElementHandle("raise:foot", layout.Foot, layout.Bottom + RaiseOver, true, HandleShape.Raise));
                break;
            }
        }

        return handles;
    }

    /// <summary>A box's corners, anticlockwise from its lowest.</summary>
    private static Point2D[] Corners(BoundingBox2D box) =>
        new[] { new Point2D(box.MinX, box.MinY), new Point2D(box.MaxX, box.MinY), new Point2D(box.MaxX, box.MaxY), new Point2D(box.MinX, box.MaxY) };

    /// <summary>A closed outline's handles: a corner at each point, a "+" midway along each side.</summary>
    private static void Outline(List<ElementHandle> handles, IReadOnlyList<Point2D> outline, Func<Point2D, double> height)
    {
        for (var i = 0; i < outline.Count; i++)
        {
            handles.Add(new ElementHandle($"corner:{i}", outline[i], height(outline[i]), false, HandleShape.Corner));
            var middle = outline[i].MidpointTo(outline[(i + 1) % outline.Count]);
            handles.Add(new ElementHandle($"insert:{i}", middle, height(middle), false, HandleShape.Insert));
        }
    }

    /// <summary>
    /// A handle dragged to a point in plan - or, one dragged up and down, to a height. What it did,
    /// in words, and the handle to follow from now on: a "+" dragged becomes the corner it adds.
    /// </summary>
    public static (string Said, string Key) Drag(BimDocument document, Element element, ElementHandle handle, Point2D to, double height)
    {
        var (kind, index) = Parse(handle.Key);
        switch (element)
        {
            case Railing railing:
            {
                var path = railing.Path.ToList();
                if (kind == "insert" && index < path.Count)
                {
                    // The straight length split at the new corner: the others keep how they bow.
                    var at = index + 1 < path.Count ? index + 1 : path.Count;
                    var bulges = Enumerable.Range(0, path.Count).Select(railing.BulgeAt).ToList();
                    path.Insert(at, to);
                    bulges.Insert(at, 0);
                    railing.SetPath(path, bulges);
                    return ($"A corner added: {Railings.Length(railing) / 1000:0.00} m.", $"corner:{at}");
                }

                if (kind == "bend" && index < path.Count)
                {
                    // Dragged straight, it keeps the least of a bow while the drag lasts, so its handle stays a bend: straight once let go.
                    var (start, end) = (path[index], path[(index + 1) % path.Count]);
                    var bulge = WallCurve.BulgeThrough(start, end, to);
                    railing.SetBulge(index, Math.Abs(bulge) >= Bowed ? bulge : railing.BulgeAt(index) < 0 ? -Bowed : Bowed);
                    var curve = WallCurve.Of(start, end, railing.BulgeAt(index));
                    return (curve.IsArc ? $"An arc {curve.Length / 1000:0.00} m round, radius {Math.Abs(curve.Length / curve.Sweep) / 1000:0.00} m." : "Straight.", handle.Key);
                }

                if (kind == "corner" && index < path.Count)
                {
                    path[index] = to;
                    railing.SetPath(path);
                    return ($"{Railings.Length(railing) / 1000:0.00} m.", handle.Key);
                }

                break;
            }

            case Beam beam:
            {
                var level = document.FindLevel(beam.LevelId)?.Elevation ?? 0;
                var justification = Beams.Top(document, beam, 0) - Beams.Line(document, beam, 0);
                switch (handle.Key)
                {
                    case "start": beam.Start = to; break;
                    case "end": beam.End = to; break;
                    case "raise:start": beam.StartOffset = Math.Round(height - 300 - justification - level); break;
                    case "raise:end": beam.EndOffset = Math.Round(height - 300 - justification - level); break;
                    default: return (string.Empty, handle.Key);
                }

                return ($"{beam.Length / 1000:0.00} m, its underside at {Beams.Bottom(document, beam, 0):0} and {Beams.Bottom(document, beam, 1):0}.", handle.Key);
            }

            case RoomSeparator separator:
            {
                switch (handle.Key)
                {
                    case "start": separator.Start = to; break;
                    case "end": separator.End = to; break;
                    default: return (string.Empty, handle.Key);
                }

                return ($"{separator.Length / 1000:0.00} m.", handle.Key);
            }

            case Toposurface ground when index < ground.Points.Count:
            {
                var points = ground.Points.ToList();
                var point = points[index];
                points[index] = kind == "raise" ? point with { Z = Math.Round(height - RaiseOver) } : new Point3D(to.X, to.Y, point.Z);
                ground.SetPoints(points);
                return ($"Spot level at {Units.FormatLength(points[index].Z)}.", handle.Key);
            }

            case SitePad pad:
            {
                pad.SetBoundary(Moved(pad.Boundary, kind, index, to, out _));
                return ($"{Polygon2D.Area(pad.Boundary) / 1e6:0.0} m². {SitePads.Verdict(document, pad)}.", Following(pad.Boundary, to, handle.Key));
            }

            case PropertyLine line:
            {
                line.SetBoundary(Moved(line.Boundary, kind, index, to, out _));
                return ($"{Polygon2D.Area(line.Boundary) / 1e6:0.0} m². {PropertyLines.Verdict(document, line)}.", Following(line.Boundary, to, handle.Key));
            }

            case Callout callout when kind == "corner" && index is >= 0 and < 4:
            {
                // The corner across from it stays; the box is never smaller than a callout can be.
                var fixedCorner = Corners(callout.Region)[(index + 2) % 4];
                var away = new Vector2D(Math.Sign(to.X - fixedCorner.X), Math.Sign(to.Y - fixedCorner.Y));
                if (away.X == 0) away = away with { X = 1 };
                if (away.Y == 0) away = away with { Y = 1 };
                var corner = new Point2D(
                    Math.Abs(to.X - fixedCorner.X) < Callouts.Smallest ? fixedCorner.X + away.X * Callouts.Smallest : to.X,
                    Math.Abs(to.Y - fixedCorner.Y) < Callouts.Smallest ? fixedCorner.Y + away.Y * Callouts.Smallest : to.Y);
                callout.Region = new BoundingBox2D(Math.Min(corner.X, fixedCorner.X), Math.Min(corner.Y, fixedCorner.Y), Math.Max(corner.X, fixedCorner.X), Math.Max(corner.Y, fixedCorner.Y));
                var follow = Enumerable.Range(0, 4).OrderBy(i => Corners(callout.Region)[i].DistanceTo(corner)).First();
                return ($"Cropped to {callout.Region.Width / 1000:0.00} m by {callout.Region.Height / 1000:0.00} m.", $"corner:{follow}");
            }

            case GroundRegion region:
            {
                region.SetBoundary(Moved(region.Boundary, kind, index, to, out _));
                return ($"{Polygon2D.Area(region.Boundary) / 1e6:0.0} m².", Following(region.Boundary, to, handle.Key));
            }

            case BeamSystem system:
            {
                system.SetBoundary(Moved(system.Boundary, kind, index, to, out _));
                return ($"{BeamSystems.Joists(document, system).Count} joists.", Following(system.Boundary, to, handle.Key));
            }

            case Ramp ramp:
            {
                var level = document.FindLevel(ramp.LevelId)?.Elevation ?? 0;
                switch (handle.Key)
                {
                    case "start":
                        ramp.Start = to;
                        break;
                    case "turn":
                    {
                        // Up it from its foot; down it from its head.
                        var way = ramp.Anchor == RampAnchor.Foot ? to - ramp.Start : ramp.Start - to;
                        if (way.Length < 50) break;
                        ramp.Angle = (Math.Round(Math.Atan2(way.Y, way.X) * 180 / Math.PI, 1) % 360 + 360) % 360;
                        break;
                    }
                    case "width":
                        ramp.Width = Math.Clamp(Math.Round(2 * Math.Abs((to - ramp.Start).Cross(ramp.Up)) / 10) * 10, 600, 6000);
                        break;
                    case "raise:head":
                    {
                        var top = Math.Max(Ramps.Bottom(document, ramp) + Ramps.MinimumRise, Math.Round(height - RaiseOver));
                        if (ramp.TopLevelId is { } topId && document.FindLevel(topId) is { } upper) ramp.TopOffset = top - upper.Elevation;
                        else ramp.UnconnectedHeight = top - Ramps.Bottom(document, ramp);
                        break;
                    }
                    case "raise:foot":
                    {
                        var top = Ramps.Top(document, ramp);
                        var bottom = Math.Min(top - Ramps.MinimumRise, Math.Round(height - RaiseOver));
                        if (ramp.TopLevelId is null) ramp.UnconnectedHeight = top - bottom;
                        ramp.BaseOffset = bottom - level;
                        break;
                    }
                    default:
                        if (kind != "corner" || ramp.Shape != RampShape.Custom) return (string.Empty, handle.Key);
                        var points = Ramps.Layout(document, ramp).Walk.ToList();
                        if (index <= 0 || index >= points.Count - 1) return (string.Empty, handle.Key);
                        points[index] = to;

                        // Hung by its head, drawn down from it: its line from its head down again.
                        var down = ramp.Anchor == RampAnchor.Head;
                        if (down) points.Reverse();
                        Ramps.SetPath(ramp, points, down);
                        break;
                }

                return ($"{Ramps.Summary(document, ramp)}. {Ramps.Verdict(document, ramp)}.", handle.Key);
            }
        }

        return (string.Empty, handle.Key);
    }

    /// <summary>What a ramp's handles move, as it is now: where and which way it runs, how wide, how it turns, how high it starts and ends.</summary>
    private sealed record RampState(Point2D Start, double Angle, double Width, double[] Lengths, double[] Turns, double BaseOffset, double TopOffset, double UnconnectedHeight)
    {
        public bool Same(RampState other) =>
            Start == other.Start && Angle == other.Angle && Width == other.Width && Lengths.SequenceEqual(other.Lengths) && Turns.SequenceEqual(other.Turns)
            && BaseOffset == other.BaseOffset && TopOffset == other.TopOffset && UnconnectedHeight == other.UnconnectedHeight;
    }

    /// <summary>How far over a spot level its arrow raising it is, mm.</summary>
    private const double RaiseOver = 300;

    /// <summary>The least an arc is bowed while its handle drags it: straight, as near as makes no odds, but still an arc.</summary>
    private const double Bowed = 1e-6;

    /// <summary>
    /// The corner of an outline a drag goes on with: the one just put where the drag is - wherever
    /// it is in the outline now, turned round as an outline is when it is dragged inside out.
    /// </summary>
    private static string Following(IReadOnlyList<Point2D> outline, Point2D to, string key)
    {
        for (var i = 0; i < outline.Count; i++)
            if (outline[i].DistanceTo(to) < 1e-6) return $"corner:{i}";
        return key;
    }

    /// <summary>A closed outline with a corner moved, or one added midway along a side - and, added, the key of the corner it is.</summary>
    private static List<Point2D> Moved(IReadOnlyList<Point2D> outline, string kind, int index, Point2D to, out string? key)
    {
        var points = outline.ToList();
        key = null;
        if (kind == "insert" && index < points.Count)
        {
            points.Insert(index + 1, to);
            key = $"corner:{index + 1}";
        }
        else if (kind == "corner" && index < points.Count) points[index] = to;
        return points;
    }

    /// <summary>
    /// After a drag, what it left tidied: a corner dropped on the next taken out - a railing
    /// keeps two, an outline three - and a spot level dropped on another taken out, the ground
    /// keeping three.
    /// </summary>
    public static void Settle(Element element, string? key = null)
    {
        switch (element)
        {
            case Railing railing:
            {
                // An arc dragged all but straight: straight.
                if (key is not null && Parse(key) is ("bend", var bent) && Math.Abs(railing.BulgeAt(bent)) < 1e-4) railing.SetBulge(bent, 0);

                // A corner dropped on the next taken out, merged into the one before it: the length on
                // from it bows as the last length on from those merged into it did.
                var (path, count) = (railing.Path, railing.Path.Count);
                var (kept, last) = (new List<int>(), new List<int>());
                for (var i = 0; i < count; i++)
                {
                    if (kept.Count == 0 || path[kept[^1]].DistanceTo(path[i]) >= 50)
                    {
                        kept.Add(i);
                        last.Add(i);
                    }
                    else last[^1] = i;
                }

                // Round to its first again, the last dropped on the first: the length on to it bows as it did.
                if (railing.Closed && kept.Count > 1 && path[kept[^1]].DistanceTo(path[kept[0]]) < 50)
                {
                    kept.RemoveAt(kept.Count - 1);
                    last.RemoveAt(last.Count - 1);
                }

                if (kept.Count >= 2 && kept.Count != count)
                    railing.SetPath(kept.Select(i => path[i]).ToList(), last.Select(railing.BulgeAt).ToList());
                if (railing.Path.Count < 3) railing.Closed = false;
                break;
            }

            case GroundRegion region:
                region.SetBoundary(Merged(region.Boundary, closed: true, least: 3));
                break;

            case SitePad pad:
                pad.SetBoundary(Merged(pad.Boundary, closed: true, least: 3));
                break;

            case PropertyLine line:
                line.SetBoundary(Merged(line.Boundary, closed: true, least: 3));
                break;

            case BeamSystem system:
                system.SetBoundary(Merged(system.Boundary, closed: true, least: 3));
                break;

            case Toposurface ground when key is not null && Parse(key) is ("point", var moved) && moved < ground.Points.Count:
            {
                // The spot level dragged, dropped on another, taken out - and no other: points close together in a survey stay.
                var dropped = ground.Points[moved];
                if (ground.Points.Count > 3 && ground.Points.Where((_, i) => i != moved).Any(other => Math.Abs(other.X - dropped.X) < 50 && Math.Abs(other.Y - dropped.Y) < 50))
                    ground.SetPoints(ground.Points.Where((_, i) => i != moved).ToList());
                break;
            }
        }
    }

    /// <summary>Points with any within 50 of the one before taken out, so long as as many as are wanted are left.</summary>
    private static List<Point2D> Merged(IReadOnlyList<Point2D> points, bool closed, int least)
    {
        var kept = new List<Point2D>();
        foreach (var point in points)
            if (kept.Count == 0 || kept[^1].DistanceTo(point) >= 50) kept.Add(point);
        if (closed && kept.Count > 1 && kept[^1].DistanceTo(kept[0]) < 50) kept.RemoveAt(kept.Count - 1);
        return kept.Count >= least ? kept : points.ToList();
    }

    private static (string Kind, int Index) Parse(string key)
    {
        var parts = key.Split(':');
        return (parts[0], parts.Length > 1 && int.TryParse(parts[1], out var index) ? index : 0);
    }

    /// <summary>What a drag of a handle is called, for the undo list.</summary>
    public static string Name(Element element, string key) => element switch
    {
        Railing => key.StartsWith("insert", StringComparison.Ordinal) ? "Add Railing Corner" : key.StartsWith("bend", StringComparison.Ordinal) ? "Bend Railing" : "Move Railing Corner",
        Beam => key.StartsWith("raise", StringComparison.Ordinal) ? "Slope Beam" : "Move Beam End",
        RoomSeparator => "Move Room Separator End",
        Toposurface => key.StartsWith("raise", StringComparison.Ordinal) ? "Raise Ground Point" : "Move Ground Point",
        GroundRegion => "Reshape Region",
        SitePad => "Reshape Pad",
        PropertyLine => "Reshape Property Line",
        BeamSystem => "Reshape Joist Layout",
        Ramp => key switch
        {
            "start" => "Move Ramp",
            "turn" => "Turn Ramp",
            "width" => "Widen Ramp",
            "raise:head" or "raise:foot" => "Raise Ramp",
            _ => "Move Ramp Turn"
        },
        _ => "Move Handle"
    };

    /// <summary>What a handle moves, as it is now: to put back if the drag is undone.</summary>
    public static object Snapshot(Element element) => element switch
    {
        Railing railing => (railing.Path.ToList(), railing.Closed, railing.Bulges.ToList()),
        Beam beam => (beam.Start, beam.End, beam.StartOffset, beam.EndOffset),
        RoomSeparator separator => (separator.Start, separator.End),
        Toposurface ground => ground.Points.ToList(),
        GroundRegion region => region.Boundary.ToList(),
        SitePad pad => pad.Boundary.ToList(),
        PropertyLine line => line.Boundary.ToList(),
        BeamSystem system => system.Boundary.ToList(),
        Callout callout => callout.Region,
        Ramp ramp => new RampState(ramp.Start, ramp.Angle, ramp.Width, ramp.PathLengths.ToArray(), ramp.PathTurns.ToArray(), ramp.BaseOffset, ramp.TopOffset, ramp.UnconnectedHeight),
        _ => new object()
    };

    public static void Restore(Element element, object snapshot)
    {
        switch (element, snapshot)
        {
            case (Railing railing, ValueTuple<List<Point2D>, bool, List<double>> state):
                railing.SetPath(state.Item1, state.Item3);
                railing.Closed = state.Item2;
                break;
            case (Beam beam, ValueTuple<Point2D, Point2D, double, double> state):
                (beam.Start, beam.End, beam.StartOffset, beam.EndOffset) = state;
                break;
            case (RoomSeparator separator, ValueTuple<Point2D, Point2D> state):
                (separator.Start, separator.End) = state;
                break;
            case (Toposurface ground, List<Point3D> points):
                ground.SetPoints(points);
                break;
            case (GroundRegion region, List<Point2D> boundary):
                region.SetBoundary(boundary);
                break;
            case (SitePad pad, List<Point2D> boundary):
                pad.SetBoundary(boundary);
                break;
            case (PropertyLine line, List<Point2D> boundary):
                line.SetBoundary(boundary);
                break;
            case (BeamSystem system, List<Point2D> boundary):
                system.SetBoundary(boundary);
                break;
            case (Callout callout, BoundingBox2D region):
                callout.Region = region;
                break;
            case (Ramp ramp, RampState state):
                (ramp.Start, ramp.Angle, ramp.Width, ramp.BaseOffset, ramp.TopOffset, ramp.UnconnectedHeight) =
                    (state.Start, state.Angle, state.Width, state.BaseOffset, state.TopOffset, state.UnconnectedHeight);
                (ramp.PathLengths, ramp.PathTurns) = (state.Lengths.ToList(), state.Turns.ToList());
                break;
        }
    }

    /// <summary>Whether two snapshots of the same element are the same.</summary>
    public static bool Same(object a, object b) => (a, b) switch
    {
        (ValueTuple<List<Point2D>, bool, List<double>> x, ValueTuple<List<Point2D>, bool, List<double>> y) =>
            x.Item2 == y.Item2 && x.Item1.SequenceEqual(y.Item1) && x.Item3.SequenceEqual(y.Item3),
        (List<Point3D> x, List<Point3D> y) => x.SequenceEqual(y),
        (List<Point2D> x, List<Point2D> y) => x.SequenceEqual(y),
        (RampState x, RampState y) => x.Same(y),
        _ => Equals(a, b)
    };
}

/// <summary>A drag of a handle on a railing, a beam, the ground, a region or a joist layout - done, and undone.</summary>
public sealed class HandleEditCommand : IUndoableCommand
{
    private readonly Element _element;
    private readonly object _before;
    private readonly object _after;

    public HandleEditCommand(Element element, object before, object after, string name)
    {
        (_element, _before, _after) = (element, before, after);
        Name = name;
    }

    public string Name { get; }

    public void Redo() => ElementHandles.Restore(_element, _after);

    public void Undo() => ElementHandles.Restore(_element, _before);
}
