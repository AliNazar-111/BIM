using System.Globalization;
using System.Xml.Linq;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Infrastructure.Interoperability;
using static BIMDesigner.Tests.VentilationTests;
using BimWindow = BIMDesigner.Core.Architecture.Window;

namespace BIMDesigner.Tests;

/// <summary>
/// gbXML for energy analysis (round M8): each room a space with its surfaces at the walls' centre lines
/// closing round it, wound away from the first space adjacent; walls between rooms and floors between
/// storeys one surface for both; windows and doors openings in their walls; constructions with their
/// layers and U-values; every id unique and every reference resolving.
/// </summary>
[Collection("Wpf")]
public class GbXmlTests
{
    private static readonly XNamespace G = GbXmlExport.Ns;

    private static double D(XElement element) => double.Parse(element.Value, CultureInfo.InvariantCulture);

    private static List<Point3D> Points(XElement loop) =>
        loop.Elements(G + "CartesianPoint").Select(point => point.Elements(G + "Coordinate").Select(D).ToList()).Select(c => new Point3D(c[0], c[1], c[2])).ToList();

    /// <summary>A loop's area vector: half the sum of its edges' cross products - its normal times its area.</summary>
    private static (double X, double Y, double Z) AreaVector(IReadOnlyList<Point3D> loop)
    {
        double x = 0, y = 0, z = 0;
        for (var i = 0; i < loop.Count; i++)
        {
            var (p, q) = (loop[i], loop[(i + 1) % loop.Count]);
            x += p.Y * q.Z - p.Z * q.Y;
            y += p.Z * q.X - p.X * q.Z;
            z += p.X * q.Y - p.Y * q.X;
        }

        return (x / 2, y / 2, z / 2);
    }

    /// <summary>The volume a closed shell of outward loops holds, by the divergence theorem.</summary>
    private static double Volume(IEnumerable<IReadOnlyList<Point3D>> shell) =>
        shell.Sum(loop =>
        {
            var area = AreaVector(loop);
            return (area.X * loop[0].X + area.Y * loop[0].Y + area.Z * loop[0].Z) / 3;
        });

    private static void Holds(XDocument file)
    {
        var ids = file.Descendants().Select(element => element.Attribute("id")?.Value).OfType<string>().ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.All(ids, id => Assert.False(char.IsDigit(id[0])));
        foreach (var reference in file.Descendants().SelectMany(element => element.Attributes()).Where(attribute => attribute.Name.LocalName.EndsWith("IdRef") || attribute.Name.LocalName is "programId" or "personId"))
            Assert.Contains(reference.Value, ids);
        Assert.DoesNotContain(file.Descendants(G + "Coordinate"), coordinate => coordinate.Value.Contains('E'));
    }

    [Fact]
    public void EachRoomIsASpaceItsSurfacesClosedRoundItAndTheWallBetweenTwoRoomsIsOneSurface()
    {
        var (document, rooms, partitions) = Row("Generic - 200mm", "Living Room", "Kitchen");
        var window = document.TypesOf<WindowType>().First();
        var south = document.Walls.First(wall => Math.Abs(wall.Start.Y - wall.End.Y) < 1 && wall.Start.Y < 0);
        document.Add(new BimWindow { TypeId = window.Id, LevelId = south.LevelId, HostWallId = south.Id, DistanceAlongWall = 2000 + 150, SillHeight = 900 });
        var file = GbXmlExport.Build(document);
        Holds(file);

        var root = file.Root!;
        Assert.Equal(("7.03", "Meters", "Centerline"), (root.Attribute("version")!.Value, root.Attribute("lengthUnit")!.Value, root.Attribute("SurfaceReferenceLocation")!.Value));
        var spaces = file.Descendants(G + "Space").ToList();
        Assert.Equal(2, spaces.Count);

        // Each closes: its loops, outward, sum to nothing, and hold its volume - the centre-line box, 3 m high.
        foreach (var space in spaces)
        {
            var shell = space.Descendants(G + "ClosedShell").Single().Elements(G + "PolyLoop").Select(loop => (IReadOnlyList<Point3D>)Points(loop)).ToList();
            var total = shell.Select(AreaVector).Aggregate((0.0, 0.0, 0.0), (sum, v) => (sum.Item1 + v.X, sum.Item2 + v.Y, sum.Item3 + v.Z));
            Assert.True(Math.Abs(total.Item1) + Math.Abs(total.Item2) + Math.Abs(total.Item3) < 1e-6);
            Assert.Equal(D(space.Element(G + "Volume")!), Volume(shell), 3);
        }

        // The partition between them: one interior wall, both spaces adjacent, at the partition's centre line.
        var x = (partitions[0].Start.X) / 1000;
        var shared = file.Descendants(G + "Surface").Where(surface => surface.Elements(G + "AdjacentSpaceId").Count() == 2).ToList();
        var wall = Assert.Single(shared, surface => surface.Attribute("surfaceType")!.Value == "InteriorWall");
        Assert.All(Points(wall.Descendants(G + "PolyLoop").First()), point => Assert.Equal(x, point.X, 3));
        Assert.Equal(4 + 1 + 1, file.Descendants(G + "Surface").Count(surface => surface.Attribute("surfaceType")!.Value == "ExteriorWall"));
        Assert.Equal(2, file.Descendants(G + "Surface").Count(surface => surface.Attribute("surfaceType")!.Value == "Roof"));
        Assert.Equal(2, file.Descendants(G + "Surface").Count(surface => surface.Attribute("surfaceType")!.Value == "SlabOnGrade"));

        // The window: an opening in the south wall, its window type with all three of what OpenStudio needs.
        var opening = Assert.Single(file.Descendants(G + "Opening"));
        Assert.Equal("OperableWindow", opening.Attribute("openingType")!.Value);
        var type = file.Descendants(G + "WindowType").Single(each => each.Attribute("id")!.Value == opening.Attribute("windowTypeIdRef")!.Value);
        Assert.NotNull(type.Element(G + "U-value"));
        Assert.NotNull(type.Element(G + "SolarHeatGainCoeff"));
        Assert.Equal("Visible", type.Element(G + "Transmittance")!.Attribute("type")!.Value);

        // The walls' constructions with their layers, outside first, and their U-value.
        var construction = file.Descendants(G + "Construction").Single(each => each.Attribute("id")!.Value == wall.Attribute("constructionIdRef")!.Value);
        Assert.NotEmpty(construction.Elements(G + "LayerId"));

        // Exterior walls face out: their normal away from their space's middle.
        foreach (var exterior in file.Descendants(G + "Surface").Where(surface => surface.Attribute("surfaceType")!.Value == "ExteriorWall"))
        {
            var loop = Points(exterior.Descendants(G + "PolyLoop").First());
            var normal = AreaVector(loop);
            var space = rooms.Single(room => file.Descendants(G + "Space").Single(each => each.Attribute("id")!.Value == exterior.Element(G + "AdjacentSpaceId")!.Attribute("spaceIdRef")!.Value)
                .Element(G + "CADObjectId")!.Value == room.Id.ToString());
            var (cx, cy) = (space.Location.X / 1000, space.Location.Y / 1000);
            Assert.True(normal.X * (loop[0].X - cx) + normal.Y * (loop[0].Y - cy) > 0);
        }
    }

    [Fact]
    public void TheGroundIsUnderTheLowestRoomAndARetainingWallIsUnderground()
    {
        // A foundation level under the ground floor: the ground floor's slab is still on the ground.
        var (document, rooms, _) = Row("Generic - 200mm", "Living Room");
        document.AddLevel(new BIMDesigner.Core.Datums.Level { Name = "Foundations", Elevation = -1000 });
        var file = GbXmlExport.Build(document);
        Holds(file);
        Assert.Contains(file.Descendants(G + "Surface"), surface => surface.Attribute("surfaceType")!.Value == "SlabOnGrade");
        Assert.DoesNotContain(file.Descendants(G + "Surface"), surface => surface.Attribute("surfaceType")!.Value == "ExposedFloor");

        // An outside wall that retains the ground: an underground wall, not one between rooms.
        var north = document.Walls.First(wall => Math.Abs(wall.Start.Y - wall.End.Y) < 1 && wall.Start.Y > 0);
        var retaining = document.TypesOf<WallType>().First(type => type.Function == WallFunction.Exterior).Duplicate("Retaining");
        retaining.Function = WallFunction.Retaining;
        document.AddType(retaining);
        north.TypeId = retaining.Id;
        file = GbXmlExport.Build(document);
        Assert.Contains(file.Descendants(G + "Surface"), surface => surface.Attribute("surfaceType")!.Value == "UndergroundWall");
        Assert.Equal(0, file.Descendants(G + "Surface").Count(surface => surface.Attribute("surfaceType")!.Value == "InteriorWall"));
        _ = rooms;
    }

    [Fact]
    public void AFloorBetweenTwoStoreysIsOneSurfaceTheUpperSpaceFirst()
    {
        var (document, rooms, _) = Row("Generic - 200mm", "Living Room");
        var first = document.Levels[1];
        var outside = document.Walls.ToList();
        foreach (var wall in outside)
            document.Add(new Wall { Start = wall.Start, End = wall.End, TypeId = wall.TypeId, LevelId = first.Id, UnconnectedHeight = 3000 });
        document.Add(new Room { LevelId = first.Id, Location = rooms[0].Location, Name = "Bedroom", Number = "2" });

        var file = GbXmlExport.Build(document);
        Holds(file);
        var floor = Assert.Single(file.Descendants(G + "Surface"), surface => surface.Attribute("surfaceType")!.Value == "InteriorFloor");
        var adjacent = floor.Elements(G + "AdjacentSpaceId").ToList();
        Assert.Equal(("InteriorFloor", "Ceiling"), (adjacent[0].Attribute("surfaceType")!.Value, adjacent[1].Attribute("surfaceType")!.Value));
        Assert.True(AreaVector(Points(floor.Descendants(G + "PolyLoop").First())).Z < 0);
        Assert.Equal(1, file.Descendants(G + "Surface").Count(surface => surface.Attribute("surfaceType")!.Value == "Roof"));

        foreach (var space in file.Descendants(G + "Space"))
        {
            var shell = space.Descendants(G + "ClosedShell").Single().Elements(G + "PolyLoop").Select(loop => (IReadOnlyList<Point3D>)Points(loop)).ToList();
            Assert.Equal(D(space.Element(G + "Volume")!), Volume(shell), 3);
        }
    }
}
