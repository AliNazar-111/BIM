using System.IO;
using System.Windows;
using System.Windows.Input;
using BIMDesigner.Core.Coordination;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;
using BIMDesigner.Infrastructure.Interoperability;
using BIMDesigner.UI.Rendering;

namespace BIMDesigner.UI;

/// <summary>
/// Issues of a model compared: this project with its last save or another file, a linked model with the
/// issue its reload replaced or another; the comparison coloured on the plan and in 3D, and each change
/// gone to.
/// </summary>
public partial class MainWindow
{
    private CompareWindow? _compareWindow;
    private ComparisonOverlay? _comparisonShown;

    /// <summary>Where the project was last saved or opened from; null where it never has been.</summary>
    internal string? ProjectPath => _path;

    private void OnCompare(object sender, RoutedEventArgs e) => OpenCompare();

    /// <summary>The Compare window, opened or brought forward, its choices as they now are.</summary>
    internal CompareWindow OpenCompare()
    {
        var window = Open(ref _compareWindow, () => new CompareWindow(this), this);
        window.Sources();
        return window;
    }

    /// <summary>This project as it is now against a saved project or IFC file: what has changed since.</summary>
    internal ComparisonOverlay? CompareWithFile(string path)
    {
        BimDocument before;
        try
        {
            before = LinkFiles.Read(path);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            MessageBox.Show(this, $"{Path.GetFileName(path)} could not be read: {error.Message}", "Compare", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }

        var comparison = Comparisons.Compare(before, _document, $"{Path.GetFileName(path)}, saved {File.GetLastWriteTime(path):d MMM yyyy HH:mm}", "This project as it is now", DateTime.Now);
        return new ComparisonOverlay(comparison, before, null);
    }

    /// <summary>A linked model as it now is against the issue read before its last reload, or against another issue of it read from a file.</summary>
    internal ComparisonOverlay? CompareLink(ModelLink link, string? path)
    {
        if (ModelLinks.Of(link) is not { Document: { } now })
        {
            MessageBox.Show(this, $"{link.Title} is not loaded: {ModelLinks.Status(link)}", "Compare", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }

        if (path is null)
            return ModelLinks.SinceLastIssue(link) is { } since && ModelLinks.PreviousOf(link) is { } previous ? new ComparisonOverlay(since, previous, link) : null;

        BimDocument before;
        try
        {
            before = LinkFiles.Read(path);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            MessageBox.Show(this, $"{Path.GetFileName(path)} could not be read: {error.Message}", "Compare", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }

        var comparison = Comparisons.Compare(before, now, $"{Path.GetFileName(path)}, saved {File.GetLastWriteTime(path):d MMM yyyy HH:mm}",
            $"{link.Title}, as linked", DateTime.Now);
        return new ComparisonOverlay(comparison, before, link);
    }

    /// <summary>A comparison coloured on the plan and in 3D; none, to draw them as they are.</summary>
    internal void ShowComparison(ComparisonOverlay? overlay)
    {
        _comparisonShown = overlay;
        Plan.Comparison = overlay;
        Model3D.Comparison = overlay;
    }

    /// <summary>Whether a change can be gone to: an element's, not a type's or a level's.</summary>
    internal bool CanShowChange(ComparisonOverlay overlay, ElementChange change) => overlay.Comparison.Of(change.Id) is not null;

    /// <summary>
    /// A change gone to: the element selected - or, removed or in a linked model, its link - on its
    /// storey, and the plan turned to where it is or was.
    /// </summary>
    internal void ShowChange(ComparisonOverlay overlay, ElementChange change)
    {
        var removed = change.Kind == ChangeKind.Removed;
        var source = overlay.Link is { } link
            ? removed ? overlay.Before : ModelLinks.Of(link)?.Document
            : removed ? overlay.Before : _document;
        if (source?.FindElement(change.Id) is not { } element) return;

        // Its storey here: the same, or one of its name.
        var storey = source.FindLevel(element.LevelId) is { } level
            ? _document.FindLevel(level.Id) ?? _document.Levels.FirstOrDefault(each => each.Name == level.Name)
            : null;
        if (overlay.Link is null && storey is not null && storey.Id != Plan.ActiveLevelId) LevelPicker.SelectedItem = storey;
        if (overlay.Link is { } linked) SelectFromBrowser(linked);
        else SelectFromBrowser(removed ? null : element);

        // Where it is: the points it is set out by, or - a door, a window, carried by its wall - its shape.
        IReadOnlyList<Point2D> points;
        try
        {
            points = ElementTransforms.PlanPoints(element);
            if (points.Count == 0)
                points = ModelMeshBuilder.Build(source, each => each.Id == element.Id || (element is IHostedElement hosted && each.Id == hosted.HostId))
                    .Where(mesh => mesh.ElementId == element.Id).SelectMany(mesh => mesh.Positions).Select(point => new Point2D(point.X, point.Y)).ToList();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            points = Array.Empty<Point2D>();
        }

        if (overlay.Link is { } placedBy && points.Count > 0)
        {
            var place = ModelLinks.Placement(_document, placedBy, source);
            points = points.Select(point => place(new Point3D(point.X, point.Y, 0))).Select(point => new Point2D(point.X, point.Y)).ToList();
        }

        if (points.Count > 0)
        {
            ShowPlanView();
            Plan.ZoomTo(new Point2D(points.Min(p => p.X), points.Min(p => p.Y)), new Point2D(points.Max(p => p.X), points.Max(p => p.Y)));
        }

        StatusHint.Text = $"{change.Name}: {change.Summary}";
    }

    /// <summary>What was compared for the last project forgotten, as another opens.</summary>
    private void ForgetComparison()
    {
        _compareWindow?.Close();
        _compareWindow = null;
        ShowComparison(null);
    }

    /// <summary>After a linked model is reloaded: what its new issue has changed, said, and shown where the Compare window is open.</summary>
    private void SayLinkChanges(ModelLink link)
    {
        if (ModelLinks.SinceLastIssue(link) is not { } since) return;
        StatusHint.Text = since.Same
            ? $"Reloaded {link.Title}: nothing has changed since the issue before."
            : $"Reloaded {link.Title}: {since.Said()} since the issue before - Coordinate → Compare to see them.";
        if (_compareWindow is { IsLoaded: true } window && ModelLinks.PreviousOf(link) is { } previous)
        {
            window.Sources();
            window.Display(new ComparisonOverlay(since, previous, link));
        }
    }
}
