using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Parameters;

namespace BIMDesigner.Core.Views;

/// <summary>
/// A section marker on a plan, and the view it defines (specification section 6.1).
///
/// The marker is the view. There is no second object holding "the section drawing" - the
/// drawing is produced by cutting the model along this line whenever it is looked at, so a
/// section can never show the building as it used to be. Moving the marker redraws the
/// section; there is nothing to regenerate.
/// </summary>
public sealed class SectionMarker : Element
{
    public override BuiltInCategory Category => BuiltInCategory.Sections;

    /// <summary>The name in the head and on the drawing: "A", "B".</summary>
    public string Name { get; set; } = "A";

    /// <summary>Start of the cut line on plan.</summary>
    public Point2D Start { get; set; }

    /// <summary>End of the cut line on plan.</summary>
    public Point2D End { get; set; }

    /// <summary>Which side of the line the view looks toward.</summary>
    public bool Flipped { get; set; }

    /// <summary>
    /// How far past the cut plane the view sees, in millimetres. A section with no depth
    /// would show only what the knife touched, which is not how a section is read.
    /// </summary>
    public double ViewDepth { get; set; } = 20000;

    public double Length => Start.DistanceTo(End);

    /// <summary>Along the cut line, which is the horizontal axis of the section drawing.</summary>
    public Vector2D Direction => (End - Start).NormalisedOrDefault(Vector2D.UnitX);

    /// <summary>The way the viewer faces. Everything within the view depth this way is seen.</summary>
    public Vector2D LookDirection
    {
        get
        {
            var normal = Direction.PerpendicularLeft();
            return Flipped ? -normal : normal;
        }
    }

    public Line2D Line => new(Start, Direction);

    /// <summary>Distance along the cut line, which is a point's horizontal place in the section.</summary>
    public double DistanceAlong(Point2D point) => (point - Start).Dot(Direction);

    /// <summary>How far in front of the cut plane a point lies. Negative is behind the viewer.</summary>
    public double DepthOf(Point2D point) => (point - Start).Dot(LookDirection);

    public override IEnumerable<ParameterValue> GetInstanceParameters(BimDocument document)
    {
        yield return ParameterValue.Bind(SectionParameters.Name, () => Name, v => Name = v);
        yield return ParameterValue.ReadOnly(SectionParameters.Length, () => Length);
        yield return ParameterValue.Bind(SectionParameters.Flipped, () => Flipped, v => Flipped = v);
        yield return ParameterValue.Bind(
            SectionParameters.ViewDepth,
            () => ViewDepth,
            v => { if (v > 0) ViewDepth = v; });

        foreach (var parameter in GetCommonParameters(document)) yield return parameter;
    }

    public override string ToString() => $"Section {Name}";
}

public static class Sections
{
    /// <summary>
    /// The next unused section letter. Sections are lettered on drawings, and reusing a
    /// letter would make two different cuts share a reference on a sheet.
    /// </summary>
    public static string NextName(BimDocument document)
    {
        var used = document.Elements
            .OfType<SectionMarker>()
            .Select(section => section.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        for (var letter = 'A'; letter <= 'Z'; letter++)
            if (!used.Contains(letter.ToString())) return letter.ToString();

        return $"S{used.Count + 1}";
    }
}

public static class SectionParameters
{
    public static readonly ParameterDefinition Name =
        new("Name", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.IdentityData);

    public static readonly ParameterDefinition Length =
        new("Cut Length", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);

    public static readonly ParameterDefinition Flipped =
        new("Look the Other Way", ParameterDataType.YesNo, ParameterBinding.Instance, ParameterGroup.Constraints);

    public static readonly ParameterDefinition ViewDepth =
        new("View Depth", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Constraints);
}
