using BIMDesigner.Core.Materials;

namespace BIMDesigner.Core.Geometry;

/// <summary>A point in the building, in millimetres. Z is up.</summary>
public readonly record struct Point3D(double X, double Y, double Z)
{
    public static Point3D On(Point2D plan, double elevation) => new(plan.X, plan.Y, elevation);
}

/// <summary>What a piece of 3D geometry is, so a viewer can style it without knowing the model.</summary>
public enum MeshKind
{
    Wall,
    Floor,
    Ceiling,
    Roof,
    Glazing,
    DoorLeaf,

    /// <summary>A profile run along a wall face: a skirting, plinth, cornice.</summary>
    Sweep,

    /// <summary>The frame of a curtain wall: mullions along its grid lines.</summary>
    Mullion
}

/// <summary>
/// A triangle mesh belonging to one element, in one colour.
///
/// Faces do not share vertices. Every flat face carries its own corners, so a renderer that
/// works out normals from the triangles gets crisp edges on a wall rather than a wall that
/// looks as if it were moulded from soap.
/// </summary>
public sealed class Mesh3D
{
    private readonly List<Point3D> _positions = new();
    private readonly List<int> _indices = new();
    private readonly List<(Point3D From, Point3D To)> _edges = new();

    public Mesh3D(Guid elementId, Guid levelId, MeshKind kind, ColourRgb colour, string description)
    {
        ElementId = elementId;
        LevelId = levelId;
        Kind = kind;
        Colour = colour;
        Description = description;
    }

    public Guid ElementId { get; }

    /// <summary>The storey it belongs to, so a viewer can hide a whole floor to see inside.</summary>
    public Guid LevelId { get; }

    public MeshKind Kind { get; }

    public ColourRgb Colour { get; }

    public string Description { get; }

    /// <summary>Glass is seen through; everything else is solid.</summary>
    public double Opacity => Kind == MeshKind.Glazing ? 0.35 : 1.0;

    public IReadOnlyList<Point3D> Positions => _positions;

    /// <summary>
    /// Moves every point of the solid, and its edges with it: how a vertical extrusion is made
    /// to lean or taper without being built again.
    /// </summary>
    public void Transform(Func<Point3D, Point3D> map)
    {
        for (var i = 0; i < _positions.Count; i++) _positions[i] = map(_positions[i]);
        for (var i = 0; i < _edges.Count; i++) _edges[i] = (map(_edges[i].From), map(_edges[i].To));
    }

    /// <summary>
    /// Cuts every face that crosses a height into the part below and the part above, and
    /// every edge likewise, so the solid can be bent there by <see cref="Transform"/>: a bent
    /// wall is built straight, cut at its bend, then leaned. The cut becomes an edge of its
    /// own, as the fold it will be.
    /// </summary>
    public void SplitAt(double z)
    {
        const double onPlane = 1e-6;
        var positions = _positions.ToList();
        var indices = _indices.ToList();
        _positions.Clear();
        _indices.Clear();

        static Point3D Cross(Point3D p, Point3D q, double z)
        {
            var t = (z - p.Z) / (q.Z - p.Z);
            return new Point3D(p.X + (q.X - p.X) * t, p.Y + (q.Y - p.Y) * t, z);
        }

        void Fan(IReadOnlyList<Point3D> polygon)
        {
            for (var i = 1; i + 1 < polygon.Count; i++) AddTriangle(polygon[0], polygon[i], polygon[i + 1]);
        }

        for (var t = 0; t + 2 < indices.Count; t += 3)
        {
            var corners = new[] { positions[indices[t]], positions[indices[t + 1]], positions[indices[t + 2]] };
            var sides = corners.Select(p => Math.Abs(p.Z - z) <= onPlane ? 0 : Math.Sign(p.Z - z)).ToArray();

            if (!sides.Contains(-1) || !sides.Contains(1))
            {
                AddTriangle(corners[0], corners[1], corners[2]);
                continue;
            }

            // Walked round the face, each corner goes to its side and each crossing to both.
            var below = new List<Point3D>();
            var above = new List<Point3D>();
            var cut = new List<Point3D>();
            for (var i = 0; i < 3; i++)
            {
                var (p, q) = (corners[i], corners[(i + 1) % 3]);
                var (sp, sq) = (sides[i], sides[(i + 1) % 3]);
                if (sp <= 0) below.Add(p);
                if (sp >= 0) above.Add(p);
                if (sp == 0) cut.Add(p);
                if (sp * sq < 0)
                {
                    var crossing = Cross(p, q, z);
                    below.Add(crossing);
                    above.Add(crossing);
                    cut.Add(crossing);
                }
            }

            Fan(below);
            Fan(above);
            if (cut.Count == 2) AddEdge(cut[0], cut[1]);
        }

        for (var i = _edges.Count - 1; i >= 0; i--)
        {
            var (from, to) = _edges[i];
            if ((from.Z - z) * (to.Z - z) >= 0 || Math.Abs(from.Z - z) <= onPlane || Math.Abs(to.Z - z) <= onPlane) continue;

            var crossing = Cross(from, to, z);
            _edges[i] = (from, crossing);
            _edges.Add((crossing, to));
        }
    }

    /// <summary>Index triples into <see cref="Positions"/>.</summary>
    public IReadOnlyList<int> Indices => _indices;

    /// <summary>
    /// The lines where this solid visibly changes direction: the outline top and bottom, and
    /// the vertical corners between them.
    ///
    /// A shaded model without them reads as one mass, because two faces of similar tone have
    /// nothing between them - which is why architectural views have always drawn the edges
    /// over the shading rather than relying on shading alone.
    ///
    /// They are recorded while the solid is built rather than worked out afterwards from the
    /// triangles: we know each element is an extrusion, so we know exactly which lines matter.
    /// Recovering them later would mean comparing face normals and guessing at a threshold.
    /// </summary>
    public IReadOnlyList<(Point3D From, Point3D To)> Edges => _edges;

    public int TriangleCount => _indices.Count / 3;

    public bool IsEmpty => _indices.Count == 0;

    public void AddTriangle(Point3D a, Point3D b, Point3D c)
    {
        var start = _positions.Count;

        _positions.Add(a);
        _positions.Add(b);
        _positions.Add(c);

        _indices.Add(start);
        _indices.Add(start + 1);
        _indices.Add(start + 2);
    }

    public void AddEdge(Point3D from, Point3D to)
    {
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var dz = to.Z - from.Z;

        if (dx * dx + dy * dy + dz * dz <= 1e-12) return;

        _edges.Add((from, to));
    }

    public void AddQuad(Point3D a, Point3D b, Point3D c, Point3D d)
    {
        AddTriangle(a, b, c);
        AddTriangle(a, c, d);
    }

    /// <summary>
    /// Adds a prism: a plan outline swept straight up from one elevation to another. Walls,
    /// wall layers, slabs and the pieces of wall above and below an opening are all this.
    /// </summary>
    public void AddExtrusion(IReadOnlyList<Point2D> outline, double bottom, double top)
    {
        if (top - bottom <= 1e-6) return;
        AddExtrusion(outline, _ => bottom, _ => top);
    }

    /// <summary>
    /// A prism whose top and bottom need not be level: each corner of the plan outline runs
    /// from its own bottom height to its own top height. For the sloping top of a wall with an
    /// edited profile, where the heights are planes across the outline.
    /// </summary>
    public void AddExtrusion(IReadOnlyList<Point2D> outline, Func<Point2D, double> bottom, Func<Point2D, double> top)
    {
        if (outline.Count < 3) return;

        // Anticlockwise from above, so the caps and sides all face outward.
        var ring = Polygon2D.SignedArea(outline) >= 0 ? outline : outline.Reverse().ToList();
        var low = ring.Select(bottom).ToArray();
        var high = ring.Select(top).ToArray();
        if (Enumerable.Range(0, ring.Count).All(i => high[i] - low[i] <= 1e-6)) return;

        Point3D Low(int i) => Point3D.On(ring[i], low[i]);
        Point3D High(int i) => Point3D.On(ring[i], high[i]);

        for (var i = 0; i < ring.Count; i++)
        {
            var j = (i + 1) % ring.Count;
            if (ring[i].DistanceTo(ring[j]) <= 1e-9) continue;

            AddQuad(Low(i), Low(j), High(j), High(i));

            // The outline at both ends, and the corner joining them - where there is a corner. A
            // curved wall is drawn as many short faces, and a line up each join between them
            // would stripe what is one smooth surface.
            AddEdge(Low(i), Low(j));
            AddEdge(High(i), High(j));
            if (IsCorner(ring, i)) AddEdge(Low(i), High(i));
        }

        foreach (var (i, j, k) in Polygon2D.Triangulate(ring))
        {
            AddTriangle(High(i), High(j), High(k));

            // The underside faces down, so it winds the other way.
            AddTriangle(Low(i), Low(k), Low(j));
        }
    }

    /// <summary>The most an outline may turn at a point and still be one smooth face, in degrees.</summary>
    private const double SmoothTurn = 15;

    /// <summary>Whether an outline turns at a point by more than a curve drawn in pieces does.</summary>
    private static bool IsCorner(IReadOnlyList<Point2D> ring, int i)
    {
        var n = ring.Count;
        var before = ring[i] - ring[(i - 1 + n) % n];
        var after = ring[(i + 1) % n] - ring[i];
        if (before.Length <= 1e-9 || after.Length <= 1e-9) return true;

        var turn = Math.Abs(Math.Atan2(before.Cross(after), before.Dot(after))) * 180 / Math.PI;
        return turn > SmoothTurn;
    }

    /// <summary>
    /// Takes out the edges that are only where two of this mesh's blocks meet flush.
    ///
    /// A wall with a window in it is built as blocks - beside the window, under the sill,
    /// over the head - and each block records its own outline. Where two blocks touch, both
    /// record the same line, and drawn it cuts across what is one flat face of wall. A real
    /// edge - the corner of the reveal, the top of the wall - belongs to one block only. So:
    /// an edge recorded twice is a seam and goes; a vertical corner goes wherever two blocks'
    /// corners overlap, and stays where only one block has it.
    /// </summary>
    public void RemoveSeams()
    {
        const double tolerance = 1e-3;
        static long Round(double value) => (long)Math.Round(value / tolerance);

        var verticals = new Dictionary<(long X, long Y), (Point3D At, List<(double Low, double High)> Spans)>();
        var others = new Dictionary<((long, long, long), (long, long, long)), ((Point3D From, Point3D To) Edge, int Count)>();

        foreach (var (from, to) in _edges)
        {
            if (Math.Abs(from.X - to.X) < tolerance && Math.Abs(from.Y - to.Y) < tolerance)
            {
                var key = (Round(from.X), Round(from.Y));
                if (!verticals.TryGetValue(key, out var entry))
                {
                    entry = (from, new List<(double, double)>());
                    verticals[key] = entry;
                }

                entry.Spans.Add((Math.Min(from.Z, to.Z), Math.Max(from.Z, to.Z)));
                continue;
            }

            var a = (Round(from.X), Round(from.Y), Round(from.Z));
            var b = (Round(to.X), Round(to.Y), Round(to.Z));
            var pair = a.CompareTo(b) <= 0 ? (a, b) : (b, a);
            others[pair] = others.TryGetValue(pair, out var seen) ? (seen.Edge, seen.Count + 1) : ((from, to), 1);
        }

        var kept = others.Values.Where(e => e.Count == 1).Select(e => e.Edge).ToList();

        foreach (var (at, spans) in verticals.Values)
        {
            var heights = spans.SelectMany(s => new[] { s.Low, s.High }).Distinct().OrderBy(z => z).ToList();
            double? runStart = null;
            var runEnd = 0.0;

            for (var i = 0; i + 1 < heights.Count; i++)
            {
                var (low, high) = (heights[i], heights[i + 1]);
                if (high - low <= tolerance) continue;

                var middle = (low + high) / 2;
                var covering = spans.Count(s => s.Low <= middle && s.High >= middle);

                if (covering == 1)
                {
                    if (runStart is null || Math.Abs(runEnd - low) > tolerance) Flush();
                    runStart ??= low;
                    runEnd = high;
                }
                else
                {
                    Flush();
                }
            }

            Flush();

            void Flush()
            {
                if (runStart is { } start && runEnd - start > tolerance)
                    kept.Add((new Point3D(at.X, at.Y, start), new Point3D(at.X, at.Y, runEnd)));
                runStart = null;
            }
        }

        _edges.Clear();
        _edges.AddRange(kept);
    }

    /// <summary>The smallest box around everything, or null for an empty mesh.</summary>
    public (Point3D Min, Point3D Max)? Bounds()
    {
        if (_positions.Count == 0) return null;

        return (
            new Point3D(_positions.Min(p => p.X), _positions.Min(p => p.Y), _positions.Min(p => p.Z)),
            new Point3D(_positions.Max(p => p.X), _positions.Max(p => p.Y), _positions.Max(p => p.Z)));
    }
}
