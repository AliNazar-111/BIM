using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Parameters;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// A roof built from a footprint (specification section 3.3; Revit's Roof by Footprint,
/// IFC's IfcRoof).
///
/// The footprint is a slab's outline, but a roof is not a flat slab: each edge of the outline
/// either defines a slope - an eave the roof rises from - or does not, in which case the roof
/// runs past it and is cut off there. That one setting per edge is the whole of a footprint
/// roof: all four edges sloping is a hip, two opposite ones a gable, one a shed, none a flat
/// roof. Everything else - ridges, hips, valleys, the height at any point, the area of the
/// actual sloping surface - follows from it and is never stored.
///
/// IFC calls the result IfcRoof with a PredefinedType (HIP_ROOF, GABLE_ROOF, SHED_ROOF,
/// FLAT_ROOF); it is deliberately not an IfcSlab, because a pitched roof is not a slab and
/// every quantity downstream would inherit the lie.
/// </summary>
public sealed class Roof : Slab
{
    private readonly List<RoofEdge> _edges = new();

    public override BuiltInCategory Category => BuiltInCategory.Roofs;

    /// <summary>One per boundary segment, in the order the boundary points are stored.</summary>
    public IReadOnlyList<RoofEdge> Edges => _edges;

    public override void SetBoundary(IEnumerable<Point2D> points)
    {
        base.SetBoundary(points);
        MatchEdgesToBoundary();
    }

    /// <summary>
    /// For a roof by extrusion, its profile and how far it runs; null for a roof by footprint.
    /// An extruded roof's outline in plan is not drawn but follows from this - the profile's
    /// width by the extrusion's depth - so the two can never disagree.
    /// </summary>
    public RoofExtrusion? Extrusion { get; private set; }

    public bool IsExtrusion => Extrusion is not null;

    /// <summary>Makes this a roof by extrusion with this profile, or a roof by footprint again with null.</summary>
    public void SetExtrusion(RoofExtrusion? extrusion)
    {
        Extrusion = extrusion;
        if (extrusion is not null && extrusion.Footprint() is { Count: >= 3 } footprint) SetBoundary(footprint);
    }

    /// <summary>Replaces every edge's settings, as reading a file or copying a roof does.</summary>
    public void SetEdges(IEnumerable<RoofEdge> edges)
    {
        _edges.Clear();
        _edges.AddRange(edges);
        MatchEdgesToBoundary();
    }

    /// <summary>
    /// Keeps one setting per boundary segment. A new segment starts as a plain cut-off edge:
    /// a roof that grew an edge should not silently grow a slope with it.
    /// </summary>
    private void MatchEdgesToBoundary()
    {
        while (_edges.Count < Boundary.Count) _edges.Add(new RoofEdge());
        while (_edges.Count > Boundary.Count) _edges.RemoveAt(_edges.Count - 1);
    }

    private readonly List<RoofSlopeArrow> _arrows = new();

    /// <summary>
    /// The slope arrows drawn in the roof's sketch - slopes that are not square up from an
    /// edge: a fall across a flat roof, a hip partway up a gable, a dormer lifted out of an eave.
    /// </summary>
    public IReadOnlyList<RoofSlopeArrow> SlopeArrows => _arrows;

    /// <summary>Replaces the slope arrows, as finishing a sketch, reading a file or copying a roof does.</summary>
    public void SetSlopeArrows(IEnumerable<RoofSlopeArrow> arrows)
    {
        _arrows.Clear();
        _arrows.AddRange(arrows);
    }

    /// <summary>What this roof is: a hip, a gable, a shed, flat, or something of its own.</summary>
    public RoofForm Form => Extrusion?.Form() ?? RoofShape.FormOf(Boundary, _edges, _arrows.Count);

    /// <summary>
    /// The pitch the sloping edges are laid at, or 0 where none of them slope. Setting it
    /// changes every sloping edge together, which is what Revit's Slope property does; on a
    /// roof with no sloping edge at all, it makes every edge slope, since a pitch that nothing
    /// is pitched at would be a number with no effect.
    /// </summary>
    public double SlopeDegrees
    {
        get => _edges.FirstOrDefault(edge => edge.DefinesSlope)?.SlopeDegrees ?? 0;
        set
        {
            var pitch = Math.Clamp(value, 0, 89.9);
            if (pitch <= 0)
            {
                foreach (var edge in _edges) edge.DefinesSlope = false;
                return;
            }

            var any = _edges.Any(edge => edge.DefinesSlope);
            foreach (var edge in _edges)
            {
                if (!any) edge.DefinesSlope = true;
                if (edge.DefinesSlope) edge.SlopeDegrees = pitch;
            }
        }
    }

    /// <summary>
    /// Sets the roof to one of the shapes with a name, by choosing which edges slope.
    ///
    /// A gable or a shed has to pick edges, and it picks by length: a ridge runs the long way
    /// down a building, so the long sides are the ones that slope and the short ends are left
    /// open. It is a starting point - any edge can be changed afterwards on its own.
    /// </summary>
    public void SetShape(RoofForm form)
    {
        if (_edges.Count == 0) return;

        var pitch = SlopeDegrees > 0 ? SlopeDegrees : RoofEdge.DefaultSlopeDegrees;
        foreach (var edge in _edges) edge.SlopeDegrees = pitch;

        switch (form)
        {
            case RoofForm.Flat:
                foreach (var edge in _edges) edge.DefinesSlope = false;
                break;

            case RoofForm.Hip:
                foreach (var edge in _edges) edge.DefinesSlope = true;
                break;

            case RoofForm.Shed:
                var longest = LongestEdge();
                for (var i = 0; i < _edges.Count; i++) _edges[i].DefinesSlope = i == longest;
                break;

            case RoofForm.Gable:
                var opposite = OppositeOf(LongestEdge());
                for (var i = 0; i < _edges.Count; i++) _edges[i].DefinesSlope = i == LongestEdge() || i == opposite;
                break;
        }
    }

    private int LongestEdge()
    {
        var best = 0;
        var length = -1.0;

        for (var i = 0; i < Boundary.Count; i++)
        {
            var span = Boundary[i].DistanceTo(Boundary[(i + 1) % Boundary.Count]);
            if (span <= length) continue;

            length = span;
            best = i;
        }

        return best;
    }

    /// <summary>
    /// The edge facing the given one: the one running most nearly the opposite way. On a
    /// rectangle that is the far side; on anything else it is the best available answer, and
    /// the user can say otherwise edge by edge.
    /// </summary>
    private int OppositeOf(int index)
    {
        if (Boundary.Count < 4) return -1;

        var along = (Boundary[(index + 1) % Boundary.Count] - Boundary[index]).NormalisedOrDefault(default);
        var best = -1;
        var facing = -0.5;

        for (var i = 0; i < Boundary.Count; i++)
        {
            if (i == index) continue;

            var other = (Boundary[(i + 1) % Boundary.Count] - Boundary[i]).NormalisedOrDefault(default);
            var against = -along.Dot(other);
            if (against <= facing) continue;

            facing = against;
            best = i;
        }

        return best;
    }

    /// <summary>
    /// How far above its base the roof stops, flat on top, or 0 to let it run to its ridge -
    /// Revit's Cutoff Level.
    ///
    /// It is what a dutch gable is made of: a hip roof cut off at the height the gablet's
    /// eaves want to be, with the gablet built on the deck that leaves. Without it the two
    /// roofs pass through each other, and the hip nobody can see is still in the quantities.
    /// </summary>
    public double CutoffOffset { get; set; }

    /// <summary>
    /// The level the cutoff is measured from - Revit's Cutoff Level. Null measures it from the
    /// roof's own base instead, which is how a roof with a cutoff and no level to hang it on
    /// was always kept.
    /// </summary>
    public Guid? CutoffLevelId { get; set; }

    /// <summary>
    /// Where the roof stops, flat on top: the cutoff level plus the offset, or the base plus
    /// the offset when no level is set. Null when the roof runs to its ridge.
    /// </summary>
    public double? CutoffElevation(BimDocument document)
    {
        if (CutoffLevelId is { } id && document.FindLevel(id) is { } level) return level.Elevation + CutoffOffset;
        return CutoffOffset > 0 ? BaseElevation(document) + CutoffOffset : null;
    }

    /// <summary>
    /// How the eaves are cut - Revit's Rafter Cut. Plumb by default: straight down at the
    /// roof's edge, the way most eaves end.
    /// </summary>
    public RafterCut RafterCut { get; set; } = RafterCut.PlumbCut;

    /// <summary>
    /// How deep the upright part of a two-cut eave is - Revit's Fascia Depth. Between nothing
    /// and the roof's thickness; the rest of the eave is cut away level underneath, which is
    /// where a soffit board goes.
    /// </summary>
    public double FasciaDepth { get; set; } = DefaultFasciaDepth;

    public const double DefaultFasciaDepth = 150;

    /// <summary>The Cutoff Level choices that are not levels.</summary>
    public const string NoCutoff = "None";
    public const string RoofBase = "Roof Base";

    /// <summary>
    /// Where the roof bears: its level plus its base offset. The roof's <em>underside</em> is
    /// here where it sits on its walls, and its build-up rises from it.
    ///
    /// This is the other way up from a floor, on purpose, and it is how Revit does it. A floor
    /// is set out by the surface people walk on and hangs down from it; a roof is set out by
    /// what carries it. Put a roof's base at the top of the walls and it sits on them, however
    /// thick its build-up turns out to be.
    /// </summary>
    public double BaseElevation(BimDocument document) =>
        (document.FindLevel(LevelId)?.Elevation ?? 0) + HeightOffset;

    /// <summary>The roof's underside where it bears - what a wall attached under it reaches up to.</summary>
    public override double GetBottomElevation(BimDocument document) => BaseElevation(document);

    /// <summary>The top of the build-up where the roof bears. For a flat roof, its top surface.</summary>
    public override double GetTopElevation(BimDocument document) =>
        BaseElevation(document) + Thickness(document);

    private double Thickness(BimDocument document) => document.FindType<SlabType>(TypeId)?.Thickness ?? 0;

    /// <summary>
    /// The roof's faces and the lines between them, worked out from the footprint. The planes
    /// are the roof's underside; each face's build-up stands on it, square to the slope.
    /// </summary>
    public RoofSurface Surface(BimDocument document)
    {
        var bottom = BaseElevation(document);
        var cutoff = CutoffElevation(document);

        // Working the faces out takes a few dozen polygon operations, and every wall attached
        // under the roof asks for them each time it is drawn. So the last answer is kept, with
        // everything it was worked out from, and handed back while none of that has changed.
        var signature = Signature(bottom, cutoff);
        if (_surface is { } kept && _surfaceSignature is { } was && was.SequenceEqual(signature)) return kept;

        _surface = Extrusion is { } extrusion
            ? RoofShape.BuildExtrusion(extrusion, bottom)
            : RoofShape.Build(Boundary, _edges, bottom, cutoff, _arrows);
        _surfaceSignature = signature;
        return _surface;
    }

    private RoofSurface? _surface;
    private double[]? _surfaceSignature;

    private double[] Signature(double bottom, double? cutoff)
    {
        var values = new List<double>(4 + Boundary.Count * 2 + _edges.Count * 5) { bottom, cutoff ?? double.NaN };

        foreach (var point in Boundary)
        {
            values.Add(point.X);
            values.Add(point.Y);
        }

        if (Extrusion is { } extrusion)
        {
            values.Add(extrusion.Origin.X);
            values.Add(extrusion.Origin.Y);
            values.Add(extrusion.Direction.X);
            values.Add(extrusion.Direction.Y);
            values.Add(extrusion.Start);
            values.Add(extrusion.End);

            foreach (var point in extrusion.Profile)
            {
                values.Add(point.X);
                values.Add(point.Y);
            }
        }

        foreach (var edge in _edges)
        {
            values.Add(edge.DefinesSlope ? 1 : 0);
            values.Add(edge.SlopeDegrees);
            values.Add(edge.PlateOffset);
            values.Add(edge.BearingInset);
            values.Add(edge.WallId is null ? 0 : 1);
        }

        foreach (var arrow in _arrows)
        {
            values.Add(arrow.Tail.X);
            values.Add(arrow.Tail.Y);
            values.Add(arrow.Head.X);
            values.Add(arrow.Head.Y);
            values.Add(arrow.Rise);
            values.Add(arrow.TailOffset);
        }

        return values.ToArray();
    }

    /// <summary>The underside of the roof over a point in plan - what a wall under it meets.</summary>
    public double UndersideAt(BimDocument document, Point2D point) => Surface(document).HeightAt(point);

    /// <summary>The top of the roof over a point in plan - the weathering surface.</summary>
    public double TopAt(BimDocument document, Point2D point)
    {
        var surface = Surface(document);
        var face = surface.FaceAt(point);
        return surface.HeightAt(point) + Thickness(document) * (face?.Plane.VerticalStretch ?? 1);
    }

    /// <summary>
    /// The very top of the roof - its ridge, measured on the weathering surface. Revit's
    /// Maximum Ridge Height, above the project's origin rather than the base.
    /// </summary>
    public double RidgeElevation(BimDocument document)
    {
        var thickness = Thickness(document);
        var surface = Surface(document);

        if (surface.Facets.Count == 0) return BaseElevation(document) + thickness;

        return surface.Facets.Max(facet =>
            facet.Points3D.Max(point => point.Z) + thickness * facet.Plane.VerticalStretch);
    }

    /// <summary>
    /// The area of the roof surface, which on a pitched roof is more than its footprint - and
    /// it is the surface, not the footprint, that tiles are bought by.
    /// </summary>
    public double SlopingArea(BimDocument document) => Surface(document).SlopingArea;

    public override double GetVolume(BimDocument document) =>
        SlopingArea(document) * (document.FindType<SlabType>(TypeId)?.Thickness ?? 0);

    protected override IEnumerable<ParameterValue> ExtraParameters(BimDocument document) =>
        Extrusion is null ? FootprintParameters(document) : ExtrusionParameters(document);

    /// <summary>
    /// A roof by extrusion's own settings - Revit's Extrusion Start and End - and what it
    /// comes to. The rest belongs to its profile, changed with Edit Profile.
    /// </summary>
    private IEnumerable<ParameterValue> ExtrusionParameters(BimDocument document)
    {
        yield return ParameterValue.ReadOnly(RoofParameters.Shape, () => EnumText.Humanise(Form));

        yield return ParameterValue.BindValidated(
            RoofParameters.ExtrusionStart, () => Extrusion!.Start, (double start) =>
            {
                if (Extrusion is not { } extrusion || start >= extrusion.End - 1) return false;

                SetExtrusion(new RoofExtrusion(extrusion.Origin, extrusion.Direction, extrusion.Profile, start, extrusion.End));
                return true;
            });

        yield return ParameterValue.BindValidated(
            RoofParameters.ExtrusionEnd, () => Extrusion!.End, (double end) =>
            {
                if (Extrusion is not { } extrusion || end <= extrusion.Start + 1) return false;

                SetExtrusion(new RoofExtrusion(extrusion.Origin, extrusion.Direction, extrusion.Profile, extrusion.Start, end));
                return true;
            });

        yield return ParameterValue.ReadOnly(
            RoofParameters.RidgeHeight, () => RidgeElevation(document) - BaseElevation(document));

        yield return ParameterValue.ReadOnly(RoofParameters.SlopingArea, () => SlopingArea(document));
    }

    private IEnumerable<ParameterValue> FootprintParameters(BimDocument document)
    {
        yield return ParameterValue.BindChoice(
            RoofParameters.Shape,
            () => EnumText.Humanise(Form),
            value =>
            {
                // Freeform is what a roof is called once its edges have been set one at a
                // time; choosing it would have nothing to do, so it is left alone.
                if (EnumText.TryParse<RoofForm>(value, out var form) && form is not (RoofForm.Freeform or RoofForm.Gambrel or RoofForm.Barrel))
                    SetShape(form);
            },
            // A gambrel or a vault is a profile, drawn as a roof by extrusion: a footprint
            // cannot be one, so neither is offered here.
            new[] { RoofForm.Flat, RoofForm.Shed, RoofForm.Gable, RoofForm.Hip, RoofForm.Freeform }
                .Select(form => EnumText.Humanise(form)).ToArray());

        yield return ParameterValue.BindValidated(
            RoofParameters.Slope, () => SlopeDegrees, (double pitch) =>
            {
                // A roof at 90° would be a wall, and one at a negative pitch would drain into
                // the building: neither is a roof, so neither is accepted.
                if (pitch < 0 || pitch >= 90) return false;

                SlopeDegrees = pitch;
                return true;
            });

        // Cutoff: none, measured from the roof's own base, or from a level - Revit's Cutoff
        // Level and Cutoff Offset.
        yield return ParameterValue.BindChoice(
            RoofParameters.CutoffLevel,
            () => CutoffLevelId is { } id && document.FindLevel(id) is { } level
                ? level.Name
                : CutoffOffset > 0 ? RoofBase : NoCutoff,
            value =>
            {
                if (value == NoCutoff)
                {
                    CutoffLevelId = null;
                    CutoffOffset = 0;
                }
                else if (value == RoofBase)
                {
                    CutoffLevelId = null;
                }
                else if (document.Levels.FirstOrDefault(level => level.Name == value) is { } level)
                {
                    CutoffLevelId = level.Id;
                }
            },
            new[] { NoCutoff, RoofBase }.Concat(document.Levels.Select(level => level.Name)).ToArray());

        yield return ParameterValue.Bind(RoofParameters.CutoffOffset, () => CutoffOffset, value => CutoffOffset = value);

        yield return ParameterValue.BindChoice(
            RoofParameters.RafterCut,
            () => EnumText.Humanise(RafterCut),
            value => { if (EnumText.TryParse<RafterCut>(value, out var cut)) RafterCut = cut; },
            EnumText.Choices<RafterCut>());

        yield return ParameterValue.BindValidated(
            RoofParameters.FasciaDepth, () => FasciaDepth, (double depth) =>
            {
                // No deeper than the roof itself: the upright part of the eave is cut out of
                // the roof's own thickness.
                if (depth < 0 || depth > Thickness(document) + 1e-6) return false;

                FasciaDepth = depth;
                return true;
            });

        yield return ParameterValue.ReadOnly(
            RoofParameters.RidgeHeight, () => RidgeElevation(document) - BaseElevation(document));

        yield return ParameterValue.ReadOnly(RoofParameters.SlopingArea, () => SlopingArea(document));

        yield return ParameterValue.ReadOnly(
            RoofParameters.SlopingEdges, () => _edges.Count(edge => edge.DefinesSlope));

        foreach (var parameter in EdgeParameters()) yield return parameter;
    }

    /// <summary>
    /// The pitch and eave height of each sloping edge on its own.
    ///
    /// Two of the shapes people actually build need this and nothing else: a catslide is a
    /// gable with one eave carried down lower, and a half-hip is a gable whose ends slope from
    /// a plate raised near the ridge. Both are one number per edge, so the edges are offered
    /// one at a time rather than only as the roof-wide pitch.
    ///
    /// Only edges that slope are listed: a pitch for an edge the roof is cut off at would be a
    /// number with nothing to do.
    /// </summary>
    private IEnumerable<ParameterValue> EdgeParameters()
    {
        for (var i = 0; i < _edges.Count; i++)
        {
            if (!_edges[i].DefinesSlope) continue;

            var edge = _edges[i];
            var number = i + 1;

            yield return ParameterValue.BindValidated(
                new ParameterDefinition($"Slope at Edge {number}", ParameterDataType.Angle,
                    ParameterBinding.Instance, ParameterGroup.Dimensions),
                () => edge.SlopeDegrees,
                (double pitch) =>
                {
                    if (pitch <= 0 || pitch >= 90) return false;

                    edge.SlopeDegrees = pitch;
                    return true;
                });

            yield return ParameterValue.Bind(
                new ParameterDefinition($"Eave Offset at Edge {number}", ParameterDataType.Length,
                    ParameterBinding.Instance, ParameterGroup.Dimensions),
                () => edge.PlateOffset,
                value => edge.PlateOffset = value);
        }
    }
}

/// <summary>
/// Changes what a roof does at its edges, as one undoable step.
///
/// The edges are stored, the shape is not, so this is the only thing there is to record: put
/// the old settings back and the old roof comes back with them.
/// </summary>
public sealed class SetRoofEdgesCommand : IUndoableCommand
{
    private readonly Roof _roof;
    private readonly List<RoofEdge> _before;
    private readonly List<RoofEdge> _after;

    public SetRoofEdgesCommand(Roof roof, IEnumerable<RoofEdge> edges, string name)
    {
        _roof = roof;
        _before = roof.Edges.Select(edge => edge.Copy()).ToList();
        _after = edges.Select(edge => edge.Copy()).ToList();
        Name = name;
    }

    public string Name { get; }

    public void Redo() => _roof.SetEdges(_after.Select(edge => edge.Copy()));

    public void Undo() => _roof.SetEdges(_before.Select(edge => edge.Copy()));
}

public static class RoofParameters
{
    public static readonly ParameterDefinition Shape =
        new("Roof Shape", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Constraints);

    public static readonly ParameterDefinition Slope =
        new("Slope", ParameterDataType.Angle, ParameterBinding.Instance, ParameterGroup.Dimensions);

    public static readonly ParameterDefinition ExtrusionStart =
        new("Extrusion Start", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Constraints);

    public static readonly ParameterDefinition ExtrusionEnd =
        new("Extrusion End", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Constraints);

    public static readonly ParameterDefinition CutoffLevel =
        new("Cutoff Level", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Constraints);

    public static readonly ParameterDefinition CutoffOffset =
        new("Cutoff Offset", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Constraints);

    public static readonly ParameterDefinition RafterCut =
        new("Rafter Cut", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Construction);

    public static readonly ParameterDefinition FasciaDepth =
        new("Fascia Depth", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Construction);

    public static readonly ParameterDefinition RidgeHeight =
        new("Ridge Height Above Base", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);

    public static readonly ParameterDefinition SlopingArea =
        new("Roof Surface Area", ParameterDataType.Area, ParameterBinding.Instance, ParameterGroup.Dimensions);

    public static readonly ParameterDefinition SlopingEdges =
        new("Slope Defining Edges", ParameterDataType.Integer, ParameterBinding.Instance, ParameterGroup.Dimensions);
}
