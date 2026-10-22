using BIMDesigner.Core.Geometry;
using Xbim.Ifc4.Interfaces;

namespace BIMDesigner.Infrastructure.Interoperability;

/// <summary>
/// What a clipping takes away: everything on one side of a plane - the side <see cref="Into"/>
/// points to - or, where it is bounded, only what of that lies within a polygon on the plane's
/// own ground, carried up and down square to it. A wall clipped to the slopes of a roof over it, a
/// beam cut off square at its end, a slab cut back at an angle.
/// </summary>
internal sealed record HalfSpace(Point3D Origin, Point3D Into, Frame Ground, IReadOnlyList<Point2D>? Boundary)
{
    /// <summary>How far a point is into what is taken away, positive inside it.</summary>
    public double Depth(Point3D p) => Frame.Dot(new Point3D(p.X - Origin.X, p.Y - Origin.Y, p.Z - Origin.Z), Into);

    /// <summary>Whether a point is within the bound - always, where there is none.</summary>
    public bool Within(Point3D p) => Boundary is null || Polygon2D.Contains(Boundary, Flat(p));

    /// <summary>A point on the ground the bound is drawn on.</summary>
    public Point2D Flat(Point3D p)
    {
        var d = new Point3D(p.X - Ground.Origin.X, p.Y - Ground.Origin.Y, p.Z - Ground.Origin.Z);
        return new Point2D(Frame.Dot(d, Frame.Normalised(Ground.X)) / Ground.Scale, Frame.Dot(d, Frame.Normalised(Ground.Y)) / Ground.Scale);
    }
}

internal sealed partial class IfcGeometry
{
    /// <summary>
    /// A boolean result as what it starts from and the clippings taken off it, in order - where
    /// all it does is clip; null where it does anything else (a union, an opening cut by a solid).
    /// </summary>
    public static (IIfcRepresentationItem Start, List<IIfcHalfSpaceSolid> Clippings)? Unclipped(IIfcRepresentationItem item)
    {
        var clippings = new List<IIfcHalfSpaceSolid>();
        while (item is IIfcBooleanResult boolean)
        {
            if (boolean.Operator != IfcBooleanOperator.DIFFERENCE || boolean.SecondOperand is not IIfcHalfSpaceSolid half ||
                boolean.FirstOperand is not IIfcRepresentationItem first)
                return null;
            clippings.Insert(0, half);
            item = first;
        }

        return (item, clippings);
    }

    /// <summary>What a half-space solid takes away, in a frame.</summary>
    public HalfSpace? Cut(IIfcHalfSpaceSolid half, Frame frame)
    {
        if (half.BaseSurface is not IIfcPlane plane) return null;
        var position = frame.Then(Axis(plane.Position));
        var normal = Frame.Normalised(position.Z);

        // The flag says the plane's normal points away from the half-space's material - what is taken away.
        var into = half.AgreementFlag ? new Point3D(-normal.X, -normal.Y, -normal.Z) : normal;

        if (half is IIfcPolygonalBoundedHalfSpace bounded)
        {
            var ground = frame.Then(Axis(bounded.Position));
            var boundary = Curve(bounded.PolygonalBoundary as IIfcCurve);
            if (boundary.Count < 3) return null;
            return new HalfSpace(position.Origin, into, ground, boundary);
        }

        return new HalfSpace(position.Origin, into, position, null);
    }

    /// <summary>
    /// A solid with a half-space taken away: each face kept as far as it is outside it, and the cut
    /// closed over where the plane went through. Where the half-space is bounded, a face is cut only
    /// where its middle is within the bound - near enough for the ends of walls, beams and slabs cut
    /// to a roof, which is what bounded half-spaces are used for.
    /// </summary>
    public static Mesh3D Clip(Mesh3D solid, HalfSpace cut)
    {
        const double touching = 1e-6;
        var kept = new Mesh3D(Guid.Empty, Guid.Empty, MeshKind.Wall, default, string.Empty);
        var seams = new List<(Point3D A, Point3D B)>();
        var positions = solid.Positions;
        var indices = solid.Indices;

        for (var i = 0; i + 2 < indices.Count; i += 3)
        {
            var corners = new[] { positions[indices[i]], positions[indices[i + 1]], positions[indices[i + 2]] };
            var depths = corners.Select(cut.Depth).ToArray();
            if (depths.All(d => d <= touching) || !cut.Within(Middle(corners)))
            {
                kept.AddTriangle(corners[0], corners[1], corners[2]);
                continue;
            }

            if (depths.All(d => d >= -touching)) continue;

            // Round the triangle, keeping what is outside and stepping onto the plane where it crosses.
            var outside = new List<Point3D>();
            var crossings = new List<Point3D>();
            for (var k = 0; k < 3; k++)
            {
                var (p, q) = (corners[k], corners[(k + 1) % 3]);
                var (dp, dq) = (depths[k], depths[(k + 1) % 3]);
                if (dp <= touching) outside.Add(p);
                if ((dp < -touching && dq > touching) || (dp > touching && dq < -touching))
                {
                    var t = dp / (dp - dq);
                    var at = new Point3D(p.X + (q.X - p.X) * t, p.Y + (q.Y - p.Y) * t, p.Z + (q.Z - p.Z) * t);
                    outside.Add(at);
                    crossings.Add(at);
                }
                else if (Math.Abs(dp) <= touching) crossings.Add(p);
            }

            for (var k = 1; k + 1 < outside.Count; k++) kept.AddTriangle(outside[0], outside[k], outside[k + 1]);
            if (crossings.Count == 2) seams.Add((crossings[0], crossings[1]));
        }

        // The cut closed: its edges joined into loops, each loop a face on the plane.
        foreach (var loop in Loops(seams))
            if (loop.Count >= 3 && cut.Within(Middle(loop)))
                Polygon(loop, kept);

        return kept;
    }

    private static Point3D Middle(IReadOnlyList<Point3D> points) =>
        new(points.Average(p => p.X), points.Average(p => p.Y), points.Average(p => p.Z));

    /// <summary>Segments end to end, as closed loops; ends a hair apart are one end.</summary>
    internal static List<List<Point3D>> Loops(List<(Point3D A, Point3D B)> segments)
    {
        static (long, long, long) Key(Point3D p) => ((long)Math.Round(p.X * 100), (long)Math.Round(p.Y * 100), (long)Math.Round(p.Z * 100));

        var from = new Dictionary<(long, long, long), List<int>>();
        for (var i = 0; i < segments.Count; i++)
            foreach (var end in new[] { segments[i].A, segments[i].B })
            {
                if (!from.TryGetValue(Key(end), out var list)) from[Key(end)] = list = new List<int>();
                list.Add(i);
            }

        var used = new bool[segments.Count];
        var loops = new List<List<Point3D>>();
        for (var start = 0; start < segments.Count; start++)
        {
            if (used[start]) continue;
            used[start] = true;
            var loop = new List<Point3D> { segments[start].A };
            var at = segments[start].B;
            while (Key(at) != Key(loop[0]) && loop.Count <= segments.Count)
            {
                loop.Add(at);
                var next = from[Key(at)].FirstOrDefault(i => !used[i], -1);
                if (next < 0) break;
                used[next] = true;
                at = Key(segments[next].A) == Key(at) ? segments[next].B : segments[next].A;
            }

            if (Key(at) == Key(loop[0])) loops.Add(loop);
        }

        return loops;
    }

    /// <summary>
    /// The corners of a face's bound: a loop of points, or a loop of edges - each edge's start, and
    /// the points of a polyline or spline it follows between - taken the way the loop runs.
    /// </summary>
    private List<Point3D> LoopPoints(IIfcLoop loop)
    {
        switch (loop)
        {
            case IIfcPolyLoop poly:
                return poly.Polygon.Select(Point).ToList();

            case IIfcEdgeLoop edges:
            {
                var points = new List<Point3D>();
                foreach (var oriented in edges.EdgeList)
                {
                    var edge = oriented.EdgeElement;
                    var start = oriented.Orientation ? edge.EdgeStart : edge.EdgeEnd;
                    if (start is IIfcVertexPoint { VertexGeometry: IIfcCartesianPoint first }) points.Add(Point(first));

                    // A curved edge followed, not cut straight across.
                    if (edge is IIfcEdgeCurve curve)
                    {
                        var between = curve.EdgeGeometry switch
                        {
                            IIfcPolyline polyline => polyline.Points.Select(Point).ToList(),
                            IIfcBSplineCurve spline => spline.ControlPointsList.Select(Point).ToList(),
                            _ => new List<Point3D>()
                        };
                        if (between.Count > 2)
                        {
                            if (curve.SameSense != oriented.Orientation) between.Reverse();
                            points.AddRange(between.Skip(1).Take(between.Count - 2));
                        }
                    }
                }

                return points;
            }

            default:
                return new List<Point3D>();
        }
    }
}
