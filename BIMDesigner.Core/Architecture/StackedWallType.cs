using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Parameters;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// One tier of a stacked wall: a wall type, and how tall it is. A height of zero marks the
/// variable tier, which takes up whatever height the others leave.
/// </summary>
public sealed record StackTier(Guid WallTypeId, double Height)
{
    public bool IsVariable => Height <= 0;
}

/// <summary>
/// A wall made of different constructions one above another (specification section 3.1,
/// "stacked walls"): brick to dado height with render above, a plinth under a timber frame.
///
/// It is a type like any other, so a wall simply has a stacked type. The tiers run from the
/// bottom up, and exactly one of them is variable, so the wall can be any height and the
/// fixed tiers keep theirs.
///
/// A plan cuts the wall at <see cref="PlanCutHeight"/> above its base, and shows the tier it
/// cuts - that tier is the one the wall joins, hosts and bounds rooms with. The 3D model,
/// sections and quantities build every tier at its own height.
/// </summary>
public sealed class StackedWallType : ElementType
{
    /// <summary>Where a floor plan cuts walls, above their base. Millimetres.</summary>
    public const double PlanCutHeight = 1200;

    public StackedWallType(string name) : base(name)
    {
    }

    public override BuiltInCategory Category => BuiltInCategory.Walls;

    /// <summary>Bottom tier first.</summary>
    public List<StackTier> Tiers { get; } = new();

    /// <summary>
    /// The tiers of a wall of this type and height, as heights above its base. The variable
    /// tier stretches or shrinks; if the wall is shorter than the fixed tiers, the ones that do
    /// not fit are cut off at the top.
    /// </summary>
    public IReadOnlyList<(WallType Type, double Bottom, double Top)> Layout(BimDocument document, double height)
    {
        var fixedHeight = Tiers.Where(tier => !tier.IsVariable).Sum(tier => tier.Height);
        var variable = Math.Max(0, height - fixedHeight);

        var result = new List<(WallType, double, double)>();
        var bottom = 0.0;

        foreach (var tier in Tiers)
        {
            if (bottom >= height) break;
            if (document.FindType<WallType>(tier.WallTypeId) is not { } type) continue;

            var top = Math.Min(height, bottom + (tier.IsVariable ? variable : tier.Height));
            if (top > bottom) result.Add((type, bottom, top));
            bottom = top;
        }

        return result;
    }

    /// <summary>The tier at a height above the base: what a plan cut there shows.</summary>
    public WallType? TierAt(BimDocument document, double height, double at)
    {
        var layout = Layout(document, height);
        if (layout.Count == 0) return null;

        foreach (var (type, bottom, top) in layout)
            if (at >= bottom && at < top) return type;

        return at < layout[0].Bottom ? layout[0].Type : layout[^1].Type;
    }

    /// <summary>A copy to edit, as new types are always made.</summary>
    public StackedWallType Duplicate(string name)
    {
        var copy = new StackedWallType(name)
        {
            TypeMark = TypeMark,
            AssemblyCode = AssemblyCode,
            Keynote = Keynote,
            Manufacturer = Manufacturer,
            Url = Url,
            Description = Description,
            Cost = Cost
        };
        copy.Tiers.AddRange(Tiers);
        return copy;
    }

    /// <summary>Why these tiers cannot make a stacked wall, or null if they can.</summary>
    public static string? Problem(BimDocument document, IReadOnlyList<StackTier> tiers)
    {
        if (tiers.Count < 2) return "A stacked wall needs at least two tiers.";
        if (tiers.Count(tier => tier.IsVariable) != 1) return "Exactly one tier must be variable, to take up the rest of the height.";
        if (tiers.Any(tier => document.FindType<WallType>(tier.WallTypeId) is null)) return "Every tier needs a wall type.";
        return null;
    }

    public override IEnumerable<ParameterValue> GetTypeParameters()
    {
        foreach (var parameter in GetCommonTypeParameters()) yield return parameter;

        yield return ParameterValue.ReadOnly(StackedWallTypeParameters.TierCount, () => Tiers.Count);
        yield return ParameterValue.ReadOnly(StackedWallTypeParameters.FixedHeight,
            () => Tiers.Where(tier => !tier.IsVariable).Sum(tier => tier.Height));
    }
}

public static class StackedWallTypeParameters
{
    public static readonly ParameterDefinition TierCount =
        new("Tiers", ParameterDataType.Integer, ParameterBinding.Type, ParameterGroup.Construction);

    public static readonly ParameterDefinition FixedHeight =
        new("Fixed Tier Height", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Construction);
}
