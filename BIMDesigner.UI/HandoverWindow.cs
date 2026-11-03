using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Handover;
using BIMDesigner.Core.Parameters;

namespace BIMDesigner.UI;

/// <summary>
/// The handover: the asset register - each asset's tag, where it is, its maker and model, and its serial
/// number, bar code and dates to fill in as it is put in - the maintenance plan, task by task, with what
/// is overdue and the work to record as done; the year ahead; the replacements the assets' lives bring;
/// the work recorded; and the handover's own settings. The manual and the guide are written from here.
/// It follows the model as it changes.
/// </summary>
internal sealed class HandoverWindow : System.Windows.Window
{
    private readonly MainWindow _main;
    private readonly DataGrid _assets = Grid(editable: true), _plan = Grid(), _year = Grid(), _replacements = Grid(), _done = Grid();
    private readonly TabItem _assetsTab = new() { Header = "Assets" }, _planTab = new() { Header = "Maintenance" };
    private readonly DatePicker _doneOn = new() { Width = 120, Margin = new Thickness(6, 0, 12, 0) };
    private readonly TextBox _doneBy = new() { Width = 160, Margin = new Thickness(6, 0, 12, 0) }, _notes = new() { Width = 220, Margin = new Thickness(6, 0, 12, 0) };
    private readonly DatePicker _handedOver = new() { Width = 130 };
    private readonly CheckBox _let = new() { Content = "Let to tenants (England): the landlord's gas safety checks, five-yearly electrical inspections and alarm checks at each new tenancy" };
    private readonly CheckBox _flats = new() { Content = "A building of flats with common parts: the responsible person's fire door checks where a storey is over 11 m" };
    private readonly TextBlock _said = new();
    private bool _filling;

    public HandoverWindow(MainWindow main)
    {
        _main = main;
        DialogLook.Apply(this, "Handover - Assets and Maintenance", 1180, 640);
        MinWidth = 720;
        MinHeight = 360;

        var tag = new Button { Content = "Tag Assets", ToolTip = "Write each asset's tag into its mark where it has none - so the tag on the drawings, the labels and the register is one" };
        tag.Click += (_, e) => _main.OnTagAssets(this, e);
        var manual = new Button { Content = "O&M Manual...", ToolTip = "The operation and maintenance manual as a PDF: the Home User Guide, the services system by system, where they are, the register, the plan, labels for the equipment" };
        manual.Click += (_, e) => _main.OnOmManual(this, e);
        var guide = new Button { Content = "Home User Guide...", ToolTip = "The Home User Guide on its own, as a PDF: heating and hot water, ventilation, electricity made on site, staying cool in hot weather" };
        guide.Click += (_, e) => _main.OnHomeUserGuide(this, e);
        var close = new Button { Content = "Close", IsCancel = true, IsDefault = true };
        close.Click += (_, _) => Close();

        _assets.CellEditEnding += OnAssetEdited;
        _assets.MouseDoubleClick += (_, _) => GoTo(_assets);
        _plan.MouseDoubleClick += (_, _) => GoTo(_plan);
        _plan.SelectionMode = DataGridSelectionMode.Extended;
        _plan.CellStyle = OverdueCells();

        // Recording what was done: the selected tasks, on a day, by someone.
        var record = new Button { Content = "Record Done", Padding = new Thickness(10, 2, 10, 2), ToolTip = "Record the selected tasks as done on the day given: each next falls due its interval on from it" };
        record.Click += (_, _) => RecordDone();
        _doneOn.SelectedDate = DateTime.Today;
        var recordBar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        recordBar.Children.Add(new TextBlock { Text = "Done on", VerticalAlignment = VerticalAlignment.Center });
        recordBar.Children.Add(_doneOn);
        recordBar.Children.Add(new TextBlock { Text = "by", VerticalAlignment = VerticalAlignment.Center });
        recordBar.Children.Add(_doneBy);
        recordBar.Children.Add(new TextBlock { Text = "notes", VerticalAlignment = VerticalAlignment.Center });
        recordBar.Children.Add(_notes);
        recordBar.Children.Add(record);
        var planPanel = new DockPanel();
        DockPanel.SetDock(recordBar, Dock.Top);
        planPanel.Children.Add(recordBar);
        planPanel.Children.Add(_plan);
        _planTab.Content = planPanel;

        var remove = new Button { Content = "Remove Record", Padding = new Thickness(10, 2, 10, 2), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 6) };
        remove.Click += (_, _) => RemoveRecord();
        var donePanel = new DockPanel();
        DockPanel.SetDock(remove, Dock.Top);
        donePanel.Children.Add(remove);
        donePanel.Children.Add(_done);

        // The handover's own settings.
        var settings = new StackPanel { Margin = new Thickness(12) };
        var handedRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        handedRow.Children.Add(new TextBlock { Text = "Handed over on", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        handedRow.Children.Add(_handedOver);
        var clear = new Button { Content = "Not Yet", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(8, 1, 8, 1) };
        clear.Click += (_, _) => Change("Clear Handover Date", handover => handover.HandedOver = null);
        handedRow.Children.Add(clear);
        settings.Children.Add(handedRow);
        settings.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12), Opacity = 0.75,
            Text = "What installation dates are taken as where an asset has none of its own, and what the maintenance plan runs from. Once set, maintenance that falls due and is not recorded as done is listed in Warnings."
        });
        _let.Margin = new Thickness(0, 0, 0, 8);
        settings.Children.Add(_let);
        settings.Children.Add(_flats);
        _handedOver.SelectedDateChanged += (_, _) =>
        {
            if (_filling || _handedOver.SelectedDate is not { } day) return;
            Change("Set Handover Date", handover => handover.HandedOver = DateOnly.FromDateTime(day));
        };
        _let.Click += (_, _) => Change(_let.IsChecked == true ? "Let to Tenants" : "Not Let", handover => handover.Let = _let.IsChecked == true);
        _flats.Click += (_, _) => Change(_flats.IsChecked == true ? "A Building of Flats" : "Not Flats", handover => handover.Flats = _flats.IsChecked == true);

        var tabs = new TabControl { Margin = new Thickness(16, 0, 16, 6) };
        _assetsTab.Content = _assets;
        tabs.Items.Add(_assetsTab);
        tabs.Items.Add(_planTab);
        tabs.Items.Add(new TabItem { Header = "Year Ahead", Content = _year });
        tabs.Items.Add(new TabItem { Header = "Replacements", Content = _replacements });
        tabs.Items.Add(new TabItem { Header = "Work Done", Content = donePanel });
        tabs.Items.Add(new TabItem { Header = "Settings", Content = new ScrollViewer { Content = settings, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } });

        var root = new DockPanel();
        root.Children.Add(DialogLook.Footer(_said, tag, manual, guide, close));
        root.Children.Add(DialogLook.Intro("What is kept up in the home, found from the model: its tag, where it is, its maker and model - set on its type - and its serial number, bar code and dates, filled in here as it is put in. " +
                                           "Then each task that keeps it up, how often, by whom and what it rests on - the law, a standard, the maker's instructions or common practice - and the work recorded as done."));
        root.Children.Add(tabs);
        Content = root;

        _main.History.Changed += OnHistoryChanged;
        Closed += (_, _) => _main.History.Changed -= OnHistoryChanged;
        Refresh();
    }

    private static DataGrid Grid(bool editable = false) => new()
    {
        AutoGenerateColumns = false, IsReadOnly = !editable, HeadersVisibility = DataGridHeadersVisibility.Column, GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
        CanUserAddRows = false, CanUserDeleteRows = false, SelectionMode = DataGridSelectionMode.Single
    };

    private void OnHistoryChanged(object? sender, EventArgs e) => Refresh();

    /// <summary>One asset's row of the register: what it is and where, read; its serial number, bar code and dates, to fill in.</summary>
    internal sealed class AssetRow
    {
        public required Asset Asset { get; init; }
        public string Tag { get; init; } = string.Empty;
        public string What { get; init; } = string.Empty;
        public string Where { get; init; } = string.Empty;
        public string Maker { get; init; } = string.Empty;
        public string SerialNumber { get; set; } = string.Empty;
        public string BarCode { get; set; } = string.Empty;
        public string Installed { get; set; } = string.Empty;
        public string WarrantyStart { get; set; } = string.Empty;
        public string WarrantyTo { get; init; } = string.Empty;
        public string Life { get; init; } = string.Empty;
        public string Wants { get; init; } = string.Empty;
    }

    /// <summary>One task on one asset in the plan.</summary>
    internal sealed record TaskRow(DueTask Item, string Status, string Due, string Tag, string Asset, string Task, string Often, string By, string Last, string RestsOn);

    private static void Columns(DataGrid grid, params (string Heading, string Path, bool Editable)[] columns)
    {
        grid.Columns.Clear();
        for (var i = 0; i < columns.Length; i++)
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = columns[i].Heading,
                IsReadOnly = !columns[i].Editable,
                Width = i == columns.Length - 1 ? new DataGridLength(1, DataGridLengthUnitType.Star) : DataGridLength.Auto,
                Binding = new Binding(columns[i].Path) { Mode = columns[i].Editable ? BindingMode.TwoWay : BindingMode.OneWay, UpdateSourceTrigger = UpdateSourceTrigger.LostFocus }
            });
    }

    /// <summary>The register and the plan as the model now stands.</summary>
    internal void Refresh()
    {
        _filling = true;
        try
        {
            Fill();
        }
        finally
        {
            _filling = false;
        }
    }

    private void Fill()
    {
        _assets.CancelEdit(DataGridEditingUnit.Row);
        var document = _main.Document;
        var us = document.ProjectInformation.ClearanceStandard.IsUs();
        var today = DateOnly.FromDateTime(DateTime.Today);
        using var cache = Core.Documents.ModelCache.Begin(document);
        var assets = Assets.Of(document);

        // What is filled in next to what it is and where, the rest after.
        Columns(_assets, ("Tag", nameof(AssetRow.Tag), false), ("What it is", nameof(AssetRow.What), false), ("Where", nameof(AssetRow.Where), false),
            ("Serial number", nameof(AssetRow.SerialNumber), true), ("Installed", nameof(AssetRow.Installed), true), ("Warranty from", nameof(AssetRow.WarrantyStart), true),
            ("Bar code", nameof(AssetRow.BarCode), true), ("Still wanted", nameof(AssetRow.Wants), false), ("Maker and model", nameof(AssetRow.Maker), false),
            ("Warranty to", nameof(AssetRow.WarrantyTo), false), ("Life", nameof(AssetRow.Life), false));
        _assets.ItemsSource = assets.Select(asset =>
        {
            var life = Assets.LifeOf(document, asset);
            return new AssetRow
            {
                Asset = asset, Tag = asset.Tag, What = $"{Assets.Describe(asset.Kind, us)}: {asset.Name}",
                Where = string.Join(", ", new[] { asset.Room, asset.Level?.Name ?? string.Empty }.Where(part => part.Length > 0)),
                Maker = $"{asset.Maker} {asset.Model}".Trim(), SerialNumber = Assets.HasSerial(asset.Kind) ? asset.Record.SerialNumber : "n/a", BarCode = asset.Record.BarCode,
                Installed = Assets.Said(asset.Record.InstalledOn), WarrantyStart = Assets.Said(asset.Record.WarrantyStart),
                WarrantyTo = Assets.Said(Assets.WarrantyEnds(document, asset)),
                Life = life.Years is null ? life.Said : $"{life.Said}{(Assets.ReplaceBy(document, asset) is { } by ? $", to {by.Year}" : string.Empty)}",
                Wants = string.Join(", ", Assets.Missing(document, asset))
            };
        }).ToList();
        var wanting = assets.Count(asset => Assets.Missing(document, asset).Count > 0);
        _assetsTab.Header = $"Assets ({assets.Count})";

        // The plan: overdue first, then by when each is due.
        var due = MaintenancePlan.Of(document, today);
        Columns(_plan, ("", nameof(TaskRow.Status), false), ("Due", nameof(TaskRow.Due), false), ("Tag", nameof(TaskRow.Tag), false), ("Asset", nameof(TaskRow.Asset), false),
            ("Task", nameof(TaskRow.Task), false), ("How often", nameof(TaskRow.Often), false), ("By", nameof(TaskRow.By), false), ("Last done", nameof(TaskRow.Last), false),
            ("Rests on", nameof(TaskRow.RestsOn), false));
        var overdue = due.Count(item => item.OverdueOn(today));
        _plan.ItemsSource = due.OrderBy(item => item.Next ?? DateOnly.MaxValue).ThenBy(item => item.Asset.Tag, StringComparer.Ordinal).Select(item => new TaskRow(item,
            item.OverdueOn(today) ? "Overdue" : item.Next is { } next && next <= today.AddDays(30) ? "Due soon" : string.Empty,
            item.Next is { } when ? MaintenancePlan.Said(when) : item.Task.When, item.Asset.Tag, item.Asset.Name, item.Task.Name, item.Task.Often, item.Task.By,
            item.Last is { } last ? MaintenancePlan.Said(last) : "-",
            $"{item.Task.Basis}: {item.Task.Source}")).ToList();
        _planTab.Header = overdue > 0 ? $"Maintenance ({overdue} overdue)" : "Maintenance";

        // The year ahead.
        Columns(_year, ("Due", "Due", false), ("Tag", "Tag", false), ("Asset", "Asset", false), ("Task", "Task", false), ("By", "By", false));
        _year.ItemsSource = MaintenancePlan.Between(document, today, today.AddYears(1).AddDays(-1), today).Take(5000)
            .Select(entry => new { Due = MaintenancePlan.Said(entry.Due), entry.Item.Asset.Tag, Asset = entry.Item.Asset.Name, Task = entry.Item.Task.Name, entry.Item.Task.By }).ToList();

        // Replacements over 30 years.
        var currency = us ? "$" : "£";
        Columns(_replacements, ("Year", "Year", false), ("Cost", "Cost", false), ("What comes to the end of its life", "What", false));
        _replacements.ItemsSource = MaintenancePlan.Replacements(document, today.Year, 30).Select(year => new
        {
            Year = year.Year.ToString(System.Globalization.CultureInfo.CurrentCulture),
            Cost = year.Assets.Sum(entry => entry.Cost) is var cost && cost > 0 ? $"{currency}{cost.ToString("#,0", System.Globalization.CultureInfo.CurrentCulture)}" : "-",
            What = string.Join(", ", year.Assets.Select(entry => $"{entry.Asset.Tag} {entry.Asset.Name}"))
        }).ToList();

        // The work done.
        var byId = assets.ToDictionary(asset => asset.Element.Id);
        Columns(_done, ("Done", "Done", false), ("Tag", "Tag", false), ("Task", "Task", false), ("By", "By", false), ("Notes", "Notes", false));
        _done.ItemsSource = document.Handover.Done.OrderByDescending(record => record.On).Select(record =>
        {
            var asset = byId.GetValueOrDefault(record.ElementId);
            var task = asset is null ? null : MaintenanceLibrary.For(document, asset).FirstOrDefault(each => each.Id == record.TaskId);
            return new DoneRow(record, MaintenancePlan.Said(record.On), asset?.Tag ?? "-", task?.Name ?? record.TaskId, record.By, record.Notes);
        }).ToList();

        var handover = document.Handover;
        _handedOver.SelectedDate = handover.HandedOver?.ToDateTime(TimeOnly.MinValue);
        _let.IsChecked = handover.Let;
        _let.IsEnabled = !us;
        _flats.IsChecked = handover.Flats;
        _flats.IsEnabled = !us;

        DialogLook.Say(_said, assets.Count == 0
            ? "Nothing to keep up is in the model yet: boilers, alarms, appliances, sanitaryware, doors and windows are."
            : $"{assets.Count} asset{(assets.Count == 1 ? "" : "s")}, {wanting} still wanting their details; {due.Count} task{(due.Count == 1 ? "" : "s")} on them" +
              (overdue > 0 ? $", {overdue} overdue." : "."));
    }

    internal sealed record DoneRow(MaintenanceRecord Record, string Done, string Tag, string Task, string By, string Notes);

    private static readonly Brush OverdueBrush = Frozen(Color.FromRgb(0xD0, 0x3A, 0x2C));

    private static Brush Frozen(Color colour)
    {
        var brush = new SolidColorBrush(colour);
        brush.Freeze();
        return brush;
    }

    /// <summary>An overdue task's cells in red, on whatever the theme draws cells with.</summary>
    private Style OverdueCells()
    {
        var style = new Style(typeof(DataGridCell), TryFindResource(typeof(DataGridCell)) as Style);
        var overdue = new DataTrigger { Binding = new Binding(nameof(TaskRow.Status)), Value = "Overdue" };
        overdue.Setters.Add(new Setter(Control.ForegroundProperty, OverdueBrush));
        overdue.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));
        style.Triggers.Add(overdue);
        return style;
    }

    /// <summary>A serial number, bar code or date typed into the register: written to the asset through its parameter, as one step to undo.</summary>
    private void OnAssetEdited(object? sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit || e.Row.Item is not AssetRow row || e.EditingElement is not TextBox box) return;
        var definition = (e.Column.Header as string) switch
        {
            "Serial number" => AssetParameters.SerialNumber,
            "Bar code" => AssetParameters.BarCode,
            "Installed" => AssetParameters.InstalledOn,
            "Warranty from" => AssetParameters.WarrantyStart,
            _ => null
        };
        if (definition is null) return;
        var document = _main.Document;
        var parameter = AssetParameters.Of(document, row.Asset.Element).FirstOrDefault(each => each.Definition == definition);
        if (parameter is null) return;
        var text = box.Text.Trim();
        var was = parameter.Value as string ?? string.Empty;
        if (text == was) return;
        if (definition.Name.Contains("Date", StringComparison.Ordinal) && text.Length > 0 && Assets.ParseDate(text) is null)
        {
            DialogLook.Warn(_said, $"\"{text}\" is not a date: type it as 3/11/2026, 3 Nov 2026 or 2026-11-03.");
            e.Cancel = true;
            return;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _assets.CommitEdit(DataGridEditingUnit.Row, true);
            _main.History.Execute(new ParameterChangeCommand(parameter, was, text));
        });
    }

    private void GoTo(DataGrid grid)
    {
        var element = grid.SelectedItem switch
        {
            AssetRow row => row.Asset.Element,
            TaskRow row => row.Item.Asset.Element,
            _ => null
        };
        if (element is not null) _main.GoTo(new[] { element });
    }

    /// <summary>The selected tasks recorded as done, as one step to undo.</summary>
    private void RecordDone()
    {
        var items = _plan.SelectedItems.OfType<TaskRow>().Select(row => row.Item).ToList();
        if (items.Count == 0)
        {
            DialogLook.Warn(_said, "Pick the tasks done in the list first - Ctrl or Shift for several.");
            return;
        }

        var on = DateOnly.FromDateTime(_doneOn.SelectedDate ?? DateTime.Today);
        if (on > DateOnly.FromDateTime(DateTime.Today))
        {
            DialogLook.Warn(_said, "Work is recorded once it is done: the day cannot be after today.");
            return;
        }

        _main.History.Record(MaintenancePlan.Record(_main.Document, items, on, _doneBy.Text, _notes.Text));
        _notes.Text = string.Empty;
        DialogLook.Say(_said, items.Count == 1 ? $"Recorded: {items[0].Task.Name} on {items[0].Asset.Tag}, {MaintenancePlan.Said(on)}." : $"Recorded {items.Count} tasks done on {MaintenancePlan.Said(on)}.");
    }

    private void RemoveRecord()
    {
        if (_done.SelectedItem is not DoneRow row) return;
        _main.History.Record(HandoverEditCommand.Make(_main.Document, "Remove Maintenance Record", () => _main.Document.Handover.Done.Remove(row.Record)));
    }

    /// <summary>The handover's settings changed, as one step to undo.</summary>
    private void Change(string name, Action<HandoverSettings> edit)
    {
        if (_filling) return;
        _main.History.Record(HandoverEditCommand.Make(_main.Document, name, () => edit(_main.Document.Handover)));
    }
}
