namespace BIMDesigner.Core.Parameters;

/// <summary>
/// The definition of a parameter: what it is called, what kind of value it holds and where
/// it belongs. Definitions are shared - every wall's "Unconnected Height" points at the
/// same definition object, which is what lets schedules group by parameter later.
/// </summary>
public sealed class ParameterDefinition
{
    public ParameterDefinition(
        string name,
        ParameterDataType dataType,
        ParameterBinding binding,
        ParameterGroup group,
        Guid? sharedGuid = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A parameter must have a name.", nameof(name));

        Name = name;
        DataType = dataType;
        Binding = binding;
        Group = group;
        SharedGuid = sharedGuid;
    }

    public string Name { get; }

    public ParameterDataType DataType { get; }

    public ParameterBinding Binding { get; }

    public ParameterGroup Group { get; }

    /// <summary>
    /// Set for shared parameters (specification section 7), which keep their identity
    /// across projects and families. Null for built-in parameters.
    /// </summary>
    public Guid? SharedGuid { get; }

    public override string ToString() => Name;
}
