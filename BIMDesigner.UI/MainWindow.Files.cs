using System.IO;
using System.Windows;
using System.Windows.Input;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Infrastructure.Interoperability;
using BIMDesigner.UI.Export;
using Microsoft.Win32;

namespace BIMDesigner.UI;

/// <summary>Files to and from other programs: IFC in, IFC2x3 out, DWG and DXF drawings in to work over and out as plans and sheets, and .bimx files opened by a double-click.</summary>
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
    private void OnImportCad(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import a DWG or DXF Drawing",
            Filter = "CAD drawings (*.dwg;*.dxf)|*.dwg;*.dxf|DWG drawing (*.dwg)|*.dwg|DXF drawing (*.dxf)|*.dxf|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) == true) ImportCad(dialog.FileName, quiet: false);
    }

    /// <summary>
    /// A DWG or DXF drawing brought onto the storey shown, to work over: origin to origin, or - one
    /// set out far from the project's origin, a site surveyed to the national grid - its middle at
    /// the middle of the view. Said on the status bar; quietly, where the window is driven unseen.
    /// </summary>
    internal CadUnderlay? ImportCad(string path, bool quiet)
    {
        if (_document.FindLevel(Plan.ActiveLevelId) is not { } level) return null;
        CadImport.Result read;
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            read = CadImport.Read(path);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Mouse.OverrideCursor = null;
            if (!quiet) MessageBox.Show(this, $"Could not read {Path.GetFileName(path)}.\n\n{exception.Message}", "Import DWG or DXF", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }

        if (read.Drawing.Paths.Count == 0 && read.Drawing.Labels.Count == 0)
        {
            if (!quiet) MessageBox.Show(this, $"{Path.GetFileName(path)} has nothing in its model space to draw.", "Import DWG or DXF", MessageBoxButton.OK, MessageBoxImage.Information);
            return null;
        }

        var underlay = new CadUnderlay { LevelId = level.Id, Source = path, Drawing = read.Drawing, Scale = read.Scale };
        underlay.HiddenLayers.UnionWith(read.HiddenLayers);
        var bounds = underlay.Bounds;
        var far = !bounds.IsEmpty && Math.Max(Math.Abs(bounds.Centre.X), Math.Abs(bounds.Centre.Y)) > FarFromOrigin;
        if (far) underlay.Location = Plan.ViewCentre - (bounds.Centre - new Point2D(0, 0));

        _history.Execute(new AddElementCommand(_document, underlay, "Import DWG or DXF"));
        Plan.Select(underlay);
        Plan.ZoomToFit();
        StatusHint.Text = $"Imported {Path.GetFileName(path)} onto {level.Name}: {read.Drawing.Paths.Count:#,0} lines and {read.Drawing.Labels.Count:#,0} texts on {read.Drawing.Layers.Count} layers, in {read.Units.ToLowerInvariant()}"
                          + (far ? ", its middle put in the middle of the view - it is set out far from the project's origin." : ", origin to origin.")
                          + " Double-click it for its layers; the drawing tools snap to its lines. " + string.Join(" ", read.Notes);
        return underlay;
    }

    /// <summary>How far from the project's origin, mm, a drawing is taken to be set out somewhere else - a national grid - and is brought to the view instead: a kilometre.</summary>
    private const double FarFromOrigin = 1_000_000;

    /// <summary>An imported drawing's layers, ticked where shown; what is unticked is hidden as one step to undo.</summary>
    internal void EditUnderlayLayers(CadUnderlay underlay)
    {
        var window = new CadLayersWindow(underlay) { Owner = this };
        if (window.ShowDialog() != true) return;
        var hidden = window.Hidden.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (hidden.SetEquals(underlay.HiddenLayers)) return;
        _history.Execute(new SetUnderlayLayersCommand(underlay, hidden));
        StatusHint.Text = $"{underlay.Drawing.Layers.Count - hidden.Count} of {underlay.Drawing.Layers.Count} layers of {Path.GetFileName(underlay.Source)} shown.";
    }

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

    /// <summary>What a drawing can be exported as: DWG first, as CAD users most often want it.</summary>
    private const string CadFilter = "DWG drawing, AutoCAD 2018 (*.dwg)|*.dwg|DXF drawing (*.dxf)|*.dxf|All files (*.*)|*.*";

    private void OnExportPlanCad(object sender, RoutedEventArgs e)
    {
        if (_document.FindLevel(Plan.ActiveLevelId) is not { } level) return;
        var dialog = new SaveFileDialog
        {
            Title = "Export Plan to DWG or DXF",
            Filter = CadFilter,
            DefaultExt = ".dwg",
            FileName = CadExport.SuggestedFileName(_document, $"{level.Name} Plan")
        };
        if (dialog.ShowDialog(this) != true) return;
        if (WriteCad(() => CadExport.Plan(_document, level, Plan.CurrentView), dialog.FileName))
            StatusHint.Text = $"Exported {level.Name} Plan to {Path.GetFileName(dialog.FileName)}: full size, in millimetres, each kind of element on its own layer.";
    }

    private void OnExportSheetCad(object sender, RoutedEventArgs e)
    {
        if (SheetSurface.Sheet is not { } sheet)
        {
            MessageBox.Show(this, "Open a sheet first: View tab → New Sheet, or Ctrl+H.", "Export to DWG or DXF", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Export Sheet to DWG or DXF",
            Filter = CadFilter,
            DefaultExt = ".dwg",
            FileName = CadExport.SuggestedFileName(_document, sheet.Number)
        };
        if (dialog.ShowDialog(this) != true) return;
        if (WriteCad(() => CadExport.Sheet(_document, sheet), dialog.FileName))
            StatusHint.Text = $"Exported {sheet.Number} to {Path.GetFileName(dialog.FileName)}: paper size, in millimetres.";
    }

    private bool WriteCad(Func<ACadSharp.CadDocument> make, string path)
    {
        Mouse.OverrideCursor = Cursors.Wait;
        var kind = CadExport.IsDwg(path) ? "DWG" : "DXF";
        try
        {
            CadExport.Save(make(), path);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"Could not write the {kind}.\n\n{exception.Message}\n\nIf it is open in another program, close it and try again.",
                $"Export to {kind}", MessageBoxButton.OK, MessageBoxImage.Warning);
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
