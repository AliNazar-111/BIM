using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// What a room has over it, point by point: the room is as tall, at each point of its floor, as
/// the lowest thing over that point - a ceiling, the underside of the floor of the storey above,
/// a roof. Under a sloping roof that changes across the room: tall in the middle, nothing at the
/// eaves. Where nothing is over it, its upper limit is its height.
/// </summary>
public sealed record RoomSpace(
    double Area, double Volume, double Lowest, double Highest, double AreaCounted, double AreaFullHeight, bool UnderRoof, bool Bounded)
{
    /// <summary>How much of the room has the full headroom a habitable room wants, as a share of its area.</summary>
    public double FullHeightShare => Area > 0 ? AreaFullHeight / Area : 0;
}

/// <summary>
/// Rooms under roofs: their volume, headroom, and the area that counts. Measured as surveyors
/// measure: floor with less than 1.5 m of headroom is not counted (RICS Code of Measuring
/// Practice, and the Nationally Described Space Standard), and a room is full height where it
/// has 2.3 m - which the space standard wants over at least three quarters of it.
/// </summary>
public static class RoomHeadroom
{
    /// <summary>Headroom below which floor is not counted in a room's area, mm.</summary>
    public const double CountedHeadroom = 1500;

    /// <summary>Headroom a room has at full height, mm.</summary>
    public const double FullHeadroom = 2300;

    /// <summary>How much of a room the space standard wants at full height.</summary>
    public const double FullHeightShare = 0.75;

    /// <summary>The finest a room is measured over, mm; a big room is measured more coarsely, never in more than this many pieces.</summary>
    private const double Cell = 100, MostCells = 40000;

    /// <summary>The floor of a room, in project elevation.</summary>
    public static double Floor(BimDocument document, Room room) =>
        (document.FindLevel(room.LevelId)?.Elevation ?? 0) + room.BaseOffset;

    /// <summary>What may be over a room: the ceilings, floors and roofs over its outline that are above its floor.</summary>
    public static IReadOnlyList<Slab> Covers(BimDocument document, Room room, IReadOnlyList<Point2D> outline)
    {
        var floor = Floor(document, room);
        var bounds = Bounds(outline);
        return document.Elements.OfType<Slab>()
            .Where(slab => slab.Boundary.Count >= 3 && Overlaps(bounds, Bounds(slab.Boundary)))
            .Where(slab => slab switch
            {
                // A flat roof the room stands on - one doing a floor's job - is under it, not over it.
                Roof roof => !roof.IsExtrusion && (roof.Surface(document).IsFlat
                    ? roof.GetBottomElevation(document) > floor + 1
                    : roof.RidgeElevation(document) > floor + 1),
                _ => slab.GetBottomElevation(document) > floor + 1
            })
            .ToList();
    }

    /// <summary>
    /// The headroom over a point of a room, and what gives it: the lowest ceiling, floor or roof
    /// over it, or null where nothing is. A roof coming down below the floor leaves no headroom.
    /// </summary>
    public static (double Headroom, Slab By)? Over(BimDocument document, double floor, Point2D at, IReadOnlyList<Slab> covers)
    {
        (double, Slab)? lowest = null;
        foreach (var slab in covers)
        {
            if (!slab.Contains(at)) continue;

            var underside = slab is Roof roof ? roof.UndersideAt(document, at) : slab.GetBottomElevation(document);
            var headroom = Math.Max(0, underside - floor);
            if (lowest is null || headroom < lowest.Value.Item1) lowest = (headroom, slab);
        }

        return lowest;
    }

    /// <summary>
    /// A room measured under what is over it: its volume, its lowest and highest headroom, and
    /// the area at 1.5 m headroom and over, and at full height. Null for a room not enclosed.
    /// </summary>
    public static RoomSpace? Measure(BimDocument document, Room room)
    {
        var boundary = room.GetBoundary(document);
        if (!boundary.IsEnclosed || boundary.Polygon.Count < 3) return null;

        var outline = boundary.Polygon;
        var floor = Floor(document, room);
        var covers = Covers(document, room, outline);
        var limit = room.Height;

        // Nothing over it: as tall as its upper limit all over.
        if (covers.Count == 0)
            return new RoomSpace(boundary.Area, boundary.Area * limit, limit, limit,
                limit >= CountedHeadroom ? boundary.Area : 0, limit >= FullHeadroom ? boundary.Area : 0, false, false);

        // Measured a cell at a time, at the middle of each - exact under anything level or
        // sloping in one plane, close where a ridge or a ceiling's edge crosses a cell.
        var bounds = Bounds(outline);
        var polygonArea = Polygon2D.Area(outline);
        var cell = Math.Max(Cell, Math.Sqrt(polygonArea / MostCells));
        double area = 0, volume = 0, counted = 0, full = 0, lowest = double.MaxValue, highest = 0;
        var underRoof = false;
        var bounded = false;

        for (var x = bounds.MinX + cell / 2; x < bounds.MaxX; x += cell)
        for (var y = bounds.MinY + cell / 2; y < bounds.MaxY; y += cell)
        {
            var at = new Point2D(x, y);
            if (!Polygon2D.Contains(outline, at)) continue;

            var over = Over(document, floor, at, covers);
            var headroom = over?.Headroom ?? limit;
            if (over is { By: Roof }) underRoof = true;
            if (over is not null) bounded = true;

            var piece = cell * cell;
            area += piece;
            volume += piece * headroom;
            if (headroom >= CountedHeadroom - 1e-6) counted += piece;
            if (headroom >= FullHeadroom - 1e-6) full += piece;
            lowest = Math.Min(lowest, headroom);
            highest = Math.Max(highest, headroom);
        }

        if (area <= 0) return new RoomSpace(boundary.Area, boundary.Area * limit, limit, limit, 0, 0, false, false);

        // Scaled to the room's own area, which leaves out any columns standing in it.
        var scale = boundary.Area / area;
        return new RoomSpace(boundary.Area, volume * scale, lowest, highest, counted * scale, full * scale, underRoof, bounded);
    }

    /// <summary>
    /// Where the headroom under a sloping roof is a given height, across a room: the lines a plan
    /// draws to show where floor stops counting. Only where that roof is what is over the room.
    /// </summary>
    public static IReadOnlyList<(Point2D From, Point2D To)> Contour(BimDocument document, Room room, double headroom)
    {
        var boundary = room.GetBoundary(document);
        if (!boundary.IsEnclosed || boundary.Polygon.Count < 3) return Array.Empty<(Point2D, Point2D)>();

        var outline = boundary.Polygon;
        var floor = Floor(document, room);
        var covers = Covers(document, room, outline);
        var lines = new List<(Point2D, Point2D)>();

        foreach (var roof in covers.OfType<Roof>())
        foreach (var facet in roof.Surface(document).Facets)
        {
            var plane = facet.Plane;
            if (plane.Rise < 1e-6) continue;

            // The level line on the roof's underside, the headroom above the floor.
            var uphill = new Vector2D(plane.A, plane.B) * (1 / plane.Rise);
            var through = new Point2D(0, 0) + uphill * ((floor + headroom - plane.C) / plane.Rise);
            var along = uphill.PerpendicularLeft();

            foreach (var part in PolygonBoolean.Combine(facet.Outline, outline, BooleanOperation.Intersection))
            {
                var reach = part.Outer.Max(point => point.DistanceTo(through)) + 1000;
                foreach (var (from, to) in Inside(part.Outer, through - along * reach, through + along * reach))
                {
                    // Only where this roof is the lowest thing there: not over a ceiling under it.
                    var middle = from.MidpointTo(to);
                    if (Over(document, floor, middle, covers) is { By: var by } && ReferenceEquals(by, roof))
                        lines.Add((from, to));
                }
            }
        }

        return lines;
    }

    /// <summary>The stretches of a line from one point to another that lie inside an outline.</summary>
    private static IEnumerable<(Point2D From, Point2D To)> Inside(IReadOnlyList<Point2D> outline, Point2D from, Point2D to)
    {
        var shares = new List<double> { 0, 1 };
        var r = to - from;
        for (var i = 0; i < outline.Count; i++)
        {
            var (a, b) = (outline[i], outline[(i + 1) % outline.Count]);
            var s = b - a;
            var denominator = r.X * s.Y - r.Y * s.X;
            if (Math.Abs(denominator) < 1e-12) continue;

            var t = ((a.X - from.X) * s.Y - (a.Y - from.Y) * s.X) / denominator;
            var u = ((a.X - from.X) * r.Y - (a.Y - from.Y) * r.X) / denominator;
            if (t > 0 && t < 1 && u >= -1e-9 && u <= 1 + 1e-9) shares.Add(t);
        }

        shares.Sort();
        for (var i = 0; i + 1 < shares.Count; i++)
        {
            var (start, end) = (from + r * shares[i], from + r * shares[i + 1]);
            if (start.DistanceTo(end) > 1 && Polygon2D.Contains(outline, start.MidpointTo(end))) yield return (start, end);
        }
    }

    private static (double MinX, double MinY, double MaxX, double MaxY) Bounds(IReadOnlyList<Point2D> points) =>
        (points.Min(point => point.X), points.Min(point => point.Y), points.Max(point => point.X), points.Max(point => point.Y));

    private static bool Overlaps((double MinX, double MinY, double MaxX, double MaxY) a, (double MinX, double MinY, double MaxX, double MaxY) b) =>
        a.MinX <= b.MaxX && b.MinX <= a.MaxX && a.MinY <= b.MaxY && b.MinY <= a.MaxY;
}
