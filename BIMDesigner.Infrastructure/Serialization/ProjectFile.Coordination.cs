using BIMDesigner.Core.Coordination;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;

namespace BIMDesigner.Infrastructure.Serialization;

/// <summary>
/// What a project keeps for coordination: its clash tests and each clash as an issue, its
/// construction programme, and the additions its cost plan carries.
/// </summary>
public static partial class ProjectFile
{
    private static void WriteCoordination(BimDocument document, ProjectFileDto dto)
    {
        WriteIssues(document, dto);
        dto.ClashTests = document.ClashTests.Select(test => new ClashTestDto
        {
            Id = test.Id, Name = test.Name, Kind = test.Kind.ToString(), Tolerance = test.Tolerance, IgnoreHosted = test.IgnoreHosted, LastRun = test.LastRun,
            AllowInsulation = test.AllowInsulation ? true : null,
            SelectionA = test.SelectionA.Select(category => category.ToString()).ToList(),
            SelectionB = test.SelectionB.Select(category => category.ToString()).ToList(),
            Results = test.Results.Select(clash => new ClashDto
            {
                Id = clash.Id, Name = clash.Name, ElementA = clash.ElementA, ElementB = clash.ElementB,
                X = clash.Point.X, Y = clash.Point.Y, Z = clash.Point.Z, Distance = clash.Distance, Status = clash.Status.ToString(), Found = clash.Found,
                Description = clash.Description, GridLocation = clash.GridLocation, Level = clash.Level, AssignedTo = clash.AssignedTo,
                Priority = clash.Priority.ToString(), DueDate = clash.DueDate,
                Comments = clash.Comments.Select(comment => new ClashCommentDto { Date = comment.Date, Author = comment.Author, Text = comment.Text }).ToList()
            }).ToList()
        }).ToList();

        var schedule = document.Construction;
        dto.Construction = new ConstructionScheduleDto
        {
            Start = schedule.Start, StatusDate = schedule.StatusDate,
            WorkingDays = schedule.WorkingDays.Select(day => day.ToString()).ToList(),
            Holidays = schedule.Holidays.ToList(),
            Outputs = new Dictionary<string, double>(schedule.Outputs),
            Tasks = schedule.Tasks.Select(task => new ConstructionTaskDto
            {
                Id = task.Id, Name = task.Name, Kind = task.Kind.ToString(), Duration = task.Duration, StartNoEarlierThan = task.StartNoEarlierThan,
                Predecessors = task.Predecessors.Select(link => new TaskLinkDto { Predecessor = link.Predecessor, Lag = link.Lag }).ToList(),
                ElementIds = task.ElementIds.ToList(), LevelId = task.LevelId,
                Categories = task.Categories.Select(category => category.ToString()).ToList(),
                Walls = task.Walls.ToString(), Phase = task.Phase?.ToString(),
                BaselineStart = task.BaselineStart, BaselineFinish = task.BaselineFinish,
                ActualStart = task.ActualStart, ActualFinish = task.ActualFinish, PercentComplete = task.PercentComplete, ActualCost = task.ActualCost,
                Notes = task.Notes
            }).ToList()
        };

        var cost = document.CostSettings;
        dto.CostSettings = new CostSettingsDto
        {
            Preliminaries = cost.Preliminaries, OverheadsAndProfit = cost.OverheadsAndProfit, DesignFees = cost.DesignFees, OtherCosts = cost.OtherCosts,
            Risk = cost.Risk, Inflation = cost.Inflation, Tax = cost.Tax, Currency = cost.Currency
        };
    }

    private static void ReadCoordination(ProjectFileDto dto, BimDocument document)
    {
        ReadIssues(dto, document);
        static List<BuiltInCategory> Categories(IEnumerable<string>? names) =>
            (names ?? Enumerable.Empty<string>()).Select(name => Enum.TryParse<BuiltInCategory>(name, out var category) ? (BuiltInCategory?)category : null)
                .OfType<BuiltInCategory>().ToList();
        static double Finite(double value, double otherwise) => double.IsFinite(value) ? value : otherwise;

        // A project from before clash tests starts with the ones a new project has.
        if (dto.ClashTests is null) document.ClashTests.AddRange(ClashDetective.Defaults());
        else
            foreach (var saved in dto.ClashTests)
            {
                var test = new ClashTest
                {
                    Id = saved.Id, Name = saved.Name ?? "Clash test", Kind = ParseEnum(saved.Kind, ClashKind.Hard),
                    Tolerance = Math.Clamp(Finite(saved.Tolerance, 10), 0, 10000), IgnoreHosted = saved.IgnoreHosted, LastRun = saved.LastRun,
                    AllowInsulation = saved.AllowInsulation == true
                };
                test.SelectionA.AddRange(Categories(saved.SelectionA));
                test.SelectionB.AddRange(Categories(saved.SelectionB));
                foreach (var result in saved.Results ?? new List<ClashDto>())
                {
                    var clash = new Clash
                    {
                        Id = result.Id, Name = result.Name ?? string.Empty, ElementA = result.ElementA, ElementB = result.ElementB,
                        Point = new Core.Geometry.Point3D(Finite(result.X, 0), Finite(result.Y, 0), Finite(result.Z, 0)), Distance = Finite(result.Distance, 0),
                        Status = ParseEnum(result.Status, ClashStatus.New), Found = result.Found, Description = result.Description ?? string.Empty,
                        GridLocation = result.GridLocation ?? string.Empty, Level = result.Level ?? string.Empty, AssignedTo = result.AssignedTo ?? string.Empty,
                        Priority = ParseEnum(result.Priority, ClashPriority.Normal), DueDate = result.DueDate
                    };
                    clash.Comments.AddRange((result.Comments ?? new List<ClashCommentDto>()).Select(comment => new ClashComment(comment.Date, comment.Author ?? string.Empty, comment.Text ?? string.Empty)));
                    test.Results.Add(clash);
                }

                document.ClashTests.Add(test);
            }

        if (dto.Construction is { } construction)
        {
            var schedule = document.Construction;
            schedule.Start = construction.Start;
            schedule.StatusDate = construction.StatusDate;
            var days = (construction.WorkingDays ?? new List<string>()).Select(name => Enum.TryParse<DayOfWeek>(name, out var day) ? (DayOfWeek?)day : null).OfType<DayOfWeek>().Distinct().ToList();
            if (construction.WorkingDays is not null)
            {
                schedule.WorkingDays.Clear();
                schedule.WorkingDays.AddRange(days);
            }

            schedule.Holidays.AddRange(construction.Holidays ?? new List<DateOnly>());
            foreach (var (trade, output) in construction.Outputs ?? new Dictionary<string, double>())
                if (double.IsFinite(output) && output > 0) schedule.Outputs[trade] = output;

            foreach (var saved in construction.Tasks ?? new List<ConstructionTaskDto>())
            {
                var task = new ConstructionTask
                {
                    Id = saved.Id, Name = saved.Name ?? "Task", Kind = ParseEnum(saved.Kind, TaskKind.Construct), Duration = Math.Clamp(saved.Duration, 0, 10000),
                    StartNoEarlierThan = saved.StartNoEarlierThan, LevelId = saved.LevelId, Walls = ParseEnum(saved.Walls, WallSide.Any),
                    Phase = saved.Phase is null ? null : ParseEnum(saved.Phase, DesignPhase.New),
                    BaselineStart = saved.BaselineStart, BaselineFinish = saved.BaselineFinish, ActualStart = saved.ActualStart, ActualFinish = saved.ActualFinish,
                    PercentComplete = Math.Clamp(Finite(saved.PercentComplete, 0), 0, 100), ActualCost = saved.ActualCost, Notes = saved.Notes ?? string.Empty
                };
                task.Predecessors.AddRange((saved.Predecessors ?? new List<TaskLinkDto>()).Select(link => new TaskLink(link.Predecessor, link.Lag)));
                task.ElementIds.AddRange(saved.ElementIds ?? new List<Guid>());
                task.Categories.AddRange(Categories(saved.Categories));
                schedule.Tasks.Add(task);
            }
        }

        if (dto.CostSettings is { } cost)
        {
            var settings = document.CostSettings;
            static double Percent(double value, double otherwise) => double.IsFinite(value) && value is >= 0 and <= 500 ? value : otherwise;
            settings.Preliminaries = Percent(cost.Preliminaries, 12);
            settings.OverheadsAndProfit = Percent(cost.OverheadsAndProfit, 5);
            settings.DesignFees = Percent(cost.DesignFees, 0);
            settings.OtherCosts = cost.OtherCosts;
            settings.Risk = Percent(cost.Risk, 5);
            settings.Inflation = Percent(cost.Inflation, 0);
            settings.Tax = Percent(cost.Tax, 0);
            settings.Currency = cost.Currency ?? string.Empty;
        }
    }
}

internal sealed class ClashTestDto
{
    public Guid Id { get; set; }
    public string? Name { get; set; }
    public string? Kind { get; set; }
    public double Tolerance { get; set; } = 10;
    public bool IgnoreHosted { get; set; } = true;

    /// <summary>Whether pipes and ducts are taken with their insulation round them; none in a file from before.</summary>
    public bool? AllowInsulation { get; set; }
    public DateTime? LastRun { get; set; }
    public List<string>? SelectionA { get; set; }
    public List<string>? SelectionB { get; set; }
    public List<ClashDto>? Results { get; set; }
}

internal sealed class ClashDto
{
    public Guid Id { get; set; }
    public string? Name { get; set; }
    public Guid ElementA { get; set; }
    public Guid ElementB { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public double Distance { get; set; }
    public string? Status { get; set; }
    public DateTime Found { get; set; }
    public string? Description { get; set; }
    public string? GridLocation { get; set; }
    public string? Level { get; set; }
    public string? AssignedTo { get; set; }
    public string? Priority { get; set; }
    public DateOnly? DueDate { get; set; }
    public List<ClashCommentDto>? Comments { get; set; }
}

internal sealed class ClashCommentDto
{
    public DateTime Date { get; set; }
    public string? Author { get; set; }
    public string? Text { get; set; }
}

internal sealed class ConstructionScheduleDto
{
    public DateOnly Start { get; set; }
    public DateOnly? StatusDate { get; set; }
    public List<string>? WorkingDays { get; set; }
    public List<DateOnly>? Holidays { get; set; }
    public Dictionary<string, double>? Outputs { get; set; }
    public List<ConstructionTaskDto>? Tasks { get; set; }
}

internal sealed class ConstructionTaskDto
{
    public Guid Id { get; set; }
    public string? Name { get; set; }
    public string? Kind { get; set; }
    public int Duration { get; set; }
    public DateOnly? StartNoEarlierThan { get; set; }
    public List<TaskLinkDto>? Predecessors { get; set; }
    public List<Guid>? ElementIds { get; set; }
    public Guid? LevelId { get; set; }
    public List<string>? Categories { get; set; }
    public string? Walls { get; set; }
    public string? Phase { get; set; }
    public DateOnly? BaselineStart { get; set; }
    public DateOnly? BaselineFinish { get; set; }
    public DateOnly? ActualStart { get; set; }
    public DateOnly? ActualFinish { get; set; }
    public double PercentComplete { get; set; }
    public decimal ActualCost { get; set; }
    public string? Notes { get; set; }
}

internal sealed class TaskLinkDto
{
    public Guid Predecessor { get; set; }
    public int Lag { get; set; }
}

internal sealed class CostSettingsDto
{
    public double Preliminaries { get; set; } = 12;
    public double OverheadsAndProfit { get; set; } = 5;
    public double DesignFees { get; set; }
    public decimal OtherCosts { get; set; }
    public double Risk { get; set; } = 5;
    public double Inflation { get; set; }
    public double Tax { get; set; }
    public string? Currency { get; set; }
}
