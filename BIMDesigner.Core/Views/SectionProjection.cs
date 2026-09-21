using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;

namespace BIMDesigner.Core.Views;

/// <summary>Whether a piece was sliced by the cut plane or is merely visible beyond it.</summary>
public enum SectionDepth
{
    Seen = 0,
    Cut = 1
}

/// <summary>What a piece of the section drawing is, so the renderer can weight it properly.</summary>
public enum SectionPart
{
    WallLayer,
    WallFace,
    SlabLayer,
    DoorLeaf,
    Glazing,
    Frame
}

/// <summary>
/// A rectangle in section coordinates: X runs along the cut line from its start, Y is the
/// true elevation above the project base point. Y is up, unlike a screen.
/// </summary>
public readonly record struct SectionRect(double Left, double Bottom, double Right, double Top)
{
    public double Width => Right - Left;

    public double Height => Top - Bottom;

    public bool IsEmpty => Width <= 1e-6 || Height <= 1e-6;
}

/// <summary>One drawn rectangle of the section, and enough about it to draw and identify it.</summary>
public sealed record SectionPiece(
    SectionRect Bounds,
    SectionPart Part,
    SectionDepth Depth,
    ColourRgb Fill,
    string Description,
    Guid ElementId);

public sealed record SectionLevelLine(string Name, double Elevation);

/// <summary>
/// The result of cutting the model along a marker. It holds no reference back to the
/// document: it is produced fresh whenever the section is drawn, so it cannot go stale.
/// </summary>
public sealed class SectionDrawing
{
    public SectionDrawing(
        string name,
        double width,
        IReadOnlyList<SectionPiece> pieces,
        IReadOnlyList<SectionLevelLine> levels)
    {
        Name = name;
        Width = width;
        Pieces = pieces;
        Levels = levels;
    }

    public string Name { get; }

    /// <summary>Length of the cut line, which is the drawing's horizontal extent.</summary>
    public double Width { get; }

    /// <summary>Seen pieces first, cut pieces last, so drawing them in order layers correctly.</summary>
    public IReadOnlyList<SectionPiece> Pieces { get; }

    public IReadOnlyList<SectionLevelLine> Levels { get; }

    public bool IsEmpty => Pieces.Count == 0;

    public double MinElevation => Extent(lowest: true);

    public double MaxElevation => Extent(lowest: false);

    private double Extent(bool lowest)
    {
        var values = Pieces
            .Select(piece => lowest ? piece.Bounds.Bottom : piece.Bounds.Top)
            .Concat(Levels.Select(level => level.Elevation));

        var found = values.ToList();
        if (found.Count == 0) return 0;

        return lowest ? found.Min() : found.Max();
    }
}

/// <summary>
/// Cuts the model along a section marker (specification section 6.1).
///
/// Every number it draws with is read from the model at the moment of drawing - level
/// elevations, wall base offsets and heights, slab thicknesses, opening sill and head
/// heights. Nothing about the section is stored, so a section cannot show the building as it
/// used to be, and there is no "regenerate" step that someone can forget.
/// </summary>
public static class SectionProjection
{
    private const double Epsilon = 1e-9;

    /// <summary>
    /// How close to parallel a wall may run to the cut line before slicing it stops making
    /// sense. Cutting lengthways along a wall would report its apparent thickness as metres
    /// of masonry; past this angle the wall is shown in elevation instead.
    /// </summary>
    private const double GlancingSine = 0.2;

    private static readonly ColourRgb DefaultCut = new(0x7A, 0x7A, 0x7A);
    private static readonly ColourRgb LeafColour = new(0xB0, 0x8E, 0x62);
    private static readonly ColourRgb GlazingColour = new(0x7F, 0xA8, 0xC0);

    public static SectionDrawing Build(BimDocument document, SectionMarker marker, Func<Element, bool>? shows = null)
    {
        shows ??= _ => true;

        var pieces = new List<SectionPiece>();

        if (marker.Length > Epsilon)
        {
            foreach (var wall in document.Walls.Where(wall => shows(wall))) AddWall(document, marker, wall, pieces);
            foreach (var slab in document.Elements.OfType<Slab>()) AddSlab(document, marker, slab, pieces);
        }

        var levels = document.Levels
            .OrderBy(level => level.Elevation)
            .Select(level => new SectionLevelLine(level.Name, level.Elevation))
            .ToList();

        // Stable sort by depth: everything beyond the cut plane is drawn before the cut
        // itself, so the cut always reads as the front of the drawing.
        var ordered = pieces
            .Where(piece => !piece.Bounds.IsEmpty)
            .OrderBy(piece => (int)piece.Depth)
            .ToList();

        return new SectionDrawing(marker.Name, marker.Length, ordered, levels);
    }

    // ---- walls -----------------------------------------------------------------

    private static void AddWall(
        BimDocument document, SectionMarker marker, Wall wall, List<SectionPiece> pieces)
    {
        var type = document.GetWallType(wall);
        if (type is null) return;

        var structure = type.Structure;
        var width = structure.TotalWidth;
        if (width <= 0 || wall.Length <= Epsilon) return;

        var baseElevation = (document.FindLevel(wall.LevelId)?.Elevation ?? 0) + wall.BaseOffset;
        var topElevation = baseElevation + wall.GetHeight(document);
        if (topElevation <= baseElevation) return;

        var (bodyStart, bodyEnd) = wall.GetBodyCentreline(structure);
        var normal = wall.ExteriorNormal;
        var half = width / 2;

        var glancing = Math.Abs(marker.Direction.Cross(wall.Direction)) < GlancingSine;

        if (glancing ||
            !ClipToBand(marker, bodyStart, bodyEnd, normal, -half, half, out var from, out var to))
        {
            AddSeenWall(document, marker, wall, type, structure, baseElevation, topElevation, pieces);
            return;
        }

        // Where along the wall the cut passes, which is what decides whether it went through
        // a doorway or through solid wall.
        var crossing = marker.Start + marker.Direction * ((from + to) / 2);
        var distanceAlong = (crossing - wall.Start).Dot(wall.Direction);

        var openings = OpeningsAt(document, wall, distanceAlong);
        var solid = SolidHeights(document, openings, baseElevation, topElevation);

        foreach (var (layer, start, end) in structure.GetLayerOffsets())
        {
            // A membrane is a line with no volume: nothing to build or cut.
            if (layer.Thickness <= 0) continue;

            // Layers are measured from the exterior face inward; the band's signed offsets
            // from the centreline therefore run the other way.
            if (!ClipToBand(marker, bodyStart, bodyEnd, normal, half - end, half - start,
                    out var layerFrom, out var layerTo))
            {
                continue;
            }

            var material = document.FindMaterial(layer.MaterialId);

            foreach (var (bottom, top) in solid)
            {
                pieces.Add(new SectionPiece(
                    new SectionRect(layerFrom, bottom, layerTo, top),
                    SectionPart.WallLayer,
                    SectionDepth.Cut,
                    material?.CutColour ?? DefaultCut,
                    material?.Name ?? layer.Function.ToString(),
                    wall.Id));
            }
        }

        foreach (var (opening, openingType) in openings)
        {
            var sill = Math.Max(baseElevation, baseElevation + opening.SillHeight);
            var head = Math.Min(topElevation, baseElevation + opening.SillHeight + openingType.Height);
            if (head <= sill) continue;

            AddOpeningPieces(
                new SectionRect(from, sill, to, head), opening, openingType, SectionDepth.Cut, pieces);
        }
    }

    /// <summary>
    /// A wall the cut did not slice, drawn as the elevation it presents to the viewer - but
    /// only if it is actually in front of the cut plane and within the view depth.
    /// </summary>
    private static void AddSeenWall(
        BimDocument document,
        SectionMarker marker,
        Wall wall,
        WallType type,
        CompoundStructure structure,
        double baseElevation,
        double topElevation,
        List<SectionPiece> pieces)
    {
        var (bodyStart, bodyEnd) = wall.GetBodyCentreline(structure);
        var offset = wall.ExteriorNormal * (structure.TotalWidth / 2);

        var corners = new[] { bodyStart + offset, bodyEnd + offset, bodyEnd - offset, bodyStart - offset };
        var depths = corners.Select(marker.DepthOf).ToList();

        if (depths.Max() <= Epsilon) return;                 // behind the viewer
        if (depths.Min() >= marker.ViewDepth) return;         // past the far clip

        var alongs = corners.Select(marker.DistanceAlong).ToList();
        var left = Math.Max(0, alongs.Min());
        var right = Math.Min(marker.Length, alongs.Max());
        if (right - left <= Epsilon) return;

        pieces.Add(new SectionPiece(
            new SectionRect(left, baseElevation, right, topElevation),
            SectionPart.WallFace,
            SectionDepth.Seen,
            type.CoarseScaleFillColour,
            type.Name,
            wall.Id));

        foreach (var opening in WallOpenings.Of(document, wall))
        {
            var openingType = document.FindType<OpeningType>(opening.TypeId);
            if (openingType is null) continue;

            var (spanFrom, spanTo) = opening.GetSpan(openingType);
            var a = marker.DistanceAlong(wall.Start + wall.Direction * spanFrom);
            var b = marker.DistanceAlong(wall.Start + wall.Direction * spanTo);

            var openingLeft = Math.Max(left, Math.Min(a, b));
            var openingRight = Math.Min(right, Math.Max(a, b));
            if (openingRight - openingLeft <= Epsilon) continue;

            var sill = Math.Max(baseElevation, baseElevation + opening.SillHeight);
            var head = Math.Min(topElevation, baseElevation + opening.SillHeight + openingType.Height);
            if (head <= sill) continue;

            AddOpeningPieces(
                new SectionRect(openingLeft, sill, openingRight, head),
                opening, openingType, SectionDepth.Seen, pieces);
        }
    }

    private static void AddOpeningPieces(
        SectionRect bounds,
        Opening opening,
        OpeningType openingType,
        SectionDepth depth,
        List<SectionPiece> pieces)
    {
        if (opening is Window)
        {
            pieces.Add(new SectionPiece(
                bounds, SectionPart.Glazing, depth, GlazingColour, openingType.Name, opening.Id));
        }
        else
        {
            pieces.Add(new SectionPiece(
                bounds, SectionPart.DoorLeaf, depth, LeafColour, openingType.Name, opening.Id));
        }
    }

    /// <summary>Openings whose span covers this distance along the wall.</summary>
    private static List<(Opening Opening, OpeningType Type)> OpeningsAt(
        BimDocument document, Wall wall, double distanceAlong)
    {
        var found = new List<(Opening, OpeningType)>();

        foreach (var opening in WallOpenings.Of(document, wall))
        {
            var type = document.FindType<OpeningType>(opening.TypeId);
            if (type is null) continue;

            var (from, to) = opening.GetSpan(type);
            if (distanceAlong > from && distanceAlong < to) found.Add((opening, type));
        }

        return found;
    }

    /// <summary>
    /// The heights at which the wall is still solid once its openings have been taken out of
    /// it - the same idea as the solid runs along a wall in plan, turned on its side.
    /// </summary>
    private static List<(double Bottom, double Top)> SolidHeights(
        BimDocument document,
        List<(Opening Opening, OpeningType Type)> openings,
        double baseElevation,
        double topElevation)
    {
        var runs = new List<(double Bottom, double Top)>();
        if (openings.Count == 0) return new List<(double, double)> { (baseElevation, topElevation) };

        var holes = openings
            .Select(entry =>
            {
                var sill = Math.Max(baseElevation, baseElevation + entry.Opening.SillHeight);
                var head = Math.Min(topElevation, baseElevation + entry.Opening.SillHeight + entry.Type.Height);
                return (Sill: sill, Head: head);
            })
            .Where(hole => hole.Head > hole.Sill)
            .OrderBy(hole => hole.Sill)
            .ToList();

        var cursor = baseElevation;

        foreach (var (sill, head) in holes)
        {
            if (sill > cursor) runs.Add((cursor, sill));
            cursor = Math.Max(cursor, head);
        }

        if (cursor < topElevation) runs.Add((cursor, topElevation));
        return runs;
    }

    // ---- slabs -----------------------------------------------------------------

    private static void AddSlab(
        BimDocument document, SectionMarker marker, Slab slab, List<SectionPiece> pieces)
    {
        var type = document.FindType<SlabType>(slab.TypeId);
        if (type is null || slab.Boundary.Count < 3) return;

        var structure = type.Structure;
        if (structure.TotalWidth <= 0) return;

        // The level is the slab's upper surface and its layers build downward from there,
        // which is the order a floor build-up is specified in.
        var top = (document.FindLevel(slab.LevelId)?.Elevation ?? 0) + slab.HeightOffset;

        foreach (var (from, to) in CrossPolygon(marker, slab.Boundary))
        {
            foreach (var (layer, start, end) in structure.GetLayerOffsets())
            {
                if (layer.Thickness <= 0) continue;

                var material = document.FindMaterial(layer.MaterialId);

                pieces.Add(new SectionPiece(
                    new SectionRect(from, top - end, to, top - start),
                    SectionPart.SlabLayer,
                    SectionDepth.Cut,
                    material?.CutColour ?? DefaultCut,
                    material?.Name ?? layer.Function.ToString(),
                    slab.Id));
            }
        }
    }

    // ---- clipping --------------------------------------------------------------

    /// <summary>
    /// Finds the stretch of the cut line that lies inside a band: the strip swept between two
    /// signed offsets either side of the segment <paramref name="a"/> to <paramref name="b"/>.
    /// One wall layer is such a band, and so is a whole wall.
    ///
    /// Returned distances are measured along the cut line from its start, already clamped to
    /// its ends, so they are the drawing's own X coordinates.
    /// </summary>
    private static bool ClipToBand(
        SectionMarker marker,
        Point2D a,
        Point2D b,
        Vector2D normal,
        double offsetLow,
        double offsetHigh,
        out double from,
        out double to)
    {
        from = 0;
        to = marker.Length;

        var direction = (b - a).NormalisedOrDefault(Vector2D.UnitX);
        var origin = marker.Start;
        var ray = marker.Direction;

        var inside =
            Clip(a, -direction, origin, ray, ref from, ref to) &&
            Clip(b, direction, origin, ray, ref from, ref to) &&
            Clip(a + normal * offsetHigh, normal, origin, ray, ref from, ref to) &&
            Clip(a + normal * offsetLow, -normal, origin, ray, ref from, ref to);

        return inside && to - from > Epsilon;
    }

    /// <summary>
    /// Trims the run [<paramref name="from"/>, <paramref name="to"/>] to the inside of one
    /// half-plane - the side of the line through <paramref name="origin"/> that
    /// <paramref name="outward"/> points away from.
    /// </summary>
    private static bool Clip(
        Point2D origin,
        Vector2D outward,
        Point2D rayOrigin,
        Vector2D rayDirection,
        ref double from,
        ref double to)
    {
        var distance = (rayOrigin - origin).Dot(outward);
        var rate = rayDirection.Dot(outward);

        // Running along the boundary: the whole ray is on one side of it or the other.
        if (Math.Abs(rate) < Epsilon) return distance <= 0;

        var crossing = -distance / rate;

        if (rate > 0) to = Math.Min(to, crossing);
        else from = Math.Max(from, crossing);

        return from <= to;
    }

    /// <summary>
    /// The stretches of the cut line that fall inside a closed outline. Concave outlines give
    /// more than one, which is why this returns spans rather than a single range.
    /// </summary>
    private static IEnumerable<(double From, double To)> CrossPolygon(
        SectionMarker marker, IReadOnlyList<Point2D> polygon)
    {
        var length = marker.Length;
        var origin = marker.Start;
        var ray = marker.Direction;

        var breaks = new List<double> { 0, length };

        for (var i = 0; i < polygon.Count; i++)
        {
            var a = polygon[i];
            var edge = polygon[(i + 1) % polygon.Count] - a;

            var denominator = ray.Cross(edge);
            if (Math.Abs(denominator) < Epsilon) continue;

            var offset = a - origin;
            var alongEdge = offset.Cross(ray) / denominator;
            if (alongEdge is < 0 or > 1) continue;

            var alongRay = offset.Cross(edge) / denominator;
            if (alongRay > 0 && alongRay < length) breaks.Add(alongRay);
        }

        breaks.Sort();

        for (var i = 0; i + 1 < breaks.Count; i++)
        {
            var from = breaks[i];
            var to = breaks[i + 1];
            if (to - from <= Epsilon) continue;

            // Testing the middle of each stretch is what makes this work for a concave
            // outline: counting crossings alone cannot say which stretches are the inside.
            if (Polygon2D.Contains(polygon, origin + ray * ((from + to) / 2))) yield return (from, to);
        }
    }
}
