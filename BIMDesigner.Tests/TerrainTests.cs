using System.Windows;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Infrastructure.Interoperability;
using BIMDesigner.Infrastructure.Serialization;
using BIMDesigner.UI;
using BIMDesigner.UI.Controls;
using Xbim.Ifc4.Interfaces;
using Xbim.Ifc4.ProductExtension;
using static BIMDesigner.Tests.StairTests;

namespace BIMDesigner.Tests;

/// <summary>
/// The ground: a surface through spot levels, drawn between them as Delaunay triangles,
/// contoured, cut away under the floor of its storey; an outside stair standing on it where it
/// slopes, a railing following it, the drop under a balcony measured to it; and checked
/// against the walls - the damp-proof course 150 above it, and under the IRC falling away.
/// </summary>
[Collection("Wpf")]
public class TerrainTests
{
    /// <summary>Ground over 20 m by 20 m round the house, falling from +h at y = -7000 to 0 at y = 13000: a plane.</summary>
    private static Toposurface Slope(BimDocument document, double high, double low = 0, double from = -7000, double to = 13000)
    {
        var ground = new Toposurface { LevelId = document.Levels[0].Id };
        ground.SetPoints(new[]
        {
            new Point3D(-7000, from, high), new Point3D(13000, from, high), new Point3D(13000, to, low), new Point3D(-7000, to, low),
            new Point3D(3000, (from + to) / 2, (high + low) / 2)
        });
        document.Add(ground);
        return ground;
    }

    [Fact]
    public void ItsTrianglesAreTheFattestItsPointsAllow()
    {
        var random = new Random(7);
        var points = Enumerable.Range(0, 60).Select(_ => new Point2D(random.NextDouble() * 10000, random.NextDouble() * 10000)).ToList();
        var triangles = Delaunay.Triangulate(points);

        // No point inside any triangle's circumcircle.
        foreach (var (a, b, c) in triangles)
        {
            var (pa, pb, pc) = (points[a], points[b], points[c]);
            var d = 2 * (pa.X * (pb.Y - pc.Y) + pb.X * (pc.Y - pa.Y) + pc.X * (pa.Y - pb.Y));
            var centre = new Point2D(
                ((pa.X * pa.X + pa.Y * pa.Y) * (pb.Y - pc.Y) + (pb.X * pb.X + pb.Y * pb.Y) * (pc.Y - pa.Y) + (pc.X * pc.X + pc.Y * pc.Y) * (pa.Y - pb.Y)) / d,
                ((pa.X * pa.X + pa.Y * pa.Y) * (pc.X - pb.X) + (pb.X * pb.X + pb.Y * pb.Y) * (pa.X - pc.X) + (pc.X * pc.X + pc.Y * pc.Y) * (pb.X - pa.X)) / d);
            var radius = centre.DistanceTo(pa);
            Assert.DoesNotContain(points.Where((_, i) => i != a && i != b && i != c), point => centre.DistanceTo(point) < radius - 1e-6);
        }

        // Covering their hull: their areas add up to its.
        var area = triangles.Sum(t => Math.Abs(Polygon2D.SignedArea(new[] { points[t.A], points[t.B], points[t.C] })));
        Assert.InRange(area, 0.5 * 1e8, 1e8);
        Assert.Equal(2, Delaunay.Triangulate(new[] { new Point2D(0, 0), new Point2D(10, 0), new Point2D(10, 10), new Point2D(0, 10) }).Count);
    }

    [Fact]
    public void ItsHeightIsReadBetweenItsSpotLevelsAndItIsContoured()
    {
        var document = BimDocument.CreateDefault();
        var ground = Slope(document, 1000);
        Assert.Equal(500, Terrains.HeightAt(ground, new Point2D(0, 3000))!.Value, 6);
        Assert.Equal(750, Terrains.HeightAt(ground, new Point2D(1234, -2000))!.Value, 6);
        Assert.Null(Terrains.HeightAt(ground, new Point2D(20000, 0)));

        // Contours every 250: at 250, 500, 750 - and 1000 along its top edge.
        var heights = Terrains.Contours(document, ground).Select(contour => contour.Height).Distinct().OrderBy(height => height).ToList();
        Assert.Equal(new[] { 250.0, 500, 750, 1000 }, heights);

        // Every 200: every fifth, at 1000, a major one.
        ground.ContourInterval = 200;
        Assert.Equal(new[] { 1000.0 }, Terrains.Contours(document, ground).Where(contour => contour.Major).Select(contour => contour.Height).Distinct());
    }

    [Fact]
    public void ItIsCutAwayUnderTheFloorOfItsStorey()
    {
        var (document, _) = House();
        var ground = Slope(document, -150, -150);
        var whole = Terrains.Mesh(document, ground)!.TriangleCount;

        var slab = new Floor { LevelId = document.Levels[0].Id, TypeId = document.TypesOf<FloorType>().First().Id };
        slab.SetBoundary(new[] { new Point2D(0, 0), new Point2D(6000, 0), new Point2D(6000, 5000), new Point2D(0, 5000) });
        document.Add(slab);

        var mesh = Terrains.Mesh(document, ground)!;
        Assert.NotEqual(whole, mesh.TriangleCount);
        var tops = Enumerable.Range(0, mesh.TriangleCount)
            .Select(i => new Point2D((mesh.Positions[3 * i].X + mesh.Positions[3 * i + 1].X + mesh.Positions[3 * i + 2].X) / 3, (mesh.Positions[3 * i].Y + mesh.Positions[3 * i + 1].Y + mesh.Positions[3 * i + 2].Y) / 3));
        Assert.DoesNotContain(tops, middle => slab.Contains(middle));
        Assert.Equal(-150 - Terrains.Depth, mesh.Positions.Min(point => point.Z), 6);
    }

    [Fact]
    public void AStairOutsideStandsOnTheGroundWhereItSlopes()
    {
        // The ground falling away to the south of the house: 450 below the ground floor's level in front of the stair.
        var (document, _) = House();
        Slope(document, -900, 0, -7000, 0);
        var stair = Put(document, 7000, -2500);
        stair.Outside = true;

        var layout = Stairs.Layout(document, stair);
        Assert.InRange(layout.Bottom, -900 * (2500 + 150) / 7000.0 - 1, -900 * (2500 + 150) / 7000.0 + 1);
        Assert.Equal(3000 - layout.Bottom, layout.Top - layout.Bottom, 6);
        Assert.Equal(17, layout.Risers);
    }

    [Fact]
    public void ARailingFollowsTheGroundAndADecksDropIsMeasuredToIt()
    {
        var (document, _) = House();
        var ground = Slope(document, 1000, 0, -7000, 13000);

        // A fence along the slope: each post on the ground where it stands.
        var fence = new Railing { LevelId = document.Levels[0].Id, TypeId = document.TypesOf<RailingType>().First().Id };
        fence.SetPath(new[] { new Point2D(8000, -6000), new Point2D(8000, 12000) });
        document.Add(fence);
        Assert.Equal(Terrains.HeightAt(ground, new Point2D(8000, -6000))!.Value, Railings.Base(document, fence, fence.Path[0]), 6);
        Assert.Equal(Terrains.HeightAt(ground, new Point2D(8000, 12000))!.Value, Railings.Base(document, fence, fence.Path[1]), 6);

        // A deck 450 to 500 over the ground at its edges: no guarding asked.
        var deck = new Floor { LevelId = document.Levels[0].Id, TypeId = document.TypesOf<FloorType>().First().Id, HeightOffset = 1200 };
        deck.SetBoundary(new[] { new Point2D(7000, -2000), new Point2D(9000, -2000), new Point2D(9000, -1000), new Point2D(7000, -1000) });
        document.Add(deck);
        Assert.Empty(Railings.Edges(document, deck, RailingBuilding.Dwelling).Open);
        deck.HeightOffset = 2000;
        Assert.NotEmpty(Railings.Edges(document, deck, RailingBuilding.Dwelling).Open);

        // Under the IRC: 1450 up, it drops 753 just past its north edge, but the ground falls on to 796 below it 36 in out - a guard is asked.
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.Irc;
        deck.HeightOffset = 1450;
        var open = Assert.Single(Railings.Edges(document, deck, RailingBuilding.Dwelling).Open);
        Assert.All(open.Points, point => Assert.Equal(-1000, point.Y, 1));
        Assert.InRange(open.Drop, 790, 800);
    }

    [Fact]
    public void TheGroundIsKeptDownAndFallsAwayFromTheWalls()
    {
        var (document, _) = House();
        foreach (var wall in document.Walls) wall.TypeId = document.TypesOf<WallType>().First(type => type.Function == WallFunction.Exterior).Id;
        var slab = new Floor { LevelId = document.Levels[0].Id, TypeId = document.TypesOf<FloorType>().First().Id };
        slab.SetBoundary(new[] { new Point2D(0, 0), new Point2D(6000, 0), new Point2D(6000, 5000), new Point2D(0, 5000) });
        document.Add(slab);

        // Level with the floor: the damp-proof course has no 150 over it.
        var ground = Slope(document, 0, 0);
        Assert.Contains(Terrains.Problems(document, ground), problem => problem.Contains("Approved Document C asks for the damp-proof course 150 above the ground"));

        // 200 below it, and falling away: clear.
        ground.SetPoints(ground.Points.Select(point => point with { Z = -200 }));
        Assert.Empty(Terrains.Problems(document, ground));

        // Under the IRC, level ground does not fall 6 in in 10 ft.
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.Ada;
        Assert.Contains(Terrains.Problems(document, ground), problem => problem.Contains("falls only 0 in the first 10 ft"));
    }

    [Fact]
    public void PointsAreReadFromAFileTheHigherKeptWhereTwoMeet()
    {
        var read = Terrains.ReadPoints("x,y,z\n0,0,1.5\n10;0;2.0\n10\t10\t2.5 spot\n0 10 3\n0,0,1.75\nbad line\n", 1000);
        Assert.Equal(4, read.Count);
        Assert.Contains(new Point3D(0, 0, 1750), read);
        Assert.Contains(new Point3D(10000, 10000, 2500), read);
    }

    [Fact]
    public void ItIsSavedCopiedMirroredAndExported()
    {
        var document = BimDocument.CreateDefault();
        var ground = Slope(document, 1000);
        ground.ContourInterval = 500;

        var back = ProjectFile.FromJson(ProjectFile.ToJson(document)).Elements.OfType<Toposurface>().Single();
        Assert.Equal(ground.Points, back.Points);
        Assert.Equal(500, back.ContourInterval);

        var copy = (Toposurface)ElementCopy.Clone(ground)!;
        new MirrorElementsCommand(new[] { copy }, new Line2D(new Point2D(0, 0), new Vector2D(0, 1))).Redo();
        Assert.Equal(new Point3D(7000, -7000, 1000), copy.Points[0]);
        Assert.Equal(1000, Terrains.HeightAt(copy, new Point2D(0, -7000))!.Value, 6);

        using var model = IfcExport.Build(document);
        var exported = Assert.Single(model.Instances.OfType<IfcGeographicElement>());
        Assert.Equal(IfcGeographicElementTypeEnum.TERRAIN, exported.PredefinedType);
    }

    [Fact]
    public void TheToposurfaceToolMakesTheGroundAndAddsToIt()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (Application.Current is null) new App().InitializeComponent();
                var document = BimDocument.CreateDefault();
                var plan = new PlanView { Document = document, ActiveLevelId = document.Levels[0].Id };
                plan.SetTool(PlanTool.Terrain);
                var history = new UndoStack();
                plan.History = history;

                plan.ActiveGroundElevation = -300;
                Assert.True(plan.ClickGround(new Point2D(0, 0)));
                Assert.True(plan.ClickGround(new Point2D(10000, 0)));
                Assert.Empty(document.Elements.OfType<Toposurface>());
                plan.ActiveGroundElevation = 600;
                Assert.True(plan.ClickGround(new Point2D(5000, 8000)));
                var ground = Assert.Single(document.Elements.OfType<Toposurface>());
                Assert.Equal(3, ground.Points.Count);

                // A fourth adds to it; read in, more.
                plan.ClickGround(new Point2D(5000, -6000));
                Assert.Equal(4, ground.Points.Count);
                Assert.Equal(2, plan.ImportGround("1000,1000,0.1\n2000,1000,0.2", 1000));
                Assert.Equal(6, ground.Points.Count);
                history.Undo();
                Assert.Equal(4, ground.Points.Count);
            }
            catch (Exception e) { failure = e; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        if (failure is not null) throw failure;
    }
}
