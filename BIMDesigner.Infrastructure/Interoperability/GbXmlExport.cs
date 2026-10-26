using System.Globalization;
using System.Xml.Linq;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;
using BIMDesigner.Core.Mep;
using CoreWindow = BIMDesigner.Core.Architecture.Window;

namespace BIMDesigner.Infrastructure.Interoperability;

/// <summary>
/// The building as gbXML (schema 7.03) for energy analysis - OpenStudio and EnergyPlus, IES VE,
/// DesignBuilder and the like.
///
/// Each enclosed room is a space, on its storey, its own zone, heated or not as its heat loss has it.
/// Its walls are taken at their centre lines - the room's outline moved out by half of each wall it
/// is bounded by - so the wall between two rooms is one surface with both of them adjacent; its
/// floor and ceiling at its level and the next one up, so the floor between two storeys is one
/// surface over the room below and under the room above. Surfaces facing outside are exterior walls
/// and roofs, those on the ground slabs on grade; windows and doors are openings in the walls they
/// are in. Each surface's points are wound so its normal points away from the first space adjacent to
/// it, and each space's surfaces close round it. Constructions carry their layers - each material's
/// thickness, conductivity and density and so its R-value - and their U-value; window types their
/// U-value, solar heat gain coefficient and light transmittance, all three, as OpenStudio needs them.
/// In metres - OpenStudio's importer mistakes millimetres.
/// </summary>
public static class GbXmlExport
{
    public static readonly XNamespace Ns = "http://www.gbxml.org/schema";
    public const string SchemaVersion = "7.03";

    public static void Save(BimDocument document, string path) => Build(document).Save(path);

    public static XDocument Build(BimDocument document)
    {
        using var cache = ModelCache.Begin(document);
        return new Exporter(document).Run();
    }

    private sealed record Space(int Index, Room Room, string Id, List<Point2D> Outline, List<Wall?> Sides, double Bottom, double Top, Guid LevelId, bool Heated, double? Temperature);

    private sealed record Hole(string Id, string Kind, string? WindowType, string? Construction, List<(double X, double Y, double Z)> Loop, string Name, Guid Element, double Left, double Up, double Width, double Height);

    private sealed class Face
    {
        public required string Id { get; init; }
        public required string Type { get; init; }
        public required string Construction { get; init; }
        public required bool Sun { get; init; }
        public required List<(double X, double Y, double Z)> Loop { get; init; }
        public required List<(Space Space, string Type)> Adjacent { get; init; }
        public List<Hole> Holes { get; } = new();
        public required string Name { get; init; }
        public Guid? Element { get; init; }
    }

    private sealed class Exporter
    {
        private readonly BimDocument _document;
        private readonly List<Space> _spaces = new();
        private readonly List<Face> _faces = new();
        private readonly Dictionary<string, XElement> _constructions = new();
        private readonly Dictionary<string, XElement> _layers = new();
        private readonly Dictionary<string, XElement> _materials = new();
        private readonly Dictionary<Guid, XElement> _windowTypes = new();
        private int _ids;

        public Exporter(BimDocument document) => _document = document;

        private string Next(string prefix) => $"{prefix}-{++_ids}";

        private static string N(double value) => Math.Round(value, 4).ToString("0.####", CultureInfo.InvariantCulture);

        private static XElement E(string name, params object?[] content) => new(Ns + name, content.Where(item => item is not null));

        public XDocument Run()
        {
            Spaces();
            foreach (var group in _spaces.GroupBy(space => space.LevelId)) Walls(group.ToList());
            foreach (var space in _spaces) FloorsAndRoofs(space);

            var information = _document.ProjectInformation;
            var levels = _spaces.Select(space => space.LevelId).Distinct().Select(id => _document.FindLevel(id)).OfType<Core.Datums.Level>().OrderBy(level => level.Elevation).ToList();
            var storeys = levels.ToDictionary(level => level.Id, _ => Next("storey"));
            var building = E("Building", new XAttribute("id", "building-1"), new XAttribute("buildingType", "SingleFamily"),
                E("Name", string.IsNullOrWhiteSpace(information.Name) ? "Building" : information.Name),
                E("Area", N(_spaces.Sum(space => Polygon2D.Area(space.Outline)) / 1e6)),
                levels.Select(level => E("BuildingStorey", new XAttribute("id", storeys[level.Id]), E("Name", level.Name), E("Level", new XAttribute("unit", "Meters"), N(level.Elevation / 1000)))),
                _spaces.Select(space => SpaceElement(space, storeys[space.LevelId])));

            var campus = E("Campus", new XAttribute("id", "campus-1"),
                E("Name", string.IsNullOrWhiteSpace(information.Name) ? "Site" : information.Name),
                E("Location",
                    E("Name", string.IsNullOrWhiteSpace(information.Address) ? information.Name : information.Address),
                    E("ZipcodeOrPostalCode", string.Empty),
                    E("Elevation", new XAttribute("unit", "Meters"), N(information.SiteAltitude)),
                    E("CADModelAzimuth", "0")),
                building,
                _faces.Select(SurfaceElement));

            var root = E("gbXML",
                new XAttribute("version", SchemaVersion), new XAttribute("temperatureUnit", "C"), new XAttribute("lengthUnit", "Meters"),
                new XAttribute("areaUnit", "SquareMeters"), new XAttribute("volumeUnit", "CubicMeters"), new XAttribute("useSIUnitsForResults", "true"),
                new XAttribute("SurfaceReferenceLocation", "Centerline"),
                campus,
                _constructions.Values, _layers.Values, _materials.Values, _windowTypes.Values,
                _spaces.Select(space => E("Zone", new XAttribute("id", $"zone-{space.Index}"), E("Name", RoomsOf.Label(space.Room)),
                    space.Temperature is { } heat ? E("DesignHeatT", new XAttribute("unit", "C"), N(heat)) : null)),
                E("DocumentHistory",
                    E("ProgramInfo", new XAttribute("id", "program-1"), E("CompanyName", "BIMDesigner"), E("ProductName", "BIMDesigner"),
                        E("Version", typeof(GbXmlExport).Assembly.GetName().Version?.ToString() ?? "1.0"), E("Platform", "Windows")),
                    E("PersonInfo", new XAttribute("id", "person-1"), E("FirstName", "Designer"), E("LastName", "Designer")),
                    E("CreatedBy", new XAttribute("programId", "program-1"), new XAttribute("personId", "person-1"),
                        new XAttribute("date", DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)))));
            return new XDocument(new XDeclaration("1.0", "UTF-8", null), root);
        }

        // ---- the spaces ----------------------------------------------------------------

        /// <summary>Each enclosed room, its outline moved out to its walls' centre lines, anticlockwise; from its level up to the next, or its own height at the top.</summary>
        private void Spaces()
        {
            var levels = _document.Levels.OrderBy(level => level.Elevation).ToList();
            var heated = HeatLoss.Of(_document);
            foreach (var room in _document.All<Room>().OrderBy(room => room.Number, StringComparer.OrdinalIgnoreCase))
            {
                var boundary = room.GetBoundary(_document);
                if (!boundary.IsEnclosed || boundary.Polygon.Count < 3 || _document.FindLevel(room.LevelId) is not { } level) continue;
                var outline = boundary.Polygon.ToList();
                var sides = boundary.Sides.Count == outline.Count ? boundary.Sides.ToList() : outline.Select(_ => (Wall?)null).ToList();
                if (Polygon2D.SignedArea(outline) < 0)
                {
                    var n = outline.Count;
                    outline.Reverse();
                    sides = Enumerable.Range(0, n).Select(k => sides[((n - 2 - k) % n + n) % n]).ToList();
                }

                var above = levels.FirstOrDefault(other => other.Elevation > level.Elevation + 1);
                var top = above?.Elevation ?? level.Elevation + (room.Height > 100 ? room.Height : 2400);
                var temperature = heated?.Rooms.FirstOrDefault(each => each.Room == room)?.Temperature;
                var index = _spaces.Count + 1;
                _spaces.Add(new Space(index, room, $"space-{index}", Centreline(outline, sides), sides, level.Elevation, top, level.Id, temperature is not null, temperature));
            }
        }

        /// <summary>An outline moved out, side by side, by half the thickness of the wall along each - to the walls' centre lines.</summary>
        private List<Point2D> Centreline(List<Point2D> outline, List<Wall?> sides)
        {
            var n = outline.Count;
            var lines = new List<(Point2D At, Vector2D Along)>();
            for (var i = 0; i < n; i++)
            {
                var (a, b) = (outline[i], outline[(i + 1) % n]);
                var along = (b - a).NormalisedOrDefault(Vector2D.UnitX);
                var outward = new Vector2D(along.Y, -along.X);
                var half = sides[i] is { } wall && _document.GetWallType(wall) is { } type ? type.Structure.TotalWidth / 2 : 0;
                lines.Add((a + outward * half, along));
            }

            var corners = new List<Point2D>();
            for (var i = 0; i < n; i++)
            {
                var (before, here) = (lines[(i - 1 + n) % n], lines[i]);
                var cross = before.Along.X * here.Along.Y - before.Along.Y * here.Along.X;
                Point2D corner;
                if (Math.Abs(cross) < 1e-9) corner = here.At;
                else
                {
                    var delta = here.At - before.At;
                    var t = (delta.X * here.Along.Y - delta.Y * here.Along.X) / cross;
                    corner = before.At + before.Along * t;
                }

                corners.Add(new Point2D(Math.Round(corner.X, 1), Math.Round(corner.Y, 1)));
            }

            return corners;
        }

        private XElement SpaceElement(Space space, string storey)
        {
            var area = Polygon2D.Area(space.Outline) / 1e6;
            var own = _faces.Where(face => face.Adjacent.Any(entry => entry.Space == space)).ToList();
            return E("Space", new XAttribute("id", space.Id), new XAttribute("zoneIdRef", $"zone-{space.Index}"),
                new XAttribute("conditionType", space.Heated ? "Heated" : "Unconditioned"), new XAttribute("buildingStoreyIdRef", storey),
                E("Name", RoomsOf.Label(space.Room)),
                E("Area", N(area)),
                E("Volume", N(area * (space.Top - space.Bottom) / 1000)),
                E("ShellGeometry", new XAttribute("id", $"shell-{space.Index}"),
                    E("ClosedShell", own.Select(face => Loop(face.Adjacent[0].Space == space ? face.Loop : Reversed(face.Loop))))),
                E("CADObjectId", space.Room.Id.ToString()),
                own.Select(face => E("SpaceBoundary", new XAttribute("isSecondLevelBoundary", "false"), new XAttribute("surfaceIdRef", face.Id),
                    E("PlanarGeometry", Loop(face.Loop)))));
        }

        private static List<(double X, double Y, double Z)> Reversed(List<(double X, double Y, double Z)> loop)
        {
            var reversed = loop.ToList();
            reversed.Reverse();
            return reversed;
        }

        private static XElement Loop(IEnumerable<(double X, double Y, double Z)> points) =>
            E("PolyLoop", points.Select(point => E("CartesianPoint", E("Coordinate", N(point.X / 1000)), E("Coordinate", N(point.Y / 1000)), E("Coordinate", N(point.Z / 1000)))));

        // ---- the walls --------------------------------------------------------------

        /// <summary>
        /// The walls of one storey's spaces: each side of each, split where another space's side runs
        /// back along it - a wall between the two, given once - and what is left facing outside or nothing.
        /// </summary>
        private void Walls(List<Space> storey)
        {
            foreach (var space in storey)
            {
                var n = space.Outline.Count;
                for (var i = 0; i < n; i++)
                {
                    var (a, b) = (space.Outline[i], space.Outline[(i + 1) % n]);
                    var length = a.DistanceTo(b);
                    if (length < 1) continue;
                    var along = (b - a) * (1 / length);

                    // Where other spaces' sides run back along this one.
                    var shared = new List<(double From, double To, Space Other)>();
                    foreach (var other in storey.Where(other => other != space))
                        for (var j = 0; j < other.Outline.Count; j++)
                        {
                            var (c, d) = (other.Outline[j], other.Outline[(j + 1) % other.Outline.Count]);
                            if (c.DistanceTo(d) < 1) continue;
                            var direction = (d - c).NormalisedOrDefault(Vector2D.UnitX);
                            if (direction.Dot(along) > -0.999) continue;
                            if (Math.Abs(Cross(along, c - a)) > 1 || Math.Abs(Cross(along, d - a)) > 1) continue;
                            var (u, v) = ((c - a).Dot(along), (d - a).Dot(along));
                            var (from, to) = (Math.Max(0, Math.Min(u, v)), Math.Min(length, Math.Max(u, v)));
                            if (to - from > 1) shared.Add((from, to, other));
                        }

                    var cuts = shared.SelectMany(entry => new[] { entry.From, entry.To }).Append(0).Append(length).Distinct().OrderBy(t => t).ToList();
                    for (var k = 0; k + 1 < cuts.Count; k++)
                    {
                        var (from, to) = (cuts[k], cuts[k + 1]);
                        if (to - from < 1) continue;
                        var middle = (from + to) / 2;
                        var neighbour = shared.FirstOrDefault(entry => entry.From <= middle && entry.To >= middle).Other;
                        if (neighbour is not null && neighbour.Index < space.Index) continue;
                        var (start, end) = (a + along * from, a + along * to);
                        var wall = space.Sides[i];
                        if (neighbour is not null)
                        {
                            var top = Math.Min(space.Top, neighbour.Top);
                            AddWall(space, neighbour, wall, start, end, space.Bottom, top);
                            if (space.Top > top + 1) AddWall(space, null, wall, start, end, top, space.Top, adiabatic: true);
                            if (neighbour.Top > top + 1) AddWall(neighbour, null, wall, end, start, top, neighbour.Top, adiabatic: true);
                        }
                        else AddWall(space, null, wall, start, end, space.Bottom, space.Top);
                    }
                }
            }
        }

        private static double Cross(Vector2D a, Vector2D b) => a.X * b.Y - a.Y * b.X;

        private void AddWall(Space space, Space? neighbour, Wall? wall, Point2D start, Point2D end, double bottom, double top, bool adiabatic = false)
        {
            if (top - bottom < 1) return;
            // Outside walls, curtain walls, and walls against the ground - retaining and foundation walls - bound the outside.
            var function = wall is null ? null : _document.GetWallType(wall)?.Function;
            var curtain = wall is not null && _document.FindType<CurtainWallType>(wall.TypeId) is not null;
            var exterior = neighbour is null && !adiabatic && wall is not null && (curtain || function is WallFunction.Exterior or WallFunction.Retaining or WallFunction.Foundation);
            var underground = exterior && (top <= 0 || function is WallFunction.Retaining or WallFunction.Foundation);
            var type = neighbour is not null || !exterior ? "InteriorWall" : underground ? "UndergroundWall" : "ExteriorWall";
            var loop = new List<(double, double, double)> { (start.X, start.Y, bottom), (end.X, end.Y, bottom), (end.X, end.Y, top), (start.X, start.Y, top) };
            var adjacent = new List<(Space, string)> { (space, type) };
            if (neighbour is not null) adjacent.Add((neighbour, type));
            var face = new Face
            {
                Id = Next("surface"), Type = type, Construction = wall is not null ? WallConstruction(wall) : AirConstruction(), Sun = exterior && !underground,
                Loop = loop, Adjacent = adjacent, Name = wall is not null ? $"{_document.GetWallType(wall)?.Name ?? "Wall"} - {RoomsOf.Label(space.Room)}" : $"Room separator - {RoomsOf.Label(space.Room)}",
                Element = wall?.Id
            };
            if (wall is not null) Openings(face, wall, start, end, bottom, top);
            _faces.Add(face);
        }

        /// <summary>The windows and doors in a wall that fall within a stretch of it, as openings in the surface there.</summary>
        private void Openings(Face face, Wall wall, Point2D start, Point2D end, double bottom, double top)
        {
            var length = start.DistanceTo(end);
            var along = (end - start) * (1 / length);
            var direction = (wall.End - wall.Start).NormalisedOrDefault(Vector2D.UnitX);
            var baseHeight = wall.GetBaseElevation(_document);
            foreach (var opening in _document.All<Opening>().Where(opening => opening.HostWallId == wall.Id && opening is CoreWindow or Door))
            {
                var type = _document.FindType<OpeningType>(opening.TypeId);
                var width = opening.WidthOf(type);
                var (sill, height) = opening.Placed(_document, type, wall);
                if (width < 1 || height < 1) continue;
                var centre = wall.Start + direction * opening.DistanceAlongWall;
                var u = (centre - start).Dot(along);
                var (left, right) = (u - width / 2, u + width / 2);
                var (low, high) = (baseHeight + sill, baseHeight + sill + height);
                if (left < -0.5 || right > length + 0.5 || low < bottom - 0.5 || high > top + 0.5) continue;
                var (p, q) = (start + along * left, start + along * right);
                var isDoor = opening is Door;
                face.Holes.Add(new Hole(Next("opening"), isDoor ? "NonSlidingDoor" : "OperableWindow",
                    isDoor || type is not WindowType windowType ? null : WindowTypeOf(windowType),
                    isDoor && type is not null ? DoorConstruction(type) : null,
                    new List<(double, double, double)> { (p.X, p.Y, low), (q.X, q.Y, low), (q.X, q.Y, high), (p.X, p.Y, high) },
                    $"{(isDoor ? "Door" : "Window")} {(string.IsNullOrWhiteSpace(opening.Mark) ? type?.Name : opening.Mark)}", opening.Id, left, low - bottom, width, height));
            }
        }

        // ---- floors, ceilings and roofs -------------------------------------------------

        /// <summary>
        /// A space's floor - over the spaces below it, one surface each with them; on the ground, or over
        /// the outside, where none is - and its top where no space above covers it, a roof.
        /// </summary>
        private void FloorsAndRoofs(Space space)
        {
            var below = _spaces.Where(other => other != space && Math.Abs(other.Top - space.Bottom) < 1).ToList();
            var above = _spaces.Where(other => other != space && Math.Abs(other.Bottom - space.Top) < 1).ToList();
            // On the ground: the lowest floor any room has - not a foundation's level under it.
            var lowest = _spaces.Min(other => other.Bottom);
            var floorConstruction = FloorConstruction(space.LevelId, space.Outline);

            // Over the spaces below: the upper space first, its normal down; the lower one's ceiling.
            var uncovered = new List<IReadOnlyList<Point2D>> { space.Outline };
            foreach (var other in below)
            {
                foreach (var piece in Pieces(space.Outline, other.Outline, BooleanOperation.Intersection))
                    _faces.Add(new Face
                    {
                        Id = Next("surface"), Type = "InteriorFloor", Construction = floorConstruction, Sun = false,
                        Loop = Flat(piece, space.Bottom, down: true), Adjacent = new List<(Space, string)> { (space, "InteriorFloor"), (other, "Ceiling") },
                        Name = $"Floor - {RoomsOf.Label(space.Room)} over {RoomsOf.Label(other.Room)}"
                    });
                uncovered = uncovered.SelectMany(left => Pieces(left, other.Outline, BooleanOperation.Difference)).ToList();
            }

            foreach (var piece in uncovered)
                _faces.Add(new Face
                {
                    Id = Next("surface"), Type = space.Bottom <= lowest + 1 ? "SlabOnGrade" : "ExposedFloor", Construction = floorConstruction, Sun = false,
                    Loop = Flat(piece, space.Bottom, down: true), Adjacent = new List<(Space, string)> { (space, space.Bottom <= lowest + 1 ? "SlabOnGrade" : "ExposedFloor") },
                    Name = $"Floor - {RoomsOf.Label(space.Room)}"
                });

            // Its top where nothing is over it: a roof.
            var open = new List<IReadOnlyList<Point2D>> { space.Outline };
            foreach (var other in above) open = open.SelectMany(left => Pieces(left, other.Outline, BooleanOperation.Difference)).ToList();
            var roofConstruction = RoofConstruction(space);
            foreach (var piece in open)
                _faces.Add(new Face
                {
                    Id = Next("surface"), Type = "Roof", Construction = roofConstruction, Sun = true,
                    Loop = Flat(piece, space.Top, down: false), Adjacent = new List<(Space, string)> { (space, "Roof") },
                    Name = $"Roof - {RoomsOf.Label(space.Room)}"
                });
        }

        /// <summary>The pieces of two outlines combined, each as one loop, the smallest slivers left out.</summary>
        private static IEnumerable<IReadOnlyList<Point2D>> Pieces(IReadOnlyList<Point2D> a, IReadOnlyList<Point2D> b, BooleanOperation operation)
        {
            IReadOnlyList<PolygonBoolean.Region> regions;
            try { regions = PolygonBoolean.Combine(a, b, operation); }
            catch (Exception) { return operation == BooleanOperation.Difference ? new[] { a } : Array.Empty<IReadOnlyList<Point2D>>(); }
            return regions.SelectMany(PolygonBoolean.WithoutHoles)
                .Select(loop => (IReadOnlyList<Point2D>)loop.Select(point => new Point2D(Math.Round(point.X, 1), Math.Round(point.Y, 1))).ToList())
                .Where(loop => loop.Count >= 3 && Polygon2D.Area(loop) > 10_000)
                .ToList();
        }

        /// <summary>A flat loop at a height, anticlockwise from above - its normal up - or the other way for one facing down.</summary>
        private static List<(double X, double Y, double Z)> Flat(IReadOnlyList<Point2D> outline, double z, bool down)
        {
            var points = outline.Select(point => (point.X, point.Y, z)).ToList();
            var anticlockwise = Polygon2D.SignedArea(outline) > 0;
            if (anticlockwise == down) points.Reverse();
            return points;
        }

        // ---- constructions --------------------------------------------------------

        private string Construction(string key, string name, double? u, IEnumerable<MaterialLayer> layers)
        {
            if (_constructions.ContainsKey(key)) return key;
            var element = E("Construction", new XAttribute("id", key), E("Name", name),
                u is { } value ? E("U-value", new XAttribute("unit", "WPerSquareMeterK"), N(value)) : null);
            foreach (var layer in layers.Where(layer => layer.Thickness > 0))
                element.Add(E("LayerId", new XAttribute("layerIdRef", LayerOf(layer))));
            _constructions[key] = element;
            return key;
        }

        private string LayerOf(MaterialLayer layer)
        {
            var key = $"layer-{layer.MaterialId:N}-{layer.Thickness:0}";
            if (_layers.ContainsKey(key)) return key;
            var material = _document.FindMaterial(layer.MaterialId);
            var materialKey = $"material-{layer.MaterialId:N}-{layer.Thickness:0}";
            if (!_materials.ContainsKey(materialKey))
            {
                var thickness = layer.Thickness / 1000;
                var conductivity = material?.ThermalConductivity ?? 0;
                _materials[materialKey] = E("Material", new XAttribute("id", materialKey),
                    E("Name", $"{material?.Name ?? "Material"} {layer.Thickness:0} mm"),
                    conductivity > 0 ? E("R-value", new XAttribute("unit", "SquareMeterKPerW"), N(thickness / conductivity)) : null,
                    E("Thickness", new XAttribute("unit", "Meters"), N(thickness)),
                    conductivity > 0 ? E("Conductivity", new XAttribute("unit", "WPerMeterK"), N(conductivity)) : null,
                    material is { Density: > 0 } ? E("Density", new XAttribute("unit", "KgPerCubicM"), N(material.Density)) : null);
            }

            _layers[key] = E("Layer", new XAttribute("id", key), E("MaterialId", new XAttribute("materialIdRef", materialKey)));
            return key;
        }

        private string WallConstruction(Wall wall) =>
            _document.GetWallType(wall) is { } type
                ? Construction($"construction-{type.Id:N}", type.Name, WallRules.UValue(_document, type), type.Structure.Layers)
                : _document.FindType<CurtainWallType>(wall.TypeId) is { } curtain
                    ? Construction($"construction-{curtain.Id:N}", curtain.Name, curtain.HeatTransferCoefficient > 0 ? curtain.HeatTransferCoefficient : null, Array.Empty<MaterialLayer>())
                    : AirConstruction();

        private string AirConstruction() => Construction("construction-air", "Air boundary - a room separator", null, Array.Empty<MaterialLayer>());

        /// <summary>The floor under a space: its type's layers from the underside up, the side away from the space first.</summary>
        private string FloorConstruction(Guid levelId, IReadOnlyList<Point2D> outline)
        {
            var middle = Polygon2D.Centroid(outline, outline[0]);
            var floor = _document.All<Floor>().FirstOrDefault(each => each.LevelId == levelId && Polygon2D.Contains(each.Boundary, middle))
                        ?? _document.All<Floor>().FirstOrDefault(each => each.LevelId == levelId);
            if (floor is null || _document.FindType<SlabType>(floor.TypeId) is not { } type) return Construction("construction-floor", "Floor", null, Array.Empty<MaterialLayer>());
            return Construction($"construction-{type.Id:N}", type.Name, SlabRules.UValue(_document, floor) ?? Thermal.UValue(_document, type.Structure, Thermal.Flow.Downward),
                type.Structure.Layers.Reverse());
        }

        /// <summary>The roof over a space, or the floor of the storey over it where there is no roof there.</summary>
        private string RoofConstruction(Space space)
        {
            var middle = Polygon2D.Centroid(space.Outline, space.Outline[0]);
            var roof = _document.All<Roof>().FirstOrDefault(each => each.Contains(middle) && each.GetBottomElevation(_document) >= space.Bottom + 1)
                       ?? _document.All<Roof>().FirstOrDefault(each => each.LevelId == space.LevelId);
            if (roof is null || _document.FindType<SlabType>(roof.TypeId) is not { } type) return Construction("construction-roof", "Roof", null, Array.Empty<MaterialLayer>());
            return Construction($"construction-{type.Id:N}", type.Name, SlabRules.UValue(_document, roof), type.Structure.Layers);
        }

        /// <summary>A door's construction: its U-value, as one layer of that resistance less the surfaces' 0.17 m²K/W.</summary>
        private string DoorConstruction(OpeningType type)
        {
            var key = $"construction-{type.Id:N}";
            if (_constructions.ContainsKey(key)) return key;
            var u = type.HeatTransferCoefficient > 0 ? type.HeatTransferCoefficient : (double?)null;
            var element = E("Construction", new XAttribute("id", key), E("Name", type.Name), u is { } value ? E("U-value", new XAttribute("unit", "WPerSquareMeterK"), N(value)) : null);
            if (u is { } given)
            {
                var materialKey = $"material-{type.Id:N}";
                _materials[materialKey] = E("Material", new XAttribute("id", materialKey), E("Name", $"{type.Name} leaf"),
                    E("R-value", new XAttribute("unit", "SquareMeterKPerW"), N(Math.Max(0.01, 1 / given - 0.17))));
                _layers[$"layer-{type.Id:N}"] = E("Layer", new XAttribute("id", $"layer-{type.Id:N}"), E("MaterialId", new XAttribute("materialIdRef", materialKey)));
                element.Add(E("LayerId", new XAttribute("layerIdRef", $"layer-{type.Id:N}")));
            }

            _constructions[key] = element;
            return key;
        }

        private string WindowTypeOf(WindowType type)
        {
            if (!_windowTypes.ContainsKey(type.Id))
                _windowTypes[type.Id] = E("WindowType", new XAttribute("id", $"window-{type.Id:N}"), E("Name", type.Name),
                    E("U-value", new XAttribute("unit", "WPerSquareMeterK"), N(type.HeatTransferCoefficient > 0 ? type.HeatTransferCoefficient : 1.4)),
                    E("SolarHeatGainCoeff", new XAttribute("unit", "Fraction"), N(type.SolarHeatGainCoefficient)),
                    E("Transmittance", new XAttribute("unit", "Fraction"), new XAttribute("type", "Visible"), new XAttribute("surfaceType", "Both"), N(type.LightTransmittance)));
            return $"window-{type.Id:N}";
        }

        // ---- the surfaces -------------------------------------------------------------

        private XElement SurfaceElement(Face face)
        {
            var (azimuth, tilt, width, height) = Orientation(face.Loop);
            var element = E("Surface", new XAttribute("id", face.Id), new XAttribute("surfaceType", face.Type), new XAttribute("constructionIdRef", face.Construction),
                new XAttribute("exposedToSun", face.Sun ? "true" : "false"),
                E("Name", face.Name),
                face.Adjacent.Select(entry => E("AdjacentSpaceId", new XAttribute("spaceIdRef", entry.Space.Id), new XAttribute("surfaceType", entry.Type))),
                E("RectangularGeometry", E("Azimuth", N(azimuth)),
                    E("CartesianPoint", E("Coordinate", N(face.Loop[0].X / 1000)), E("Coordinate", N(face.Loop[0].Y / 1000)), E("Coordinate", N(face.Loop[0].Z / 1000))),
                    E("Tilt", N(tilt)), E("Width", N(width / 1000)), E("Height", N(height / 1000))),
                E("PlanarGeometry", Loop(face.Loop)));
            foreach (var hole in face.Holes)
                element.Add(E("Opening", new XAttribute("id", hole.Id), new XAttribute("openingType", hole.Kind),
                    hole.WindowType is { } window ? new XAttribute("windowTypeIdRef", window) : null,
                    hole.Construction is { } construction ? new XAttribute("constructionIdRef", construction) : null,
                    E("Name", hole.Name),
                    E("RectangularGeometry",
                        E("CartesianPoint", E("Coordinate", N(hole.Left / 1000)), E("Coordinate", N(hole.Up / 1000))),
                        E("Width", N(hole.Width / 1000)), E("Height", N(hole.Height / 1000))),
                    E("PlanarGeometry", Loop(hole.Loop)),
                    E("CADObjectId", hole.Element.ToString())));
            if (face.Element is { } id) element.Add(E("CADObjectId", id.ToString()));
            return element;
        }

        /// <summary>A loop's normal as an azimuth clockwise from north and a tilt from straight up, and its extent across and up it, mm.</summary>
        private static (double Azimuth, double Tilt, double Width, double Height) Orientation(List<(double X, double Y, double Z)> loop)
        {
            double nx = 0, ny = 0, nz = 0;
            for (var i = 0; i < loop.Count; i++)
            {
                var (p, q) = (loop[i], loop[(i + 1) % loop.Count]);
                nx += (p.Y - q.Y) * (p.Z + q.Z);
                ny += (p.Z - q.Z) * (p.X + q.X);
                nz += (p.X - q.X) * (p.Y + q.Y);
            }

            var length = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (length < 1e-9) return (0, 90, 0, 0);
            (nx, ny, nz) = (nx / length, ny / length, nz / length);
            var tilt = Math.Acos(Math.Clamp(nz, -1, 1)) * 180 / Math.PI;
            var azimuth = Math.Abs(nz) > 0.999 ? 0 : (Math.Atan2(nx, ny) * 180 / Math.PI + 360) % 360;
            if (Math.Abs(nz) > 0.999)
                return (azimuth, tilt, loop.Max(p => p.X) - loop.Min(p => p.X), loop.Max(p => p.Y) - loop.Min(p => p.Y));
            var across = loop.Select(p => p.X * ny - p.Y * nx).ToList();
            return (azimuth, tilt, across.Max() - across.Min(), loop.Max(p => p.Z) - loop.Min(p => p.Z));
        }
    }
}
