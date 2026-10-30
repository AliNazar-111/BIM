using System.Windows;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;

namespace BIMDesigner.UI;

/// <summary>
/// Keeping the project in order: Warnings, everything wrong with the model in one list to go
/// through and go to; and Purge Unused, the types and materials nothing uses taken out.
/// </summary>
public partial class MainWindow
{
    private WarningsWindow? _warningsWindow;
    private PurgeWindow? _purgeWindow;

    private void OnWarnings(object sender, RoutedEventArgs e) => OpenWarnings();

    /// <summary>The Warnings window, opened or brought forward.</summary>
    internal WarningsWindow OpenWarnings() => Open(ref _warningsWindow, () => new WarningsWindow(this), this);

    private void OnPurgeUnused(object sender, RoutedEventArgs e) => OpenPurge();

    /// <summary>The Purge Unused window, opened or brought forward, listing what is unused now.</summary>
    internal PurgeWindow OpenPurge()
    {
        var window = Open(ref _purgeWindow, () => new PurgeWindow(this), this);
        window.Refresh();
        return window;
    }

    /// <summary>
    /// The types the tools are set to draw with - the wall tool's, the beam tool's, every one's -
    /// which are kept by a purge whether anything is drawn with them yet or not.
    /// </summary>
    internal IReadOnlySet<Guid> ToolTypes() =>
        Plan.GetType().GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Where(property => property.PropertyType == typeof(Guid) && property.Name.StartsWith("Active", StringComparison.Ordinal) && property.Name.EndsWith("TypeId", StringComparison.Ordinal))
            .Select(property => (Guid)property.GetValue(Plan)!)
            .Where(id => id != Guid.Empty)
            .ToHashSet();

    /// <summary>After a purge, or its undo: the tools' type lists and every view follow.</summary>
    internal void AfterPurge()
    {
        _typesSeen = (_document.ElementTypes.Count, _document.Materials.Count);
        AfterTypesChanged();
    }

    /// <summary>How many types and materials the tools' lists were last filled from.</summary>
    private (int Types, int Materials) _typesSeen;

    /// <summary>
    /// After any step, undone or redone: where types or materials have come or gone - a purge
    /// undone - the tools' lists follow, once the step is through.
    /// </summary>
    private void FollowTypes()
    {
        var now = (_document.ElementTypes.Count, _document.Materials.Count);
        if (now == _typesSeen) return;
        _typesSeen = now;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(AfterTypesChanged));
    }

    /// <summary>Something said on the status bar, from a window of its own.</summary>
    internal void StatusSay(string said) => StatusHint.Text = said;

    /// <summary>
    /// What a warning is about, gone to: its storey shown, it selected, and the plan turned to
    /// where it is.
    /// </summary>
    internal void GoTo(IReadOnlyList<Element> elements)
    {
        var present = elements.Where(element => _document.FindElement(element.Id) is not null).ToList();
        if (present.Count == 0) return;

        // Its storey: its own, or for a door or window its wall's.
        Guid StoreyOf(Element element) => element is Opening opening && _document.Find<Wall>(opening.HostWallId) is { } wall ? wall.LevelId : element.LevelId;
        if (present.Select(StoreyOf).FirstOrDefault(id => _document.FindLevel(id) is not null) is var storey && _document.FindLevel(storey) is { } level && level.Id != Plan.ActiveLevelId)
            LevelPicker.SelectedItem = level;

        ShowPlanView();
        Plan.SelectMany(present);
        RefreshProperties();

        // Where it is: a room's outline, the points the rest are set out by, or - a door, a window - its shape in its wall.
        var points = new List<Point2D>();
        foreach (var element in present)
        {
            try
            {
                if (element is Room room && room.GetBoundary(_document) is { IsEnclosed: true } boundary)
                {
                    points.AddRange(boundary.Polygon);
                    continue;
                }

                var set = ElementTransforms.PlanPoints(element);
                if (set.Count == 0)
                    set = ModelMeshBuilder.Build(_document, each => each.Id == element.Id || (element is IHostedElement hosted && each.Id == hosted.HostId))
                        .Where(mesh => mesh.ElementId == element.Id).SelectMany(mesh => mesh.Positions).Select(point => new Point2D(point.X, point.Y)).ToList();
                points.AddRange(set);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                // Somewhere it cannot be found: selected, but not zoomed to.
            }
        }

        if (points.Count > 0)
            Plan.ZoomTo(new Point2D(points.Min(p => p.X), points.Min(p => p.Y)), new Point2D(points.Max(p => p.X), points.Max(p => p.Y)));
    }
}
