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

    private Guid? _ownerId;

    /// <summary>
    /// The element this one is part of, when it is part of something larger: a curtain wall's
    /// panel belongs to the wall. Clicking picks the panel, but selecting the wall lights up
    /// everything that belongs to it. The element itself when nothing owns it.
    /// </summary>
    public Guid OwnerId
    {
        get => _ownerId ?? ElementId;
        init => _ownerId = value;
    }

    /// <summary>The storey it belongs to, so a viewer can hide a whole floor to see inside.</summary>
    public Guid LevelId { get; }

    public MeshKind Kind { get; }

    public ColourRgb Colour { get; }

    public string Description { get; }

    private readonly double? _opacity;

    /// <summary>
    /// Glass is seen through; everything else is solid. A mesh can say otherwise for itself:
    /// frosted glass is cloudy and a spandrel panel is not seen through at all.
    /// </summary>
    public double Opacity
    {
        get => _opacity ?? (Kind == MeshKind.Glazing ? 0.35 : 1.0);
        init => _opacity = Math.Clamp(value, 0.05, 1);
    }

    public IReadOnlyList<Point3D> Positions => _positions;

    /// <summary>
    /// Moves every point of the solid, and its edges with it: how a vertical extrusion is made
    /// to lean or taper without being built again.
    /// </summary>
    public void Transform(Func<Point3D, Point3D> map)
    {
        // Moved points face new ways; the view works them out again from the faces.
        _normals = null;

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
        _normals = null;
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

    private int AddPosition(Point3D point)
    {
        _positions.Add(point);
        return _positions.Count - 1;
    }

    /// <summary>A triangle by the indices of points already added, wound to face <paramref name="outward"/>.</summary>
    private void AddFacing(int a, int b, int c, Point3D outward)
    {
        var (pa, pb, pc) = (_positions[a], _positions[b], _positions[c]);
        var (ux, uy, uz) = (pb.X - pa.X, pb.Y - pa.Y, pb.Z - pa.Z);
        var (vx, vy, vz) = (pc.X - pa.X, pc.Y - pa.Y, pc.Z - pa.Z);
        var facing = (uy * vz - uz * vy) * outward.X + (uz * vx - ux * vz) * outward.Y + (ux * vy - uy * vx) * outward.Z;

        _indices.Add(a);
        _indices.Add(facing >= 0 ? b : c);
        _indices.Add(facing >= 0 ? c : b);
    }

    /// <summary>
    /// A surface swept straight across between two matching rows of points - one end of a
    /// curved roof and the other. Where a join is marked smooth the strips either side share
    /// their corners, so the surface shades as one curve rather than a row of flat bands; where
    /// it is not, they meet at a crease. Every strip faces the way <paramref name="outward"/> says.
    /// </summary>
    public void AddSweep(IReadOnlyList<Point3D> near, IReadOnlyList<Point3D> far, IReadOnlyList<bool> smooth, Point3D outward)
    {
        int? sharedNear = null;
        int? sharedFar = null;

        for (var i = 0; i + 1 < near.Count && i + 1 < far.Count; i++)
        {
            var joined = i > 0 && i < smooth.Count && smooth[i] && sharedNear is not null;
            var n0 = joined ? sharedNear!.Value : AddPosition(near[i]);
            var f0 = joined ? sharedFar!.Value : AddPosition(far[i]);
            var n1 = AddPosition(near[i + 1]);
            var f1 = AddPosition(far[i + 1]);

            AddFacing(n0, n1, f1, outward);
            AddFacing(n0, f1, f0, outward);

            sharedNear = n1;
            sharedFar = f1;
        }
    }

    /// <summary>A flat face given as a polygon and its triangles, wound to face <paramref name="outward"/>.</summary>
    public void AddFace(IReadOnlyList<Point3D> polygon, IReadOnlyList<(int A, int B, int C)> triangles, Point3D outward)
    {
        foreach (var (a, b, c) in triangles)
            AddFacing(AddPosition(polygon[a]), AddPosition(polygon[b]), AddPosition(polygon[c]), outward);
    }

    /// <summary>A flat four-sided face, wound to face <paramref name="outward"/>.</summary>
    public void AddFace(Point3D a, Point3D b, Point3D c, Point3D d, Point3D outward) =>
        AddFace(new[] { a, b, c, d }, new[] { (0, 1, 2), (0, 2, 3) }, outward);

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
    public void AddExtrusion(IReadOnlyList<Point2D> outline, Func<Point2D, double> bottom, Func<Point2D, double> top) =>
        AddExtrusion(outline, bottom, top, null);

    /// <summary>
    /// As above, with the top and bottom marked as part of a curved surface - a cone built
    /// from flat faces - so <see cref="SmoothGroups"/> can shade them as one.
    /// </summary>
    public void AddExtrusion(IReadOnlyList<Point2D> outline, Func<Point2D, double> bottom, Func<Point2D, double> top, int? smoothGroup)
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
            if (ring.Count <= DetailedLoop && IsCorner(ring, i)) AddEdge(Low(i), High(i));
        }

        foreach (var (i, j, k) in Polygon2D.Triangulate(ring))
        {
            if (smoothGroup is { } group) _smoothTriangles[TriangleCount] = group;
            AddTriangle(High(i), High(j), High(k));

            // The underside faces down, so it winds the other way.
            if (smoothGroup is { } under) _smoothTriangles[TriangleCount] = under;
            AddTriangle(Low(i), Low(k), Low(j));
        }
    }

    /// <summary>Triangles that may share corners with their neighbours in the same group, by triangle.</summary>
    private readonly Dictionary<int, int> _smoothTriangles = new();

    private Point3D[]? _normals;

    /// <summary>
    /// Which way the surface faces at each point, for shading - given where a curved surface
    /// was built from flat faces, so it is shaded as the curve it is. Null when every face is
    /// simply shaded flat, or by the corners it shares.
    /// </summary>
    public IReadOnlyList<Point3D>? Normals => _normals;

    /// <summary>
    /// Shades each curved surface as one. Each corner of a face in a group is shaded facing the
    /// average of that face and the faces of the group around the same point that face nearly
    /// the same way - so the shading runs on across every join instead of showing a band per
    /// flat face, and at a point where many faces meet, as at the top of a cone, each face is
    /// averaged only with those beside it. A real crease - more than the angle given - stays
    /// sharp, and every face not in a group is shaded flat.
    /// </summary>
    public void SmoothGroups(double maxDegrees = 25)
    {
        if (_smoothTriangles.Count == 0) return;

        var limit = Math.Cos(maxDegrees * Math.PI / 180);
        const double tolerance = 1e-3;

        (long, long, long) Key(Point3D p) =>
            ((long)Math.Round(p.X / tolerance), (long)Math.Round(p.Y / tolerance), (long)Math.Round(p.Z / tolerance));

        var faces = new Point3D[TriangleCount];
        for (var t = 0; t < TriangleCount; t++)
            faces[t] = FaceNormal(t);

        // Every point of every grouped face, with the faces of that group touching it.
        var around = new Dictionary<(int Group, (long, long, long) At), List<Point3D>>();
        foreach (var (triangle, group) in _smoothTriangles)
        {
            if (faces[triangle] is { X: 0, Y: 0, Z: 0 }) continue;

            for (var k = 0; k < 3; k++)
            {
                var key = (group, Key(_positions[_indices[triangle * 3 + k]]));
                if (!around.TryGetValue(key, out var list)) around[key] = list = new List<Point3D>();
                list.Add(faces[triangle]);
            }
        }

        _normals = new Point3D[_positions.Count];

        for (var t = 0; t < TriangleCount; t++)
        {
            var face = faces[t];
            var grouped = _smoothTriangles.TryGetValue(t, out var group);

            for (var k = 0; k < 3; k++)
            {
                var index = _indices[t * 3 + k];

                if (!grouped || !around.TryGetValue((group, Key(_positions[index])), out var neighbours))
                {
                    _normals[index] = face;
                    continue;
                }

                var (x, y, z) = (0.0, 0.0, 0.0);
                foreach (var other in neighbours)
                {
                    if (other.X * face.X + other.Y * face.Y + other.Z * face.Z < limit) continue;
                    (x, y, z) = (x + other.X, y + other.Y, z + other.Z);
                }

                var length = Math.Sqrt(x * x + y * y + z * z);
                _normals[index] = length < 1e-12 ? face : new Point3D(x / length, y / length, z / length);
            }
        }
    }

    /// <summary>The way a triangle faces, as a unit vector; nothing for one with no area.</summary>
    private Point3D FaceNormal(int triangle)
    {
        var first = triangle * 3;
        var (a, b, c) = (_positions[_indices[first]], _positions[_indices[first + 1]], _positions[_indices[first + 2]]);
        var (ux, uy, uz) = (b.X - a.X, b.Y - a.Y, b.Z - a.Z);
        var (vx, vy, vz) = (c.X - a.X, c.Y - a.Y, c.Z - a.Z);
        var (nx, ny, nz) = (uy * vz - uz * vy, uz * vx - ux * vz, ux * vy - uy * vx);
        var length = Math.Sqrt(nx * nx + ny * ny + nz * nz);
        return length < 1e-12 ? default : new Point3D(nx / length, ny / length, nz / length);
    }

    /// <summary>
    /// An outline with holes through it, run between two heights: the walls of the shape, the
    /// walls of each hole, and caps that go round the holes rather than over them.
    ///
    /// A hole cut in a profile has to be a hole in the solid too. Capping over it would leave a
    /// column that reads as hollow in plan and solid in the 3D view - the two views disagreeing
    /// about the same object, which is the one thing a model must never do.
    /// </summary>
    public void AddExtrusion(
        IReadOnlyList<Point2D> outline, IReadOnlyList<IReadOnlyList<Point2D>> holes, double bottom, double top)
    {
        if (outline.Count < 3 || top - bottom <= 1e-6) return;

        var solid = holes.Where(hole => hole.Count >= 3).ToList();
        if (solid.Count == 0)
        {
            AddExtrusion(outline, bottom, top);
            return;
        }

        // The walls: the outside facing out, and each hole facing in, which is why its loop is
        // wound the other way.
        Walls(Anticlockwise(outline, true), bottom, top);
        foreach (var hole in solid) Walls(Anticlockwise(hole, false), bottom, top);

        // The caps: one simple outline that goes round every hole, so ear clipping can cut it.
        var bridged = Polygon2D.BridgeHoles(Anticlockwise(outline, true), solid.Select(hole => Anticlockwise(hole, true)).ToList());

        foreach (var (i, j, k) in Polygon2D.Triangulate(bridged))
        {
            AddTriangle(Point3D.On(bridged[i], top), Point3D.On(bridged[j], top), Point3D.On(bridged[k], top));
            AddTriangle(Point3D.On(bridged[i], bottom), Point3D.On(bridged[k], bottom), Point3D.On(bridged[j], bottom));
        }
    }

    /// <summary>
    /// A solid lofted through a stack of outlines at rising heights: the walls run from each
    /// ring to the next, and the ends are capped.
    ///
    /// This is what lets one description serve a straight column, a tapered one, a twisted one
    /// and one built in parts. Each ring is the same outline transformed, so the points line up
    /// one to one and the wall between two rings is a strip of quads; where a ring has a
    /// different number of points - a base spread wider than the shaft - the strip is skipped
    /// and the two are capped against each other instead, which is the step that belongs there.
    /// </summary>
    public void AddLoft(IReadOnlyList<(IReadOnlyList<Point2D> Outer, IReadOnlyList<IReadOnlyList<Point2D>> Holes, double Elevation)> rings)
    {
        var solid = rings.Where(ring => ring.Outer.Count >= 3).ToList();
        if (solid.Count == 0) return;

        if (solid.Count == 1)
        {
            AddExtrusion(solid[0].Outer, solid[0].Holes, solid[0].Elevation, solid[0].Elevation);
            return;
        }

        for (var i = 0; i + 1 < solid.Count; i++)
        {
            var below = solid[i];
            var above = solid[i + 1];
            if (above.Elevation - below.Elevation <= 1e-9) continue;

            // The outside, then each hole - which faces the other way, being a wall seen from
            // inside the column.
            Band(Anticlockwise(below.Outer, true), Anticlockwise(above.Outer, true), below.Elevation, above.Elevation);

            for (var h = 0; h < Math.Min(below.Holes.Count, above.Holes.Count); h++)
                Band(Anticlockwise(below.Holes[h], false), Anticlockwise(above.Holes[h], false),
                    below.Elevation, above.Elevation);

            // Where one ring has more points than the next, the change is a step: cap the
            // difference so the solid closes rather than leaving a gap at the join.
            if (below.Outer.Count != above.Outer.Count || below.Holes.Count != above.Holes.Count)
            {
                Cap(below.Outer, below.Holes, below.Elevation, up: true);
                Cap(above.Outer, above.Holes, above.Elevation, up: false);
            }
        }

        // The two ends of the whole thing.
        Cap(solid[0].Outer, solid[0].Holes, solid[0].Elevation, up: false);
        Cap(solid[^1].Outer, solid[^1].Holes, solid[^1].Elevation, up: true);
    }

    /// <summary>The wall between one loop and the loop above it, point for point.</summary>
    private void Band(IReadOnlyList<Point2D> below, IReadOnlyList<Point2D> above, double low, double high)
    {
        if (below.Count != above.Count) return;

        for (var i = 0; i < below.Count; i++)
        {
            var j = (i + 1) % below.Count;
            if (below[i].DistanceTo(below[j]) <= 1e-9 && above[i].DistanceTo(above[j]) <= 1e-9) continue;

            AddQuad(Point3D.On(below[i], low), Point3D.On(below[j], low),
                Point3D.On(above[j], high), Point3D.On(above[i], high));

            AddEdge(Point3D.On(below[i], low), Point3D.On(below[j], low));
            AddEdge(Point3D.On(above[i], high), Point3D.On(above[j], high));

            // A corner gets a line up it - but a section modelled in fine detail, a fluted
            // shaft say, has hundreds of them, and drawing every one turns the column into a
            // thicket of lines instead of a column. Past that, the shape carries itself.
            if (below.Count <= DetailedLoop && IsCorner(below, i))
                AddEdge(Point3D.On(below[i], low), Point3D.On(above[i], high));
        }
    }

    /// <summary>One end of a loft, facing up or down, with its holes left out.</summary>
    private void Cap(IReadOnlyList<Point2D> outer, IReadOnlyList<IReadOnlyList<Point2D>> holes, double z, bool up)
    {
        var ring = Anticlockwise(outer, true);
        var solid = holes.Where(hole => hole.Count >= 3).Select(hole => Anticlockwise(hole, true)).ToList();
        var outline = solid.Count == 0 ? ring : Polygon2D.BridgeHoles(ring, solid);

        foreach (var (i, j, k) in Polygon2D.Triangulate(outline))
        {
            if (up) AddTriangle(Point3D.On(outline[i], z), Point3D.On(outline[j], z), Point3D.On(outline[k], z));
            else AddTriangle(Point3D.On(outline[i], z), Point3D.On(outline[k], z), Point3D.On(outline[j], z));
        }
    }

    private static IReadOnlyList<Point2D> Anticlockwise(IReadOnlyList<Point2D> loop, bool anticlockwise) =>
        Polygon2D.SignedArea(loop) >= 0 == anticlockwise ? loop : loop.Reverse().ToList();

    /// <summary>One loop's worth of wall, with the lines round its top and bottom and at its corners.</summary>
    private void Walls(IReadOnlyList<Point2D> ring, double bottom, double top)
    {
        for (var i = 0; i < ring.Count; i++)
        {
            var j = (i + 1) % ring.Count;
            if (ring[i].DistanceTo(ring[j]) <= 1e-9) continue;

            AddQuad(Point3D.On(ring[i], bottom), Point3D.On(ring[j], bottom),
                Point3D.On(ring[j], top), Point3D.On(ring[i], top));

            AddEdge(Point3D.On(ring[i], bottom), Point3D.On(ring[j], bottom));
            AddEdge(Point3D.On(ring[i], top), Point3D.On(ring[j], top));
            if (ring.Count <= DetailedLoop && IsCorner(ring, i)) AddEdge(Point3D.On(ring[i], bottom), Point3D.On(ring[i], top));
        }
    }

    /// <summary>The most an outline may turn at a point and still be one smooth face, in degrees.</summary>
    private const double SmoothTurn = 15;

    /// <summary>Past this many points a loop is modelled detail rather than a shape with corners.</summary>
    private const int DetailedLoop = 48;

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
