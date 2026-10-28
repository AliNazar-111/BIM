using System.IO;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Tables;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;
using CSMath;
using CadColor = ACadSharp.Color;

namespace BIMDesigner.Infrastructure.Interoperability;

/// <summary>
/// A DWG or DXF drawing read for working over (any AutoCAD version from R14, AC1014, to 2018 and
/// on, AC1032): its model space's lines, polylines, arcs, circles, ellipses and splines as points
/// along them, blocks put in where they are inserted - turned, scaled and arrayed - dimensions as
/// they are drawn, solids by their outlines, and text; each on its layer, its colour by its layer
/// or its block or its own. Hatches, images, 3D solids and the like are left out, and counted.
/// </summary>
public static class CadImport
{
    /// <summary>A drawing read: what it holds, the units its file says it is in (millimetres a unit), and anything worth saying about it.</summary>
    public sealed record Result(CadDrawing Drawing, double Scale, string Units, IReadOnlyList<string> Notes, IReadOnlyCollection<string> HiddenLayers);

    /// <summary>How many points a full circle is drawn with; an arc with its share of them, at least two.</summary>
    private const int CircleSegments = 72;

    /// <summary>How deep blocks inside blocks are followed.</summary>
    private const int DeepestBlock = 12;

    /// <summary>Reads a DWG or DXF file, by its extension.</summary>
    public static Result Read(string path)
    {
        var warnings = 0;
        void Note(object? sender, NotificationEventArgs e)
        {
            if (e.NotificationType is NotificationType.Warning or NotificationType.Error) warnings++;
        }

        var document = string.Equals(Path.GetExtension(path), ".dwg", StringComparison.OrdinalIgnoreCase)
            ? DwgReader.Read(path, Note)
            : DxfReader.Read(path, Note);
        return Read(document);
    }

    /// <summary>A drawing already read.</summary>
    public static Result Read(CadDocument document)
    {
        var reader = new Reader(document);
        foreach (var entity in document.Entities) reader.Add(entity, Affine.Identity, null, 0);

        var (scale, units) = UnitsOf(document);
        var notes = new List<string>();
        if (reader.Skipped.Count > 0)
            notes.Add("Left out: " + string.Join(", ", reader.Skipped.OrderByDescending(entry => entry.Value).Select(entry => $"{entry.Value:#,0} {entry.Key}")) + ".");
        var hidden = document.Layers.Where(layer => !layer.IsOn || layer.Flags.HasFlag(LayerFlags.Frozen)).Select(layer => layer.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new Result(new CadDrawing(reader.Layers, reader.Paths, reader.Labels), scale, units, notes, hidden);
    }

    /// <summary>The units the file says it is drawn in, as millimetres a unit; unitless taken as millimetres.</summary>
    public static (double Scale, string Units) UnitsOf(CadDocument document) => document.Header.InsUnits switch
    {
        ACadSharp.Types.Units.UnitsType.Inches => (25.4, "Inches"),
        ACadSharp.Types.Units.UnitsType.Feet => (304.8, "Feet"),
        ACadSharp.Types.Units.UnitsType.Centimeters => (10, "Centimetres"),
        ACadSharp.Types.Units.UnitsType.Meters => (1000, "Metres"),
        ACadSharp.Types.Units.UnitsType.Millimeters => (1, "Millimetres"),
        _ => (1, "Millimetres - the file does not say")
    };

    /// <summary>A turn, a scale and a shift in the plane: x' = A x + B y + E, y' = C x + D y + F.</summary>
    private readonly record struct Affine(double A, double B, double C, double D, double E, double F)
    {
        public static readonly Affine Identity = new(1, 0, 0, 1, 0, 0);

        public Point2D Apply(double x, double y) => new(A * x + B * y + E, C * x + D * y + F);

        public Point2D Apply(XYZ point) => Apply(point.X, point.Y);

        /// <summary>This one done after another: other first, then this.</summary>
        public Affine After(Affine other) => new(
            A * other.A + B * other.C, A * other.B + B * other.D,
            C * other.A + D * other.C, C * other.B + D * other.D,
            A * other.E + B * other.F + E, C * other.E + D * other.F + F);

        /// <summary>How much it scales lengths, on average.</summary>
        public double Size => Math.Sqrt(Math.Abs(A * D - B * C));

        /// <summary>How far it turns, radians.</summary>
        public double Turn => Math.Atan2(C, A);

        /// <summary>Whether it mirrors: an arc's way round goes the other way.</summary>
        public bool Mirrors => A * D - B * C < 0;
    }

    private sealed class Reader
    {
        private readonly Dictionary<string, int> _layerIndex = new(StringComparer.OrdinalIgnoreCase);

        public Reader(CadDocument document)
        {
            foreach (var layer in document.Layers) LayerOf(layer);
        }

        public List<CadDrawingLayer> Layers { get; } = new();
        public List<CadPath> Paths { get; } = new();
        public List<CadLabel> Labels { get; } = new();
        public Dictionary<string, int> Skipped { get; } = new();

        private int LayerOf(Layer? layer)
        {
            var name = layer?.Name ?? "0";
            if (_layerIndex.TryGetValue(name, out var index)) return index;
            index = Layers.Count;
            Layers.Add(new CadDrawingLayer(name, Rgb(layer?.Color ?? new CadColor(7)) ?? new ColourRgb(0, 0, 0)));
            _layerIndex[name] = index;
            return index;
        }

        /// <summary>A colour as red, green and blue - an index colour by AutoCAD's table; none by its layer or its block.</summary>
        private static ColourRgb? Rgb(CadColor colour)
        {
            if (colour.IsByLayer || colour.IsByBlock) return null;
            var rgb = colour.GetRgb();
            return rgb.Length >= 3 ? new ColourRgb(rgb[0], rgb[1], rgb[2]) : null;
        }

        /// <summary>
        /// An entity, placed by the blocks it is in: on its own layer - or the block's where it is on
        /// layer 0 - in its own colour, its layer's, or the block's where it is by block.
        /// </summary>
        public void Add(Entity entity, Affine place, Insert? within, int depth, int? blockLayer = null, ColourRgb? blockColour = null)
        {
            if (entity.IsInvisible) return;
            var layer = entity.Layer is { Name: "0" } && blockLayer is { } inherited ? inherited : LayerOf(entity.Layer);
            var colour = entity.Color.IsByBlock ? blockColour : Rgb(entity.Color);

            // Drawn looking up from under the plane - mirrored in AutoCAD - its own x runs the other way.
            var normal = entity switch
            {
                Circle circle => circle.Normal,
                LwPolyline polyline => polyline.Normal,
                Polyline2D polyline => polyline.Normal,
                Solid solid => solid.Normal,
                _ => XYZ.AxisZ
            };
            if (normal.Z < 0) place = place.After(new Affine(-1, 0, 0, 1, 0, 0));

            void Path(IEnumerable<Point2D> points, bool closed)
            {
                var list = points.ToList();
                for (var i = list.Count - 1; i > 0; i--)
                    if (list[i].DistanceTo(list[i - 1]) < 1e-9) list.RemoveAt(i);
                if (list.Count >= 2) Paths.Add(new CadPath(layer, list, closed && list.Count >= 3, colour));
            }

            switch (entity)
            {
                case Line line:
                    Path(new[] { place.Apply(line.StartPoint), place.Apply(line.EndPoint) }, false);
                    break;

                case LwPolyline polyline:
                    Path(Bulged(polyline.Vertices.Select(vertex => (vertex.Location.X, vertex.Location.Y, vertex.Bulge)).ToList(), polyline.IsClosed, place), polyline.IsClosed);
                    break;

                case Polyline2D polyline:
                    Path(Bulged(polyline.Vertices.Select(vertex => (vertex.Location.X, vertex.Location.Y, vertex.Bulge)).ToList(), polyline.IsClosed, place), polyline.IsClosed);
                    break;

                case Polyline3D polyline:
                    Path(polyline.Vertices.Select(vertex => place.Apply(vertex.Location)), polyline.IsClosed);
                    break;

                case Arc arc:
                {
                    var sweep = arc.EndAngle - arc.StartAngle;
                    if (sweep <= 0) sweep += 2 * Math.PI;
                    var steps = Math.Max(2, (int)Math.Ceiling(CircleSegments * sweep / (2 * Math.PI)));
                    Path(Enumerable.Range(0, steps + 1).Select(i =>
                    {
                        var angle = arc.StartAngle + sweep * i / steps;
                        return place.Apply(arc.Center.X + arc.Radius * Math.Cos(angle), arc.Center.Y + arc.Radius * Math.Sin(angle));
                    }), false);
                    break;
                }

                case Circle circle:
                    Path(Enumerable.Range(0, CircleSegments).Select(i =>
                    {
                        var angle = 2 * Math.PI * i / CircleSegments;
                        return place.Apply(circle.Center.X + circle.Radius * Math.Cos(angle), circle.Center.Y + circle.Radius * Math.Sin(angle));
                    }), true);
                    break;

                case Ellipse ellipse:
                    Path(ellipse.PolygonalVertexes(CircleSegments).Select(point => place.Apply(point)), false);
                    break;

                case Spline spline:
                    Path(spline.PolygonalVertexes(Math.Max(CircleSegments, spline.ControlPoints.Count * 8)).Select(point => place.Apply(point)), false);
                    break;

                case Solid solid:
                    Path(new[] { solid.FirstCorner, solid.SecondCorner, solid.FourthCorner, solid.ThirdCorner }.Select(corner => place.Apply(corner)), true);
                    break;

                case Face3D face:
                    Path(new[] { face.FirstCorner, face.SecondCorner, face.ThirdCorner, face.FourthCorner }.Select(corner => place.Apply(corner)), true);
                    break;

                case Leader leader:
                    Path(leader.Vertices.Select(vertex => place.Apply(vertex)), false);
                    break;

                case TextEntity text when !string.IsNullOrWhiteSpace(text.Value):
                    Labels.Add(new CadLabel(layer, place.Apply(text.InsertPoint), text.Height * place.Size, (text.Rotation + place.Turn) * 180 / Math.PI, text.Value.Trim(), colour));
                    break;

                case MText text:
                {
                    var lines = text.GetPlainTextLines().Where(line => !string.IsNullOrWhiteSpace(line)).ToList();
                    for (var i = 0; i < lines.Count; i++)
                    {
                        // Each line under the last, a line and two thirds of its height down.
                        var turn = text.Rotation;
                        var down = -text.Height * 1.667 * (i + 1);
                        var at = place.Apply(text.InsertPoint.X - down * Math.Sin(turn), text.InsertPoint.Y + down * Math.Cos(turn));
                        Labels.Add(new CadLabel(layer, at, text.Height * place.Size, (turn + place.Turn) * 180 / Math.PI, lines[i].Trim(), colour));
                    }

                    break;
                }

                case Insert insert when depth < DeepestBlock && insert.Block is { } block:
                {
                    // The block's base point to the insertion point, turned and scaled - and repeated as an array is.
                    var basePoint = block.BlockEntity?.BasePoint ?? XYZ.Zero;
                    var (cos, sin) = (Math.Cos(insert.Rotation), Math.Sin(insert.Rotation));
                    var columns = Math.Max(1, (int)insert.ColumnCount);
                    var rows = Math.Max(1, (int)insert.RowCount);
                    for (var r = 0; r < rows; r++)
                        for (var c = 0; c < columns; c++)
                        {
                            var shiftX = c * insert.ColumnSpacing;
                            var shiftY = r * insert.RowSpacing;
                            var local = new Affine(insert.XScale, 0, 0, insert.YScale, -basePoint.X * insert.XScale, -basePoint.Y * insert.YScale);
                            var turned = new Affine(cos, -sin, sin, cos,
                                insert.InsertPoint.X + shiftX * cos - shiftY * sin, insert.InsertPoint.Y + shiftX * sin + shiftY * cos);
                            var whole = place.After(turned.After(local));
                            foreach (var inner in block.Entities) Add(inner, whole, insert, depth + 1, layer, colour);
                        }

                    // Its attributes' values, where they show.
                    foreach (var attribute in insert.Attributes.Where(attribute => !attribute.IsInvisible && !string.IsNullOrWhiteSpace(attribute.Value)))
                        Labels.Add(new CadLabel(layer, place.Apply(attribute.InsertPoint), attribute.Height * place.Size, (attribute.Rotation + place.Turn) * 180 / Math.PI, attribute.Value.Trim(), colour));
                    break;
                }

                // A dimension is drawn by its block, in the drawing's own coordinates.
                case Dimension dimension when depth < DeepestBlock && dimension.Block is { } drawn:
                    foreach (var inner in drawn.Entities) Add(inner, place, within, depth + 1, layer, colour);
                    break;

                default:
                    var kind = entity switch
                    {
                        Hatch => "hatches",
                        RasterImage or Wipeout => "images",
                        Viewport => "viewports",
                        Point => "points",
                        _ => $"{entity.ObjectName.ToLowerInvariant()} entities"
                    };
                    if (entity is not (Viewport or AttributeDefinition)) Skipped[kind] = Skipped.GetValueOrDefault(kind) + 1;
                    break;
            }
        }

        /// <summary>A polyline's points, its bulged segments drawn as the arcs they are: a bulge is the tangent of a quarter of the arc's angle.</summary>
        private static List<Point2D> Bulged(List<(double X, double Y, double Bulge)> vertices, bool closed, Affine place)
        {
            var points = new List<Point2D>();
            var count = closed ? vertices.Count : vertices.Count - 1;
            for (var i = 0; i < vertices.Count; i++)
            {
                var (x, y, bulge) = vertices[i];
                points.Add(place.Apply(x, y));
                if (i >= count || Math.Abs(bulge) < 1e-9) continue;
                var (nx, ny, _) = vertices[(i + 1) % vertices.Count];
                var chord = Math.Sqrt((nx - x) * (nx - x) + (ny - y) * (ny - y));
                if (chord < 1e-12) continue;

                // The arc's angle, its radius, and its centre off the chord's middle.
                var angle = 4 * Math.Atan(bulge);
                var radius = chord / (2 * Math.Sin(Math.Abs(angle) / 2));
                var (mx, my) = ((x + nx) / 2, (y + ny) / 2);
                var (ux, uy) = ((nx - x) / chord, (ny - y) / chord);
                var offset = radius * Math.Cos(Math.Abs(angle) / 2) * Math.Sign(bulge);
                var (cx, cy) = (mx - uy * offset, my + ux * offset);
                var start = Math.Atan2(y - cy, x - cx);
                var steps = Math.Max(2, (int)Math.Ceiling(CircleSegments * Math.Abs(angle) / (2 * Math.PI)));
                for (var s = 1; s < steps; s++)
                {
                    var t = start + angle * s / steps;
                    points.Add(place.Apply(cx + radius * Math.Cos(t), cy + radius * Math.Sin(t)));
                }
            }

            return points;
        }
    }
}
