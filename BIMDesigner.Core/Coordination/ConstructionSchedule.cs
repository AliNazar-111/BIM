using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Coordination;

/// <summary>What a task does to what it is linked to: builds it, takes it down, or puts it up and takes it away again.</summary>
public enum TaskKind
{
    Construct,
    Demolish,
    Temporary
}

/// <summary>Which walls a task's rule takes: any, only those outside, only those inside.</summary>
public enum WallSide
{
    Any,
    Exterior,
    Interior
}

/// <summary>A task waiting on another: to start once it finishes, so many working days after.</summary>
public sealed record TaskLink(Guid Predecessor, int Lag = 0);

/// <summary>
/// A task in the construction programme: what it is, how long it takes in working days, what it
/// waits for, what it builds - elements picked one by one, and everything of these categories on
/// a storey - and how far it has got: its actual start and finish, how complete, what it has cost.
/// </summary>
public sealed class ConstructionTask
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Name { get; set; } = "Task";

    public TaskKind Kind { get; set; } = TaskKind.Construct;

    /// <summary>Working days; none for a milestone.</summary>
    public int Duration { get; set; } = 5;

    /// <summary>The earliest it may start, whatever it waits for.</summary>
    public DateOnly? StartNoEarlierThan { get; set; }

    public List<TaskLink> Predecessors { get; } = new();

    public List<Guid> ElementIds { get; } = new();

    /// <summary>Its rule: everything of these categories on this storey - none set, no rule.</summary>
    public Guid? LevelId { get; set; }

    public List<BuiltInCategory> Categories { get; } = new();

    public WallSide Walls { get; set; } = WallSide.Any;

    /// <summary>Only elements standing in this phase - existing ones to demolish, temporary works.</summary>
    public DesignPhase? Phase { get; set; }

    public DateOnly? BaselineStart { get; set; }

    public DateOnly? BaselineFinish { get; set; }

    public DateOnly? ActualStart { get; set; }

    public DateOnly? ActualFinish { get; set; }

    /// <summary>How much of it is done, 0 to 100.</summary>
    public double PercentComplete { get; set; }

    public decimal ActualCost { get; set; }

    public string Notes { get; set; } = string.Empty;
}

/// <summary>
/// The construction programme: when work starts, which days are worked, the tasks, the date
/// progress is measured to, and how much a gang gets through in a day for each trade - what a
/// programme made from the model is timed by.
/// </summary>
public sealed class ConstructionSchedule
{
    public DateOnly Start { get; set; } = DateOnly.FromDateTime(DateTime.Today);

    public List<DayOfWeek> WorkingDays { get; } = new() { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday };

    public List<DateOnly> Holidays { get; } = new();

    public List<ConstructionTask> Tasks { get; } = new();

    /// <summary>The date progress is measured to; none, today.</summary>
    public DateOnly? StatusDate { get; set; }

    public Dictionary<string, double> Outputs { get; } = new(ConstructionPlanning.DefaultOutputs, StringComparer.OrdinalIgnoreCase);

    public ConstructionTask? Find(Guid id) => Tasks.FirstOrDefault(task => task.Id == id);
}

/// <summary>A task timed: its start and finish, the earliest and latest it could run, its float, and whether it is on the critical path.</summary>
public sealed record TaskTimes(ConstructionTask Task, DateOnly Start, DateOnly Finish, int EarlyStart, int EarlyFinish, int LateStart, int LateFinish)
{
    public int TotalFloat => LateStart - EarlyStart;

    public bool Critical => TotalFloat <= 0;

    public bool IsMilestone => EarlyFinish == EarlyStart;
}

/// <summary>Where an element is on a date: not started, going up, built, coming down, gone, up for a while, or in no task.</summary>
public enum BuildState
{
    Unscheduled,
    NotStarted,
    InProgress,
    Built,
    Demolishing,
    Demolished,
    Temporary,
    TemporaryGone
}

/// <summary>
/// Timing a construction programme by the critical path method: each task as early as what it
/// waits for and its own earliest start allow, finish to start, in working days - the forward
/// pass - then as late as the tasks after it allow without the end moving - the backward pass;
/// the difference its total float, and the tasks with none the critical path. A task started or
/// finished is timed from its actual dates.
/// </summary>
public static class ConstructionPlanning
{
    /// <summary>
    /// How much a gang gets through in a working day, by trade - a starting point, to be set from
    /// the contractor's own outputs. Brickwork from BCIS's 600 bricks a day for a good bricklayer,
    /// about 10 m², two of them to a gang; blockwork 20 m² a day; the rest assumed.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, double> DefaultOutputs = new Dictionary<string, double>
    {
        ["Footings (m)"] = 12,
        ["Ground slab (m²)"] = 60,
        ["Columns and beams (nr)"] = 5,
        ["External walls (m²)"] = 20,
        ["Internal walls (m²)"] = 20,
        ["Curtain walls (m²)"] = 15,
        ["Upper floors (m²)"] = 40,
        ["Roofs (m²)"] = 25,
        ["Roof edges (m)"] = 20,
        ["Windows and doors (nr)"] = 6,
        ["Stairs and ramps (nr)"] = 1,
        ["Ceilings (m²)"] = 30,
        ["Railings (m)"] = 15,
        ["Fittings and services (nr)"] = 8,
        ["Pipes and ducts (m)"] = 25,
        ["External works (nr)"] = 4,
        ["Demolition (m²)"] = 30
    };

    // ---- calendar ----------------------------------------------------------------

    private static bool Works(ConstructionSchedule schedule, DateOnly date) =>
        schedule.WorkingDays.Contains(date.DayOfWeek) && !schedule.Holidays.Contains(date);

    /// <summary>The first working day the programme has: its start, or the next worked after it.</summary>
    public static DateOnly FirstDay(ConstructionSchedule schedule)
    {
        var date = schedule.Start;
        if (schedule.WorkingDays.Count == 0) return date;
        while (!Works(schedule, date)) date = date.AddDays(1);
        return date;
    }

    /// <summary>The date of a working day, counted from the first.</summary>
    public static DateOnly DateOf(ConstructionSchedule schedule, int day)
    {
        var date = FirstDay(schedule);
        if (schedule.WorkingDays.Count == 0) return date.AddDays(day);
        var step = day >= 0 ? 1 : -1;
        for (var counted = 0; counted != day;)
        {
            date = date.AddDays(step);
            if (Works(schedule, date)) counted += step;
        }

        return date;
    }

    /// <summary>Which working day a date falls on, counted from the first - a day off counted as the next worked.</summary>
    public static int DayOf(ConstructionSchedule schedule, DateOnly date)
    {
        var first = FirstDay(schedule);
        if (schedule.WorkingDays.Count == 0) return date.DayNumber - first.DayNumber;
        var (count, step) = (0, date >= first ? 1 : -1);
        for (var day = first; day != date; day = day.AddDays(step))
            if (Works(schedule, step > 0 ? day : day.AddDays(-1))) count += step;
        return count;
    }

    // ---- critical path -------------------------------------------------------------

    /// <summary>Every task timed, in the order they come.</summary>
    public static IReadOnlyList<TaskTimes> Time(ConstructionSchedule schedule)
    {
        var tasks = schedule.Tasks.ToDictionary(task => task.Id);
        var order = Ordered(schedule);
        var (early, finish) = (new Dictionary<Guid, int>(), new Dictionary<Guid, int>());

        foreach (var task in order)
        {
            var start = task.StartNoEarlierThan is { } earliest ? Math.Max(0, DayOf(schedule, earliest)) : 0;
            foreach (var link in task.Predecessors.Where(link => finish.ContainsKey(link.Predecessor)))
                start = Math.Max(start, finish[link.Predecessor] + link.Lag);
            if (task.ActualStart is { } began) start = DayOf(schedule, began);
            var end = start + Math.Max(0, task.Duration);
            if (task.ActualFinish is { } ended) end = Math.Max(start, DayOf(schedule, ended) + 1);
            (early[task.Id], finish[task.Id]) = (start, end);
        }

        var last = finish.Values.DefaultIfEmpty(0).Max();
        var (lateFinish, lateStart) = (new Dictionary<Guid, int>(), new Dictionary<Guid, int>());
        var followers = order.ToDictionary(task => task.Id, _ => new List<(Guid, int)>());
        foreach (var task in order)
        foreach (var link in task.Predecessors.Where(link => followers.ContainsKey(link.Predecessor)))
            followers[link.Predecessor].Add((task.Id, link.Lag));

        foreach (var task in Enumerable.Reverse(order))
        {
            var latest = followers[task.Id].Count == 0 ? last : followers[task.Id].Min(next => lateStart[next.Item1] - next.Item2);
            lateFinish[task.Id] = latest;
            lateStart[task.Id] = latest - (finish[task.Id] - early[task.Id]);
        }

        return order.Select(task =>
        {
            var start = DateOf(schedule, early[task.Id]);
            var end = finish[task.Id] > early[task.Id] ? DateOf(schedule, finish[task.Id] - 1) : start;
            return new TaskTimes(task, start, end, early[task.Id], finish[task.Id], lateStart[task.Id], lateFinish[task.Id]);
        }).ToList();
    }

    /// <summary>The tasks so each comes after what it waits for - a loop of links broken where it closes.</summary>
    private static List<ConstructionTask> Ordered(ConstructionSchedule schedule)
    {
        var byId = schedule.Tasks.ToDictionary(task => task.Id);
        var (done, visiting, order) = (new HashSet<Guid>(), new HashSet<Guid>(), new List<ConstructionTask>());
        void Visit(ConstructionTask task)
        {
            if (done.Contains(task.Id) || !visiting.Add(task.Id)) return;
            foreach (var link in task.Predecessors)
                if (byId.TryGetValue(link.Predecessor, out var before)) Visit(before);
            visiting.Remove(task.Id);
            done.Add(task.Id);
            order.Add(task);
        }

        foreach (var task in schedule.Tasks) Visit(task);
        return order;
    }

    /// <summary>When the programme finishes: its last task's finish.</summary>
    public static DateOnly Finish(ConstructionSchedule schedule) =>
        Time(schedule).Select(times => times.Finish).DefaultIfEmpty(FirstDay(schedule)).Max();

    /// <summary>Takes the programme as it now stands as the baseline progress is measured against.</summary>
    public static void SaveBaseline(ConstructionSchedule schedule)
    {
        foreach (var times in Time(schedule))
            (times.Task.BaselineStart, times.Task.BaselineFinish) = (times.Start, times.Finish);
    }

    // ---- what a task builds ---------------------------------------------------------

    /// <summary>The elements a task is linked to: those picked, and those its rule takes.</summary>
    public static IReadOnlyList<Element> Elements(BimDocument document, ConstructionTask task)
    {
        var found = new Dictionary<Guid, Element>();
        foreach (var id in task.ElementIds)
            if (document.FindElement(id) is { } element) found[id] = element;

        if (task.Categories.Count > 0)
        {
            var categories = task.Categories.ToHashSet();
            foreach (var element in document.Elements)
            {
                if (!categories.Contains(element.Category)) continue;
                if (task.LevelId is { } level && element.LevelId != level) continue;
                if (task.Phase is { } phase && (element.PhaseCreated != phase || phase == DesignPhase.Existing && element.PhaseDemolished is null)) continue;
                if (task.Phase is null && element.PhaseCreated != DesignPhase.New) continue;
                if (task.Walls != WallSide.Any && !SideOf(document, element, task.Walls)) continue;
                found[element.Id] = element;
            }
        }

        return found.Values.ToList();
    }

    /// <summary>Whether an element is on the side asked: a wall by its type's function, a door or window by its wall's.</summary>
    private static bool SideOf(BimDocument document, Element element, WallSide side)
    {
        var wall = element as Wall ?? (element is Opening opening ? document.Find<Wall>(opening.HostWallId) : null);
        if (wall is null) return side == WallSide.Interior;
        var exterior = document.IsCurtainWall(wall)
            ? document.FindType<CurtainWallType>(wall.TypeId)?.Function == WallFunction.Exterior
            : document.GetWallType(wall)?.Function is WallFunction.Exterior or WallFunction.Foundation or WallFunction.Retaining;
        return exterior == (side == WallSide.Exterior);
    }

    /// <summary>Where each element is on a date - by the planned programme, its baseline where it has one; or by what has actually happened.</summary>
    public static Dictionary<Guid, (BuildState State, ConstructionTask Task)> StateOn(BimDocument document, DateOnly date, bool actual = false)
    {
        var schedule = document.Construction;
        var states = new Dictionary<Guid, (BuildState State, ConstructionTask Task)>();
        var times = Time(schedule).ToDictionary(timed => timed.Task.Id);

        (DateOnly Start, DateOnly Finish)? When(ConstructionTask task)
        {
            if (actual)
            {
                if (task.ActualStart is not { } began) return null;
                return (began, task.ActualFinish ?? DateOnly.MaxValue);
            }

            if (task.BaselineStart is { } planned && task.BaselineFinish is { } done) return (planned, done);
            return times.TryGetValue(task.Id, out var timed) ? (timed.Start, timed.Finish) : null;
        }

        // Each element's tasks in the order they start: the last begun says where it is.
        var tasksOf = new Dictionary<Guid, List<(ConstructionTask Task, DateOnly Start, DateOnly Finish)>>();
        foreach (var task in schedule.Tasks)
        {
            var when = actual ? When(task) ?? (DateOnly.MaxValue, DateOnly.MaxValue) : When(task);
            if (when is not var (start, finish)) continue;
            foreach (var element in Elements(document, task))
                (tasksOf.TryGetValue(element.Id, out var list) ? list : tasksOf[element.Id] = new()).Add((task, start, finish));
        }

        foreach (var (id, list) in tasksOf)
        {
            var ordered = list.OrderBy(entry => entry.Start).ToList();
            var current = ordered.LastOrDefault(entry => entry.Start <= date);
            if (current.Task is null)
            {
                var first = ordered[0];
                states[id] = (first.Task.Kind == TaskKind.Demolish ? BuildState.Built : BuildState.NotStarted, first.Task);
                continue;
            }

            var running = date <= current.Finish;
            states[id] = (current.Task.Kind, running) switch
            {
                (TaskKind.Construct, true) => (BuildState.InProgress, current.Task),
                (TaskKind.Construct, false) => (BuildState.Built, current.Task),
                (TaskKind.Demolish, true) => (BuildState.Demolishing, current.Task),
                (TaskKind.Demolish, false) => (BuildState.Demolished, current.Task),
                (TaskKind.Temporary, true) => (BuildState.Temporary, current.Task),
                _ => (BuildState.TemporaryGone, current.Task)
            };
        }

        return states;
    }

    // ---- a programme from the model -------------------------------------------------

    private sealed record Trade(string Name, string Output, BuiltInCategory[] Categories, WallSide Walls = WallSide.Any);

    /// <summary>The services' first fix: the pipes and ducts run before the ceilings close them in.</summary>
    private static readonly BuiltInCategory[] FirstFix = { BuiltInCategory.Pipes, BuiltInCategory.Ducts };

    /// <summary>The services' second fix: the fittings, equipment and terminals put on after the ceilings.</summary>
    private static readonly BuiltInCategory[] SecondFix =
    {
        BuiltInCategory.PlumbingFixtures, BuiltInCategory.MechanicalEquipment, BuiltInCategory.ElectricalEquipment, BuiltInCategory.ElectricalFixtures,
        BuiltInCategory.LightingFixtures, BuiltInCategory.LightingDevices, BuiltInCategory.FireAlarmDevices, BuiltInCategory.AirTerminals, BuiltInCategory.Sprinklers
    };

    private static readonly BuiltInCategory[] Fittings =
    {
        BuiltInCategory.Furniture, BuiltInCategory.Casework, BuiltInCategory.SpecialtyEquipment, BuiltInCategory.GenericModels
    };

    /// <summary>How much of an element a trade measures it by: its area, its length, or one.</summary>
    private static double Quantity(BimDocument document, Element element, string output) =>
        output.EndsWith("(m²)", StringComparison.Ordinal) ? CostPlanning.Measure(document, element, CostUnit.Area) / 1e6
        : output.EndsWith("(m)", StringComparison.Ordinal) ? CostPlanning.Measure(document, element, CostUnit.Length) / 1000
        : 1;

    /// <summary>How long a task takes for what it builds, at its trade's output: whole days, at least one.</summary>
    public static int DurationFor(BimDocument document, ConstructionTask task, string output)
    {
        var rate = document.Construction.Outputs.TryGetValue(output, out var given) && given > 0 ? given : DefaultOutputs.GetValueOrDefault(output, 10);
        var amount = Elements(document, task).Sum(element => Quantity(document, element, output));
        return Math.Max(1, (int)Math.Ceiling(amount / rate - 1e-9));
    }

    /// <summary>
    /// A programme made from the model, storey by storey from the bottom, as a building goes up:
    /// what is to be taken down first; footings and the ground slab; on each storey its walls and
    /// frame, then the floor over them; the roof on the top storey, its edges after; windows,
    /// outside doors and curtain walls on each storey once its walls are up; inside, once the roof
    /// is on, partitions, stairs, inside doors, ceilings and railings, then the fittings and
    /// services; outside works last. Each task timed by its trade's output.
    /// </summary>
    public static void Generate(BimDocument document, DateOnly start)
    {
        var schedule = document.Construction;
        schedule.Tasks.Clear();
        schedule.Start = start;

        var levels = document.Levels.OrderBy(level => level.Elevation).ToList();
        var made = new List<(ConstructionTask Task, string Output)>();
        ConstructionTask? Add(string name, string output, IEnumerable<BuiltInCategory> categories, Guid? level, WallSide walls, IEnumerable<ConstructionTask?> after, TaskKind kind = TaskKind.Construct, DesignPhase? phase = null)
        {
            var task = new ConstructionTask { Name = name, Kind = kind, LevelId = level, Walls = walls, Phase = phase };
            task.Categories.AddRange(categories);
            if (Elements(document, task).Count == 0) return null;
            foreach (var before in after.OfType<ConstructionTask>().Distinct()) task.Predecessors.Add(new TaskLink(before.Id));
            schedule.Tasks.Add(task);
            made.Add((task, output));
            return task;
        }

        var solid = Enum.GetValues<BuiltInCategory>().Where(ClashDetective.Solidly).ToArray();
        var demolish = Add("Demolition", "Demolition (m²)", solid, null, WallSide.Any, Array.Empty<ConstructionTask>(), TaskKind.Demolish, DesignPhase.Existing);
        var temporary = Add("Temporary works", "External works (nr)", solid, null, WallSide.Any, new[] { demolish }, TaskKind.Temporary, DesignPhase.Temporary);

        ConstructionTask? below = null, floorHere = null, roof = null;
        var envelope = new List<ConstructionTask?>();
        var levelTasks = new List<(Guid Level, string Name)>();
        for (var i = 0; i < levels.Count; i++)
        {
            var level = levels[i];
            if (i == 0)
            {
                var footings = Add($"{level.Name} - Footings", "Footings (m)", new[] { BuiltInCategory.StructuralFoundations }, null, WallSide.Any, new[] { demolish });
                floorHere = Add($"{level.Name} - Ground slab", "Ground slab (m²)", new[] { BuiltInCategory.Floors, BuiltInCategory.Pads }, level.Id, WallSide.Any, new[] { footings, demolish }) ?? footings;
            }
            else
                floorHere = Add($"{level.Name} - Floor", "Upper floors (m²)", new[] { BuiltInCategory.Floors }, level.Id, WallSide.Any, new[] { below }) ?? below;

            var frame = Add($"{level.Name} - Columns and beams", "Columns and beams (nr)",
                new[] { BuiltInCategory.StructuralColumns, BuiltInCategory.Columns, BuiltInCategory.StructuralFraming }, level.Id, WallSide.Any, new[] { floorHere });
            var outside = Add($"{level.Name} - External walls", "External walls (m²)", new[] { BuiltInCategory.Walls }, level.Id, WallSide.Exterior, new[] { floorHere });
            var structure = new[] { frame, outside };
            below = structure.FirstOrDefault(task => task is not null) is null ? floorHere : null;
            below ??= Join($"{level.Name} - Structure complete", structure);

            envelope.Add(Add($"{level.Name} - Windows and external doors", "Windows and doors (nr)", new[] { BuiltInCategory.Windows, BuiltInCategory.Doors }, level.Id, WallSide.Exterior, new[] { outside }));
            levelTasks.Add((level.Id, level.Name));
        }

        ConstructionTask? Join(string name, IEnumerable<ConstructionTask?> tasks)
        {
            var real = tasks.OfType<ConstructionTask>().ToList();
            if (real.Count <= 1) return real.FirstOrDefault();
            var milestone = new ConstructionTask { Name = name, Duration = 0 };
            foreach (var task in real) milestone.Predecessors.Add(new TaskLink(task.Id));
            schedule.Tasks.Add(milestone);
            return milestone;
        }

        var top = levels.LastOrDefault();
        roof = Add("Roof", "Roofs (m²)", new[] { BuiltInCategory.Roofs }, null, WallSide.Any, new[] { below });
        var edges = Add("Roof edges, rainwater and chimneys", "Roof edges (m)",
            new[] { BuiltInCategory.Fascias, BuiltInCategory.Gutters, BuiltInCategory.RoofSoffits, BuiltInCategory.Eaves, BuiltInCategory.Downpipes, BuiltInCategory.RoofDrains, BuiltInCategory.RoofWindows, BuiltInCategory.Chimneys },
            null, WallSide.Any, new[] { roof ?? below });
        var weathertight = roof ?? below;

        // Curtain walls hang from the floors: once the structure is up.
        var curtain = new ConstructionTask { Name = "Curtain walls", LevelId = null };
        var curtainWalls = document.Walls.Where(document.IsCurtainWall).Select(wall => wall.Id).ToList();
        if (curtainWalls.Count > 0)
        {
            curtain.ElementIds.AddRange(curtainWalls);
            curtain.Predecessors.Add(new TaskLink((below ?? floorHere)!.Id));
            schedule.Tasks.Add(curtain);
            made.Add((curtain, "Curtain walls (m²)"));
        }

        var fitOut = new List<ConstructionTask?>();
        foreach (var (level, name) in levelTasks)
        {
            var inside = Add($"{name} - Internal walls and partitions", "Internal walls (m²)", new[] { BuiltInCategory.Walls }, level, WallSide.Interior, new[] { weathertight });
            var stairs = Add($"{name} - Stairs and ramps", "Stairs and ramps (nr)", new[] { BuiltInCategory.Stairs, BuiltInCategory.Ramps }, level, WallSide.Any, new[] { weathertight });
            var doors = Add($"{name} - Internal doors", "Windows and doors (nr)", new[] { BuiltInCategory.Doors }, level, WallSide.Interior, new[] { inside ?? weathertight });
            var firstFix = Add($"{name} - Services first fix", "Pipes and ducts (m)", FirstFix, level, WallSide.Any, new[] { inside ?? weathertight });
            var ceilings = Add($"{name} - Ceilings", "Ceilings (m²)", new[] { BuiltInCategory.Ceilings }, level, WallSide.Any, new[] { firstFix ?? inside ?? weathertight });
            var railings = Add($"{name} - Railings", "Railings (m)", new[] { BuiltInCategory.Railings }, level, WallSide.Any, new[] { stairs ?? weathertight });
            var secondFix = Add($"{name} - Services second fix", "Fittings and services (nr)", SecondFix, level, WallSide.Any,
                new[] { ceilings ?? firstFix ?? inside ?? weathertight, doors });
            fitOut.Add(secondFix);
            fitOut.Add(Add($"{name} - Fittings and furniture", "Fittings and services (nr)", Fittings, level, WallSide.Any,
                new[] { secondFix ?? ceilings ?? inside ?? weathertight, doors }));
            _ = railings;
        }

        Add("External works", "External works (nr)", new[] { BuiltInCategory.Parking, BuiltInCategory.Planting, BuiltInCategory.Topography, BuiltInCategory.SlabEdges },
            null, WallSide.Any, new[] { edges ?? weathertight }.Concat(envelope));
        _ = temporary;

        foreach (var (task, output) in made) task.Duration = DurationFor(document, task, output);
    }
}

/// <summary>
/// The programme checked against what holds what up: something built before what it stands on
/// is finished - a wall going up on a slab not yet cast, a floor before its walls, a roof before
/// the walls under it - is a clash in time.
/// </summary>
public static class ConstructionSequence
{
    private static readonly HashSet<BuiltInCategory> Bearing = new()
    {
        BuiltInCategory.Floors, BuiltInCategory.Walls, BuiltInCategory.Columns, BuiltInCategory.StructuralColumns,
        BuiltInCategory.StructuralFraming, BuiltInCategory.StructuralFoundations, BuiltInCategory.Pads, BuiltInCategory.Stairs
    };

    internal static IEnumerable<(Guid A, Guid B, Point3D Point, double Distance, string Said)> Clashes(BimDocument document, ClashTest test)
    {
        var states = new Dictionary<Guid, TaskTimes>();
        foreach (var times in ConstructionPlanning.Time(document.Construction).Where(timed => timed.Task.Kind == TaskKind.Construct))
        foreach (var element in ConstructionPlanning.Elements(document, times.Task))
            states.TryAdd(element.Id, times);
        if (states.Count == 0) yield break;

        var solids = ClashDetective.Solids(document);
        var setA = test.SelectionA.ToHashSet();
        var setB = test.SelectionB.ToHashSet();
        foreach (var (id, times) in states)
        {
            if (document.FindElement(id) is not { } element || (setA.Count > 0 && !setA.Contains(element.Category)) || !solids.TryGetValue(id, out var solid)) continue;

            // What it stands on: a bearing element whose top is at its foot, under it in plan.
            foreach (var (otherId, other) in solids)
            {
                if (otherId == id || !states.TryGetValue(otherId, out var support) || document.FindElement(otherId) is not { } under) continue;
                if (!Bearing.Contains(under.Category) || (setB.Count > 0 && !setB.Contains(under.Category))) continue;
                if (Math.Abs(other.Max.Z - solid.Min.Z) > 50) continue;
                var (dx, dy) = (Math.Min(solid.Max.X, other.Max.X) - Math.Max(solid.Min.X, other.Min.X), Math.Min(solid.Max.Y, other.Max.Y) - Math.Max(solid.Min.Y, other.Min.Y));
                if (dx <= 1 || dy <= 1) continue;
                if (times.EarlyStart >= support.EarlyFinish) continue;

                var early = support.EarlyFinish - times.EarlyStart;
                var at = new Point3D((Math.Max(solid.Min.X, other.Min.X) + Math.Min(solid.Max.X, other.Max.X)) / 2,
                    (Math.Max(solid.Min.Y, other.Min.Y) + Math.Min(solid.Max.Y, other.Max.Y)) / 2, solid.Min.Z);
                yield return (id, otherId, at, -early,
                    $"{ClashDetective.NameOf(document, id)} starts on {times.Start:d MMM yyyy} ({times.Task.Name}), {early} working day{(early == 1 ? "" : "s")} before {ClashDetective.NameOf(document, otherId)} it stands on is finished ({support.Task.Name}, {support.Finish:d MMM yyyy}).");
            }
        }
    }
}
