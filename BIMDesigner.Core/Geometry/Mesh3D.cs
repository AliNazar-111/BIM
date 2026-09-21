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
    Sweep
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
        if (outline.Count < 3 || top - bottom <= 1e-6) return;

        // Anticlockwise from above, so the caps and sides all face outward.
        var ring = Polygon2D.SignedArea(outline) >= 0 ? outline : outline.Reverse().ToList();

        for (var i = 0; i < ring.Count; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % ring.Count];
            if (a.DistanceTo(b) <= 1e-9) continue;

            AddQuad(Point3D.On(a, bottom), Point3D.On(b, bottom), Point3D.On(b, top), Point3D.On(a, top));

            // The outline at both ends, and the corner joining them.
            AddEdge(Point3D.On(a, bottom), Point3D.On(b, bottom));
            AddEdge(Point3D.On(a, top), Point3D.On(b, top));
            AddEdge(Point3D.On(a, bottom), Point3D.On(a, top));
        }

        foreach (var (i, j, k) in Polygon2D.Triangulate(ring))
        {
            AddTriangle(Point3D.On(ring[i], top), Point3D.On(ring[j], top), Point3D.On(ring[k], top));

            // The underside faces down, so it winds the other way.
            AddTriangle(Point3D.On(ring[i], bottom), Point3D.On(ring[k], bottom), Point3D.On(ring[j], bottom));
        }
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
