using BIMDesigner.Core;
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
using Xbim.Ifc4.ElectricalDomain;
using Xbim.Ifc4.HvacDomain;
using Xbim.Ifc4.PlumbingFireProtectionDomain;
using Xbim.Ifc4.SharedBldgElements;
using Xbim.Ifc4.SharedFacilitiesElements;
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
            // Rooms traced and chimneys measured once for the whole export, nothing changing under it.
            using var rooms = new RoomsOf.Outlines();
            using var heights = new Chimneys.Heights();

            var project = CreateProject();
            var building = CreateSpatialStructure(project);

            foreach (var material in _document.Materials) _materials[material.Id] = CreateMaterial(material);

            foreach (var wall in _document.Walls) ExportWall(wall);
            foreach (var slab in _document.Elements.OfType<Slab>()) ExportSlab(slab);
            foreach (var edge in _document.Elements.OfType<RoofEdgeSweep>()) ExportRoofEdgeSweep(edge);
            foreach (var pipe in _document.Elements.OfType<Downpipe>()) ExportDownpipe(pipe);
            foreach (var drain in _document.Elements.OfType<RoofDrain>()) ExportRoofDrain(drain);
            foreach (var chimney in _document.Elements.OfType<Chimney>()) ExportChimney(chimney);
            foreach (var stair in _document.Elements.OfType<Stair>()) ExportStair(stair);
            foreach (var ramp in _document.Elements.OfType<Ramp>()) ExportRamp(ramp);
            foreach (var railing in _document.Elements.OfType<Railing>()) ExportRailing(railing);
            foreach (var beam in _document.Elements.OfType<Beam>()) ExportBeam(beam);
            foreach (var ground in _document.Elements.OfType<Toposurface>()) ExportGround(ground);
            foreach (var system in _document.Elements.OfType<BeamSystem>()) ExportBeamSystem(system);
            foreach (var component in _document.Elements.OfType<Component>()) ExportComponent(component);
            foreach (var shaped in _document.Elements.OfType<PolygonWall>()) ExportPolygonWall(shaped);
            foreach (var roofWindow in _document.Elements.OfType<RoofWindow>()) ExportRoofWindow(roofWindow);
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

            if (_document.FindType<CurtainWallType>(wall.TypeId) is { } curtainType)
            {
                ExportCurtainWall(wall, curtainType, bodyStart);
                return;
            }

            // The outline the plan and the 3D view draw, mitres and all, so the exported wall
            // is the wall that was modelled rather than an idealised rectangle.
            var outline = WallJoins.GetBandOutline(_document, wall, type, half, -half);

            var ifcWall = New<IfcWall>(w =>
            {
                w.GlobalId = wall.Id.ToIfc();
                w.Name = $"{_document.FindType<StackedWallType>(wall.TypeId)?.Name ?? type.Name} {wall.Mark}".Trim();
                w.PredefinedType = IfcWallTypeEnum.SOLIDWALL;
                w.Tag = wall.Mark;
                // Relative to its storey: where the wall actually starts, whether that is the
                // base offset or the top of a floor it stands on.
                w.ObjectPlacement = PlacementFor(wall, bodyStart,
                    wall.GetBaseElevation(_document) - (_document.FindLevel(wall.LevelId)?.Elevation ?? 0));
                w.Representation = WallLean.Leans(wall, type) || WallProfile.Of(_document, wall) is not null ||
                                   WallHoles.Of(_document, wall).Count > 0 ||
                                   _document.GetWallTiers(wall).Any(t => t.Type.Sweeps.Any(s => s.Kind == SweepKind.Reveal || s.CutsWall)) ||
                                   WallSweeps.Placed(_document, wall).Any(p => p.Sweep.Kind == SweepKind.Reveal || p.Sweep.CutsWall)
                    ? Tessellated(wall, bodyStart)
                    : ExtrudeTiers(wall, bodyStart, outline, height);
            });

            _walls[wall.Id] = ifcWall;

            Contain(wall, ifcWall);

            // Sweeps go with their wall but are not part of its body: a skirting is its own
            // thing to a receiving application, so it is exported as one.
            if (_document.GetWallTiers(wall).Any(t => t.Type.Sweeps.Any(s => s.Kind == SweepKind.Sweep)) ||
                WallSweeps.Placed(_document, wall).Any(p => p.Sweep.Kind == SweepKind.Sweep))
            {
                var sweeps = New<IfcBuildingElementProxy>(p =>
                {
                    p.GlobalId = IfcGloballyUniqueId.ConvertToBase64(SweepId(wall.Id));
                    p.Name = $"Wall sweeps {wall.Mark}".Trim();
                    p.ObjectPlacement = PlacementFor(wall, bodyStart,
                        wall.GetBaseElevation(_document) - (_document.FindLevel(wall.LevelId)?.Elevation ?? 0));
                    p.Representation = Tessellated(wall, bodyStart, MeshKind.Sweep);
                });

                Contain(wall, sweeps);
            }
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

            // A roof is an IfcRoof, whatever its shape. IFC keeps a separate entity for it
            // because a roof is not a floor: it weathers the building, it is scheduled by its
            // own covering, and a receiving application groups it with the parts it is made of.
            if (slab is Roof roof)
            {
                ExportRoof(roof, type, thickness);
                return;
            }

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
                    s.PredefinedType = IfcSlabTypeEnum.FLOOR;
                    s.ObjectPlacement = placement;
                    s.Representation = Extrude(profile, thickness);
                });

            Contain(slab, product);
            AssignLayers(product, type.Id, type.Structure, flipped: false);
            AddSlabProperties(product, slab, type);

            // The shafts through it: the whole slab with a void for each, as IFC keeps a hole,
            // so a receiving application can move the shaft or fill it in.
            foreach (var shaft in Shafts.Through(_document, slab))
            {
                var voidPlacement = New<IfcLocalPlacement>(p =>
                {
                    p.PlacementRelTo = placement;
                    p.RelativePlacement = Placement(Point3D(0, 0, -1));
                });

                var hole = New<IfcOpeningElement>(o =>
                {
                    o.Name = "Shaft opening";
                    o.PredefinedType = IfcOpeningElementTypeEnum.OPENING;
                    o.ObjectPlacement = voidPlacement;
                    o.Representation = Extrude(shaft.Outline, thickness + 2);
                });

                New<IfcRelVoidsElement>(relation =>
                {
                    relation.RelatingBuildingElement = product;
                    relation.RelatedOpeningElement = hole;
                });
            }
        }

        private void AddSlabProperties(IfcElement product, Slab slab, SlabType type) =>
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

        /// <summary>
        /// A roof as IfcRoof, with the shape it came out as recorded on it.
        ///
        /// IFC lets a roof be either one entity with a shape of its own or an assembly of the
        /// parts it is built from, and this uses both: a flat roof is a single solid, while a
        /// pitched one is an IfcRoof that aggregates one IfcSlab per face. That is what the
        /// standard asks for and what it means - each face is a real piece of construction
        /// with its own plane, and only the whole thing is placed on the storey, because a
        /// part of an aggregate is not separately contained in the building.
        /// </summary>
        private void ExportRoof(Roof roof, SlabType type, double thickness)
        {
            var surface = roof.Surface(_document);
            var storey = StoreyOf(roof);

            // A roof with holes in it, or trimmed by another, is exported face by face as built,
            // even when flat: one solid from its outline would fill the holes back in.
            var cut = roof.Openings.Count > 0 || roof.DormerOpenings.Count > 0 || roof.JoinedTo is not null;
            var flat = surface.Form == RoofForm.Flat && !cut;

            // A flat roof is one solid extruded up from its underside; a pitched one is placed
            // at its storey and each of its faces carries its own frame.
            var placement = New<IfcLocalPlacement>(p =>
            {
                p.PlacementRelTo = storey?.ObjectPlacement;
                p.RelativePlacement = Placement(Point3D(0, 0, flat ? roof.HeightOffset : 0));
            });

            var product = New<IfcRoof>(r =>
            {
                r.GlobalId = roof.Id.ToIfc();
                r.Name = type.Name;
                r.PredefinedType = RoofTypeOf(roof.Form);
                r.ObjectPlacement = placement;

                // A flat roof has one plane and no parts worth naming, so it carries its own
                // body rather than aggregating a single face.
                if (flat) r.Representation = Extrude(roof.Boundary.ToList(), thickness);
            });

            if (flat)
            {
                AssignLayers(product, type.Id, type.Structure, flipped: false);
            }
            else
            {
                var storeyElevation = _document.FindLevel(roof.LevelId)?.Elevation ?? 0;

                foreach (var facet in RoofJoin.CutFacets(_document, roof))
                {
                    var face = ExportRoofFacet(roof, type, facet, thickness, storeyElevation, storey);
                    if (face is not null) Aggregate(product, face);
                }
            }

            Contain(roof, product);
            AddSlabProperties(product, roof, type);
        }

        /// <summary>
        /// One face of a pitched roof: a slab lying in the face's own plane.
        ///
        /// The face is placed in a frame of its own - up the slope, along the eave, and square
        /// out of the surface - and its outline is measured in that frame. Extruding it square
        /// to the plane is what makes the build-up the thickness a roof is actually specified
        /// at, rather than the deeper slice a vertical extrusion would give.
        /// </summary>
        private IfcSlab? ExportRoofFacet(
            Roof roof, SlabType type, RoofFacet facet, double thickness,
            double storeyElevation, IfcBuildingStorey? storey)
        {
            if (facet.Outline.Count < 3) return null;

            var plane = facet.Plane;
            var scale = Math.Sqrt(plane.A * plane.A + plane.B * plane.B + 1);

            // Square out of the surface, pointing up out of the roof.
            var normal = (X: -plane.A / scale, Y: -plane.B / scale, Z: 1 / scale);

            // Along the eave: the level direction in the plane. A flat face has no slope to
            // run along, so it takes the X axis.
            var fall = Math.Sqrt(plane.A * plane.A + plane.B * plane.B);
            var along = fall > 1e-9
                ? (X: -plane.B / fall, Y: plane.A / fall, Z: 0.0)
                : (X: 1.0, Y: 0.0, Z: 0.0);

            // Up the slope, completing the frame: normal × along.
            var up = (
                X: normal.Y * along.Z - normal.Z * along.Y,
                Y: normal.Z * along.X - normal.X * along.Z,
                Z: normal.X * along.Y - normal.Y * along.X);

            // The face's plane is the roof's underside, so the frame starts on it and the
            // extrusion runs up out of it by the build-up.
            var first = facet.Outline[0];
            var origin = (
                X: first.X,
                Y: first.Y,
                Z: plane.HeightAt(first) - storeyElevation);

            var outline = facet.Outline
                .Select(point =>
                {
                    var dx = point.X - origin.X;
                    var dy = point.Y - origin.Y;
                    var dz = plane.HeightAt(point) - storeyElevation - origin.Z;

                    return new Point2D(
                        dx * along.X + dy * along.Y + dz * along.Z,
                        dx * up.X + dy * up.Y + dz * up.Z);
                })
                .ToList();

            var face = New<IfcSlab>(s =>
            {
                s.Name = type.Name;
                s.PredefinedType = IfcSlabTypeEnum.ROOF;
                s.ObjectPlacement = New<IfcLocalPlacement>(p =>
                {
                    p.PlacementRelTo = storey?.ObjectPlacement;
                    p.RelativePlacement = New<IfcAxis2Placement3D>(axis =>
                    {
                        axis.Location = Point3D(origin.X, origin.Y, origin.Z);
                        axis.Axis = Direction(normal.X, normal.Y, normal.Z);
                        axis.RefDirection = Direction(along.X, along.Y, along.Z);
                    });
                });
                s.Representation = Extrude(outline, thickness);
            });

            AssignLayers(face, type.Id, type.Structure, flipped: false);
            return face;
        }

        private static IfcRoofTypeEnum RoofTypeOf(RoofForm form) => form switch
        {
            RoofForm.Flat => IfcRoofTypeEnum.FLAT_ROOF,
            RoofForm.Shed => IfcRoofTypeEnum.SHED_ROOF,
            RoofForm.Gable => IfcRoofTypeEnum.GABLE_ROOF,
            RoofForm.Hip => IfcRoofTypeEnum.HIP_ROOF,
            RoofForm.Gambrel => IfcRoofTypeEnum.GAMBREL_ROOF,
            RoofForm.Barrel => IfcRoofTypeEnum.BARREL_ROOF,
            RoofForm.Conical => IfcRoofTypeEnum.FREEFORM,
            _ => IfcRoofTypeEnum.FREEFORM
        };

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

            // A box through the wall, in the wall's own coordinates: along it, across well beyond
            // both faces, from sill to head. Taken from the wall's frame, so in a curved wall it
            // spans the arc of the opening.
            var structure = wallType.Structure;
            var profile = ToLocal(new[]
            {
                wall.PointAt(structure, from, -thickness),
                wall.PointAt(structure, to, -thickness),
                wall.PointAt(structure, to, thickness),
                wall.PointAt(structure, from, thickness)
            }, bodyStart, wall.Direction);

            // The door or window itself sits square across its opening: on the chord between
            // its jambs, which on a straight wall is the wall itself.
            var jambFrom = wall.PointAt(structure, from, 0);
            var jambTo = wall.PointAt(structure, to, 0);
            var chord = (jambTo - jambFrom).NormalisedOrDefault(wall.Direction);
            var inward = chord.PerpendicularLeft();
            var panelOrigin = ToLocal(new[] { jambFrom - inward * (type.Thickness / 2) }, bodyStart, wall.Direction)[0];
            var panelDirection = ToLocal(new[] { bodyStart + chord }, bodyStart, wall.Direction)[0];

            // Where it actually is: brought inside a wall lower than it, as a dormer's gable is.
            var (placedSill, placedHeight) = opening.Placed(_document, type, wall);
            var sill = wall.BaseOffset + placedSill;

            var voidPlacement = New<IfcLocalPlacement>(p =>
            {
                p.PlacementRelTo = ifcWall.ObjectPlacement;
                p.RelativePlacement = Placement(Point3D(0, 0, placedSill));
            });

            var hole = New<IfcOpeningElement>(o =>
            {
                o.Name = $"{type.Name} opening";
                o.PredefinedType = IfcOpeningElementTypeEnum.OPENING;
                o.ObjectPlacement = voidPlacement;
                o.Representation = Extrude(profile, placedHeight);
            });

            New<IfcRelVoidsElement>(relation =>
            {
                relation.RelatingBuildingElement = ifcWall;
                relation.RelatedOpeningElement = hole;
            });

            var placement = New<IfcLocalPlacement>(p =>
            {
                p.PlacementRelTo = ifcWall.ObjectPlacement;
                p.RelativePlacement = New<IfcAxis2Placement3D>(axis =>
                {
                    axis.Location = Point3D(panelOrigin.X, panelOrigin.Y, placedSill);
                    axis.Axis = Direction(0, 0, 1);
                    axis.RefDirection = Direction(panelDirection.X, panelDirection.Y, 0);
                });
            });

            var width = opening.WidthOf(type);
            var height = placedHeight;

            var panel = new List<Point2D>
            {
                new(0, 0),
                new(width, 0),
                new(width, type.Thickness),
                new(0, type.Thickness)
            };

            IfcElement product = opening is CoreWindow
                ? New<IfcWindow>(w =>
                {
                    w.GlobalId = opening.Id.ToIfc();
                    w.Name = type.Name;
                    w.Tag = opening.Mark;
                    w.OverallWidth = width;
                    w.OverallHeight = height;
                    w.ObjectPlacement = placement;
                    w.Representation = Extrude(panel, height);
                })
                : New<IfcDoor>(d =>
                {
                    d.GlobalId = opening.Id.ToIfc();
                    d.Name = type.Name;
                    d.Tag = opening.Mark;
                    d.OverallWidth = width;
                    d.OverallHeight = height;
                    d.ObjectPlacement = placement;
                    d.Representation = Extrude(panel, height);
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
        /// A stable id for a wall's sweeps, made from the wall's: the same wall exports its sweeps
        /// under the same id every time, as it does itself.
        /// </summary>
        private static Guid SweepId(Guid wallId) => PartId(wallId, 0x5A, 0xA5);

        /// <summary>A stable id for one part of a wall, made from the wall's and a salt for the part.</summary>
        private static Guid PartId(Guid wallId, byte salt, byte pepper)
        {
            var bytes = wallId.ToByteArray();
            bytes[15] ^= salt;
            bytes[14] ^= pepper;
            return new Guid(bytes);
        }

        /// <summary>
        /// A curtain wall as IFC has it: an <c>IfcCurtainWall</c> made up of its panels, as one
        /// <c>IfcPlate</c>, and its mullions, as one <c>IfcMember</c>, each with its real shape.
        /// </summary>
        private void ExportCurtainWall(CoreWall wall, CurtainWallType type, Point2D bodyStart)
        {
            var elevation = wall.GetBaseElevation(_document) - (_document.FindLevel(wall.LevelId)?.Elevation ?? 0);

            var curtain = New<IfcCurtainWall>(c =>
            {
                c.GlobalId = wall.Id.ToIfc();
                c.Name = $"{type.Name} {wall.Mark}".Trim();
                c.Tag = wall.Mark;
                c.PredefinedType = IfcCurtainWallTypeEnum.NOTDEFINED;
                c.ObjectPlacement = PlacementFor(wall, bodyStart, elevation);
            });
            Contain(wall, curtain);

            var panels = New<IfcPlate>(p =>
            {
                p.GlobalId = IfcGloballyUniqueId.ConvertToBase64(PartId(wall.Id, 0x3C, 0xC3));
                p.Name = $"Panels {wall.Mark}".Trim();
                p.PredefinedType = IfcPlateTypeEnum.CURTAIN_PANEL;
                p.ObjectPlacement = PlacementFor(wall, bodyStart, elevation);
                p.Representation = Tessellated(wall, bodyStart, MeshKind.Glazing, MeshKind.Wall, MeshKind.DoorLeaf);
            });
            Aggregate(curtain, panels);

            if (type.MullionWidth > 0)
            {
                var mullions = New<IfcMember>(m =>
                {
                    m.GlobalId = IfcGloballyUniqueId.ConvertToBase64(PartId(wall.Id, 0x69, 0x96));
                    m.Name = $"Mullions {wall.Mark}".Trim();
                    m.PredefinedType = IfcMemberTypeEnum.MULLION;
                    m.ObjectPlacement = PlacementFor(wall, bodyStart, elevation);
                    m.Representation = Tessellated(wall, bodyStart, MeshKind.Mullion);
                });
                Aggregate(curtain, mullions);
            }

            AddProperties(curtain, wall, "Pset_CurtainWallCommon", new Dictionary<string, IfcValue?>
            {
                ["IsExternal"] = new IfcBoolean(type.Function == WallFunction.Exterior),
                ["Reference"] = new IfcIdentifier(type.Name)
            });
        }

        /// <summary>A roof window as an IfcWindow of the skylight kind, its frame and pane as one body.</summary>
        private void ExportRoofWindow(RoofWindow window)
        {
            var meshes = RoofWindows.Meshes(_document, window);
            if (meshes.Count == 0) return;

            var type = _document.FindType<RoofWindowType>(window.TypeId) is { } found ? window.Built(found) : null;
            var storey = StoreyOf(window);
            var storeyElevation = _document.FindLevel(window.LevelId)?.Elevation ?? 0;

            var product = New<IfcWindow>(w =>
            {
                w.GlobalId = window.Id.ToIfc();
                w.Name = type?.Name ?? "Roof Window";
                w.ObjectType = "Roof Window";
                w.Tag = window.Mark;
                w.PredefinedType = IfcWindowTypeEnum.SKYLIGHT;
                if (type is not null)
                {
                    w.OverallWidth = type.Width;
                    w.OverallHeight = type.Height;
                }

                w.ObjectPlacement = New<IfcLocalPlacement>(placement =>
                {
                    placement.PlacementRelTo = storey?.ObjectPlacement;
                    placement.RelativePlacement = Placement(Point3D(0, 0, 0));
                });

                var representation = New<IfcShapeRepresentation>(r =>
                {
                    r.ContextOfItems = _context;
                    r.RepresentationIdentifier = "Body";
                    r.RepresentationType = "Tessellation";

                    foreach (var mesh in meshes)
                    {
                        var points = New<IfcCartesianPointList3D>(list =>
                        {
                            foreach (var point in mesh.Positions)
                                list.CoordList.GetAt(list.CoordList.Count).AddRange(new IfcLengthMeasure[] { point.X, point.Y, point.Z - storeyElevation });
                        });

                        r.Items.Add(New<IfcTriangulatedFaceSet>(faces =>
                        {
                            faces.Coordinates = points;
                            faces.Closed = true;
                            for (var i = 0; i + 2 < mesh.Indices.Count; i += 3)
                                faces.CoordIndex.GetAt(faces.CoordIndex.Count).AddRange(new IfcPositiveInteger[]
                                {
                                    mesh.Indices[i] + 1, mesh.Indices[i + 1] + 1, mesh.Indices[i + 2] + 1
                                });
                        }));
                    }
                });

                w.Representation = New<IfcProductDefinitionShape>(shape => shape.Representations.Add(representation));
            });

            Contain(window, product);
        }

        /// <summary>
        /// A fascia or gutter: its own element to a receiving application, placed on the storey of
        /// the roof it runs along, its body the triangles the 3D view draws.
        /// </summary>
        private void ExportRoofEdgeSweep(RoofEdgeSweep sweep)
        {
            var mesh = RoofEdgeSweeps.Mesh(_document, sweep, sweep.LevelId);
            if (mesh.IsEmpty) return;

            var kind = sweep switch { Gutter => "Gutter", Soffit => "Soffit", _ => "Fascia" };
            var proxy = ExportMeshProxy(sweep, mesh, _document.ElementTypes.FirstOrDefault(type => type.Id == sweep.TypeId)?.Name ?? kind, kind);

            // A soffit's board, and the air it lets into the roof - what eaves ventilation is checked by.
            if (sweep is Soffit && _document.FindType<SoffitType>(sweep.TypeId) is { } soffit)
                WriteSet(proxy, "Soffit", new Dictionary<string, IfcValue?>
                {
                    ["Board"] = new IfcLabel(EnumText.Humanise(soffit.Board)),
                    ["FreeAirAreaPerMetre"] = new IfcReal(soffit.FreeAirArea),
                    ["FreeAirArea"] = new IfcAreaMeasure(soffit.FreeAirArea * sweep.Length(_document) / 1000 / 1e6)
                });
        }

        /// <summary>A downpipe: its own element to a receiving application, its body the triangles the 3D view draws.</summary>
        private void ExportDownpipe(Downpipe pipe)
        {
            if (Downpipes.Mesh(_document, pipe) is not { } mesh) return;

            var gutter = _document.Elements.OfType<Gutter>().FirstOrDefault(candidate => candidate.Id == pipe.GutterId);
            var type = gutter is null ? null : _document.FindType<GutterType>(gutter.TypeId);
            var proxy = ExportMeshProxy(pipe, mesh, type is null ? "Downpipe" : $"Downpipe {Downpipes.Size(type)}", "Downpipe");

            if (type is not null && Downpipes.Path(_document, pipe) is { } path)
                WriteSet(proxy, "Downpipe", new Dictionary<string, IfcValue?>
                {
                    ["Shape"] = new IfcLabel(EnumText.Humanise(type.DownpipeShape)),
                    ["Size"] = new IfcLabel(Downpipes.Size(type)),
                    ["Length"] = new IfcLengthMeasure(path.Length)
                });
        }

        /// <summary>
        /// What a drawn stair is in IFC, by its turns: none, a straight run; a landing straight on,
        /// two straight runs; one, two or three right angles one way, a quarter, half or
        /// three-quarter turn - winding, on winders; two opposite ways, two quarter turns.
        /// </summary>
        private static IfcStairTypeEnum DrawnStairType(Stair stair)
        {
            var turns = stair.PathTurns.Take(Math.Max(0, stair.PathLengths.Count - 1)).ToList();
            var winding = stair.Corner != StairCorner.Landing;
            if (turns.Count == 0) return IfcStairTypeEnum.STRAIGHT_RUN_STAIR;
            if (turns.All(turn => Math.Abs(turn) < 0.5)) return turns.Count == 1 ? IfcStairTypeEnum.TWO_STRAIGHT_RUN_STAIR : IfcStairTypeEnum.NOTDEFINED;
            if (!turns.All(turn => Math.Abs(Math.Abs(turn) - 90) < 0.5)) return IfcStairTypeEnum.NOTDEFINED;
            if (turns.All(turn => turn > 0) || turns.All(turn => turn < 0))
                return turns.Count switch
                {
                    1 => winding ? IfcStairTypeEnum.QUARTER_WINDING_STAIR : IfcStairTypeEnum.QUARTER_TURN_STAIR,
                    2 => winding ? IfcStairTypeEnum.HALF_WINDING_STAIR : IfcStairTypeEnum.HALF_TURN_STAIR,
                    3 => winding ? IfcStairTypeEnum.THREE_QUARTER_WINDING_STAIR : IfcStairTypeEnum.THREE_QUARTER_TURN_STAIR,
                    _ => IfcStairTypeEnum.NOTDEFINED
                };
            return turns.Count == 2 ? winding ? IfcStairTypeEnum.TWO_QUARTER_WINDING_STAIR : IfcStairTypeEnum.TWO_QUARTER_TURN_STAIR : IfcStairTypeEnum.NOTDEFINED;
        }

        /// <summary>
        /// A stair as IFC has it: an IfcStair made up of its flights - IfcStairFlights, each with
        /// its risers, treads and going - its landings as IfcSlabs, the strings or spine that
        /// carry them as an IfcMember, and its handrails, balustrades and guarding as IfcRailings.
        /// The stair itself is drawn by the line walked up it, its axis; its pieces are its body.
        /// </summary>
        private void ExportStair(Stair stair)
        {
            var parts = StairModel.Parts(_document, stair);
            if (parts.Stair.Count == 0) return;

            var type = Stairs.TypeOf(_document, stair);
            var layout = Stairs.Layout(_document, stair);
            var storey = StoreyOf(stair);
            var storeyElevation = _document.FindLevel(stair.LevelId)?.Elevation ?? 0;
            IfcObjectPlacement OnStorey() => New<IfcLocalPlacement>(placement =>
            {
                placement.PlacementRelTo = storey?.ObjectPlacement;
                placement.RelativePlacement = Placement(Point3D(0, 0, 0));
            });

            var product = New<IfcStair>(s =>
            {
                s.GlobalId = stair.Id.ToIfc();
                s.Name = type.Name;
                s.Tag = stair.Mark;
                s.PredefinedType = stair.Shape switch
                {
                    StairShape.QuarterTurn when stair.Corner != StairCorner.Landing => IfcStairTypeEnum.QUARTER_WINDING_STAIR,
                    StairShape.QuarterTurn => IfcStairTypeEnum.QUARTER_TURN_STAIR,
                    StairShape.HalfTurn when stair.Corner != StairCorner.Landing => IfcStairTypeEnum.HALF_WINDING_STAIR,
                    StairShape.HalfTurn => IfcStairTypeEnum.HALF_TURN_STAIR,
                    StairShape.Spiral => IfcStairTypeEnum.SPIRAL_STAIR,
                    StairShape.Curved => IfcStairTypeEnum.CURVED_RUN_STAIR,
                    StairShape.Bifurcated => IfcStairTypeEnum.DOUBLE_RETURN_STAIR,
                    StairShape.ThreeQuarterTurn when stair.Corner != StairCorner.Landing => IfcStairTypeEnum.THREE_QUARTER_WINDING_STAIR,
                    StairShape.ThreeQuarterTurn => IfcStairTypeEnum.THREE_QUARTER_TURN_STAIR,
                    StairShape.Custom => DrawnStairType(stair),
                    StairShape.AlternatingTread or StairShape.Ladder => IfcStairTypeEnum.USERDEFINED,
                    _ => IfcStairTypeEnum.STRAIGHT_RUN_STAIR
                };
                if (stair.Shape is StairShape.AlternatingTread or StairShape.Ladder) s.ObjectType = stair.Shape == StairShape.Ladder ? "LADDER" : "ALTERNATING_TREAD_STAIR";
                s.ObjectPlacement = OnStorey();
                s.Representation = AxisShape(StairModel.Axis(layout), storeyElevation);
            });
            Contain(stair, product);

            // Its flights and landings, one after another up it, and what carries them.
            var flights = 0;
            foreach (var piece in parts.Pieces.Where(piece => piece.Meshes.Count > 0))
            {
                if (piece.Kind == StairPieceKind.Landing)
                {
                    Aggregate(product, New<IfcSlab>(slab =>
                    {
                        slab.GlobalId = Guid.NewGuid().ToIfc();
                        slab.Name = $"{type.Name} - landing at step {piece.Steps[0].Number}";
                        slab.PredefinedType = IfcSlabTypeEnum.LANDING;
                        slab.ObjectPlacement = OnStorey();
                        slab.Representation = TessellatedBody(piece.Meshes, storeyElevation);
                    }));
                    continue;
                }

                var number = ++flights;
                var flight = New<IfcStairFlight>(f =>
                {
                    f.GlobalId = Guid.NewGuid().ToIfc();
                    f.Name = $"{type.Name} - flight {number}";
                    f.PredefinedType = layout.Shape switch
                    {
                        StairShape.Spiral => IfcStairFlightTypeEnum.SPIRAL,
                        StairShape.Curved => IfcStairFlightTypeEnum.CURVED,
                        _ when piece.Steps.Any(step => step.Kind == StairStepKind.Winder) => IfcStairFlightTypeEnum.WINDER,
                        _ => IfcStairFlightTypeEnum.STRAIGHT
                    };
                    f.ObjectPlacement = OnStorey();
                    f.Representation = TessellatedBody(piece.Meshes, storeyElevation);
                });
                Aggregate(product, flight);
                WriteSet(flight, "Pset_StairFlightCommon", new Dictionary<string, IfcValue?>
                {
                    ["NumberOfRiser"] = new IfcCountMeasure(piece.Steps.Count + 1),
                    ["NumberOfTreads"] = new IfcCountMeasure(piece.Steps.Count),
                    ["RiserHeight"] = new IfcPositiveLengthMeasure(layout.Rise),
                    ["TreadLength"] = new IfcPositiveLengthMeasure(layout.Going),
                    ["NosingLength"] = new IfcLengthMeasure(type.Nosing),
                    ["WalkingLineOffset"] = new IfcPositiveLengthMeasure(layout.Width / 2),
                    ["TreadLengthAtInnerSide"] = layout.GoingAt is { } going && going(0) > 0 ? new IfcPositiveLengthMeasure(going(0)) : null,
                    ["WaistThickness"] = type.Construction == StairConstruction.Concrete ? new IfcPositiveLengthMeasure(150) : null
                });
            }

            if (parts.Stringers.Count > 0)
                Aggregate(product, New<IfcMember>(member =>
                {
                    member.GlobalId = Guid.NewGuid().ToIfc();
                    member.Name = $"{type.Name} - {(type.Support == StairSupport.MonoStringer ? "spine" : "strings")}";
                    member.PredefinedType = IfcMemberTypeEnum.STRINGER;
                    member.ObjectPlacement = OnStorey();
                    member.Representation = TessellatedBody(parts.Stringers, storeyElevation);
                }));

            WriteSet(product, "Pset_StairCommon", new Dictionary<string, IfcValue?>
            {
                ["Reference"] = string.IsNullOrEmpty(type.TypeMark) ? null : new IfcIdentifier(type.TypeMark),
                ["NumberOfRiser"] = new IfcCountMeasure(layout.Risers),
                ["NumberOfTreads"] = new IfcCountMeasure(layout.Steps.Count(step => step.Kind != StairStepKind.Landing)),
                ["RiserHeight"] = new IfcPositiveLengthMeasure(layout.Rise),
                ["TreadLength"] = new IfcPositiveLengthMeasure(layout.Going),
                ["NosingLength"] = new IfcLengthMeasure(type.Nosing),
                ["RequiredHeadroom"] = new IfcPositiveLengthMeasure(Stairs.Headroom(_document.ProjectInformation.ClearanceStandard, stair.Shape)),
                ["IsExternal"] = new IfcBoolean(stair.Outside)
            });
            WriteSet(product, "Stair", new Dictionary<string, IfcValue?>
            {
                ["Construction"] = new IfcLabel(EnumText.Humanise(type.Construction)),
                ["Support"] = type.Construction == StairConstruction.Timber ? new IfcLabel(EnumText.Humanise(type.Support)) : null,
                ["Balustrade"] = new IfcLabel(EnumText.Humanise(type.Balustrade)),
                ["Corner"] = stair.Shape is StairShape.QuarterTurn or StairShape.HalfTurn ? new IfcLabel(EnumText.Humanise(stair.Corner)) : null,
                ["Use"] = new IfcLabel(EnumText.Humanise(stair.Use)),
                ["LoftConversion"] = new IfcBoolean(stair.LoftConversion),
                ["Width"] = new IfcPositiveLengthMeasure(layout.Width),
                ["Pitch"] = new IfcReal(Math.Round(layout.Pitch, 2)),
                ["Regulations"] = new IfcText(Stairs.Verdict(_document, stair))
            });

            // Its railings as IFC has them: its handrails on walls, its balustrades, and the guarding round its stairwell.
            foreach (var (meshes, kind, name, height) in new[]
                     {
                         (parts.Handrails, IfcRailingTypeEnum.HANDRAIL, "handrails", type.HandrailHeight),
                         (parts.Balustrades, IfcRailingTypeEnum.BALUSTRADE, "balustrades", type.HandrailHeight),
                         (parts.Guarding, IfcRailingTypeEnum.GUARDRAIL, "guarding", type.GuardHeight)
                     })
            {
                if (meshes.Count == 0) continue;
                var railing = New<IfcRailing>(r =>
                {
                    r.GlobalId = Guid.NewGuid().ToIfc();
                    r.Name = $"{type.Name} - {name}";
                    r.PredefinedType = kind;
                    r.ObjectPlacement = OnStorey();
                    r.Representation = TessellatedBody(meshes, storeyElevation);
                });
                Aggregate(product, railing);
                WriteSet(railing, "Pset_RailingCommon", new Dictionary<string, IfcValue?>
                {
                    ["Height"] = new IfcPositiveLengthMeasure(height),
                    ["Diameter"] = type.HandrailProfile == HandrailProfile.Round ? new IfcPositiveLengthMeasure(type.HandrailWidth) : null
                });
            }
        }

        /// <summary>
        /// A ramp as IFC has it: an IfcRamp made up of its flights - IfcRampFlights, each with its
        /// slope and clear width - its landings as IfcSlabs, its kerbs as a member, and its handrails
        /// and guarding as IfcRailings. The ramp itself is drawn by the line walked up it.
        /// </summary>
        private void ExportRamp(Ramp ramp)
        {
            var parts = RampModel.Parts(_document, ramp);
            if (parts.All.Count == 0) return;

            var type = Ramps.TypeOf(_document, ramp);
            var layout = Ramps.Layout(_document, ramp);
            var storey = StoreyOf(ramp);
            var storeyElevation = _document.FindLevel(ramp.LevelId)?.Elevation ?? 0;
            IfcObjectPlacement OnStorey() => New<IfcLocalPlacement>(placement =>
            {
                placement.PlacementRelTo = storey?.ObjectPlacement;
                placement.RelativePlacement = Placement(Point3D(0, 0, 0));
            });

            var flights = layout.FlightCount;
            var product = New<IfcRamp>(r =>
            {
                r.GlobalId = ramp.Id.ToIfc();
                r.Name = type.Name;
                r.Tag = ramp.Mark;
                r.PredefinedType = ramp.Shape switch
                {
                    RampShape.QuarterTurn => IfcRampTypeEnum.QUARTER_TURN_RAMP,
                    RampShape.HalfTurn => IfcRampTypeEnum.HALF_TURN_RAMP,
                    RampShape.Custom => IfcRampTypeEnum.NOTDEFINED,
                    _ when flights >= 2 => IfcRampTypeEnum.TWO_STRAIGHT_RUN_RAMP,
                    _ => IfcRampTypeEnum.STRAIGHT_RUN_RAMP
                };
                r.ObjectPlacement = OnStorey();
                r.Representation = AxisShape(RampModel.Axis(layout), storeyElevation);
            });
            Contain(ramp, product);

            // Its flights and landings, one after another up it.
            var number = 0;
            foreach (var (piece, body) in parts.Pieces.Where(piece => !piece.Body.IsEmpty))
            {
                if (piece.Kind == RampPieceKind.Landing)
                {
                    Aggregate(product, New<IfcSlab>(slab =>
                    {
                        slab.GlobalId = Guid.NewGuid().ToIfc();
                        slab.Name = $"{type.Name} - landing at {piece.Low:0}";
                        slab.PredefinedType = IfcSlabTypeEnum.LANDING;
                        slab.ObjectPlacement = OnStorey();
                        slab.Representation = TessellatedBody(new[] { body }, storeyElevation);
                    }));
                    continue;
                }

                var count = ++number;
                var flight = New<IfcRampFlight>(f =>
                {
                    f.GlobalId = Guid.NewGuid().ToIfc();
                    f.Name = $"{type.Name} - flight {count}";
                    f.PredefinedType = IfcRampFlightTypeEnum.STRAIGHT;
                    f.ObjectPlacement = OnStorey();
                    f.Representation = TessellatedBody(new[] { body }, storeyElevation);
                });
                Aggregate(product, flight);
                WriteSet(flight, "Pset_RampFlightCommon", new Dictionary<string, IfcValue?>
                {
                    ["ClearWidth"] = new IfcPositiveLengthMeasure(layout.Width),
                    ["Slope"] = new IfcPlaneAngleMeasure(Math.Atan2(piece.Rise, piece.Going))
                });
            }

            if (parts.Kerbs.Any(kerb => !kerb.IsEmpty))
                Aggregate(product, New<IfcMember>(member =>
                {
                    member.GlobalId = Guid.NewGuid().ToIfc();
                    member.Name = $"{type.Name} - kerbs";
                    member.PredefinedType = IfcMemberTypeEnum.USERDEFINED;
                    member.ObjectType = "KERB";
                    member.ObjectPlacement = OnStorey();
                    member.Representation = TessellatedBody(parts.Kerbs.Where(kerb => !kerb.IsEmpty).ToList(), storeyElevation);
                }));

            WriteSet(product, "Pset_RampCommon", new Dictionary<string, IfcValue?>
            {
                ["Reference"] = string.IsNullOrEmpty(type.TypeMark) ? null : new IfcIdentifier(type.TypeMark),
                ["RequiredSlope"] = new IfcPlaneAngleMeasure(Math.Atan(1 / Math.Max(1, ramp.Gradient))),
                ["HandicapAccessible"] = new IfcBoolean(Ramps.Problems(_document, ramp).Count == 0),
                ["IsExternal"] = new IfcBoolean(ramp.Outside)
            });
            WriteSet(product, "Ramp", new Dictionary<string, IfcValue?>
            {
                ["Construction"] = new IfcLabel(EnumText.Humanise(type.Construction)),
                ["Gradient"] = new IfcLabel($"1:{ramp.Gradient:0.#}"),
                ["Rise"] = new IfcLengthMeasure(layout.Rise),
                ["Flights"] = new IfcCountMeasure(flights),
                ["Width"] = new IfcPositiveLengthMeasure(layout.Width),
                ["Building"] = new IfcLabel(EnumText.Humanise(ramp.Building)),
                ["Regulations"] = new IfcText(Ramps.Verdict(_document, ramp))
            });

            // Its handrails and its guarding, as IFC has them.
            foreach (var (meshes, kind, name, height) in new[]
                     {
                         (parts.Handrails, IfcRailingTypeEnum.HANDRAIL, "handrails", type.HandrailHeight),
                         (parts.Guarding, IfcRailingTypeEnum.GUARDRAIL, "guarding", Ramps.GuardHeight(_document, ramp, true).Height)
                     })
            {
                var solid = meshes.Where(mesh => !mesh.IsEmpty).ToList();
                if (solid.Count == 0) continue;
                var railing = New<IfcRailing>(r =>
                {
                    r.GlobalId = Guid.NewGuid().ToIfc();
                    r.Name = $"{type.Name} - {name}";
                    r.PredefinedType = kind;
                    r.ObjectPlacement = OnStorey();
                    r.Representation = TessellatedBody(solid, storeyElevation);
                });
                Aggregate(product, railing);
                WriteSet(railing, "Pset_RailingCommon", new Dictionary<string, IfcValue?>
                {
                    ["Height"] = new IfcPositiveLengthMeasure(height),
                    ["Diameter"] = kind == IfcRailingTypeEnum.HANDRAIL ? new IfcPositiveLengthMeasure(type.HandrailDiameter) : null
                });
            }
        }

        /// <summary>
        /// A railing as an IfcRailing guarding a balcony, a terrace or a floor's edge - IFC's
        /// BALUSTRADE, at the edge of a floor, a balcony or a roof; a Juliet balcony across a door
        /// or window, a GUARDRAIL; a fence, user defined as one - with its height, its handrail's
        /// diameter, whether it is outside, the loads it is made for, and whether it meets its rules.
        /// </summary>
        private void ExportRailing(Railing railing)
        {
            var meshes = RailingModel.Meshes(_document, railing);
            if (meshes.Count == 0) return;

            var type = Railings.TypeOf(_document, railing);
            var storey = StoreyOf(railing);
            var storeyElevation = _document.FindLevel(railing.LevelId)?.Elevation ?? 0;
            var product = New<IfcRailing>(r =>
            {
                r.GlobalId = railing.Id.ToIfc();
                r.Name = type.Name;
                r.Tag = railing.Mark;
                r.PredefinedType = type.Use == RailingUse.Fence ? IfcRailingTypeEnum.USERDEFINED
                    : railing.HostOpeningId is not null ? IfcRailingTypeEnum.GUARDRAIL : IfcRailingTypeEnum.BALUSTRADE;
                if (type.Use == RailingUse.Fence) r.ObjectType = "FENCE";
                r.ObjectPlacement = New<IfcLocalPlacement>(placement =>
                {
                    placement.PlacementRelTo = storey?.ObjectPlacement;
                    placement.RelativePlacement = Placement(Point3D(0, 0, 0));
                });
                r.Representation = TessellatedBody(meshes, storeyElevation);
            });
            Contain(railing, product);

            WriteSet(product, "Pset_RailingCommon", new Dictionary<string, IfcValue?>
            {
                ["Reference"] = string.IsNullOrEmpty(type.TypeMark) ? null : new IfcIdentifier(type.TypeMark),
                ["Height"] = new IfcPositiveLengthMeasure(type.Height),
                ["Diameter"] = type.Handrail != RailingHandrail.None ? new IfcPositiveLengthMeasure(type.HandrailDiameter)
                    : type.TopRailProfile == HandrailProfile.Round && Railings.RailDepth(type) > 0 ? new IfcPositiveLengthMeasure(type.TopRailWidth) : null,
                ["IsExternal"] = new IfcBoolean(railing.Outside)
            });
            WriteSet(product, "Railing", new Dictionary<string, IfcValue?>
            {
                ["Length"] = new IfcLengthMeasure(Railings.Length(railing)),
                ["Infill"] = new IfcLabel(EnumText.Humanise(type.Infill)),
                ["Use"] = new IfcLabel(EnumText.Humanise(type.Use)),
                ["Building"] = new IfcLabel(EnumText.Humanise(railing.Building)),
                ["HeightAboveFloorGuarded"] = new IfcLengthMeasure(Railings.EffectiveHeight(_document, railing)),
                ["LineLoad"] = new IfcReal(type.LineLoad),
                ["PointLoad"] = new IfcReal(type.PointLoad),
                ["LaminatedGlass"] = type.Infill is RailingInfill.Glass or RailingInfill.FramelessGlass ? new IfcBoolean(type.GlassLaminated) : null,
                ["Regulations"] = new IfcText(Railings.Verdict(_document, railing))
            });
        }

        /// <summary>
        /// A joist layout as an IfcElementAssembly - a BEAM_GRID - made up of its joists and
        /// trimmers, each an IfcBeam: a JOIST.
        /// </summary>
        private void ExportBeamSystem(BeamSystem system)
        {
            var members = BeamSystems.Members(_document, system);
            if (members.Count == 0) return;

            var type = BeamSystems.TypeOf(_document, system);
            var storey = StoreyOf(system);
            var storeyElevation = _document.FindLevel(system.LevelId)?.Elevation ?? 0;
            IfcObjectPlacement OnStorey() => New<IfcLocalPlacement>(placement =>
            {
                placement.PlacementRelTo = storey?.ObjectPlacement;
                placement.RelativePlacement = Placement(Point3D(0, 0, 0));
            });

            var assembly = New<Xbim.Ifc4.ProductExtension.IfcElementAssembly>(a =>
            {
                a.GlobalId = system.Id.ToIfc();
                a.Name = $"{type.Name} @ {system.Spacing:0}";
                a.Tag = system.Mark;
                a.PredefinedType = Xbim.Ifc4.Interfaces.IfcElementAssemblyTypeEnum.BEAM_GRID;
                a.ObjectPlacement = OnStorey();
            });
            Contain(system, assembly);

            foreach (var member in members)
            {
                if (Beams.Mesh(_document, member, system.Id) is not { } mesh) continue;
                Aggregate(assembly, New<IfcBeam>(b =>
                {
                    b.GlobalId = Guid.NewGuid().ToIfc();
                    b.Name = type.Name;
                    b.PredefinedType = IfcBeamTypeEnum.JOIST;
                    b.ObjectPlacement = OnStorey();
                    b.Representation = TessellatedBody(new[] { mesh }, storeyElevation);
                }));
            }
        }

        /// <summary>The ground as an IfcGeographicElement - TERRAIN - its surface and sides as its body; each region on it as one of its own.</summary>
        private void ExportGround(Toposurface ground)
        {
            foreach (var regionMesh in Terrains.Meshes(_document, ground).Where(mesh => mesh.ElementId != ground.Id))
            {
                if (_document.Elements.OfType<GroundRegion>().FirstOrDefault(region => region.Id == regionMesh.ElementId) is not { } region) continue;
                var regionStorey = StoreyOf(region);
                var regionElement = New<Xbim.Ifc4.ProductExtension.IfcGeographicElement>(g =>
                {
                    g.GlobalId = region.Id.ToIfc();
                    g.Name = EnumText.Humanise(region.Surface);
                    g.Tag = region.Mark;
                    g.PredefinedType = Xbim.Ifc4.Interfaces.IfcGeographicElementTypeEnum.USERDEFINED;
                    g.ObjectType = region.Surface.ToString().ToUpperInvariant();
                    g.ObjectPlacement = New<IfcLocalPlacement>(placement =>
                    {
                        placement.PlacementRelTo = regionStorey?.ObjectPlacement;
                        placement.RelativePlacement = Placement(Point3D(0, 0, 0));
                    });
                    g.Representation = TessellatedBody(new[] { regionMesh }, _document.FindLevel(region.LevelId)?.Elevation ?? 0);
                });
                Contain(region, regionElement);
            }

            if (Terrains.Mesh(_document, ground) is not { } mesh) return;

            var storey = StoreyOf(ground);
            var storeyElevation = _document.FindLevel(ground.LevelId)?.Elevation ?? 0;
            var product = New<Xbim.Ifc4.ProductExtension.IfcGeographicElement>(g =>
            {
                g.GlobalId = ground.Id.ToIfc();
                g.Name = "Terrain";
                g.Tag = ground.Mark;
                g.PredefinedType = Xbim.Ifc4.Interfaces.IfcGeographicElementTypeEnum.TERRAIN;
                g.ObjectPlacement = New<IfcLocalPlacement>(placement =>
                {
                    placement.PlacementRelTo = storey?.ObjectPlacement;
                    placement.RelativePlacement = Placement(Point3D(0, 0, 0));
                });
                g.Representation = TessellatedBody(new[] { mesh }, storeyElevation);
            });
            Contain(ground, product);
            WriteSet(product, "Terrain", new Dictionary<string, IfcValue?>
            {
                ["Points"] = new IfcInteger(ground.Points.Count),
                ["ProjectedArea"] = new IfcAreaMeasure(Terrains.Area(ground) / 1e6),
                ["GroundAtTheWalls"] = new IfcText(Terrains.Verdict(_document, ground))
            });
        }

        /// <summary>
        /// A beam as an IfcBeam - a BEAM, a JOIST or a LINTEL, a purlin as one of its own - with its
        /// span and slope, load-bearing, and its section, material and weight.
        /// </summary>
        private void ExportBeam(Beam beam)
        {
            if (Beams.Mesh(_document, beam) is not { } mesh) return;

            var type = Beams.TypeOf(_document, beam);
            var storey = StoreyOf(beam);
            var storeyElevation = _document.FindLevel(beam.LevelId)?.Elevation ?? 0;
            var product = New<IfcBeam>(b =>
            {
                b.GlobalId = beam.Id.ToIfc();
                b.Name = type.Name;
                b.Tag = beam.Mark;
                b.PredefinedType = beam.Usage switch
                {
                    BeamUsage.Joist => IfcBeamTypeEnum.JOIST,
                    BeamUsage.Lintel => IfcBeamTypeEnum.LINTEL,
                    BeamUsage.Purlin => IfcBeamTypeEnum.USERDEFINED,
                    _ => IfcBeamTypeEnum.BEAM
                };
                if (beam.Usage == BeamUsage.Purlin) b.ObjectType = "PURLIN";
                b.ObjectPlacement = New<IfcLocalPlacement>(placement =>
                {
                    placement.PlacementRelTo = storey?.ObjectPlacement;
                    placement.RelativePlacement = Placement(Point3D(0, 0, 0));
                });
                b.Representation = TessellatedBody(new[] { mesh }, storeyElevation);
            });
            Contain(beam, product);

            var rise = Beams.Top(_document, beam, 1) - Beams.Top(_document, beam, 0);
            WriteSet(product, "Pset_BeamCommon", new Dictionary<string, IfcValue?>
            {
                ["Reference"] = new IfcIdentifier(type.Name),
                ["Span"] = new IfcPositiveLengthMeasure(Math.Max(1, beam.Length)),
                ["Slope"] = new IfcPlaneAngleMeasure(Math.Round(Math.Atan2(rise, Math.Max(1, beam.Length)) * 180 / Math.PI, 3)),
                ["LoadBearing"] = new IfcBoolean(true),
                ["IsExternal"] = new IfcBoolean(false)
            });
            WriteSet(product, "Beam", new Dictionary<string, IfcValue?>
            {
                ["Section"] = new IfcLabel(EnumText.Humanise(type.Section)),
                ["Material"] = new IfcLabel(EnumText.Humanise(type.Material)),
                ["Depth"] = new IfcPositiveLengthMeasure(type.Depth),
                ["Width"] = new IfcPositiveLengthMeasure(type.Width),
                ["MassPerMetre"] = new IfcReal(Math.Round(type.MassPerMetre, 1)),
                ["Clearances"] = new IfcText(Beams.Verdict(_document, beam))
            });
        }

        /// <summary>
        /// A chimney as an IfcChimney, all of it as one body, with the height its rule asks for,
        /// how many flues it draws, its fire rating and its EN 1443 designation.
        /// </summary>
        private void ExportChimney(Chimney chimney)
        {
            var meshes = Chimneys.Meshes(_document, chimney);
            if (meshes.Count == 0) return;

            var type = Chimneys.TypeOf(_document, chimney);
            var storey = StoreyOf(chimney);
            var storeyElevation = _document.FindLevel(chimney.LevelId)?.Elevation ?? 0;
            var product = New<IfcChimney>(c =>
            {
                c.GlobalId = chimney.Id.ToIfc();
                c.Name = type.Name;
                c.ObjectType = EnumText.Humanise(type.Construction);
                c.Tag = chimney.Mark;
                c.ObjectPlacement = New<IfcLocalPlacement>(placement =>
                {
                    placement.PlacementRelTo = storey?.ObjectPlacement;
                    placement.RelativePlacement = Placement(Point3D(0, 0, 0));
                });
                c.Representation = TessellatedBody(meshes, storeyElevation);
            });
            Contain(chimney, product);

            WriteSet(product, "Pset_ChimneyCommon", new Dictionary<string, IfcValue?>
            {
                ["Reference"] = string.IsNullOrEmpty(type.TypeMark) ? null : new IfcIdentifier(type.TypeMark),
                ["NumberOfDrafts"] = type.HasFlue ? new IfcCountMeasure(chimney.Flues) : null,
                ["FireRating"] = string.IsNullOrEmpty(type.FireRating) ? null : new IfcLabel(type.FireRating),
                ["IsExternal"] = new IfcBoolean(Chimneys.HighestContact(_document, chimney) is not null)
            });
            WriteSet(product, "Chimney", new Dictionary<string, IfcValue?>
            {
                ["Construction"] = new IfcLabel(EnumText.Humanise(type.Construction)),
                ["Flues"] = new IfcInteger(chimney.Flues),
                ["FlueInternalDiameter"] = type.HasFlue ? new IfcPositiveLengthMeasure(type.FlueDiameter) : null,
                ["Designation"] = new IfcLabel(type.Designation),
                ["ClearanceToCombustibles"] = new IfcLengthMeasure(type.ClearanceToCombustibles),
                ["AtItsFoot"] = new IfcLabel(EnumText.Humanise(chimney.Fireplace)),
                ["HeightRule"] = new IfcLabel(chimney.Rule == ChimneyRule.ThreeTwoTen ? "US 3-2-10" : "Approved Document J"),
                ["Height"] = new IfcLengthMeasure(Chimneys.Top(_document, chimney) - Chimneys.Foot(_document, chimney))
            });
        }

        /// <summary>What a component goes out of IFC as.</summary>
        private enum ComponentClass { Furniture, Sanitary, Appliance, Light, Heater, Outlet, Switch, Proxy }

        /// <summary>
        /// What a component is in IFC: a fridge, cooker, dishwasher or washing machine an
        /// appliance and a sink a sanitary fitting, whatever category they were put in; otherwise
        /// as its category says.
        /// </summary>
        private static ComponentClass ClassOf(ComponentType type) => type.Form switch
        {
            ComponentForm.Fridge or ComponentForm.Cooker or ComponentForm.Dishwasher or ComponentForm.WashingMachine => ComponentClass.Appliance,
            ComponentForm.Sink or ComponentForm.Shower => ComponentClass.Sanitary,
            ComponentForm.Radiator => ComponentClass.Heater,
            ComponentForm.Socket => ComponentClass.Outlet,
            ComponentForm.Switch => ComponentClass.Switch,
            _ => type.Kind switch
            {
                BuiltInCategory.Furniture or BuiltInCategory.Casework => ComponentClass.Furniture,
                BuiltInCategory.PlumbingFixtures => ComponentClass.Sanitary,
                BuiltInCategory.LightingFixtures => ComponentClass.Light,
                _ => ComponentClass.Proxy
            }
        };

        /// <summary>
        /// A component as what it is: furniture and kitchen units as IfcFurniture - a chair, a
        /// table, a desk, a bed, a sofa, shelving - sanitary fittings as IfcSanitaryTerminal - a WC
        /// pan, a wash-hand basin, a bath, a sink - kitchen appliances as IfcElectricAppliance - a
        /// fridge or fridge-freezer, a cooker, a dishwasher, a washing machine - and lights as
        /// IfcLightFixture; a tree or anything else as a proxy. Its body as the 3D view builds it,
        /// its size, and the room it is in.
        /// </summary>
        private void ExportComponent(Component component)
        {
            if (_document.FindType<ComponentType>(component.TypeId) is not { } type) return;

            var meshes = ModelMeshBuilder.BuildComponent(_document, component);
            if (meshes.Count == 0) return;

            var exported = ClassOf(type);
            IfcElement product = exported switch
            {
                ComponentClass.Furniture => ComponentProduct<IfcFurniture>(component, type, meshes, furniture =>
                    furniture.PredefinedType = type.Form switch
                    {
                        ComponentForm.Chair or ComponentForm.Armchair => IfcFurnitureTypeEnum.CHAIR,
                        ComponentForm.Table or ComponentForm.CoffeeTable => IfcFurnitureTypeEnum.TABLE,
                        ComponentForm.Desk => IfcFurnitureTypeEnum.DESK,
                        ComponentForm.Bed => IfcFurnitureTypeEnum.BED,
                        ComponentForm.Sofa => IfcFurnitureTypeEnum.SOFA,
                        ComponentForm.Shelving => IfcFurnitureTypeEnum.SHELF,
                        _ => IfcFurnitureTypeEnum.USERDEFINED
                    }),
                ComponentClass.Sanitary => ComponentProduct<IfcSanitaryTerminal>(component, type, meshes, sanitary =>
                    sanitary.PredefinedType = type.Form switch
                    {
                        ComponentForm.Toilet => IfcSanitaryTerminalTypeEnum.TOILETPAN,
                        ComponentForm.Basin => IfcSanitaryTerminalTypeEnum.WASHHANDBASIN,
                        ComponentForm.Bath => IfcSanitaryTerminalTypeEnum.BATH,
                        ComponentForm.Sink => IfcSanitaryTerminalTypeEnum.SINK,
                        ComponentForm.Shower => IfcSanitaryTerminalTypeEnum.SHOWER,
                        _ => IfcSanitaryTerminalTypeEnum.USERDEFINED
                    }),
                ComponentClass.Appliance => ComponentProduct<IfcElectricAppliance>(component, type, meshes, appliance =>
                    appliance.PredefinedType = type.Form switch
                    {
                        ComponentForm.Fridge => type.Height > 1200 ? IfcElectricApplianceTypeEnum.FRIDGE_FREEZER : IfcElectricApplianceTypeEnum.REFRIGERATOR,
                        ComponentForm.Cooker => IfcElectricApplianceTypeEnum.ELECTRICCOOKER,
                        ComponentForm.Dishwasher => IfcElectricApplianceTypeEnum.DISHWASHER,
                        ComponentForm.WashingMachine => IfcElectricApplianceTypeEnum.WASHINGMACHINE,
                        _ => IfcElectricApplianceTypeEnum.USERDEFINED
                    }),
                ComponentClass.Light => ComponentProduct<IfcLightFixture>(component, type, meshes, light =>
                    light.PredefinedType = IfcLightFixtureTypeEnum.POINTSOURCE),
                ComponentClass.Heater => ComponentProduct<IfcSpaceHeater>(component, type, meshes, heater =>
                    heater.PredefinedType = IfcSpaceHeaterTypeEnum.RADIATOR),
                ComponentClass.Outlet => ComponentProduct<IfcOutlet>(component, type, meshes, outlet =>
                    outlet.PredefinedType = IfcOutletTypeEnum.POWEROUTLET),
                ComponentClass.Switch => ComponentProduct<IfcSwitchingDevice>(component, type, meshes, device =>
                    device.PredefinedType = IfcSwitchingDeviceTypeEnum.TOGGLESWITCH),
                _ => ComponentProduct<IfcBuildingElementProxy>(component, type, meshes, _ => { })
            };

            // The type's common set, as the IFC property set for its kind names it.
            var common = exported switch
            {
                ComponentClass.Furniture => "Pset_FurnitureTypeCommon",
                ComponentClass.Sanitary => "Pset_SanitaryTerminalTypeCommon",
                ComponentClass.Appliance => "Pset_ElectricApplianceTypeCommon",
                ComponentClass.Light => "Pset_LightFixtureTypeCommon",
                ComponentClass.Heater => "Pset_SpaceHeaterTypeCommon",
                ComponentClass.Outlet => "Pset_OutletTypeCommon",
                ComponentClass.Switch => "Pset_SwitchingDeviceTypeCommon",
                _ => "Pset_BuildingElementProxyCommon"
            };
            WriteSet((IfcObject)product, common, new Dictionary<string, IfcValue?>
            {
                ["Reference"] = string.IsNullOrEmpty(type.TypeMark) ? null : new IfcIdentifier(type.TypeMark),
                ["NominalLength"] = new IfcPositiveLengthMeasure(type.Width),
                ["NominalDepth"] = new IfcPositiveLengthMeasure(type.Depth),
                ["NominalHeight"] = new IfcPositiveLengthMeasure(type.Height),
                ["IsBuiltIn"] = exported == ComponentClass.Furniture && type.Kind == BuiltInCategory.Casework ? new IfcBoolean(true) : null
            });

            WriteSet((IfcObject)product, "Component", new Dictionary<string, IfcValue?>
            {
                ["Category"] = new IfcLabel(EnumText.Humanise(type.Kind)),
                ["Room"] = RoomsOf.Containing(_document, component.LevelId, component.Location) is { } room ? new IfcLabel(RoomsOf.Label(room)) : null,
                ["Material"] = _document.FindMaterial(type.MaterialId) is { } material ? new IfcLabel(material.Name) : null,
                ["Cost"] = type.Cost > 0 ? new IfcMonetaryMeasure((double)type.Cost) : null
            });
        }

        /// <summary>One component as a product of a kind, placed on its storey with its body - what its kind adds set by the caller.</summary>
        private T ComponentProduct<T>(Component component, ComponentType type, IReadOnlyList<Mesh3D> meshes, Action<T> kind)
            where T : IfcElement, IInstantiableEntity
        {
            var storey = StoreyOf(component);
            var storeyElevation = _document.FindLevel(component.LevelId)?.Elevation ?? 0;
            var product = New<T>(element =>
            {
                element.GlobalId = component.Id.ToIfc();
                element.Name = type.Name;
                element.ObjectType = EnumText.Humanise(type.Form);
                element.Tag = component.Mark;
                element.ObjectPlacement = New<IfcLocalPlacement>(placement =>
                {
                    placement.PlacementRelTo = storey?.ObjectPlacement;
                    placement.RelativePlacement = Placement(Point3D(0, 0, 0));
                });
                element.Representation = TessellatedBody(meshes, storeyElevation);
                kind(element);
            });

            Contain(component, product);
            return product;
        }

        /// <summary>A wall shaped in plan as an IfcWall, its body stood up from its outline, with what it is made of.</summary>
        private void ExportPolygonWall(PolygonWall shaped)
        {
            if (PolygonWalls.Mesh(_document, shaped) is not { } mesh) return;

            var storey = StoreyOf(shaped);
            var storeyElevation = _document.FindLevel(shaped.LevelId)?.Elevation ?? 0;
            var product = New<IfcWall>(w =>
            {
                w.GlobalId = shaped.Id.ToIfc();
                w.Name = shaped.Kind == PolygonWallKind.Trapezoid ? "Trapezoid Wall" : "Polygon Wall";
                w.Tag = shaped.Mark;
                w.PredefinedType = IfcWallTypeEnum.SOLIDWALL;
                w.ObjectPlacement = New<IfcLocalPlacement>(placement =>
                {
                    placement.PlacementRelTo = storey?.ObjectPlacement;
                    placement.RelativePlacement = Placement(Point3D(0, 0, 0));
                });
                w.Representation = TessellatedBody(new[] { mesh }, storeyElevation);
            });
            Contain(shaped, product);

            WriteSet(product, "Pset_WallCommon", new Dictionary<string, IfcValue?>
            {
                ["Reference"] = new IfcIdentifier(shaped.Kind.ToString())
            });
            WriteSet(product, "Wall", new Dictionary<string, IfcValue?>
            {
                ["Material"] = _document.FindMaterial(shaped.MaterialId) is { } material ? new IfcLabel(material.Name) : null,
                ["ThicknessAtStart"] = shaped.Kind == PolygonWallKind.Trapezoid ? new IfcPositiveLengthMeasure(shaped.StartThickness) : null,
                ["ThicknessAtEnd"] = shaped.Kind == PolygonWallKind.Trapezoid ? new IfcPositiveLengthMeasure(shaped.EndThickness) : null,
                ["Volume"] = new IfcVolumeMeasure(PolygonWalls.Volume(_document, shaped))
            });
        }

        /// <summary>Meshes as one triangulated body, their heights taken from the storey.</summary>
        /// <summary>A line through points, as an element's axis: its 'Axis' representation, a 3D curve.</summary>
        private IfcProductDefinitionShape AxisShape(IEnumerable<BIMDesigner.Core.Geometry.Point3D> points, double storeyElevation)
        {
            var line = New<IfcPolyline>(polyline =>
            {
                foreach (var point in points) polyline.Points.Add(Point3D(point.X, point.Y, point.Z - storeyElevation));
            });
            var representation = New<IfcShapeRepresentation>(r =>
            {
                r.ContextOfItems = _context;
                r.RepresentationIdentifier = "Axis";
                r.RepresentationType = "Curve3D";
                r.Items.Add(line);
            });
            return New<IfcProductDefinitionShape>(shape => shape.Representations.Add(representation));
        }

        private IfcProductDefinitionShape TessellatedBody(IReadOnlyList<Mesh3D> meshes, double storeyElevation)
        {
            var representation = New<IfcShapeRepresentation>(r =>
            {
                r.ContextOfItems = _context;
                r.RepresentationIdentifier = "Body";
                r.RepresentationType = "Tessellation";
                foreach (var mesh in meshes.Where(mesh => !mesh.IsEmpty))
                {
                    var points = New<IfcCartesianPointList3D>(list =>
                    {
                        foreach (var point in mesh.Positions)
                            list.CoordList.GetAt(list.CoordList.Count).AddRange(new IfcLengthMeasure[] { point.X, point.Y, point.Z - storeyElevation });
                    });
                    r.Items.Add(New<IfcTriangulatedFaceSet>(faces =>
                    {
                        faces.Coordinates = points;
                        faces.Closed = true;
                        for (var i = 0; i + 2 < mesh.Indices.Count; i += 3)
                            faces.CoordIndex.GetAt(faces.CoordIndex.Count).AddRange(new IfcPositiveInteger[]
                            {
                                mesh.Indices[i] + 1, mesh.Indices[i + 1] + 1, mesh.Indices[i + 2] + 1
                            });
                    }));
                }
            });

            return New<IfcProductDefinitionShape>(shape => shape.Representations.Add(representation));
        }

        /// <summary>A roof drain: its own element, with its outlet size and the roof it takes the water from.</summary>
        private void ExportRoofDrain(RoofDrain drain)
        {
            if (RoofDrainage.Mesh(_document, drain) is not { } mesh) return;

            var proxy = ExportMeshProxy(drain, mesh, $"Roof Drain {drain.OutletDiameter:0}", "Roof Drain");
            var roof = _document.Elements.OfType<Roof>().FirstOrDefault(candidate => candidate.Id == drain.RoofId);
            WriteSet(proxy, "RoofDrain", new Dictionary<string, IfcValue?>
            {
                ["OutletDiameter"] = new IfcLengthMeasure(drain.OutletDiameter),
                ["CatchmentArea"] = roof is null ? null : new IfcAreaMeasure(RoofDrainage.CatchmentArea(_document, roof, drain) / 1e6)
            });
        }

        /// <summary>An element as a building element proxy, on its storey, its body a triangulated face set.</summary>
        private IfcBuildingElementProxy ExportMeshProxy(Element element, Mesh3D mesh, string name, string kind)
        {
            var storey = StoreyOf(element);
            var storeyElevation = _document.FindLevel(element.LevelId)?.Elevation ?? 0;

            var proxy = New<IfcBuildingElementProxy>(p =>
            {
                p.GlobalId = element.Id.ToIfc();
                p.Name = name;
                p.ObjectType = kind;
                p.Tag = element.Mark;
                p.ObjectPlacement = New<IfcLocalPlacement>(placement =>
                {
                    placement.PlacementRelTo = storey?.ObjectPlacement;
                    placement.RelativePlacement = Placement(Point3D(0, 0, 0));
                });

                var representation = New<IfcShapeRepresentation>(r =>
                {
                    r.ContextOfItems = _context;
                    r.RepresentationIdentifier = "Body";
                    r.RepresentationType = "Tessellation";

                    var points = New<IfcCartesianPointList3D>(list =>
                    {
                        foreach (var point in mesh.Positions)
                            list.CoordList.GetAt(list.CoordList.Count).AddRange(new IfcLengthMeasure[] { point.X, point.Y, point.Z - storeyElevation });
                    });

                    r.Items.Add(New<IfcTriangulatedFaceSet>(faces =>
                    {
                        faces.Coordinates = points;
                        faces.Closed = true;
                        for (var i = 0; i + 2 < mesh.Indices.Count; i += 3)
                            faces.CoordIndex.GetAt(faces.CoordIndex.Count).AddRange(new IfcPositiveInteger[]
                            {
                                mesh.Indices[i] + 1, mesh.Indices[i + 1] + 1, mesh.Indices[i + 2] + 1
                            });
                    }));
                });

                p.Representation = New<IfcProductDefinitionShape>(shape => shape.Representations.Add(representation));
            });

            Contain(element, proxy);
            return proxy;
        }

        /// <summary>
        /// A leaning wall's body. A slanted or tapered wall is not an upright extrusion of one
        /// outline, so it is written as the triangles of its solids - the same ones the 3D view
        /// draws - in the wall's own coordinates.
        /// </summary>
        private IfcProductDefinitionShape Tessellated(CoreWall wall, Point2D bodyStart, params MeshKind[] kinds)
        {
            var baseElevation = wall.GetBaseElevation(_document);
            var direction = wall.Direction;
            var across = direction.PerpendicularLeft();

            var representation = New<IfcShapeRepresentation>(r =>
            {
                r.ContextOfItems = _context;
                r.RepresentationIdentifier = "Body";
                r.RepresentationType = "Tessellation";

                foreach (var mesh in ModelMeshBuilder.BuildWall(_document, wall).Where(m => kinds.Length == 0 ? m.Kind == MeshKind.Wall : kinds.Contains(m.Kind)))
                {
                    var points = New<IfcCartesianPointList3D>(list =>
                    {
                        foreach (var p in mesh.Positions)
                        {
                            var offset = new Point2D(p.X, p.Y) - bodyStart;
                            list.CoordList.GetAt(list.CoordList.Count).AddRange(new IfcLengthMeasure[]
                            {
                                offset.Dot(direction), offset.Dot(across), p.Z - baseElevation
                            });
                        }
                    });

                    r.Items.Add(New<IfcTriangulatedFaceSet>(faces =>
                    {
                        faces.Coordinates = points;
                        faces.Closed = true;

                        // IFC counts points from one.
                        for (var i = 0; i + 2 < mesh.Indices.Count; i += 3)
                        {
                            faces.CoordIndex.GetAt(faces.CoordIndex.Count).AddRange(new IfcPositiveInteger[]
                            {
                                mesh.Indices[i] + 1, mesh.Indices[i + 1] + 1, mesh.Indices[i + 2] + 1
                            });
                        }
                    }));
                }
            });

            return New<IfcProductDefinitionShape>(shape => shape.Representations.Add(representation));
        }

        /// <summary>
        /// A wall's body: one extrusion for an ordinary wall, one per tier for a stacked wall,
        /// each its own construction's outline between its own heights. Still one wall to
        /// whoever receives it - which is what it is.
        /// </summary>
        private IfcProductDefinitionShape ExtrudeTiers(CoreWall wall, Point2D bodyStart, IReadOnlyList<Point2D> planOutline, double height)
        {
            var tiers = _document.GetWallTiers(wall);
            if (tiers.Count <= 1) return Extrude(ToLocal(planOutline, bodyStart, wall.Direction), height);

            var baseElevation = wall.GetBaseElevation(_document);

            var representation = New<IfcShapeRepresentation>(r =>
            {
                r.ContextOfItems = _context;
                r.RepresentationIdentifier = "Body";
                r.RepresentationType = "SweptSolid";

                foreach (var (type, bottom, top) in tiers)
                {
                    var outline = WallJoins.GetBandOutline(_document, wall, type, type.Width / 2, -type.Width / 2);

                    r.Items.Add(New<IfcExtrudedAreaSolid>(s =>
                    {
                        s.SweptArea = Profile(ToLocal(outline, bodyStart, wall.Direction));
                        s.Position = Placement(Point3D(0, 0, bottom - baseElevation));
                        s.ExtrudedDirection = Direction(0, 0, 1);
                        s.Depth = top - bottom;
                    }));
                }
            });

            return New<IfcProductDefinitionShape>(shape => shape.Representations.Add(representation));
        }

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
