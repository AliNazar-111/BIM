using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using BIMDesigner.Core.Documents;

namespace BIMDesigner.UI;

/// <summary>
/// Everything wrong with the model in one list: the model's own warnings - walls drawn over one
/// another, the same thing twice in one place, rooms nothing encloses or two to a space, marks used
/// twice, a door with no wall - then each building regulation and each services rule it falls short
/// of, by kind of element. Ticks show or hide each sort, and a search narrows it; picking one goes
/// to what it is about, selected on its storey. It checks again as the model changes, where that is
/// quick; on a model that takes longer, it says the list is out of date until Check Again.
/// </summary>
internal sealed class WarningsWindow : System.Windows.Window
{
    private readonly MainWindow _main;
    private readonly ListBox _list = new() { BorderThickness = new Thickness(0) };
    private readonly TextBlock _said = new();
    private readonly TextBox _search = new() { MinWidth = 180, Padding = new Thickness(4, 2, 4, 2), VerticalContentAlignment = VerticalAlignment.Center };
    private readonly Dictionary<WarningKind, CheckBox> _kinds = new();
    private readonly DispatcherTimer _again = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private IReadOnlyList<Warning> _warnings = Array.Empty<Warning>();
    private TimeSpan _took;
    private bool _stale;

    /// <summary>While the list is filled: what was picked is picked again, not gone to again.</summary>
    private bool _showing;

    /// <summary>A check that takes longer than this is not run again at every change: the list says it is out of date instead.</summary>
    private static readonly TimeSpan QuickEnough = TimeSpan.FromSeconds(1.5);

    public WarningsWindow(MainWindow main)
    {
        _main = main;
        DialogLook.Apply(this, "Warnings", 760, 560);
        MinWidth = 480;
        MinHeight = 300;
        _list.SetResourceReference(BackgroundProperty, "Bg.Window");
        _list.SetResourceReference(ForegroundProperty, "Text");
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        _list.SelectionChanged += (_, _) =>
        {
            if (!_showing && Picked is { } warning) _main.GoTo(warning.Elements);
        };

        var bar = new WrapPanel { Margin = new Thickness(16, 0, 16, 8) };
        foreach (var kind in Enum.GetValues<WarningKind>())
        {
            var box = new CheckBox { IsChecked = true, Margin = new Thickness(0, 4, 14, 4), VerticalAlignment = VerticalAlignment.Center };
            box.SetResourceReference(ForegroundProperty, "Text");
            box.Checked += (_, _) => Show();
            box.Unchecked += (_, _) => Show();
            _kinds[kind] = box;
            bar.Children.Add(box);
        }

        var find = new TextBlock { Text = "Find", Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        bar.Children.Add(find);
        _search.TextChanged += (_, _) => Show();
        bar.Children.Add(_search);
        DockPanel.SetDock(bar, Dock.Top);

        var again = new Button { Content = "Check Again" };
        again.Click += (_, _) => Check();
        var close = new Button { Content = "Close", IsCancel = true };
        close.Click += (_, _) => Close();

        var root = new DockPanel();
        root.Children.Add(DialogLook.Footer(_said, again, close));
        root.Children.Add(DialogLook.Intro(
            "What is wrong with the model, and what the building falls short of: each the same as its own Properties say. Pick one to go to it on its storey."));
        root.Children.Add(bar);
        root.Children.Add(new Border { Child = _list, BorderThickness = new Thickness(1), Margin = new Thickness(16, 0, 16, 8) }.Lined());
        Content = root;

        _again.Tick += (_, _) =>
        {
            _again.Stop();
            if (IsLoaded) Check();
        };
        _main.History.Changed += OnHistoryChanged;
        Closed += (_, _) =>
        {
            _again.Stop();
            _main.History.Changed -= OnHistoryChanged;
        };
        Check();
    }

    /// <summary>The warnings found, as last checked.</summary>
    public IReadOnlyList<Warning> Warnings => _warnings;

    /// <summary>The warning picked.</summary>
    public Warning? Picked => (_list.SelectedItem as ListBoxItem)?.Tag as Warning;

    private void OnHistoryChanged(object? sender, EventArgs e)
    {
        if (_took <= QuickEnough)
        {
            _again.Stop();
            _again.Start();
            return;
        }

        _stale = true;
        Count();
    }

    /// <summary>The model checked now. Public so the harness can press it.</summary>
    public void Check()
    {
        var clock = Stopwatch.StartNew();
        _warnings = Core.Documents.Warnings.Of(_main.Document);
        _took = clock.Elapsed;
        _stale = false;
        Show();
    }

    /// <summary>The warnings of the sorts ticked that match the search, by sort and heading.</summary>
    private void Show()
    {
        var picked = Picked;
        var find = _search.Text.Trim();
        var shown = _warnings
            .Where(warning => _kinds[warning.Kind].IsChecked == true)
            .Where(warning => find.Length == 0 || warning.Said.Contains(find, StringComparison.CurrentCultureIgnoreCase) || warning.Heading.Contains(find, StringComparison.CurrentCultureIgnoreCase))
            .ToList();

        _showing = true;
        _list.Items.Clear();
        foreach (var sort in shown.GroupBy(warning => warning.Kind).OrderBy(sort => sort.Key))
        foreach (var heading in sort.GroupBy(warning => warning.Heading).OrderBy(heading => heading.Key, StringComparer.CurrentCulture))
        {
            _list.Items.Add(new ListBoxItem
            {
                Content = new TextBlock { Text = $"{Title(sort.Key)}: {heading.Key} ({heading.Count()})", FontWeight = FontWeights.SemiBold },
                IsEnabled = false,
                Margin = new Thickness(0, _list.Items.Count == 0 ? 2 : 10, 0, 2)
            });
            foreach (var warning in heading)
            {
                var item = new ListBoxItem
                {
                    Tag = warning,
                    Content = new TextBlock { Text = warning.Said, TextWrapping = TextWrapping.Wrap },
                    Padding = new Thickness(18, 3, 6, 3)
                };
                _list.Items.Add(item);
                if (ReferenceEquals(warning, picked) || picked is not null && warning.Said == picked.Said) _list.SelectedItem = item;
            }
        }

        if (_list.Items.Count == 0)
            _list.Items.Add(new ListBoxItem
            {
                Content = new TextBlock { Text = _warnings.Count == 0 ? "Nothing wrong: no warnings." : "None of the sorts ticked match.", Opacity = 0.75 },
                IsEnabled = false
            });

        _showing = false;
        Count();
    }

    private static string Title(WarningKind kind) => kind switch
    {
        WarningKind.Model => "Model",
        WarningKind.Regulations => "Regulations",
        _ => "Services"
    };

    private void Count()
    {
        foreach (var (kind, box) in _kinds)
            box.Content = $"{Title(kind)} ({_warnings.Count(warning => warning.Kind == kind)})";

        if (_stale)
        {
            DialogLook.Warn(_said, $"The model has changed since it was checked ({_took.TotalSeconds:0.#} s): Check Again.");
            return;
        }

        DialogLook.Say(_said, _warnings.Count == 0
            ? "No warnings."
            : $"{_warnings.Count} {(_warnings.Count == 1 ? "warning" : "warnings")}, checked in {_took.TotalSeconds:0.0} s.");
    }
}
