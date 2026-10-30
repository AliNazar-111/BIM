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

    /// <summary>How many steps there are to undo: where the history stands, to come back to.</summary>
    public int Depth => _done.Count;

    public bool CanRedo => _undone.Count > 0;

    public string? UndoName => CanUndo ? _done[^1].Name : null;

    public string? RedoName => CanRedo ? _undone[^1].Name : null;

    /// <summary>The steps there are to undo, by name, the latest first: the history as a list to go back through.</summary>
    public IReadOnlyList<string> UndoNames => Enumerable.Range(1, _done.Count).Select(back => _done[^back].Name).ToList();

    /// <summary>The steps undone there are to redo, by name, the next first.</summary>
    public IReadOnlyList<string> RedoNames => Enumerable.Range(1, _undone.Count).Select(back => _undone[^back].Name).ToList();

    /// <summary>
    /// Several steps undone at once, the latest first - back to a point in the history picked
    /// from its list. One that will not undo stops there, those before it undone, and is told.
    /// </summary>
    public void Undo(int steps)
    {
        for (var step = 0; step < steps && CanUndo; step++) Undo();
    }

    /// <summary>Several steps redone at once, the next first.</summary>
    public void Redo(int steps)
    {
        for (var step = 0; step < steps && CanRedo; step++) Redo();
    }

    /// <summary>True when the document has changes that are not in the saved file.</summary>
    public bool IsModified => _done.Count != _cleanDepth;

    /// <summary>Raised whenever the history or the modified state changes.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Applies a change and records it. One that goes wrong part-way is taken back as far as it
    /// will go and not recorded - the model is not left half changed with nothing to undo - and
    /// what went wrong is passed on, to be told.
    /// </summary>
    public void Execute(IUndoableCommand command)
    {
        try
        {
            command.Redo();
        }
        catch (Exception failure)
        {
            TakeBack(command.Undo);
            throw new EditFailedException(command.Name, failure);
        }

        Record(command);
    }

    /// <summary>Undoes what a step that went wrong had done, as far as it can: it is the step's own failure that is told, not this.</summary>
    private void TakeBack(Action undo)
    {
        _isReplaying = true;
        try { undo(); }
        catch (Exception)
        {
            // Nothing more can be done for it here.
        }
        finally { _isReplaying = false; }
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

        // An undo that goes wrong part-way is put back as it was, still there to undo.
        _isReplaying = true;
        try { command.Undo(); }
        catch (Exception failure)
        {
            _isReplaying = false;
            TakeBack(command.Redo);
            throw new EditFailedException($"Undo {command.Name}", failure);
        }
        finally { _isReplaying = false; }

        _done.RemoveAt(_done.Count - 1);
        _undone.Add(command);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Redo()
    {
        if (!CanRedo) return;

        var command = _undone[^1];

        _isReplaying = true;
        try { command.Redo(); }
        catch (Exception failure)
        {
            _isReplaying = false;
            TakeBack(command.Undo);
            throw new EditFailedException($"Redo {command.Name}", failure);
        }
        finally { _isReplaying = false; }

        _undone.RemoveAt(_undone.Count - 1);
        _done.Add(command);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// That the model has changes not in any file, whatever the history says: a project brought
    /// back after a crash is in no file until it is saved.
    /// </summary>
    public void MarkModified()
    {
        _cleanDepth = -1;
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
