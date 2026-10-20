using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;
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
        private readonly HashSet<int> _handled = new();
        private readonly Dictionary<string, ElementType> _types = new();
        private readonly Dictionary<string, Guid> _materials = new(StringComparer.OrdinalIgnoreCase);
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

            // Everything else with a shape, as a component carrying it.
            foreach (var product in _model.Instances.OfType<IIfcProduct>())
            {
                if (_handled.Contains(product.EntityLabel) || product is IIfcSpatialElement or IIfcFeatureElementSubtraction or IIfcGrid or IIfcAnnotation or IIfcVirtualElement or IIfcPort)
                    continue;
                ImportShape(product);
            }

            return _result;
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

            // Its body: extrusions straight up, one or a tier each.
            var extrusions = Body(wall).OfType<IIfcExtrudedAreaSolid>().ToList();
            if (extrusions.Count == 0 || extrusions.Count != Body(wall).Count) return false;
            var bottom = double.MaxValue;
            var top = double.MinValue;
            var outline = new List<Point2D>();
            foreach (var extrusion in extrusions)
            {
                var position = frame.Then(_geometry.Axis(extrusion.Position));
                var up = position.Turn(IfcGeometry.Direction(extrusion.ExtrudedDirection));
                if (Math.Abs(Math.Abs(up.Z) - 1) > 1e-6) return false;
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
                // The layers' middle off the axis: their start on it, plus half their thickness, the way they run.
                var shift = (_geometry.Length(usage.OffsetFromReferenceLine) + structure.TotalWidth / 2) * (flipped ? -1 : 1);
                var across = direction.PerpendicularLeft() * shift;
                (start, end) = (start + across, end + across);
            }

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
            _document.Add(created);
            _walls[wall.EntityLabel] = created;
            _result.Count("Walls");
            return true;
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
            // The wall it fills a hole in.
            var hole = element.FillsVoids.FirstOrDefault()?.RelatingOpeningElement;
            var host = hole?.VoidsElements?.RelatingBuildingElement;
            if (host is null || !_walls.TryGetValue(host.EntityLabel, out var wall)) return false;
            if (_document.GetWallType(wall) is not { } wallType) return false;

            // Where the hole is, along the wall and up it: its shape, or the door's own.
            var mesh = new Mesh3D(Guid.Empty, Guid.Empty, MeshKind.Wall, default, string.Empty);
            foreach (var item in Body(hole!)) _geometry.Triangles(item, _geometry.Placement(hole!.ObjectPlacement), mesh);
            if (mesh.IsEmpty) foreach (var item in Body(element)) _geometry.Triangles(item, _geometry.Placement(element.ObjectPlacement), mesh);
            if (mesh.IsEmpty) return false;

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
            _result.Count(isWindow ? "Windows" : "Doors");
            return true;
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

        /// <summary>An element made of one outline extruded straight up or down: its outline in plan, its bottom and its top.</summary>
        private (List<Point2D> Outline, List<List<Point2D>> Holes, double Bottom, double Top)? Flat(IIfcProduct product)
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
            if (Flat(space) is { } flat)
            {
                location = new Point2D(flat.Outline.Average(p => p.X), flat.Outline.Average(p => p.Y));
                if (Polygon2D.Triangulate(flat.Outline).Count > 0) location = Polygon2D.Centroid(flat.Outline, flat.Outline[0]);
                bottom = flat.Bottom;
            }

            var level = LevelOf(space, bottom);
            var hasLongName = !string.IsNullOrWhiteSpace(space.LongName);
            _document.Add(new Room
            {
                Location = location, LevelId = level.Id,
                Name = hasLongName ? space.LongName!.ToString()! : space.Name?.ToString() ?? "Room",
                Number = hasLongName ? space.Name?.ToString() ?? string.Empty : string.Empty
            });
            _result.Count("Rooms");
            return true;
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

            var category = CategoryOf(product);
            var name = TypeName(product);
            if (string.IsNullOrWhiteSpace(name)) name = string.IsNullOrWhiteSpace(product.Name) ? Kind(product) : product.Name!.ToString()!;
            var (minX, maxX) = (mesh.Positions.Min(p => p.X), mesh.Positions.Max(p => p.X));
            var (minY, maxY) = (mesh.Positions.Min(p => p.Y), mesh.Positions.Max(p => p.Y));
            var key = $"shape:{product.ExpressType.Name}:{name}:{maxX - minX:0}x{maxY - minY:0}x{mesh.Positions.Max(p => p.Z) - mesh.Positions.Min(p => p.Z):0}:{mesh.TriangleCount}";
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
            var middle = new Point3D((minX + maxX) / 2, (minY + maxY) / 2, mesh.Positions.Min(p => p.Z));
            var world = upright ? frame.Apply(middle) : middle;
            var level = LevelOf(product, world.Z);
            _document.Add(new Component
            {
                TypeId = componentType.Id, LevelId = level.Id, Location = Plan(world),
                Rotation = upright ? Math.Round(frame.Rotation, 6) : 0,
                Elevation = Math.Round(world.Z - level.Elevation, 3),
                Mark = (product as IIfcElement)?.Tag?.ToString() ?? string.Empty
            });
            _result.Count(category switch
            {
                BuiltInCategory.Furniture => "Furniture",
                BuiltInCategory.PlumbingFixtures => "Sanitary fittings",
                _ => "Other elements"
            });
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
