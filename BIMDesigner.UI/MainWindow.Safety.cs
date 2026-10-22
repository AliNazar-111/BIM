using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using BIMDesigner.Infrastructure.Diagnostics;
using BIMDesigner.Infrastructure.Serialization;

namespace BIMDesigner.UI;

/// <summary>
/// Keeping the work safe: a recovery copy of unsaved work every few minutes, and whenever
/// something goes wrong; the copy thrown away once the work is saved or closed; and, at the next
/// start, work left by a session that stopped without doing either offered back.
/// </summary>
public partial class MainWindow
{
    /// <summary>How often unsaved work is kept, while there is any.</summary>
    private static readonly TimeSpan KeepEvery = TimeSpan.FromMinutes(5);

    private readonly RecoverySession _recovery = new();
    private DispatcherTimer? _keepTimer;

    /// <summary>Whether the model has changed since the last recovery copy was kept.</summary>
    private bool _changedSinceKept;

    /// <summary>Whether there are changes not in any file.</summary>
    public bool HasUnsavedWork => _history.IsModified;

    /// <summary>Says something on the status bar.</summary>
    public void Tell(string text) => StatusHint.Text = text;

    /// <summary>Starts keeping a copy of unsaved work every few minutes, for as long as the window is open.</summary>
    public void StartKeepingWork()
    {
        if (_keepTimer is not null) return;

        _keepTimer = new DispatcherTimer(DispatcherPriority.ApplicationIdle, Dispatcher) { Interval = KeepEvery };
        _keepTimer.Tick += (_, _) => KeepWork();
        _keepTimer.Start();
        Closed += (_, _) =>
        {
            _keepTimer.Stop();
            _recovery.Dispose();
            AppLog.Info("Closed.");
        };
    }

    /// <summary>
    /// Keeps a copy of the work in hand, where it has changes not saved that the last copy does not
    /// have - but not in the middle of a drag, unless it is wanted now. False where there was
    /// nothing to keep, or it could not be kept.
    /// </summary>
    public bool KeepWork(bool now = false)
    {
        if (!_history.IsModified)
        {
            _recovery.Discard();
            return false;
        }

        if (!_changedSinceKept && _recovery.HasCopy) return true;
        if (!now && (Mouse.LeftButton == MouseButtonState.Pressed || Plan.IsMouseCaptured)) return false;

        if (!_recovery.Keep(_document, _path)) return false;
        _changedSinceKept = false;
        return true;
    }

    /// <summary>A new history to watch: any change to it is work the recovery copy does not have yet.</summary>
    private void WatchForWorkToKeep()
    {
        _recovery.Discard();
        _changedSinceKept = false;
        _history.Changed += (_, _) => _changedSinceKept = true;
    }

    /// <summary>
    /// At the start: work left by a session that stopped without saving it, offered back - the
    /// most recent. Brought back, it is as it was, and not yet in any file; turned down, it is
    /// thrown away; put off, it is offered again next time.
    /// </summary>
    public void OfferRecovery(Func<RecoveredWork, MessageBoxResult>? ask = null)
    {
        if (RecoverySession.Left() is not [var work, ..]) return;

        var name = work.SavedAs is { } saved ? Path.GetFileName(saved) : work.Name;
        var answer = (ask ?? Ask)(work);
        if (answer == MessageBoxResult.Cancel) return;

        if (answer == MessageBoxResult.Yes)
        {
            try
            {
                LoadDocument(ProjectFile.Load(work.File), work.SavedAs, seedExample: false);
                _history.MarkModified();
                StatusHint.Text = $"Brought back {name} as it was at {work.Kept:HH:mm} on {work.Kept:d MMMM}. Save it to keep it.";
                AppLog.Info($"Brought back the work on {name} kept at {work.Kept:yyyy-MM-dd HH:mm}.");
            }
            catch (ProjectFileException exception)
            {
                AppLog.Warning($"The work kept on {name} could not be read.", exception);
                MessageBox.Show(this, $"The copy kept of {name} could not be read: {exception.Message}", "Recover unsaved work",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        RecoverySession.Forget(work);

        MessageBoxResult Ask(RecoveredWork left) => MessageBox.Show(this,
            $"BIMDesigner stopped without saving your work on {name}. A copy of it was kept at {left.Kept:HH:mm} on {left.Kept:d MMMM}.\n\n" +
            "Open it now? No throws it away; Cancel asks again next time.",
            "Recover unsaved work", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
    }
}
