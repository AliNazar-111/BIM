using System.Windows;
using System.Windows.Controls;
using BIMDesigner.Core.Annotation;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Parameters;
using BIMDesigner.Core.Sheets;
using BIMDesigner.Core.Views;
using BIMDesigner.UI.Controls;

namespace BIMDesigner.UI;

/// <summary>Revisions, their clouds and the sheets they are on; keynotes, their table and their tags.</summary>
public partial class MainWindow
{
    /// <summary>A revision as the cloud tool's picker lists it.</summary>
    private sealed record RevisionChoice(Revision Revision, string Display)
    {
        public override string ToString() => Display;
    }

    /// <summary>A key as the keynote tool's picker lists it.</summary>
    private sealed record KeyChoice(string Key, string Display)
    {
        public override string ToString() => Display;
    }

    private void ShowCloudOptions()
    {
        _loadingOptions = true;
        var open = _document.Revisions.Where(revision => !revision.Issued)
            .Select(revision => new RevisionChoice(revision, Revisions.Describe(_document, revision))).ToList();
        CloudRevisionPicker.ItemsSource = open;
        CloudRevisionPicker.SelectedItem = open.FirstOrDefault(choice => choice.Revision == Plan.CloudRevision());
        CloudTagBox.IsChecked = Plan.CloudTagged;
        _loadingOptions = false;
        if (open.Count == 0)
            StatusHint.Text = _document.Revisions.Count == 0
                ? "No revisions yet: the first cloud makes the first, P01, dated today - or add them in Revisions."
                : "Every revision has been issued: add a new one in Revisions to cloud what it changes.";
    }

    private void OnCloudRevisionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingOptions || CloudRevisionPicker.SelectedItem is not RevisionChoice choice) return;
        Plan.ActiveRevisionId = choice.Revision.Id;
    }

    private void OnCloudTagChanged(object sender, RoutedEventArgs e)
    {
        if (!_loadingOptions) Plan.CloudTagged = CloudTagBox.IsChecked == true;
    }

    private void ShowKeynoteOptions()
    {
        _loadingOptions = true;
        KeynoteSourcePicker.ItemsSource ??= EnumText.Choices<KeynoteSource>();
        KeynoteSourcePicker.SelectedItem = EnumText.Humanise(Plan.ActiveKeynoteSource);
        var keys = _document.Keynotes.Notes.Select(entry => new KeyChoice(entry.Key, $"{entry.Key}   {entry.Text}")).ToList();
        KeynoteKeyPicker.ItemsSource = keys;
        KeynoteKeyPicker.SelectedItem = keys.FirstOrDefault(choice => choice.Key == Plan.ActiveKeynoteKey);
        var user = Plan.ActiveKeynoteSource == KeynoteSource.User;
        KeynoteKeyPicker.Visibility = KeynoteKeyLabel.Visibility = user ? Visibility.Visible : Visibility.Collapsed;
        _loadingOptions = false;
    }

    private void OnKeynoteSourceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingOptions || KeynoteSourcePicker.SelectedItem is not string text || !EnumText.TryParse<KeynoteSource>(text, out var source)) return;
        Plan.ActiveKeynoteSource = source;
        Plan.CancelPendingOperation();
        ShowKeynoteOptions();
        StatusHint.Text = source switch
        {
            KeynoteSource.Element => "Click an element - its type's keynote is what the tag reads - then where the box goes.",
            KeynoteSource.Material => "Click a wall, floor, roof or ceiling where the material is, then where the box goes.",
            _ => "Pick the key, click what it notes, then where the box goes."
        };
    }

    private void OnKeynoteKeyChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loadingOptions && KeynoteKeyPicker.SelectedItem is KeyChoice choice) Plan.ActiveKeynoteKey = choice.Key;
    }

    /// <summary>Revisions, keynotes or what is on a sheet changed: every view of them follows.</summary>
    private void AnnotationsChanged()
    {
        Plan.RefreshModel();
        SheetSurface.Refresh();
        RefreshProperties();
        RefreshSchedule();
        ShowOptionsForActiveTool();
    }

    private void OnShowRevisions(object sender, RoutedEventArgs e) => EditRevisions();

    /// <summary>The Revisions window, its changes kept as one step. <paramref name="accept"/> stands in for the dialog, where the window is driven without being shown.</summary>
    private bool EditRevisions(Func<RevisionsWindow, bool>? accept = null)
    {
        var window = new RevisionsWindow(_document);
        if (accept is null) window.Owner = this;
        if (!(accept?.Invoke(window) ?? window.ShowDialog() == true)) return false;

        var before = _document.Revisions.Select(revision => revision.Copy()).ToList();
        var after = window.Revisions.Select(revision => revision.Copy()).ToList();
        void Put(List<Revision> revisions)
        {
            _document.Revisions.Clear();
            _document.Revisions.AddRange(revisions.Select(revision => revision.Copy()));
        }

        _history.Execute(new DelegateCommand("Revisions", () => Put(after), () => Put(before)));
        AnnotationsChanged();
        return true;
    }

    private void OnRevisionsOnSheet(object sender, RoutedEventArgs e)
    {
        if (SheetSurface.Sheet is not { } sheet)
        {
            StatusHint.Text = "Open a sheet first: its revisions are what is put on it.";
            return;
        }

        RevisionsOnSheet(sheet);
    }

    private bool RevisionsOnSheet(Sheet sheet, Func<RevisionsOnSheetWindow, bool>? accept = null)
    {
        var window = new RevisionsOnSheetWindow(_document, sheet);
        if (accept is null) window.Owner = this;
        if (!(accept?.Invoke(window) ?? window.ShowDialog() == true)) return false;

        var before = sheet.RevisionIds.ToList();
        var after = window.Ticked.ToList();
        void Put(List<Guid> ids)
        {
            sheet.RevisionIds.Clear();
            sheet.RevisionIds.AddRange(ids);
        }

        _history.Execute(new DelegateCommand($"Revisions on Sheet {sheet.Number}", () => Put(after), () => Put(before)));
        AnnotationsChanged();
        return true;
    }

    private void OnShowKeynotes(object sender, RoutedEventArgs e) => EditKeynotes();

    /// <summary>The Keynotes window, the table and the materials' keys kept as one step.</summary>
    private bool EditKeynotes(Func<KeynotesWindow, bool>? accept = null)
    {
        var window = new KeynotesWindow(_document);
        if (accept is null) window.Owner = this;
        if (!(accept?.Invoke(window) ?? window.ShowDialog() == true)) return false;

        var (tableBefore, tableAfter) = (_document.Keynotes.Copy(), window.Table);
        var materialsBefore = _document.Materials.ToDictionary(material => material.Id, material => material.Keynote);
        var materialsAfter = window.MaterialKeynotes;
        void Put(KeynoteTable table, IReadOnlyDictionary<Guid, string> keys)
        {
            _document.Keynotes.CopyFrom(table);
            foreach (var material in _document.Materials)
                if (keys.TryGetValue(material.Id, out var key)) material.Keynote = key;
        }

        _history.Execute(new DelegateCommand("Keynotes", () => Put(tableAfter, materialsAfter), () => Put(tableBefore, materialsBefore)));
        AnnotationsChanged();
        return true;
    }
}
