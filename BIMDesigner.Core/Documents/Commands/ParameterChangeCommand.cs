using BIMDesigner.Core.Parameters;

namespace BIMDesigner.Core.Documents.Commands;

/// <summary>
/// Reverses a single parameter edit made from the property panel.
///
/// The <see cref="ParameterValue"/> closes over the element it belongs to, so holding onto
/// it keeps working after the element is deleted and restored by undo - the object is the
/// same instance either way.
/// </summary>
public sealed class ParameterChangeCommand : IUndoableCommand
{
    private readonly ParameterValue _parameter;
    private readonly object? _oldValue;
    private readonly object? _newValue;

    public ParameterChangeCommand(ParameterValue parameter, object? oldValue, object? newValue)
    {
        _parameter = parameter;
        _oldValue = oldValue;
        _newValue = newValue;
        Name = $"Change {parameter.Name}";
    }

    public string Name { get; }

    public void Redo() => _parameter.TrySet(_newValue);

    public void Undo() => _parameter.TrySet(_oldValue);
}
