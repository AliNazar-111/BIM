using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BIMDesigner.Core.Coordination;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Parameters;
using BIMDesigner.Infrastructure.Interoperability;
using Microsoft.Win32;

namespace BIMDesigner.UI;

/// <summary>What the coordination windows ask of the main window: the project, a change to record, and the model to show.</summary>
internal interface ICoordinationHost
{
    BimDocument Document { get; }

    /// <summary>Records a change made to the coordination as one step to undo, and draws what it changes.</summary>
    void Record(IUndoableCommand command);

    /// <summary>Selects a clash's two elements and turns the 3D view to it.</summary>
    void ShowClash(Clash clash);

    /// <summary>A picture of the 3D view at a clash, as a PNG - none where it cannot be drawn.</summary>
    byte[]? Snapshot(Clash clash);

    /// <summary>The elements selected in the model.</summary>
    IReadOnlyList<Element> Selected { get; }

    /// <summary>Selects these elements in the model.</summary>
    void Select(IEnumerable<Element> elements);

    /// <summary>Shows the 3D view as the building stands on a date of its programme - or, with none, all of it.</summary>
    void ShowConstruction(DateOnly? date, bool actual, int lookAheadDays);

    /// <summary>Raised when the coordination has changed - an undo, a redo, a project opened.</summary>
    event EventHandler? CoordinationChanged;

    /// <summary>A viewpoint of the 3D view as it is - opened first if it is not: its camera, section box, what is selected, and a picture.</summary>
    IssueViewpoint CaptureViewpoint();

    /// <summary>Turns the 3D view to a viewpoint - its camera and section box, as a step to undo - and selects the elements it is about.</summary>
    void ShowViewpoint(IssueViewpoint viewpoint);
}

/// <summary>
/// Clash Detective: the project's clash tests - what each checks against what, how closely - run
/// one at a time or all together, and what each found: each clash with its status, where it is,
/// what it is between and by how much, who has it and by when, and what has been said about it.
/// Show turns the 3D view to it. The clashes go out as BCF for other BIM tools, as a report, or
/// as a spreadsheet. Every change is a step to undo.
/// </summary>
internal sealed class ClashDetectiveWindow : System.Windows.Window
{
    private readonly ICoordinationHost _host;
    private readonly ListBox _tests = new() { MinHeight = 140 };
    private readonly StackPanel _editor = new();
    private readonly DataGrid _results = new()
    {
        AutoGenerateColumns = false, IsReadOnly = true, SelectionMode = DataGridSelectionMode.Single, HeadersVisibility = DataGridHeadersVisibility.Column,
        GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, CanUserAddRows = false
    };

    private readonly StackPanel _issue = new() { Margin = new Thickness(0, 8, 0, 0) };
    private readonly CheckBox _showResolved = new() { Content = "Show resolved and approved", Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBlock _problem = new();
    private Guid? _testId;
    private bool _loading;

    public ClashDetectiveWindow(ICoordinationHost host)
    {
        _host = host;
        DialogLook.Apply(this, "Clash Detective", 1280, 760);
        ShowInTaskbar = false;
        MinWidth = 900;
        MinHeight = 520;

        _results.Columns.Add(Column("Clash", nameof(ClashRow.Name), 70));
        _results.Columns.Add(Column("Status", nameof(ClashRow.Status), 72));
        _results.Columns.Add(Column("Distance", nameof(ClashRow.Distance), 70));
        _results.Columns.Add(Column("Level", nameof(ClashRow.Level), 90));
        _results.Columns.Add(Column("Grid", nameof(ClashRow.Grid), 50));
        _results.Columns.Add(Column("Element 1", nameof(ClashRow.First), 150));
        _results.Columns.Add(Column("Element 2", nameof(ClashRow.Second), 150));
        _results.Columns.Add(Column("Assigned to", nameof(ClashRow.AssignedTo), 100));
        _results.Columns.Add(Column("Priority", nameof(ClashRow.Priority), 64));
        _results.Columns.Add(Column("Due", nameof(ClashRow.Due), 76));
        _results.Columns.Add(Column("Found", nameof(ClashRow.Found), 76));
        _results.SelectionChanged += (_, _) => ShowIssue();
        _results.MouseDoubleClick += (_, _) => { if (Picked is { } clash) _host.ShowClash(clash); };
        _tests.SelectionChanged += (_, _) =>
        {
            if (_loading) return;
            _testId = (_tests.SelectedItem as ListBoxItem)?.Tag as Guid?;
            ShowTest();
        };
        _showResolved.Click += (_, _) => ShowResults();

        Button Make(string text, Action act, string tip)
        {
            var button = new Button { Content = text, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 6, 0), ToolTip = tip };
            button.Click += (_, _) => act();
            return button;
        }

        var testButtons = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        testButtons.Children.Add(Make("New", AddTest, "A new clash test: pick what is checked against what"));
        testButtons.Children.Add(Make("Delete", DeleteTest, "Take the test away, with what it found"));
        testButtons.Children.Add(Make("Run", () => Run(all: false), "Run the test picked: what it finds again keeps its status; what it no longer finds is resolved"));
        testButtons.Children.Add(Make("Run All", () => Run(all: true), "Run every test"));
        testButtons.Children.Add(Make("Defaults", AddDefaults, "Add the tests a project starts with, where they are missing"));

        var left = new StackPanel { Width = 330, Margin = new Thickness(16, 0, 12, 0) };
        left.Children.Add(Label("TESTS", 0));
        left.Children.Add(_tests);
        left.Children.Add(testButtons);
        left.Children.Add(_editor);
        var leftScroll = new ScrollViewer { Content = left, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        DockPanel.SetDock(leftScroll, Dock.Left);

        var right = new DockPanel { Margin = new Thickness(0, 0, 16, 0) };
        var top = new DockPanel();
        top.Children.Add(Label("CLASHES  -  DOUBLE-CLICK ONE TO SEE IT", 0));
        DockPanel.SetDock(top, Dock.Top);
        right.Children.Add(top);
        var bottom = new StackPanel();
        bottom.Children.Add(_showResolved);
        bottom.Children.Add(_issue);
        DockPanel.SetDock(bottom, Dock.Bottom);
        right.Children.Add(bottom);
        right.Children.Add(_results);

        var dock = new DockPanel();
        dock.Children.Add(DialogLook.Intro(
            "A hard clash is one solid running into another by more than its tolerance - faces that only touch, and what is meant to meet, like a door in its wall or a gutter on its roof, are left out. "
            + "A clearance clash is two coming nearer than the distance given; a duplicate is the same thing drawn twice in one place; a sequence clash is something built, by the construction programme, before what it stands on is finished. "
            + "Run a test again after changes: what it still finds keeps its status, what it no longer finds is resolved."));
        dock.Children.Add(DialogLook.Footer(_problem,
            Make("Show", () => { if (Picked is { } clash) _host.ShowClash(clash); }, "Select the clash's two elements and turn the 3D view to it"),
            Make("BCF...", ExportBcf, "The open clashes as BCF 2.1 issues, with a picture of each, for Navisworks, Solibri, BIMcollab and the rest to open against the IFC"),
            Make("Report...", ExportHtml, "Every test and clash as a web page, with a picture of each"),
            Make("Excel...", ExportExcel, "Every clash as a spreadsheet row"),
            new Button { Content = "Close", IsCancel = true }));
        dock.Children.Add(leftScroll);
        dock.Children.Add(right);
        Content = dock;

        _host.CoordinationChanged += OnCoordinationChanged;
        Closed += (_, _) => _host.CoordinationChanged -= OnCoordinationChanged;
        Reload();
    }

    private void OnCoordinationChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(Reload);

    private BimDocument Document => _host.Document;

    private ClashTest? Test => Document.ClashTests.FirstOrDefault(test => test.Id == _testId);

    private Clash? Picked => (_results.SelectedItem as ClashRow)?.Clash is { } shown ? Test?.Results.FirstOrDefault(clash => clash.Id == shown.Id) : null;

    private static TextBlock Label(string text, double top = 10) => new() { Text = text, FontWeight = FontWeights.SemiBold, Opacity = 0.8, Margin = new Thickness(0, top, 0, 4) };

    private static DataGridTextColumn Column(string header, string path, double width) =>
        new() { Header = header, Binding = new System.Windows.Data.Binding(path), Width = new DataGridLength(width, DataGridLengthUnitType.Pixel) };

    /// <summary>Makes a change to the coordination as one step to undo.</summary>
    private void Change(string name, Action change)
    {
        _host.Record(CoordinationEditCommand.Make(Document, name, change));
        Reload();
    }

    // ---- tests --------------------------------------------------------------------------

    public void Reload()
    {
        _loading = true;
        _tests.Items.Clear();
        foreach (var test in Document.ClashTests)
        {
            var open = test.Results.Count(clash => clash.IsOpen);
            var item = new ListBoxItem
            {
                Content = $"{test.Name}  -  {(test.LastRun is null ? "not run" : open == 0 ? "no clashes" : $"{open} open")}",
                Tag = test.Id
            };
            _tests.Items.Add(item);
            if (test.Id == _testId) item.IsSelected = true;
        }

        if (_tests.SelectedItem is null && _tests.Items.Count > 0) _tests.SelectedIndex = 0;
        _testId = (_tests.SelectedItem as ListBoxItem)?.Tag as Guid?;
        _loading = false;
        ShowTest();
    }

    private void AddTest() => Change("New Clash Test", () =>
    {
        var test = new ClashTest { Name = $"Clash test {Document.ClashTests.Count + 1}" };
        test.SelectionA.Add(BuiltInCategory.StructuralFraming);
        test.SelectionB.Add(BuiltInCategory.Walls);
        Document.ClashTests.Add(test);
        _testId = test.Id;
    });

    private void DeleteTest()
    {
        if (Test is not { } test) return;
        Change("Delete Clash Test", () => Document.ClashTests.RemoveAll(each => each.Id == test.Id));
    }

    private void AddDefaults() => Change("Default Clash Tests", () =>
    {
        foreach (var test in ClashDetective.Defaults())
            if (Document.ClashTests.All(each => each.Name != test.Name)) Document.ClashTests.Add(test);
    });

    /// <summary>Runs the test picked - or all of them - as one step.</summary>
    public void Run(bool all)
    {
        var tests = all ? Document.ClashTests.ToList() : Test is { } one ? new List<ClashTest> { one } : new List<ClashTest>();
        if (tests.Count == 0) return;
        Cursor = System.Windows.Input.Cursors.Wait;
        try
        {
            var found = 0;
            var meshes = Core.Views.ModelMeshBuilder.Build(Document);
            Change(all ? "Run Clash Tests" : $"Run {tests[0].Name}", () =>
            {
                foreach (var test in tests)
                {
                    var run = Document.ClashTests.First(each => each.Id == test.Id);
                    found += ClashDetective.Run(Document, run, DateTime.Now, meshes).Count;
                }
            });
            if (found == 0) DialogLook.Say(_problem, "No open clashes.");
            else DialogLook.Warn(_problem, $"{found} open clash{(found == 1 ? "" : "es")}.");
        }
        finally
        {
            Cursor = null;
        }
    }

    private void ShowTest()
    {
        _editor.Children.Clear();
        if (Test is not { } test)
        {
            _editor.Children.Add(new TextBlock { Text = "No clash tests: New makes one; Defaults adds the ones a project starts with.", TextWrapping = TextWrapping.Wrap, Opacity = 0.7, Margin = new Thickness(0, 10, 0, 0) });
            ShowResults();
            return;
        }

        var name = new TextBox { Text = test.Name };
        name.LostFocus += (_, _) => { if (name.Text.Trim() is { Length: > 0 } text && text != test.Name) Change("Rename Clash Test", () => Test!.Name = text); };
        var kind = new ComboBox { ItemsSource = EnumText.Choices<ClashKind>(), SelectedItem = EnumText.Humanise(test.Kind) };
        kind.SelectionChanged += (_, _) =>
        {
            if (EnumText.TryParse<ClashKind>(kind.SelectedItem as string, out var picked) && picked != test.Kind) Change("Clash Test Kind", () => Test!.Kind = picked);
        };
        var tolerance = new TextBox { Text = test.Tolerance.ToString("0.#"), Width = 80, HorizontalAlignment = HorizontalAlignment.Left };
        tolerance.LostFocus += (_, _) =>
        {
            if (double.TryParse(tolerance.Text, out var value) && value >= 0 && value <= 10000 && Math.Abs(value - test.Tolerance) > 1e-9)
                Change("Clash Test Tolerance", () => Test!.Tolerance = value);
        };
        var hosted = new CheckBox { Content = "Leave out what is meant to meet", IsChecked = test.IgnoreHosted, Margin = new Thickness(0, 8, 0, 0),
            ToolTip = "A door and its wall, a gutter and its roof, a footing and what stands on it, walls joined at a corner" };
        hosted.Click += (_, _) => Change("Clash Test Hosts", () => Test!.IgnoreHosted = hosted.IsChecked == true);
        var insulated = new CheckBox { Content = "Allow for pipes' and ducts' insulation", IsChecked = test.AllowInsulation, Margin = new Thickness(0, 4, 0, 0),
            ToolTip = "A pipe or duct is taken with its insulation round it: insulation running into a beam, a column or another pipe clashes" };
        insulated.Click += (_, _) => Change("Clash Test Insulation", () => Test!.AllowInsulation = insulated.IsChecked == true);

        _editor.Children.Add(Label("NAME"));
        _editor.Children.Add(name);
        _editor.Children.Add(Label("WHAT FOR"));
        _editor.Children.Add(kind);
        _editor.Children.Add(Label(test.Kind == ClashKind.Clearance ? "KEPT CLEAR BY (MM)" : test.Kind == ClashKind.Duplicate ? "WITHIN (MM)" : "TOLERANCE (MM)"));
        _editor.Children.Add(tolerance);
        _editor.Children.Add(hosted);
        _editor.Children.Add(insulated);
        _editor.Children.Add(Label("SELECTION A  -  AGAINST  -  SELECTION B"));
        var sets = new Grid();
        sets.ColumnDefinitions.Add(new ColumnDefinition());
        sets.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        sets.ColumnDefinitions.Add(new ColumnDefinition());
        var a = Categories(test.SelectionA, "Selection A");
        var b = Categories(test.SelectionB, "Selection B");
        Grid.SetColumn(b, 2);
        sets.Children.Add(a);
        sets.Children.Add(b);
        _editor.Children.Add(sets);
        _editor.Children.Add(new TextBlock { Text = test.LastRun is { } run ? $"Last run {run:d MMM yyyy HH:mm}." : "Not run yet.", Opacity = 0.7, Margin = new Thickness(0, 8, 0, 0) });
        ShowResults();
    }

    /// <summary>A list of the categories with something solid to clash, each ticked if the set has it.</summary>
    private ScrollViewer Categories(List<BuiltInCategory> set, string name)
    {
        var list = new StackPanel();
        foreach (var category in Enum.GetValues<BuiltInCategory>().Where(ClashDetective.Solidly).OrderBy(category => EnumText.Humanise(category)))
        {
            var box = new CheckBox { Content = EnumText.Humanise(category), IsChecked = set.Contains(category), Margin = new Thickness(0, 1, 0, 1) };
            box.Click += (_, _) => Change($"{name} {(box.IsChecked == true ? "+" : "-")} {EnumText.Humanise(category)}", () =>
            {
                var test = Test!;
                var target = name == "Selection A" ? test.SelectionA : test.SelectionB;
                if (box.IsChecked == true && !target.Contains(category)) target.Add(category);
                else target.Remove(category);
            });
            list.Children.Add(box);
        }

        return new ScrollViewer { Content = list, Height = 220, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    // ---- clashes ------------------------------------------------------------------------

    /// <summary>A clash as the list shows it.</summary>
    private sealed record ClashRow(Clash Clash, string Name, string Status, string Distance, string Level, string Grid, string First, string Second, string AssignedTo, string Priority, string Due, string Found);

    private void ShowResults()
    {
        var keep = Picked?.Id;
        var rows = (Test?.Results ?? new List<Clash>())
            .Where(clash => _showResolved.IsChecked == true || clash.IsOpen)
            .Select(clash => new ClashRow(clash, clash.Name, clash.Status.ToString(), $"{Core.Units.Millimetres(clash.Distance, "0")}", clash.Level, clash.GridLocation,
                ClashDetective.NameOf(Document, clash.ElementA), ClashDetective.NameOf(Document, clash.ElementB), clash.AssignedTo, clash.Priority.ToString(),
                clash.DueDate?.ToString("d MMM yyyy") ?? string.Empty, clash.Found.ToString("d MMM yyyy")))
            .ToList();
        _results.ItemsSource = rows;
        _results.SelectedItem = rows.FirstOrDefault(row => row.Clash.Id == keep);
        ShowIssue();
    }

    /// <summary>The clash picked as an issue: its status, priority, who has it, when it is due, and what has been said.</summary>
    private void ShowIssue()
    {
        _issue.Children.Clear();
        if (Picked is not { } clash)
        {
            _issue.Children.Add(new TextBlock { Text = "Pick a clash to give it a status, assign it and comment on it.", Opacity = 0.7 });
            return;
        }

        void Set(string what, Action<Clash> change)
        {
            var id = clash.Id;
            Change($"Clash {what}", () =>
            {
                if (Test?.Results.FirstOrDefault(each => each.Id == id) is { } live) change(live);
            });
        }

        _issue.Children.Add(new TextBlock { Text = clash.Description, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
        var row = new WrapPanel();
        var status = new ComboBox { ItemsSource = Enum.GetNames<ClashStatus>(), SelectedItem = clash.Status.ToString(), Width = 100, Margin = new Thickness(0, 0, 8, 0) };
        status.SelectionChanged += (_, _) => { if (Enum.TryParse<ClashStatus>(status.SelectedItem as string, out var value) && value != clash.Status) Set("Status", live => live.Status = value); };
        var priority = new ComboBox { ItemsSource = Enum.GetNames<ClashPriority>(), SelectedItem = clash.Priority.ToString(), Width = 90, Margin = new Thickness(0, 0, 8, 0) };
        priority.SelectionChanged += (_, _) => { if (Enum.TryParse<ClashPriority>(priority.SelectedItem as string, out var value) && value != clash.Priority) Set("Priority", live => live.Priority = value); };
        var assigned = new TextBox { Text = clash.AssignedTo, Width = 180, Margin = new Thickness(0, 0, 8, 0), ToolTip = "Who it is assigned to" };
        assigned.LostFocus += (_, _) => { if (assigned.Text.Trim() != clash.AssignedTo) Set("Assigned To", live => live.AssignedTo = assigned.Text.Trim()); };
        var due = new TextBox { Text = clash.DueDate?.ToString("yyyy-MM-dd") ?? string.Empty, Width = 96, ToolTip = "Due by: a date, such as 2026-11-30" };
        due.LostFocus += (_, _) =>
        {
            var text = due.Text.Trim();
            DateOnly? value = text.Length == 0 ? null : DateOnly.TryParse(text, out var parsed) ? parsed : clash.DueDate;
            if (value != clash.DueDate) Set("Due Date", live => live.DueDate = value);
        };
        foreach (var (label, control) in new (string, Control)[] { ("Status", status), ("Priority", priority), ("Assigned to", assigned), ("Due", due) })
        {
            row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0), Opacity = 0.75 });
            row.Children.Add(control);
        }

        _issue.Children.Add(row);
        foreach (var comment in clash.Comments)
            _issue.Children.Add(new TextBlock { Text = $"{comment.Author}, {comment.Date:d MMM yyyy HH:mm}: {comment.Text}", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
        var say = new TextBox { Width = 520, Margin = new Thickness(0, 6, 8, 0) };
        var add = new Button { Content = "Comment", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 6, 0, 0) };
        add.Click += (_, _) =>
        {
            if (say.Text.Trim() is not { Length: > 0 } text) return;
            Set("Comment", live => live.Comments.Add(new ClashComment(DateTime.Now, Environment.UserName, text)));
        };
        var commentRow = new StackPanel { Orientation = Orientation.Horizontal };
        commentRow.Children.Add(say);
        commentRow.Children.Add(add);
        _issue.Children.Add(commentRow);
    }

    // ---- reports ------------------------------------------------------------------------

    private void ExportBcf()
    {
        var dialog = new SaveFileDialog { Title = "Export Clashes as BCF", Filter = "BCF 2.1 (*.bcf)|*.bcf", FileName = $"{Document.ProjectInformation.Name} clashes.bcf" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            using var file = File.Create(dialog.FileName);
            ClashReports.WriteBcf(file, Document, Document.ClashTests, Environment.UserName, _host.Snapshot);
            _problem.Text = $"Saved {Path.GetFileName(dialog.FileName)}.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { _problem.Text = exception.Message; }
    }

    private void ExportHtml()
    {
        var dialog = new SaveFileDialog { Title = "Clash Report", Filter = "Web page (*.html)|*.html", FileName = $"{Document.ProjectInformation.Name} clash report.html" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, ClashReports.Html(Document, Document.ClashTests, clash => clash.IsOpen ? _host.Snapshot(clash) : null));
            _problem.Text = $"Saved {Path.GetFileName(dialog.FileName)}.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { _problem.Text = exception.Message; }
    }

    private void ExportExcel()
    {
        var dialog = new SaveFileDialog { Title = "Clashes as a Spreadsheet", Filter = "Excel workbook (*.xlsx)|*.xlsx", FileName = $"{Document.ProjectInformation.Name} clashes.xlsx" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            using var file = File.Create(dialog.FileName);
            ClashReports.WriteXlsx(file, Document, Document.ClashTests);
            _problem.Text = $"Saved {Path.GetFileName(dialog.FileName)}.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { _problem.Text = exception.Message; }
    }
}
