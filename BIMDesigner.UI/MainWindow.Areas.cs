using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Parameters;
using BIMDesigner.Core.Sheets;
using BIMDesigner.Core.Views;

namespace BIMDesigner.UI;

/// <summary>
/// Area plans: each storey measured in a scheme - GEA, GIA, NIA, or in the US the gross and the
/// finished area - opened from the ribbon or the Project Browser, its area and what is left out
/// said as it opens and in Properties, and placed on a sheet with its schedule.
/// </summary>
public partial class MainWindow
{
    private void OnAreaPlanMenu(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = AreaPlanButton, Placement = PlacementMode.Bottom };
        foreach (var scheme in AreaPlans.Schemes(_document))
        {
            var item = new MenuItem { Header = AreaPlans.Title(_document, scheme) };
            item.Click += (_, _) =>
            {
                if (_document.FindLevel(Plan.ActiveLevelId) is { } level) ShowAreaPlan(level, scheme);
                else if (_document.Levels.FirstOrDefault() is { } first) ShowAreaPlan(first, scheme);
            };
            menu.Items.Add(item);
        }

        menu.IsOpen = true;
    }

    /// <summary>A storey's area plan in a scheme, opened: its area and what is left out of it said.</summary>
    internal void ShowAreaPlan(Level level, AreaScheme scheme)
    {
        Plan.ActiveCallout = null;
        LevelPicker.SelectedItem = level;
        Plan.ActiveAreaScheme = scheme;
        ShowPlanView();
        RefreshProjectBrowser();
        RefreshProperties();
        StatusHint.Text = AreaSaid(level, scheme);
    }

    /// <summary>A storey's area in a scheme, as the status bar says it.</summary>
    private string AreaSaid(Level level, AreaScheme scheme)
    {
        var measure = AreaPlans.Measure(_document, level.Id, scheme);
        if (measure.Outlines.Count == 0)
            return measure.Note ?? $"{level.Name}: nothing to measure - its outside walls make no outline{(scheme == AreaScheme.NetInternal ? ", or it has no rooms" : string.Empty)}.";
        var notes = AreaPlans.Notes(measure);
        return $"{level.Name} {AreaPlans.Title(_document, scheme)}: {AreaPlans.Said(scheme, measure.Area)}{(measure.BelowGrade && scheme == AreaScheme.Finished ? ", below grade" : string.Empty)}{(notes.Length > 0 ? $" - {notes}" : string.Empty)}. {AreaPlans.Code(scheme)}";
    }

    /// <summary>The Project Browser's area plans: each scheme's storeys, those with anything to measure.</summary>
    private TreeViewItem AreaPlansItem()
    {
        var areas = new TreeViewItem { Header = "Area Plans", IsExpanded = Plan.ActiveAreaScheme is not null };
        foreach (var scheme in AreaPlans.Schemes(_document))
        {
            var group = new TreeViewItem { Header = AreaPlans.Title(_document, scheme), IsExpanded = Plan.ActiveAreaScheme == scheme };
            foreach (var level in _document.Levels.OrderByDescending(level => level.Elevation))
            {
                var measure = AreaPlans.Measure(_document, level.Id, scheme);
                if (measure.Outlines.Count == 0) continue;
                var active = Plan.ActiveAreaScheme == scheme && level.Id == Plan.ActiveLevelId && Plan.ActiveCallout is null;
                var item = new TreeViewItem
                {
                    Header = $"{level.Name}  —  {AreaPlans.Said(scheme, measure.Area)}",
                    FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal,
                    IsSelected = active
                };
                item.MouseLeftButtonUp += (_, args) =>
                {
                    ShowAreaPlan(level, scheme);
                    args.Handled = true;
                };
                group.Items.Add(item);
            }

            if (group.Items.Count == 0) group.Items.Add(new TreeViewItem { Header = "Nothing to measure yet: draw the outside walls", IsEnabled = false });
            areas.Items.Add(group);
        }

        return areas;
    }

    /// <summary>What Place offers of the area plans: each storey's in each scheme, and each scheme's schedule.</summary>
    private IEnumerable<SheetViewOption> AreaPlanOptions()
    {
        foreach (var scheme in AreaPlans.Schemes(_document))
        {
            var measured = AreaPlans.Storeys(_document, scheme);
            foreach (var (level, _) in measured)
                yield return new SheetViewOption($"{AreaPlans.Short(_document, scheme)} Area Plan: {level.Name}", AreaPlans.View(level.Id, scheme));
            if (measured.Count > 0)
                yield return new SheetViewOption(AreaPlans.ScheduleTitle(_document, AreaPlans.Schedule(scheme)), AreaPlans.Schedule(scheme));
        }
    }

    private static readonly ParameterDefinition AreaParameter = new("Area", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Dimensions);
    private static readonly ParameterDefinition AreaNotesParameter = new("Left Out and Stated", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Dimensions);
    private static readonly ParameterDefinition AreaCodeParameter = new("Measured By", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.IdentityData);

    /// <summary>An area plan's own: the storey's area in its scheme, what is left out and stated, and the code it is measured by.</summary>
    private IEnumerable<ParameterValue> AreaPlanParameters(ViewReference view, AreaScheme scheme)
    {
        var measure = AreaPlans.Measure(_document, view.TargetId, scheme);
        yield return ParameterValue.ReadOnly(AreaParameter, () => AreaPlans.Said(scheme, measure.Area) + (measure.BelowGrade && scheme == AreaScheme.Finished ? ", below grade" : string.Empty));
        yield return ParameterValue.ReadOnly(AreaNotesParameter, () => AreaPlans.Notes(measure) is { Length: > 0 } notes ? notes : "Nothing");
        yield return ParameterValue.ReadOnly(AreaCodeParameter, () => AreaPlans.Code(scheme));
    }
}
