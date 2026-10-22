using System.Collections.Specialized;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Documents;

/// <summary>
/// What a drawing asks of the model again and again while it is drawn - the walls, a wall's
/// openings, the walls near a point, how each end of a wall is cut - worked out once and kept
/// until the drawing is done.
///
/// A plan, a section or the 3D model of a building of a few thousand walls asks of each wall who
/// it meets at both ends, and of each of those who they meet: looking through the whole model
/// every time is what made a large building take most of a minute to draw. Nothing is kept past
/// the drawing, so nothing can go stale: the model does not change while it is drawn, the next
/// drawing starts again from the model as it then is, and anything added or taken away in the
/// middle of one clears what was kept.
/// </summary>
public sealed class ModelCache : IDisposable
{
    [ThreadStatic] private static ModelCache? _current;

    private readonly BimDocument _document;
    private readonly ModelCache? _outer;
    private readonly Dictionary<Key, object?> _kept = new();
    private Dictionary<Guid, IReadOnlyList<Opening>>? _openings;

    private readonly record struct Key(string What, object A, object? B, object? C);

    private ModelCache(BimDocument document, ModelCache? outer)
    {
        _document = document;
        _outer = outer;
        document.Elements.CollectionChanged += Changed;
    }

    /// <summary>What is kept of this document on this thread, or null when it is not being drawn.</summary>
    public static ModelCache? Of(BimDocument document) =>
        _current is { } cache && ReferenceEquals(cache._document, document) ? cache : null;

    /// <summary>Keeps what is worked out of this document on this thread until disposed - inside another drawing of it, that drawing's.</summary>
    public static IDisposable Begin(BimDocument document) =>
        Of(document) is not null ? Nothing.Instance : _current = new ModelCache(document, _current);

    public void Dispose()
    {
        _document.Elements.CollectionChanged -= Changed;
        if (ReferenceEquals(_current, this)) _current = _outer;
    }

    private void Changed(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _kept.Clear();
        _openings = null;
    }

    /// <summary>The openings hosted in a wall, in order along it.</summary>
    public IReadOnlyList<Opening> OpeningsIn(Guid wall)
    {
        _openings ??= _document.Kept<Opening>()
            .GroupBy(opening => opening.HostWallId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<Opening>)group.OrderBy(opening => opening.DistanceAlongWall).ToList());
        return _openings.GetValueOrDefault(wall) ?? Array.Empty<Opening>();
    }

    /// <summary>Something worked out once for these, and the same answer given every other time it is asked for while drawing.</summary>
    public T Keep<T>(string what, object a, object? b, object? c, Func<T> work)
    {
        var key = new Key(what, a, b, c);
        if (_kept.TryGetValue(key, out var kept)) return (T)kept!;

        var value = work();
        _kept[key] = value;
        return value;
    }

    /// <summary>
    /// The walls of a list whose line comes into a box: found from a grid laid over the plan
    /// rather than by looking at every wall, and given in the list's own order, so whatever takes
    /// the first of them takes the one it would have taken looking at them all.
    /// </summary>
    public IReadOnlyList<Wall> Near(IReadOnlyList<Wall> walls, BoundingBox2D box) =>
        Keep("wall grid", walls, null, null, () => new WallGrid(walls)).Near(box);

    /// <summary>The box a wall's line lies in, curves and all.</summary>
    public static BoundingBox2D Bounds(Wall wall)
    {
        if (!wall.IsCurved) return BoundingBox2D.Empty.Include(wall.Start).Include(wall.End);

        // Close enough points along it that the curve between two of them is never further out than the margin.
        const int pieces = 32;
        var curve = wall.LocationCurve;
        var box = BoundingBox2D.Empty;
        for (var i = 0; i <= pieces; i++) box = box.Include(curve.PointAt(curve.Length * i / pieces));
        return box.Expand(curve.Length / pieces + 1);
    }

    /// <summary>The walls in squares of the plan, each in every square its line crosses the box of.</summary>
    private sealed class WallGrid
    {
        private const double Cell = 2000;

        // More squares than this to a wall or a question, and it is quicker to look at every wall.
        private const long MostCells = 4096;

        private readonly IReadOnlyList<Wall> _walls;
        private readonly Dictionary<(long X, long Y), List<int>> _cells = new();
        private readonly List<int> _everywhere = new();

        public WallGrid(IReadOnlyList<Wall> walls)
        {
            _walls = walls;
            for (var i = 0; i < walls.Count; i++)
            {
                if (Cells(Bounds(walls[i])) is not (var x0, var y0, var x1, var y1))
                {
                    _everywhere.Add(i);
                    continue;
                }

                for (var x = x0; x <= x1; x++)
                for (var y = y0; y <= y1; y++)
                {
                    if (!_cells.TryGetValue((x, y), out var cell)) _cells[(x, y)] = cell = new List<int>();
                    cell.Add(i);
                }
            }
        }

        public IReadOnlyList<Wall> Near(BoundingBox2D box)
        {
            if (Cells(box) is not (var x0, var y0, var x1, var y1)) return _walls;

            var found = new HashSet<int>(_everywhere);
            for (var x = x0; x <= x1; x++)
            for (var y = y0; y <= y1; y++)
                if (_cells.TryGetValue((x, y), out var cell)) found.UnionWith(cell);

            var order = found.ToArray();
            Array.Sort(order);
            return order.Select(i => _walls[i]).ToArray();
        }

        private static (long, long, long, long)? Cells(BoundingBox2D box)
        {
            if (box.IsEmpty || !double.IsFinite(box.MinX) || !double.IsFinite(box.MinY) || !double.IsFinite(box.MaxX) || !double.IsFinite(box.MaxY))
                return null;

            var (x0, y0) = ((long)Math.Floor(box.MinX / Cell), (long)Math.Floor(box.MinY / Cell));
            var (x1, y1) = ((long)Math.Floor(box.MaxX / Cell), (long)Math.Floor(box.MaxY / Cell));
            return (x1 - x0 + 1) * (y1 - y0 + 1) > MostCells ? null : (x0, y0, x1, y1);
        }
    }

    private sealed class Nothing : IDisposable
    {
        public static readonly Nothing Instance = new();

        public void Dispose()
        {
        }
    }
}
