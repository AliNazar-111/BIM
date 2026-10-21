using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Coordination;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Tests;

/// <summary>
/// The construction programme (4D): working days, the critical path, a programme made from the
/// model storey by storey, where each element is on a date, and building something before what
/// holds it up found as a clash in time.
/// </summary>
public class ConstructionTests
{
    /// <summary>A two-storey box, 10 m by 8 m: a ground slab, walls, a first floor, walls, a flat roof, a window and a desk.</summary>
    public static BimDocument House()
    {
        var document = BimDocument.CreateDefault();
        var (ground, first) = (document.Levels[0], document.Levels[1]);
        var outside = document.TypesOf<WallType>().First(type => type.Function == WallFunction.Exterior && type.Log is null).Id;
        Point2D[] corners = { new(0, 0), new(10000, 0), new(10000, 8000), new(0, 8000) };
        foreach (var level in new[] { ground, first })
        {
            var floor = new Floor { LevelId = level.Id, TypeId = document.TypesOf<FloorType>().First().Id };
            floor.SetBoundary(corners);
            document.Add(floor);
            for (var i = 0; i < 4; i++)
                document.Add(new Wall { Start = corners[i], End = corners[(i + 1) % 4], TypeId = outside, LevelId = level.Id, UnconnectedHeight = 3000 });
        }

        var roof = new Roof { TypeId = document.TypesOf<RoofType>().First(type => type.Name.Contains("Flat")).Id, LevelId = first.Id, HeightOffset = 3000 };
        roof.SetBoundary(corners);
        document.Add(roof);

        var front = document.Walls.First(wall => wall.LevelId == ground.Id);
        document.Add(new Window { TypeId = document.TypesOf<WindowType>().First().Id, LevelId = ground.Id, HostWallId = front.Id, DistanceAlongWall = 3000, SillHeight = 900 });
        var desk = document.TypesOf<ComponentType>().Single(type => type.Name.StartsWith("Desk - 1500"));
        document.Add(new Component { TypeId = desk.Id, TypeKind = desk.Kind, LevelId = ground.Id, Location = new Point2D(5000, 4000) });
        return document;
    }

    [Fact]
    public void WorkingDaysSkipWeekendsAndHolidays()
    {
        var schedule = new ConstructionSchedule { Start = new DateOnly(2026, 10, 24) };
        Assert.Equal(new DateOnly(2026, 10, 26), ConstructionPlanning.FirstDay(schedule));
        Assert.Equal(new DateOnly(2026, 11, 2), ConstructionPlanning.DateOf(schedule, 5));
        schedule.Holidays.Add(new DateOnly(2026, 10, 28));
        Assert.Equal(new DateOnly(2026, 11, 3), ConstructionPlanning.DateOf(schedule, 5));
        Assert.Equal(5, ConstructionPlanning.DayOf(schedule, new DateOnly(2026, 11, 3)));

        // A day off is taken as the working day after it.
        Assert.Equal(ConstructionPlanning.DayOf(schedule, new DateOnly(2026, 11, 2)), ConstructionPlanning.DayOf(schedule, new DateOnly(2026, 10, 31)));
    }

    [Fact]
    public void TheCriticalPathIsTheChainWithNoFloat()
    {
        var schedule = new ConstructionSchedule { Start = new DateOnly(2026, 11, 2) };
        ConstructionTask Task(string name, int days, params ConstructionTask[] after)
        {
            var task = new ConstructionTask { Name = name, Duration = days };
            task.Predecessors.AddRange(after.Select(before => new TaskLink(before.Id)));
            schedule.Tasks.Add(task);
            return task;
        }

        var a = Task("Footings", 5);
        var b = Task("Walls", 3, a);
        var c = Task("Drainage", 2, a);
        var d = Task("Roof", 1, b, c);
        var times = ConstructionPlanning.Time(schedule).ToDictionary(timed => timed.Task);

        Assert.Equal((0, 5), (times[a].EarlyStart, times[a].EarlyFinish));
        Assert.Equal((5, 8), (times[b].EarlyStart, times[b].EarlyFinish));
        Assert.Equal((8, 9), (times[d].EarlyStart, times[d].EarlyFinish));
        Assert.Equal(new[] { true, true, false, true }, new[] { a, b, c, d }.Select(task => times[task].Critical));
        Assert.Equal(1, times[c].TotalFloat);
        Assert.Equal(new DateOnly(2026, 11, 12), ConstructionPlanning.Finish(schedule));

        // A lag of two days on the roof's wait for the walls.
        d.Predecessors[0] = new TaskLink(b.Id, 2);
        Assert.Equal(10, ConstructionPlanning.Time(schedule).Single(timed => timed.Task == d).EarlyStart);

        // Started late: timed from when it actually started.
        a.ActualStart = new DateOnly(2026, 11, 4);
        Assert.Equal(new DateOnly(2026, 11, 4), ConstructionPlanning.Time(schedule).Single(timed => timed.Task == a).Start);
    }

    [Fact]
    public void AProgrammeIsMadeFromTheModelStoreyByStorey()
    {
        var document = House();
        ConstructionPlanning.Generate(document, new DateOnly(2026, 11, 2));
        var schedule = document.Construction;
        var names = schedule.Tasks.Select(task => task.Name).ToList();
        var ground = document.Levels[0].Name;
        var first = document.Levels[1].Name;
        Assert.Contains($"{ground} - Ground slab", names);
        Assert.Contains($"{ground} - External walls", names);
        Assert.Contains($"{first} - Floor", names);
        Assert.Contains("Roof", names);
        Assert.Contains($"{ground} - Fittings, furniture and services", names);

        var times = ConstructionPlanning.Time(schedule).ToDictionary(timed => timed.Task.Name);
        Assert.True(times[$"{ground} - Ground slab"].EarlyFinish <= times[$"{ground} - External walls"].EarlyStart);
        Assert.True(times[$"{ground} - External walls"].EarlyFinish <= times[$"{first} - Floor"].EarlyStart);
        Assert.True(times["Roof"].EarlyFinish <= times[$"{ground} - Fittings, furniture and services"].EarlyStart);

        // The ground floor's walls: 36 m of wall 3 m high, less the window, at 20 m² a day - 6 days.
        var walls = schedule.Tasks.Single(task => task.Name == $"{ground} - External walls");
        Assert.Equal(4, ConstructionPlanning.Elements(document, walls).Count);
        var area = ConstructionPlanning.Elements(document, walls).Sum(wall => CostPlanning.Measure(document, wall, CostUnit.Area)) / 1e6;
        Assert.Equal((int)Math.Ceiling(area / 20), walls.Duration);

        // On its first day only the footings and slab are going; at the end, all of it is built.
        var slab = document.Elements.OfType<Floor>().Single(floor => floor.LevelId == document.Levels[0].Id);
        var start = ConstructionPlanning.StateOn(document, times[$"{ground} - Ground slab"].Start);
        Assert.Equal(BuildState.InProgress, start[slab.Id].State);
        Assert.Equal(BuildState.NotStarted, start[walls.Categories.Count > 0 ? ConstructionPlanning.Elements(document, walls)[0].Id : Guid.Empty].State);
        var end = ConstructionPlanning.StateOn(document, ConstructionPlanning.Finish(document.Construction).AddDays(1));
        Assert.All(end.Values, state => Assert.Equal(BuildState.Built, state.State));
    }

    [Fact]
    public void BuildingSomethingBeforeWhatHoldsItUpIsAClashInTime()
    {
        var document = House();
        ConstructionPlanning.Generate(document, new DateOnly(2026, 11, 2));
        var test = new ClashTest { Name = "Sequence", Kind = ClashKind.Sequence };
        Assert.Empty(ClashDetective.Run(document, test, DateTime.Now));

        // The first floor's walls let start on day one, before the floor they stand on is laid.
        var walls = document.Construction.Tasks.Single(task => task.Name == $"{document.Levels[1].Name} - External walls");
        walls.Predecessors.Clear();
        var clashes = ClashDetective.Run(document, test, DateTime.Now);
        Assert.NotEmpty(clashes);
        Assert.All(clashes, clash => Assert.Contains("before", clash.Description));
        Assert.Contains(clashes, clash => document.FindElement(clash.ElementB) is Floor);
    }

    [Fact]
    public void ProgressIsMeasuredAgainstTheBaseline()
    {
        var document = House();
        ConstructionPlanning.Generate(document, new DateOnly(2026, 11, 2));
        ConstructionPlanning.SaveBaseline(document.Construction);
        var slab = document.Construction.Tasks.Single(task => task.Name.EndsWith("Ground slab"));
        Assert.Equal(new DateOnly(2026, 11, 2), slab.BaselineStart);

        // Actually started a week late: the plan has it going, what happened has not begun.
        var element = ConstructionPlanning.Elements(document, slab)[0];
        var planned = ConstructionPlanning.StateOn(document, slab.BaselineStart!.Value);
        var actual = ConstructionPlanning.StateOn(document, slab.BaselineStart!.Value, actual: true);
        Assert.Equal(BuildState.InProgress, planned[element.Id].State);
        Assert.Equal(BuildState.NotStarted, actual[element.Id].State);
        slab.ActualStart = new DateOnly(2026, 11, 9);
        Assert.Equal(BuildState.InProgress, ConstructionPlanning.StateOn(document, new DateOnly(2026, 11, 9), actual: true)[element.Id].State);
    }

    [Fact]
    public void TheProgrammeIsSaved()
    {
        var document = House();
        ConstructionPlanning.Generate(document, new DateOnly(2026, 11, 2));
        var schedule = document.Construction;
        schedule.Holidays.Add(new DateOnly(2026, 12, 25));
        schedule.Outputs["External walls (m²)"] = 30;
        var task = schedule.Tasks[2];
        task.PercentComplete = 40;
        task.ActualStart = new DateOnly(2026, 11, 3);
        task.ActualCost = 1234.5m;

        var path = Path.Combine(Path.GetTempPath(), $"bimtest-{Guid.NewGuid():N}{Infrastructure.Serialization.ProjectFile.Extension}");
        try
        {
            Infrastructure.Serialization.ProjectFile.Save(document, path);
            var again = Infrastructure.Serialization.ProjectFile.Load(path).Construction;
            Assert.Equal(schedule.Tasks.Count, again.Tasks.Count);
            Assert.Equal((schedule.Start, 30.0), (again.Start, again.Outputs["External walls (m²)"]));
            Assert.Contains(new DateOnly(2026, 12, 25), again.Holidays);
            var kept = again.Tasks[2];
            Assert.Equal((task.Name, task.Duration, 40.0, task.ActualStart, 1234.5m, task.Categories.Count, task.Predecessors.Count),
                (kept.Name, kept.Duration, kept.PercentComplete, kept.ActualStart, kept.ActualCost, kept.Categories.Count, kept.Predecessors.Count));
            Assert.Equal(ConstructionPlanning.Finish(schedule), ConstructionPlanning.Finish(again));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
