using System.Windows;
using BIMDesigner.Core.Views;

namespace BIMDesigner.UI;

/// <summary>
/// The drawings an application goes in with: the planning drawings - location plan and block plan,
/// in the US vicinity map and site plan - and the drawing set as one PDF, bookmarked by sheet and
/// signed with the designer's digital ID where a permit portal asks for it.
/// </summary>
public partial class MainWindow
{
    private PlanningWindow? _planningWindow;
    private SignedSetWindow? _signedSetWindow;

    private void OnPlanningDrawings(object sender, RoutedEventArgs e) => OpenPlanning();

    /// <summary>The Planning Drawings window, opened or brought forward.</summary>
    internal PlanningWindow OpenPlanning()
    {
        var window = Open(ref _planningWindow, () => new PlanningWindow(this), this);
        window.Fill();
        return window;
    }

    /// <summary>A planning drawing's sheet opened, where it has been made.</summary>
    internal void OpenPlanningSheet(PlanningDrawing drawing)
    {
        if (PlanningDrawings.SheetOf(_document, drawing) is { } sheet) ShowSheet(sheet);
        else StatusHint.Text = "Make the drawings first: Planning Drawings → Make Drawings.";
    }

    /// <summary>After the red and blue lines or the planning drawings change: the plan, the sheets and the browser follow.</summary>
    internal void AfterPlanningChange()
    {
        RefreshSheetPicker();
        AfterHistoryChange();
    }

    private void OnExportSignedSet(object sender, RoutedEventArgs e) => OpenSignedSet();

    /// <summary>The window to sign the drawing set with, opened or brought forward.</summary>
    internal SignedSetWindow OpenSignedSet() => Open(ref _signedSetWindow, () => new SignedSetWindow(this), this);

    /// <summary>Every sheet, in drawing-number order: what a set is issued as.</summary>
    internal IReadOnlyList<Core.Sheets.Sheet> SheetsOfTheSet() => SheetsInOrder();
}
