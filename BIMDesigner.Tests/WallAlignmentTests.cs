using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Infrastructure.Serialization;

namespace BIMDesigner.Tests;

/// <summary>
/// A wall running into one of a different thickness: it lines up with a face of it rather than
/// standing in the middle, which is what a shopfront between two masonry piers has to do.
/// </summary>
public class WallAlignmentTests
{
    private static (BimDocument Document, Wall Thick, Wall Thin) EndToEnd(bool sameType = false)
    {
        var document = BimDocument.CreateDefault();
        var thickType = document.TypesOf<WallType>().First(t => t.Name.StartsWith("Exterior"));
        var thinType = sameType ? thickType : document.TypesOf<WallType>().First(t => t.Name.StartsWith("Interior"));
        var level = document.Levels[0].Id;

        var thick = new Wall { Start = new Point2D(0, 0), End = new Point2D(3000, 0), TypeId = thickType.Id, LevelId = level };
        var thin = new Wall { Start = new Point2D(3000, 0), End = new Point2D(6000, 0), TypeId = thinType.Id, LevelId = level };
        document.Add(thick);
        document.Add(thin);
        return (document, thick, thin);
    }

    private static double InteriorFace(BimDocument document, Wall wall, double along)
    {
        var type = document.GetWallType(wall)!;
        return wall.PointAt(type.Structure, along, -type.Width / 2).Y;
    }

    private static double ExteriorFace(BimDocument document, Wall wall, double along)
    {
        var type = document.GetWallType(wall)!;
        return wall.PointAt(type.Structure, along, type.Width / 2).Y;
    }

    [Fact]
    public void AThinWallLinesUpWithTheInnerFaceOfTheThickOneItMeets()
    {
        var (document, thick, thin) = EndToEnd();
        var thickWidth = document.GetWallType(thick)!.Width;
        var thinWidth = document.GetWallType(thin)!.Width;
        Assert.True(thickWidth > thinWidth);

        // Drawn on the same line, the thin wall stands off both faces of the thick one.
        Assert.NotEqual(InteriorFace(document, thick, thick.Length), InteriorFace(document, thin, 0), precision: 3);

        thin.AcrossOffset = WallAlignment.OffsetFor(document, thin)!.Value;

        // Now the inner faces are one plane, and the step is all on the outside.
        Assert.Equal(InteriorFace(document, thick, thick.Length), InteriorFace(document, thin, 0), precision: 6);
        Assert.Equal(-(thickWidth - thinWidth) / 2, thin.AcrossOffset, precision: 6);
    }

    [Fact]
    public void ItCanLineUpWithTheOuterFaceInstead()
    {
        var (document, thick, thin) = EndToEnd();

        thin.AcrossOffset = WallAlignment.OffsetFor(document, thin, WallAlignment.Face.Exterior)!.Value;
        Assert.Equal(ExteriorFace(document, thick, thick.Length), ExteriorFace(document, thin, 0), precision: 6);

        // Flush with one face, there is nothing left to do but the other.
        Assert.Null(WallAlignment.OffsetFor(document, thin, WallAlignment.Face.Exterior));
        Assert.NotNull(WallAlignment.OffsetFor(document, thin, WallAlignment.Face.Interior));
    }

    [Fact]
    public void WallsOfTheSameThicknessAreLeftAlone()
    {
        var (document, _, thin) = EndToEnd(sameType: true);
        Assert.Null(WallAlignment.OffsetFor(document, thin));
    }

    [Fact]
    public void AWallTurningACornerIsNotLinedUp()
    {
        var (document, _, thin) = EndToEnd();

        // The same two walls, but the thin one turns off at a right angle.
        thin.End = new Point2D(3000, 3000);
        Assert.Null(WallAlignment.OffsetFor(document, thin));
        Assert.False(WallAlignment.Joins(document, thin));
    }

    [Fact]
    public void AWallThatMeetsNothingIsLeftAlone()
    {
        var document = BimDocument.CreateDefault();
        var wall = new Wall
        {
            Start = new Point2D(0, 0), End = new Point2D(3000, 0),
            TypeId = document.TypesOf<WallType>().First().Id, LevelId = document.Levels[0].Id
        };
        document.Add(wall);

        Assert.Null(WallAlignment.OffsetFor(document, wall));
    }

    [Fact]
    public void TheOffsetIsSaved()
    {
        var (document, _, thin) = EndToEnd();
        thin.AcrossOffset = WallAlignment.OffsetFor(document, thin)!.Value;

        var path = Path.Combine(Path.GetTempPath(), $"bimtest-{Guid.NewGuid():N}{ProjectFile.Extension}");
        try
        {
            ProjectFile.Save(document, path);
            var copy = ProjectFile.Load(path).Walls.Single(w => w.Id == thin.Id);
            Assert.Equal(thin.AcrossOffset, copy.AcrossOffset, precision: 6);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
