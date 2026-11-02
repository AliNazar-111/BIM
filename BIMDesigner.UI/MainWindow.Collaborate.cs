using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Materials;
using BIMDesigner.Core.Views;
using BIMDesigner.Infrastructure.Collaboration;
using Microsoft.Win32;

namespace BIMDesigner.UI;

/// <summary>
/// Worksharing: a project shared through a central model, each person working in a local copy of
/// their own. Opening a central makes the local copy; every edit borrows what it changes, or is
/// refused and who has it said; Synchronize sends one's own and brings back the others', Reload
/// Latest brings back the others'; worksets made, made one's own and asked for; and every view
/// coloured by who has what, or by workset.
/// </summary>
public partial class MainWindow
{
    /// <summary>The worksharing session of the local copy open, or null where the project is not shared.</summary>
    private WorkshareSession? _workshare;

    /// <summary>Who is working, as the others see them: the Windows user name unless set. Settable so tests and the harness can be two people.</summary>
    internal string WorkshareUser { get; set; } = Environment.UserName;

    /// <summary>Where local copies are made; null for the app's own folder. Settable for tests and the harness.</summary>
    internal string? LocalCopiesFolder { get; set; }

    internal WorkshareSession? Workshare => _workshare;

    private WorksetsWindow? _worksetsWindow;
    private EditingRequestsWindow? _requestsWindow;

    /// <summary>What has been said of the central and of requests, so each is said once.</summary>
    private (int Version, int Requests) _shareSaid;

    /// <summary>
    /// A project as it opens: a central model is not worked in - a local copy of it is made for
    /// whoever opens it and that opened instead; a local copy is taken up as it is. What to say, or null.
    /// </summary>
    private (BimDocument Document, string? Path, string? Said) Shared(BimDocument document, string? path)
    {
        _workshare = null;
        if (!document.Worksharing.Enabled || path is null) return (document, path, null);
        try
        {
            if (document.Worksharing.IsCentral)
            {
                _workshare = WorkshareSession.CreateLocal(path, WorkshareUser, LocalCopiesFolder);
                return (_workshare.Document, _workshare.LocalPath,
                    $"A central model: your local copy of it opened - {_workshare.LocalPath}. Work here, and Synchronize to send your changes.");
            }

            _workshare = WorkshareSession.Attach(document, path, WorkshareUser);
            return (document, path, _workshare.CentralIsNewer ? "The central model has been changed since this copy was brought up to it: Reload Latest." : null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or BIMDesigner.Infrastructure.Serialization.ProjectFileException)
        {
            _workshare = null;
            return (document, path, $"Its central model could not be reached - {error.Message} - so it is open on its own, not shared.");
        }
    }

    /// <summary>What an edit changed borrowed, or why it cannot be - for the edit to be taken back.</summary>
    private string? BorrowForEdit() => _workshare?.Borrow();

    // ---- enabling ------------------------------------------------------------------------

    private void OnWorksharing(object sender, RoutedEventArgs e)
    {
        if (_document.Worksharing.Enabled)
        {
            OpenWorksets();
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Save the Central Model",
            Filter = BIMDesigner.Infrastructure.Serialization.ProjectFile.FileFilter,
            FileName = Path.GetFileName(_path ?? "Central.bimx")
        };
        if (dialog.ShowDialog(this) == true) EnableWorksharing(dialog.FileName);
    }

    /// <summary>
    /// This project shared: saved as the central model where it is said - somewhere the team can reach -
    /// and a local copy of it made and opened to work in. Internal so tests and the harness can share.
    /// </summary>
    internal void EnableWorksharing(string centralPath)
    {
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            WorkshareSession.Enable(_document, centralPath);
            var session = WorkshareSession.CreateLocal(centralPath, WorkshareUser, LocalCopiesFolder);
            TakeUpShared(session, $"Shared: the central model is {centralPath}, issue 1; you work in your local copy, {session.LocalPath}. Others open the central to get theirs.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            StatusHint.Text = $"Could not share it: {error.Message}";
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    /// <summary>A shared copy's document opened in place of the one open - after sharing, synchronising or reloading.</summary>
    private void TakeUpShared(WorkshareSession session, string said)
    {
        var level = _document.FindLevel(Plan.ActiveLevelId)?.Id;
        LoadDocument(session.Document, session.LocalPath, seedExample: false);
        _workshare = session;
        if (level is { } id && _document.FindLevel(id) is { } same) LevelPicker.SelectedItem = same;
        _shareSaid = (session.Document.Worksharing.BaseVersion, 0);
        RefreshCollaborate();
        StatusHint.Text = said;
    }

    // ---- synchronising -----------------------------------------------------------------------

    private void OnSynchronize(object sender, RoutedEventArgs e)
    {
        if (!RequireShared()) return;
        var dialog = new SynchronizeWindow { Owner = this };
        if (dialog.ShowDialog() == true) Synchronize(dialog.Comment, dialog.Relinquish);
    }

    /// <summary>Synchronize with Central: one's own sent, the others' brought back, what was borrowed let go - asked. Internal for tests and the harness.</summary>
    internal void Synchronize(string comment, bool relinquish)
    {
        if (!RequireShared()) return;
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            var result = _workshare!.Synchronize(comment, relinquish);
            TakeUpShared(_workshare, result.Said);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            StatusHint.Text = $"Could not synchronise: {error.Message}";
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    private void OnReloadLatest(object sender, RoutedEventArgs e) => ReloadLatest();

    /// <summary>Everyone else's changes brought back, one's own kept. Internal for tests and the harness.</summary>
    internal void ReloadLatest()
    {
        if (!RequireShared()) return;
        try
        {
            var result = _workshare!.ReloadLatest();
            TakeUpShared(_workshare, result.Said);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            StatusHint.Text = $"Could not reload: {error.Message}";
        }
    }

    private void OnRelinquish(object sender, RoutedEventArgs e)
    {
        if (!RequireShared()) return;
        StatusHint.Text = _workshare!.Relinquish();
        Plan.RefreshModel();
        _worksetsWindow?.Refresh();
    }

    private bool RequireShared()
    {
        if (_workshare is not null) return true;
        StatusHint.Text = _document.Worksharing.Enabled
            ? "This copy is not connected to its central model: open the central to get a local copy of it."
            : "This project is not shared: Collaborate, Worksharing makes it a central model.";
        return false;
    }

    // ---- worksets, requests and display -------------------------------------------------------

    private void OnWorksets(object sender, RoutedEventArgs e) => OpenWorksets();

    internal WorksetsWindow OpenWorksets()
    {
        var window = Open(ref _worksetsWindow, () => new WorksetsWindow(this), this);
        window.Refresh();
        return window;
    }

    private void OnEditingRequests(object sender, RoutedEventArgs e) => OpenEditingRequests();

    internal EditingRequestsWindow OpenEditingRequests()
    {
        var window = Open(ref _requestsWindow, () => new EditingRequestsWindow(this), this);
        window.Refresh();
        return window;
    }

    private void OnActiveWorksetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingOptions || ActiveWorksetPicker.SelectedItem is not string name || name == _document.Worksharing.Active) return;
        _history.Execute(Worksets.SetActive(_document, name));
        StatusHint.Text = $"New elements go on {name}.";
    }

    private static readonly string[] DisplayChoices = { "Off", "Who has each element", "Each element's workset" };

    private void OnWorksharingDisplayChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingOptions) return;
        RefreshWorksharingDisplay();
    }

    /// <summary>Every view coloured as the display is set: by who has each element, by its workset, or not at all.</summary>
    internal void RefreshWorksharingDisplay()
    {
        var choice = WorksharingDisplayPicker.SelectedIndex;
        if (choice <= 0 || !_document.Worksharing.Enabled)
        {
            _document.ViewSettings.Display = null;
        }
        else if (choice == 1 && _workshare is { } session)
        {
            var registry = session.Registry();
            var me = session.User;
            _document.ViewSettings.Display = element => registry.OwnerOf(element.Id, element.Workset) is { } owner
                ? new ElementStyle(Colour: owner == me ? Mine : Palette[Math.Abs(StringComparer.OrdinalIgnoreCase.GetHashCode(owner)) % Palette.Length])
                : null;
        }
        else
        {
            var names = _document.Worksharing.Worksets.Select(workset => workset.Name).ToList();
            _document.ViewSettings.Display = element => names.FindIndex(name => string.Equals(name, element.Workset, StringComparison.OrdinalIgnoreCase)) is var at && at >= 0
                ? new ElementStyle(Colour: Palette[at % Palette.Length])
                : null;
        }

        Plan.RefreshModel();
        Model3D.Rebuild();
    }

    private static readonly ColourRgb Mine = ColourRgb.FromHex("2E9E4F");

    private static readonly ColourRgb[] Palette =
    {
        ColourRgb.FromHex("D9822B"), ColourRgb.FromHex("2F6FD0"), ColourRgb.FromHex("B23A8E"), ColourRgb.FromHex("1F9E9E"),
        ColourRgb.FromHex("C9A227"), ColourRgb.FromHex("7A4FC4"), ColourRgb.FromHex("C0392B"), ColourRgb.FromHex("5B7F2A")
    };

    /// <summary>The Collaborate tab as the project now stands: the active workset's list, and the display's.</summary>
    internal void RefreshCollaborate()
    {
        _loadingOptions = true;
        var shared = _document.Worksharing.Enabled;
        ActiveWorksetPicker.ItemsSource = shared ? Worksets.Choices(_document) : Array.Empty<string>();
        ActiveWorksetPicker.SelectedItem = shared ? _document.Worksharing.Active : null;
        ActiveWorksetPicker.IsEnabled = shared;
        WorksharingDisplayPicker.ItemsSource ??= DisplayChoices;
        if (WorksharingDisplayPicker.SelectedIndex < 0) WorksharingDisplayPicker.SelectedIndex = 0;
        WorksharingButton.Content = shared ? "Worksets" : "Worksharing";
        _loadingOptions = false;
        if (WorksharingDisplayPicker.SelectedIndex > 0) RefreshWorksharingDisplay();
    }

    /// <summary>
    /// The central looked at every few seconds: written by someone since this copy was brought up to it,
    /// or something asked of this person, or answered - each said once.
    /// </summary>
    private void WatchCentral()
    {
        if (_workshare is not { } session) return;
        try
        {
            var registry = session.Registry();
            var asked = session.Requests();
            if (registry.Version > session.Document.Worksharing.BaseVersion && registry.Version != _shareSaid.Version)
            {
                var last = registry.History.LastOrDefault();
                StatusHint.Text = $"The central model is at issue {registry.Version}{(last is null ? string.Empty : $" - {last.User}: {last.Comment}")}: Reload Latest to bring it in.";
                _shareSaid = (registry.Version, _shareSaid.Requests);
            }

            if (asked.Count != _shareSaid.Requests)
            {
                if (asked.FirstOrDefault(request => request.To == session.User) is { } request)
                    StatusHint.Text = $"{request.From} asks for {request.Name}: Collaborate, Editing Requests.";
                else if (asked.FirstOrDefault(request => request.From == session.User) is { } answered)
                    StatusHint.Text = $"{answered.To} {answered.Answer.ToLowerInvariant()} you {answered.Name}.";
                _shareSaid = (_shareSaid.Version, asked.Count);
                _requestsWindow?.Refresh();
            }
        }
        catch (IOException)
        {
            // The central's folder out of reach for now: looked at again in a few seconds.
        }
    }
}
