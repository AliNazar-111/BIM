using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;
using BIMDesigner.Core.Parameters;

namespace BIMDesigner.Core.Architecture;

/// <summary>The section a curtain wall mullion is made in - straight, round, or one of the corner sections.</summary>
public enum MullionShape
{
    /// <summary>A box: so wide each side of its grid line, so deep through the wall - with a cover cap on its face if it has one.</summary>
    Rectangular,

    /// <summary>A round tube, its diameter its width.</summary>
    Circular,

    /// <summary>L corner: two legs along the two walls, meeting at their outside corner.</summary>
    CornerL,

    /// <summary>V corner: two legs along the two walls, their outside corner cut off square to the angle between them.</summary>
    CornerV,

    /// <summary>Trapezoid corner: one face across the outside of the corner, from leg end to leg end.</summary>
    CornerTrapezoid,

    /// <summary>Quad corner: a block on the corner, so long along each wall.</summary>
    CornerQuad
}

/// <summary>What a mullion is made of, for its stiffness: aluminium extrusion, steel or timber.</summary>
public enum MullionMaterial
{
    Aluminium,
    Steel,
    Timber
}

/// <summary>Where a curtain wall's vertical mullions are spliced, one length to the next: nowhere, above every floor, above every other floor.</summary>
public enum MullionSplices
{
    None,
    EveryFloor,
    EveryOtherFloor
}

/// <summary>Which mullions run through where they meet: the verticals, the transoms stopping at them, or the other way.</summary>
public enum CurtainJoin
{
    VerticalContinuous,
    HorizontalContinuous
}

/// <summary>
/// A curtain wall mullion type (Revit's Curtain Wall Mullions): its section - a box so wide each
/// side of its grid line and so deep, offset in or out, with a cover cap on its face; a round
/// tube; or a corner section where two curtain walls meet - what it is made of and how thick its
/// extrusion's wall is, which is what it bends by.
/// </summary>
public sealed class MullionType : ElementType
{
    public MullionType(string name) : base(name)
    {
    }

    public override BuiltInCategory Category => BuiltInCategory.CurtainWallMullions;

    public MullionShape Shape { get; set; } = MullionShape.Rectangular;

    /// <summary>How far its face reaches each side of its grid line, mm: toward the wall's start (or below), and toward its end (or above). A round one's diameter is both together.</summary>
    public double Width1 { get; set; } = 25;

    public double Width2 { get; set; } = 25;

    /// <summary>How deep it is through the wall, mm.</summary>
    public double Thickness { get; set; } = 150;

    /// <summary>How far its middle is out from the panels' middle, toward the exterior, mm.</summary>
    public double Offset { get; set; }

    /// <summary>A corner section's legs along the two walls, mm.</summary>
    public double Leg1 { get; set; } = 100;

    public double Leg2 { get; set; } = 100;

    /// <summary>How far a cover cap - the pressure plate's cap on a stick system - stands out from its outside face, mm; none at 0.</summary>
    public double CapDepth { get; set; }

    /// <summary>The wall of a hollow extrusion, mm; nothing for a solid section.</summary>
    public double WallThickness { get; set; } = 3;

    public MullionMaterial Material { get; set; } = MullionMaterial.Aluminium;

    public Guid MaterialId { get; set; }

    /// <summary>
    /// How wide it is seen face-on, mm - for a corner one, how far along each wall from the corner
    /// it reaches, which is where the panels start: a quad half its leg, the others their leg less
    /// the outside corner they start from, out beyond the walls' lines.
    /// </summary>
    public double FaceWidth => Shape switch
    {
        MullionShape.CornerQuad => Math.Min(Leg1, Leg2) / 2,
        MullionShape.CornerL or MullionShape.CornerV or MullionShape.CornerTrapezoid => Math.Max(20, Math.Min(Leg1, Leg2) - Offset - Thickness / 2),
        _ => Width1 + Width2
    };

    /// <summary>Whether it is a corner section, for where two curtain walls meet.</summary>
    public bool IsCorner => Shape is MullionShape.CornerL or MullionShape.CornerV or MullionShape.CornerTrapezoid or MullionShape.CornerQuad;

    /// <summary>How deep it reaches through the wall, cap and all: the wall's thickness for joining and rooms.</summary>
    public double Depth => (Shape == MullionShape.Circular ? FaceWidth : Thickness) + CapDepth;

    /// <summary>Its stiffness, N/mm²: aluminium 70,000, steel 210,000, timber 11,000.</summary>
    public double Modulus => Material switch
    {
        MullionMaterial.Steel => 210000,
        MullionMaterial.Timber => 11000,
        _ => 70000
    };

    /// <summary>
    /// Its section, across its face (along the wall, or up for a transom) and through the wall,
    /// about its grid line and the panels' middle: the outline and its hollow - a corner one taken
    /// as one leg, square across the wall.
    /// </summary>
    public ColumnProfile Section()
    {
        var hollow = Material != MullionMaterial.Timber && WallThickness > 0;
        switch (Shape)
        {
            case MullionShape.Circular:
            {
                var outer = ColumnProfile.Ellipse(FaceWidth, FaceWidth).Outer.Select(point => new Point2D(point.X + (Width2 - Width1) / 2, point.Y + Offset)).ToList();
                if (!hollow || 2 * WallThickness >= FaceWidth) return new ColumnProfile(outer);
                var inner = ColumnProfile.Ellipse(FaceWidth - 2 * WallThickness, FaceWidth - 2 * WallThickness).Outer
                    .Select(point => new Point2D(point.X + (Width2 - Width1) / 2, point.Y + Offset)).Reverse().ToList();
                return new ColumnProfile(outer, new[] { inner });
            }

            default:
            {
                var (from, to) = IsCorner ? (-Thickness / 2, Thickness / 2) : (-Width1, Width2);
                var (low, high) = IsCorner ? (Offset - Math.Max(Leg1, Leg2) / 2, Offset + Math.Max(Leg1, Leg2) / 2) : (Offset - Thickness / 2, Offset + Thickness / 2);
                var outer = new List<Point2D> { new(from, low), new(to, low), new(to, high), new(from, high) };
                if (!hollow || 2 * WallThickness >= Math.Min(to - from, high - low)) return new ColumnProfile(outer);
                var t = WallThickness;
                var inner = new List<Point2D> { new(from + t, low + t), new(from + t, high - t), new(to - t, high - t), new(to - t, low + t) };
                return new ColumnProfile(outer, new[] { inner });
            }
        }
    }

    public MullionType Duplicate(string name) => WithCommonOf(new MullionType(name)
    {
        Shape = Shape, Width1 = Width1, Width2 = Width2, Thickness = Thickness, Offset = Offset, Leg1 = Leg1, Leg2 = Leg2,
        CapDepth = CapDepth, WallThickness = WallThickness, Material = Material, MaterialId = MaterialId
    });

    public override IEnumerable<ParameterValue> GetTypeParameters(BimDocument? document = null)
    {
        // A curtain wall is as thick as its deepest mullion: those framed with this one follow a change to its depth.
        void Retake()
        {
            if (document is null) return;
            foreach (var curtain in document.TypesOf<CurtainWallType>()) curtain.TakeDepth(document);
        }

        yield return ParameterValue.BindChoice(MullionParameters.Shape, () => EnumText.Humanise(Shape),
            v => { if (EnumText.TryParse<MullionShape>(v, out var shape)) Shape = shape; Retake(); }, EnumText.Choices<MullionShape>());
        if (IsCorner)
        {
            yield return Size(MullionParameters.Leg1, () => Leg1, v => Leg1 = v, 10, 1000);
            yield return Size(MullionParameters.Leg2, () => Leg2, v => Leg2 = v, 10, 1000);
        }
        else
        {
            yield return Size(MullionParameters.Width1, () => Width1, v => Width1 = v, 1, 500);
            yield return Size(MullionParameters.Width2, () => Width2, v => Width2 = v, 1, 500);
        }

        if (Shape != MullionShape.Circular) yield return Size(MullionParameters.Thickness, () => Thickness, v => { Thickness = v; Retake(); }, 5, 1000);
        yield return ParameterValue.BindValidated(MullionParameters.Offset, () => Offset, v => { if (Math.Abs(v) > 1000) return false; Offset = v; Retake(); return true; });
        if (!IsCorner) yield return Size(MullionParameters.CapDepth, () => CapDepth, v => { CapDepth = v; Retake(); }, 0, 200);
        yield return ParameterValue.BindChoice(MullionParameters.Material, () => EnumText.Humanise(Material),
            v => { if (EnumText.TryParse<MullionMaterial>(v, out var made)) Material = made; }, EnumText.Choices<MullionMaterial>());
        if (Material != MullionMaterial.Timber) yield return Size(MullionParameters.WallThickness, () => WallThickness, v => WallThickness = v, 0, 50);
        if (RoofEdgeSweepParameters.MaterialOf(document, () => MaterialId, id => MaterialId = id, MullionParameters.Finish) is { } finish) yield return finish;
        var section = Section().Properties();
        yield return ParameterValue.ReadOnly(MullionParameters.Stiffness, () => Math.Round(section.Ixx / 1e4, 1));
        foreach (var parameter in GetCommonTypeParameters()) yield return parameter;
    }

    private static ParameterValue Size(ParameterDefinition definition, Func<double> get, Action<double> set, double least, double most) =>
        ParameterValue.BindValidated(definition, get, v =>
        {
            if (v < least || v > most) return false;
            set(v);
            return true;
        });
}

/// <summary>
/// A curtain wall's own choice for one stretch of one of its grid lines - between the lines
/// crossing it - over what its type puts there: another mullion type, or none, the panels either
/// side butted. Kept by where the stretch is, as a stretch taken out of the grid is.
/// </summary>
public readonly record struct CurtainMullionOverride(bool Vertical, double Line, double At, Guid? TypeId)
{
    /// <summary>Whether this is the choice for the stretch of a line at this place that a point along it falls in.</summary>
    public bool Covers(bool vertical, double line, double from, double to) =>
        Vertical == vertical && Math.Abs(Line - line) < 0.5 && At >= Math.Min(from, to) - 1e-6 && At <= Math.Max(from, to) + 1e-6;
}

public static class MullionParameters
{
    public static readonly ParameterDefinition Shape = new("Profile", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.Construction);
    public static readonly ParameterDefinition Width1 = new("Width on Side 1", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Width2 = new("Width on Side 2", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Thickness = new("Thickness", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Offset = new("Offset", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Leg1 = new("Leg 1", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Leg2 = new("Leg 2", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition CapDepth = new("Cover Cap Depth (0 = none)", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition WallThickness = new("Extrusion Wall (0 = solid)", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Material = new("Structural Material", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.MaterialsAndFinishes);
    public static readonly ParameterDefinition Finish = new("Material", ParameterDataType.Material, ParameterBinding.Type, ParameterGroup.MaterialsAndFinishes);
    public static readonly ParameterDefinition Stiffness = new("Second Moment Across the Wall (cm⁴)", ParameterDataType.Number, ParameterBinding.Type, ParameterGroup.Analytical);
}

/// <summary>
/// Where two curtain walls meet at a corner, and the corner mullion between them: which wall
/// draws it, and its outline in plan from the two walls' directions.
/// </summary>
public static class CurtainCorners
{
    /// <summary>The other curtain wall meeting a curtain wall's end at an angle, and which of its ends it is; null where none does.</summary>
    public static (Wall Other, bool OtherAtStart)? Partner(BimDocument document, Wall wall, bool atStart)
    {
        if (wall.IsCurved) return null;
        var end = atStart ? wall.Start : wall.End;
        var way = (wall.End - wall.Start).NormalisedOrDefault(Vector2D.UnitX);
        foreach (var other in document.Walls)
        {
            if (ReferenceEquals(other, wall) || other.IsCurved || other.LevelId != wall.LevelId || !document.IsCurtainWall(other)) continue;
            var otherWay = (other.End - other.Start).NormalisedOrDefault(Vector2D.UnitX);
            if (Math.Abs(way.Cross(otherWay)) < Math.Sin(5 * Math.PI / 180)) continue;
            if (other.Start.DistanceTo(end) < 50) return (other, true);
            if (other.End.DistanceTo(end) < 50) return (other, false);
        }

        return null;
    }

    /// <summary>
    /// The corner mullion a curtain wall's end has, and whether this wall is the one to build it:
    /// where either wall's border mullion at the corner is a corner section, one corner mullion
    /// stands there, built by the wall whose id comes first, and neither builds its own end mullion.
    /// </summary>
    public static (MullionType Type, bool Builds, Wall Other, bool OtherAtStart)? At(BimDocument document, Wall wall, bool atStart)
    {
        if (Partner(document, wall, atStart) is not var (other, otherAtStart)) return null;
        var mine = BorderType(document, wall, atStart);
        var theirs = BorderType(document, other, otherAtStart);
        var corner = mine is { IsCorner: true } ? mine : theirs is { IsCorner: true } ? theirs : null;
        if (corner is null) return null;
        var builds = mine is { IsCorner: true } && (theirs is not { IsCorner: true } || wall.Id.CompareTo(other.Id) < 0);
        return (corner, builds, other, otherAtStart);
    }

    /// <summary>The border mullion type a curtain wall has at an end of it, as its type puts it there.</summary>
    private static MullionType? BorderType(BimDocument document, Wall wall, bool atStart) =>
        document.FindType<CurtainWallType>(wall.TypeId) is { } type
            ? type.Resolve(document, atStart ? type.BorderVerticalStart : type.BorderVerticalEnd, border: true)
            : null;

    /// <summary>A corner mullion's outline in plan, at the corner of two walls, by its section.</summary>
    public static IReadOnlyList<Point2D> Outline(BimDocument document, Wall wall, bool atStart, Wall other, bool otherAtStart, MullionType type)
    {
        var bodyA = document.GetWallType(wall)!.Structure;
        var bodyB = document.GetWallType(other)!.Structure;
        var (endA, endB) = (atStart ? 0 : wall.Length, otherAtStart ? 0 : other.Length);
        var corner = wall.PointAt(bodyA, endA, 0);

        // Back along each wall from the corner, and out of each toward its exterior.
        var intoA = (wall.PointAt(bodyA, atStart ? Math.Min(1000, wall.Length) : wall.Length - Math.Min(1000, wall.Length), 0) - corner).NormalisedOrDefault(Vector2D.UnitX);
        var intoB = (other.PointAt(bodyB, otherAtStart ? Math.Min(1000, other.Length) : other.Length - Math.Min(1000, other.Length), 0) - other.PointAt(bodyB, endB, 0)).NormalisedOrDefault(Vector2D.UnitY);
        var outA = (wall.PointAt(bodyA, endA, 1) - corner).NormalisedOrDefault(intoA.PerpendicularLeft());
        var outB = (other.PointAt(bodyB, endB, 1) - other.PointAt(bodyB, endB, 0)).NormalisedOrDefault(intoB.PerpendicularLeft());

        var (t, o) = (type.Thickness, type.Offset);
        Point2D Meet(Point2D a, Vector2D da, Point2D b, Vector2D db)
        {
            var denominator = da.Cross(db);
            if (Math.Abs(denominator) < 1e-9) return a;
            return a + da * ((b - a).Cross(db) / denominator);
        }

        var outer = Meet(corner + outA * (o + t / 2), intoA, corner + outB * (o + t / 2), intoB);
        var inner = Meet(corner + outA * (o - t / 2), intoA, corner + outB * (o - t / 2), intoB);
        var (endOnA, endOnB) = (outer + intoA * type.Leg1, outer + intoB * type.Leg2);
        var (backOnA, backOnB) = (endOnA - outA * t, endOnB - outB * t);

        List<Point2D> points = type.Shape switch
        {
            MullionShape.CornerQuad => new List<Point2D>
            {
                corner - intoA * (type.Leg1 / 2) - intoB * (type.Leg2 / 2), corner + intoA * (type.Leg1 / 2) - intoB * (type.Leg2 / 2),
                corner + intoA * (type.Leg1 / 2) + intoB * (type.Leg2 / 2), corner - intoA * (type.Leg1 / 2) + intoB * (type.Leg2 / 2)
            },
            MullionShape.CornerTrapezoid => new List<Point2D> { endOnA, backOnA, inner, backOnB, endOnB },
            MullionShape.CornerV => new List<Point2D> { outer + intoA * Math.Min(t, type.Leg1 / 2), endOnA, backOnA, inner, backOnB, endOnB, outer + intoB * Math.Min(t, type.Leg2 / 2) },
            _ => new List<Point2D> { outer, endOnA, backOnA, inner, backOnB, endOnB }
        };
        if (Polygon2D.SignedArea(points) < 0) points.Reverse();
        return points;
    }
}

/// <summary>
/// How far a curtain wall's mullions and transoms bend, against the limits the industry holds
/// them to. Under wind each is a beam simply supported between its supports - a mullion between
/// the floors it spans, a transom between the mullions - carrying the pressure on half the panels
/// either side: 5wL⁴/384EI. In England, as the CWCT's standard for building envelopes has it,
/// span/200 up to 3 m, 5 mm + span/300 up to 7.5 m, span/250 beyond; in the US, span/175 or 3/4
/// in, whichever is less (IBC 2403.3), and span/240 + 1/4 in over 13 ft 6 in (AAMA TIR-A11).
/// Under the glass it carries, a transom sags between its setting blocks - half the pane's weight
/// on each, a quarter of the span in from its ends: span/500 or 3 mm in England, span/360 or
/// 1/8 in in the US.
/// </summary>
public static class MullionRules
{
    /// <summary>A pane's glass, kN/m²: a double-glazed unit's two 6 mm panes - or a thinner panel all glass - at 25 kN/m³.</summary>
    public static double GlassWeight(CurtainWallType type) => 25 * Math.Min(type.PanelThickness >= 20 ? 12 : type.PanelThickness, 12) / 1000;

    /// <summary>How far a member may bend across the wall under wind, mm, over a span, mm.</summary>
    public static double WindLimit(double span, bool us) => us
        ? span <= 4115 ? Math.Min(span / 175, 19.05) : span / 240 + 6.35
        : span <= 3000 ? span / 200 : span <= 7500 ? 5 + span / 300 : span / 250;

    /// <summary>How far a transom may sag under its glass, mm.</summary>
    public static double DeadLimit(double span, bool us) => us ? Math.Min(span / 360, 3.175) : Math.Min(span / 500, 3);

    /// <summary>One member's bending: where it is, its span, what it bends and what it may.</summary>
    public sealed record Bending(CurtainMullion Mullion, string What, double Span, double Deflection, double Allowed, string Rule, double Pressure = 0, string Wind = "")
    {
        public bool TooMuch => Deflection > Allowed + 0.05;
    }

    /// <summary>
    /// Every member of a curtain wall and how far it bends: each mullion and transom under wind -
    /// on an exterior wall, the only one the wind reaches - each transom under its glass.
    /// </summary>
    public static IReadOnlyList<Bending> Of(BimDocument document, Wall wall, bool? usRules = null)
    {
        var found = new List<Bending>();
        if (CurtainLayout.Of(document, wall) is not { } layout) return found;
        var us = usRules ?? document.ProjectInformation.ClearanceStandard.IsUs();
        var exterior = layout.Type.Function == WallFunction.Exterior;

        var weight = GlassWeight(layout.Type) / 1000;
        var windRule = us
            ? "IBC 2403.3 holds framing carrying glass to span/175 or 3/4 in under wind - 0.6 times the components and cladding load, as IBC 1605 combines it - and AAMA TIR-A11 a span over 13 ft 6 in to span/240 + 1/4 in"
            : "the CWCT's standard for building envelopes holds a mullion under wind to span/200 up to 3 m, 5 mm + span/300 up to 7.5 m and span/250 beyond";
        var deadRule = us
            ? "AAMA TIR-A11 holds a transom under its glass to span/360 or 1/8 in"
            : "a transom under its glass is held to span/500 or 3 mm";

        // Where a vertical mullion is held: the wall's base and top, and the floors it passes.
        var supports = MullionFixings.Supports(document, wall, layout);

        double Half(IReadOnlyList<double> lines, double at)
        {
            var before = lines.Where(line => line < at - 1).DefaultIfEmpty(at).Max();
            var after = lines.Where(line => line > at + 1).DefaultIfEmpty(at).Min();
            return (at - before) / 2 + (after - at) / 2;
        }

        foreach (var mullion in layout.Mullions)
        {
            if (mullion.Type is not { } type || mullion.IsSloped) continue;
            var section = type.Section().Properties();
            if (section.Area <= 0) continue;
            var middle = (mullion.From + mullion.To) / 2;

            if (mullion.IsVertical)
            {
                if (!exterior) continue;
                var span = 0.0;
                var points = supports.Where(height => height >= mullion.Bottom - 1 && height <= mullion.Top + 1).ToList();
                if (points.Count < 2) points = new List<double> { mullion.Bottom, mullion.Top };
                for (var i = 0; i + 1 < points.Count; i++) span = Math.Max(span, points[i + 1] - points[i]);
                var width = Half(layout.Verticals, layout.Verticals.OrderBy(line => Math.Abs(line - middle)).First());
                var wind = Wind.OnMember(document, wall, middle, span, width);
                var load = wind.Pressure / 1000 * width;
                var deflection = 5 * load * Math.Pow(span, 4) / (384 * type.Modulus * section.Ixx);
                found.Add(new Bending(mullion, "mullion", span, deflection, WindLimit(span, us), windRule, wind.Pressure, wind.Said));
            }
            else
            {
                // A transom spans between the mullions holding it up.
                var line = layout.Horizontals.OrderBy(height => Math.Abs(height - (mullion.Bottom + mullion.Top) / 2)).First();
                var held = layout.Verticals.Where(along => along >= mullion.From - 200 && along <= mullion.To + 200).ToList();
                var span = held.Count >= 2 ? held.Zip(held.Skip(1), (a, b) => b - a).Max() : mullion.To - mullion.From;
                var width = Half(layout.Horizontals, line);
                var wind = Wind.OnMember(document, wall, middle, span, width);
                var bent = 5 * wind.Pressure / 1000 * width * Math.Pow(span, 4) / (384 * type.Modulus * section.Ixx);
                if (exterior) found.Add(new Bending(mullion, "transom", span, bent, WindLimit(span, us), windRule, wind.Pressure, wind.Said));

                // The pane above it stands on it: its weight in two halves, a quarter in from each end.
                var above = layout.Cells.Where(cell => Math.Abs(cell.Bottom - line) < 1 && cell.From < mullion.To && cell.To > mullion.From && cell.Kind != CurtainPanelKind.Empty).ToList();
                if (above.Count > 0)
                {
                    var pane = above.MaxBy(cell => (cell.ClearTo - cell.ClearFrom) * (cell.ClearTop - cell.ClearBottom))!;
                    var force = weight * (pane.ClearTo - pane.ClearFrom) * (pane.ClearTop - pane.ClearBottom) / 2;
                    var a = span / 4;
                    var sag = force * a * (3 * span * span - 4 * a * a) / (24 * type.Modulus * section.Iyy);
                    found.Add(new Bending(mullion, "transom under its glass", span, sag, DeadLimit(span, us), deadRule));
                }
            }
        }

        return found;
    }

    /// <summary>What a curtain wall's mullions fall short of: said, and the rule.</summary>
    public static IReadOnlyList<(string Said, string Rule)> Problems(BimDocument document, Wall wall, bool? usRules = null)
    {
        var problems = new List<(string, string)>();
        foreach (var group in Of(document, wall, usRules).Where(bending => bending.TooMuch).GroupBy(bending => bending.What))
        {
            var worst = group.MaxBy(bending => bending.Deflection / Math.Max(bending.Allowed, 1e-6))!;
            var name = worst.Mullion.Type?.Name ?? "Mullion";
            var (many, under) = group.Key == "transom under its glass"
                ? ($"{group.Count()} transoms sag", "under the glass on it")
                : ($"{group.Count()} {group.Key}s bend", $"at {worst.Pressure:0.00} kPa of wind ({worst.Wind})");
            var said = group.Count() == 1 ? (group.Key == "transom under its glass" ? "a transom sags" : $"a {group.Key} bends") : many;
            problems.Add(($"{said} too far - the worst, {name}, {Units.Millimetres(worst.Deflection, "0.0")} over {Units.Metres(worst.Span, "0.00")}, {Units.Bare(worst.Allowed, "0.0")} allowed, {under}.",
                $"{char.ToUpperInvariant(worst.Rule[0])}{worst.Rule[1..]} - a deeper or stiffer mullion type, closer grid lines, or a support at the floor."));
        }

        return problems;
    }

    /// <summary>The worst of a curtain wall's members, as its Properties say it.</summary>
    public static string Verdict(BimDocument document, Wall wall)
    {
        var all = Of(document, wall);
        if (all.Count == 0) return "No mullions";
        var worst = all.MaxBy(bending => bending.Deflection / Math.Max(bending.Allowed, 1e-6))!;
        return $"{(worst.TooMuch ? "Too far" : "Within limits")}: its worst, a {worst.What}, bends {Units.Millimetres(worst.Deflection, "0.0")} over {Units.Metres(worst.Span, "0.00")}, {Units.Bare(worst.Allowed, "0.0")} allowed";
    }
}

/// <summary>An anchor holding a vertical mullion to the building at a floor, its base or its head - how high up the wall, and what it takes: wind, kN, both ways, and the weight of the length it carries, kN.</summary>
public sealed record MullionAnchor(CurtainMullion Mullion, double Height, double Wind, double Dead)
{
    /// <summary>Whether it carries a length's weight as well as the wind - the anchor at its foot; the rest are slotted to let it move up and down.</summary>
    public bool CarriesWeight => Dead > 0;
}

/// <summary>A splice in a vertical mullion: how high up the wall, and the stack joint there.</summary>
public sealed record MullionSplice(CurtainMullion Mullion, double Height, double Joint);

/// <summary>
/// How a curtain wall's vertical mullions are fixed to the building and joined up its height.
///
/// Each is anchored at every floor it passes, its base and its head: every anchor takes the wind on
/// half the span either side of it; the anchor at the foot of each length also takes its weight -
/// the glass either side and its own - and the others are slotted, so the length can move up and
/// down. Each length is spliced to the next a little above a floor, a stack joint between them for
/// the length to grow and shrink with the heat - a metal face goes from -17 to 82 °C (AAMA's curtain
/// wall guide specification), aluminium moving 0.023 mm a metre a degree, steel 0.012, timber 0.005 -
/// for the floor's edge to deflect under its live load, held to 1/2 in, and the column under it to
/// shorten, 1/8 in a floor. A length is no longer than the 24 ft (7.3 m) aluminium mullions are
/// sold in.
/// </summary>
public static class MullionFixings
{
    /// <summary>The range of temperature a curtain wall's metal face sees, K.</summary>
    public const double TemperatureRange = 99;

    /// <summary>A slab edge's live-load deflection and a column's shortening at each floor, mm, that a stack joint takes as well.</summary>
    public const double FloorMovement = 12.7 + 3.2;

    /// <summary>The longest aluminium mullion sold, mm: 24 ft.</summary>
    public const double StockLength = 7300;

    /// <summary>Where a vertical mullion is held up the wall, from its base: its base and top, and the floors in between.</summary>
    public static IReadOnlyList<double> Supports(BimDocument document, Wall wall, CurtainLayout layout)
    {
        var bottom = wall.GetBaseElevation(document);
        var supports = new List<double> { 0, layout.Height };
        supports.AddRange(document.Levels.Select(level => level.Elevation - bottom).Where(height => height > 100 && height < layout.Height - 100));
        return supports.Distinct().OrderBy(height => height).ToList();
    }

    /// <summary>How much a material grows, mm per mm per degree.</summary>
    public static double Expansion(MullionMaterial material) => material switch
    {
        MullionMaterial.Steel => 12e-6,
        MullionMaterial.Timber => 5e-6,
        _ => 23e-6
    };

    /// <summary>How heavy a material is, kg/m³.</summary>
    private static double Density(MullionMaterial material) => material switch
    {
        MullionMaterial.Steel => 7850,
        MullionMaterial.Timber => 500,
        _ => 2700
    };

    /// <summary>How wide a stack joint has to be for a length this long, mm: its movement with the heat, and the floor's.</summary>
    public static double JointNeeded(MullionMaterial material, double length) => Expansion(material) * length * TemperatureRange + FloorMovement;

    /// <summary>
    /// A curtain wall's vertical mullions as they are fixed: the anchors on each - if its type
    /// anchors them - the splices up each, and each length between them, from the bottom up.
    /// A corner mullion is fixed by the wall that builds it.
    /// </summary>
    public static (IReadOnlyList<MullionAnchor> Anchors, IReadOnlyList<MullionSplice> Splices, IReadOnlyList<(CurtainMullion Mullion, double From, double To)> Lengths) Of(
        BimDocument document, Wall wall, CurtainLayout? given = null)
    {
        var (anchors, splices, lengths) = (new List<MullionAnchor>(), new List<MullionSplice>(), new List<(CurtainMullion, double, double)>());
        if ((given ?? CurtainLayout.Of(document, wall)) is not { } layout) return (anchors, splices, lengths);

        var type = layout.Type;
        var supports = Supports(document, wall, layout);
        var corners = new[] { true, false }.ToDictionary(atStart => atStart, atStart => CurtainCorners.At(document, wall, atStart));
        var weight = MullionRules.GlassWeight(type) / 1000;

        foreach (var mullion in layout.Mullions.Where(m => m.IsVertical && !m.IsSloped && m.Type is not null))
        {
            var made = mullion.Type!;
            if (mullion.IsBorder && corners[mullion.From <= 1] is { } corner)
            {
                if (!corner.Builds) continue;
                made = corner.Type;
            }

            var held = supports.Where(height => height >= mullion.Bottom - 1 && height <= mullion.Top + 1).ToList();
            if (held.Count < 2) held = new List<double> { mullion.Bottom, mullion.Top };

            // Spliced a little above the floors it passes - every one, or every other.
            var cuts = new List<double>();
            if (type.Splices != MullionSplices.None)
                for (var i = 1; i + 1 < held.Count; i++)
                    if (type.Splices == MullionSplices.EveryFloor || i % 2 == 0)
                        cuts.Add(Math.Min(held[i] + type.SpliceAbove, held[i + 1] - 100));
            splices.AddRange(cuts.Select(height => new MullionSplice(mullion, height, type.StackJoint)));

            var ends = cuts.Prepend(mullion.Bottom).Append(mullion.Top).ToList();
            for (var i = 0; i + 1 < ends.Count; i++) lengths.Add((mullion, ends[i], ends[i + 1]));

            if (!type.MullionAnchors) continue;

            // The wind on half the span either side of each anchor; the weight of each length on the anchor at its foot.
            var middle = (mullion.From + mullion.To) / 2;
            var lines = layout.Verticals;
            var before = lines.Where(line => line < middle - 1).DefaultIfEmpty(middle).Max();
            var after = lines.Where(line => line > middle + 1).DefaultIfEmpty(middle).Min();
            var width = (middle - before) / 2 + (after - middle) / 2;
            var span = held.Zip(held.Skip(1), (a, b) => b - a).Max();
            var pressure = Wind.OnMember(document, wall, middle, span, width).Pressure / 1000;
            var area = made.Section().Properties().Area;

            for (var i = 0; i < held.Count; i++)
            {
                var below = i > 0 ? held[i] - held[i - 1] : 0;
                var above = i + 1 < held.Count ? held[i + 1] - held[i] : 0;
                var wind = pressure * width * (below + above) / 2 / 1000;

                // The anchor at the foot of a length carries it: the one at or just under its bottom.
                var dead = 0.0;
                for (var k = 0; k + 1 < ends.Count; k++)
                {
                    var foot = held.Where(height => height <= ends[k] + 1).DefaultIfEmpty(held[0]).Max();
                    if (Math.Abs(foot - held[i]) > 1e-6) continue;
                    var length = ends[k + 1] - ends[k];
                    dead += (weight * width * length + area * length * Density(made.Material) * 9.81e-9) / 1000;
                }

                anchors.Add(new MullionAnchor(mullion, held[i], wind, dead));
            }
        }

        return (anchors, splices, lengths);
    }

    /// <summary>What a curtain wall's fixings fall short of: its stack joints, its lengths, its anchors - said, and the rule.</summary>
    public static IReadOnlyList<(string Said, string Rule)> Problems(BimDocument document, Wall wall)
    {
        var problems = new List<(string, string)>();
        if (CurtainLayout.Of(document, wall) is not { } layout) return problems;
        var type = layout.Type;
        var (anchors, splices, lengths) = Of(document, wall, layout);

        // The longest length, its joint, and what it has to take.
        if (lengths.Count > 0)
        {
            var longest = lengths.MaxBy(piece => piece.To - piece.From);
            var length = longest.To - longest.From;
            var material = longest.Mullion.Type!.Material;

            if (splices.Count > 0 && JointNeeded(material, length) > type.StackJoint + 0.05)
                problems.Add(($"its stack joints are {Units.Millimetres(type.StackJoint, "0")}; a {Units.Metres(length, "0.00")} length of {EnumText.Humanise(material).ToLowerInvariant()} moves {Units.Millimetres(Expansion(material) * length * TemperatureRange, "0.0")} with the heat, and the floor's edge and column {Units.Bare(FloorMovement, "0.0")} more: {Units.Millimetres(JointNeeded(material, length), "0")} wanted.",
                    "A stack joint takes a length's movement over the -17 to 82 °C its face sees (AAMA's curtain wall guide specification), a slab edge's live-load deflection of 1/2 in and a column's shortening of 1/8 in at each floor - a wider joint, or splices more often."));

            if (material == MullionMaterial.Aluminium && length > StockLength + 0.5)
                problems.Add(($"its mullions run {Units.Metres(length, "0.00")} without a splice.",
                    $"Aluminium mullions are sold in 24 ft{Units.MetricAside("7.3 m")} lengths - splice them above the floors (Mullion Splices)."));
        }

        if (anchors.Count > 0 && type.AnchorCapacity > 0)
        {
            var worst = anchors.MaxBy(anchor => Math.Max(anchor.Wind, anchor.Dead))!;
            var load = Math.Max(worst.Wind, worst.Dead);
            if (load > type.AnchorCapacity + 1e-6)
                problems.Add(($"an anchor takes {load:0.00} kN - {worst.Wind:0.00} of wind, {worst.Dead:0.00} of weight - over the {type.AnchorCapacity:0.00} kN it is good for.",
                    "An anchor is to carry the wind on the spans either side of it and the weight of the length standing on it - a stronger anchor, or more of them."));
        }

        return problems;
    }

    /// <summary>A curtain wall's fixings as its Properties say them.</summary>
    public static string Verdict(BimDocument document, Wall wall)
    {
        if (CurtainLayout.Of(document, wall) is not { } layout) return string.Empty;
        var type = layout.Type;
        var (anchors, splices, lengths) = Of(document, wall, layout);
        var said = new List<string>
        {
            anchors.Count > 0
                ? $"{anchors.Count} anchors, the worst taking {anchors.Max(anchor => anchor.Wind):0.00} kN of wind and {anchors.Max(anchor => anchor.Dead):0.00} kN of weight"
                : "not anchored as drawn",
            splices.Count > 0
                ? $"{splices.Count} splices with {Units.Millimetres(type.StackJoint, "0")} stack joints, the longest length {Units.Metres(lengths.Max(piece => piece.To - piece.From), "0.00")}"
                : lengths.Count > 0 ? $"unspliced, the longest {Units.Metres(lengths.Max(piece => piece.To - piece.From), "0.00")}" : "no vertical mullions"
        };
        var text = string.Join("; ", said);
        text = $"{char.ToUpperInvariant(text[0])}{text[1..]}";
        var problems = Problems(document, wall);
        return problems.Count == 0 ? text : $"{text} - {string.Join(" ", problems.Select(problem => $"{char.ToUpperInvariant(problem.Said[0])}{problem.Said[1..]} {problem.Rule}"))}";
    }
}
