using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;
using BIMDesigner.Core.Parameters;
using BIMDesigner.Core.Views;

namespace BIMDesigner.Core.Coordination;

/// <summary>What Copy/Monitor brings from a linked model: its levels, its gridlines, its columns, its walls.</summary>
[Flags]
public enum CopyMonitorKinds
{
    None = 0,
    Levels = 1,
    Grids = 2,
    Columns = 4,
    Walls = 8,
    Datums = Levels | Grids
}

/// <summary>
/// The agreed state of a linked model's column or wall one of this project's follows, as the link puts
/// it in this project: a wall's line and its bow, a column's point and how it is turned; its bottom and
/// top; the name of its type; and its mark, by which it is found again in a model written afresh with new ids.
/// </summary>
public sealed record ElementMonitor(Guid LinkId, Guid SourceId, string SourceName, string TypeName, Point2D Start, Point2D End, double Rotation, double Bulge, double Bottom, double Top, string Mark = "");

public static class ElementMonitorParameters
{
    public static readonly ParameterDefinition Monitors = new("Monitors", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.IdentityData);
}

/// <summary>
/// Copy/monitor of a linked model's columns and walls - the architect's columns into the engineer's
/// model, their walls round the engineer's frame: each copied where the link puts it, onto this
/// project's storey following its own, or the one it stands at, of this project's type of the same
/// name - or the linked model's brought in with its materials, matched by name to this project's
/// own where it has them - or paired with one of this project's already standing there. Each then
/// follows its original: moved, its bottom or top moved, of another type, or gone, the Coordination
/// Review says so.
/// </summary>
public static partial class CopyMonitor
{
    /// <summary>A column or wall of a linked model as the link puts it in this project; none for one that cannot be followed - a curtain wall, an elliptical or spline wall.</summary>
    public static ElementMonitor? StateOf(BimDocument host, ModelLink link, BimDocument linked, Element element)
    {
        var place = ModelLinks.Placement(host, link, linked);
        Point2D At(Point2D point) => place(new Point3D(point.X, point.Y, 0)) is var moved ? new Point2D(moved.X, moved.Y) : point;
        var up = place(new Point3D(0, 0, 0)).Z;
        switch (element)
        {
            case Column column when linked.FindType<ColumnType>(column.TypeId) is { } type:
            {
                var at = At(column.Location);
                return new ElementMonitor(link.Id, column.Id, Called(linked, column, type.Name), type.Name, at, at, Turned(column.Rotation + link.Rotation), 0,
                    column.GetBaseElevation(linked) + up, column.GetTopElevation(linked) + up, column.Mark ?? string.Empty);
            }

            case Wall wall when !wall.IsElliptical && !wall.IsSpline && linked.FindType<WallType>(wall.TypeId) is { } type:
                return new ElementMonitor(link.Id, wall.Id, Called(linked, wall, type.Name), type.Name, At(wall.Start), At(wall.End), 0, wall.Bulge,
                    wall.GetBaseElevation(linked) + up, wall.GetTopElevation(linked) + up, wall.Mark ?? string.Empty);

            default:
                return null;
        }
    }

    private static string Called(BimDocument document, Element element, string type) =>
        string.IsNullOrWhiteSpace(element.Mark) ? $"{(element is Column ? "column" : "wall")} of {type}" : $"{(element is Column ? "column" : "wall")} {element.Mark}";

    private static double Turned(double degrees) => ((degrees % 360) + 360) % 360;

    /// <summary>How far apart two turns are, degrees, whichever way round.</summary>
    private static double Apart(double a, double b) => Math.Abs(((a - b) % 360 + 540) % 360 - 180);

    /// <summary>Whether two states are the same place: line or point, bow, turn, bottom and top - not the type.</summary>
    private static bool SamePlace(ElementMonitor a, ElementMonitor b) =>
        SameLine((a.Start, a.End), (b.Start, b.End)) && Math.Abs(a.Bulge - b.Bulge) < 1e-6 && Apart(a.Rotation, b.Rotation) < 0.01;

    private static bool SameHeight(ElementMonitor a, ElementMonitor b) => Math.Abs(a.Bottom - b.Bottom) <= Tolerance && Math.Abs(a.Top - b.Top) <= Tolerance;

    /// <summary>One of this project's own columns or walls as it stands, for pairing: as a monitor of its own would agree it.</summary>
    private static ElementMonitor? Own(BimDocument host, Element element) => element switch
    {
        Column column => new ElementMonitor(Guid.Empty, column.Id, string.Empty, host.FindType<ElementType>(column.TypeId)?.Name ?? string.Empty, column.Location, column.Location,
            Turned(column.Rotation), 0, column.GetBaseElevation(host), column.GetTopElevation(host)),
        Wall wall when !wall.IsElliptical && !wall.IsSpline => new ElementMonitor(Guid.Empty, wall.Id, string.Empty, host.FindType<ElementType>(wall.TypeId)?.Name ?? string.Empty,
            wall.Start, wall.End, 0, wall.Bulge, wall.GetBaseElevation(host), wall.GetTopElevation(host)),
        _ => null
    };

    private static ElementMonitor? ElementMonitorOf(Element element) => element switch { Column column => column.Monitor, Wall wall => wall.Monitor, _ => null };

    private static void SetElementMonitor(Element element, ElementMonitor? monitor)
    {
        if (element is Column column) column.Monitor = monitor;
        else if (element is Wall wall) wall.Monitor = monitor;
    }

    /// <summary>A column or wall set following an original, or no longer, as one step.</summary>
    private static IUndoableCommand Following(Element element, ElementMonitor? monitor)
    {
        var before = ElementMonitorOf(element);
        return new DelegateCommand(monitor is null ? "Stop Monitoring" : "Monitor", () => SetElementMonitor(element, monitor), () => SetElementMonitor(element, before));
    }

    /// <summary>
    /// What a linked model's columns and walls bring: each the linked model's, paired or copied. The
    /// levels already decided on - this project's following a linked level, and those being copied
    /// in with them - say which storey each stands on.
    /// </summary>
    private static void CopyElements(BimDocument host, ModelLink link, BimDocument linked, CopyMonitorKinds kinds, IReadOnlyDictionary<Guid, Level> levelFor,
        List<IUndoableCommand> steps, List<string> copied, List<string> paired, List<string> passed)
    {
        var sources = new List<Element>();
        if (kinds.HasFlag(CopyMonitorKinds.Columns)) sources.AddRange(linked.All<Column>());
        if (kinds.HasFlag(CopyMonitorKinds.Walls)) sources.AddRange(linked.All<Wall>());
        var levels = host.Levels.Concat(levelFor.Values).Distinct().OrderBy(level => level.Elevation).ToList();
        var brought = new Dictionary<string, ElementType>(StringComparer.OrdinalIgnoreCase);
        var broughtMaterials = new Dictionary<string, Material>(StringComparer.OrdinalIgnoreCase);
        var (columns, walls) = (0, 0);

        // The storey of this project a linked level is: the one following it, or being copied from it; none where there is neither.
        Level? LevelFollowing(Guid? sourceLevel) =>
            sourceLevel is not { } id ? null
            : levelFor.TryGetValue(id, out var mapped) ? mapped
            : host.Levels.FirstOrDefault(level => level.Monitor is { } monitor && monitor.LinkId == link.Id && monitor.SourceId == id);

        // Where nothing follows its level: the storey at its bottom, or the highest below it, or the lowest.
        Level StoreyAt(double elevation) =>
            levels.LastOrDefault(level => level.Elevation <= elevation + Tolerance) ?? levels[0];

        foreach (var source in sources)
        {
            if (StateOf(host, link, linked, source) is not { } state)
            {
                passed.Add(source is Wall ? "curtain, stacked, elliptical or spline wall" : "column of no type");
                continue;
            }

            // Followed already: left as it is.
            if (host.Elements.Any(element => ElementMonitorOf(element) is { } already && already.LinkId == link.Id && element.GetType() == source.GetType() &&
                                             (already.SourceId == source.Id || already.Mark.Length > 0 && already.Mark == source.Mark && linked.FindElement(already.SourceId) is null))) continue;

            // One of this project's standing where it does, of its kind and following nothing: paired with it.
            var here = host.Elements.Where(element => element.GetType() == source.GetType() && ElementMonitorOf(element) is null && (element is not Column own || own.Structural == ((Column)source).Structural))
                .FirstOrDefault(element => Own(host, element) is { } mine && SamePlace(mine, state) && SameHeight(mine, state));
            if (here is not null)
            {
                steps.Add(Following(here, state));
                paired.Add(state.SourceName);
                continue;
            }

            var bottom = state.Bottom;
            var baseLevel = LevelFollowing(source.LevelId) is { } followed && followed.Elevation <= bottom + Tolerance ? followed : StoreyAt(bottom);
            Level? topLevel = source switch
            {
                Column { TopLevelId: { } top } => LevelFollowing(top),
                Wall { TopLevelId: { } top } => LevelFollowing(top),
                _ => null
            };
            if (topLevel is not null && topLevel.Elevation <= bottom + Tolerance) topLevel = null;

            Element copy;
            switch (source)
            {
                case Column column:
                {
                    var type = TypeFor(host, linked, linked.FindType<ColumnType>(column.TypeId)!, steps, brought, broughtMaterials);
                    var made = new Column
                    {
                        Structural = column.Structural, Location = state.Start, Rotation = state.Rotation, LevelId = baseLevel.Id, TypeId = type.Id,
                        BaseOffset = bottom - baseLevel.Elevation + type.OffsetBase, RoomBounding = column.RoomBounding, Monitor = state
                    };
                    if (topLevel is not null) (made.TopLevelId, made.TopOffset) = (topLevel.Id, state.Top - topLevel.Elevation - type.OffsetTop);
                    else made.UnconnectedHeight = state.Top - bottom;
                    copy = made;
                    columns++;
                    break;
                }

                default:
                {
                    var wall = (Wall)source;
                    var type = TypeFor(host, linked, linked.FindType<WallType>(wall.TypeId)!, steps, brought, broughtMaterials);
                    var made = new Wall
                    {
                        Start = state.Start, End = state.End, Bulge = state.Bulge, LevelId = baseLevel.Id, TypeId = type.Id, BaseOffset = bottom - baseLevel.Elevation,
                        LocationLine = wall.LocationLine, Flipped = wall.Flipped, StructuralUsage = wall.StructuralUsage, RoomBounding = wall.RoomBounding, Monitor = state
                    };
                    if (topLevel is not null) (made.TopLevelId, made.TopOffset) = (topLevel.Id, state.Top - topLevel.Elevation);
                    else made.UnconnectedHeight = state.Top - bottom;
                    copy = made;
                    walls++;
                    break;
                }
            }

            copy.Mark = source.Mark;
            steps.Add(new AddElementCommand(host, copy, copy is Column ? "Copy Column" : "Copy Wall"));
        }

        if (columns > 0) copied.Add($"{columns} column{(columns == 1 ? "" : "s")}");
        if (walls > 0) copied.Add($"{walls} wall{(walls == 1 ? "" : "s")}");
        if (brought.Count > 0) copied.Add($"the type{(brought.Count == 1 ? "" : "s")} {string.Join(", ", brought.Keys)}");
    }

    /// <summary>
    /// The type of this project a linked column or wall is given: its own of the same name, or the
    /// linked model's brought in - its layers' materials this project's of the same names where it has
    /// them, or brought in too - its sweeps and bands, which name other types and materials, left behind.
    /// </summary>
    private static T TypeFor<T>(BimDocument host, BimDocument linked, T source, List<IUndoableCommand> steps, Dictionary<string, ElementType> brought, Dictionary<string, Material> broughtMaterials)
        where T : ElementType
    {
        if (host.TypesOf<T>().FirstOrDefault(type => string.Equals(type.Name, source.Name, StringComparison.OrdinalIgnoreCase)) is { } same) return same;
        if (brought.TryGetValue(source.Name, out var made)) return (T)made;

        ElementType copy;
        switch (source)
        {
            case WallType wall:
                var duplicate = wall.Duplicate(wall.Name);
                duplicate.Sweeps.Clear();
                duplicate.Bands.Clear();
                copy = duplicate;
                break;
            case ColumnType column:
                copy = column.Duplicate(column.Name);
                break;
            default:
                throw new ArgumentException("Only column and wall types are brought across.", nameof(source));
        }

        Guid Material(Guid id)
        {
            if (id == Guid.Empty || host.FindMaterial(id) is not null || linked.FindMaterial(id) is not { } material) return id;
            if (host.Materials.FirstOrDefault(own => string.Equals(own.Name, material.Name, StringComparison.OrdinalIgnoreCase)) is { } own) return own.Id;
            if (!broughtMaterials.TryGetValue(material.Name, out var mine))
            {
                broughtMaterials[material.Name] = mine = MaterialCopies.Copy(material, material.Name);
                steps.Add(new AddMaterialCommand(host, mine));
            }

            return mine.Id;
        }

        switch (copy)
        {
            case WallType wall:
                foreach (var layer in wall.Structure.Layers) layer.MaterialId = Material(layer.MaterialId);
                break;
            case ColumnType column:
                column.MaterialId = Material(column.MaterialId);
                break;
        }

        brought[source.Name] = copy;
        steps.Add(new AddTypeCommand(host, copy));
        return (T)copy;
    }

    /// <summary>What has changed in the linked models' columns and walls since each following one was last agreed.</summary>
    private static IEnumerable<MonitorFinding> ReviewElements(BimDocument host)
    {
        foreach (var element in host.Elements.Where(element => element is Column or Wall))
        {
            if (ElementMonitorOf(element) is not { } monitor) continue;
            var name = NameOf(element);
            if (host.Find<ModelLink>(monitor.LinkId) is not { } link)
            {
                yield return new MonitorFinding(element, null, MonitorChange.LinkGone, $"{name} follows the {monitor.SourceName} of a linked model since taken out.");
                continue;
            }

            if (ModelLinks.Of(link) is not { Document: { } linked }) continue;
            if (SourceOf(linked, monitor, element) is not { } source || StateOf(host, link, linked, source) is not { } now)
            {
                yield return new MonitorFinding(element, link, MonitorChange.Gone, $"{name} follows the {monitor.SourceName} of {link.Title}, which is no longer in it.");
                continue;
            }

            if (!SamePlace(now, monitor))
            {
                var moved = Math.Max(Shift((monitor.Start, monitor.End), (now.Start, now.End)), 0);
                var turned = Apart(now.Rotation, monitor.Rotation);
                yield return new MonitorFinding(element, link, MonitorChange.Moved,
                    $"The {now.SourceName} of {link.Title} has {(moved > Tolerance ? $"moved {Units.FormatLength(moved)}" : "")}{(moved > Tolerance && turned >= 0.01 ? " and " : "")}{(turned >= 0.01 ? $"turned {turned:0.#}°" : "")}{(moved <= Tolerance && turned < 0.01 ? "changed its curve" : "")}: {name} follows it.");
            }

            if (!SameHeight(now, monitor))
            {
                string Said(string end, double was, double then) => Math.Abs(then - was) <= Tolerance ? string.Empty : $"its {end} {Units.FormatLength(Math.Abs(then - was))} {(then > was ? "higher" : "lower")}";
                var said = string.Join(" and ", new[] { Said("bottom", monitor.Bottom, now.Bottom), Said("top", monitor.Top, now.Top) }.Where(text => text.Length > 0));
                yield return new MonitorFinding(element, link, MonitorChange.Resized, $"The {now.SourceName} of {link.Title} now has {said}: {name} follows it.");
            }

            if (!string.Equals(now.TypeName, monitor.TypeName, StringComparison.OrdinalIgnoreCase))
                yield return new MonitorFinding(element, link, MonitorChange.Retyped, $"The {monitor.SourceName} of {link.Title} is now {now.TypeName}, no longer {monitor.TypeName}: {name} follows it.");
        }
    }

    /// <summary>
    /// The column or wall of a linked model one of this project's follows: by its id - or, where a model
    /// written again from scratch has given its elements new ids, the one of its kind there of the mark it had.
    /// </summary>
    private static Element? SourceOf(BimDocument linked, ElementMonitor monitor, Element follower) =>
        linked.FindElement(monitor.SourceId) is { } same && same.GetType() == follower.GetType() ? same
        : monitor.Mark.Length > 0 && linked.Elements.Where(each => each.GetType() == follower.GetType() && each.Mark == monitor.Mark).Take(2).ToList() is [var marked] ? marked
        : null;

    private static string NameOf(Element element) =>
        (element is Column ? "Column" : "Wall") + (string.IsNullOrWhiteSpace(element.Mark) ? string.Empty : $" {element.Mark}");

    /// <summary>
    /// A change to a linked column or wall answered by bringing this project's to it: moved and turned
    /// where it now is, its bottom and top where they now are, or of the type it now is - and agreed
    /// there. One step to undo.
    /// </summary>
    private static IUndoableCommand? MatchElement(BimDocument host, MonitorFinding finding, Element element)
    {
        if (finding.Link is not { } link || ModelLinks.Of(link) is not { Document: { } linked } || ElementMonitorOf(element) is not { } monitor) return null;
        if (SourceOf(linked, monitor, element) is not { } source || StateOf(host, link, linked, source) is not { } now) return null;

        var steps = new List<IUndoableCommand>();
        switch (finding.Change)
        {
            case MonitorChange.Moved:
                if (element is Column column)
                {
                    var (wasAt, wasTurn) = (column.Location, column.Rotation);
                    steps.Add(new DelegateCommand("Move Column to Match", () => (column.Location, column.Rotation) = (now.Start, now.Rotation), () => (column.Location, column.Rotation) = (wasAt, wasTurn)));
                }
                else if (element is Wall wall)
                {
                    var (wasStart, wasEnd, wasBulge) = (wall.Start, wall.End, wall.Bulge);
                    steps.Add(new DelegateCommand("Move Wall to Match", () => (wall.Start, wall.End, wall.Bulge) = (now.Start, now.End, now.Bulge), () => (wall.Start, wall.End, wall.Bulge) = (wasStart, wasEnd, wasBulge)));
                }

                steps.Add(Following(element, monitor with { SourceId = source.Id, Start = now.Start, End = now.End, Rotation = now.Rotation, Bulge = now.Bulge, SourceName = now.SourceName }));
                break;

            case MonitorChange.Resized:
                steps.Add(Heighted(host, element, now.Bottom, now.Top));
                steps.Add(Following(element, monitor with { SourceId = source.Id, Bottom = now.Bottom, Top = now.Top, SourceName = now.SourceName }));
                break;

            case MonitorChange.Retyped:
            {
                var made = new List<IUndoableCommand>();
                var brought = new Dictionary<string, ElementType>(StringComparer.OrdinalIgnoreCase);
                var materials = new Dictionary<string, Material>(StringComparer.OrdinalIgnoreCase);
                ElementType? type = source switch
                {
                    Column linkedColumn when linked.FindType<ColumnType>(linkedColumn.TypeId) is { } kind => TypeFor(host, linked, kind, made, brought, materials),
                    Wall linkedWall when linked.FindType<WallType>(linkedWall.TypeId) is { } kind => TypeFor(host, linked, kind, made, brought, materials),
                    _ => null
                };
                if (type is null) return null;
                steps.AddRange(made);
                var was = element.TypeId;
                steps.Add(new DelegateCommand("Change Type to Match", () => element.TypeId = type.Id, () => element.TypeId = was));
                steps.Add(Following(element, monitor with { SourceId = source.Id, TypeName = now.TypeName, SourceName = now.SourceName }));
                break;
            }

            default:
                return null;
        }

        return new CompositeCommand(finding.Change switch
        {
            MonitorChange.Moved => element is Column ? "Move Column to Match" : "Move Wall to Match",
            MonitorChange.Resized => "Match Height",
            _ => "Change Type to Match"
        }, steps);
    }

    /// <summary>A column's or wall's bottom and top set where they are to be - by its offsets, on the storeys it keeps - as one step.</summary>
    private static IUndoableCommand Heighted(BimDocument host, Element element, double bottom, double top)
    {
        switch (element)
        {
            case Column column:
            {
                var type = host.FindType<ColumnType>(column.TypeId);
                var (offsetBase, offsetTop) = (type?.OffsetBase ?? 0, type?.OffsetTop ?? 0);
                var was = (column.BaseOffset, column.TopOffset, column.UnconnectedHeight);
                var level = host.FindLevel(column.LevelId)?.Elevation ?? 0;
                var topLevel = column.TopLevelId is { } id && host.FindLevel(id) is { } upper && upper.Elevation > bottom ? upper : null;
                var to = (bottom - level + offsetBase, topLevel is null ? column.TopOffset : top - topLevel.Elevation - offsetTop, topLevel is null ? top - bottom : column.UnconnectedHeight);
                return new DelegateCommand("Match Height", () => (column.BaseOffset, column.TopOffset, column.UnconnectedHeight) = to, () => (column.BaseOffset, column.TopOffset, column.UnconnectedHeight) = was);
            }

            case Wall wall:
            {
                var was = (wall.BaseOffset, wall.TopOffset, wall.UnconnectedHeight);
                var level = host.FindLevel(wall.LevelId)?.Elevation ?? 0;
                var topLevel = wall.TopLevelId is { } id && host.FindLevel(id) is { } upper && upper.Elevation > bottom ? upper : null;
                var to = (bottom - level, topLevel is null ? wall.TopOffset : top - topLevel.Elevation, topLevel is null ? top - bottom : wall.UnconnectedHeight);
                return new DelegateCommand("Match Height", () => (wall.BaseOffset, wall.TopOffset, wall.UnconnectedHeight) = to, () => (wall.BaseOffset, wall.TopOffset, wall.UnconnectedHeight) = was);
            }

            default:
                throw new ArgumentException("Only columns and walls follow linked ones.", nameof(element));
        }
    }

    /// <summary>A change to a linked column or wall accepted as it now is: this project's left as it is, its original agreed as it now stands.</summary>
    private static IUndoableCommand? AcceptElement(BimDocument host, MonitorFinding finding, Element element)
    {
        if (finding.Link is not { } link || ModelLinks.Of(link) is not { Document: { } linked } || ElementMonitorOf(element) is not { } monitor) return null;
        if (SourceOf(linked, monitor, element) is not { } source || StateOf(host, link, linked, source) is not { } now) return null;
        return Following(element, now);
    }

    /// <summary>What a column or wall follows, as its Properties say it.</summary>
    public static string Said(BimDocument host, ElementMonitor? monitor) =>
        monitor is null ? "Nothing"
        : host.Find<ModelLink>(monitor.LinkId) is { } link ? $"The {monitor.SourceName} in {link.Title}"
        : $"The {monitor.SourceName} in a link since taken out";
}
