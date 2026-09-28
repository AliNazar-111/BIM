using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;
using BIMDesigner.Core.Parameters;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// Which line of the assembly the user actually draws (specification section 3.1).
///
/// This matters in practice: an architect drawing to a grid wants the core face on the
/// grid, while a draughtsman setting out a plot boundary wants the finish face. The stored
/// end points are always on the location line; the wall body is offset from it.
/// </summary>
public enum WallLocationLine
{
    WallCentreline,
    CoreCentreline,
    FinishFaceExterior,
    FinishFaceInterior,
    CoreFaceExterior,
    CoreFaceInterior
}

/// <summary>How a wall participates in the structure (specification section 3.1).</summary>
public enum StructuralUsage
{
    NonBearing,
    Bearing,
    Shear,
    StructuralCombined
}

/// <summary>
/// A wall (specification sections 3.1 and 13.1).
///
/// Instance parameters live here - where this wall is, how tall, which level. Everything
/// shared by walls of the same construction - layers, fire rating, cost - lives on
/// <see cref="WallType"/>. Quantities are computed from the two together and are never
/// stored, so they cannot drift out of date.
/// </summary>
public sealed class Wall : Element
{
    private double _unconnectedHeight = 3000;

    public override BuiltInCategory Category => BuiltInCategory.Walls;

    /// <summary>Start of the location line, in millimetres.</summary>
    public Point2D Start { get; set; }

    /// <summary>End of the location line, in millimetres.</summary>
    public Point2D End { get; set; }

    /// <summary>
    /// How far the wall bows between its ends: zero for a straight wall, otherwise the tangent
    /// of a quarter of the angle the arc turns through, positive turning left. See <see cref="WallCurve"/>.
    /// </summary>
    public double Bulge { get; set; }

    /// <summary>
    /// The shape of an elliptical wall, or null when the wall is straight or an arc. Takes
    /// precedence over <see cref="Bulge"/>. See <see cref="WallEllipse"/>.
    /// </summary>
    public WallEllipse? Ellipse { get; set; }

    /// <summary>
    /// The shape of a spline or freeform wall, or null. Takes precedence over <see cref="Ellipse"/>
    /// and <see cref="Bulge"/>. See <see cref="WallSpline"/>.
    /// </summary>
    public WallSpline? Spline { get; set; }

    /// <summary>
    /// The walls this one was laid against and joined to, face to face: their doors and windows
    /// cut through it and its through them. See <see cref="WallLamination"/>.
    /// </summary>
    public List<Guid> JoinedTo { get; } = new();

    /// <summary>Whether this wall moves with the walls it is joined to, and they with it.</summary>
    public bool LockedToJoined { get; set; }

    /// <summary>
    /// Whether the corner at this wall's start is locked: the walls meeting there stay meeting
    /// when any of them is moved. See <see cref="WallJointLock"/>.
    /// </summary>
    public bool StartLocked { get; set; }

    /// <summary>Whether the corner at this wall's end is locked.</summary>
    public bool EndLocked { get; set; }

    /// <summary>How the join at this wall's start is drawn in plan: cleaned, not, or as the view says.</summary>
    public WallJoinCleanup StartCleanup { get; set; }

    /// <summary>How the join at this wall's end is drawn in plan.</summary>
    public WallJoinCleanup EndCleanup { get; set; }

    /// <summary>Whether the wall is an arc, elliptical or a spline rather than a straight line.</summary>
    public bool IsCurved => Math.Abs(Bulge) >= WallCurve.StraightBulge || IsElliptical || IsSpline;

    /// <summary>Whether the wall is a piece of an ellipse.</summary>
    public bool IsElliptical => !IsSpline && Ellipse is { IsValid: true };

    /// <summary>Whether the wall follows a spline.</summary>
    public bool IsSpline => Spline is { IsValid: true };

    /// <summary>The line the wall was drawn along - the location line - straight or curved.</summary>
    public WallCurve LocationCurve
    {
        get
        {
            // An elliptical curve is measured along its length when made, so it is kept until
            // the wall's ends or shape change.
            var key = (Start, End, Bulge, Ellipse, Spline);
            if (_curve is null || !_curveKey.Equals(key))
            {
                _curve = WallCurve.Of(Start, End, Bulge, Ellipse, Spline);
                _curveKey = key;
            }

            return _curve;
        }
    }

    private WallCurve? _curve;
    private (Point2D, Point2D, double, WallEllipse?, WallSpline?) _curveKey;

    public WallLocationLine LocationLine { get; set; } = WallLocationLine.WallCentreline;

    /// <summary>Swaps which side of the wall counts as exterior.</summary>
    public bool Flipped { get; set; }

    /// <summary>Millimetres above the base level. Positive lifts the wall.</summary>
    public double BaseOffset { get; set; }

    /// <summary>
    /// Top constraint. Null means the wall uses <see cref="UnconnectedHeight"/>; otherwise
    /// its height follows that level and changes when the level moves.
    /// </summary>
    public Guid? TopLevelId { get; set; }

    /// <summary>Millimetres relative to the top level. Negative drops below it.</summary>
    public double TopOffset { get; set; }

    /// <summary>Height used when the wall has no top constraint. Millimetres.</summary>
    public double UnconnectedHeight
    {
        get => _unconnectedHeight;
        set => _unconnectedHeight = value > 0
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), "A wall must have a positive height.");
    }

    /// <summary>Whether this wall bounds rooms (specification section 2.5).</summary>
    public bool RoomBounding { get; set; } = true;

    public StructuralUsage StructuralUsage { get; set; } = StructuralUsage.NonBearing;

    /// <summary>Vertical, slanted or tapered (specification section 3.1). See <see cref="WallLean"/>.</summary>
    public WallCrossSection CrossSection { get; set; } = WallCrossSection.Vertical;

    /// <summary>How far a slanted wall leans from vertical, in degrees. Positive leans toward the exterior.</summary>
    public double SlantAngle { get; set; }

    /// <summary>How far a double-slanted wall leans above its break, in degrees. Positive leans toward the exterior.</summary>
    public double UpperSlantAngle { get; set; }

    /// <summary>Where a double-slanted wall changes its lean, in millimetres above its base.</summary>
    public double SlantBreakHeight { get; set; } = 1500;

    /// <summary>Whether this wall's taper angles are its own rather than its type's.</summary>
    public bool OverrideTaper { get; set; }

    /// <summary>This wall's own lean inward of its exterior face, when overriding its type. Degrees.</summary>
    public double ExteriorTaper { get; set; }

    /// <summary>This wall's own lean inward of its interior face, when overriding its type. Degrees.</summary>
    public double InteriorTaper { get; set; }

    /// <summary>How the start of the wall is joined to whatever meets it there.</summary>
    public WallJoinKind StartJoin { get; set; } = WallJoinKind.Auto;

    /// <summary>How the end of the wall is joined to whatever meets it there.</summary>
    public WallJoinKind EndJoin { get; set; } = WallJoinKind.Auto;

    /// <summary>
    /// How far the wall body is shifted across its location line, on top of whatever the
    /// location line rule already says, positive toward the exterior. It is what lets a thin
    /// wall run flush with the face of a thicker one it meets instead of sitting in the middle
    /// of it. See <see cref="WallAlignment"/>.
    /// </summary>
    public double AcrossOffset { get; set; }

    /// <summary>
    /// The wall's edited elevation outline, or null for the rectangle its length and height give.
    /// Points are distance along the location line and height above the base. See <see cref="WallProfile"/>.
    /// </summary>
    public IReadOnlyList<Point2D>? Profile { get; set; }

    /// <summary>How long the wall was when its profile was edited: corners at that distance stay on the end.</summary>
    public double ProfileLength { get; set; }

    /// <summary>
    /// A curtain wall's own grid lines, replacing the ones its type sets out; null to follow the
    /// type. See <see cref="CurtainLayout"/>.
    /// </summary>
    public CurtainGrid? CurtainGrid { get; set; }

    /// <summary>The cells of a curtain wall filled with something other than glass.</summary>
    public IReadOnlyList<CurtainPanelOverride>? CurtainPanels { get; set; }

    /// <summary>What this curtain wall is glazed with, for every panel that does not say otherwise.</summary>
    public CurtainGlass CurtainGlass { get; set; } = CurtainGlass.Clear;

    /// <summary>Length of the location line, in millimetres.</summary>
    public double Length => IsCurved ? LocationCurve.Length : Start.DistanceTo(End);

    /// <summary>Direction from start to end, or the X axis for a degenerate wall.</summary>
    public Vector2D Direction => (End - Start).NormalisedOrDefault(Vector2D.UnitX);

    /// <summary>
    /// Unit vector pointing to the exterior side of the wall. Layer 0 of the assembly faces
    /// this way.
    /// </summary>
    public Vector2D ExteriorNormal
    {
        get
        {
            var normal = Direction.PerpendicularLeft();
            return Flipped ? -normal : normal;
        }
    }

    /// <summary>
    /// The floor, ceiling or roof the top of the wall is attached to, if any (specification
    /// section 3.1, "attach top/base"). The top then follows that slab's underside, whatever
    /// the top constraint says, and moves when the slab does.
    /// </summary>
    public Guid? TopAttachedTo { get; set; }

    /// <summary>The floor the base of the wall stands on, if it is attached to one.</summary>
    public Guid? BaseAttachedTo { get; set; }

    /// <summary>
    /// Where the wall starts, in project elevation: on the floor it is attached to, or at its
    /// base level plus the base offset.
    /// </summary>
    public double GetBaseElevation(BimDocument document) => GetBaseElevation(document, 0);

    // Walls attached to walls follow one another up and down; the depth stops two walls
    // attached to each other from going round for ever.
    private const int MaxAttachmentChain = 8;

    private double GetBaseElevation(BimDocument document, int depth)
    {
        if (BaseAttachedTo is { } slabId && FindSlab(document, slabId) is { } slab)
        {
            // On a pitched roof the base is the lowest the roof's top comes under the wall; the
            // wall's outline rises from there with the roof, so it stands on it all along.
            if (slab is Roof roof && !roof.Surface(document).IsFlat && WallProfile.LowestTopUnder(document, this, roof) is { } lowest)
                return lowest;

            return slab.GetTopElevation(document);
        }

        // Standing on the wall below: its top.
        if (depth < MaxAttachmentChain && BaseAttachedTo is { } belowId && FindWall(document, belowId) is { } below)
            return below.GetTopElevation(document, depth + 1);

        return (document.FindLevel(LevelId)?.Elevation ?? 0) + BaseOffset;
    }

    /// <summary>
    /// Where the wall stops, in project elevation: under the slab it is attached to, at its top
    /// level plus the top offset, or its unconnected height above the base. A top that would
    /// come at or below the base is ignored for the unconnected height, so a wall never
    /// vanishes because a level or slab moved.
    /// </summary>
    public double GetTopElevation(BimDocument document) => GetTopElevation(document, 0);

    /// <summary>
    /// Where the wall would stop if nothing were attached over it: its top level plus offset,
    /// or its unconnected height. What a wall attached to a roof stands to along any stretch
    /// the roof does not cover.
    /// </summary>
    public double GetUnattachedTopElevation(BimDocument document)
    {
        var bottom = GetBaseElevation(document);

        if (TopLevelId is { } topId && document.FindLevel(topId) is { } topLevel)
        {
            var top = topLevel.Elevation + TopOffset;
            if (top > bottom) return top;
        }

        return bottom + UnconnectedHeight;
    }

    /// <summary>The roof the wall's top is attached to, if it is attached to one.</summary>
    public Roof? AttachedRoof(BimDocument document) =>
        TopAttachedTo is { } id ? FindSlab(document, id) as Roof : null;

    /// <summary>The roof the wall stands on - a dormer's wall on the roof it rises out of - if its base is attached to one.</summary>
    public Roof? RoofUnder(BimDocument document) =>
        BaseAttachedTo is { } id ? FindSlab(document, id) as Roof : null;

    private double GetTopElevation(BimDocument document, int depth)
    {
        var bottom = GetBaseElevation(document, depth + 1);

        if (TopAttachedTo is { } slabId && FindSlab(document, slabId) is { } slab)
        {
            var underside = slab.GetBottomElevation(document);
            if (underside > bottom) return underside;
        }

        // Up to the wall above: its base.
        if (depth < MaxAttachmentChain && TopAttachedTo is { } aboveId && FindWall(document, aboveId) is { } above)
        {
            var foot = above.GetBaseElevation(document, depth + 1);
            if (foot > bottom) return foot;
        }

        if (TopLevelId is { } topId && document.FindLevel(topId) is { } topLevel)
        {
            var top = topLevel.Elevation + TopOffset;
            if (top > bottom) return top;
        }

        return bottom + UnconnectedHeight;
    }

    /// <summary>Resolves the wall's height, from wherever its base and top are held.</summary>
    public double GetHeight(BimDocument document) => GetTopElevation(document) - GetBaseElevation(document);

    /// <summary>
    /// Makes a straight wall a new length, as Revit's Length parameter does: its start stays
    /// where it is and its end moves along it - and the walls that meet it there come too, so
    /// the corner holds. Null when the length cannot be had: none at all, or one that would
    /// shrink a wall meeting it there to nothing.
    /// </summary>
    public IUndoableCommand? LengthChange(BimDocument document, double length)
    {
        if (IsCurved || length <= WallJoins.JoinTolerance || Length <= WallJoins.JoinTolerance) return null;

        var end = Start + (End - Start) / Length * length;
        var commands = new List<IUndoableCommand> { new MoveWallCommand(this, Start, End, Start, end, "Change Length") };

        foreach (var (other, atStart) in WallCorners.At(document, LevelId, End).Where(corner => !ReferenceEquals(corner.Wall, this)))
        {
            var (start, otherEnd) = atStart ? (end, other.End) : (other.Start, end);
            if (start.DistanceTo(otherEnd) <= WallJoins.JoinTolerance) return null;

            commands.Add(new MoveWallCommand(other, other.Start, other.End, start, otherEnd, "Change Length"));
        }

        return commands.Count == 1 ? commands[0] : new CompositeCommand("Change Length", commands);
    }

    /// <summary>
    /// Makes the wall a new height by moving its top: the top offset from its top level where
    /// it has one, its unconnected height where it has not.
    /// </summary>
    private bool TrySetHeight(BimDocument document, double height)
    {
        if (height <= 0) return false;

        if (TopLevelId is { } topId && document.FindLevel(topId) is { } topLevel)
        {
            var offset = GetBaseElevation(document) + height - topLevel.Elevation;
            if (!KeepsHeight(document, LevelId, BaseOffset, TopLevelId, offset)) return false;

            TopOffset = offset;
            return true;
        }

        UnconnectedHeight = height;
        return true;
    }

    /// <summary>What an attachment is to, as the property panel shows it.</summary>
    private static string AttachmentName(BimDocument document, Guid? slabId)
    {
        if (slabId is not { } id) return "None";
        if (FindWall(document, id) is { } wall)
            return $"Wall: {document.FindType<ElementType>(wall.TypeId)?.Name ?? "wall"}, {document.FindLevel(wall.LevelId)?.Name}";
        if (FindSlab(document, id) is not { } slab) return "None (deleted)";

        var type = document.FindType<SlabType>(slab.TypeId)?.Name ?? slab.Category.ToString();
        var level = document.FindLevel(slab.LevelId)?.Name;
        return level is null ? type : $"{type}, {level}";
    }

    private static Slab? FindSlab(BimDocument document, Guid id) =>
        document.Elements.OfType<Slab>().FirstOrDefault(slab => slab.Id == id);

    private static Wall? FindWall(BimDocument document, Guid id) =>
        document.Walls.FirstOrDefault(wall => wall.Id == id);

    /// <summary>The name shown for a wall with no top level.</summary>
    public const string Unconnected = "Unconnected";

    /// <summary>The top constraint as shown in the property panel.</summary>
    public string TopConstraintName(BimDocument document) =>
        TopLevelId is { } id && document.FindLevel(id) is { } level ? $"Up to level: {level.Name}" : Unconnected;

    /// <summary>
    /// What the top can be set to: unconnected, or up to its own level or any level above it.
    /// Up to its own level with the base offset below it is a wall that hangs down from the
    /// level: a foundation wall, or a basement wall drawn from ground floor.
    /// </summary>
    public IReadOnlyList<string> TopConstraintChoices(BimDocument document)
    {
        var baseElevation = document.FindLevel(LevelId)?.Elevation ?? double.NegativeInfinity;

        var choices = new List<string> { Unconnected };
        choices.AddRange(document.Levels
            .Where(level => level.Elevation >= baseElevation || level.Id == TopLevelId)
            .Select(level => $"Up to level: {level.Name}"));

        return choices;
    }

    /// <summary>
    /// The change that sets the top constraint to one of <see cref="TopConstraintChoices"/>,
    /// or null if that would leave the wall with no height.
    ///
    /// Letting go of the top level keeps the wall the height it is now, rather than jumping
    /// back to whatever unconnected height it had before it was attached.
    /// </summary>
    public IUndoableCommand? SetTopConstraint(BimDocument document, string choice)
    {
        if (choice == Unconnected)
            return new SetWallTopCommand(this, null, TopOffset, GetHeight(document));

        var level = document.Levels.FirstOrDefault(l => $"Up to level: {l.Name}" == choice);
        if (level is null || !KeepsHeight(document, LevelId, BaseOffset, level.Id, TopOffset)) return null;

        return new SetWallTopCommand(this, level.Id, TopOffset, UnconnectedHeight);
    }

    /// <summary>Whether these constraints leave the wall with a positive height.</summary>
    public static bool KeepsHeight(
        BimDocument document, Guid baseLevelId, double baseOffset, Guid? topLevelId, double topOffset)
    {
        if (topLevelId is not { } topId) return true;

        var baseLevel = document.FindLevel(baseLevelId);
        var topLevel = document.FindLevel(topId);
        if (baseLevel is null || topLevel is null) return true;

        return topLevel.Elevation + topOffset - (baseLevel.Elevation + baseOffset) > 0;
    }

    /// <summary>Elevation area of one wall face, in mm². The quantity used for finishes.</summary>
    public double GetArea(BimDocument document) =>
        WallProfile.Of(document, this) is { } profile
            ? WallProfile.Strips(profile, Length).Sum(strip => strip.Area)
            : Length * GetHeight(document);

    /// <summary>Gross volume, in mm³. The quantity used for concrete and masonry takeoff.</summary>
    public double GetVolume(BimDocument document)
    {
        // A curtain wall is its panels and mullions, not a solid the size of its frame.
        if (CurtainLayout.Of(document, this) is { } curtain) return curtain.Volume;

        // Each tier of a stacked wall is as thick as its own construction.
        if (document.FindType<WallType>(TypeId) is null && document.FindType<StackedWallType>(TypeId) is null) return 0;

        // An edited profile is one construction, its elevation area through its thickness.
        if (WallProfile.Of(document, this) is not null && document.GetWallType(this) is { } type) return GetArea(document) * type.Width;

        return document.GetWallTiers(this).Sum(tier => Length * (tier.Top - tier.Bottom) * tier.Type.Width);
    }

    /// <summary>
    /// Signed distance from the wall centreline to the location line, positive toward the
    /// exterior. The wall body is drawn by shifting the stored line back by this amount.
    /// </summary>
    public double GetLocationLineOffset(CompoundStructure structure)
    {
        var half = structure.TotalWidth / 2;

        return Rule() - AcrossOffset;

        double Rule() => LocationLine switch
        {
            WallLocationLine.WallCentreline => 0,
            WallLocationLine.FinishFaceExterior => half,
            WallLocationLine.FinishFaceInterior => -half,
            WallLocationLine.CoreFaceExterior => half - structure.ExteriorWidth,
            WallLocationLine.CoreFaceInterior => -half + structure.InteriorWidth,
            WallLocationLine.CoreCentreline => half - structure.ExteriorWidth - structure.CoreWidth / 2,
            _ => 0
        };
    }

    /// <summary>
    /// The centreline of the wall body, which is the drawn line moved back off the location
    /// line. Plan rendering, joins and 3D extrusion all start from this.
    /// </summary>
    public (Point2D Start, Point2D End) GetBodyCentreline(CompoundStructure structure)
    {
        var offset = GetLocationLineOffset(structure);
        if (offset == 0) return (Start, End);

        if (IsCurved)
        {
            var curve = LocationCurve;
            return (PointAt(structure, 0, 0), PointAt(structure, curve.Length, 0));
        }

        var shift = ExteriorNormal * -offset;
        return (Start + shift, End + shift);
    }

    /// <summary>
    /// A point on the wall: this far along its location line from the start, and this far
    /// across from its body centreline toward the exterior. The frame every wall drawing is
    /// built in, so straight and curved walls are drawn by the same code.
    /// </summary>
    public Point2D PointAt(CompoundStructure structure, double along, double across) =>
        LocationCurve.At(along, ExteriorSign * (across - GetLocationLineOffset(structure)));

    /// <summary>
    /// Where a line crosses the edge of the wall this far across from its body centreline -
    /// a line on a straight wall, an arc on a curved one - taking the crossing nearest to a
    /// point when there are two. Null when they do not meet.
    /// </summary>
    public Point2D? EdgeCrossing(CompoundStructure structure, Line2D line, double across, Point2D near) =>
        LocationCurve.Intersect(line, ExteriorSign * (across - GetLocationLineOffset(structure)), near);

    /// <summary>Where a point lies in the wall's frame. The inverse of <see cref="PointAt"/>.</summary>
    public (double Along, double Across) Locate(CompoundStructure structure, Point2D point)
    {
        var (along, left) = LocationCurve.Locate(point);
        return (along, ExteriorSign * left + GetLocationLineOffset(structure));
    }

    /// <summary>The direction of the wall at this distance along it.</summary>
    public Vector2D TangentAt(double along) => IsCurved ? LocationCurve.TangentAt(along) : Direction;

    /// <summary>Unit vector toward the exterior at this distance along.</summary>
    public Vector2D ExteriorNormalAt(double along) => TangentAt(along).PerpendicularLeft() * ExteriorSign;

    /// <summary>How far to the left of the location line a point this far across the body lies.</summary>
    public double LeftOf(CompoundStructure structure, double across) =>
        ExteriorSign * (across - GetLocationLineOffset(structure));

    /// <summary>+1 when the exterior is to the left of the drawing direction, -1 when flipped.</summary>
    private double ExteriorSign => Flipped ? -1 : 1;

    public override IEnumerable<ParameterValue> GetInstanceParameters(BimDocument document)
    {
        // Constraints - specification section 13.1
        yield return ParameterValue.BindChoiceValidated(
            WallParameters.BaseConstraint,
            () => document.FindLevel(LevelId)?.Name ?? string.Empty,
            v =>
            {
                var level = document.Levels.FirstOrDefault(l => l.Name == v);
                if (level is null || !KeepsHeight(document, level.Id, BaseOffset, TopLevelId, TopOffset)) return false;
                LevelId = level.Id;
                return true;
            },
            document.Levels.Select(l => l.Name).ToArray());
        yield return ParameterValue.BindValidated(
            WallParameters.BaseOffset,
            () => BaseOffset,
            v =>
            {
                if (!KeepsHeight(document, LevelId, v, TopLevelId, TopOffset)) return false;
                BaseOffset = v;
                return true;
            });
        yield return ParameterValue.BindChoiceCommand(
            WallParameters.TopConstraint,
            () => TopConstraintName(document),
            v => SetTopConstraint(document, v),
            TopConstraintChoices(document));

        // The top offset only means something against a level, and the unconnected height
        // only when there is none - each is shown read-only while the other is in charge.
        if (TopLevelId is null)
        {
            yield return ParameterValue.ReadOnly(WallParameters.TopOffset, () => TopOffset);
            yield return ParameterValue.BindValidated(
                WallParameters.UnconnectedHeight,
                () => UnconnectedHeight,
                v =>
                {
                    if (v <= 0) return false;
                    UnconnectedHeight = v;
                    return true;
                });
        }
        else
        {
            yield return ParameterValue.BindValidated(
                WallParameters.TopOffset,
                () => TopOffset,
                v =>
                {
                    if (!KeepsHeight(document, LevelId, BaseOffset, TopLevelId, v)) return false;
                    TopOffset = v;
                    return true;
                });
            yield return ParameterValue.ReadOnly(WallParameters.UnconnectedHeight, () => UnconnectedHeight);
        }

        // Walls joined face to face: how many, and whether they move together.
        if (JoinedTo.Count > 0)
        {
            yield return ParameterValue.ReadOnly(WallParameters.JoinedWalls, () => JoinedTo.Count);
            yield return ParameterValue.BindValidated(WallParameters.LockedToJoined, () => LockedToJoined, v =>
            {
                if (v && !WallLamination.CanLock(this)) return false;
                LockedToJoined = v;
                return true;
            });
        }

        // Leaning: the profile, and the angles that apply to it. Each is editable only when it
        // means something for the profile chosen.
        yield return ParameterValue.BindChoice(
            WallParameters.CrossSection,
            () => EnumText.Humanise(CrossSection),
            v => { if (EnumText.TryParse<WallCrossSection>(v, out var section)) CrossSection = section; },
            EnumText.Choices<WallCrossSection>());

        if (CrossSection is WallCrossSection.Slanted or WallCrossSection.DoubleSlanted)
            yield return ParameterValue.BindValidated(
                CrossSection == WallCrossSection.DoubleSlanted ? WallParameters.LowerSlantAngle : WallParameters.SlantAngle, () => SlantAngle, v =>
            {
                if (Math.Abs(v) > WallLean.MaxAngle) return false;
                SlantAngle = v;
                return true;
            });

        if (CrossSection == WallCrossSection.DoubleSlanted)
        {
            yield return ParameterValue.BindValidated(WallParameters.UpperSlantAngle, () => UpperSlantAngle, v =>
            {
                if (Math.Abs(v) > WallLean.MaxAngle) return false;
                UpperSlantAngle = v;
                return true;
            });
            yield return ParameterValue.BindValidated(WallParameters.SlantBreakHeight, () => SlantBreakHeight, v =>
            {
                if (!double.IsFinite(v) || v < 0) return false;
                SlantBreakHeight = v;
                return true;
            });
        }

        if (CrossSection == WallCrossSection.Tapered)
        {
            yield return ParameterValue.Bind(WallParameters.OverrideTaper, () => OverrideTaper, v => OverrideTaper = v);

            if (OverrideTaper)
            {
                yield return ParameterValue.BindValidated(WallParameters.ExteriorTaper, () => ExteriorTaper, v =>
                {
                    if (Math.Abs(v) > WallLean.MaxAngle) return false;
                    ExteriorTaper = v;
                    return true;
                });
                yield return ParameterValue.BindValidated(WallParameters.InteriorTaper, () => InteriorTaper, v =>
                {
                    if (Math.Abs(v) > WallLean.MaxAngle) return false;
                    InteriorTaper = v;
                    return true;
                });
            }
        }

        // What the top and base are attached to. Attaching is done from the Edit menu, where
        // the slab above or below is found for you; here it is only reported.
        yield return ParameterValue.ReadOnly(WallParameters.TopAttachedTo, () => AttachmentName(document, TopAttachedTo));
        yield return ParameterValue.ReadOnly(WallParameters.BaseAttachedTo, () => AttachmentName(document, BaseAttachedTo));

        yield return ParameterValue.BindChoiceCommand(
            WallParameters.LocationLine,
            () => EnumText.Humanise(LocationLine),
            v => EnumText.TryParse<WallLocationLine>(v, out var line)
                ? new ChangeLocationLineCommand(document, this, line)
                : null,
            EnumText.Choices<WallLocationLine>());
        // How far the wall stands off the line it was drawn on, which is what lets a thin wall
        // run flush with the face of a thicker one it meets.
        yield return ParameterValue.Bind(WallParameters.AcrossOffset, () => AcrossOffset, v => AcrossOffset = v);

        yield return ParameterValue.Bind(WallParameters.RoomBounding, () => RoomBounding, v => RoomBounding = v);

        // Dimensions - all computed, never stored. Length and height can be typed all the
        // same, as in Revit: a new length moves the wall's end, and the ends of the walls that
        // meet it there; a new height moves its top, by its top offset or its unconnected height.
        // A curved wall's length, and the height of a wall whose top follows something over it
        // or an outline of its own, are what those make them, and are shown, not set.
        yield return IsCurved
            ? ParameterValue.ReadOnly(WallParameters.Length, () => Length)
            : ParameterValue.BindCommand<double>(WallParameters.Length, () => Length, length => LengthChange(document, length));
        yield return TopAttachedTo is null && Profile is null
            ? ParameterValue.BindValidated<double>(WallParameters.Height, () => GetHeight(document), height => TrySetHeight(document, height))
            : ParameterValue.ReadOnly(WallParameters.Height, () => GetHeight(document));
        yield return ParameterValue.ReadOnly(WallParameters.Area, () => GetArea(document));
        yield return ParameterValue.ReadOnly(WallParameters.Volume, () => GetVolume(document));
        if (CurtainLayout.Of(document, this) is not null)
        {
            // What the whole wall is glazed with. A panel given glass of its own keeps it; the
            // rest follow this, so one choice reglazes the wall.
            yield return ParameterValue.BindChoice(
                WallParameters.CurtainGlass,
                () => EnumText.Humanise(CurtainGlass),
                v => { if (EnumText.TryParse<CurtainGlass>(v, out var glass)) CurtainGlass = glass; },
                EnumText.Choices<CurtainGlass>());

            yield return ParameterValue.ReadOnly(WallParameters.CurtainPanels, () => CurtainLayout.Of(document, this)?.PanelCount ?? 0);
            yield return ParameterValue.ReadOnly(WallParameters.MullionLength, () => CurtainLayout.Of(document, this)?.MullionLength ?? 0);
        }

        yield return ParameterValue.ReadOnly(WallParameters.Profile, () => WallProfile.Of(document, this) is null
            ? Profile is null ? "Rectangular" : "Edited (not in use: the wall must be straight, upright and not stacked)"
            : Profile is null ? "Follows the roof it is attached to" : "Edited");

        // Joins - one setting per end, kept in step with the wall at the other side of it
        foreach (var atStart in new[] { true, false })
        {
            var end = atStart;
            yield return ParameterValue.BindChoiceCommand(
                atStart ? WallParameters.StartJoin : WallParameters.EndJoin,
                () => EnumText.Humanise(end ? StartJoin : EndJoin),
                v => EnumText.TryParse<WallJoinKind>(v, out var kind)
                    ? new SetWallJoinCommand(document, this, end, kind)
                    : null,
                EnumText.Choices<WallJoinKind>());
        }

        // Analytical
        yield return ParameterValue.BindChoice(
            WallParameters.StructuralUsage,
            () => EnumText.Humanise(StructuralUsage),
            v => { if (EnumText.TryParse<StructuralUsage>(v, out var u)) StructuralUsage = u; },
            EnumText.Choices<StructuralUsage>());

        foreach (var parameter in GetCommonParameters(document)) yield return parameter;
    }
}

public static class WallParameters
{
    public static readonly ParameterDefinition BaseConstraint =
        new("Base Constraint", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Constraints);

    public static readonly ParameterDefinition BaseOffset =
        new("Base Offset", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Constraints);

    public static readonly ParameterDefinition TopConstraint =
        new("Top Constraint", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Constraints);

    public static readonly ParameterDefinition TopOffset =
        new("Top Offset", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Constraints);

    public static readonly ParameterDefinition UnconnectedHeight =
        new("Unconnected Height", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Constraints);

    public static readonly ParameterDefinition LocationLine =
        new("Location Line", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Constraints);

    public static readonly ParameterDefinition RoomBounding =
        new("Room Bounding", ParameterDataType.YesNo, ParameterBinding.Instance, ParameterGroup.Constraints);

    public static readonly ParameterDefinition Length =
        new("Length", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);

    public static readonly ParameterDefinition Height =
        new("Height", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);

    public static readonly ParameterDefinition Area =
        new("Area", ParameterDataType.Area, ParameterBinding.Instance, ParameterGroup.Dimensions);

    public static readonly ParameterDefinition Volume =
        new("Volume", ParameterDataType.Volume, ParameterBinding.Instance, ParameterGroup.Dimensions);

    public static readonly ParameterDefinition Profile =
        new("Profile", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Dimensions);

    public static readonly ParameterDefinition AcrossOffset =
        new("Offset Across", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Constraints);

    public static readonly ParameterDefinition CurtainGlass =
        new("Glass", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.MaterialsAndFinishes);

    public static readonly ParameterDefinition CurtainPanels =
        new("Panels", ParameterDataType.Integer, ParameterBinding.Instance, ParameterGroup.Dimensions);

    public static readonly ParameterDefinition MullionLength =
        new("Mullion Length", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);

    public static readonly ParameterDefinition CrossSection =
        new("Cross-Section", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Constraints);

    public static readonly ParameterDefinition SlantAngle =
        new("Angle from Vertical", ParameterDataType.Angle, ParameterBinding.Instance, ParameterGroup.Constraints);

    public static readonly ParameterDefinition JoinedWalls =
        new("Joined Walls", ParameterDataType.Integer, ParameterBinding.Instance, ParameterGroup.Constraints);

    public static readonly ParameterDefinition LockedToJoined =
        new("Locked to Joined Walls", ParameterDataType.YesNo, ParameterBinding.Instance, ParameterGroup.Constraints);

    public static readonly ParameterDefinition LowerSlantAngle =
        new("Lower Angle from Vertical", ParameterDataType.Angle, ParameterBinding.Instance, ParameterGroup.Constraints);

    public static readonly ParameterDefinition UpperSlantAngle =
        new("Upper Angle from Vertical", ParameterDataType.Angle, ParameterBinding.Instance, ParameterGroup.Constraints);

    public static readonly ParameterDefinition SlantBreakHeight =
        new("Slant Break Height", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Constraints);

    public static readonly ParameterDefinition OverrideTaper =
        new("Override Type Taper", ParameterDataType.YesNo, ParameterBinding.Instance, ParameterGroup.Constraints);

    public static readonly ParameterDefinition ExteriorTaper =
        new("Exterior Taper", ParameterDataType.Angle, ParameterBinding.Instance, ParameterGroup.Constraints);

    public static readonly ParameterDefinition InteriorTaper =
        new("Interior Taper", ParameterDataType.Angle, ParameterBinding.Instance, ParameterGroup.Constraints);

    public static readonly ParameterDefinition TopAttachedTo =
        new("Top Attached To", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Constraints);

    public static readonly ParameterDefinition BaseAttachedTo =
        new("Base Attached To", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Constraints);

    public static readonly ParameterDefinition StartJoin =
        new("Start Join", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Constraints);

    public static readonly ParameterDefinition EndJoin =
        new("End Join", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Constraints);

    public static readonly ParameterDefinition StructuralUsage =
        new("Structural Usage", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Analytical);
}
