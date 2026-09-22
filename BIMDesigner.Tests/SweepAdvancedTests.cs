using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;
using BIMDesigner.Infrastructure.Interoperability;
using BIMDesigner.Infrastructure.Serialization;
using Xbim.Ifc4.SharedBldgElements;

namespace BIMDesigner.Tests;

/// <summary>
/// Sweeps as Revit's Wall Sweeps table has them - offset, flip, setback, cuts wall, cuttable -
/// meeting at corners and returning round open ends; drawn profiles; and sweeps and reveals
/// placed on walls one by one, lying or standing.
/// </summary>
public class SweepAdvancedTests
{
    /// <summary>A 5 m generic wall along X, exterior toward +Y, 3 m high; its type carries the sweeps given.</summary>
    private static (BimDocument Document, WallType Type, Wall Wall, double Half) Wall(params WallSweep[] sweeps)
    {
        var document = BimDocument.CreateDefault();
        var type = document.TypesOf<WallType>().Single(t => t.Name.StartsWith("Generic"));
        type.Sweeps.AddRange(sweeps);
        var wall = new Wall
        {
            Start = new Point2D(0, 0), End = new Point2D(5000, 0),
            TypeId = type.Id, LevelId = document.Levels[0].Id, UnconnectedHeight = 3000
        };
        document.Add(wall);
        return (document, type, wall, type.Width / 2);
    }

    private static Guid Material(BimDocument document) => document.Materials.First().Id;

    private static WallSweep Board(BimDocument document, WallSide side = WallSide.Exterior) =>
        new(SweepKind.Sweep, SweepProfile.Rectangle, side, 20, 100, 0, false, Material(document));

    private static (Point3D Min, Point3D Max) SweepBounds(BimDocument document, Wall wall) =>
        ModelMeshBuilder.BuildWall(document, wall).Where(m => m.Kind == MeshKind.Sweep).Select(m => m.Bounds()!.Value)
            .Aggregate((a, b) => (new Point3D(Math.Min(a.Min.X, b.Min.X), Math.Min(a.Min.Y, b.Min.Y), Math.Min(a.Min.Z, b.Min.Z)),
                                  new Point3D(Math.Max(a.Max.X, b.Max.X), Math.Max(a.Max.Y, b.Max.Y), Math.Max(a.Max.Z, b.Max.Z))));

    [Fact]
    public void OffsetMovesASweepOffTheFace()
    {
        var probe = BimDocument.CreateDefault();
        var (document, _, wall, half) = Wall(Board(probe) with { Offset = 10 });
        var bounds = SweepBounds(document, wall);

        Assert.Equal(half + 10, bounds.Min.Y, precision: 6);
        Assert.Equal(half + 30, bounds.Max.Y, precision: 6);
    }

    [Fact]
    public void FlipTurnsTheProfileUpsideDown()
    {
        var sweep = new WallSweep(SweepKind.Sweep, SweepProfile.Skirting, WallSide.Exterior, 20, 100, 0, false, Guid.Empty);
        var upright = sweep.Shape();
        var flipped = (sweep with { Flip = true }).Shape();

        // The skirting's splayed top becomes a splayed bottom.
        Assert.Equal(upright.Count, flipped.Count);
        Assert.Contains(flipped, p => Math.Abs(p.Out - 0.4) < 1e-9 && Math.Abs(p.Up) < 1e-9);
        Assert.True(Polygon2D.SignedArea(flipped.Select(p => new Point2D(p.Out, p.Up)).ToList()) > 0, "Still anticlockwise.");
    }

    [Fact]
    public void SetbackStopsItShortOfTheWallsEnds()
    {
        var probe = BimDocument.CreateDefault();
        var (document, _, wall, _) = Wall(Board(probe) with { Setback = 300 });
        var bounds = SweepBounds(document, wall);

        Assert.Equal(300, bounds.Min.X, precision: 6);
        Assert.Equal(4700, bounds.Max.X, precision: 6);
    }

    [Fact]
    public void ADoorBreaksASweepOnlyIfItIsCuttable()
    {
        var probe = BimDocument.CreateDefault();
        foreach (var (cuttable, runs) in new[] { (true, 2), (false, 1) })
        {
            var (document, type, wall, _) = Wall(Board(probe, WallSide.Interior) with { Cuttable = cuttable });
            document.Add(new Door { TypeId = document.TypesOf<DoorType>().First().Id, LevelId = wall.LevelId, HostWallId = wall.Id, DistanceAlongWall = 2500 });

            Assert.Equal(runs, WallSweeps.Runs(document, wall, type, type.Sweeps[0], 0, 100).Count);
        }
    }

    [Fact]
    public void ASweepSetIntoTheWallCutsItsOwnRecessWhenAskedTo()
    {
        var probe = BimDocument.CreateDefault();
        var (document, type, wall, half) = Wall(Board(probe) with { Offset = -15, CutsWall = true, Elevation = 1000 });

        var reveals = WallSweeps.Reveals(document, wall, type, 0, 3000);
        var recess = Assert.Single(reveals);
        Assert.Equal((1000.0, 1100.0, WallSide.Exterior, 15.0), recess);

        // Without Cuts Wall it just sits there, half inside the wall.
        type.Sweeps[0] = type.Sweeps[0] with { CutsWall = false };
        Assert.Empty(WallSweeps.Reveals(document, wall, type, 0, 3000));
        _ = half;
    }

    [Fact]
    public void SweepsOfTwoJoinedWallsMeetInAMitreAtTheCorner()
    {
        var probe = BimDocument.CreateDefault();
        var (document, type, first, half) = Wall(Board(probe));

        // Round the corner, anticlockwise, so both walls' exterior faces are outside it.
        first.Start = new Point2D(0, 0);
        first.End = new Point2D(5000, 0);
        first.Flipped = true;
        var second = new Wall
        {
            Start = new Point2D(5000, 0), End = new Point2D(5000, 5000),
            TypeId = type.Id, LevelId = first.LevelId, UnconnectedHeight = 3000, Flipped = true
        };
        document.Add(second);

        // The outermost point of each sweep, at the bottom: the tip of the mitre.
        Point3D Tip(Wall wall) => ModelMeshBuilder.BuildWall(document, wall).Single(m => m.Kind == MeshKind.Sweep).Positions
            .Where(p => Math.Abs(p.Z) < 1e-6)
            .OrderByDescending(p => p.X - p.Y)
            .First();

        var expected = new Point3D(5000 + half + 20, -half - 20, 0);
        Assert.Equal(expected.X, Tip(first).X, precision: 6);
        Assert.Equal(expected.Y, Tip(first).Y, precision: 6);
        Assert.Equal(expected.X, Tip(second).X, precision: 6);
        Assert.Equal(expected.Y, Tip(second).Y, precision: 6);
    }

    [Fact]
    public void AReturningSweepTurnsRoundAnOpenEnd()
    {
        var probe = BimDocument.CreateDefault();
        var (document, _, wall, half) = Wall(Board(probe) with { Returns = true });
        var bounds = SweepBounds(document, wall);

        // Round both ends, across to the far face, standing out past each end.
        Assert.Equal(-half, bounds.Min.Y, precision: 6);
        Assert.Equal(-20, bounds.Min.X, precision: 6);
        Assert.Equal(5020, bounds.Max.X, precision: 6);

        var (plainDocument, _, plainWall, _) = Wall(Board(probe));
        Assert.Equal(half, SweepBounds(plainDocument, plainWall).Min.Y, precision: 6);
    }

    [Fact]
    public void ADrawnProfileGivesTheSweepItsShape()
    {
        var document = BimDocument.CreateDefault();
        var dado = document.TypesOf<SweepProfileType>().Single(p => p.Name == "Dado Rail");
        var sweep = Board(document) with { ProfileId = dado.Id, Depth = dado.Depth, Height = dado.Height };

        var shape = sweep.Shape(document);
        Assert.Equal(dado.Points.Count, shape.Count);
        Assert.Equal(1, shape.Max(p => p.Out), precision: 9);
        Assert.Equal(1, shape.Max(p => p.Up), precision: 9);

        Assert.Null(SweepProfileType.Problem(dado.Points));
        Assert.NotNull(SweepProfileType.Problem(new[] { new Point2D(0, 0), new Point2D(10, 10), new Point2D(10, 0), new Point2D(0, 10) }));
    }

    [Fact]
    public void APlacedSweepRunsAlongEveryWallItIsOnAtOneHeight()
    {
        var (document, type, first, half) = Wall();
        var second = new Wall
        {
            Start = new Point2D(5000, 0), End = new Point2D(9000, 0), TypeId = type.Id, LevelId = first.LevelId,
            UnconnectedHeight = 3000, BaseOffset = 200
        };
        document.Add(second);

        var skirting = document.TypesOf<WallSweepType>().Single(t => t.Name.StartsWith("Skirting"));
        var placed = new PlacedSweep { TypeId = skirting.Id, LevelId = first.LevelId, Elevation = 900, Side = WallSide.Interior };
        placed.HostWallIds.AddRange(new[] { first.Id, second.Id });
        document.Add(placed);

        var onSecond = placed.On(document, second)!;
        Assert.Equal(700, onSecond.Elevation, precision: 9);

        var meshes = ModelMeshBuilder.Build(document).Where(m => m.ElementId == placed.Id).ToList();
        Assert.Equal(2, meshes.Count);
        Assert.All(meshes, m => Assert.Equal(900, m.Bounds()!.Value.Min.Z, precision: 6));
        Assert.All(meshes, m => Assert.Equal(-half - 20, m.Bounds()!.Value.Min.Y, precision: 6));
        Assert.Equal(9000, placed.Length(document), precision: 6);
    }

    [Fact]
    public void AnUprightSweepStandsFullHeightAtItsPlace()
    {
        var (document, _, wall, half) = Wall();
        var sweepType = new WallSweepType("Pilaster", SweepKind.Sweep) { Depth = 60, Height = 300, MaterialId = Material(document) };
        document.AddType(sweepType);
        var placed = new PlacedSweep { TypeId = sweepType.Id, LevelId = wall.LevelId, Vertical = true, Along = 2000 };
        placed.HostWallIds.Add(wall.Id);
        document.Add(placed);

        var bounds = ModelMeshBuilder.BuildWall(document, wall).Single(m => m.ElementId == placed.Id).Bounds()!.Value;
        Assert.Equal((1850.0, 2150.0), (bounds.Min.X, bounds.Max.X));
        Assert.Equal((0.0, 3000.0), (bounds.Min.Z, bounds.Max.Z));
        Assert.Equal(half + 60, bounds.Max.Y, precision: 6);

        // A section across it cuts it full height.
        var marker = new SectionMarker { Name = "A", Start = new Point2D(2000, -2000), End = new Point2D(2000, 2000), LevelId = wall.LevelId };
        document.Add(marker);
        var cut = Assert.Single(SectionProjection.Build(document, marker).Pieces, p => p.ElementId == placed.Id);
        Assert.Equal(3000, cut.Bounds.Top - cut.Bounds.Bottom, precision: 6);
    }

    [Fact]
    public void PlacedRevealsCutGroovesLyingAndStanding()
    {
        var (document, _, wall, half) = Wall();
        var reveal = document.TypesOf<WallSweepType>().Single(t => t.Name.StartsWith("Reveal"));

        var lying = new PlacedSweep { TypeId = reveal.Id, LevelId = wall.LevelId, Kind = SweepKind.Reveal, Elevation = 1500 };
        lying.HostWallIds.Add(wall.Id);
        var standing = new PlacedSweep { TypeId = reveal.Id, LevelId = wall.LevelId, Kind = SweepKind.Reveal, Vertical = true, Along = 3000 };
        standing.HostWallIds.Add(wall.Id);
        document.Add(lying);
        document.Add(standing);

        var points = ModelMeshBuilder.BuildWall(document, wall).Where(m => m.ElementId == wall.Id && m.Kind == MeshKind.Wall)
            .SelectMany(m => m.Positions).ToList();

        // The standing groove: along it the exterior face is 20 back, top to bottom.
        Assert.Contains(points, p => Math.Abs(p.X - 2990) < 1e-6 && Math.Abs(p.Y - (half - 20)) < 1e-6);
        Assert.DoesNotContain(points, p => p.X > 2990 + 1e-6 && p.X < 3010 - 1e-6 && p.Y > half - 20 + 1e-6);

        // The lying one: a set-back face between 1500 and 1520.
        Assert.Contains(points, p => Math.Abs(p.Z - 1500) < 1e-6 && Math.Abs(p.Y - (half - 20)) < 1e-6);
        Assert.DoesNotContain(ModelMeshBuilder.BuildWall(document, wall), m => m.ElementId == lying.Id);
    }

    [Fact]
    public void PlacedSweepsTheirTypesAndProfilesAreSaved()
    {
        var (document, _, wall, _) = Wall();
        var dado = document.TypesOf<WallSweepType>().Single(t => t.Name.StartsWith("Dado"));
        var placed = new PlacedSweep
        {
            TypeId = dado.Id, LevelId = wall.LevelId, Elevation = 900, Offset = 5, Flip = true, ReturnAtEnd = true, Mark = "DR1"
        };
        placed.HostWallIds.Add(wall.Id);
        document.Add(placed);

        var path = Path.Combine(Path.GetTempPath(), $"bimtest-{Guid.NewGuid():N}{ProjectFile.Extension}");
        try
        {
            ProjectFile.Save(document, path);
            var reloaded = ProjectFile.Load(path);

            var again = reloaded.Elements.OfType<PlacedSweep>().Single();
            Assert.Equal((900.0, 5.0, true, true, "DR1"), (again.Elevation, again.Offset, again.Flip, again.ReturnAtEnd, again.Mark));
            Assert.Equal(new[] { wall.Id }, again.HostWallIds);

            var type = reloaded.FindType<WallSweepType>(dado.Id)!;
            Assert.Equal(dado.ProfileId, type.ProfileId);
            Assert.Equal(10, reloaded.FindType<SweepProfileType>(type.ProfileId!.Value)!.Points.Count);
        }
        finally
        {
            File.Delete(path);
        }

        using var model = IfcExport.Build(document);
        Assert.Single(model.Instances.OfType<IfcBuildingElementProxy>());
    }
}
