using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Coordination;
using BIMDesigner.Core.Documents;
using BIMDesigner.Infrastructure.Interoperability;
using Microsoft.Win32;

namespace BIMDesigner.UI;

/// <summary>
/// The cost plan (5D): the model measured and costed at its types' rates, set out by NRM1's
/// elements in England or UniFormat II's in the US, with the additions to the total - each
/// percentage here to change - the cost per square metre of floor, every element costed, and the
/// spend over the construction programme. It follows the model as it changes.
/// </summary>
internal sealed class CostPlanWindow : System.Windows.Window
{
    private readonly ICoordinationHost _host;
    private readonly DataGrid _plan = Grid();
    private readonly DataGrid _lines = Grid();
    private readonly Controls.CostCurve _curve = new() { Height = 300 };
    private readonly StackPanel _settings = new() { Width = 230, Margin = new Thickness(16, 0, 12, 0) };
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), FontWeight = FontWeights.SemiBold };
    private readonly TextBlock _problem = new();

    public CostPlanWindow(ICoordinationHost host)
    {
        _host = host;
        DialogLook.Apply(this, "Cost Plan", 1180, 760);
        MinWidth = 860;
        MinHeight = 520;

        foreach (var (header, path, width) in new[] { ("Code", nameof(PlanRow.Code), 70.0), ("Element", nameof(PlanRow.Title), 380), ("Amount", nameof(PlanRow.Amount), 140), ("%", nameof(PlanRow.Percent), 60), ("Items", nameof(PlanRow.Items), 60) })
            _plan.Columns.Add(new DataGridTextColumn { Header = header, Binding = new System.Windows.Data.Binding(path), Width = new DataGridLength(width) });
        foreach (var (header, path, width) in new[]
                 {
                     ("Code", nameof(LineRow.Code), 60.0), ("Cost element", nameof(LineRow.Title), 190), ("Type", nameof(LineRow.Type), 230), ("Mark", nameof(LineRow.Mark), 60),
                     ("Level", nameof(LineRow.Level), 100), ("Quantity", nameof(LineRow.Quantity), 80), ("Unit", nameof(LineRow.Unit), 40), ("Rate", nameof(LineRow.Rate), 80), ("Total", nameof(LineRow.Total), 100)
                 })
            _lines.Columns.Add(new DataGridTextColumn { Header = header, Binding = new System.Windows.Data.Binding(path), Width = new DataGridLength(width) });
        _lines.MouseDoubleClick += (_, _) => { if (_lines.SelectedItem is LineRow row) _host.Select(new[] { row.Line.Element }); };

        var flow = new StackPanel { Margin = new Thickness(8) };
        flow.Children.Add(new TextBlock { Text = "The planned spend over the construction programme - each task's elements' cost spread over its working days - with the value earned and the cost so far at the status date.", TextWrapping = TextWrapping.Wrap, Opacity = 0.75, Margin = new Thickness(0, 0, 0, 8) });
        flow.Children.Add(_curve);

        var tabs = new TabControl { Margin = new Thickness(0, 0, 16, 6) };
        tabs.Items.Add(new TabItem { Header = "Cost Plan", Content = _plan });
        tabs.Items.Add(new TabItem { Header = "Elements", Content = _lines });
        tabs.Items.Add(new TabItem { Header = "Cash Flow", Content = new ScrollViewer { Content = flow } });

        var refresh = new Button { Content = "Refresh" };
        refresh.Click += (_, _) => Reload();
        var excel = new Button { Content = "Excel..." };
        excel.Click += (_, _) => Export();

        var left = new ScrollViewer { Content = _settings, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        DockPanel.SetDock(left, Dock.Left);
        var dock = new DockPanel();
        dock.Children.Add(DialogLook.Intro(
            "Each type's Cost is its rate: walls, floors, roofs and ceilings by the square metre - a wall net of its doors and windows, which are costed themselves - beams, railings and what runs along an edge by the metre, the rest each. "
            + "What stays of an existing building is not costed; what is demolished is. Double-click an element to select it."));
        dock.Children.Add(DialogLook.Footer(_problem, refresh, excel, new Button { Content = "Close", IsCancel = true }));
        dock.Children.Add(left);
        dock.Children.Add(tabs);
        Content = dock;

        _host.CoordinationChanged += OnChanged;
        Activated += OnChanged;
        Closed += (_, _) => _host.CoordinationChanged -= OnChanged;
        Reload();
    }

    private void OnChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(Reload);

    private BimDocument Document => _host.Document;

    private static DataGrid Grid() => new()
    {
        AutoGenerateColumns = false, IsReadOnly = true, HeadersVisibility = DataGridHeadersVisibility.Column, GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, CanUserAddRows = false
    };

    private sealed record PlanRow(string Code, string Title, string Amount, string Percent, string Items, bool IsTotal);

    private sealed record LineRow(CostLine Line, string Code, string Title, string Type, string Mark, string Level, string Quantity, string Unit, string Rate, string Total);

    public void Reload()
    {
        var plan = CostPlanning.Summary(Document);
        var currency = plan.Currency;
        string Money(decimal amount) => $"{currency}{amount:N2}";
        _plan.ItemsSource = plan.Rows.Select(row => new PlanRow(row.Code, row.IsTotal && row.Lines.Count == 0 && row.Percent is null ? row.Title.ToUpper(CultureInfo.CurrentCulture) : row.Title,
            Money(row.Amount), row.Percent is { } percent ? $"{percent:0.##}%" : string.Empty, row.Lines.Count > 0 ? row.Lines.Count.ToString(CultureInfo.CurrentCulture) : string.Empty, row.IsTotal)).ToList();
        _lines.ItemsSource = CostPlanning.Lines(Document).OrderBy(line => line.Code, StringComparer.Ordinal)
            .Select(line => new LineRow(line, line.Code, line.Title, line.Type?.Name ?? line.Element.Category.ToString(), line.Element.Mark,
                Document.FindLevel(line.Element.LevelId)?.Name ?? string.Empty, line.Quantity.ToString("0.###", CultureInfo.CurrentCulture),
                CostPlanning.Abbreviation(line.Unit), Money(line.Rate), Money(line.Total)))
            .ToList();
        var status = Document.Construction.StatusDate ?? DateOnly.FromDateTime(DateTime.Today);
        _curve.Show(CostPlanning.CashFlow(Document), Document.Construction.Tasks.Count > 0 ? CostPlanning.Earned(Document, status) : null, currency);
        _summary.Text = $"{Money(plan.Total)} in all - {plan.FloorArea:0.#} m² of floor, {Money(plan.PerSquareMetre)} a m².";
        ShowSettings();
    }

    /// <summary>The additions to the building works, each to change: a step to undo.</summary>
    private void ShowSettings()
    {
        _settings.Children.Clear();
        var us = Document.ProjectInformation.ClearanceStandard.IsUs();
        var cost = Document.CostSettings;
        _settings.Children.Add(new TextBlock { Text = us ? "UNIFORMAT II, ADDITIONS" : "NRM1, ADDITIONS", FontWeight = FontWeights.SemiBold, Opacity = 0.8, Margin = new Thickness(0, 0, 0, 6) });

        void Field(string label, string value, Action<string> commit)
        {
            _settings.Children.Add(new TextBlock { Text = label, Opacity = 0.75, Margin = new Thickness(0, 6, 0, 2) });
            var box = new TextBox { Text = value };
            box.LostFocus += (_, _) => { if (box.Text.Trim() != value) commit(box.Text.Trim()); };
            _settings.Children.Add(box);
        }

        void Percent(string label, double value, Action<CostSettings, double> set) =>
            Field($"{label} (%)", value.ToString("0.##", CultureInfo.CurrentCulture), text =>
            {
                if (!double.TryParse(text, out var percent) || percent is < 0 or > 500) { _problem.Text = $"{label}: a percentage, 0 to 500."; return; }
                _host.Record(CoordinationEditCommand.Make(Document, label, () => set(Document.CostSettings, percent)));
                Reload();
            });

        Percent(us ? "General conditions" : "Main contractor's preliminaries", cost.Preliminaries, (settings, value) => settings.Preliminaries = value);
        Percent(us ? "Overhead and profit" : "Overheads and profit", cost.OverheadsAndProfit, (settings, value) => settings.OverheadsAndProfit = value);
        Percent(us ? "Design fees" : "Project and design team fees", cost.DesignFees, (settings, value) => settings.DesignFees = value);
        Field(us ? "Other project costs" : "Other development and project costs", cost.OtherCosts.ToString("0.##", CultureInfo.CurrentCulture), text =>
        {
            if (!decimal.TryParse(text, out var amount) || amount < 0) { _problem.Text = "Other costs: an amount."; return; }
            _host.Record(CoordinationEditCommand.Make(Document, "Other Costs", () => Document.CostSettings.OtherCosts = amount));
            Reload();
        });
        Percent(us ? "Contingency" : "Risk allowances", cost.Risk, (settings, value) => settings.Risk = value);
        Percent(us ? "Escalation" : "Inflation", cost.Inflation, (settings, value) => settings.Inflation = value);
        Percent(us ? "Sales tax" : "VAT", cost.Tax, (settings, value) => settings.Tax = value);
        Field("Currency symbol (blank: the standard's)", cost.Currency, text =>
        {
            _host.Record(CoordinationEditCommand.Make(Document, "Currency", () => Document.CostSettings.Currency = text));
            Reload();
        });
        _settings.Children.Add(_summary);
    }

    private void Export()
    {
        var dialog = new SaveFileDialog { Title = "Export Cost Plan", Filter = "Excel workbook (*.xlsx)|*.xlsx", FileName = $"{Document.ProjectInformation.Name} cost plan.xlsx" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            using var file = File.Create(dialog.FileName);
            CostReports.WriteXlsx(file, Document);
            _problem.Text = $"Saved {Path.GetFileName(dialog.FileName)}.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { _problem.Text = exception.Message; }
    }
}
