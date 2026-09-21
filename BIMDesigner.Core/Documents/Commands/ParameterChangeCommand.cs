using BIMDesigner.Core.Parameters;

namespace BIMDesigner.Core.Documents.Commands;

/// <summary>
/// Reverses a single parameter edit made from the property panel.
///
/// The <see cref="ParameterValue"/> closes over the element it belongs to, so holding onto
/// it keeps working after the element is deleted and restored by undo - the object is the
/// same instance either way.
///
/// Some parameters change more than their own value - a wall's location line moves the
/// walls joined to it. Those carry out the edit through a command, and this replays that
/// command instead of writing the old value back.
/// </summary>
public sealed class ParameterChangeCommand : IUndoableCommand
{
    private readonly ParameterValue _parameter;
    private readonly object? _oldValue;
    private readonly object? _newValue;
    private readonly IUndoableCommand? _applied;

    public ParameterChangeCommand(ParameterValue parameter, object? oldValue, object? newValue)
    {
        _parameter = parameter;
        _oldValue = oldValue;
        _newValue = newValue;
        _applied = parameter.TakeAppliedChange();
        Name = $"Change {parameter.Name}";
    }

    public string Name { get; }

    public void Redo()
    {
        if (_applied is not null) _applied.Redo();
        else _parameter.TrySet(_newValue);
    }

    public void Undo()
    {
        if (_applied is not null) _applied.Undo();
        else _parameter.TrySet(_oldValue);
    }
}
