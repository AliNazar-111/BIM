using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;
using BIMDesigner.Core.Parameters;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// What the ground under the building is: Approved Document A's types of ground (Table 10) and
/// the IRC's classes of material (Table R401.4.1), side by side - one answer to both.
/// </summary>
public enum GroundType
{
    /// <summary>Rock, not inferior to sandstone, limestone or firm chalk (ADA I); sedimentary rock, 4,000 psf (IRC).</summary>
    Rock,

    /// <summary>Gravel, medium dense (ADA II); sandy gravel and gravel - GW, GP - 3,000 psf.</summary>
    Gravel,

    /// <summary>Sand, medium dense (ADA II); sand, silty and clayey sand, silty and clayey gravel - SW, SP, SM, SC, GM, GC - 2,000 psf.</summary>
    Sand,

    /// <summary>Stiff clay or sandy clay (ADA III); clay and silt - CL, ML, MH, CH - 1,500 psf.</summary>
    StiffClay,

    /// <summary>Firm clay or sandy clay (ADA IV); clay and silt, 1,500 psf.</summary>
    FirmClay,

    /// <summary>Loose sand, silty or clayey sand (ADA V); sand's class, 2,000 psf.</summary>
    LooseSand,

    /// <summary>Soft silt, clay or sandy clay (ADA VI); softer than the IRC's least presumptive value - a soils investigation.</summary>
    SoftClay,

    /// <summary>Very soft silt or clay (ADA VII): specialist advice, a soils investigation.</summary>
    VerySoft
}

/// <summary>How much a clay shrinks and swells as it dries and wets (its Modified Plasticity Index): what Approved Document A takes footings down for.</summary>
public enum ClayShrinkage
{
    None,
    Low,
    Medium,
    High
}

/// <summary>What a footing is: a strip under a wall, or a pad under a column.</summary>
public enum FootingKind
{
    Strip,
    Pad
}

/// <summary>
/// A footing's type: a strip of concrete under a wall, so wide and so thick, or a pad under a
/// column, so wide each way - and the masonry or concrete standing on it up to what it carries.
/// </summary>
public sealed class FootingType : ElementType
{
    public FootingType(string name, FootingKind kind) : base(name)
    {
        Kind = kind;
    }

    public override BuiltInCategory Category => BuiltInCategory.StructuralFoundations;

    public FootingKind Kind { get; set; }

    /// <summary>Across a strip, or a pad's side across, mm.</summary>
    public double Width { get; set; } = 600;

    /// <summary>A pad's other side, mm.</summary>
    public double Length { get; set; } = 600;

    /// <summary>How thick the concrete is, top to underside, mm.</summary>
    public double Thickness { get; set; } = 225;

    public Guid MaterialId { get; set; }

    /// <summary>What stands on it up to the wall or column it carries: blockwork under a wall, a concrete pier under a column.</summary>
    public Guid StemMaterialId { get; set; }

    public FootingType Duplicate(string name) => WithCommonOf(new FootingType(name, Kind)
    {
        Width = Width, Length = Length, Thickness = Thickness, MaterialId = MaterialId, StemMaterialId = StemMaterialId
    });

    public override IEnumerable<ParameterValue> GetTypeParameters(BimDocument? document = null)
    {
        yield return ParameterValue.ReadOnly(FootingParameters.Kind, () => EnumText.Humanise(Kind));
        yield return ParameterValue.BindValidated(FootingParameters.Width, () => Width, v => { if (v < 150 || v > 6000) return false; Width = v; return true; });
        if (Kind == FootingKind.Pad)
            yield return ParameterValue.BindValidated(FootingParameters.Length, () => Length, v => { if (v < 150 || v > 6000) return false; Length = v; return true; });
        yield return ParameterValue.BindValidated(FootingParameters.Thickness, () => Thickness, v => { if (v < 100 || v > 3000) return false; Thickness = v; return true; });
        if (RoofEdgeSweepParameters.MaterialOf(document, () => MaterialId, id => MaterialId = id, FootingParameters.Material) is { } material) yield return material;
        if (RoofEdgeSweepParameters.MaterialOf(document, () => StemMaterialId, id => StemMaterialId = id, FootingParameters.StemMaterial) is { } stem) yield return stem;
        foreach (var parameter in GetCommonTypeParameters()) yield return parameter;
    }
}

/// <summary>
/// A strip footing under a wall (Revit's Wall Foundation): a strip of concrete along it, so deep
/// below the level the wall stands on, and the masonry from it up to the wall's base. It follows
/// the wall, and goes with it.
/// </summary>
public sealed class WallFooting : Element, IHostedElement
{
    public override BuiltInCategory Category => BuiltInCategory.StructuralFoundations;

    public Guid WallId { get; set; }

    public Guid HostId => WallId;

    /// <summary>How far its underside is below the level its wall stands on, mm.</summary>
    public double Depth { get; set; } = 1000;

    /// <summary>How far its middle is off the wall's, out toward the wall's exterior face, mm.</summary>
    public double Eccentricity { get; set; }

    /// <summary>
    /// Whether it steps up the ground where the ground rises along it - each run as shallow as the
    /// ground over it allows - rather than all of it as deep as its lowest point asks.
    /// </summary>
    public bool Stepped { get; set; } = true;

    /// <summary>Whether it is reinforced, and with what: bars along it - "3 H12" - or at centres across it - "H12 @ 200". A strip is mass concrete unless it says.</summary>
    public bool Reinforced { get; set; }

    public string Bars { get; set; } = "3 H12";

    /// <summary>How it holds the wall on it in the analytical model: pinned, free to turn about its line, or fixed.</summary>
    public SupportFixity Fixity { get; set; }

    public override IEnumerable<ParameterValue> GetInstanceParameters(BimDocument document)
    {
        var type = document.FindType<FootingType>(TypeId);
        yield return ParameterValue.ReadOnly(FootingParameters.Host, () => document.Find<Wall>(WallId) is { } wall
            ? document.GetWallType(wall)?.Name ?? "Wall" : "<none>");
        yield return ParameterValue.BindValidated(FootingParameters.Depth, () => Depth, v => { if (v < (type?.Thickness ?? 0) || v > 10000) return false; Depth = v; return true; });
        yield return ParameterValue.Bind(FootingParameters.Eccentricity, () => Eccentricity, v => Eccentricity = v);
        yield return ParameterValue.Bind(FootingParameters.Stepped, () => Stepped, v => Stepped = v);
        yield return ParameterValue.ReadOnly(FootingParameters.Steps, () => Footings.StepsSaid(document, this));
        foreach (var parameter in FootingParameters.Steel(() => Reinforced, v => Reinforced = v, () => Bars, v => Bars = v)) yield return parameter;
        yield return ParameterValue.ReadOnly(FootingParameters.BelowGround, () => Math.Round(Footings.DepthBelowGround(document, this) ?? 0));
        yield return Units.Imperial
            ? ParameterValue.ReadOnly(FootingParameters.LineLoadPounds, () => document.Find<Wall>(WallId) is { } wall ? Math.Round(Loads.OnWall(document, wall) * Units.PoundsPerFootPerKilonewtonPerMetre) : 0)
            : ParameterValue.ReadOnly(FootingParameters.LineLoad, () => document.Find<Wall>(WallId) is { } wall ? Math.Round(Loads.OnWall(document, wall), 1) : 0);
        yield return ParameterValue.ReadOnly(FootingParameters.Volume, () => Footings.Volume(document, this));
        yield return ParameterValue.ReadOnly(FootingParameters.Regulations, () => StructureRules.Verdict(document, this));
        yield return AnalyticalParameters.FixityOf(() => Fixity, v => Fixity = v);
        yield return AnalyticalParameters.Said(document, this);
        foreach (var parameter in GetCommonParameters(document)) yield return parameter;
    }
}

/// <summary>
/// A pad footing (Revit's Isolated Foundation): a block of concrete under a column, so deep below
/// the level, with a pier from it up to the column's base. Under a column it follows the column;
/// placed on its own, it stays where it is put.
/// </summary>
public sealed class PadFooting : Element, IHostedElement
{
    public override BuiltInCategory Category => BuiltInCategory.StructuralFoundations;

    /// <summary>The column it carries, if it carries one.</summary>
    public Guid? ColumnId { get; set; }

    public Guid HostId => ColumnId ?? Id;

    /// <summary>Its middle, where it carries no column.</summary>
    public Point2D Location { get; set; }

    /// <summary>How it is turned, degrees anticlockwise, where it carries no column; under one, it turns with it.</summary>
    public double Rotation { get; set; }

    /// <summary>How far its underside is below its level, mm.</summary>
    public double Depth { get; set; } = 1000;

    /// <summary>Whether it is reinforced, and with what: bars each way at centres - "H12 @ 200" - or so many each way - "4 H16".</summary>
    public bool Reinforced { get; set; }

    public string Bars { get; set; } = "H12 @ 200";

    /// <summary>How it holds the column on it in the analytical model: pinned, free to turn, or fixed.</summary>
    public SupportFixity Fixity { get; set; }

    public override IEnumerable<ParameterValue> GetInstanceParameters(BimDocument document)
    {
        var type = document.FindType<FootingType>(TypeId);
        yield return ParameterValue.ReadOnly(FootingParameters.Host, () => ColumnId is { } id && document.FindElement(id) is Column column
            ? document.FindType<ColumnType>(column.TypeId)?.Name ?? "Column" : "<none>");
        yield return ParameterValue.BindValidated(FootingParameters.Depth, () => Depth, v => { if (v < (type?.Thickness ?? 0) || v > 10000) return false; Depth = v; return true; });
        if (ColumnId is null) yield return ParameterValue.Bind(FootingParameters.Rotation, () => Rotation, v => Rotation = ((v % 360) + 360) % 360);
        foreach (var parameter in FootingParameters.Steel(() => Reinforced, v => Reinforced = v, () => Bars, v => Bars = v)) yield return parameter;
        yield return ParameterValue.ReadOnly(FootingParameters.BelowGround, () => Math.Round(Footings.DepthBelowGround(document, this) ?? 0));
        yield return Units.Imperial
            ? ParameterValue.ReadOnly(FootingParameters.PointLoadPounds, () => ColumnId is { } id && document.FindElement(id) is Column carried ? Math.Round(Loads.OnColumn(document, carried) * Units.PoundsPerKilonewton) : 0)
            : ParameterValue.ReadOnly(FootingParameters.PointLoad, () => ColumnId is { } id && document.FindElement(id) is Column carried ? Math.Round(Loads.OnColumn(document, carried), 1) : 0);
        yield return ParameterValue.ReadOnly(FootingParameters.Volume, () => Footings.Volume(document, this));
        yield return ParameterValue.ReadOnly(FootingParameters.Regulations, () => StructureRules.Verdict(document, this));
        yield return AnalyticalParameters.FixityOf(() => Fixity, v => Fixity = v);
        yield return AnalyticalParameters.Said(document, this);
        foreach (var parameter in GetCommonParameters(document)) yield return parameter;
    }
}

public static class FootingParameters
{
    public static readonly ParameterDefinition Kind = new("Footing", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.Construction);
    public static readonly ParameterDefinition Width = new("Width", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Length = new("Length", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Thickness = new("Foundation Thickness", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Material = new("Structural Material", ParameterDataType.Material, ParameterBinding.Type, ParameterGroup.MaterialsAndFinishes);
    public static readonly ParameterDefinition StemMaterial = new("Wall or Pier Material", ParameterDataType.Material, ParameterBinding.Type, ParameterGroup.MaterialsAndFinishes);

    public static readonly ParameterDefinition Host = new("Carries", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition Depth = new("Depth of Underside below Level", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition Eccentricity = new("Eccentricity", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition Stepped = new("Stepped", ParameterDataType.YesNo, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition Steps = new("Steps", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Rotation = new("Rotation", ParameterDataType.Angle, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition BelowGround = new("Depth below Ground", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition LineLoad = new("Load Carried (kN/m)", ParameterDataType.Number, ParameterBinding.Instance, ParameterGroup.Analytical);
    public static readonly ParameterDefinition PointLoad = new("Load Carried (kN)", ParameterDataType.Number, ParameterBinding.Instance, ParameterGroup.Analytical);
    public static readonly ParameterDefinition LineLoadPounds = new("Load Carried (lb/ft)", ParameterDataType.Number, ParameterBinding.Instance, ParameterGroup.Analytical);
    public static readonly ParameterDefinition PointLoadPounds = new("Load Carried (lb)", ParameterDataType.Number, ParameterBinding.Instance, ParameterGroup.Analytical);
    public static readonly ParameterDefinition Volume = new("Volume", ParameterDataType.Volume, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Regulations = new("Regulations", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.IdentityData);
    public static readonly ParameterDefinition Reinforced = new("Reinforced", ParameterDataType.YesNo, ParameterBinding.Instance, ParameterGroup.Structural);
    public static readonly ParameterDefinition Bars = new("Reinforcement", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Structural);

    /// <summary>Whether a member is reinforced and its bars, written as a drawing writes them and refused where they read as no bar or fabric made.</summary>
    public static IEnumerable<ParameterValue> Steel(Func<bool> reinforced, Action<bool> reinforce, Func<string> bars, Action<string> setBars)
    {
        yield return ParameterValue.Bind(Reinforced, reinforced, reinforce);
        yield return ParameterValue.BindValidated(Bars, bars, value => { if (BarSpec.Parse(value) is not { } spec) return false; setBars(spec.ToString()); return true; });
    }
}

/// <summary>
/// Where footings are and what they are: their outlines in plan, their heights, the wall or pier
/// standing on them, and their solids.
/// </summary>
public static class Footings
{
    /// <summary>The footing under a wall, if it has one.</summary>
    public static WallFooting? Under(BimDocument document, Wall wall) =>
        document.All<WallFooting>().FirstOrDefault(footing => footing.WallId == wall.Id);

    /// <summary>The pad under a column, if it has one.</summary>
    public static PadFooting? Under(BimDocument document, Column column) =>
        document.All<PadFooting>().FirstOrDefault(pad => pad.ColumnId == column.Id);

    /// <summary>A foundation member as a bar schedule names it: what it is, then its mark, or its place among those of its kind.</summary>
    public static string MemberName(BimDocument document, Element element)
    {
        var kind = element switch { WallFooting => "Strip footing", PadFooting => "Pad", RaftFoundation => "Raft", GroundBeam => "Ground beam", Pile => "Pile", _ => element.Category.ToString() };
        if (!string.IsNullOrWhiteSpace(element.Mark)) return $"{kind} {element.Mark}";
        var same = document.Elements.Where(each => each.GetType() == element.GetType()).ToList();
        return $"{kind} {same.IndexOf(element) + 1}";
    }

    /// <summary>How thick a wall is, mm: its type's whole build-up.</summary>
    public static double WallThickness(BimDocument document, Wall wall) => document.GetWallType(wall)?.Structure.TotalWidth ?? 0;

    /// <summary>Whether a wall's end runs on into another wall: a footing turns the corner there, rather than stopping short.</summary>
    private static bool Joined(BimDocument document, Wall wall, Point2D end) =>
        document.Walls.Any(other => !ReferenceEquals(other, wall) && other.LevelId == wall.LevelId && !other.IsCurved && other.Length > 1 &&
                                    Line2D.DistanceFromSegment(end, other.Start, other.End) < WallThickness(document, other) / 2 + 1);

    /// <summary>
    /// A strip footing's outline in plan: along its wall, so wide, its middle off the wall's by its
    /// eccentricity - run on past the wall's ends by half its width where the wall joins another,
    /// so the strips are continuous round the corners, and by its projection where it stops.
    /// </summary>
    public static IReadOnlyList<Point2D>? Outline(BimDocument document, WallFooting footing)
    {
        if (document.Find<Wall>(footing.WallId) is not { } wall || wall.IsCurved || wall.Length < 1) return null;
        if (document.FindType<FootingType>(footing.TypeId) is not { } type || document.GetWallType(wall) is not { } wallType) return null;

        var along = (wall.End - wall.Start).NormalisedOrDefault(Vector2D.UnitX);
        var middle = wall.PointAt(wallType.Structure, 0, 0);
        var across = (wall.PointAt(wallType.Structure, 0, 1) - middle).NormalisedOrDefault(along.PerpendicularLeft());
        var start = wall.PointAt(wallType.Structure, 0, 0) + across * footing.Eccentricity;
        var end = wall.PointAt(wallType.Structure, wall.Length, 0) + across * footing.Eccentricity;
        var projection = Math.Max(0, (type.Width - wallType.Structure.TotalWidth) / 2);
        start -= along * (Joined(document, wall, wall.Start) ? type.Width / 2 : projection);
        end += along * (Joined(document, wall, wall.End) ? type.Width / 2 : projection);
        return Box(start, end, across, type.Width / 2);
    }

    /// <summary>The masonry from a strip footing up to its wall's base, in plan: the wall's thickness, run on into the walls it joins.</summary>
    public static IReadOnlyList<Point2D>? StemOutline(BimDocument document, WallFooting footing)
    {
        if (document.Find<Wall>(footing.WallId) is not { } wall || wall.IsCurved || wall.Length < 1) return null;
        if (document.GetWallType(wall) is not { } wallType) return null;
        var along = (wall.End - wall.Start).NormalisedOrDefault(Vector2D.UnitX);
        var middle = wall.PointAt(wallType.Structure, 0, 0);
        var across = (wall.PointAt(wallType.Structure, 0, 1) - middle).NormalisedOrDefault(along.PerpendicularLeft());
        var half = wallType.Structure.TotalWidth / 2;
        var start = middle - along * (Joined(document, wall, wall.Start) ? half : 0);
        var end = wall.PointAt(wallType.Structure, wall.Length, 0) + along * (Joined(document, wall, wall.End) ? half : 0);
        return Box(start, end, across, half);
    }

    private static IReadOnlyList<Point2D> Box(Point2D start, Point2D end, Vector2D across, double half)
    {
        var box = new List<Point2D> { start - across * half, end - across * half, end + across * half, start + across * half };
        if (Polygon2D.SignedArea(box) < 0) box.Reverse();
        return box;
    }

    /// <summary>Where a pad is and how it is turned: under its column, or where it was put.</summary>
    public static (Point2D Location, Vector2D Across) Placement(BimDocument document, PadFooting pad) =>
        pad.ColumnId is { } id && document.FindElement(id) is Column column
            ? (column.Location, column.Across)
            : (pad.Location, new Vector2D(Math.Cos(pad.Rotation * Math.PI / 180), Math.Sin(pad.Rotation * Math.PI / 180)));

    /// <summary>A pad's outline in plan: its width across, its length along, about its middle.</summary>
    public static IReadOnlyList<Point2D>? Outline(BimDocument document, PadFooting pad)
    {
        if (document.FindType<FootingType>(pad.TypeId) is not { } type) return null;
        var (at, across) = Placement(document, pad);
        var along = across.PerpendicularLeft();
        return Box(at - along * (type.Length / 2), at + along * (type.Length / 2), across, type.Width / 2);
    }

    /// <summary>How far the pier on a pad stands out past its column each way, mm.</summary>
    public const double PierMargin = 75;

    /// <summary>The pier from a pad up to its column's base, in plan - the column's size and a margin round it; none with no column.</summary>
    public static IReadOnlyList<Point2D>? PierOutline(BimDocument document, PadFooting pad)
    {
        if (pad.ColumnId is not { } id || document.FindElement(id) is not Column column || document.FindType<ColumnType>(column.TypeId) is not { } type) return null;
        var (width, depth) = type.Profile.Extent;
        var along = column.Across.PerpendicularLeft();
        return Box(column.Location - along * (depth / 2 + PierMargin), column.Location + along * (depth / 2 + PierMargin), column.Across, width / 2 + PierMargin);
    }

    /// <summary>The level elevation a footing hangs below: its wall's base level, or a pad's own level.</summary>
    private static double LevelOf(BimDocument document, Element element, Guid levelId) => document.FindLevel(levelId)?.Elevation ?? 0;

    /// <summary>A strip footing's underside and top, mm.</summary>
    public static (double Bottom, double Top)? Heights(BimDocument document, WallFooting footing)
    {
        if (document.Find<Wall>(footing.WallId) is not { } wall || document.FindType<FootingType>(footing.TypeId) is not { } type) return null;
        var bottom = LevelOf(document, wall, wall.LevelId) - footing.Depth;
        return (bottom, bottom + type.Thickness);
    }

    /// <summary>A pad's underside and top, mm.</summary>
    public static (double Bottom, double Top)? Heights(BimDocument document, PadFooting pad)
    {
        if (document.FindType<FootingType>(pad.TypeId) is not { } type) return null;
        var levelId = pad.ColumnId is { } id && document.FindElement(id) is Column column ? column.LevelId : pad.LevelId;
        var bottom = LevelOf(document, pad, levelId) - pad.Depth;
        return (bottom, bottom + type.Thickness);
    }

    /// <summary>
    /// The ground over a point, mm: the surveyed ground there - undisturbed, before the building
    /// cuts it away - or, with no ground modelled, the level.
    /// </summary>
    public static double GroundAt(BimDocument document, Point2D at, double level)
    {
        foreach (var surface in document.All<Toposurface>())
            if (Terrains.HeightAt(surface, at) is { } height) return height;
        return level;
    }

    /// <summary>How far a footing's underside is below the ground beside it, mm: the least of its runs' - each run's lower side.</summary>
    public static double? DepthBelowGround(BimDocument document, WallFooting footing)
    {
        if (Outline(document, footing) is not { } outline) return null;
        var level = document.Find<Wall>(footing.WallId) is { } wall ? document.FindLevel(wall.LevelId)?.Elevation ?? 0 : 0;
        var runs = Runs(document, footing);
        if (runs.Count == 0) return null;
        return runs.Min(run => Ground(document, outline, run.From, run.To, level) - run.Bottom);
    }

    // ---- stepping up the ground ----------------------------------------------------------

    /// <summary>The steps a stepped footing's underside is set out in: a course of blockwork, 75 mm - 3 in in the US.</summary>
    public static double StepIncrement(BimDocument document) => document.ProjectInformation.ClearanceStandard.IsUs() ? 76.2 : 75;

    /// <summary>
    /// The least run between steps, mm: room for the lap the lower run makes under the higher - the
    /// greater of twice the highest step, the footing's thickness, and 300 mm (Approved Document A 2E2(d));
    /// with a step no higher than the footing is thick, twice its thickness or 300 mm.
    /// </summary>
    public static double LeastRun(double thickness) => Math.Max(2 * thickness, 300);

    /// <summary>How far the lower run goes on under the higher at a step of a height, mm: twice the step, the thickness, or 300 mm, whichever is greater.</summary>
    public static double Lap(double step, double thickness) => Math.Max(Math.Max(2 * step, thickness), 300);

    /// <summary>A strip footing's run between steps: along it from its start end, mm, and its underside's elevation.</summary>
    public sealed record FootingRun(double From, double To, double Bottom);

    /// <summary>How deep under the ground the rules ask a footing's underside: the frost and clay depth, the IRC's 12 in or frost line - and no less than its own thickness and 150 under it.</summary>
    public static double DepthAsked(BimDocument document, FootingType type)
    {
        var project = document.ProjectInformation;
        var asked = project.ClearanceStandard.IsUs()
            ? Math.Max(305, project.FrostDepth)
            : StructureRules.AdaDepth(project.Ground, project.ClayShrinkage);
        return Math.Max(asked, type.Thickness + 150);
    }

    /// <summary>The lowest ground over a stretch of a strip footing's outline, mm: along both its long sides - and no higher than its level.</summary>
    private static double Ground(BimDocument document, IReadOnlyList<Point2D> outline, double from, double to, double level)
    {
        if (!document.All<Toposurface>().Any()) return level;
        var (a, b, c, d) = (outline[0], outline[1], outline[2], outline[3]);
        var length = a.DistanceTo(b);
        if (length < 1) return GroundAt(document, a, level);
        var along = (b - a) / length;
        var lowest = double.MaxValue;
        var steps = Math.Max(2, (int)Math.Ceiling((to - from) / 150));
        for (var i = 0; i <= steps; i++)
        {
            var at = Math.Clamp(from + (to - from) * i / steps, 0, length);
            lowest = Math.Min(lowest, Math.Min(GroundAt(document, a + along * at, level), GroundAt(document, d + along * at, level)));
        }

        // Inside, the ground is dug down to the floor: where it stands higher outside, the footing goes down from the level.
        return Math.Min(lowest, level);
    }

    /// <summary>
    /// A strip footing's runs, from its start end: one, at its depth below its level, unless it is
    /// stepped and the ground rises along it - then in runs at least <see cref="LeastRun"/> long, each
    /// as shallow as the ground over it lets it be, set out in <see cref="StepIncrement"/>s, no step
    /// higher than the footing is thick (Approved Document A 2E2(e); in the US, its underside level in each
    /// run, IRC R403.1.5). None at its own depth is shallower than the depth it is given.
    /// </summary>
    public static IReadOnlyList<FootingRun> Runs(BimDocument document, WallFooting footing)
    {
        if (Outline(document, footing) is not { } outline || Heights(document, footing) is not var (lowest, _)) return Array.Empty<FootingRun>();
        var type = document.FindType<FootingType>(footing.TypeId)!;
        var length = outline[0].DistanceTo(outline[1]);
        var whole = new[] { new FootingRun(0, length, lowest) };
        if (!footing.Stepped || !document.All<Toposurface>().Any() || length < 2 * LeastRun(type.Thickness)) return whole;

        var level = document.Find<Wall>(footing.WallId) is { } wall ? document.FindLevel(wall.LevelId)?.Elevation ?? 0 : 0;
        var count = (int)Math.Floor(length / LeastRun(type.Thickness));
        var asked = DepthAsked(document, type);
        var increment = StepIncrement(document);
        var bottoms = new double[count];
        for (var i = 0; i < count; i++)
        {
            var (from, to) = (length * i / count, length * (i + 1) / count);
            var allowed = Ground(document, outline, from, to, level) - asked;
            bottoms[i] = Math.Max(lowest, lowest + Math.Floor((allowed - lowest) / increment) * increment);
        }

        // No step higher than the footing is thick: the higher of two lowered until it is not.
        var most = Math.Floor(type.Thickness / increment) * increment;
        for (var changed = true; changed;)
        {
            changed = false;
            for (var i = 0; i + 1 < count; i++)
            {
                if (bottoms[i + 1] - bottoms[i] > most + 1e-6) { bottoms[i + 1] = bottoms[i] + most; changed = true; }
                if (bottoms[i] - bottoms[i + 1] > most + 1e-6) { bottoms[i] = bottoms[i + 1] + most; changed = true; }
            }
        }

        var runs = new List<FootingRun>();
        for (var i = 0; i < count; i++)
        {
            var (from, to) = (length * i / count, length * (i + 1) / count);
            if (runs.Count > 0 && Math.Abs(runs[^1].Bottom - bottoms[i]) < 1e-6) runs[^1] = runs[^1] with { To = to };
            else runs.Add(new FootingRun(from, to, bottoms[i]));
        }

        return runs;
    }

    /// <summary>A stretch of a long box outline - two corners at its start, two at its end - from one distance along it to another.</summary>
    private static IReadOnlyList<Point2D> Stretch(IReadOnlyList<Point2D> box, double from, double to)
    {
        var length = box[0].DistanceTo(box[1]);
        var along = length < 1 ? Vector2D.UnitX : (box[1] - box[0]) / length;
        return new[] { box[0] + along * from, box[0] + along * to, box[3] + along * to, box[3] + along * from };
    }

    /// <summary>The concrete counted twice where a lower run laps under a higher: the lap's length, the footing's width and how far the two overlap in height.</summary>
    private static double Overlaps(BimDocument document, WallFooting footing, FootingType type)
    {
        var runs = Runs(document, footing);
        double overlap = 0;
        for (var i = 0; i + 1 < runs.Count; i++)
        {
            var step = Math.Abs(runs[i + 1].Bottom - runs[i].Bottom);
            overlap += Lap(step, type.Thickness) * type.Width * Math.Max(0, type.Thickness - step);
        }

        return overlap;
    }

    /// <summary>A footing's steps, as its Properties say them: none, or each one's height and the lap under it.</summary>
    public static string StepsSaid(BimDocument document, WallFooting footing)
    {
        var runs = Runs(document, footing);
        if (runs.Count < 2 || document.FindType<FootingType>(footing.TypeId) is not { } type) return footing.Stepped ? "None: the ground is level along it" : "Not stepped";
        return string.Join(", ", Enumerable.Range(0, runs.Count - 1).Select(i =>
        {
            var step = Math.Abs(runs[i + 1].Bottom - runs[i].Bottom);
            return $"{Units.FormatMm(step)} up at {Units.Metres(runs[i].To, "0.00")}, lapped {Units.FormatMm(Lap(step, type.Thickness))}";
        }));
    }

    /// <summary>
    /// The concrete of each run of a strip footing in plan, as cast: its own stretch, and the lower of
    /// two runs carried on under the higher by its lap at each step.
    /// </summary>
    public static IReadOnlyList<(IReadOnlyList<Point2D> Outline, double Bottom)> RunOutlines(BimDocument document, WallFooting footing)
    {
        if (Outline(document, footing) is not { } outline || document.FindType<FootingType>(footing.TypeId) is not { } type) return Array.Empty<(IReadOnlyList<Point2D>, double)>();
        var runs = Runs(document, footing);
        var length = outline[0].DistanceTo(outline[1]);
        var result = new List<(IReadOnlyList<Point2D>, double)>();
        for (var i = 0; i < runs.Count; i++)
        {
            var (from, to) = (runs[i].From, runs[i].To);
            if (i > 0 && runs[i - 1].Bottom > runs[i].Bottom) from = Math.Max(0, from - Lap(runs[i - 1].Bottom - runs[i].Bottom, type.Thickness));
            if (i + 1 < runs.Count && runs[i + 1].Bottom > runs[i].Bottom) to = Math.Min(length, to + Lap(runs[i + 1].Bottom - runs[i].Bottom, type.Thickness));
            result.Add((Stretch(outline, from, to), runs[i].Bottom));
        }

        return result;
    }

    public static double? DepthBelowGround(BimDocument document, PadFooting pad)
    {
        if (Heights(document, pad) is not var (bottom, _) || Outline(document, pad) is not { } outline) return null;
        var levelId = pad.ColumnId is { } id && document.FindElement(id) is Column column ? column.LevelId : pad.LevelId;
        var level = document.FindLevel(levelId)?.Elevation ?? 0;
        return Math.Min(level, outline.Min(point => GroundAt(document, point, level))) - bottom;
    }

    /// <summary>The concrete in a footing, mm³.</summary>
    public static double Volume(BimDocument document, Element footing) => footing switch
    {
        WallFooting strip when document.FindType<FootingType>(strip.TypeId) is { } type => RunOutlines(document, strip).Sum(run => Polygon2D.Area(run.Outline)) * type.Thickness
            - Overlaps(document, strip, type),
        PadFooting pad when Outline(document, pad) is { } outline && document.FindType<FootingType>(pad.TypeId) is { } type => Polygon2D.Area(outline) * type.Thickness,
        _ => 0
    };

    /// <summary>A footing's solids: its concrete, and the wall or pier standing on it up to what it carries.</summary>
    public static IReadOnlyList<Mesh3D> Meshes(BimDocument document, Element footing)
    {
        if (document.FindType<FootingType>(footing.TypeId) is not { } type) return Array.Empty<Mesh3D>();
        var concrete = document.FindMaterial(type.MaterialId);
        var stemMaterial = document.FindMaterial(type.StemMaterialId);
        var levelId = footing.LevelId;
        var mesh = new Mesh3D(footing.Id, levelId, MeshKind.Floor, concrete?.SurfaceColour ?? ColourRgb.FromHex("B5B3AE"), concrete?.Name ?? "Footing");
        var stem = new Mesh3D(footing.Id, levelId, MeshKind.Wall, stemMaterial?.SurfaceColour ?? ColourRgb.FromHex("A8A49C"), stemMaterial?.Name ?? "Foundation Wall");

        switch (footing)
        {
            case WallFooting strip when Outline(document, strip) is not null:
            {
                // Each run its own solid, so where the lower laps under the higher a section is not cut hollow.
                var runs = RunOutlines(document, strip);
                var parts = new List<Mesh3D>();
                foreach (var (outline, bottom) in runs)
                {
                    var run = new Mesh3D(footing.Id, levelId, MeshKind.Floor, mesh.Colour, mesh.Description);
                    run.AddExtrusion(outline, bottom, bottom + type.Thickness);
                    parts.Add(run);
                }

                if (document.Find<Wall>(strip.WallId) is { } wall && StemOutline(document, strip) is { } wallOutline)
                {
                    // The masonry from each run's top up to the wall: the stem's stretch over that run.
                    var footingStart = Outline(document, strip)![0];
                    var stemLength = wallOutline[0].DistanceTo(wallOutline[1]);
                    var stemAlong = stemLength < 1 ? Vector2D.UnitX : (wallOutline[1] - wallOutline[0]) / stemLength;
                    var offset = (wallOutline[0] - footingStart).Dot(stemAlong);
                    foreach (var run in Runs(document, strip))
                    {
                        var (from, to) = (Math.Clamp(run.From - offset, 0, stemLength), Math.Clamp(run.To - offset, 0, stemLength));
                        if (to - from < 1) continue;
                        var piece = new Mesh3D(footing.Id, levelId, MeshKind.Wall, stem.Colour, stem.Description);
                        piece.AddExtrusion(Stretch(wallOutline, from, to), run.Bottom + type.Thickness, wall.GetBaseElevation(document));
                        parts.Add(piece);
                    }
                }

                return parts.Where(part => !part.IsEmpty).ToList();
            }

            case PadFooting pad when Outline(document, pad) is { } outline && Heights(document, pad) is var (bottom, top):
            {
                mesh.AddExtrusion(outline, bottom, top);
                if (pad.ColumnId is { } id && document.FindElement(id) is Column column && PierOutline(document, pad) is { } pier)
                    stem.AddExtrusion(pier, top, column.GetBaseElevation(document));
                break;
            }
        }

        return new[] { mesh, stem }.Where(part => !part.IsEmpty).ToList();
    }

    /// <summary>A footing in plan: its outline - drawn dashed, being under the floor.</summary>
    public static IReadOnlyList<Point2D>? Footprint(BimDocument document, Element footing) => footing switch
    {
        WallFooting strip => Outline(document, strip),
        PadFooting pad => Outline(document, pad),
        RaftFoundation raft => raft.Outline.Count >= 3 ? raft.Outline : null,
        GroundBeam beam => Piles.Outline(document, beam),
        Pile pile => Piles.Circle(Piles.Position(document, pile), pile.Diameter / 2),
        _ => null
    };

    /// <summary>
    /// How deep a footing is to go below its level, mm: as far down as the rules take its underside
    /// below the ground - 0.45 m, or 0.75 to 1.0 m in a shrinkable clay; the IRC's 12 in, or the
    /// frost line - however far the ground is above or below the level there, rounded up to 50.
    /// </summary>
    public static double DepthFor(BimDocument document, Element footing, FootingType type)
    {
        var asked = DepthAsked(document, type);
        var level = document.FindLevel(LevelOf(document, footing))?.Elevation ?? 0;
        var outline = footing switch
        {
            WallFooting strip => Outline(document, strip),
            PadFooting pad => Outline(document, pad),
            _ => null
        };

        // The lowest ground along a strip, not only at its corners: a dip half way along takes it down too.
        var ground = footing is WallFooting && outline is { Count: 4 } box ? Ground(document, box, 0, box[0].DistanceTo(box[1]), level)
            : outline is { Count: > 0 } ? Math.Min(level, outline.Min(point => GroundAt(document, point, level))) : level;
        return Math.Ceiling((asked + level - ground) / 50) * 50;
    }

    /// <summary>
    /// The footing a wall asks for: the narrowest of the project's strip types the rules are met
    /// by there, taken as deep as they ask - the widest, where none is. Not yet in the project.
    /// </summary>
    public static WallFooting Fit(BimDocument document, Wall wall, FootingType? chosen = null)
    {
        var footing = new WallFooting { WallId = wall.Id, LevelId = wall.LevelId };
        var types = chosen is not null ? new List<FootingType> { chosen } : Candidates(document, FootingKind.Strip);
        foreach (var type in types)
        {
            footing.TypeId = type.Id;
            footing.Depth = DepthFor(document, footing, type);
            if (StructureRules.Problems(document, footing, document.ProjectInformation.ClearanceStandard).Count == 0) return footing;
        }

        if (types.LastOrDefault() is { } widest)
        {
            footing.TypeId = widest.Id;
            footing.Depth = DepthFor(document, footing, widest);
        }

        return footing;
    }

    /// <summary>
    /// The pad a structural column asks for: the smallest of the project's pad types the rules are
    /// met by under it, as deep as they ask - the biggest, where none is. Not yet in the project.
    /// </summary>
    public static PadFooting Fit(BimDocument document, Column column, FootingType? chosen = null)
    {
        var pad = new PadFooting { ColumnId = column.Id, LevelId = column.LevelId, Location = column.Location, Rotation = column.Rotation };
        var types = chosen is not null ? new List<FootingType> { chosen } : Candidates(document, FootingKind.Pad);
        foreach (var type in types)
        {
            pad.TypeId = type.Id;
            pad.Depth = DepthFor(document, pad, type);
            if (StructureRules.Problems(document, pad, document.ProjectInformation.ClearanceStandard).Count == 0) return pad;
        }

        if (types.LastOrDefault() is { } biggest)
        {
            pad.TypeId = biggest.Id;
            pad.Depth = DepthFor(document, pad, biggest);
        }

        return pad;
    }

    /// <summary>
    /// The types a fit tries, smallest first: those sized in the project's own units - inches for a
    /// US house, millimetres for one in England - before the others.
    /// </summary>
    private static List<FootingType> Candidates(BimDocument document, FootingKind kind)
    {
        var us = document.ProjectInformation.ClearanceStandard.IsUs();
        return document.TypesOf<FootingType>().Where(type => type.Kind == kind)
            .OrderBy(type => type.Name.EndsWith(" in", StringComparison.OrdinalIgnoreCase) == us ? 0 : 1)
            .ThenBy(type => type.Width * (kind == FootingKind.Pad ? type.Length : 1))
            .ThenBy(type => type.Thickness)
            .ToList();
    }

    /// <summary>The level a footing is drawn on: its wall's, its column's, or its own.</summary>
    /// <summary>Whether an element is a foundation: a strip or pad footing, a raft, a ground beam or a pile.</summary>
    public static bool IsFoundation(Element element) => element is WallFooting or PadFooting or RaftFoundation or GroundBeam or Pile;

    public static Guid LevelOf(BimDocument document, Element footing) => footing switch
    {
        WallFooting strip when document.Find<Wall>(strip.WallId) is { } wall => wall.LevelId,
        PadFooting { ColumnId: { } id } when document.FindElement(id) is Column column => column.LevelId,
        GroundBeam beam when document.Find<Wall>(beam.WallId) is { } beamWall => beamWall.LevelId,
        Pile { BeamId: { } beamId } when document.Find<GroundBeam>(beamId) is { } carried && document.Find<Wall>(carried.WallId) is { } pileWall => pileWall.LevelId,
        _ => footing.LevelId
    };
}
