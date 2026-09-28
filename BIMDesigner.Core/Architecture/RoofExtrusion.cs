using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// The profile of a roof by extrusion as it is drawn: its points, left to right, and for each
/// line between two of them how far it bows into an arc - its sagitta, the height of the arc's
/// middle above the straight line between its ends. Nothing for a straight line; positive
/// bows up, negative sags.
///
/// An arc is kept as an arc, not as the short straight pieces it is built from, so a barrel
/// vault is one line with one number on it, and the pieces are fine enough to read as a curve
/// however it is edited.
/// </summary>
public sealed record RoofProfile(IReadOnlyList<Point2D> Points, IReadOnlyList<double> Sagittas)
{
    /// <summary>A profile of straight lines only.</summary>
    public static RoofProfile Straight(IEnumerable<Point2D> points)
    {
        var list = points.ToList();
        return new RoofProfile(list, new double[Math.Max(0, list.Count - 1)]);
    }

    public bool HasArcs => Sagittas.Any(sagitta => sagitta != 0);

    /// <summary>The profile moved along its line, as a starting shape is set over where the building is.</summary>
    public RoofProfile Shifted(double along) =>
        new(Points.Select(point => new Point2D(point.X + along, point.Y)).ToList(), Sagittas);
}

/// <summary>
/// A roof by extrusion (specification section 3.3; Revit's Roof by Extrusion): a profile drawn
/// square-on, as an elevation of the roof, pushed straight back through the building.
///
/// It is for the roofs a footprint cannot describe - those whose section is the thing that
/// matters: a gambrel, with two pitches a side; a barrel vault; a saltbox; any roof the same
/// all the way along. The profile is the roof's underside, as a footprint roof's planes are,
/// so an extruded roof also sits on what carries it and walls attach up to it the same way.
///
/// Positions are in a frame of the roof's own. The profile runs along <see cref="Direction"/>
/// from <see cref="Origin"/> - the line drawn in plan - with each point's X the distance along
/// it and Y the height above the roof's base. The roof then runs across that line, from
/// <see cref="Start"/> to <see cref="End"/>, measured to the left of the drawn direction.
/// </summary>
public sealed class RoofExtrusion
{
    /// <summary>The most an arc turns between two of the straight pieces it is built from.</summary>
    private const double MaxFacetTurn = 3 * Math.PI / 180;

    private readonly List<bool> _smooth;

    public RoofExtrusion(
        Point2D origin, Vector2D direction, IEnumerable<Point2D> profile, double start, double end,
        IEnumerable<double>? sagittas = null)
    {
        Origin = origin;
        Direction = direction.NormalisedOrDefault(Vector2D.UnitX);
        Profile = profile.ToList();
        Start = Math.Min(start, end);
        End = Math.Max(start, end);

        var bows = sagittas?.ToList() ?? new List<double>();
        Sagittas = Enumerable.Range(0, Math.Max(0, Profile.Count - 1))
            .Select(i => i < bows.Count && double.IsFinite(bows[i]) ? bows[i] : 0)
            .ToList();

        (Points, _smooth) = Facet(Profile, Sagittas);
    }

    public RoofExtrusion(Point2D origin, Vector2D direction, RoofProfile profile, double start, double end)
        : this(origin, direction, profile.Points, start, end, profile.Sagittas)
    {
    }

    /// <summary>Where the profile's line starts in plan.</summary>
    public Point2D Origin { get; }

    /// <summary>Which way the profile runs in plan, as a unit vector.</summary>
    public Vector2D Direction { get; }

    /// <summary>The profile's points as drawn: distance along the line, height above the base. Left to right.</summary>
    public IReadOnlyList<Point2D> Profile { get; }

    /// <summary>How far each line of the profile bows into an arc; zero for a straight one.</summary>
    public IReadOnlyList<double> Sagittas { get; }

    /// <summary>The profile as drawn - points and arcs together.</summary>
    public RoofProfile Shape => new(Profile, Sagittas);

    public bool HasArcs => Sagittas.Any(sagitta => sagitta != 0);

    /// <summary>
    /// The profile as the roof is built from it: the drawn points, with every arc broken into
    /// short straight pieces between them.
    /// </summary>
    public IReadOnlyList<Point2D> Points { get; }

    /// <summary>
    /// Whether one of <see cref="Points"/> is part way round an arc rather than a corner of the
    /// profile. The roof turns there too, but so little it is one curved surface: no ridge is
    /// drawn along it, and it is shaded smooth.
    /// </summary>
    public bool IsSmooth(int point) => point >= 0 && point < _smooth.Count && _smooth[point];

    /// <summary>Where the roof starts across the line - Revit's Extrusion Start.</summary>
    public double Start { get; }

    /// <summary>Where it ends - Revit's Extrusion End.</summary>
    public double End { get; }

    /// <summary>Across the line, the way the roof is pushed.</summary>
    public Vector2D Across => Direction.PerpendicularLeft();

    public double Depth => End - Start;

    public RoofExtrusion With(
        Point2D? origin = null, Vector2D? direction = null, RoofProfile? shape = null,
        double? start = null, double? end = null)
    {
        var profile = shape ?? Shape;
        return new RoofExtrusion(origin ?? Origin, direction ?? Direction, profile.Points, start ?? Start, end ?? End, profile.Sagittas);
    }

    /// <summary>A point in plan, from a distance along the line and one across it.</summary>
    public Point2D At(double along, double across) => Origin + Direction * along + Across * across;

    /// <summary>
    /// The roof in plan: from the profile's first point to its last, across from start to end.
    /// Anticlockwise, the way every slab outline is kept.
    /// </summary>
    public IReadOnlyList<Point2D> Footprint()
    {
        if (Profile.Count < 2) return Array.Empty<Point2D>();

        var from = Profile[0].X;
        var to = Profile[^1].X;

        var corners = new List<Point2D>
        {
            At(from, Start), At(to, Start), At(to, End), At(from, End)
        };

        return Polygon2D.SignedArea(corners) >= 0 ? corners : corners.AsEnumerable().Reverse().ToList();
    }

    /// <summary>
    /// What is wrong with the profile, in words that say what to do about it, or null if it
    /// makes a roof. Revit's rule is that every part of an extruded roof must face upward -
    /// nothing upright, nothing folding back under itself - which comes to the profile always
    /// moving on in the same direction, arcs included.
    /// </summary>
    public string? Problem()
    {
        if (Profile.Count < 2) return "The profile needs at least two points.";
        if (Depth <= 1) return "The roof needs some depth: its start and end are the same.";

        // A half circle stands upright where it starts and ends, and past one an arc curls
        // back under itself.
        for (var i = 0; i + 1 < Profile.Count; i++)
        {
            if (Sagittas[i] != 0 && Math.Abs(Sagittas[i]) >= Profile[i].DistanceTo(Profile[i + 1]) / 2 - 1)
                return "An arc in the profile bows as far as a half circle, so it would stand upright at its ends. Make it flatter.";
        }

        for (var i = 0; i + 1 < Points.Count; i++)
        {
            var run = Points[i + 1].X - Points[i].X;
            var drawn = IsSmooth(i) || IsSmooth(i + 1) ? "An arc in the profile" : "A line in the profile";

            if (Math.Abs(run) <= 1)
                return $"{drawn} is upright. Every part of a roof must face upward - slope it, or take the point out.";

            if (run < 0)
                return $"{drawn} folds back under itself. Every part of a roof must face upward: keep each point further along than the last.";
        }

        return null;
    }

    public bool IsValid => Problem() is null;

    /// <summary>
    /// The plane under one of the straight pieces the roof is built from (<see cref="Points"/>),
    /// in plan: level along the extrusion, sloping as the piece does. Its eave is the lower end.
    /// </summary>
    public RoofPlane PlaneUnder(int segment, double baseElevation)
    {
        var (a, b) = (Points[segment], Points[segment + 1]);
        var slope = (b.Y - a.Y) / (b.X - a.X);

        // Height = base + a.Y + slope * (along - a.X), where along = (p - Origin) . Direction.
        var dirA = slope * Direction.X;
        var dirB = slope * Direction.Y;
        var c = baseElevation + a.Y - slope * (a.X + Origin.X * Direction.X + Origin.Y * Direction.Y);

        return new RoofPlane(dirA, dirB, c, baseElevation + Math.Min(a.Y, b.Y));
    }

    /// <summary>What shape the profile is, in the terms IFC names roofs by.</summary>
    public RoofForm Form()
    {
        // Arcs all bowing up, and nothing else: a vault. Any other mix of arcs is freeform.
        if (HasArcs) return Sagittas.All(sagitta => sagitta > 0) ? RoofForm.Barrel : RoofForm.Freeform;

        var slopes = Enumerable.Range(0, Math.Max(0, Profile.Count - 1))
            .Select(i => (Profile[i + 1].Y - Profile[i].Y) / (Profile[i + 1].X - Profile[i].X))
            .ToList();

        if (slopes.Count == 0 || slopes.All(slope => Math.Abs(slope) < 1e-6)) return RoofForm.Flat;
        if (slopes.Count == 1) return RoofForm.Shed;

        // Rising then falling, one way each: a gable.
        if (slopes.Count == 2 && slopes[0] > 0 && slopes[1] < 0) return RoofForm.Gable;

        // Two pitches up and two down, the lower ones steeper: a gambrel.
        if (slopes.Count == 4 && slopes[0] > slopes[1] && slopes[1] > 0 && slopes[2] < 0 && slopes[3] < slopes[2])
            return RoofForm.Gambrel;

        // Many short lines turning steadily one way: an arch drawn a line at a time.
        if (slopes.Count >= 6)
        {
            var turning = Enumerable.Range(0, slopes.Count - 1).All(i => slopes[i + 1] < slopes[i]);
            if (turning) return RoofForm.Barrel;
        }

        return RoofForm.Freeform;
    }

    // ---- arcs --------------------------------------------------------------------------

    /// <summary>The middle of the arc from <paramref name="a"/> to <paramref name="b"/> - or of the line, when it is straight.</summary>
    public static Point2D ArcMiddle(Point2D a, Point2D b, double sagitta) =>
        a + (b - a) / 2 + (b - a).NormalisedOrDefault(Vector2D.UnitX).PerpendicularLeft() * sagitta;

    /// <summary>The circle an arc lies on, or null when the line is straight.</summary>
    public static (Point2D Centre, double Radius)? Circle(Point2D a, Point2D b, double sagitta)
    {
        var chord = b - a;
        var length = chord.Length;
        if (Math.Abs(sagitta) < 1e-9 || length < 1e-9) return null;

        var half = length / 2;
        var radius = (half * half + sagitta * sagitta) / (2 * Math.Abs(sagitta));
        var normal = (chord / length).PerpendicularLeft();

        // The centre is on the far side of the chord from the bow, for anything short of a half circle.
        return (a + chord / 2 + normal * (Math.Sign(sagitta) * (Math.Abs(sagitta) - radius)), radius);
    }

    /// <summary>How far round its circle an arc runs, in radians: four times the angle whose tangent is sagitta over half the chord.</summary>
    private static double Sweep(Point2D a, Point2D b, double sagitta) =>
        4 * Math.Atan(Math.Abs(sagitta) / Math.Max(a.DistanceTo(b) / 2, 1e-9));

    /// <summary>An arc as the short straight pieces it is built from, both ends included - just the ends for a straight line.</summary>
    public static IReadOnlyList<Point2D> ArcPoints(Point2D a, Point2D b, double sagitta)
    {
        if (Circle(a, b, sagitta) is not { } circle) return new[] { a, b };

        var (centre, radius) = circle;

        var sweep = Sweep(a, b, sagitta);

        // Even, so the middle of the arc - a vault's crown - is a point of its own.
        var pieces = Math.Clamp((int)Math.Ceiling(sweep / MaxFacetTurn), 2, 120);
        if (pieces % 2 == 1) pieces++;

        // Bowing to the left of the way it runs means turning clockwise round the centre.
        var from = Math.Atan2(a.Y - centre.Y, a.X - centre.X);
        var turn = -Math.Sign(sagitta) * sweep;

        var points = new List<Point2D> { a };
        for (var k = 1; k < pieces; k++)
        {
            var angle = from + turn * k / pieces;
            points.Add(new Point2D(centre.X + radius * Math.Cos(angle), centre.Y + radius * Math.Sin(angle)));
        }

        points.Add(b);
        return points;
    }

    /// <summary>
    /// The sagitta of the arc from <paramref name="a"/> to <paramref name="b"/> that passes
    /// through <paramref name="through"/> - how two arcs of one circle become one again when
    /// the point between them is taken out. Zero when the three are in line.
    /// </summary>
    public static double SagittaThrough(Point2D a, Point2D through, Point2D b)
    {
        var chord = b - a;
        var length = chord.Length;
        if (length < 1e-9) return 0;

        var normal = (chord / length).PerpendicularLeft();
        var side = (through - a).Dot(normal);
        if (Math.Abs(side) < 1e-6) return 0;

        // The circle through all three.
        var ab = b - a;
        var ap = through - a;
        var d = 2 * ab.Cross(ap);
        if (Math.Abs(d) < 1e-12) return 0;

        var abSq = ab.Dot(ab);
        var apSq = ap.Dot(ap);
        var centre = a + new Vector2D(ap.Y * abSq - ab.Y * apSq, ab.X * apSq - ap.X * abSq) / d;
        var radius = centre.DistanceTo(a);

        // The furthest point of the circle from the chord on the side the middle point is.
        return (centre - a).Dot(normal) + Math.Sign(side) * radius;
    }

    /// <summary>
    /// Divides an arc at the point on it nearest <paramref name="near"/>: where that is, and
    /// the sagittas of the two arcs either side, which together are the same curve.
    /// </summary>
    public static (Point2D At, double First, double Second) SplitArc(Point2D a, Point2D b, double sagitta, Point2D near)
    {
        if (Circle(a, b, sagitta) is not { } circle)
        {
            var chord = b - a;
            var t = Math.Clamp((near - a).Dot(chord) / Math.Max(chord.Dot(chord), 1e-12), 0.02, 0.98);
            return (a + chord * t, 0, 0);
        }

        var (centre, radius) = circle;

        var sweep = Sweep(a, b, sagitta);
        var from = Math.Atan2(a.Y - centre.Y, a.X - centre.X);
        var direction = -Math.Sign(sagitta);

        var turned = (Math.Atan2(near.Y - centre.Y, near.X - centre.X) - from) * direction;
        turned = ((turned % (2 * Math.PI)) + 2 * Math.PI) % (2 * Math.PI);

        // Off the end of the arc: whichever end is nearer.
        if (turned > sweep) turned = turned - sweep < 2 * Math.PI - turned ? sweep : 0;
        var fraction = Math.Clamp(turned / sweep, 0.02, 0.98);

        var angle = from + direction * sweep * fraction;
        var at = new Point2D(centre.X + radius * Math.Cos(angle), centre.Y + radius * Math.Sin(angle));

        double Part(Point2D p, Point2D q, double part) =>
            Math.Sign(sagitta) * p.DistanceTo(q) / 2 * Math.Tan(part / 4);

        return (at, Part(a, at, sweep * fraction), Part(at, b, sweep * (1 - fraction)));
    }

    /// <summary>The profile broken into straight pieces, marking which of their points lie part way round an arc.</summary>
    private static (List<Point2D> Points, List<bool> Smooth) Facet(IReadOnlyList<Point2D> profile, IReadOnlyList<double> sagittas)
    {
        var points = new List<Point2D>();
        var smooth = new List<bool>();
        if (profile.Count == 0) return (points, smooth);

        points.Add(profile[0]);
        smooth.Add(false);

        for (var i = 0; i + 1 < profile.Count; i++)
        {
            var arc = ArcPoints(profile[i], profile[i + 1], sagittas[i]);

            for (var k = 1; k < arc.Count; k++)
            {
                points.Add(arc[k]);
                smooth.Add(k < arc.Count - 1);
            }
        }

        return (points, smooth);
    }

    // ---- the solid ----------------------------------------------------------------

    /// <summary>
    /// The profile moved up out of the roof by a distance measured square to it, with its
    /// corners mitred: where one piece's offset line meets the next's. It is what a layer
    /// boundary of the build-up is, and mitring is what keeps the top of a gambrel from
    /// stepping at its break, where the steep pitch's build-up is deeper in a vertical cut
    /// than the shallow one's. Round an arc it is the arc a build-up's depth further out.
    /// </summary>
    private List<Point2D> Offset(double distance)
    {
        var count = Points.Count;
        var normals = new List<Vector2D>();

        for (var i = 0; i + 1 < count; i++)
        {
            var run = Points[i + 1] - Points[i];
            var unit = run.NormalisedOrDefault(Vector2D.UnitX);
            normals.Add(new Vector2D(-unit.Y, unit.X));      // up, since the profile runs forward
        }

        var points = new List<Point2D> { Points[0] + normals[0] * distance };

        for (var i = 1; i + 1 < count; i++)
        {
            var before = (From: Points[i - 1] + normals[i - 1] * distance, To: Points[i] + normals[i - 1] * distance);
            var after = (From: Points[i] + normals[i] * distance, To: Points[i + 1] + normals[i] * distance);
            points.Add(Crossing(before.From, before.To, after.From, after.To) ?? after.From);
        }

        points.Add(Points[^1] + normals[^1] * distance);
        return points;
    }

    private static Point2D? Crossing(Point2D a0, Point2D a1, Point2D b0, Point2D b1)
    {
        var a = a1 - a0;
        var b = b1 - b0;
        var denominator = a.Cross(b);
        if (Math.Abs(denominator) <= a.Length * b.Length * 1e-9) return null;

        return a0 + a * ((b0 - a0).Cross(b) / denominator);
    }

    /// <summary>A polyline in the profile's frame, as a height at any distance along it - carried straight on past its ends.</summary>
    private static (double Slope, double At0) LineUnder(IReadOnlyList<Point2D> polyline, double along)
    {
        var i = 0;
        while (i + 2 < polyline.Count && along > polyline[i + 1].X) i++;

        var (a, b) = (polyline[i], polyline[i + 1]);
        var slope = Math.Abs(b.X - a.X) < 1e-9 ? 0 : (b.Y - a.Y) / (b.X - a.X);
        return (slope, a.Y - slope * a.X);
    }

    /// <summary>
    /// The roof as solid pieces, layer by layer: each layer between two mitred offsets of the
    /// profile, cut into upright slices wherever either of its faces turns a corner, so that
    /// in every slice the layer runs between two planes. The ends are cut plumb at the first
    /// and last points of the profile, which is where the roof's edges are drawn.
    /// </summary>
    public IReadOnlyList<RoofPiece> Pieces(CompoundStructure structure, double baseElevation)
    {
        if (!IsValid) return Array.Empty<RoofPiece>();

        var total = structure.TotalWidth;
        var from = Profile[0].X;
        var to = Profile[^1].X;
        var pieces = new List<RoofPiece>();

        foreach (var (layer, start, end) in structure.GetLayerOffsets())
        {
            if (layer.Thickness <= 0) continue;

            // Offsets are measured up from the underside; the layers are listed from the top.
            var lower = Offset(total - end);
            var upper = Offset(total - start);

            var cuts = lower.Concat(upper)
                .Select(point => point.X)
                .Where(x => x > from + 1e-6 && x < to - 1e-6)
                .Append(from)
                .Append(to)
                .Distinct()
                .OrderBy(x => x)
                .ToList();

            for (var k = 0; k + 1 < cuts.Count; k++)
            {
                var (a, b) = (cuts[k], cuts[k + 1]);
                if (b - a <= 1e-6) continue;

                var middle = (a + b) / 2;
                var bottom = LineUnder(lower, middle);
                var top = LineUnder(upper, middle);

                pieces.Add(new RoofPiece(
                    new[] { At(a, Start), At(b, Start), At(b, End), At(a, End) },
                    layer,
                    Plane(bottom, baseElevation),
                    Plane(top, baseElevation)));
            }
        }

        return pieces;
    }

    /// <summary>
    /// The build-up in section, a layer at a time: the line of its underside and of its top,
    /// each from the first point of the profile to the last with the ends cut plumb, and which
    /// of their points lie part way round an arc. What the 3D model sweeps back through the
    /// roof, so that a vault is one smooth surface rather than a row of flat strips.
    /// </summary>
    public IReadOnlyList<RoofLayerSection> Sections(CompoundStructure structure)
    {
        if (!IsValid) return Array.Empty<RoofLayerSection>();

        var total = structure.TotalWidth;
        var from = Profile[0].X;
        var to = Profile[^1].X;
        var sections = new List<RoofLayerSection>();

        (List<Point2D> Line, List<bool> Smooth) Plumb(List<Point2D> offset)
        {
            var line = new List<Point2D>();
            var smooth = new List<bool>();

            void Add(double x, bool isSmooth)
            {
                var (slope, at0) = LineUnder(offset, x);
                line.Add(new Point2D(x, slope * x + at0));
                smooth.Add(isSmooth);
            }

            Add(from, false);

            for (var i = 0; i < offset.Count; i++)
            {
                if (offset[i].X <= from + 1e-6 || offset[i].X >= to - 1e-6) continue;
                if (offset[i].X <= line[^1].X + 1e-6) continue;

                line.Add(offset[i]);
                smooth.Add(IsSmooth(i));
            }

            Add(to, false);
            return (line, smooth);
        }

        foreach (var (layer, start, end) in structure.GetLayerOffsets())
        {
            if (layer.Thickness <= 0) continue;

            var (lower, lowerSmooth) = Plumb(Offset(total - end));
            var (upper, upperSmooth) = Plumb(Offset(total - start));
            sections.Add(new RoofLayerSection(layer, lower, upper, lowerSmooth, upperSmooth));
        }

        return sections;
    }

    /// <summary>A height along the profile, as a plane in plan.</summary>
    private RoofPlane Plane((double Slope, double At0) line, double baseElevation)
    {
        var a = line.Slope * Direction.X;
        var b = line.Slope * Direction.Y;
        var c = baseElevation + line.At0 - line.Slope * (Origin.X * Direction.X + Origin.Y * Direction.Y);
        return new RoofPlane(a, b, c, baseElevation + line.At0);
    }

    // ---- starting points ---------------------------------------------------------------

    /// <summary>
    /// The shapes a profile is usually begun from, set out over a span: a building <paramref name="width"/>
    /// wide, rising <paramref name="rise"/> to its highest point, with the eaves carried
    /// <paramref name="overhang"/> out past each side and down at the same pitch - or round the
    /// same curve, for a vault.
    /// </summary>
    public static RoofProfile Preset(RoofForm form, double width, double rise, double overhang = 0)
    {
        width = Math.Max(width, 100);
        rise = Math.Max(rise, 0);
        overhang = Math.Max(overhang, 0);

        if (form == RoofForm.Barrel) return Vault(width, rise, overhang);

        List<Point2D> points = form switch
        {
            RoofForm.Shed => new() { new(0, 0), new(width, rise) },

            RoofForm.Gable => new() { new(0, 0), new(width / 2, rise), new(width, 0) },

            // A gambrel's break is a fifth of the way in, seven tenths of the way up: steep
            // below, shallow above, the barn roof everyone recognises.
            RoofForm.Gambrel => new()
            {
                new(0, 0), new(width * 0.2, rise * 0.7), new(width / 2, rise),
                new(width * 0.8, rise * 0.7), new(width, 0)
            },

            RoofForm.Flat => new() { new(0, 0), new(width, 0) },

            // Falling to a gutter in the middle.
            _ => new() { new(0, rise), new(width / 2, 0), new(width, rise) }
        };

        return RoofProfile.Straight(overhang > 0 ? WithOverhang(points, overhang) : points);
    }

    /// <summary>
    /// A segmental vault over the span: one arc from side to side, no higher than a half
    /// circle. An overhang carries the same curve on down past the walls - but not so far that
    /// its eaves turn toward upright.
    /// </summary>
    private static RoofProfile Vault(double width, double rise, double overhang)
    {
        // Short of a half circle, which would stand upright where it springs from the walls.
        var half = width / 2;
        rise = Math.Min(rise, half * 0.9);
        if (rise <= 1) return RoofProfile.Straight(new[] { new Point2D(-overhang, 0), new Point2D(width + overhang, 0) });

        if (overhang <= 0) return new RoofProfile(new[] { new Point2D(0, 0), new Point2D(width, 0) }, new[] { rise });

        var radius = (half * half + rise * rise) / (2 * rise);
        var centreY = rise - radius;

        // No further out than where the curve is pitched at 70 degrees.
        var reach = Math.Min(half + overhang, radius * Math.Sin(70 * Math.PI / 180));
        if (reach <= half) return new RoofProfile(new[] { new Point2D(0, 0), new Point2D(width, 0) }, new[] { rise });

        var eave = centreY + Math.Sqrt(radius * radius - reach * reach);
        return new RoofProfile(
            new[] { new Point2D(half - reach, eave), new Point2D(half + reach, eave) },
            new[] { rise - eave });
    }

    /// <summary>Carries the first and last segments on outward and down, as eaves past the walls.</summary>
    private static List<Point2D> WithOverhang(List<Point2D> points, double overhang)
    {
        var first = points[1] - points[0];
        var last = points[^1] - points[^2];

        var extended = new List<Point2D>(points);
        extended[0] = points[0] - first * (overhang / Math.Abs(first.X));
        extended[^1] = points[^1] + last * (overhang / Math.Abs(last.X));
        return extended;
    }
}

/// <summary>
/// One layer of an extruded roof's build-up in section: its underside and its top, from eave
/// to eave, with which points of each lie part way round an arc.
/// </summary>
public sealed record RoofLayerSection(
    MaterialLayer Layer,
    IReadOnlyList<Point2D> Lower, IReadOnlyList<Point2D> Upper,
    IReadOnlyList<bool> LowerSmooth, IReadOnlyList<bool> UpperSmooth);

/// <summary>
/// Gives a roof a new extrusion - a new profile, depth or placing - as one undoable step. What
/// Edit Profile does when it is finished.
/// </summary>
public sealed class SetRoofExtrusionCommand : Documents.Commands.IUndoableCommand
{
    private readonly Roof _roof;
    private readonly RoofExtrusion? _old;
    private readonly RoofExtrusion? _new;

    public SetRoofExtrusionCommand(Roof roof, RoofExtrusion? extrusion, string name = "Edit Profile")
    {
        _roof = roof;
        _old = roof.Extrusion;
        _new = extrusion;
        Name = name;
    }

    public string Name { get; }

    public void Redo() => _roof.SetExtrusion(_new);

    public void Undo() => _roof.SetExtrusion(_old);
}
