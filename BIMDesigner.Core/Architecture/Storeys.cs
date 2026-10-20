using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// Storeys as they are built. A floor - or a flat roof laid as one - divides the walls that run
/// up through it, whichever was drawn first: each is split at the floor, the part under it
/// stopping at its underside and the part over it standing on its top, so the floor's edge
/// shows between them rather than the wall running on through it. A floor with a room over it,
/// at a height no level is at - the floor of a room in the roof, laid as a flat roof over the
/// storey below - is made a storey of its own: a level at its top, standing on it and moving
/// with it, and the walls over it on that level.
/// </summary>
public static class Storeys
{
    /// <summary>How far past a floor's underside and its top a wall must run to run through it, mm.</summary>
    private const double Past = 50;

    /// <summary>How high the room over a floor must be for the floor to be a storey's, mm.</summary>
    public const double RoomHeight = 2000;

    private static readonly string[] Ordinals = { "Ground", "First", "Second", "Third", "Fourth", "Fifth", "Sixth", "Seventh", "Eighth", "Ninth", "Tenth" };

    /// <summary>
    /// The walls running up through a floor, or a flat roof walked on: from under its underside
    /// to over its top, and into it all along - its outline reaching into them from end to end.
    /// A floor stopping short of a wall, as one does behind a curtain wall hung in front of it,
    /// leaves the wall running on past it; so does one reaching into it only along a part of it.
    /// Not a wall drawn to an outline of its own, leaning or tapered, nor one with a door or
    /// window where the floor would cut it. Among those given, or every wall.
    /// </summary>
    public static IReadOnlyList<Wall> Through(BimDocument document, Slab slab, IEnumerable<Wall>? among = null)
    {
        if (slab.Boundary.Count < 3 || slab is Ceiling || !FloorSurfaces.IsStoodOn(document, slab)) return Array.Empty<Wall>();

        var (under, top) = (slab.GetBottomElevation(document), slab.GetTopElevation(document));
        var (min, max) = (new Point2D(slab.Boundary.Min(p => p.X), slab.Boundary.Min(p => p.Y)), new Point2D(slab.Boundary.Max(p => p.X), slab.Boundary.Max(p => p.Y)));
        var walls = new List<Wall>();
        foreach (var wall in among ?? document.Walls)
        {
            if (wall.Length < 1 || wall.Profile is not null || wall.TopAttachedTo == slab.Id || wall.BaseAttachedTo == slab.Id) continue;
            if (wall.CrossSection != WallCrossSection.Vertical || wall.OverrideTaper || document.GetWallType(wall) is not { } body) continue;

            // Nowhere near it.
            var reach = body.Structure.TotalWidth + (wall.IsCurved ? wall.Length / 2 : 0);
            if (Math.Max(wall.Start.X, wall.End.X) + reach < min.X || Math.Min(wall.Start.X, wall.End.X) - reach > max.X
                || Math.Max(wall.Start.Y, wall.End.Y) + reach < min.Y || Math.Min(wall.Start.Y, wall.End.Y) - reach > max.Y) continue;

            var (foot, head) = Span(document, wall);
            if (foot > under - Past || head < top + Past) continue;

            // Into it from end to end - its corners aside: not a wall it reaches into along a part of it, nor one it touches at an end.
            // How far along it the floor reaches into it - drawn to its centreline or its outer face alike - and that it is into it, not touching it.
            var width = Math.Max(1, body.Structure.TotalWidth);
            var overlap = PolygonBoolean.Combine(slab.Boundary, SlabEdges.Slot(wall, body, 0), BooleanOperation.Intersection);
            var along = overlap.Sum(region => region.Outer.Max(point => wall.LocationCurve.Locate(point).Along) - region.Outer.Min(point => wall.LocationCurve.Locate(point).Along));
            if (along < Math.Max(wall.Length / 2, wall.Length - 2 * width - 1) || overlap.Sum(region => region.Area) < along * 10) continue;

            // Not through a door or a window: one the floor would cut across stays as it is, its wall whole.
            var (low, high) = (under - foot, top - foot);
            if (WallOpenings.Of(document, wall).Any(opening => opening.SillHeight < high - 1 && opening.SillHeight + opening.HeightOf(document.FindType<OpeningType>(opening.TypeId)) > low + 1)
                || document.Elements.OfType<WallOpening>().Any(cut => cut.HostWallId == wall.Id && cut.SillHeight < high - 1 && cut.SillHeight + cut.Height > low + 1)) continue;
            walls.Add(wall);
        }

        return walls;
    }

    /// <summary>The walls standing on a floor, as high as a room or higher: a storey's, built on it.</summary>
    private static IReadOnlyList<Wall> StandingOn(BimDocument document, Slab slab) =>
        document.Walls.Where(wall => wall.BaseAttachedTo == slab.Id && Span(document, wall) is var (foot, head) && head - foot >= RoomHeight).ToList();

    /// <summary>Where a wall starts, and how high it reaches at its highest: a gable to its apex, a curtain wall to the top of its glass.</summary>
    public static (double Foot, double Head) Span(BimDocument document, Wall wall)
    {
        var foot = wall.GetBaseElevation(document);
        if (document.IsCurtainWall(wall)) return (foot, foot + SlabEdges.CurtainHeight(document, wall));
        return (foot, WallProfile.Of(document, wall) is { Count: >= 3 } outline ? foot + WallProfile.Top(outline) : wall.GetTopElevation(document));
    }

    /// <summary>
    /// The name for a storey at a height: the next of a building's floors counted up from the
    /// ground - the second floor over the first - a mezzanine of the storey it is in, or the next number.
    /// </summary>
    public static string NameAt(BimDocument document, double elevation)
    {
        // The floors under it counted up from the ground - whatever is over it, a roof level - one after another.
        var levels = document.Levels.OrderBy(level => level.Elevation).ToList();
        var under = levels.Where(level => level.Elevation < elevation).ToList();
        var counted = under.Select(level => Array.FindIndex(Ordinals, word => string.Equals(level.Name, $"{word} Floor", StringComparison.OrdinalIgnoreCase))).ToList();
        var ordinal = counted.Count > 0 && counted.Select((index, at) => index == counted[0] + at).All(follows => follows) && counted[^1] + 1 < Ordinals.Length
            ? $"{Ordinals[counted[^1] + 1]} Floor"
            : null;
        var below = under.Count;

        var name = ordinal is not null && Levels.IsNameAvailable(document, ordinal)
            ? ordinal
            : below > 0 && below < levels.Count ? $"{levels[below - 1].Name} Mezzanine" : Levels.NextName(document);

        var unique = name;
        for (var n = 2; !Levels.IsNameAvailable(document, unique); n++) unique = $"{name} {n}";
        return unique;
    }

    /// <summary>
    /// The walls running through the floors given - all those through each - and those given
    /// running through any floor, split at them, the lowest floor first, and a storey made at
    /// each floor with a room over it at a height no level is at. Done, as one step to undo,
    /// with what was done; null where nothing runs through anything.
    /// </summary>
    public static (IUndoableCommand Done, string Said)? Divide(BimDocument document, IEnumerable<Element> changed)
    {
        var list = changed.ToList();
        var floors = list.OfType<Slab>().ToHashSet();
        var walls = list.OfType<Wall>().ToHashSet();
        if (floors.Count == 0 && walls.Count == 0) return null;

        var steps = new List<IUndoableCommand>();
        var said = new List<string>();
        foreach (var slab in document.Elements.OfType<Slab>().Where(slab => FloorSurfaces.IsStoodOn(document, slab)).OrderBy(slab => slab.GetTopElevation(document)).ToList())
        {
            // At a floor that changed, every wall through it - and the walls built on it; at any
            // other, only the walls that changed, and what is left of them over a floor under it.
            var changedFloor = floors.Contains(slab);
            var through = changedFloor ? Through(document, slab) : walls.Count == 0 ? Array.Empty<Wall>() : Through(document, slab, walls.ToList());
            var standing = changedFloor ? StandingOn(document, slab) : Array.Empty<Wall>();
            if (through.Count == 0 && standing.Count == 0) continue;

            var (step, uppers, made) = Build(document, slab, through, standing);
            if (step is null) continue;
            step.Redo();
            steps.Add(step);
            foreach (var upper in uppers) walls.Add(upper);

            var floorName = document.FindType<ElementType>(slab.TypeId)?.Name ?? "the floor";
            var split = through.Count == 0 ? null : $"{Walls(through.Count)} running up through it split there";
            said.Add(made is not null
                ? $"{made} made at {slab.GetTopElevation(document):0}, standing on {floorName} and moving with it; {split ?? $"{Walls(standing.Count)} built on it"}, the part over it on {made}"
                : $"{split ?? Walls(through.Count)} running up through {floorName}: the part under it stops at its underside, the part over it stands on its top");
        }

        return steps.Count == 0 ? null : (new CompositeCommand(steps.Count == 1 ? Name(steps[0]) : "Split Walls at Floors", steps), string.Join(". ", said) + ".");
    }

    private static string Name(IUndoableCommand step) => step.Name;

    private static string Walls(int count) => count == 1 ? "A wall" : $"{count} walls";

    /// <summary>
    /// The step that builds the storey at a floor, not yet made: the walls given split there,
    /// and - where the floor has a room over it at a height no level is at - a level at its top,
    /// standing on it. With the walls over the floor it will make, and the level's name.
    /// </summary>
    private static (IUndoableCommand? Step, IReadOnlyList<Wall> Uppers, string? Made) Build(BimDocument document, Slab slab, IReadOnlyList<Wall> through, IReadOnlyList<Wall> standing)
    {
        var top = slab.GetTopElevation(document);
        var steps = new List<IUndoableCommand>();
        var level = document.Levels.FirstOrDefault(candidate => candidate.StandsOn == slab.Id)
                    ?? document.Levels.FirstOrDefault(candidate => Math.Abs(candidate.Elevation - top) < Past);

        string? made = null;
        if (level is null && (through.Any(wall => Span(document, wall).Head >= top + RoomHeight) || standing.Count > 0))
        {
            var storey = new Level { Name = NameAt(document, top), Elevation = top };
            steps.Add(new AddLevelCommand(document, storey));
            steps.Add(new StandOnSlabCommand(new (Action, Action)[] { (() => storey.StandsOn = slab.Id, () => storey.StandsOn = null) }));
            (level, made) = (storey, storey.Name);

            // The walls built on it already, on the storey it is the floor of now.
            steps.AddRange(standing.Select(wall =>
            {
                var was = wall.LevelId;
                return (IUndoableCommand)new StandOnSlabCommand(new (Action, Action)[] { (() => wall.LevelId = storey.Id, () => wall.LevelId = was) });
            }));
        }

        if (through.Count == 0 && made is null) return (null, Array.Empty<Wall>(), null);

        // A wall and its lining split together: the parts over the floor joined to one another, not each to the other's part under it.
        var splits = through.Select(wall => new SplitWallAtFloorCommand(document, wall, slab, level?.Id)).ToList();
        var uppers = splits.ToDictionary(split => split.Wall.Id, split => split.Upper);
        foreach (var split in splits) split.Pair(uppers);
        steps.AddRange(splits);
        return (new CompositeCommand(made is null ? "Split Walls at Floor" : $"Add {made}", steps), splits.Select(split => split.Upper).ToList(), made);
    }
}

/// <summary>
/// A wall split at a floor it runs up through: the wall itself - keeping its id - stopping at
/// the floor's underside, and a new wall, its copy, standing on the floor's top - on the level
/// given - up to where the wall went. The curtain grid, the doors and windows, and what stood
/// on the wall's top go with the part they are in.
/// </summary>
public sealed class SplitWallAtFloorCommand : IUndoableCommand
{
    private readonly BimDocument _document;
    private readonly Wall _wall;
    private readonly (Guid? TopAttachedTo, Guid? TopLevelId, double UnconnectedHeight, IReadOnlyList<WallFaceRegion> Regions) _was;
    private readonly (double Height, IReadOnlyList<WallFaceRegion> Regions) _cut;
    private readonly (CurtainGrid? Grid, IReadOnlyList<CurtainPanelOverride>? Panels, IReadOnlyList<CurtainMullionOverride>? Mullions) _before, _under;
    private readonly List<(Opening Opening, double Sill, Guid Level)> _openings = new();
    private readonly List<(WallOpening Cut, double Sill)> _cuts = new();
    private readonly List<Wall> _standing = new();
    private readonly List<Wall> _linings = new();
    private readonly double _rise;

    public SplitWallAtFloorCommand(BimDocument document, Wall wall, Slab floor, Guid? levelId)
    {
        _document = document;
        _wall = wall;

        // Where the floor is, in heights above the wall's base.
        var (foot, head) = Storeys.Span(document, wall);
        var under = floor.GetBottomElevation(document) - foot;
        _rise = floor.GetTopElevation(document) - foot;

        // The part under it: up to its underside, attached to it - and that high on its own, let go of it, not back up through the part over it.
        _was = (wall.TopAttachedTo, wall.TopLevelId, wall.UnconnectedHeight, wall.FaceRegions);
        _cut = (Math.Max(100, under), wall.FaceRegions
            .Where(region => region.Bottom is not { } bottom || bottom < under)
            .Select(region => region.Top is { } top ? region with { Top = Math.Min(top, under) } : region)
            .ToList());

        // The part over it: standing on its top - on the storey given, or at the floor's height over the wall's own level - up to where the wall went.
        Upper = (Wall)ElementCopy.Clone(wall)!;
        Upper.LevelId = levelId ?? wall.LevelId;
        Upper.BaseOffset = levelId is null ? floor.GetTopElevation(document) - (document.FindLevel(wall.LevelId)?.Elevation ?? 0) : 0;
        Upper.BaseAttachedTo = floor.Id;
        Upper.UnconnectedHeight = Math.Max(100, head - floor.GetTopElevation(document));
        Upper.FaceRegions = wall.FaceRegions
            .Where(region => region.Top is not { } top || top > _rise)
            .Select(region => region with { Bottom = region.Bottom is { } bottom ? Math.Max(0, bottom - _rise) : null, Top = region.Top - _rise })
            .ToList();
        Upper.JoinedTo.Clear();
        Upper.JoinedTo.AddRange(wall.JoinedTo);
        _linings.AddRange(document.Walls.Where(other => other.JoinedTo.Contains(wall.Id)));

        // A curtain wall keeps its grid where it was: the lines and panels under the floor stay, those over it go up with the part over it.
        _before = (wall.CurtainGrid, wall.CurtainPanels, wall.CurtainMullions);
        _under = _before;
        if (CurtainLayout.Of(document, wall) is { } layout)
        {
            var heights = layout.Horizontals;
            var inner = layout.Verticals.Skip(1).SkipLast(1).ToList();
            var lines = heights.Skip(1).SkipLast(1).ToList();
            var removed = wall.CurtainGrid?.Removed ?? Array.Empty<CurtainSegment>();
            var panels = wall.CurtainPanels ?? Array.Empty<CurtainPanelOverride>();

            // The rows under the floor, and the first row - wholly or partly - over it: lines within a millimetre of either face taken as on it.
            var rowsUnder = Enumerable.Range(0, heights.Count - 1).Count(row => heights[row] < under - 1);
            var firstOver = Enumerable.Range(0, heights.Count - 1).Count(row => heights[row + 1] <= _rise + 1);

            // A mullion chosen on its own goes with the part it is in: a transom by its height, an
            // upright by its row - to both parts when the floor runs through that row.
            var (mullionsUnder, mullionsOver) = (new List<CurtainMullionOverride>(), new List<CurtainMullionOverride>());
            foreach (var chosen in wall.CurtainMullions ?? Array.Empty<CurtainMullionOverride>())
            {
                if (!chosen.Vertical)
                {
                    if (chosen.Line < under - 1) mullionsUnder.Add(chosen);
                    else if (chosen.Line > _rise + 1) mullionsOver.Add(chosen with { Line = chosen.Line - _rise });
                    continue;
                }

                var row = Enumerable.Range(0, heights.Count - 1).FirstOrDefault(r => chosen.At <= heights[r + 1] + 1e-6, heights.Count - 2);
                var (low, high) = (heights[row], heights[row + 1]);
                if (low < under - 1e-6) mullionsUnder.Add(chosen with { At = (low + Math.Min(high, under)) / 2 });
                if (high > _rise + 1e-6) mullionsOver.Add(chosen with { At = (Math.Max(low, _rise) + high) / 2 - _rise });
            }

            _under = (new CurtainGrid(inner, lines.Where(h => h < under - 1).ToList()).WithRemoved(removed
                    .Where(s => s.Vertical ? Math.Min(s.From, s.To) < under : s.Line < under)
                    .Select(s => s.Vertical ? s with { From = Math.Min(s.From, s.To), To = Math.Min(Math.Max(s.From, s.To), under) } : s)),
                panels.Where(p => p.Row < rowsUnder).ToList(), wall.CurtainMullions is null ? null : mullionsUnder);
            Upper.CurtainMullions = wall.CurtainMullions is null ? null : mullionsOver;
            Upper.CurtainGrid = new CurtainGrid(inner, lines.Where(h => h > _rise + 1).Select(h => h - _rise).ToList()).WithRemoved(removed
                .Where(s => s.Vertical ? Math.Max(s.From, s.To) > _rise : s.Line > _rise)
                .Select(s => s.Vertical
                    ? s with { From = Math.Max(Math.Min(s.From, s.To), _rise) - _rise, To = Math.Max(s.From, s.To) - _rise }
                    : s with { Line = s.Line - _rise }));
            Upper.CurtainPanels = panels.Where(p => p.Row >= firstOver).Select(p => p with { Row = p.Row - firstOver }).ToList();
        }

        // Doors and windows over the floor's underside go up with the part over it, on its storey, their sills from its base.
        foreach (var opening in WallOpenings.Of(document, wall).Where(opening => opening.SillHeight >= under - 1))
            _openings.Add((opening, opening.SillHeight, opening.LevelId));
        foreach (var cut in document.Elements.OfType<WallOpening>().Where(cut => cut.HostWallId == wall.Id && cut.SillHeight >= under - 1))
            _cuts.Add((cut, cut.SillHeight));

        // What stood on its top stands on the top of the part over the floor.
        _standing.AddRange(document.Walls.Where(other => other.BaseAttachedTo == wall.Id));
    }

    /// <summary>The wall split.</summary>
    public Wall Wall => _wall;

    /// <summary>The part over the floor.</summary>
    public Wall Upper { get; }

    public string Name => "Split Wall at Floor";

    /// <summary>
    /// Split with the walls given - each with its part over the floor: the part over it joined to
    /// theirs, not to what is left of them under it, and they to it.
    /// </summary>
    public void Pair(IReadOnlyDictionary<Guid, Wall> uppers)
    {
        for (var i = 0; i < Upper.JoinedTo.Count; i++)
            if (uppers.TryGetValue(Upper.JoinedTo[i], out var their)) Upper.JoinedTo[i] = their.Id;
        _linings.RemoveAll(lining => uppers.ContainsKey(lining.Id));
    }

    public void Redo()
    {
        _document.Add(Upper);
        (_wall.TopAttachedTo, _wall.TopLevelId, _wall.UnconnectedHeight, _wall.FaceRegions) = (Upper.BaseAttachedTo, null, _cut.Height, _cut.Regions);
        (_wall.CurtainGrid, _wall.CurtainPanels, _wall.CurtainMullions) = _under;
        foreach (var lining in _linings) lining.JoinedTo.Add(Upper.Id);
        foreach (var (opening, sill, _) in _openings) (opening.HostWallId, opening.SillHeight, opening.LevelId) = (Upper.Id, Math.Max(0, sill - _rise), Upper.LevelId);
        foreach (var (cut, sill) in _cuts) (cut.HostWallId, cut.SillHeight) = (Upper.Id, Math.Max(0, sill - _rise));
        foreach (var other in _standing) other.BaseAttachedTo = Upper.Id;
    }

    public void Undo()
    {
        foreach (var other in _standing) other.BaseAttachedTo = _wall.Id;
        foreach (var (cut, sill) in _cuts) (cut.HostWallId, cut.SillHeight) = (_wall.Id, sill);
        foreach (var (opening, sill, level) in _openings) (opening.HostWallId, opening.SillHeight, opening.LevelId) = (_wall.Id, sill, level);
        foreach (var lining in _linings) lining.JoinedTo.Remove(Upper.Id);
        (_wall.CurtainGrid, _wall.CurtainPanels, _wall.CurtainMullions) = _before;
        (_wall.TopAttachedTo, _wall.TopLevelId, _wall.UnconnectedHeight, _wall.FaceRegions) = _was;
        _document.Remove(Upper);
    }
}

/// <summary>
/// A step followed by what it leads to - walls split at a floor just put in - worked out once
/// the step is made, and kept, so redo makes the same again.
/// </summary>
public sealed class FollowedCommand : IUndoableCommand
{
    private readonly IUndoableCommand _first;
    private readonly Func<(IUndoableCommand Done, string Said)?> _then;
    private IUndoableCommand? _followed;
    private bool _asked;

    public FollowedCommand(IUndoableCommand first, Func<(IUndoableCommand Done, string Said)?> then) => (_first, _then) = (first, then);

    public string Name => _first.Name;

    /// <summary>What followed, in words, once it has; null where nothing did.</summary>
    public string? Said { get; private set; }

    public void Redo()
    {
        _first.Redo();
        if (_asked)
        {
            _followed?.Redo();
            return;
        }

        _asked = true;
        if (_then() is { } followed) (_followed, Said) = (followed.Done, followed.Said);
    }

    public void Undo()
    {
        _followed?.Undo();
        _first.Undo();
    }
}
