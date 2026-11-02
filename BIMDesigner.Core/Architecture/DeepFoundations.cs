using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;
using BIMDesigner.Core.Parameters;
using BIMDesigner.Core.Views;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// A raft foundation: one reinforced slab under the whole building - the ground floor and its
/// foundation in one - thickened round its edge, and under the load-bearing walls inside it, down
/// below the frost; reinforced with fabric top and bottom and bars along the thickenings.
/// </summary>
public sealed class RaftFoundation : Element
{
    private readonly List<Point2D> _outline = new();

    public override BuiltInCategory Category => BuiltInCategory.StructuralFoundations;

    /// <summary>Its edge in plan, anticlockwise.</summary>
    public IReadOnlyList<Point2D> Outline => _outline;

    public void SetOutline(IEnumerable<Point2D> points)
    {
        var replacement = points.ToList();
        _outline.Clear();
        _outline.AddRange(replacement);
        if (Polygon2D.SignedArea(_outline) < 0) _outline.Reverse();
    }

    /// <summary>The slab's thickness, mm.</summary>
    public double Thickness { get; set; } = 200;

    /// <summary>How wide its thickened edge is, mm.</summary>
    public double EdgeWidth { get; set; } = 450;

    /// <summary>How deep its thickened edge goes below its top, mm.</summary>
    public double EdgeDepth { get; set; } = 600;

    /// <summary>How far its top is below the level it is on, mm: nothing for a raft that is the ground floor.</summary>
    public double TopDepth { get; set; }

    /// <summary>Whether it is thickened under the load-bearing walls inside it as round its edge.</summary>
    public bool ThickenUnderWalls { get; set; } = true;

    /// <summary>The fabric or bars in its top, and in its bottom - "A393", "H12 @ 200", "#4 @ 12 in".</summary>
    public string TopReinforcement { get; set; } = "A393";

    public string BottomReinforcement { get; set; } = "A393";

    /// <summary>The bars along its thickenings, at their bottom - "4 H12" - and the links round them.</summary>
    public string EdgeBars { get; set; } = "4 H12";

    public string EdgeLinks { get; set; } = "H8 @ 300";

    public override IEnumerable<ParameterValue> GetInstanceParameters(BimDocument document)
    {
        yield return ParameterValue.BindValidated(DeepFoundationParameters.Thickness, () => Thickness, v => Within(v, 75, 1500, x => Thickness = x));
        yield return ParameterValue.BindValidated(DeepFoundationParameters.EdgeWidth, () => EdgeWidth, v => Within(v, 100, 3000, x => EdgeWidth = x));
        yield return ParameterValue.BindValidated(DeepFoundationParameters.EdgeDepth, () => EdgeDepth, v => Within(v, Thickness, 5000, x => EdgeDepth = x));
        yield return ParameterValue.BindValidated(DeepFoundationParameters.TopDepth, () => TopDepth, v => Within(v, -2000, 5000, x => TopDepth = x));
        yield return ParameterValue.Bind(DeepFoundationParameters.ThickenUnderWalls, () => ThickenUnderWalls, v => ThickenUnderWalls = v);
        yield return ParameterValue.BindValidated(DeepFoundationParameters.TopReinforcement, () => TopReinforcement, v => Spec(v, x => TopReinforcement = x));
        yield return ParameterValue.BindValidated(DeepFoundationParameters.BottomReinforcement, () => BottomReinforcement, v => Spec(v, x => BottomReinforcement = x));
        yield return ParameterValue.BindValidated(DeepFoundationParameters.EdgeBars, () => EdgeBars, v => Spec(v, x => EdgeBars = x));
        yield return ParameterValue.BindValidated(DeepFoundationParameters.EdgeLinks, () => EdgeLinks, v => Spec(v, x => EdgeLinks = x));
        yield return ParameterValue.ReadOnly(DeepFoundationParameters.Area, () => Polygon2D.Area(Outline));
        yield return ParameterValue.ReadOnly(FootingParameters.BelowGround, () => Math.Round(Rafts.EdgeBelowGround(document, this)));
        yield return ParameterValue.ReadOnly(FootingParameters.Volume, () => Rafts.Volume(document, this));
        yield return ParameterValue.ReadOnly(FootingParameters.Regulations, () => StructureRules.Verdict(document, this));
        yield return AnalyticalParameters.Said(document, this);
        foreach (var parameter in GetCommonParameters(document)) yield return parameter;
    }

    internal static bool Within(double value, double least, double most, Action<double> set)
    {
        if (!double.IsFinite(value) || value < least || value > most) return false;
        set(value);
        return true;
    }

    internal static bool Spec(string value, Action<string> set)
    {
        if (string.IsNullOrWhiteSpace(value)) { set(string.Empty); return true; }
        if (BarSpec.Parse(value) is null) return false;
        set(value.Trim());
        return true;
    }
}

/// <summary>
/// A ground beam: a reinforced concrete beam under a wall, spanning from pile to pile, its top just
/// under the ground, with the masonry on it up to the wall - over a void in a shrinkable clay, so the
/// clay can swell without lifting it.
/// </summary>
public sealed class GroundBeam : Element, IHostedElement
{
    public override BuiltInCategory Category => BuiltInCategory.StructuralFoundations;

    public Guid WallId { get; set; }

    public Guid HostId => WallId;

    public double Width { get; set; } = 450;

    /// <summary>How deep it is, mm.</summary>
    public double Depth { get; set; } = 600;

    /// <summary>How far its top is below the level its wall stands on, mm.</summary>
    public double TopDepth { get; set; } = 150;

    /// <summary>The void - or compressible board - under it, against the clay swelling, mm.</summary>
    public double Void { get; set; }

    public string TopBars { get; set; } = "2 H16";

    public string BottomBars { get; set; } = "3 H16";

    public string Links { get; set; } = "H8 @ 200";

    public override IEnumerable<ParameterValue> GetInstanceParameters(BimDocument document)
    {
        yield return ParameterValue.ReadOnly(FootingParameters.Host, () => document.Find<Wall>(WallId) is { } wall ? document.GetWallType(wall)?.Name ?? "Wall" : "<none>");
        yield return ParameterValue.BindValidated(DeepFoundationParameters.Width, () => Width, v => RaftFoundation.Within(v, 150, 3000, x => Width = x));
        yield return ParameterValue.BindValidated(DeepFoundationParameters.BeamDepth, () => Depth, v => RaftFoundation.Within(v, 150, 3000, x => Depth = x));
        yield return ParameterValue.BindValidated(DeepFoundationParameters.TopDepth, () => TopDepth, v => RaftFoundation.Within(v, -1000, 5000, x => TopDepth = x));
        yield return ParameterValue.BindValidated(DeepFoundationParameters.Void, () => Void, v => RaftFoundation.Within(v, 0, 500, x => Void = x));
        yield return ParameterValue.BindValidated(DeepFoundationParameters.TopBars, () => TopBars, v => RaftFoundation.Spec(v, x => TopBars = x));
        yield return ParameterValue.BindValidated(DeepFoundationParameters.BottomBars, () => BottomBars, v => RaftFoundation.Spec(v, x => BottomBars = x));
        yield return ParameterValue.BindValidated(DeepFoundationParameters.Links, () => Links, v => RaftFoundation.Spec(v, x => Links = x));
        yield return ParameterValue.ReadOnly(DeepFoundationParameters.Piles, () => Piles.On(document, this).Count);
        yield return ParameterValue.ReadOnly(FootingParameters.Volume, () => Piles.Volume(document, this));
        yield return ParameterValue.ReadOnly(FootingParameters.Regulations, () => StructureRules.Verdict(document, this));
        yield return AnalyticalParameters.Said(document, this);
        foreach (var parameter in GetCommonParameters(document)) yield return parameter;
    }
}

/// <summary>
/// A bored concrete pile: so wide, so long, its head under the ground beam it carries - or under its
/// level, on its own. Under a beam it stands so far along the beam's wall, and goes where the wall goes.
/// </summary>
public sealed class Pile : Element, IHostedElement
{
    public override BuiltInCategory Category => BuiltInCategory.StructuralFoundations;

    public Guid HostId => BeamId ?? Id;

    /// <summary>Where it stands, on its own.</summary>
    public Point2D Location { get; set; }

    /// <summary>Under a beam, how far along the beam's wall from its start, mm.</summary>
    public double Along { get; set; }

    public double Diameter { get; set; } = 300;

    public double Length { get; set; } = 3000;

    /// <summary>The ground beam it carries, if any: its head is at the beam's underside.</summary>
    public Guid? BeamId { get; set; }

    /// <summary>On its own, how far its head is below its level, mm.</summary>
    public double TopDepth { get; set; } = 750;

    /// <summary>The bars in its top, as a cage - "4 H12" - and how far down they go, mm.</summary>
    public string Bars { get; set; } = "4 H12";

    public double CageLength { get; set; } = 2000;

    /// <summary>How it holds what is on it in the analytical model: pinned, free to turn, or fixed.</summary>
    public SupportFixity Fixity { get; set; }

    public override IEnumerable<ParameterValue> GetInstanceParameters(BimDocument document)
    {
        yield return ParameterValue.BindValidated(DeepFoundationParameters.Diameter, () => Diameter, v => RaftFoundation.Within(v, 100, 2500, x => Diameter = x));
        yield return ParameterValue.BindValidated(DeepFoundationParameters.PileLength, () => Length, v => RaftFoundation.Within(v, 500, 60000, x => Length = x));
        yield return ParameterValue.BindValidated(DeepFoundationParameters.Bars, () => Bars, v => RaftFoundation.Spec(v, x => Bars = x));
        yield return ParameterValue.BindValidated(DeepFoundationParameters.CageLength, () => CageLength, v => RaftFoundation.Within(v, 0, 60000, x => CageLength = x));
        yield return Units.Imperial
            ? ParameterValue.ReadOnly(FootingParameters.PointLoadPounds, () => Math.Round(Piles.Load(document, this) * Units.PoundsPerKilonewton))
            : ParameterValue.ReadOnly(FootingParameters.PointLoad, () => Math.Round(Piles.Load(document, this), 1));
        yield return ParameterValue.ReadOnly(DeepFoundationParameters.Capacity, () => Piles.CapacitySaid(document, this));
        yield return ParameterValue.ReadOnly(FootingParameters.Regulations, () => StructureRules.Verdict(document, this));
        yield return AnalyticalParameters.FixityOf(() => Fixity, v => Fixity = v);
        yield return AnalyticalParameters.Said(document, this);
        foreach (var parameter in GetCommonParameters(document)) yield return parameter;
    }
}

public static class DeepFoundationParameters
{
    public static readonly ParameterDefinition Thickness = new("Slab Thickness", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition EdgeWidth = new("Thickening Width", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition EdgeDepth = new("Thickening Depth", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition TopDepth = new("Top below Level", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition ThickenUnderWalls = new("Thickened under Walls", ParameterDataType.YesNo, ParameterBinding.Instance, ParameterGroup.Construction);
    public static readonly ParameterDefinition TopReinforcement = new("Top Reinforcement", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Structural);
    public static readonly ParameterDefinition BottomReinforcement = new("Bottom Reinforcement", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Structural);
    public static readonly ParameterDefinition EdgeBars = new("Thickening Bars", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Structural);
    public static readonly ParameterDefinition EdgeLinks = new("Thickening Links", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Structural);
    public static readonly ParameterDefinition Area = new("Area", ParameterDataType.Area, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Width = new("Width", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition BeamDepth = new("Depth", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Void = new("Void Under", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition TopBars = new("Top Bars", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Structural);
    public static readonly ParameterDefinition BottomBars = new("Bottom Bars", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Structural);
    public static readonly ParameterDefinition Links = new("Links", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Structural);
    public static readonly ParameterDefinition Piles = new("Piles", ParameterDataType.Integer, ParameterBinding.Instance, ParameterGroup.Structural);
    public static readonly ParameterDefinition Diameter = new("Diameter", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition PileLength = new("Length", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Bars = new("Bars", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Structural);
    public static readonly ParameterDefinition CageLength = new("Cage Length", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Structural);
    public static readonly ParameterDefinition Capacity = new("Capacity", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Analytical);
}
