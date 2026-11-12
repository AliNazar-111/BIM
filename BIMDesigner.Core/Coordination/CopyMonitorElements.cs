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

/// <summary>What Copy/Monitor brings from a linked model: its levels, its gridlines, its columns, its walls, its floors, the doors and windows in its walls.</summary>
[Flags]
public enum CopyMonitorKinds
{
    None = 0,
    Levels = 1,
    Grids = 2,
    Columns = 4,
    Walls = 8,
    Floors = 16,
    Openings = 32,
    Datums = Levels | Grids
}

/// <summary>
/// The agreed state of a linked model's column, wall, floor, door or window one of this project's follows, as the link puts it
/// in this project: a wall's line and its bow, a column's point and how it is turned, a floor's outline, a door's or window's
/// middle on its wall and its width; its bottom and top; the name of its type; and its mark, by which it is found again in a
/// model written afresh with new ids.
/// </summary>
public sealed record ElementMonitor(Guid LinkId, Guid SourceId, string SourceName, string TypeName, Point2D Start, Point2D End, double Rotation, double Bulge, double Bottom, double Top,
    string Mark = "", IReadOnlyList<Point2D>? Outline = null, double Width = 0);

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

            // A floor: its outline where the link puts it, its middle, its underside and top.
            case Floor floor when floor.Boundary.Count >= 3 && linked.FindType<FloorType>(floor.TypeId) is { } type:
            {
                var outline = floor.Boundary.Select(At).ToList();
                var middle = new Point2D(outline.Average(point => point.X), outline.Average(point => point.Y));
                return new ElementMonitor(link.Id, floor.Id, Called(linked, floor, type.Name), type.Name, middle, middle, 0, 0,
                    floor.GetBottomElevation(linked) + up, floor.GetTopElevation(linked) + up, floor.Mark ?? string.Empty, outline);
            }

            // A door or window: its middle on its wall's line, its sill and head, how wide.
            case Opening opening when linked.Find<Wall>(opening.HostWallId) is { } inWall && linked.FindType<OpeningType>(opening.TypeId) is { } type:
            {
                var middle = At(opening.GetCentre(inWall));
                var sill = inWall.GetBaseElevation(linked) + opening.SillHeight + up;
                return new ElementMonitor(link.Id, opening.Id, Called(linked, opening, type.Name), type.Name, middle, middle, 0, 0,
                    sill, sill + opening.HeightOf(type), opening.Mark ?? string.Empty, null, opening.WidthOf(type));
            }

            default:
                return null;
        }
    }

    /// <summary>What was left, said of more than one: "curtain ... walls", "columns of no type", "doors or windows in a wall ...".</summary>
    private static string Plural(string said) =>
        said.EndsWith(" wall", StringComparison.Ordinal) ? said + "s"
        : said.StartsWith("door or window", StringComparison.Ordinal) ? "doors or windows" + said["door or window".Length..]
        : said.StartsWith("column", StringComparison.Ordinal) ? "columns" + said["column".Length..]
        : said.StartsWith("floor", StringComparison.Ordinal) ? "floors" + said["floor".Length..]
        : said;

    /// <summary>What a kind of element is called, in what is said of it.</summary>
    private static string KindOf(Element element) => element switch
    {
        Column => "column",
        Wall => "wall",
        Floor => "floor",
        Door => "door",
        _ => "window"
    };

    private static string Called(BimDocument document, Element element, string type) =>
        string.IsNullOrWhiteSpace(element.Mark) ? $"{KindOf(element)} of {type}" : $"{KindOf(element)} {element.Mark}";

    /// <summary>Whether two outlines are the same, corner by corner, within the tolerance - starting from any corner, either way round.</summary>
    private static bool SameOutline(IReadOnlyList<Point2D>? a, IReadOnlyList<Point2D>? b)
    {
        if (a is null || b is null) return a is null && b is null;
        if (a.Count != b.Count) return false;
        for (var start = 0; start < b.Count; start++)
            foreach (var way in new[] { 1, -1 })
                if (Enumerable.Range(0, a.Count).All(i => a[i].DistanceTo(b[((start + way * i) % b.Count + b.Count) % b.Count]) <= Tolerance)) return true;
        return false;
    }

    private static double Turned(double degrees) => ((degrees % 360) + 360) % 360;

    /// <summary>How far apart two turns are, degrees, whichever way round.</summary>
    private static double Apart(double a, double b) => Math.Abs(((a - b) % 360 + 540) % 360 - 180);

    /// <summary>Whether two states are the same place: line or point, bow, turn, bottom and top - not the type.</summary>
    private static bool SamePlace(ElementMonitor a, ElementMonitor b) =>
        SameLine((a.Start, a.End), (b.Start, b.End)) && Math.Abs(a.Bulge - b.Bulge) < 1e-6 && Apart(a.Rotation, b.Rotation) < 0.01 && SameOutline(a.Outline, b.Outline);

    /// <summary>Whether two states are the same size: bottom, top, and a door's or window's width.</summary>
    private static bool SameHeight(ElementMonitor a, ElementMonitor b) =>
        Math.Abs(a.Bottom - b.Bottom) <= Tolerance && Math.Abs(a.Top - b.Top) <= Tolerance && Math.Abs(a.Width - b.Width) <= Tolerance;

    /// <summary>One of this project's own columns, walls, floors, doors or windows as it stands, for pairing: as a monitor of its own would agree it.</summary>
    private static ElementMonitor? Own(BimDocument host, Element element) => element switch
    {
        Column column => new ElementMonitor(Guid.Empty, column.Id, string.Empty, host.FindType<ElementType>(column.TypeId)?.Name ?? string.Empty, column.Location, column.Location,
            Turned(column.Rotation), 0, column.GetBaseElevation(host), column.GetTopElevation(host)),
        Wall wall when !wall.IsElliptical && !wall.IsSpline => new ElementMonitor(Guid.Empty, wall.Id, string.Empty, host.FindType<ElementType>(wall.TypeId)?.Name ?? string.Empty,
            wall.Start, wall.End, 0, wall.Bulge, wall.GetBaseElevation(host), wall.GetTopElevation(host)),
        Floor floor when floor.Boundary.Count >= 3 => new ElementMonitor(Guid.Empty, floor.Id, string.Empty, host.FindType<ElementType>(floor.TypeId)?.Name ?? string.Empty,
            Middle(floor.Boundary), Middle(floor.Boundary), 0, 0, floor.GetBottomElevation(host), floor.GetTopElevation(host), string.Empty, floor.Boundary.ToList()),
        Opening opening when host.Find<Wall>(opening.HostWallId) is { } wall && host.FindType<OpeningType>(opening.TypeId) is { } type =>
            new ElementMonitor(Guid.Empty, opening.Id, string.Empty, type.Name, opening.GetCentre(wall), opening.GetCentre(wall), 0, 0,
                wall.GetBaseElevation(host) + opening.SillHeight, wall.GetBaseElevation(host) + opening.SillHeight + opening.HeightOf(type), string.Empty, null, opening.WidthOf(type)),
        _ => null
    };

    private static Point2D Middle(IReadOnlyList<Point2D> outline) => new(outline.Average(point => point.X), outline.Average(point => point.Y));

    private static ElementMonitor? ElementMonitorOf(Element element) => element switch
    {
        Column column => column.Monitor,
        Wall wall => wall.Monitor,
        Floor floor => floor.Monitor,
        Opening opening => opening.Monitor,
        _ => null
    };

    private static void SetElementMonitor(Element element, ElementMonitor? monitor)
    {
        switch (element)
        {
            case Column column: column.Monitor = monitor; break;
            case Wall wall: wall.Monitor = monitor; break;
            case Floor floor: floor.Monitor = monitor; break;
            case Opening opening: opening.Monitor = monitor; break;
        }
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
        if (kinds.HasFlag(CopyMonitorKinds.Floors)) sources.AddRange(linked.All<Floor>());
        if (kinds.HasFlag(CopyMonitorKinds.Openings)) sources.AddRange(linked.All<Opening>());
        var levels = host.Levels.Concat(levelFor.Values).Distinct().OrderBy(level => level.Elevation).ToList();
        var brought = new Dictionary<string, ElementType>(StringComparer.OrdinalIgnoreCase);
        var broughtMaterials = new Dictionary<string, Material>(StringComparer.OrdinalIgnoreCase);
        var (columns, walls, floors, openings) = (0, 0, 0, 0);

        // This project's wall each linked wall is: the one following it already, or paired or copied here - where a door or window goes.
        var wallFor = host.All<Wall>().Where(wall => wall.Monitor is { } monitor && monitor.LinkId == link.Id)
            .Select(wall => (Source: SourceOf(linked, wall.Monitor!, wall), Wall: wall)).Where(each => each.Source is not null)
            .GroupBy(each => each.Source!.Id).ToDictionary(group => group.Key, group => group.First().Wall);

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
                passed.Add(source switch
                {
                    Wall => "curtain, stacked, elliptical or spline wall",
                    Floor => "floor of no type or outline",
                    Opening => "door or window of no type",
                    _ => "column of no type"
                });
                continue;
            }

            // A door or window goes only into a wall of this project's following the one it is in there.
            Wall? hostWall = null;
            if (source is Opening hosted && !wallFor.TryGetValue(hosted.HostWallId, out hostWall))
            {
                passed.Add("door or window in a wall not followed here");
                continue;
            }

            // Followed already: left as it is.
            if (host.Elements.Any(element => ElementMonitorOf(element) is { } already && already.LinkId == link.Id && element.GetType() == source.GetType() &&
                                             (already.SourceId == source.Id || already.Mark.Length > 0 && already.Mark == source.Mark && linked.FindElement(already.SourceId) is null))) continue;

            // One of this project's standing where it does, of its kind and following nothing: paired with it.
            var here = host.Elements.Where(element => element.GetType() == source.GetType() && ElementMonitorOf(element) is null && (element is not Column own || own.Structural == ((Column)source).Structural))
                .FirstOrDefault(element => Own(host, element) is { } mine && SamePlace(mine, state) && SameHeight(mine, state));
            if (here is not null && (here is not Opening pairedOpening || pairedOpening.HostWallId == hostWall!.Id))
            {
                steps.Add(Following(here, state));
                paired.Add(state.SourceName);
                if (source is Wall && here is Wall pairedWall) wallFor[source.Id] = pairedWall;
                continue;
            }

            // A door or window into its wall here: as far along it, as high above its foot, facing and hung as it was.
            if (source is Opening opening)
            {
                var (along, flipped) = AlongWall(hostWall!, state.Start, linked.Find<Wall>(opening.HostWallId)!, host, link, linked);
                var type = TypeFor(host, linked, linked.FindType<OpeningType>(opening.TypeId)!, steps, brought, broughtMaterials);
                Opening made = opening is Door ? new Door() : new Window();
                (made.TypeId, made.LevelId, made.HostWallId, made.DistanceAlongWall) = (type.Id, hostWall!.LevelId, hostWall.Id, Math.Max(0, along));
                (made.SillHeight, made.FlipFacing, made.FlipHand, made.IsOpen) = (opening.SillHeight, opening.FlipFacing != flipped, opening.FlipHand != flipped, opening.IsOpen);
                (made.WidthOverride, made.HeightOverride, made.Monitor, made.Mark) = (opening.WidthOverride, opening.HeightOverride, state, opening.Mark);
                steps.Add(new AddElementCommand(host, made, opening is Door ? "Copy Door" : "Copy Window"));
                openings++;
                continue;
            }

            // A floor onto this project's storey following its own - or at its top - its outline and holes where the link puts them.
            if (source is Floor floor)
            {
                var type = TypeFor(host, linked, linked.FindType<FloorType>(floor.TypeId)!, steps, brought, broughtMaterials);
                var storey = LevelFollowing(floor.LevelId) is { } own && own.Elevation <= state.Top + Tolerance ? own : StoreyAt(state.Top);
                var place = ModelLinks.Placement(host, link, linked);
                Point2D Placed(Point2D point) => place(new Point3D(point.X, point.Y, 0)) is var moved ? new Point2D(moved.X, moved.Y) : point;
                var made = new Floor
                {
                    TypeId = type.Id, LevelId = storey.Id, HeightOffset = state.Top - storey.Elevation, AnalyseAs = floor.AnalyseAs,
                    SpanDirection = floor.SpanDirection is { } span ? Turned(span + link.Rotation) : null, Monitor = state, Mark = floor.Mark
                };
                made.SetBoundary(state.Outline!);
                made.SetHoles(floor.Holes.Select(hole => hole.Select(Placed)));
                steps.Add(new AddElementCommand(host, made, "Copy Floor"));
                floors++;
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
                    wallFor[wall.Id] = made;
                    walls++;
                    break;
                }
            }

            copy.Mark = source.Mark;
            steps.Add(new AddElementCommand(host, copy, copy is Column ? "Copy Column" : "Copy Wall"));
        }

        if (columns > 0) copied.Add($"{columns} column{(columns == 1 ? "" : "s")}");
        if (walls > 0) copied.Add($"{walls} wall{(walls == 1 ? "" : "s")}");
        if (floors > 0) copied.Add($"{floors} floor{(floors == 1 ? "" : "s")}");
        if (openings > 0) copied.Add($"{openings} door{(openings == 1 ? "" : "s")} and window{(openings == 1 ? "" : "s")}".Replace("1 door and window", "1 door or window"));
        if (brought.Count > 0) copied.Add($"the type{(brought.Count == 1 ? "" : "s")} {string.Join(", ", brought.Keys)}");
    }

    /// <summary>
    /// How far along a wall of this project a point of a linked door or window is - its middle where the link puts it - and
    /// whether the wall runs the other way to the linked one it follows, so the door faces and hangs the other way round on it.
    /// </summary>
    private static (double Along, bool Flipped) AlongWall(Wall wall, Point2D middle, Wall original, BimDocument host, ModelLink link, BimDocument linked)
    {
        var place = ModelLinks.Placement(host, link, linked);
        var start = place(new Point3D(original.Start.X, original.Start.Y, 0));
        var flipped = new Point2D(start.X, start.Y).DistanceTo(wall.Start) > new Point2D(start.X, start.Y).DistanceTo(wall.End);
        return (wall.LocationCurve.Locate(middle).Along, flipped);
    }

    /// <summary>
    /// The type of this project a linked column or wall is given: its own of the same name, or the
    /// linked model's brought in - its layers' materials this project's of the same names where it has
    /// them, or brought in too - its sweeps and bands, which name other types and materials, left behind.
    /// </summary>
    private static T TypeFor<T>(BimDocument host, BimDocument linked, T source, List<IUndoableCommand> steps, Dictionary<string, ElementType> brought, Dictionary<string, Material> broughtMaterials)
        where T : ElementType
    {
        if (host.TypesOf<T>().FirstOrDefault(type => type.GetType() == source.GetType() && string.Equals(type.Name, source.Name, StringComparison.OrdinalIgnoreCase)) is { } same) return same;
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
            case FloorType floor:
                var layered = new FloorType(floor.Name, floor.Structure.Clone());
                CopySettings(floor, layered);
                copy = layered;
                break;
            case OpeningType opening when TypeDuplicates.Duplicate(opening, opening.Name) is { } doorOrWindow:
                copy = doorOrWindow;
                break;
            default:
                throw new ArgumentException("Only column, wall, floor, door and window types are brought across.", nameof(source));
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
            case FloorType floor:
                foreach (var layer in floor.Structure.Layers) layer.MaterialId = Material(layer.MaterialId);
                break;
        }

        brought[source.Name] = copy;
        steps.Add(new AddTypeCommand(host, copy));
        return (T)copy;
    }

    /// <summary>
    /// A floor type's settings carried to its copy - its function, its heat and sound figures, its identity - each plain value
    /// it has that can be set; its layers are copied with it.
    /// </summary>
    private static void CopySettings(SlabType from, SlabType to)
    {
        foreach (var property in typeof(SlabType).GetProperties().Concat(from.GetType().GetProperties()).Distinct())
        {
            if (property.Name is nameof(ElementType.Id) or nameof(ElementType.Name) || !property.CanRead || !property.CanWrite || property.GetIndexParameters().Length > 0) continue;
            var kind = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            if (kind.IsPrimitive || kind.IsEnum || kind == typeof(string) || kind == typeof(decimal) || kind == typeof(Guid) || kind.IsValueType && kind.Namespace == typeof(ColourRgb).Namespace)
                property.SetValue(to, property.GetValue(from));
        }
    }

    /// <summary>What has changed in the linked models' columns, walls, floors, doors and windows since each following one was last agreed.</summary>
    private static IEnumerable<MonitorFinding> ReviewElements(BimDocument host)
    {
        foreach (var element in host.Elements.Where(element => element is Column or Wall or Floor or Opening))
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

            if (!SamePlace(now, monitor) && (now.Outline is not null || element is Opening))
            {
                // A floor reshaped or moved; a door or window moved along its wall.
                var by = now.Start.DistanceTo(monitor.Start);
                yield return new MonitorFinding(element, link, MonitorChange.Moved, element is Opening
                    ? $"The {now.SourceName} of {link.Title} has moved {Units.FormatLength(by)} along its wall: {name} follows it."
                    : $"The {now.SourceName} of {link.Title} has {(SameOutline(Shifted(monitor.Outline!, now.Start - monitor.Start), now.Outline) ? $"moved {Units.FormatLength(by)}" : "changed its outline")}: {name} follows it.");
            }
            else if (!SamePlace(now, monitor))
            {
                var moved = Math.Max(Shift((monitor.Start, monitor.End), (now.Start, now.End)), 0);
                var turned = Apart(now.Rotation, monitor.Rotation);
                yield return new MonitorFinding(element, link, MonitorChange.Moved,
                    $"The {now.SourceName} of {link.Title} has {(moved > Tolerance ? $"moved {Units.FormatLength(moved)}" : "")}{(moved > Tolerance && turned >= 0.01 ? " and " : "")}{(turned >= 0.01 ? $"turned {turned:0.#}°" : "")}{(moved <= Tolerance && turned < 0.01 ? "changed its curve" : "")}: {name} follows it.");
            }

            if (!SameHeight(now, monitor))
            {
                string Said(string end, double was, double then) => Math.Abs(then - was) <= Tolerance ? string.Empty : $"its {end} {Units.FormatLength(Math.Abs(then - was))} {(then > was ? "higher" : "lower")}";
                var (bottom, top) = element is Opening ? ("sill", "head") : ("bottom", "top");
                var wider = Math.Abs(now.Width - monitor.Width) <= Tolerance ? string.Empty : $"its width {Units.FormatLength(Math.Abs(now.Width - monitor.Width))} {(now.Width > monitor.Width ? "more" : "less")}";
                var said = string.Join(" and ", new[] { Said(bottom, monitor.Bottom, now.Bottom), Said(top, monitor.Top, now.Top), wider }.Where(text => text.Length > 0));
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

    private static string NameOf(Element element)
    {
        var kind = KindOf(element);
        return char.ToUpperInvariant(kind[0]) + kind[1..] + (string.IsNullOrWhiteSpace(element.Mark) ? string.Empty : $" {element.Mark}");
    }

    private static IReadOnlyList<Point2D> Shifted(IReadOnlyList<Point2D> outline, Vector2D by) => outline.Select(point => point + by).ToList();

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
                else if (element is Floor floor && source is Floor original)
                {
                    // Its outline and holes as the linked floor's now are, where the link puts them.
                    var place = ModelLinks.Placement(host, link, linked);
                    Point2D Placed(Point2D point) => place(new Point3D(point.X, point.Y, 0)) is var moved ? new Point2D(moved.X, moved.Y) : point;
                    var (wasOutline, wasHoles) = (floor.Boundary.ToList(), floor.Holes.Select(hole => hole.ToList()).ToList());
                    var (toOutline, toHoles) = (now.Outline!.ToList(), original.Holes.Select(hole => hole.Select(Placed).ToList()).ToList());
                    steps.Add(new DelegateCommand("Move Floor to Match", () => { floor.SetBoundary(toOutline); floor.SetHoles(toHoles); }, () => { floor.SetBoundary(wasOutline); floor.SetHoles(wasHoles); }));
                }
                else if (element is Opening opening && host.Find<Wall>(opening.HostWallId) is { } hostWall && source is Opening linkedOpening && linked.Find<Wall>(linkedOpening.HostWallId) is { } originalWall)
                {
                    var (along, _) = AlongWall(hostWall, now.Start, originalWall, host, link, linked);
                    var was = opening.DistanceAlongWall;
                    var to = Math.Max(0, along);
                    steps.Add(new DelegateCommand("Move to Match", () => opening.DistanceAlongWall = to, () => opening.DistanceAlongWall = was));
                }

                steps.Add(Following(element, monitor with { SourceId = source.Id, Start = now.Start, End = now.End, Rotation = now.Rotation, Bulge = now.Bulge, Outline = now.Outline, SourceName = now.SourceName }));
                break;

            case MonitorChange.Resized:
                steps.Add(Heighted(host, element, now.Bottom, now.Top, now.Width));
                steps.Add(Following(element, monitor with { SourceId = source.Id, Bottom = now.Bottom, Top = now.Top, Width = now.Width, SourceName = now.SourceName }));
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
                    Floor linkedFloor when linked.FindType<FloorType>(linkedFloor.TypeId) is { } kind => TypeFor(host, linked, kind, made, brought, materials),
                    Opening linkedOpening when linked.FindType<OpeningType>(linkedOpening.TypeId) is { } kind => TypeFor(host, linked, kind, made, brought, materials),
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
            MonitorChange.Moved => $"Move {NameOf(element).Split(' ')[0]} to Match",
            MonitorChange.Resized => element is Opening ? "Match Size" : "Match Height",
            _ => "Change Type to Match"
        }, steps);
    }

    /// <summary>
    /// A column's, wall's or floor's bottom and top set where they are to be - by its offsets, on the storeys it keeps - or a door's
    /// or window's sill, height and width, as one step.
    /// </summary>
    private static IUndoableCommand Heighted(BimDocument host, Element element, double bottom, double top, double width = 0)
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

            // A floor by its top: its offset from its storey.
            case Floor floor:
            {
                var was = floor.HeightOffset;
                var to = top - (host.FindLevel(floor.LevelId)?.Elevation ?? 0);
                return new DelegateCommand("Match Height", () => floor.HeightOffset = to, () => floor.HeightOffset = was);
            }

            // A door or window: its sill above its wall's foot, and its own height and width where its type's are not those.
            case Opening opening:
            {
                var type = host.FindType<OpeningType>(opening.TypeId);
                var foot = host.Find<Wall>(opening.HostWallId)?.GetBaseElevation(host) ?? 0;
                var was = (opening.SillHeight, opening.HeightOverride, opening.WidthOverride);
                double? Own(double value, double? typed) => typed is { } kind && Math.Abs(kind - value) <= Tolerance ? null : value;
                var to = (bottom - foot, Own(top - bottom, type?.Height), Own(width, type?.Width));
                return new DelegateCommand("Match Size", () => (opening.SillHeight, opening.HeightOverride, opening.WidthOverride) = to, () => (opening.SillHeight, opening.HeightOverride, opening.WidthOverride) = was);
            }

            default:
                throw new ArgumentException("Only columns, walls, floors, doors and windows follow linked ones.", nameof(element));
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
