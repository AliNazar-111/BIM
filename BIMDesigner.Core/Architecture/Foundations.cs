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

    public override IEnumerable<ParameterValue> GetInstanceParameters(BimDocument document)
    {
        var type = document.FindType<FootingType>(TypeId);
        yield return ParameterValue.ReadOnly(FootingParameters.Host, () => document.Find<Wall>(WallId) is { } wall
            ? document.GetWallType(wall)?.Name ?? "Wall" : "<none>");
        yield return ParameterValue.BindValidated(FootingParameters.Depth, () => Depth, v => { if (v < (type?.Thickness ?? 0) || v > 10000) return false; Depth = v; return true; });
        yield return ParameterValue.Bind(FootingParameters.Eccentricity, () => Eccentricity, v => Eccentricity = v);
        yield return ParameterValue.ReadOnly(FootingParameters.BelowGround, () => Math.Round(Footings.DepthBelowGround(document, this) ?? 0));
        yield return Units.Imperial
            ? ParameterValue.ReadOnly(FootingParameters.LineLoadPounds, () => document.Find<Wall>(WallId) is { } wall ? Math.Round(Loads.OnWall(document, wall) * Units.PoundsPerFootPerKilonewtonPerMetre) : 0)
            : ParameterValue.ReadOnly(FootingParameters.LineLoad, () => document.Find<Wall>(WallId) is { } wall ? Math.Round(Loads.OnWall(document, wall), 1) : 0);
        yield return ParameterValue.ReadOnly(FootingParameters.Volume, () => Footings.Volume(document, this));
        yield return ParameterValue.ReadOnly(FootingParameters.Regulations, () => StructureRules.Verdict(document, this));
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

    public override IEnumerable<ParameterValue> GetInstanceParameters(BimDocument document)
    {
        var type = document.FindType<FootingType>(TypeId);
        yield return ParameterValue.ReadOnly(FootingParameters.Host, () => ColumnId is { } id && document.FindElement(id) is Column column
            ? document.FindType<ColumnType>(column.TypeId)?.Name ?? "Column" : "<none>");
        yield return ParameterValue.BindValidated(FootingParameters.Depth, () => Depth, v => { if (v < (type?.Thickness ?? 0) || v > 10000) return false; Depth = v; return true; });
        if (ColumnId is null) yield return ParameterValue.Bind(FootingParameters.Rotation, () => Rotation, v => Rotation = ((v % 360) + 360) % 360);
        yield return ParameterValue.ReadOnly(FootingParameters.BelowGround, () => Math.Round(Footings.DepthBelowGround(document, this) ?? 0));
        yield return Units.Imperial
            ? ParameterValue.ReadOnly(FootingParameters.PointLoadPounds, () => ColumnId is { } id && document.FindElement(id) is Column carried ? Math.Round(Loads.OnColumn(document, carried) * Units.PoundsPerKilonewton) : 0)
            : ParameterValue.ReadOnly(FootingParameters.PointLoad, () => ColumnId is { } id && document.FindElement(id) is Column carried ? Math.Round(Loads.OnColumn(document, carried), 1) : 0);
        yield return ParameterValue.ReadOnly(FootingParameters.Volume, () => Footings.Volume(document, this));
        yield return ParameterValue.ReadOnly(FootingParameters.Regulations, () => StructureRules.Verdict(document, this));
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
    public static readonly ParameterDefinition Rotation = new("Rotation", ParameterDataType.Angle, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition BelowGround = new("Depth below Ground", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition LineLoad = new("Load Carried (kN/m)", ParameterDataType.Number, ParameterBinding.Instance, ParameterGroup.Analytical);
    public static readonly ParameterDefinition PointLoad = new("Load Carried (kN)", ParameterDataType.Number, ParameterBinding.Instance, ParameterGroup.Analytical);
    public static readonly ParameterDefinition LineLoadPounds = new("Load Carried (lb/ft)", ParameterDataType.Number, ParameterBinding.Instance, ParameterGroup.Analytical);
    public static readonly ParameterDefinition PointLoadPounds = new("Load Carried (lb)", ParameterDataType.Number, ParameterBinding.Instance, ParameterGroup.Analytical);
    public static readonly ParameterDefinition Volume = new("Volume", ParameterDataType.Volume, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Regulations = new("Regulations", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.IdentityData);
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

    /// <summary>How far a footing's underside is below the ground beside it, mm: the lower side of a wall's, the lowest corner of a pad's.</summary>
    public static double? DepthBelowGround(BimDocument document, WallFooting footing)
    {
        if (Heights(document, footing) is not var (bottom, _) || Outline(document, footing) is not { } outline) return null;
        var level = document.Find<Wall>(footing.WallId) is { } wall ? document.FindLevel(wall.LevelId)?.Elevation ?? 0 : 0;
        return outline.Min(point => GroundAt(document, point, level)) - bottom;
    }

    public static double? DepthBelowGround(BimDocument document, PadFooting pad)
    {
        if (Heights(document, pad) is not var (bottom, _) || Outline(document, pad) is not { } outline) return null;
        var levelId = pad.ColumnId is { } id && document.FindElement(id) is Column column ? column.LevelId : pad.LevelId;
        var level = document.FindLevel(levelId)?.Elevation ?? 0;
        return outline.Min(point => GroundAt(document, point, level)) - bottom;
    }

    /// <summary>The concrete in a footing, mm³.</summary>
    public static double Volume(BimDocument document, Element footing) => footing switch
    {
        WallFooting strip when Outline(document, strip) is { } outline && document.FindType<FootingType>(strip.TypeId) is { } type => Polygon2D.Area(outline) * type.Thickness,
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
            case WallFooting strip when Outline(document, strip) is { } outline && Heights(document, strip) is var (bottom, top):
            {
                mesh.AddExtrusion(outline, bottom, top);
                if (document.Find<Wall>(strip.WallId) is { } wall && StemOutline(document, strip) is { } wallOutline)
                    stem.AddExtrusion(wallOutline, top, wall.GetBaseElevation(document));
                break;
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
        _ => null
    };

    /// <summary>
    /// How deep a footing is to go below its level, mm: as far down as the rules take its underside
    /// below the ground - 0.45 m, or 0.75 to 1.0 m in a shrinkable clay; the IRC's 12 in, or the
    /// frost line - however far the ground is above or below the level there, rounded up to 50.
    /// </summary>
    public static double DepthFor(BimDocument document, Element footing, FootingType type)
    {
        var project = document.ProjectInformation;
        var asked = project.ClearanceStandard.IsUs()
            ? Math.Max(305, project.FrostDepth)
            : StructureRules.AdaDepth(project.Ground, project.ClayShrinkage);
        asked = Math.Max(asked, type.Thickness + 150);

        var level = document.FindLevel(LevelOf(document, footing))?.Elevation ?? 0;
        var outline = footing switch
        {
            WallFooting strip => Outline(document, strip),
            PadFooting pad => Outline(document, pad),
            _ => null
        };
        var ground = outline is { Count: > 0 } ? outline.Min(point => GroundAt(document, point, level)) : level;
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
    public static Guid LevelOf(BimDocument document, Element footing) => footing switch
    {
        WallFooting strip when document.Find<Wall>(strip.WallId) is { } wall => wall.LevelId,
        PadFooting { ColumnId: { } id } when document.FindElement(id) is Column column => column.LevelId,
        _ => footing.LevelId
    };
}
