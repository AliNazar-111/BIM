using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;
using BIMDesigner.Core.Parameters;
using Xbim.Common;
using Xbim.Ifc;
using Xbim.Ifc4.Interfaces;

using CoreMaterial = BIMDesigner.Core.Materials.Material;
using CoreWall = BIMDesigner.Core.Architecture.Wall;
using CoreWindow = BIMDesigner.Core.Architecture.Window;

namespace BIMDesigner.Infrastructure.Interoperability;

/// <summary>What an IFC file came in as, and what of it did not come in as itself.</summary>
public sealed class IfcImportResult
{
    public required BimDocument Document { get; init; }

    /// <summary>"IFC4", "IFC2X3".</summary>
    public required string Schema { get; init; }

    /// <summary>How many of each thing it made, by what it is: "Walls" 12, "Doors" 4.</summary>
    public Dictionary<string, int> Counts { get; } = new();

    /// <summary>What was moved, left out, or brought in only as its shape, and why.</summary>
    public List<string> Notes { get; } = new();

    public int this[string kind] => Counts.GetValueOrDefault(kind);

    internal void Count(string kind) => Counts[kind] = Counts.GetValueOrDefault(kind) + 1;

    /// <summary>"12 walls, 4 doors, 3 storeys ..." - for the status bar.</summary>
    public string Summary() => string.Join(", ", Counts.Where(entry => entry.Value > 0).Select(entry => $"{entry.Value} {entry.Key.ToLowerInvariant()}"));
}

/// <summary>
/// Reads an IFC file - IFC2x3 or IFC4 - into a new project (specification section 8).
///
/// Its storeys become levels. Walls standing upright on a line become walls - their axis, height
/// and the layers of their material layer set, a type made of those layers; doors and windows in
/// them become doors and windows at their place in the wall, at their size. Floors, flat roofs and
/// ceilings laid flat as one extruded outline become floors, roofs and ceilings with their
/// outline and thickness; spaces become rooms and grids gridlines. Anything else with a shape -
/// stairs, railings, furniture, a pitched roof, a proxy - comes in as a component carrying that
/// shape, one type to each kind of thing alike, so it is seen, placed and scheduled, if not
/// edited as what it was. What could not be read at all is listed, so nothing goes missing quietly.
/// </summary>
public static class IfcImport
{
    public static IfcImportResult Open(string path)
    {
        using var model = IfcStore.Open(path);
        return Read(model, Path.GetFileNameWithoutExtension(path));
    }

    public static IfcImportResult Read(IModel model, string name = "Imported") => new Importer(model, name).Run();

    /// <summary>A model further than this from the origin is brought to it: ten kilometres.</summary>
    private const double FarAway = 10_000_000;

    private sealed class Importer
    {
        private readonly IModel _model;
        private readonly IfcGeometry _geometry;
        private readonly BimDocument _document = BimDocument.CreateDefault();
        private readonly IfcImportResult _result;
        private readonly Dictionary<int, Level> _levels = new();
        private readonly Dictionary<int, CoreWall> _walls = new();
        private readonly Dictionary<int, Element> _made = new();
        private readonly HashSet<int> _handled = new();
        private readonly Dictionary<string, ElementType> _types = new();
        private readonly Dictionary<string, Guid> _materials = new(StringComparer.OrdinalIgnoreCase);

        // Each room's outline as the file gives it, for the lines to bound it by where no wall does.
        private readonly Dictionary<Room, List<Point2D>> _spaces = new();
        private Vector2D _shift;

        private static readonly Point2D Origin = new(0, 0);

        public Importer(IModel model, string name)
        {
            _model = model;
            var (length, angle) = Units(model);
            _geometry = new IfcGeometry(length, angle);
            _result = new IfcImportResult { Document = _document, Schema = model.SchemaVersion.ToString().ToUpperInvariant() };

            var project = model.Instances.OfType<IIfcProject>().FirstOrDefault();
            _document.ProjectInformation.Name = !string.IsNullOrWhiteSpace(project?.Name) ? project!.Name! : name;
            if (!string.IsNullOrWhiteSpace(project?.LongName)) _document.ProjectInformation.Number = project!.LongName!;
            foreach (var material in _document.Materials) _materials[material.Name] = material.Id;
        }

        /// <summary>
        /// Millimetres to the file's unit of length, and radians to its unit of angle - read from
        /// the project's units: a metre with its prefix, or a foot or an inch by its conversion.
        /// </summary>
        private static (double Length, double Angle) Units(IModel model)
        {
            var units = model.Instances.OfType<IIfcProject>().FirstOrDefault()?.UnitsInContext?.Units ?? Enumerable.Empty<IIfcUnit>();
            double? Factor(IfcUnitEnum kind)
            {
                foreach (var unit in units)
                    switch (unit)
                    {
                        case IIfcSIUnit si when si.UnitType == kind:
                            return si.Prefix switch
                            {
                                IfcSIPrefix.MILLI => 1e-3,
                                IfcSIPrefix.CENTI => 1e-2,
                                IfcSIPrefix.DECI => 1e-1,
                                IfcSIPrefix.KILO => 1e3,
                                _ => 1
                            };
                        case IIfcConversionBasedUnit converted when converted.UnitType == kind:
                        {
                            var value = Convert.ToDouble(converted.ConversionFactor.ValueComponent.Value);
                            var of = converted.ConversionFactor.UnitComponent is IIfcSIUnit inner
                                ? inner.Prefix switch { IfcSIPrefix.MILLI => 1e-3, IfcSIPrefix.CENTI => 1e-2, _ => 1 }
                                : 1;
                            return value * of;
                        }
                    }

                return null;
            }

            var metres = Factor(IfcUnitEnum.LENGTHUNIT) ?? model.ModelFactors.LengthToMetresConversionFactor;
            var radians = Factor(IfcUnitEnum.PLANEANGLEUNIT) ?? model.ModelFactors.AngleToRadiansConversionFactor;
            return (metres * 1000, radians);
        }

        public IfcImportResult Run()
        {
            ReadStoreys();
            FindShift();

            foreach (var wall in _model.Instances.OfType<IIfcWall>()) Try(wall, ImportWall);
            foreach (var opening in _model.Instances.OfType<IIfcDoor>()) Try(opening, ImportOpening);
            foreach (var opening in _model.Instances.OfType<IIfcWindow>()) Try(opening, ImportOpening);
            foreach (var slab in _model.Instances.OfType<IIfcSlab>()) Try(slab, ImportSlab);
            foreach (var roof in _model.Instances.OfType<IIfcRoof>()) Try(roof, ImportRoof);
            foreach (var covering in _model.Instances.OfType<IIfcCovering>()) Try(covering, ImportCovering);
            foreach (var space in _model.Instances.OfType<IIfcSpace>()) Try(space, ImportSpace);
            foreach (var grid in _model.Instances.OfType<IIfcGrid>()) Try(grid, ImportGrid);

            // Walls drawn up to the face of the walls they meet, as some programs draw them, carried on to their middle.
            JoinEnds();

            // And rooms open to each other - a kitchen to the living room, a corridor to an open office - parted where the file parts them.
            SeparateRooms();

            // Everything else with a shape, as a component carrying it.
            foreach (var product in _model.Instances.OfType<IIfcProduct>())
            {
                if (_handled.Contains(product.EntityLabel) || product is IIfcSpatialElement or IIfcFeatureElementSubtraction or IIfcGrid or IIfcAnnotation or IIfcVirtualElement or IIfcPort)
                    continue;
                ImportShape(product);
            }

            ReadClassifications();
            return _result;
        }

        /// <summary>
        /// Each classification reference, onto what it classifies: a room's own code; for anything
        /// else, its type's - the first given - and the code into the project's table where it is not.
        /// </summary>
        private void ReadClassifications()
        {
            foreach (var relation in _model.Instances.OfType<IIfcRelAssociatesClassification>())
            {
                if (relation.RelatingClassification is not IIfcClassificationReference reference) continue;
                var code = reference.Identification?.ToString();
                if (string.IsNullOrWhiteSpace(code)) continue;
                var title = reference.Name?.ToString() ?? string.Empty;
                if (_document.Classification.Find(code) is null && title.Length > 0)
                    _document.Classification.Entries.Add(new ClassificationEntry(code.Trim(), title));

                var related = relation.RelatedObjects.OfType<IIfcObjectDefinition>().SelectMany(item => item is IIfcTypeObject type
                    ? type.Types.SelectMany(typed => typed.RelatedObjects).Cast<IIfcObjectDefinition>()
                    : new[] { item });
                foreach (var item in related)
                {
                    if (!_made.TryGetValue(item.EntityLabel, out var element)) continue;
                    if (element is Room room)
                    {
                        if (room.ClassificationCode.Length == 0) room.ClassificationCode = code.Trim();
                    }
                    else if (_document.ElementTypes.FirstOrDefault(type => type.Id == element.TypeId) is { ClassificationCode.Length: 0 } type)
                        type.ClassificationCode = code.Trim();
                }
            }
        }

        /// <summary>One element read as itself, or - where that fails - left for the shapes.</summary>
        private void Try<T>(T product, Func<T, bool> import) where T : IIfcProduct
        {
            bool done;
            try { done = import(product); }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                _result.Notes.Add($"{Describe(product)}: {exception.Message}");
                done = false;
            }

            if (done) _handled.Add(product.EntityLabel);
        }

        private static string Describe(IIfcRoot root) => $"{root.ExpressType.Name} {(string.IsNullOrWhiteSpace(root.Name) ? "#" + root.EntityLabel : root.Name)}";

        // ---- storeys and placement -----------------------------------------------------

        private void ReadStoreys()
        {
            var storeys = _model.Instances.OfType<IIfcBuildingStorey>()
                .Select(storey => (Storey: storey, Elevation: storey.ObjectPlacement is not null
                    ? _geometry.Placement(storey.ObjectPlacement).Origin.Z
                    : _geometry.Length(storey.Elevation ?? 0)))
                .OrderBy(entry => entry.Elevation)
                .ToList();

            // The project's own levels give way to the file's storeys.
            foreach (var level in _document.Levels.ToList()) _document.RemoveLevel(level);

            foreach (var (storey, elevation) in storeys)
            {
                var level = new Level { Name = string.IsNullOrWhiteSpace(storey.Name) ? $"Level {_document.Levels.Count + 1}" : storey.Name!, Elevation = Math.Round(elevation, 3) };
                _document.AddLevel(level);
                _levels[storey.EntityLabel] = level;
                _result.Count("Storeys");
            }

            if (_document.Levels.Count == 0)
            {
                _document.AddLevel(new Level { Name = "Level 0", Elevation = 0 });
                _result.Notes.Add("The file has no storeys: everything is on one level at 0.");
            }
        }

        /// <summary>A model set out kilometres from its origin - on a national grid - brought to it, so it can be drawn.</summary>
        private void FindShift()
        {
            var origins = _model.Instances.OfType<IIfcProduct>()
                .Where(product => product is not IIfcSpatialStructureElement && product.ObjectPlacement is not null)
                .Select(product => _geometry.Placement(product.ObjectPlacement).Origin)
                .ToList();
            if (origins.Count == 0) return;

            // Where most of it is: the middle of its elements, an odd one at the origin or the
            // far side of the site not counting.
            var (x, y) = (Median(origins.Select(p => p.X)), Median(origins.Select(p => p.Y)));
            if (Math.Max(Math.Abs(x), Math.Abs(y)) < FarAway) return;

            _shift = new Vector2D(-Math.Round(x / 1e6) * 1e6, -Math.Round(y / 1e6) * 1e6);
            _result.Notes.Add($"It is set out {Math.Abs(x) / 1e6:0.#} km and {Math.Abs(y) / 1e6:0.#} km from its origin: brought to the origin, moved by {-_shift.X / 1000:0} m and {-_shift.Y / 1000:0} m.");
        }

        private static double Median(IEnumerable<double> values)
        {
            var sorted = values.OrderBy(value => value).ToList();
            return sorted[sorted.Count / 2];
        }

        private Point2D Plan(Point3D world) => new(world.X + _shift.X, world.Y + _shift.Y);

        /// <summary>The storey a product is on: where it is contained, what it is part of, or the highest storey under it.</summary>
        private Level LevelOf(IIfcProduct product, double z)
        {
            for (IIfcObjectDefinition? current = product; current is not null;)
            {
                if (current is IIfcBuildingStorey storey && _levels.TryGetValue(storey.EntityLabel, out var level)) return level;
                if (current is IIfcElement element && element.ContainedInStructure.FirstOrDefault()?.RelatingStructure is { } structure)
                {
                    current = structure;
                    if (structure is IIfcBuildingStorey) continue;
                    break;
                }

                current = current.Decomposes.FirstOrDefault()?.RelatingObject;
            }

            return _document.Levels.Where(level => level.Elevation <= z + 1).OrderByDescending(level => level.Elevation).FirstOrDefault()
                   ?? _document.Levels.OrderBy(level => level.Elevation).First();
        }

        // ---- representations and properties ---------------------------------------------

        private static IEnumerable<IIfcRepresentationItem> Items(IIfcProduct product, string identifier) =>
            product.Representation?.Representations
                .Where(representation => string.Equals(representation.RepresentationIdentifier, identifier, StringComparison.OrdinalIgnoreCase))
                .SelectMany(representation => representation.Items) ?? Enumerable.Empty<IIfcRepresentationItem>();

        /// <summary>A product's body: the representation called Body, or whatever solid one it has.</summary>
        private static List<IIfcRepresentationItem> Body(IIfcProduct product)
        {
            var body = Items(product, "Body").ToList();
            if (body.Count > 0) return body;
            return product.Representation?.Representations
                .Where(representation => representation.RepresentationIdentifier?.ToString() is not ("Axis" or "FootPrint" or "Box" or "Annotation" or "Profile" or "Reference"))
                .SelectMany(representation => representation.Items)
                .ToList() ?? new List<IIfcRepresentationItem>();
        }

        private static object? Property(IIfcObject item, string set, string name)
        {
            IEnumerable<IIfcPropertySetDefinition> sets = item.IsDefinedBy.Select(relation => relation.RelatingPropertyDefinition).OfType<IIfcPropertySetDefinition>();
            if (item.IsTypedBy.FirstOrDefault()?.RelatingType is { } type) sets = sets.Concat(type.HasPropertySets ?? Enumerable.Empty<IIfcPropertySetDefinition>());
            return sets.OfType<IIfcPropertySet>()
                .Where(candidate => string.Equals(candidate.Name, set, StringComparison.OrdinalIgnoreCase))
                .SelectMany(candidate => candidate.HasProperties.OfType<IIfcPropertySingleValue>())
                .FirstOrDefault(property => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))?.NominalValue?.Value;
        }

        private static string TypeName(IIfcObject item) => item.IsTypedBy.FirstOrDefault()?.RelatingType?.Name?.ToString() ?? string.Empty;

        /// <summary>The material layers an element - or its type - is built of, in order, and how they sit on its axis.</summary>
        private static (IIfcMaterialLayerSet? Set, IIfcMaterialLayerSetUsage? Usage) Layers(IIfcObject item)
        {
            var materials = item.HasAssociations.OfType<IIfcRelAssociatesMaterial>().Select(relation => relation.RelatingMaterial).ToList();
            if (item.IsTypedBy.FirstOrDefault()?.RelatingType is { } type)
                materials.AddRange(type.HasAssociations.OfType<IIfcRelAssociatesMaterial>().Select(relation => relation.RelatingMaterial));
            foreach (var material in materials)
            {
                if (material is IIfcMaterialLayerSetUsage usage) return (usage.ForLayerSet, usage);
                if (material is IIfcMaterialLayerSet set) return (set, null);
            }

            return (null, null);
        }

        private Guid MaterialNamed(string? name)
        {
            name = string.IsNullOrWhiteSpace(name) ? "Imported" : name.Trim();
            if (_materials.TryGetValue(name, out var id)) return id;
            var material = new CoreMaterial(name);
            _document.AddMaterial(material);
            _materials[name] = material.Id;
            return material.Id;
        }

        /// <summary>A layer set as a build-up: each layer its material, thickness and - read from its name - what it does.</summary>
        private CompoundStructure? Structure(IIfcMaterialLayerSet? set)
        {
            if (set is null || set.MaterialLayers.Count == 0) return null;
            var structure = new CompoundStructure();
            foreach (var layer in set.MaterialLayers)
            {
                var thickness = _geometry.Length(layer.LayerThickness);
                var function = LayerFunctions.TryParse(layer.Name, out var named) ? named
                    : layer.Category?.ToString() switch
                    {
                        "Insulation" => LayerFunction.ThermalAir,
                        "InnerFinish" or "OuterFinish" or "Finish" => LayerFunction.Finish1,
                        _ => Equals(layer.IsVentilated?.Value, true) ? LayerFunction.ThermalAir : LayerFunction.Structure
                    };
                if (thickness <= 0 && function != LayerFunction.Membrane) continue;
                structure.Add(new MaterialLayer(function, MaterialNamed(layer.Material?.Name), thickness));
            }

            return structure.Layers.Count == 0 ? null : structure;
        }

        // ---- walls --------------------------------------------------------------------

        private bool ImportWall(IIfcWall wall)
        {
            var frame = _geometry.Placement(wall.ObjectPlacement);
            if (!frame.IsUpright) return false;

            // Its body: extrusions straight up, one or a tier each - clipped, perhaps, to a roof over it.
            var clippings = new List<IIfcHalfSpaceSolid>();
            var extrusions = new List<IIfcExtrudedAreaSolid>();
            foreach (var item in Body(wall))
            {
                if (IfcGeometry.Unclipped(item) is not { Start: IIfcExtrudedAreaSolid extrusion } unclipped) return SolidWall(wall, frame);
                extrusions.Add(extrusion);
                clippings.AddRange(unclipped.Clippings);
            }

            if (extrusions.Count == 0) return SolidWall(wall, frame);
            var bottom = double.MaxValue;
            var top = double.MinValue;
            var outline = new List<Point2D>();
            foreach (var extrusion in extrusions)
            {
                var position = frame.Then(_geometry.Axis(extrusion.Position));
                var up = position.Turn(IfcGeometry.Direction(extrusion.ExtrudedDirection));
                if (Math.Abs(Math.Abs(up.Z) - 1) > 1e-6) return SolidWall(wall, frame);
                var depth = _geometry.Length(extrusion.Depth) * Math.Sign(up.Z);
                var (z0, z1) = (position.Origin.Z, position.Origin.Z + depth);
                (bottom, top) = (Math.Min(bottom, Math.Min(z0, z1)), Math.Max(top, Math.Max(z0, z1)));

                // The outline in the wall's own frame: along it, and across it.
                var local = frame.Then(_geometry.Axis(extrusion.Position));
                foreach (var point in _geometry.Profile(extrusion.SweptArea))
                {
                    var world = local.Apply(new Point3D(point.X, point.Y, 0));
                    var d = new Point3D(world.X - frame.Origin.X, world.Y - frame.Origin.Y, 0);
                    outline.Add(new Point2D(Frame.Dot(d, frame.X), Frame.Dot(d, frame.Y)));
                }
            }

            if (outline.Count < 3 || top - bottom < 1) return false;

            // Its axis, as drawn; or down the middle of its body along its longest side.
            var axis = Items(wall, "Axis").Select(item => _geometry.Curve(item as IIfcCurve)).FirstOrDefault(points => points.Count >= 2);
            Point2D start, end;
            var bulge = 0.0;
            if (axis is not null)
            {
                (start, end) = (axis[0], axis[^1]);
                if (axis.Count >= 3)
                {
                    var middle = axis[axis.Count / 2];
                    var chord = end - start;
                    if (chord.Length < 1) return false;
                    var left = chord.NormalisedOrDefault(Vector2D.UnitX).PerpendicularLeft().Dot(middle - start);
                    if (Math.Abs(left) > 5)
                    {
                        // Sagitta to bulge: the tangent of a quarter of the arc's turn, positive turning left.
                        bulge = -2 * left / chord.Length;
                        if (Math.Abs(bulge) >= 1) return false;
                    }
                }
            }
            else
            {
                var longest = Enumerable.Range(0, outline.Count).Select(i => (outline[i], outline[(i + 1) % outline.Count])).OrderByDescending(e => e.Item1.DistanceTo(e.Item2)).First();
                var along = (longest.Item2 - longest.Item1).NormalisedOrDefault(Vector2D.UnitX);
                var across = along.PerpendicularLeft();
                var (a0, a1) = (outline.Min(p => along.Dot(p - Origin)), outline.Max(p => along.Dot(p - Origin)));
                var (c0, c1) = (outline.Min(p => across.Dot(p - Origin)), outline.Max(p => across.Dot(p - Origin)));
                var middle = (c0 + c1) / 2;
                start = Origin + along * a0 + across * middle;
                end = Origin + along * a1 + across * middle;
            }

            // Its build-up, and where on the axis it sits.
            var (set, usage) = Layers(wall);
            var structure = Structure(set);
            var direction = (end - start).NormalisedOrDefault(Vector2D.UnitX);
            var thickness = structure?.TotalWidth ?? outline.Max(p => direction.PerpendicularLeft().Dot(p - Origin)) - outline.Min(p => direction.PerpendicularLeft().Dot(p - Origin));
            if (thickness < 1) return false;
            structure ??= CompoundStructure.Single(MaterialNamed("Imported"), thickness);

            var flipped = usage is not null && usage.DirectionSense == IfcDirectionSenseEnum.NEGATIVE;
            if (usage is not null && usage.LayerSetDirection == IfcLayerSetDirectionEnum.AXIS2)
            {
                // The layers' middle off the axis: where they start - the offset, measured the positive way -
                // and on from there half their thickness, the way they run.
                var shift = _geometry.Length(usage.OffsetFromReferenceLine) + structure.TotalWidth / 2 * (flipped ? -1 : 1);
                var across = direction.PerpendicularLeft() * shift;
                (start, end) = (start + across, end + across);
            }

            // Clipped to the roof over it: its elevation the shape the clippings leave.
            var profile = bulge == 0 ? Clipped(clippings, start, end, bottom - frame.Origin.Z, top - bottom) : null;
            return Place(wall, frame, start, end, bulge, bottom, top, structure, flipped, profile);
        }

        /// <summary>
        /// A wall whose body is a solid - faces, rather than an outline stood up - read as a wall where
        /// it is one: upright, its plan a long thin rectangle, its axis down the middle of it. Its
        /// openings are cut in it already and are put back as its doors and windows; a top that is not
        /// level - a gable, a wall under a sloping roof - comes in as its outline.
        /// </summary>
        private bool SolidWall(IIfcWall wall, Frame frame)
        {
            var mesh = new Mesh3D(Guid.Empty, Guid.Empty, MeshKind.Wall, default, string.Empty);
            foreach (var item in Body(wall)) _geometry.Triangles(item, Frame.Identity, mesh);
            if (mesh.TriangleCount < 4) return false;

            var points = mesh.Positions.Select(p => new Point2D(p.X, p.Y)).ToList();
            var hull = ConvexHull(points);
            if (hull.Count < 3) return false;

            // Along the axis drawn, or the way it is narrowest across.
            var axis = Items(wall, "Axis").Select(item => _geometry.Curve(item as IIfcCurve)).FirstOrDefault(curve => curve.Count == 2);
            var along = axis is not null
                ? (axis[1] - axis[0]).NormalisedOrDefault(Vector2D.UnitX)
                : Enumerable.Range(0, hull.Count).Select(i => (hull[(i + 1) % hull.Count] - hull[i]).NormalisedOrDefault(Vector2D.UnitX))
                    .OrderBy(edge => Width(hull, edge.PerpendicularLeft())).First();
            var across = along.PerpendicularLeft();
            var (a0, a1) = (hull.Min(p => along.Dot(p - Origin)), hull.Max(p => along.Dot(p - Origin)));
            var (c0, c1) = (hull.Min(p => across.Dot(p - Origin)), hull.Max(p => across.Dot(p - Origin)));
            var (length, thickness) = (a1 - a0, c1 - c0);

            // A wall: thinner than it is long, and filling the rectangle round it.
            if (thickness < 1 || length < thickness || Polygon2D.Area(hull) < 0.8 * length * thickness) return false;

            var (low, high) = (mesh.Positions.Min(p => p.Z), mesh.Positions.Max(p => p.Z));
            if (high - low < 1) return false;
            var start = Origin + along * a0 + across * ((c0 + c1) / 2);
            var end = Origin + along * a1 + across * ((c0 + c1) / 2);

            var structure = Structure(Layers(wall).Set);
            if (structure is null || Math.Abs(structure.TotalWidth - thickness) > 1) structure = CompoundStructure.Single(structure?.Layers.FirstOrDefault()?.MaterialId ?? MaterialNamed("Imported"), thickness);

            // Its top as the solid's top along it, where that is not level.
            var elevation = mesh.Positions.Select(p => new Point2D(along.Dot(new Point2D(p.X, p.Y) - Origin) - a0, p.Z - low)).ToList();
            var profile = UpperHull(elevation, length, high - low);
            return Place(wall, frame, start, end, 0, frame.Origin.Z + low, frame.Origin.Z + high, structure, false, profile);
        }

        /// <summary>A wall made: its line and height in the model, its type, its storey - and its outline, where it has one.</summary>
        private bool Place(IIfcWall wall, Frame frame, Point2D start, Point2D end, double bulge, double bottom, double top,
            CompoundStructure structure, bool flipped, IReadOnlyList<Point2D>? profile)
        {
            var worldStart = Plan(frame.Apply(new Point3D(start.X, start.Y, 0)));
            var worldEnd = Plan(frame.Apply(new Point3D(end.X, end.Y, 0)));
            if (worldStart.DistanceTo(worldEnd) < 1) return false;

            var type = WallTypeFor(wall, structure);
            var level = LevelOf(wall, bottom);
            var created = new CoreWall
            {
                Start = worldStart, End = worldEnd, Bulge = bulge, TypeId = type.Id, LevelId = level.Id, Flipped = flipped,
                BaseOffset = Math.Round(bottom - level.Elevation, 3), UnconnectedHeight = Math.Round(top - bottom, 3), Mark = wall.Tag?.ToString() ?? string.Empty
            };
            if (profile is not null)
            {
                created.Profile = profile;
                created.ProfileLength = created.Length;
                _result.Count("Walls shaped to a roof");
            }

            _document.Add(created);
            _made[wall.EntityLabel] = created;
            _walls[wall.EntityLabel] = created;
            _result.Count("Walls");
            return true;
        }

        /// <summary>
        /// A wall's elevation - from its start to its end, from its base to its top - as the clippings
        /// taken off it leave it: cut down under a roof's slopes to a gable or a rake, or up over a
        /// slope it stands on. Null where they leave it whole. Each clipping is a plane through the
        /// wall's middle; one bounded takes away only as far along the wall as its bound reaches.
        /// </summary>
        private IReadOnlyList<Point2D>? Clipped(List<IIfcHalfSpaceSolid> clippings, Point2D start, Point2D end, double baseZ, double height)
        {
            var length = start.DistanceTo(end);
            if (clippings.Count == 0 || length < 1) return null;
            var direction = (end - start) / length;
            Point3D At(double along, double up) => new(start.X + direction.X * along, start.Y + direction.Y * along, baseZ + up);

            // Each as a line on the elevation: what is above it (or below it) taken away, where it reaches.
            var lines = new List<(double F0, double Fa, double Fh, HalfSpace Cut)>();
            foreach (var half in clippings)
            {
                if (_geometry.Cut(half, Frame.Identity) is not { } cut) continue;
                var f0 = cut.Depth(At(0, 0));
                var (fa, fh) = (cut.Depth(At(1, 0)) - f0, cut.Depth(At(0, 1)) - f0);

                // Upright, it cuts the wall's end off square or at an angle: its joins see to that.
                if (Math.Abs(fh) < 1e-9) continue;
                lines.Add((f0, fa, fh, cut));
            }

            if (lines.Count == 0) return null;
            double HeightOf((double F0, double Fa, double Fh, HalfSpace Cut) line, double along) => -(line.F0 + line.Fa * along) / line.Fh;
            bool Reaches((double F0, double Fa, double Fh, HalfSpace Cut) line, double along) => line.Cut.Within(At(along, height / 2));

            // Where the outline can bend: the ends, where two lines cross or one meets the top or the
            // base, and either side of where a bound stops.
            var breaks = new List<double> { 0, length };
            foreach (var line in lines)
            {
                if (Math.Abs(line.Fa) > 1e-12)
                    breaks.AddRange(new[] { 0.0, height }.Select(up => -(line.F0 + line.Fh * up) / line.Fa));
                if (line.Cut.Boundary is null) continue;
                const int steps = 400;
                for (var i = 0; i < steps; i++)
                {
                    var (a, b) = (length * i / steps, length * (i + 1) / steps);
                    if (Reaches(line, a) == Reaches(line, b)) continue;
                    for (var k = 0; k < 40; k++)
                    {
                        var m = (a + b) / 2;
                        if (Reaches(line, m) == Reaches(line, a)) a = m;
                        else b = m;
                    }

                    breaks.AddRange(new[] { a - 0.5, b + 0.5 });
                }
            }

            for (var i = 0; i < lines.Count; i++)
            for (var j = i + 1; j < lines.Count; j++)
            {
                // Where they are as high as each other.
                var (p, q) = (lines[i], lines[j]);
                var denominator = p.Fa / p.Fh - q.Fa / q.Fh;
                if (Math.Abs(denominator) > 1e-12) breaks.Add((q.F0 / q.Fh - p.F0 / p.Fh) / denominator);
            }

            var at = breaks.Where(a => a >= 0 && a <= length).Select(a => Math.Round(a, 3)).Distinct().OrderBy(a => a).ToList();
            var tops = at.Select(a => (A: a, H: Math.Min(height, lines.Where(l => l.Fh > 0 && Reaches(l, a)).Select(l => HeightOf(l, a)).DefaultIfEmpty(height).Min()))).ToList();
            var bases = at.Select(a => (A: a, H: Math.Max(0, lines.Where(l => l.Fh < 0 && Reaches(l, a)).Select(l => HeightOf(l, a)).DefaultIfEmpty(0).Max()))).ToList();
            if (tops.All(t => t.H >= height - 0.5) && bases.All(b => b.H <= 0.5)) return null;

            // Cut down to nothing at a point - a rake coming down to the floor - it is there no higher than its base.
            tops = tops.Zip(bases, (t, b) => (t.A, H: Math.Max(t.H, b.H))).ToList();

            var outline = bases.Select(b => new Point2D(b.A, Math.Round(b.H, 3)))
                .Concat(tops.AsEnumerable().Reverse().Select(t => new Point2D(t.A, Math.Round(t.H, 3))))
                .ToList();
            return Simplified(outline);
        }

        /// <summary>
        /// A solid's top along a wall, where it is not level: the highest of its points, seen square
        /// to the wall, joined over the top - a gable's two rakes, a wall under a lean-to's slope.
        /// Null where the top is level all along.
        /// </summary>
        private static IReadOnlyList<Point2D>? UpperHull(List<Point2D> elevation, double length, double height)
        {
            var sorted = elevation.OrderBy(p => p.X).ThenBy(p => p.Y).ToList();
            var upper = new List<Point2D>();
            foreach (var p in sorted)
            {
                while (upper.Count >= 2 && (upper[^1] - upper[^2]).Cross(p - upper[^2]) >= 0) upper.RemoveAt(upper.Count - 1);
                upper.Add(p);
            }

            // The base corners at either end are not its top.
            if (upper.Count < 2 || upper.Where(p => p.Y > 0.5 || (p.X > 0.5 && p.X < length - 0.5)).All(p => p.Y >= height - 0.5)) return null;
            var outline = new List<Point2D> { new(0, 0), new(length, 0) };
            outline.AddRange(upper.AsEnumerable().Reverse().Select(p => new Point2D(Math.Clamp(Math.Round(p.X, 3), 0, length), Math.Round(p.Y, 3))));
            return Simplified(outline);
        }

        /// <summary>An outline without corners that are not corners: points on the line between their neighbours, and points twice over.</summary>
        private static IReadOnlyList<Point2D>? Simplified(List<Point2D> outline)
        {
            var points = new List<Point2D>();
            foreach (var p in outline)
                if (points.Count == 0 || points[^1].DistanceTo(p) > 0.5) points.Add(p);
            if (points.Count > 1 && points[0].DistanceTo(points[^1]) <= 0.5) points.RemoveAt(points.Count - 1);

            for (var changed = true; changed && points.Count > 3;)
            {
                changed = false;
                for (var i = 0; i < points.Count && points.Count > 3; i++)
                {
                    var (a, b, c) = (points[(i + points.Count - 1) % points.Count], points[i], points[(i + 1) % points.Count]);
                    if (Math.Abs((b - a).Cross(c - a)) > 0.5 * Math.Max(a.DistanceTo(c), 1)) continue;
                    points.RemoveAt(i);
                    changed = true;
                    break;
                }
            }

            return points.Count >= 3 && Polygon2D.Area(points) > 1 ? points : null;
        }

        /// <summary>The convex hull of points, anticlockwise.</summary>
        private static List<Point2D> ConvexHull(List<Point2D> points)
        {
            var sorted = points.Distinct().OrderBy(p => p.X).ThenBy(p => p.Y).ToList();
            if (sorted.Count < 3) return sorted;
            var hull = new List<Point2D>();
            foreach (var pass in new[] { sorted, Enumerable.Reverse(sorted).ToList() })
            {
                var start = hull.Count;
                foreach (var p in pass)
                {
                    while (hull.Count >= start + 2 && (hull[^1] - hull[^2]).Cross(p - hull[^2]) <= 0) hull.RemoveAt(hull.Count - 1);
                    hull.Add(p);
                }

                hull.RemoveAt(hull.Count - 1);
            }

            return hull;
        }

        private static double Width(List<Point2D> hull, Vector2D across) =>
            hull.Max(p => across.Dot(p - Origin)) - hull.Min(p => across.Dot(p - Origin));

        /// <summary>
        /// Walls meeting as they meet here: the end of each on the middle line of the wall it runs
        /// into. Some programs draw a wall's line on one of its faces, or stop a wall at the face of
        /// the wall it meets; read down the middle of its body, such a wall stops short of the other's
        /// middle, or runs past it, by up to half the other's thickness - and then nothing joins and no
        /// room closes. Each end within that reach of another wall's middle line, not running along it,
        /// is carried on or cut back to it; its doors and windows and its outline go with its start.
        /// </summary>
        private void JoinEnds()
        {
            var walls = _walls.Values.Where(wall => !wall.IsCurved).ToList();
            var widths = walls.ToDictionary(wall => wall, wall => _document.GetWallType(wall)?.Width ?? 0);
            foreach (var wall in walls)
            foreach (var atStart in new[] { true, false })
            {
                var end = atStart ? wall.Start : wall.End;
                var outward = atStart ? -wall.Direction : wall.Direction;
                Point2D? best = null;
                var nearest = double.MaxValue;
                foreach (var other in walls)
                {
                    if (ReferenceEquals(other, wall) || other.LevelId != wall.LevelId) continue;
                    var along = other.Direction;
                    var turn = outward.Cross(along);
                    if (Math.Abs(turn) < 0.1) continue;

                    // Where this wall's line meets the other's: how far on from the end, and how far along the other.
                    var t = (other.Start - end).Cross(along) / turn;
                    var reach = widths[other] / 2 + widths[wall] / 2 + 10;
                    if (Math.Abs(t) > reach || Math.Abs(t) >= nearest) continue;
                    var meet = end + outward * t;
                    var s = along.Dot(meet - other.Start);
                    if (s < -widths[wall] / 2 - 10 || s > other.Length + widths[wall] / 2 + 10) continue;
                    (best, nearest) = (meet, Math.Abs(t));
                }

                if (best is not { } to || to.DistanceTo(end) < 0.5) continue;
                if (atStart) MoveStart(wall, to);
                else wall.End = to;
            }
        }

        /// <summary>A wall's start moved along its line: its doors and windows, and its outline, measured from it, kept where they were.</summary>
        private void MoveStart(CoreWall wall, Point2D to)
        {
            var shift = wall.Direction.Dot(to - wall.Start);
            wall.Start = to;
            foreach (var opening in _document.All<Opening>().Where(opening => opening.HostWallId == wall.Id))
                opening.DistanceAlongWall -= shift;
            if (wall.Profile is not { } profile) return;

            // Its corners at the start stay at the start; the rest keep their place in the building.
            wall.Profile = profile.Select(p => p.X <= 1e-6 ? p : new Point2D(Math.Max(0, p.X - shift), p.Y)).ToList();
            wall.ProfileLength -= shift;
        }

        private WallType WallTypeFor(IIfcWall wall, CompoundStructure structure)
        {
            var name = TypeName(wall);
            if (string.IsNullOrWhiteSpace(name)) name = Layers(wall).Set?.LayerSetName?.ToString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name)) name = $"Wall {structure.TotalWidth:0} mm";

            var key = "wall:" + name + ":" + string.Join(",", structure.Layers.Select(layer => $"{layer.MaterialId}/{layer.Thickness:0.#}"));
            if (_types.TryGetValue(key, out var known)) return (WallType)known;

            // One of the project's own, the same by name and thickness, is that type.
            var type = _document.TypesOf<WallType>().FirstOrDefault(candidate => candidate.Name == name && Math.Abs(candidate.Structure.TotalWidth - structure.TotalWidth) < 0.5);
            if (type is null)
            {
                type = new WallType(UniqueName(name), structure)
                {
                    Function = Property(wall, "Pset_WallCommon", "IsExternal") is true ? WallFunction.Exterior : WallFunction.Interior,
                    FireRating = Property(wall, "Pset_WallCommon", "FireRating")?.ToString() ?? string.Empty
                };
                _document.AddType(type);
            }

            _types[key] = type;
            return type;
        }

        private string UniqueName(string name)
        {
            var taken = _document.ElementTypes.Select(type => type.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!taken.Contains(name)) return name;
            return Enumerable.Range(2, 999).Select(i => $"{name} ({i})").First(candidate => !taken.Contains(candidate));
        }

        // ---- doors and windows -------------------------------------------------------------

        private bool ImportOpening(IIfcElement element)
        {
            // The wall it fills a hole in - or, where it says of none, the wall it stands in.
            var hole = element.FillsVoids.FirstOrDefault()?.RelatingOpeningElement;
            var host = hole?.VoidsElements?.RelatingBuildingElement;

            // Where the hole is, along the wall and up it: its shape, or the door's own.
            var mesh = new Mesh3D(Guid.Empty, Guid.Empty, MeshKind.Wall, default, string.Empty);
            if (hole is not null) foreach (var item in Body(hole)) _geometry.Triangles(item, _geometry.Placement(hole.ObjectPlacement), mesh);
            if (mesh.IsEmpty) foreach (var item in Body(element)) _geometry.Triangles(item, _geometry.Placement(element.ObjectPlacement), mesh);
            if (mesh.IsEmpty) return false;

            CoreWall? wall = null;
            if (host is not null) _walls.TryGetValue(host.EntityLabel, out wall);
            else wall = WallAround(mesh);
            if (wall is null || _document.GetWallType(wall) is null) return false;

            var direction = (wall.End - wall.Start).NormalisedOrDefault(Vector2D.UnitX);
            var along = mesh.Positions.Select(p => direction.Dot(Plan(p) - wall.Start)).ToList();
            var (from, to) = (Math.Max(0, along.Min()), Math.Min(wall.Length, along.Max()));
            var (low, high) = (mesh.Positions.Min(p => p.Z), mesh.Positions.Max(p => p.Z));

            var (width, height) = element switch
            {
                IIfcDoor door => (door.OverallWidth is { } w ? _geometry.Length(w) : to - from, door.OverallHeight is { } h ? _geometry.Length(h) : high - low),
                IIfcWindow window => (window.OverallWidth is { } w ? _geometry.Length(w) : to - from, window.OverallHeight is { } h ? _geometry.Length(h) : high - low),
                _ => (to - from, high - low)
            };
            if (width < 1 || height < 1 || to - from < 1) return false;

            var baseElevation = (_document.FindLevel(wall.LevelId)?.Elevation ?? 0) + wall.BaseOffset;
            var isWindow = element is IIfcWindow;
            var type = OpeningTypeFor(element, isWindow, Math.Round(width, 1), Math.Round(height, 1));
            Opening opening = isWindow ? new CoreWindow() : new Door();
            opening.HostWallId = wall.Id;
            opening.TypeId = type.Id;
            opening.LevelId = wall.LevelId;
            opening.DistanceAlongWall = (from + to) / 2;
            opening.SillHeight = Math.Max(0, Math.Round(low - baseElevation, 3));
            opening.Mark = element.Tag?.ToString() ?? string.Empty;
            _document.Add(opening);
            _made[element.EntityLabel] = opening;
            _result.Count(isWindow ? "Windows" : "Doors");
            return true;
        }

        /// <summary>
        /// The wall a door or window stands in, where the file does not say: the one whose body its
        /// middle is in, in plan, along its length and between its base and top - the nearest such.
        /// </summary>
        private CoreWall? WallAround(Mesh3D mesh)
        {
            var middle = Plan(new Point3D(
                (mesh.Positions.Min(p => p.X) + mesh.Positions.Max(p => p.X)) / 2,
                (mesh.Positions.Min(p => p.Y) + mesh.Positions.Max(p => p.Y)) / 2, 0));
            var z = (mesh.Positions.Min(p => p.Z) + mesh.Positions.Max(p => p.Z)) / 2;

            CoreWall? best = null;
            var nearest = double.MaxValue;
            foreach (var wall in _walls.Values)
            {
                if (wall.IsCurved || _document.GetWallType(wall) is not { } type) continue;
                var bottom = (_document.FindLevel(wall.LevelId)?.Elevation ?? 0) + wall.BaseOffset;
                if (z < bottom || z > bottom + wall.UnconnectedHeight) continue;

                var along = wall.Direction.Dot(middle - wall.Start);
                var off = Math.Abs(wall.Direction.PerpendicularLeft().Dot(middle - wall.Start));
                if (along < 0 || along > wall.Length || off > type.Width / 2 + 50 || off >= nearest) continue;
                (best, nearest) = (wall, off);
            }

            return best;
        }

        private OpeningType OpeningTypeFor(IIfcElement element, bool isWindow, double width, double height)
        {
            var name = TypeName(element);
            if (string.IsNullOrWhiteSpace(name)) name = element.Name?.ToString() ?? (isWindow ? "Window" : "Door");
            var key = $"{(isWindow ? "window" : "door")}:{name}:{width:0.#}x{height:0.#}";
            if (_types.TryGetValue(key, out var known)) return (OpeningType)known;

            // One of the project's own, the same by name and size, is that type; else a copy of the first, sized.
            OpeningType? type = isWindow
                ? _document.TypesOf<WindowType>().FirstOrDefault(t => t.Name == name && Math.Abs(t.Width - width) < 0.5 && Math.Abs(t.Height - height) < 0.5)
                : _document.TypesOf<DoorType>().FirstOrDefault(t => t.Name == name && Math.Abs(t.Width - width) < 0.5 && Math.Abs(t.Height - height) < 0.5);
            if (type is null)
            {
                var label = UniqueName(_document.ElementTypes.Any(t => t.Name == name) ? $"{name} {width:0} x {height:0}" : name);
                OpeningType made = isWindow
                    ? _document.TypesOf<WindowType>().FirstOrDefault()?.Duplicate(label) ?? new WindowType(label, width, height)
                    : _document.TypesOf<DoorType>().FirstOrDefault()?.Duplicate(label) ?? new DoorType(label, width, height);
                (made.Width, made.Height) = (width, height);
                _document.AddType(made);
                type = made;
            }

            _types[key] = type;
            return type;
        }

        // ---- floors, roofs and ceilings ------------------------------------------------------

        /// <summary>An element laid flat - one outline extruded, or a solid with a level top and bottom: its outline in plan, its holes, its bottom and its top.</summary>
        private (List<Point2D> Outline, List<List<Point2D>> Holes, double Bottom, double Top)? Flat(IIfcProduct product) =>
            Extruded(product) ?? Prism(product);

        /// <summary>
        /// An element whose body is a solid - faces rather than an outline stood up - with a level top
        /// and bottom: a floor, a ceiling or a room drawn that way. Its outline and holes are the edges
        /// of its top that no two faces share; null where any point of it is neither at its top nor its bottom.
        /// </summary>
        private (List<Point2D> Outline, List<List<Point2D>> Holes, double Bottom, double Top)? Prism(IIfcProduct product)
        {
            var frame = _geometry.Placement(product.ObjectPlacement);
            var mesh = new Mesh3D(Guid.Empty, Guid.Empty, MeshKind.Wall, default, string.Empty);
            foreach (var item in Body(product)) _geometry.Triangles(item, frame, mesh);
            if (mesh.TriangleCount < 4) return null;

            var (low, high) = (mesh.Positions.Min(p => p.Z), mesh.Positions.Max(p => p.Z));
            if (high - low < 1 || mesh.Positions.Any(p => Math.Abs(p.Z - low) > 1 && Math.Abs(p.Z - high) > 1)) return null;

            // The top's edges, each counted for the faces on it: those on one face only go round it.
            static (long, long) Key(Point3D p) => ((long)Math.Round(p.X * 100), (long)Math.Round(p.Y * 100));
            var edges = new Dictionary<((long, long), (long, long)), (Point3D A, Point3D B, int Count)>();
            var (positions, indices) = (mesh.Positions, mesh.Indices);
            for (var i = 0; i + 2 < indices.Count; i += 3)
            {
                var corners = new[] { positions[indices[i]], positions[indices[i + 1]], positions[indices[i + 2]] };
                if (corners.Any(p => Math.Abs(p.Z - high) > 1)) continue;
                for (var k = 0; k < 3; k++)
                {
                    var (a, b) = (corners[k], corners[(k + 1) % 3]);
                    var key = Key(a).CompareTo(Key(b)) < 0 ? (Key(a), Key(b)) : (Key(b), Key(a));
                    if (key.Item1 == key.Item2) continue;
                    edges[key] = edges.TryGetValue(key, out var seen) ? (seen.A, seen.B, seen.Count + 1) : (a, b, 1);
                }
            }

            var loops = IfcGeometry.Loops(edges.Values.Where(edge => edge.Count == 1).Select(edge => (edge.A, edge.B)).ToList())
                .Select(loop => loop.Select(Plan).ToList())
                .Where(loop => loop.Count >= 3 && Polygon2D.Area(loop) > 1)
                .OrderByDescending(Polygon2D.Area)
                .ToList();
            if (loops.Count == 0) return null;

            var outline = loops[0];
            var holes = loops.Skip(1).Where(loop => Polygon2D.Contains(outline, loop[0])).ToList();
            return (outline, holes, low, high);
        }

        /// <summary>An element made of one outline extruded straight up or down: its outline in plan, its bottom and its top.</summary>
        private (List<Point2D> Outline, List<List<Point2D>> Holes, double Bottom, double Top)? Extruded(IIfcProduct product)
        {
            var body = Body(product);
            if (body.Count != 1 || body[0] is not IIfcExtrudedAreaSolid extrusion) return null;

            var frame = _geometry.Placement(product.ObjectPlacement).Then(_geometry.Axis(extrusion.Position));
            var up = frame.Turn(IfcGeometry.Direction(extrusion.ExtrudedDirection));
            if (Math.Abs(Math.Abs(up.Z) - 1) > 1e-6 || Math.Abs(Math.Abs(frame.Z.Z) - 1) > 1e-6) return null;

            List<Point2D> ToPlan(IEnumerable<Point2D> points) => points.Select(p => Plan(frame.Apply(new Point3D(p.X, p.Y, 0)))).ToList();
            var outline = ToPlan(_geometry.Profile(extrusion.SweptArea));
            if (outline.Count < 3) return null;

            var holes = extrusion.SweptArea is IIfcArbitraryProfileDefWithVoids voids
                ? voids.InnerCurves.Select(curve => ToPlan(_geometry.Curve(curve))).Where(hole => hole.Count >= 3).ToList()
                : new List<List<Point2D>>();
            var depth = _geometry.Length(extrusion.Depth) * Math.Sign(up.Z);
            var (z0, z1) = (frame.Origin.Z, frame.Origin.Z + depth);
            return (outline, holes, Math.Min(z0, z1), Math.Max(z0, z1));
        }

        private bool ImportSlab(IIfcSlab slab)
        {
            if (Flat(slab) is not { } flat || flat.Top - flat.Bottom < 1) return false;
            var isRoof = slab.PredefinedType == IfcSlabTypeEnum.ROOF;
            var thickness = flat.Top - flat.Bottom;
            var structure = Structure(Layers(slab).Set);
            if (structure is not null && Math.Abs(structure.TotalWidth - thickness) > 1) structure = null;
            var level = LevelOf(slab, flat.Bottom);

            Slab made = isRoof
                ? new Roof { TypeId = SlabTypeFor<RoofType>(slab, thickness, structure, (name, s) => new RoofType(name, s)).Id }
                : new Floor { TypeId = SlabTypeFor<FloorType>(slab, thickness, structure, (name, s) => new FloorType(name, s)).Id };
            made.LevelId = level.Id;
            // A floor is held by its top, a roof by its underside - where it sits on its walls.
            made.HeightOffset = Math.Round((isRoof ? flat.Bottom : flat.Top) - level.Elevation, 3);
            made.SetBoundary(flat.Outline);
            if (made is Roof roof) roof.SetEdges(flat.Outline.Select(_ => new RoofEdge()));
            if (flat.Holes.Count > 0) made.SetHoles(flat.Holes);
            made.Mark = slab.Tag?.ToString() ?? string.Empty;
            _document.Add(made);
            _made[slab.EntityLabel] = made;
            _result.Count(isRoof ? "Roofs" : "Floors");
            return true;
        }

        private bool ImportRoof(IIfcRoof roof)
        {
            // A roof made of parts is its parts: they come in as themselves.
            if (roof.IsDecomposedBy.Any(relation => relation.RelatedObjects.Any())) return true;
            if (Flat(roof) is not { } flat || flat.Top - flat.Bottom < 1) return false;

            var level = LevelOf(roof, flat.Bottom);
            var made = new Roof { TypeId = SlabTypeFor<RoofType>(roof, flat.Top - flat.Bottom, null, (name, s) => new RoofType(name, s)).Id, LevelId = level.Id, HeightOffset = Math.Round(flat.Bottom - level.Elevation, 3) };
            made.SetBoundary(flat.Outline);
            made.SetEdges(flat.Outline.Select(_ => new RoofEdge()));
            _document.Add(made);
            _made[roof.EntityLabel] = made;
            _result.Count("Roofs");
            return true;
        }

        private bool ImportCovering(IIfcCovering covering)
        {
            if (covering.PredefinedType != IfcCoveringTypeEnum.CEILING || Flat(covering) is not { } flat || flat.Top - flat.Bottom < 1) return false;
            var thickness = flat.Top - flat.Bottom;
            var level = LevelOf(covering, flat.Bottom);
            var ceiling = new Ceiling
            {
                TypeId = SlabTypeFor<CeilingType>(covering, thickness, Structure(Layers(covering).Set), (name, s) => new CeilingType(name, s)).Id,
                LevelId = level.Id, HeightOffset = Math.Round(flat.Top - level.Elevation, 3)
            };
            ceiling.SetBoundary(flat.Outline);
            _document.Add(ceiling);
            _made[covering.EntityLabel] = ceiling;
            _result.Count("Ceilings");
            return true;
        }

        private T SlabTypeFor<T>(IIfcObject item, double thickness, CompoundStructure? structure, Func<string, CompoundStructure, T> make) where T : SlabType
        {
            var name = TypeName(item);
            if (string.IsNullOrWhiteSpace(name)) name = item.Name?.ToString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name)) name = $"{typeof(T).Name.Replace("Type", string.Empty)} {thickness:0} mm";
            var key = $"{typeof(T).Name}:{name}:{thickness:0.#}";
            if (_types.TryGetValue(key, out var known)) return (T)known;

            var type = _document.TypesOf<T>().FirstOrDefault(candidate => candidate.Name == name && Math.Abs(candidate.Structure.TotalWidth - thickness) < 0.5);
            if (type is null)
            {
                type = make(UniqueName(name), structure ?? CompoundStructure.Single(MaterialNamed("Concrete, Cast In Situ"), thickness));
                _document.AddType(type);
            }

            _types[key] = type;
            return type;
        }

        // ---- spaces and grids ----------------------------------------------------------------

        private bool ImportSpace(IIfcSpace space)
        {
            var frame = _geometry.Placement(space.ObjectPlacement);
            var location = Plan(frame.Origin);
            var bottom = frame.Origin.Z;

            // Its outline, from its body or the footprint drawn of it: the room is told by a point inside that.
            var outline = Flat(space) is { } flat ? flat.Outline : Footprint(space, frame);
            if (outline is not null) location = Inside(outline);
            if (Flat(space) is { } solid) bottom = solid.Bottom;

            var level = LevelOf(space, bottom);
            var hasLongName = !string.IsNullOrWhiteSpace(space.LongName);
            var room = new Room
            {
                Location = location, LevelId = level.Id,
                Name = hasLongName ? space.LongName!.ToString()! : space.Name?.ToString() ?? "Room",
                Number = hasLongName ? space.Name?.ToString() ?? string.Empty : string.Empty
            };
            _document.Add(room);
            _made[space.EntityLabel] = room;
            if (outline is not null) _spaces[room] = outline;
            _result.Count("Rooms");
            return true;
        }

        /// <summary>
        /// Rooms the walls do not close - open to the room next door - parted by room separators
        /// where the file's outline of them runs with no wall along it, each line carried on to the
        /// middle of the wall it ends at, and laid once where two rooms share it. A room the walls
        /// close as the file draws it is left as it is.
        /// </summary>
        private void SeparateRooms()
        {
            var laid = new List<RoomSeparator>();

            // Each end where it is reached, the same for every line ending at the same point, so a chain of them stays joined.
            var reached = new Dictionary<(long, long), Point2D>();
            Point2D Reached(Point2D end, Vector2D outward, List<CoreWall> walls)
            {
                var key = ((long)Math.Round(end.X), (long)Math.Round(end.Y));
                if (!reached.TryGetValue(key, out var at)) reached[key] = at = Reach(end, outward, walls);
                return at;
            }

            // Which rooms the walls leave open, found together: each storey's walls split where they cross once, not once a room.
            List<(Room Room, List<Point2D> Outline)> open;
            using (ModelCache.Begin(_document))
                open = _spaces.Select(space => (Room: space.Key, Outline: space.Value))
                    .Where(space => RoomBoundary.Trace(_document, space.Room.LevelId, space.Room.Location) is var traced
                                    && !(traced.IsEnclosed && Math.Abs(traced.Area - Polygon2D.Area(space.Outline)) <= 0.1 * Polygon2D.Area(space.Outline)))
                    .ToList();

            foreach (var (room, outline) in open)
            {

                var walls = _walls.Values.Where(wall => wall.LevelId == room.LevelId && !wall.IsCurved && wall.RoomBounding).ToList();
                for (var i = 0; i < outline.Count; i++)
                {
                    var (a, b) = (outline[i], outline[(i + 1) % outline.Count]);
                    if (a.DistanceTo(b) < 10) continue;

                    // Along a wall's face: the wall bounds it there.
                    var middle = new Point2D((a.X + b.X) / 2, (a.Y + b.Y) / 2);
                    var along = (b - a).NormalisedOrDefault(Vector2D.UnitX);
                    if (walls.Any(wall => Math.Abs(wall.Direction.Cross(along)) < 0.05
                                          && Line2D.DistanceFromSegment(middle, wall.Start, wall.End) <= (_document.GetWallType(wall)?.Width ?? 0) / 2 + 30))
                        continue;

                    // Laid already, for the room the other side.
                    if (laid.Any(line => line.LevelId == room.LevelId && Line2D.DistanceFromSegment(a, line.Start, line.End) <= 50 && Line2D.DistanceFromSegment(b, line.Start, line.End) <= 50))
                        continue;

                    var separator = new RoomSeparator { LevelId = room.LevelId, Start = Reached(a, -along, walls), End = Reached(b, along, walls) };
                    if (separator.Length < 10) continue;
                    _document.Add(separator);
                    laid.Add(separator);
                    _result.Count("Room separators");
                }
            }
        }

        /// <summary>
        /// An end of a room separator carried on - or back - to the middle of the wall it meets there,
        /// if one is within its thickness; else, where it carries on from a wall's face past the wall's
        /// end, moved onto that end.
        /// </summary>
        private Point2D Reach(Point2D end, Vector2D outward, List<CoreWall> walls)
        {
            var best = end;
            var nearest = double.MaxValue;
            foreach (var wall in walls)
            {
                var turn = outward.Cross(wall.Direction);
                if (Math.Abs(turn) < 0.1) continue;
                var t = (wall.Start - end).Cross(wall.Direction) / turn;
                var reach = (_document.GetWallType(wall)?.Width ?? 0) / 2 + 30;
                if (Math.Abs(t) > reach || Math.Abs(t) >= nearest) continue;
                var meet = end + outward * t;
                var s = wall.Direction.Dot(meet - wall.Start);
                if (s < -1 || s > wall.Length + 1) continue;
                (best, nearest) = (meet, Math.Abs(t));
            }

            if (nearest < double.MaxValue) return best;

            // Off the corner where walls end - diagonally, as far as their thickness puts it: onto that corner.
            foreach (var wall in walls)
            foreach (var corner in new[] { wall.Start, wall.End })
            {
                var off = corner.DistanceTo(end);
                if (off > 1.5 * (_document.GetWallType(wall)?.Width ?? 0) / 2 + 30 || off >= nearest) continue;
                (best, nearest) = (corner, off);
            }

            if (nearest < double.MaxValue) return best;
            foreach (var wall in walls)
            {
                var on = Line2D.ClosestPointOnSegment(end, wall.Start, wall.End);
                var off = on.DistanceTo(end);
                if (off > (_document.GetWallType(wall)?.Width ?? 0) / 2 + 30 || off >= nearest) continue;
                (best, nearest) = (on, off);
            }

            return best;
        }

        /// <summary>The outline drawn of an element's footprint - the largest of the closed curves given for it - in plan.</summary>
        private List<Point2D>? Footprint(IIfcProduct product, Frame frame) =>
            Items(product, "FootPrint")
                .SelectMany(item => item switch
                {
                    IIfcGeometricCurveSet set => set.Elements.OfType<IIfcCurve>(),
                    IIfcCurve curve => new[] { curve },
                    _ => Enumerable.Empty<IIfcCurve>()
                })
                .Select(curve => _geometry.Curve(curve).Select(p => Plan(frame.Apply(new Point3D(p.X, p.Y, 0)))).ToList())
                .Where(points => points.Count >= 3 && Polygon2D.Area(points) > 1)
                .OrderByDescending(Polygon2D.Area)
                .FirstOrDefault();

        /// <summary>A point well inside an outline: its middle, where that is inside it; else the middle of the largest piece it divides into.</summary>
        private static Point2D Inside(List<Point2D> outline)
        {
            var middle = Polygon2D.Centroid(outline, new Point2D(outline.Average(p => p.X), outline.Average(p => p.Y)));
            if (Polygon2D.Contains(outline, middle)) return middle;

            var pieces = Polygon2D.Triangulate(outline);
            if (pieces.Count == 0) return middle;
            var (a, b, c) = pieces.OrderByDescending(t => Math.Abs((outline[t.B] - outline[t.A]).Cross(outline[t.C] - outline[t.A]))).First();
            return new Point2D((outline[a].X + outline[b].X + outline[c].X) / 3, (outline[a].Y + outline[b].Y + outline[c].Y) / 3);
        }

        private bool ImportGrid(IIfcGrid grid)
        {
            var frame = _geometry.Placement(grid.ObjectPlacement);
            foreach (var axis in grid.UAxes.Concat(grid.VAxes).Concat(grid.WAxes ?? Enumerable.Empty<IIfcGridAxis>()))
            {
                var points = _geometry.Curve(axis.AxisCurve);
                if (points.Count < 2) continue;
                var (start, end) = (Plan(frame.Apply(new Point3D(points[0].X, points[0].Y, 0))), Plan(frame.Apply(new Point3D(points[^1].X, points[^1].Y, 0))));
                if (axis.SameSense is { } same && !(bool)same) (start, end) = (end, start);
                if (start.DistanceTo(end) < 1) continue;
                _document.Add(new Grid { Name = axis.AxisTag?.ToString() ?? string.Empty, Start = start, End = end, LevelId = _document.Levels[0].Id });
                _result.Count("Grids");
            }

            return true;
        }

        // ---- everything else, as its shape -------------------------------------------------------

        private void ImportShape(IIfcProduct product)
        {
            var body = Body(product);
            if (body.Count == 0) return;

            // Its shape in its own frame where it stands upright - so things alike share a type - else as it is.
            var frame = _geometry.Placement(product.ObjectPlacement);
            var upright = frame.IsUpright;
            var mesh = new Mesh3D(Guid.Empty, Guid.Empty, MeshKind.Wall, default, string.Empty);
            foreach (var item in body) _geometry.Triangles(item, upright ? Frame.Identity with { Scale = frame.Scale } : frame, mesh);
            if (mesh.IsEmpty)
            {
                _result.Notes.Add($"{Describe(product)} has a shape this does not read: left out.");
                return;
            }

            // Turned square to its own long side, so its box fits it in plan: a thing set at an angle
            // within its frame, or with no upright frame to turn it by.
            var turn = Squared(mesh);
            if (turn != 0) mesh = Turned(mesh, -turn);

            var category = CategoryOf(product);
            var name = TypeName(product);
            if (string.IsNullOrWhiteSpace(name)) name = string.IsNullOrWhiteSpace(product.Name) ? Kind(product) : product.Name!.ToString()!;
            var (minX, maxX) = (mesh.Positions.Min(p => p.X), mesh.Positions.Max(p => p.X));
            var (minY, maxY) = (mesh.Positions.Min(p => p.Y), mesh.Positions.Max(p => p.Y));
            // Things alike are one type: the same shape point for point, not just the same size -
            // a roof's two slopes are the same size, and lean opposite ways.
            var shape = new HashCode();
            foreach (var p in mesh.Positions)
            {
                shape.Add(Math.Round(p.X - minX));
                shape.Add(Math.Round(p.Y - minY));
                shape.Add(Math.Round(p.Z));
            }

            var key = $"shape:{product.ExpressType.Name}:{name}:{maxX - minX:0}x{maxY - minY:0}x{mesh.Positions.Max(p => p.Z) - mesh.Positions.Min(p => p.Z):0}:{mesh.TriangleCount}:{shape.ToHashCode()}";
            if (!_types.TryGetValue(key, out var known))
            {
                if (ComponentMeshes.From(mesh.Positions.ToList(), mesh.Indices.ToList(), ModelUnits.Millimetres, yUp: false, $"IFC {product.ExpressType.Name}") is not { } fitted)
                {
                    _result.Notes.Add($"{Describe(product)} has a shape too small or broken to keep: left out.");
                    return;
                }

                var type = new ComponentType(UniqueName($"{Kind(product)} - {name}"), category, ComponentForm.Custom, fitted.Width, fitted.Depth, fitted.Height)
                {
                    Mesh = fitted.Mesh, AgainstWall = false, Placement = ComponentPlacement.Freestanding
                };
                _document.AddType(type);
                known = type;
                _types[key] = type;
            }

            var componentType = (ComponentType)known;

            // Where its box stands: the middle of it in plan, its underside, turned as it was.
            var middle = Turn(new Point3D((minX + maxX) / 2, (minY + maxY) / 2, mesh.Positions.Min(p => p.Z)), turn);
            var world = upright ? frame.Apply(middle) : middle;
            var level = LevelOf(product, world.Z);
            var placed = new Component
            {
                TypeId = componentType.Id, LevelId = level.Id, Location = Plan(world),
                Rotation = Math.Round((upright ? frame.Rotation : 0) + turn * 180 / Math.PI, 6),
                Elevation = Math.Round(world.Z - level.Elevation, 3),
                Mark = (product as IIfcElement)?.Tag?.ToString() ?? string.Empty
            };
            _document.Add(placed);
            _made[product.EntityLabel] = placed;
            _result.Count(category switch
            {
                BuiltInCategory.Furniture => "Furniture",
                BuiltInCategory.PlumbingFixtures => "Sanitary fittings",
                _ => "Other elements"
            });
        }

        /// <summary>
        /// How far a shape is turned from square in plan, radians: the turn that makes the box round
        /// it smallest, within an eighth of a turn either way; none where square already fits it near enough.
        /// </summary>
        private static double Squared(Mesh3D mesh)
        {
            var hull = ConvexHull(mesh.Positions.Select(p => new Point2D(p.X, p.Y)).ToList());
            if (hull.Count < 3) return 0;

            double Box(double angle)
            {
                var (cos, sin) = (Math.Cos(-angle), Math.Sin(-angle));
                var xs = hull.Select(p => p.X * cos - p.Y * sin).ToList();
                var ys = hull.Select(p => p.X * sin + p.Y * cos).ToList();
                return (xs.Max() - xs.Min()) * (ys.Max() - ys.Min());
            }

            var square = Box(0);
            var (best, least) = (0.0, square);
            for (var i = 0; i < hull.Count; i++)
            {
                var edge = hull[(i + 1) % hull.Count] - hull[i];
                var angle = Math.Atan2(edge.Y, edge.X);
                angle -= Math.Round(angle / (Math.PI / 2)) * (Math.PI / 2);
                var area = Box(angle);
                if (area < least) (best, least) = (angle, area);
            }

            return least < square * 0.95 ? best : 0;
        }

        private static Point3D Turn(Point3D p, double angle)
        {
            var (cos, sin) = (Math.Cos(angle), Math.Sin(angle));
            return new Point3D(p.X * cos - p.Y * sin, p.X * sin + p.Y * cos, p.Z);
        }

        private static Mesh3D Turned(Mesh3D mesh, double angle)
        {
            var turned = new Mesh3D(Guid.Empty, Guid.Empty, MeshKind.Wall, default, string.Empty);
            var points = mesh.Positions.Select(p => Turn(p, angle)).ToList();
            for (var i = 0; i + 2 < mesh.Indices.Count; i += 3)
                turned.AddTriangle(points[mesh.Indices[i]], points[mesh.Indices[i + 1]], points[mesh.Indices[i + 2]]);
            return turned;
        }

        /// <summary>What a kind of IFC element is called here: "Stair", "Railing", "Column".</summary>
        private static string Kind(IIfcProduct product)
        {
            var name = product.ExpressType.Name;
            if (name.StartsWith("Ifc", StringComparison.Ordinal)) name = name[3..];
            if (name.EndsWith("StandardCase", StringComparison.Ordinal)) name = name[..^"StandardCase".Length];
            return string.Concat(name.Select((c, i) => i > 0 && char.IsUpper(c) && !char.IsUpper(name[i - 1]) ? " " + c : c.ToString()));
        }

        private static BuiltInCategory CategoryOf(IIfcProduct product) => product switch
        {
            IIfcFurniture or IIfcFurnishingElement => BuiltInCategory.Furniture,
            IIfcSanitaryTerminal => BuiltInCategory.PlumbingFixtures,
            IIfcLightFixture => BuiltInCategory.LightingFixtures,
            IIfcSpaceHeater => BuiltInCategory.MechanicalEquipment,
            IIfcFlowTerminal terminal when terminal.IsTypedBy.FirstOrDefault()?.RelatingType is IIfcSanitaryTerminalType => BuiltInCategory.PlumbingFixtures,
            _ => BuiltInCategory.GenericModels
        };
    }
}
