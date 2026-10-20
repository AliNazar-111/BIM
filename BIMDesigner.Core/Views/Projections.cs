using System.Runtime.CompilerServices;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;
using BIMDesigner.Core.Sheets;

namespace BIMDesigner.Core.Views;

/// <summary>A face of a projected view, painted: its outline on the drawing, its colour, how solid it is.</summary>
public sealed record ProjectedFill(IReadOnlyList<Point2D> Polygon, ColourRgb Colour, double Opacity);

/// <summary>A line of a projected view: an edge, or as much of one as is not hidden - and the element it is an edge of.</summary>
public readonly record struct ProjectedLine(Point2D From, Point2D To, Guid ElementId = default);

/// <summary>A face of a projected view as it lies on the drawing, how far each corner is from the eye, and whose it is: what a click picks.</summary>
public sealed record ProjectedPick(Point2D A, Point2D B, Point2D C, double DepthA, double DepthB, double DepthC, Guid ElementId);

/// <summary>A level across an elevation: its name, and how high it is on the drawing.</summary>
public readonly record struct ProjectedLevel(string Name, double Elevation, double Y);

/// <summary>
/// A view of the model drawn flat: what a 3D view or an elevation looks like on paper. Faces are
/// painted far to near; edges are drawn only where nothing nearer covers them, worked out edge by
/// edge, so the lines are right even where painting alone would put a face over one in front.
/// In the view's own millimetres: across, and up.
/// </summary>
public sealed class ProjectedDrawing
{
    public static readonly ProjectedDrawing Empty = new() { Fills = Array.Empty<ProjectedFill>(), Lines = Array.Empty<ProjectedLine>(), Extent = BoundingBox2D.Empty };

    public required IReadOnlyList<ProjectedFill> Fills { get; init; }

    public required IReadOnlyList<ProjectedLine> Lines { get; init; }

    /// <summary>Round everything drawn, levels and all.</summary>
    public required BoundingBox2D Extent { get; init; }

    /// <summary>The levels across an elevation; none in a 3D view.</summary>
    public IReadOnlyList<ProjectedLevel> Levels { get; init; } = Array.Empty<ProjectedLevel>();

    /// <summary>Where an interior elevation is cropped, to its room; null where nothing is.</summary>
    public BoundingBox2D? Crop { get; init; }

    /// <summary>Every face, to pick from.</summary>
    public IReadOnlyList<ProjectedPick> Picks { get; init; } = Array.Empty<ProjectedPick>();

    /// <summary>The element whose face is nearest the eye at a point of the drawing, or null over nothing.</summary>
    public Guid? ElementAt(Point2D at)
    {
        if (Crop is { } crop && (at.X < crop.MinX || at.X > crop.MaxX || at.Y < crop.MinY || at.Y > crop.MaxY)) return null;

        (Guid Id, double Depth)? nearest = null;
        foreach (var pick in Picks)
        {
            var det = (pick.B.Y - pick.C.Y) * (pick.A.X - pick.C.X) + (pick.C.X - pick.B.X) * (pick.A.Y - pick.C.Y);
            if (Math.Abs(det) < 1e-9) continue;
            var l1 = ((pick.B.Y - pick.C.Y) * (at.X - pick.C.X) + (pick.C.X - pick.B.X) * (at.Y - pick.C.Y)) / det;
            var l2 = ((pick.C.Y - pick.A.Y) * (at.X - pick.C.X) + (pick.A.X - pick.C.X) * (at.Y - pick.C.Y)) / det;
            var l3 = 1 - l1 - l2;
            if (l1 < 0 || l2 < 0 || l3 < 0) continue;

            var depth = l1 * pick.DepthA + l2 * pick.DepthB + l3 * pick.DepthC;
            if (nearest is null || depth < nearest.Value.Depth) nearest = (pick.ElementId, depth);
        }

        return nearest?.Id;
    }

    public bool IsEmpty => Fills.Count == 0 && Lines.Count == 0;
}

/// <summary>
/// Projecting the model onto a view: a 3D view as it is left - camera, style, section box, what it
/// leaves out - an elevation of the building's side, or an interior elevation cropped to its room.
/// What is put on a sheet, where a picture of the screen would not print as lines.
/// </summary>
public static class Projections
{
    /// <summary>Nothing nearer the eye than this is drawn in a perspective, mm.</summary>
    private const double Near = 150;

    /// <summary>The projects whose projections are kept: those open in the window, each until it changes.</summary>
    private static readonly ConditionalWeakTable<BimDocument, Dictionary<ViewReference, ProjectedDrawing>> Kept = new();

    /// <summary>
    /// Keeps a project's projections until <see cref="Forget"/> is called - the window does this
    /// for the project it has open, and forgets at every change, so a sheet being laid out is not
    /// projected again at every move of the mouse. Any other project is projected afresh each time.
    /// </summary>
    public static void Keep(BimDocument document) => Kept.GetValue(document, _ => new Dictionary<ViewReference, ProjectedDrawing>());

    /// <summary>What has been kept for a project thrown away: it has changed. It goes on being kept.</summary>
    public static void Forget(BimDocument document)
    {
        if (Kept.TryGetValue(document, out var kept)) kept.Clear();
    }

    /// <summary>A view projected - kept, for a project whose projections are.</summary>
    public static ProjectedDrawing Of(BimDocument document, ViewReference view)
    {
        if (!Kept.TryGetValue(document, out var kept)) return Project(document, view);
        if (kept.TryGetValue(view, out var drawing)) return drawing;
        return kept[view] = Project(document, view);
    }

    private static ProjectedDrawing Project(BimDocument document, ViewReference view) => view.Kind switch
    {
        ViewKind.Model3D when Views3D.Find(document, view) is { } view3D => ThreeD(document, view3D, view),
        ViewKind.Elevation when view.TargetId == Guid.Empty => Exterior(document, (ElevationSide)Math.Clamp(view.Index, 0, 3), view),
        ViewKind.Elevation when document.FindElement(view.TargetId) is InteriorElevationMarker marker => Interior(document, marker, view.Index, view),
        _ => ProjectedDrawing.Empty
    };

    /// <summary>A 3D view as it was left: its camera - fitted round the model if it has none yet - style, section box and what it shows.</summary>
    public static ProjectedDrawing ThreeD(BimDocument document, View3D view, ViewReference reference)
    {
        var meshes = ModelMeshBuilder.Build(document, document.ViewSettings.FilterFor(document, reference)).Where(view.Shows).ToList();
        IReadOnlyList<Mesh3D> shown = view.ActiveBox is { } box ? SectionBoxes.Clip(meshes, box) : meshes;
        var camera = (view.Camera ?? Fitted(shown)) with { Perspective = view.Perspective };

        // A perspective shows what the eye takes in, in a frame as wide as its field of view.
        BoundingBox2D? frame = null;
        if (camera.Perspective)
        {
            var (width, height) = (camera.Width, camera.Width * 0.625);
            frame = new BoundingBox2D(-width / 2, -height / 2, width / 2, height / 2);
        }

        return Draw(shown, camera, view.Style, null, frame, Array.Empty<ProjectedLevel>());
    }

    /// <summary>A 3D view looking at everything, from the south-west and above, as the {3D} view first opens.</summary>
    public static Camera3D Fitted(IEnumerable<Mesh3D> meshes)
    {
        if (ModelMeshBuilder.Bounds(meshes) is not { } bounds) return Camera3D.FromOrbit(new Point3D(0, 0, 0), -135, 28, 30000, perspective: false);
        var centre = new Point3D((bounds.Min.X + bounds.Max.X) / 2, (bounds.Min.Y + bounds.Max.Y) / 2, (bounds.Min.Z + bounds.Max.Z) / 2);
        var radius = Camera3D.Length(Camera3D.Minus(bounds.Max, bounds.Min)) / 2;
        return Camera3D.FromOrbit(centre, -135, 28, Math.Max(radius / Math.Sin(22.5 * Math.PI / 180) * 1.08, 1000), perspective: false);
    }

    /// <summary>An elevation of one side of the building: straight on, in hidden line, with the levels across it.</summary>
    public static ProjectedDrawing Exterior(BimDocument document, ElevationSide side, ViewReference reference)
    {
        var meshes = ModelMeshBuilder.Build(document, document.ViewSettings.FilterFor(document, reference));
        var outward = Elevations.Outward(side);
        var extents = Elevations.Extents(document);
        var middle = extents is { } e ? new Point2D((e.Min.X + e.Max.X) / 2, (e.Min.Y + e.Max.Y) / 2) : new Point2D(0, 0);
        var reach = extents is { } f ? f.Min.DistanceTo(f.Max) + 10000 : 10000;
        var eye = Point3D.On(middle + outward * reach, 0);
        var camera = new Camera3D(eye, Point3D.On(middle, 0), Perspective: false);

        var levels = document.Levels.Select(level => new ProjectedLevel(level.Name, level.Elevation, level.Elevation)).ToList();
        return Draw(meshes, camera, VisualStyle.HiddenLine, null, null, levels);
    }

    /// <summary>An interior elevation: one wall of a room seen from its marker, cropped to the room, its floor and ceiling.</summary>
    public static ProjectedDrawing Interior(BimDocument document, InteriorElevationMarker marker, int direction, ViewReference reference)
    {
        var frame = InteriorElevations.Frame(document, marker, direction);
        var meshes = ModelMeshBuilder.Build(document, document.ViewSettings.FilterFor(document, reference));
        var floor = document.FindLevel(marker.LevelId)?.Elevation ?? 0;

        var levels = document.Levels
            .Where(level => level.Elevation - floor >= frame.Crop.Min.Y - 1 && level.Elevation - floor <= frame.Crop.Max.Y + 1)
            .Select(level => new ProjectedLevel(level.Name, level.Elevation, level.Elevation - frame.Camera.Eye.Z))
            .ToList();
        var crop = new BoundingBox2D(frame.Crop.Min.X, frame.Crop.Min.Y, frame.Crop.Max.X, frame.Crop.Max.Y);
        return Draw(meshes, frame.Camera, VisualStyle.HiddenLine, frame.Crop, crop, levels);
    }

    /// <summary>
    /// The meshes put into the view's frame - across, up, and away from the eye - cut to the
    /// view's box where it has one, projected, painted far to near, and edged where seen.
    /// </summary>
    private static ProjectedDrawing Draw(IReadOnlyList<Mesh3D> meshes, Camera3D camera, VisualStyle style, Box3D? box, BoundingBox2D? frame, IReadOnlyList<ProjectedLevel> levels)
    {
        var (eye, right, up, look) = (camera.Eye, camera.Right, camera.Up, camera.Look);
        Point3D ToView(Point3D p)
        {
            var d = Camera3D.Minus(p, eye);
            return new Point3D(Camera3D.Dot(d, right), Camera3D.Dot(d, up), Camera3D.Dot(d, look));
        }

        IReadOnlyList<Mesh3D> inView = meshes.Select(mesh => mesh.Mapped(ToView)).ToList();
        // Cut to the box - closed only on the face toward the eye, where the cut is seen; its other faces are edge-on or behind.
        if (box is { } cut) inView = SectionBoxes.Clip(inView, cut, face => face == 4);

        // In perspective, nothing behind the eye or right up against it.
        var perspective = camera.Perspective;
        if (perspective)
        {
            var ahead = new Box3D(new Point3D(-1e12, -1e12, Near), new Point3D(1e12, 1e12, 1e12));
            inView = inView.Select(mesh => mesh.ClippedTo(ahead)).Where(mesh => mesh is not null).Select(mesh => mesh!).ToList();
        }

        var distance = Math.Max(camera.Distance, 1);
        Point2D Flat(Point3D p) => perspective ? new Point2D(p.X * distance / p.Z, p.Y * distance / p.Z) : new Point2D(p.X, p.Y);

        // Every face, flattened, with its depths; how solid and what colour it is drawn.
        var faces = new List<Face>();
        var lines = new List<(Point3D A, Point3D B, Guid Id)>();
        foreach (var mesh in inView)
        {
            var glass = mesh.Kind == MeshKind.Glazing || mesh.Opacity < 1;
            var opacity = style switch
            {
                VisualStyle.XRay => Math.Min(mesh.Opacity, 0.28),
                VisualStyle.Wireframe => 0,
                VisualStyle.HiddenLine => glass ? 0 : 1,
                _ => mesh.Opacity
            };

            var (positions, indices) = (mesh.Positions, mesh.Indices);
            for (var i = 0; i + 2 < indices.Count; i += 3)
            {
                var (a, b, c) = (positions[indices[i]], positions[indices[i + 1]], positions[indices[i + 2]]);
                faces.Add(new Face(a, b, c, Flat(a), Flat(b), Flat(c), Colour(mesh, a, b, c, style), opacity, mesh.Cut, Occludes: !glass && style is not (VisualStyle.Wireframe or VisualStyle.XRay))
                    { ElementId = mesh.ElementId });
            }

            if (mesh.Kind != MeshKind.Glazing || style == VisualStyle.Wireframe)
                lines.AddRange(mesh.Edges.Select(edge => (edge.From, edge.To, mesh.ElementId)));
        }

        var extent = BoundingBox2D.Empty;
        foreach (var face in faces) extent = extent.Include(face.FlatA).Include(face.FlatB).Include(face.FlatC);
        foreach (var (a, b, _) in lines) extent = extent.Include(Flat(a)).Include(Flat(b));
        if (frame is { } framed) extent = framed;
        if (extent.IsEmpty) return ProjectedDrawing.Empty;

        var size = Math.Max(extent.Width, extent.Height);

        // What hides what: the solid faces, sorted over the drawing - what of each face shows is worked
        // out from them, and each edge is drawn as far as nothing nearer covers it.
        var grid = new Grid(extent, faces.Where(face => face.Occludes).ToList(), perspective);
        var fills = new List<(double Depth, ProjectedFill Fill)>();

        // In hidden line only what is cut is painted, grey: the lines are what is seen, each worked out on its own.
        // Each face is painted as exactly what of it shows: itself, less what the faces in front of
        // it cover of it - so what is painted is right however the faces lie, and in no order. In
        // X-ray nothing hides anything: everything is painted, far to near, see-through.
        foreach (var face in faces.Where(face => style == VisualStyle.HiddenLine ? face.Cut : face.Opacity > 0))
        {
            if (style == VisualStyle.XRay)
            {
                Paint(face, size / 8, grid, Flat, 0, fills);
                continue;
            }

            var depth = (face.A.Z + face.B.Z + face.C.Z) / 3;
            foreach (var shows in grid.Visible(face))
                fills.Add((depth, new ProjectedFill(shows, face.Colour, face.Opacity)));
        }

        var step = size / 250;
        var shown = new List<ProjectedLine>();
        foreach (var (a, b, id) in lines)
        {
            var (fa, fb) = (Flat(a), Flat(b));
            if (fa.DistanceTo(fb) < 0.01) continue;
            var pieces = Math.Clamp((int)Math.Ceiling(fa.DistanceTo(fb) / Math.Max(step, 1e-6)), 1, 80);
            int? runStart = null;
            for (var k = 0; k <= pieces; k++)
            {
                var seen = k < pieces && !grid.Hides(Along(a, b, (k + 0.5) / pieces), Flat);
                if (seen && runStart is null) runStart = k;
                if (seen || runStart is not { } start) continue;

                shown.Add(new ProjectedLine(Flat(Along(a, b, (double)start / pieces)), Flat(Along(a, b, (double)k / pieces)), id));
                runStart = null;
            }
        }

        var drawnFills = fills.OrderByDescending(entry => entry.Depth).Select(entry => entry.Fill).ToList();
        var drawnLines = shown;
        if (frame is { } crop)
        {
            drawnFills = drawnFills.Select(fill => fill with { Polygon = ClipToBox(fill.Polygon, crop) }).Where(fill => fill.Polygon.Count >= 3).ToList();
            drawnLines = shown.Select(line => ClipToBox(line, crop)).Where(line => line is not null).Select(line => line!.Value).ToList();
        }

        // The levels across the drawing; the extent wide enough for their names.
        var drawn = BoundingBox2D.Empty;
        foreach (var fill in drawnFills) foreach (var point in fill.Polygon) drawn = drawn.Include(point);
        foreach (var line in drawnLines) drawn = drawn.Include(line.From).Include(line.To);
        if (frame is { } kept) drawn = drawn.Include(kept);
        foreach (var level in levels) drawn = drawn.Include(drawn.IsEmpty ? 0 : drawn.MinX, level.Y);

        return new ProjectedDrawing
        {
            Fills = drawnFills, Lines = drawnLines, Levels = levels,
            Extent = drawn, Crop = box is null ? null : frame,
            Picks = faces.Where(face => face.ElementId != Guid.Empty)
                .Select(face => new ProjectedPick(face.FlatA, face.FlatB, face.FlatC, face.A.Z, face.B.Z, face.C.Z, face.ElementId)).ToList()
        };
    }

    /// <summary>
    /// A face painted as far as it shows: cut in four where it is long on the drawing, so far-off
    /// faces are not painted over near ones; left out where all of it is hidden; cut smaller again
    /// where it is partly hidden, down to a sliver, so what is hidden is painted out.
    /// </summary>
    private static void Paint(Face face, double longest, Grid grid, Func<Point3D, Point2D> flat, int depth, List<(double, ProjectedFill)> into)
    {
        var span = Math.Max(face.FlatA.DistanceTo(face.FlatB), Math.Max(face.FlatB.DistanceTo(face.FlatC), face.FlatC.DistanceTo(face.FlatA)));
        var split = span > longest && depth < 2;
        if (!split)
        {
            var polygon = new[] { face.FlatA, face.FlatB, face.FlatC };
            if (Math.Abs(Polygon2D.SignedArea(polygon)) < 1e-6) return;

            // Its middle, and toward each corner: all hidden, it is not painted; some, it is cut smaller.
            var middle = new Point3D((face.A.X + face.B.X + face.C.X) / 3, (face.A.Y + face.B.Y + face.C.Y) / 3, (face.A.Z + face.B.Z + face.C.Z) / 3);
            var hidden = new[] { middle, Along(middle, face.A, 0.9), Along(middle, face.B, 0.9), Along(middle, face.C, 0.9) }.Count(point => grid.Hides(point, flat));
            if (hidden == 4) return;
            if (hidden == 0 || depth >= 4)
            {
                into.Add((middle.Z, new ProjectedFill(polygon, face.Colour, face.Opacity)));
                return;
            }
        }

        var (ab, bc, ca) = (Along(face.A, face.B, 0.5), Along(face.B, face.C, 0.5), Along(face.C, face.A, 0.5));
        foreach (var (p, q, r) in new[] { (face.A, ab, ca), (ab, face.B, bc), (ca, bc, face.C), (ab, bc, ca) })
            Paint(face with { A = p, B = q, C = r, FlatA = flat(p), FlatB = flat(q), FlatC = flat(r) }, longest, grid, flat, depth + 1, into);
    }

    private static Point3D Along
(Point3D a, Point3D b, double t) => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t);

    /// <summary>The colour a face is painted: shaded by which way it faces, its own, or a cut's dark grey in hidden line.</summary>
    private static ColourRgb Colour(Mesh3D mesh, Point3D a, Point3D b, Point3D c, VisualStyle style)
    {
        if (style == VisualStyle.HiddenLine) return mesh.Cut ? new ColourRgb(0x80, 0x80, 0x80) : new ColourRgb(0xFF, 0xFF, 0xFF);
        if (style != VisualStyle.Shaded) return mesh.Colour;

        // A sun over the left shoulder, as the 3D view is lit.
        var normal = Camera3D.Unit(Camera3D.Cross(Camera3D.Minus(b, a), Camera3D.Minus(c, a)), new Point3D(0, 0, 1));
        var light = Camera3D.Unit(new Point3D(-0.4, 0.6, -0.7), new Point3D(0, 0, -1));
        var lit = 0.5 + 0.5 * Math.Abs(Camera3D.Dot(normal, light));
        return new ColourRgb((byte)(mesh.Colour.R * lit), (byte)(mesh.Colour.G * lit), (byte)(mesh.Colour.B * lit));
    }

    private static IReadOnlyList<Point2D> ClipToBox(IReadOnlyList<Point2D> polygon, BoundingBox2D box)
    {
        var points = polygon.ToList();
        foreach (var (axis, limit, low) in new[] { (0, box.MinX, true), (0, box.MaxX, false), (1, box.MinY, true), (1, box.MaxY, false) })
        {
            if (points.Count == 0) break;
            double Inside(Point2D p)
            {
                var value = axis == 0 ? p.X : p.Y;
                return low ? value - limit : limit - value;
            }

            var next = new List<Point2D>();
            for (var i = 0; i < points.Count; i++)
            {
                var (p, q) = (points[i], points[(i + 1) % points.Count]);
                var (dp, dq) = (Inside(p), Inside(q));
                if (dp >= 0) next.Add(p);
                if (dp >= 0 != dq >= 0) next.Add(p + (q - p) * (dp / (dp - dq)));
            }

            points = next;
        }

        return points;
    }

    private static ProjectedLine? ClipToBox(ProjectedLine line, BoundingBox2D box)
    {
        var (t0, t1) = (0.0, 1.0);
        var d = line.To - line.From;
        foreach (var (p, q) in new[] { (-d.X, line.From.X - box.MinX), (d.X, box.MaxX - line.From.X), (-d.Y, line.From.Y - box.MinY), (d.Y, box.MaxY - line.From.Y) })
        {
            if (Math.Abs(p) < 1e-12)
            {
                if (q < 0) return null;
                continue;
            }

            var t = q / p;
            if (p < 0) t0 = Math.Max(t0, t);
            else t1 = Math.Min(t1, t);
        }

        return t0 >= t1 ? null : new ProjectedLine(line.From + d * t0, line.From + d * t1, line.ElementId);
    }

    /// <summary>A triangle in the view's frame, and flattened onto the drawing.</summary>
    private sealed record Face(Point3D A, Point3D B, Point3D C, Point2D FlatA, Point2D FlatB, Point2D FlatC, ColourRgb Colour, double Opacity, bool Cut, bool Occludes)
    {
        /// <summary>Whose face it is.</summary>
        public Guid ElementId { get; init; }
    }

    /// <summary>The faces that hide what is behind them, sorted into cells over the drawing so a point asks only those over it.</summary>
    private sealed class Grid
    {
        private const int Cells = 64;
        private readonly BoundingBox2D _extent;
        private readonly List<Face>[] _cells = new List<Face>[Cells * Cells];
        private readonly bool _perspective;

        private readonly IReadOnlyList<Face> _faces;

        public Grid(BoundingBox2D extent, IReadOnlyList<Face> faces, bool perspective)
        {
            (_extent, _perspective) = (extent, perspective);
            _faces = faces;
            foreach (var face in faces)
            {
                var (minX, maxX) = (Math.Min(face.FlatA.X, Math.Min(face.FlatB.X, face.FlatC.X)), Math.Max(face.FlatA.X, Math.Max(face.FlatB.X, face.FlatC.X)));
                var (minY, maxY) = (Math.Min(face.FlatA.Y, Math.Min(face.FlatB.Y, face.FlatC.Y)), Math.Max(face.FlatA.Y, Math.Max(face.FlatB.Y, face.FlatC.Y)));
                var (i0, j0, i1, j1) = (Cell(minX, true), Cell(minY, false), Cell(maxX, true), Cell(maxY, false));
                for (var i = i0; i <= i1; i++)
                for (var j = j0; j <= j1; j++)
                    (_cells[i * Cells + j] ??= new List<Face>()).Add(face);
            }
        }

        private int Cell(double value, bool across)
        {
            var (low, span) = across ? (_extent.MinX, _extent.Width) : (_extent.MinY, _extent.Height);
            return Math.Clamp((int)((value - low) / Math.Max(span, 1e-9) * Cells), 0, Cells - 1);
        }

        /// <summary>
        /// What of a face shows on the drawing: itself, less where each face in front of it lies over
        /// it. Two faces that do not pass through each other are one in front of the other all over
        /// where they overlap, so which is in front is told at the middle of the overlap.
        /// </summary>
        public IEnumerable<IReadOnlyList<Point2D>> Visible(Face face)
        {
            IReadOnlyList<Point2D> triangle = new[] { face.FlatA, face.FlatB, face.FlatC };
            var area = Polygon2D.SignedArea(triangle);
            if (Math.Abs(area) < 1e-6) yield break;
            if (area < 0) triangle = new[] { face.FlatA, face.FlatC, face.FlatB };

            var box = BoundingBox2D.Empty.Include(face.FlatA).Include(face.FlatB).Include(face.FlatC);
            var farthest = Math.Max(face.A.Z, Math.Max(face.B.Z, face.C.Z));
            var hidden = new List<IReadOnlyList<Point2D>>();
            foreach (var other in Near(box))
            {
                if (ReferenceEquals(other, face)) continue;

                // Clear of it on the drawing.
                if (Math.Max(other.FlatA.X, Math.Max(other.FlatB.X, other.FlatC.X)) <= box.MinX || Math.Min(other.FlatA.X, Math.Min(other.FlatB.X, other.FlatC.X)) >= box.MaxX ||
                    Math.Max(other.FlatA.Y, Math.Max(other.FlatB.Y, other.FlatC.Y)) <= box.MinY || Math.Min(other.FlatA.Y, Math.Min(other.FlatB.Y, other.FlatC.Y)) >= box.MaxY) continue;

                // Wholly behind it: hides nothing of it.
                if (Math.Min(other.A.Z, Math.Min(other.B.Z, other.C.Z)) >= farthest - Tolerance(farthest)) continue;

                var overlap = Overlap(triangle, other);
                if (overlap.Count < 3 || Math.Abs(Polygon2D.SignedArea(overlap)) < 1e-3) continue;

                var middle = new Point2D(overlap.Average(p => p.X), overlap.Average(p => p.Y));
                if (DepthAt(other, middle) is not { } there || DepthAt(face, middle) is not { } here) continue;
                if (there >= here - Tolerance(here)) continue;

                // Covered whole by one in front: none of it shows.
                if (Math.Abs(Polygon2D.SignedArea(overlap)) >= Math.Abs(area) * (1 - 1e-9)) yield break;
                hidden.Add(overlap);
            }

            if (hidden.Count == 0)
            {
                yield return triangle;
                yield break;
            }

            // The biggest taken away first, so what is left is soon small. Every piece is convex, and
            // so is every part taken away: one less the other is cut off it an edge at a time.
            var left = new List<IReadOnlyList<Point2D>> { triangle };
            foreach (var part in hidden.OrderByDescending(part => Math.Abs(Polygon2D.SignedArea(part))))
            {
                left = Without(left, part);
                if (left.Count == 0) yield break;
            }

            foreach (var piece in left) yield return piece;
        }

        /// <summary>
        /// Convex pieces less a convex part: each piece cut by the part's edges in turn - what is
        /// outside an edge kept, what is inside it cut by the next - and what is inside them all gone.
        /// </summary>
        private static List<IReadOnlyList<Point2D>> Without(List<IReadOnlyList<Point2D>> pieces, IReadOnlyList<Point2D> part)
        {
            var box = BoundingBox2D.Empty;
            foreach (var point in part) box = box.Include(point);

            var left = new List<IReadOnlyList<Point2D>>(pieces.Count + 4);
            foreach (var piece in pieces)
            {
                var (minX, maxX, minY, maxY) = (piece.Min(p => p.X), piece.Max(p => p.X), piece.Min(p => p.Y), piece.Max(p => p.Y));
                if (maxX <= box.MinX || minX >= box.MaxX || maxY <= box.MinY || minY >= box.MaxY)
                {
                    left.Add(piece);
                    continue;
                }

                IReadOnlyList<Point2D> inside = piece;
                for (var k = 0; k < part.Count && inside.Count >= 3; k++)
                {
                    var (p, q) = (part[k], part[(k + 1) % part.Count]);
                    if (p.DistanceTo(q) < 1e-6) continue;
                    var outside = HalfOf(inside, p, q, keepLeft: false);
                    if (outside.Count >= 3 && Math.Abs(Polygon2D.SignedArea(outside)) > 1e-3) left.Add(outside);
                    inside = HalfOf(inside, p, q, keepLeft: true);
                }
            }

            return left;
        }

        /// <summary>What of a convex outline lies to one side of the line through two points.</summary>
        private static IReadOnlyList<Point2D> HalfOf(IReadOnlyList<Point2D> outline, Point2D p, Point2D q, bool keepLeft)
        {
            double Side(Point2D at)
            {
                var side = (q.X - p.X) * (at.Y - p.Y) - (q.Y - p.Y) * (at.X - p.X);
                return keepLeft ? side : -side;
            }

            var kept = new List<Point2D>(outline.Count + 2);
            for (var i = 0; i < outline.Count; i++)
            {
                var (a, b) = (outline[i], outline[(i + 1) % outline.Count]);
                var (sa, sb) = (Side(a), Side(b));
                if (sa >= 0) kept.Add(a);
                if (sa >= 0 != sb >= 0) kept.Add(a + (b - a) * (sa / (sa - sb)));
            }

            return kept;
        }

        /// <summary>The faces that may lie over a box of the drawing: those sorted into the cells it covers.</summary>
        private IEnumerable<Face> Near(BoundingBox2D box)
        {
            var seen = new HashSet<Face>(ReferenceEqualityComparer.Instance);
            var (i0, j0, i1, j1) = (Cell(box.MinX, true), Cell(box.MinY, false), Cell(box.MaxX, true), Cell(box.MaxY, false));

            // Over many cells, the same faces would come up over and over: all of them are looked at once instead.
            if ((i1 - i0 + 1) * (j1 - j0 + 1) > 48)
            {
                foreach (var face in _faces) yield return face;
                yield break;
            }

            for (var i = i0; i <= i1; i++)
            for (var j = j0; j <= j1; j++)
                if (_cells[i * Cells + j] is { } faces)
                    foreach (var face in faces)
                        if (seen.Add(face)) yield return face;
        }

        /// <summary>Where a triangle on the drawing and another face's overlap: one convex outline cut by the other.</summary>
        private static IReadOnlyList<Point2D> Overlap(IReadOnlyList<Point2D> triangle, Face other)
        {
            var clip = new[] { other.FlatA, other.FlatB, other.FlatC };
            if (Polygon2D.SignedArea(clip) < 0) clip = new[] { other.FlatA, other.FlatC, other.FlatB };

            var points = triangle.ToList();
            for (var k = 0; k < 3 && points.Count >= 3; k++)
            {
                var (p, q) = (clip[k], clip[(k + 1) % 3]);
                double Side(Point2D at) => (q.X - p.X) * (at.Y - p.Y) - (q.Y - p.Y) * (at.X - p.X);
                var next = new List<Point2D>(points.Count + 2);
                for (var i = 0; i < points.Count; i++)
                {
                    var (a, b) = (points[i], points[(i + 1) % points.Count]);
                    var (sa, sb) = (Side(a), Side(b));
                    if (sa >= 0) next.Add(a);
                    if (sa >= 0 != sb >= 0) next.Add(a + (b - a) * (sa / (sa - sb)));
                }

                points = next;
            }

            return points;
        }

        /// <summary>How far a face is from the eye at a point of the drawing over it; null off it.</summary>
        private double? DepthAt(Face face, Point2D at)
        {
            var det = (face.FlatB.Y - face.FlatC.Y) * (face.FlatA.X - face.FlatC.X) + (face.FlatC.X - face.FlatB.X) * (face.FlatA.Y - face.FlatC.Y);
            if (Math.Abs(det) < 1e-9) return null;
            var l1 = ((face.FlatB.Y - face.FlatC.Y) * (at.X - face.FlatC.X) + (face.FlatC.X - face.FlatB.X) * (at.Y - face.FlatC.Y)) / det;
            var l2 = ((face.FlatC.Y - face.FlatA.Y) * (at.X - face.FlatC.X) + (face.FlatA.X - face.FlatC.X) * (at.Y - face.FlatC.Y)) / det;
            var l3 = 1 - l1 - l2;
            return _perspective ? 1 / (l1 / face.A.Z + l2 / face.B.Z + l3 / face.C.Z) : l1 * face.A.Z + l2 * face.B.Z + l3 * face.C.Z;
        }

        private static double Tolerance(double depth) => 0.5 + Math.Abs(depth) * 1e-4;

        /// <summary>Whether a face nearer the eye is over a point.</summary>
        public bool Hides(Point3D point, Func<Point3D, Point2D> flat)
        {
            var at = flat(point);
            if (at.X < _extent.MinX || at.X > _extent.MaxX || at.Y < _extent.MinY || at.Y > _extent.MaxY) return false;
            if (_cells[Cell(at.X, true) * Cells + Cell(at.Y, false)] is not { } faces) return false;

            var tolerance = 0.5 + Math.Abs(point.Z) * 1e-4;
            foreach (var face in faces)
            {
                var det = (face.FlatB.Y - face.FlatC.Y) * (face.FlatA.X - face.FlatC.X) + (face.FlatC.X - face.FlatB.X) * (face.FlatA.Y - face.FlatC.Y);
                if (Math.Abs(det) < 1e-9) continue;
                var l1 = ((face.FlatB.Y - face.FlatC.Y) * (at.X - face.FlatC.X) + (face.FlatC.X - face.FlatB.X) * (at.Y - face.FlatC.Y)) / det;
                var l2 = ((face.FlatC.Y - face.FlatA.Y) * (at.X - face.FlatC.X) + (face.FlatA.X - face.FlatC.X) * (at.Y - face.FlatC.Y)) / det;
                var l3 = 1 - l1 - l2;
                if (l1 < 1e-6 || l2 < 1e-6 || l3 < 1e-6) continue;

                // How far the face is there: straight between its corners, or in perspective, its nearness is.
                var depth = _perspective
                    ? 1 / (l1 / face.A.Z + l2 / face.B.Z + l3 / face.C.Z)
                    : l1 * face.A.Z + l2 * face.B.Z + l3 * face.C.Z;
                if (depth < point.Z - tolerance) return true;
            }

            return false;
        }
    }
}
