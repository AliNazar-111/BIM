using System.Numerics;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;

namespace BIMDesigner.Core.Rendering;

/// <summary>What a surface of a rendering is: its colour or pattern, how it reflects, whether light passes it.</summary>
internal sealed class SceneMaterial
{
    /// <summary>Its colour as light: the share of each of red, green and blue it gives back.</summary>
    public Vector3 Albedo;

    public SurfaceTexture? Texture;

    public float Reflectance;

    /// <summary>Glass: light passes straight through it, a little of it reflected.</summary>
    public bool Glass;

    /// <summary>How much of what is behind a faded thing it hides, 0 to 1.</summary>
    public float Opacity = 1;

    /// <summary>How far its pattern stands proud, mm: a joint's depth, a slate's thickness.</summary>
    public float Relief;

    /// <summary>Ground cover - grass, gravel, planting - lighter and darker across the ground, so its pattern's repeat is lost.</summary>
    public bool Varies;
}

/// <summary>
/// The model as a rendering sees it: every triangle of what the 3D view shows, in metres about its
/// middle, with what each is made of - and the ground, ten kilometres of it each way - sorted into
/// a bounding volume hierarchy, so a ray finds what it hits among a
/// million triangles in a few dozen tests.
/// </summary>
public sealed class RenderScene
{
    private Vector3[] _a = Array.Empty<Vector3>(), _e1 = Array.Empty<Vector3>(), _e2 = Array.Empty<Vector3>();
    internal Vector3[] Normal = Array.Empty<Vector3>();
    internal int[] MaterialOf = Array.Empty<int>();

    /// <summary>A curved surface's normals at each triangle's corners, three to a triangle; -1 for a flat one.</summary>
    internal int[] SmoothOf = Array.Empty<int>();
    internal Vector3[] Smooth = Array.Empty<Vector3>();
    internal SceneMaterial[] Materials = Array.Empty<SceneMaterial>();
    private Node[] _nodes = Array.Empty<Node>();

    /// <summary>The middle of the model, mm: where the scene's metres are measured from.</summary>
    public (double X, double Y, double Z) Origin { get; private set; }

    public int TriangleCount => _a.Length;

    private struct Node
    {
        public Vector3 Min, Max;

        /// <summary>A leaf's first triangle and how many; a branch's first child, the other after it, and no triangles.</summary>
        public int Start, Count;
    }

    /// <summary>A point of the model, mm, in the scene's metres.</summary>
    public Vector3 ToScene(Point3D point) => new((float)((point.X - Origin.X) / 1000), (float)((point.Y - Origin.Y) / 1000), (float)((point.Z - Origin.Z) / 1000));

    /// <summary>
    /// The scene of the meshes: each in its material's finish, found by the name it is described by;
    /// with the ground under it when <paramref name="ground"/> - ten kilometres of grass a little
    /// below the project's zero, or below the lowest of its own ground.
    /// </summary>
    public static RenderScene Build(IReadOnlyList<Mesh3D> meshes, IReadOnlyDictionary<string, Material> materials, bool ground)
    {
        var scene = new RenderScene();
        var (min, max) = (new Point3D(double.MaxValue, double.MaxValue, double.MaxValue), new Point3D(double.MinValue, double.MinValue, double.MinValue));
        foreach (var point in meshes.SelectMany(mesh => mesh.Positions))
        {
            min = new Point3D(Math.Min(min.X, point.X), Math.Min(min.Y, point.Y), Math.Min(min.Z, point.Z));
            max = new Point3D(Math.Max(max.X, point.X), Math.Max(max.Y, point.Y), Math.Max(max.Z, point.Z));
        }

        scene.Origin = min.X <= max.X ? ((min.X + max.X) / 2, (min.Y + max.Y) / 2, (min.Z + max.Z) / 2) : (0, 0, 0);

        var a = new List<Vector3>();
        var e1 = new List<Vector3>();
        var e2 = new List<Vector3>();
        var normals = new List<Vector3>();
        var materialOf = new List<int>();
        var smoothOf = new List<int>();
        var smooth = new List<Vector3>();
        var sceneMaterials = new List<SceneMaterial>();
        var byLook = new Dictionary<(SurfaceFinish, bool, float), int>();

        int MaterialFor(SurfaceFinish finish, bool glass, float opacity)
        {
            var key = (finish, glass, opacity);
            if (byLook.TryGetValue(key, out var index)) return index;
            var albedo = Linear(finish.Colour);
            sceneMaterials.Add(new SceneMaterial
            {
                Albedo = Vector3.Min(albedo, new Vector3(0.9f)),
                Texture = glass ? null : SurfaceTextures.Of(finish),
                Reflectance = (float)Math.Clamp(finish.Reflectance, 0, 1),
                Glass = glass,
                Opacity = glass ? 1 : opacity,
                Relief = Relief(finish.Pattern),
                Varies = finish.Pattern is SurfacePattern.Grass or SurfacePattern.Gravel or SurfacePattern.Planting
            });
            return byLook[key] = sceneMaterials.Count - 1;
        }

        void Add(Vector3 p, Vector3 q, Vector3 r, int material, Vector3? np = null, Vector3? nq = null, Vector3? nr = null)
        {
            var (u, v) = (q - p, r - p);
            var cross = Vector3.Cross(u, v);
            var length = cross.Length();
            if (length < 1e-12f || !float.IsFinite(length)) return;
            a.Add(p);
            e1.Add(u);
            e2.Add(v);
            normals.Add(cross / length);
            materialOf.Add(material);
            if (np is { } n0 && nq is { } n1 && nr is { } n2)
            {
                smoothOf.Add(smooth.Count);
                smooth.Add(n0);
                smooth.Add(n1);
                smooth.Add(n2);
            }
            else smoothOf.Add(-1);
        }

        double? lowestGround = null;
        foreach (var mesh in meshes)
        {
            var glass = mesh.Kind == MeshKind.Glazing && mesh.Opacity < 0.99;
            var material = MaterialFor(SurfacePatterns.Of(mesh, materials), glass, (float)mesh.Opacity);
            var positions = mesh.Positions;
            var meshNormals = mesh.Normals is { } list && list.Count == positions.Count ? list : null;
            if (mesh.Kind == MeshKind.Terrain && positions.Count > 0) lowestGround = Math.Min(lowestGround ?? double.MaxValue, positions.Min(point => point.Z));
            var indices = mesh.Indices;
            for (var i = 0; i + 2 < indices.Count; i += 3)
            {
                var (i0, i1, i2) = (indices[i], indices[i + 1], indices[i + 2]);
                if (meshNormals is null) Add(scene.ToScene(positions[i0]), scene.ToScene(positions[i1]), scene.ToScene(positions[i2]), material);
                else Add(scene.ToScene(positions[i0]), scene.ToScene(positions[i1]), scene.ToScene(positions[i2]), material,
                    Unit(meshNormals[i0]), Unit(meshNormals[i1]), Unit(meshNormals[i2]));
            }
        }

        if (ground)
        {
            var grass = MaterialFor(SurfacePatterns.Standard(SurfacePattern.Grass, new ColourRgb(0x6C, 0x8A, 0x4C)), false, 1);
            var z = (float)((lowestGround is { } lowest ? lowest - 30 : -10) - scene.Origin.Z) / 1000;
            const float Reach = 10000;
            Add(new Vector3(-Reach, -Reach, z), new Vector3(Reach, -Reach, z), new Vector3(Reach, Reach, z), grass);
            Add(new Vector3(-Reach, -Reach, z), new Vector3(Reach, Reach, z), new Vector3(-Reach, Reach, z), grass);
        }

        (scene._a, scene._e1, scene._e2, scene.Normal) = (a.ToArray(), e1.ToArray(), e2.ToArray(), normals.ToArray());
        (scene.MaterialOf, scene.SmoothOf, scene.Smooth, scene.Materials) = (materialOf.ToArray(), smoothOf.ToArray(), smooth.ToArray(), sceneMaterials.ToArray());
        scene.BuildHierarchy();
        return scene;
    }

    /// <summary>How far a pattern stands proud for its light and shade, mm.</summary>
    private static float Relief(SurfacePattern pattern) => pattern switch
    {
        SurfacePattern.Brick or SurfacePattern.Block or SurfacePattern.Stone => 3,
        SurfacePattern.Slate or SurfacePattern.PlainTiles or SurfacePattern.InterlockingTiles => 6,
        SurfacePattern.StandingSeam => 6,
        SurfacePattern.Boards or SurfacePattern.Paving => 2,
        SurfacePattern.Tiles => 1,
        SurfacePattern.Gravel => 4,
        SurfacePattern.Grass => 1.5f,
        SurfacePattern.Planting => 6,
        SurfacePattern.Concrete => 0.6f,
        SurfacePattern.Render => 0.3f,
        _ => 0
    };

    private static Vector3 Unit(Point3D n)
    {
        var v = new Vector3((float)n.X, (float)n.Y, (float)n.Z);
        var length = v.Length();
        return length > 1e-9f ? v / length : Vector3.UnitZ;
    }

    /// <summary>The share of each of red, green and blue a colour gives back, undoing its screen gamma.</summary>
    internal static Vector3 Linear(ColourRgb colour) => new(ToLinear[colour.R], ToLinear[colour.G], ToLinear[colour.B]);

    internal static readonly float[] ToLinear = Enumerable.Range(0, 256).Select(i =>
    {
        var c = i / 255.0;
        return (float)(c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4));
    }).ToArray();

    // ---- the hierarchy ------------------------------------------------------------------------

    private void BuildHierarchy()
    {
        var count = _a.Length;
        var order = Enumerable.Range(0, count).ToArray();
        var centres = new Vector3[count];
        var lows = new Vector3[count];
        var highs = new Vector3[count];
        for (var i = 0; i < count; i++)
        {
            var (p, q, r) = (_a[i], _a[i] + _e1[i], _a[i] + _e2[i]);
            lows[i] = Vector3.Min(p, Vector3.Min(q, r));
            highs[i] = Vector3.Max(p, Vector3.Max(q, r));
            centres[i] = (lows[i] + highs[i]) / 2;
        }

        var nodes = new List<Node> { default };
        Split(0, 0, count, 0);
        _nodes = nodes.ToArray();

        // Triangles put in the order the leaves hold them.
        (_a, _e1, _e2, Normal) = (order.Select(i => _a[i]).ToArray(), order.Select(i => _e1[i]).ToArray(), order.Select(i => _e2[i]).ToArray(), order.Select(i => Normal[i]).ToArray());
        (MaterialOf, SmoothOf) = (order.Select(i => MaterialOf[i]).ToArray(), order.Select(i => SmoothOf[i]).ToArray());
        return;

        void Split(int index, int start, int length, int depth)
        {
            var (low, high) = (new Vector3(float.MaxValue), new Vector3(float.MinValue));
            var (centreLow, centreHigh) = (new Vector3(float.MaxValue), new Vector3(float.MinValue));
            for (var i = start; i < start + length; i++)
            {
                var t = order[i];
                (low, high) = (Vector3.Min(low, lows[t]), Vector3.Max(high, highs[t]));
                (centreLow, centreHigh) = (Vector3.Min(centreLow, centres[t]), Vector3.Max(centreHigh, centres[t]));
            }

            void Leaf() => nodes[index] = new Node { Min = low, Max = high, Start = start, Count = length };
            if (length <= 4 || depth > 48)
            {
                Leaf();
                return;
            }

            // Binned by the surface area heuristic: the split along whichever axis costs least to look through.
            const int Bins = 12;
            var (bestCost, bestAxis, bestBin) = (float.MaxValue, -1, -1);
            var extent = centreHigh - centreLow;
            for (var axis = 0; axis < 3; axis++)
            {
                var span = Component(extent, axis);
                if (span < 1e-7f) continue;
                var binCount = new int[Bins];
                var binLow = Enumerable.Repeat(new Vector3(float.MaxValue), Bins).ToArray();
                var binHigh = Enumerable.Repeat(new Vector3(float.MinValue), Bins).ToArray();
                for (var i = start; i < start + length; i++)
                {
                    var t = order[i];
                    var b = Math.Min(Bins - 1, (int)((Component(centres[t], axis) - Component(centreLow, axis)) / span * Bins));
                    binCount[b]++;
                    (binLow[b], binHigh[b]) = (Vector3.Min(binLow[b], lows[t]), Vector3.Max(binHigh[b], highs[t]));
                }

                var rightArea = new float[Bins];
                var rightCount = new int[Bins];
                var (rl, rh, rc) = (new Vector3(float.MaxValue), new Vector3(float.MinValue), 0);
                for (var b = Bins - 1; b > 0; b--)
                {
                    (rl, rh, rc) = (Vector3.Min(rl, binLow[b]), Vector3.Max(rh, binHigh[b]), rc + binCount[b]);
                    (rightArea[b], rightCount[b]) = (Area(rl, rh), rc);
                }

                var (ll, lh, lc) = (new Vector3(float.MaxValue), new Vector3(float.MinValue), 0);
                for (var b = 0; b < Bins - 1; b++)
                {
                    (ll, lh, lc) = (Vector3.Min(ll, binLow[b]), Vector3.Max(lh, binHigh[b]), lc + binCount[b]);
                    if (lc == 0 || rightCount[b + 1] == 0) continue;
                    var cost = Area(ll, lh) * lc + rightArea[b + 1] * rightCount[b + 1];
                    if (cost < bestCost) (bestCost, bestAxis, bestBin) = (cost, axis, b);
                }
            }

            if (bestAxis < 0 || (bestCost >= Area(low, high) * length && length <= 12))
            {
                Leaf();
                return;
            }

            // Those on the near side of the split to the front.
            var span2 = Component(extent, bestAxis);
            var origin = Component(centreLow, bestAxis);
            var (left, right) = (start, start + length - 1);
            while (left <= right)
            {
                var b = Math.Min(Bins - 1, (int)((Component(centres[order[left]], bestAxis) - origin) / span2 * Bins));
                if (b <= bestBin) left++;
                else (order[left], order[right], right) = (order[right], order[left], right - 1);
            }

            var leftCount = left - start;
            if (leftCount == 0 || leftCount == length) leftCount = length / 2;

            var first = nodes.Count;
            nodes.Add(default);
            nodes.Add(default);
            nodes[index] = new Node { Min = low, Max = high, Start = first, Count = 0 };
            Split(first, start, leftCount, depth + 1);
            Split(first + 1, start + leftCount, length - leftCount, depth + 1);
        }
    }

    private static float Component(Vector3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;

    private static float Area(Vector3 low, Vector3 high)
    {
        var d = Vector3.Max(high - low, Vector3.Zero);
        return d.X * d.Y + d.Y * d.Z + d.Z * d.X;
    }

    // ---- rays ---------------------------------------------------------------------------------

    /// <summary>What a ray meets first: how far along it, which triangle, and where on the triangle.</summary>
    internal struct Hit
    {
        public float T, U, V;
        public int Triangle;
    }

    /// <summary>The first triangle a ray from a point meets nearer than <paramref name="far"/>, if any.</summary>
    internal bool Intersect(Vector3 origin, Vector3 direction, float far, out Hit hit)
    {
        hit = new Hit { T = far, Triangle = -1 };
        if (_nodes.Length == 0 || _a.Length == 0) return false;
        var inverse = new Vector3(1 / NonZero(direction.X), 1 / NonZero(direction.Y), 1 / NonZero(direction.Z));
        Span<int> stack = stackalloc int[96];
        var top = 0;
        stack[top++] = 0;
        while (top > 0)
        {
            ref var node = ref _nodes[stack[--top]];
            if (!Meets(node.Min, node.Max, origin, inverse, hit.T, out _)) continue;
            if (node.Count > 0)
            {
                for (var i = node.Start; i < node.Start + node.Count; i++)
                    if (Triangle(i, origin, direction, hit.T, out var t, out var u, out var v))
                        hit = new Hit { T = t, U = u, V = v, Triangle = i };
                continue;
            }

            // The nearer child looked in first.
            var (near, farChild) = (node.Start, node.Start + 1);
            var nearHit = Meets(_nodes[near].Min, _nodes[near].Max, origin, inverse, hit.T, out var dNear);
            var farHit = Meets(_nodes[farChild].Min, _nodes[farChild].Max, origin, inverse, hit.T, out var dFar);
            if (nearHit && farHit && dFar < dNear) (near, farChild) = (farChild, near);
            if (top + 2 > stack.Length) continue;
            if (farHit || nearHit)
            {
                stack[top++] = farChild;
                stack[top++] = near;
            }
        }

        return hit.Triangle >= 0;
    }

    private static float NonZero(float value) => Math.Abs(value) < 1e-12f ? (value < 0 ? -1e-12f : 1e-12f) : value;

    private static bool Meets(Vector3 low, Vector3 high, Vector3 origin, Vector3 inverse, float far, out float near)
    {
        var t0 = (low - origin) * inverse;
        var t1 = (high - origin) * inverse;
        var (tMin, tMax) = (Vector3.Min(t0, t1), Vector3.Max(t0, t1));
        near = Math.Max(Math.Max(tMin.X, tMin.Y), Math.Max(tMin.Z, 0));
        var exit = Math.Min(Math.Min(tMax.X, tMax.Y), Math.Min(tMax.Z, far));
        return near <= exit;
    }

    /// <summary>Möller and Trumbore's test of a ray against a triangle.</summary>
    private bool Triangle(int i, Vector3 origin, Vector3 direction, float far, out float t, out float u, out float v)
    {
        (t, u, v) = (0, 0, 0);
        var p = Vector3.Cross(direction, _e2[i]);
        var det = Vector3.Dot(_e1[i], p);
        if (Math.Abs(det) < 1e-14f) return false;
        var inverse = 1 / det;
        var s = origin - _a[i];
        u = Vector3.Dot(s, p) * inverse;
        if (u < 0 || u > 1) return false;
        var q = Vector3.Cross(s, _e1[i]);
        v = Vector3.Dot(direction, q) * inverse;
        if (v < 0 || u + v > 1) return false;
        t = Vector3.Dot(_e2[i], q) * inverse;
        return t > 1e-5f && t < far;
    }

    /// <summary>How far along a ray from a point of the model, mm, it first meets a face of the scene; none if it meets nothing.</summary>
    public double? Cast(Point3D from, (double X, double Y, double Z) direction)
    {
        var way = new Vector3((float)direction.X, (float)direction.Y, (float)direction.Z);
        if (way.Length() < 1e-9f) return null;
        return Intersect(ToScene(from), Vector3.Normalize(way), float.MaxValue, out var hit) ? hit.T * 1000.0 : null;
    }

    /// <summary>The way a triangle faces at a point of it: its curve's where it is curved.</summary>
    internal Vector3 NormalAt(in Hit hit)
    {
        var smooth = SmoothOf[hit.Triangle];
        if (smooth < 0) return Normal[hit.Triangle];
        var n = Smooth[smooth] * (1 - hit.U - hit.V) + Smooth[smooth + 1] * hit.U + Smooth[smooth + 2] * hit.V;
        var length = n.Length();
        return length > 1e-9f ? n / length : Normal[hit.Triangle];
    }

    /// <summary>
    /// How much light gets along a ray to the sun: all of it with nothing in the way, a share through
    /// glass and anything faded, none through anything solid.
    /// </summary>
    internal float Transmission(Vector3 origin, Vector3 direction)
    {
        var through = 1f;
        for (var step = 0; step < 8; step++)
        {
            if (!Intersect(origin, direction, float.MaxValue, out var hit)) return through;
            var material = Materials[MaterialOf[hit.Triangle]];
            if (material.Glass) through *= 0.85f;
            else if (material.Opacity < 1) through *= 1 - material.Opacity;
            else return 0;
            if (through < 0.01f) return 0;
            origin += direction * (hit.T + 1e-4f);
        }

        return through;
    }
}
