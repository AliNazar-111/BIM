using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BIMDesigner.Core.Annotation;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Parameters;
using BIMDesigner.Core.Sheets;

namespace BIMDesigner.UI;

/// <summary>Dialogs built in code share the main window's look.</summary>
internal static class DialogLook
{
    public static void Apply(System.Windows.Window window, string title, double width, double height)
    {
        window.Title = title;
        window.Width = width;
        window.Height = height;
        window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        window.ShowInTaskbar = false;
        window.SetResourceReference(Control.BackgroundProperty, "Bg.Window");
        window.SetResourceReference(Control.ForegroundProperty, "Text");
        window.SetResourceReference(Control.FontFamilyProperty, "UiFont");
        window.SetResourceReference(Control.FontSizeProperty, "UiFontSize");
        TextOptions.SetTextFormattingMode(window, TextFormattingMode.Display);
        window.UseLayoutRounding = true;
    }

    /// <summary>A strip along the bottom: what is wrong on the left, the buttons on the right.</summary>
    public static Border Footer(TextBlock problem, params Button[] buttons)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        problem.VerticalAlignment = VerticalAlignment.Center;
        problem.TextTrimming = TextTrimming.CharacterEllipsis;
        problem.Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0x5A, 0x4A));
        grid.Children.Add(problem);
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var button in buttons)
        {
            button.Margin = new Thickness(8, 0, 0, 0);
            button.Padding = new Thickness(16, 5, 16, 5);
            right.Children.Add(button);
        }

        Grid.SetColumn(right, 1);
        grid.Children.Add(right);
        var footer = new Border { Padding = new Thickness(16, 10, 16, 14), BorderThickness = new Thickness(0, 1, 0, 0), Child = grid };
        footer.SetResourceReference(Border.BackgroundProperty, "Bg.Panel");
        footer.SetResourceReference(Border.BorderBrushProperty, "Line");
        DockPanel.SetDock(footer, Dock.Bottom);
        return footer;
    }

    public static TextBlock Intro(string text)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16, 14, 16, 10), Opacity = 0.75 };
        DockPanel.SetDock(block, Dock.Top);
        return block;
    }
}

/// <summary>
/// The project's revisions, in order: each numbered by its place among those numbered as it is,
/// with its date, what it was, who it went to and from, whether it is issued, and what of it is
/// drawn. An issued revision is locked: only untick Issued to change it. Changes are made to a
/// copy, and OK puts them into the project as one step to undo.
/// </summary>
public sealed class RevisionsWindow : System.Windows.Window
{
    private readonly BimDocument _document;
    private readonly List<Revision> _working;
    private readonly Grid _table = new() { Margin = new Thickness(16, 0, 16, 12) };
    private readonly TextBlock _problem = new();

    private static readonly string[] Headings = { "", "Number", "Numbering", "Date", "Description", "Issued to", "Issued by", "Issued", "Shows", "" };

    public RevisionsWindow(BimDocument document)
    {
        DialogLook.Apply(this, "Revisions", 1180, 520);
        _document = document;
        _working = document.Revisions.Select(revision => revision.Copy()).ToList();

        var add = new Button { Content = "Add Revision" };
        add.Click += (_, _) => Add();
        var ok = new Button { Content = "OK", IsDefault = true };
        ok.Click += (_, _) => DialogResult = true;
        var cancel = new Button { Content = "Cancel", IsCancel = true };

        var dock = new DockPanel();
        dock.Children.Add(DialogLook.Intro(
            "Each revision is numbered by its place among those numbered the same way: P01, P02 for preliminary issues and C01, C02 for contract issues, as the UK annex to BS EN ISO 19650-2 has it. "
            + "A sheet lists the revisions clouded in its views and those put on it by hand, and shows the latest as its current revision. Tick Issued once it has gone out: it is locked from then on."));
        dock.Children.Add(DialogLook.Footer(_problem, add, ok, cancel));
        dock.Children.Add(new ScrollViewer { Content = _table, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = dock;
        Refresh();
    }

    /// <summary>The revisions as edited: what OK hands back.</summary>
    public IReadOnlyList<Revision> Revisions => _working;

    /// <summary>A new revision after the others, dated today, numbered as the last is.</summary>
    public Revision Add()
    {
        var revision = new Revision { Date = DateTime.Today, Numbering = _working.LastOrDefault()?.Numbering ?? RevisionNumbering.Preliminary };
        _working.Add(revision);
        Refresh();
        return revision;
    }

    /// <summary>A revision moved up or down the list - which renumbers it and those it passes. Null, or why not.</summary>
    public string? Move(Revision revision, int by)
    {
        var index = _working.IndexOf(revision);
        var to = Math.Clamp(index + by, 0, _working.Count - 1);
        if (index < 0 || to == index) return null;

        // What was issued keeps the number it was issued with.
        if (_working.Skip(Math.Min(index, to)).Take(Math.Abs(to - index) + 1).Any(other => other.Issued && other.Numbering == revision.Numbering && !ReferenceEquals(other, revision)) || revision.Issued)
            return Say("An issued revision keeps the number it went out with: it cannot be moved, nor another moved past it.");

        _working.RemoveAt(index);
        _working.Insert(to, revision);
        Refresh();
        return null;
    }

    /// <summary>A revision deleted, unless it was issued or has clouds. Null, or why not.</summary>
    public string? Delete(Revision revision)
    {
        if (revision.Issued) return Say("An issued revision cannot be deleted: it went out.");
        var clouds = _document.All<RevisionCloud>().Count(cloud => cloud.RevisionId == revision.Id);
        if (clouds > 0) return Say($"It has {clouds} {(clouds == 1 ? "cloud" : "clouds")}: give {(clouds == 1 ? "it" : "them")} another revision, or delete {(clouds == 1 ? "it" : "them")}, first.");
        _working.Remove(revision);
        Refresh();
        return null;
    }

    private string Say(string problem)
    {
        _problem.Text = problem;
        return problem;
    }

    private void Refresh()
    {
        _problem.Text = string.Empty;
        _table.Children.Clear();
        _table.RowDefinitions.Clear();
        _table.ColumnDefinitions.Clear();
        foreach (var width in new[] { 30.0, 70, 120, 110, 260, 130, 110, 60, 150, 100 })
            _table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width) });

        void Put(UIElement element, int row, int column)
        {
            Grid.SetRow(element, row);
            Grid.SetColumn(element, column);
            if (element is FrameworkElement framework) framework.Margin = new Thickness(0, 2, 6, 2);
            _table.Children.Add(element);
        }

        _table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var c = 0; c < Headings.Length; c++) Put(new TextBlock { Text = Headings[c], FontWeight = FontWeights.SemiBold }, 0, c);

        if (_working.Count == 0)
        {
            _table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var none = new TextBlock { Text = "No revisions yet: Add Revision makes the first.", Opacity = 0.7 };
            Grid.SetColumnSpan(none, Headings.Length);
            Put(none, 1, 0);
            return;
        }

        for (var i = 0; i < _working.Count; i++)
        {
            var revision = _working[i];
            var row = i + 1;
            var open = !revision.Issued;
            _table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            Put(new TextBlock { Text = (i + 1).ToString(), Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center }, row, 0);
            Put(new TextBlock { Text = Core.Annotation.Revisions.NumberOf(_working, revision), FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center }, row, 1);

            var numbering = new ComboBox { ItemsSource = EnumText.Choices<RevisionNumbering>(), SelectedItem = EnumText.Humanise(revision.Numbering), IsEnabled = open };
            numbering.SelectionChanged += (_, _) =>
            {
                if (numbering.SelectedItem is string text && EnumText.TryParse<RevisionNumbering>(text, out var chosen) && chosen != revision.Numbering)
                {
                    revision.Numbering = chosen;
                    Dispatcher.BeginInvoke(Refresh);
                }
            };
            Put(numbering, row, 2);

            var date = new TextBox { Text = Core.Annotation.Revisions.FormatDate(revision.Date), IsEnabled = open, ToolTip = "3 Oct 2026, 2026-10-03 or 03/10/2026" };
            date.LostFocus += (_, _) =>
            {
                if (Core.Annotation.Revisions.TryParseDate(date.Text, out var parsed))
                {
                    revision.Date = parsed;
                    date.Text = Core.Annotation.Revisions.FormatDate(parsed);
                }
                else Say($"'{date.Text}' is not a date: write it as 3 Oct 2026.");
            };
            Put(date, row, 3);

            Put(Text(revision.Description, open, text => revision.Description = text), row, 4);
            Put(Text(revision.IssuedTo, open, text => revision.IssuedTo = text), row, 5);
            Put(Text(revision.IssuedBy, open, text => revision.IssuedBy = text), row, 6);

            var issued = new CheckBox { IsChecked = revision.Issued, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Gone out: locked, its clouds with it" };
            issued.Click += (_, _) =>
            {
                revision.Issued = issued.IsChecked == true;
                Refresh();
            };
            Put(issued, row, 7);

            var shows = new ComboBox { ItemsSource = new[] { "Clouds and tags", "Tags only", "Nothing" }, SelectedIndex = (int)revision.Visibility };
            shows.SelectionChanged += (_, _) => { if (shows.SelectedIndex >= 0) revision.Visibility = (RevisionVisibility)shows.SelectedIndex; };
            Put(shows, row, 8);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            buttons.Children.Add(Small("▲", "Earlier", () => Move(revision, -1)));
            buttons.Children.Add(Small("▼", "Later", () => Move(revision, 1)));
            buttons.Children.Add(Small("✕", "Delete", () => Delete(revision)));
            Put(buttons, row, 9);
        }
    }

    private static TextBox Text(string value, bool open, Action<string> set)
    {
        var box = new TextBox { Text = value, IsEnabled = open };
        box.TextChanged += (_, _) => set(box.Text);
        return box;
    }

    private static Button Small(string text, string tip, Func<string?> act)
    {
        var button = new Button { Content = text, Padding = new Thickness(7, 1, 7, 1), Margin = new Thickness(0, 0, 4, 0), ToolTip = tip };
        button.Click += (_, _) => act();
        return button;
    }
}

/// <summary>
/// The revisions on a sheet: those clouded in its views, ticked and fixed; and any put on it by
/// hand, for a change with nothing on a plan to cloud.
/// </summary>
public sealed class RevisionsOnSheetWindow : System.Windows.Window
{
    private readonly Dictionary<Guid, CheckBox> _boxes = new();

    public RevisionsOnSheetWindow(BimDocument document, Sheet sheet)
    {
        DialogLook.Apply(this, $"Revisions on Sheet {sheet.Number}", 560, 440);
        var clouded = Core.Annotation.Revisions.Clouded(document, sheet).ToHashSet();

        var list = new StackPanel { Margin = new Thickness(16, 0, 16, 12) };
        foreach (var revision in document.Revisions)
        {
            var isClouded = clouded.Contains(revision.Id);
            var box = new CheckBox
            {
                Content = Core.Annotation.Revisions.Describe(document, revision) + (isClouded ? "   (clouded in a view on this sheet)" : string.Empty),
                IsChecked = isClouded || sheet.RevisionIds.Contains(revision.Id),
                IsEnabled = !isClouded, Margin = new Thickness(0, 3, 0, 3)
            };
            if (!isClouded) _boxes[revision.Id] = box;
            list.Children.Add(box);
        }

        if (document.Revisions.Count == 0) list.Children.Add(new TextBlock { Text = "The project has no revisions yet: add them in Revisions.", Opacity = 0.7 });

        var ok = new Button { Content = "OK", IsDefault = true };
        ok.Click += (_, _) => DialogResult = true;
        var dock = new DockPanel();
        dock.Children.Add(DialogLook.Intro("Tick the revisions to show on this sheet's revision schedule. Those clouded in a view on it are on it already."));
        dock.Children.Add(DialogLook.Footer(new TextBlock(), ok, new Button { Content = "Cancel", IsCancel = true }));
        dock.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = dock;
    }

    /// <summary>The revisions ticked by hand: what OK hands back.</summary>
    public IReadOnlyList<Guid> Ticked => _boxes.Where(entry => entry.Value.IsChecked == true).Select(entry => entry.Key).ToList();

    /// <summary>A revision ticked or not, as a click would. For driving it without showing it.</summary>
    public void Tick(Guid revision, bool on)
    {
        if (_boxes.TryGetValue(revision, out var box)) box.IsChecked = on;
    }
}

