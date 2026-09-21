using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Parameters;

namespace BIMDesigner.Core.Datums;

/// <summary>
/// A gridline (specification section 2.3): the setting-out lines a building is dimensioned
/// and columns are placed against.
///
/// A grid is a <em>datum</em>, not a thing on a floor. It belongs to the building, not to a
/// level, so it appears on every plan - which is exactly what makes it useful for lining
/// storeys up with each other. That is why it carries no level, unlike every other element
/// here.
/// </summary>
public sealed class Grid : Element
{
    public override BuiltInCategory Category => BuiltInCategory.Grids;

    /// <summary>The name in the bubble: "A", "B", "1", "2".</summary>
    public string Name { get; set; } = string.Empty;

    public Point2D Start { get; set; }

    public Point2D End { get; set; }

    /// <summary>Whether the head is drawn at each end. Both by default.</summary>
    public bool BubbleAtStart { get; set; } = true;

    public bool BubbleAtEnd { get; set; } = true;

    public double Length => Start.DistanceTo(End);

    public Vector2D Direction => (End - Start).NormalisedOrDefault(Vector2D.UnitX);

    /// <summary>The infinite line the grid lies on, for finding intersections with others.</summary>
    public Line2D Line => new(Start, Direction);

    /// <summary>
    /// Whether the grid runs more up-the-page than across it. Convention letters the
    /// horizontal grids and numbers the vertical ones, so this decides which it gets.
    /// </summary>
    public bool IsVertical => Math.Abs(Direction.Y) >= Math.Abs(Direction.X);

    public override IEnumerable<ParameterValue> GetInstanceParameters(BimDocument document)
    {
        yield return ParameterValue.Bind(GridParameters.Name, () => Name, v => Name = v);
        yield return ParameterValue.ReadOnly(GridParameters.Length, () => Length);
        yield return ParameterValue.Bind(GridParameters.BubbleAtStart, () => BubbleAtStart, v => BubbleAtStart = v);
        yield return ParameterValue.Bind(GridParameters.BubbleAtEnd, () => BubbleAtEnd, v => BubbleAtEnd = v);

        foreach (var parameter in GetCommonParameters(document)) yield return parameter;
    }

    public override string ToString() => string.IsNullOrWhiteSpace(Name) ? "Grid" : $"Grid {Name}";
}

public static class GridParameters
{
    public static readonly ParameterDefinition Name =
        new("Name", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.IdentityData);

    public static readonly ParameterDefinition Length =
        new("Length", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);

    public static readonly ParameterDefinition BubbleAtStart =
        new("Bubble at Start", ParameterDataType.YesNo, ParameterBinding.Instance, ParameterGroup.Constraints);

    public static readonly ParameterDefinition BubbleAtEnd =
        new("Bubble at End", ParameterDataType.YesNo, ParameterBinding.Instance, ParameterGroup.Constraints);
}

/// <summary>
/// Naming and intersection for gridlines. Grids are only worth having if they line up, so
/// the useful questions are "what should this one be called?" and "where do two of them
/// cross?" - the second being where a column goes.
/// </summary>
public static class Grids
{
    /// <summary>
    /// The next name in sequence: letters for grids running across the page, numbers for
    /// those running up it, each counted separately as drawings conventionally do.
    /// </summary>
    public static string NextName(BimDocument document, bool isVertical)
    {
        var used = document.Elements
            .OfType<Grid>()
            .Where(grid => grid.IsVertical == isVertical)
            .Select(grid => grid.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (isVertical)
        {
            for (var number = 1; ; number++)
                if (used.Add(number.ToString())) return number.ToString();
        }

        for (var index = 0; ; index++)
        {
            // A..Z, then AA, AB, and so on for a very large grid.
            var name = string.Empty;
            for (var n = index; ; n = n / 26 - 1)
            {
                name = (char)('A' + n % 26) + name;
                if (n < 26) break;
            }

            if (used.Add(name)) return name;
        }
    }

    /// <summary>Every point where two gridlines cross, which is where columns are set out.</summary>
    public static IEnumerable<Point2D> Intersections(BimDocument document)
    {
        var grids = document.Elements.OfType<Grid>().ToList();

        for (var i = 0; i < grids.Count; i++)
        for (var j = i + 1; j < grids.Count; j++)
        {
            if (Line2D.TryIntersect(grids[i].Line, grids[j].Line, out var crossing))
                yield return crossing;
        }
    }
}
