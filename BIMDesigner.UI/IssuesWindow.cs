using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BIMDesigner.Core.Coordination;
using BIMDesigner.Core.Documents;
using BIMDesigner.Infrastructure.Interoperability;
using Microsoft.Win32;

namespace BIMDesigner.UI;

/// <summary>
/// Issues - BCF topics: what is to be put right or answered about the model, raised here from the 3D
/// view as it is or brought in from Solibri, Navisworks, BIMcollab and the rest as BCF 2.0, 2.1 or
/// 3.0. Each with its title, type, status and priority, who has it and by when, its labels, what it
/// says, its views - each a camera, the elements it is about and a picture, shown in the 3D view with
/// a click - and what has been said about it. Open clashes become issues; the issues listed go out as
/// BCF 2.1 or 3.0. Every change is a step to undo.
/// </summary>
internal sealed class IssuesWindow : System.Windows.Window
{
    private readonly ICoordinationHost _host;
    private readonly ComboBox _filter = new() { Width = 140, ItemsSource = new[] { "Open issues", "All issues", "Closed issues" }, SelectedIndex = 0 };
    private readonly TextBox _search = new() { Width = 200, Margin = new Thickness(8, 0, 0, 0), ToolTip = "Find an issue by its title, description, labels or who has it" };
    private readonly DataGrid _list = new()
    {
        AutoGenerateColumns = false, IsReadOnly = true, SelectionMode = DataGridSelectionMode.Single, HeadersVisibility = DataGridHeadersVisibility.Column,
        GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, CanUserAddRows = false
    };

    private readonly TextBox _title = new();
    private readonly ComboBox _type = new();
    private readonly ComboBox _status = new();
    private readonly ComboBox _priority = new();
    private readonly TextBox _assigned = new() { ToolTip = "Who has it - best as an email address, as BCF recommends" };
    private readonly DatePicker _due = new();
    private readonly TextBox _labels = new() { ToolTip = "Labels to group issues by, separated by commas" };
    private readonly TextBox _description = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 64, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBlock _raised = new() { Opacity = 0.7, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 6) };
    private readonly Image _snapshot = new() { Stretch = Stretch.Uniform, Height = 190 };
    private readonly TextBlock _viewCaption = new() { Opacity = 0.75, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) };
    private readonly StackPanel _comments = new();
    private readonly TextBox _comment = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 44 };
    private readonly CheckBox _withView = new() { Content = "With the 3D view as it is", Margin = new Thickness(0, 4, 0, 0) };
    private readonly FrameworkElement _details;
    private readonly TextBlock _problem = new();
    private Guid? _issueId;
    private int _view;
    private bool _loading;

    public IssuesWindow(ICoordinationHost host)
    {
        _host = host;
        DialogLook.Apply(this, "Issues", 1240, 780);
        ShowInTaskbar = false;
        MinWidth = 960;
        MinHeight = 560;

        _list.Columns.Add(Column("#", nameof(IssueRow.Number), 36));
        _list.Columns.Add(Column("Title", nameof(IssueRow.Title), 230));
        _list.Columns.Add(Column("Type", nameof(IssueRow.Type), 70));
        _list.Columns.Add(Column("Status", nameof(IssueRow.Status), 80));
        _list.Columns.Add(Column("Priority", nameof(IssueRow.Priority), 66));
        _list.Columns.Add(Column("Assigned to", nameof(IssueRow.AssignedTo), 110));
        _list.Columns.Add(Column("Due", nameof(IssueRow.Due), 76));
        _list.Columns.Add(Column("Said", nameof(IssueRow.Comments), 40));
        _list.SelectionChanged += (_, _) =>
        {
            if (_loading) return;
            _issueId = (_list.SelectedItem as IssueRow)?.Issue.Id;
            _view = 0;
            ShowIssue();
        };
        _list.MouseDoubleClick += (_, _) => ShowView();
        _filter.SelectionChanged += (_, _) => { if (!_loading) Reload(); };
        _search.TextChanged += (_, _) => { if (!_loading) Reload(); };

        Button Make(string text, Action act, string tip)
        {
            var button = new Button { Content = text, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 6, 0), ToolTip = tip };
            button.Click += (_, _) => act();
            return button;
        }

        // The list, and what makes and takes issues.
        var filters = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        filters.Children.Add(_filter);
        filters.Children.Add(_search);
        DockPanel.SetDock(filters, Dock.Top);
        var listButtons = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        listButtons.Children.Add(Make("New Issue", NewIssue, "An issue about the 3D view as it is: its camera, its section box, what is selected, and a picture of it"));
        listButtons.Children.Add(Make("From Clashes", FromClashes, "An issue for each open clash that has none yet: its two elements, a view of it and what has been said"));
        listButtons.Children.Add(Make("Delete", DeleteIssue, "Take the issue away"));
        DockPanel.SetDock(listButtons, Dock.Bottom);
        var left = new DockPanel { Margin = new Thickness(16, 0, 12, 0) };
        left.Children.Add(filters);
        left.Children.Add(listButtons);
        left.Children.Add(_list);

        // The issue picked.
        var form = new Grid();
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        void Field(int row, int column, string label, FrameworkElement field, int span = 1)
        {
            while (form.RowDefinitions.Count <= row) form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var text = new TextBlock { Text = label, Opacity = 0.85, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(column == 0 ? 0 : 12, 3, 8, 3) };
            Grid.SetRow(text, row);
            Grid.SetColumn(text, column);
            field.Margin = new Thickness(0, 3, 0, 3);
            Grid.SetRow(field, row);
            Grid.SetColumn(field, column + 1);
            Grid.SetColumnSpan(field, span);
            form.Children.Add(text);
            form.Children.Add(field);
        }

        Field(0, 0, "Title", _title, 3);
        Field(1, 0, "Type", _type);
        Field(1, 2, "Status", _status);
        Field(2, 0, "Priority", _priority);
        Field(2, 2, "Due", _due);
        Field(3, 0, "Assigned to", _assigned);
        Field(3, 2, "Labels", _labels);
        Field(4, 0, "Description", _description, 3);

        foreach (var box in new[] { _title, _assigned, _labels, _description }) box.LostFocus += (_, _) => Commit();
        foreach (var box in new[] { _type, _status, _priority }) box.SelectionChanged += (_, _) => Commit();

        _due.SelectedDateChanged += (_, _) => Commit();

        var viewBar = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
        var views = new StackPanel { Orientation = Orientation.Horizontal };
        views.Children.Add(Make("<", () => Step(-1), "The view before"));
        views.Children.Add(_viewCaption);
        views.Children.Add(Make(">", () => Step(1), "The view after"));
        views.Children.Add(Make("Show in 3D", ShowView, "Turn the 3D view to this view and select what it is about"));
        views.Children.Add(Make("Add View", AddView, "Add the 3D view as it is as another view of this issue"));
        viewBar.Children.Add(views);

        var say = Make("Comment", AddComment, "Add what is written, as you, now");
        var commentBar = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        DockPanel.SetDock(say, Dock.Right);
        commentBar.Children.Add(say);
        commentBar.Children.Add(_withView);

        var details = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
        details.Children.Add(form);
        details.Children.Add(_raised);
        details.Children.Add(new Border { Child = _snapshot, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Background = new SolidColorBrush(Color.FromRgb(0x20, 0x22, 0x26)) });
        details.Children.Add(viewBar);
        details.Children.Add(new TextBlock { Text = "COMMENTS", FontWeight = FontWeights.SemiBold, Opacity = 0.8, Margin = new Thickness(0, 8, 0, 4) });
        details.Children.Add(_comments);
        details.Children.Add(_comment);
        details.Children.Add(commentBar);
        _details = new ScrollViewer { Content = details, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Width = 520 };
        DockPanel.SetDock(_details, Dock.Right);

        var dock = new DockPanel();
        dock.Children.Add(DialogLook.Intro(
            "Issues raised about the model, or brought in as BCF from other programs. Each view is a camera, the elements it is about - by their IFC GlobalIds, so the same issue opens on the IFC elsewhere - and a picture. "
            + "Double-click an issue, or Show in 3D, to see it in the model."));
        dock.Children.Add(DialogLook.Footer(_problem,
            Make("Import BCF...", ImportBcf, "Bring in issues from a BCF file - 2.0, 2.1 or 3.0: one of an issue here is brought up to date, its comments and views added to"),
            Make("Export BCF...", ExportBcf, "The issues listed as a BCF file - 2.1 or 3.0 - for Solibri, Navisworks, BIMcollab and the rest to open against the IFC"),
            new Button { Content = "Close", IsCancel = true }));
        dock.Children.Add(_details);
        dock.Children.Add(left);
        Content = dock;

        _host.CoordinationChanged += OnCoordinationChanged;
        Closed += (_, _) => _host.CoordinationChanged -= OnCoordinationChanged;
        Reload();
    }

    private void OnCoordinationChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(Reload);

    private BimDocument Document => _host.Document;

    private Issue? Picked => Document.Issues.FirstOrDefault(issue => issue.Id == _issueId);

    private static string Me => Environment.UserName;

    private static DataGridTextColumn Column(string header, string path, double width) =>
        new() { Header = header, Binding = new System.Windows.Data.Binding(path), Width = new DataGridLength(width, DataGridLengthUnitType.Pixel) };

    private sealed record IssueRow(Issue Issue, int Number, string Title, string Type, string Status, string Priority, string AssignedTo, string Due, int Comments);

    /// <summary>The issues as shown: those the filter and search let through.</summary>
    private List<Issue> Listed()
    {
        var find = _search.Text.Trim();
        return Document.Issues.Where(issue => _filter.SelectedIndex switch { 0 => issue.IsOpen, 2 => !issue.IsOpen, _ => true })
            .Where(issue => find.Length == 0 || new[] { issue.Title, issue.Description, issue.AssignedTo, string.Join(" ", issue.Labels), issue.Type, issue.Status }
                .Any(text => text.Contains(find, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    public void Reload()
    {
        _loading = true;
        var rows = Listed().Select(issue => new IssueRow(issue, Document.Issues.IndexOf(issue) + 1, issue.Title, issue.Type, issue.Status, issue.Priority, issue.AssignedTo,
            issue.Due is { } due ? due.ToLocalTime().ToString("d", CultureInfo.CurrentCulture) : string.Empty, issue.Comments.Count)).ToList();
        _list.ItemsSource = rows;
        _list.SelectedItem = rows.FirstOrDefault(row => row.Issue.Id == _issueId) ?? rows.FirstOrDefault();
        _issueId = (_list.SelectedItem as IssueRow)?.Issue.Id;
        _loading = false;
        ShowIssue();
        var open = Document.Issues.Count(issue => issue.IsOpen);
        DialogLook.Say(_problem, Document.Issues.Count switch { 0 => "No issues yet.", 1 => $"1 issue, {open} open.", var count => $"{count} issues, {open} open." });
    }

    /// <summary>The picked issue in the form, its view and what has been said.</summary>
    private void ShowIssue()
    {
        var issue = Picked;
        _loading = true;
        _details.IsEnabled = issue is not null;
        _title.Text = issue?.Title ?? string.Empty;
        Offer(_type, Issues.Types, issue?.Type);
        Offer(_status, Issues.Statuses, issue?.Status);
        Offer(_priority, Issues.Priorities, issue?.Priority);
        _assigned.Text = issue?.AssignedTo ?? string.Empty;
        _due.SelectedDate = issue?.Due?.ToLocalTime().Date;
        _labels.Text = issue is null ? string.Empty : string.Join(", ", issue.Labels);
        _description.Text = issue?.Description ?? string.Empty;
        _raised.Text = issue is null ? string.Empty
            : $"Raised by {(string.IsNullOrWhiteSpace(issue.Author) ? "someone" : issue.Author)} on {issue.Created.ToLocalTime():g}" +
              (issue.Modified is { } modified ? $"; changed by {(string.IsNullOrWhiteSpace(issue.ModifiedBy) ? "someone" : issue.ModifiedBy)} on {modified.ToLocalTime():g}" : string.Empty) +
              (string.IsNullOrWhiteSpace(issue.Stage) ? string.Empty : $"; stage {issue.Stage}");

        var views = issue?.Viewpoints ?? new List<IssueViewpoint>();
        _view = views.Count == 0 ? 0 : Math.Clamp(_view, 0, views.Count - 1);
        var view = views.Count == 0 ? null : views[_view];
        _snapshot.Source = view?.Snapshot is { } picture ? Picture(picture) : null;
        _viewCaption.Text = views.Count == 0 ? "no views"
            : $"view {_view + 1} of {views.Count}{(view!.Selected.Count > 0 ? $" - {Issues.Selected(Document, view).Count} of {view.Selected.Count} elements here" : string.Empty)}{(view.Camera is null ? ", no camera" : string.Empty)}";

        _comments.Children.Clear();
        foreach (var comment in issue?.Comments ?? new List<IssueComment>())
        {
            var text = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
            text.Inlines.Add(new System.Windows.Documents.Run($"{(string.IsNullOrWhiteSpace(comment.Author) ? "Someone" : comment.Author)}, {comment.Date.ToLocalTime():g}") { FontWeight = FontWeights.SemiBold });
            text.Inlines.Add(new System.Windows.Documents.Run(comment.Viewpoint is null ? string.Empty : "  (with a view)") { FontStyle = FontStyles.Italic });
            text.Inlines.Add(new System.Windows.Documents.LineBreak());
            text.Inlines.Add(new System.Windows.Documents.Run(comment.Text));
            _comments.Children.Add(text);
        }

        _loading = false;
    }

    /// <summary>A list to pick from: the usual values and, where the issue came with another of its own, that too.</summary>
    private static void Offer(ComboBox box, IEnumerable<string> usual, string? current)
    {
        var items = usual.ToList();
        if (!string.IsNullOrWhiteSpace(current) && !items.Contains(current, StringComparer.OrdinalIgnoreCase)) items.Add(current);
        box.ItemsSource = items;
        box.SelectedItem = items.FirstOrDefault(item => item.Equals(current, StringComparison.OrdinalIgnoreCase));
    }

    private static string Chosen(ComboBox box, string otherwise) => box.SelectedItem as string ?? otherwise;

    private static BitmapSource? Picture(byte[] data)
    {
        try
        {
            using var stream = new MemoryStream(data);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception exception) when (exception is NotSupportedException or FileFormatException or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    /// <summary>The issues with one changed, as one step to undo.</summary>
    private void Apply(IEnumerable<Issue> issues, string name)
    {
        var command = new SetIssuesCommand(Document, issues, name);
        command.Redo();
        _host.Record(command);
        Reload();
    }

    private void Replace(Issue changed, string name)
    {
        changed.Modified = DateTime.UtcNow;
        changed.ModifiedBy = Me;
        Apply(Document.Issues.Select(issue => issue.Id == changed.Id ? changed : issue), name);
    }

    /// <summary>The form as it stands made the issue's, where it differs.</summary>
    private void Commit()
    {
        if (_loading || Picked is not { } issue) return;
        var labels = _labels.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var due = _due.SelectedDate is { } date ? DateTime.SpecifyKind(date.Date.AddHours(17), DateTimeKind.Local).ToUniversalTime() : (DateTime?)null;
        var (type, status, priority) = (Chosen(_type, issue.Type), Chosen(_status, issue.Status), Chosen(_priority, issue.Priority));
        var same = issue.Title == _title.Text.Trim() && issue.Type == type && issue.Status == status && issue.Priority == priority
                   && issue.AssignedTo == _assigned.Text.Trim() && issue.Description == _description.Text.Trim() && issue.Labels.SequenceEqual(labels)
                   && issue.Due?.ToLocalTime().Date == due?.ToLocalTime().Date;
        if (same) return;
        if (_title.Text.Trim().Length == 0)
        {
            DialogLook.Warn(_problem, "An issue needs a title.");
            _title.Text = issue.Title;
            return;
        }

        var changed = issue.Copy();
        (changed.Title, changed.Type, changed.Status, changed.Priority) = (_title.Text.Trim(), type, status, priority);
        (changed.AssignedTo, changed.Description, changed.Due) = (_assigned.Text.Trim(), _description.Text.Trim(), due);
        changed.Labels.Clear();
        changed.Labels.AddRange(labels);
        Replace(changed, $"Edit Issue {changed.Title}");
    }


    private void NewIssue()
    {
        var view = _host.CaptureViewpoint();
        var issue = new Issue
        {
            Title = $"Issue {Document.Issues.Count + 1}",
            Author = Me,
            Created = DateTime.UtcNow,
            Index = Document.Issues.Count + 1
        };
        issue.Viewpoints.Add(view);
        _issueId = issue.Id;
        _filter.SelectedIndex = _filter.SelectedIndex == 2 ? 0 : _filter.SelectedIndex;
        Apply(Document.Issues.Append(issue), "New Issue");
        DialogLook.Say(_problem, $"{issue.Title} raised on the 3D view as it is{(view.Selected.Count > 0 ? $", about the {view.Selected.Count} elements selected" : string.Empty)}. Give it a title.");
        _title.Focus();
        _title.SelectAll();
    }

    private void DeleteIssue()
    {
        if (Picked is not { } issue) return;
        Apply(Document.Issues.Where(other => other.Id != issue.Id), $"Delete Issue {issue.Title}");
        DialogLook.Say(_problem, $"{issue.Title} taken away.");
    }

    private void Step(int by)
    {
        if (Picked is not { } issue || issue.Viewpoints.Count == 0) return;
        _view = (_view + by + issue.Viewpoints.Count) % issue.Viewpoints.Count;
        ShowIssue();
    }

    private void ShowView()
    {
        if (Picked is not { } issue || issue.Viewpoints.Count == 0) return;
        _host.ShowViewpoint(issue.Viewpoints[Math.Clamp(_view, 0, issue.Viewpoints.Count - 1)]);
    }

    private void AddView()
    {
        if (Picked is not { } issue) return;
        var changed = issue.Copy();
        changed.Viewpoints.Add(_host.CaptureViewpoint());
        _view = changed.Viewpoints.Count - 1;
        Replace(changed, $"Add View to {issue.Title}");
    }

    private void AddComment()
    {
        if (Picked is not { } issue) return;
        var text = _comment.Text.Trim();
        if (text.Length == 0 && _withView.IsChecked != true)
        {
            DialogLook.Warn(_problem, "Write the comment first.");
            return;
        }

        var changed = issue.Copy();
        Guid? view = null;
        if (_withView.IsChecked == true)
        {
            var viewpoint = _host.CaptureViewpoint();
            changed.Viewpoints.Add(viewpoint);
            (view, _view) = (viewpoint.Id, changed.Viewpoints.Count - 1);
        }

        changed.Comments.Add(new IssueComment { Date = DateTime.UtcNow, Author = Me, Text = text, Viewpoint = view });
        _comment.Text = string.Empty;
        _withView.IsChecked = false;
        Replace(changed, $"Comment on {issue.Title}");
    }

    /// <summary>An issue for each open clash with none of its own yet - by the clash's GUID - with a picture of it.</summary>
    private void FromClashes()
    {
        var known = Document.Issues.Select(issue => issue.Id).ToHashSet();
        var made = new List<Issue>();
        var index = Document.Issues.Count;
        foreach (var test in Document.ClashTests)
            foreach (var clash in test.Results.Where(clash => clash.IsOpen && !known.Contains(clash.Id)))
                made.Add(Issues.FromClash(test, clash, Me, _host.Snapshot(clash), ++index));
        var open = Document.ClashTests.Sum(test => test.Results.Count(clash => clash.IsOpen));
        if (made.Count == 0)
        {
            DialogLook.Say(_problem, open == 0 ? "There are no open clashes: run the clash tests in Clash Detective first." : $"Each of the {open} open clashes is an issue already.");
            return;
        }

        _filter.SelectedIndex = 1;
        Apply(Document.Issues.Concat(made), "Issues from Clashes");
        DialogLook.Say(_problem, $"{made.Count} issues made from open clashes{(open > made.Count ? $"; {open - made.Count} were issues already" : string.Empty)}.");
    }

    private void ImportBcf()
    {
        var dialog = new OpenFileDialog { Title = "Import BCF", Filter = "BCF files (*.bcf;*.bcfzip)|*.bcf;*.bcfzip|All files (*.*)|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            using var file = File.OpenRead(dialog.FileName);
            var read = BcfFile.Read(file, Document.ProjectInformation.SurveyOffset);
            if (read.Issues.Count == 0)
            {
                DialogLook.Warn(_problem, $"{Path.GetFileName(dialog.FileName)} has no topics{(read.Notes.Count > 0 ? ": " + read.Notes[0] : ".")}");
                return;
            }

            var updated = read.Issues.Count(issue => Document.Issues.Any(own => own.Id == issue.Id));
            var references = read.Issues.SelectMany(issue => issue.Viewpoints).SelectMany(view => view.Selected).Distinct().ToList();
            var found = references.Count(guid => Core.Elements.IfcGuids.TryExpand(guid, out var id) && Document.FindElement(id) is not null);
            _filter.SelectedIndex = 1;
            Apply(Issues.Merge(Document.Issues, read.Issues), "Import BCF");
            DialogLook.Say(_problem, $"BCF {read.Version}: {read.Issues.Count - updated} issues brought in, {updated} brought up to date" +
                                     (references.Count > 0 ? $"; {found} of the {references.Count} elements they are about are in this model" : string.Empty) +
                                     (read.Notes.Count > 0 ? $". {string.Join(" ", read.Notes.Take(2))}" : "."));
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            DialogLook.Warn(_problem, $"It could not be read: {exception.Message}");
        }
    }

    private void ExportBcf()
    {
        var issues = Listed();
        if (issues.Count == 0)
        {
            DialogLook.Warn(_problem, "There are no issues listed to export.");
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Export BCF",
            Filter = "BCF 2.1 (*.bcf)|*.bcf|BCF 3.0 (*.bcf)|*.bcf",
            FileName = Export.CadExport.SuggestedFileName(Document, "issues", ".bcf")
        };
        if (dialog.ShowDialog(this) != true) return;
        var version = dialog.FilterIndex == 2 ? "3.0" : "2.1";
        try
        {
            using var file = File.Create(dialog.FileName);
            BcfFile.Write(file, Document, issues, version);
            DialogLook.Say(_problem, $"{issues.Count} issues written to {Path.GetFileName(dialog.FileName)} as BCF {version}.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            DialogLook.Warn(_problem, $"It could not be written: {exception.Message}");
        }
    }
}
