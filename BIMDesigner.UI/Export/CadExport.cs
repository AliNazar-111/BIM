using System.IO;
using System.Windows;
using System.Windows.Media;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Tables;
using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Sheets;
using BIMDesigner.Core.Views;
using BIMDesigner.UI.Rendering;
using CSMath;
using CadColor = ACadSharp.Color;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using Vector = System.Windows.Vector;

namespace BIMDesigner.UI.Export;

/// <summary>
/// Plans and sheets as DWG or DXF (specification section 8), for whoever carries on in a CAD
/// package: a consultant, a setting-out engineer, a planning portal.
///
/// It draws them with the same renderers the screen and the PDF use and writes down what was
/// drawn - lines as polylines, filled areas as solid hatches, text as text - so the file is the
/// drawing that was checked, not a second drawing of the model that could come out different.
/// A plan is written at full size in millimetres, its notes at the size they print at its
/// scale; a sheet at paper size in millimetres. Each element goes on the layer the US National CAD
/// Standard names for its kind - A-WALL, A-DOOR, A-GLAZ, A-ANNO-DIMS - its fills on the -PATT layer
/// beside it, so a CAD user can turn the poché off without losing its outlines; each in the
/// colour and lineweight it is drawn in. DWG is written as AutoCAD 2018 (AC1032), DXF as text.
/// </summary>
public static class CadExport
{
    /// <summary>A plan at full size: model millimetres, its storey's walls, floors, notes and all.</summary>
    public static CadDocument Plan(BimDocument document, Level level, ViewReference? view = null)
    {
        var reference = view ?? ViewReference.FloorPlan(level.Id);
        var scale = Math.Max(1, document.ViewSettings.ScaleOf(reference));
        var renderer = new PlanRenderer(DrawingPalette.Paper)
        {
            Document = document,
            ActiveLevelId = level.Id,
            Filter = document.ViewSettings.FilterFor(document, reference),
            Style = document.ViewSettings.StyleFor(document, reference),
            Discipline = Core.Mep.ServicesPlans.Of(reference),
            DetailLevel = document.ViewSettings.DetailLevelOf(reference) ?? DetailLevel.Fine,
            ShowWallLabels = false,
            View = reference,
            PixelsPerDip = 1,

            // A millimetre of paper is the scale's worth of model: notes come out the size they print.
            PixelsPerPaperMm = scale
        };

        renderer.SetSelection(null);
        renderer.SetTransform(point => new Point(point.X, -point.Y), 1);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) renderer.DrawContent(dc);

        return Write(visual.Drawing, flipAt: 0, unitsPerPaperMm: scale, "A-ANNO");
    }

    /// <summary>A sheet at paper size, in millimetres: its border, title block and every view on it.</summary>
    public static CadDocument Sheet(BimDocument document, Sheet sheet) =>
        Write(SheetExport.Render(document, sheet, 1), flipAt: sheet.Height, unitsPerPaperMm: 1, "G-ANNO-TTLB");

    /// <summary>Whether a file is to be written as DWG rather than DXF, by its extension.</summary>
    public static bool IsDwg(string path) => string.Equals(Path.GetExtension(path), ".dwg", StringComparison.OrdinalIgnoreCase);

    /// <summary>Writes a drawing to a file: DWG or DXF by its extension.</summary>
    public static void Save(CadDocument drawing, string path)
    {
        if (IsDwg(path)) DwgWriter.Write(path, drawing);
        else DxfWriter.Write(path, drawing, false);
    }

    /// <summary>
    /// A drawing written down: its y turned up from <paramref name="flipAt"/>, its pens as
    /// lineweights at <paramref name="unitsPerPaperMm"/> drawing units to the paper millimetre;
    /// what no element was drawn for on <paramref name="otherwise"/>.
    /// </summary>
    public static CadDocument Write(Drawing drawing, double flipAt, double unitsPerPaperMm, string otherwise)
    {
        var cad = new CadDocument(ACadVersion.AC1032);
        cad.Header.InsUnits = ACadSharp.Types.Units.UnitsType.Millimeters;
        var writer = new Writer(cad, flipAt, unitsPerPaperMm);
        writer.Draw(drawing, Matrix.Identity, null, 1, otherwise);
        writer.Flush();
        return cad;
    }

    private sealed class Writer
    {
        private readonly CadDocument _cad;
        private readonly double _flipAt;
        private readonly double _unitsPerPaperMm;
        private readonly Dictionary<string, Layer> _layers = new(StringComparer.OrdinalIgnoreCase);
        private readonly TextStyle _style = new("BIM Sans") { Filename = "segoeui.ttf" };
        private readonly LineType _dashed = new("DASHED") { Description = "Dashed __ __ __" };

        public Writer(CadDocument cad, double flipAt, double unitsPerPaperMm)
        {
            (_cad, _flipAt, _unitsPerPaperMm) = (cad, flipAt, unitsPerPaperMm);
            _cad.TextStyles.Add(_style);

            // DASHED as CAD packages ship it: a dash of 0.5 and a gap of 0.25, scaled to the pen's own dash.
            _dashed.AddSegment(new LineType.Segment { Length = 0.5 });
            _dashed.AddSegment(new LineType.Segment { Length = -0.25 });
            _cad.LineTypes.Add(_dashed);
        }

        private XY Out(Point point) => new(point.X, _flipAt - point.Y);

        public void Draw(System.Windows.Media.Drawing drawing, Matrix transform, Rect? clip, double opacity, string layer)
        {
            switch (drawing)
            {
                case DrawingGroup group:
                {
                    var local = transform;
                    if (group.Transform is { } own) local = own.Value * local;

                    // What it was drawn for says its layer, for it and all in it.
                    var inner = clip;
                    var named = CadLayers.GetLayer(group) ?? (CadLayers.GetElement(group) is { } element ? CadLayers.Of(element) : layer);

                    // Clipped to the bounds of its clip, in the drawing's own coordinates - a
                    // viewport's frame, a callout's crop.
                    if (group.ClipGeometry is { } geometry)
                    {
                        var bounds = Bounds(geometry, local);
                        inner = inner is { } outer ? Rect.Intersect(outer, bounds) : bounds;
                        if (inner is { IsEmpty: true }) return;
                    }

                    foreach (var child in group.Children) Draw(child, local, inner, opacity * group.Opacity, named);
                    break;
                }

                case GeometryDrawing shape when shape.Geometry is not null:
                    Flush();
                    DrawGeometry(shape, transform, clip, opacity, layer);
                    break;

                case GlyphRunDrawing glyphs when glyphs.GlyphRun is not null:
                    DrawText(glyphs, transform, clip, opacity, layer);
                    break;
            }
        }

        private static Rect Bounds(Geometry geometry, Matrix transform)
        {
            var bounds = geometry.Bounds;
            if (bounds.IsEmpty) return Rect.Empty;
            var corners = new[] { bounds.TopLeft, bounds.TopRight, bounds.BottomRight, bounds.BottomLeft }.Select(transform.Transform).ToList();
            var (minX, maxX) = (corners.Min(c => c.X), corners.Max(c => c.X));
            var (minY, maxY) = (corners.Min(c => c.Y), corners.Max(c => c.Y));
            return new Rect(minX, minY, maxX - minX, maxY - minY);
        }

        private static double ScaleOf(Matrix transform)
        {
            var scale = Math.Sqrt(Math.Abs(transform.M11 * transform.M22 - transform.M12 * transform.M21));
            return scale > 0 && double.IsFinite(scale) ? scale : 1;
        }

        /// <summary>The figures of a geometry as point lists in output units, flattened finer than a pen.</summary>
        private List<(List<Point> Points, bool Closed)> Figures(Geometry geometry, Matrix transform)
        {
            var scale = ScaleOf(transform);
            var tolerance = Math.Max(0.02 * _unitsPerPaperMm / scale, 1e-4);
            var figures = new List<(List<Point>, bool)>();

            // WPF flattens only figures that are filled: a line drawn as an open figure - a section's, a revision
            // cloud's, an arc dimension's - would be left out. A copy of it, every figure filled, is flattened instead.
            var whole = PathGeometry.CreateFromGeometry(geometry).CloneCurrentValue();
            foreach (var figure in whole.Figures) figure.IsFilled = true;
            foreach (var figure in whole.GetFlattenedPathGeometry(tolerance, ToleranceType.Absolute).Figures)
            {
                var points = new List<Point> { transform.Transform(figure.StartPoint) };
                foreach (var segment in figure.Segments)
                    switch (segment)
                    {
                        case PolyLineSegment poly:
                            points.AddRange(poly.Points.Select(transform.Transform));
                            break;
                        case LineSegment line:
                            points.Add(transform.Transform(line.Point));
                            break;
                    }

                if (points.Count >= 2) figures.Add((points, figure.IsClosed));
            }

            return figures;
        }

        private static Color? Solid(Brush? brush, double opacity) =>
            brush is SolidColorBrush solid && solid.Color.A * solid.Opacity * opacity > 0.5 ? solid.Color : null;

        /// <summary>A layer by its name - its colour the first drawn on it - and the colour to give what is drawn on it: by its layer where they agree.</summary>
        private (Layer Layer, CadColor Colour) LayerFor(string name, Color colour)
        {
            var own = new CadColor(colour.R, colour.G, colour.B);
            if (!_layers.TryGetValue(name, out var layer))
            {
                layer = new Layer(name) { Color = own };
                _cad.Layers.Add(layer);
                _layers[name] = layer;
            }

            return (layer, layer.Color.Equals(own) ? CadColor.ByLayer : own);
        }

        private void DrawGeometry(GeometryDrawing shape, Matrix transform, Rect? clip, double opacity, string layerName)
        {
            var figures = Figures(shape.Geometry, transform);
            if (figures.Count == 0) return;

            // A fill, but not one as pale as the paper: a backdrop behind a label, the sheet
            // itself - on a CAD screen's dark ground it would hide everything under it.
            if (Solid(shape.Brush, opacity) is { } fill && !(fill.R > 0xF0 && fill.G > 0xF0 && fill.B > 0xF0))
            {
                var rings = figures
                    .Select(figure => clip is { } box ? ClipPolygon(figure.Points, box) : figure.Points)
                    .Where(ring => ring.Count >= 3 && Math.Abs(Area(ring)) > 1e-9)
                    .ToList();
                if (rings.Count > 0)
                {
                    var (layer, colour) = LayerFor($"{layerName}-PATT", fill);
                    var hatch = new Hatch { Layer = layer, Color = colour, IsSolid = true, Pattern = HatchPattern.Solid };
                    foreach (var ring in rings)
                    {
                        var path = new Hatch.BoundaryPath();
                        path.Edges.Add(new Hatch.BoundaryPath.Polyline(ring.Select(point => { var at = Out(point); return new XYZ(at.X, at.Y, 0); }), true));
                        hatch.Paths.Add(path);
                    }

                    _cad.Entities.Add(hatch);
                }
            }

            if (shape.Pen is not { } pen || Solid(pen.Brush, opacity) is not { } stroke) return;

            var (lineLayer, lineColour) = LayerFor(layerName, stroke);
            var weight = Weight(pen.Thickness * ScaleOf(transform) / _unitsPerPaperMm);
            var dashes = pen.DashStyle?.Dashes;
            var dashed = dashes is { Count: > 0 };
            var dashLength = dashed ? Math.Max(dashes![0] * pen.Thickness * ScaleOf(transform), 1e-3) : 0;

            foreach (var (points, closed) in figures)
            {
                var runs = clip is { } box ? ClipPolyline(closed ? points.Append(points[0]).ToList() : points, box) : new List<List<Point>> { points };
                foreach (var run in runs.Where(run => run.Count >= 2))
                {
                    var polyline = new LwPolyline(run.Select(point => (IVector)Out(point)))
                    {
                        Layer = lineLayer,
                        Color = lineColour,
                        LineWeight = weight,
                        IsClosed = clip is null && closed
                    };
                    if (dashed)
                    {
                        polyline.LineType = _dashed;
                        polyline.LineTypeScale = dashLength / 0.5;
                    }

                    _cad.Entities.Add(polyline);
                }
            }
        }

        private void DrawText(GlyphRunDrawing glyphs, Matrix transform, Rect? clip, double opacity, string layerName)
        {
            var run = glyphs.GlyphRun;
            if (Solid(glyphs.ForegroundBrush, opacity) is not { } colour || run.Characters is not { Count: > 0 } characters) return;

            var origin = transform.Transform(run.BaselineOrigin);
            if (clip is { } box && !box.Contains(origin)) return;

            var along = transform.Transform(new Vector(1, 0));
            var scale = ScaleOf(transform);

            // A capital in Segoe UI stands seven tenths of its em: what a CAD text's height is.
            var height = run.FontRenderingEmSize * scale * 0.7;
            var text = new string(characters.ToArray()).Replace("\r", string.Empty).Replace("\n", " ");
            if (text.Length == 0 || height <= 0) return;

            // Turned as it was drawn, up the right way: the page's y is down, the drawing's up.
            var rotation = -Math.Atan2(along.Y, along.X);
            var advance = run.AdvanceWidths.Sum() * scale;
            var direction = along;
            direction.Normalize();
            var end = origin + direction * advance;
            var (layer, textColour) = LayerFor(layerName, colour);

            // WPF breaks a line of text into runs where its characters change kind - "A-101" is
            // three. A run that carries on from the last, on its line, joins it: one text.
            if (_pending is { } last && last.Layer == layer && last.Colour.Equals(textColour) && Math.Abs(last.Height - height) < 1e-6 && Math.Abs(last.Rotation - rotation) < 1e-6
                && (origin - last.End).Length < height * 0.3)
            {
                _pending = last with { Text = last.Text + text, End = end };
                return;
            }

            Flush();
            _pending = new PendingText(text, origin, end, height, rotation, layer, textColour);
        }

        private sealed record PendingText(string Text, Point Origin, Point End, double Height, double Rotation, Layer Layer, CadColor Colour);

        private PendingText? _pending;

        /// <summary>The text being gathered, written.</summary>
        public void Flush()
        {
            if (_pending is not { } text) return;
            _pending = null;
            if (string.IsNullOrWhiteSpace(text.Text)) return;
            var at = Out(text.Origin);
            _cad.Entities.Add(new TextEntity(text.Text.TrimEnd())
            {
                Layer = text.Layer,
                Color = text.Colour,
                Style = _style,
                InsertPoint = new XYZ(at.X, at.Y, 0),
                Height = text.Height,
                Rotation = text.Rotation
            });
        }

        /// <summary>The nearest of the lineweights CAD has to a pen's width on paper.</summary>
        private static LineWeightType Weight(double paperMm)
        {
            var hundredths = paperMm * 100;
            return Enum.GetValues<LineWeightType>().Where(weight => (short)weight > 0)
                .OrderBy(weight => Math.Abs((short)weight - hundredths)).First();
        }

        private static double Area(IReadOnlyList<Point> ring)
        {
            var sum = 0.0;
            for (var i = 0; i < ring.Count; i++)
            {
                var (a, b) = (ring[i], ring[(i + 1) % ring.Count]);
                sum += a.X * b.Y - b.X * a.Y;
            }

            return sum / 2;
        }

        /// <summary>A polygon cut to a box, edge by edge (Sutherland-Hodgman).</summary>
        private static List<Point> ClipPolygon(List<Point> polygon, Rect box)
        {
            var output = polygon;
            foreach (var (inside, cross) in new (Func<Point, bool>, Func<Point, Point, Point>)[]
                     {
                         (p => p.X >= box.Left, (a, b) => Across(a, b, (box.Left - a.X) / (b.X - a.X))),
                         (p => p.X <= box.Right, (a, b) => Across(a, b, (box.Right - a.X) / (b.X - a.X))),
                         (p => p.Y >= box.Top, (a, b) => Across(a, b, (box.Top - a.Y) / (b.Y - a.Y))),
                         (p => p.Y <= box.Bottom, (a, b) => Across(a, b, (box.Bottom - a.Y) / (b.Y - a.Y)))
                     })
            {
                var input = output;
                output = new List<Point>();
                for (var i = 0; i < input.Count; i++)
                {
                    var (current, previous) = (input[i], input[(i + input.Count - 1) % input.Count]);
                    if (inside(current))
                    {
                        if (!inside(previous)) output.Add(cross(previous, current));
                        output.Add(current);
                    }
                    else if (inside(previous)) output.Add(cross(previous, current));
                }

                if (output.Count == 0) break;
            }

            return output;
        }

        private static Point Across(Point a, Point b, double t) => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);

        /// <summary>A polyline cut to a box: the runs of it inside (Liang-Barsky, a segment at a time).</summary>
        private static List<List<Point>> ClipPolyline(List<Point> points, Rect box)
        {
            var runs = new List<List<Point>>();
            List<Point>? current = null;
            for (var i = 0; i + 1 < points.Count; i++)
            {
                if (ClipSegment(points[i], points[i + 1], box) is not { } piece)
                {
                    current = null;
                    continue;
                }

                if (current is null || (current[^1] - piece.From).Length > 1e-9)
                {
                    current = new List<Point> { piece.From };
                    runs.Add(current);
                }

                current.Add(piece.To);
                if ((piece.To - points[i + 1]).Length > 1e-9) current = null;
            }

            return runs;
        }

        private static (Point From, Point To)? ClipSegment(Point a, Point b, Rect box)
        {
            var (t0, t1) = (0.0, 1.0);
            var (dx, dy) = (b.X - a.X, b.Y - a.Y);
            foreach (var (p, q) in new[] { (-dx, a.X - box.Left), (dx, box.Right - a.X), (-dy, a.Y - box.Top), (dy, box.Bottom - a.Y) })
            {
                if (Math.Abs(p) < 1e-12)
                {
                    if (q < 0) return null;
                    continue;
                }

                var t = q / p;
                if (p < 0) t0 = Math.Max(t0, t);
                else t1 = Math.Min(t1, t);
                if (t0 > t1) return null;
            }

            return (Across(a, b, t0), Across(a, b, t1));
        }
    }

    /// <summary>A suggested file name: the project and the plan or sheet, with the extension given.</summary>
    public static string SuggestedFileName(BimDocument document, string what, string extension = ".dwg")
    {
        var name = $"{document.ProjectInformation.Name} - {what}";
        return new string(name.Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray()).Trim() + extension;
    }
}
