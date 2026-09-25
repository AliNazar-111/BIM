using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;

namespace BIMDesigner.Tests;

/// <summary>
/// Walls meeting a pitched roof (specification section 3.1, "attach top to roofs"): attached,
/// a wall's top follows the roof's underside along its length - which closes a gable end - and
/// keeps following it as the roof changes.
/// </summary>
public class RoofWallTests
{
    private const double Half = 165;
    private static readonly double Rise30 = Math.Tan(30 * Math.PI / 180);

    /// <summary>
    /// A 10 m x 6 m box of exterior walls up to the first floor, under a 30° gable roof on
    /// their outside faces, sloping from the long sides. The walls' tops attached to it.
    /// </summary>
    private static (BimDocument Document, List<Wall> Walls, Roof Roof) GabledBox(bool attach = true)
    {
        var document = BimDocument.CreateDefault();
        var type = document.TypesOf<WallType>().First(t => t.Name.StartsWith("Exterior"));
        var ground = document.Levels[0];
        var first = document.Levels[1];

        var corners = new[]
        {
            new Point2D(0, 0), new Point2D(10000, 0), new Point2D(10000, 6000), new Point2D(0, 6000)
        };

        var walls = new List<Wall>();
        for (var i = 0; i < corners.Length; i++)
        {
            var wall = new Wall
            {
                Start = corners[i], End = corners[(i + 1) % corners.Length],
                TypeId = type.Id, LevelId = ground.Id, TopLevelId = first.Id
            };

            document.Add(wall);
            walls.Add(wall);
        }

        var roof = new Roof
        {
            TypeId = document.TypesOf<RoofType>().First().Id,
            LevelId = ground.Id,
            HeightOffset = first.Elevation
        };

        roof.SetBoundary(new[]
        {
            new Point2D(-Half, -Half), new Point2D(10000 + Half, -Half),
            new Point2D(10000 + Half, 6000 + Half), new Point2D(-Half, 6000 + Half)
        });

        roof.SetEdges(new[]
        {
            new RoofEdge { DefinesSlope = true, SlopeDegrees = 30 },
            new RoofEdge(),
            new RoofEdge { DefinesSlope = true, SlopeDegrees = 30 },
            new RoofEdge()
        });

        document.Add(roof);

        if (attach)
            foreach (var wall in walls) wall.TopAttachedTo = roof.Id;

        return (document, walls, roof);
    }

    private static double RidgeUnderside => 3000 + (3000 + Half) * Rise30;

    [Fact]
    public void AGableWallRisesToTheRidge()
    {
        var (document, walls, _) = GabledBox();
        var gable = walls[1];   // the east end, under the gable

        var profile = WallProfile.Of(document, gable);
        Assert.NotNull(profile);

        // Up to the roof's underside under the ridge, halfway along...
        Assert.Equal(RidgeUnderside, profile!.Max(point => point.Y), precision: 3);
        var peak = profile.First(point => Math.Abs(point.Y - profile.Max(p => p.Y)) < 1e-6);
        Assert.Equal(3000, peak.X, precision: 3);

        // ...and down to the eaves at either end, where the roof comes over the wall.
        Assert.Equal(3000 + Half * Rise30, profile.Where(point => point.Y > 0 && point.X < 1).Max(point => point.Y), precision: 3);
    }

    [Fact]
    public void AnEaveWallStaysAtThePlate()
    {
        var (document, walls, roof) = GabledBox();
        var eave = walls[0];   // the south side, under the eave

        var profile = WallProfile.Of(document, eave)!;
        var tops = profile.Where(point => point.Y > 0).Select(point => point.Y).Distinct().ToList();

        // Level along its whole length: the roof does not rise along an eave. It is taken to
        // the underside over the wall's inner face, so no wedge of air shows along the inside;
        // the rest is inside the roof's build-up.
        Assert.All(tops, top => Assert.Equal(3000 + 2 * Half * Rise30, top, precision: 3));

        var thickness = document.FindType<SlabType>(roof.TypeId)!.Thickness;
        Assert.True(2 * Half * Rise30 < thickness, "The wall would show through the top of the roof.");
    }

    [Fact]
    public void TheGableIsBuiltIn3DAndCountedInTheWallsArea()
    {
        var (document, walls, _) = GabledBox();
        var gable = walls[1];

        var meshes = ModelMeshBuilder.Build(document).Where(mesh => mesh.ElementId == gable.Id).ToList();
        var bounds = ModelMeshBuilder.Bounds(meshes)!.Value;

        Assert.Equal(RidgeUnderside, bounds.Max.Z, precision: 1);

        // A plain 6 m wall 3 m high, plus the triangle over it.
        var (unattachedDocument, unattachedWalls, _) = GabledBox(attach: false);
        var plain = unattachedWalls[1].GetArea(unattachedDocument);
        Assert.True(gable.GetArea(document) > plain + 4e6,
            $"The gable wall's area ({gable.GetArea(document) / 1e6:0.00} m²) did not grow by the gable.");
    }

    [Fact]
    public void TheGableFollowsTheRoofWhenThePitchChanges()
    {
        var (document, walls, roof) = GabledBox();
        var gable = walls[1];

        var before = WallProfile.Of(document, gable)!.Max(point => point.Y);

        roof.SlopeDegrees = 45;

        var after = WallProfile.Of(document, gable)!.Max(point => point.Y);
        Assert.Equal(3000 + (3000 + Half), after, precision: 3);
        Assert.True(after > before);
    }

    [Fact]
    public void AnUnattachedWallKeepsItsOwnHeight()
    {
        var (document, walls, _) = GabledBox(attach: false);

        Assert.Null(WallProfile.Of(document, walls[1]));
        Assert.Equal(3000, walls[1].GetTopElevation(document), precision: 6);
    }

    [Fact]
    public void AWallUnderAFlatRoofIsLeftRectangular()
    {
        var (document, walls, roof) = GabledBox();
        roof.SetShape(RoofForm.Flat);

        Assert.Null(WallProfile.Of(document, walls[1]));

        // It still stops under the roof, as any wall attached to a slab does.
        Assert.Equal(roof.GetBottomElevation(document), walls[1].GetTopElevation(document), precision: 6);
    }

    [Fact]
    public void AnOutlineDrawnByHandWinsOverTheRoof()
    {
        var (document, walls, _) = GabledBox();
        var gable = walls[1];

        gable.Profile = WallProfile.Rectangle(gable.Length, 2500);
        gable.ProfileLength = gable.Length;

        Assert.Equal(2500, WallProfile.Of(document, gable)!.Max(point => point.Y), precision: 6);
    }

    [Fact]
    public void AWallRunningPastTheRoofKeepsItsOwnTopBeyondIt()
    {
        var (document, walls, _) = GabledBox();

        // A garden wall carried on 4 m past the east end of the house, attached like the rest.
        var type = document.GetWallType(walls[0])!;
        var garden = new Wall
        {
            Start = new Point2D(10000, 3000), End = new Point2D(14000, 3000),
            TypeId = type.Id, LevelId = walls[0].LevelId, UnconnectedHeight = 1800,
            TopAttachedTo = walls[0].TopAttachedTo
        };

        document.Add(garden);

        var profile = WallProfile.Of(document, garden)!;

        // Under the roof it rises to it; out past the verge it is its own 1.8 m.
        Assert.True(profile.Max(point => point.Y) > 3000);
        Assert.Equal(1800, profile.Where(point => point.X > garden.Length - 1 && point.Y > 0).Min(point => point.Y), precision: 3);
    }
}
