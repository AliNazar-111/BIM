using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;

namespace BIMDesigner.Core.Coordination;

/// <summary>
/// A change to a project's coordination - its clash tests and what they found, its construction
/// programme, its cost plan's additions - as one step to undo: what they were before it and
/// after, each copied whole.
/// </summary>
public sealed class CoordinationEditCommand : IUndoableCommand
{
    private readonly BimDocument _document;
    private readonly State _before;
    private State _after;

    /// <summary>Takes what the coordination is now; the change is then made, and <see cref="Done"/> takes what it has become.</summary>
    public CoordinationEditCommand(BimDocument document, string name)
    {
        _document = document;
        Name = name;
        _before = State.Of(document);
        _after = _before;
    }

    /// <summary>Makes a change as one step: taken before, made, taken after.</summary>
    public static CoordinationEditCommand Make(BimDocument document, string name, Action change)
    {
        var command = new CoordinationEditCommand(document, name);
        change();
        command.Done();
        return command;
    }

    public string Name { get; }

    /// <summary>Takes what the coordination has become, once the change is made.</summary>
    public void Done() => _after = State.Of(_document);

    public void Redo() => _after.Restore(_document);

    public void Undo() => _before.Restore(_document);

    private sealed record State(IReadOnlyList<ClashTest> Tests, ConstructionSchedule Schedule, CostSettings Cost)
    {
        public static State Of(BimDocument document) =>
            new(document.ClashTests.Select(Copy).ToList(), Copy(document.Construction), Copy(document.CostSettings));

        public void Restore(BimDocument document)
        {
            document.ClashTests.Clear();
            document.ClashTests.AddRange(Tests.Select(Copy));
            var schedule = document.Construction;
            var saved = Copy(Schedule);
            schedule.Start = saved.Start;
            schedule.StatusDate = saved.StatusDate;
            schedule.WorkingDays.Clear();
            schedule.WorkingDays.AddRange(saved.WorkingDays);
            schedule.Holidays.Clear();
            schedule.Holidays.AddRange(saved.Holidays);
            schedule.Outputs.Clear();
            foreach (var (trade, output) in saved.Outputs) schedule.Outputs[trade] = output;
            schedule.Tasks.Clear();
            schedule.Tasks.AddRange(saved.Tasks);
            var cost = document.CostSettings;
            (cost.Preliminaries, cost.OverheadsAndProfit, cost.DesignFees, cost.OtherCosts, cost.Risk, cost.Inflation, cost.Tax, cost.Currency) =
                (Cost.Preliminaries, Cost.OverheadsAndProfit, Cost.DesignFees, Cost.OtherCosts, Cost.Risk, Cost.Inflation, Cost.Tax, Cost.Currency);
        }
    }

    // ---- copies --------------------------------------------------------------------------

    public static ClashTest Copy(ClashTest test)
    {
        var copy = new ClashTest { Id = test.Id, Name = test.Name, Kind = test.Kind, Tolerance = test.Tolerance, IgnoreHosted = test.IgnoreHosted, AllowInsulation = test.AllowInsulation, AgainstLinks = test.AgainstLinks, LastRun = test.LastRun };
        copy.SelectionA.AddRange(test.SelectionA);
        copy.SelectionB.AddRange(test.SelectionB);
        copy.Results.AddRange(test.Results.Select(Copy));
        return copy;
    }

    public static Clash Copy(Clash clash)
    {
        var copy = new Clash
        {
            Id = clash.Id, Name = clash.Name, ElementA = clash.ElementA, ElementB = clash.ElementB, Point = clash.Point, Distance = clash.Distance,
            Status = clash.Status, Found = clash.Found, Description = clash.Description, GridLocation = clash.GridLocation, Level = clash.Level,
            AssignedTo = clash.AssignedTo, Priority = clash.Priority, DueDate = clash.DueDate
        };
        copy.Comments.AddRange(clash.Comments);
        return copy;
    }

    public static ConstructionSchedule Copy(ConstructionSchedule schedule)
    {
        var copy = new ConstructionSchedule { Start = schedule.Start, StatusDate = schedule.StatusDate };
        copy.WorkingDays.Clear();
        copy.WorkingDays.AddRange(schedule.WorkingDays);
        copy.Holidays.AddRange(schedule.Holidays);
        copy.Outputs.Clear();
        foreach (var (trade, output) in schedule.Outputs) copy.Outputs[trade] = output;
        copy.Tasks.AddRange(schedule.Tasks.Select(Copy));
        return copy;
    }

    public static ConstructionTask Copy(ConstructionTask task)
    {
        var copy = new ConstructionTask
        {
            Id = task.Id, Name = task.Name, Kind = task.Kind, Duration = task.Duration, StartNoEarlierThan = task.StartNoEarlierThan,
            LevelId = task.LevelId, Walls = task.Walls, Phase = task.Phase, BaselineStart = task.BaselineStart, BaselineFinish = task.BaselineFinish,
            ActualStart = task.ActualStart, ActualFinish = task.ActualFinish, PercentComplete = task.PercentComplete, ActualCost = task.ActualCost, Notes = task.Notes
        };
        copy.Predecessors.AddRange(task.Predecessors);
        copy.ElementIds.AddRange(task.ElementIds);
        copy.Categories.AddRange(task.Categories);
        return copy;
    }

    public static CostSettings Copy(CostSettings cost) => new()
    {
        Preliminaries = cost.Preliminaries, OverheadsAndProfit = cost.OverheadsAndProfit, DesignFees = cost.DesignFees, OtherCosts = cost.OtherCosts,
        Risk = cost.Risk, Inflation = cost.Inflation, Tax = cost.Tax, Currency = cost.Currency
    };
}
