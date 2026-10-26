using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;
using BIMDesigner.Core.Parameters;
using BIMDesigner.Core.Views;
using Xbim.Common;
using Xbim.Common.Step21;
using Xbim.Ifc;
using Xbim.Ifc2x3.GeometricConstraintResource;
using Xbim.Ifc2x3.GeometricModelResource;
using Xbim.Ifc2x3.GeometryResource;
using Xbim.Ifc2x3.Kernel;
using Xbim.Ifc2x3.MaterialResource;
using Xbim.Ifc2x3.MeasureResource;
using Xbim.Ifc2x3.ProductExtension;
using Xbim.Ifc2x3.ProfileResource;
using Xbim.Ifc2x3.PropertyResource;
using Xbim.Ifc2x3.RepresentationResource;
using Xbim.Ifc2x3.SharedBldgElements;
using Xbim.Ifc2x3.TopologyResource;
using Xbim.Ifc2x3.UtilityResource;
using Xbim.IO;

using CoreMaterial = BIMDesigner.Core.Materials.Material;
using CoreWall = BIMDesigner.Core.Architecture.Wall;
using CoreWindow = BIMDesigner.Core.Architecture.Window;
using Point3D = BIMDesigner.Core.Geometry.Point3D;

namespace BIMDesigner.Infrastructure.Interoperability;

/// <summary>
/// The model as IFC2x3 (specification section 8), Coordination View 2.0, for whoever is still on
/// it - older viewers, checkers and authoring tools that do not yet read IFC4.
///
/// Walls that stand straight up go out as <c>IfcWallStandardCase</c> - their axis, their body
/// extruded from their outline, their material layer set and which side it starts - floors as
/// slabs, ceilings as coverings, doors and windows in the openings they fill, rooms as spaces
/// and gridlines as a grid, each with its common property set and every parameter it has.
/// Everything else - a stair, a railing, a chimney, a pitched roof, furniture - goes out as what
/// it is, its body the faces the 3D view draws as a faceted solid, since IFC2x3 has no
/// triangulated face set.
/// </summary>
public static class Ifc2x3Export
{
    public static void Save(BimDocument document, string path, IfcExportOptions? options = null)
    {
        using var model = Build(document, options ?? new IfcExportOptions());
        model.SaveAs(path, StorageType.Ifc);
    }

    public static IfcStore Build(BimDocument document, IfcExportOptions? options = null)
    {
        options ??= new IfcExportOptions();
        var credentials = new XbimEditorCredentials
        {
            ApplicationDevelopersName = options.ApplicationName,
            ApplicationFullName = options.ApplicationName,
            ApplicationIdentifier = options.ApplicationName,
            ApplicationVersion = options.ApplicationVersion,
            EditorsGivenName = options.AuthorGivenName,
            EditorsFamilyName = options.AuthorFamilyName,
            EditorsOrganisationName = options.Organisation
        };

        var model = IfcStore.Create(credentials, XbimSchemaVersion.Ifc2X3, XbimStoreType.InMemoryModel);
        model.Header.FileDescription.Description.Clear();
        model.Header.FileDescription.Description.Add("ViewDefinition [CoordinationView_V2.0]");
        model.Header.FileName.Name = document.ProjectInformation.Name;
        model.Header.FileName.OriginatingSystem = $"{options.ApplicationName} {options.ApplicationVersion}";

        using (var transaction = model.BeginTransaction("Export IFC2x3"))
        {
            new Exporter(model, document).Run();
            transaction.Commit();
        }

        return model;
    }

    private sealed class Exporter
    {
        private readonly IfcStore _model;
        private readonly BimDocument _document;
        private readonly Dictionary<Guid, IfcBuildingStorey> _storeys = new();
        private readonly Dictionary<Guid, IfcMaterial> _materials = new();
        private readonly Dictionary<Guid, IfcMaterialLayerSet> _layerSets = new();
        private readonly Dictionary<Guid, IfcWallStandardCase> _walls = new();
        private readonly HashSet<Guid> _done = new();
        private IfcGeometricRepresentationContext _context = null!;
        private IfcLocalPlacement _origin = null!;

        public Exporter(IfcStore model, BimDocument document) => (_model, _document) = (model, document);

        private T New<T>(Action<T>? initialise = null) where T : IInstantiableEntity, IPersistEntity => _model.Instances.New(initialise ?? (_ => { }));

        public void Run()
        {
            using var rooms = new RoomsOf.Outlines();
            using var cache = ModelCache.Begin(_document);
            CreateProject();
            foreach (var material in _document.Materials) _materials[material.Id] = New<IfcMaterial>(m => m.Name = material.Name);

            foreach (var wall in _document.Walls) ExportWall(wall);
            foreach (var slab in _document.All<Slab>().Where(slab => slab is not Roof)) ExportSlab(slab);
            foreach (var opening in _document.Openings) ExportOpening(opening);
            foreach (var room in _document.All<Room>()) ExportRoom(room);
            ExportGrids();

            // Everything else as the 3D view draws it, a faceted solid to each.
            foreach (var group in ModelMeshBuilder.Build(_document).Where(mesh => !mesh.IsEmpty).GroupBy(mesh => mesh.OwnerId))
                if (!_done.Contains(group.Key) && _document.FindElement(group.Key) is { } element)
                    ExportShape(element, group.ToList());

            ExportClassifications();
        }

        /// <summary>Each element's classification, its type's or a room's own, as IFC2x3 keeps one: a reference to its code.</summary>
        private void ExportClassifications()
        {
            var table = _document.Classification;
            var products = _model.Instances.OfType<IfcRoot>().OfType<IfcObjectDefinition>()
                .GroupBy(item => item.GlobalId.ToString()).ToDictionary(group => group.Key, group => group.First());
            var coded = new Dictionary<string, List<IfcObjectDefinition>>(StringComparer.OrdinalIgnoreCase);
            foreach (var element in _document.Elements)
            {
                var code = element is Room room ? room.ClassificationCode : _document.ElementTypes.FirstOrDefault(type => type.Id == element.TypeId)?.ClassificationCode ?? string.Empty;
                if (string.IsNullOrWhiteSpace(code) || !products.TryGetValue(element.Id.ToIfc2x3().ToString(), out var item)) continue;
                if (!coded.TryGetValue(code.Trim(), out var list)) coded[code.Trim()] = list = new List<IfcObjectDefinition>();
                list.Add(item);
            }

            if (coded.Count == 0) return;
            var system = New<Xbim.Ifc2x3.ExternalReferenceResource.IfcClassification>(c =>
            {
                c.Name = table.System;
                c.Source = table.System.StartsWith("Uniclass", StringComparison.OrdinalIgnoreCase) ? "NBS" : "-";
                c.Edition = string.IsNullOrWhiteSpace(table.Edition) ? "-" : table.Edition;
            });

            foreach (var (code, items) in coded.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            {
                var reference = New<Xbim.Ifc2x3.ExternalReferenceResource.IfcClassificationReference>(r =>
                {
                    r.ItemReference = code;
                    r.Name = table.Find(code)?.Title;
                    r.ReferencedSource = system;
                });
                New<IfcRelAssociatesClassification>(relation =>
                {
                    relation.RelatingClassification = reference;
                    foreach (var item in items) relation.RelatedObjects.Add(item);
                });
            }
        }

        // ---- project and storeys --------------------------------------------------------

        private void CreateProject()
        {
            var units = New<IfcUnitAssignment>(assignment =>
            {
                assignment.Units.Add(New<IfcSIUnit>(u => { u.UnitType = IfcUnitEnum.LENGTHUNIT; u.Name = IfcSIUnitName.METRE; u.Prefix = IfcSIPrefix.MILLI; }));
                assignment.Units.Add(New<IfcSIUnit>(u => { u.UnitType = IfcUnitEnum.AREAUNIT; u.Name = IfcSIUnitName.SQUARE_METRE; }));
                assignment.Units.Add(New<IfcSIUnit>(u => { u.UnitType = IfcUnitEnum.VOLUMEUNIT; u.Name = IfcSIUnitName.CUBIC_METRE; }));
                assignment.Units.Add(New<IfcSIUnit>(u => { u.UnitType = IfcUnitEnum.PLANEANGLEUNIT; u.Name = IfcSIUnitName.RADIAN; }));
            });

            _context = New<IfcGeometricRepresentationContext>(context =>
            {
                context.ContextType = "Model";
                context.CoordinateSpaceDimension = 3;
                context.Precision = 1e-5;
                context.WorldCoordinateSystem = Placement(0, 0, 0);
                context.TrueNorth = Direction(0, 1, 0);
            });
            _origin = New<IfcLocalPlacement>(p => p.RelativePlacement = Placement(0, 0, 0));

            var information = _document.ProjectInformation;
            var project = New<IfcProject>(p =>
            {
                p.GlobalId = Guid.NewGuid().ToIfc2x3();
                p.Name = information.Name;
                p.LongName = information.Number;
                p.UnitsInContext = units;
                p.RepresentationContexts.Add(_context);
            });

            var site = New<IfcSite>(s => { s.Name = string.IsNullOrWhiteSpace(information.Address) ? "Site" : information.Address; s.CompositionType = IfcElementCompositionEnum.ELEMENT; s.ObjectPlacement = _origin; });
            var building = New<IfcBuilding>(b => { b.Name = string.IsNullOrWhiteSpace(information.Name) ? "Building" : information.Name; b.CompositionType = IfcElementCompositionEnum.ELEMENT; b.ObjectPlacement = _origin; });
            Aggregate(project, site);
            Aggregate(site, building);

            foreach (var level in _document.Levels.OrderBy(level => level.Elevation))
            {
                var storey = New<IfcBuildingStorey>(s =>
                {
                    s.GlobalId = level.Id.ToIfc2x3();
                    s.Name = level.Name;
                    s.CompositionType = IfcElementCompositionEnum.ELEMENT;
                    s.Elevation = level.Elevation;
                    s.ObjectPlacement = New<IfcLocalPlacement>(p => { p.PlacementRelTo = _origin; p.RelativePlacement = Placement(0, 0, level.Elevation); });
                });
                Aggregate(building, storey);
                _storeys[level.Id] = storey;
            }
        }

        private void Aggregate(IfcObjectDefinition whole, IfcObjectDefinition part) =>
            New<IfcRelAggregates>(r => { r.RelatingObject = whole; r.RelatedObjects.Add(part); });

        private IfcBuildingStorey? StoreyOf(Element element) =>
            _storeys.TryGetValue(element.LevelId, out var storey) ? storey : _storeys.Values.FirstOrDefault();

        private double StoreyElevation(Element element) => _document.FindLevel(element.LevelId)?.Elevation ?? 0;

        private void Contain(Element element, IfcProduct product)
        {
            if (StoreyOf(element) is not { } storey) return;
            New<IfcRelContainedInSpatialStructure>(r => { r.RelatingStructure = storey; r.RelatedElements.Add(product); });
        }

        // ---- walls --------------------------------------------------------------------

        private void ExportWall(CoreWall wall)
        {
            var type = _document.GetWallType(wall);
            if (type is null || wall.Length <= WallJoins.JoinTolerance || _document.FindType<CurtainWallType>(wall.TypeId) is not null) return;
            if (WallLean.Leans(wall, type) || WallProfile.Of(_document, wall) is not null || WallHoles.Of(_document, wall).Count > 0 || _document.GetWallTiers(wall).Count > 1) return;
            var height = wall.GetHeight(_document);
            if (height <= 0) return;

            var structure = type.Structure;
            var (bodyStart, _) = wall.GetBodyCentreline(structure);
            var outline = WallJoins.GetBandOutline(_document, wall, type, structure.TotalWidth / 2, -structure.TotalWidth / 2);
            var steps = wall.IsCurved ? 24 : 1;
            var axis = ToLocal(Enumerable.Range(0, steps + 1).Select(i => wall.PointAt(structure, wall.Length * i / steps, 0)).ToList(), bodyStart, wall.Direction);

            var product = New<IfcWallStandardCase>(w =>
            {
                w.GlobalId = wall.Id.ToIfc2x3();
                w.Name = $"{type.Name} {wall.Mark}".Trim();
                w.Tag = wall.Mark;
                w.ObjectPlacement = New<IfcLocalPlacement>(p =>
                {
                    p.PlacementRelTo = StoreyOf(wall)?.ObjectPlacement;
                    p.RelativePlacement = New<IfcAxis2Placement3D>(a =>
                    {
                        a.Location = Point(bodyStart.X, bodyStart.Y, wall.GetBaseElevation(_document) - StoreyElevation(wall));
                        a.Axis = Direction(0, 0, 1);
                        a.RefDirection = Direction(wall.Direction.X, wall.Direction.Y, 0);
                    });
                });
                w.Representation = New<IfcProductDefinitionShape>(shape =>
                {
                    shape.Representations.Add(New<IfcShapeRepresentation>(r =>
                    {
                        r.ContextOfItems = _context;
                        r.RepresentationIdentifier = "Axis";
                        r.RepresentationType = "Curve2D";
                        r.Items.Add(New<IfcPolyline>(line => { foreach (var p in axis) line.Points.Add(Point(p.X, p.Y)); }));
                    }));
                    shape.Representations.Add(Body(ToLocal(outline, bodyStart, wall.Direction), height));
                });
            });

            _walls[wall.Id] = product;
            _done.Add(wall.Id);
            Contain(wall, product);
            AssignLayers(product, type.Id, structure, wall.Flipped);
            Properties(product, wall, "Pset_WallCommon", new Dictionary<string, IfcValue?>
            {
                ["Reference"] = Label(type.TypeMark),
                ["IsExternal"] = new IfcBoolean(type.Function == WallFunction.Exterior),
                ["LoadBearing"] = new IfcBoolean(wall.StructuralUsage != StructuralUsage.NonBearing),
                ["FireRating"] = Label(type.FireRating),
                ["ThermalTransmittance"] = type.HeatTransferCoefficient > 0 ? new IfcThermalTransmittanceMeasure(type.HeatTransferCoefficient) : null
            });
        }

        // ---- floors and ceilings -----------------------------------------------------------

        private void ExportSlab(Slab slab)
        {
            if (slab.Boundary.Count < 3 || _document.FindType<SlabType>(slab.TypeId) is not { } type) return;
            var thickness = type.Structure.TotalWidth;
            if (thickness <= 0) return;

            var placement = New<IfcLocalPlacement>(p => { p.PlacementRelTo = StoreyOf(slab)?.ObjectPlacement; p.RelativePlacement = Placement(0, 0, slab.HeightOffset - thickness); });
            var shape = New<IfcProductDefinitionShape>(s => s.Representations.Add(Body(slab.Boundary.ToList(), thickness, slab.Holes)));

            IfcBuildingElement product = slab is Ceiling
                ? New<IfcCovering>(c => { c.GlobalId = slab.Id.ToIfc2x3(); c.Name = type.Name; c.PredefinedType = IfcCoveringTypeEnum.CEILING; c.ObjectPlacement = placement; c.Representation = shape; })
                : New<IfcSlab>(s => { s.GlobalId = slab.Id.ToIfc2x3(); s.Name = type.Name; s.PredefinedType = IfcSlabTypeEnum.FLOOR; s.ObjectPlacement = placement; s.Representation = shape; });

            _done.Add(slab.Id);
            Contain(slab, product);
            AssignLayers(product, type.Id, type.Structure, flipped: false, LayerDirection.Axis3);
            Properties(product, slab, slab is Ceiling ? "Pset_CoveringCommon" : "Pset_SlabCommon", new Dictionary<string, IfcValue?>
            {
                ["Reference"] = Label(type.TypeMark),
                ["IsExternal"] = new IfcBoolean(false),
                ["LoadBearing"] = new IfcBoolean(type.Function == SlabFunction.Structural),
                ["FireRating"] = Label(type.FireRating)
            });
        }

        // ---- doors and windows ------------------------------------------------------------

        private void ExportOpening(Opening opening)
        {
            if (_document.Find<Wall>(opening.HostWallId) is not { } wall || !_walls.TryGetValue(wall.Id, out var ifcWall)) return;
            if (_document.FindType<OpeningType>(opening.TypeId) is not { } type || _document.GetWallType(wall) is not { } wallType) return;

            var structure = wallType.Structure;
            var (from, to) = opening.GetSpan(type);
            var thickness = wallType.Width;
            var (bodyStart, _) = wall.GetBodyCentreline(structure);
            var profile = ToLocal(new[] { wall.PointAt(structure, from, -thickness), wall.PointAt(structure, to, -thickness), wall.PointAt(structure, to, thickness), wall.PointAt(structure, from, thickness) }, bodyStart, wall.Direction);
            var (sill, height) = opening.Placed(_document, type, wall);
            var width = opening.WidthOf(type);

            var hole = New<IfcOpeningElement>(o =>
            {
                o.Name = $"{type.Name} opening";
                o.ObjectPlacement = New<IfcLocalPlacement>(p => { p.PlacementRelTo = ifcWall.ObjectPlacement; p.RelativePlacement = Placement(0, 0, sill); });
                o.Representation = New<IfcProductDefinitionShape>(s => s.Representations.Add(Body(profile, height)));
            });
            New<IfcRelVoidsElement>(r => { r.RelatingBuildingElement = ifcWall; r.RelatedOpeningElement = hole; });

            var jamb = wall.PointAt(structure, from, 0);
            var chord = (wall.PointAt(structure, to, 0) - jamb).NormalisedOrDefault(wall.Direction);
            var origin = ToLocal(new[] { jamb - chord.PerpendicularLeft() * (type.Thickness / 2) }, bodyStart, wall.Direction)[0];
            var facing = ToLocal(new[] { bodyStart + chord }, bodyStart, wall.Direction)[0];
            var placement = New<IfcLocalPlacement>(p =>
            {
                p.PlacementRelTo = ifcWall.ObjectPlacement;
                p.RelativePlacement = New<IfcAxis2Placement3D>(a => { a.Location = Point(origin.X, origin.Y, sill); a.Axis = Direction(0, 0, 1); a.RefDirection = Direction(facing.X, facing.Y, 0); });
            });
            var panel = New<IfcProductDefinitionShape>(s => s.Representations.Add(Body(new List<Point2D> { new(0, 0), new(width, 0), new(width, type.Thickness), new(0, type.Thickness) }, height)));

            IfcBuildingElement product = opening is CoreWindow
                ? New<IfcWindow>(w => { w.GlobalId = opening.Id.ToIfc2x3(); w.Name = type.Name; w.Tag = opening.Mark; w.OverallWidth = width; w.OverallHeight = height; w.ObjectPlacement = placement; w.Representation = panel; })
                : New<IfcDoor>(d => { d.GlobalId = opening.Id.ToIfc2x3(); d.Name = type.Name; d.Tag = opening.Mark; d.OverallWidth = width; d.OverallHeight = height; d.ObjectPlacement = placement; d.Representation = panel; });
            New<IfcRelFillsElement>(r => { r.RelatingOpeningElement = hole; r.RelatedBuildingElement = product; });

            _done.Add(opening.Id);
            Contain(opening, product);
            Properties(product, opening, opening is CoreWindow ? "Pset_WindowCommon" : "Pset_DoorCommon", new Dictionary<string, IfcValue?>
            {
                ["Reference"] = Label(type.TypeMark),
                ["IsExternal"] = new IfcBoolean(wallType.Function == WallFunction.Exterior),
                ["FireRating"] = Label(type.FireRating)
            });
        }

        // ---- spaces and grids ------------------------------------------------------------------

        private void ExportRoom(Room room)
        {
            var boundary = room.GetBoundary(_document);
            if (!boundary.IsEnclosed || boundary.Polygon.Count < 3 || StoreyOf(room) is not { } storey) return;
            var height = room.UpperLimitOffset - room.BaseOffset;
            if (height <= 0) height = Levels.DefaultStoreyHeight;

            var space = New<IfcSpace>(s =>
            {
                s.GlobalId = room.Id.ToIfc2x3();
                s.Name = room.Number;
                s.LongName = room.Name;
                s.CompositionType = IfcElementCompositionEnum.ELEMENT;
                s.InteriorOrExteriorSpace = IfcInternalOrExternalEnum.INTERNAL;
                s.ObjectPlacement = New<IfcLocalPlacement>(p => { p.PlacementRelTo = storey.ObjectPlacement; p.RelativePlacement = Placement(0, 0, room.BaseOffset); });
                s.Representation = New<IfcProductDefinitionShape>(shape => shape.Representations.Add(Body(boundary.Polygon.ToList(), height)));
            });
            Aggregate(storey, space);
            _done.Add(room.Id);
            Properties(space, room, "Pset_SpaceCommon", new Dictionary<string, IfcValue?>
            {
                ["Reference"] = Label(room.Number),
                ["GrossPlannedArea"] = new IfcAreaMeasure(boundary.Area / 1e6)
            });
        }

        private void ExportGrids()
        {
            var grids = _document.All<Grid>().Where(grid => grid.Start.DistanceTo(grid.End) > 1).ToList();
            if (grids.Count == 0 || _storeys.Count == 0) return;
            var storey = _storeys[_document.Levels.OrderBy(level => level.Elevation).First().Id];
            var first = (grids[0].End - grids[0].Start).NormalisedOrDefault(Vector2D.UnitX);

            var axes = grids.Select(grid => (Grid: grid, Axis: New<IfcGridAxis>(axis =>
            {
                axis.AxisTag = grid.Name;
                axis.SameSense = true;
                axis.AxisCurve = New<IfcPolyline>(line => { line.Points.Add(Point(grid.Start.X, grid.Start.Y)); line.Points.Add(Point(grid.End.X, grid.End.Y)); });
            }))).ToList();

            var ifcGrid = New<IfcGrid>(g =>
            {
                g.GlobalId = Guid.NewGuid().ToIfc2x3();
                g.Name = "Grid";
                g.ObjectPlacement = New<IfcLocalPlacement>(p => { p.PlacementRelTo = storey.ObjectPlacement; p.RelativePlacement = Placement(0, 0, 0); });
                foreach (var (grid, axis) in axes)
                {
                    if (Math.Abs((grid.End - grid.Start).NormalisedOrDefault(Vector2D.UnitX).Dot(first)) >= Math.Cos(Math.PI / 4)) g.UAxes.Add(axis);
                    else g.VAxes.Add(axis);
                }
            });
            foreach (var grid in grids) _done.Add(grid.Id);
            New<IfcRelContainedInSpatialStructure>(r => { r.RelatingStructure = storey; r.RelatedElements.Add(ifcGrid); });
        }

        // ---- everything else -------------------------------------------------------------------

        private void ExportShape(Element element, List<Mesh3D> meshes)
        {
            var elevation = StoreyElevation(element);
            var shape = New<IfcProductDefinitionShape>(s => s.Representations.Add(New<IfcShapeRepresentation>(r =>
            {
                r.ContextOfItems = _context;
                r.RepresentationIdentifier = "Body";
                r.RepresentationType = "Brep";
                foreach (var mesh in meshes) r.Items.Add(Brep(mesh, elevation));
            })));
            var placement = New<IfcLocalPlacement>(p => { p.PlacementRelTo = StoreyOf(element)?.ObjectPlacement; p.RelativePlacement = Placement(0, 0, 0); });
            var name = _document.ElementTypes.FirstOrDefault(type => type.Id == element.TypeId)?.Name ?? element.ToString() ?? element.Category.ToString();

            void Set(IfcProduct product)
            {
                product.GlobalId = element.Id.ToIfc2x3();
                product.Name = name;
                product.ObjectPlacement = placement;
                product.Representation = shape;
            }

            IfcProduct made = element switch
            {
                Stair => New<IfcStair>(Set),
                Ramp => New<IfcRamp>(Set),
                Railing => New<IfcRailing>(Set),
                Beam => New<IfcBeam>(Set),
                Column => New<IfcColumn>(Set),
                Roof => New<IfcRoof>(Set),
                CoreWall or PolygonWall => New<IfcWall>(Set),
                Component component when _document.FindType<ComponentType>(component.TypeId) is { Kind: BuiltInCategory.Furniture or BuiltInCategory.Casework } => New<IfcFurnishingElement>(Set),
                Component component when _document.FindType<ComponentType>(component.TypeId) is { Kind: BuiltInCategory.PlumbingFixtures or BuiltInCategory.LightingFixtures or BuiltInCategory.MechanicalEquipment or BuiltInCategory.ElectricalFixtures } => New<Xbim.Ifc2x3.SharedBldgServiceElements.IfcFlowTerminal>(Set),
                _ => New<IfcBuildingElementProxy>(Set)
            };

            if (made is IfcElement placed)
            {
                Contain(element, placed);
                Properties(placed, element, null, new Dictionary<string, IfcValue?>());
            }
        }

        /// <summary>A mesh as a faceted solid: a closed shell of its triangles, each a face of three corners.</summary>
        private IfcFacetedBrep Brep(Mesh3D mesh, double elevation)
        {
            var points = mesh.Positions.Select(p => Point(p.X, p.Y, p.Z - elevation)).ToList();
            var shell = New<IfcClosedShell>(s =>
            {
                for (var i = 0; i + 2 < mesh.Indices.Count; i += 3)
                {
                    var (a, b, c) = (mesh.Indices[i], mesh.Indices[i + 1], mesh.Indices[i + 2]);
                    if (a == b || b == c || a == c) continue;
                    s.CfsFaces.Add(New<IfcFace>(face => face.Bounds.Add(New<IfcFaceOuterBound>(bound =>
                    {
                        bound.Orientation = true;
                        bound.Bound = New<IfcPolyLoop>(loop => { loop.Polygon.Add(points[a]); loop.Polygon.Add(points[b]); loop.Polygon.Add(points[c]); });
                    }))));
                }
            });
            return New<IfcFacetedBrep>(brep => brep.Outer = shell);
        }

        // ---- materials and properties ------------------------------------------------------------

        private enum LayerDirection { Axis2, Axis3 }

        private void AssignLayers(IfcBuildingElement element, Guid typeId, CompoundStructure structure, bool flipped, LayerDirection direction = LayerDirection.Axis2)
        {
            if (structure.Layers.Count == 0) return;
            if (!_layerSets.TryGetValue(typeId, out var set))
            {
                set = New<IfcMaterialLayerSet>(s =>
                {
                    s.LayerSetName = _document.ElementTypes.FirstOrDefault(type => type.Id == typeId)?.Name;
                    foreach (var layer in structure.Layers)
                        s.MaterialLayers.Add(New<IfcMaterialLayer>(l =>
                        {
                            l.Material = _materials.GetValueOrDefault(layer.MaterialId);
                            l.LayerThickness = layer.Thickness;
                        }));
                });
                _layerSets[typeId] = set;
            }

            var usage = New<IfcMaterialLayerSetUsage>(u =>
            {
                u.ForLayerSet = set;
                u.LayerSetDirection = direction == LayerDirection.Axis2 ? IfcLayerSetDirectionEnum.AXIS2 : IfcLayerSetDirectionEnum.AXIS3;
                u.DirectionSense = flipped ? IfcDirectionSenseEnum.NEGATIVE : IfcDirectionSenseEnum.POSITIVE;
                u.OffsetFromReferenceLine = direction == LayerDirection.Axis2 ? (flipped ? 1 : -1) * structure.TotalWidth / 2 : 0;
            });
            New<IfcRelAssociatesMaterial>(r => { r.RelatingMaterial = usage; r.RelatedObjects.Add(element); });
        }

        private void Properties(IfcObject product, Element element, string? standardSet, Dictionary<string, IfcValue?> standard)
        {
            if (standardSet is not null) Set(product, standardSet, standard);
            var parameters = new Dictionary<string, IfcValue?>();
            foreach (var parameter in element.GetInstanceParameters(_document)) parameters[parameter.Name] = Measure(parameter);
            Set(product, "BIMDesigner_Parameters", parameters);
        }

        private void Set(IfcObject product, string name, Dictionary<string, IfcValue?> values)
        {
            var properties = values.Where(entry => entry.Value is not null)
                .Select(entry => New<IfcPropertySingleValue>(p => { p.Name = entry.Key; p.NominalValue = entry.Value; }))
                .ToList();
            if (properties.Count == 0) return;
            var set = New<IfcPropertySet>(s => { s.Name = name; foreach (var property in properties) s.HasProperties.Add(property); });
            New<IfcRelDefinesByProperties>(r => { r.RelatingPropertyDefinition = set; r.RelatedObjects.Add(product); });
        }

        private static IfcValue? Measure(ParameterValue parameter) => parameter.Value switch
        {
            null => null,
            string text => string.IsNullOrWhiteSpace(text) ? null : new IfcText(text),
            bool flag => new IfcBoolean(flag),
            int number => new IfcInteger(number),
            decimal money => new IfcMonetaryMeasure((double)money),
            double number => parameter.DataType switch
            {
                ParameterDataType.Length => new IfcLengthMeasure(number),
                ParameterDataType.Area => new IfcAreaMeasure(number),
                ParameterDataType.Volume => new IfcVolumeMeasure(number),
                ParameterDataType.Angle => new IfcPlaneAngleMeasure(number),
                _ => new IfcReal(number)
            },
            var other => new IfcText(other.ToString() ?? string.Empty)
        };

        private static IfcLabel? Label(string? text) => string.IsNullOrWhiteSpace(text) ? null : new IfcLabel(text);

        // ---- geometry ----------------------------------------------------------------------------

        private IfcShapeRepresentation Body(IReadOnlyList<Point2D> outline, double depth, IReadOnlyList<IReadOnlyList<Point2D>>? holes = null) =>
            New<IfcShapeRepresentation>(r =>
            {
                r.ContextOfItems = _context;
                r.RepresentationIdentifier = "Body";
                r.RepresentationType = "SweptSolid";
                r.Items.Add(New<IfcExtrudedAreaSolid>(s =>
                {
                    s.SweptArea = holes is { Count: > 0 }
                        ? New<IfcArbitraryProfileDefWithVoids>(p =>
                        {
                            p.ProfileType = IfcProfileTypeEnum.AREA;
                            p.OuterCurve = Ring(outline, anticlockwise: true);
                            foreach (var hole in holes) p.InnerCurves.Add(Ring(hole, anticlockwise: false));
                        })
                        : New<IfcArbitraryClosedProfileDef>(p => { p.ProfileType = IfcProfileTypeEnum.AREA; p.OuterCurve = Ring(outline, anticlockwise: true); });
                    s.Position = Placement(0, 0, 0);
                    s.ExtrudedDirection = Direction(0, 0, 1);
                    s.Depth = depth;
                }));
            });

        private IfcPolyline Ring(IReadOnlyList<Point2D> outline, bool anticlockwise)
        {
            var ring = (Polygon2D.SignedArea(outline) >= 0) == anticlockwise ? outline : outline.Reverse().ToList();
            return New<IfcPolyline>(line =>
            {
                foreach (var point in ring) line.Points.Add(Point(point.X, point.Y));
                line.Points.Add(Point(ring[0].X, ring[0].Y));
            });
        }

        private static List<Point2D> ToLocal(IReadOnlyList<Point2D> outline, Point2D origin, Vector2D direction)
        {
            var across = direction.PerpendicularLeft();
            return outline.Select(point => new Point2D((point - origin).Dot(direction), (point - origin).Dot(across))).ToList();
        }

        private IfcAxis2Placement3D Placement(double x, double y, double z) => New<IfcAxis2Placement3D>(a => a.Location = Point(x, y, z));

        private IfcCartesianPoint Point(double x, double y, double z) => New<IfcCartesianPoint>(p => p.SetXYZ(x, y, z));

        private IfcCartesianPoint Point(double x, double y) => New<IfcCartesianPoint>(p => p.SetXY(x, y));

        private IfcDirection Direction(double x, double y, double z) => New<IfcDirection>(d => d.SetXYZ(x, y, z));
    }
}

internal static class Ifc2x3GuidExtensions
{
    public static IfcGloballyUniqueId ToIfc2x3(this Guid id) => IfcGloballyUniqueId.ConvertToBase64(id);
}
