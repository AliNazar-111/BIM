using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Parameters;

namespace BIMDesigner.Core.Elements;

/// <summary>
/// The base entity of the model (specification section 2.1): a unique ID, a category, a
/// type, a level, a phase, a workset and instance parameters.
///
/// Everything placed in a project derives from this. Subclasses add their own geometry and
/// their own instance parameters, but the identity and bookkeeping live here so that
/// schedules, filters, selection and IFC export can treat every element the same way.
/// </summary>
public abstract class Element
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Fixed per subclass; decides graphics and available parameters.</summary>
    public abstract BuiltInCategory Category { get; }

    /// <summary>The <see cref="ElementType"/> this instance is an occurrence of.</summary>
    public Guid TypeId { get; set; }

    /// <summary>The level this element is hosted on - its base constraint.</summary>
    public Guid LevelId { get; set; }

    /// <summary>User-facing identifier used by tags and schedules, e.g. a door number.</summary>
    public string Mark { get; set; } = string.Empty;

    public string Comments { get; set; } = string.Empty;

    public DesignPhase PhaseCreated { get; set; } = DesignPhase.New;

    /// <summary>Null while the element survives to the end of the project.</summary>
    public DesignPhase? PhaseDemolished { get; set; }

    /// <summary>
    /// Pinned where it is: it is not moved, turned, scaled, mirrored or deleted until it is
    /// unpinned. A copy of it is not pinned.
    /// </summary>
    public bool Pinned { get; set; }

    /// <summary>Worksharing bucket (specification section 7). One default workset for now.</summary>
    public string Workset { get; set; } = "Workset1";

    /// <summary>
    /// The parameters belonging to this placed element, as opposed to its type.
    /// The document is passed in so elements can resolve their level, materials and type.
    /// </summary>
    public abstract IEnumerable<ParameterValue> GetInstanceParameters(BimDocument document);

    /// <summary>Parameters every element carries, appended by subclasses to their own.</summary>
    protected IEnumerable<ParameterValue> GetCommonParameters(BimDocument document)
    {
        yield return ParameterValue.Bind(CommonParameters.Mark, () => Mark, v => Mark = v);
        yield return ParameterValue.Bind(CommonParameters.Comments, () => Comments, v => Comments = v);
        yield return ParameterValue.BindChoice(
            CommonParameters.PhaseCreated,
            () => EnumText.Humanise(PhaseCreated),
            v => { if (EnumText.TryParse<DesignPhase>(v, out var p)) PhaseCreated = p; },
            EnumText.Choices<DesignPhase>());
        // "None" until it is demolished, as Revit shows it.
        yield return ParameterValue.BindChoice(
            CommonParameters.PhaseDemolished,
            () => PhaseDemolished is { } phase ? EnumText.Humanise(phase) : "None",
            v => PhaseDemolished = EnumText.TryParse<DesignPhase>(v, out var p) ? p : null,
            new[] { "None" }.Concat(EnumText.Choices<DesignPhase>()).ToArray());

        yield return ParameterValue.ReadOnly(CommonParameters.Workset, () => Workset);
    }
}

/// <summary>Parameter definitions shared by every category.</summary>
public static class CommonParameters
{
    public static readonly ParameterDefinition Mark =
        new("Mark", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.IdentityData);

    public static readonly ParameterDefinition Comments =
        new("Comments", ParameterDataType.MultilineText, ParameterBinding.Instance, ParameterGroup.IdentityData);

    public static readonly ParameterDefinition PhaseCreated =
        new("Phase Created", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Phasing);

    public static readonly ParameterDefinition PhaseDemolished =
        new("Phase Demolished", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Phasing);

    public static readonly ParameterDefinition Workset =
        new("Workset", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.IdentityData);
}
