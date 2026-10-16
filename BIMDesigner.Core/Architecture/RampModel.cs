using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// A length of guarding along one side of a ramp, where it drops too far to be left open: its
/// points on the kerb, with the ramp's surface height there, and how high it stands over it.
/// </summary>
public sealed record RampGuard(IReadOnlyList<RampEdgePoint> Points, double Height);

/// <summary>
/// A ramp's solids in their parts: each flight and landing, its kerbs along its sides, its
/// handrails with their posts, and its guarding where it is high - as IFC has them.
/// </summary>
public sealed record RampParts(IReadOnlyList<(RampPiece Piece, Mesh3D Body)> Pieces, IReadOnlyList<Mesh3D> Kerbs, IReadOnlyList<Mesh3D> Handrails, IReadOnlyList<Mesh3D> Guarding)
{
    public IReadOnlyList<Mesh3D> All =>
        Pieces.Select(piece => piece.Body).Concat(Kerbs).Concat(Handrails).Concat(Guarding).Where(mesh => !mesh.IsEmpty).ToList();
}

/// <summary>
/// A ramp as it is built, for the 3D view, sections and IFC: its flights and landings - solid
/// down to below its foot and to the ground where that falls away, or a slab or deck of its own
/// thickness - a kerb along each open side, its sides taken down to the ground if it is closed
/// underneath; its handrails running on past its ends and turned down there, on posts on the
/// kerb or on brackets from a wall beside it, a second rail at 600 if its type has one, and
/// guarding - balusters or glass - where an open side drops too far.
/// </summary>
public static class RampModel
{
    private static readonly ColourRgb Steel = new(0xBF, 0xC4, 0xC9);
    private static readonly ColourRgb GlassTint = new(0xBF, 0xDC, 0xE6);

    private const double PostSize = 40;
    private const double BalusterSize = 16;
    private const double BalusterSpacing = 110;
    private const double GlassThickness = 12;

    /// <summary>The gap between a handrail on a wall and the wall, for the hand: Approved Document M asks 50 to 75, the ADA 1½ in (38) at least.</summary>
    public const double WallGap = 50;

    /// <summary>How far apart the brackets holding a handrail on a wall are, at most, mm.</summary>
    private const double BracketSpacing = 900;

    public static IReadOnlyList<Mesh3D> Meshes(BimDocument document, Ramp ramp) => Parts(document, ramp).All;

    public static RampParts Parts(BimDocument document, Ramp ramp)
    {
        var type = Ramps.TypeOf(document, ramp);
        var layout = Ramps.Layout(document, ramp);
        var sides = Ramps.Sides(document, layout);
        var made = document.FindMaterial(type.MaterialId);
        var railMade = document.FindMaterial(type.RailMaterialId);
        Mesh3D Body() => new(ramp.Id, ramp.LevelId, MeshKind.Ramp, made?.SurfaceColour ?? type.Colour, made?.Name ?? type.Name);
        Mesh3D Rail() => new(ramp.Id, ramp.LevelId, MeshKind.Ramp, railMade?.SurfaceColour ?? Steel, railMade?.Name ?? "Stainless Steel");

        // Its flights and landings: solid down to under its foot - and on down to the ground where
        // the ground falls away under it - or a slab its thickness under its surface.
        var solid = type.Construction == RampConstruction.Solid;
        var floor = layout.Bottom - type.Thickness;
        double Founded(Point2D at) => Terrains.GroundAt(document, at) is { } ground && ground < layout.Bottom ? Math.Min(floor, ground - 100) : floor;
        var pieces = new List<(RampPiece, Mesh3D)>();
        foreach (var piece in layout.Pieces)
        {
            var body = Body();
            body.AddExtrusion(piece.Outline, at => solid ? Founded(at) : piece.HeightAt(at) - type.Thickness, piece.HeightAt);
            pieces.Add((piece, body));
        }

        // A kerb along each open side - or, with none, the edge its handrail posts stand on - none
        // along a wall; closed underneath, taken down to the floor or the ground under it.
        var kerbs = Body();
        var closed = !solid && ramp.ClosedUnderneath;
        double Under(Point2D at, double surface) =>
            solid ? Founded(at) : closed ? Math.Min(surface - type.Thickness, Beneath(document, layout, at, surface - type.Thickness)) : surface - type.Thickness;
        foreach (var (side, outward, faces) in new[] { (sides.Left.Points, 1.0, sides.Left.Walls), (sides.Right.Points, -1.0, sides.Right.Walls) })
        {
            var outer = Outside(side, outward * type.KerbWidth);
            for (var i = 0; i + 1 < side.Count; i++)
            {
                var (a, b) = (side[i], side[i + 1]);
                if (a.At.DistanceTo(b.At) < 1 || faces[i] is not null) continue;
                double Surface(Point2D at) => Along(a, b, at);
                kerbs.AddExtrusion(new[] { a.At, b.At, outer[i + 1], outer[i] }, at => Under(at, Surface(at)), at => Surface(at) + type.KerbHeight);
            }
        }

        // Its handrails, turned down at each end so a sleeve or a strap is not caught on them.
        var rails = Rail();
        var (left, right) = Ramps.Handrails(document, ramp, layout);
        var radius = type.HandrailDiameter / 2;
        foreach (var run in HandrailRuns(document, ramp, layout, sides))
        {
            var path = run.ToList();
            path.Insert(0, path[0] with { Z = path[0].Z - 100 });
            path.Add(path[^1] with { Z = path[^1].Z - 100 });
            rails.AddTube(path, radius, 10);
        }

        // On posts on the kerb along an open side, on brackets from the wall along a wall; and a second rail lower down if its type has one.
        foreach (var (side, outward, wanted, faces) in new[] { (sides.Left.Points, 1.0, left, sides.Left.Walls), (sides.Right.Points, -1.0, right, sides.Right.Walls) })
        {
            if (!wanted || side.Count < 2) continue;
            if (type.LowerRail) rails.AddTube(Handrail(side, faces, outward, radius).Select(point => Point3D.On(point.At, point.Height + 600)).ToList(), radius, 10);
            for (var i = 0; i + 1 < side.Count; i++)
            {
                var (a, b) = (side[i], side[i + 1]);
                var length = a.At.DistanceTo(b.At);
                if (length < 1) continue;
                var outwards = (b.At - a.At).PerpendicularLeft() / length * outward;
                if (faces[i] is { } face)
                {
                    var count = Math.Max(1, (int)Math.Ceiling(length / BracketSpacing - 1e-9));
                    for (var k = 0; k < count; k++)
                    {
                        var t = (k + 0.5) / count;
                        var at = a.At + (b.At - a.At) * t;
                        var under = a.Height + (b.Height - a.Height) * t + type.HandrailHeight - radius - 30;
                        var rail = at + outwards * (face - WallGap - radius);
                        rails.AddTube(new[] { Point3D.On(rail, under + 30), Point3D.On(rail, under), Point3D.On(at + outwards * face, under) }, 8, 8);
                    }

                    continue;
                }

                var posts = Math.Max(1, (int)Math.Ceiling(length / Math.Max(300, type.PostSpacing) - 1e-9));
                for (var k = i > 0 && faces[i - 1] is null ? 1 : 0; k <= posts; k++)
                {
                    var t = (double)k / posts;
                    var height = a.Height + (b.Height - a.Height) * t;
                    Post(rails, a.At + (b.At - a.At) * t + outwards * radius, height + type.KerbHeight, height + type.HandrailHeight - radius);
                }
            }
        }

        // Its guarding where an open side drops too far: a top rail at the height asked, balusters or glass under it.
        var guarding = new List<Mesh3D>();
        var guardRails = Rail();
        var glass = new Mesh3D(ramp.Id, ramp.LevelId, MeshKind.Ramp, GlassTint, "Glass, Toughened Laminated") { Opacity = 0.35 };
        foreach (var guard in Guards(document, ramp, layout, sides))
        {
            var points = guard.Points;
            guardRails.AddTube(points.Select(point => Point3D.On(point.At, point.Height + guard.Height - 20)).ToList(), 20, 10);
            foreach (var (at, height) in Posts(points, type.PostSpacing))
                Post(guardRails, at, height + type.KerbHeight, height + guard.Height - 40);
            for (var i = 0; i + 1 < points.Count; i++)
            {
                var (a, b) = (points[i], points[i + 1]);
                var length = a.At.DistanceTo(b.At);
                if (length < 1) continue;
                var along = (b.At - a.At) / length;
                if (type.Guarding == RampGuarding.Glass)
                {
                    var across = along.PerpendicularLeft() * (GlassThickness / 2);
                    glass.AddExtrusion(new[] { a.At - across, b.At - across, b.At + across, a.At + across },
                        at => Along(a, b, at) + type.KerbHeight + 30,
                        at => Along(a, b, at) + guard.Height - 60);
                    continue;
                }

                var count = Math.Max(1, (int)Math.Ceiling(length / BalusterSpacing));
                for (var k = 0; k < count; k++)
                {
                    var at = a.At + along * ((k + 0.5) * length / count);
                    var height = Along(a, b, at);
                    Post(guardRails, at, height + type.KerbHeight, height + guard.Height - 40, BalusterSize);
                }
            }
        }

        guarding.Add(guardRails);
        guarding.Add(glass);
        return new RampParts(pieces, new[] { kerbs }, new[] { rails }, guarding);
    }

    /// <summary>
    /// Its handrails' centre lines, side by side: each along a side - its inner face at the
    /// ramp's clear width on posts, or the hand's gap off a wall beside it - at its handrail height
    /// over the surface - over a flight's slope, straight up - running on level past its foot and
    /// its head as far as its type says.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<Point3D>> HandrailRuns(BimDocument document, Ramp ramp, RampLayout layout) =>
        HandrailRuns(document, ramp, layout, Ramps.Sides(document, layout));

    private static IReadOnlyList<IReadOnlyList<Point3D>> HandrailRuns(BimDocument document, Ramp ramp, RampLayout layout, (RampSide Left, RampSide Right) sides)
    {
        var type = Ramps.TypeOf(document, ramp);
        var (left, right) = Ramps.Handrails(document, ramp, layout);
        var runs = new List<IReadOnlyList<Point3D>>();
        foreach (var (side, outward, wanted, faces) in new[] { (sides.Left.Points, 1.0, left, sides.Left.Walls), (sides.Right.Points, -1.0, right, sides.Right.Walls) })
        {
            if (!wanted || side.Count < 2) continue;
            var path = Handrail(side, faces, outward, type.HandrailDiameter / 2).Select(point => Point3D.On(point.At, point.Height + type.HandrailHeight)).ToList();
            if (path.Count < 2) continue;
            if (type.HandrailExtension > 0.5)
            {
                var (first, last) = (path[0], path[^1]);
                var (foot, head) = (layout.FootUp * -type.HandrailExtension, layout.HeadUp * type.HandrailExtension);
                path.Insert(0, first with { X = first.X + foot.X, Y = first.Y + foot.Y });
                path.Add(last with { X = last.X + head.X, Y = last.Y + head.Y });
            }

            runs.Add(path);
        }

        return runs;
    }

    /// <summary>
    /// Where its sides want guarding: each stretch of an open side that drops more than its rules
    /// allow to be left open - to a floor or the ground beside it, or, with none, below its foot -
    /// as a line along the middle of its kerb, at the height asked: higher round a landing,
    /// elsewhere than a dwelling. None along a wall, nor for one told to have no handrails.
    /// </summary>
    public static IReadOnlyList<RampGuard> Guards(BimDocument document, Ramp ramp, RampLayout layout) =>
        Guards(document, ramp, layout, Ramps.Sides(document, layout));

    private static IReadOnlyList<RampGuard> Guards(BimDocument document, Ramp ramp, RampLayout layout, (RampSide Left, RampSide Right) sides)
    {
        var guards = new List<RampGuard>();
        if (ramp.Railings == StairRailings.None) return guards;

        var type = Ramps.TypeOf(document, ramp);
        var limit = Railings.GuardedDrop(document, ramp.Building);
        var reach = type.KerbWidth + 200;
        foreach (var (side, outward, faces) in new[] { (sides.Left.Points, 1.0, sides.Left.Walls), (sides.Right.Points, -1.0, sides.Right.Walls) })
        {
            if (side.Count < 2) continue;
            var line = Outside(side, outward * type.KerbWidth / 2);
            var run = new List<RampEdgePoint>();
            double? height = null;
            for (var i = 0; i + 1 < side.Count; i++)
            {
                var (a, b) = (side[i], side[i + 1]);
                var across = (b.At - a.At).NormalisedOrDefault(Vector2D.UnitX).PerpendicularLeft() * (outward * reach);
                var drop = faces[i] is not null ? 0
                    : new[] { (a.At, a.Height), (a.At.MidpointTo(b.At), (a.Height + b.Height) / 2), (b.At, b.Height) }
                        .Max(sample => Ramps.DropBeside(document, layout, sample.Item1 + across, sample.Item2));
                var landing = Math.Abs(b.Height - a.Height) < 1;
                var asked = Ramps.GuardHeight(document, ramp, landing).Height;
                if (drop <= limit + 0.5 || (height is { } before && Math.Abs(before - asked) > 1))
                {
                    if (run.Count >= 2) guards.Add(new RampGuard(run, height ?? asked));
                    run = new List<RampEdgePoint>();
                    height = null;
                }

                if (drop <= limit + 0.5) continue;
                if (run.Count == 0) run.Add(new RampEdgePoint(line[i], a.Height));
                run.Add(new RampEdgePoint(line[i + 1], b.Height));
                height = asked;
            }

            if (run.Count >= 2) guards.Add(new RampGuard(run, height ?? 900));
        }

        return guards;
    }

    /// <summary>The line walked up a ramp as it rises - its foot, the ends of its flights and landings, and the turns between - what IFC has as its axis.</summary>
    public static IReadOnlyList<Point3D> Axis(RampLayout layout)
    {
        var axis = new List<Point3D>();
        if (layout.Pieces.Count == 0) return axis;
        axis.Add(Point3D.On(layout.Pieces[0].Start, layout.Pieces[0].Low));
        foreach (var piece in layout.Pieces)
        {
            if (piece.Kind == RampPieceKind.Landing)
                axis.AddRange(layout.Walk.Where(turn => Polygon2D.Contains(piece.Outline, turn) && turn.DistanceTo(piece.Start) > 1 && turn.DistanceTo(piece.End) > 1)
                    .Select(turn => Point3D.On(turn, piece.Low)));
            axis.Add(Point3D.On(piece.End, piece.High));
        }

        return axis;
    }

    /// <summary>
    /// A handrail's centre line along a side, with the surface's height along it: its inner face
    /// at the ramp's clear width on an open side, the hand's gap off a wall along a wall - set
    /// out length by length, mitred where two lengths set out alike meet, stepped where they differ.
    /// </summary>
    private static List<RampEdgePoint> Handrail(IReadOnlyList<RampEdgePoint> side, IReadOnlyList<double?> faces, double outward, double radius) =>
        SetOut(side, i => faces[i] is { } face ? face - WallGap - radius : radius, outward);

    /// <summary>A line along a side, set out from it length by length - each its own distance, out to its left going along it for a positive outward - mitred where two lengths set out alike meet, stepped where they differ.</summary>
    private static List<RampEdgePoint> SetOut(IReadOnlyList<RampEdgePoint> side, Func<int, double> by, double outward)
    {
        var line = new List<RampEdgePoint>();
        (Point2D From, Point2D To, double By)? before = null;
        for (var i = 0; i + 1 < side.Count; i++)
        {
            var (a, b) = (side[i], side[i + 1]);
            var length = a.At.DistanceTo(b.At);
            if (length < 1) continue;
            var normal = (b.At - a.At).PerpendicularLeft() / length * outward;
            var distance = by(i);
            var (from, to) = (a.At + normal * distance, b.At + normal * distance);
            if (before is not { } last) line.Add(new RampEdgePoint(from, a.Height));
            else if (Math.Abs(distance - last.By) > 0.5)
            {
                line.Add(new RampEdgePoint(last.To, a.Height));
                line.Add(new RampEdgePoint(from, a.Height));
            }
            else line.Add(new RampEdgePoint(Crossing(last.From, last.To, from, to) ?? from, a.Height));
            before = (from, to, distance);
        }

        if (before is { } end) line.Add(new RampEdgePoint(end.To, side[^1].Height));
        return line;
    }

    /// <summary>Where the lines through two lengths cross - none where they run the same way.</summary>
    private static Point2D? Crossing(Point2D a, Point2D b, Point2D c, Point2D d)
    {
        var (first, second) = (b - a, d - c);
        var across = first.Cross(second);
        if (Math.Abs(across) < 1e-6 * Math.Max(1, first.Length * second.Length)) return null;
        return a + first * ((c - a).Cross(second) / across);
    }

    /// <summary>The floor or the ground under a point of a ramp, below a height - or, with neither, its foot's height.</summary>
    private static double Beneath(BimDocument document, RampLayout layout, Point2D at, double below)
    {
        var under = new List<double>();
        if (Terrains.GroundAt(document, at) is { } ground && ground < below) under.Add(ground);
        under.AddRange(document.Elements.OfType<Slab>()
            .Where(slab => slab.Boundary.Count >= 3 && slab.Contains(at) && FloorSurfaces.IsStoodOn(document, slab))
            .Select(slab => FloorSurfaces.TopAt(document, slab, at)).Where(top => top < below));
        return under.Count > 0 ? under.Max() : Math.Min(below, Ramps.GroundUnder(document, at));
    }

    /// <summary>A side's points set out sideways - out to its left going along it for a positive distance - mitred at its corners.</summary>
    private static IReadOnlyList<Point2D> Outside(IReadOnlyList<RampEdgePoint> side, double by) =>
        side.Count < 2 ? side.Select(point => point.At).ToList() : Railings.SetIn(side.Select(point => point.At).ToList(), false, by);

    /// <summary>The surface's height at a point along a length of a side, as far along it as the point is.</summary>
    private static double Along(RampEdgePoint a, RampEdgePoint b, Point2D at)
    {
        var way = b.At - a.At;
        var length = way.Dot(way);
        return length <= 1e-9 ? a.Height : a.Height + (b.Height - a.Height) * Math.Clamp((at - a.At).Dot(way) / length, 0, 1);
    }

    /// <summary>Where posts stand along a line: at each end and corner, and between as often as the spacing asks - with the surface's height there.</summary>
    private static IEnumerable<(Point2D At, double Height)> Posts(IReadOnlyList<RampEdgePoint> line, double spacing)
    {
        for (var i = 0; i + 1 < line.Count; i++)
        {
            var (a, b) = (line[i], line[i + 1]);
            var length = a.At.DistanceTo(b.At);
            if (i == 0) yield return (a.At, a.Height);
            if (length < 1) continue;
            var count = Math.Max(1, (int)Math.Ceiling(length / Math.Max(300, spacing) - 1e-9));
            for (var k = 1; k <= count; k++)
            {
                var t = (double)k / count;
                yield return (a.At + (b.At - a.At) * t, a.Height + (b.Height - a.Height) * t);
            }
        }
    }

    private static void Post(Mesh3D mesh, Point2D at, double bottom, double top, double size = PostSize)
    {
        var half = size / 2;
        mesh.AddExtrusion(new[]
        {
            new Point2D(at.X - half, at.Y - half), new Point2D(at.X + half, at.Y - half),
            new Point2D(at.X + half, at.Y + half), new Point2D(at.X - half, at.Y + half)
        }, bottom, top);
    }
}
