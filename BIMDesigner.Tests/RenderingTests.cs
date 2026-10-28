using System.Numerics;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;
using BIMDesigner.Core.Rendering;
using BIMDesigner.Core.Views;
using BIMDesigner.Infrastructure.Serialization;

namespace BIMDesigner.Tests;

/// <summary>
/// Materials' faces and renderings: a pattern guessed from a material's name, the patterns drawn at
/// their true size, how they lie on a wall and up a roof, where the sun is, rays finding what they
/// meet, shadows cast and seen through glass, and all of it kept in the project.
/// </summary>
public class RenderingTests
{
    [Theory]
    [InlineData("Brick, Common", SurfacePattern.Brick)]
    [InlineData("Concrete Block", SurfacePattern.Block)]
    [InlineData("Aircrete Block", SurfacePattern.Block)]
    [InlineData("Roof Slate, Blue-Grey", SurfacePattern.Slate)]
    [InlineData("Roof Tile, Clay Plain", SurfacePattern.PlainTiles)]
    [InlineData("Roof Tile, Concrete Interlocking", SurfacePattern.InterlockingTiles)]
    [InlineData("Timber, Western Red Cedar", SurfacePattern.Boards)]
    [InlineData("Composite, Wood-Plastic Grey", SurfacePattern.Boards)]
    [InlineData("Timber Fascia, Painted White", SurfacePattern.Render)]
    [InlineData("Cement Plaster", SurfacePattern.Render)]
    [InlineData("Ceramic Tile, White Gloss", SurfacePattern.Tiles)]
    [InlineData("Zinc, Pre-Weathered", SurfacePattern.StandingSeam)]
    [InlineData("Aluminium, Anodised", SurfacePattern.Metal)]
    [InlineData("Concrete, Cast In Situ", SurfacePattern.Concrete)]
    [InlineData("Glass, Clear", SurfacePattern.None)]
    [InlineData("Rigid Insulation", SurfacePattern.None)]
    [InlineData("Lawn", SurfacePattern.Grass)]
    [InlineData("Gravel", SurfacePattern.Gravel)]
    public void AMaterialsPatternIsGuessedFromItsName(string name, SurfacePattern pattern)
    {
        Assert.Equal(pattern, SurfacePatterns.Guess(name, new ColourRgb(0x80, 0x80, 0x80)).Pattern);
    }

    [Fact]
    public void ANewProjectsMaterialsAreLaidAsTheirNamesSay()
    {
        var document = BimDocument.CreateDefault();
        var brick = document.Materials.Single(material => material.Name == "Brick, Common");
        Assert.Equal((SurfacePattern.Brick, 215.0, 65.0, 10.0), (brick.Pattern, brick.UnitLength, brick.UnitHeight, brick.JointWidth));
        Assert.Equal(SurfacePattern.Slate, document.Materials.Single(material => material.Name == "Roof Slate, Blue-Grey").Pattern);
        Assert.True(document.Materials.Single(material => material.Name == "Aluminium, Anodised").Reflectance > 0.1);
    }

    [Fact]
    public void BrickIsDrawnInStretcherBondAtItsTrueSize()
    {
        var finish = SurfacePatterns.Standard(SurfacePattern.Brick, ColourRgb.FromHex("A8563C"));
        var texture = SurfaceTextures.Of(finish)!;

        // Four bricks and their joints across, six courses up; drawn once and kept.
        Assert.Equal((900.0, 450.0), (texture.Length, texture.Rise));
        Assert.Same(texture, SurfaceTextures.Of(finish));
        Assert.Null(SurfaceTextures.Of(SurfacePatterns.Standard(SurfacePattern.None, finish.Colour)));

        // The top of the picture is the top of a course: its bed joint in mortar, then brick.
        static int Distance(ColourRgb a, ColourRgb b) => Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);
        var mortar = finish.JointColour;
        Assert.True(Distance(texture.At(100, 450 - 2), mortar) < 40);
        Assert.True(Distance(texture.At(110, 450 - 37), finish.Colour) < 90);
        Assert.True(Distance(texture.At(110, 450 - 37), mortar) > 150);

        // The perpends: under a perpend of one course, a brick's middle in the next.
        Assert.True(Distance(texture.At(225, 450 - 37), mortar) < 40);
        Assert.True(Distance(texture.At(225, 450 - 75 - 37), mortar) > 150);

        // Joints sunk, the brick proud; the same everywhere it repeats.
        Assert.True(texture.ReliefAt(110, 450 - 37) > texture.ReliefAt(100, 450 - 2));
        Assert.Equal(texture.At(110, 413), texture.At(110 + 900 * 3, 413 - 450 * 2));
    }

    [Fact]
    public void EveryPatternIsDrawn()
    {
        foreach (var pattern in Enum.GetValues<SurfacePattern>().Where(pattern => pattern != SurfacePattern.None))
        {
            var texture = SurfaceTextures.Of(SurfacePatterns.Standard(pattern, ColourRgb.FromHex("8A7A6A")))!;
            Assert.True(texture.Width is >= 8 and <= 2048 && texture.Height is >= 8 and <= 2048, $"{pattern}: {texture.Width} x {texture.Height}");
            Assert.Equal(texture.Width * texture.Height * 3, texture.Pixels.Length);
            Assert.True(texture.Pixels.Distinct().Count() > 4, $"{pattern} is one colour");
        }
    }

    [Fact]
    public void APatternLiesLevelAlongAWallAndUpARoofsSlope()
    {
        // A wall facing east: along it northwards, up it upwards.
        var (along, up) = TextureMapping.Basis(1, 0, 0);
        Assert.Equal((0.0, 1.0, 0.0), along);
        Assert.Equal((0.0, 0.0, 1.0), up);
        Assert.Equal((2000.0, 1500.0), TextureMapping.Of(5000, 2000, 1500, 1, 0, 0));

        // A roof facing south at 45°: along the eaves, up the slope.
        var (eaves, slope) = TextureMapping.Basis(0, -Math.Sqrt(0.5), Math.Sqrt(0.5));
        Assert.Equal(1, eaves.X, 9);
        Assert.Equal((Math.Sqrt(0.5), Math.Sqrt(0.5)), (Math.Round(slope.Y, 9), Math.Round(slope.Z, 9)), new Tolerance());

        // A floor: along the plan's x and y.
        Assert.Equal((300.0, 400.0), TextureMapping.Of(300, 400, 2700, 0, 0, 1));
    }

    private sealed class Tolerance : IEqualityComparer<(double, double)>
    {
        public bool Equals((double, double) a, (double, double) b) => Math.Abs(a.Item1 - b.Item1) < 1e-6 && Math.Abs(a.Item2 - b.Item2) < 1e-6;
        public int GetHashCode((double, double) obj) => 0;
    }

    [Fact]
    public void TheSunIsWhereTheAlmanacPutsIt()
    {
        // London at midsummer noon: 90 - 51.5 + 23.44 = 61.9° up, due south; at midwinter 15.1°.
        var (summer, south) = SunPosition.Of(51.5, -0.13, new DateTime(2026, 6, 21, 12, 2, 0, DateTimeKind.Utc));
        Assert.InRange(summer, 61.6, 62.1);
        Assert.InRange(south, 177, 183);
        var (winter, _) = SunPosition.Of(51.5, -0.13, new DateTime(2026, 12, 21, 12, 0, 0, DateTimeKind.Utc));
        Assert.InRange(winter, 14.8, 15.3);

        // Morning sun in the east, evening in the west; down at midnight.
        Assert.InRange(SunPosition.Of(51.5, 0, new DateTime(2026, 3, 20, 6, 30, 0, DateTimeKind.Utc)).Azimuth, 80, 100);
        Assert.InRange(SunPosition.Of(51.5, 0, new DateTime(2026, 3, 20, 17, 30, 0, DateTimeKind.Utc)).Azimuth, 260, 280);
        Assert.True(SunPosition.Of(51.5, 0, new DateTime(2026, 6, 21, 0, 0, 0, DateTimeKind.Utc)).Altitude < 0);

        // British Summer Time from the last Sunday in March; US daylight time from the second Sunday.
        Assert.Equal(new DateTime(2026, 6, 21, 12, 0, 0), SunPosition.ToUtc(new DateOnly(2026, 6, 21), new TimeOnly(13, 0), 0, SummerTime.European));
        Assert.Equal(new DateTime(2026, 12, 21, 13, 0, 0), SunPosition.ToUtc(new DateOnly(2026, 12, 21), new TimeOnly(13, 0), 0, SummerTime.European));
        Assert.Equal(new DateTime(2026, 3, 28, 13, 0, 0), SunPosition.ToUtc(new DateOnly(2026, 3, 28), new TimeOnly(13, 0), 0, SummerTime.European));
        Assert.Equal(new DateTime(2026, 3, 29, 12, 0, 0), SunPosition.ToUtc(new DateOnly(2026, 3, 29), new TimeOnly(13, 0), 0, SummerTime.European));
        Assert.Equal(new DateTime(2026, 7, 4, 16, 0, 0), SunPosition.ToUtc(new DateOnly(2026, 7, 4), new TimeOnly(12, 0), -5, SummerTime.NorthAmerican));

        // Its way in the model: due south down the plan; with true north turned to the right, to the left.
        var (x, y, z) = SunPosition.Direction(0, 180, 0);
        Assert.Equal((0, -1, 0), (Math.Round(x, 9), Math.Round(y, 9), Math.Round(z, 9)));
        (x, y, _) = SunPosition.Direction(0, 180, 90);
        Assert.Equal((-1, 0), (Math.Round(x, 9), Math.Round(y, 9)));

        // The project's settings: London by default for England, and its clocks.
        var document = BimDocument.CreateDefault();
        var settings = document.ProjectInformation.Render;
        Assert.Equal((51.5, -0.13, 0.0, SummerTime.European), settings.Place(document));
        settings.Time = new TimeOnly(13, 2);
        Assert.InRange(settings.Sun(document).Altitude, 61.6, 62.1);
    }

    /// <summary>A box standing on the ground at the origin, mm.</summary>
    private static Mesh3D Box(double width, double depth, double height, MeshKind kind = MeshKind.Wall, double opacity = 1)
    {
        var mesh = new Mesh3D(Guid.NewGuid(), Guid.Empty, kind, ColourRgb.FromHex("B0B0B0"), "Box");
        if (opacity < 1) mesh.Opacity = opacity;
        var (x0, x1, y0, y1) = (-width / 2, width / 2, -depth / 2, depth / 2);
        Point3D P(double x, double y, double z) => new(x, y, z);
        mesh.AddQuad(P(x0, y0, 0), P(x1, y0, 0), P(x1, y0, height), P(x0, y0, height));
        mesh.AddQuad(P(x1, y1, 0), P(x0, y1, 0), P(x0, y1, height), P(x1, y1, height));
        mesh.AddQuad(P(x0, y1, 0), P(x0, y0, 0), P(x0, y0, height), P(x0, y1, height));
        mesh.AddQuad(P(x1, y0, 0), P(x1, y1, 0), P(x1, y1, height), P(x1, y0, height));
        mesh.AddQuad(P(x0, y0, height), P(x1, y0, height), P(x1, y1, height), P(x0, y1, height));
        return mesh;
    }

    [Fact]
    public void ARayFindsTheFirstFaceItMeets()
    {
        // Two hundred small boxes scattered about, rays cast through them every way, against every triangle tried in turn.
        var random = new Random(5);
        var meshes = new List<Mesh3D>();
        for (var i = 0; i < 200; i++)
        {
            var box = Box(200 + random.NextDouble() * 800, 200 + random.NextDouble() * 800, 200 + random.NextDouble() * 2000);
            var (dx, dy, dz) = (random.NextDouble() * 20000 - 10000, random.NextDouble() * 20000 - 10000, random.NextDouble() * 6000);
            box.Transform(p => new Point3D(p.X + dx, p.Y + dy, p.Z + dz));
            meshes.Add(box);
        }

        var scene = RenderScene.Build(meshes, new Dictionary<string, Material>(), ground: false);
        Assert.Equal(200 * 10, scene.TriangleCount);
        var triangles = meshes.SelectMany(mesh => Enumerable.Range(0, mesh.Indices.Count / 3)
            .Select(t => (mesh.Positions[mesh.Indices[3 * t]], mesh.Positions[mesh.Indices[3 * t + 1]], mesh.Positions[mesh.Indices[3 * t + 2]]))).ToList();

        for (var ray = 0; ray < 300; ray++)
        {
            var from = new Point3D(random.NextDouble() * 24000 - 12000, random.NextDouble() * 24000 - 12000, random.NextDouble() * 8000);
            var (x, y, z) = (random.NextDouble() * 2 - 1, random.NextDouble() * 2 - 1, random.NextDouble() * 2 - 1);
            var length = Math.Sqrt(x * x + y * y + z * z);
            var way = (x / length, y / length, z / length);
            var expected = triangles.Select(t => Meet(from, way, t)).Where(d => d is not null).Min();
            var found = scene.Cast(from, way);
            if (expected is null) Assert.Null(found);
            else Assert.Equal(expected.Value, found!.Value, 0.5);
        }
    }

    /// <summary>Where a ray meets a triangle, mm along it, worked out in double without the hierarchy.</summary>
    private static double? Meet(Point3D o, (double X, double Y, double Z) d, (Point3D A, Point3D B, Point3D C) t)
    {
        var (e1, e2) = ((t.B.X - t.A.X, t.B.Y - t.A.Y, t.B.Z - t.A.Z), (t.C.X - t.A.X, t.C.Y - t.A.Y, t.C.Z - t.A.Z));
        (double, double, double) Cross((double X, double Y, double Z) a, (double X, double Y, double Z) b) => (a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
        double Dot((double X, double Y, double Z) a, (double X, double Y, double Z) b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        var p = Cross(d, e2);
        var det = Dot(e1, p);
        if (Math.Abs(det) < 1e-12) return null;
        var s = (o.X - t.A.X, o.Y - t.A.Y, o.Z - t.A.Z);
        var u = Dot(s, p) / det;
        if (u < 0 || u > 1) return null;
        var q = Cross(s, e1);
        var v = Dot(d, q) / det;
        if (v < 0 || u + v > 1) return null;
        var distance = Dot(e2, q) / det;
        return distance > 0.01 ? distance : null;
    }

    /// <summary>The ground round a box seen from overhead with the sun 45° up in the south: how bright to the north of it, in its shadow, and to the south.</summary>
    private static (double North, double South) Shadow(Mesh3D box, RenderSky sky = RenderSky.Clear)
    {
        var scene = RenderScene.Build(new[] { box }, new Dictionary<string, Material>(), ground: true);
        var camera = new Camera3D(new Point3D(0, 0, 50000), new Point3D(0, 0, 0), Perspective: false) { FieldOfView = 2 * Math.Atan(0.2) * 180 / Math.PI };
        var sun = Vector3.Normalize(new Vector3(0, -1, 1));
        var light = sky == RenderSky.Clear ? new RenderLight(sun, Vector3.One, 3.2f, sky, 1) : new RenderLight(sun, Vector3.Zero, 0, sky, 1);
        var job = new RenderJob(scene, camera, light, 100, 100, 0);
        job.Pass(8);
        var pixels = job.Bgra();

        // 0.2 m a pixel, north up: the shadow 3 m long behind a 3 m box.
        double Mean(int rowFrom, int rowTo) =>
            Enumerable.Range(rowFrom, rowTo - rowFrom).SelectMany(row => Enumerable.Range(46, 8).Select(column => (double)pixels[4 * (row * 100 + column) + 1])).Average();
        return (Mean(33, 42), Mean(58, 67));
    }

    [Fact]
    public void TheSunCastsAShadowAndGlassLetsItThrough()
    {
        var (north, south) = Shadow(Box(2000, 2000, 3000));
        Assert.True(north < 0.6 * south, $"in its shadow {north:0}, in the sun {south:0}");

        // Glass in its place: the ground behind it nearly as bright as in the sun.
        var (behind, front) = Shadow(Box(2000, 2000, 3000, MeshKind.Glazing, 0.35));
        Assert.True(behind > 0.75 * front, $"behind the glass {behind:0}, in front {front:0}");

        // Under an overcast sky there is no shadow to speak of.
        var (dull, bright) = Shadow(Box(2000, 2000, 3000), RenderSky.Overcast);
        Assert.True(dull > 0.6 * bright, $"overcast north {dull:0}, south {bright:0}");
    }

    [Fact]
    public void TheSunsLightIsWeakerAndRedderLowInTheSky()
    {
        var document = BimDocument.CreateDefault();
        var settings = document.ProjectInformation.Render;
        settings.Time = new TimeOnly(13, 0);
        var noon = RenderLight.For(document, settings);
        settings.Time = new TimeOnly(20, 45);
        var evening = RenderLight.For(document, settings);
        Assert.True(evening.SunStrength * evening.SunColour.Z < noon.SunStrength * noon.SunColour.Z);
        Assert.True(evening.SunColour.Z / evening.SunColour.X < noon.SunColour.Z / noon.SunColour.X);
        settings.Time = new TimeOnly(23, 30);
        Assert.Equal(0, RenderLight.For(document, settings).SunStrength);
        settings.Time = new TimeOnly(13, 0);
        settings.Sky = RenderSky.Overcast;
        Assert.Equal(0, RenderLight.For(document, settings).SunStrength);
    }

    [Fact]
    public void FacesAndRenderSettingsAreKeptInTheProject()
    {
        var document = BimDocument.CreateDefault();
        var brick = document.Materials.Single(material => material.Name == "Brick, Common");
        new SetMaterialCommand(brick, MaterialLook.Of(brick) with
        {
            Finish = brick.Finish with { UnitLength = 290, UnitHeight = 50, JointColour = ColourRgb.FromHex("404040"), Reflectance = 0.02 }
        }).Redo();
        var render = document.ProjectInformation.Render;
        (render.Date, render.Time, render.Latitude, render.Longitude, render.UtcOffset, render.Summer) = (new DateOnly(2026, 9, 1), new TimeOnly(9, 15), 53.48, -2.24, 0, SummerTime.European);
        (render.Sky, render.Quality, render.Width, render.Height, render.Exposure) = (RenderSky.Overcast, RenderQuality.High, 1920, 1080, 0.5);

        var read = ProjectFile.FromJson(ProjectFile.ToJson(document));
        var kept = read.Materials.Single(material => material.Name == "Brick, Common");
        Assert.Equal(brick.Finish, kept.Finish);
        Assert.True(read.ProjectInformation.Render.SameAs(render));

        // A material saved before patterns: laid as its name says; one set to none stays so.
        var json = ProjectFile.ToJson(document).Replace("\"Pattern\": \"Brick\"", "\"Pattern\": null");
        Assert.Equal(SurfacePattern.Brick, ProjectFile.FromJson(json).Materials.Single(material => material.Name == "Brick, Common").Pattern);
        new SetMaterialCommand(brick, MaterialLook.Of(brick) with { Finish = brick.Finish with { Pattern = SurfacePattern.None, UnitLength = 0, Reflectance = 0 } }).Redo();
        Assert.Equal(SurfacePattern.None, ProjectFile.FromJson(ProjectFile.ToJson(document)).Materials.Single(material => material.Name == "Brick, Common").Pattern);
    }

    [Fact]
    public void AMaterialIsCopiedUnderANameOfItsOwnAndAddedAsOneStep()
    {
        var document = BimDocument.CreateDefault();
        var brick = document.Materials.Single(material => material.Name == "Brick, Common");
        var copy = MaterialCopies.Copy(brick, MaterialCopies.FreeName(document, brick.Name));
        Assert.Equal("Brick, Common 2", copy.Name);
        Assert.Equal(brick.Finish, copy.Finish);
        var add = new AddMaterialCommand(document, copy);
        add.Redo();
        Assert.Contains(copy, document.Materials);
        add.Undo();
        Assert.DoesNotContain(copy, document.Materials);
    }
}
