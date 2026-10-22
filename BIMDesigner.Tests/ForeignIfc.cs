using Xbim.Common;
using Xbim.Common.Step21;
using Xbim.Ifc;
using Xbim.Ifc4.GeometricConstraintResource;
using Xbim.Ifc4.GeometricModelResource;
using Xbim.Ifc4.GeometryResource;
using Xbim.Ifc4.Interfaces;
using Xbim.Ifc4.Kernel;
using Xbim.Ifc4.MaterialResource;
using Xbim.Ifc4.MeasureResource;
using Xbim.Ifc4.ProductExtension;
using Xbim.Ifc4.ProfileResource;
using Xbim.Ifc4.RepresentationResource;
using Xbim.Ifc4.SharedBldgElements;
using Xbim.Ifc4.TopologyResource;
using Xbim.IO;

namespace BIMDesigner.Tests;

/// <summary>
/// IFC files written the way other programs write them, not the way this one does: walls drawn
/// on a face or on their centre line with the layers offset to suit, walls clipped to a roof,
/// walls, floors and rooms as faceted solids, windows that do not say which wall they are in,
/// open-plan rooms with no wall between them, Revit's advanced solids. One storey at 0, in millimetres.
/// </summary>
internal sealed class ForeignIfc : IDisposable
{
    private readonly ITransaction _transaction;
    private readonly IfcGeometricRepresentationContext _context;
    private readonly IfcLocalPlacement _storeyPlacement;

    public ForeignIfc()
    {
        var credentials = new XbimEditorCredentials
        {
            ApplicationDevelopersName = "Test", ApplicationFullName = "Test", ApplicationIdentifier = "Test", ApplicationVersion = "1",
            EditorsGivenName = "Test", EditorsFamilyName = "Test", EditorsOrganisationName = "Test"
        };
        Model = IfcStore.Create(credentials, XbimSchemaVersion.Ifc4, XbimStoreType.InMemoryModel);
        _transaction = Model.BeginTransaction("Write");

        var units = New<IfcUnitAssignment>(assignment =>
        {
            assignment.Units.Add(New<IfcSIUnit>(u => { u.UnitType = IfcUnitEnum.LENGTHUNIT; u.Name = IfcSIUnitName.METRE; u.Prefix = IfcSIPrefix.MILLI; }));
            assignment.Units.Add(New<IfcSIUnit>(u => { u.UnitType = IfcUnitEnum.PLANEANGLEUNIT; u.Name = IfcSIUnitName.RADIAN; }));
        });
        _context = New<IfcGeometricRepresentationContext>(c =>
        {
            c.ContextType = "Model";
            c.CoordinateSpaceDimension = 3;
            c.Precision = 1e-5;
            c.WorldCoordinateSystem = Axis(0, 0, 0);
        });
        var project = New<IfcProject>(p =>
        {
            p.Name = "Elsewhere";
            p.UnitsInContext = units;
            p.RepresentationContexts.Add(_context);
        });

        var origin = New<IfcLocalPlacement>(p => p.RelativePlacement = Axis(0, 0, 0));
        var building = New<IfcBuilding>(b => { b.Name = "Building"; b.ObjectPlacement = origin; });
        _storeyPlacement = New<IfcLocalPlacement>(p => { p.PlacementRelTo = origin; p.RelativePlacement = Axis(0, 0, 0); });
        Storey = New<IfcBuildingStorey>(s => { s.Name = "Ground"; s.Elevation = 0; s.ObjectPlacement = _storeyPlacement; });
        New<IfcRelAggregates>(r => { r.RelatingObject = project; r.RelatedObjects.Add(building); });
        New<IfcRelAggregates>(r => { r.RelatingObject = building; r.RelatedObjects.Add(Storey); });
        Contents = New<IfcRelContainedInSpatialStructure>(r => r.RelatingStructure = Storey);
    }

    public IfcStore Model { get; }

    public IfcBuildingStorey Storey { get; }

    private IfcRelContainedInSpatialStructure Contents { get; }

    public T New<T>(Action<T> init) where T : IInstantiableEntity => Model.Instances.New(init);

    public IfcCartesianPoint Point(double x, double y, double z) => New<IfcCartesianPoint>(p => p.SetXYZ(x, y, z));

    public IfcCartesianPoint Point(double x, double y) => New<IfcCartesianPoint>(p => p.SetXY(x, y));

    public IfcDirection Direction(double x, double y, double z) => New<IfcDirection>(d => d.SetXYZ(x, y, z));

    public IfcAxis2Placement3D Axis(double x, double y, double z, (double X, double Y, double Z)? up = null, (double X, double Y, double Z)? forward = null) =>
        New<IfcAxis2Placement3D>(a =>
        {
            a.Location = Point(x, y, z);
            if (up is { } u) a.Axis = Direction(u.X, u.Y, u.Z);
            if (forward is { } f) a.RefDirection = Direction(f.X, f.Y, f.Z);
        });

    /// <summary>A placement on the storey.</summary>
    public IfcLocalPlacement Placed(double x = 0, double y = 0, double z = 0, (double X, double Y, double Z)? forward = null) =>
        New<IfcLocalPlacement>(p => { p.PlacementRelTo = _storeyPlacement; p.RelativePlacement = Axis(x, y, z, null, forward); });

    public IfcPolyline Polyline(params (double X, double Y)[] points) =>
        New<IfcPolyline>(line => { foreach (var (x, y) in points) line.Points.Add(Point(x, y)); });

    /// <summary>An outline stood up: a closed polyline profile extruded straight up from its position.</summary>
    public IfcExtrudedAreaSolid Extrusion(double height, IfcAxis2Placement3D? at, params (double X, double Y)[] outline) =>
        New<IfcExtrudedAreaSolid>(e =>
        {
            e.SweptArea = New<IfcArbitraryClosedProfileDef>(p =>
            {
                p.ProfileType = IfcProfileTypeEnum.AREA;
                p.OuterCurve = Polyline(outline.Append(outline[0]).ToArray());
            });
            e.Position = at ?? Axis(0, 0, 0);
            e.ExtrudedDirection = Direction(0, 0, 1);
            e.Depth = height;
        });

    /// <summary>A plane through a point, square to a direction; what is on the side it points to is taken away.</summary>
    public IfcHalfSpaceSolid HalfSpace(double x, double y, double z, double nx, double ny, double nz) =>
        New<IfcHalfSpaceSolid>(h =>
        {
            h.BaseSurface = New<IfcPlane>(p => p.Position = Axis(x, y, z, (nx, ny, nz)));
            h.AgreementFlag = false;
        });

    public IfcBooleanClippingResult Clipped(IfcBooleanOperand first, IfcHalfSpaceSolid cut) =>
        New<IfcBooleanClippingResult>(b => { b.Operator = IfcBooleanOperator.DIFFERENCE; b.FirstOperand = first; b.SecondOperand = cut; });

    /// <summary>A prism as faces: an outline, anticlockwise, from a bottom to a top.</summary>
    public IfcFacetedBrep FacetedPrism(double bottom, double top, params (double X, double Y)[] outline)
    {
        var faces = new List<IfcFace>();
        IfcFace Face(IEnumerable<(double X, double Y, double Z)> corners) => New<IfcFace>(f => f.Bounds.Add(New<IfcFaceOuterBound>(b =>
        {
            b.Orientation = true;
            b.Bound = New<IfcPolyLoop>(loop => { foreach (var (x, y, z) in corners) loop.Polygon.Add(Point(x, y, z)); });
        })));
        faces.Add(Face(outline.Reverse().Select(p => (p.X, p.Y, bottom))));
        faces.Add(Face(outline.Select(p => (p.X, p.Y, top))));
        for (var i = 0; i < outline.Length; i++)
        {
            var (a, b) = (outline[i], outline[(i + 1) % outline.Length]);
            faces.Add(Face(new[] { (a.X, a.Y, bottom), (b.X, b.Y, bottom), (b.X, b.Y, top), (a.X, a.Y, top) }));
        }

        return New<IfcFacetedBrep>(brep => brep.Outer = New<IfcClosedShell>(shell => shell.CfsFaces.AddRange(faces)));
    }

    /// <summary>A box as Revit's newer exports give it: advanced faces on planes, bounded by loops of edges between vertices.</summary>
    public IfcAdvancedBrep AdvancedBox(double x0, double y0, double z0, double x1, double y1, double z1)
    {
        var corner = new Dictionary<(double, double, double), IfcVertexPoint>();
        IfcVertexPoint Vertex(double x, double y, double z)
        {
            if (!corner.TryGetValue((x, y, z), out var vertex)) corner[(x, y, z)] = vertex = New<IfcVertexPoint>(v => v.VertexGeometry = Point(x, y, z));
            return vertex;
        }

        IfcAdvancedFace Face(params (double X, double Y, double Z)[] points) => New<IfcAdvancedFace>(face =>
        {
            face.FaceSurface = New<IfcPlane>(p => p.Position = Axis(points[0].X, points[0].Y, points[0].Z));
            face.SameSense = true;
            face.Bounds.Add(New<IfcFaceOuterBound>(bound =>
            {
                bound.Orientation = true;
                bound.Bound = New<IfcEdgeLoop>(loop =>
                {
                    for (var i = 0; i < points.Length; i++)
                    {
                        var (a, b) = (points[i], points[(i + 1) % points.Length]);
                        loop.EdgeList.Add(New<IfcOrientedEdge>(edge =>
                        {
                            edge.EdgeElement = New<IfcEdgeCurve>(curve =>
                            {
                                curve.EdgeStart = Vertex(a.X, a.Y, a.Z);
                                curve.EdgeEnd = Vertex(b.X, b.Y, b.Z);
                                curve.EdgeGeometry = New<IfcPolyline>(line => { line.Points.Add(Point(a.X, a.Y, a.Z)); line.Points.Add(Point(b.X, b.Y, b.Z)); });
                                curve.SameSense = true;
                            });
                            edge.Orientation = true;
                        }));
                    }
                });
            }));
        });

        var faces = new[]
        {
            Face((x0, y0, z0), (x0, y1, z0), (x1, y1, z0), (x1, y0, z0)),
            Face((x0, y0, z1), (x1, y0, z1), (x1, y1, z1), (x0, y1, z1)),
            Face((x0, y0, z0), (x1, y0, z0), (x1, y0, z1), (x0, y0, z1)),
            Face((x1, y0, z0), (x1, y1, z0), (x1, y1, z1), (x1, y0, z1)),
            Face((x1, y1, z0), (x0, y1, z0), (x0, y1, z1), (x1, y1, z1)),
            Face((x0, y1, z0), (x0, y0, z0), (x0, y0, z1), (x0, y1, z1))
        };
        return New<IfcAdvancedBrep>(brep => brep.Outer = New<IfcClosedShell>(shell => shell.CfsFaces.AddRange(faces)));
    }

    /// <summary>A body - and an axis, where given - as a product's shape.</summary>
    public IfcProductDefinitionShape Shape(IfcRepresentationItem body, IfcPolyline? axis = null) =>
        New<IfcProductDefinitionShape>(shape =>
        {
            if (axis is not null)
                shape.Representations.Add(New<IfcShapeRepresentation>(r =>
                {
                    r.ContextOfItems = _context;
                    r.RepresentationIdentifier = "Axis";
                    r.RepresentationType = "Curve2D";
                    r.Items.Add(axis);
                }));
            shape.Representations.Add(New<IfcShapeRepresentation>(r =>
            {
                r.ContextOfItems = _context;
                r.RepresentationIdentifier = "Body";
                r.RepresentationType = "SweptSolid";
                r.Items.Add(body);
            }));
        });

    /// <summary>Something on the storey.</summary>
    public T Contained<T>(T product) where T : IfcProduct
    {
        Contents.RelatedElements.Add(product);
        return product;
    }

    /// <summary>A wall's layers - one, of a thickness - and how they sit on its axis.</summary>
    public void Layers(IfcWall wall, double thickness, IfcDirectionSenseEnum sense, double offset) =>
        New<IfcRelAssociatesMaterial>(relation =>
        {
            relation.RelatedObjects.Add(wall);
            relation.RelatingMaterial = New<IfcMaterialLayerSetUsage>(usage =>
            {
                usage.ForLayerSet = New<IfcMaterialLayerSet>(set =>
                {
                    set.LayerSetName = $"Block {thickness:0}";
                    set.MaterialLayers.Add(New<IfcMaterialLayer>(layer =>
                    {
                        layer.LayerThickness = thickness;
                        layer.Material = New<IfcMaterial>(material => material.Name = "Block");
                    }));
                });
                usage.LayerSetDirection = IfcLayerSetDirectionEnum.AXIS2;
                usage.DirectionSense = sense;
                usage.OffsetFromReferenceLine = offset;
            });
        });

    /// <summary>A straight wall from one point to another, height and thickness, its body round its axis as given.</summary>
    public IfcWall Wall(double x0, double y0, double x1, double y1, double thickness, double height, double fromAxis = 0)
    {
        var length = Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));
        var (dx, dy) = ((x1 - x0) / length, (y1 - y0) / length);
        var (low, high) = (fromAxis - thickness / 2, fromAxis + thickness / 2);
        return Contained(New<IfcWall>(wall =>
        {
            wall.Name = "Wall";
            wall.ObjectPlacement = Placed(x0, y0, 0, (dx, dy, 0));
            wall.Representation = Shape(Extrusion(height, null, (0, low), (length, low), (length, high), (0, high)), Polyline((0, 0), (length, 0)));
        }));
    }

    public IfcSpace Space(string name, double height, params (double X, double Y)[] outline)
    {
        var space = New<IfcSpace>(s =>
        {
            s.Name = name;
            s.LongName = name;
            s.ObjectPlacement = Placed();
            s.Representation = Shape(Extrusion(height, null, outline));
        });
        New<IfcRelAggregates>(r => { r.RelatingObject = Storey; r.RelatedObjects.Add(space); });
        return space;
    }

    /// <summary>Done: written, and read as a project.</summary>
    public Infrastructure.Interoperability.IfcImportResult Read()
    {
        _transaction.Commit();
        return Infrastructure.Interoperability.IfcImport.Read(Model, "Elsewhere");
    }

    public void Dispose() => Model.Dispose();
}
