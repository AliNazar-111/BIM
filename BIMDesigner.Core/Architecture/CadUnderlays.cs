using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;
using BIMDesigner.Core.Parameters;

namespace BIMDesigner.Core.Architecture;

/// <summary>A layer of an imported drawing: its name and the colour its lines are drawn in by it.</summary>
public sealed record CadDrawingLayer(string Name, ColourRgb Colour);

/// <summary>A line of an imported drawing - a line, a polyline, an arc or a circle as points along it - on a layer, in its own colour where it has one.</summary>
public sealed record CadPath(int Layer, IReadOnlyList<Point2D> Points, bool Closed, ColourRgb? Colour = null);

/// <summary>A piece of text of an imported drawing: where its baseline starts, its height, its turn in degrees anticlockwise.</summary>
public sealed record CadLabel(int Layer, Point2D At, double Height, double Rotation, string Text, ColourRgb? Colour = null);

/// <summary>
/// What a DWG or DXF drawing holds, in its own units about its own origin: its layers, its lines
/// and its text. Kept as read, so it draws and is saved the same however it is placed.
/// </summary>
public sealed class CadDrawing
{
    public CadDrawing(IReadOnlyList<CadDrawingLayer> layers, IReadOnlyList<CadPath> paths, IReadOnlyList<CadLabel> labels)
    {
        Layers = layers;
        Paths = paths;
        Labels = labels;
        var bounds = BoundingBox2D.Empty;
        foreach (var path in paths)
            foreach (var point in path.Points) bounds = bounds.Include(point);
        foreach (var label in labels) bounds = bounds.Include(label.At);
        Bounds = bounds;
    }

    public IReadOnlyList<CadDrawingLayer> Layers { get; }

    public IReadOnlyList<CadPath> Paths { get; }

    public IReadOnlyList<CadLabel> Labels { get; }

    /// <summary>The box it all lies in, in its own units.</summary>
    public BoundingBox2D Bounds { get; }

    /// <summary>Its line segments in squares of its own units, to find what is near a point without looking at them all.</summary>
    private Dictionary<(int, int), List<(int Path, int Index)>>? _cells;

    private double _cell;

    /// <summary>The segments of its paths near a point, in its own units: each path's index and the segment's first point's.</summary>
    public IEnumerable<(int Path, int Index)> SegmentsNear(Point2D at, double reach)
    {
        if (_cells is null) Index();
        var (x0, y0) = ((int)Math.Floor((at.X - reach) / _cell), (int)Math.Floor((at.Y - reach) / _cell));
        var (x1, y1) = ((int)Math.Floor((at.X + reach) / _cell), (int)Math.Floor((at.Y + reach) / _cell));
        var seen = new HashSet<(int, int)>();
        for (var x = x0; x <= x1; x++)
            for (var y = y0; y <= y1; y++)
                if (_cells!.TryGetValue((x, y), out var found))
                    foreach (var segment in found)
                        if (seen.Add(segment))
                            yield return segment;
    }

    /// <summary>The squares are a hundredth of its larger side, so a few hundred segments fall in each at most.</summary>
    private void Index()
    {
        _cell = Math.Max(Math.Max(Bounds.Width, Bounds.Height) / 100, 1e-6);
        var cells = new Dictionary<(int, int), List<(int, int)>>();
        for (var p = 0; p < Paths.Count; p++)
        {
            var points = Paths[p].Points;
            var count = Paths[p].Closed ? points.Count : points.Count - 1;
            for (var i = 0; i < count; i++)
            {
                var (a, b) = (points[i], points[(i + 1) % points.Count]);
                var (x0, x1) = ((int)Math.Floor(Math.Min(a.X, b.X) / _cell), (int)Math.Floor(Math.Max(a.X, b.X) / _cell));
                var (y0, y1) = ((int)Math.Floor(Math.Min(a.Y, b.Y) / _cell), (int)Math.Floor(Math.Max(a.Y, b.Y) / _cell));

                // A long segment across many squares is kept in the squares along it, not the whole box round it.
                if ((x1 - x0 + 1) * (y1 - y0 + 1) > 64)
                {
                    var steps = Math.Max(x1 - x0, y1 - y0) + 1;
                    for (var s = 0; s <= steps; s++)
                    {
                        var at = a + (b - a) * (s / (double)steps);
                        Add((int)Math.Floor(at.X / _cell), (int)Math.Floor(at.Y / _cell), p, i);
                    }

                    continue;
                }

                for (var x = x0; x <= x1; x++)
                    for (var y = y0; y <= y1; y++) Add(x, y, p, i);
            }
        }

        _cells = cells;

        void Add(int x, int y, int path, int index)
        {
            if (!cells.TryGetValue((x, y), out var list)) cells[(x, y)] = list = new List<(int, int)>();
            if (list.Count == 0 || list[^1] != (path, index)) list.Add((path, index));
        }
    }
}

/// <summary>
/// A DWG or DXF drawing brought onto a storey to work over: a surveyor's site plan, a consultant's
/// layout, an existing building's plans. Its origin is set at a point of the plan, it is turned
/// about that point, and its units are taken as millimetres, metres, inches or feet; it is drawn
/// under the model, faded to grey unless wanted in its own colours, each of its layers shown or
/// hidden. The drawing tools snap to its ends and along its lines, so walls can be traced over
/// it. It is not built, scheduled or exported: it is something to draw on.
/// </summary>
public sealed class CadUnderlay : Element
{
    public override BuiltInCategory Category => BuiltInCategory.ImportedDrawings;

    /// <summary>The file it was read from, as it was then.</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// Whether it is linked to its file rather than kept as it was read: read again from it when the project is
    /// opened and when it is reloaded - its place, turn, units and hidden layers kept - the copy kept in the
    /// project drawn where the file cannot be found.
    /// </summary>
    public bool Linked { get; set; }

    public CadDrawing Drawing { get; set; } = new(Array.Empty<CadDrawingLayer>(), Array.Empty<CadPath>(), Array.Empty<CadLabel>());

    /// <summary>Where on the plan the drawing's origin is, mm.</summary>
    public Point2D Location { get; set; }

    /// <summary>How far it is turned about its origin, degrees anticlockwise.</summary>
    public double Rotation { get; set; }

    /// <summary>How many millimetres a unit of the drawing is: 1 for millimetres, 1000 for metres, 25.4 for inches.</summary>
    public double Scale { get; set; } = 1;

    /// <summary>Whether it is drawn faded, in grey, so the model stands out over it; or in its own colours.</summary>
    public bool Halftone { get; set; } = true;

    /// <summary>The layers of it not shown, by name.</summary>
    public HashSet<string> HiddenLayers { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether a layer of it is shown.</summary>
    public bool Shows(int layer) => layer < 0 || layer >= Drawing.Layers.Count || !HiddenLayers.Contains(Drawing.Layers[layer].Name);

    /// <summary>A point of the drawing, where it is on the plan.</summary>
    public Point2D ToModel(Point2D point)
    {
        var turn = Rotation * Math.PI / 180;
        var (x, y) = (point.X * Scale, point.Y * Scale);
        return new Point2D(Location.X + x * Math.Cos(turn) - y * Math.Sin(turn), Location.Y + x * Math.Sin(turn) + y * Math.Cos(turn));
    }

    /// <summary>A point of the plan, where it is in the drawing.</summary>
    public Point2D ToDrawing(Point2D point)
    {
        var turn = -Rotation * Math.PI / 180;
        var (x, y) = (point.X - Location.X, point.Y - Location.Y);
        return new Point2D((x * Math.Cos(turn) - y * Math.Sin(turn)) / Scale, (x * Math.Sin(turn) + y * Math.Cos(turn)) / Scale);
    }

    /// <summary>Where its drawing lies on the plan: the box round its corners as placed.</summary>
    public BoundingBox2D Bounds
    {
        get
        {
            var box = Drawing.Bounds;
            if (box.IsEmpty) return BoundingBox2D.Empty;
            return BoundingBox2D.Around(new[]
            {
                ToModel(new Point2D(box.MinX, box.MinY)), ToModel(new Point2D(box.MaxX, box.MinY)),
                ToModel(new Point2D(box.MaxX, box.MaxY)), ToModel(new Point2D(box.MinX, box.MaxY))
            });
        }
    }

    /// <summary>The ends and corners of its shown lines within a reach of a point of the plan, and the nearest point on each line, on the plan.</summary>
    public (List<Point2D> Ends, List<Point2D> Along) Near(Point2D at, double reach)
    {
        var ends = new List<Point2D>();
        var along = new List<Point2D>();
        var local = ToDrawing(at);
        var span = reach / Scale;
        foreach (var (p, i) in Drawing.SegmentsNear(local, span))
        {
            var path = Drawing.Paths[p];
            if (!Shows(path.Layer)) continue;
            var (a, b) = (path.Points[i], path.Points[(i + 1) % path.Points.Count]);
            if (a.DistanceTo(local) <= span) ends.Add(ToModel(a));
            if (b.DistanceTo(local) <= span) ends.Add(ToModel(b));
            var way = b - a;
            var length = way.Dot(way);
            if (length <= 0) continue;
            var nearest = a + way * Math.Clamp((local - a).Dot(way) / length, 0, 1);
            if (nearest.DistanceTo(local) <= span) along.Add(ToModel(nearest));
        }

        return (ends, along);
    }

    public override IEnumerable<ParameterValue> GetInstanceParameters(BimDocument document)
    {
        yield return ParameterValue.BindChoice(CadUnderlayParameters.Level,
            () => document.FindLevel(LevelId)?.Name ?? "<none>",
            v => { if (document.Levels.FirstOrDefault(level => level.Name == v) is { } level) LevelId = level.Id; },
            document.Levels.Select(level => level.Name).ToArray());
        yield return ParameterValue.ReadOnly(CadUnderlayParameters.Source, () => System.IO.Path.GetFileName(Source));
        yield return ParameterValue.ReadOnly(CadUnderlayParameters.Linked, () => Linked ? "Linked to its file: read again as the project opens and when reloaded" : "Imported: kept as it was read");
        yield return ParameterValue.BindChoice(CadUnderlayParameters.Units,
            () => CadUnits.Name(Scale),
            v => { if (CadUnits.ScaleOf(v) is { } scale) Scale = scale; },
            CadUnits.Names);
        yield return ParameterValue.BindValidated(CadUnderlayParameters.Rotation, () => Rotation, (double v) =>
        {
            if (!double.IsFinite(v)) return false;
            Rotation = (v % 360 + 360) % 360;
            return true;
        });
        yield return ParameterValue.Bind(CadUnderlayParameters.Halftone, () => Halftone, v => Halftone = v);
        yield return ParameterValue.ReadOnly(CadUnderlayParameters.Layers,
            () => $"{Drawing.Layers.Count(layer => !HiddenLayers.Contains(layer.Name))} of {Drawing.Layers.Count} shown");
        yield return ParameterValue.ReadOnly(CadUnderlayParameters.Contents,
            () => $"{Drawing.Paths.Count:#,0} lines, {Drawing.Labels.Count:#,0} texts");

        foreach (var parameter in GetCommonParameters(document)) yield return parameter;
    }
}

/// <summary>The units a drawing can be taken in, and how many millimetres each is.</summary>
public static class CadUnits
{
    private static readonly (string Name, double Millimetres)[] All =
    {
        ("Millimetres", 1), ("Centimetres", 10), ("Metres", 1000), ("Inches", 25.4), ("Feet", 304.8)
    };

    public static string[] Names => All.Select(unit => unit.Name).ToArray();

    public static string Name(double scale) =>
        All.FirstOrDefault(unit => Math.Abs(unit.Millimetres - scale) < 1e-9).Name ?? $"{scale:0.###} mm a unit";

    public static double? ScaleOf(string? name) =>
        All.FirstOrDefault(unit => string.Equals(unit.Name, name, StringComparison.OrdinalIgnoreCase)) is { Name: not null } unit ? unit.Millimetres : null;
}

public static class CadUnderlayParameters
{
    public static readonly ParameterDefinition Level = new("Level", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition Source = new("File", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.IdentityData);
    public static readonly ParameterDefinition Linked = new("Linked", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.IdentityData);
    public static readonly ParameterDefinition Units = new("Drawing Units", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition Rotation = new("Rotation", ParameterDataType.Angle, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition Halftone = new("Halftone", ParameterDataType.YesNo, ParameterBinding.Instance, ParameterGroup.Graphics);
    public static readonly ParameterDefinition Layers = new("Layers", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Graphics);
    public static readonly ParameterDefinition Contents = new("Contents", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.IdentityData);
}

/// <summary>The layers of an imported drawing hidden, as one step to undo.</summary>
public sealed class SetUnderlayLayersCommand : Documents.Commands.IUndoableCommand
{
    private readonly CadUnderlay _underlay;
    private readonly string[] _before;
    private readonly string[] _after;

    public SetUnderlayLayersCommand(CadUnderlay underlay, IEnumerable<string> hidden)
    {
        _underlay = underlay;
        _before = underlay.HiddenLayers.ToArray();
        _after = hidden.ToArray();
    }

    public string Name => "Imported Drawing Layers";

    public void Redo() => Set(_after);

    public void Undo() => Set(_before);

    private void Set(IEnumerable<string> hidden)
    {
        _underlay.HiddenLayers.Clear();
        _underlay.HiddenLayers.UnionWith(hidden);
    }
}

/// <summary>A linked drawing read again from its file - or from another - as one step to undo.</summary>
public sealed class ReloadUnderlayCommand : Documents.Commands.IUndoableCommand
{
    private readonly CadUnderlay _underlay;
    private readonly (CadDrawing Drawing, string Source, bool Linked) _before;
    private readonly (CadDrawing Drawing, string Source, bool Linked) _after;

    public ReloadUnderlayCommand(CadUnderlay underlay, CadDrawing drawing, string source)
    {
        _underlay = underlay;
        _before = (underlay.Drawing, underlay.Source, underlay.Linked);
        _after = (drawing, source, true);
    }

    public string Name => $"Reload {System.IO.Path.GetFileName(_after.Source)}";

    public void Redo() => (_underlay.Drawing, _underlay.Source, _underlay.Linked) = _after;

    public void Undo() => (_underlay.Drawing, _underlay.Source, _underlay.Linked) = _before;
}
