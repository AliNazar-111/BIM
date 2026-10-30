using System.Windows;
using System.Windows.Controls;
using BIMDesigner.Core.Annotation;
using BIMDesigner.UI.Controls;

namespace BIMDesigner.UI;

/// <summary>
/// Drawing in 2D: the Detail Line and Filled Region tools' style and pattern; and drafting views -
/// made, listed in the Project Browser, opened in the plan to draw in, and only the tools that
/// draw detail working there.
/// </summary>
public partial class MainWindow
{
    /// <summary>The tools a drafting view can be drawn with: nothing that builds the model.</summary>
    private static bool DraftsIn(PlanTool tool) => tool is PlanTool.Select or PlanTool.DetailLine or PlanTool.FilledRegion or PlanTool.Text
        or PlanTool.Dimension or PlanTool.AngularDimension or PlanTool.RadialDimension or PlanTool.RevisionCloud or PlanTool.Mirror or PlanTool.Array
        or PlanTool.Rotate or PlanTool.Scale or PlanTool.Offset or PlanTool.Split or PlanTool.Trim;

    /// <summary>The style or pattern picker for the tool, set to what it draws with.</summary>
    private void ShowDetailOptions(PlanTool tool)
    {
        _loadingOptions = true;
        var line = tool == PlanTool.DetailLine;
        DetailOptionsLabel.Text = line ? "Line style" : "Fill pattern";
        DetailStylePicker.Visibility = line ? Visibility.Visible : Visibility.Collapsed;
        FillPatternPicker.Visibility = line ? Visibility.Collapsed : Visibility.Visible;
        DetailStylePicker.ItemsSource ??= Detailing.LineStyles;
        FillPatternPicker.ItemsSource ??= Detailing.Patterns;
        DetailStylePicker.SelectedItem = Detailing.Name(Plan.ActiveDetailLineStyle);
        FillPatternPicker.SelectedItem = Detailing.Name(Plan.ActiveFillPattern);
        _loadingOptions = false;
    }

    private void OnDetailOptionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingOptions || Plan is null) return;
        if (DetailStylePicker.SelectedItem is string style)
            Plan.ActiveDetailLineStyle = Enum.GetValues<DetailLineStyle>().FirstOrDefault(each => Detailing.Name(each) == style);
        if (FillPatternPicker.SelectedItem is string pattern)
            Plan.ActiveFillPattern = Enum.GetValues<FillPattern>().FirstOrDefault(each => Detailing.Name(each) == pattern);
    }

    private void OnNewDraftingView(object sender, RoutedEventArgs e) => NewDraftingView();

    /// <summary>A drafting view made and opened, to draw a detail in. Internal so the harness and tests can make one.</summary>
    internal DraftingView NewDraftingView(string? name = null)
    {
        var (view, command) = Detailing.Make(_document, name);
        _history.Execute(command);
        OpenDrafting(view);
        StatusHint.Text = $"{view.Name}: a sheet of 2D detail at 1:5. Draw it with Detail Line, Filled Region, Text and Dimension; put it on a sheet with Place. Rename it in Properties.";
        return view;
    }

    /// <summary>A drafting view opened in the plan: what is drawn now goes into it.</summary>
    internal void OpenDrafting(DraftingView view)
    {
        if (!_document.Elements.Contains(view)) return;
        ShowPlanView();
        Plan.ActiveCallout = null;
        Plan.ActiveDiscipline = Core.Mep.Discipline.Architectural;
        _loadingOptions = true;
        LevelPicker.SelectedItem = null;
        _loadingOptions = false;
        Plan.ActiveLevelId = view.Id;
        if (!DraftsIn(Plan.ActiveTool)) SelectTool.IsChecked = true;
        Plan.ZoomToFit();
        RefreshProjectBrowser();
        RefreshProperties();
    }

    /// <summary>The Project Browser's drafting views, each opened by a click; none where there are none.</summary>
    private TreeViewItem? DraftingViewsItem()
    {
        var views = _document.All<DraftingView>().OrderBy(view => view.Name, StringComparer.OrdinalIgnoreCase).ToList();
        if (views.Count == 0) return null;

        var group = new TreeViewItem { Header = "Drafting Views", IsExpanded = true };
        foreach (var view in views)
        {
            var drawn = _document.Elements.Count(element => element.LevelId == view.Id);
            var item = new TreeViewItem { Header = $"{view.Name}  —  {(drawn == 0 ? "empty" : drawn == 1 ? "1 item" : $"{drawn} items")}", Tag = view };
            if (ReferenceEquals(Plan.ActiveDrafting, view)) item.FontWeight = FontWeights.SemiBold;
            item.MouseLeftButtonUp += (_, args) =>
            {
                OpenDrafting(view);
                args.Handled = true;
            };
            item.ContextMenu = DraftingMenu(view);
            group.Items.Add(item);
        }

        return group;
    }

    /// <summary>A drafting view copied, or deleted with what is drawn in it, from the Project Browser.</summary>
    private ContextMenu DraftingMenu(DraftingView view)
    {
        var menu = new ContextMenu();
        var copy = new MenuItem { Header = "Duplicate" };
        copy.Click += (_, _) => DuplicateDrafting(view);
        menu.Items.Add(copy);
        var delete = new MenuItem { Header = "Delete" };
        delete.Click += (_, _) => DeleteDrafting(view);
        menu.Items.Add(delete);
        return menu;
    }

    /// <summary>A drafting view copied with its detail and opened. Internal so the harness and tests can copy one.</summary>
    internal DraftingView DuplicateDrafting(DraftingView view)
    {
        var (copy, command) = Detailing.Duplicate(_document, view);
        _history.Execute(command);
        OpenDrafting(copy);
        StatusHint.Text = $"{view.Name} copied as {copy.Name}, with its detail. Rename it in Properties.";
        return copy;
    }

    /// <summary>A drafting view deleted with its detail; open, the plan goes back to a storey.</summary>
    internal void DeleteDrafting(DraftingView view)
    {
        var drawn = _document.Elements.Count(element => element.LevelId == view.Id);
        _history.Execute(Detailing.Delete(_document, view));
        AfterHistoryChange();
        StatusHint.Text = $"{view.Name} deleted{(drawn == 0 ? "" : drawn == 1 ? ", with the 1 item drawn in it" : $", with the {drawn} items drawn in it")}. Undo brings it back.";
    }
}
