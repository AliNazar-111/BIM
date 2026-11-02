using System.Windows;
using System.Windows.Controls;
using BIMDesigner.Infrastructure.Collaboration;

namespace BIMDesigner.UI;

/// <summary>
/// Editing requests: what the others ask of this person - granted, it is theirs; denied, it stays -
/// and the answers to what this person has asked for; and what is selected asked for from whoever has it.
/// </summary>
internal sealed class EditingRequestsWindow : System.Windows.Window
{
    private readonly MainWindow _main;
    private readonly StackPanel _rows = new() { Margin = new Thickness(16, 4, 16, 8) };
    private readonly TextBlock _said = new();

    public EditingRequestsWindow(MainWindow main)
    {
        _main = main;
        DialogLook.Apply(this, "Editing Requests", 640, 420);
        MinWidth = 460;
        MinHeight = 260;

        var ask = new Button { Content = "Ask for Selected", ToolTip = "Ask whoever has each element selected to let you have it" };
        ask.Click += (_, _) => AskForSelected();
        var close = new Button { Content = "Close", IsCancel = true, IsDefault = true };
        close.Click += (_, _) => Close();

        var root = new DockPanel();
        root.Children.Add(DialogLook.Footer(_said, ask, close));
        root.Children.Add(DialogLook.Intro("What others ask of you - grant it and it is theirs - and the answers to what you have asked for. Select what you want to change and Ask for Selected."));
        root.Children.Add(new ScrollViewer { Content = _rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;
    }

    /// <summary>What is selected asked for from whoever has each of it. Internal for the harness.</summary>
    internal void AskForSelected()
    {
        if (_main.Workshare is not { } session)
        {
            DialogLook.Warn(_said, "Open your local copy of the central model first.");
            return;
        }

        var selected = _main.Plan.SelectedElements.ToList();
        if (selected.Count == 0)
        {
            DialogLook.Warn(_said, "Select what you want to change first.");
            return;
        }

        DialogLook.Say(_said, string.Join(" ", selected.Select(element => session.Request(element.Id, element.Workset,
            BIMDesigner.Core.Coordination.Comparisons.NameOf(_main.Document, element))).Distinct()));
        Refresh();
    }

    internal void Refresh()
    {
        _rows.Children.Clear();
        if (_main.Workshare is not { } session)
        {
            DialogLook.Say(_said, "This project is not shared.");
            return;
        }

        var requests = session.Requests();
        foreach (var request in requests) _rows.Children.Add(Row(session, request));
        if (requests.Count == 0) DialogLook.Say(_said, "Nothing asked of you, and nothing of yours answered.");
    }

    private FrameworkElement Row(WorkshareSession session, EditRequest request)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 4, 0, 8) };
        var buttons = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        void Add(string text, Action click)
        {
            var button = new Button { Content = text, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(10, 3, 10, 3) };
            button.Click += (_, _) => click();
            buttons.Children.Add(button);
        }

        if (request.To == session.User && request.Answer.Length == 0)
        {
            panel.Children.Add(new TextBlock { Text = $"{request.From} asks for {request.Name} - {request.When:HH:mm}.", TextWrapping = TextWrapping.Wrap });
            Add("Grant", () => Answer(session, request, grant: true));
            Add("Deny", () => Answer(session, request, grant: false));
        }
        else
        {
            panel.Children.Add(new TextBlock { Text = $"{request.To} {request.Answer.ToLowerInvariant()} you {request.Name}.", TextWrapping = TextWrapping.Wrap });
            Add("OK", () =>
            {
                session.Seen(request.Id);
                Refresh();
            });
        }

        panel.Children.Add(buttons);
        return panel;
    }

    /// <summary>A request answered. Internal for the harness.</summary>
    internal void Answer(WorkshareSession session, EditRequest request, bool grant)
    {
        DialogLook.Say(_said, session.Answer(request.Id, grant));
        _main.RefreshWorksharingDisplay();
        Refresh();
    }
}
