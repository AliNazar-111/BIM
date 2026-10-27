using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using BIMDesigner.Core.Coordination;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Parameters;
using BIMDesigner.Infrastructure.Interoperability;
using Microsoft.Win32;

namespace BIMDesigner.UI;

/// <summary>
/// The construction programme (4D): its tasks - made from the model storey by storey, or by hand,
/// or read from Microsoft Project, Primavera P6 or Asta - each with its duration, what it waits for
/// and what it builds; timed by the critical path; shown as a bar chart; measured against its
/// baseline by earned value; and played through in the 3D view day by day, what is going up green,
/// what is coming down red, what is late orange.
/// </summary>
internal sealed class ConstructionScheduleWindow : System.Windows.Window
{
    private readonly ICoordinationHost _host;
    private readonly DataGrid _grid = new()
    {
        AutoGenerateColumns = false, IsReadOnly = true, SelectionMode = DataGridSelectionMode.Single, HeadersVisibility = DataGridHeadersVisibility.Column,
        GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, CanUserAddRows = false
    };

    private readonly StackPanel _editor = new() { Margin = new Thickness(0, 8, 0, 0) };
    private readonly Controls.GanttChart _gantt = new();
    private readonly Controls.CostCurve _curve = new() { Height = 240 };
    private readonly TextBlock _value = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBox _start = new() { Width = 96 };
    private readonly TextBox _statusDate = new() { Width = 96 };
    private readonly Slider _slider = new() { Minimum = 0, Maximum = 1, IsSnapToTickEnabled = true, TickFrequency = 1, Width = 420, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _day = new() { Width = 220, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) };
    private readonly ComboBox _mode = new() { ItemsSource = new[] { "As planned", "As it happened" }, SelectedIndex = 0, Width = 120 };
    private readonly ComboBox _lookAhead = new() { ItemsSource = new[] { "No look-ahead", "1 week ahead", "2 weeks ahead", "4 weeks ahead" }, SelectedIndex = 0, Width = 120, Margin = new Thickness(6, 0, 0, 0) };
    private readonly Button _play = new() { Content = "Play", Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(6, 0, 0, 0) };
    private readonly TextBlock _problem = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private Guid? _taskId;
    private bool _loading;
    private bool _simulating;

    public ConstructionScheduleWindow(ICoordinationHost host)
    {
        _host = host;
        DialogLook.Apply(this, "Construction Programme", 1320, 800);
        MinWidth = 960;
        MinHeight = 560;

        foreach (var (header, path, width) in new[]
                 {
                     ("#", nameof(TaskRow.Number), 34.0), ("Task", nameof(TaskRow.Name), 260), ("Kind", nameof(TaskRow.Kind), 76), ("Days", nameof(TaskRow.Duration), 44),
                     ("Start", nameof(TaskRow.Start), 84), ("Finish", nameof(TaskRow.Finish), 84), ("Waits for", nameof(TaskRow.Predecessors), 80),
                     ("Float", nameof(TaskRow.Float), 44), ("Done", nameof(TaskRow.Done), 50), ("Budget", nameof(TaskRow.Budget), 90), ("Elements", nameof(TaskRow.Elements), 64)
                 })
            _grid.Columns.Add(new DataGridTextColumn { Header = header, Binding = new System.Windows.Data.Binding(path), Width = new DataGridLength(width) });
        _grid.SelectionChanged += (_, _) =>
        {
            if (_loading) return;
            _taskId = (_grid.SelectedItem as TaskRow)?.Task.Id;
            ShowTask();
        };

        Button Make(string text, Action act, string tip)
        {
            var button = new Button { Content = text, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 6, 0), ToolTip = tip };
            button.Click += (_, _) => act();
            return button;
        }

        TextBlock Words(string text) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 4, 0), Opacity = 0.8 };

        var tools = new WrapPanel { Margin = new Thickness(16, 0, 16, 6) };
        tools.Children.Add(Make("Make from Model", Generate, "A programme from the model, storey by storey as it would be built - footings and slab, walls and frame, the floor over, the roof, windows, then inside - each task timed by its trade's output. It replaces the tasks there are."));
        tools.Children.Add(Make("New Task", AddTask, "A task of your own, after the one picked"));
        tools.Children.Add(Make("Delete", DeleteTask, "Take the task picked away"));
        tools.Children.Add(Make("Save Baseline", SaveBaseline, "Take the programme as it stands as the plan progress is measured against"));
        tools.Children.Add(Make("Import...", Import, "Read a programme from Microsoft Project XML - Primavera P6 and Asta Powerproject write it too: tasks of the same name keep what they build"));
        tools.Children.Add(Make("Export...", Export, "Write the programme as Microsoft Project XML, for Project, Primavera P6 or Asta"));
        tools.Children.Add(Words("Starts"));
        tools.Children.Add(_start);
        tools.Children.Add(Words("Status date"));
        tools.Children.Add(_statusDate);
        _start.LostFocus += (_, _) =>
        {
            if (DateOnly.TryParse(_start.Text, out var date) && date != Document.Construction.Start) Change("Programme Start", () => Document.Construction.Start = date);
        };
        _statusDate.LostFocus += (_, _) =>
        {
            DateOnly? date = _statusDate.Text.Trim().Length == 0 ? null : DateOnly.TryParse(_statusDate.Text, out var parsed) ? parsed : Document.Construction.StatusDate;
            if (date != Document.Construction.StatusDate) Change("Status Date", () => Document.Construction.StatusDate = date);
        };

        var tasks = new DockPanel();
        var editing = new ScrollViewer { Content = _editor, MaxHeight = 260, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        DockPanel.SetDock(editing, Dock.Bottom);
        tasks.Children.Add(editing);
        tasks.Children.Add(_grid);

        var value = new StackPanel { Margin = new Thickness(8) };
        value.Children.Add(_curve);
        value.Children.Add(_value);

        var tabs = new TabControl { Margin = new Thickness(16, 0, 16, 6) };
        tabs.Items.Add(new TabItem { Header = "Tasks", Content = tasks });
        tabs.Items.Add(new TabItem { Header = "Bar Chart", Content = new ScrollViewer { Content = _gantt, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } });
        tabs.Items.Add(new TabItem { Header = "Earned Value", Content = new ScrollViewer { Content = value } });

        // The simulation: a day of the programme, shown in the 3D view.
        var simulate = new WrapPanel { Margin = new Thickness(16, 4, 16, 6) };
        simulate.Children.Add(new TextBlock { Text = "SIMULATE", FontWeight = FontWeights.SemiBold, Opacity = 0.8, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        simulate.Children.Add(_slider);
        simulate.Children.Add(_day);
        simulate.Children.Add(_mode);
        simulate.Children.Add(_lookAhead);
        simulate.Children.Add(_play);
        simulate.Children.Add(Make("Show All", StopSimulating, "The whole model again, as it is drawn"));
        simulate.Children.Add(Make("Video...", SaveVideo, "The programme played through day by day in the 3D view, saved as a video"));
        _slider.ValueChanged += (_, _) => { if (!_loading) Simulate(); };
        _mode.SelectionChanged += (_, _) => { if (_simulating) Simulate(); };
        _lookAhead.SelectionChanged += (_, _) => { if (_simulating) Simulate(); };
        _play.Click += (_, _) => TogglePlay();
        _timer.Tick += (_, _) =>
        {
            if (_slider.Value >= _slider.Maximum) TogglePlay();
            else _slider.Value += 1;
        };

        var dock = new DockPanel();
        dock.Children.Add(DialogLook.Intro(
            "Each task builds what it is linked to - the elements picked, and everything of its categories on its storey - and waits for the tasks it follows to finish. It is timed in working days by the critical path: "
            + "the critical tasks, red in the bar chart, have no float. Save a baseline, then put in what actually happened - each task's actual start, finish, how much is done and what it has cost - to see where it stands against the plan."));
        dock.Children.Add(DialogLook.Footer(_problem, new Button { Content = "Close", IsCancel = true }));
        DockPanel.SetDock(tools, Dock.Top);
        dock.Children.Add(tools);
        DockPanel.SetDock(simulate, Dock.Bottom);
        dock.Children.Add(simulate);
        dock.Children.Add(tabs);
        Content = dock;

        _host.CoordinationChanged += OnCoordinationChanged;
        Closed += (_, _) =>
        {
            _timer.Stop();
            _host.CoordinationChanged -= OnCoordinationChanged;
            _host.ShowConstruction(null, false, 0);
        };
        Reload();
    }

    private void OnCoordinationChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(Reload);

    private BimDocument Document => _host.Document;

    private ConstructionSchedule Schedule => Document.Construction;

    private ConstructionTask? Picked => Schedule.Find(_taskId ?? Guid.Empty);

    private void Change(string name, Action change)
    {
        _host.Record(CoordinationEditCommand.Make(Document, name, change));
        Reload();
    }

    private static string Short(DateOnly? date) => date?.ToString("d MMM yy", CultureInfo.CurrentCulture) ?? string.Empty;

    private sealed record TaskRow(ConstructionTask Task, int Number, string Name, string Kind, int Duration, string Start, string Finish, string Predecessors, string Float, string Done, string Budget, int Elements);

    public void Reload()
    {
        _loading = true;
        var times = ConstructionPlanning.Time(Schedule).ToDictionary(timed => timed.Task.Id);
        var budgets = CostPlanning.TaskBudgets(Document);
        var currency = CostPlanning.Currency(Document);
        var numbers = Schedule.Tasks.Select((task, index) => (task.Id, index + 1)).ToDictionary(entry => entry.Id, entry => entry.Item2);
        var rows = Schedule.Tasks.Select((task, index) =>
        {
            var timed = times[task.Id];
            var waits = string.Join(", ", task.Predecessors.Where(link => numbers.ContainsKey(link.Predecessor))
                .Select(link => $"{numbers[link.Predecessor]}{(link.Lag != 0 ? $"{link.Lag:+0;-0}" : string.Empty)}"));
            return new TaskRow(task, index + 1, task.Name, EnumText.Humanise(task.Kind), task.Duration, Short(timed.Start), Short(timed.Finish), waits,
                timed.TotalFloat.ToString(CultureInfo.CurrentCulture), $"{task.PercentComplete:0}%", $"{currency}{budgets.GetValueOrDefault(task.Id):N0}",
                ConstructionPlanning.Elements(Document, task).Count);
        }).ToList();
        _grid.ItemsSource = rows;
        _grid.SelectedItem = rows.FirstOrDefault(row => row.Task.Id == _taskId);
        _start.Text = Schedule.Start.ToString("yyyy-MM-dd");
        _statusDate.Text = Schedule.StatusDate?.ToString("yyyy-MM-dd") ?? string.Empty;

        var finish = ConstructionPlanning.Finish(Schedule);
        _slider.Maximum = Math.Max(1, ConstructionPlanning.DayOf(Schedule, finish) + 1);
        _gantt.Show(ConstructionPlanning.Time(Schedule), Schedule.StatusDate, _simulating ? Shown : null);

        var status = Schedule.StatusDate ?? DateOnly.FromDateTime(DateTime.Today);
        var earned = CostPlanning.Earned(Document, status);
        _curve.Show(CostPlanning.CashFlow(Document), earned, currency);
        _value.Text = Schedule.Tasks.Count == 0 ? string.Empty
            : $"At {status:d MMMM yyyy}: budget {currency}{earned.Budget:N0}; planned value {currency}{earned.Planned:N0}; earned value {currency}{earned.Earned:N0}; actual cost {currency}{earned.Actual:N0}. "
              + $"Schedule variance {currency}{earned.ScheduleVariance:N0} (SPI {earned.SchedulePerformance:0.00}); cost variance {currency}{earned.CostVariance:N0} (CPI {earned.CostPerformance:0.00}); "
              + $"estimate at completion {currency}{earned.EstimateAtCompletion:N0}, {currency}{earned.VarianceAtCompletion:N0} {(earned.VarianceAtCompletion >= 0 ? "under" : "over")} budget. "
              + $"The programme finishes {finish:d MMMM yyyy}.";
        _loading = false;
        ShowTask();
    }

    // ---- tasks ----------------------------------------------------------------------

    private void Generate()
    {
        if (Schedule.Tasks.Count > 0 && MessageBox.Show(this, "Make a new programme from the model? It replaces the tasks there are.", "Construction Programme",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        var start = DateOnly.TryParse(_start.Text, out var given) ? given : DateOnly.FromDateTime(DateTime.Today);
        Change("Make Programme from Model", () => ConstructionPlanning.Generate(Document, start));
        DialogLook.Say(_problem, $"{Schedule.Tasks.Count} tasks, finishing {ConstructionPlanning.Finish(Schedule):d MMMM yyyy}.");
    }

    private void AddTask() => Change("New Task", () =>
    {
        var task = new ConstructionTask { Name = $"Task {Schedule.Tasks.Count + 1}" };
        var at = Picked is { } after ? Schedule.Tasks.IndexOf(after) + 1 : Schedule.Tasks.Count;
        if (Picked is { } before) task.Predecessors.Add(new TaskLink(before.Id));
        Schedule.Tasks.Insert(at, task);
        _taskId = task.Id;
    });

    private void DeleteTask()
    {
        if (Picked is not { } task) return;
        Change("Delete Task", () =>
        {
            Schedule.Tasks.RemoveAll(each => each.Id == task.Id);
            foreach (var other in Schedule.Tasks) other.Predecessors.RemoveAll(link => link.Predecessor == task.Id);
        });
    }

    private void SaveBaseline()
    {
        Change("Save Baseline", () => ConstructionPlanning.SaveBaseline(Schedule));
        DialogLook.Say(_problem, "Baseline saved: progress is measured against the programme as it stands.");
    }

    private void ShowTask()
    {
        _editor.Children.Clear();
        if (Picked is not { } task)
        {
            _editor.Children.Add(new TextBlock { Text = Schedule.Tasks.Count == 0 ? "No tasks yet: Make from Model sets out a programme from what is drawn." : "Pick a task to change it.", Opacity = 0.7 });
            return;
        }

        void Set(string what, Action<ConstructionTask> change)
        {
            var id = task.Id;
            Change(what, () => { if (Schedule.Find(id) is { } live) change(live); });
        }

        TextBox Box(string text, double width, Action<string> commit)
        {
            var box = new TextBox { Text = text, Width = width, Margin = new Thickness(0, 0, 10, 0) };
            box.LostFocus += (_, _) => { if (box.Text != text) commit(box.Text.Trim()); };
            return box;
        }

        WrapPanel Row(params (string Label, FrameworkElement Control)[] fields)
        {
            var row = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            foreach (var (label, control) in fields)
            {
                row.Children.Add(new TextBlock { Text = label, Opacity = 0.75, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
                row.Children.Add(control);
            }

            return row;
        }

        DateOnly? Date(string text, DateOnly? otherwise) => text.Length == 0 ? null : DateOnly.TryParse(text, out var date) ? date : otherwise;
        var numbers = Schedule.Tasks.Select((each, index) => (each.Id, index + 1)).ToDictionary(entry => entry.Id, entry => entry.Item2);
        var waits = string.Join(", ", task.Predecessors.Where(link => numbers.ContainsKey(link.Predecessor)).Select(link => $"{numbers[link.Predecessor]}{(link.Lag != 0 ? $"{link.Lag:+0;-0}" : string.Empty)}"));

        var kind = new ComboBox { ItemsSource = EnumText.Choices<TaskKind>(), SelectedItem = EnumText.Humanise(task.Kind), Width = 110, Margin = new Thickness(0, 0, 10, 0) };
        kind.SelectionChanged += (_, _) => { if (EnumText.TryParse<TaskKind>(kind.SelectedItem as string, out var picked) && picked != task.Kind) Set("Task Kind", live => live.Kind = picked); };

        _editor.Children.Add(Row(
            ("Task", Box(task.Name, 280, text => { if (text.Length > 0) Set("Rename Task", live => live.Name = text); })),
            ("Kind", kind),
            ("Working days", Box(task.Duration.ToString(CultureInfo.CurrentCulture), 50, text => { if (int.TryParse(text, out var days) && days >= 0) Set("Task Duration", live => live.Duration = days); })),
            ("Not before", Box(task.StartNoEarlierThan?.ToString("yyyy-MM-dd") ?? string.Empty, 96, text => Set("Task Earliest Start", live => live.StartNoEarlierThan = Date(text, live.StartNoEarlierThan)))),
            ("Waits for", Box(waits, 110, text => Set("Task Links", live => Link(live, text))))));

        _editor.Children.Add(Row(
            ("Done %", Box(task.PercentComplete.ToString("0", CultureInfo.CurrentCulture), 46, text => { if (double.TryParse(text, out var done) && done is >= 0 and <= 100) Set("Task Progress", live => live.PercentComplete = done); })),
            ("Actual start", Box(task.ActualStart?.ToString("yyyy-MM-dd") ?? string.Empty, 96, text => Set("Task Actual Start", live => live.ActualStart = Date(text, live.ActualStart)))),
            ("Actual finish", Box(task.ActualFinish?.ToString("yyyy-MM-dd") ?? string.Empty, 96, text => Set("Task Actual Finish", live => live.ActualFinish = Date(text, live.ActualFinish)))),
            ("Actual cost", Box(task.ActualCost.ToString("0.##", CultureInfo.CurrentCulture), 90, text => { if (decimal.TryParse(text, out var cost) && cost >= 0) Set("Task Actual Cost", live => live.ActualCost = cost); })),
            ("Baseline", new TextBlock { Text = task.BaselineStart is { } bs && task.BaselineFinish is { } bf ? $"{bs:d MMM} - {bf:d MMM yy}" : "none", VerticalAlignment = VerticalAlignment.Center })));

        var elements = ConstructionPlanning.Elements(Document, task);
        var rule = task.Categories.Count == 0 ? "no rule" :
            $"every {string.Join(", ", task.Categories.Select(category => EnumText.Humanise(category).ToLowerInvariant()))}{(task.Walls != WallSide.Any ? $" ({task.Walls.ToString().ToLowerInvariant()})" : string.Empty)}"
            + (task.LevelId is { } level ? $" on {Document.FindLevel(level)?.Name}" : string.Empty);
        var builds = new WrapPanel();
        builds.Children.Add(new TextBlock { Text = $"Builds {elements.Count} element{(elements.Count == 1 ? "" : "s")}: {task.ElementIds.Count} picked, and {rule}.", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
        Button Act(string text, Action act, string tip)
        {
            var button = new Button { Content = text, Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 6, 0), ToolTip = tip };
            button.Click += (_, _) => act();
            return button;
        }

        builds.Children.Add(Act("Attach Selection", () =>
        {
            var picked = _host.Selected.Select(element => element.Id).ToList();
            if (picked.Count == 0) { DialogLook.Warn(_problem, "Select the elements in the model first."); return; }
            Set("Attach to Task", live => live.ElementIds.AddRange(picked.Where(id => !live.ElementIds.Contains(id))));
        }, "Link what is selected in the model to this task"));
        builds.Children.Add(Act("Detach Selection", () =>
        {
            var picked = _host.Selected.Select(element => element.Id).ToHashSet();
            Set("Detach from Task", live => live.ElementIds.RemoveAll(picked.Contains));
        }, "Unlink what is selected from this task - those its rule takes stay"));
        builds.Children.Add(Act("Clear Rule", () => Set("Clear Task Rule", live => { live.Categories.Clear(); live.LevelId = null; }), "Keep only the elements picked"));
        builds.Children.Add(Act("Select", () => _host.Select(elements), "Select what this task builds"));
        _editor.Children.Add(builds);
    }

    /// <summary>Sets what a task waits for from text: task numbers, each with a lag in days after it - "2, 5+3".</summary>
    private void Link(ConstructionTask task, string text)
    {
        var links = new List<TaskLink>();
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var sign = part.IndexOfAny(new[] { '+', '-' }, 1);
            var (number, lag) = sign > 0 ? (part[..sign], part[sign..]) : (part, "0");
            if (!int.TryParse(number, out var index) || index < 1 || index > Schedule.Tasks.Count || !int.TryParse(lag, out var days)) continue;
            var before = Schedule.Tasks[index - 1];
            if (before.Id == task.Id) continue;
            links.Add(new TaskLink(before.Id, days));
        }

        task.Predecessors.Clear();
        task.Predecessors.AddRange(links);
    }

    // ---- files -------------------------------------------------------------------------

    private void Import()
    {
        var dialog = new OpenFileDialog { Title = "Import Programme", Filter = "Microsoft Project XML (*.xml)|*.xml|All files (*.*)|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var read = 0;
            using var file = File.OpenRead(dialog.FileName);
            Change("Import Programme", () => read = MsProjectXml.Read(file, Document));
            DialogLook.Say(_problem, $"{read} tasks read from {Path.GetFileName(dialog.FileName)}.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Xml.XmlException or InvalidDataException) { DialogLook.Warn(_problem, exception.Message); }
    }

    private void Export()
    {
        var dialog = new SaveFileDialog { Title = "Export Programme", Filter = "Microsoft Project XML (*.xml)|*.xml", FileName = $"{Document.ProjectInformation.Name} programme.xml" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            using var file = File.Create(dialog.FileName);
            MsProjectXml.Write(file, Document);
            DialogLook.Say(_problem, $"Saved {Path.GetFileName(dialog.FileName)}.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { DialogLook.Warn(_problem, exception.Message); }
    }

    // ---- simulation ----------------------------------------------------------------------

    private DateOnly Shown => ConstructionPlanning.DateOf(Schedule, (int)_slider.Value);

    private int LookAheadDays => _lookAhead.SelectedIndex switch { 1 => 7, 2 => 14, 3 => 28, _ => 0 };

    private void Simulate()
    {
        if (Schedule.Tasks.Count == 0) return;
        _simulating = true;
        var date = Shown;
        _day.Text = $"{date:dddd d MMMM yyyy} (day {(int)_slider.Value + 1})";
        _host.ShowConstruction(date, _mode.SelectedIndex == 1, LookAheadDays);
        _gantt.Show(ConstructionPlanning.Time(Schedule), Schedule.StatusDate, date);
    }

    private void TogglePlay()
    {
        if (_timer.IsEnabled)
        {
            _timer.Stop();
            _play.Content = "Play";
            return;
        }

        if (_slider.Value >= _slider.Maximum) _slider.Value = 0;
        _timer.Start();
        _play.Content = "Pause";
        Simulate();
    }

    private void StopSimulating()
    {
        _timer.Stop();
        _play.Content = "Play";
        _simulating = false;
        _day.Text = string.Empty;
        _host.ShowConstruction(null, false, 0);
        _gantt.Show(ConstructionPlanning.Time(Schedule), Schedule.StatusDate, null);
    }

    private void SaveVideo()
    {
        if (Schedule.Tasks.Count == 0) return;
        var dialog = new SaveFileDialog { Title = "Save Construction Video", Filter = "Video (*.avi)|*.avi", FileName = $"{Document.ProjectInformation.Name} construction.avi" };
        if (dialog.ShowDialog(this) != true) return;
        var days = Enumerable.Range(0, (int)_slider.Maximum + 1).Select(day => ConstructionPlanning.DateOf(Schedule, day)).ToList();
        if (_host is MainWindow main) _ = main.SaveConstructionVideo(dialog.FileName, days, _mode.SelectedIndex == 1, LookAheadDays);
    }
}
