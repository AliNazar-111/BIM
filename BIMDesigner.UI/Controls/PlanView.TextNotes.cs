using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using BIMDesigner.Core.Annotation;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;

namespace BIMDesigner.UI.Controls;

/// <summary>
/// Notes typed where they go: the Text tool opens a box at the click, and a note is written
/// into it there, as it is on paper - Enter puts it down, Shift+Enter starts another line, Esc
/// or nothing typed leaves nothing. A note double-clicked is typed into the same way.
/// </summary>
public partial class PlanView
{
    private Popup? _noteEditor;
    private Point2D _noteAt;
    private TextNote? _noteEdited;

    /// <summary>Whether a note is being typed.</summary>
    public bool TypingNote => _noteEditor is not null;

    /// <summary>
    /// Opens the box to type a note in: a new one here, or <paramref name="note"/> as it is.
    /// Public so tests can drive it.
    /// </summary>
    public void BeginNoteEdit(Point2D at, TextNote? note = null)
    {
        CloseNoteEditor();
        _noteAt = note?.Position ?? at;
        _noteEdited = note;

        var box = new TextBox
        {
            Text = note?.Text ?? string.Empty,
            MinWidth = 160,
            Padding = new System.Windows.Thickness(3, 1, 3, 1),
            AcceptsReturn = false
        };

        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                var caret = box.CaretIndex;
                box.Text = box.Text.Insert(caret, "\n");
                box.CaretIndex = caret + 1;
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                CommitNoteText(box.Text);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                var wasNew = _noteEdited is null;
                CloseNoteEditor();
                HintChanged?.Invoke(this, wasNew ? DefaultHintFor(PlanTool.Text) : "Left as it was.");
                e.Handled = true;
            }
        };

        // Clicking away keeps what was typed, as leaving any box does.
        box.LostKeyboardFocus += (_, _) =>
        {
            if (_noteEditor is not null) CommitNoteText(box.Text);
        };

        var screen = ModelToScreen(_noteAt);
        _noteEditor = new Popup
        {
            PlacementTarget = this,
            Placement = PlacementMode.Relative,
            HorizontalOffset = screen.X - 3,
            VerticalOffset = screen.Y - 12,
            AllowsTransparency = true,
            StaysOpen = false,
            Child = box
        };

        // Closed by a click away from it: what was typed is kept.
        _noteEditor.Closed += (_, _) =>
        {
            if (_noteEditor is not null) CommitNoteText(box.Text);
            Focus();
        };

        _noteEditor.IsOpen = true;
        box.Focus();
        box.SelectAll();

        HintChanged?.Invoke(this, "Type the note and press Enter - Shift+Enter for a new line. Esc leaves it out.");
    }

    /// <summary>
    /// What was typed, put down: a new note where the box was opened, or the note being edited
    /// changed - one step to undo either way. Nothing typed makes no note, and a note typed
    /// empty is left as it was. Public so tests can drive it.
    /// </summary>
    public bool CommitNoteText(string text)
    {
        var edited = _noteEdited;
        CloseNoteEditor();
        if (Document is null) return false;

        var typed = text.Replace("\r\n", "\n").Trim();
        if (typed.Length == 0)
        {
            HintChanged?.Invoke(this, edited is null ? "Nothing typed, so no note. Click where the next one goes." : "A note has to say something: left as it was. Delete it to take it away.");
            return false;
        }

        if (edited is not null)
        {
            var before = edited.Text;
            if (before == typed) return true;
            Apply(new DelegateCommand("Edit Text", () => edited.Text = typed, () => edited.Text = before));
            Select(edited);
            HintChanged?.Invoke(this, "Note changed. Double-click it to change it again.");
        }
        else
        {
            var note = new TextNote { Position = _noteAt, Text = typed, LevelId = ActiveLevelId };
            Apply(new AddElementCommand(Document, note, "Add Text"));
            Select(note);
            HintChanged?.Invoke(this, "Note added. Click where the next one goes, or Esc. Double-click a note to change it.");
        }

        ModelChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
        return true;
    }

    private void CloseNoteEditor()
    {
        var editor = _noteEditor;
        _noteEditor = null;
        _noteEdited = null;
        if (editor is not null) editor.IsOpen = false;
    }
}
