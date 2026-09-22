using System.Windows;
using System.Windows.Controls;
using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.UI.ViewModels;

using Window = System.Windows.Window;

namespace BIMDesigner.UI;

/// <summary>
/// The storey list (specification section 2.3).
///
/// Every change here goes straight through the undo stack onto the model, so the plan behind
/// the dialog updates as levels are edited. A levels editor that only applied its changes on
/// OK would be showing you a building that is not the building.
/// </summary>
public partial class LevelsWindow : Window
{
    private readonly BimDocument _document;
    private readonly UndoStack _history;

    public LevelsWindow(BimDocument document, UndoStack history)
    {
        InitializeComponent();
        ScreenFit.Apply(this);

        _document = document;
        _history = history;

        Refresh();
    }

    /// <summary>Raised whenever the model changed, so the owner can repaint and resync.</summary>
    public event EventHandler? Changed;

    private LevelRow? Selected => LevelGrid.SelectedItem as LevelRow;

    private void Refresh()
    {
        var selectedId = Selected?.Level.Id;

        var rows = _document.Levels
            .Select(level =>
            {
                var row = new LevelRow(_document, level);
                row.Edited += OnRowEdited;
                return row;
            })
            .ToList();

        LevelGrid.ItemsSource = rows;
        LevelGrid.SelectedItem = rows.FirstOrDefault(row => row.Level.Id == selectedId);

        UpdateButtons();
    }

    private void UpdateButtons()
    {
        // The last level cannot go: a project with no storeys has nowhere to put anything,
        // and every tool that hosts on a level would silently stop working.
        DeleteButton.IsEnabled = Selected is not null && _document.Levels.Count > 1;
    }

    private void OnRowEdited(object? sender, LevelEditEventArgs e)
    {
        if (e.NewName is { } name) _history.Execute(new RenameLevelCommand(e.Level, name));
        else if (e.NewElevation is { } elevation)
            _history.Execute(new MoveLevelCommand(_document, e.Level, elevation));

        // A move can reorder the storeys, so the table is rebuilt rather than patched.
        Refresh();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnAddLevel(object sender, RoutedEventArgs e)
    {
        var level = new Level
        {
            Name = Levels.NextName(_document),
            Elevation = Levels.NextElevation(_document)
        };

        _history.Execute(new AddLevelCommand(_document, level));

        Refresh();
        Changed?.Invoke(this, EventArgs.Empty);

        LevelGrid.SelectedItem = LevelGrid.Items
            .OfType<LevelRow>()
            .FirstOrDefault(row => row.Level.Id == level.Id);
    }

    private void OnDeleteLevel(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row) return;
        if (_document.Levels.Count <= 1) return;

        var command = new DeleteLevelCommand(_document, row.Level);

        if (command.Hosted.Count > 0 || command.Released.Count > 0)
        {
            var lines = new List<string> { $"Delete {row.Level.Name}?" };

            if (command.Hosted.Count > 0)
            {
                lines.Add(command.Hosted.Count == 1
                    ? "\n1 element stands on it and will be deleted with it."
                    : $"\n{command.Hosted.Count} elements stand on it and will be deleted with it.");
            }

            if (command.Released.Count > 0)
            {
                lines.Add(command.Released.Count == 1
                    ? "1 wall reaches up to it and will keep its current height."
                    : $"{command.Released.Count} walls reach up to it and will keep their current height.");
            }

            lines.Add("\nThis can be undone in one step.");

            var answer = MessageBox.Show(this, string.Join("\n", lines), "Delete Level",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning);

            if (answer != MessageBoxResult.OK) return;
        }

        _history.Execute(command);

        Refresh();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();

    /// <summary>
    /// A cell commits on losing focus, which happens after this fires, so the counts are
    /// refreshed once the edit has actually landed.
    /// </summary>
    private void OnCellEditEnding(object? sender, DataGridCellEditEndingEventArgs e) =>
        Dispatcher.BeginInvoke(() =>
        {
            foreach (var row in LevelGrid.Items.OfType<LevelRow>()) row.Refresh();
            Changed?.Invoke(this, EventArgs.Empty);
        });

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
