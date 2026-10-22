namespace BIMDesigner.Core.Documents.Commands;

/// <summary>
/// An edit that went wrong part-way. What it had done has been taken back as far as it would go
/// and it is not in the history; what went wrong inside it is the inner exception.
/// </summary>
public sealed class EditFailedException(string edit, Exception inner)
    : Exception($"{edit} could not be done: {inner.Message}", inner)
{
    /// <summary>The edit, as the Edit menu names it: "Draw Wall", "Undo Move".</summary>
    public string Edit { get; } = edit;
}
