using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Parameters;
using BIMDesigner.Core.Schedules;

namespace BIMDesigner.Core.Annotation;

/// <summary>
/// A tag: a label on the drawing that reads a parameter off the thing it points at
/// (specification section 6.3).
///
/// A tag holds no text of its own. It holds a reference and a field name, and reads the
/// value when the drawing is drawn - so renaming a door type relabels every door on every
/// sheet, and a tag can never say something the model does not. Typed-in labels are how
/// drawing sets come to contradict the building.
/// </summary>
public sealed class Tag : Element, IHostedElement
{
    public override BuiltInCategory Category => BuiltInCategory.Tags;

    /// <summary>The element being labelled.</summary>
    public Guid TargetId { get; set; }

    public Guid HostId => TargetId;

    /// <summary>The parameter to show, by name: "Mark", "Type Name", "Fire Rating".</summary>
    public string Field { get; set; } = "Mark";

    /// <summary>Where the label sits. The leader runs from here back to the element.</summary>
    public Point2D Position { get; set; }

    /// <summary>Whether to draw a line from the label to what it labels.</summary>
    public bool ShowLeader { get; set; } = true;

    public Element? Target(BimDocument document) =>
        document.Elements.FirstOrDefault(element => element.Id == TargetId);

    /// <summary>
    /// What the tag says right now. An empty parameter shows as "?" rather than as nothing,
    /// because a blank tag on a drawing looks like a tag that was never filled in - which is
    /// exactly what it is, and the reader should see that.
    /// </summary>
    public string Read(BimDocument document)
    {
        var target = Target(document);
        if (target is null) return "<deleted>";

        var parameters = Schedule.ParametersOf(document, target);
        if (!parameters.TryGetValue(Field, out var parameter)) return "?";

        var text = parameter.DisplayValue;
        return string.IsNullOrWhiteSpace(text) ? "?" : text;
    }

    public override IEnumerable<ParameterValue> GetInstanceParameters(BimDocument document)
    {
        var target = Target(document);

        yield return ParameterValue.ReadOnly(TagParameters.Label, () => Read(document));
        yield return ParameterValue.ReadOnly(
            TagParameters.Tagged,
            () => target is null ? "<deleted>" : target.Category.ToString());

        // Only fields the tagged element actually has, so a tag cannot be pointed at a
        // parameter that will never resolve.
        var fields = target is null
            ? new[] { Field }
            : Schedule.ParametersOf(document, target).Keys.OrderBy(name => name).ToArray();

        yield return ParameterValue.BindChoice(
            TagParameters.Field, () => Field, v => Field = v, fields);

        yield return ParameterValue.Bind(TagParameters.ShowLeader, () => ShowLeader, v => ShowLeader = v);

        foreach (var parameter in GetCommonParameters(document)) yield return parameter;
    }

    public override string ToString() => $"Tag ({Field})";
}

public static class TagParameters
{
    public static readonly ParameterDefinition Label =
        new("Label", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.IdentityData);

    public static readonly ParameterDefinition Tagged =
        new("Tagged Element", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Constraints);

    public static readonly ParameterDefinition Field =
        new("Field", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.IdentityData);

    public static readonly ParameterDefinition ShowLeader =
        new("Show Leader", ParameterDataType.YesNo, ParameterBinding.Instance, ParameterGroup.Constraints);
}

/// <summary>
/// A note written on the drawing (specification section 6.3).
///
/// Unlike a tag, this really is the author's own words - a caption, an instruction, a
/// clarification. It is the one place on a drawing where free text belongs, which is why it
/// is a separate thing from a tag rather than a tag with the reference left empty.
/// </summary>
public sealed class TextNote : Element
{
    public override BuiltInCategory Category => BuiltInCategory.TextNotes;

    public Point2D Position { get; set; }

    public string Text { get; set; } = "Note";

    /// <summary>Where the leader points, if the note has one.</summary>
    public Point2D? LeaderEnd { get; set; }

    public override IEnumerable<ParameterValue> GetInstanceParameters(BimDocument document)
    {
        yield return ParameterValue.Bind(TextNoteParameters.Text, () => Text, v => Text = v);
        yield return ParameterValue.ReadOnly(TextNoteParameters.HasLeader, () => LeaderEnd is not null);

        foreach (var parameter in GetCommonParameters(document)) yield return parameter;
    }

    public override string ToString() => Text;
}

public static class TextNoteParameters
{
    public static readonly ParameterDefinition Text =
        new("Text", ParameterDataType.MultilineText, ParameterBinding.Instance, ParameterGroup.IdentityData);

    public static readonly ParameterDefinition HasLeader =
        new("Has Leader", ParameterDataType.YesNo, ParameterBinding.Instance, ParameterGroup.Constraints);
}
