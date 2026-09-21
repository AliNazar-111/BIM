namespace BIMDesigner.Core.Documents.Commands;

/// <summary>
/// The project's edit history (specification section 12.3).
///
/// Also answers "are there unsaved changes?". Rather than a separate dirty flag that edits
/// have to remember to set, the stack records how deep it was when the file was last saved;
/// anything above that mark is unsaved work. Undoing back to the mark makes the document
/// clean again, which is what people expect.
/// </summary>
public sealed class UndoStack
{
    private readonly List<IUndoableCommand> _done = new();
    private readonly List<IUndoableCommand> _undone = new();

    /// <summary>Depth of <see cref="_done"/> at the last save. -1 once that point is unreachable.</summary>
    private int _cleanDepth;

    /// <summary>Suppresses recording while the stack is applying an undo or redo.</summary>
    private bool _isReplaying;

    public bool CanUndo => _done.Count > 0;

    public bool CanRedo => _undone.Count > 0;

    public string? UndoName => CanUndo ? _done[^1].Name : null;

    public string? RedoName => CanRedo ? _undone[^1].Name : null;

    /// <summary>True when the document has changes that are not in the saved file.</summary>
    public bool IsModified => _done.Count != _cleanDepth;

    /// <summary>Raised whenever the history or the modified state changes.</summary>
    public event EventHandler? Changed;

    /// <summary>Applies a change and records it.</summary>
    public void Execute(IUndoableCommand command)
    {
        command.Redo();
        Record(command);
    }

    /// <summary>
    /// Records a change that has already been applied. Used by the property panel, which
    /// writes through the parameter before it knows whether the model accepted the value.
    /// </summary>
    public void Record(IUndoableCommand command)
    {
        if (_isReplaying) return;

        _done.Add(command);

        // A new edit after an undo discards the redo branch. If the save mark lived on that
        // branch it can never be reached again, so the document is permanently modified.
        if (_undone.Count > 0)
        {
            _undone.Clear();
            if (_cleanDepth >= _done.Count) _cleanDepth = -1;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Undo()
    {
        if (!CanUndo) return;

        var command = _done[^1];
        _done.RemoveAt(_done.Count - 1);

        _isReplaying = true;
        try { command.Undo(); }
        finally { _isReplaying = false; }

        _undone.Add(command);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Redo()
    {
        if (!CanRedo) return;

        var command = _undone[^1];
        _undone.RemoveAt(_undone.Count - 1);

        _isReplaying = true;
        try { command.Redo(); }
        finally { _isReplaying = false; }

        _done.Add(command);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Called after a successful save or load: the current state is now on disk.</summary>
    public void MarkClean()
    {
        _cleanDepth = _done.Count;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Discards all history, as when a different project is opened.</summary>
    public void Clear()
    {
        _done.Clear();
        _undone.Clear();
        _cleanDepth = 0;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
