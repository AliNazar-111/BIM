using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;
using BIMDesigner.Infrastructure.Serialization;

namespace BIMDesigner.Tests;

/// <summary>Double-slanted walls: one lean up to a break height, another above it.</summary>
public class DoubleSlantedWallTests
{
    /// <summary>A 3 m wall along the X axis, leaning out 15 degrees to 1.2 m and back in 10 degrees above.</summary>
    private static (BimDocument Document, WallType Type, Wall Wall) Project()
    {
        var document = BimDocument.CreateDefault();
        var type = document.TypesOf<WallType>().Single(t => t.Name.StartsWith("Exterior"));
        var wall = new Wall
        {
            Start = new Point2D(0, 0), End = new Point2D(5000, 0),
            TypeId = type.Id, LevelId = document.Levels[0].Id, UnconnectedHeight = 3000,
            CrossSection = WallCrossSection.DoubleSlanted, SlantAngle = 15, UpperSlantAngle = -10, SlantBreakHeight = 1200
        };
        document.Add(wall);
        return (document, type, wall);
    }

    private static double Tan(double degrees) => Math.Tan(degrees * Math.PI / 180);

    [Fact]
    public void EachPartLeansByItsOwnAngle()
    {
        var (_, type, wall) = Project();

        Assert.True(WallLean.Leans(wall, type));
        Assert.Equal(Tan(15) * 600, WallLean.Shift(wall, type, 0, 600), precision: 9);
        Assert.Equal(Tan(15) * 1200, WallLean.Shift(wall, type, 0, 1200), precision: 9);
        Assert.Equal(Tan(15) * 1200 - Tan(10) * 1800, WallLean.Shift(wall, type, 0, 3000), precision: 9);
    }

    [Fact]
    public void The3DWallFoldsAtTheBreak()
    {
        var (document, type, wall) = Project();
        var half = type.Width / 2;

        var points = ModelMeshBuilder.BuildWall(document, wall)
            .Where(m => m.Kind == MeshKind.Wall)
            .SelectMany(m => m.Positions)
            .ToList();

        // There are points on the fold, and the wall is furthest out there.
        var fold = points.Where(p => Math.Abs(p.Z - 1200) < 1e-6).ToList();
        Assert.NotEmpty(fold);
        Assert.Equal(half + Tan(15) * 1200, fold.Max(p => p.Y), precision: 6);
        Assert.Equal(half + Tan(15) * 1200, points.Max(p => p.Y), precision: 6);
        Assert.Equal(half + Tan(15) * 1200 - Tan(10) * 1800, points.Where(p => p.Z > 2999).Max(p => p.Y), precision: 6);
    }

    [Fact]
    public void ASectionHasACornerAtTheBreak()
    {
        var (document, _, wall) = Project();
        var marker = new SectionMarker
        {
            Name = "A", Start = new Point2D(2500, -2000), End = new Point2D(2500, 2000), LevelId = wall.LevelId
        };
        document.Add(marker);

        var cut = SectionProjection.Build(document, marker).Pieces
            .Where(p => p.ElementId == wall.Id && p.Depth == SectionDepth.Cut)
            .ToList();

        Assert.NotEmpty(cut);
        Assert.Contains(cut, piece => piece.Shape is { Count: 6 } shape && shape.Any(corner => Math.Abs(corner.Y - 1200) < 1e-6));
    }

    [Fact]
    public void TheAnglesAndBreakAreParameters()
    {
        var (document, _, wall) = Project();
        var parameters = wall.GetInstanceParameters(document).ToList();

        Assert.Contains(parameters, p => p.Name == "Lower Angle from Vertical");
        Assert.Contains(parameters, p => p.Name == "Upper Angle from Vertical");
        var breakHeight = parameters.Single(p => p.Name == "Slant Break Height");

        Assert.False(breakHeight.TrySet(-5.0));
        Assert.True(breakHeight.TrySet(900.0));
        Assert.Equal(900, wall.SlantBreakHeight);
    }

    [Fact]
    public void ADoubleSlantedWallIsSavedAndLoaded()
    {
        var (document, _, wall) = Project();
        var path = Path.Combine(Path.GetTempPath(), $"bimtest-{Guid.NewGuid():N}{ProjectFile.Extension}");
        try
        {
            ProjectFile.Save(document, path);
            var copy = ProjectFile.Load(path).Walls.Single(w => w.Id == wall.Id);

            Assert.Equal(WallCrossSection.DoubleSlanted, copy.CrossSection);
            Assert.Equal(15, copy.SlantAngle);
            Assert.Equal(-10, copy.UpperSlantAngle);
            Assert.Equal(1200, copy.SlantBreakHeight);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
