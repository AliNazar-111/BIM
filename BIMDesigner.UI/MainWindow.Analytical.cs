using System.IO;
using System.Windows;
using System.Windows.Input;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Infrastructure.Interoperability;
using Microsoft.Win32;

namespace BIMDesigner.UI;

/// <summary>
/// The analytical model on the Structure tab: shown over the plan and in 3D, its loads and
/// combinations in a window of their own, and written out for an analysis program as SAF or as
/// IFC4's structural analysis view.
/// </summary>
public partial class MainWindow
{
    private AnalyticalModelWindow? _analyticalWindow;

    /// <summary>Whether the analytical model is shown over the plan and in 3D.</summary>
    private bool ShowsAnalytical => AnalyticalToggle.IsChecked == true;

    private void OnAnalyticalToggled(object sender, RoutedEventArgs e) => ShowAnalytical(true);

    /// <summary>The analytical model as the model now stands over the plan and in 3D - or neither, where it is not shown.</summary>
    internal void ShowAnalytical(bool say = false)
    {
        var model = ShowsAnalytical ? AnalyticalModel.Of(_document) : null;
        Plan.Analytical = model;
        Model3D.Analytical = model;
        if (!say) return;
        StatusHint.Text = model is null
            ? "The analytical model is hidden."
            : model.Members.Count == 0 && model.Panels.Count == 0
                ? "Nothing is in the analytical model yet: draw structural columns and beams, or set walls' Structural Usage to Bearing."
                : $"The analytical model: {model.Members.Count} member{(model.Members.Count == 1 ? "" : "s")} and {model.Panels.Count} plate{(model.Panels.Count == 1 ? "" : "s")} on {model.Nodes.Count} nodes, held by {model.Supports.Count} support{(model.Supports.Count == 1 ? "" : "s")}{(model.Panels.Any(panel => panel.Subgrade is not null) ? " and the ground" : "")}{(model.Problems.Count > 0 ? $" - {model.Problems.Count} thing{(model.Problems.Count == 1 ? "" : "s")} not joined up, in Warnings" : "")}.";
    }

    private void OnAnalyticalLoads(object sender, RoutedEventArgs e) => Open(ref _analyticalWindow, () => new AnalyticalModelWindow(this), this).Refresh();

    private AnalysisWindow? _analysisWindow;

    /// <summary>The structure analysed here, and its results shown - the analytical model turned on to see its deflected shape over.</summary>
    internal void OnAnalyse(object sender, RoutedEventArgs e)
    {
        var model = AnalyticalModel.Of(_document);
        if (model.Members.Count == 0 && model.Panels.Count == 0)
        {
            MessageBox.Show(this, "There is nothing to analyse yet: draw structural columns and beams, or set walls' Structural Usage to Bearing.", "Analyse",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            var started = DateTime.Now;
            var analysis = StructuralAnalysis.Run(_document, model, StructuralLoading.Of(_document, model));
            if (AnalyticalToggle.IsChecked != true)
            {
                AnalyticalToggle.IsChecked = true;
                ShowAnalytical();
            }

            if (_analysisWindow is { IsLoaded: true } open) open.Show(analysis);
            else Open(ref _analysisWindow, () => new AnalysisWindow(this, analysis), this);
            StatusHint.Text = $"Analysed in {(DateTime.Now - started).TotalSeconds:0.0} s: {analysis.Solver.NodeCount} nodes, {analysis.Solver.Beams.Count} beams, {analysis.Solver.Shells.Count} triangles" +
                              (analysis.Problems.Count > 0 ? $" - {analysis.Problems[0]}" : ".");
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    /// <summary>The analysed structure's deflected shape in 3D, or none.</summary>
    internal void ShowDeflected((StructuralAnalysis Analysis, object Loading, double Scale)? deflected) => Model3D.Deflected = deflected;

    /// <summary>The name an exported file is offered under: the project's, made safe for a file.</summary>
    private string ExportName(string suffix, string extension)
    {
        var name = _document.ProjectInformation.Name;
        if (string.IsNullOrWhiteSpace(name)) name = "Model";
        return $"{new string(name.Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray()).Trim()}{suffix}{extension}";
    }

    /// <summary>Whether there is an analytical model to write out; said where there is not.</summary>
    private bool HasAnalytical(string title)
    {
        var model = AnalyticalModel.Of(_document);
        if (model.Members.Count > 0 || model.Panels.Count > 0) return true;
        MessageBox.Show(this, "There is no analytical model to export yet: draw structural columns and beams, or set walls' Structural Usage to Bearing.",
            title, MessageBoxButton.OK, MessageBoxImage.Information);
        return false;
    }

    internal void OnExportSaf(object sender, RoutedEventArgs e)
    {
        if (!HasAnalytical("Export SAF")) return;
        var dialog = new SaveFileDialog
        {
            Title = "Export SAF",
            Filter = "SAF structural analysis model (*.xlsx)|*.xlsx|All files (*.*)|*.*",
            DefaultExt = ".xlsx",
            FileName = ExportName(" - analytical", ".xlsx")
        };
        if (dialog.ShowDialog(this) != true) return;

        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            SafExport.Save(_document, dialog.FileName);
            StatusHint.Text = $"The analytical model written as SAF {SafExport.Version}: {Path.GetFileName(dialog.FileName)}.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"Could not write the SAF file.\n\n{exception.Message}", "Export SAF", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    internal void OnExportIfcAnalytical(object sender, RoutedEventArgs e)
    {
        if (!HasAnalytical("Export IFC with Analytical Model")) return;
        var dialog = new SaveFileDialog
        {
            Title = "Export IFC with Analytical Model",
            Filter = "IFC model (*.ifc)|*.ifc|All files (*.*)|*.*",
            DefaultExt = ".ifc",
            FileName = ExportName(" - structural analysis", ".ifc")
        };
        if (dialog.ShowDialog(this) != true) return;

        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            IfcExport.Save(_document, dialog.FileName, new IfcExportOptions
            {
                ApplicationVersion = AppVersion,
                AuthorGivenName = Environment.UserName,
                Organisation = _document.ProjectInformation.Client,
                Analytical = true
            });
            StatusHint.Text = $"The model written as IFC4 with its analytical model: {Path.GetFileName(dialog.FileName)}.";
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, $"Could not write the IFC file.\n\n{exception.Message}", "Export IFC with Analytical Model", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }
}
