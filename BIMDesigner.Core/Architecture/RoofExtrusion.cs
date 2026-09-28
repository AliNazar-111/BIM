using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;

namespace BIMDesigner.Core.Architecture;

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
    public RoofExtrusion(Point2D origin, Vector2D direction, IEnumerable<Point2D> profile, double start, double end)
    {
        Origin = origin;
        Direction = direction.NormalisedOrDefault(Vector2D.UnitX);
        Profile = profile.ToList();
        Start = Math.Min(start, end);
        End = Math.Max(start, end);
    }

    /// <summary>Where the profile's line starts in plan.</summary>
    public Point2D Origin { get; }

    /// <summary>Which way the profile runs in plan, as a unit vector.</summary>
    public Vector2D Direction { get; }

    /// <summary>The profile: distance along the line, height above the base. Left to right.</summary>
    public IReadOnlyList<Point2D> Profile { get; }

    /// <summary>Where the roof starts across the line - Revit's Extrusion Start.</summary>
    public double Start { get; }

    /// <summary>Where it ends - Revit's Extrusion End.</summary>
    public double End { get; }

    /// <summary>Across the line, the way the roof is pushed.</summary>
    public Vector2D Across => Direction.PerpendicularLeft();

    public double Depth => End - Start;

    public RoofExtrusion With(
        Point2D? origin = null, Vector2D? direction = null, IEnumerable<Point2D>? profile = null,
        double? start = null, double? end = null) =>
        new(origin ?? Origin, direction ?? Direction, profile ?? Profile, start ?? Start, end ?? End);

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
    /// moving on in the same direction.
    /// </summary>
    public string? Problem()
    {
        if (Profile.Count < 2) return "The profile needs at least two points.";
        if (Depth <= 1) return "The roof needs some depth: its start and end are the same.";

        for (var i = 0; i + 1 < Profile.Count; i++)
        {
            var run = Profile[i + 1].X - Profile[i].X;

            if (Math.Abs(run) <= 1)
                return "A line in the profile is upright. Every part of a roof must face upward - slope it, or take the point out.";

            if (run < 0)
                return "The profile folds back under itself. Every part of a roof must face upward: keep each point further along than the last.";
        }

        return null;
    }

    public bool IsValid => Problem() is null;

    /// <summary>
    /// The plane under one segment of the profile, in plan: level along the extrusion,
    /// sloping as the segment does. Its eave is the lower end, where the segment starts
    /// rising.
    /// </summary>
    public RoofPlane PlaneUnder(int segment, double baseElevation)
    {
        var (a, b) = (Profile[segment], Profile[segment + 1]);
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

        // Many short segments turning steadily one way: an arch.
        if (slopes.Count >= 6)
        {
            var turning = Enumerable.Range(0, slopes.Count - 1).All(i => slopes[i + 1] < slopes[i]);
            if (turning) return RoofForm.Barrel;
        }

        return RoofForm.Freeform;
    }

    // ---- the solid ----------------------------------------------------------------

    /// <summary>
    /// The profile moved up out of the roof by a distance measured square to it, with its
    /// corners mitred: where one segment's offset line meets the next's. It is what a layer
    /// boundary of the build-up is, and mitring is what keeps the top of a gambrel from
    /// stepping at its break, where the steep pitch's build-up is deeper in a vertical cut
    /// than the shallow one's.
    /// </summary>
    private List<Point2D> Offset(double distance)
    {
        var count = Profile.Count;
        var normals = new List<Vector2D>();

        for (var i = 0; i + 1 < count; i++)
        {
            var run = Profile[i + 1] - Profile[i];
            var unit = run.NormalisedOrDefault(Vector2D.UnitX);
            normals.Add(new Vector2D(-unit.Y, unit.X));      // up, since the profile runs forward
        }

        var points = new List<Point2D> { Profile[0] + normals[0] * distance };

        for (var i = 1; i + 1 < count; i++)
        {
            var before = (From: Profile[i - 1] + normals[i - 1] * distance, To: Profile[i] + normals[i - 1] * distance);
            var after = (From: Profile[i] + normals[i] * distance, To: Profile[i + 1] + normals[i] * distance);
            points.Add(Crossing(before.From, before.To, after.From, after.To) ?? after.From);
        }

        points.Add(Profile[^1] + normals[^1] * distance);
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
    /// <paramref name="overhang"/> out past each side and down at the same pitch.
    /// </summary>
    public static IReadOnlyList<Point2D> Preset(RoofForm form, double width, double rise, double overhang = 0)
    {
        width = Math.Max(width, 100);
        rise = Math.Max(rise, 0);
        overhang = Math.Max(overhang, 0);

        List<Point2D> points = form switch
        {
            RoofForm.Shed => new() { new(0, 0), new(width, rise) },

            RoofForm.Gable => new() { new(0, 0), new(width / 2, rise), new(width, 0) },

            // A gambrel's break is a quarter of the way in, three quarters of the way up: steep
            // below, shallow above, the barn roof everyone recognises.
            RoofForm.Gambrel => new()
            {
                new(0, 0), new(width * 0.2, rise * 0.7), new(width / 2, rise),
                new(width * 0.8, rise * 0.7), new(width, 0)
            },

            RoofForm.Barrel => Arch(width, rise),

            RoofForm.Flat => new() { new(0, 0), new(width, 0) },

            // Falling to a gutter in the middle.
            _ => new() { new(0, rise), new(width / 2, 0), new(width, rise) }
        };

        return overhang > 0 ? WithOverhang(points, overhang) : points;
    }

    /// <summary>A segmental arch over the span, as short straight pieces - a line every few degrees.</summary>
    private static List<Point2D> Arch(double width, double rise)
    {
        if (rise <= 1) return new() { new(0, 0), new(width, 0) };

        // The circle through both springing points and the crown.
        var half = width / 2;
        var radius = (half * half + rise * rise) / (2 * rise);
        var centre = new Point2D(half, rise - radius);
        var sweep = 2 * Math.Asin(half / radius);
        var pieces = Math.Clamp((int)Math.Ceiling(sweep / (6 * Math.PI / 180)), 6, 48);

        var start = Math.PI / 2 + sweep / 2;
        return Enumerable.Range(0, pieces + 1)
            .Select(i => start - sweep * i / pieces)
            .Select(angle => new Point2D(centre.X + radius * Math.Cos(angle), centre.Y + radius * Math.Sin(angle)))
            .ToList();
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
