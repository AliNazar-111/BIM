using BIMDesigner.Core.Geometry;
using Xbim.Ifc4.Interfaces;

namespace BIMDesigner.Infrastructure.Interoperability;

/// <summary>A point and three axes: where an IFC placement puts things, and which way they face.</summary>
internal readonly record struct Frame(Point3D Origin, Point3D X, Point3D Y, Point3D Z, double Scale = 1)
{
    public static readonly Frame Identity = new(new Point3D(0, 0, 0), new Point3D(1, 0, 0), new Point3D(0, 1, 0), new Point3D(0, 0, 1));

    /// <summary>A point in this frame, out in the frame it sits in.</summary>
    public Point3D Apply(Point3D p) => new(
        Origin.X + (X.X * p.X + Y.X * p.Y + Z.X * p.Z) * Scale,
        Origin.Y + (X.Y * p.X + Y.Y * p.Y + Z.Y * p.Z) * Scale,
        Origin.Z + (X.Z * p.X + Y.Z * p.Y + Z.Z * p.Z) * Scale);

    /// <summary>A direction in this frame, out in the frame it sits in.</summary>
    public Point3D Turn(Point3D d) => new(X.X * d.X + Y.X * d.Y + Z.X * d.Z, X.Y * d.X + Y.Y * d.Y + Z.Y * d.Z, X.Z * d.X + Y.Z * d.Y + Z.Z * d.Z);

    /// <summary>A frame inside this one, out in the frame this one sits in.</summary>
    public Frame Then(Frame inner) => new(Apply(inner.Origin), Scaled(Turn(inner.X), inner.Scale), Scaled(Turn(inner.Y), inner.Scale), Scaled(Turn(inner.Z), inner.Scale), Scale);

    private static Point3D Scaled(Point3D p, double s) => new(p.X * s, p.Y * s, p.Z * s);

    /// <summary>Whether its up is up: what a component can be turned about.</summary>
    public bool IsUpright => Math.Abs(Z.Z - 1) < 1e-6 && Math.Abs(X.Z) < 1e-6;

    /// <summary>How far it is turned about the upright, degrees anticlockwise.</summary>
    public double Rotation => Math.Atan2(X.Y, X.X) * 180 / Math.PI;

    public static Point3D Normalised(Point3D p)
    {
        var length = Math.Sqrt(p.X * p.X + p.Y * p.Y + p.Z * p.Z);
        return length < 1e-12 ? p : new Point3D(p.X / length, p.Y / length, p.Z / length);
    }

    public static Point3D Cross(Point3D a, Point3D b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    public static double Dot(Point3D a, Point3D b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    /// <summary>A frame from an origin, an up and a forward, the forward squared to the up.</summary>
    public static Frame From(Point3D origin, Point3D? up, Point3D? forward)
    {
        var z = Normalised(up ?? new Point3D(0, 0, 1));
        var x = forward ?? (Math.Abs(z.Z) > 0.9 ? new Point3D(1, 0, 0) : new Point3D(0, 0, 1));
        var along = Dot(x, z);
        x = Normalised(new Point3D(x.X - z.X * along, x.Y - z.Y * along, x.Z - z.Z * along));
        return new Frame(origin, x, Cross(z, x), z);
    }
}

/// <summary>
/// IFC geometry made into what this model can use: placements resolved to frames, profiles to
/// outlines, and the bodies of things into triangles - extrusions, faceted, advanced and
/// triangulated solids, surface models, mapped items and swept tubes; a boolean as what it starts
/// from with its clippings taken off (an opening cut by a solid is cut away by its own opening
/// element), a union as both. Everything in millimetres, at <c>scale</c> to the file's unit.
/// </summary>
internal sealed partial class IfcGeometry
{
    private readonly double _scale;
    private readonly double _angle;
    private readonly Dictionary<int, Frame> _placements = new();

    public IfcGeometry(double millimetresPerUnit, double radiansPerUnit)
    {
        _scale = millimetresPerUnit;
        _angle = radiansPerUnit;
    }

    public double Length(double value) => value * _scale;

    public Point3D Point(IIfcCartesianPoint point)
    {
        var c = point.Coordinates;
        return new Point3D(c.Count > 0 ? c[0] * _scale : 0, c.Count > 1 ? c[1] * _scale : 0, c.Count > 2 ? c[2] * _scale : 0);
    }

    public static Point3D Direction(IIfcDirection direction)
    {
        var r = direction.DirectionRatios;
        return Frame.Normalised(new Point3D(r.Count > 0 ? r[0] : 0, r.Count > 1 ? r[1] : 0, r.Count > 2 ? r[2] : 0));
    }

    /// <summary>A placement as a frame in the file's world, its parents resolved; cached, as many share one.</summary>
    public Frame Placement(IIfcObjectPlacement? placement)
    {
        if (placement is null) return Frame.Identity;
        if (_placements.TryGetValue(placement.EntityLabel, out var known)) return known;

        var frame = placement is IIfcLocalPlacement local
            ? Placement(local.PlacementRelTo).Then(Axis(local.RelativePlacement))
            : Frame.Identity;
        _placements[placement.EntityLabel] = frame;
        return frame;
    }

    public Frame Axis(IIfcAxis2Placement? placement) => placement switch
    {
        IIfcAxis2Placement3D three => Frame.From(Point(three.Location),
            three.Axis is { } up ? Direction(up) : null,
            three.RefDirection is { } forward ? Direction(forward) : null),
        IIfcAxis2Placement2D two => Frame.From(Point(two.Location), null,
            two.RefDirection is { } forward ? Direction(forward) : null),
        _ => Frame.Identity
    };

    private Frame Operator(IIfcCartesianTransformationOperator? op)
    {
        if (op is null) return Frame.Identity;
        var origin = Point(op.LocalOrigin);
        var x = op.Axis1 is { } a1 ? Direction(a1) : new Point3D(1, 0, 0);
        var up = op is IIfcCartesianTransformationOperator3D three && three.Axis3 is { } a3 ? Direction(a3) : new Point3D(0, 0, 1);
        var frame = Frame.From(origin, up, x);
        return frame with { Scale = op.Scale is { } s ? (double)s : 1 };
    }

    // ---- profiles ----------------------------------------------------------------

    /// <summary>A curve as points in its own plane: a polyline, an indexed polycurve, a composite of them and of arcs, a circle.</summary>
    public List<Point2D> Curve(IIfcCurve? curve)
    {
        switch (curve)
        {
            case IIfcPolyline polyline:
                return polyline.Points.Select(p => { var q = Point(p); return new Point2D(q.X, q.Y); }).ToList();

            case IIfcIndexedPolyCurve indexed when indexed.Points is IIfcCartesianPointList2D list:
                return list.CoordList.Select(c => new Point2D(c[0] * _scale, c[1] * _scale)).ToList();

            case IIfcCompositeCurve composite:
            {
                var points = new List<Point2D>();
                foreach (var segment in composite.Segments)
                {
                    var part = Curve(segment.ParentCurve);
                    if (!(bool)segment.SameSense) part.Reverse();
                    foreach (var point in part)
                        if (points.Count == 0 || points[^1].DistanceTo(point) > 1e-6) points.Add(point);
                }

                return points;
            }

            case IIfcTrimmedCurve trimmed when trimmed.BasisCurve is IIfcCircle circle:
            {
                var centre = Axis(circle.Position as IIfcAxis2Placement);
                var radius = circle.Radius * _scale;
                double? Parameter(IEnumerable<IIfcTrimmingSelect> trims) =>
                    trims.OfType<Xbim.Ifc4.MeasureResource.IfcParameterValue>().Select(v => (double?)(double)v.Value).FirstOrDefault();
                var from = (Parameter(trimmed.Trim1) ?? 0) * _angle;
                var to = (Parameter(trimmed.Trim2) ?? 2 * Math.PI / _angle) * _angle;
                if (!trimmed.SenseAgreement) (from, to) = (to, from);
                if (to <= from) to += 2 * Math.PI;
                var steps = Math.Max(4, (int)Math.Ceiling((to - from) / (Math.PI / 16)));
                var points = Enumerable.Range(0, steps + 1)
                    .Select(i => from + (to - from) * i / steps)
                    .Select(a => centre.Apply(new Point3D(radius * Math.Cos(a), radius * Math.Sin(a), 0)))
                    .Select(p => new Point2D(p.X, p.Y)).ToList();
                if (!trimmed.SenseAgreement) points.Reverse();
                return points;
            }

            case IIfcCircle whole:
            {
                var centre = Axis(whole.Position as IIfcAxis2Placement);
                var radius = whole.Radius * _scale;
                return Enumerable.Range(0, 32).Select(i => centre.Apply(new Point3D(radius * Math.Cos(i * Math.PI / 16), radius * Math.Sin(i * Math.PI / 16), 0)))
                    .Select(p => new Point2D(p.X, p.Y)).ToList();
            }

            default:
                return new List<Point2D>();
        }
    }

    /// <summary>A profile's outline in its own plane, closed but not repeated; empty for one it cannot read.</summary>
    public List<Point2D> Profile(IIfcProfileDef profile)
    {
        List<Point2D> outline;
        Frame position;
        switch (profile)
        {
            case IIfcArbitraryClosedProfileDef arbitrary:
                outline = Curve(arbitrary.OuterCurve);
                position = Frame.Identity;
                break;

            case IIfcRectangleProfileDef rectangle:
            {
                var (x, y) = (rectangle.XDim * _scale / 2, rectangle.YDim * _scale / 2);
                outline = new List<Point2D> { new(-x, -y), new(x, -y), new(x, y), new(-x, y) };
                position = Axis(rectangle.Position);
                break;
            }

            case IIfcCircleProfileDef circle:
            {
                var r = circle.Radius * _scale;
                outline = Enumerable.Range(0, 24).Select(i => new Point2D(r * Math.Cos(i * Math.PI / 12), r * Math.Sin(i * Math.PI / 12))).ToList();
                position = Axis(circle.Position);
                break;
            }

            case IIfcEllipseProfileDef ellipse:
            {
                var (a, b) = (ellipse.SemiAxis1 * _scale, ellipse.SemiAxis2 * _scale);
                outline = Enumerable.Range(0, 24).Select(i => new Point2D(a * Math.Cos(i * Math.PI / 12), b * Math.Sin(i * Math.PI / 12))).ToList();
                position = Axis(ellipse.Position);
                break;
            }

            // A steel section, an angle or a channel: the box round it, which is where it stands.
            case IIfcIShapeProfileDef i:
                outline = Box(i.OverallWidth, i.OverallDepth);
                position = Axis(i.Position);
                break;
            case IIfcLShapeProfileDef l:
                outline = Box(l.Width ?? l.Depth, l.Depth);
                position = Axis(l.Position);
                break;
            case IIfcUShapeProfileDef u:
                outline = Box(u.FlangeWidth, u.Depth);
                position = Axis(u.Position);
                break;
            case IIfcTShapeProfileDef t:
                outline = Box(t.FlangeWidth, t.Depth);
                position = Axis(t.Position);
                break;

            default:
                return new List<Point2D>();
        }

        if (outline.Count > 1 && outline[0].DistanceTo(outline[^1]) < 1e-6) outline.RemoveAt(outline.Count - 1);
        return outline.Select(p => position.Apply(new Point3D(p.X, p.Y, 0))).Select(p => new Point2D(p.X, p.Y)).ToList();
    }

    private List<Point2D> Box(double width, double depth)
    {
        var (x, y) = (width * _scale / 2, depth * _scale / 2);
        return new List<Point2D> { new(-x, -y), new(x, -y), new(x, y), new(-x, y) };
    }

    // ---- bodies ------------------------------------------------------------------

    /// <summary>The triangles of an item, in a frame: what it looks like, as near as this reads it.</summary>
    public void Triangles(IIfcRepresentationItem item, Frame frame, Mesh3D mesh)
    {
        switch (item)
        {
            case IIfcExtrudedAreaSolid extrusion:
            {
                var outline = Profile(extrusion.SweptArea);
                if (outline.Count < 3) return;
                var position = frame.Then(Axis(extrusion.Position));
                var direction = Direction(extrusion.ExtrudedDirection);
                var depth = extrusion.Depth * _scale;
                var local = new Mesh3D(Guid.Empty, Guid.Empty, MeshKind.Wall, default, string.Empty);
                local.AddExtrusion(outline, 0, depth);

                // Along its direction, however it leans: a height up the prism is a distance along it.
                Copy(local, mesh, p => position.Apply(new Point3D(p.X + direction.X * p.Z, p.Y + direction.Y * p.Z, direction.Z * p.Z)));
                return;
            }

            case IIfcManifoldSolidBrep brep:
                Faces(brep.Outer.CfsFaces, frame, mesh);
                return;

            case IIfcShellBasedSurfaceModel shells:
                foreach (var shell in shells.SbsmBoundary.OfType<IIfcConnectedFaceSet>()) Faces(shell.CfsFaces, frame, mesh);
                return;

            case IIfcFaceBasedSurfaceModel faces:
                foreach (var set in faces.FbsmFaces) Faces(set.CfsFaces, frame, mesh);
                return;

            case IIfcTriangulatedFaceSet triangulated:
            {
                var points = triangulated.Coordinates.CoordList.Select(c => frame.Apply(new Point3D(c[0] * _scale, c[1] * _scale, c[2] * _scale))).ToList();
                foreach (var index in triangulated.CoordIndex)
                {
                    var (a, b, c) = ((int)(long)index[0] - 1, (int)(long)index[1] - 1, (int)(long)index[2] - 1);
                    if (a < points.Count && b < points.Count && c < points.Count && a >= 0 && b >= 0 && c >= 0)
                        mesh.AddTriangle(points[a], points[b], points[c]);
                }

                return;
            }

            case IIfcPolygonalFaceSet polygonal:
            {
                var points = polygonal.Coordinates.CoordList.Select(c => frame.Apply(new Point3D(c[0] * _scale, c[1] * _scale, c[2] * _scale))).ToList();
                foreach (var face in polygonal.Faces)
                    Polygon(face.CoordIndex.Select(i => (int)(long)i - 1).Where(i => i >= 0 && i < points.Count).Select(i => points[i]).ToList(), mesh);
                return;
            }

            case IIfcMappedItem mapped:
            {
                var target = frame.Then(Operator(mapped.MappingTarget)).Then(Axis(mapped.MappingSource.MappingOrigin));
                foreach (var inner in mapped.MappingSource.MappedRepresentation.Items) Triangles(inner, target, mesh);
                return;
            }

            case IIfcBooleanResult boolean when boolean.FirstOperand is IIfcRepresentationItem first:
            {
                var start = new Mesh3D(Guid.Empty, Guid.Empty, MeshKind.Wall, default, string.Empty);
                Triangles(first, frame, start);
                if (boolean.Operator == IfcBooleanOperator.UNION && boolean.SecondOperand is IIfcRepresentationItem second) Triangles(second, frame, start);
                else if (boolean.Operator == IfcBooleanOperator.DIFFERENCE && boolean.SecondOperand is IIfcHalfSpaceSolid half && Cut(half, frame) is { } cut)
                    start = Clip(start, cut);
                Copy(start, mesh, p => p);
                return;
            }

            case IIfcSweptDiskSolid disk:
            {
                var path = Curve3D(disk.Directrix).Select(frame.Apply).ToList();
                if (path.Count >= 2) mesh.AddTube(path, disk.Radius * _scale * frame.Scale, 8);
                return;
            }
        }
    }

    private List<Point3D> Curve3D(IIfcCurve curve) => curve switch
    {
        IIfcPolyline polyline => polyline.Points.Select(Point).ToList(),
        IIfcIndexedPolyCurve { Points: IIfcCartesianPointList3D list } => list.CoordList.Select(c => new Point3D(c[0] * _scale, c[1] * _scale, c[2] * _scale)).ToList(),
        _ => Curve(curve).Select(p => new Point3D(p.X, p.Y, 0)).ToList()
    };

    private static void Copy(Mesh3D from, Mesh3D to, Func<Point3D, Point3D> map)
    {
        var points = from.Positions.Select(map).ToList();
        for (var i = 0; i + 2 < from.Indices.Count; i += 3)
            to.AddTriangle(points[from.Indices[i]], points[from.Indices[i + 1]], points[from.Indices[i + 2]]);
    }

    private void Faces(IEnumerable<IIfcFace> faces, Frame frame, Mesh3D mesh)
    {
        foreach (var face in faces)
        {
            var outer = face.Bounds.FirstOrDefault(bound => bound is IIfcFaceOuterBound) ?? face.Bounds.FirstOrDefault();
            if (outer?.Bound is not { } loop) continue;
            var points = LoopPoints(loop).Select(frame.Apply).ToList();
            if (!outer.Orientation) points.Reverse();
            Polygon(points, mesh);
        }
    }

    /// <summary>A flat polygon in space as triangles: laid flat on its own plane, cut up, and stood back up.</summary>
    private static void Polygon(IReadOnlyList<Point3D> points, Mesh3D mesh)
    {
        if (points.Count < 3) return;
        if (points.Count == 3)
        {
            mesh.AddTriangle(points[0], points[1], points[2]);
            return;
        }

        // Newell's normal, then two axes in the plane.
        var normal = new Point3D(0, 0, 0);
        for (var i = 0; i < points.Count; i++)
        {
            var (a, b) = (points[i], points[(i + 1) % points.Count]);
            normal = new Point3D(normal.X + (a.Y - b.Y) * (a.Z + b.Z), normal.Y + (a.Z - b.Z) * (a.X + b.X), normal.Z + (a.X - b.X) * (a.Y + b.Y));
        }

        var plane = Frame.From(points[0], normal, new Point3D(points[1].X - points[0].X, points[1].Y - points[0].Y, points[1].Z - points[0].Z));
        var flat = points.Select(p =>
        {
            var d = new Point3D(p.X - points[0].X, p.Y - points[0].Y, p.Z - points[0].Z);
            return new Point2D(Frame.Dot(d, plane.X), Frame.Dot(d, plane.Y));
        }).ToList();

        foreach (var (a, b, c) in Polygon2D.Triangulate(flat)) mesh.AddTriangle(points[a], points[b], points[c]);
    }
}
