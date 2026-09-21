using BIMDesigner.Core;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;
using BIMDesigner.Core.Parameters;
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
using Xbim.Ifc4.PropertyResource;
using Xbim.Ifc4.RepresentationResource;
using Xbim.Ifc4.SharedBldgElements;
using Xbim.Ifc4.UtilityResource;
using Xbim.IO;

using CoreMaterial = BIMDesigner.Core.Materials.Material;
using CoreWall = BIMDesigner.Core.Architecture.Wall;
using CoreWindow = BIMDesigner.Core.Architecture.Window;

namespace BIMDesigner.Infrastructure.Interoperability;

/// <summary>Who produced the file, which IFC records in every entity's owner history.</summary>
public sealed class IfcExportOptions
{
    public string ApplicationName { get; init; } = "BIMDesigner";

    public string ApplicationVersion { get; init; } = "0.1";

    public string AuthorGivenName { get; init; } = Environment.UserName;

    public string AuthorFamilyName { get; init; } = string.Empty;

    public string Organisation { get; init; } = string.Empty;
}

/// <summary>
/// Exports the model as IFC4 (specification section 8).
///
/// This is the difference between BIM and CAD at the point of handover: the receiving
/// application gets walls, storeys, spaces and materials that it understands as such, not a
/// pile of lines it has to guess at.
///
/// The IFC schema itself is handled by Xbim.Essentials rather than written by hand. IFC4 has
/// hundreds of entity types with strict attribute ordering and derived and inverse
/// attributes; a file that looks right to us and that another application quietly refuses is
/// the expensive failure here, and schema correctness is what the library is for. It lives in
/// Infrastructure and nothing in Core knows it exists.
///
/// Geometry is written as <c>IfcExtrudedAreaSolid</c> - real profiles swept to real heights -
/// rather than as triangles, because every element in this model genuinely is an extruded
/// outline. A tessellated export would throw away the fact that a wall is a wall.
/// </summary>
public static class IfcExport
{
    /// <summary>Our model is millimetres, and so is the file. Nothing is scaled on the way out.</summary>
    private const double Precision = 1e-5;

    public static void Save(BimDocument document, string path, IfcExportOptions? options = null)
    {
        using var model = Build(document, options ?? new IfcExportOptions());
        model.SaveAs(path, StorageType.Ifc);
    }

    /// <summary>
    /// Builds the whole model in memory. Separate from saving so tests can read it back
    /// without going near a file.
    /// </summary>
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

        var model = IfcStore.Create(credentials, XbimSchemaVersion.Ifc4, XbimStoreType.InMemoryModel);

        // The header says which subset of IFC this file claims to be. Receiving applications
        // read it to decide how to interpret what follows, and a file that declares nothing
        // gets treated with suspicion - so the view definition is stated rather than left
        // blank, and the originating system is us rather than whatever process is running.
        model.Header.FileDescription.Description.Clear();
        model.Header.FileDescription.Description.Add("ViewDefinition [CoordinationView]");

        model.Header.FileName.Name = document.ProjectInformation.Name;
        model.Header.FileName.OriginatingSystem = $"{options.ApplicationName} {options.ApplicationVersion}";

        var author = $"{options.AuthorGivenName} {options.AuthorFamilyName}".Trim();
        if (author.Length > 0) model.Header.FileName.AuthorName.Add(author);
        if (options.Organisation.Length > 0) model.Header.FileName.Organization.Add(options.Organisation);

        using (var transaction = model.BeginTransaction("Export"))
        {
            new Exporter(model, document).Run();
            transaction.Commit();
        }

        return model;
    }

    /// <summary>
    /// One export. It is a class rather than a pile of static methods because almost every
    /// step needs the same few lookups - which storey an element belongs to, which IFC
    /// material stands for one of ours - and threading those through arguments obscures what
    /// each step is doing.
    /// </summary>
    private sealed class Exporter
    {
        private readonly IfcStore _model;
        private readonly BimDocument _document;

        private readonly Dictionary<Guid, IfcBuildingStorey> _storeys = new();
        private readonly Dictionary<Guid, IfcMaterial> _materials = new();
        private readonly Dictionary<Guid, IfcTypeProduct> _types = new();
        private readonly Dictionary<Guid, IfcMaterialLayerSet> _layerSets = new();
        private readonly Dictionary<Guid, IfcWall> _walls = new();

        private IfcGeometricRepresentationContext _context = null!;
        private IfcLocalPlacement _origin = null!;

        public Exporter(IfcStore model, BimDocument document)
        {
            _model = model;
            _document = document;
        }

        private T New<T>(Action<T>? initialise = null) where T : IInstantiableEntity, IPersistEntity =>
            _model.Instances.New(initialise ?? (_ => { }));

        public void Run()
        {
            var project = CreateProject();
            var building = CreateSpatialStructure(project);

            foreach (var material in _document.Materials) _materials[material.Id] = CreateMaterial(material);

            foreach (var wall in _document.Walls) ExportWall(wall);
            foreach (var slab in _document.Elements.OfType<Slab>()) ExportSlab(slab);
            foreach (var opening in _document.Openings) ExportOpening(opening);
            foreach (var room in _document.Elements.OfType<Room>()) ExportRoom(room);

            _ = building;
        }

        // ---- project, units and the spatial tree --------------------------------

        private IfcProject CreateProject()
        {
            // Millimetres, degrees, square and cubic millimetres: the units the model is in,
            // so no coordinate is scaled on the way out and nothing is lost to rounding.
            var units = New<IfcUnitAssignment>(assignment =>
            {
                assignment.Units.Add(SiUnit(IfcUnitEnum.LENGTHUNIT, IfcSIUnitName.METRE, IfcSIPrefix.MILLI));
                assignment.Units.Add(SiUnit(IfcUnitEnum.AREAUNIT, IfcSIUnitName.SQUARE_METRE));
                assignment.Units.Add(SiUnit(IfcUnitEnum.VOLUMEUNIT, IfcSIUnitName.CUBIC_METRE));
                assignment.Units.Add(SiUnit(IfcUnitEnum.PLANEANGLEUNIT, IfcSIUnitName.RADIAN));
            });

            _context = New<IfcGeometricRepresentationContext>(context =>
            {
                context.ContextType = "Model";
                context.CoordinateSpaceDimension = 3;
                context.Precision = Precision;
                context.WorldCoordinateSystem = Placement(Point3D(0, 0, 0));
                context.TrueNorth = Direction(0, 1, 0);
            });

            _origin = New<IfcLocalPlacement>(placement => placement.RelativePlacement = Placement(Point3D(0, 0, 0)));

            var information = _document.ProjectInformation;

            return New<IfcProject>(project =>
            {
                project.GlobalId = Guid.NewGuid().ToIfc();
                project.Name = information.Name;
                project.LongName = information.Number;
                project.Phase = information.BuildingType;
                project.UnitsInContext = units;
                project.RepresentationContexts.Add(_context);
            });
        }

        /// <summary>
        /// Project contains site contains building contains storeys - the containment chain
        /// IFC expects. Elements hang off the storeys, and a storey that is not reachable from
        /// the project is a storey most viewers will not show.
        /// </summary>
        private IfcBuilding CreateSpatialStructure(IfcProject project)
        {
            var information = _document.ProjectInformation;

            var site = New<IfcSite>(s =>
            {
                s.Name = string.IsNullOrWhiteSpace(information.Address) ? "Site" : information.Address;
                s.CompositionType = IfcElementCompositionEnum.ELEMENT;
                s.ObjectPlacement = _origin;
            });

            var building = New<IfcBuilding>(b =>
            {
                b.Name = string.IsNullOrWhiteSpace(information.Name) ? "Building" : information.Name;
                b.CompositionType = IfcElementCompositionEnum.ELEMENT;
                b.ObjectPlacement = _origin;
            });

            Aggregate(project, site);
            Aggregate(site, building);

            foreach (var level in _document.Levels.OrderBy(level => level.Elevation))
            {
                var storey = New<IfcBuildingStorey>(s =>
                {
                    s.GlobalId = level.Id.ToIfc();
                    s.Name = level.Name;
                    s.CompositionType = IfcElementCompositionEnum.ELEMENT;
                    s.Elevation = level.Elevation;

                    // Each storey sits at its own height, and everything on it is placed
                    // relative to that - so moving a storey in IFC moves its contents too.
                    s.ObjectPlacement = New<IfcLocalPlacement>(placement =>
                    {
                        placement.PlacementRelTo = _origin;
                        placement.RelativePlacement = Placement(Point3D(0, 0, level.Elevation));
                    });
                });

                Aggregate(building, storey);
                _storeys[level.Id] = storey;
            }

            return building;
        }

        private IfcBuildingStorey? StoreyOf(Element element) =>
            _storeys.TryGetValue(element.LevelId, out var storey) ? storey : _storeys.Values.FirstOrDefault();

        /// <summary>Puts a product on its storey, which is how a viewer's tree is built.</summary>
        private void Contain(Element element, IfcProduct product)
        {
            if (StoreyOf(element) is not { } storey) return;

            New<IfcRelContainedInSpatialStructure>(relation =>
            {
                relation.RelatingStructure = storey;
                relation.RelatedElements.Add(product);
            });
        }

        private void Aggregate(IfcObjectDefinition whole, IfcObjectDefinition part) =>
            New<IfcRelAggregates>(relation =>
            {
                relation.RelatingObject = whole;
                relation.RelatedObjects.Add(part);
            });

        // ---- walls ---------------------------------------------------------------

        private void ExportWall(CoreWall wall)
        {
            var type = _document.GetWallType(wall);
            if (type is null || wall.Length <= WallJoins.JoinTolerance) return;

            var height = wall.GetHeight(_document);
            if (height <= 0) return;

            var structure = type.Structure;
            var (bodyStart, _) = wall.GetBodyCentreline(structure);
            var half = structure.TotalWidth / 2;

            // The outline the plan and the 3D view draw, mitres and all, so the exported wall
            // is the wall that was modelled rather than an idealised rectangle.
            var outline = WallJoins.GetBandOutline(_document, wall, type, half, -half);

            var ifcWall = New<IfcWall>(w =>
            {
                w.GlobalId = wall.Id.ToIfc();
                w.Name = $"{type.Name} {wall.Mark}".Trim();
                w.PredefinedType = IfcWallTypeEnum.SOLIDWALL;
                w.Tag = wall.Mark;
                w.ObjectPlacement = PlacementFor(wall, bodyStart, wall.BaseOffset);
                w.Representation = Extrude(ToLocal(outline, bodyStart, wall.Direction), height);
            });

            _walls[wall.Id] = ifcWall;

            Contain(wall, ifcWall);
            AssignType(ifcWall, type, () => CreateWallType(type));
            AssignLayers(ifcWall, type.Id, structure, wall.Flipped);

            AddProperties(ifcWall, wall, "Pset_WallCommon", new Dictionary<string, IfcValue?>
            {
                ["Reference"] = Reference(type.TypeMark),
                ["IsExternal"] = new IfcBoolean(type.Function == WallFunction.Exterior),
                ["LoadBearing"] = new IfcBoolean(wall.StructuralUsage != StructuralUsage.NonBearing),
                ["FireRating"] = Label(type.FireRating),
                ["AcousticRating"] = type.AcousticRating > 0 ? new IfcLabel(type.AcousticRating.ToString("0.#")) : null,
                ["ThermalTransmittance"] = type.HeatTransferCoefficient > 0
                    ? new IfcThermalTransmittanceMeasure(type.HeatTransferCoefficient)
                    : null
            });
        }

        private IfcWallType CreateWallType(WallType type) => New<IfcWallType>(t =>
        {
            t.GlobalId = type.Id.ToIfc();
            t.Name = type.Name;
            t.Tag = type.TypeMark;
            t.Description = type.Description;
            t.PredefinedType = IfcWallTypeEnum.SOLIDWALL;
        });

        // ---- slabs ---------------------------------------------------------------

        private void ExportSlab(Slab slab)
        {
            if (slab.Boundary.Count < 3) return;
            if (_document.FindType<SlabType>(slab.TypeId) is not { } type) return;

            var thickness = type.Structure.TotalWidth;
            if (thickness <= 0) return;

            // Levels carry their own height, so a slab is placed relative to its storey.
            var bottom = slab.HeightOffset - thickness;
            var profile = slab.Boundary.Select(point => new Point2D(point.X, point.Y)).ToList();

            var placement = New<IfcLocalPlacement>(p =>
            {
                p.PlacementRelTo = StoreyOf(slab)?.ObjectPlacement;
                p.RelativePlacement = Placement(Point3D(0, 0, bottom));
            });

            IfcElement product = slab is Ceiling
                // A ceiling is a finish, not a structural slab. IFC has a separate entity for
                // it, and a receiving application will schedule the two differently.
                ? New<IfcCovering>(c =>
                {
                    c.GlobalId = slab.Id.ToIfc();
                    c.Name = type.Name;
                    c.PredefinedType = IfcCoveringTypeEnum.CEILING;
                    c.ObjectPlacement = placement;
                    c.Representation = Extrude(profile, thickness);
                })
                : New<IfcSlab>(s =>
                {
                    s.GlobalId = slab.Id.ToIfc();
                    s.Name = type.Name;
                    s.PredefinedType = slab is Roof ? IfcSlabTypeEnum.ROOF : IfcSlabTypeEnum.FLOOR;
                    s.ObjectPlacement = placement;
                    s.Representation = Extrude(profile, thickness);
                });

            Contain(slab, product);
            AssignLayers(product, type.Id, type.Structure, flipped: false);

            AddProperties(product, slab, StandardSetFor(slab), new Dictionary<string, IfcValue?>
            {
                ["Reference"] = Reference(type.TypeMark),
                ["IsExternal"] = new IfcBoolean(slab is Roof),
                ["LoadBearing"] = new IfcBoolean(type.Function == SlabFunction.Structural),
                ["FireRating"] = Label(type.FireRating),
                ["AcousticRating"] = type.AcousticRating > 0 ? new IfcLabel(type.AcousticRating.ToString("0.#")) : null,
                ["ThermalTransmittance"] = type.HeatTransferCoefficient > 0
                    ? new IfcThermalTransmittanceMeasure(type.HeatTransferCoefficient)
                    : null
            });
        }

        // ---- doors and windows ---------------------------------------------------

        /// <summary>
        /// An opening is exported as a real hole: an <c>IfcOpeningElement</c> that voids the
        /// wall, with the door or window filling it.
        ///
        /// Our own 3D view cuts the hole out of the wall mesh directly, but IFC wants the
        /// whole wall plus the void, because that is what lets the receiving application move
        /// the door, schedule the reveal, or take the door out and leave the hole.
        /// </summary>
        private void ExportOpening(Opening opening)
        {
            if (_document.Walls.FirstOrDefault(w => w.Id == opening.HostWallId) is not { } wall) return;
            if (!_walls.TryGetValue(wall.Id, out var ifcWall)) return;
            if (_document.FindType<OpeningType>(opening.TypeId) is not { } type) return;
            if (_document.GetWallType(wall) is not { } wallType) return;

            var (from, to) = opening.GetSpan(type);
            var thickness = wallType.Width;
            var (bodyStart, _) = wall.GetBodyCentreline(wallType.Structure);

            // A box through the wall, in the wall's own coordinates: along it, across its full
            // thickness, from sill to head.
            var profile = new List<Point2D>
            {
                new(from, -thickness),
                new(to, -thickness),
                new(to, thickness),
                new(from, thickness)
            };

            var sill = wall.BaseOffset + opening.SillHeight;

            var voidPlacement = New<IfcLocalPlacement>(p =>
            {
                p.PlacementRelTo = ifcWall.ObjectPlacement;
                p.RelativePlacement = Placement(Point3D(0, 0, opening.SillHeight));
            });

            var hole = New<IfcOpeningElement>(o =>
            {
                o.Name = $"{type.Name} opening";
                o.PredefinedType = IfcOpeningElementTypeEnum.OPENING;
                o.ObjectPlacement = voidPlacement;
                o.Representation = Extrude(profile, type.Height);
            });

            New<IfcRelVoidsElement>(relation =>
            {
                relation.RelatingBuildingElement = ifcWall;
                relation.RelatedOpeningElement = hole;
            });

            var placement = New<IfcLocalPlacement>(p =>
            {
                p.PlacementRelTo = ifcWall.ObjectPlacement;
                p.RelativePlacement = Placement(Point3D(from, -type.Thickness / 2, opening.SillHeight));
            });

            var panel = new List<Point2D>
            {
                new(0, 0),
                new(type.Width, 0),
                new(type.Width, type.Thickness),
                new(0, type.Thickness)
            };

            IfcElement product = opening is CoreWindow
                ? New<IfcWindow>(w =>
                {
                    w.GlobalId = opening.Id.ToIfc();
                    w.Name = type.Name;
                    w.Tag = opening.Mark;
                    w.OverallWidth = type.Width;
                    w.OverallHeight = type.Height;
                    w.ObjectPlacement = placement;
                    w.Representation = Extrude(panel, type.Height);
                })
                : New<IfcDoor>(d =>
                {
                    d.GlobalId = opening.Id.ToIfc();
                    d.Name = type.Name;
                    d.Tag = opening.Mark;
                    d.OverallWidth = type.Width;
                    d.OverallHeight = type.Height;
                    d.ObjectPlacement = placement;
                    d.Representation = Extrude(panel, type.Height);
                });

            New<IfcRelFillsElement>(relation =>
            {
                relation.RelatingOpeningElement = hole;
                relation.RelatedBuildingElement = product;
            });

            Contain(opening, product);

            AddProperties(product, opening,
                opening is CoreWindow ? "Pset_WindowCommon" : "Pset_DoorCommon",
                new Dictionary<string, IfcValue?>
                {
                    ["Reference"] = Reference(type.TypeMark),
                    ["IsExternal"] = new IfcBoolean(wallType.Function == WallFunction.Exterior),
                    ["FireRating"] = Label(type.FireRating),
                    ["AcousticRating"] = type.AcousticRating > 0 ? new IfcLabel(type.AcousticRating.ToString("0.#")) : null,
                    ["ThermalTransmittance"] = type.HeatTransferCoefficient > 0
                        ? new IfcThermalTransmittanceMeasure(type.HeatTransferCoefficient)
                        : null,
                    ["SillHeight"] = new IfcLengthMeasure(sill)
                });
        }

        // ---- rooms ---------------------------------------------------------------

        /// <summary>
        /// A room becomes an <c>IfcSpace</c>, part of the storey's decomposition rather than
        /// something contained in it - a space is a division of the storey, not an object
        /// standing on it.
        ///
        /// An unenclosed room is skipped rather than exported flat: a space with no boundary
        /// would arrive as a zero-area room in someone else's schedule.
        /// </summary>
        private void ExportRoom(Room room)
        {
            var boundary = room.GetBoundary(_document);
            if (!boundary.IsEnclosed || boundary.Polygon.Count < 3) return;

            if (StoreyOf(room) is not { } storey) return;

            var height = room.UpperLimitOffset - room.BaseOffset;
            if (height <= 0) height = Levels.DefaultStoreyHeight;

            var space = New<IfcSpace>(s =>
            {
                s.GlobalId = room.Id.ToIfc();
                s.Name = room.Number;
                s.LongName = room.Name;
                s.CompositionType = IfcElementCompositionEnum.ELEMENT;
                s.PredefinedType = IfcSpaceTypeEnum.SPACE;

                s.ObjectPlacement = New<IfcLocalPlacement>(p =>
                {
                    p.PlacementRelTo = storey.ObjectPlacement;
                    p.RelativePlacement = Placement(Point3D(0, 0, room.BaseOffset));
                });

                s.Representation = Extrude(boundary.Polygon.ToList(), height);
            });

            Aggregate(storey, space);

            AddProperties(space, room, "Pset_SpaceCommon", new Dictionary<string, IfcValue?>
            {
                ["Reference"] = Reference(room.Number),
                ["IsExternal"] = new IfcBoolean(false),
                ["GrossPlannedArea"] = new IfcAreaMeasure(boundary.Area),
                ["Category"] = Label(room.Department)
            });
        }

        // ---- materials, types and properties -------------------------------------

        private IfcMaterial CreateMaterial(CoreMaterial material) => New<IfcMaterial>(m =>
        {
            m.Name = material.Name;
            m.Category = material.ClassificationCode;
        });

        /// <summary>
        /// Writes a compound structure as an <c>IfcMaterialLayerSet</c>: the layers, in order,
        /// with their thicknesses and materials.
        ///
        /// This is the part most exporters lose. A layered wall flattened into one solid
        /// arrives as an anonymous lump of "wall", and every takeoff, U-value and fire rating
        /// downstream has to be entered again by hand. Our compound structure maps onto IFC's
        /// layer set almost exactly, so it survives the trip.
        /// </summary>
        private void AssignLayers(IfcElement element, Guid typeId, CompoundStructure structure, bool flipped)
        {
            if (structure.Layers.Count == 0) return;

            if (!_layerSets.TryGetValue(typeId, out var layerSet))
            {
                layerSet = New<IfcMaterialLayerSet>(set =>
                {
                    for (var i = 0; i < structure.Layers.Count; i++)
                    {
                        var layer = structure.Layers[i];
                        var category = LayerCategory(layer.Function, i, structure);

                        set.MaterialLayers.Add(New<IfcMaterialLayer>(l =>
                        {
                            l.Material = _materials.GetValueOrDefault(layer.MaterialId);
                            l.LayerThickness = layer.Thickness;
                            l.Name = LayerFunctions.Label(layer.Function);
                            if (category is not null) l.Category = category;
                        }));
                    }
                });

                _layerSets[typeId] = layerSet;
            }

            // The usage says which side of the element's own axis the layers start from, which
            // is what lets a receiving application put the brick on the right face.
            var usage = New<IfcMaterialLayerSetUsage>(u =>
            {
                u.ForLayerSet = layerSet;
                u.LayerSetDirection = IfcLayerSetDirectionEnum.AXIS2;
                u.DirectionSense = flipped ? IfcDirectionSenseEnum.NEGATIVE : IfcDirectionSenseEnum.POSITIVE;
                u.OffsetFromReferenceLine = -structure.TotalWidth / 2;
            });

            New<IfcRelAssociatesMaterial>(relation =>
            {
                relation.RelatingMaterial = usage;
                relation.RelatedObjects.Add(element);
            });
        }

        private void AssignType(IfcObject element, ElementType type, Func<IfcTypeProduct> create)
        {
            if (!_types.TryGetValue(type.Id, out var ifcType))
            {
                ifcType = create();
                _types[type.Id] = ifcType;
            }

            New<IfcRelDefinesByType>(relation =>
            {
                relation.RelatingType = ifcType;
                relation.RelatedObjects.Add(element);
            });
        }

        /// <summary>
        /// Writes the standard property set for this kind of element, and alongside it every
        /// instance parameter the element reports.
        ///
        /// The second half comes free: our parameters already know their own name, type and
        /// value, so they map onto IFC properties without a translation table. It is the same
        /// framework that draws the property panel and fills the schedules.
        /// </summary>
        private void AddProperties(
            IfcObject product, Element element, string standardSet, Dictionary<string, IfcValue?> standard)
        {
            WriteSet(product, standardSet, standard);

            var parameters = new Dictionary<string, IfcValue?>();

            foreach (var parameter in element.GetInstanceParameters(_document))
                parameters[parameter.Name] = Measure(parameter);

            WriteSet(product, "BIMDesigner_Parameters", parameters);
        }

        private void WriteSet(IfcObject product, string name, Dictionary<string, IfcValue?> values)
        {
            var properties = values
                .Where(entry => entry.Value is not null)
                .Select(entry => New<IfcPropertySingleValue>(property =>
                {
                    property.Name = entry.Key;
                    property.NominalValue = entry.Value;
                }))
                .ToList();

            if (properties.Count == 0) return;

            var set = New<IfcPropertySet>(s =>
            {
                s.Name = name;
                foreach (var property in properties) s.HasProperties.Add(property);
            });

            New<IfcRelDefinesByProperties>(relation =>
            {
                relation.RelatingPropertyDefinition = set;
                relation.RelatedObjects.Add(product);
            });
        }

        /// <summary>
        /// A parameter as the IFC measure it actually is.
        ///
        /// The parameter already declares what kind of quantity it holds, so a length goes out
        /// as <c>IfcLengthMeasure</c> and a U-value as <c>IfcThermalTransmittanceMeasure</c>.
        /// Writing every number as a bare <c>IfcReal</c> - which is what this did at first -
        /// hands the receiving application a number with no idea what it measures: it cannot
        /// convert the units, schedule it as a length, or check it against anything.
        /// </summary>
        /// <summary>
        /// The IFC 4 category of a layer: load bearing, insulation, or a finish on the outer or
        /// inner side of the core. Receiving applications use it to tell the structure from
        /// what is fixed to it. Substrates and membranes have no standard category.
        /// </summary>
        private static string? LayerCategory(LayerFunction function, int index, CompoundStructure structure) =>
            function switch
            {
                LayerFunction.Structure => "LoadBearing",
                LayerFunction.ThermalAir => "Insulation",
                LayerFunction.Finish1 or LayerFunction.Finish2 =>
                    structure.CoreStartIndex >= 0 && index > structure.CoreEndIndex ? "InnerFinish" : "OuterFinish",
                _ => null
            };

        private static IfcValue? Measure(ParameterValue parameter)
        {
            var value = parameter.Value;

            return value switch
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

                _ => new IfcText(value.ToString() ?? string.Empty)
            };
        }

        /// <summary>
        /// The standard property set for a horizontal element. Each kind has its own, and a
        /// ceiling's properties belong to a covering rather than to a slab.
        /// </summary>
        private static string StandardSetFor(Slab slab) => slab switch
        {
            Roof => "Pset_RoofCommon",
            Ceiling => "Pset_CoveringCommon",
            _ => "Pset_SlabCommon"
        };

        /// <summary>A label, or nothing at all when there is no value to write.</summary>
        private static IfcLabel? Label(string? text) =>
            string.IsNullOrWhiteSpace(text) ? null : new IfcLabel(text);

        private static IfcIdentifier? Reference(string? text) =>
            string.IsNullOrWhiteSpace(text) ? null : new IfcIdentifier(text);

        // ---- geometry ------------------------------------------------------------

        /// <summary>
        /// A closed outline swept straight up. Every element in this model is one of these,
        /// which is why the export can be real solids rather than triangles.
        /// </summary>
        private IfcProductDefinitionShape Extrude(IReadOnlyList<Point2D> outline, double depth)
        {
            var solid = New<IfcExtrudedAreaSolid>(s =>
            {
                s.SweptArea = Profile(outline);
                s.Position = Placement(Point3D(0, 0, 0));
                s.ExtrudedDirection = Direction(0, 0, 1);
                s.Depth = depth;
            });

            var representation = New<IfcShapeRepresentation>(r =>
            {
                r.ContextOfItems = _context;
                r.RepresentationIdentifier = "Body";
                r.RepresentationType = "SweptSolid";
                r.Items.Add(solid);
            });

            return New<IfcProductDefinitionShape>(shape => shape.Representations.Add(representation));
        }

        private IfcArbitraryClosedProfileDef Profile(IReadOnlyList<Point2D> outline)
        {
            // Anticlockwise, and closed by repeating the first point: an IFC profile curve that
            // does not come back to where it started is not a profile.
            var ring = Polygon2D.SignedArea(outline) >= 0 ? outline : outline.Reverse().ToList();

            var polyline = New<IfcPolyline>(line =>
            {
                foreach (var point in ring) line.Points.Add(Point2Dto(point));
                line.Points.Add(Point2Dto(ring[0]));
            });

            return New<IfcArbitraryClosedProfileDef>(profile =>
            {
                profile.ProfileType = IfcProfileTypeEnum.AREA;
                profile.OuterCurve = polyline;
            });
        }

        /// <summary>
        /// A plan outline expressed in an element's own coordinates: along it, and across it.
        /// IFC places an object and then describes its shape relative to that placement, which
        /// is what makes the shape reusable and the object movable.
        /// </summary>
        private static List<Point2D> ToLocal(IReadOnlyList<Point2D> outline, Point2D origin, Vector2D direction)
        {
            var across = direction.PerpendicularLeft();

            return outline
                .Select(point => new Point2D((point - origin).Dot(direction), (point - origin).Dot(across)))
                .ToList();
        }

        private IfcLocalPlacement PlacementFor(CoreWall wall, Point2D origin, double elevation) =>
            New<IfcLocalPlacement>(placement =>
            {
                placement.PlacementRelTo = StoreyOf(wall)?.ObjectPlacement;
                placement.RelativePlacement = New<IfcAxis2Placement3D>(axis =>
                {
                    axis.Location = Point3D(origin.X, origin.Y, elevation);
                    axis.Axis = Direction(0, 0, 1);
                    axis.RefDirection = Direction(wall.Direction.X, wall.Direction.Y, 0);
                });
            });

        private IfcAxis2Placement3D Placement(IfcCartesianPoint location) =>
            New<IfcAxis2Placement3D>(axis => axis.Location = location);

        private IfcCartesianPoint Point3D(double x, double y, double z) =>
            New<IfcCartesianPoint>(point => point.SetXYZ(x, y, z));

        private IfcCartesianPoint Point2Dto(Point2D point) =>
            New<IfcCartesianPoint>(p => p.SetXY(point.X, point.Y));

        private IfcDirection Direction(double x, double y, double z) =>
            New<IfcDirection>(direction => direction.SetXYZ(x, y, z));

        private IfcSIUnit SiUnit(IfcUnitEnum type, IfcSIUnitName name, IfcSIPrefix? prefix = null) =>
            New<IfcSIUnit>(unit =>
            {
                unit.UnitType = type;
                unit.Name = name;
                unit.Prefix = prefix;
            });
    }
}

internal static class IfcGuidExtensions
{
    /// <summary>
    /// Our element id as IFC's own 22-character identifier.
    ///
    /// Deriving it from the element's own id rather than making a fresh one means exporting
    /// the same project twice produces the same identifiers - which is what lets the
    /// receiving end tell an edited wall from a new one.
    /// </summary>
    public static IfcGloballyUniqueId ToIfc(this Guid id) => IfcGloballyUniqueId.ConvertToBase64(id);
}
