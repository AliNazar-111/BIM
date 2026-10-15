using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// A railing in 3D: its posts at its ends, corners and between; its top rail on them, round or
/// a flat capping; and under it its balusters on a bottom rail, its glass, panels, mesh, rails
/// or cables along - or frameless glass standing in a channel, with no posts - each bay
/// standing on what is under it, so it follows a terrace's falls. Intermediate rails across it,
/// and a handrail along either side of it on brackets, run on past its ends or turned into the
/// wall or down to the floor there.
/// </summary>
public static class RailingModel
{
    private static readonly ColourRgb GlassTint = new(0xBF, 0xDC, 0xE6);

    /// <summary>An intermediate rail's section, a handrail bracket's, and how much wider than its glass a frameless channel is either side, mm.</summary>
    private const double RailSize = 30, BracketSize = 14, ChannelSide = 20;

    /// <summary>The furthest apart a frameless railing's handrail brackets are, mm.</summary>
    private const double BracketSpacing = 900;

    public static IReadOnlyList<Mesh3D> Meshes(BimDocument document, Railing railing)
    {
        var type = Railings.TypeOf(document, railing);
        if (Railings.Length(railing) < 1) return Array.Empty<Mesh3D>();

        var made = document.FindMaterial(type.MaterialId);
        var filled = document.FindMaterial(type.InfillMaterialId);
        var frame = new Mesh3D(railing.Id, railing.LevelId, MeshKind.Railing, made?.SurfaceColour ?? type.Colour, made?.Name ?? type.Name);
        var infill = new Mesh3D(railing.Id, railing.LevelId, MeshKind.Railing, filled?.SurfaceColour ?? made?.SurfaceColour ?? type.Colour, filled?.Name ?? made?.Name ?? type.Name);
        var glass = new Mesh3D(railing.Id, railing.LevelId, MeshKind.Railing, GlassTint, type.GlassLaminated ? "Glass, Toughened Laminated" : "Glass, Toughened") { Opacity = 0.35 };
        var mesh = new Mesh3D(railing.Id, railing.LevelId, MeshKind.Railing, filled?.SurfaceColour ?? made?.SurfaceColour ?? type.Colour, filled?.Name ?? "Mesh") { Opacity = 0.55 };

        var height = type.Height;
        var depth = Railings.RailDepth(type);
        var halfPost = type.PostSize / 2;
        var frameless = type.Infill == RailingInfill.FramelessGlass;

        foreach (var post in Railings.Posts(document, railing, type))
        {
            var foot = Railings.Base(document, railing, post);
            frame.AddExtrusion(Square(post, halfPost), foot, foot + height - depth);
        }

        foreach (var (from, to) in Railings.Bays(document, railing, type))
        {
            var (atFrom, atTo) = (Railings.Base(document, railing, from), Railings.Base(document, railing, to));
            var along = (to - from).NormalisedOrDefault(Vector2D.UnitX);
            var across = along.PerpendicularLeft();
            var span = (to - from).Length;
            double Foot(Point2D point) => atFrom + (atTo - atFrom) * Math.Clamp((point - from).Dot(along) / span, 0, 1);

            // Its top rail, post to post - or along the top of the glass, where frameless glass has one.
            if (depth > 0)
            {
                if (type.TopRailProfile == HandrailProfile.Round)
                {
                    var radius = type.TopRailWidth / 2;
                    frame.AddTube(new[] { At(from, atFrom + height - radius), At(to, atTo + height - radius) }, radius);
                }
                else frame.AddExtrusion(Strip(from, to, across, type.TopRailWidth / 2), point => Foot(point) + height - depth, point => Foot(point) + height);
            }

            // Frameless glass: its channel along the floor, and a pane in it with a joint either end.
            if (frameless)
            {
                if (type.ChannelHeight > 0)
                    frame.AddExtrusion(Strip(from, to, across, type.GlassThickness / 2 + ChannelSide), Foot, point => Foot(point) + type.ChannelHeight);
                var (first, last) = (from + along * (type.GlassJoint / 2), to - along * (type.GlassJoint / 2));
                if ((last - first).Length > 1)
                    glass.AddExtrusion(Strip(first, last, across, type.GlassThickness / 2), point => Foot(point) + Math.Min(10, type.ChannelHeight), point => Foot(point) + height - depth);
                continue;
            }

            // Between its posts' faces.
            var (start, end) = (from + along * halfPost, to - along * halfPost);
            var clear = (end - start).Length;
            if (clear < 1) continue;
            var rail = type.BottomGap + Railings.BottomRail;

            switch (type.Infill)
            {
                case RailingInfill.Glass:
                    glass.AddExtrusion(Strip(start + along * 10, end - along * 10, across, type.GlassThickness / 2),
                        point => Foot(point) + type.BottomGap, point => Foot(point) + height - depth);
                    break;

                case RailingInfill.Balusters:
                {
                    infill.AddExtrusion(Strip(start, end, across, Math.Max(type.BalusterSize, 20) / 2), point => Foot(point) + type.BottomGap, point => Foot(point) + rail);

                    // As few as leave every gap - between two, and from a post to the first - no wider than between them at their spacing.
                    var (size, spacing) = (type.BalusterSize, Math.Max(20, type.BalusterSpacing));
                    var count = Math.Max(0, (int)Math.Ceiling((clear + size) / spacing - 1 - 1e-9));
                    var gap = (clear - count * size) / (count + 1);
                    for (var k = 1; k <= count; k++)
                    {
                        var at = start + along * (k * gap + (k - 0.5) * size);
                        infill.AddExtrusion(Square(at, size / 2), Foot(at) + rail, Foot(at) + height - depth);
                    }

                    break;
                }

                case RailingInfill.HorizontalBars:
                case RailingInfill.Cables:
                {
                    infill.AddExtrusion(Strip(start, end, across, Railings.BarSize / 2), point => Foot(point) + type.BottomGap, point => Foot(point) + rail);
                    var (gap, size) = (Railings.BarGap(type), Railings.BarSizeOf(type));
                    for (var k = 1; k <= Railings.Bars(type); k++)
                    {
                        var low = rail + k * gap + (k - 1) * size;
                        if (type.Infill == RailingInfill.Cables)
                            infill.AddTube(new[] { At(start, Foot(start) + low + size / 2), At(end, Foot(end) + low + size / 2) }, size / 2);
                        else infill.AddExtrusion(Strip(start, end, across, size / 2), point => Foot(point) + low, point => Foot(point) + low + size);
                    }

                    break;
                }

                case RailingInfill.Panels:
                case RailingInfill.Mesh:
                    (type.Infill == RailingInfill.Panels ? infill : mesh).AddExtrusion(Strip(start, end, across, type.PanelThickness / 2),
                        point => Foot(point) + type.BottomGap, point => Foot(point) + height - depth);
                    break;
            }

            // Rails across it, evenly between its bottom rail and its top.
            for (var k = 1; k <= type.IntermediateRails; k++)
            {
                var low = rail + (height - depth - rail) * k / (type.IntermediateRails + 1) - RailSize / 2;
                frame.AddExtrusion(Strip(start, end, across, RailSize / 2), point => Foot(point) + low, point => Foot(point) + low + RailSize);
            }
        }

        AddHandrails(document, railing, type, frame);
        return new[] { frame, infill, glass, mesh }.Where(part => !part.IsEmpty).ToList();
    }

    /// <summary>
    /// The lengths a railing is built along joined end to end into runs - one all the way round,
    /// closed, where nothing breaks it - each with whether it is closed.
    /// </summary>
    public static IReadOnlyList<(IReadOnlyList<Point2D> Points, bool Closed)> Runs(BimDocument document, Railing railing)
    {
        var runs = new List<(IReadOnlyList<Point2D>, bool)>();
        var current = new List<Point2D>();
        foreach (var (from, to) in Railings.Segments(document, railing))
        {
            if (current.Count > 0 && current[^1].DistanceTo(from) > 1)
            {
                runs.Add((current, false));
                current = new List<Point2D>();
            }

            if (current.Count == 0) current.Add(from);
            current.Add(to);
        }

        if (current.Count >= 2) runs.Add((current, false));

        // Round to where it began: one closed run.
        if (runs.Count == 1 && railing.Closed && runs[0].Item1.Count >= 4 && runs[0].Item1[0].DistanceTo(runs[0].Item1[^1]) < 1)
            runs[0] = (runs[0].Item1.Take(runs[0].Item1.Count - 1).ToList(), true);

        // A railing that was closed but is broken by a stair: its two ends at the break joined into one run.
        if (railing.Closed && runs.Count >= 2 && runs[^1].Item1[^1].DistanceTo(runs[0].Item1[0]) < 1)
        {
            var joined = runs[^1].Item1.Concat(runs[0].Item1.Skip(1)).ToList();
            runs[0] = (joined, false);
            runs.RemoveAt(runs.Count - 1);
        }

        return runs;
    }

    /// <summary>
    /// Its handrails: along the side - or sides - its type says, clear of its posts or its glass by
    /// its clearance, at its height over what it stands on; on brackets from each post, or along
    /// the glass; and at each open end run on, turned into the wall there, down to the floor, or
    /// stopped as its type says.
    /// </summary>
    private static void AddHandrails(BimDocument document, Railing railing, RailingType type, Mesh3D frame)
    {
        if (type.Handrail == RailingHandrail.None) return;

        var radius = type.HandrailDiameter / 2;
        var face = type.Infill == RailingInfill.FramelessGlass ? type.GlassThickness / 2 : Math.Max(type.PostSize / 2, type.PanelThickness / 2);
        var off = face + type.HandrailClearance + radius;
        var sides = type.Handrail switch { RailingHandrail.Left => new[] { 1.0 }, RailingHandrail.Right => new[] { -1.0 }, _ => new[] { 1.0, -1.0 } };
        var posts = Railings.Posts(document, railing, type);
        var walls = StairModel.WallBands(document);

        foreach (var (points, closed) in Runs(document, railing))
        foreach (var side in sides)
        {
            var line = Railings.SetIn(points, closed, side * off).ToList();
            double Top(Point2D at) => Railings.Base(document, railing, at) + type.HandrailHeight - radius;
            var rail = line.Select(point => At(point, Top(point))).ToList();

            // Its ends: run on level, turned into the wall ahead or down to the floor - or stopped, on its end post or cut off.
            if (!closed && line.Count >= 2 && type.HandrailEnds is HandrailEnds.ReturnToWall or HandrailEnds.ReturnToFloor)
            {
                IEnumerable<Point3D> End(Point2D at, Point2D from)
                {
                    var way = (at - from).NormalisedOrDefault(Vector2D.UnitX);
                    var z = Top(at);
                    if (type.HandrailEnds == HandrailEnds.ReturnToWall)
                    {
                        for (var ahead = 0.0; ahead <= type.HandrailExtension + 300; ahead += 10)
                            if (walls.Any(wall => wall.Holds(at + way * (ahead + radius)) && wall.Base < z && wall.Top > z))
                                return new[] { At(at + way * ahead, z) };
                    }

                    var past = at + way * type.HandrailExtension;
                    return type.HandrailEnds == HandrailEnds.ReturnToFloor
                        ? new[] { At(past, z), At(past, Railings.Base(document, railing, at) + 100) }
                        : new[] { At(past, z) };
                }

                rail.InsertRange(0, End(line[0], line[1]).Reverse());
                rail.AddRange(End(line[^1], line[^2]));
            }

            if (closed) rail.Add(rail[0]);
            frame.AddTube(rail, radius);

            // Its brackets: from each post - or, along frameless glass, at most so far apart.
            var fixings = type.Infill != RailingInfill.FramelessGlass
                ? posts.Where(post => points.Zip(points.Skip(1)).Any(piece => Line2D.DistanceFromSegment(post, piece.First, piece.Second) < 1)).ToList()
                : Along(points, closed, BracketSpacing);
            // Each bracket square across from its fixing to the nearest point of the rail, at the rail's height there.
            var pieces = line.Zip(line.Skip(1)).ToList();
            if (closed && line.Count >= 3) pieces.Add((line[^1], line[0]));
            foreach (var at in fixings)
            {
                var on = pieces.Select(piece => Line2D.ClosestPointOnSegment(at, piece.First, piece.Second)).MinBy(point => point.DistanceTo(at));
                var outward = (on - at).NormalisedOrDefault(Vector2D.UnitX);
                frame.AddTube(new[] { At(at + outward * face, Top(on)), At(on, Top(on)) }, BracketSize / 2);
            }
        }
    }

    /// <summary>Points along a run, at each end and evenly between at most so far apart.</summary>
    private static IReadOnlyList<Point2D> Along(IReadOnlyList<Point2D> points, bool closed, double spacing)
    {
        var at = new List<Point2D>();
        var pieces = points.Zip(points.Skip(1)).ToList();
        if (closed) pieces.Add((points[^1], points[0]));
        foreach (var (from, to) in pieces)
        {
            var count = Math.Max(1, (int)Math.Ceiling((to - from).Length / spacing - 1e-9));
            for (var k = 0; k <= count; k++)
            {
                var point = from + (to - from) * ((double)k / count);
                if (!at.Any(other => other.DistanceTo(point) < 1)) at.Add(point);
            }
        }

        return at;
    }

    private static Point3D At(Point2D point, double z) => new(point.X, point.Y, z);

    private static IReadOnlyList<Point2D> Square(Point2D centre, double half) => new[]
    {
        new Point2D(centre.X - half, centre.Y - half), new Point2D(centre.X + half, centre.Y - half),
        new Point2D(centre.X + half, centre.Y + half), new Point2D(centre.X - half, centre.Y + half)
    };

    private static IReadOnlyList<Point2D> Strip(Point2D from, Point2D to, Vector2D across, double half) => new[]
    {
        from - across * half, to - across * half, to + across * half, from + across * half
    };
}
