using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;
using BIMDesigner.Infrastructure.Interoperability;
using BIMDesigner.Infrastructure.Serialization;
using Xbim.Ifc4.GeometricModelResource;

namespace BIMDesigner.Tests;

/// <summary>Slanted and tapered walls.</summary>
public class LeaningWallTests
{
    /// <summary>A 3 m brick-on-block wall along the X axis; its exterior is up the page, toward +Y.</summary>
    private static (BimDocument Document, WallType Type, Wall Wall) Project()
    {
        var document = BimDocument.CreateDefault();
        var type = document.TypesOf<WallType>().Single(t => t.Name.StartsWith("Exterior"));
        var wall = new Wall
        {
            Start = new Point2D(0, 0), End = new Point2D(5000, 0),
            TypeId = type.Id, LevelId = document.Levels[0].Id, UnconnectedHeight = 3000
        };
        document.Add(wall);
        return (document, type, wall);
    }

    private static IEnumerable<Point3D> Points(BimDocument document, Wall wall) =>
        ModelMeshBuilder.BuildWall(document, wall).Where(m => m.Kind == MeshKind.Wall).SelectMany(m => m.Positions);

    [Fact]
    public void ASlantedWallLeansByItsAngle()
    {
        var (document, type, wall) = Project();
        wall.CrossSection = WallCrossSection.Slanted;
        wall.SlantAngle = 10;

        var points = Points(document, wall).ToList();
        var half = type.Width / 2;
        var lean = Math.Tan(10 * Math.PI / 180) * 3000;

        // At the base the wall is where it was drawn; at the top it has moved toward the exterior.
        Assert.Equal(half, points.Where(p => p.Z < 1).Max(p => p.Y), precision: 6);
        Assert.Equal(half + lean, points.Where(p => p.Z > 2999).Max(p => p.Y), precision: 6);
        Assert.Equal(-half + lean, points.Where(p => p.Z > 2999).Min(p => p.Y), precision: 6);
    }

    [Fact]
    public void ATaperedWallIsThinnerAtTheTopAndItsFinishesKeepTheirThickness()
    {
        var (document, type, wall) = Project();
        type.ExteriorTaperAngle = 1;
        type.InteriorTaperAngle = 0.5;
        wall.CrossSection = WallCrossSection.Tapered;

        var points = Points(document, wall).ToList();
        var half = type.Width / 2;
        var exterior = Math.Tan(1 * Math.PI / 180) * 3000;
        var interior = Math.Tan(0.5 * Math.PI / 180) * 3000;

        var top = points.Where(p => p.Z > 2999).ToList();
        Assert.Equal(half - exterior, top.Max(p => p.Y), precision: 6);
        Assert.Equal(-half + interior, top.Min(p => p.Y), precision: 6);

        // The brick is still 100 thick at the top: only the core takes up the change.
        var brick = ModelMeshBuilder.BuildWall(document, wall).First(m => m.Kind == MeshKind.Wall);
        var brickTop = brick.Positions.Where(p => p.Z > 2999).ToList();
        Assert.Equal(100, brickTop.Max(p => p.Y) - brickTop.Min(p => p.Y), precision: 6);
    }

    [Fact]
    public void AWallCannotTaperThroughItself()
    {
        var (document, type, wall) = Project();
        wall.CrossSection = WallCrossSection.Tapered;
        wall.OverrideTaper = true;
        wall.ExteriorTaper = 45;
        wall.InteriorTaper = 45;

        var core = WallLean.VariableBand(type.Structure);
        var top = WallLean.Shift(wall, type, core.Outer, 3000) + core.Outer - (WallLean.Shift(wall, type, core.Inner, 3000) + core.Inner);

        Assert.True(top >= 1 - 1e-9, $"The core closed up to {top:0.###} mm.");
    }

    [Fact]
    public void APlanShowsASlantedWallWhereItIsCut()
    {
        var (document, type, wall) = Project();
        wall.CrossSection = WallCrossSection.Slanted;
        wall.SlantAngle = -15;

        var moved = WallLean.Move(wall, type, new Point2D(2500, 0), StackedWallType.PlanCutHeight);
        Assert.Equal(-Math.Tan(15 * Math.PI / 180) * 1200, moved.Y, precision: 6);
        Assert.Equal(2500, moved.X, precision: 6);
    }

    [Fact]
    public void ASectionDrawsTheLean()
    {
        var (document, _, wall) = Project();
        wall.CrossSection = WallCrossSection.Slanted;
        wall.SlantAngle = 20;

        var marker = new SectionMarker
        {
            Name = "A", Start = new Point2D(2500, -2000), End = new Point2D(2500, 2000), LevelId = wall.LevelId
        };
        document.Add(marker);

        var cut = SectionProjection.Build(document, marker).Pieces
            .Where(p => p.ElementId == wall.Id && p.Depth == SectionDepth.Cut)
            .ToList();

        Assert.NotEmpty(cut);
        Assert.All(cut, piece =>
        {
            var shape = Assert.IsAssignableFrom<IReadOnlyList<(double X, double Y)>>(piece.Shape);
            var bottomLeft = shape[0].X;
            var topLeft = shape[3].X;
            Assert.Equal(Math.Tan(20 * Math.PI / 180) * (shape[3].Y - shape[0].Y), topLeft - bottomLeft, precision: 6);
        });
    }

    [Fact]
    public void TheLeanIsAParameterWithLimits()
    {
        var (document, _, wall) = Project();

        var section = wall.GetInstanceParameters(document).Single(p => p.Name == "Cross-Section");
        Assert.True(section.TrySet("Slanted"));

        var angle = wall.GetInstanceParameters(document).Single(p => p.Name == "Angle from Vertical");
        Assert.True(angle.TrySet(12.5));
        Assert.False(angle.TrySet(80.0));
        Assert.Equal(12.5, wall.SlantAngle);
    }

    [Fact]
    public void ALeaningWallExportsAsItsTriangles()
    {
        var (document, _, wall) = Project();
        wall.CrossSection = WallCrossSection.Slanted;
        wall.SlantAngle = 8;

        using var model = IfcExport.Build(document);

        Assert.NotEmpty(model.Instances.OfType<IfcTriangulatedFaceSet>());
        Assert.DoesNotContain(model.Instances.OfType<IfcExtrudedAreaSolid>(), s => s.Depth == 3000);
    }

    [Fact]
    public void TheLeanIsSaved()
    {
        var (document, type, wall) = Project();
        wall.CrossSection = WallCrossSection.Tapered;
        wall.OverrideTaper = true;
        wall.ExteriorTaper = 3;
        wall.InteriorTaper = 1.5;
        type.ExteriorTaperAngle = 4;

        var path = Path.Combine(Path.GetTempPath(), $"bimtest-{Guid.NewGuid():N}{ProjectFile.Extension}");
        try
        {
            ProjectFile.Save(document, path);
            var reloaded = ProjectFile.Load(path);
            var copy = reloaded.Walls.Single();

            Assert.Equal(WallCrossSection.Tapered, copy.CrossSection);
            Assert.True(copy.OverrideTaper);
            Assert.Equal(3, copy.ExteriorTaper);
            Assert.Equal(1.5, copy.InteriorTaper);
            Assert.Equal(4, reloaded.GetWallType(copy)!.ExteriorTaperAngle);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
