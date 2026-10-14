using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// One run of handrail along a side of a stair: its points with the pitch line's height there,
/// whether it is fixed to a wall - a rail on brackets - or a balustrade on an open side, with
/// newels and balusters under it, and which way the stair is from it: on its right going along
/// it, as it is of a left side going up.
/// </summary>
public sealed record StairRailing(IReadOnlyList<StairSidePoint> Points, bool OnWall, bool LeftSide);

/// <summary>What a piece of a stair is: a flight of treads and winders, or a landing.</summary>
public enum StairPieceKind
{
    Flight,
    Landing
}

/// <summary>
/// A piece of a stair as IFC has it: a flight - its treads and winders one after another, with
/// their risers - or a landing; its steps, and its solids.
/// </summary>
public sealed record StairPiece(StairPieceKind Kind, List<StairStep> Steps, Mesh3D Body, Mesh3D Painted, Mesh3D Steel)
{
    public IReadOnlyList<Mesh3D> Meshes => new[] { Body, Painted, Steel }.Where(mesh => !mesh.IsEmpty).ToList();
}

/// <summary>
/// A stair's solids in its parts: its flights and landings; the strings or spine that carry
/// them; its handrails on walls; its balustrades up its open sides and round its landings; and
/// the guarding round its stairwell on the floor above.
/// </summary>
public sealed record StairParts(
    IReadOnlyList<StairPiece> Pieces, IReadOnlyList<Mesh3D> Stringers,
    IReadOnlyList<Mesh3D> Handrails, IReadOnlyList<Mesh3D> Balustrades, IReadOnlyList<Mesh3D> Guarding)
{
    /// <summary>The stair itself: its flights, landings and stringers.</summary>
    public IReadOnlyList<Mesh3D> Stair => Pieces.SelectMany(piece => piece.Meshes).Concat(Stringers).ToList();

    public IReadOnlyList<Mesh3D> Railings => Handrails.Concat(Balustrades).Concat(Guarding).ToList();
}

/// <summary>
/// A stair as it is built, for the 3D view, sections and IFC: its treads - housed in strings,
/// sat on a cut string, on a steel spine or steel stringers, or built into the wall, with risers
/// between unless they are open (barred, if its type says) - or cast in concrete down to a
/// soffit; its landings; the newels its winding corners turn on, or a spiral's column; and its
/// handrails - a balustrade with newels and balusters, or glass, along an open side, a rail on a
/// wall - as its handrail setting and the walls beside it say.
/// </summary>
public static class StairModel
{
    private static readonly ColourRgb Paint = new(0xF2, 0xF2, 0xEE);
    private static readonly ColourRgb Steel = new(0xBF, 0xC4, 0xC9);
    private static readonly ColourRgb DarkSteel = new(0x3C, 0x3F, 0x44);
    private static readonly ColourRgb GlassTint = new(0xBF, 0xDC, 0xE6);

    private const double StringThickness = 32;
    private const double RiserThickness = 18;
    private const double LandingDepth = 200;

    private const double NewelSize = 90;
    private const double SteelStringerThickness = 15;
    private const double SpineWidth = 160;
    private const double SpineDepth = 200;
    private const double GlassThickness = 12;

    /// <summary>A baluster's size, square, mm.</summary>
    public const double BalusterSize = 32;

    /// <summary>A bar across an open riser, square, mm.</summary>
    public const double RiserBarSize = 25;

    /// <summary>How far a string stands over the pitch line - where balusters on it stand: a timber string's 75, a steel plate's 40.</summary>
    private static double StringTop(StairType type) => type.Support == StairSupport.SteelStringers ? 40 : 75;

    /// <summary>Whether a stair's balusters stand on a string up its side, rather than on its treads.</summary>
    private static bool OnString(StairType type, StairLayout layout) =>
        type.Construction == StairConstruction.Timber && layout.Shape != StairShape.Spiral
        && type.Support is StairSupport.ClosedStrings or StairSupport.SteelStringers;

    /// <summary>
    /// The runs of handrail a stair has. Open sides are guarded with a balustrade; a side against
    /// a wall has a rail on the wall where a handrail is wanted there - both sides of a stair
    /// 1000 wide or more, as Approved Document K asks, or the left of one between two walls.
    /// Said otherwise, the sides it is told. A spiral stair's column is its handhold on the inside.
    /// </summary>
    public static IReadOnlyList<StairRailing> Railings(BimDocument document, Stair stair, StairLayout layout)
    {
        if (stair.Railings == StairRailings.None) return Array.Empty<StairRailing>();
        var type = Stairs.TypeOf(document, stair);

        var sides = layout.Sides.Where(side => !side.AgainstPole)
            .Select(side => (Side: side, Segments: Segments(document, stair, layout, side.Points, side.StairOnRight)))
            .ToList();
        var anyOpen = sides.Any(side => side.Segments.Any(segment => !segment.OnWall));
        var both = stair.Railings == StairRailings.BothSides || layout.WidestFlight >= 1000;

        var railings = new List<StairRailing>();
        foreach (var (side, segments) in sides)
        {
            var wanted = side.Role switch
            {
                StairSideRole.Left => stair.Railings != StairRailings.RightSide,
                StairSideRole.Right => stair.Railings != StairRailings.LeftSide,
                _ => true
            };
            if (!wanted) continue;

            // On the wall too where it is wanted there: told to, both sides wanted, or the only handrail it would have.
            // The back of a split stair's landing is only guarded where it is open, unless both sides are wanted.
            var left = side.StairOnRight;
            var onWall = side.Role == StairSideRole.Other
                ? both
                : stair.Railings != StairRailings.Auto || both || (!anyOpen && side.Role == StairSideRole.Left);

            var run = new List<StairSidePoint>();
            bool? runOnWall = null;
            foreach (var (from, to, wall) in segments)
            {
                var keep = !wall || onWall;
                if (!keep || (runOnWall is { } kind && kind != wall))
                {
                    if (run.Count >= 2) railings.Add(Finished(run, runOnWall ?? false, left, side.Points, type));
                    run = new List<StairSidePoint>();
                    runOnWall = null;
                }

                if (!keep) continue;
                if (run.Count == 0) run.Add(from);
                run.Add(to);
                runOnWall = wall;
            }

            if (run.Count >= 2) railings.Add(Finished(run, runOnWall ?? false, left, side.Points, type));
        }

        return railings;
    }

    /// <summary>
    /// A run of handrail as it is put up: on a wall, running on level past the stair's foot and
    /// its head as far as its type says - where the run reaches them.
    /// </summary>
    private static StairRailing Finished(List<StairSidePoint> run, bool onWall, bool left, IReadOnlyList<StairSidePoint> side, StairType type)
    {
        var extension = type.HandrailExtension;
        if (onWall && extension > 0)
        {
            var first = run.Zip(run.Skip(1)).Select(pair => pair.Second.At - pair.First.At).FirstOrDefault(way => way.Length >= 1);
            var last = run.Zip(run.Skip(1)).Select(pair => pair.Second.At - pair.First.At).LastOrDefault(way => way.Length >= 1);
            if (first.Length >= 1 && run[0].At.DistanceTo(side[0].At) < 1)
                run.Insert(0, new StairSidePoint(run[0].At - first.NormalisedOrDefault(Vector2D.UnitX) * extension, run[0].Height));
            if (last.Length >= 1 && run[^1].At.DistanceTo(side[^1].At) < 1)
                run.Add(new StairSidePoint(run[^1].At + last.NormalisedOrDefault(Vector2D.UnitX) * extension, run[^1].Height));
        }

        return new StairRailing(run, onWall, left);
    }

    /// <summary>A side's lengths, each with whether there is a wall right against it - a step up in place riding with the length before it.</summary>
    private static List<(StairSidePoint From, StairSidePoint To, bool OnWall)> Segments(
        BimDocument document, Stair stair, StairLayout layout, IReadOnlyList<StairSidePoint> points, bool left)
    {
        var walls = WallBands(document);

        var segments = new List<(StairSidePoint, StairSidePoint, bool)>();
        for (var i = 0; i + 1 < points.Count; i++)
        {
            var (a, b) = (points[i], points[i + 1]);
            var along = b.At - a.At;
            if (along.Length < 1)
            {
                // A step up in place: part of the run it is in, a newel standing at it.
                segments.Add((a, b, segments.Count > 0 && segments[^1].Item3));
                continue;
            }

            // A probe just outside the side, at its middle and its ends: against a wall if most are in one.
            var outward = Inward(along, left) * -1;
            var height = (a.Height + b.Height) / 2;
            var probes = new[] { 0.15, 0.5, 0.85 }.Select(t => a.At + along * t + outward * 60).ToList();
            var walled = probes.Count(probe => walls.Any(wall => wall.Base <= height && wall.Top >= height + 300 && wall.Holds(probe))) >= 2;
            segments.Add((a, b, walled));
        }

        return segments;
    }

    /// <summary>
    /// A wall as the stair looks for it beside its sides: its footprint, with a box round it to
    /// rule it out quickly, and how high it stands - worked out once, not for every probe.
    /// </summary>
    internal sealed record WallBand(IReadOnlyList<Point2D> Band, double Base, double Top, Point2D Min, Point2D Max, Wall Wall)
    {
        public bool Holds(Point2D point) =>
            point.X >= Min.X && point.X <= Max.X && point.Y >= Min.Y && point.Y <= Max.Y && Polygon2D.Contains(Band, point);
    }

    internal static List<WallBand> WallBands(BimDocument document) =>
        document.Walls
            .Where(wall => document.GetWallType(wall) is { } type && type.Structure.TotalWidth > 0 && wall.Length > 1)
            .Select(wall =>
            {
                var type = document.GetWallType(wall)!;
                var band = CurtainGeometry.Band(wall, type, 0, wall.Length, type.Structure.TotalWidth / 2, -type.Structure.TotalWidth / 2);
                return new WallBand(band, wall.GetBaseElevation(document), wall.GetTopElevation(document),
                    new Point2D(band.Min(point => point.X), band.Min(point => point.Y)), new Point2D(band.Max(point => point.X), band.Max(point => point.Y)), wall);
            })
            .Where(wall => wall.Band.Count >= 3)
            .ToList();

    /// <summary>
    /// The walls a stair runs through, each with the lowest step it crosses: standing across a
    /// step from below its tread to higher than it - not one it is set against, beside it; nor
    /// one under it, stopping below its treads; nor one standing on the floor it comes up to.
    /// </summary>
    public static IReadOnlyList<(Wall Wall, int Step)> WallsThrough(BimDocument document, StairLayout layout)
    {
        var through = new List<(Wall Wall, int Step)>();
        foreach (var band in WallBands(document))
        {
            foreach (var step in layout.Steps.OrderBy(step => step.Number))
            {
                if (band.Base >= step.Top - 1 || band.Top <= step.Top + 100) continue;
                var (min, max) = (new Point2D(step.Outline.Min(point => point.X), step.Outline.Min(point => point.Y)), new Point2D(step.Outline.Max(point => point.X), step.Outline.Max(point => point.Y)));
                if (min.X > band.Max.X || max.X < band.Min.X || min.Y > band.Max.Y || max.Y < band.Min.Y) continue;
                if (PolygonBoolean.Combine(band.Band, step.Outline, BooleanOperation.Intersection).Sum(region => region.Area) < 5000) continue;
                through.Add((band.Wall, step.Number));
                break;
            }
        }

        return through;
    }

    /// <summary>
    /// The open lengths of a stair's sides it is told to leave without a railing - beside no
    /// wall - each said by its side, with how high it is at its highest.
    /// </summary>
    public static IReadOnlyList<(StairSideRole Role, double Highest)> Unguarded(BimDocument document, Stair stair, StairLayout layout)
    {
        var open = new List<(StairSideRole, double)>();
        if (stair.Railings is StairRailings.Auto or StairRailings.BothSides) return open;

        foreach (var side in layout.Sides.Where(side => !side.AgainstPole))
        {
            var railed = side.Role switch
            {
                StairSideRole.Left => stair.Railings == StairRailings.LeftSide,
                StairSideRole.Right => stair.Railings == StairRailings.RightSide,
                _ => stair.Railings != StairRailings.None
            };
            if (railed) continue;

            var lengths = Segments(document, stair, layout, side.Points, side.StairOnRight).Where(segment => !segment.OnWall).ToList();
            if (lengths.Count > 0) open.Add((side.Role, lengths.Max(segment => Math.Max(segment.From.Height, segment.To.Height))));
        }

        return open;
    }

    /// <summary>Whether a side of a stair runs along a wall for the most part: what cantilevered treads are built into.</summary>
    public static bool AgainstAWall(BimDocument document, Stair stair, StairLayout layout) =>
        layout.Sides.Where(side => !side.AgainstPole).Any(side =>
        {
            var segments = Segments(document, stair, layout, side.Points, side.StairOnRight);
            var total = segments.Sum(segment => (segment.To.At - segment.From.At).Length);
            return total > 1 && segments.Where(segment => segment.OnWall).Sum(segment => (segment.To.At - segment.From.At).Length) >= total / 2;
        });

    /// <summary>Which way the stair is from a length of its side: right of it for the left side, left of it for the right.</summary>
    public static Vector2D Inward(Vector2D along, bool left)
    {
        var normal = along.NormalisedOrDefault(Vector2D.UnitX).PerpendicularLeft();
        return left ? normal * -1 : normal;
    }

    /// <summary>
    /// A strip along one length of a side, from so far in from it to so far - a string, a
    /// handrail - mitred with the lengths either side where the side bends gently, round a
    /// curve, so the strips along it run on without notches.
    /// </summary>
    private static IReadOnlyList<Point2D> Strip(IReadOnlyList<StairSidePoint> points, int i, bool left, double near, double far)
    {
        var (from, to) = (points[i].At, points[i + 1].At);
        var own = Inward(to - from, left);
        var start = Mitre(own, i > 0 ? points[i].At - points[i - 1].At : null, left);
        var end = Mitre(own, i + 2 < points.Count ? points[i + 2].At - points[i + 1].At : null, left);
        return new[] { from + start * near, to + end * near, to + end * far, from + start * far };
    }

    /// <summary>The way in at a joint between two lengths, keeping each strip's width: theirs and the next's halfway, where they turn by less than 30°.</summary>
    private static Vector2D Mitre(Vector2D own, Vector2D? next, bool left)
    {
        if (next is not { } other || other.Length < 1) return own;
        var theirs = Inward(other, left);
        var cos = own.Dot(theirs);
        return cos < Math.Cos(Math.PI / 6) ? own : (own + theirs) * (1 / (1 + cos));
    }

    /// <summary>A closed outline with its longer edges cut into lengths no longer than so long.</summary>
    private static IReadOnlyList<Point2D> Pieces(IReadOnlyList<Point2D> loop, double longest)
    {
        var points = new List<Point2D>();
        for (var i = 0; i < loop.Count; i++)
        {
            var (a, b) = (loop[i], loop[(i + 1) % loop.Count]);
            var count = Math.Max(1, (int)Math.Ceiling((b - a).Length / longest));
            for (var k = 0; k < count; k++) points.Add(a + (b - a) * ((double)k / count));
        }

        return points;
    }

    /// <summary>Whether a point is in or right against an outline - a newel, a spiral's column.</summary>
    private static bool Against(IReadOnlyList<Point2D> outline, Point2D point, double within) =>
        Polygon2D.Contains(outline, point)
        || Enumerable.Range(0, outline.Count).Any(i => Line2D.DistanceFromSegment(point, outline[i], outline[(i + 1) % outline.Count]) < within);

    /// <summary>The pitch line's height at a point along a length of side, between the heights at its ends.</summary>
    private static Func<Point2D, double> Along(StairSidePoint a, StairSidePoint b)
    {
        var along = b.At - a.At;
        var length = Math.Max(1e-9, along.Length);
        var direction = along * (1 / length);
        return point => a.Height + (b.Height - a.Height) * Math.Clamp((point - a.At).Dot(direction) / length, 0, 1);
    }

    public static IReadOnlyList<Mesh3D> Meshes(BimDocument document, Stair stair)
    {
        var (steps, railings) = Build(document, stair);
        return steps.Concat(railings).ToList();
    }

    /// <summary>
    /// A stair's solids, as the stair itself and as its handrails and balustrades - which IFC has
    /// as railings of their own: storey by storey, for one repeated up a stairwell, each on the
    /// level it goes up from.
    /// </summary>
    public static (IReadOnlyList<Mesh3D> Stair, IReadOnlyList<Mesh3D> Railings) Build(BimDocument document, Stair stair)
    {
        var parts = Parts(document, stair);
        return (parts.Stair, parts.Railings);
    }

    /// <summary>
    /// A stair's solids in its parts: the stair itself; its handrails on walls; its balustrades
    /// up its open sides and round its landings, with their newels; and the guarding round its
    /// stairwell on the floor above - storey by storey.
    /// </summary>
    public static StairParts Parts(BimDocument document, Stair stair)
    {
        var (pieces, stringers) = (new List<StairPiece>(), new List<Mesh3D>());
        var (handrails, balustrades, guarding) = (new List<Mesh3D>(), new List<Mesh3D>(), new List<Mesh3D>());
        var flights = Stairs.Flights(document, stair);
        foreach (var flight in flights)
        {
            var goingOn = flights.FirstOrDefault(next => next.LevelId == flight.TopLevelId)?.Layout.Steps;
            var parts = BuildFlight(document, stair, flight.Layout, flight.LevelId, goingOn);
            pieces.AddRange(parts.Pieces);
            stringers.AddRange(parts.Stringers);
            handrails.AddRange(parts.Handrails);
            balustrades.AddRange(parts.Balustrades);
            guarding.AddRange(parts.Guarding);
        }

        return new StairParts(pieces, stringers, handrails, balustrades, guarding);
    }

    /// <summary>The line walked up a stair as it rises - its foot, through each riser at the height of the step above, to its head: what IFC has as its axis.</summary>
    public static IReadOnlyList<Point3D> Axis(StairLayout layout) =>
        layout.Walks.Count == 0 ? Array.Empty<Point3D>() : WalkPitch(layout, 0).Select(point => new Point3D(point.At.X, point.At.Y, point.Height)).ToList();

    /// <summary>A railing's solids as they are built up: its rails and newels, its balusters, its glass, and the brackets that fix it to a wall.</summary>
    private sealed record RailSet(Mesh3D Rails, Mesh3D Balusters, Mesh3D Glass, Mesh3D Fixings)
    {
        /// <summary>
        /// Those with anything in them - the balusters shaded but not outlined: so many, so thin,
        /// their edges would blacken them.
        /// </summary>
        public IReadOnlyList<Mesh3D> Meshes()
        {
            Balusters.DropEdges();
            return new[] { Rails, Balusters, Glass, Fixings }.Where(mesh => !mesh.IsEmpty).ToList();
        }
    }

    /// <summary>One storey of a stair, its solids on the level it goes up from.</summary>
    private static StairParts BuildFlight(BimDocument document, Stair stair, StairLayout layout, Guid levelId, IReadOnlyList<StairStep>? goingOn)
    {
        var type = Stairs.TypeOf(document, stair);
        var timber = type.Construction == StairConstruction.Timber;

        // Each part in its own material where its type gives it one: its treads, risers, strings, rails and balusters.
        var material = document.FindMaterial(type.MaterialId);
        Mesh3D Made(Guid materialId, ColourRgb colour, string name) =>
            document.FindMaterial(materialId) is { } made ? new(stair.Id, levelId, MeshKind.Stair, made.SurfaceColour, made.Name) : new(stair.Id, levelId, MeshKind.Stair, colour, name);
        Mesh3D Body() => new(stair.Id, levelId, MeshKind.Stair, material?.SurfaceColour ?? type.Colour, material?.Name ?? type.Name);
        Mesh3D Painted() => Made(type.RiserMaterialId, timber ? Paint : Steel, timber ? "Paint, White" : "Stainless Steel");
        Mesh3D Plate() => new(stair.Id, levelId, MeshKind.Stair, DarkSteel, "Steel, Painted Dark Grey");

        // What carries the treads: a concrete stair is cast as one, a spiral's treads are fixed to its column.
        var support = timber && layout.Shape != StairShape.Spiral ? type.Support : (StairSupport?)null;

        // Its steps in pieces: a flight, its treads and winders one after another, until a landing - a piece of its own.
        var pieces = new List<StairPiece>();
        StairPiece? piece = null;
        var tops = layout.Steps.GroupBy(step => step.Number).ToDictionary(group => group.Key, group => group.First().Top);
        foreach (var step in layout.Steps)
        {
            if (piece is null || step.Kind == StairStepKind.Landing || piece.Kind == StairPieceKind.Landing || step.Number != piece.Steps[^1].Number + 1)
            {
                piece = new StairPiece(step.Kind == StairStepKind.Landing ? StairPieceKind.Landing : StairPieceKind.Flight, new List<StairStep>(), Body(), Painted(), Plate());
                pieces.Add(piece);
            }

            piece.Steps.Add(step);
            var (body, painted, steel) = (piece.Body, piece.Painted, piece.Steel);
            if (step.Kind == StairStepKind.Landing)
            {
                body.AddExtrusion(step.Outline, step.Top - LandingDepth, step.Top);
            }
            else if (timber && type.NosingProfile != NosingProfile.Square && step.Kind == StairStepKind.Tread && step.Outline.Count == 4)
            {
                AddShapedTread(body, step, type);
            }
            else if (timber)
            {
                body.AddExtrusion(step.Outline, step.Top - type.TreadThickness, step.Top);
            }
            else
            {
                // Cast as one: each step down to a soffit parallel to the pitch, 150 under the steps' roots.
                body.AddExtrusion(step.Outline, point => Math.Max(layout.Bottom, layout.PitchOver(step, point) - layout.Rise - 150), _ => step.Top);
            }

            // The riser under its nosing, from the step below to the underside of its tread.
            if (timber && !type.OpenRisers)
            {
                var below = tops.TryGetValue(step.Number - 1, out var lower) ? lower : layout.Bottom;
                var board = new[] { step.RiserFrom, step.RiserTo, step.RiserTo + step.Up * RiserThickness, step.RiserFrom + step.Up * RiserThickness };
                painted.AddExtrusion(board, below, step.Top - (step.Kind == StairStepKind.Landing ? LandingDepth : type.TreadThickness));
            }
            else if (timber && type.RiserBars)
            {
                // An open riser barred halfway up, so no 100 mm sphere passes between the treads: a
                // bar from string to string, or with no strings to fix one to, an upstand hanging
                // from the tread's front as far down as leaves the same gap under it.
                var below = tops.TryGetValue(step.Number - 1, out var lower) ? lower : layout.Bottom;
                var under = step.Top - (step.Kind == StairStepKind.Landing ? LandingDepth : type.TreadThickness);
                if (support is StairSupport.ClosedStrings or StairSupport.CutStrings or StairSupport.SteelStringers)
                {
                    var middle = (below + under) / 2;
                    var bar = new[] { step.RiserFrom + step.Up * 10, step.RiserTo + step.Up * 10, step.RiserTo + step.Up * (10 + RiserBarSize), step.RiserFrom + step.Up * (10 + RiserBarSize) };
                    steel.AddExtrusion(bar, middle - RiserBarSize / 2, middle + RiserBarSize / 2);
                }
                else
                {
                    var upstand = new[] { step.RiserFrom, step.RiserTo, step.RiserTo + step.Up * RiserThickness, step.RiserFrom + step.Up * RiserThickness };
                    body.AddExtrusion(upstand, under - (under - below + RiserBarSize) / 2, under);
                }
            }
        }

        // The strings up each side, following the pitch line: housed timber strings, a cut string
        // on an open side, or steel plates. A spine up the middle instead, or nothing for treads
        // built into the wall.
        var (strings, plates) = (Made(type.StringMaterialId, material?.SurfaceColour ?? type.Colour, material?.Name ?? type.Name), Plate());
        if (support is StairSupport.ClosedStrings or StairSupport.CutStrings or StairSupport.SteelStringers)
            foreach (var side in layout.Sides)
            {
                var walls = support == StairSupport.CutStrings ? Segments(document, stair, layout, side.Points, side.StairOnRight) : null;
                for (var i = 0; i + 1 < side.Points.Count; i++)
                {
                    var (a, b) = (side.Points[i], side.Points[i + 1]);
                    if ((b.At - a.At).Length < 1) continue;
                    var height = Along(a, b);
                    if (support == StairSupport.SteelStringers)
                        plates.AddExtrusion(Strip(side.Points, i, side.StairOnRight, 0, SteelStringerThickness),
                            point => Math.Max(layout.Bottom, height(point) - 280), point => height(point) + StringTop(type));
                    else if (walls is not null && !walls[i].OnWall)
                        AddCutString(strings, layout, type, side.Points, i, side.StairOnRight);
                    else
                        strings.AddExtrusion(Strip(side.Points, i, side.StairOnRight, 0, StringThickness),
                            point => Math.Max(layout.Bottom, height(point) - 220), point => height(point) + StringTop(type));
                }
            }
        else if (support == StairSupport.MonoStringer)
            for (var w = 0; w < layout.Walks.Count; w++)
                AddSpine(plates, layout, type, w);

        // A timber stair's handrails and newels are its timber, its balusters painted; a concrete one's all steel.
        var (railColour, railName) = timber ? (material?.SurfaceColour ?? type.Colour, material?.Name ?? type.Name) : (Steel, "Stainless Steel");
        RailSet Set() => new(
            Made(type.RailMaterialId, railColour, railName),
            Made(type.BalusterMaterialId, timber ? Paint : Steel, timber ? "Paint, White" : "Stainless Steel"),
            new Mesh3D(stair.Id, levelId, MeshKind.Stair, GlassTint, "Glass, Toughened") { Opacity = 0.35 },
            new Mesh3D(stair.Id, levelId, MeshKind.Stair, Steel, "Stainless Steel"));
        var (handrails, balustrades, guards) = (Set(), Set(), Set());

        // The newels its winding corners turn on, from the floor to above the handrail; a spiral's column, up past the floor it reaches.
        foreach (var newel in layout.Newels)
            balustrades.Rails.AddExtrusion(newel.Outline, layout.Bottom, newel.Top);

        foreach (var railing in Railings(document, stair, layout))
            AddRailing(railing, type, layout, railing.OnWall ? handrails : balustrades);
        foreach (var railing in Guarding(document, stair, layout, goingOn))
            AddRailing(railing, type, layout, guards);

        return new StairParts(pieces, new[] { strings, plates }.Where(mesh => !mesh.IsEmpty).ToList(), handrails.Meshes(), balustrades.Meshes(), guards.Meshes());
    }

    /// <summary>
    /// A cut string along one length of an open side: its top cut to the steps - level under each
    /// tread, stepping up at each riser - so the treads sit on it with their ends showing, and
    /// its underside a rise and 150 under the pitch line.
    /// </summary>
    private static void AddCutString(Mesh3D body, StairLayout layout, StairType type, IReadOnlyList<StairSidePoint> points, int i, bool left)
    {
        var (a, b) = (points[i], points[i + 1]);
        var along = b.At - a.At;
        var length = along.Length;
        var direction = along * (1 / length);
        var height = Along(a, b);
        var inward = Inward(along, left);

        // Cut where each riser meets it.
        var cuts = layout.Steps.SelectMany(step => new[] { step.RiserFrom, step.RiserTo })
            .Where(point => Line2D.DistanceFromSegment(point, a.At, b.At) < 1)
            .Select(point => (point - a.At).Dot(direction) / length)
            .Where(t => t > 1e-4 && t < 1 - 1e-4)
            .Append(0.0).Append(1.0)
            .OrderBy(t => t).ToList();

        for (var k = 0; k + 1 < cuts.Count; k++)
        {
            var (t0, t1) = (cuts[k], cuts[k + 1]);
            if (t1 - t0 < 1e-4) continue;
            var (from, to) = (a.At + along * t0, a.At + along * t1);
            var inside = a.At + along * ((t0 + t1) / 2) + inward * (StringThickness + 10);
            // The step it carries: the highest under the pitch line there - not one a turn further up a stair that winds over itself.
            var step = layout.Steps.Where(candidate => candidate.Top <= height(inside) + 1 && Polygon2D.Contains(candidate.Outline, inside))
                .OrderByDescending(candidate => candidate.Top).FirstOrDefault();
            var under = step is null ? height(inside) - layout.Rise : step.Top - (step.Kind == StairStepKind.Landing ? LandingDepth : type.TreadThickness);
            body.AddExtrusion(new[] { from, to, to + inward * StringThickness, from + inward * StringThickness },
                point => Math.Min(under - 20, Math.Max(layout.Bottom, height(point) - layout.Rise - 150)), _ => under);
        }
    }

    /// <summary>
    /// A steel spine up the line walked, under the middle of its treads: 200 deep, its top a rise
    /// and a tread under the pitch line so every tread clears it, standing on the floor at its
    /// foot, with a bracket up from it to each tread over it.
    /// </summary>
    private static void AddSpine(Mesh3D steel, StairLayout layout, StairType type, int index)
    {
        var points = WalkPitch(layout, index);
        var drop = layout.Rise + type.TreadThickness;
        for (var i = 0; i + 1 < points.Count; i++)
        {
            var (a, b) = (points[i], points[i + 1]);
            if ((b.At - a.At).Length < 1) continue;
            var height = Along(a, b);
            steel.AddExtrusion(Strip(points, i, true, -SpineWidth / 2, SpineWidth / 2),
                point => Math.Max(layout.Bottom, height(point) - drop - SpineDepth), point => Math.Max(layout.Bottom + 50, height(point) - drop));
        }

        foreach (var step in layout.Steps.Where(step => step.Kind != StairStepKind.Landing))
        {
            var centre = Polygon2D.Centroid(step.Outline, step.RiserFrom);
            var nearest = Enumerable.Range(0, points.Count - 1)
                .Where(i => (points[i + 1].At - points[i].At).Length >= 1)
                .Select(i =>
                {
                    var (a, b) = (points[i], points[i + 1]);
                    var along = b.At - a.At;
                    var t = Math.Clamp((centre - a.At).Dot(along) / along.Dot(along), 0, 1);
                    var at = a.At + along * t;
                    return (At: at, Pitch: Along(a, b)(at), Off: (centre - at).Length);
                })
                .OrderBy(entry => entry.Off).FirstOrDefault();
            if (nearest.Off > layout.Width / 2) continue;

            var (foot, head) = (Math.Max(layout.Bottom + 50, nearest.Pitch - drop), step.Top - type.TreadThickness);
            if (head - foot > 1) steel.AddExtrusion(Square(nearest.At, 50), foot, head);
        }
    }

    /// <summary>
    /// A line walked up a stair, with the pitch line's height along it: over each riser it
    /// crosses, the height of the step above; in between, rising from one to the next.
    /// </summary>
    private static List<StairSidePoint> WalkPitch(StairLayout layout, int index)
    {
        var walk = layout.Walks[index];
        var lengths = new double[walk.Count];
        for (var i = 1; i < walk.Count; i++) lengths[i] = lengths[i - 1] + (walk[i] - walk[i - 1]).Length;

        // A split stair's second line starts on its landing.
        var start = index == 0
            ? layout.Bottom + layout.Rise
            : layout.Steps.Where(step => Polygon2D.Contains(step.Outline, walk[0])).Select(step => step.Top).DefaultIfEmpty(layout.Bottom).Max();
        var crossings = new List<(double At, double Height)> { (0, start), (lengths[^1], layout.Top) };
        foreach (var step in layout.Steps)
            for (var i = 0; i + 1 < walk.Count; i++)
            {
                var (d, e, f) = (walk[i + 1] - walk[i], step.RiserTo - step.RiserFrom, step.RiserFrom - walk[i]);
                var det = d.Cross(e);
                if (Math.Abs(det) < 1e-9) continue;
                var (t, u) = (f.Cross(e) / det, f.Cross(d) / det);
                if (t < -1e-9 || t > 1 + 1e-9 || u < -1e-3 || u > 1 + 1e-3) continue;
                crossings.Add((lengths[i] + t * (lengths[i + 1] - lengths[i]), step.Top));
            }

        crossings.Sort((x, y) => x.At.CompareTo(y.At));
        double HeightAt(double s)
        {
            var after = crossings.FindIndex(crossing => crossing.At >= s - 1e-9);
            if (after <= 0) return crossings[Math.Max(0, after)].Height;
            var (low, high) = (crossings[after - 1], crossings[after]);
            return high.At - low.At < 1e-9 ? high.Height : low.Height + (high.Height - low.Height) * (s - low.At) / (high.At - low.At);
        }

        return walk.Select((point, i) => new StairSidePoint(point, HeightAt(lengths[i]))).ToList();
    }

    /// <summary>
    /// The balustrade round the stairwell on the floor above: along every edge of the hole the
    /// stair cuts but those it comes up at, those against a wall and a spiral's column, and
    /// those with no floor beside them - where the hole runs out past the edge of a deck a stair
    /// outside comes up onto - so the floor's edge is guarded where it drops into the stairwell.
    /// None for a stair told it has no handrails.
    /// </summary>
    public static IReadOnlyList<StairRailing> Guarding(BimDocument document, Stair stair, StairLayout layout, IReadOnlyList<StairStep>? goingOn = null)
    {
        if (stair.Railings == StairRailings.None) return Array.Empty<StairRailing>();

        var walls = WallBands(document);

        var guarding = new List<StairRailing>();
        var seen = new HashSet<string>();
        foreach (var (floor, hole) in Stairs.Stairwells(document, stair, layout))
        {
            // Two floors laid one on the other at the same height have the one stairwell: guarded once.
            var key = $"{floor.GetTopElevation(document):0}:" + string.Join(";", hole.Select(point => $"{point.X:0},{point.Y:0}"));
            if (!seen.Add(key)) continue;

            // Anticlockwise, so the floor is on the right of each edge going round.
            var loop = Polygon2D.SignedArea(hole) >= 0 ? hole : hole.Reverse().ToList();

            // Where the stair goes on up from this floor, or the hole runs out past its edge, its edges in short lengths: guarded or not a length at a time.
            if (goingOn is not null || hole.Any(point => !Polygon2D.Contains(floor.Boundary, point))) loop = Pieces(loop, 200);
            var height = floor.GetTopElevation(document);

            bool Guarded(Point2D a, Point2D b)
            {
                var middle = new Point2D((a.X + b.X) / 2, (a.Y + b.Y) / 2);
                if (layout.Arrivals.Any(arrival => Line2D.DistanceFromSegment(middle, arrival.From, arrival.To) < 5)) return false;
                if (layout.Newels.Any(newel => Against(newel.Outline, middle, 5))) return false;

                // Where the same stair goes on up from this floor, its own steps are in the way - across its
                // foot, beside its lowest treads - as far up as a guard would reach; higher up it, the floor's edge is guarded under it.
                if (goingOn is not null && goingOn.Any(step => step.Top < height + 1100 && Against(step.Outline, middle, 30))) return false;
                var outward = Inward(b - a, true);
                var probe = middle + outward * 60;
                if (!floor.Contains(probe)) return false;
                return !walls.Any(wall => wall.Base <= height + 100 && wall.Top >= height + 900 && wall.Holds(probe));
            }

            // Runs of guarded edges, going round from just after an edge that is not.
            var start = Enumerable.Range(0, loop.Count).FirstOrDefault(i => !Guarded(loop[i], loop[(i + 1) % loop.Count]), -1);
            var run = new List<StairSidePoint>();
            for (var k = 0; k < loop.Count; k++)
            {
                var i = (start + 1 + k) % loop.Count;
                var (a, b) = (loop[i], loop[(i + 1) % loop.Count]);
                if ((b - a).Length < 1) continue;
                if (!Guarded(a, b))
                {
                    if (run.Count >= 2) guarding.Add(new StairRailing(run, false, true));
                    run = new List<StairSidePoint>();
                    continue;
                }

                if (run.Count == 0) run.Add(new StairSidePoint(a, height));
                run.Add(new StairSidePoint(b, height));
            }

            if (run.Count >= 2) guarding.Add(new StairRailing(run, false, true));
        }

        return guarding;
    }

    /// <summary>
    /// A timber tread with its nosing shaped as its type says, carried across the stair from one
    /// side to the other: rounded over, a half round as thick as the tread, or chamfered 10.
    /// </summary>
    private static void AddShapedTread(Mesh3D body, StairStep step, StairType type)
    {
        var o = step.Outline;
        var (front, top, thick) = (o[0], step.Top, type.TreadThickness);
        var along = (o[1] - o[0]).NormalisedOrDefault(step.Up);
        var deep = (o[1] - o[0]).Length;
        var across = o[3] - o[0];

        var section = new List<(double Along, double Z)> { (deep, top - thick), (deep, top) };
        var smooth = new List<int>();
        if (type.NosingProfile == NosingProfile.Rounded)
        {
            var radius = thick / 2;
            for (var k = 0; k <= 8; k++)
            {
                var angle = Math.PI / 2 + Math.PI * k / 8;
                if (k is > 0 and < 8) smooth.Add(section.Count);
                section.Add((radius + radius * Math.Cos(angle), top - radius + radius * Math.Sin(angle)));
            }
        }
        else
        {
            var chamfer = Math.Min(10, thick / 3);
            section.AddRange(new[] { (chamfer, top), (0.0, top - chamfer), (0.0, top - thick) });
        }

        body.AddProfile(section, front, along, across.NormalisedOrDefault(Vector2D.UnitX), across.Length, smooth);
    }

    /// <summary>A type's handrail section: how wide across, and how deep - a round one as deep as it is wide.</summary>
    private static (double Width, double Depth) RailSection(StairType type) =>
        (type.HandrailWidth, type.HandrailProfile == HandrailProfile.Round ? type.HandrailWidth : type.HandrailDepth);

    /// <summary>
    /// How high a rail's top is over the pitch line along a length of it: the handrail's height
    /// up a slope - and on a wall, where it is a handrail rather than guarding, along the level
    /// too; the guarding's height where an open side is level, along a landing or round a stairwell.
    /// </summary>
    private static double RailHeight(StairType type, bool level, bool onWall) => level && !onWall ? type.GuardHeight : type.HandrailHeight;

    /// <summary>Where a rail's line is set in from the side it runs along: on a wall, clear of it; otherwise over the side; fixed to the outside of a stair with no string, just outside it.</summary>
    private static (double Near, double Far) RailOffsets(StairType type, bool onWall, bool sideFixed)
    {
        var width = RailSection(type).Width;
        return onWall ? (type.WallClearance, type.WallClearance + width) : sideFixed ? (-width - 10, -10.0) : (0.0, width);
    }

    /// <summary>Where a run of rail is set in from its side, as the plan draws it.</summary>
    public static (double Near, double Far) PlanOffsets(StairType type, StairRailing railing) => RailOffsets(type, railing.OnWall, sideFixed: false);

    /// <summary>
    /// A run of handrail: the rail along it at its height over the pitch line; on an open side,
    /// balusters under it no further apart than its type says, and newels at its ends and corners.
    /// </summary>
    private static void AddRailing(StairRailing railing, StairType type, StairLayout layout, RailSet set)
    {
        var points = railing.Points;
        var glazed = type.Balustrade == StairBalustrade.Glass && !railing.OnWall;
        var onString = OnString(type, layout);
        var (_, depth) = RailSection(type);

        // Glass up a stair with no string to stand on is fixed to the side of it, just outside, its rail on top.
        var sideFixed = glazed && !onString && points.Zip(points.Skip(1)).Any(pair => Math.Abs(pair.First.Height - pair.Second.Height) > 1);
        var (near, far) = RailOffsets(type, railing.OnWall, sideFixed);

        // The rail, and a second one at 600 under it for children, if the type has one.
        AddRail(set.Rails, points, railing.LeftSide, type, near, far, level => RailHeight(type, level, railing.OnWall));
        if (type.LowerRail) AddRail(set.Rails, points, railing.LeftSide, type, near, far, _ => 600);

        for (var i = 0; i + 1 < points.Count; i++)
        {
            var (a, b) = (points[i], points[i + 1]);
            if ((b.At - a.At).Length < 1 || railing.OnWall) continue;

            var height = Along(a, b);
            var level = Math.Abs(a.Height - b.Height) < 1;
            var top = RailHeight(type, level, onWall: false) - depth;
            if (glazed)
            {
                // A panel under the rail: on the floor or a landing, on the string, or down past the treads' ends.
                var middle = (near + far) / 2;
                set.Glass.AddExtrusion(Strip(points, i, railing.LeftSide, middle - GlassThickness / 2, middle + GlassThickness / 2),
                    point => level ? height(point) - (sideFixed ? 150 : 0)
                        : onString ? height(point) + StringTop(type)
                        : Math.Max(layout.Bottom, height(point) - layout.Rise - type.TreadThickness - 20),
                    point => height(point) + top);
                continue;
            }

            foreach (var (middle, foot, baluster) in BalustersAlong(a, b, railing.LeftSide, type, layout))
                set.Balusters.AddExtrusion(Square(middle, BalusterSize / 2), foot, baluster);
        }

        if (railing.OnWall)
        {
            AddWallFixings(set, railing, type, layout, near, far);
            return;
        }

        if (glazed) return;

        // Newels where it starts and ends, and wherever it turns or steps up.
        for (var i = 0; i < points.Count; i++)
        {
            var at = points[i];
            var turns = i == 0 || i == points.Count - 1 || (points[i + 1].At - at.At).Length < 1 || (at.At - points[i - 1].At).Length < 1
                        || Math.Abs((points[i + 1].At - at.At).NormalisedOrDefault(Vector2D.UnitX).Dot((at.At - points[i - 1].At).NormalisedOrDefault(Vector2D.UnitX))) < 0.9;
            if (!turns) continue;
            if (i > 0 && (at.At - points[i - 1].At).Length < 1 && i < points.Count - 1) continue;
            if (layout.Newels.Any(newel => Against(newel.Outline, at.At, 2))) continue;

            var low = points.Where(p => (p.At - at.At).Length < 1).Min(p => p.Height);
            var high = points.Where(p => (p.At - at.At).Length < 1).Max(p => p.Height);
            var direction = i + 1 < points.Count && (points[i + 1].At - at.At).Length >= 1 ? points[i + 1].At - at.At : at.At - points[Math.Max(0, i - 1)].At;
            var centre = at.At + Inward(direction, railing.LeftSide) * ((near + far) / 2);
            set.Rails.AddExtrusion(Square(centre, NewelSize / 2), Math.Max(layout.Bottom, low - 300), high + Math.Max(type.HandrailHeight, type.GuardHeight) + 100);
        }
    }

    /// <summary>
    /// A rail along a run of points, its top so high over the pitch line along each length:
    /// round, a bar swept along it in one piece round its corners and up its steps; rectangular,
    /// a strip along each length.
    /// </summary>
    private static void AddRail(Mesh3D rails, IReadOnlyList<StairSidePoint> points, bool left, StairType type, double near, double far, Func<bool, double> heightOver)
    {
        var (width, depth) = RailSection(type);
        if (type.HandrailProfile == HandrailProfile.Rectangular)
        {
            for (var i = 0; i + 1 < points.Count; i++)
            {
                var (a, b) = (points[i], points[i + 1]);
                if ((b.At - a.At).Length < 1) continue;
                var height = Along(a, b);
                var over = heightOver(Math.Abs(a.Height - b.Height) < 1);
                rails.AddExtrusion(Strip(points, i, left, near, far), point => height(point) + over - depth, point => height(point) + over);
            }

            return;
        }

        foreach (var line in RailLines(points, left, (near + far) / 2, heightOver))
            rails.AddTube(line.Select(point => new Point3D(point.At.X, point.At.Y, point.Height - width / 2)).ToList(), width / 2);
    }

    /// <summary>
    /// The line along the top of a rail, set in from its side - mitred at each corner so it turns
    /// in one line - with its top so high over the pitch line along each length; where that
    /// changes, it steps straight up or down. In one line, or more where it doubles back on itself.
    /// </summary>
    private static List<List<StairSidePoint>> RailLines(IReadOnlyList<StairSidePoint> points, bool left, double offset, Func<bool, double> heightOver)
    {
        // The way in at each point: halfway between the lengths either side of it that go anywhere.
        Vector2D? WayIn(int index, int step)
        {
            for (var k = index; k + step >= 0 && k + step < points.Count; k += step)
            {
                var way = step > 0 ? points[k + 1].At - points[k].At : points[k].At - points[k - 1].At;
                if (way.Length >= 1) return Inward(way, left);
            }

            return null;
        }

        Point2D Set(int index)
        {
            var (before, after) = (WayIn(index, -1), WayIn(index, 1));
            var inward = (before, after) switch
            {
                ({ } b, { } a) when 1 + b.Dot(a) > 0.1 => (b + a) * (1 / (1 + b.Dot(a))),
                (_, { } a) => a,
                ({ } b, _) => b,
                _ => Vector2D.UnitX
            };
            return points[index].At + inward * offset;
        }

        var lines = new List<List<StairSidePoint>> { new() };
        for (var i = 0; i + 1 < points.Count; i++)
        {
            var (a, b) = (points[i], points[i + 1]);
            if ((b.At - a.At).Length < 1)
            {
                // A step up in place: the rail goes straight up it, if it is on the line already.
                if (lines[^1].Count > 0) lines[^1].Add(new StairSidePoint(Set(i + 1), b.Height + heightOver(false)));
                continue;
            }

            var over = heightOver(Math.Abs(a.Height - b.Height) < 1);
            var line = lines[^1];
            var start = new StairSidePoint(Set(i), a.Height + over);
            if (line.Count == 0 || (line[^1].At - start.At).Length > 1 || Math.Abs(line[^1].Height - start.Height) > 1) line.Add(start);
            line.Add(new StairSidePoint(Set(i + 1), b.Height + over));
        }

        return lines.Where(line => line.Count >= 2).ToList();
    }

    /// <summary>
    /// A handrail on a wall fixed to it: a bracket from the wall up to the rail no more than 900
    /// apart, 150 in from its ends; and its ends finished as its type says - turned back into the
    /// wall, turned down to the floor, stopped on a post, or left cut off.
    /// </summary>
    private static void AddWallFixings(RailSet set, StairRailing railing, StairType type, StairLayout layout, double near, double far)
    {
        var points = railing.Points;
        var (width, depth) = RailSection(type);
        var middle = (near + far) / 2;
        var lengths = points.Zip(points.Skip(1), (a, b) => (b.At - a.At).Length).ToList();
        var total = lengths.Sum();
        if (total < 1) return;

        // A point so far along the run: where it is, the way in from the wall, and the rail's underside over it.
        (Point2D At, Vector2D In, double Under) Along(double distance)
        {
            for (var i = 0; i < lengths.Count; i++)
            {
                if (lengths[i] < 1 || (distance > lengths[i] && i < lengths.Count - 1))
                {
                    distance -= lengths[i];
                    continue;
                }

                var (a, b) = (points[i], points[i + 1]);
                var t = Math.Clamp(distance / lengths[i], 0, 1);
                var level = Math.Abs(a.Height - b.Height) < 1;
                return (a.At + (b.At - a.At) * t, Inward(b.At - a.At, railing.LeftSide), a.Height + (b.Height - a.Height) * t + RailHeight(type, level, onWall: true) - depth);
            }

            return (points[^1].At, Vector2D.UnitX, points[^1].Height + type.HandrailHeight - depth);
        }

        var brackets = Math.Max(2, (int)Math.Ceiling((total - 300) / 900) + 1);
        for (var k = 0; k < brackets; k++)
        {
            var (at, inward, under) = Along(total < 300 ? total / 2 : 150 + (total - 300) * k / (brackets - 1));
            var across = inward.PerpendicularLeft() * 6;
            var arm = new[] { at - across, at + inward * middle - across, at + inward * middle + across, at + across };
            set.Fixings.AddExtrusion(arm, under - 42, under - 30);
            set.Fixings.AddExtrusion(Square(at + inward * middle, 6), under - 42, under);
        }

        // Its ends: the first point, and the last.
        foreach (var (end, toward) in new[] { (0, 1), (points.Count - 1, -1) })
        {
            var (at, inward, under) = Along(end == 0 ? 0 : total);
            var centre = at + inward * middle;
            var top = under + depth;
            var floor = layout.Steps.Where(step => step.Top <= points[end].Height + 1 && Polygon2D.Contains(step.Outline, centre)).Select(step => (double?)step.Top).Max()
                        ?? (points[end].Height >= layout.Top - 1 ? layout.Top : layout.Bottom);
            switch (type.HandrailEnds)
            {
                case HandrailEnds.ReturnToWall when type.HandrailProfile == HandrailProfile.Round:
                    set.Rails.AddTube(new[] { new Point3D(centre.X, centre.Y, top - width / 2), new Point3D(at.X, at.Y, top - width / 2) }, width / 2);
                    break;
                case HandrailEnds.ReturnToWall:
                    var along = (points[end + toward].At - points[end].At).NormalisedOrDefault(Vector2D.UnitX) * (width / 2);
                    set.Rails.AddExtrusion(new[] { at - along, at + inward * far - along, at + inward * far + along, at + along }, under, top);
                    break;
                case HandrailEnds.ReturnToFloor when type.HandrailProfile == HandrailProfile.Round:
                    set.Rails.AddTube(new[] { new Point3D(centre.X, centre.Y, top - width / 2), new Point3D(centre.X, centre.Y, floor) }, width / 2);
                    break;
                case HandrailEnds.ReturnToFloor:
                    set.Rails.AddExtrusion(Square(centre, width / 2), floor, top);
                    break;
                case HandrailEnds.Post:
                    set.Rails.AddExtrusion(Square(centre, NewelSize / 2), floor, top + 100);
                    break;
            }
        }
    }

    /// <summary>
    /// The balusters along a length of a balustrade: where each stands, what it stands on, and
    /// up to the handrail's underside. On a timber stair they stand on the string, 75 over the
    /// pitch line; on a concrete one or a spiral, with no string, on the tread under each - the
    /// one under the handrail, not one a turn further up a stair that winds over itself; on a
    /// landing or the floor above, on that.
    /// </summary>
    public static IEnumerable<(Point2D At, double Foot, double Top)> BalustersAlong(
        StairSidePoint a, StairSidePoint b, bool leftSide, StairType type, StairLayout layout)
    {
        var along = b.At - a.At;
        if (along.Length < 1) yield break;

        var inward = Inward(along, leftSide);
        var height = Along(a, b);
        var level = Math.Abs(a.Height - b.Height) < 1;
        var onString = OnString(type, layout);
        var (width, depth) = RailSection(type);
        var top = RailHeight(type, level, onWall: false) - depth;
        var count = Math.Max(1, (int)Math.Ceiling(along.Length / Math.Max(40, type.BalusterSpacing)));
        for (var k = 0; k < count; k++)
        {
            var middle = a.At + along * ((k + 0.5) / count) + inward * (width / 2);
            var pitch = height(middle);
            var foot = level ? pitch
                : onString ? pitch + StringTop(type)
                : layout.Steps.Where(step => step.Top <= pitch + 1 && Polygon2D.Contains(step.Outline, middle)).Select(step => (double?)step.Top).Max() ?? pitch - layout.Rise;
            yield return (middle, foot, pitch + top);
        }
    }

    private static IReadOnlyList<Point2D> Square(Point2D centre, double half) => new[]
    {
        new Point2D(centre.X - half, centre.Y - half), new Point2D(centre.X + half, centre.Y - half),
        new Point2D(centre.X + half, centre.Y + half), new Point2D(centre.X - half, centre.Y + half)
    };
}
