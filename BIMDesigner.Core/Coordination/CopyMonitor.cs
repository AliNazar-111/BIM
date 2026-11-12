using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;

namespace BIMDesigner.Core.Coordination;

/// <summary>What has become of a level, gridline, column or wall of a linked model one of this project's follows.</summary>
public enum MonitorChange
{
    /// <summary>It has moved: a level up or down, a gridline across or along.</summary>
    Moved,

    /// <summary>It has been renamed.</summary>
    Renamed,

    /// <summary>A column's or wall's bottom or top has moved up or down.</summary>
    Resized,

    /// <summary>A column or wall is of another type.</summary>
    Retyped,

    /// <summary>It is no longer in the linked model.</summary>
    Gone,

    /// <summary>The link it is in has been taken out of this project.</summary>
    LinkGone
}

/// <summary>A change found in what a level or gridline follows: this project's datum, its link, what has become of it and what is said.</summary>
public sealed record MonitorFinding(object Datum, ModelLink? Link, MonitorChange Change, string Said)
{
    public string Name => Datum switch
    {
        Level level => level.Name,
        Grid grid => $"Grid {grid.Name}",
        Column column => string.IsNullOrWhiteSpace(column.Mark) ? "Column" : $"Column {column.Mark}",
        Wall wall => string.IsNullOrWhiteSpace(wall.Mark) ? "Wall" : $"Wall {wall.Mark}",
        Floor floor => string.IsNullOrWhiteSpace(floor.Mark) ? "Floor" : $"Floor {floor.Mark}",
        Ceiling ceiling => string.IsNullOrWhiteSpace(ceiling.Mark) ? "Ceiling" : $"Ceiling {ceiling.Mark}",
        Component fixture => string.IsNullOrWhiteSpace(fixture.Mark) ? "Fixture" : $"Fixture {fixture.Mark}",
        Door door => string.IsNullOrWhiteSpace(door.Mark) ? "Door" : $"Door {door.Mark}",
        Window window => string.IsNullOrWhiteSpace(window.Mark) ? "Window" : $"Window {window.Mark}",
        _ => "?"
    };
}

/// <summary>
/// Copy/monitor: the levels and gridlines of a linked model - the structural engineer's grid, the
/// architect's storeys - brought into this project where the link puts them, or paired with this
/// project's own already there, each then following its original. Where the linked model is issued
/// again and one has moved, been renamed or gone, the Coordination Review says so, and each change
/// is moved to match, accepted as it now is, or no longer followed - one step to undo. Its columns
/// and walls are copied and followed as well, where asked.
/// </summary>
public static partial class CopyMonitor
{
    /// <summary>How far apart a datum and its original may be and still be the same place, mm.</summary>
    public const double Tolerance = 0.5;

    /// <summary>A linked model's level as it stands in this project: its elevation, moved up with its link.</summary>
    public static double ElevationOf(BimDocument host, ModelLink link, BimDocument linked, Level level) =>
        ModelLinks.Placement(host, link, linked)(new Point3D(0, 0, level.Elevation)).Z;

    /// <summary>A linked model's gridline as it stands in this project: its ends, put where the link puts them.</summary>
    public static (Point2D Start, Point2D End) LineOf(BimDocument host, ModelLink link, BimDocument linked, Grid grid)
    {
        var place = ModelLinks.Placement(host, link, linked);
        var (a, b) = (place(new Point3D(grid.Start.X, grid.Start.Y, 0)), place(new Point3D(grid.End.X, grid.End.Y, 0)));
        return (new Point2D(a.X, a.Y), new Point2D(b.X, b.Y));
    }

    private static bool SameLine((Point2D Start, Point2D End) a, (Point2D Start, Point2D End) b) =>
        (a.Start.DistanceTo(b.Start) <= Tolerance && a.End.DistanceTo(b.End) <= Tolerance) ||
        (a.Start.DistanceTo(b.End) <= Tolerance && a.End.DistanceTo(b.Start) <= Tolerance);

    /// <summary>
    /// A link's levels and gridlines - those asked for, or all - and its columns and walls where asked,
    /// copied into this project where the link puts them, each following its original; one already here
    /// at the same place is paired with it instead, not copied again. One step to undo, and what was
    /// done said; null where there is nothing to do.
    /// </summary>
    public static (IUndoableCommand? Command, string Said) Copy(BimDocument host, ModelLink link, IReadOnlyCollection<Guid>? levels = null, IReadOnlyCollection<Guid>? grids = null,
        CopyMonitorKinds kinds = CopyMonitorKinds.Datums)
    {
        if (ModelLinks.Of(link) is not { Document: { } linked })
            return (null, $"{link.Title} is not loaded: load it to copy from it.");

        var steps = new List<IUndoableCommand>();
        var (copied, paired, passed) = (new List<string>(), new List<string>(), new List<string>());
        var levelFor = new Dictionary<Guid, Level>();
        var takenLevels = host.Levels.Select(level => level.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var takenGrids = host.All<Grid>().Select(grid => grid.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var source in linked.Levels.Where(level => kinds.HasFlag(CopyMonitorKinds.Levels) && (levels is null || levels.Contains(level.Id))))
        {
            var elevation = ElevationOf(host, link, linked, source);
            var monitor = new DatumMonitor(link.Id, source.Id, source.Name, elevation, default, default);
            if (host.Levels.FirstOrDefault(level => level.Monitor is null && Math.Abs(level.Elevation - elevation) <= Tolerance) is { } here)
            {
                if (here.Monitor is not null) continue;
                steps.Add(Monitoring(here, monitor));
                paired.Add($"{here.Name} with {source.Name}");
                levelFor[source.Id] = here;
                continue;
            }

            if (host.Levels.Any(level => level.Monitor is { } already && already.LinkId == link.Id && (already.SourceId == source.Id || already.SourceName == source.Name))) continue;
            var name = Unique(source.Name, takenLevels);
            var made = new Level { Name = name, Elevation = elevation, Monitor = monitor };
            steps.Add(new AddLevelCommand(host, made));
            levelFor[source.Id] = made;
            copied.Add(name);
        }

        foreach (var source in linked.All<Grid>().Where(grid => kinds.HasFlag(CopyMonitorKinds.Grids) && (grids is null || grids.Contains(grid.Id))))
        {
            var line = LineOf(host, link, linked, source);
            var monitor = new DatumMonitor(link.Id, source.Id, source.Name, 0, line.Start, line.End);
            if (host.All<Grid>().Any(grid => grid.Monitor is { } already && already.LinkId == link.Id && (already.SourceId == source.Id || already.SourceName == source.Name))) continue;
            if (host.All<Grid>().FirstOrDefault(grid => grid.Monitor is null && SameLine((grid.Start, grid.End), line)) is { } here)
            {
                steps.Add(Monitoring(here, monitor));
                paired.Add($"grid {here.Name} with {source.Name}");
                continue;
            }

            var name = Unique(source.Name, takenGrids);
            var grid = new Grid { Name = name, Start = line.Start, End = line.End, LevelId = host.Levels.FirstOrDefault()?.Id ?? Guid.Empty, Monitor = monitor };
            steps.Add(new AddElementCommand(host, grid, "Copy Grid"));
            copied.Add($"grid {name}");
        }

        if ((kinds & (CopyMonitorKinds.Columns | CopyMonitorKinds.Walls | CopyMonitorKinds.Floors | CopyMonitorKinds.Openings | CopyMonitorKinds.Ceilings | CopyMonitorKinds.Fixtures)) != 0)
            CopyElements(host, link, linked, kinds, levelFor, steps, copied, paired, passed);

        var left = passed.GroupBy(what => what).Select(what => $"{what.Count()} {(what.Count() == 1 ? what.Key : Plural(what.Key))}").ToList();
        var what = string.Join(", ", new[] { kinds.HasFlag(CopyMonitorKinds.Levels) ? "levels" : null, kinds.HasFlag(CopyMonitorKinds.Grids) ? "gridlines" : null,
            kinds.HasFlag(CopyMonitorKinds.Columns) ? "columns" : null, kinds.HasFlag(CopyMonitorKinds.Walls) ? "walls" : null,
            kinds.HasFlag(CopyMonitorKinds.Floors) ? "floors" : null, kinds.HasFlag(CopyMonitorKinds.Ceilings) ? "ceilings" : null,
            kinds.HasFlag(CopyMonitorKinds.Openings) ? "doors and windows" : null, kinds.HasFlag(CopyMonitorKinds.Fixtures) ? "fixtures" : null }.OfType<string>());
        if (steps.Count == 0) return (null, $"Nothing more to copy from {link.Title}: its {what} are followed already{(left.Count > 0 ? $" - but for {string.Join(", ", left)}, which cannot be followed" : string.Empty)}.");
        var said = $"From {link.Title}: " + string.Join("; ", new[]
        {
            copied.Count > 0 ? $"copied {string.Join(", ", copied)}" : null,
            paired.Count > 0 ? $"paired {string.Join(", ", paired)}" : null,
            left.Count > 0 ? $"left {string.Join(", ", left)}, which cannot be followed" : null
        }.OfType<string>()) + ". Each now follows its original: the Coordination Review says when one changes.";
        return (new CompositeCommand("Copy/Monitor", steps), said);
    }

    private static string Unique(string name, HashSet<string> taken)
    {
        var unique = string.IsNullOrWhiteSpace(name) ? "Level" : name.Trim();
        for (var i = 2; taken.Contains(unique); i++) unique = $"{name.Trim()} ({i})";
        taken.Add(unique);
        return unique;
    }

    /// <summary>A datum set following an original, or no longer, as one step.</summary>
    private static IUndoableCommand Monitoring(object datum, DatumMonitor? monitor)
    {
        var before = MonitorOf(datum);
        return new DelegateCommand(monitor is null ? "Stop Monitoring" : "Monitor", () => SetMonitor(datum, monitor), () => SetMonitor(datum, before));
    }

    private static DatumMonitor? MonitorOf(object datum) => datum switch { Level level => level.Monitor, Grid grid => grid.Monitor, _ => null };

    private static void SetMonitor(object datum, DatumMonitor? monitor)
    {
        if (datum is Level level) level.Monitor = monitor;
        else if (datum is Grid grid) grid.Monitor = monitor;
    }

    /// <summary>
    /// What has changed in the linked models since each datum, column or wall following one was last
    /// agreed: an original moved, renamed, its bottom or top moved, of another type or gone, or its
    /// link taken out. A link not loaded says nothing.
    /// </summary>
    public static IReadOnlyList<MonitorFinding> Review(BimDocument host)
    {
        var findings = new List<MonitorFinding>();
        var monitored = host.Levels.Where(level => level.Monitor is not null).Select(level => (Datum: (object)level, Monitor: level.Monitor!))
            .Concat(host.All<Grid>().Where(grid => grid.Monitor is not null).Select(grid => (Datum: (object)grid, Monitor: grid.Monitor!)));

        foreach (var (datum, monitor) in monitored)
        {
            var name = datum is Level level ? level.Name : $"Grid {((Grid)datum).Name}";
            if (host.Find<ModelLink>(monitor.LinkId) is not { } link)
            {
                findings.Add(new MonitorFinding(datum, null, MonitorChange.LinkGone, $"{name} follows {monitor.SourceName} in a linked model since taken out."));
                continue;
            }

            if (ModelLinks.Of(link) is not { Document: { } linked }) continue;
            switch (datum)
            {
                case Level:
                    if (SourceLevel(linked, monitor) is not { } sourceLevel)
                    {
                        findings.Add(new MonitorFinding(datum, link, MonitorChange.Gone, $"{name} follows level {monitor.SourceName} of {link.Title}, which is no longer in it."));
                        break;
                    }

                    var elevation = ElevationOf(host, link, linked, sourceLevel);
                    if (Math.Abs(elevation - monitor.Elevation) > Tolerance)
                        findings.Add(new MonitorFinding(datum, link, MonitorChange.Moved,
                            $"Level {sourceLevel.Name} of {link.Title} has moved {(elevation > monitor.Elevation ? "up" : "down")} {Units.FormatLength(Math.Abs(elevation - monitor.Elevation))}, to {Units.FormatLength(elevation)}: {name} follows it."));
                    if (sourceLevel.Name != monitor.SourceName)
                        findings.Add(new MonitorFinding(datum, link, MonitorChange.Renamed, $"Level {monitor.SourceName} of {link.Title} is now called {sourceLevel.Name}: {name} follows it."));
                    break;

                case Grid:
                    if (SourceGrid(linked, monitor) is not { } sourceGrid)
                    {
                        findings.Add(new MonitorFinding(datum, link, MonitorChange.Gone, $"{name} follows gridline {monitor.SourceName} of {link.Title}, which is no longer in it."));
                        break;
                    }

                    var line = LineOf(host, link, linked, sourceGrid);
                    if (!SameLine(line, (monitor.Start, monitor.End)))
                        findings.Add(new MonitorFinding(datum, link, MonitorChange.Moved,
                            $"Gridline {sourceGrid.Name} of {link.Title} has moved {Units.FormatLength(Shift((monitor.Start, monitor.End), line))}: {name} follows it."));
                    if (sourceGrid.Name != monitor.SourceName)
                        findings.Add(new MonitorFinding(datum, link, MonitorChange.Renamed, $"Gridline {monitor.SourceName} of {link.Title} is now {sourceGrid.Name}: {name} follows it."));
                    break;
            }
        }

        findings.AddRange(ReviewElements(host));
        return findings;
    }

    /// <summary>
    /// The level of a linked model a datum follows: by its id - or, where a model written again from
    /// scratch has given its levels new ids, the one level there of the name it had.
    /// </summary>
    private static Level? SourceLevel(BimDocument linked, DatumMonitor monitor) =>
        linked.Levels.FirstOrDefault(each => each.Id == monitor.SourceId)
        ?? (linked.Levels.Where(each => each.Name == monitor.SourceName).ToList() is [var named] ? named : null);

    /// <summary>The gridline of a linked model a datum follows: by its id, or the one gridline there of the name it had.</summary>
    private static Grid? SourceGrid(BimDocument linked, DatumMonitor monitor) =>
        linked.Find<Grid>(monitor.SourceId)
        ?? (linked.All<Grid>().Where(each => each.Name == monitor.SourceName).ToList() is [var named] ? named : null);

    /// <summary>How far a line has moved: the further its ends have gone, whichever way round it was drawn.</summary>
    private static double Shift((Point2D Start, Point2D End) was, (Point2D Start, Point2D End) now) =>
        Math.Min(Math.Max(was.Start.DistanceTo(now.Start), was.End.DistanceTo(now.End)), Math.Max(was.Start.DistanceTo(now.End), was.End.DistanceTo(now.Start)));

    /// <summary>
    /// A change answered by bringing this project's datum to its original: moved to where it now is, or
    /// renamed as it now is - and agreed there. One step to undo; null for one gone, which only stops being followed.
    /// </summary>
    public static IUndoableCommand? Match(BimDocument host, MonitorFinding finding)
    {
        if (FollowsElements(finding.Datum)) return MatchElement(host, finding, (Element)finding.Datum);
        if (finding.Link is not { } link || ModelLinks.Of(link) is not { Document: { } linked } || MonitorOf(finding.Datum) is not { } monitor) return null;
        switch (finding.Datum)
        {
            case Level level when SourceLevel(linked, monitor) is { } source:
            {
                var elevation = ElevationOf(host, link, linked, source);
                var steps = new List<IUndoableCommand> { Monitoring(level, monitor with { SourceId = source.Id, Elevation = elevation, SourceName = source.Name }) };
                if (finding.Change == MonitorChange.Moved) steps.Insert(0, new MoveLevelCommand(host, level, elevation));
                if (finding.Change == MonitorChange.Renamed) steps.Insert(0, new RenameLevelCommand(level, source.Name));
                return new CompositeCommand(finding.Change == MonitorChange.Moved ? "Move Level to Match" : "Rename Level to Match", steps);
            }

            case Grid grid when SourceGrid(linked, monitor) is { } source:
            {
                var (start, end) = LineOf(host, link, linked, source);
                var (wasStart, wasEnd, wasName) = (grid.Start, grid.End, grid.Name);
                var (toStart, toEnd, toName) = finding.Change == MonitorChange.Moved ? (start, end, grid.Name) : (grid.Start, grid.End, source.Name);
                return new CompositeCommand(finding.Change == MonitorChange.Moved ? "Move Grid to Match" : "Rename Grid to Match", new IUndoableCommand[]
                {
                    new DelegateCommand("Match Grid", () => (grid.Start, grid.End, grid.Name) = (toStart, toEnd, toName), () => (grid.Start, grid.End, grid.Name) = (wasStart, wasEnd, wasName)),
                    Monitoring(grid, monitor with { SourceId = source.Id, Start = start, End = end, SourceName = source.Name })
                });
            }
        }

        return null;
    }

    /// <summary>A change accepted as it now is: this project's datum left where it is, its original agreed where it now stands. One step to undo.</summary>
    public static IUndoableCommand? Accept(BimDocument host, MonitorFinding finding)
    {
        if (FollowsElements(finding.Datum)) return AcceptElement(host, finding, (Element)finding.Datum);
        if (finding.Link is not { } link || ModelLinks.Of(link) is not { Document: { } linked } || MonitorOf(finding.Datum) is not { } monitor) return null;
        return finding.Datum switch
        {
            Level level when SourceLevel(linked, monitor) is { } source =>
                Monitoring(level, monitor with { SourceId = source.Id, Elevation = ElevationOf(host, link, linked, source), SourceName = source.Name }),
            Grid grid when SourceGrid(linked, monitor) is { } source && LineOf(host, link, linked, source) is var (start, end) =>
                Monitoring(grid, monitor with { SourceId = source.Id, Start = start, End = end, SourceName = source.Name }),
            _ => null
        };
    }

    /// <summary>A datum no longer following its original. One step to undo.</summary>
    public static IUndoableCommand Stop(MonitorFinding finding) => Stop(finding.Datum);

    /// <summary>A datum, column or wall no longer following its original, from its Properties. One step to undo.</summary>
    public static IUndoableCommand Stop(object datum) => FollowsElements(datum) ? Following((Element)datum, null) : Monitoring(datum, null);

    /// <summary>What a datum follows, as its Properties say it.</summary>
    public static string Said(BimDocument host, DatumMonitor? monitor) =>
        monitor is null ? "Nothing"
        : host.Find<ModelLink>(monitor.LinkId) is { } link ? $"{monitor.SourceName} in {link.Title}"
        : $"{monitor.SourceName} in a link since taken out";
}
