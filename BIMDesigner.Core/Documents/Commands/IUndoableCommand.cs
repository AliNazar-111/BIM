namespace BIMDesigner.Core.Documents.Commands;

/// <summary>
/// One reversible change to the model (specification section 12.3, "undo/redo stack").
///
/// Every edit is a command rather than a direct mutation, so the stack can replay it
/// backwards. Commands store only what they need to reverse themselves - not a copy of the
/// whole document - which is what will keep undo affordable on models with millions of
/// elements.
/// </summary>
public interface IUndoableCommand
{
    /// <summary>Shown in the Edit menu, e.g. "Undo Draw Wall".</summary>
    string Name { get; }

    /// <summary>Applies the change. Called once when the edit happens, and again on redo.</summary>
    void Redo();

    /// <summary>Reverses the change, leaving the model exactly as it was.</summary>
    void Undo();
}
