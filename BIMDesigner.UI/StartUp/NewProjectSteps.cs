using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Mep;

namespace BIMDesigner.UI.StartUp;

/// <summary>
/// A new project, step by step: the template it is drawn and checked to; what it is called and
/// whose it is; where it is and what it stands on; its storeys; and where it is kept. Each step
/// is checked before the next; what has been chosen so far is shown beside them; Create Project
/// makes it. Everything can be changed later in the project's Properties.
/// </summary>
public sealed class NewProjectSteps : Grid
{
    public static readonly string[] Steps = { "Template", "Project", "Site", "Storeys", "Save" };

    private static readonly string[] BuildingTypes =
        { "Detached house", "Semi-detached house", "Terraced house", "Bungalow", "Flat", "Extension", "Other" };

    private ProjectTemplate _template = ProjectTemplate.EnglandHouse;
    private readonly List<RadioButton> _templates = new();

    private readonly TextBox _name = Box("Untitled Project");
    private readonly TextBox _number = Box(string.Empty);
    private readonly TextBox _client = Box(string.Empty);
    private readonly TextBox _address = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 52, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly ComboBox _buildingType = new() { ItemsSource = BuildingTypes, SelectedIndex = 0 };

    private readonly ComboBox _station = new();
    private readonly TextBox _altitude = Box("0");
    private readonly ComboBox _ground = new();
    private readonly TextBox _winter = Box(string.Empty);
    private readonly TextBox _snow = Box("20");
    private readonly TextBox _frost = Box("12");

    private readonly ComboBox _storeys = new() { ItemsSource = Enumerable.Range(1, ProjectTemplates.MostStoreys).ToList(), SelectedItem = 2, Width = 120, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBox _height = Box("3000");
    private TextBlock? _heightLabel;

    // Whether the storey height box is in feet and inches, as it is for the US template.
    private bool _heightInFeet;
    private readonly CheckBox _basement = new() { Content = "A basement under the ground floor" };
    private readonly StackPanel _levels = new() { Margin = new Thickness(0, 6, 0, 0) };

    private readonly TextBox _fileName = Box("Untitled Project");
    private readonly TextBox _folder = Box(DefaultFolder);
    private readonly CheckBox _saveNow = new() { Content = "Save it there now", IsChecked = true, Margin = new Thickness(0, 4, 0, 0) };
    private readonly TextBlock _where = Muted(string.Empty);
    private bool _fileNamed;

    private readonly StackPanel _stepper = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 18, 0, 22) };
    private readonly ContentControl _page = new();
    private readonly StackPanel _summary = new();
    private readonly TextBlock _problem = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0x5A, 0x4A)) };
    private readonly Button _back = new() { Content = "Back", Margin = new Thickness(8, 0, 0, 0), MinWidth = 90 };
    private readonly Button _next;
    private readonly Button _cancel;
    private readonly FrameworkElement[] _pages;

    /// <param name="cancel">What going back out of the steps is called: back to the home screen, or Cancel.</param>
    public NewProjectSteps(string cancel = "Cancel")
    {
        _next = new Button { Content = "Next", Margin = new Thickness(8, 0, 0, 0), MinWidth = 120, IsDefault = true, Style = (Style)Application.Current.FindResource("Start.Primary") };
        _cancel = new Button { Content = cancel, MinWidth = 90 };
        _back.Click += (_, _) => Back();
        _next.Click += (_, _) =>
        {
            if (Step == Steps.Length - 1) Create();
            else Next();
        };
        _cancel.Click += (_, _) => Cancelled?.Invoke(this, EventArgs.Empty);

        foreach (var location in Enum.GetValues<HeatingLocation>())
            _station.Items.Add(new ComboBoxItem { Content = Station(location), Tag = location, IsSelected = location == HeatingLocation.London });
        foreach (var ground in Enum.GetValues<GroundType>())
            _ground.Items.Add(new ComboBoxItem { Content = MainWindow.GroundLabel(ground), Tag = ground, IsSelected = ground == GroundType.FirmClay });

        // The file named as the project is, until it is named otherwise.
        _name.TextChanged += (_, _) =>
        {
            if (!_fileNamed) _fileName.Text = FileNameOf(_name.Text);
            Changed();
        };
        _fileName.TextChanged += (_, _) =>
        {
            if (_fileName.IsKeyboardFocused) _fileNamed = true;
            Changed();
        };
        foreach (var box in new[] { _number, _client, _address, _altitude, _winter, _snow, _frost, _height, _folder }) box.TextChanged += (_, _) => Changed();
        foreach (var picker in new Selector[] { _station, _ground, _storeys }) picker.SelectionChanged += (_, _) => Changed();
        _buildingType.SelectionChanged += (_, _) => Changed();
        foreach (var tick in new[] { _basement, _saveNow })
        {
            tick.Checked += (_, _) => Changed();
            tick.Unchecked += (_, _) => Changed();
        }

        _pages = new FrameworkElement[] { TemplatePage(), ProjectPage(), SitePage(), StoreysPage(), SavePage() };

        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = new StackPanel();
        heading.Children.Add(new TextBlock { Text = "Create a new project", FontSize = 24, FontWeight = FontWeights.SemiBold });
        heading.Children.Add(Muted("Five steps. Everything chosen here can be changed later in the project's Properties."));
        Add(heading, 0);
        Add(_stepper, 1);

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
        var scroll = new ScrollViewer { Content = _page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 24, 0) };
        body.Children.Add(scroll);
        var summary = new Border { CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Padding = new Thickness(18, 16, 18, 16), Child = _summary, VerticalAlignment = VerticalAlignment.Top };
        summary.SetResourceReference(Border.BackgroundProperty, "Bg.Panel");
        summary.SetResourceReference(Border.BorderBrushProperty, "Line");
        SetColumn(summary, 1);
        body.Children.Add(summary);
        Add(body, 2);

        var footer = new DockPanel { Margin = new Thickness(0, 18, 0, 0), LastChildFill = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(_cancel);
        buttons.Children.Add(_back);
        buttons.Children.Add(_next);
        DockPanel.SetDock(buttons, Dock.Right);
        footer.Children.Add(buttons);
        footer.Children.Add(_problem);
        Add(footer, 3);

        Show();
    }

    /// <summary>The project made, ready to open.</summary>
    public event EventHandler<StartChoice>? Created;

    public event EventHandler? Cancelled;

    /// <summary>Which step is shown, from 0.</summary>
    public int Step { get; private set; }

    /// <summary>What is wrong with the step shown, where Next or Create was refused.</summary>
    public string Problem => _problem.Text;

    public ProjectTemplate Template
    {
        get => _template;
        set
        {
            _template = value;
            foreach (var choice in _templates) choice.IsChecked = (ProjectTemplate)choice.Tag == value;
            ShowSite();
            Changed();
        }
    }

    // The boxes, for driving the steps without a mouse.
    public TextBox ProjectName => _name;

    public TextBox ProjectNumber => _number;

    public TextBox Client => _client;

    public TextBox Address => _address;

    public TextBox Altitude => _altitude;

    public TextBox WinterTemperature => _winter;

    public TextBox StoreyHeight => _height;

    public ComboBox Storeys => _storeys;

    public CheckBox Basement => _basement;

    public TextBox FileName => _fileName;

    public TextBox Folder => _folder;

    public CheckBox SaveNow => _saveNow;

    /// <summary>Where projects are kept unless another folder is picked: a folder of their own in Documents.</summary>
    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "BIMDesigner Projects");

    private bool Us => _template == ProjectTemplate.UsHouse;

    /// <summary>On to the next step, if this one is complete; false, with why, where it is not.</summary>
    public bool Next()
    {
        if (Check(Step) is { } why)
        {
            _problem.Text = why;
            return false;
        }

        if (Step < Steps.Length - 1) Step++;
        Show();
        return true;
    }

    public void Back()
    {
        if (Step > 0) Step--;
        Show();
    }

    /// <summary>The project made from the steps and handed on - false, with why, where a step is not complete.</summary>
    public bool Create()
    {
        for (var step = 0; step < Steps.Length; step++)
            if (Check(step) is { } why)
            {
                Step = step;
                Show();
                _problem.Text = why;
                return false;
            }

        var spec = Spec;
        var document = ProjectTemplates.Create(spec);
        var storeys = spec.Storeys == 1 ? "one storey" : $"{spec.Storeys} storeys";
        var said = $"New project {document.ProjectInformation.Name}: {spec.Template.Title()}, {storeys} {(Us ? Core.Units.FeetAndInches(spec.StoreyHeight) : $"{spec.StoreyHeight / 1000:0.0##} m")} floor to floor{(spec.Basement ? " over a basement" : string.Empty)}. "
                   + "Start drawing walls on the Architecture tab.";
        Created?.Invoke(this, new StartChoice(document, null, said) { SaveTo = SavePath });
        return true;
    }

    /// <summary>The answers so far.</summary>
    public NewProjectSpec Spec => new()
    {
        Template = _template, Name = _name.Text.Trim(), Number = _number.Text, Client = _client.Text, Address = _address.Text, BuildingType = _buildingType.SelectedItem as string ?? string.Empty,
        HeatingLocation = (_station.SelectedItem as ComboBoxItem)?.Tag is HeatingLocation location ? location : HeatingLocation.London,
        SiteAltitude = Number(_altitude.Text) ?? 0,
        Ground = (_ground.SelectedItem as ComboBoxItem)?.Tag is GroundType ground ? ground : GroundType.FirmClay,
        WinterDesignTemperature = Number(_winter.Text),
        GroundSnowLoad = Number(_snow.Text) ?? 0,
        FrostDepth = (Number(_frost.Text) ?? 0) * 25.4,
        Storeys = _storeys.SelectedItem is int storeys ? storeys : 2,
        StoreyHeight = Height(Us) ?? 3000,
        Basement = _basement.IsChecked == true
    };

    /// <summary>Where it is to be saved as soon as it is made, or null where it is not.</summary>
    public string? SavePath => _saveNow.IsChecked == true && _folder.Text.Trim().Length > 0 ? Full(Path.Combine(_folder.Text.Trim(), FileNameOf(_fileName.Text) + ".bimx")) : null;

    // ---- checking ----------------------------------------------------------------------

    private string? Check(int step)
    {
        switch (step)
        {
            case 1 when string.IsNullOrWhiteSpace(_name.Text):
                return "Give the project a name.";
            case 2 when Number(_altitude.Text) is not { } altitude || altitude < 0 || altitude > 2000:
                return "The site's altitude is a height in metres, 0 to 2000.";
            case 2 when Us && !string.IsNullOrWhiteSpace(_winter.Text) && Number(_winter.Text) is not (>= -60 and <= 70):
                return "The winter design temperature is in °F, from the jurisdiction's Table R301.2 - or leave it empty for now.";
            case 2 when Us && (Number(_snow.Text) is not { } snow || snow < 0):
                return "The ground snow load is in psf, 0 or more.";
            case 2 when Us && (Number(_frost.Text) is not { } frost || frost < 0):
                return "The frost line depth is in inches, 0 or more.";
            case 3 when Us && (Height(true) is not { } height || height < 7 * Core.Units.MillimetresPerFoot || height > 19 * Core.Units.MillimetresPerFoot):
                return "The storey height is floor to floor in feet and inches - 10'-0\", 10' 6\" or 10 - from 7' to 19'.";
            case 3 when !Us && (Height(false) is not { } height || height < 2000 || height > 6000):
                return "The storey height is floor to floor in millimetres, 2000 to 6000.";
            case 4 when _saveNow.IsChecked == true:
                if (string.IsNullOrWhiteSpace(_folder.Text)) return "Pick a folder to keep the project in.";
                if (string.IsNullOrWhiteSpace(_fileName.Text) || _fileName.Text.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    return "The file name cannot have \\ / : * ? \" < > | in it.";
                try
                {
                    if (File.Exists(SavePath)) return "There is a project of that name in that folder already - choose another name or folder.";
                }
                catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
                {
                    return "That folder is not a place a file can be kept.";
                }

                return null;
            default:
                return null;
        }
    }

    // ---- showing --------------------------------------------------------------------------

    private void Show()
    {
        _problem.Text = string.Empty;
        _page.Content = _pages[Step];
        _back.IsEnabled = Step > 0;
        _next.Content = Step == Steps.Length - 1 ? "Create Project" : "Next";

        _stepper.Children.Clear();
        for (var i = 0; i < Steps.Length; i++)
        {
            if (i > 0)
            {
                var join = new Border { Width = 36, Height = 2, Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
                join.SetResourceReference(Border.BackgroundProperty, i <= Step ? "Accent" : "Line.Strong");
                _stepper.Children.Add(join);
            }

            var done = i < Step;
            var number = new TextBlock
            {
                Text = done ? "" : $"{i + 1}", FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            };
            if (done) number.FontFamily = new FontFamily("Segoe MDL2 Assets");
            var dot = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1.5), Child = number };
            dot.SetResourceReference(Border.BackgroundProperty, i == Step ? "Accent.Dim" : done ? "Bg.Selected" : "Bg.Panel");
            dot.SetResourceReference(Border.BorderBrushProperty, i <= Step ? "Accent" : "Line.Strong");
            var label = new TextBlock { Text = Steps[i], Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, FontWeight = i == Step ? FontWeights.SemiBold : FontWeights.Normal };
            label.SetResourceReference(TextBlock.ForegroundProperty, i <= Step ? "Text" : "Text.Muted");
            var stop = new StackPanel { Orientation = Orientation.Horizontal };
            stop.Children.Add(dot);
            stop.Children.Add(label);
            _stepper.Children.Add(stop);
        }

        Changed();
        Dispatcher.BeginInvoke(() => FirstBox(_pages[Step])?.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>Something changed: the summary, the storeys drawn, where it will be kept.</summary>
    private void Changed()
    {
        if (_pages is null) return;
        if (_problem.Text.Length > 0 && Check(Step) is null) _problem.Text = string.Empty;
        var spec = Spec;

        _summary.Children.Clear();
        _summary.Children.Add(new TextBlock { Text = "Your project", FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) });
        void Line(string what, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            _summary.Children.Add(Muted(what));
            _summary.Children.Add(new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 9) });
        }

        Line("Template", spec.Template.Title());
        Line("Name", spec.Name);
        Line("Number", spec.Number.Trim());
        Line("Client", spec.Client.Trim());
        Line("Address", string.Join(", ", spec.Address.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)));
        Line("Building", spec.BuildingType.Trim());
        Line("Climate", Us
            ? spec.WinterDesignTemperature is { } winter ? $"{winter:0} °F winter design temperature" : "Winter design temperature to be set"
            : $"{Station(spec.HeatingLocation)}, {spec.SiteAltitude:0} m up");
        Line("Ground", MainWindow.GroundLabel(spec.Ground));
        Line("Storeys", $"{spec.Storeys} at {(Us ? Core.Units.FeetAndInches(spec.StoreyHeight) : $"{spec.StoreyHeight:0} mm")}{(spec.Basement ? ", and a basement" : string.Empty)}");
        Line("Kept in", SavePath ?? "Not saved yet");

        // The levels it will have, top down.
        _levels.Children.Clear();
        var height = Math.Clamp(spec.StoreyHeight, 2000, 6000);
        var levels = Enumerable.Range(0, spec.Storeys + 1)
            .Select(i => (Name: i == spec.Storeys ? "Roof" : ProjectTemplates.StoreyName(spec.Template, i), Elevation: i * height))
            .ToList();
        if (spec.Basement) levels.Add((Name: "Basement", Elevation: -height));
        levels.Sort((a, b) => b.Elevation.CompareTo(a.Elevation));
        foreach (var (name, elevation) in levels)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
            var bar = new Border { Height = 3, VerticalAlignment = VerticalAlignment.Center, CornerRadius = new CornerRadius(1.5) };
            bar.SetResourceReference(Border.BackgroundProperty, name == "Roof" ? "Line.Strong" : "Accent");
            var text = new TextBlock { Text = name, FontWeight = FontWeights.SemiBold };
            var at = Muted($"{(elevation >= 0 ? "+" : "−")}{(Us ? Core.Units.FeetAndInches(Math.Abs(elevation)) : $"{Math.Abs(elevation) / 1000:0.000} m")}");
            at.HorizontalAlignment = HorizontalAlignment.Right;
            SetColumn(bar, 1);
            SetColumn(at, 2);
            row.Children.Add(text);
            row.Children.Add(bar);
            row.Children.Add(at);
            _levels.Children.Add(row);
        }

        _where.Text = SavePath is { } path ? $"Saved as {path}" : "Not saved until Save is chosen - File > Save As keeps it wherever you choose.";
    }

    // ---- the steps --------------------------------------------------------------------------

    private FrameworkElement TemplatePage()
    {
        var page = Page("Choose a template", "What the project is drawn and checked to. Its wall, floor and fitting types, its rules and its climate come from it.");
        _templates.Clear();
        foreach (var template in Enum.GetValues<ProjectTemplate>())
        {
            var badge = new Border { Width = 54, Height = 54, CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Top };
            badge.SetResourceReference(Border.BackgroundProperty, "Accent.Dim");
            badge.Child = new TextBlock
            {
                Text = template switch { ProjectTemplate.UsHouse => "US", ProjectTemplate.EnglandWheelchairHouse => "M4(3)", _ => "UK" },
                FontWeight = FontWeights.SemiBold, FontSize = template == ProjectTemplate.EnglandWheelchairHouse ? 13 : 17,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            };
            var words = new StackPanel();
            words.Children.Add(new TextBlock { Text = template.Title(), FontSize = 15, FontWeight = FontWeights.SemiBold });
            words.Children.Add(new TextBlock { Text = template.Description(), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4) });
            words.Children.Add(Muted(template == ProjectTemplate.UsHouse ? "Feet and inches - IRC 2021 - NEC 2023" : "Millimetres - Approved Documents - BS 7671:2018+A4:2026"));
            var content = new DockPanel();
            DockPanel.SetDock(badge, Dock.Left);
            content.Children.Add(badge);
            content.Children.Add(words);

            var choice = new RadioButton { Content = content, Tag = template, GroupName = "Template", IsChecked = template == _template, Style = (Style)Application.Current.FindResource("Start.Choice") };
            choice.Checked += (_, _) =>
            {
                if (_template == template) return;
                _template = template;
                ShowSite();
                Changed();
            };
            choice.MouseDoubleClick += (_, _) => Next();
            _templates.Add(choice);
            page.Children.Add(choice);
        }

        return page;
    }

    private FrameworkElement ProjectPage()
    {
        var page = Page("The project", "What it is called and whose it is - shown on every sheet's title block and in the files sent out.");
        page.Children.Add(Field("Project name", _name));
        page.Children.Add(Pair(Field("Project number", _number), Field("Building type", _buildingType)));
        page.Children.Add(Field("Client", _client));
        page.Children.Add(Field("Site address", _address));
        return page;
    }

    // The site step is built once, its England and US questions shown by the template picked.
    private readonly StackPanel _ukSite = new();
    private readonly StackPanel _usSite = new();
    private TextBlock? _siteIntro;
    private TextBlock? _groundHint;

    private FrameworkElement SitePage()
    {
        var page = Page("The site", string.Empty);
        _siteIntro = (TextBlock)page.Children[1];
        _usSite.Children.Add(Field("Winter design temperature, °F", _winter, "From the jurisdiction's Table R301.2. The heat loss needs it - it can be left empty for now."));
        _usSite.Children.Add(Pair(Field("Ground snow load, psf", _snow), Field("Frost line depth, in", _frost)));
        _ukSite.Children.Add(Field("Nearest weather station", _station, "CIBSE Guide A's design temperature there, 0.6 °C colder for each whole 100 m the site is higher."));
        _ukSite.Children.Add(Field("Site altitude, m above sea level", _altitude));
        page.Children.Add(_ukSite);
        page.Children.Add(_usSite);
        var ground = (StackPanel)Field("Ground", _ground, " ");
        _groundHint = (TextBlock)ground.Children[^1];
        page.Children.Add(ground);
        ShowSite();
        return page;
    }

    /// <summary>The site's questions for the template picked: England's weather station and altitude, or the US jurisdiction's design conditions.</summary>
    private void ShowSite()
    {
        _ukSite.Visibility = Us ? Visibility.Collapsed : Visibility.Visible;
        _usSite.Visibility = Us ? Visibility.Visible : Visibility.Collapsed;
        if (_siteIntro is not null)
            _siteIntro.Text = Us
                ? "The jurisdiction's design conditions from its Table R301.2 - what the heating, the roof and the footings are designed for."
                : "Where the building is: the weather station the heat loss works to, how high the site is, and the ground under it.";
        if (_groundHint is not null)
            _groundHint.Text = Us ? "Its presumptive bearing value sizes the footings (IRC Table R401.4.1)." : "Approved Document A's ground type sizes the strip footings.";
        ShowHeightUnits();
    }

    /// <summary>
    /// The storey height in the template's units: millimetres, or feet and inches for the US -
    /// the 3000 it starts on made 10'-0", and a height typed in the other's carried over.
    /// </summary>
    private void ShowHeightUnits()
    {
        if (_heightLabel is not null) _heightLabel.Text = Us ? "Storey height, floor to floor, ft-in" : "Storey height, floor to floor, mm";
        if (_heightInFeet == Us) return;
        var was = Height(_heightInFeet);
        _heightInFeet = Us;
        if (was is not { } mm) return;
        _height.Text = Us
            ? Math.Abs(mm - 3000) < 0.5 ? "10'-0\"" : Core.Units.FeetAndInches(mm)
            : Math.Abs(mm - 10 * Core.Units.MillimetresPerFoot) < 0.5 ? "3000" : $"{mm:0}";
    }

    /// <summary>The storey height typed, in mm: millimetres; or for the US, feet and inches - 10'-0", 10' 6", or 10 feet bare.</summary>
    private double? Height(bool feet)
    {
        using var units = Core.Units.Use(feet ? Core.UnitSystem.Imperial : Core.UnitSystem.Metric);
        return Core.Units.TryParseLength(_height.Text, out var mm) ? mm : feet ? null : Number(_height.Text);
    }

    private FrameworkElement StoreysPage()
    {
        var page = Page("Storeys", "A level for each storey and one for the roof over them - more can be added, and any moved, in Manage > Levels.");
        var height = (StackPanel)Field("Storey height, floor to floor, mm", _height);
        _heightLabel = (TextBlock)height.Children[0];
        page.Children.Add(Pair(Field("Storeys above the ground", _storeys), height));
        ShowHeightUnits();
        page.Children.Add(_basement);
        var drawn = new Border { CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Padding = new Thickness(16, 12, 16, 10), Margin = new Thickness(0, 16, 0, 0), Child = _levels };
        drawn.SetResourceReference(Border.BackgroundProperty, "Bg.Field");
        drawn.SetResourceReference(Border.BorderBrushProperty, "Line");
        page.Children.Add(drawn);
        return page;
    }

    private FrameworkElement SavePage()
    {
        var page = Page("Where it is kept", "A project is one .bimx file. It is kept every few minutes while it has changes not saved, and offered back if BIMDesigner stops.");
        page.Children.Add(Field("File name", _fileName));
        var browse = new Button { Content = "Browse...", Margin = new Thickness(8, 0, 0, 0) };
        browse.Click += (_, _) =>
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Keep the project in", InitialDirectory = Directory.Exists(_folder.Text) ? _folder.Text : DefaultFolder };
            if (dialog.ShowDialog(System.Windows.Window.GetWindow(this)) == true) _folder.Text = dialog.FolderName;
        };
        var folder = new DockPanel();
        DockPanel.SetDock(browse, Dock.Right);
        folder.Children.Add(browse);
        folder.Children.Add(_folder);
        page.Children.Add(Field("Folder", folder));
        page.Children.Add(_saveNow);
        _where.Margin = new Thickness(0, 10, 0, 0);
        _where.TextWrapping = TextWrapping.Wrap;
        page.Children.Add(_where);
        return page;
    }

    // ---- building blocks ----------------------------------------------------------------------

    private void Add(UIElement element, int row)
    {
        SetRow(element, row);
        Children.Add(element);
    }

    private static StackPanel Page(string title, string intro)
    {
        var page = new StackPanel { MaxWidth = 720, HorizontalAlignment = HorizontalAlignment.Left };
        page.Children.Add(new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeights.SemiBold });
        var words = Muted(intro);
        words.Margin = new Thickness(0, 4, 0, 18);
        page.Children.Add(words);
        return page;
    }

    private static FrameworkElement Field(string label, FrameworkElement input, string? hint = null)
    {
        var field = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        var said = Muted(label);
        said.Margin = new Thickness(0, 0, 0, 4);
        field.Children.Add(said);
        field.Children.Add(input);
        if (hint is not null)
        {
            var under = Muted(hint);
            under.Margin = new Thickness(0, 4, 0, 0);
            under.FontSize = 11;
            field.Children.Add(under);
        }

        return field;
    }

    private static Grid Pair(FrameworkElement left, FrameworkElement right)
    {
        var pair = new Grid();
        pair.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        pair.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        pair.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        SetColumn(right, 2);
        pair.Children.Add(left);
        pair.Children.Add(right);
        return pair;
    }

    private static TextBox Box(string text) => new() { Text = text };

    private static TextBlock Muted(string text)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        block.SetResourceReference(TextBlock.ForegroundProperty, "Text.Muted");
        return block;
    }

    private static Control? FirstBox(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is TextBox or ComboBox && ((Control)child).IsEnabled) return (Control)child;
            if (FirstBox(child) is { } found) return found;
        }

        return root is Panel panel ? panel.Children.OfType<Control>().FirstOrDefault(control => control is TextBox or ComboBox or RadioButton { IsChecked: true }) : null;
    }

    private static string Station(HeatingLocation location)
    {
        var station = Climate.Of(location);
        return $"{location} - {station.Name}, {station.Design:0.0} °C";
    }

    /// <summary>A path as Windows has it whole - or as it is, where it is not one Windows can make whole.</summary>
    private static string Full(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    private static string FileNameOf(string name)
    {
        var cleaned = new string(name.Trim().Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c).ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "Untitled Project" : cleaned;
    }

    private static double? Number(string text) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out var value) || double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            ? value
            : null;
}
