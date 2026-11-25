using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;
using BIMDesigner.Core.Rendering;
using BIMDesigner.Core.Views;
using BIMDesigner.Infrastructure.Serialization;

using BimWindow = BIMDesigner.Core.Architecture.Window;

namespace BIMDesigner.Tests;

/// <summary>
/// Period looks (round 142): shutters either side of a window on the wall's outside face - louvred, panelled or board, their finish's colour,
/// set on the window's type, saved and copied with it; half-timbering a storey high over render; and curved clay tiles, Spanish or mission.
/// </summary>
[Collection("Wpf")]
public class PeriodLookTests
{
    /// <summary>A 6 m outside wall along x, its outside toward +y, and a window of a type in its middle, 900 up.</summary>
    private static (BimDocument Document, Wall Wall, BimWindow Window, double WallHalf) WallWith(WindowType type)
    {
        var document = BimDocument.CreateDefault();
        var wallType = document.TypesOf<WallType>().First(each => each.Name == "Exterior - Brick on Block 330mm");
        var wall = new Wall { Start = new Point2D(0, 0), End = new Point2D(6000, 0), TypeId = wallType.Id, LevelId = document.Levels[0].Id, UnconnectedHeight = 3000 };
        document.Add(wall);
        if (document.FindType<WindowType>(type.Id) is null) document.AddType(type);
        var window = new BimWindow { SillHeight = 900, TypeId = type.Id, LevelId = wall.LevelId, HostWallId = wall.Id, DistanceAlongWall = 3000 };
        document.Add(window);
        return (document, wall, window, wallType.Width / 2);
    }

    private static List<Mesh3D> MeshesOf(BimDocument document, Wall wall, BimWindow window) =>
        ModelMeshBuilder.BuildWall(document, wall).Where(mesh => mesh.ElementId == window.Id).ToList();

    [Fact]
    public void ShuttersStandEitherSideOfTheWindowOnTheOutsideFace()
    {
        // A window 1000 wide, 1200 high: no shutters, none drawn.
        var type = new WindowType("Test - 1000 x 1200", 1000, 1200);
        var (document, wall, window, half) = WallWith(type);
        Assert.DoesNotContain(MeshesOf(document, wall, window), mesh => mesh.Description == "Shutters");

        // Louvred, painted green: each 500 wide, 40 off the opening, from the sill to the head, standing off the outside face.
        foreach (var kind in new[] { WindowShutters.Louvred, WindowShutters.Panelled, WindowShutters.Board })
        {
            (type.Shutters, type.ShutterFinish) = (kind, "Painted Green");
            var shutters = Assert.Single(MeshesOf(document, wall, window), mesh => mesh.Description == "Shutters");
            var bounds = shutters.Bounds()!.Value;
            Assert.Equal(3000 - 500 - 40 - 500, bounds.Min.X, 1);
            Assert.Equal(3000 + 500 + 40 + 500, bounds.Max.X, 1);
            Assert.True(bounds.Min.Y >= half - 1, $"{kind}: {bounds.Min.Y} against the face at {half}");
            Assert.InRange(bounds.Max.Y, half + 20, half + 60);
            Assert.Equal(900, bounds.Min.Z, 1);
            Assert.Equal(2100, bounds.Max.Z, 1);
            Assert.Equal(new ColourRgb(0x2E, 0x4A, 0x35), shutters.Colour);
        }

        // Its type's Properties offer them, and their finish only where it has them.
        Assert.Contains(type.GetTypeParameters(document), parameter => parameter.Definition.Name == "Shutters");
        Assert.Contains(type.GetTypeParameters(document), parameter => parameter.Definition.Name == "Shutter Finish");
        type.Shutters = WindowShutters.None;
        Assert.DoesNotContain(type.GetTypeParameters(document), parameter => parameter.Definition.Name == "Shutter Finish");
    }

    [Fact]
    public void ShuttersAreCopiedAndSavedWithTheirType()
    {
        var document = BimDocument.CreateDefault();
        var shuttered = document.TypesOf<WindowType>().Single(each => each.Name == "Double Hung - 36 x 60 in, Louvred Shutters");
        Assert.Equal((WindowShutters.Louvred, "Painted Black"), (shuttered.Shutters, shuttered.ShutterFinish));
        var copy = shuttered.Duplicate("Copy");
        Assert.Equal((WindowShutters.Louvred, "Painted Black"), (copy.Shutters, copy.ShutterFinish));

        shuttered.ShutterFinish = "Painted Blue";
        var path = Path.Combine(Path.GetTempPath(), $"shutters-{Guid.NewGuid():N}.bimx");
        try
        {
            ProjectFile.Save(document, path);
            var opened = ProjectFile.Load(path).FindType<WindowType>(shuttered.Id)!;
            Assert.Equal((WindowShutters.Louvred, "Painted Blue"), (opened.Shutters, opened.ShutterFinish));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void HalfTimberingAndCurvedClayTilesAreDrawnAsTheyAreBuilt()
    {
        var render = new ColourRgb(0xEE, 0xE8, 0xDA);
        Assert.Equal(SurfacePattern.HalfTimbered, SurfacePatterns.Guess("Half-Timbering, Black Oak on White Render", render).Pattern);
        Assert.Equal(SurfacePattern.BarrelTiles, SurfacePatterns.Guess("Roof Tile, Clay Barrel (Spanish)", render).Pattern);
        Assert.Equal(SurfacePattern.PlainTiles, SurfacePatterns.Guess("Roof Tile, Clay Plain", render).Pattern);

        // Half-timbering: four panels 900 wide, a storey 3 m high; a post at each panel's edge and the plates top and bottom dark and proud,
        // the render between light.
        var finish = SurfacePatterns.Standard(SurfacePattern.HalfTimbered, render);
        var frame = SurfaceTextures.Of(finish)!;
        Assert.Equal((3600.0, 3000.0), (frame.Length, frame.Rise));
        static int Bright(ColourRgb c) => c.R + c.G + c.B;
        Assert.True(Bright(frame.At(900, 2200)) < Bright(render) / 3, "a post");
        Assert.True(Bright(frame.At(450, 40)) < Bright(render) / 3, "the sole plate");
        Assert.True(Bright(frame.At(450, 2300)) > Bright(render) * 0.85, "the render in the first panel's upper half");
        Assert.True(frame.ReliefAt(900, 2200) > frame.ReliefAt(450, 2300) + 0.5);

        // Curved clay tiles: the cover's crest standing up over the pan's trough.
        var tiles = SurfaceTextures.Of(SurfacePatterns.Standard(SurfacePattern.BarrelTiles, new ColourRgb(0xB8, 0x61, 0x3A)))!;
        Assert.True(tiles.ReliefAt(0, 200) > tiles.ReliefAt(115, 200) + 0.6);

        // The library's: a half-timbered wall, a barrel tile roof - taken at its maker's pitch in England, 2 1/2 in 12 by the IRC.
        var document = BimDocument.CreateDefault();
        Assert.Equal(SurfacePattern.HalfTimbered, document.Materials.Single(material => material.Name == "Half-Timbering, Black Oak on White Render").Pattern);
        var roof = document.TypesOf<RoofType>().Single(each => each.Name == "Roof - Clay Barrel Tiles on Insulated Rafters 270mm");
        Assert.Equal(SurfacePattern.BarrelTiles, document.FindMaterial(roof.Structure.Layers[0].MaterialId)!.Pattern);
        Assert.Null(SlabRules.CoveringPitch(document, roof));
        Assert.Equal(2.5, SlabRules.IrcCoveringSlope(document, roof)!.Value.Rise);
        Assert.Contains(document.TypesOf<WallType>(), each => each.Name == "Half-Timbered - Render on Block 330mm");
    }
}
