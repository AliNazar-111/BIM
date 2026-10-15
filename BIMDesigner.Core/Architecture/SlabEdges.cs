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
        if (slab.Boundary.Count < 3 || !(slab is Floor || slab is Roof && FloorSurfaces.IsStoodOn(document, slab))) return Array.Empty<(Wall, WallType)>();

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

    /// <summary>How far past the outer face of a wall a floor may reach and still be a lip to cut off - not a balcony or a terrace, mm.</summary>
    public const double LipDepth = 600;

    /// <summary>
    /// The lips of a floor or a roof that run on a little past an outside wall - or a curtain wall
    /// - rising above it: standing on it, as an upper storey's wall stands on its floor, or coming
    /// up through it. Each is cut back to the wall's face, whichever was drawn first, so the floor
    /// or the roof stops at the wall rather than coming out through it - inside or out. A balcony
    /// or a terrace, reaching 600 or more past the wall, is left; and so are the eaves of a roof
    /// over the walls carrying it, and a slab on the ground running on out as a step or an apron.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<Point2D>> Lips(BimDocument document, Slab slab)
    {
        var lips = new List<IReadOnlyList<Point2D>>();
        if (slab.Boundary.Count < 3 || slab is Ceiling || slab is Roof { IsExtrusion: true }) return lips;

        // A slab on the ground - the floor of the lowest storey with walls, or below it - runs on out as a step or an apron.
        var grounded = document.Levels.Where(level => document.Walls.Any(wall => wall.LevelId == level.Id)).Select(level => (double?)level.Elevation).Min()
                       ?? (document.Levels.Count > 0 ? document.Levels.Min(level => level.Elevation) : 0);
        if (slab.GetTopElevation(document) <= grounded + 50) return lips;

        // A dormer's walls stand on the roof they rise out of, its eaves running on past them.
        var dormers = document.Elements.OfType<Roof>().SelectMany(roof => roof.DormerWalls).ToHashSet();
        var (min, max) = (new Point2D(slab.Boundary.Min(point => point.X), slab.Boundary.Min(point => point.Y)), new Point2D(slab.Boundary.Max(point => point.X), slab.Boundary.Max(point => point.Y)));
        foreach (var wall in document.Walls)
        {
            if (wall.Length < 1 || dormers.Contains(wall.Id) || document.GetWallType(wall) is not { } body) continue;
            var curtain = document.IsCurtainWall(wall);
            if (!curtain && body.Function != WallFunction.Exterior) continue;
            if (wall.RoofUnder(document) is { } under && !under.Surface(document).IsFlat) continue;

            var structure = body.Structure;
            var half = structure.TotalWidth / 2;
            var reach = half + LipDepth + 50;

            // Nowhere near it: passed over, before anything about it is worked out.
            var bulge = reach + (wall.IsCurved ? wall.Length / 2 : 0);
            if (Math.Max(wall.Start.X, wall.End.X) + bulge < min.X || Math.Min(wall.Start.X, wall.End.X) - bulge > max.X
                || Math.Max(wall.Start.Y, wall.End.Y) + bulge < min.Y || Math.Min(wall.Start.Y, wall.End.Y) - bulge > max.Y) continue;

            // Standing on it or coming up through it, and rising well above it there - a gable to its apex: not a wall under it, carrying it.
            var middle = wall.LocationCurve.PointAt(wall.Length / 2);
            var surface = slab is Roof roof && roof.Contains(middle) ? roof.TopAt(document, middle) : slab.GetTopElevation(document);
            var (foot, head) = Storeys.Span(document, wall);
            if (foot > surface + 50 || head < surface + 300) continue;

            // Along it, and on past its ends as far as a lip would reach round a corner.
            var length = wall.Length;
            var way = (wall.PointAt(structure, length, 0) - wall.PointAt(structure, 0, 0)).NormalisedOrDefault(Vector2D.UnitX);
            Point2D At(double along, double off)
            {
                var clamped = Math.Clamp(along, 0, length);
                return wall.PointAt(structure, clamped, off) + (wall.IsCurved ? new Vector2D(0, 0) : way * (along - clamped));
            }

            var extend = wall.IsCurved ? 0 : half + LipDepth;
            const double step = 200;
            var count = Math.Max(1, (int)Math.Ceiling((length + 2 * extend) / step));
            foreach (var side in new[] { 1.0, -1.0 })
            {
                var run = new List<double>();
                void Flush()
                {
                    if (run.Count > 0)
                    {
                        var (from, to) = (Math.Max(-extend, run[0] - step / 2), Math.Min(length + extend, run[^1] + step / 2));
                        var near = half - 1;
                        var far = half + LipDepth + 50;

                        // Round a curved wall, following its face, not cut straight across inside it.
                        var pieces = wall.IsCurved ? Math.Max(1, (int)Math.Ceiling((to - from) / step)) : 1;
                        var alongs = Enumerable.Range(0, pieces + 1).Select(k => from + (to - from) * k / pieces).ToList();
                        var cutout = alongs.Select(along => At(along, side * near)).Concat(alongs.AsEnumerable().Reverse().Select(along => At(along, side * far))).ToList();
                        if (Polygon2D.SignedArea(cutout) < 0) cutout.Reverse();
                        lips.Add(cutout);
                    }

                    run.Clear();
                }

                for (var k = 0; k <= count; k++)
                {
                    var along = -extend + (length + 2 * extend) * k / count;
                    var lip = slab.Contains(At(along, side * (half + 30))) && !slab.Contains(At(along, side * (half + LipDepth)))
                              && slab.Contains(At(Math.Clamp(along, 0, length), -side * (half + 300)));
                    if (lip) run.Add(along);
                    else Flush();
                }

                Flush();
            }
        }

        return lips;
    }

    /// <summary>
    /// What of a roof is on the far side of the walls it runs through - curtain walls, or outside
    /// walls, passing up past its whole depth, as a glass front rising past the roof behind it to
    /// its gable does - where they divide it: the roof stops at the walls, and what of it they cut
    /// off from the most of it is taken away, at their faces, whichever was drawn first. Walls
    /// passing through without dividing it - a dormer's cheeks - leave it whole, and so does a
    /// wall a canopy only meets.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<Point2D>> Beyond(BimDocument document, Slab slab)
    {
        if (slab is not Roof { IsExtrusion: false } roof || roof.Boundary.Count < 3) return Array.Empty<IReadOnlyList<Point2D>>();

        var blades = new List<IReadOnlyList<Point2D>>();
        foreach (var wall in document.Walls)
        {
            if (wall.Length < 1 || document.GetWallType(wall) is not { } body) continue;
            var curtain = document.IsCurtainWall(wall);
            if (!curtain && body.Function != WallFunction.Exterior) continue;

            // Past its whole depth: from under its underside to over its top, where the wall is.
            var middle = wall.LocationCurve.PointAt(wall.Length / 2);
            var (under, over) = roof.Contains(middle)
                ? (roof.UndersideAt(document, middle), roof.TopAt(document, middle))
                : (roof.GetBottomElevation(document), roof.GetTopElevation(document));
            var (foot, head) = Storeys.Span(document, wall);
            if (foot > under - 50 || head < over + 50) continue;
            if (Overlap(roof.Boundary, Slot(wall, body, 300)) < 1000) continue;

            // Face to face, and on past its ends far enough to close the corner where it meets the next.
            var structure = body.Structure;
            var half = structure.TotalWidth / 2;
            var length = wall.Length;
            if (wall.IsCurved)
            {
                blades.Add(Slot(wall, body, -1));
                continue;
            }

            var way = (wall.PointAt(structure, length, 0) - wall.PointAt(structure, 0, 0)).NormalisedOrDefault(Vector2D.UnitX);
            var (from, to) = (wall.PointAt(structure, 0, 0) - way * (half + 10), wall.PointAt(structure, length, 0) + way * (half + 10));
            var across = new Vector2D(-way.Y, way.X) * (half - 1);
            var blade = new List<Point2D> { from - across, to - across, to + across, from + across };
            if (Polygon2D.SignedArea(blade) < 0) blade.Reverse();
            blades.Add(blade);
        }

        if (blades.Count == 0) return Array.Empty<IReadOnlyList<Point2D>>();

        // What is left of it either side of them: divided, the most of it kept.
        IReadOnlyList<PolygonBoolean.Region> pieces = new[] { new PolygonBoolean.Region(roof.Boundary, Array.Empty<IReadOnlyList<Point2D>>()) };
        foreach (var blade in blades)
            pieces = pieces
                .SelectMany(piece => PolygonBoolean.Combine(Shafts.Loops(piece), new[] { blade }, BooleanOperation.Difference))
                .Where(piece => piece.Area > 1000)
                .ToList();
        if (pieces.Count < 2) return Array.Empty<IReadOnlyList<Point2D>>();

        var most = pieces.MaxBy(piece => piece.Area);
        return pieces.Where(piece => piece != most && piece.Holes.Count == 0).Select(piece => piece.Outer).ToList();
    }

    /// <summary>Whether a floor is anything other than its sketched outline: cut back from a curtain wall, or a lip past a wall, or with a shaft through it.</summary>
    public static bool IsCut(BimDocument document, Slab slab) =>
        Shafts.Through(document, slab).Any() || CurtainWallsAt(document, slab).Count > 0 || Chimneys.Holes(document, slab).Any() ||
        RoofDrainage.PipeHoles(document, slab).Any() || Stairs.Holes(document, slab).Any() || Lips(document, slab).Count > 0 || Beyond(document, slab).Count > 0;

    /// <summary>A floor's plan as built: its outline cut back from the curtain walls in front of it and the walls it reaches past, less the shafts through it.</summary>
    public static IReadOnlyList<PolygonBoolean.Region> Regions(BimDocument document, Slab slab)
    {
        var cuts = CurtainWallsAt(document, slab).Select(entry => Slot(entry.Wall, entry.Body, SafingGap))
            .Concat(Lips(document, slab))
            .Concat(Beyond(document, slab))
            .Concat(Shafts.Holes(document, slab))
            .Concat(Chimneys.Holes(document, slab))
            .Concat(RoofDrainage.PipeHoles(document, slab))
            .Concat(Stairs.Holes(document, slab))
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
