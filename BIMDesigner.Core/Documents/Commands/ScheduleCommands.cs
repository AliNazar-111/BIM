using BIMDesigner.Core.Schedules;
using BIMDesigner.Core.Sheets;
using BIMDesigner.Core.Views;

namespace BIMDesigner.Core.Documents.Commands;

/// <summary>Schedules made, changed and deleted, each as one step to undo.</summary>
public static class ScheduleCommands
{
    /// <summary>A schedule added to the project, after the others.</summary>
    public static IUndoableCommand Add(BimDocument document, ScheduleDefinition schedule, string name = "New Schedule") =>
        new DelegateCommand(name, () => document.Schedules.Add(schedule), () => document.Schedules.Remove(schedule));

    /// <summary>A schedule made the same as an edited copy of it: undone, it is as it was.</summary>
    public static IUndoableCommand Change(ScheduleDefinition schedule, ScheduleDefinition edited, string name = "Edit Schedule")
    {
        var before = schedule.Copy(schedule.Name);
        var after = edited.Copy(edited.Name);
        return new DelegateCommand(name, () => schedule.CopyFrom(after), () => schedule.CopyFrom(before));
    }

    /// <summary>A schedule deleted, and taken off every sheet it was placed on.</summary>
    public static IUndoableCommand Delete(BimDocument document, ScheduleDefinition schedule)
    {
        var index = document.Schedules.IndexOf(schedule);
        var placed = document.Elements.OfType<Sheet>()
            .SelectMany(sheet => sheet.Viewports.Where(viewport => ReferenceEquals(viewport.View.ScheduleIn(document), schedule))
                .Select(viewport => (IUndoableCommand)new RemoveViewportCommand(sheet, viewport)))
            .ToList();

        return new CompositeCommand($"Delete {schedule.Name}", placed.Append(new DelegateCommand(
            "Delete Schedule",
            () => document.Schedules.Remove(schedule),
            () => document.Schedules.Insert(Math.Clamp(index, 0, document.Schedules.Count), schedule))));
    }
}
