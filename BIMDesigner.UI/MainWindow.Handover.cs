using System.IO;
using System.Windows;
using System.Windows.Input;
using BIMDesigner.Core.Handover;
using BIMDesigner.UI.Export;
using Microsoft.Win32;

namespace BIMDesigner.UI;

/// <summary>
/// The handover, on the Coordinate tab: the asset register and the maintenance plan in a window of
/// their own, each asset tagged, and the operation and maintenance manual and the Home User Guide
/// written out as PDFs.
/// </summary>
public partial class MainWindow
{
    private HandoverWindow? _handoverWindow;

    private void OnHandover(object sender, RoutedEventArgs e) => Open(ref _handoverWindow, () => new HandoverWindow(this), this).Refresh();

    /// <summary>Each asset's tag written into its mark where it has none, as one step.</summary>
    internal void OnTagAssets(object sender, RoutedEventArgs e)
    {
        if (Assets.TagCommand(_document) is not { } command)
        {
            StatusHint.Text = Assets.Of(_document).Count == 0 ? "There are no assets to tag yet." : "Every asset has a mark already: its tag.";
            return;
        }

        _history.Execute(command);
        Plan.RefreshModel();
        RefreshProperties();
        StatusHint.Text = $"{command.Name}: each one's tag is now its mark, on the drawings, the labels and the register alike.";
    }

    internal void OnOmManual(object sender, RoutedEventArgs e) => SaveHandover(guide: false);

    internal void OnHomeUserGuide(object sender, RoutedEventArgs e) => SaveHandover(guide: true);

    private void SaveHandover(bool guide)
    {
        var title = guide ? "Home User Guide" : "Operation and Maintenance Manual";
        if (HandoverReport.WhyNot(_document) is { } why)
        {
            MessageBox.Show(this, why, title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = $"Save the {title}",
            Filter = "PDF document (*.pdf)|*.pdf",
            DefaultExt = ".pdf",
            FileName = HandoverReport.SuggestedFileName(_document, guide)
        };
        if (dialog.ShowDialog(this) != true) return;
        SaveHandoverTo(dialog.FileName, guide);
    }

    /// <summary>The manual or the guide written to a file, and how long it runs said; its page count, or none where it could not be written.</summary>
    internal int? SaveHandoverTo(string path, bool guide)
    {
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            var report = guide ? HandoverReport.Guide(_document, AppVersion, DateTime.Today) : HandoverReport.Manual(_document, AppVersion, DateTime.Today);
            if (report is null) return null;
            HandoverReport.ToPdf(report, path);
            StatusHint.Text = $"{(guide ? "The Home User Guide" : "The operation and maintenance manual")} saved: {Path.GetFileName(path)}, {report.PageCount} pages.";
            return report.PageCount;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"Could not write the PDF.\n\n{exception.Message}", guide ? "Home User Guide" : "Operation and Maintenance Manual", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }
}
