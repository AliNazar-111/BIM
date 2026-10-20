using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;
using BIMDesigner.Infrastructure.Interoperability;

namespace BIMDesigner.Tests;

/// <summary>IFC read back into a project: what was written comes in as itself, where it is, the size it was.</summary>
public class IfcImportTests
{
    /// <summary>A small house: four outside walls, a partition, a curved wall, a door and a window, a floor, a ceiling, a flat roof, a room, a gridline and a chair.</summary>
    internal static BimDocument House()
    {
        var document = BimDocument.CreateDefault();
        var (ground, first) = (document.Levels[0], document.Levels[1]);
        var exterior = document.TypesOf<WallType>().First(t => t.Function == WallFunction.Exterior);
        var interior = document.TypesOf<WallType>().First(t => t.Function == WallFunction.Interior);
        var corners = new[] { new Point2D(0, 0), new Point2D(8000, 0), new Point2D(8000, 6000), new Point2D(0, 6000) };
        for (var i = 0; i < 4; i++)
            document.Add(new Wall { Start = corners[i], End = corners[(i + 1) % 4], TypeId = exterior.Id, LevelId = ground.Id, UnconnectedHeight = 3000, Mark = $"W{i + 1}" });
        document.Add(new Wall { Start = new Point2D(4000, 0), End = new Point2D(4000, 6000), TypeId = interior.Id, LevelId = ground.Id, UnconnectedHeight = 2700, BaseOffset = 50, Flipped = true });
        document.Add(new Wall { Start = new Point2D(8000, 1000), End = new Point2D(8000, 5000), Bulge = -0.4, TypeId = interior.Id, LevelId = ground.Id, UnconnectedHeight = 3000 });

        var front = document.Walls.First();
        document.Add(new Door { HostWallId = front.Id, TypeId = document.TypesOf<DoorType>().First().Id, DistanceAlongWall = 2000, LevelId = ground.Id, Mark = "D1" });
        document.Add(new Window { HostWallId = front.Id, TypeId = document.TypesOf<WindowType>().First().Id, DistanceAlongWall = 6000, SillHeight = 900, LevelId = ground.Id });

        var floor = new Floor { TypeId = document.TypesOf<FloorType>().First().Id, LevelId = ground.Id };
        floor.SetBoundary(corners);
        document.Add(floor);
        var ceiling = new Ceiling { TypeId = document.TypesOf<CeilingType>().First().Id, LevelId = ground.Id, HeightOffset = 2400 };
        ceiling.SetBoundary(new[] { new Point2D(200, 200), new Point2D(3800, 200), new Point2D(3800, 5800), new Point2D(200, 5800) });
        document.Add(ceiling);
        var roof = new Roof { TypeId = document.TypesOf<RoofType>().First().Id, LevelId = first.Id, HeightOffset = 0 };
        roof.SetBoundary(corners);
        roof.SetEdges(corners.Select(_ => new RoofEdge()));
        document.Add(roof);

        document.Add(new Room { Location = new Point2D(2000, 3000), LevelId = ground.Id, Name = "Kitchen", Number = "G01" });
        document.Add(new Grid { Name = "A", Start = new Point2D(-1000, -1000), End = new Point2D(-1000, 7000), LevelId = ground.Id });

        var chair = document.TypesOf<ComponentType>().First(t => t.Kind == BuiltInCategory.Furniture && t.Form == ComponentForm.Chair);
        document.Add(new Component { TypeId = chair.Id, LevelId = ground.Id, Location = new Point2D(2000, 2000), Rotation = 30 });
        return document;
    }

    private static IfcImportResult RoundTrip(BimDocument document)
    {
        using var model = IfcExport.Build(document);
        return IfcImport.Read(model, "House");
    }

    [Fact]
    public void StoreysWallsDoorsAndWindowsComeBackAsThemselves()
    {
        var document = House();
        var result = RoundTrip(document);
        var back = result.Document;
        Assert.Equal("IFC4", result.Schema);

        Assert.Equal(document.Levels.Select(l => (l.Name, l.Elevation)), back.Levels.Select(l => (l.Name, l.Elevation)));
        Assert.Equal(6, result["Walls"]);

        foreach (var wall in document.Walls)
        {
            var type = document.GetWallType(wall)!;
            var match = back.Walls.SingleOrDefault(w => w.Start.DistanceTo(wall.Start) < 1 && w.End.DistanceTo(wall.End) < 1);
            Assert.True(match is not null, $"No wall from {wall.Start} to {wall.End}");
            var matchType = back.GetWallType(match!)!;
            Assert.Equal(type.Name, matchType.Name);
            Assert.Equal(type.Structure.TotalWidth, matchType.Structure.TotalWidth, 3);
            Assert.Equal(type.Structure.Layers.Select(l => (l.Function, l.Thickness)), matchType.Structure.Layers.Select(l => (l.Function, l.Thickness)));
            Assert.Equal(wall.GetHeight(document), match!.GetHeight(back), 3);
            Assert.Equal(wall.BaseOffset, match.BaseOffset, 3);
            Assert.Equal(wall.Flipped, match.Flipped);
            Assert.Equal(wall.Bulge, match.Bulge, 2);
            Assert.Equal(back.Levels.Single(l => l.Name == document.FindLevel(wall.LevelId)!.Name).Id, match.LevelId);
        }

        Assert.Equal("W1", back.Walls.Single(w => w.Start.DistanceTo(new Point2D(0, 0)) < 1).Mark);

        // The door and window in the front wall, where they were and the size they were.
        var frontBack = back.Walls.Single(w => w.Start.DistanceTo(new Point2D(0, 0)) < 1);
        var door = Assert.Single(back.Elements.OfType<Door>());
        var window = Assert.Single(back.Elements.OfType<Window>());
        Assert.Equal(frontBack.Id, door.HostWallId);
        Assert.Equal(2000, door.DistanceAlongWall, 1);
        Assert.Equal(6000, window.DistanceAlongWall, 1);
        Assert.Equal(0, door.SillHeight, 1);
        Assert.Equal(900, window.SillHeight, 1);
        var (doorType, windowType) = (document.TypesOf<DoorType>().First(), document.TypesOf<WindowType>().First());
        Assert.Equal((doorType.Name, doorType.Width, doorType.Height), (back.FindType<DoorType>(door.TypeId)!.Name, back.FindType<DoorType>(door.TypeId)!.Width, back.FindType<DoorType>(door.TypeId)!.Height));
        Assert.Equal((windowType.Width, windowType.Height), (back.FindType<WindowType>(window.TypeId)!.Width, back.FindType<WindowType>(window.TypeId)!.Height));
        Assert.Equal("D1", door.Mark);
    }

    [Fact]
    public void FloorsCeilingsRoofsRoomsGridsAndFurnitureComeBack()
    {
        var document = House();
        var back = RoundTrip(document).Document;

        var floor = Assert.Single(back.Elements.OfType<Floor>());
        var original = document.Elements.OfType<Floor>().Single();
        Assert.Equal(Polygon2D.Area(original.Boundary), Polygon2D.Area(floor.Boundary), 1);
        Assert.Equal(original.HeightOffset, floor.HeightOffset, 3);
        Assert.Equal(document.FindType<FloorType>(original.TypeId)!.Name, back.FindType<FloorType>(floor.TypeId)!.Name);

        var ceiling = Assert.Single(back.Elements.OfType<Ceiling>());
        Assert.Equal(2400, ceiling.HeightOffset, 3);
        Assert.Equal(3600.0 * 5600, Polygon2D.Area(ceiling.Boundary), 1);

        var roof = Assert.Single(back.Elements.OfType<Roof>());
        Assert.Equal(back.Levels[1].Id, roof.LevelId);
        Assert.Equal(0, roof.HeightOffset, 3);
        Assert.Equal(RoofForm.Flat, roof.Form);

        var room = Assert.Single(back.Elements.OfType<Room>());
        Assert.Equal(("Kitchen", "G01"), (room.Name, room.Number));

        var grid = Assert.Single(back.Elements.OfType<Grid>());
        Assert.Equal("A", grid.Name);
        Assert.True(grid.Start.DistanceTo(new Point2D(-1000, -1000)) < 1 && grid.End.DistanceTo(new Point2D(-1000, 7000)) < 1);

        // The chair as a component with its shape, where it stood, turned as it was.
        var chair = Assert.Single(back.Elements.OfType<Component>());
        var chairType = back.FindType<ComponentType>(chair.TypeId)!;
        Assert.Equal(BuiltInCategory.Furniture, chairType.Kind);
        Assert.NotNull(chairType.Mesh);
        Assert.Equal(30, chair.Rotation, 3);

        // Its shape in 3D where the original's was, give or take a millimetre.
        Box3D Bounds(BimDocument from, Guid id)
        {
            var points = ModelMeshBuilder.Build(from).Where(m => m.OwnerId == id).SelectMany(m => m.Positions).ToList();
            return new Box3D(new Point3D(points.Min(p => p.X), points.Min(p => p.Y), points.Min(p => p.Z)), new Point3D(points.Max(p => p.X), points.Max(p => p.Y), points.Max(p => p.Z)));
        }

        var (before, after) = (Bounds(document, document.Elements.OfType<Component>().Single().Id), Bounds(back, chair.Id));
        Assert.Equal(before.Min.X, after.Min.X, 0);
        Assert.Equal(before.Min.Y, after.Min.Y, 0);
        Assert.Equal(before.Max.X, after.Max.X, 0);
        Assert.Equal(before.Max.Z, after.Max.Z, 0);
    }

    [Fact]
    public void AFileIsReadFromDiskAndAFarAwayModelBroughtToTheOrigin()
    {
        var document = House();
        foreach (var element in document.Elements.ToList()) ElementTransforms.Move(element, new Vector2D(530_000_000, 180_000_000));
        var path = Path.Combine(Path.GetTempPath(), $"house-{Guid.NewGuid():N}.ifc");
        try
        {
            IfcExport.Save(document, path);
            var result = IfcImport.Open(path);
            Assert.Equal(6, result["Walls"]);
            Assert.Contains(result.Notes, note => note.Contains("brought to the origin"));
            var walls = result.Document.Walls.ToList();
            Assert.InRange(walls.Min(w => Math.Min(w.Start.X, w.End.X)), -1000, 1000);
            Assert.InRange(walls.Max(w => Math.Max(w.Start.Y, w.End.Y)), 5000, 7000);
            Assert.Contains("walls", result.Summary());
        }
        finally { File.Delete(path); }
    }
}

/// <summary>The model as IFC2x3, for older software - and read back from it.</summary>
public class Ifc2x3ExportTests
{
    [Fact]
    public void AnIfc2x3FileCarriesTheHouseAndReadsBack()
    {
        var document = IfcImportTests.House();
        var path = Path.Combine(Path.GetTempPath(), $"house-{Guid.NewGuid():N}.ifc");
        try
        {
            Ifc2x3Export.Save(document, path);
            var text = File.ReadAllText(path);
            Assert.Contains("FILE_SCHEMA", text);
            Assert.Contains("IFC2X3", text.Substring(text.IndexOf("FILE_SCHEMA", StringComparison.Ordinal), 40));
            Assert.Contains("CoordinationView_V2.0", text);
            Assert.Contains("IFCWALLSTANDARDCASE", text);
            Assert.Contains("IFCFACETEDBREP", text);
            Assert.DoesNotContain("IFCTRIANGULATEDFACESET", text);

            var result = IfcImport.Open(path);
            Assert.Equal("IFC2X3", result.Schema);
            var back = result.Document;
            Assert.Equal(document.Levels.Select(l => l.Name), back.Levels.Select(l => l.Name));
            Assert.Equal(6, result["Walls"]);
            foreach (var wall in document.Walls)
                Assert.Contains(back.Walls, w => w.Start.DistanceTo(wall.Start) < 1 && w.End.DistanceTo(wall.End) < 1 && Math.Abs(w.GetHeight(back) - wall.GetHeight(document)) < 0.01);
            Assert.Equal(2000, Assert.Single(back.Elements.OfType<Door>()).DistanceAlongWall, 1);
            Assert.Equal(900, Assert.Single(back.Elements.OfType<Window>()).SillHeight, 1);
            Assert.Single(back.Elements.OfType<Floor>());
            Assert.Single(back.Elements.OfType<Ceiling>());
            Assert.Equal("Kitchen", Assert.Single(back.Elements.OfType<Room>()).Name);
            Assert.Equal("A", Assert.Single(back.Elements.OfType<Grid>()).Name);

            // The roof and the chair as the shapes they are: a roof, and a piece of furniture.
            var shapes = back.Elements.OfType<Component>().Select(c => back.FindType<ComponentType>(c.TypeId)!).ToList();
            Assert.Contains(shapes, type => type.Kind == BuiltInCategory.Furniture && type.Mesh is not null);
            Assert.Contains(shapes, type => type.Name.StartsWith("Roof"));
        }
        finally { File.Delete(path); }
    }
}
