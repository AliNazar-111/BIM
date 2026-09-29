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
    Guid ElementId)
{
    /// <summary>
    /// The true outline, in the same coordinates, when the piece is not an upright rectangle -
    /// a cut through a leaning wall. Null for the ordinary case.
    /// </summary>
    public IReadOnlyList<(double X, double Y)>? Shape { get; init; }

    /// <summary>
    /// How far beyond the cut this piece is, when it is not the same all over its element - a
    /// face of a roof, nearer or farther than the roof's others. Null to go by its element.
    /// </summary>
    public double? Distance { get; init; }
}

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

            foreach (var column in document.Elements.OfType<Column>().Where(column => shows(column)))
                AddColumn(document, marker, column, pieces);

            // Fascias and gutters, where the cut goes through them: the profile as it slices it.
            foreach (var sweep in document.Elements.OfType<RoofEdgeSweep>().Where(sweep => shows(sweep)))
            {
                var material = document.FindMaterial(sweep switch
                {
                    Fascia => document.FindType<FasciaType>(sweep.TypeId)?.MaterialId ?? Guid.Empty,
                    Gutter => document.FindType<GutterType>(sweep.TypeId)?.MaterialId ?? Guid.Empty,
                    Soffit => document.FindType<SoffitType>(sweep.TypeId)?.MaterialId ?? Guid.Empty,
                    _ => Guid.Empty
                });

                foreach (var shape in RoofEdgeSweeps.Cut(document, sweep, marker.DepthOf, marker.DistanceAlong))
                {
                    if (shape.Count < 3) continue;
                    pieces.Add(new SectionPiece(
                        new SectionRect(shape.Min(c => c.X), shape.Min(c => c.Y), shape.Max(c => c.X), shape.Max(c => c.Y)),
                        SectionPart.SlabLayer,
                        SectionDepth.Cut,
                        material?.CutColour ?? DefaultCut,
                        material?.Name ?? sweep.Category.ToString(),
                        sweep.Id)
                    {
                        Shape = shape
                    });
                }
            }
        }

        var levels = document.Levels
            .OrderBy(level => level.Elevation)
            .Select(level => new SectionLevelLine(level.Name, level.Elevation))
            .ToList();

        // Everything beyond the cut plane is drawn before the cut itself, so the cut always
        // reads as the front of the drawing - and what is beyond, farthest first, so a nearer
        // wall hides what is behind it rather than a window in a far wall showing through it.
        // The sort is stable, so a door or window still comes after the wall it is in.
        var elements = document.Elements.GroupBy(element => element.Id).ToDictionary(group => group.Key, group => group.First());
        var distances = new Dictionary<Guid, double>();

        double Nearest(IEnumerable<Point2D> points) =>
            points.Select(point => Math.Max(0, marker.DepthOf(point))).DefaultIfEmpty(0).Min();

        double Distance(Guid id)
        {
            if (distances.TryGetValue(id, out var known)) return known;

            var distance = elements.GetValueOrDefault(id) switch
            {
                Wall wall => Nearest(new[] { wall.Start, wall.End, wall.LocationCurve.PointAt(wall.Length / 2) }),
                Opening opening => Distance(opening.HostWallId),
                Slab slab => Nearest(slab.Boundary),
                Column column => Nearest(new[] { column.Location }),
                _ => 0
            };

            distances[id] = distance;
            return distance;
        }

        var ordered = pieces
            .Where(piece => !piece.Bounds.IsEmpty)
            .OrderBy(piece => (int)piece.Depth)
            .ThenByDescending(piece => piece.Depth == SectionDepth.Seen ? piece.Distance ?? Distance(piece.ElementId) : 0)
            .ToList();

        return new SectionDrawing(marker.Name, marker.Length, ordered, levels);
    }

    // ---- walls -----------------------------------------------------------------

    /// <summary>
    /// A wall, tier by tier: each construction it is made of is cut as if it were the whole
    /// wall, then trimmed to the heights that construction actually occupies. An ordinary wall
    /// has one tier running its full height.
    /// </summary>
    private static void AddWall(
        BimDocument document, SectionMarker marker, Wall wall, List<SectionPiece> pieces)
    {
        if (CurtainLayout.Of(document, wall) is { } curtain)
        {
            AddCurtainWall(document, marker, wall, curtain, pieces);
            return;
        }

        // An edited profile is one construction that sets its own heights: nothing to trim to.
        if (WallProfile.Of(document, wall) is not null && document.GetWallType(wall) is { } profiled)
        {
            AddWallAs(document, marker, wall, profiled, pieces);
            return;
        }

        foreach (var (type, bottom, top) in document.GetWallTiers(wall))
        {
            var tier = new List<SectionPiece>();
            AddWallAs(document, marker, wall, type, tier);

            foreach (var piece in tier)
            {
                var bounds = piece.Bounds with
                {
                    Bottom = Math.Max(piece.Bounds.Bottom, bottom),
                    Top = Math.Min(piece.Bounds.Top, top)
                };

                if (bounds.IsEmpty) continue;

                foreach (var recessed in Recessed(document, marker, wall, type, piece with { Bounds = bounds }, bottom, top))
                    pieces.Add(WallLean.Leans(wall, type) ? Leaned(document, marker, wall, type, recessed) : recessed);
            }

            AddSweepProfiles(document, marker, wall, type, bottom, top, type.Sweeps.Select(s => (s, wall.Id)), pieces);
        }

        // Sweeps placed on the wall on their own, once, over its whole height.
        if (document.GetWallType(wall) is { } host)
        {
            var wallBottom = wall.GetBaseElevation(document);
            AddSweepProfiles(document, marker, wall, host, wallBottom, wallBottom + wall.GetHeight(document),
                WallSweeps.Placed(document, wall), pieces);
        }
    }

    /// <summary>
    /// A cut layer with the type's reveals taken out of it: the piece above and below each
    /// reveal whole, and within the reveal only what is left behind the groove.
    /// </summary>
    private static IEnumerable<SectionPiece> Recessed(
        BimDocument document, SectionMarker marker, Wall wall, WallType type, SectionPiece piece, double bottom, double top)
    {
        // The horizontal reveals across this piece, and any upright one the cut passes through.
        var cutAlong = wall.Locate(type.Structure, marker.Start + marker.Direction * ((piece.Bounds.Left + piece.Bounds.Right) / 2)).Along;
        var reveals = WallSweeps.Reveals(document, wall, type, bottom, top)
            .Where(r => r.Top > piece.Bounds.Bottom && r.Bottom < piece.Bounds.Top)
            .Concat(WallSweeps.VerticalReveals(document, wall)
                .Where(v => cutAlong > v.From && cutAlong < v.To)
                .Select(v => (Bottom: double.NegativeInfinity, Top: double.PositiveInfinity, v.Side, v.Depth)))
            .ToList();

        if (piece.Part != SectionPart.WallLayer || piece.Depth != SectionDepth.Cut || reveals.Count == 0)
        {
            yield return piece;
            yield break;
        }

        var structure = type.Structure;
        var half = type.Width / 2;
        var bounds = piece.Bounds;

        double Across(double x) => wall.Locate(structure, marker.Start + marker.Direction * x).Across;
        var (leftAcross, rightAcross) = (Across(bounds.Left), Across(bounds.Right));

        // The heights at which the piece changes, and in each band what width is left of it.
        var heights = reveals.SelectMany(r => new[] { r.Bottom, r.Top })
            .Where(z => z > bounds.Bottom && z < bounds.Top)
            .Append(bounds.Bottom).Append(bounds.Top)
            .Distinct().OrderBy(z => z).ToList();

        for (var i = 0; i + 1 < heights.Count; i++)
        {
            var middle = (heights[i] + heights[i + 1]) / 2;
            var outer = Math.Max(leftAcross, rightAcross);
            var inner = Math.Min(leftAcross, rightAcross);

            if (WallSweeps.Recess(outer, inner, half, reveals, middle) is not var (keptOuter, keptInner)) continue;

            // Back from across to along the cut line, which runs straight through the wall.
            double X(double across) => Math.Abs(rightAcross - leftAcross) < 1e-9
                ? bounds.Left
                : bounds.Left + (across - leftAcross) / (rightAcross - leftAcross) * (bounds.Right - bounds.Left);

            var (a, b) = (X(keptOuter), X(keptInner));
            yield return piece with
            {
                Bounds = new SectionRect(Math.Min(a, b), heights[i], Math.Max(a, b), heights[i + 1])
            };
        }
    }

    /// <summary>
    /// Where the cut crosses a wall with sweeps, each sweep's profile as the section sees it -
    /// the actual cornice or skirting shape, standing out from the face - and, where it passes
    /// through an upright sweep, that sweep cut full height.
    /// </summary>
    private static void AddSweepProfiles(
        BimDocument document, SectionMarker marker, Wall wall, WallType type, double bottom, double top,
        IEnumerable<(WallSweep Sweep, Guid ElementId)> all, List<SectionPiece> pieces)
    {
        var sweeps = all.Where(s => s.Sweep.Kind == SweepKind.Sweep).ToList();
        if (sweeps.Count == 0) return;

        var structure = type.Structure;
        var cutLine = WallCurve.Of(marker.Start, marker.End, 0);

        foreach (var crossing in wall.LocationCurve.Crossings(cutLine))
        {
            var along = wall.LocationCurve.Locate(crossing).Along;

            foreach (var (sweep, elementId) in sweeps)
            {
                var material = document.FindMaterial(sweep.MaterialId);
                var shape = sweep.Shape(document);
                IReadOnlyList<(double X, double Y)> outline;

                if (sweep.Vertical)
                {
                    // Cut across an upright sweep: its section is as far out as the profile
                    // reaches at this point along it, the wall's full height.
                    var up = (along - sweep.Along) / sweep.Height + 0.5;
                    if (up <= 0 || up >= 1) continue;

                    var reach = Reach(shape, up);
                    var near = marker.DistanceAlong(wall.PointAt(structure, along, WallSweeps.Across(type, sweep, sweep.Offset)));
                    var far = marker.DistanceAlong(wall.PointAt(structure, along, WallSweeps.Across(type, sweep, sweep.OutAt(reach))));
                    outline = new[] { (near, bottom), (far, bottom), (far, top), (near, top) };
                }
                else
                {
                    var (sweepBottom, sweepTop) = sweep.Span(bottom, top);
                    if (!WallSweeps.Runs(document, wall, type, sweep, sweepBottom, sweepTop)
                            .Any(run => along >= run.From && along <= run.To))
                        continue;

                    outline = shape
                        .Select(p => (
                            X: marker.DistanceAlong(wall.PointAt(structure, along, WallSweeps.Across(type, sweep, sweep.OutAt(p.Out)))),
                            Y: sweepBottom + p.Up * sweep.Height))
                        .ToList();
                }

                pieces.Add(new SectionPiece(
                    new SectionRect(outline.Min(p => p.X), outline.Min(p => p.Y), outline.Max(p => p.X), outline.Max(p => p.Y)),
                    SectionPart.WallLayer,
                    SectionDepth.Cut,
                    material?.CutColour ?? DefaultCut,
                    material?.Name ?? "Sweep",
                    elementId)
                {
                    Shape = outline
                });
            }
        }
    }

    /// <summary>How far out a profile reaches at a height through it, both as fractions of its size.</summary>
    private static double Reach(IReadOnlyList<(double Out, double Up)> shape, double up)
    {
        var reach = 0.0;
        for (var i = 0; i < shape.Count; i++)
        {
            var a = shape[i];
            var b = shape[(i + 1) % shape.Count];
            if ((a.Up - up) * (b.Up - up) > 0 || Math.Abs(b.Up - a.Up) < 1e-12) continue;
            reach = Math.Max(reach, a.Out + (b.Out - a.Out) * (up - a.Up) / (b.Up - a.Up));
        }

        return reach;
    }

    /// <summary>
    /// A piece of a slanted or tapered wall as the section sees it: each side moved along the
    /// cut line by how far the wall has leaned at the bottom and at the top of the piece.
    /// </summary>
    private static SectionPiece Leaned(BimDocument document, SectionMarker marker, Wall wall, WallType type, SectionPiece piece)
    {
        var baseElevation = wall.GetBaseElevation(document);
        var bounds = piece.Bounds;

        double Along(double x, double elevation)
        {
            var point = marker.Start + marker.Direction * x;
            var moved = WallLean.Move(wall, type, point, elevation - baseElevation);
            return x + (moved - point).Dot(marker.Direction);
        }

        // From the bottom left, up the right side and back down the left, with a corner at any
        // bend on the way: the same four corners as ever for a wall that does not bend.
        var heights = new List<double> { bounds.Bottom };
        heights.AddRange(WallLean.Bends(wall, double.PositiveInfinity)
            .Select(bend => baseElevation + bend)
            .Where(elevation => elevation > bounds.Bottom + 1e-6 && elevation < bounds.Top - 1e-6));
        heights.Add(bounds.Top);

        var shape = new List<(double, double)> { (Along(bounds.Left, bounds.Bottom), bounds.Bottom) };
        shape.AddRange(heights.Select(h => (Along(bounds.Right, h), h)));
        shape.AddRange(Enumerable.Reverse(heights).SkipLast(1).Select(h => (Along(bounds.Left, h), h)));

        return piece with { Shape = shape };
    }

    private static void AddWallAs(
        BimDocument document, SectionMarker marker, Wall wall, WallType type, List<SectionPiece> pieces)
    {
        var structure = type.Structure;
        var width = structure.TotalWidth;
        if (width <= 0 || wall.Length <= Epsilon) return;

        var baseElevation = wall.GetBaseElevation(document);
        var profile = WallProfile.Of(document, wall);
        var topElevation = baseElevation + (profile is null ? wall.GetHeight(document) : WallProfile.Top(profile));
        if (topElevation <= baseElevation) return;

        if (wall.IsCurved)
        {
            AddCurvedWall(document, marker, wall, type, baseElevation, topElevation, pieces);
            return;
        }

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
        var solid = profile is null
            ? SolidHeights(document, openings, baseElevation, topElevation)
            : ProfileHeights(document, openings, baseElevation, profile, distanceAlong);

        // A curtain wall embedded here takes its own height out of this wall.
        foreach (var hole in WallHoles.Of(document, wall).Where(h => distanceAlong > h.From && distanceAlong < h.To))
        {
            var (sill, head) = (baseElevation + hole.Sill, baseElevation + hole.Head);
            solid = solid
                .SelectMany(s => s.Top <= sill || s.Bottom >= head
                    ? new[] { s }
                    : new[] { (s.Bottom, Math.Min(s.Top, sill)), (Math.Max(s.Bottom, head), s.Top) })
                .Where(s => s.Item2 - s.Item1 > Epsilon)
                .ToList();
        }

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
            var (placedSill, placedHeight) = opening.Placed(document, openingType, wall);
            var sill = Math.Max(baseElevation, baseElevation + placedSill);
            var head = Math.Min(topElevation, baseElevation + placedSill + placedHeight);
            if (head <= sill) continue;

            AddOpeningPieces(
                new SectionRect(from, sill, to, head), opening, openingType, SectionDepth.Cut, pieces);
        }
    }

    /// <summary>
    /// A curved wall cut by the section. A straight cut can pass through an arc once, twice or
    /// not at all, and never square to it, so rather than work out where a line meets a
    /// curve, the cut is taken across the wall's drawn outline and each of its layers - the
    /// same outlines the plan draws, joins and all.
    /// </summary>
    private static void AddCurvedWall(
        BimDocument document, SectionMarker marker, Wall wall, WallType type,
        double baseElevation, double topElevation, List<SectionPiece> pieces)
    {
        var structure = type.Structure;
        var half = structure.TotalWidth / 2;

        var (startCut, endCut) = WallJoins.GetEndCuts(document, wall, type);
        var whole = WallJoins.GetBandOutline(wall, type, half, -half, startCut, endCut);
        var crossings = CrossPolygon(marker, whole).ToList();

        if (crossings.Count == 0)
        {
            AddSeenWall(document, marker, wall, type, structure, baseElevation, topElevation, pieces);
            return;
        }

        var layers = structure.GetLayerOffsets()
            .Where(entry => entry.Layer.Thickness > 0)
            .Select(entry => (entry.Layer, Spans: CrossPolygon(marker,
                WallJoins.GetBandOutline(wall, type, half - entry.Start, half - entry.End, startCut, endCut)).ToList()))
            .ToList();

        foreach (var (from, to) in crossings)
        {
            var middle = marker.Start + marker.Direction * ((from + to) / 2);
            var openings = OpeningsAt(document, wall, wall.Locate(structure, middle).Along);
            var solid = SolidHeights(document, openings, baseElevation, topElevation);

            foreach (var (layer, spans) in layers)
            {
                var material = document.FindMaterial(layer.MaterialId);

                foreach (var (layerFrom, layerTo) in spans)
                {
                    var left = Math.Max(from, layerFrom);
                    var right = Math.Min(to, layerTo);
                    if (right - left <= Epsilon) continue;

                    foreach (var (bottom, top) in solid)
                    {
                        pieces.Add(new SectionPiece(
                            new SectionRect(left, bottom, right, top),
                            SectionPart.WallLayer,
                            SectionDepth.Cut,
                            material?.CutColour ?? DefaultCut,
                            material?.Name ?? layer.Function.ToString(),
                            wall.Id));
                    }
                }
            }

            foreach (var (opening, openingType) in openings)
            {
                var (placedSill, placedHeight) = opening.Placed(document, openingType, wall);
                var sill = Math.Max(baseElevation, baseElevation + placedSill);
                var head = Math.Min(topElevation, baseElevation + placedSill + placedHeight);
                if (head <= sill) continue;

                AddOpeningPieces(new SectionRect(from, sill, to, head), opening, openingType, SectionDepth.Cut, pieces);
            }
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
        // The footprint of the wall: four corners, or the whole outline of a curved one.
        var half = structure.TotalWidth / 2;
        var corners = WallJoins.GetBandOutline(document, wall, type, half, -half);
        var depths = corners.Select(marker.DepthOf).ToList();

        if (depths.Max() <= Epsilon) return;                 // behind the viewer
        if (depths.Min() >= marker.ViewDepth) return;         // past the far clip

        var alongs = corners.Select(marker.DistanceAlong).ToList();
        var left = Math.Max(0, alongs.Min());
        var right = Math.Min(marker.Length, alongs.Max());
        if (right - left <= Epsilon) return;

        // An edited profile is seen as its outline: each corner where it falls along the section.
        if (WallProfile.Of(document, wall) is { } profile)
        {
            var shape = profile
                .Select(p => (X: marker.DistanceAlong(wall.LocationCurve.PointAt(Math.Clamp(p.X, 0, wall.Length))), Y: baseElevation + p.Y))
                .ToList();

            pieces.Add(new SectionPiece(
                new SectionRect(shape.Min(p => p.X), shape.Min(p => p.Y), shape.Max(p => p.X), shape.Max(p => p.Y)),
                SectionPart.WallFace,
                SectionDepth.Seen,
                type.CoarseScaleFillColour,
                type.Name,
                wall.Id) { Shape = shape });
        }
        else
        {
            pieces.Add(new SectionPiece(
                new SectionRect(left, baseElevation, right, topElevation),
                SectionPart.WallFace,
                SectionDepth.Seen,
                type.CoarseScaleFillColour,
                type.Name,
                wall.Id));
        }

        foreach (var opening in WallOpenings.Of(document, wall))
        {
            var openingType = document.FindType<OpeningType>(opening.TypeId);
            if (openingType is null) continue;

            var (spanFrom, spanTo) = opening.GetSpan(openingType);
            var a = marker.DistanceAlong(wall.LocationCurve.PointAt(spanFrom));
            var b = marker.DistanceAlong(wall.LocationCurve.PointAt(spanTo));

            var openingLeft = Math.Max(left, Math.Min(a, b));
            var openingRight = Math.Min(right, Math.Max(a, b));
            if (openingRight - openingLeft <= Epsilon) continue;

            var (placedSill, placedHeight) = opening.Placed(document, openingType, wall);

            var sill = Math.Max(baseElevation, baseElevation + placedSill);
            var head = Math.Min(topElevation, baseElevation + placedSill + placedHeight);
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
    /// <summary>
    /// A curtain wall in section. Where the cut crosses it: through a mullion, the mullion full
    /// height; otherwise the panels of that bay and the transoms between them. Beyond the cut,
    /// its grid seen face-on: every panel and mullion.
    /// </summary>
    private static void AddCurtainWall(
        BimDocument document, SectionMarker marker, Wall wall, CurtainLayout layout, List<SectionPiece> pieces)
    {
        var type = layout.Type;
        var body = type.Body;
        var structure = body.Structure;
        var bottom = wall.GetBaseElevation(document);
        var panelHalf = type.PanelThickness / 2;
        var mullionHalf = type.MullionThickness / 2;

        var glass = document.FindMaterial(type.GlassMaterialId);
        var solid = document.FindMaterial(type.SolidMaterialId);
        var metal = document.FindMaterial(type.MullionMaterialId);

        SectionPiece Piece(double left, double right, double low, double high, SectionPart part, SectionDepth depth, Material? material, ColourRgb fallback) =>
            new(new SectionRect(Math.Min(left, right), bottom + low, Math.Max(left, right), bottom + high), part, depth,
                (depth == SectionDepth.Cut ? material?.CutColour : material?.SurfaceColour) ?? fallback,
                material?.Name ?? type.Name, wall.Id);

        // Where the section line crosses the wall, as distances along it.
        var line = WallCurve.Of(marker.Start, marker.End, 0);
        var crossings = line.Crossings(wall.LocationCurve)
            .Select(p => wall.Locate(structure, p).Along)
            .Where(s => s >= -Epsilon && s <= layout.Length + Epsilon)
            .ToList();

        if (crossings.Count > 0)
        {
            foreach (var s in crossings)
            {
                double X(double across) => marker.DistanceAlong(wall.PointAt(structure, s, across));

                var through = layout.Mullions.FirstOrDefault(m => m.IsVertical && s >= m.From && s <= m.To);
                if (through is not null)
                {
                    pieces.Add(Piece(X(mullionHalf), X(-mullionHalf), through.Bottom, through.Top,
                        SectionPart.Frame, SectionDepth.Cut, metal, DefaultCut));
                    continue;
                }

                foreach (var cell in layout.Cells.Where(c => s >= c.From && s <= c.To))
                {
                    if (cell.ClearTop - cell.ClearBottom <= Epsilon) continue;

                    var piece = cell.Kind switch
                    {
                        // Whatever the glass is: clear glass keeps its material, the rest read as themselves,
                        // and laminated glass is drawn thicker because it is two panes bonded together.
                        CurtainPanelKind.Glazed => Piece(
                            X(CurtainGlassLook.ThicknessOf(cell.Glass, panelHalf)), X(-CurtainGlassLook.ThicknessOf(cell.Glass, panelHalf)),
                            cell.ClearBottom, cell.ClearTop, SectionPart.Glazing, SectionDepth.Cut,
                            cell.Glass == CurtainGlass.Clear ? glass : null, CurtainGlassLook.ColourOf(cell.Glass, GlazingColour)),
                        CurtainPanelKind.Solid => Piece(X(panelHalf), X(-panelHalf), cell.ClearBottom, cell.ClearTop, SectionPart.WallLayer, SectionDepth.Cut, solid, DefaultCut),
                        CurtainPanelKind.Door => Piece(X(panelHalf), X(-panelHalf), cell.ClearBottom, cell.ClearTop, SectionPart.DoorLeaf, SectionDepth.Cut, null, LeafColour),
                        _ => null
                    };
                    if (piece is not null) pieces.Add(piece);
                }

                foreach (var transom in layout.Mullions.Where(m => !m.IsVertical && s >= m.From && s <= m.To))
                    pieces.Add(Piece(X(mullionHalf), X(-mullionHalf), transom.Bottom, transom.Top, SectionPart.Frame, SectionDepth.Cut, metal, DefaultCut));
            }

            return;
        }

        // Not cut: seen, if it is in front of the section and within its depth.
        var outline = CurtainGeometry.Band(wall, body, 0, layout.Length, body.Width / 2, -body.Width / 2);
        var depths = outline.Select(marker.DepthOf).ToList();
        if (depths.Max() <= Epsilon || depths.Min() >= marker.ViewDepth) return;

        double Seen(double along) => marker.DistanceAlong(wall.PointAt(structure, along, 0));

        SectionPiece Face(double from, double to, double low, double high, SectionPart part, Material? material, ColourRgb fallback)
        {
            var (a, b) = (Seen(from), Seen(to));
            return Piece(a, b, low, high, part, SectionDepth.Seen, material, fallback) with
            {
                Shape = new[] { (a, bottom + low), (b, bottom + low), (b, bottom + high), (a, bottom + high) }
            };
        }

        foreach (var cell in layout.Cells.Where(c => c.Kind != CurtainPanelKind.Empty && c.ClearTo > c.ClearFrom && c.ClearTop > c.ClearBottom))
        {
            pieces.Add(cell.Kind switch
            {
                CurtainPanelKind.Solid => Face(cell.ClearFrom, cell.ClearTo, cell.ClearBottom, cell.ClearTop, SectionPart.WallFace, solid, DefaultCut),
                CurtainPanelKind.Door => Face(cell.ClearFrom, cell.ClearTo, cell.ClearBottom, cell.ClearTop, SectionPart.DoorLeaf, null, LeafColour),
                _ => Face(cell.ClearFrom, cell.ClearTo, cell.ClearBottom, cell.ClearTop, SectionPart.Glazing,
                    cell.Glass == CurtainGlass.Clear ? glass : null, CurtainGlassLook.ColourOf(cell.Glass, GlazingColour))
            });
        }

        foreach (var mullion in layout.Mullions)
            pieces.Add(Face(mullion.From, mullion.To, mullion.Bottom, mullion.Top, SectionPart.Frame, metal, DefaultCut));
    }

    /// <summary>
    /// The heights a wall with an edited profile is solid at, this far along it: the spans of
    /// its outline there, less any door or window.
    /// </summary>
    private static List<(double Bottom, double Top)> ProfileHeights(
        BimDocument document,
        List<(Opening Opening, OpeningType Type)> openings,
        double baseElevation,
        IReadOnlyList<Point2D> profile,
        double along)
    {
        var spans = WallProfile.HeightsAt(profile, along).Select(s => (Bottom: baseElevation + s.Bottom, Top: baseElevation + s.Top)).ToList();

        foreach (var (opening, openingType) in openings)
        {
            var (placedSill, placedHeight) = opening.Placed(document, openingType, outline: profile);
            var sill = baseElevation + placedSill;
            var head = sill + placedHeight;

            spans = spans
                .SelectMany(s => s.Top <= sill || s.Bottom >= head
                    ? new[] { s }
                    : new[] { (s.Bottom, Math.Min(s.Top, sill)), (Math.Max(s.Bottom, head), s.Top) })
                .Where(s => s.Item2 - s.Item1 > Epsilon)
                .ToList();
        }

        return spans;
    }

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
                var (placedSill, placedHeight) = entry.Opening.Placed(document, entry.Type);
                var sill = Math.Max(baseElevation, baseElevation + placedSill);
                var head = Math.Min(topElevation, baseElevation + placedSill + placedHeight);
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

        // A pitched roof is cut as the sloping thing it is.
        // A roof stands on its base and may slope, so it is cut by its own rule.
        if (slab is Roof roof)
        {
            AddRoof(document, marker, roof, structure, pieces);
            return;
        }

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

    /// <summary>
    /// A pitched roof where a section cuts it.
    ///
    /// Each face is cut on its own. Within one face the roof is a plane and the cut is a
    /// straight line, so the piece is a parallelogram sloping at the pitch - exact, rather
    /// than a staircase of little rectangles pretending to be a slope. Where the cut crosses
    /// a ridge it crosses from one face to the next, and the two pieces meet there.
    /// </summary>
    private static void AddRoof(
        BimDocument document, SectionMarker marker, Roof roof, CompoundStructure structure,
        List<SectionPiece> pieces)
    {
        var origin = marker.Start;
        var ray = marker.Direction;

        // The same pieces the 3D model is built from, eave cuts included, so the cut through
        // the roof and the roof itself agree.
        foreach (var layer in RoofSolid.Pieces(document, roof).GroupBy(piece => piece.Layer))
        {
            var material = document.FindMaterial(layer.Key.MaterialId);

            // Each stretch of this layer the cut crosses: where it starts and ends along the
            // cut, and its underside and top at each end.
            var spans = layer
                .SelectMany(piece => CrossPolygon(marker, piece.Outline).Select(span =>
                {
                    var start = origin + ray * span.From;
                    var end = origin + ray * span.To;
                    return (span.From, span.To,
                        Bottom: (From: piece.Bottom.HeightAt(start), To: piece.Bottom.HeightAt(end)),
                        Top: (From: piece.Top.HeightAt(start), To: piece.Top.HeightAt(end)));
                }))
                .OrderBy(span => span.From)
                .ToList();

            // Stretches that follow on from each other are one piece of the layer, drawn as
            // one outline: the joins between them are where the eave cut changes, not edges
            // of anything, and a section that drew them would show seams that are not there.
            var run = new List<(double From, double To, (double From, double To) Bottom, (double From, double To) Top)>();

            void Flush()
            {
                if (run.Count == 0) return;

                var shape = run.Select(span => (span.From, span.Bottom.From))
                    .Append((run[^1].To, run[^1].Bottom.To))
                    .Concat(run.AsEnumerable().Reverse().Select(span => (span.To, span.Top.To)))
                    .Append((run[0].From, run[0].Top.From))
                    .ToList();

                pieces.Add(new SectionPiece(
                    new SectionRect(run[0].From, shape.Min(corner => corner.Item2), run[^1].To, shape.Max(corner => corner.Item2)),
                    SectionPart.SlabLayer,
                    SectionDepth.Cut,
                    material?.CutColour ?? DefaultCut,
                    material?.Name ?? layer.Key.Function.ToString(),
                    roof.Id)
                {
                    Shape = shape
                });

                run.Clear();
            }

            foreach (var span in spans)
            {
                if (run.Count > 0 &&
                    (Math.Abs(span.From - run[^1].To) > 1e-3 ||
                     Math.Abs(span.Top.From - run[^1].Top.To) > 1e-3 ||
                     Math.Abs(span.Bottom.From - run[^1].Bottom.To) > 1e-3))
                {
                    Flush();
                }

                run.Add(span);
            }

            Flush();
        }

        AddRoofSeen(document, marker, roof, pieces);
    }

    /// <summary>
    /// What is seen of a roof beyond the cut: each face in front of the viewer, within the
    /// section's depth, drawn where it lies - the far slope and the hipped ends rising over the
    /// rooms, as the inside of the roof is seen from below, its underside toward the viewer. A
    /// face seen edge on, as the slopes of a gable cut across its ridge are, shows nothing.
    /// </summary>
    private static void AddRoofSeen(BimDocument document, SectionMarker marker, Roof roof, List<SectionPiece> pieces)
    {
        // The underside's layer: the lowest of them.
        var underside = RoofSolid.Pieces(document, roof)
            .GroupBy(piece => piece.Layer)
            .OrderBy(layer => layer.Min(piece => piece.Bottom.HeightAt(piece.Outline[0])))
            .FirstOrDefault();
        if (underside is null) return;

        var material = document.FindMaterial(underside.Key.MaterialId);

        foreach (var piece in underside)
        {
            // Only what is in front of the viewer and not past the far clip, and within the
            // width of the section.
            var seen = ClipTo(piece.Outline, point => marker.DepthOf(point), 0, marker.ViewDepth);
            seen = ClipTo(seen, marker.DistanceAlong, 0, marker.Length);
            if (seen.Count < 3) continue;

            var shape = seen.Select(point => (X: marker.DistanceAlong(point), Y: piece.Bottom.HeightAt(point))).ToList();
            if (Math.Abs(Polygon2D.SignedArea(shape.Select(corner => new Point2D(corner.X, corner.Y)).ToList())) < 1000) continue;

            pieces.Add(new SectionPiece(
                new SectionRect(shape.Min(c => c.X), shape.Min(c => c.Y), shape.Max(c => c.X), shape.Max(c => c.Y)),
                SectionPart.SlabLayer,
                SectionDepth.Seen,
                material?.SurfaceColour ?? DefaultCut,
                material?.Name ?? underside.Key.Function.ToString(),
                roof.Id)
            {
                Shape = shape,
                // Ordered by where it is lowest - its eave, where it meets the walls it stands on - so
                // a wall in front of that edge hides what of the roof is behind it.
                Distance = marker.DepthOf(seen.MinBy(point => piece.Bottom.HeightAt(point)))
            });
        }
    }

    /// <summary>The part of a plan outline where a measure of its points lies between two values.</summary>
    private static List<Point2D> ClipTo(IReadOnlyList<Point2D> outline, Func<Point2D, double> measure, double low, double high)
    {
        var kept = outline.ToList();
        foreach (var (limit, below) in new[] { (low, false), (high, true) })
        {
            if (kept.Count == 0) break;

            double Inside(Point2D point) => below ? limit - measure(point) : measure(point) - limit;
            var next = new List<Point2D>();
            for (var i = 0; i < kept.Count; i++)
            {
                var (a, b) = (kept[i], kept[(i + 1) % kept.Count]);
                var (da, db) = (Inside(a), Inside(b));
                if (da >= 0) next.Add(a);
                if ((da >= 0) != (db >= 0)) next.Add(a + (b - a) * (da / (da - db)));
            }

            kept = next;
        }

        return kept;
    }

    /// <summary>
    /// An architectural column where a section cuts through it: its section run between what it
    /// stands on and what it reaches, in the material it is built of.
    ///
    /// A column joined to a wall is cut as that wall is - it takes the wall's material and its
    /// fill - so the section reads as one piece of construction, which is the same rule the
    /// plan follows. It is what makes a pier in a wall look like a thicker piece of that wall
    /// rather than a column that happens to be standing in it.
    /// </summary>
    private static void AddColumn(
        BimDocument document, SectionMarker marker, Column column, List<SectionPiece> pieces)
    {
        if (document.FindType<ColumnType>(column.TypeId) is not { } type) return;
        if (type.Width <= 0 || type.Depth <= 0) return;

        var bottom = column.GetBaseElevation(document);
        var top = column.GetTopElevation(document);
        if (top - bottom <= Epsilon) return;

        var material = document.FindMaterial(ColumnJoins.CutMaterial(document, column, type));

        // Less whatever wall it is buried in: what is inside the masonry is not there to cut.
        var section = ColumnJoins.CutByWalls(
            document, column, type, column.SectionAt(type), (bottom + top) / 2);

        if (section.IsEmpty) return;

        foreach (var (from, to) in CrossPolygon(marker, section.Outer))
        {
            pieces.Add(new SectionPiece(
                new SectionRect(from, bottom, to, top),
                SectionPart.WallLayer,
                SectionDepth.Cut,
                material?.CutColour ?? DefaultCut,
                material?.Name ?? type.Name,
                column.Id));
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
