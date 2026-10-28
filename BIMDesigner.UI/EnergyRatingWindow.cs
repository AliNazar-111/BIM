using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Energy;
using BIMDesigner.Core.Parameters;

namespace BIMDesigner.UI;

/// <summary>
/// The home's energy rating by SAP 10.3, and whether it meets the Future Homes Standard: its SAP
/// and environmental impact ratings on their A to G scales; its emission, primary energy and fabric
/// energy efficiency rates against the targets its notional twin sets; each other thing Approved
/// Document L 2026 asks; what was taken that the model does not say, with the settings for it; its
/// surfaces and its junctions - each junction's ψ to be given from its calculated detail - and the
/// worksheets of the home and of the notional home. Anything picked is selected in the model.
/// </summary>
internal sealed class EnergyRatingWindow : System.Windows.Window
{
    private readonly MainWindow _main;
    private readonly TreeView _tree = new() { BorderThickness = new Thickness(0) };
    private readonly StackPanel _ratings = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(16, 12, 16, 6) };
    private readonly TextBlock _problem = new();
    private readonly ComboBox _overshading = new() { Width = 170 };
    private readonly ComboBox _sheltered = new() { Width = 60, ItemsSource = new[] { "0", "1", "2", "3", "4" } };
    private readonly ComboBox _bridging = new() { Width = 270 };
    private readonly TextBox _yValue = new() { Width = 56 };
    private readonly TextBox _recovery = new() { Width = 56 };
    private readonly TextBox _battery = new() { Width = 56 };
    private readonly CheckBox _flat = new() { Content = "A flat", VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox _location = new() { Width = 130, ItemsSource = new[] { "By postcode", "Moderate risk", "High risk" } };
    private readonly TextBox _postcode = new() { Width = 90 };
    private readonly CheckBox _shutters = new() { Content = "External shutters", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 18, 0) };
    private readonly CheckBox _noisy = new() { Content = "Noisy at night", VerticalAlignment = VerticalAlignment.Center };
    private bool _filling;

    private static readonly Brush Unmet = new SolidColorBrush(Color.FromRgb(0xE0, 0x5A, 0x4A));
    private static readonly Brush Met = new SolidColorBrush(Color.FromRgb(0x3E, 0xA8, 0x5C));

    // The bands' colours, A to G, as an energy performance certificate draws them.
    private static readonly Color[] CostColours =
    {
        Color.FromRgb(0x00, 0x80, 0x54), Color.FromRgb(0x19, 0xB4, 0x59), Color.FromRgb(0x8D, 0xCE, 0x46), Color.FromRgb(0xFF, 0xD5, 0x00),
        Color.FromRgb(0xFC, 0xAA, 0x65), Color.FromRgb(0xEF, 0x80, 0x23), Color.FromRgb(0xE9, 0x15, 0x3B)
    };

    private static readonly Color[] ImpactColours =
    {
        Color.FromRgb(0x2E, 0x8B, 0xC0), Color.FromRgb(0x4A, 0xA3, 0xD3), Color.FromRgb(0x6D, 0xB7, 0xDD), Color.FromRgb(0x92, 0xC9, 0xE6),
        Color.FromRgb(0xA9, 0xB9, 0xC6), Color.FromRgb(0x8E, 0x98, 0xA3), Color.FromRgb(0x6C, 0x75, 0x7D)
    };

    private static readonly string[] Bridges = { "ψ as the notional home's (Table R2)", "SAP's defaults (Table K1)", "A y-value" };

    public EnergyRatingWindow(MainWindow main)
    {
        _main = main;
        DialogLook.Apply(this, "Energy Rating and Overheating", 900, 880);
        MinWidth = 560;
        MinHeight = 520;

        ScrollViewer.SetHorizontalScrollBarVisibility(_tree, ScrollBarVisibility.Disabled);
        _tree.SetResourceReference(BackgroundProperty, "Bg.Window");
        _tree.SetResourceReference(ForegroundProperty, "Text");
        _tree.SelectedItemChanged += (_, _) =>
        {
            if ((_tree.SelectedItem as TreeViewItem)?.Tag is Element element) _main.SelectFromBrowser(element);
        };

        // What the model does not say, set here.
        _overshading.ItemsSource = EnumText.Choices<SapOvershading>();
        _bridging.ItemsSource = Bridges;
        _overshading.SelectionChanged += (_, _) =>
        {
            if (EnumText.TryParse<SapOvershading>(_overshading.SelectedItem as string, out var shading)) Change(settings => settings.Overshading = shading);
        };
        _sheltered.SelectionChanged += (_, _) => Change(settings => settings.ShelteredSides = Math.Max(0, _sheltered.SelectedIndex));
        _bridging.SelectionChanged += (_, _) => Change(settings => settings.Bridging = (ThermalBridging)Math.Max(0, _bridging.SelectedIndex));
        _yValue.LostFocus += (_, _) => Number(_yValue, 0, 1, value => Change(settings => settings.YValue = value));
        _recovery.LostFocus += (_, _) => Number(_recovery, 0, 100, value => Change(settings => settings.WasteWaterRecovery = value / 100));
        _battery.LostFocus += (_, _) => Number(_battery, 0, 100, value => Change(settings => settings.Battery = value));
        _flat.Click += (_, _) => Change(settings => settings.Flat = _flat.IsChecked == true);
        _location.SelectionChanged += (_, _) => Change(settings => settings.OverheatingLocation = (OverheatingLocation)Math.Max(0, _location.SelectedIndex));
        _postcode.LostFocus += (_, _) => Change(settings => settings.Postcode = _postcode.Text.Trim().ToUpperInvariant());
        _postcode.KeyDown += (_, args) =>
        {
            if (args.Key != System.Windows.Input.Key.Enter) return;
            Change(settings => settings.Postcode = _postcode.Text.Trim().ToUpperInvariant());
            args.Handled = true;
        };
        _shutters.Click += (_, _) => Change(settings => settings.Shutters = _shutters.IsChecked == true);
        _noisy.Click += (_, _) => Change(settings => settings.NoisyAtNight = _noisy.IsChecked == true);

        var options = new WrapPanel { Margin = new Thickness(16, 2, 16, 6) };
        void Option(string label, FrameworkElement control, string tip)
        {
            var pair = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 18, 4), ToolTip = tip };
            pair.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0), Opacity = 0.85 });
            pair.Children.Add(control);
            options.Children.Add(pair);
        }

        Option("Overshading", _overshading, "How much the windows are shaded by what is round the home (Table 6d); 'very little' is taken as average for the regulations");
        Option("Sheltered sides", _sheltered, "Sides sheltered by buildings or trees as tall as the home within ten times its height (box 19)");
        Option("Thermal bridges", _bridging, "How each junction's ψ is taken where its own is not given below");
        Option("y-value", _yValue, "W/m²K over the area facing outside, where the bridges are taken as a y-value");
        Option("Shower heat recovery (%)", _recovery, "Instantaneous waste water heat recovery on the showers: its efficiency; 0 for none");
        Option("Battery (kWh)", _battery, "A battery's usable capacity for the solar electricity; 0 for none");
        options.Children.Add(_flat);

        // What Part O asks that the model does not say.
        var overheating = new WrapPanel { Margin = new Thickness(16, 0, 16, 6) };
        overheating.Children.Add(new TextBlock { Text = "Overheating:", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 12, 4), Opacity = 0.85 });
        void Overheating(string label, FrameworkElement control, string tip)
        {
            var pair = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 18, 4), ToolTip = tip };
            pair.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0), Opacity = 0.85 });
            pair.Children.Add(control);
            overheating.Children.Add(pair);
        }

        Overheating("Location", _location, "Approved Document O's risk category (1.3): by the postcode - the high risk part of London in Table C1 - or as set");
        Overheating("Postcode", _postcode, "The site's postcode, or its district (SW1A); left empty, the one in the project's address");
        _shutters.ToolTip = "External shutters with means of ventilation on the glazing, lockable: shading it (1.9) and keeping it secure open at night (3.7)";
        _noisy.ToolTip = "Noise at night will keep the windows shut - as the planning authority found: only dynamic thermal modelling (TM59) can then show compliance (3.1 to 3.3)";
        overheating.Children.Add(_shutters);
        overheating.Children.Add(_noisy);

        var close = new Button { Content = "Close" };
        close.Click += (_, _) => Close();

        var lists = new Grid { Margin = new Thickness(16, 0, 16, 8) };
        lists.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        lists.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        lists.Children.Add(new TextBlock { Text = "THE CHECKS, THE HOME AS SAP TAKES IT AND ITS WORKSHEETS  -  PICK A SURFACE TO SELECT IT", FontWeight = FontWeights.SemiBold, Opacity = 0.8, Margin = new Thickness(0, 6, 0, 4) });
        var border = new Border { Child = _tree, BorderThickness = new Thickness(1) }.Lined();
        Grid.SetRow(border, 1);
        lists.Children.Add(border);

        var root = new DockPanel();
        DockPanel.SetDock(_ratings, Dock.Top);
        DockPanel.SetDock(options, Dock.Top);
        DockPanel.SetDock(overheating, Dock.Top);
        root.Children.Add(DialogLook.Footer(_problem, close));
        root.Children.Add(_ratings);
        root.Children.Add(options);
        root.Children.Add(overheating);
        root.Children.Add(lists);
        Content = root;

        _main.History.Changed += OnHistoryChanged;
        Closed += (_, _) => _main.History.Changed -= OnHistoryChanged;
        Refresh();
    }

    private void OnHistoryChanged(object? sender, EventArgs e) => Refresh();

    /// <summary>A number typed into a box, if it is one and within reason; the box put back if not.</summary>
    private void Number(TextBox box, double least, double most, Action<double> use)
    {
        if (_filling) return;
        if (double.TryParse(box.Text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out var value) && value >= least && value <= most) use(value);
        else Refresh();
    }

    /// <summary>The project's energy settings changed as one step to undo - nothing done where nothing changes.</summary>
    private void Change(Action<EnergySettings> edit)
    {
        if (_filling) return;
        var settings = _main.Document.ProjectInformation.Energy;
        var after = settings.Copy();
        edit(after);
        if (after.SameAs(settings)) return;
        _main.History.Execute(new SetEnergySettingsCommand(settings, after));
    }

    /// <summary>Works the rating and the check out again, as the model now stands.</summary>
    public void Refresh()
    {
        var document = _main.Document;
        var settings = document.ProjectInformation.Energy;
        _filling = true;
        _overshading.SelectedItem = EnumText.Humanise(settings.Overshading);
        _sheltered.SelectedIndex = Math.Clamp(settings.ShelteredSides, 0, 4);
        _bridging.SelectedIndex = (int)settings.Bridging;
        _yValue.Text = settings.YValue.ToString("0.00", CultureInfo.CurrentCulture);
        _yValue.IsEnabled = settings.Bridging == ThermalBridging.YValue;
        _recovery.Text = (settings.WasteWaterRecovery * 100).ToString("0", CultureInfo.CurrentCulture);
        _battery.Text = settings.Battery.ToString("0.#", CultureInfo.CurrentCulture);
        _flat.IsChecked = settings.Flat;
        _location.SelectedIndex = (int)settings.OverheatingLocation;
        _postcode.Text = settings.Postcode;
        _shutters.IsChecked = settings.Shutters;
        _noisy.IsChecked = settings.NoisyAtNight;
        _filling = false;

        _tree.Items.Clear();
        _ratings.Children.Clear();
        _problem.Text = string.Empty;
        if (document.ProjectInformation.ClearanceStandard.IsUs())
        {
            _ratings.Children.Add(Wrapped("SAP and the Future Homes Standard rate new homes in England; this is a US project. Its heating and cooling loads are in Heat Loss."));
            return;
        }

        var assessment = FutureHomesStandard.Of(document);
        if (assessment is null)
        {
            _ratings.Children.Add(Wrapped(SapSurvey.Of(document).Notes.FirstOrDefault() ?? "Nothing to rate yet."));
            return;
        }

        // The ratings, as an energy performance certificate shows them, and the verdict beside them.
        var rating = assessment.Rating;
        _ratings.Children.Add(Chart("Energy efficiency (SAP)", rating.SapRating, CostColours));
        _ratings.Children.Add(Chart("Environmental impact", rating.EnvironmentalImpact, ImpactColours));
        var verdict = new StackPanel { Margin = new Thickness(18, 0, 0, 0), MaxWidth = 380 };
        var unmet = assessment.Checks.Count(check => !check.Met);
        verdict.Children.Add(new TextBlock
        {
            Text = assessment.Complies ? "Meets the Future Homes Standard" : $"Does not meet the Future Homes Standard - {unmet} of {assessment.Checks.Count} checks not met",
            FontWeight = FontWeights.SemiBold, FontSize = 15, TextWrapping = TextWrapping.Wrap, Foreground = assessment.Complies ? Met : Unmet, Margin = new Thickness(0, 0, 0, 8)
        });
        foreach (var check in assessment.Checks.Take(3))
            verdict.Children.Add(new TextBlock { Text = $"{(check.Met ? "✓" : "✗")} {check.Requirement}: {check.Detail}", TextWrapping = TextWrapping.Wrap, Foreground = check.Met ? Met : Unmet, Margin = new Thickness(0, 0, 0, 4) });
        var overheating = Overheating.Of(document);
        if (overheating is not null)
        {
            var hot = overheating.Checks.Count(check => !check.Met);
            verdict.Children.Add(new TextBlock
            {
                Text = overheating.Complies ? "Meets Part O by the simplified method" : $"Does not meet Part O by the simplified method - {hot} of {overheating.Checks.Count} checks not met",
                FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Foreground = overheating.Complies ? Met : Unmet, Margin = new Thickness(0, 4, 0, 4)
            });
        }

        verdict.Children.Add(Wrapped($"£{rating.Cost:#,0} a year at SAP's prices; {rating.SpaceHeating / rating.FloorArea:0} kWh/m² of space heating. " +
                                     "SAP 10.3, UK average climate; Approved Document L 2026, in force from 24 March 2027; Approved Document O 2021.", 0.75));
        _ratings.Children.Add(verdict);

        // What Approved Document L asks, each met or not.
        var checks = Item($"What Approved Document L 2026 asks  -  {assessment.Checks.Count - unmet} of {assessment.Checks.Count} met", null, true);
        foreach (var check in assessment.Checks)
        {
            var item = Item($"{(check.Met ? "✓" : "✗")} {check.Requirement}: {check.Detail}", check.Element);
            ((TextBlock)item.Header).Foreground = check.Met ? Met : Unmet;
            checks.Items.Add(item);
        }

        _tree.Items.Add(checks);

        // Part O: its checks, and each room and opening as the simplified method counts them.
        if (overheating is not null)
        {
            var part = Item($"Overheating - Approved Document O, simplified method  -  {overheating.Checks.Count(check => check.Met)} of {overheating.Checks.Count} met", null, true);
            part.Items.Add(Item($"The home is {overheating.Location}; {overheating.CrossVentilation}. Its floor {overheating.FloorArea:0.0} m², its glazing {overheating.Glazing:0.00} m², most of it facing {overheating.Facade}; " +
                                $"it opens to {overheating.TotalFree:0.00} m² of equivalent area against {overheating.TotalFreeRequired:0.00}.", null));
            foreach (var check in overheating.Checks)
            {
                var item = Item($"{(check.Met ? "✓" : "✗")} {check.Requirement}: {check.Detail}", check.Element);
                ((TextBlock)item.Header).Foreground = check.Met ? Met : Unmet;
                part.Items.Add(item);
            }

            var rooms = Item("Rooms - their glazing as a share of their floor, and a bedroom's opening at night", null);
            foreach (var room in overheating.Rooms.OrderByDescending(room => room.Share))
                rooms.Items.Add(Item($"{room.Name}: {room.Glazing:0.00} m² of glass, {room.Share * 100:0.#}% of {room.FloorArea:0.0} m², most facing {room.Facade}" +
                                     (room.Bedroom ? $"; opens {room.NightFree:0.00} m² at night" : string.Empty), room.Room));
            part.Items.Add(rooms);
            var openings = Item("Openings - their glass, and the equivalent area they open to (Appendix D)", null);
            foreach (var opening in overheating.Openings)
                openings.Items.Add(Item($"{opening.Name} in {opening.Room.Name}: {opening.Glazing:0.00} m² of glass, facing {(opening.Bearing is { } way ? Compass(way) : "up")}; " +
                                        $"opens {opening.Equivalent:0.00} m² by day, {opening.NightEquivalent:0.00} at night - {opening.Note}", opening.Element));
            part.Items.Add(openings);
            _tree.Items.Add(part);
        }

        // What was taken that the model does not say.
        var model = assessment.Model;
        if (model.Notes.Count > 0)
        {
            var notes = Item($"What was taken that the model does not say  -  {model.Notes.Count}", null, true);
            foreach (var note in model.Notes) notes.Items.Add(Item(note, null));
            _tree.Items.Add(notes);
        }

        var home = model.Home;
        _tree.Items.Add(Item($"The home: {home.FloorArea:0.0} m² over {home.Storeys} storey{(home.Storeys == 1 ? string.Empty : "s")}, {home.Volume:0} m³, living area {home.LivingArea:0.0} m², " +
                             $"{home.AirPermeability:0.#} m³/h·m² at 50 Pa, thermal mass {home.ThermalMass:0} kJ/m²K; {home.Heating.Name}, {home.Heating.Controls.ToLowerInvariant()}", null));

        // Its surfaces, kind by kind.
        var surfaces = Item($"Surfaces  -  ΣAU {home.Elements.Sum(element => element.Area * element.U):0.0} W/K", null);
        foreach (var kind in home.Elements.GroupBy(element => element.Kind))
        {
            var group = Item($"{EnumText.Humanise(kind.Key)}: {kind.Sum(element => element.Area):0.00} m²", null);
            foreach (var element in kind)
                group.Items.Add(Item($"{element.Name}: {element.Area:0.00} m² at U {element.U:0.00}" +
                                     (element.Glazed ? $", facing {Compass(element.Orientation)}{(element.Kind == SapSurface.Window ? string.Empty : $" at {element.Tilt:0}°")}, g {element.SolarTransmittance:0.00}, frame factor {element.FrameFactor:0.0}" : string.Empty),
                    element.Source));
            surfaces.Items.Add(group);
        }

        _tree.Items.Add(surfaces);

        // Its junctions, each with its ψ to give from its calculated detail.
        var bridgingTotal = home.Junctions.Count > 0 ? home.Junctions.Sum(junction => junction.Length * junction.Psi) : home.YValue * home.Elements.Where(element => element.Kind != SapSurface.PartyWall).Sum(element => element.Area);
        var bridges = Item($"Thermal bridges  -  {bridgingTotal:0.0} W/K" + (home.Junctions.Count == 0 ? $", a y-value of {home.YValue:0.00}" : "  -  type a junction's own ψ, or clear it for the setting's"), null);
        foreach (var junction in home.Junctions)
            bridges.Items.Add(JunctionRow(junction, settings));
        _tree.Items.Add(bridges);

        // The solar arrays, against requirement L3.
        if (home.Photovoltaics.Count > 0)
        {
            var arrays = Item($"Solar arrays  -  {home.Photovoltaics.Sum(array => array.PeakPower):0.00} kWp, {rating.PhotovoltaicGenerated:#,0} kWh a year", null);
            foreach (var array in home.Photovoltaics)
                arrays.Items.Add(Item($"{array.Name}: {array.PeakPower:0.00} kWp facing {Compass(array.Orientation)} at {array.Tilt:0}°, overshading factor {array.Shading:0.00}", array.Source));
            _tree.Items.Add(arrays);
        }

        _tree.Items.Add(Worksheet("Worksheet - the home, for its emission and primary energy rates", assessment.Dwelling));
        _tree.Items.Add(Worksheet("Worksheet - the notional home, for the targets", assessment.Target));
        _tree.Items.Add(Worksheet("Worksheet - the home's ratings (typical gains)", assessment.Rating));
        _tree.Items.Add(Worksheet("Fabric energy efficiency - the home", assessment.DwellingFabric));
        _tree.Items.Add(Worksheet("Fabric energy efficiency - the notional home", assessment.TargetFabric));
    }

    /// <summary>A junction's line: what it is, how long, its ψ - typed to give its own - and what it loses.</summary>
    private TreeViewItem JunctionRow(SapJunction junction, EnergySettings settings)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock { Text = $"{junction.Code} {junction.What}: {junction.Length:0.00} m at ψ ", VerticalAlignment = VerticalAlignment.Center });
        var psi = new TextBox { Text = junction.Psi.ToString("0.000", CultureInfo.CurrentCulture), Width = 56, ToolTip = "Its ψ, W/m·K, from its calculated detail; clear it for the setting's" };
        if (settings.Psi.ContainsKey(junction.Code)) psi.FontWeight = FontWeights.SemiBold;
        psi.LostFocus += (_, _) =>
        {
            var text = psi.Text.Trim();
            if (text.Length == 0) Change(each => each.Psi.Remove(junction.Code));
            else if (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) && value is > -1 and < 5)
            {
                if (Math.Abs(value - junction.Psi) > 1e-9) Change(each => each.Psi[junction.Code] = value);
            }
            else Refresh();
        };
        row.Children.Add(psi);
        row.Children.Add(new TextBlock { Text = $" W/m·K  =  {junction.Length * junction.Psi:0.00} W/K", VerticalAlignment = VerticalAlignment.Center });
        return new TreeViewItem { Header = row };
    }

    /// <summary>A worksheet, box by box.</summary>
    private static TreeViewItem Worksheet(string title, SapResult result)
    {
        var item = Item(title, null);
        foreach (var line in result.Lines)
            item.Items.Add(Item($"({line.Box})  {line.What}: {line.Value.ToString(Math.Abs(line.Value) >= 100 ? "#,0" : "0.###", CultureInfo.CurrentCulture)} {line.Unit}".TrimEnd(), null));
        return item;
    }

    /// <summary>A rating on its scale, A to G: each band a bar longer than the one above, the rating marked against its own.</summary>
    private static FrameworkElement Chart(string title, int rating, Color[] colours)
    {
        var bands = new[] { ('A', "92+"), ('B', "81-91"), ('C', "69-80"), ('D', "55-68"), ('E', "39-54"), ('F', "21-38"), ('G', "1-20") };
        var panel = new StackPanel { Margin = new Thickness(0, 0, 14, 0), Width = 196 };
        panel.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        var band = SapCalculation.Band(rating);
        for (var i = 0; i < bands.Length; i++)
        {
            var (letter, range) = bands[i];
            var row = new Grid { Height = 19, Margin = new Thickness(0, 0, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
            var light = i is 2 or 3 or 4;
            var bar = new Border
            {
                Width = 70 + i * 13, HorizontalAlignment = HorizontalAlignment.Left, Background = new SolidColorBrush(colours[i]),
                Child = new TextBlock
                {
                    Text = $"{range}  {letter}", FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(5, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Right, Foreground = light ? Brushes.Black : Brushes.White
                }
            };
            row.Children.Add(bar);
            if (letter == band)
            {
                var mark = new Border
                {
                    Background = new SolidColorBrush(colours[i]),
                    Child = new TextBlock { Text = $"{rating} {letter}", FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = light ? Brushes.Black : Brushes.White }
                };
                Grid.SetColumn(mark, 1);
                row.Children.Add(mark);
            }

            panel.Children.Add(row);
        }

        return panel;
    }

    /// <summary>The nearest of the sixteen compass points to a bearing from true north.</summary>
    private static string Compass(double bearing)
    {
        var points = new[] { "N", "NNE", "NE", "ENE", "E", "ESE", "SE", "SSE", "S", "SSW", "SW", "WSW", "W", "WNW", "NW", "NNW" };
        return points[(int)Math.Round(((bearing % 360) + 360) % 360 / 22.5) % 16];
    }

    private static TreeViewItem Item(string text, Element? element, bool expanded = false) =>
        new() { Header = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, Tag = element, IsExpanded = expanded };

    private static TextBlock Wrapped(string text, double opacity = 1) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = opacity, Margin = new Thickness(0, 0, 0, 4) };
}
