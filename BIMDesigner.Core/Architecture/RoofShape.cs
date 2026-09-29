using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// One edge of a roof's footprint, and what the roof does at it (specification section 3.3;
/// Revit's "Defines Slope" on a roof sketch line).
///
/// An edge that defines slope is an eave: the roof rises inward from it at the pitch given
/// here. An edge that does not is a gable: the roof runs past it and is cut off vertically,
/// which is why a rectangle with two sloping edges is a gable roof and one with four is a hip.
/// The whole shape of a roof by footprint comes out of those two facts, one edge at a time.
/// </summary>
public sealed class RoofEdge
{
    /// <summary>
    /// Which edge this is, whatever else changes: kept by a copy of it, so it survives the
    /// roof's sketch being edited and an undo. What runs along an edge - a fascia, a gutter -
    /// finds it by this, not by where it comes in the outline, which changes when a line is
    /// added before it. Both halves of an edge that is split keep it.
    /// </summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Whether the roof slopes up from this edge, or is cut off at it.</summary>
    public bool DefinesSlope { get; set; }

    /// <summary>The pitch in degrees from horizontal. Revit's Slope, shown as an angle.</summary>
    public double SlopeDegrees { get; set; } = DefaultSlopeDegrees;

    /// <summary>
    /// How far above the roof's base the roof bears at this edge - Revit's "Plate Offset From
    /// Base" for an edge picked from a wall, "Offset From Roof Base" for one drawn. Lifting
    /// one eave is how a roof is made to meet a wall plate that is higher on one side.
    /// </summary>
    public double PlateOffset { get; set; }

    /// <summary>
    /// The wall this edge was picked from, or null for an edge drawn as a line. A picked edge
    /// is not a line at all but a position relative to its wall: move the wall, and the edge
    /// goes with it - which is what makes a roof follow a design change instead of having to
    /// be redrawn after every one.
    /// </summary>
    public Guid? WallId { get; set; }

    /// <summary>Which face of its wall the edge was picked on: the one to the left of the wall's drawn direction, or the right.</summary>
    public bool OnLeftOfWall { get; set; }

    /// <summary>How far the edge stands out from the wall face - the eaves' projection.</summary>
    public double Overhang { get; set; }

    /// <summary>
    /// Measure the overhang from the wall's core rather than its finish face - Revit's
    /// "Extend into wall (to core)". For a roof that should bear on the structure, not on the
    /// render.
    /// </summary>
    public bool ExtendToCore { get; set; }

    /// <summary>
    /// How far in from this edge the roof bears on its wall, which is where its plate height
    /// applies. An overhanging eave continues down past the wall to the edge, so it ends up
    /// lower than the plate by the overhang times the pitch - as a built eave does. A drawn
    /// edge bears where it is.
    /// </summary>
    public double BearingInset => WallId is null ? 0 : Math.Max(0, Overhang);

    /// <summary>
    /// The arc this edge is one straight piece of, when the outline was drawn with an arc; null
    /// for a straight edge. Every piece of one arc carries the same one, which is how the roof
    /// knows a cone is one smooth surface rather than a ring of flat faces, and how Edit
    /// Footprint gives the arc back as one line.
    /// </summary>
    public Guid? ArcId { get; set; }

    public const double DefaultSlopeDegrees = 30;

    public RoofEdge Copy() => new()
    {
        Id = Id,
        DefinesSlope = DefinesSlope,
        SlopeDegrees = SlopeDegrees,
        PlateOffset = PlateOffset,
        WallId = WallId,
        OnLeftOfWall = OnLeftOfWall,
        Overhang = Overhang,
        ExtendToCore = ExtendToCore,
        ArcId = ArcId
    };
}

/// <summary>
/// Where a roof picked from walls bears on them - Revit's Rafter or Truss. The slope starts
/// at the plate height on the wall's outside face for a truss, which is Revit's default, and
/// on its inside face for rafters, which sit on the plate across the wall; with Extend to wall
/// core, on the core's faces instead. The roof's edges stay where they were drawn: it is the
/// roof that rises or falls to start its slope there.
/// </summary>
public enum RoofBearing
{
    Truss,
    Rafter
}

/// <summary>
/// A slope arrow (Revit's Slope Arrow): a slope drawn in plan, from a tail on a line of the
/// outline to a head, for a roof whose slope is not "square up from an edge".
///
/// It is a plane of its own: level along the tail's line of equal height, rising toward the
/// head. That covers what edges cannot say - a flat roof falling diagonally to a drain, a
/// hip that starts partway up a gable end, a dormer made by lifting the middle of an eave
/// into a small gable.
///
/// The slope is given either as a pitch from the tail, or as a height at the tail and a height
/// at the head, with the pitch worked out between them - Revit's two ways of saying it.
/// Heights are above the roof's base.
/// </summary>
public sealed class RoofSlopeArrow
{
    public Point2D Tail { get; set; }

    public Point2D Head { get; set; }

    /// <summary>Revit's Specify: false for Slope, true for Height at Tail (and head).</summary>
    public bool ByHeights { get; set; }

    /// <summary>The pitch, when <see cref="ByHeights"/> is off.</summary>
    public double SlopeDegrees { get; set; } = RoofEdge.DefaultSlopeDegrees;

    /// <summary>Height of the surface at the tail, above the roof's base - Revit's Height Offset at Tail.</summary>
    public double TailOffset { get; set; }

    /// <summary>Height at the head, above the roof's base, when <see cref="ByHeights"/> is on.</summary>
    public double HeadOffset { get; set; }

    public double Length => Tail.DistanceTo(Head);

    /// <summary>Rise per unit run toward the head - negative for an arrow drawn falling.</summary>
    public double Rise => ByHeights
        ? (Length > 1e-9 ? (HeadOffset - TailOffset) / Length : 0)
        : Math.Tan(Math.Clamp(SlopeDegrees, -89.95, 89.95) * Math.PI / 180);

    /// <summary>The plane it defines, on a roof based at this height.</summary>
    public RoofPlane PlaneOn(double baseElevation) => PlaneFrom(baseElevation + TailOffset);

    /// <summary>The plane it defines, starting at this height at its tail.</summary>
    public RoofPlane PlaneFrom(double atTail)
    {
        var direction = (Head - Tail).NormalisedOrDefault(Vector2D.UnitX);
        var rise = Rise;

        return new RoofPlane(
            rise * direction.X,
            rise * direction.Y,
            atTail - rise * (direction.X * Tail.X + direction.Y * Tail.Y),
            atTail);
    }

    public RoofSlopeArrow Copy() => new()
    {
        Tail = Tail,
        Head = Head,
        ByHeights = ByHeights,
        SlopeDegrees = SlopeDegrees,
        TailOffset = TailOffset,
        HeadOffset = HeadOffset
    };
}

/// <summary>
/// A plane of a roof, written as the height over any point in plan: z = A·x + B·y + C.
///
/// Written this way because the roof is then the lowest of its planes at every point, and
/// comparing two planes is comparing two straight lines in plan - which is what makes hips,
/// valleys and ridges fall out of the arithmetic instead of having to be hunted for.
/// </summary>
public readonly record struct RoofPlane(double A, double B, double C, double Eave)
{
    public double HeightAt(Point2D point) => A * point.X + B * point.Y + C;

    /// <summary>
    /// Whether this plane is a roof over a point at all.
    ///
    /// A roof plane rises from its eave inward; it does not exist on the other side of it.
    /// Without this an L-shaped roof goes wrong: the eave facing into the inside corner would
    /// carry on past it and dive under the wing, and being the lowest plane there it would win
    /// and bury the wing under a surface below its own gutters.
    /// </summary>
    public bool CoversPoint(Point2D point) => HeightAt(point) >= Eave - 1e-6;

    /// <summary>Rise per unit run: 0 for a flat plane, 1 for 45°.</summary>
    public double Rise => Math.Sqrt(A * A + B * B);

    /// <summary>The pitch in degrees from horizontal.</summary>
    public double SlopeDegrees => Math.Atan(Rise) * 180 / Math.PI;

    /// <summary>
    /// How much deeper the build-up is measured straight down than measured square to the
    /// slope. A roof's layers are square to its surface, so a 200 mm roof at 45° is 283 mm
    /// deep in a vertical cut - and getting this wrong shows as a step at every ridge.
    /// </summary>
    public double VerticalStretch => Math.Sqrt(1 + Rise * Rise);
}

/// <summary>One face of a roof: where it is in plan, and the plane it lies in.</summary>
public sealed record RoofFacet(IReadOnlyList<Point2D> Outline, RoofPlane Plane, bool HasEave = true)
{
    /// <summary>
    /// The arc of the outline this face rises from, when it does: faces of one arc are one
    /// curved surface - a cone - built from flat pieces, drawn and shaded as one.
    /// </summary>
    public Guid? ArcId { get; init; }

    /// <summary>The face's shadow on the ground, which is what a plan measures.</summary>
    public double Area => Polygon2D.Area(Outline);

    /// <summary>The face itself, which is larger than its plan area by the slope.</summary>
    public double SlopingArea => Area * Plane.VerticalStretch;

    public IReadOnlyList<Point3D> Points3D =>
        Outline.Select(point => Point3D.On(point, Plane.HeightAt(point))).ToList();
}

/// <summary>What shape a roof has come out as, in the terms IFC and everyone else uses.</summary>
public enum RoofForm
{
    Flat,
    Shed,
    Gable,
    Hip,
    Freeform,

    /// <summary>Two pitches a side, steep below and shallow above - a roof by extrusion.</summary>
    Gambrel,

    /// <summary>A curved vault - a roof by extrusion of an arc.</summary>
    Barrel,

    /// <summary>A cone - a roof rising all round from a circle, or from arcs.</summary>
    Conical
}

/// <summary>
/// The surface of a roof built from a footprint: its faces, the lines where they meet, and the
/// height over any point.
///
/// The roof is the <em>lowest</em> of the planes its eaves define. Take a rectangle with all
/// four edges sloping: each plane rises inward, and at any point the roof is the first one you
/// would meet coming down - so the four planes cut each other into a hip roof without hips
/// ever being mentioned. Leave the two ends out and the remaining planes run on until they
/// meet in a ridge over them, which is a gable roof. A valley on the inside corner of an
/// L-shaped footprint is the same rule once more.
///
/// This is the lower envelope of the eave planes. It agrees with a straight skeleton - the way
/// IfcOpenShell's roof tool builds one - wherever the pitches are equal, and unlike a skeleton
/// it keeps working when they are not.
/// </summary>
public sealed class RoofSurface
{
    private readonly List<RoofPlane> _planes;

    internal RoofSurface(
        double baseElevation,
        List<RoofPlane> planes,
        IReadOnlyList<RoofFacet> facets,
        IReadOnlyList<(Point2D From, Point2D To)> breakLines,
        RoofForm form,
        double? cutoff = null)
    {
        BaseElevation = baseElevation;
        _planes = planes;
        Facets = facets;
        BreakLines = breakLines;
        Form = form;
        Cutoff = cutoff;
    }

    /// <summary>The height the roof stops at, flat on top, or null where it runs to its ridge.</summary>
    public double? Cutoff { get; }

    /// <summary>The height the roof is built up from: its level, plus its base offset.</summary>
    public double BaseElevation { get; }

    public IReadOnlyList<RoofFacet> Facets { get; }

    /// <summary>
    /// The lines inside the footprint where two faces meet - ridges, hips and valleys. What a
    /// plan of a pitched roof is mostly made of.
    /// </summary>
    public IReadOnlyList<(Point2D From, Point2D To)> BreakLines { get; }

    public RoofForm Form { get; }

    public bool IsFlat => _planes.Count == 0;

    /// <summary>
    /// The upper surface over a point in plan.
    ///
    /// Read off the face the point is on rather than from the planes, because a plane on its
    /// own says nothing about where it stops: the faces are the roof, and a point just outside
    /// one - on an eave, or on a ridge - belongs to the nearest.
    /// </summary>
    public double HeightAt(Point2D point)
    {
        if (FaceAt(point) is not { } on) return BaseElevation;

        var height = on.Plane.HeightAt(point);
        return Cutoff is { } top ? Math.Min(height, top) : height;
    }

    /// <summary>The face a point in plan is on, or the nearest one to it; null on a roof with no faces.</summary>
    public RoofFacet? FaceAt(Point2D point)
    {
        RoofFacet? on = null;
        var nearest = double.MaxValue;

        foreach (var facet in Facets)
        {
            var away = Polygon2D.Contains(facet.Outline, point) ? 0 : DistanceTo(facet.Outline, point);
            if (away >= nearest) continue;

            nearest = away;
            on = facet;

            if (away <= 0) break;
        }

        return on;
    }

    private static double DistanceTo(IReadOnlyList<Point2D> outline, Point2D point)
    {
        var nearest = double.MaxValue;

        for (var i = 0; i < outline.Count; i++)
            nearest = Math.Min(nearest, Line2D.DistanceFromSegment(point, outline[i], outline[(i + 1) % outline.Count]));

        return nearest;
    }

    /// <summary>The top of the roof - the ridge, on a roof that has one.</summary>
    public double RidgeElevation =>
        Facets.Count == 0
            ? BaseElevation
            : Facets.Max(facet => facet.Points3D.Max(point => point.Z));

    /// <summary>The area of the roof surface itself, not of its shadow on the ground.</summary>
    public double SlopingArea => Facets.Sum(facet => facet.SlopingArea);
}

/// <summary>
/// Builds a roof from its footprint and what each edge does - Revit's roof by footprint.
/// </summary>
public static class RoofShape
{
    /// <summary>Areas under this are the leftovers of clipping rather than faces, in mm².</summary>
    private const double MinimumFacetArea = 1;

    /// <summary>Planes this close in every term are the same plane.</summary>
    private const double SamePlane = 1e-9;

    /// <summary>
    /// The surface of a roof by extrusion: one face under each segment of the profile, the
    /// width of the roof across, with a ridge or valley line wherever the profile turns.
    /// </summary>
    public static RoofSurface BuildExtrusion(RoofExtrusion extrusion, double baseElevation)
    {
        if (!extrusion.IsValid)
        {
            return new RoofSurface(baseElevation, new List<RoofPlane>(), Array.Empty<RoofFacet>(),
                Array.Empty<(Point2D, Point2D)>(), RoofForm.Flat);
        }

        var planes = new List<RoofPlane>();
        var facets = new List<RoofFacet>();
        var profile = extrusion.Points;

        for (var i = 0; i + 1 < profile.Count; i++)
        {
            var plane = extrusion.PlaneUnder(i, baseElevation);
            var outline = new List<Point2D>
            {
                extrusion.At(profile[i].X, extrusion.Start), extrusion.At(profile[i + 1].X, extrusion.Start),
                extrusion.At(profile[i + 1].X, extrusion.End), extrusion.At(profile[i].X, extrusion.End)
            };

            planes.Add(plane);
            facets.Add(new RoofFacet(Anticlockwise(outline), plane, HasEave: false));
        }

        // The profile's own corners, carried across the roof: its ridges and valleys. Not the
        // joins part way round an arc - a vault is one curved surface, not a row of strips.
        var breakLines = Enumerable.Range(1, Math.Max(0, profile.Count - 2))
            .Where(i => !extrusion.IsSmooth(i))
            .Select(i => (extrusion.At(profile[i].X, extrusion.Start), extrusion.At(profile[i].X, extrusion.End)))
            .ToList();

        var form = extrusion.Form();
        return new RoofSurface(baseElevation, form == RoofForm.Flat ? new List<RoofPlane>() : planes, facets, breakLines, form);
    }

    public static RoofSurface Build(
        IReadOnlyList<Point2D> boundary, IReadOnlyList<RoofEdge> edges, double baseElevation,
        double? cutoff = null, IReadOnlyList<RoofSlopeArrow>? arrows = null, Func<RoofEdge, double>? bearing = null)
    {
        var footprint = Anticlockwise(boundary);
        arrows ??= Array.Empty<RoofSlopeArrow>();

        if (footprint.Count < 3)
        {
            return new RoofSurface(baseElevation, new List<RoofPlane>(), Array.Empty<RoofFacet>(),
                Array.Empty<(Point2D, Point2D)>(), RoofForm.Flat);
        }

        var eaves = EavesOf(footprint, boundary, edges, baseElevation, arrows, bearing);
        var planes = eaves.Select(eave => eave.Plane).ToList();

        // Nothing slopes: the roof is its footprint, flat - which is what a roof is until
        // somebody says otherwise.
        if (planes.Count == 0)
        {
            var sheet = new RoofFacet(footprint, new RoofPlane(0, 0, baseElevation, baseElevation));
            return new RoofSurface(baseElevation, planes, new[] { sheet },
                Array.Empty<(Point2D, Point2D)>(), RoofForm.Flat);
        }

        var whole = new List<PolygonBoolean.Region>
        {
            new(footprint, Array.Empty<IReadOnlyList<Point2D>>())
        };

        // Where each eave is building roof at all: inside the footprint, on the inward side of
        // its own line, and within the wedge its corners leave it. Everything else is worked
        // out inside these.
        var covers = eaves
            .Select(eave =>
            {
                // A slope arrow has no eave to be on the inside of: its plane runs on below its
                // tail wherever nothing else is the roof - a flat roof sloped diagonally is one
                // plane from corner to corner, lower behind the tail and higher ahead.
                var ground = eave.IsArrow
                    ? whole
                    : Clip(whole, new RoofPlane(0, 0, eave.Plane.Eave, eave.Plane.Eave), eave.Plane, footprint);

                foreach (var (other, keepWhereLower) in eave.Wedges)
                {
                    ground = keepWhereLower
                        ? Clip(ground, eave.Plane, other, footprint)
                        : Clip(ground, other, eave.Plane, footprint);
                }

                return ground;
            })
            .ToList();

        // Cut off above a height: the roof stops there and is flat on top, which is how a
        // dutch gable is made - a hip taken up to the gablet's eaves, and the gablet built on
        // the deck it leaves.
        var deck = cutoff is { } top ? new RoofPlane(0, 0, top, top) : (RoofPlane?)null;

        if (deck is { } level)
            for (var i = 0; i < covers.Count; i++)
                covers[i] = Clip(covers[i], planes[i], level, footprint);

        var facets = new List<RoofFacet>();

        for (var i = 0; i < planes.Count; i++)
        {
            // This plane is the roof where no other plane that covers the same ground is lower
            // than it. Said the other way round - and this is how it is worked out - take the
            // ground this plane covers and cut away, for each of the others, the part of the
            // other's ground where the other is the lower of the two.
            var region = covers[i];

            for (var j = 0; j < planes.Count && region.Count > 0; j++)
            {
                if (j == i || Same(planes[j], planes[i])) continue;

                foreach (var under in Clip(covers[j], planes[j], planes[i], footprint))
                    region = Subtract(region, under);
            }

            foreach (var piece in region)
                if (Polygon2D.Area(piece.Outer) > MinimumFacetArea)
                    facets.Add(new RoofFacet(piece.Outer, planes[i], HasEave: !eaves[i].IsArrow) { ArcId = eaves[i].ArcId });
        }

        facets = MergeSamePlanes(facets);

        // Whatever the slopes no longer reach is the flat deck the cutoff leaves.
        if (deck is { } flat)
        {
            var left = new List<PolygonBoolean.Region> { new(footprint, Array.Empty<IReadOnlyList<Point2D>>()) };

            foreach (var facet in facets.ToList())
                left = Subtract(left, new PolygonBoolean.Region(facet.Outline, Array.Empty<IReadOnlyList<Point2D>>()));

            foreach (var piece in left)
                if (Polygon2D.Area(piece.Outer) > MinimumFacetArea)
                    facets.Add(new RoofFacet(piece.Outer, flat));
        }

        return new RoofSurface(
            baseElevation, planes, facets, BreakLinesOf(facets, footprint), FormOf(boundary, edges, arrows.Count), cutoff);
    }

    /// <summary>
    /// The eave planes, one for each edge that defines slope. Edges describing the same plane -
    /// a run of collinear segments at one pitch - are counted once, or each would clip the
    /// others away to nothing.
    /// </summary>
    /// <summary>
    /// One eave: the plane it carries, and the wedge it is allowed to work in.
    ///
    /// The wedge is what keeps an L-shaped roof honest. A plane on its own goes on for ever,
    /// and the plane of a wing's eave, extended, passes underneath the main range - lower than
    /// the range's own roof, so on "the lowest plane wins" it would take a slice out of it,
    /// metres away from any wall the wing has. A real eave does not reach there: its stretch of
    /// roof is bounded at each end by where it meets the eave beside it, and that meeting is a
    /// straight line through the corner they share. Outside those two lines the eave is not
    /// building anything.
    ///
    /// Which side of the line belongs to it is the side its own edge is on. At an ordinary
    /// corner that is where it is the lower, and the two meet in a hip; at an inside corner it
    /// is where it is the higher, and they meet in a valley - a gutter, not a ridge. Asked that
    /// way rather than by the corner's shape, the rule also holds for a slope arrow, whose plane
    /// does not rise square to any edge: the two arrows of a dormer keep the stretch of eave
    /// between them, and the eave either side keeps its own.
    /// </summary>
    private readonly record struct Eave(
        RoofPlane Plane, IReadOnlyList<(RoofPlane Other, bool KeepWhereLower)> Wedges, bool IsArrow = false, Guid? ArcId = null);

    private static List<Eave> EavesOf(
        IReadOnlyList<Point2D> footprint,
        IReadOnlyList<Point2D> boundary,
        IReadOnlyList<RoofEdge> edges,
        double baseElevation,
        IReadOnlyList<RoofSlopeArrow> arrows, Func<RoofEdge, double>? bearing)
    {
        // The footprint is worked anticlockwise so that "inward" is one rule rather than two.
        // Where the points had to be turned round to get there, the edge settings turn with
        // them: reversing a closed loop takes edge i to edge (n - 2 - i).
        var reversed = !ReferenceEquals(footprint, boundary);
        var count = footprint.Count;
        var planes = new RoofPlane?[count];

        for (var i = 0; i < count; i++)
        {
            var edge = EdgeFor(edges, count, reversed, i);
            if (edge is not { DefinesSlope: true }) continue;

            var from = footprint[i];
            var to = footprint[(i + 1) % count];
            var along = (to - from).NormalisedOrDefault(default);
            if (along.Length <= 0) continue;

            var pitch = Math.Clamp(edge.SlopeDegrees, 0.05, 89.95);
            var rise = Math.Tan(pitch * Math.PI / 180);

            // Wound anticlockwise, the inside of the footprint is to the left of every edge,
            // so this is the direction the roof climbs in.
            var inward = along.PerpendicularLeft();

            // The plate height is where the roof bears - on the wall face, for an edge picked
            // from a wall - and the overhang runs on down past it to the edge itself.
            var plate = baseElevation + edge.PlateOffset;
            var inset = bearing?.Invoke(edge) ?? edge.BearingInset;
            var atEdge = plate - rise * inset;

            planes[i] = new RoofPlane(
                rise * inward.X,
                rise * inward.Y,
                atEdge - rise * (inward.X * from.X + inward.Y * from.Y),
                atEdge);
        }

        // The slope arrows, each on the edge its tail sits on. An arrow whose tail is on no
        // edge has nothing to belong to, and is ignored rather than guessed about.
        var arrowPlanes = new List<(RoofPlane Plane, int Edge, Point2D Tail)>();
        foreach (var arrow in arrows)
        {
            if (arrow.Length <= 1) continue;
            if (EdgeUnder(footprint, arrow, index => planes[index] is not null) is not { } edge) continue;

            // An arrow starting at a corner where a sloping eave ends starts at that eave's
            // height there, not at the roof's base: a dormer continues the eave it is cut into.
            // An eave picked with an overhang is lower at its edge than the base, by the
            // overhang times the pitch, and an arrow starting at the base would stand above it
            // by that much - a step in the eave, and a dormer that never meets the roof.
            var start = footprint[edge];
            var end = footprint[(edge + 1) % count];
            var beside = arrow.Tail.DistanceTo(start) <= 1 ? planes[(edge - 1 + count) % count]
                : arrow.Tail.DistanceTo(end) <= 1 ? planes[(edge + 1) % count]
                : null;

            var atTail = (beside?.HeightAt(arrow.Tail) ?? baseElevation) + arrow.TailOffset;
            arrowPlanes.Add((arrow.PlaneFrom(atTail), edge, arrow.Tail));
        }

        // The plane that meets this edge at one of its corners: the edge's own, where it slopes,
        // or else the arrow on it whose tail is nearest that corner - which is how the two
        // arrows of a dormer each meet the eave on their own side of it.
        RoofPlane? PlaneAt(int edge, Point2D corner)
        {
            if (planes[edge] is { } own) return own;

            return arrowPlanes
                .Where(arrow => arrow.Edge == edge)
                .OrderBy(arrow => arrow.Tail.DistanceTo(corner))
                .Select(arrow => (RoofPlane?)arrow.Plane)
                .FirstOrDefault();
        }

        List<(RoofPlane, bool)> WedgesFor(RoofPlane plane, int i, bool isArrow = false)
        {
            var wedges = new List<(RoofPlane, bool)>();
            var from = footprint[i];
            var to = footprint[(i + 1) % count];
            var along = (to - from).NormalisedOrDefault(default);
            var inward = along.PerpendicularLeft();
            var step = Math.Min(from.DistanceTo(to) / 4, 10);

            // The corner at each end of this edge, with the edge beside it. An edge the roof
            // is merely cut off at has nothing to meet, and so sets no wedge: the roof runs past
            // a gable to the wall and stops there.
            foreach (var (neighbour, cornerIndex, corner, awayAlong) in new[]
                     {
                         ((i - 1 + count) % count, i, from, along),
                         ((i + 1) % count, (i + 1) % count, to, along * -1)
                     })
            {
                if (PlaneAt(neighbour, corner) is not { } beside || Same(plane, beside)) continue;

                // An arrow meets the eave only at the corner its tail is nearest. The two arrows
                // of a dormer each own one end of the stretch between them; the far end belongs
                // to the other.
                if (isArrow && PlaneAt(i, corner) is { } nearest && !Same(nearest, plane)) continue;

                // Where the two planes meet at the corner itself, the line between them runs
                // out of it, and this edge's roof is on the side of that line this edge is on. A
                // point just in from the corner, along this edge, says which side that is - so
                // the rule holds for any two planes, not only two eaves rising square to their
                // edges: it is what gives a dormer's arrows their stretch of eave.
                if (Math.Abs(plane.HeightAt(corner) - beside.HeightAt(corner)) < 1)
                {
                    var probe = corner + awayAlong * (2 * step) + inward * step;
                    var difference = plane.HeightAt(probe) - beside.HeightAt(probe);
                    if (Math.Abs(difference) < 1e-9) continue;

                    wedges.Add((beside, difference < 0));
                    continue;
                }

                // Where one starts higher than the other - a raised end eave, a hip begun
                // partway up a gable - the line misses the corner, and near it this edge's roof
                // is not there at all. Then the corner's shape decides: out, each keeps where it
                // is the lower; in, where it is the higher.
                wedges.Add((beside, IsConvex(footprint, cornerIndex)));
            }

            return wedges;
        }

        var eaves = new List<Eave>();

        for (var i = 0; i < count; i++)
            if (planes[i] is { } plane)
                eaves.Add(new Eave(plane, WedgesFor(plane, i), ArcId: EdgeFor(edges, count, reversed, i)?.ArcId));

        foreach (var (plane, edge, _) in arrowPlanes)
            eaves.Add(new Eave(plane, WedgesFor(plane, edge, isArrow: true), IsArrow: true));

        return eaves;
    }

    /// <summary>
    /// Joins faces that lie in one plane into one face.
    ///
    /// Two edges in line at the same pitch - an eave split to make room for a dormer - give
    /// the same plane twice, and each keeps the roof it would have on its own. Left apart, the
    /// stretch they share would be built twice, one copy on top of the other, and counted
    /// twice in the roof's area. It is one surface, so it becomes one face.
    /// </summary>
    private static List<RoofFacet> MergeSamePlanes(List<RoofFacet> facets)
    {
        var merged = new List<RoofFacet>();

        foreach (var group in GroupByPlane(facets))
        {
            if (group.Count == 1)
            {
                merged.Add(group[0]);
                continue;
            }

            var regions = new List<IReadOnlyList<Point2D>> { group[0].Outline };

            foreach (var facet in group.Skip(1))
            {
                var joined = new List<IReadOnlyList<Point2D>>();
                var pending = facet.Outline;

                foreach (var region in regions)
                {
                    var union = PolygonBoolean.Combine(region, pending, BooleanOperation.Union);

                    // Touching or overlapping: one region now, carried on to the next.
                    if (union.Count == 1 && pending is not null)
                    {
                        pending = union[0].Outer;
                        continue;
                    }

                    joined.Add(region);
                }

                joined.Add(pending!);
                regions = joined;
            }

            merged.AddRange(regions
                .Where(region => Polygon2D.Area(region) > MinimumFacetArea)
                .Select(region => new RoofFacet(region, group[0].Plane, group[0].HasEave) { ArcId = group[0].ArcId }));
        }

        return merged;
    }

    private static List<List<RoofFacet>> GroupByPlane(List<RoofFacet> facets)
    {
        var groups = new List<List<RoofFacet>>();

        foreach (var facet in facets)
        {
            var group = groups.FirstOrDefault(existing => Same(existing[0].Plane, facet.Plane));
            if (group is null) groups.Add(new List<RoofFacet> { facet });
            else group.Add(facet);
        }

        return groups;
    }

    /// <summary>Whether the footprint turns left at this corner - anticlockwise, a corner sticking out.</summary>
    private static bool IsConvex(IReadOnlyList<Point2D> footprint, int corner)
    {
        var count = footprint.Count;
        var into = footprint[corner] - footprint[(corner - 1 + count) % count];
        var away = footprint[(corner + 1) % count] - footprint[corner];

        return into.Cross(away) >= 0;
    }

    /// <summary>
    /// The edge of the footprint an arrow's tail is on, within a millimetre; null when on none.
    ///
    /// A tail on a corner is on two edges. The one it belongs to is the one that does not
    /// slope - an arrow gives a slope to a line that has none, which is Revit's rule too - and
    /// between two alike, the one the arrow runs along. That is what puts both arrows of a
    /// dormer on the stretch of eave between them, rather than one of them on the eave beside.
    /// </summary>
    private static int? EdgeUnder(IReadOnlyList<Point2D> footprint, RoofSlopeArrow arrow, Func<int, bool> slopes)
    {
        var direction = (arrow.Head - arrow.Tail).NormalisedOrDefault(default);

        return Enumerable.Range(0, footprint.Count)
            .Where(i => Line2D.DistanceFromSegment(arrow.Tail, footprint[i], footprint[(i + 1) % footprint.Count]) <= 1)
            .OrderBy(i => slopes(i) ? 1 : 0)
            .ThenByDescending(i => Math.Abs((footprint[(i + 1) % footprint.Count] - footprint[i])
                .NormalisedOrDefault(default).Dot(direction)))
            .Select(i => (int?)i)
            .FirstOrDefault();
    }

    private static bool Same(RoofPlane a, RoofPlane b) =>
        Math.Abs(a.A - b.A) < SamePlane && Math.Abs(a.B - b.B) < SamePlane && Math.Abs(a.C - b.C) < SamePlane;

    private static RoofEdge? EdgeFor(IReadOnlyList<RoofEdge> edges, int count, bool reversed, int index)
    {
        if (edges.Count == 0 || count == 0) return null;

        var wanted = reversed ? ((count - 2 - index) % count + count) % count : index;
        return wanted < edges.Count ? edges[wanted] : null;
    }

    /// <summary>Takes one region away from a set of them.</summary>
    internal static List<PolygonBoolean.Region> Subtract(
        List<PolygonBoolean.Region> regions, PolygonBoolean.Region cutter)
    {
        var left = new List<PolygonBoolean.Region>();

        foreach (var region in regions)
            left.AddRange(PolygonBoolean.Combine(Loops(region), Loops(cutter), BooleanOperation.Difference));

        return left;
    }

    private static IReadOnlyList<IReadOnlyList<Point2D>> Loops(PolygonBoolean.Region region)
    {
        var loops = new List<IReadOnlyList<Point2D>> { region.Outer };
        loops.AddRange(region.Holes);
        return loops;
    }

    /// <summary>
    /// Cuts regions down to where one plane is at or below another. Both are straight lines in
    /// plan, so the cut is by a half-plane - taken here as a rectangle far larger than the
    /// roof, so that the general polygon cutter can do it and any holes survive.
    /// </summary>
    internal static List<PolygonBoolean.Region> Clip(
        List<PolygonBoolean.Region> regions, RoofPlane plane, RoofPlane other, IReadOnlyList<Point2D> footprint)
    {
        var a = plane.A - other.A;
        var b = plane.B - other.B;
        var c = plane.C - other.C;
        var length = Math.Sqrt(a * a + b * b);

        // Parallel planes: one is under the other everywhere, or nowhere.
        if (length < 1e-12) return c <= SamePlane ? regions : new List<PolygonBoolean.Region>();

        var normal = new Vector2D(a / length, b / length);
        var offset = -c / length;

        // Far enough that the rectangle swallows the whole roof however far off the cutting
        // line itself runs.
        var reach = Reach(footprint) + Math.Abs(offset);
        var side = normal.PerpendicularLeft();
        var on = new Point2D(normal.X * offset, normal.Y * offset);

        var rectangle = new List<Point2D>
        {
            on + side * reach,
            on + side * reach - normal * (2 * reach),
            on - side * reach - normal * (2 * reach),
            on - side * reach
        };

        var cut = new List<PolygonBoolean.Region>();

        foreach (var region in regions)
        {
            var loops = new List<IReadOnlyList<Point2D>> { region.Outer };
            loops.AddRange(region.Holes);
            cut.AddRange(PolygonBoolean.Combine(loops, new[] { rectangle }, BooleanOperation.Intersection));
        }

        return cut;
    }

    /// <summary>A distance certain to cover the whole roof, for the cutting rectangles.</summary>
    private static double Reach(IReadOnlyList<Point2D> footprint)
    {
        var far = footprint.Max(point => Math.Abs(point.X) + Math.Abs(point.Y));
        return Math.Max(far * 4, 1000);
    }

    /// <summary>
    /// The lines where faces meet: every face edge that is not on the footprint. Each of them
    /// belongs to two faces, so they are collected once.
    /// </summary>
    private static IReadOnlyList<(Point2D From, Point2D To)> BreakLinesOf(
        IReadOnlyList<RoofFacet> facets, IReadOnlyList<Point2D> footprint)
    {
        var lines = new List<(Point2D From, Point2D To)>();
        var seen = new List<((Point2D From, Point2D To) Line, RoofFacet Facet)>();

        foreach (var facet in facets)
        {
            for (var i = 0; i < facet.Outline.Count; i++)
            {
                var from = facet.Outline[i];
                var to = facet.Outline[(i + 1) % facet.Outline.Count];

                if (from.DistanceTo(to) < 1) continue;
                if (OnBoundary(footprint, from.MidpointTo(to))) continue;

                // The line between two faces of a cone is not a hip: the roof is one curved
                // surface there, and is drawn without a line down every join - including where
                // one arc runs smoothly on into the next, as a circle's two halves do.
                var twin = seen.FindIndex(entry => Same(entry.Line, (from, to)));
                if (twin >= 0)
                {
                    if (Smooth(facet, seen[twin].Facet)) lines.RemoveAll(line => Same(line, (from, to)));
                    continue;
                }

                seen.Add(((from, to), facet));
                lines.Add((from, to));
            }
        }

        return lines;
    }

    /// <summary>
    /// Whether two faces meet as one curved surface: both rise from arcs, at the same pitch,
    /// facing nearly the same way - a turn no bigger than the steps an arc is built in.
    /// </summary>
    private static bool Smooth(RoofFacet a, RoofFacet b)
    {
        if (a.ArcId is null || b.ArcId is null) return false;
        if (a.ArcId == b.ArcId) return true;

        var (riseA, riseB) = (a.Plane.Rise, b.Plane.Rise);
        if (riseA < 1e-9 || riseB < 1e-9 || Math.Abs(riseA - riseB) > 0.02 * Math.Max(riseA, riseB)) return false;

        var turn = Math.Acos(Math.Clamp((a.Plane.A * b.Plane.A + a.Plane.B * b.Plane.B) / (riseA * riseB), -1, 1));
        return turn <= 1.5 * RoofSketch.MaxFacetTurn;
    }

    private static bool OnBoundary(IReadOnlyList<Point2D> footprint, Point2D point)
    {
        for (var i = 0; i < footprint.Count; i++)
        {
            var from = footprint[i];
            var to = footprint[(i + 1) % footprint.Count];
            if (Line2D.DistanceFromSegment(point, from, to) < 0.5) return true;
        }

        return false;
    }

    private static bool Same((Point2D From, Point2D To) a, (Point2D From, Point2D To) b) =>
        (a.From.DistanceTo(b.From) < 0.5 && a.To.DistanceTo(b.To) < 0.5) ||
        (a.From.DistanceTo(b.To) < 0.5 && a.To.DistanceTo(b.From) < 0.5);

    /// <summary>
    /// What the roof would be called. The name follows from how many edges slope and where,
    /// which is also what IFC asks to be told.
    /// </summary>
    public static RoofForm FormOf(IReadOnlyList<Point2D> boundary, IReadOnlyList<RoofEdge> edges, int arrows = 0)
    {
        var counted = Math.Min(boundary.Count, edges.Count);
        var sloping = edges.Take(counted).Count(edge => edge.DefinesSlope);

        // A slope arrow on a roof with nothing else sloping makes the whole roof one plane - a
        // shed, however the arrow is drawn. Arrows among sloping edges make something with no
        // single name.
        if (arrows > 0) return sloping == 0 && arrows == 1 ? RoofForm.Shed : RoofForm.Freeform;

        if (sloping == 0) return RoofForm.Flat;
        if (sloping == 1) return RoofForm.Shed;

        // Rising all round from arcs alone - a circle, or a round end and nothing else: a cone.
        if (sloping == counted && edges.Take(counted).All(edge => edge.ArcId is not null)) return RoofForm.Conical;

        if (sloping == counted) return RoofForm.Hip;

        // Two opposite sides of a four-sided footprint sloping and the ends left open: a gable.
        if (counted == 4 && sloping == 2 &&
            edges[0].DefinesSlope == edges[2].DefinesSlope &&
            edges[1].DefinesSlope == edges[3].DefinesSlope)
        {
            return RoofForm.Gable;
        }

        return RoofForm.Freeform;
    }

    private static IReadOnlyList<Point2D> Anticlockwise(IReadOnlyList<Point2D> loop) =>
        Polygon2D.SignedArea(loop) >= 0 ? loop : loop.Reverse().ToList();
}
