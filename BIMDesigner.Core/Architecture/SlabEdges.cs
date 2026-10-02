using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// A floor as it is built at its edges. A curtain wall is hung in front of the floors it runs
/// past, not built on them: each floor stops short of it, leaving the safing slot - 25 to 200 mm
/// across - that is packed with fire-stop insulation and sealed over, so fire and smoke cannot
/// climb from one floor to the next behind the glass. And the shafts through a floor are taken
/// out of it.
/// </summary>
public static class SlabEdges
{
    /// <summary>The gap between a floor's edge and the curtain wall in front of it, mm.</summary>
    public const double SafingGap = 50;

    /// <summary>How far round a curtain wall is looked at for a floor behind it, mm.</summary>
    private const double Behind = 300;

    /// <summary>
    /// The curtain walls running past a floor's edge: over its outline in plan, or right up to
    /// it, and across its depth - down past it, or up from under it. A curtain wall standing on
    /// the floor, its foot on the floor's top, is not in front of its edge.
    /// </summary>
    public static IReadOnlyList<(Wall Wall, WallType Body)> CurtainWallsAt(BimDocument document, Slab slab)
    {
        if (slab is not Floor || slab.Boundary.Count < 3) return Array.Empty<(Wall, WallType)>();

        var (bottom, top) = (slab.GetBottomElevation(document), slab.GetTopElevation(document));
        var walls = new List<(Wall, WallType)>();
        foreach (var wall in document.Walls.Where(document.IsCurtainWall))
        {
            var foot = wall.GetBaseElevation(document);
            if (foot >= top - 1 || foot + CurtainHeight(document, wall) <= bottom + 1) continue;
            if (document.GetWallType(wall) is not { } body) continue;

            if (Overlap(slab.Boundary, Slot(wall, body, SafingGap)) > 100) walls.Add((wall, body));
        }

        return walls;
    }

    /// <summary>
    /// Whether a slab runs up behind a curtain wall: its outline in plan reaching the wall, or
    /// within a hand's breadth of it. What a curtain wall's spandrels line up with.
    /// </summary>
    public static bool Meets(BimDocument document, Wall wall, Slab slab) =>
        slab.Boundary.Count >= 3 && document.GetWallType(wall) is { } body && Overlap(slab.Boundary, Slot(wall, body, Behind)) > 1000;

    /// <summary>How tall a curtain wall is, up to the highest point of a top cut to a roof.</summary>
    public static double CurtainHeight(BimDocument document, Wall wall) =>
        WallProfile.CurtainTop(document, wall) is { } top ? top.Max(point => point.Y) : wall.GetHeight(document);

    /// <summary>
    /// A wall's footprint in plan, widened by a margin on each side: what a floor is cut back
    /// from. Followed round a curved wall in short straight pieces.
    /// </summary>
    public static IReadOnlyList<Point2D> Slot(Wall wall, WallType body, double margin)
    {
        var structure = body.Structure;
        var reach = structure.TotalWidth / 2 + margin;
        var pieces = wall.IsCurved ? 24 : 1;
        var length = wall.Length;

        var left = new List<Point2D>();
        var right = new List<Point2D>();
        for (var i = 0; i <= pieces; i++)
        {
            var along = length * i / pieces;
            left.Add(wall.PointAt(structure, along, reach));
            right.Add(wall.PointAt(structure, along, -reach));
        }

        var outline = left.Concat(Enumerable.Reverse(right)).ToList();
        if (Polygon2D.SignedArea(outline) < 0) outline.Reverse();
        return outline;
    }

    private static double Overlap(IReadOnlyList<Point2D> a, IReadOnlyList<Point2D> b) =>
        PolygonBoolean.Combine(a, b, BooleanOperation.Intersection).Sum(region => region.Area);

    /// <summary>Whether a floor is anything other than its sketched outline: cut back from a curtain wall, or with a shaft through it.</summary>
    public static bool IsCut(BimDocument document, Slab slab) =>
        Shafts.Through(document, slab).Any() || CurtainWallsAt(document, slab).Count > 0 || Chimneys.Holes(document, slab).Any() ||
        RoofDrainage.PipeHoles(document, slab).Any();

    /// <summary>A floor's plan as built: its outline cut back from the curtain walls in front of it, less the shafts through it.</summary>
    public static IReadOnlyList<PolygonBoolean.Region> Regions(BimDocument document, Slab slab)
    {
        var cuts = CurtainWallsAt(document, slab).Select(entry => Slot(entry.Wall, entry.Body, SafingGap))
            .Concat(Shafts.Holes(document, slab))
            .Concat(Chimneys.Holes(document, slab))
            .Concat(RoofDrainage.PipeHoles(document, slab))
            .ToList();

        IReadOnlyList<PolygonBoolean.Region> regions = new[] { new PolygonBoolean.Region(slab.Boundary, Array.Empty<IReadOnlyList<Point2D>>()) };
        foreach (var cut in cuts)
            regions = regions
                .SelectMany(region => PolygonBoolean.Combine(Shafts.Loops(region), new[] { cut }, BooleanOperation.Difference))
                .Where(region => region.Area > 100)
                .ToList();

        return regions;
    }

    /// <summary>
    /// The fire stops packed into the slots between a floor's edge and the curtain walls in front
    /// of it: from the floor's edge to the wall's inner face, the depth of the floor.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<Point2D>> FireStops(BimDocument document, Slab slab)
    {
        var stops = new List<IReadOnlyList<Point2D>>();
        foreach (var (wall, body) in CurtainWallsAt(document, slab))
        {
            // The slot either side of the wall, where the floor was.
            var slots = PolygonBoolean.Combine(new[] { Slot(wall, body, SafingGap) }, new[] { Slot(wall, body, 0) }, BooleanOperation.Difference);
            foreach (var slot in slots)
            foreach (var stop in PolygonBoolean.Combine(Shafts.Loops(slot), new[] { slab.Boundary }, BooleanOperation.Intersection))
                if (stop.Area > 100) stops.Add(stop.Outer);
        }

        return stops;
    }

    /// <summary>A floor's area as built: less the shafts through it and the slots at its curtain walls, mm².</summary>
    public static double NetArea(BimDocument document, Slab slab) =>
        IsCut(document, slab) ? Regions(document, slab).Sum(region => region.Area) : slab.Area;
}
