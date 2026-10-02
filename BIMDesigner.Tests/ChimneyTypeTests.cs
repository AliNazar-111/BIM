using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Infrastructure.Interoperability;
using BIMDesigner.Infrastructure.Serialization;
using Xbim.Ifc4.PropertyResource;
using Xbim.Ifc4.SharedBldgElements;

namespace BIMDesigner.Tests;

/// <summary>
/// Chimney types - a brick stack, a block system chimney, twin-wall and air-cooled steel flues, a
/// gas vent, a concentric flue, steel, guyed and industrial stacks, a solar chimney, a framed
/// chase - and a fireplace that opens into the room through the front of its breast.
/// </summary>
public class ChimneyTypeTests
{
    private static ChimneyType Type(BimDocument document, ChimneyConstruction construction) =>
        document.TypesOf<ChimneyType>().First(type => type.Construction == construction);

    private static Chimney Put(BimDocument document, ChimneyType type, double x, double y, ChimneyFireplace? fireplace = null)
    {
        var chimney = new Chimney
        {
            LevelId = document.Levels[0].Id, TypeId = type.Id, Location = new Point2D(x, y),
            Width = type.Width, Depth = type.IsRound ? type.Width : type.Depth, Flues = type.Flues,
            Fireplace = fireplace ?? type.Fireplace
        };
        document.Add(chimney);
        return chimney;
    }

    /// <summary>Whether a mesh has a level triangle at a height over a point, seen from above.</summary>
    private static bool LevelAt(Mesh3D mesh, Point2D point, double z)
    {
        for (var i = 0; i + 2 < mesh.Indices.Count; i += 3)
        {
            var (a, b, c) = (mesh.Positions[mesh.Indices[i]], mesh.Positions[mesh.Indices[i + 1]], mesh.Positions[mesh.Indices[i + 2]]);
            if (new[] { a.Z, b.Z, c.Z }.Any(h => Math.Abs(h - z) > 1)) continue;
            var triangle = new[] { new Point2D(a.X, a.Y), new Point2D(b.X, b.Y), new Point2D(c.X, c.Y) };
            if (Math.Abs(Polygon2D.SignedArea(triangle)) > 1 && Polygon2D.Contains(triangle, point)) return true;
        }

        return false;
    }

    /// <summary>Whether a point in space lies on one of a mesh's faces.</summary>
    private static bool OnFace(Mesh3D mesh, Point3D point)
    {
        for (var i = 0; i + 2 < mesh.Indices.Count; i += 3)
        {
            var (a, b, c) = (mesh.Positions[mesh.Indices[i]], mesh.Positions[mesh.Indices[i + 1]], mesh.Positions[mesh.Indices[i + 2]]);
            var (u, v, w) = (Sub(b, a), Sub(c, a), Sub(point, a));
            var normal = Cross(u, v);
            var area = Math.Sqrt(Dot(normal, normal));
            if (area < 1e-6 || Math.Abs(Dot(normal, w)) / area > 1) continue;

            // Barycentric: inside the triangle.
            var (uu, uv, vv, wu, wv) = (Dot(u, u), Dot(u, v), Dot(v, v), Dot(w, u), Dot(w, v));
            var d = uv * uv - uu * vv;
            var s = (uv * wv - vv * wu) / d;
            var t = (uv * wu - uu * wv) / d;
            if (s >= -1e-6 && t >= -1e-6 && s + t <= 1 + 1e-6) return true;
        }

        return false;

        static (double, double, double) Sub(Point3D p, Point3D q) => (p.X - q.X, p.Y - q.Y, p.Z - q.Z);
        static double Dot((double X, double Y, double Z) p, (double X, double Y, double Z) q) => p.X * q.X + p.Y * q.Y + p.Z * q.Z;
        static (double, double, double) Cross((double X, double Y, double Z) p, (double X, double Y, double Z) q) =>
            (p.Y * q.Z - p.Z * q.Y, p.Z * q.X - p.X * q.Z, p.X * q.Y - p.Y * q.X);
    }

    [Fact]
    public void ItsFireplaceOpensIntoTheRoomThroughTheFrontOfTheBreastNotUnderneath()
    {
        var (document, _, wallType) = DormerToolTests.House(10000, 8000, 40);
        var chimney = Put(document, Type(document, ChimneyConstruction.Masonry), 3000, 600, ChimneyFireplace.Open);
        Chimneys.FitToWalls(document, chimney);

        // Put by the wall along y = 0, its back goes on the wall's inside face and its front to the room.
        var front = Chimneys.Front(chimney);
        Assert.Equal(1, front.Y, 6);
        Assert.Equal(wallType.Structure.TotalWidth / 2 + chimney.Depth / 2, chimney.Location.Y, 6);

        var breast = Chimneys.Breast(document, chimney)!;
        var foot = Chimneys.Foot(document, chimney);
        var face = breast.Outline.Max(point => point.Y);
        Assert.InRange(breast.Opening.Max(point => point.Y), face, face + 2);
        Assert.Equal(breast.Outline.Min(point => point.Y), chimney.Location.Y - chimney.Depth / 2, 6);

        var meshes = Chimneys.Meshes(document, chimney);
        var stack = meshes[0];
        Mesh3D Part(string name) => meshes.Single(mesh => mesh.Description == name);

        // Seen from the room, the front of the breast is open where the fireplace is, and brick beside it.
        var middle = (breast.Opening.Min(point => point.X) + breast.Opening.Max(point => point.X)) / 2;
        Assert.False(OnFace(stack, new Point3D(middle, face, foot + breast.OpeningHeight / 2)), "The fireplace is bricked up.");
        Assert.True(OnFace(stack, new Point3D(breast.Outline.Min(point => point.X) + 100, face, foot + breast.OpeningHeight / 2)));
        Assert.True(OnFace(stack, new Point3D(middle, face, foot + breast.OpeningHeight + 200)), "Nothing over the fireplace.");

        // Underneath, it is solid all over but for the fireplace - whose top is the breast over it.
        var (minX, maxX) = (breast.Outline.Min(point => point.X), breast.Outline.Max(point => point.X));
        var (minY, maxY) = (breast.Outline.Min(point => point.Y), face);
        for (var x = minX + 20; x < maxX; x += 97)
        for (var y = minY + 20; y < maxY; y += 41)
        {
            var at = new Point2D(x, y);
            var inOpening = Polygon2D.Contains(breast.Opening, at);
            Assert.True(LevelAt(stack, at, inOpening ? foot + breast.OpeningHeight : foot), $"Open underneath at {x:0}, {y:0}.");
        }

        // The flue opens above the breast, down from the top.
        foreach (var flue in Chimneys.Flues(document, chimney))
            Assert.False(ChimneyAndDrainPipeTests.Covers(stack, flue, breast.Top + 1), "The flue is blocked.");

        // A fire on its grate, a hearth before it, a mantel beam over it, blackened inside.
        Assert.InRange(Part("Fire").Positions.Max(point => point.Y), minY, face);
        Assert.Equal(face + 500, Part("Hearth").Positions.Max(point => point.Y), 1);
        Assert.InRange(Part("Mantel Beam").Positions.Min(point => point.Z), foot + breast.OpeningHeight, foot + breast.OpeningHeight + 100);
        Assert.NotEmpty(Part("Firebox").Positions);
    }

    [Fact]
    public void WithNoFireplaceItsFluesStopAboveASolidFoot()
    {
        var (document, _, _) = DormerToolTests.House(10000, 8000, 40);
        var chimney = Put(document, Type(document, ChimneyConstruction.Masonry), 3000, 2000, ChimneyFireplace.None);
        Assert.Null(Chimneys.Breast(document, chimney));

        var stack = Chimneys.Meshes(document, chimney)[0];
        var foot = Chimneys.Foot(document, chimney);
        Assert.True(LevelAt(stack, chimney.Location, foot), "Open underneath.");
        Assert.True(LevelAt(stack, chimney.Location, foot + Chimneys.SolidFoot));
        Assert.False(ChimneyAndDrainPipeTests.Covers(stack, chimney.Location, foot + Chimneys.SolidFoot + 1));
    }

    [Fact]
    public void AStoveStandsInTheFireplaceOrAtTheFootOfAMetalFlue()
    {
        var (document, _, _) = DormerToolTests.House(10000, 8000, 40);
        var brick = Put(document, Type(document, ChimneyConstruction.Masonry), 3000, 2000, ChimneyFireplace.Stove);
        var breast = Chimneys.Breast(document, brick)!;
        var stove = Chimneys.Meshes(document, brick).Single(mesh => mesh.Description == "Stove");
        Assert.All(stove.Positions, point => Assert.True(Polygon2D.Contains(breast.Opening, new Point2D(point.X, point.Y)) ||
                                                         Polygon2D.Contains(breast.Hearth, new Point2D(point.X, point.Y))));

        var steel = Put(document, Type(document, ChimneyConstruction.TwinWallSteel), 7000, 4000);
        Assert.Equal(ChimneyFireplace.Stove, steel.Fireplace);
        var meshes = Chimneys.Meshes(document, steel);
        var foot = Chimneys.Foot(document, steel);
        Assert.Equal(foot + 12, meshes.Single(mesh => mesh.Description == "Stove").Positions.Min(point => point.Z), 6);

        // The flue rises from the stove's top, hollow, and is capped above its top against the rain.
        var pipe = meshes.Single(mesh => mesh.Description == "Twin Wall Steel");
        Assert.Equal(foot + 650, pipe.Positions.Min(point => point.Z), 6);
        var top = Chimneys.Top(document, steel);
        var heights = pipe.Positions.Select(point => point.Z).Where(z => z > foot + 651 && z < top - 1).Distinct();
        Assert.DoesNotContain(heights, z => LevelAt(pipe, steel.Location, z));
        Assert.True(pipe.Positions.Max(point => point.Z) > top + 100);
        Assert.True(ChimneyAndDrainPipeTests.Covers(pipe, steel.Location, top + 100), "No rain cap.");
    }

    [Theory]
    [InlineData(ChimneyConstruction.Masonry)]
    [InlineData(ChimneyConstruction.PrecastBlock)]
    [InlineData(ChimneyConstruction.TwinWallSteel)]
    [InlineData(ChimneyConstruction.AirCooledMetal)]
    [InlineData(ChimneyConstruction.TypeBVent)]
    [InlineData(ChimneyConstruction.ConcentricFlue)]
    [InlineData(ChimneyConstruction.SteelStack)]
    [InlineData(ChimneyConstruction.GuyedStack)]
    [InlineData(ChimneyConstruction.IndustrialMultiFlue)]
    [InlineData(ChimneyConstruction.SolarChimney)]
    [InlineData(ChimneyConstruction.FramedChase)]
    public void EachTypeIsBuiltAndCutsItsHoleKeepingItsClearance(ChimneyConstruction construction)
    {
        var (document, roof, _) = DormerToolTests.House(30000, 20000, 30);
        var type = Type(document, construction);
        var chimney = Put(document, type, 15000, 5000);

        var meshes = Chimneys.Meshes(document, chimney);
        Assert.NotEmpty(meshes);
        Assert.All(meshes.SelectMany(mesh => mesh.Positions), point => Assert.True(double.IsFinite(point.X + point.Y + point.Z)));

        var (foot, top) = (Chimneys.Foot(document, chimney), Chimneys.Top(document, chimney));
        Assert.True(top - foot >= type.MinimumHeight - 1e-6);
        Assert.True(top >= Chimneys.HighestContact(document, chimney)!.Value + Chimneys.AboveContact - 1e-6);
        Assert.Equal(type.IsRound ? 24 : 4, Chimneys.Outline(document, chimney).Count);

        // Its hole through the roof is opened up by its clearance to combustibles.
        var hole = Assert.Single(Chimneys.Holes(document, roof));
        Assert.Equal(Polygon2D.Area(Chimneys.Grown(document, chimney, type.ClearanceToCombustibles)), Polygon2D.Area(hole), 6);
        Assert.Equal(type.ClearanceToCombustibles > 0, Polygon2D.Area(hole) > Polygon2D.Area(Chimneys.Outline(document, chimney)) + 1);
    }

    [Fact]
    public void ItsTypeCarriesItsEn1443Designation()
    {
        var document = BimDocument.CreateDefault();
        Assert.Equal("EN 1443 T600 N1 D 3 G40", Type(document, ChimneyConstruction.Masonry).Designation);
        Assert.Equal("EN 1443 T250 N1 D 1 O25", Type(document, ChimneyConstruction.TypeBVent).Designation);
        Assert.Equal("EN 1443 T120 P1 W 1 O0", Type(document, ChimneyConstruction.ConcentricFlue).Designation);
        Assert.Equal("Ventilation only", Type(document, ChimneyConstruction.SolarChimney).Designation);
        Assert.Equal(11, document.TypesOf<ChimneyType>().Count());
    }

    [Fact]
    public void AnIndustrialStackStandsToItsOwnHeightItsFluesOutOfTheTop()
    {
        var (document, _, _) = DormerToolTests.House(30000, 20000, 30);
        var type = Type(document, ChimneyConstruction.IndustrialMultiFlue);
        var chimney = Put(document, type, 15000, 5000);

        var (foot, top) = (Chimneys.Foot(document, chimney), Chimneys.Top(document, chimney));
        Assert.True(top - foot >= 30000 - 1e-6);

        var flues = Chimneys.Flues(document, chimney);
        Assert.Equal(3, flues.Count);
        Assert.All(flues, flue => Assert.True(flue.DistanceTo(chimney.Location) + type.FlueDiameter / 2 < chimney.Width / 2 - 300));
        var steel = Chimneys.Meshes(document, chimney).Single(mesh => mesh.Description == "Steel Flue");
        Assert.Equal(top + 1500, steel.Positions.Max(point => point.Z), 6);
    }

    [Fact]
    public void AGuyedStackIsHeldByThreeWiresToAnchorsInTheGround()
    {
        var (document, _, _) = DormerToolTests.House(30000, 20000, 30);
        var chimney = Put(document, Type(document, ChimneyConstruction.GuyedStack), 15000, 5000);
        var (foot, top) = (Chimneys.Foot(document, chimney), Chimneys.Top(document, chimney));

        var anchors = Chimneys.Anchors(document, chimney);
        Assert.Equal(3, anchors.Count);
        Assert.All(anchors, anchor => Assert.True(anchor.DistanceTo(chimney.Location) >= (top - foot) * 0.45 - 1e-6));

        var wires = Chimneys.Meshes(document, chimney).Single(mesh => mesh.Description == "Guy Wires");
        Assert.InRange(wires.Positions.Min(point => point.Z), foot + 200 - 15, foot + 200 + 15);
        Assert.All(anchors, anchor => Assert.Contains(wires.Positions, point => new Point2D(point.X, point.Y).DistanceTo(anchor) < 20));
    }

    [Fact]
    public void GivenAnotherTypeItTakesItsSizeAndUndoPutsItBack()
    {
        var (document, _, _) = DormerToolTests.House(10000, 8000, 40);
        var chimney = Put(document, Type(document, ChimneyConstruction.Masonry), 3000, 2000, ChimneyFireplace.Open);
        var steel = Type(document, ChimneyConstruction.TwinWallSteel);

        var command = new SetElementsTypeCommand(new[] { chimney }, steel);
        command.Redo();
        Assert.Equal(steel.Id, chimney.TypeId);
        Assert.Equal(200, chimney.Width);
        Assert.Equal(ChimneyFireplace.Stove, chimney.Fireplace);
        Assert.Equal(24, Chimneys.Outline(document, chimney).Count);

        command.Undo();
        Assert.Equal(450, chimney.Width);
        Assert.Equal(ChimneyFireplace.Open, chimney.Fireplace);
    }

    [Fact]
    public void ItsTypeAndFireplaceAreSavedCopiedAndExported()
    {
        var (document, _, _) = DormerToolTests.House(10000, 8000, 40);
        var type = Type(document, ChimneyConstruction.PrecastBlock);
        type.ClearanceToCombustibles = 75;
        var chimney = Put(document, type, 3000, 2000, ChimneyFireplace.Stove);

        var copy = ElementCopy.Duplicate(document, new[] { chimney }).OfType<Chimney>().Single();
        Assert.Equal(type.Id, copy.TypeId);
        Assert.Equal(ChimneyFireplace.Stove, copy.Fireplace);

        var loaded = ProjectFile.FromJson(ProjectFile.ToJson(document));
        var again = loaded.Elements.OfType<Chimney>().Single();
        Assert.Equal(ChimneyFireplace.Stove, again.Fireplace);
        var againType = Chimneys.TypeOf(loaded, again);
        Assert.Equal(type.Name, againType.Name);
        Assert.Equal(75, againType.ClearanceToCombustibles);
        Assert.Equal(type.Designation, againType.Designation);
        Assert.Equal(11, loaded.TypesOf<ChimneyType>().Count());

        using var model = IfcExport.Build(document);
        var product = Assert.Single(model.Instances.OfType<IfcChimney>());
        Assert.Equal(type.Name, product.Name?.ToString());
        Assert.Contains(model.Instances.OfType<IfcPropertySingleValue>(), p => p.Name == "Designation" && p.NominalValue?.ToString() == type.Designation);
        Assert.Contains(model.Instances.OfType<IfcPropertySingleValue>(), p => p.Name == "NumberOfDrafts");
    }
}
