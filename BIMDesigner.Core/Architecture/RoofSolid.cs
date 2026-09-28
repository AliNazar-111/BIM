using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;

namespace BIMDesigner.Core.Architecture;

/// <summary>How a roof's eaves are cut (Revit's Rafter Cut).</summary>
public enum RafterCut
{
    /// <summary>Straight down at the roof's edge, through the whole build-up.</summary>
    PlumbCut,

    /// <summary>Straight down for the fascia depth, then level back to the underside.</summary>
    TwoCutPlumb,

    /// <summary>Square to the slope for the fascia depth, then level back to the underside.</summary>
    TwoCutSquare
}

/// <summary>
/// One piece of a roof's solid: a region in plan, one layer of the build-up, and the planes
/// it runs between there. Everything that builds or cuts a roof - the 3D model, a section -
/// builds these, so the two cannot disagree about where the roof is.
/// </summary>
public sealed record RoofPiece(IReadOnlyList<Point2D> Outline, MaterialLayer Layer, RoofPlane Bottom, RoofPlane Top)
{
    /// <summary>The curved surface this piece is part of - a cone, built from flat faces - to be shaded as one; null for a flat face of its own.</summary>
    public Guid? SmoothGroup { get; init; }
}

/// <summary>
/// A roof as solid pieces: each face, each layer of the build-up standing on the underside
/// square to the slope, and the eaves cut the way the roof says (specification section 3.3;
/// Revit's Rafter Cut and Fascia Depth).
///
/// An eave cut is the same cut all along an eave: whatever happens to the roof's cross-section
/// happens at every point the same distance in from the edge. So a face is sliced into strips
/// parallel to its eave wherever something changes - where a layer's underside meets the level
/// soffit cut, where it meets the square cut, where the square cut gives way to the soffit -
/// and inside each strip every surface is a plane. The pieces are exact, not approximated.
/// </summary>
public static class RoofSolid
{
    private const double Tolerance = 1e-6;

    public static IReadOnlyList<RoofPiece> Pieces(BimDocument document, Roof roof) =>
        // Trimmed where it runs into a roof it is joined to, and opened for the dormers on it.
        RoofJoin.Open(document, roof, RoofJoin.Trim(document, roof, Uncut(document, roof)));

    /// <summary>The roof's pieces before any other roof has a say in them.</summary>
    private static IReadOnlyList<RoofPiece> Uncut(BimDocument document, Roof roof)
    {
        if (document.FindType<SlabType>(roof.TypeId) is not { } type) return Array.Empty<RoofPiece>();

        // A roof by extrusion is built from its profile, layer by mitred layer.
        if (roof.Extrusion is { } extrusion) return extrusion.Pieces(type.Structure, roof.BaseElevation(document));

        var layers = type.Structure.GetLayerOffsets().Where(entry => entry.Layer.Thickness > 0).ToList();
        var total = type.Structure.TotalWidth;
        var pieces = new List<RoofPiece>();

        foreach (var facet in roof.Surface(document).Facets)
        {
            var under = facet.Plane;
            var stretch = under.VerticalStretch;

            foreach (var (layer, start, end) in layers)
            {
                // The planes are the underside; the layers are counted from the top down, so
                // each one sits the rest of the build-up above the underside.
                var bottom = Lift(under, (total - end) * stretch);
                var top = Lift(under, (total - start) * stretch);

                if (roof.RafterCut == RafterCut.PlumbCut || under.Rise < 1e-9 || !facet.HasEave)
                {
                    pieces.Add(new RoofPiece(facet.Outline, layer, bottom, top) { SmoothGroup = facet.ArcId });
                    continue;
                }

                pieces.AddRange(CutAtEave(facet.Outline, layer, under, bottom, top, roof.RafterCut, roof.FasciaDepth, total)
                    .Select(piece => piece with { SmoothGroup = facet.ArcId }));
            }
        }

        return pieces;
    }

    private static RoofPlane Lift(RoofPlane plane, double by) =>
        new(plane.A, plane.B, plane.C + by, plane.Eave + by);

    /// <summary>
    /// One layer of one face, with the eave cut out of it.
    ///
    /// Distances are measured in from the face's eave, square to it, and heights are those of
    /// the roof's surfaces at that distance: the layer's two planes rise with the roof; the
    /// soffit cut is level; the square cut falls away square to the slope from the top of the
    /// eave. Between any two of the places those lines cross, the layer is bounded by one of
    /// them below and its own top above - or not there at all.
    /// </summary>
    private static IEnumerable<RoofPiece> CutAtEave(
        IReadOnlyList<Point2D> outline, MaterialLayer layer, RoofPlane under,
        RoofPlane bottom, RoofPlane top, RafterCut cut, double fasciaDepth, double total)
    {
        var rise = under.Rise;
        var stretch = under.VerticalStretch;
        var angle = Math.Atan(rise);

        // In from the eave, square to it: the direction the roof climbs.
        var inward = new Vector2D(under.A / rise, under.B / rise);
        var eavePoint = new Point2D(inward.X * (under.Eave - under.C) / rise, inward.Y * (under.Eave - under.C) / rise);
        var along = inward.PerpendicularLeft();

        // Heights as straight lines in distance x from the eave: height = at0 + slope * x.
        var eaveTop = under.Eave + total * stretch;
        var layerBottom = (At0: bottom.Eave, Slope: rise);
        var layerTop = (At0: top.Eave, Slope: rise);

        double soffit;
        (double At0, double Slope)? square = null;
        var squareEnds = 0.0;

        if (cut == RafterCut.TwoCutPlumb)
        {
            // Down the fascia depth, measured plumb, then level.
            var depth = Math.Clamp(fasciaDepth, 0, total * stretch);
            soffit = eaveTop - depth;
        }
        else
        {
            // Square to the slope for the fascia depth, from the top of the eave, then level.
            var depth = Math.Clamp(fasciaDepth, 0, total);
            square = (eaveTop, -1 / rise);
            squareEnds = depth * Math.Sin(angle);
            soffit = eaveTop - depth * Math.Cos(angle);
        }

        // What the layer is cut to below, at a distance in: the square cut near the edge, the
        // level soffit after it.
        double CutAt(double x) => square is { } s && x < squareEnds ? s.At0 + s.Slope * x : soffit;

        // Every distance at which something changes.
        var breaks = new List<double> { 0 };
        if (square is not null) breaks.Add(squareEnds);

        foreach (var line in new[] { layerBottom, layerTop })
        {
            breaks.Add((soffit - line.At0) / line.Slope);
            if (square is { } s) breaks.Add((s.At0 - line.At0) / (line.Slope - s.Slope));
        }

        // How far in the face reaches, so the last strip runs to its far side.
        var reach = outline.Max(point => Distance(point));
        double Distance(Point2D point) => (point - eavePoint).Dot(inward);

        var stations = breaks
            .Where(x => x > Tolerance && x < reach - Tolerance)
            .Append(reach + 1)
            .Distinct()
            .OrderBy(x => x)
            .ToList();

        var from = Math.Min(0, outline.Min(point => Distance(point))) - 1;

        foreach (var to in stations)
        {
            var middle = (Math.Max(from, 0) + to) / 2;
            var cutHere = CutAt(middle);
            var bottomHere = layerBottom.At0 + layerBottom.Slope * middle;
            var topHere = layerTop.At0 + layerTop.Slope * middle;

            // Wholly under the cut here: this stretch of this layer has been cut away.
            if (topHere > cutHere + Tolerance)
            {
                var strip = Strip(outline, eavePoint, inward, along, from, to);

                if (strip.Count > 0)
                {
                    // The layer's own underside where it is above the cut, the cut where not.
                    var floor = bottomHere >= cutHere
                        ? bottom
                        : square is { } s && middle < squareEnds
                            ? Plane(eavePoint, inward, s.At0, s.Slope)
                            : new RoofPlane(0, 0, soffit, soffit);

                    foreach (var piece in strip)
                        yield return new RoofPiece(piece, layer, floor, top);
                }
            }

            from = to;
        }
    }

    /// <summary>A plane given as a height at the eave line and a rise per unit inward from it.</summary>
    private static RoofPlane Plane(Point2D eavePoint, Vector2D inward, double at0, double slope)
    {
        var a = inward.X * slope;
        var b = inward.Y * slope;
        var c = at0 - a * eavePoint.X - b * eavePoint.Y;
        return new RoofPlane(a, b, c, at0);
    }

    /// <summary>
    /// The part of an outline between two distances in from the eave: a band as wide as the
    /// roof is long, cut out of it.
    /// </summary>
    private static List<IReadOnlyList<Point2D>> Strip(
        IReadOnlyList<Point2D> outline, Point2D eavePoint, Vector2D inward, Vector2D along, double from, double to)
    {
        var reach = outline.Max(point => (point - eavePoint).Length) * 2 + 1000;
        var band = new List<Point2D>
        {
            eavePoint + inward * from - along * reach,
            eavePoint + inward * from + along * reach,
            eavePoint + inward * to + along * reach,
            eavePoint + inward * to - along * reach
        };

        return PolygonBoolean.Combine(outline, band, BooleanOperation.Intersection)
            .Where(region => region.Area > 1)
            .Select(region => region.Outer)
            .ToList();
    }
}
