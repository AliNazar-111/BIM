using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Energy;

namespace BIMDesigner.UI;

/// <summary>
/// The home's daylight by BS EN 17037: each habitable room's daylight factors over its reference plane - the
/// one half of it reaches and the one 95% of it does, the level of the standard they reach and, in England, the
/// UK annex's target for the room - and the hours of sun its windows let in on the day set. Worked out on asking,
/// as the model stands, and drawn over the plan as a map of the daylight factor. Anything picked is selected.
/// </summary>
internal sealed class DaylightWindow : System.Windows.Window
{
    private readonly MainWindow _main;
    private readonly TreeView _tree = new() { BorderThickness = new Thickness(0) };
    private readonly TextBlock _summary = new() { Margin = new Thickness(16, 10, 16, 4), TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _problem = new();
    private readonly ComboBox _date = new() { Width = 120 };
    private readonly TextBox _outside = new() { Width = 70 };
    private readonly ComboBox _quality = new() { Width = 100, ItemsSource = Enum.GetNames<DaylightQuality>() };
    private readonly CheckBox _onPlan = new() { Content = "Show on the plan", IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _work = new() { Content = "Work Out" };
    private CancellationTokenSource? _cancel;
    private bool _filling;

    /// <summary>The days sunlight may be counted on: BS EN 17037's from 1 February to 21 March.</summary>
    private static readonly (string Name, int Month, int Day)[] Days = { ("1 February", 2, 1), ("15 February", 2, 15), ("1 March", 3, 1), ("21 March", 3, 21) };

    private static readonly Brush Unmet = new SolidColorBrush(Color.FromRgb(0xE0, 0x5A, 0x4A));
    private static readonly Brush Met = new SolidColorBrush(Color.FromRgb(0x3E, 0xA8, 0x5C));

    /// <summary>The result last worked out, as the plan draws it.</summary>
    public DaylightResult? Result { get; private set; }

    public DaylightWindow(MainWindow main)
    {
        _main = main;
        DialogLook.Apply(this, "Daylight", 860, 720);
        MinWidth = 560;
        MinHeight = 420;

        ScrollViewer.SetHorizontalScrollBarVisibility(_tree, ScrollBarVisibility.Disabled);
        _tree.SetResourceReference(BackgroundProperty, "Bg.Window");
        _tree.SetResourceReference(ForegroundProperty, "Text");
        _tree.SelectedItemChanged += (_, _) =>
        {
            if ((_tree.SelectedItem as TreeViewItem)?.Tag is Core.Elements.Element element) _main.SelectFromBrowser(element);
        };

        _date.ItemsSource = Days.Select(day => day.Name).ToList();
        _date.SelectionChanged += (_, _) =>
        {
            if (_date.SelectedIndex >= 0) Change(settings => settings.SunlightDate = new DateOnly(settings.SunlightDate.Year, Days[_date.SelectedIndex].Month, Days[_date.SelectedIndex].Day));
        };
        _quality.SelectionChanged += (_, _) =>
        {
            if (Enum.TryParse<DaylightQuality>(_quality.SelectedItem as string, out var quality)) Change(settings => settings.Quality = quality);
        };
        void Outside()
        {
            var text = _outside.Text.Trim().Replace(",", string.Empty);
            if (text.Length == 0) Change(settings => settings.OutsideLight = 0);
            else if (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var lux) && lux is >= 1000 and <= 100_000) Change(settings => settings.OutsideLight = lux);
            else Fill();
        }

        _outside.LostFocus += (_, _) => Outside();
        _outside.KeyDown += (_, args) =>
        {
            if (args.Key != System.Windows.Input.Key.Enter) return;
            Outside();
            args.Handled = true;
        };
        _onPlan.Click += (_, _) => _main.ShowDaylight(_onPlan.IsChecked == true ? Result : null);
        _work.Click += async (_, _) => await WorkOut();

        var options = new WrapPanel { Margin = new Thickness(16, 2, 16, 6) };
        void Option(string label, FrameworkElement control, string tip)
        {
            var pair = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 18, 4), ToolTip = tip };
            pair.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0), Opacity = 0.85 });
            pair.Children.Add(control);
            options.Children.Add(pair);
        }

        Option("Sunlight on", _date, "The day the hours of sun are counted on - any from 1 February to 21 March, BS EN 17037 says; BRE 209 takes 21 March");
        Option("Light outside (lux)", _outside, "The median diffuse illuminance outside over the daylight hours, which the daylight factor targets are set against - London's 14,100 lux (BS EN 17037 Table A.3) where left empty");
        Option("Quality", _quality, "Draft: a grid of 600 mm, 128 rays a point. Standard: 400 mm, 512. Fine: 300 mm, 1024 - slower, less noisy");
        options.Children.Add(_onPlan);

        var close = new Button { Content = "Close" };
        close.Click += (_, _) => Close();

        var lists = new Grid { Margin = new Thickness(16, 0, 16, 8) };
        lists.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        lists.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        lists.Children.Add(new TextBlock { Text = "EACH HABITABLE ROOM  -  PICK ONE TO SELECT IT", FontWeight = FontWeights.SemiBold, Opacity = 0.8, Margin = new Thickness(0, 6, 0, 4) });
        var border = new Border { Child = _tree, BorderThickness = new Thickness(1) }.Lined();
        Grid.SetRow(border, 1);
        lists.Children.Add(border);

        var intro = DialogLook.Intro("Each habitable room's daylight factors 0.85 m over its floor, 0.3 m in from its walls, worked out under the CIE overcast sky through the model - set against BS EN 17037's levels and, in England, the UK annex's targets for a home: 100 lux in a bedroom, 150 in a living room, 200 in a kitchen over half the room. And the hours of sun each room's windows let in.");
        var root = new DockPanel();
        DockPanel.SetDock(intro, Dock.Top);
        DockPanel.SetDock(options, Dock.Top);
        DockPanel.SetDock(_summary, Dock.Top);
        root.Children.Add(DialogLook.Footer(_problem, _work, close));
        root.Children.Add(intro);
        root.Children.Add(options);
        root.Children.Add(_summary);
        root.Children.Add(lists);
        Content = root;

        _main.History.Changed += OnHistoryChanged;
        Closed += (_, _) =>
        {
            _main.History.Changed -= OnHistoryChanged;
            _cancel?.Cancel();
            _main.ShowDaylight(null);
        };
        Fill();
        _summary.Text = "Work Out to see each room's daylight and sunlight as the model stands.";
    }

    private void OnHistoryChanged(object? sender, EventArgs e)
    {
        Fill();
        if (Result is not null) DialogLook.Say(_problem, "The model or the settings have changed since: Work Out again to see them.");
    }

    /// <summary>The settings as they are set.</summary>
    private void Fill()
    {
        var document = _main.Document;
        var settings = document.ProjectInformation.Daylight;
        _filling = true;
        _date.SelectedIndex = Array.FindIndex(Days, day => day.Month == settings.SunlightDate.Month && day.Day == settings.SunlightDate.Day);
        _outside.Text = settings.OutsideLight > 0 ? settings.OutsideLight.ToString("#,0", CultureInfo.CurrentCulture) : string.Empty;
        _quality.SelectedItem = settings.Quality.ToString();
        _filling = false;
    }

    /// <summary>The project's daylight settings changed as one step to undo - nothing done where nothing changes.</summary>
    private void Change(Action<DaylightSettings> edit)
    {
        if (_filling) return;
        var settings = _main.Document.ProjectInformation.Daylight;
        var after = settings.Copy();
        edit(after);
        if (after.SameAs(settings)) return;
        _main.History.Execute(new Core.Energy.SetDaylightSettingsCommand(settings, after));
    }

    /// <summary>Works the daylight out as the model stands, off the window's thread, saying how far along it is.</summary>
    public async Task WorkOut()
    {
        if (SynchronizationContext.Current is not System.Windows.Threading.DispatcherSynchronizationContext)
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(Dispatcher));

        _cancel?.Cancel();
        var cancel = _cancel = new CancellationTokenSource();
        var document = _main.Document;
        var settings = document.ProjectInformation.Daylight.Copy();
        var progress = new Progress<double>(share => DialogLook.Say(_problem, $"Tracing the sky through the model... {share:0%}"));
        _work.IsEnabled = false;
        DialogLook.Say(_problem, "Gathering the model...");
        try
        {
            var result = await Task.Run(() => Daylight.Assess(document, settings, null, progress, cancel.Token), cancel.Token);
            if (cancel.IsCancellationRequested) return;
            Result = result;
            Show(result);
            _main.ShowDaylight(_onPlan.IsChecked == true ? result : null);
            DialogLook.Say(_problem, string.Empty);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            DialogLook.Warn(_problem, $"The daylight could not be worked out: {error.Message}");
        }
        finally
        {
            _work.IsEnabled = true;
        }
    }

    private static string Said(DaylightLevel level) => level == DaylightLevel.Below ? "below the minimum" : $"its {level.ToString().ToLowerInvariant()} level";

    private void Show(DaylightResult result)
    {
        _tree.Items.Clear();
        var us = _main.Document.ProjectInformation.ClearanceStandard.IsUs();
        if (result.Rooms.Count == 0)
        {
            _summary.Text = "No habitable room enclosed by walls to work out: a living room, kitchen, dining room, study or bedroom.";
            return;
        }

        var outside = result.OutsideLight;
        string Factor(double lux) => $"{lux / outside * 100:0.0}%";
        var meeting = result.Rooms.Count(room => room.MeetsAnnex == true);
        var reaching = result.Rooms.Count(room => room.Level != DaylightLevel.Below);
        var sunniest = result.Rooms.OrderByDescending(room => room.SunHours).First();
        _summary.Text =
            $"{result.Rooms.Count} habitable room{(result.Rooms.Count == 1 ? string.Empty : "s")}: " +
            (us ? string.Empty : $"{meeting} meet{(meeting == 1 ? "s" : string.Empty)} the UK annex's target; ") +
            $"{reaching} reach{(reaching == 1 ? "es" : string.Empty)} BS EN 17037's minimum ({Factor(300)} over half the room, {Factor(100)} over 95%). " +
            $"Light outside {outside:#,0} lux: {result.OutsideFrom}.\n" +
            $"Sunlight on {result.Date.ToString("d MMMM", CultureInfo.CurrentCulture)}, counted above {result.LeastAltitude:0}° at latitude {result.Latitude:0.0}°: " +
            (result.Sunlit
                ? $"the {sunniest.Room.Name} has {sunniest.SunHours:0.0} hours - a home is asked one habitable room with 1.5 at least, the main living room preferably."
                : $"no habitable room has the 1.5 hours a home is asked one to have, the main living room preferably - the most is {sunniest.SunHours:0.0} in the {sunniest.Room.Name}.");
        _summary.Foreground = result.Sunlit && (us || meeting == result.Rooms.Count) ? (Brush)FindResource("Text") : Unmet;

        foreach (var room in result.Rooms)
        {
            var annex = !us && room.Target is { } lux
                ? $"; the UK annex's {lux:0} lux ({Factor(lux)}) over half {(room.MeetsAnnex == true ? "met" : "not met")}"
                : string.Empty;
            var item = new TreeViewItem
            {
                Header = new TextBlock { Text = $"{room.Room.Name}  -  {room.Median:0.00}% over half, {room.Least:0.00}% over 95%: {Said(room.Level)}{annex}; {room.SunHours:0.0} h of sun, {Said(room.SunLevel)}", TextWrapping = TextWrapping.Wrap, Foreground = (us ? room.Level != DaylightLevel.Below : room.MeetsAnnex == true) ? Met : Unmet },
                Tag = room.Room
            };
            item.Items.Add(new TreeViewItem { Header = $"{room.Kind} - {room.Points.Count} points, from {room.Points.Min(point => point.Factor):0.00}% to {room.Points.Max(point => point.Factor):0.00}%", Tag = room.Room });
            foreach (var level in new[] { DaylightLevel.Minimum, DaylightLevel.Medium, DaylightLevel.High })
            {
                var (half, most) = Daylight.Targets(level);
                item.Items.Add(new TreeViewItem { Header = $"{level}: {Factor(half)} over half ({half:0} lux), {Factor(most)} over 95% ({most:0} lux); {Daylight.SunHours(level):0.#} h of sun", Tag = room.Room });
            }

            item.Items.Add(new TreeViewItem
            {
                Header = room.Windows == 0 ? "No window to the outside in its walls" : $"{room.Windows} window{(room.Windows == 1 ? string.Empty : "s")} counted for its sun, at their middles inside the glass",
                Tag = room.Room
            });
            _tree.Items.Add(item);
        }
    }
}
