using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;
using BIMDesigner.Infrastructure.Interoperability;
using BIMDesigner.Infrastructure.Serialization;
using Xbim.Ifc4.GeometricModelResource;
using Xbim.Ifc4.SharedBldgElements;

namespace BIMDesigner.Tests;

/// <summary>Sweeps and reveals run along wall faces.</summary>
public class WallSweepTests
{
    /// <summary>A 5 m brick-on-block wall along the X axis, exterior toward +Y, 3 m high.</summary>
    private static (BimDocument Document, WallType Type, Wall Wall) Project(params WallSweep[] sweeps)
    {
        var document = BimDocument.CreateDefault();
        var type = document.TypesOf<WallType>().Single(t => t.Name.StartsWith("Exterior"));
        type.Sweeps.AddRange(sweeps);

        var wall = new Wall
        {
            Start = new Point2D(0, 0), End = new Point2D(5000, 0),
            TypeId = type.Id, LevelId = document.Levels[0].Id, UnconnectedHeight = 3000
        };
        document.Add(wall);
        return (document, type, wall);
    }

    private static Guid AnyMaterial(BimDocument document) => document.Materials.First().Id;

    [Fact]
    public void ASkirtingRunsTheLengthOfTheFaceAndStandsOutFromIt()
    {
        var document = BimDocument.CreateDefault();
        var skirting = new WallSweep(SweepKind.Sweep, SweepProfile.Skirting, WallSide.Interior, 20, 100, 0, false, AnyMaterial(document));
        var (doc, type, wall) = Project(skirting);

        var sweep = ModelMeshBuilder.BuildWall(doc, wall).Single(m => m.Kind == MeshKind.Sweep);
        var bounds = sweep.Bounds()!.Value;
        var half = type.Width / 2;

        Assert.Equal(-half - 20, bounds.Min.Y, precision: 6);
        Assert.Equal(-half, bounds.Max.Y, precision: 6);
        Assert.Equal(0, bounds.Min.Z, precision: 6);
        Assert.Equal(100, bounds.Max.Z, precision: 6);
        Assert.Equal(0, bounds.Min.X, precision: 6);
        Assert.Equal(5000, bounds.Max.X, precision: 6);
    }

    [Fact]
    public void ADoorwayBreaksASkirtingButNotACorniceAboveIt()
    {
        var document = BimDocument.CreateDefault();
        var material = AnyMaterial(document);
        var (doc, type, wall) = Project(
            new WallSweep(SweepKind.Sweep, SweepProfile.Skirting, WallSide.Interior, 20, 100, 0, false, material),
            new WallSweep(SweepKind.Sweep, SweepProfile.Cornice, WallSide.Interior, 80, 150, 0, true, material));

        doc.Add(new Door
        {
            TypeId = doc.TypesOf<DoorType>().First().Id, LevelId = wall.LevelId, HostWallId = wall.Id, DistanceAlongWall = 2500
        });

        Assert.Equal(2, WallSweeps.Runs(doc, wall, type, WallSide.Interior, 0, 100).Count);
        Assert.Single(WallSweeps.Runs(doc, wall, type, WallSide.Interior, 2850, 3000));
    }

    [Fact]
    public void ARevealSetsTheFaceBackWithinItsHeight()
    {
        var document = BimDocument.CreateDefault();
        var groove = new WallSweep(SweepKind.Reveal, SweepProfile.Rectangle, WallSide.Exterior, 30, 40, 1000, false, AnyMaterial(document));
        var (doc, type, wall) = Project(groove);
        var half = type.Width / 2;

        var points = ModelMeshBuilder.BuildWall(doc, wall).Where(m => m.Kind == MeshKind.Wall).SelectMany(m => m.Positions).ToList();

        // Below the groove the wall reaches its face.
        Assert.Equal(half, points.Where(p => p.Z <= 1000).Max(p => p.Y), precision: 6);

        // Sampled through the middle of the groove, nothing of the wall stands in front of the set-back face.
        var mesh = ModelMeshBuilder.BuildWall(doc, wall).Where(m => m.Kind == MeshKind.Wall).ToList();
        foreach (var m in mesh)
        {
            for (var i = 0; i + 2 < m.Indices.Count; i += 3)
            {
                var tri = new[] { m.Positions[m.Indices[i]], m.Positions[m.Indices[i + 1]], m.Positions[m.Indices[i + 2]] };
                if (tri.All(p => p.Z > 1000 + 1e-6 && p.Z < 1040 - 1e-6)) Assert.All(tri, p => Assert.True(p.Y <= half - 30 + 1e-6));
            }
        }
    }

    [Fact]
    public void ASectionShowsTheProfileAndTheGroove()
    {
        var document = BimDocument.CreateDefault();
        var material = AnyMaterial(document);
        var (doc, type, wall) = Project(
            new WallSweep(SweepKind.Sweep, SweepProfile.Cornice, WallSide.Exterior, 80, 150, 0, true, material),
            new WallSweep(SweepKind.Reveal, SweepProfile.Rectangle, WallSide.Exterior, 30, 40, 1000, false, material));

        var marker = new SectionMarker
        {
            Name = "A", Start = new Point2D(2500, -2000), End = new Point2D(2500, 2000), LevelId = wall.LevelId
        };
        doc.Add(marker);

        var pieces = SectionProjection.Build(doc, marker).Pieces.Where(p => p.ElementId == wall.Id).ToList();

        // The cornice: a shaped piece standing out from the exterior face at the top.
        var cornice = Assert.Single(pieces, p => p.Shape is { Count: > 4 });
        var faceX = marker.DistanceAlong(new Point2D(2500, type.Width / 2));
        Assert.Equal(faceX + 80, cornice.Bounds.Right, precision: 6);
        Assert.Equal(3000, cornice.Bounds.Top, precision: 6);

        // The groove: no cut piece reaches the face between 1000 and 1040.
        Assert.DoesNotContain(pieces, p => p.Depth == SectionDepth.Cut && p.Shape is null &&
                                         p.Bounds.Bottom < 1039 && p.Bounds.Top > 1001 && p.Bounds.Right > faceX - 30 + 1e-6);
    }

    [Fact]
    public void SweepsAreCheckedBeforeTheyAreApplied()
    {
        var (document, type, _) = Project();
        var design = WallTypeDesign.Of(type);
        var material = AnyMaterial(document);

        Assert.Null((design with { Sweeps = new[] { new WallSweep(SweepKind.Sweep, SweepProfile.Bead, WallSide.Exterior, 30, 30, 900, false, material) } })
            .Problem(document, type));
        Assert.NotNull((design with { Sweeps = new[] { new WallSweep(SweepKind.Sweep, SweepProfile.Bead, WallSide.Exterior, 0, 30, 900, false, material) } })
            .Problem(document, type));
        Assert.NotNull((design with { Sweeps = new[] { new WallSweep(SweepKind.Reveal, SweepProfile.Rectangle, WallSide.Exterior, 400, 30, 900, false, material) } })
            .Problem(document, type));
    }

    [Fact]
    public void SweepsAreSavedWithTheirType()
    {
        var document = BimDocument.CreateDefault();
        var skirting = new WallSweep(SweepKind.Sweep, SweepProfile.Skirting, WallSide.Interior, 20, 100, 0, false, AnyMaterial(document));
        var groove = new WallSweep(SweepKind.Reveal, SweepProfile.Rectangle, WallSide.Exterior, 30, 40, 200, true, AnyMaterial(document));
        var (doc, type, _) = Project(skirting, groove);

        var path = Path.Combine(Path.GetTempPath(), $"bimtest-{Guid.NewGuid():N}{ProjectFile.Extension}");
        try
        {
            ProjectFile.Save(doc, path);
            var reloaded = ProjectFile.Load(path).FindType<WallType>(type.Id)!;

            Assert.Equal(new[] { skirting, groove }.Select(s => s with { MaterialId = s.MaterialId }), reloaded.Sweeps);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SweepsExportAlongsideTheirWall()
    {
        var document = BimDocument.CreateDefault();
        var (doc, _, _) = Project(
            new WallSweep(SweepKind.Sweep, SweepProfile.Skirting, WallSide.Interior, 20, 100, 0, false, AnyMaterial(document)),
            new WallSweep(SweepKind.Reveal, SweepProfile.Rectangle, WallSide.Exterior, 30, 40, 1000, false, AnyMaterial(document)));

        using var model = IfcExport.Build(doc);

        Assert.Single(model.Instances.OfType<IfcWall>());
        Assert.Single(model.Instances.OfType<IfcBuildingElementProxy>());
        Assert.NotEmpty(model.Instances.OfType<IfcTriangulatedFaceSet>());
    }
}
