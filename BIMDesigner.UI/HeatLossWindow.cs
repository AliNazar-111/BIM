using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Mep;

namespace BIMDesigner.UI;

/// <summary>
/// The heat loss: what it is worked to, the building's load and its heat sources against it, the
/// home's hot water, each zone's pump duty - and every room, with what it loses through each
/// surface and with the air, and what heats it. A room or a surface picked is selected in the model.
/// </summary>
internal sealed class HeatLossWindow : System.Windows.Window
{
    private readonly MainWindow _main;
    private readonly TreeView _tree = new() { BorderThickness = new Thickness(0) };
    private readonly TextBlock _summary = new() { Margin = new Thickness(16, 12, 16, 8), TextWrapping = TextWrapping.Wrap, Opacity = 0.85 };
    private readonly TextBlock _problem = new();

    // What a US project is shown in, from the watts and metres it is worked in.
    private const double BtuPerHourPerWatt = 3.412142;
    private const double SquareFeetPerSquareMetre = 10.76391;
    private const double CubicFeetPerCubicMetre = 35.31467;
    private const double CubicMetresPerHourPerCfm = 1.699011;
    private const double UsUPerMetricU = 0.1761102;
    private const double GallonsPerMinutePerKilogramPerSecond = 15.85032;
    private const double PascalsPerFootOfHead = 2989.067;

    public HeatLossWindow(MainWindow main)
    {
        _main = main;
        DialogLook.Apply(this, "Heat Loss", 760, 760);
        MinWidth = 480;
        MinHeight = 480;

        ScrollViewer.SetHorizontalScrollBarVisibility(_tree, ScrollBarVisibility.Disabled);
        _tree.SetResourceReference(BackgroundProperty, "Bg.Window");
        _tree.SetResourceReference(ForegroundProperty, "Text");
        _tree.SelectedItemChanged += (_, _) =>
        {
            if ((_tree.SelectedItem as TreeViewItem)?.Tag is Element element) _main.SelectFromBrowser(element);
        };

        var size = new Button { Content = "Size Radiators", ToolTip = "Size each room's radiators to its heat loss at its zone's temperatures" };
        size.Click += (_, _) => _problem.Text = _main.SizeRadiators();
        var close = new Button { Content = "Close" };
        close.Click += (_, _) => Close();

        var lists = new Grid { Margin = new Thickness(16, 0, 16, 8) };
        lists.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        lists.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var label = new TextBlock { Text = "ROOMS  -  PICK ONE TO SELECT IT", FontWeight = FontWeights.SemiBold, Opacity = 0.8, Margin = new Thickness(0, 6, 0, 4) };
        lists.Children.Add(label);
        var border = new Border { Child = _tree, BorderThickness = new Thickness(1) }.Lined();
        Grid.SetRow(border, 1);
        lists.Children.Add(border);

        var root = new DockPanel();
        DockPanel.SetDock(_summary, Dock.Top);
        root.Children.Add(DialogLook.Footer(_problem, size, close));
        root.Children.Add(_summary);
        root.Children.Add(lists);
        Content = root;

        _main.History.Changed += OnHistoryChanged;
        Closed += (_, _) => _main.History.Changed -= OnHistoryChanged;
        Refresh();
    }

    private void OnHistoryChanged(object? sender, EventArgs e) => Refresh();

    /// <summary>Works the heat loss out again, as the model now stands.</summary>
    public void Refresh()
    {
        var document = _main.Document;
        _tree.Items.Clear();
        var building = HeatLoss.Of(document);
        if (building is null)
        {
            _summary.Text = $"{Climate.Describe(document)}. Set it in the plan's Properties, with nothing selected.";
            return;
        }

        // A US project's in its own units - Btu/h, sq ft, cfm and °F - worked the same way underneath.
        var us = document.ProjectInformation.ClearanceStandard.IsUs();
        string Degrees(double celsius) => us ? $"{celsius * 9 / 5 + 32:0} °F" : $"{celsius:0.#} °C";
        string Heat(double watts) => us ? $"{watts * BtuPerHourPerWatt:#,0} Btu/h" : $"{watts:#,0} W";
        string Area(double squareMetres) => us ? $"{squareMetres * SquareFeetPerSquareMetre:#,0.#} sq ft" : $"{squareMetres:0.00} m²";
        var lines = new List<string>
        {
            $"{Climate.Describe(document)}.",
            us
                ? $"Air leakage {building.AirPermeability / CubicMetresPerHourPerCfm / SquareFeetPerSquareMetre:0.00} cfm a sq ft of envelope at 50 Pa. The building loses {Heat(building.Total)}: {Heat(building.Fabric)} through its surfaces and {Heat(building.Ventilation)} with the air - " +
                  "less than its rooms' own losses add up to, each room sized for the wind on its side and the heat it passes to the rooms round it, which the whole building takes once. Worked by BS EN 12831-1 - ACCA Manual J's room-by-room load is not done yet."
                : $"Air permeability {building.AirPermeability:0.#} m³/h·m² at 50 Pa. The building loses {building.Total:#,0} W: {building.Fabric:#,0} through its surfaces and {building.Ventilation:#,0} with the air - " +
                  "less than its rooms' own losses add up to, each room sized for the wind on its side and the heat it passes to the rooms round it, which the whole building takes once (BS EN 12831-1)."
        };
        if (building is { Recovery: { } recovery, SupplyTemperature: { } supplied })
            lines.Add($"Ventilated with heat recovery at {recovery:P0}: its supply comes in at {Degrees(supplied)} at the design conditions.");
        else if (building.Extracted > 0)
            lines.Add($"Ventilated by continuous extract: the {(us ? $"{building.Extracted / 3.6 / Ventilation.LitresPerCfm:0} cfm" : $"{building.Extracted / 3.6:0.#} l/s")} extracted comes in through the envelope from outside, " +
                      "the ordinary leakage less for it (BS EN 12831-1, 6.3.3.3).");

        // Each heat source against what it heats.
        foreach (var source in document.All<MepSystem>().Where(system => system.Kind == SystemKind.Heating)
                     .Select(zone => MepSystems.SourceOf(document, zone)).OfType<Component>().Distinct())
        {
            if (document.FindType<ComponentType>(source.TypeId) is not { } type) continue;
            var (load, rooms) = Heating.LoadOn(document, source, building);
            var said = type.HeatOutput > 0 ? $"gives {Heat(type.HeatOutput)}" : "its output not set";
            lines.Add($"{type.Name}: {said} against a heat load of {Heat(load)} in the {rooms.Count} room{(rooms.Count == 1 ? string.Empty : "s")} it heats.");
        }

        // Each zone's temperatures and the pump's duty along its pipes.
        foreach (var zone in document.All<MepSystem>().Where(system => system.Kind == SystemKind.Heating))
        {
            var network = PipeNetworks.Trace(document, zone);
            var (index, pressure, mass) = HeatingPipes.IndexCircuit(document, network);
            var (flow, back) = Heating.Design(document, zone);
            var temperatures = us ? $"{flow * 9 / 5 + 32:0}/{back * 9 / 5 + 32:0} °F" : $"{flow:0}/{back:0} °C";
            var duty = us ? $"{mass * GallonsPerMinutePerKilogramPerSecond:0.0} gpm, against {pressure / PascalsPerFootOfHead:0.0} ft of head" : $"{mass * 3600:0} kg/h, against {pressure / 1000:0.0} kPa";
            lines.Add(index is null
                ? $"{zone.Name}: {temperatures}."
                : $"{zone.Name}: {temperatures}; its pump moves {duty} along the pipes to {MepSystems.Describe(document, index)} and back - the index circuit - with its emitters, valves and fittings to add.");
        }

        if (!us)
        {
            var (litres, people) = Heating.HotWaterDemand(document);
            lines.Add($"Hot water: {litres:0} litres a day for {people} {(people == 1 ? "person" : "people")} (MIS 3005-D 5.6.2).");
        }

        _summary.Text = string.Join("\n", lines);

        var wanting = MepSystems.Problems(document).Where(problem => problem.Element is Room).Select(problem => problem.Element).ToHashSet();
        foreach (var level in document.Levels.OrderBy(level => level.Elevation))
        {
            var rooms = building.Rooms.Where(room => room.Room.LevelId == level.Id).OrderBy(room => room.Name, StringComparer.OrdinalIgnoreCase).ToList();
            if (rooms.Count == 0) continue;

            var storey = new TreeViewItem { Header = $"{level.Name}  -  its rooms' own losses {Heat(rooms.Where(room => room.Heated).Sum(room => room.Total))}", IsExpanded = true };
            foreach (var room in rooms)
            {
                // Two lines: what it loses, and what heats it - wrapped to the window rather than run off it.
                var header = new StackPanel();
                header.Children.Add(new TextBlock
                {
                    Text = room.Temperature is { } temperature
                        ? $"{room.Name}  {Degrees(temperature)}  -  {Heat(room.Total)}: {Heat(room.Fabric)} through its surfaces, {Heat(room.Ventilation)} with the air"
                        : $"{room.Name}  -  not heated",
                    TextWrapping = TextWrapping.Wrap
                });
                if (room.Heated)
                    header.Children.Add(new TextBlock { Text = $"Heated by: {Heating.Verdict(document, room.Room, room)}", TextWrapping = TextWrapping.Wrap, Opacity = 0.75 });
                var item = new TreeViewItem { Header = header, Tag = room.Room };
                if (wanting.Contains(room.Room)) item.Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0x5A, 0x4A));
                static TextBlock Wrapped(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };
                foreach (var line in room.Lines)
                    item.Items.Add(new TreeViewItem
                    {
                        Header = Wrapped($"{line.What}: {Area(line.Area)} at U {(us ? $"{line.U * UsUPerMetricU:0.000}" : $"{line.U:0.00}")} to {Degrees(line.Beyond)} - {Heat(line.Watts)}"),
                        Tag = line.Through
                    });
                if (room.Heated)
                    item.Items.Add(new TreeViewItem
                    {
                        Header = Wrapped(us
                            ? $"Air: {room.AirFlow / CubicMetresPerHourPerCfm:0.0} cfm into {room.Volume * CubicFeetPerCubicMetre:#,0} cu ft ({(room.Volume > 0 ? room.AirFlow / room.Volume : 0):0.00} an hour), its envelope {Area(room.Envelope)} - {Heat(room.Ventilation)}"
                            : $"Air: {room.AirFlow:0.0} m³/h into {room.Volume:0.0} m³ ({(room.Volume > 0 ? room.AirFlow / room.Volume : 0):0.00} an hour), its envelope {room.Envelope:0.0} m² - {room.Ventilation:#,0} W")
                    });
                foreach (var note in room.Notes)
                    item.Items.Add(new TreeViewItem { Header = Wrapped($"Note: {note}"), Opacity = 0.75 });
                storey.Items.Add(item);
            }

            _tree.Items.Add(storey);
        }
    }
}
