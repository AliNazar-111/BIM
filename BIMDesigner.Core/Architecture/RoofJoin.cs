using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// Roofs meeting roofs (specification section 3.3; Revit's Join/Unjoin Roof and Dormer
/// Opening): how a dormer is built with walls.
///
/// The dormer's roof is joined to the roof it comes out of: one of its edges is carried back
/// until the dormer is buried in the main roof along it, and wherever it then runs into the
/// main roof it is trimmed - cut away where it is under the main roof's top, and stood on that
/// top where it is partly in it. The two meet in valleys, as built. Then the main roof is opened
/// for the dormer: cut away under the dormer's roof, between the walls that carry it, so the
/// room below runs on up into the dormer.
///
/// Everything is worked out from planes: each face of a roof is a plane, so where one roof is
/// above another is a half-plane in plan, and the cuts are exact.
/// </summary>
public static class RoofJoin
{
    /// <summary>Pieces smaller than this, mm², are slivers left by cutting, not roof.</summary>
    private const double MinimumArea = 100;

    /// <summary>The roof a roof is joined to, while it still exists.</summary>
    public static Roof? JoinedTarget(BimDocument document, Roof roof) =>
        roof.JoinedTo is { } id
            ? document.Elements.OfType<Roof>().FirstOrDefault(other => other.Id == id && !ReferenceEquals(other, roof))
            : null;

    /// <summary>Each face of a roof: its outline in plan, its top - the weathering surface - and its underside.</summary>
    private static List<(IReadOnlyList<Point2D> Outline, RoofPlane Top, RoofPlane Under)> Faces(BimDocument document, Roof roof)
    {
        var thickness = document.FindType<SlabType>(roof.TypeId)?.Thickness ?? 0;

        return roof.Surface(document).Facets
            .Select(facet => (facet.Outline, Lift(facet.Plane, thickness * facet.Plane.VerticalStretch), facet.Plane))
            .ToList();
    }

    private static RoofPlane Lift(RoofPlane plane, double by) => new(plane.A, plane.B, plane.C + by, plane.Eave + by);

    private static List<PolygonBoolean.Region> Region(IReadOnlyList<Point2D> outline) =>
        new() { new PolygonBoolean.Region(outline, Array.Empty<IReadOnlyList<Point2D>>()) };

    private static List<PolygonBoolean.Region> Intersect(List<PolygonBoolean.Region> regions, IReadOnlyList<Point2D> outline)
    {
        var inside = new List<PolygonBoolean.Region>();
        foreach (var region in regions)
            inside.AddRange(PolygonBoolean.Combine(region.Outer, outline, BooleanOperation.Intersection));

        return inside;
    }

    private static IEnumerable<IReadOnlyList<Point2D>> Outlines(IEnumerable<PolygonBoolean.Region> regions) =>
        regions.SelectMany(Simple).Where(outline => Polygon2D.Area(outline) > MinimumArea);

    /// <summary>
    /// A region as outlines without holes, which is what a piece of roof is built from: one with
    /// a hole in it - an opening in the middle of a face - is cut in two across the hole, and
    /// each half again until none is left with a hole.
    /// </summary>
    private static IEnumerable<IReadOnlyList<Point2D>> Simple(PolygonBoolean.Region region)
    {
        if (region.Holes.Count == 0)
        {
            yield return region.Outer;
            yield break;
        }

        var hole = region.Holes[0];
        var across = hole.Average(point => point.X);
        var reach = region.Outer.Select(point => Math.Abs(point.X - across) + Math.Abs(point.Y)).Max() * 2 + 1000;
        var middleY = region.Outer.Average(point => point.Y);

        var loops = new List<IReadOnlyList<Point2D>> { region.Outer };
        loops.AddRange(region.Holes);

        foreach (var side in new[] { -1, 1 })
        {
            var half = new[]
            {
                new Point2D(across, middleY - reach), new Point2D(across + side * reach, middleY - reach),
                new Point2D(across + side * reach, middleY + reach), new Point2D(across, middleY + reach)
            };

            foreach (var part in PolygonBoolean.Combine(loops, new[] { (IReadOnlyList<Point2D>)half }, BooleanOperation.Intersection))
            foreach (var outline in Simple(part))
                yield return outline;
        }
    }

    /// <summary>Every point of two roofs, so a cut can be made big enough to cover both.</summary>
    private static IReadOnlyList<Point2D> Extent(Roof a, Roof b) => a.Boundary.Concat(b.Boundary).ToList();

    // ---- trimming a joined roof ---------------------------------------------------------

    /// <summary>
    /// A joined roof's pieces, trimmed against the roof it is joined to: whole where they are
    /// clear above its top, stood on its top where they are partly in it, gone where they are
    /// under it. Pieces not over it at all are untouched.
    /// </summary>
    public static IReadOnlyList<RoofPiece> Trim(BimDocument document, Roof roof, IReadOnlyList<RoofPiece> pieces)
    {
        if (JoinedTarget(document, roof) is not { } target) return pieces;

        var faces = Faces(document, target);
        var extent = Extent(roof, target);
        var trimmed = new List<RoofPiece>();

        foreach (var piece in pieces)
        {
            var remaining = Region(piece.Outline);

            foreach (var (outline, top, _) in faces)
            {
                var over = Intersect(remaining, outline);
                if (over.Count == 0) continue;

                remaining = RoofShape.Subtract(remaining, new PolygonBoolean.Region(outline, Array.Empty<IReadOnlyList<Point2D>>()));

                // Clear of the roof below: whole.
                foreach (var part in Outlines(RoofShape.Clip(over, top, piece.Bottom, extent)))
                    trimmed.Add(piece with { Outline = part });

                // Partly in it: standing on its top.
                var standing = RoofShape.Clip(RoofShape.Clip(over, top, piece.Top, extent), piece.Bottom, top, extent);
                foreach (var part in Outlines(standing))
                    trimmed.Add(piece with { Outline = part, Bottom = top });
            }

            foreach (var part in Outlines(remaining))
                trimmed.Add(piece with { Outline = part });
        }

        return trimmed;
    }

    /// <summary>
    /// What of a joined roof shows from above, in plan: its faces where they are above the roof
    /// it is joined to, and wherever they are not over it. The edge of it is the valley where
    /// the two meet. A roof joined to nothing shows all of itself.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<Point2D>> Visible(BimDocument document, Roof roof)
    {
        if (JoinedTarget(document, roof) is not { } target) return new[] { roof.Boundary };

        var faces = Faces(document, target);
        var extent = Extent(roof, target);
        var shown = new List<PolygonBoolean.Region>();

        foreach (var (outline, top, _) in Faces(document, roof))
        {
            var remaining = Region(outline);

            foreach (var (other, otherTop, _) in faces)
            {
                var over = Intersect(remaining, other);
                if (over.Count == 0) continue;

                remaining = RoofShape.Subtract(remaining, new PolygonBoolean.Region(other, Array.Empty<IReadOnlyList<Point2D>>()));
                shown.AddRange(RoofShape.Clip(over, otherTop, top, extent));
            }

            shown.AddRange(remaining);
        }

        return Union(Outlines(shown)).ToList();
    }

    /// <summary>Overlapping and touching outlines joined into as few as they make.</summary>
    private static IEnumerable<IReadOnlyList<Point2D>> Union(IEnumerable<IReadOnlyList<Point2D>> outlines)
    {
        var merged = new List<IReadOnlyList<Point2D>>();

        foreach (var outline in outlines)
        {
            var pending = outline;
            var kept = new List<IReadOnlyList<Point2D>>();

            foreach (var region in merged)
            {
                var union = PolygonBoolean.Combine(region, pending, BooleanOperation.Union);
                if (union.Count == 1)
                {
                    pending = union[0].Outer;
                    continue;
                }

                kept.Add(region);
            }

            kept.Add(pending);
            merged = kept;
        }

        return merged;
    }

    // ---- dormer openings ------------------------------------------------------------------

    /// <summary>
    /// The holes cut in a roof for its dormers: under each dormer's roof, where its underside is
    /// above this roof's top, and between the inside faces of the walls whose tops carry it.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<Point2D>> Openings(BimDocument document, Roof roof)
    {
        // The holes drawn in its sketch, then those cut for its dormers.
        var openings = roof.Openings.Select(opening => opening.Points).ToList();

        foreach (var id in roof.DormerOpenings)
        {
            if (document.Elements.OfType<Roof>().FirstOrDefault(other => other.Id == id && !ReferenceEquals(other, roof)) is not { } dormer)
                continue;

            openings.AddRange(OpeningFor(document, roof, dormer));
        }

        return openings;
    }

    private static IEnumerable<IReadOnlyList<Point2D>> OpeningFor(BimDocument document, Roof roof, Roof dormer)
    {
        var extent = Extent(roof, dormer);
        var faces = Faces(document, roof);
        var under = new List<PolygonBoolean.Region>();

        // Where the dormer's underside is clear above this roof's top: the space the dormer makes.
        foreach (var (outline, _, underside) in Faces(document, dormer))
        {
            foreach (var (other, top, _) in faces)
            {
                var over = Intersect(Region(outline), other);
                if (over.Count > 0) under.AddRange(RoofShape.Clip(over, top, underside, extent));
            }
        }

        var space = Union(Outlines(under)).ToList();
        if (space.Count == 0) yield break;

        var middle = Centroid(space.OrderByDescending(Polygon2D.Area).First());

        // Between the walls that carry the dormer: on the inside of each one's inner face.
        var region = space.Select(outline => new PolygonBoolean.Region(outline, Array.Empty<IReadOnlyList<Point2D>>())).ToList();
        foreach (var wall in document.Walls.Where(wall => wall.TopAttachedTo == dormer.Id && !wall.IsCurved))
        {
            if (document.GetWallType(wall) is not { } type) continue;

            var half = type.Structure.TotalWidth / 2;
            var centre = wall.PointAt(type.Structure, 0, 0);
            var toFace = wall.PointAt(type.Structure, 0, half) - centre;
            var inner = (middle - centre).Dot(toFace) >= 0 ? half : -half;

            var from = wall.PointAt(type.Structure, 0, inner);
            var to = wall.PointAt(type.Structure, wall.Length, inner);
            region = Intersect(region, HalfPlane(from, to, middle, extent));
        }

        foreach (var outline in Outlines(region)) yield return outline;
    }

    /// <summary>The side of the line through two points that a point is on, as a rectangle far bigger than both roofs.</summary>
    private static IReadOnlyList<Point2D> HalfPlane(Point2D from, Point2D to, Point2D inside, IReadOnlyList<Point2D> extent)
    {
        var along = (to - from).NormalisedOrDefault(Vector2D.UnitX);
        var across = along.PerpendicularLeft();
        if ((inside - from).Dot(across) < 0) across = -across;

        var reach = extent.Select(point => point.DistanceTo(from)).DefaultIfEmpty(0).Max() * 4 + 10000;
        return new[]
        {
            from - along * reach, from + along * reach,
            from + along * reach + across * reach, from - along * reach + across * reach
        };
    }

    private static Point2D Centroid(IReadOnlyList<Point2D> outline) =>
        new(outline.Average(point => point.X), outline.Average(point => point.Y));

    /// <summary>A roof's pieces with its dormer openings cut out of them.</summary>
    public static IReadOnlyList<RoofPiece> Open(BimDocument document, Roof roof, IReadOnlyList<RoofPiece> pieces)
    {
        var openings = Openings(document, roof);
        if (openings.Count == 0) return pieces;

        var cut = new List<RoofPiece>();
        foreach (var piece in pieces)
        {
            var remaining = Region(piece.Outline);
            foreach (var opening in openings)
                remaining = RoofShape.Subtract(remaining, new PolygonBoolean.Region(opening, Array.Empty<IReadOnlyList<Point2D>>()));

            foreach (var part in Outlines(remaining)) cut.Add(piece with { Outline = part });
        }

        return cut;
    }

    /// <summary>
    /// The area of a roof's covering, face by face, less what a join trims away and what its
    /// dormer openings take out.
    /// </summary>
    public static double SlopingArea(BimDocument document, Roof roof) =>
        CutFacets(document, roof).Sum(facet => facet.Area * facet.Plane.VerticalStretch);

    /// <summary>
    /// A roof's faces as built: trimmed to where it shows, when it is joined to another roof,
    /// and with its openings - drawn or cut for dormers - taken out. What is exported and
    /// quantified, rather than the faces its outline alone would give.
    /// </summary>
    public static IReadOnlyList<RoofFacet> CutFacets(BimDocument document, Roof roof)
    {
        var surface = roof.Surface(document);
        var shown = JoinedTarget(document, roof) is null ? null : Visible(document, roof);
        var openings = Openings(document, roof);
        if (shown is null && openings.Count == 0) return surface.Facets;

        var cut = new List<RoofFacet>();
        foreach (var facet in surface.Facets)
        {
            var remaining = Region(facet.Outline);
            if (shown is not null)
            {
                var visible = new List<PolygonBoolean.Region>();
                foreach (var outline in shown) visible.AddRange(Intersect(remaining, outline));
                remaining = visible;
            }

            foreach (var opening in openings)
                remaining = RoofShape.Subtract(remaining, new PolygonBoolean.Region(opening, Array.Empty<IReadOnlyList<Point2D>>()));

            cut.AddRange(Outlines(remaining).Select(outline => facet with { Outline = outline }));
        }

        return cut;
    }

    // ---- joining ----------------------------------------------------------------------------

    /// <summary>
    /// Joins a roof to another - Revit's Join Roof: the edge given is carried straight back, its
    /// neighbours running on with it, until the roof is buried in the other along it; then the
    /// roof is trimmed where it runs in. One step to undo. Null, with the reason, when the roof
    /// could never meet the other - it is higher than the other roof anywhere it could reach.
    /// </summary>
    public static IUndoableCommand? Join(BimDocument document, Roof roof, int edge, Roof target, out string? problem)
    {
        problem = null;
        var boundary = roof.Boundary;
        var count = boundary.Count;

        if (ReferenceEquals(roof, target) || roof.IsExtrusion || count < 3 || edge < 0 || edge >= count)
        {
            problem = "Pick an edge of a roof drawn by footprint, then the roof it should run back into.";
            return null;
        }

        var thickness = document.FindType<SlabType>(roof.TypeId)?.Thickness ?? 0;
        var anticlockwise = Polygon2D.SignedArea(boundary) >= 0;
        var from = boundary[edge];
        var to = boundary[(edge + 1) % count];
        var outward = (to - from).NormalisedOrDefault(Vector2D.UnitX).PerpendicularLeft() * (anticlockwise ? -1 : 1);

        for (var reach = 0.0; reach <= 40000; reach += 100)
        {
            var moved = Moved(boundary, edge, outward * reach);
            var surface = RoofShape.Build(moved, roof.Edges, roof.BaseElevation(document), roof.CutoffElevation(document),
                roof.SlopeArrows, e => roof.BearingInset(document, e));

            if (!Buried(document, surface, thickness, moved[edge], moved[(edge + 1) % count], target)) continue;

            // A little further, so the trim - not the edge - is what meets the other roof.
            var final = Moved(boundary, edge, outward * (reach + 100));
            return new CompositeCommand("Join Roof", new IUndoableCommand[]
            {
                new SetRoofSketchCommand(roof, final, roof.Edges, roof.SlopeArrows, "Join Roof"),
                new SetRoofJoinCommand(roof, target.Id)
            });
        }

        problem = "That roof never meets the other one going back from that edge: it stays above it. " +
                  "Pick the edge that faces the roof it should run into, or lower the roof.";
        return null;
    }

    /// <summary>The outline with one edge moved across, its neighbours running on to meet it where it now is.</summary>
    private static List<Point2D> Moved(IReadOnlyList<Point2D> boundary, int edge, Vector2D by)
    {
        var count = boundary.Count;
        var (a, b) = (boundary[edge] + by, boundary[(edge + 1) % count] + by);
        var moved = boundary.ToList();

        var before = (boundary[(edge - 1 + count) % count], boundary[edge]);
        var after = (boundary[(edge + 1) % count], boundary[(edge + 2) % count]);

        moved[edge] = Crossing(before.Item1, before.Item2, a, b) ?? a;
        moved[(edge + 1) % count] = Crossing(after.Item1, after.Item2, a, b) ?? b;
        return moved;
    }

    /// <summary>Whether a roof's top, all along an edge, is under the other roof's top - the edge buried in it.</summary>
    private static bool Buried(BimDocument document, RoofSurface surface, double thickness, Point2D from, Point2D to, Roof target)
    {
        var samples = Enumerable.Range(0, 21).Select(i => from + (to - from) * (i / 20.0)).ToList();

        // Where a ridge or valley crosses the edge, the roof's top has a corner - check there too.
        foreach (var (a, b) in surface.BreakLines)
            if (Crossing(from, to, a, b) is { } at && Line2D.DistanceFromSegment(at, from, to) < 1) samples.Add(at);

        foreach (var point in samples)
        {
            if (!target.Contains(point)) return false;

            var face = surface.FaceAt(point);
            var top = surface.HeightAt(point) + thickness * (face?.Plane.VerticalStretch ?? 1);
            if (top > target.TopAt(document, point) - 1) return false;
        }

        return true;
    }

    private static Point2D? Crossing(Point2D a0, Point2D a1, Point2D b0, Point2D b1)
    {
        var a = a1 - a0;
        var b = b1 - b0;
        var denominator = a.Cross(b);
        if (Math.Abs(denominator) <= a.Length * b.Length * 1e-9) return null;

        return a0 + a * ((b0 - a0).Cross(b) / denominator);
    }
}

/// <summary>Joins a roof to another, or unjoins it, as one undoable step.</summary>
public sealed class SetRoofJoinCommand : IUndoableCommand
{
    private readonly Roof _roof;
    private readonly Guid? _old;
    private readonly Guid? _new;

    public SetRoofJoinCommand(Roof roof, Guid? target)
    {
        _roof = roof;
        _old = roof.JoinedTo;
        _new = target;
    }

    public string Name => _new is null ? "Unjoin Roof" : "Join Roof";

    public void Redo() => _roof.JoinedTo = _new;

    public void Undo() => _roof.JoinedTo = _old;
}

/// <summary>Opens a roof for a dormer, or closes the opening again, as one undoable step.</summary>
public sealed class SetDormerOpeningCommand : IUndoableCommand
{
    private readonly Roof _roof;
    private readonly Guid _dormer;
    private readonly bool _open;

    public SetDormerOpeningCommand(Roof roof, Guid dormer, bool open)
    {
        _roof = roof;
        _dormer = dormer;
        _open = open;
    }

    public string Name => _open ? "Dormer Opening" : "Remove Dormer Opening";

    public void Redo() => Set(_open);

    public void Undo() => Set(!_open);

    private void Set(bool open)
    {
        _roof.DormerOpenings.Remove(_dormer);
        if (open) _roof.DormerOpenings.Add(_dormer);
    }
}
