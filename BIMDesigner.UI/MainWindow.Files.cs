using System.IO;
using System.Windows;
using System.Windows.Input;
using BIMDesigner.Infrastructure.Interoperability;
using BIMDesigner.UI.Export;
using Microsoft.Win32;

namespace BIMDesigner.UI;

/// <summary>Files to and from other programs: IFC in, IFC2x3 out, plans and sheets as DXF, and .bimx files opened by a double-click.</summary>
public partial class MainWindow
{
    /// <summary>This build's version, as its project says: what an IFC file it writes names as its source.</summary>
    internal static string AppVersion =>
        typeof(MainWindow).Assembly.GetName().Version is { } version ? $"{version.Major}.{version.Minor}.{version.Build}" : "0.0.0";

    private void OnImportIfc(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscardingChanges()) return;
        var dialog = new OpenFileDialog { Title = "Open IFC", Filter = "IFC model (*.ifc)|*.ifc|All files (*.*)|*.*", DefaultExt = ".ifc" };
        if (dialog.ShowDialog(this) == true) ImportIfc(dialog.FileName, quiet: false);
    }

    /// <summary>
    /// An IFC file read into a new project, unsaved: what came in, on the status bar, and what did
    /// not come in as itself listed - quietly, where the window is driven without being shown.
    /// </summary>
    private IfcImportResult? ImportIfc(string path, bool quiet)
    {
        IfcImportResult result;
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            result = IfcImport.Open(path);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Mouse.OverrideCursor = null;
            if (!quiet) MessageBox.Show(this, $"Could not read the IFC file.\n\n{exception.Message}", "Open IFC", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }

        LoadDocument(result.Document, path: null, seedExample: false);
        StatusHint.Text = $"Read {Path.GetFileName(path)} ({result.Schema}): {result.Summary()}. Save it as a project to keep it.";

        if (!quiet && result.Notes.Count > 0)
            MessageBox.Show(this,
                $"{Path.GetFileName(path)} came in as {result.Summary()}.\n\n" + string.Join("\n", result.Notes.Take(15).Select(note => "• " + note))
                + (result.Notes.Count > 15 ? $"\n... and {result.Notes.Count - 15} more." : string.Empty),
                "Open IFC", MessageBoxButton.OK, MessageBoxImage.Information);
        return result;
    }

    private void OnExportIfc2x3(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export IFC2x3",
            Filter = "IFC model (*.ifc)|*.ifc|All files (*.*)|*.*",
            DefaultExt = ".ifc",
            FileName = $"{Sanitised(_document.ProjectInformation.Name, "Model")} (IFC2x3).ifc"
        };
        if (dialog.ShowDialog(this) != true) return;

        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            Ifc2x3Export.Save(_document, dialog.FileName, new IfcExportOptions { ApplicationVersion = AppVersion, AuthorGivenName = Environment.UserName, Organisation = _document.ProjectInformation.Client });
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            MessageBox.Show(this, $"Could not write the IFC file.\n\n{exception.Message}", "Export IFC2x3", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }

        StatusHint.Text = $"Exported IFC2x3 (Coordination View 2.0) to {Path.GetFileName(dialog.FileName)}.";
        OfferToOpen(dialog.FileName);
    }

    private void OnExportPlanDxf(object sender, RoutedEventArgs e)
    {
        if (_document.FindLevel(Plan.ActiveLevelId) is not { } level) return;
        var dialog = new SaveFileDialog
        {
            Title = "Export Plan to DXF",
            Filter = "DXF drawing (*.dxf)|*.dxf|All files (*.*)|*.*",
            DefaultExt = ".dxf",
            FileName = DxfExport.SuggestedFileName(_document, $"{level.Name} Plan")
        };
        if (dialog.ShowDialog(this) != true) return;
        if (WriteDxf(() => DxfExport.Plan(_document, level, Plan.CurrentView), dialog.FileName))
            StatusHint.Text = $"Exported {level.Name} Plan to {Path.GetFileName(dialog.FileName)}: full size, in millimetres.";
    }

    private void OnExportSheetDxf(object sender, RoutedEventArgs e)
    {
        if (SheetSurface.Sheet is not { } sheet)
        {
            MessageBox.Show(this, "Open a sheet first: View tab → New Sheet, or Ctrl+H.", "Export to DXF", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Export Sheet to DXF",
            Filter = "DXF drawing (*.dxf)|*.dxf|All files (*.*)|*.*",
            DefaultExt = ".dxf",
            FileName = DxfExport.SuggestedFileName(_document, sheet.Number)
        };
        if (dialog.ShowDialog(this) != true) return;
        if (WriteDxf(() => DxfExport.Sheet(_document, sheet), dialog.FileName))
            StatusHint.Text = $"Exported {sheet.Number} to {Path.GetFileName(dialog.FileName)}: paper size, in millimetres.";
    }

    private bool WriteDxf(Func<netDxf.DxfDocument> make, string path)
    {
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            DxfExport.Save(make(), path);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"Could not write the DXF.\n\n{exception.Message}\n\nIf it is open in another program, close it and try again.",
                "Export to DXF", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    private void OnRegisterFileTypes(object sender, RoutedEventArgs e)
    {
        if (Environment.ProcessPath is not { } executable) return;
        try
        {
            FileAssociations.RegisterForUser(executable);
            StatusHint.Text = "Project files (.bimx) now open with this copy of BIMDesigner when double-clicked; IFC files list it under Open With.";
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            MessageBox.Show(this, $"Windows would not let the file types be registered.\n\n{exception.Message}", "Open .bimx Files", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static string Sanitised(string name, string fallback)
    {
        var cleaned = new string((name ?? string.Empty).Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray()).Trim();
        return cleaned.Length == 0 ? fallback : cleaned;
    }
}
