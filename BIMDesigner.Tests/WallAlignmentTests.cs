using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;
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
    public void ACornerOfWallsOfVeryDifferentThicknessButtsRatherThanMitres()
    {
        var document = BimDocument.CreateDefault();
        var thick = document.TypesOf<WallType>().First(t => t.Name.StartsWith("Exterior"));
        var curtain = document.TypesOf<CurtainWallType>().Single(t => t.Name.Contains("Storefront"));
        var level = document.Levels[0].Id;

        var side = new Wall { Start = new Point2D(0, 0), End = new Point2D(0, 4000), TypeId = thick.Id, LevelId = level, UnconnectedHeight = 3000 };
        var front = new Wall { Start = new Point2D(5000, 0), End = new Point2D(0, 0), TypeId = curtain.Id, LevelId = level, UnconnectedHeight = 3000 };
        document.Add(side);
        document.Add(front);

        // A mitre between a 150 mm shopfront and a 330 mm wall is a long skew cut across the
        // thick wall. The thick one runs through square to the corner instead.
        var (start, _) = WallJoins.GetEndCuts(document, side, thick);
        Assert.NotEqual(WallEndCondition.Mitre, start?.Condition);

        // Two walls of much the same thickness still mitre, which is what a corner should be.
        var other = new Wall { Start = new Point2D(9000, 0), End = new Point2D(9000, 4000), TypeId = thick.Id, LevelId = level, UnconnectedHeight = 3000 };
        var along = new Wall { Start = new Point2D(14000, 0), End = new Point2D(9000, 0), TypeId = thick.Id, LevelId = level, UnconnectedHeight = 3000 };
        document.Add(other);
        document.Add(along);

        var (mitred, _) = WallJoins.GetEndCuts(document, other, thick);
        Assert.Equal(WallEndCondition.Mitre, mitred?.Condition);
    }

    [Fact]
    public void ACurtainWallStopsAgainstWhatItMeets()
    {
        var document = BimDocument.CreateDefault();
        var thick = document.TypesOf<WallType>().First(t => t.Name.StartsWith("Exterior"));
        var curtain = document.TypesOf<CurtainWallType>().Single(t => t.Name.Contains("Storefront"));
        var level = document.Levels[0].Id;

        var front = new Wall { Start = new Point2D(0, 0), End = new Point2D(5000, 0), TypeId = curtain.Id, LevelId = level, UnconnectedHeight = 3000 };
        document.Add(front);

        double Start() => ModelMeshBuilder.Bounds(
            ModelMeshBuilder.BuildWall(document, front).Where(m => m.Kind == BIMDesigner.Core.Geometry.MeshKind.Mullion))!.Value.Min.X;

        // On its own it runs the whole way.
        Assert.True(Start() < 1, "the wall does not start at its own beginning");

        // Meeting a 330 mm wall at a corner, it stops against its face rather than running
        // into it and being drawn one over the other.
        document.Add(new Wall { Start = new Point2D(0, 0), End = new Point2D(0, 4000), TypeId = thick.Id, LevelId = level, UnconnectedHeight = 3000 });

        Assert.True(Start() > 100, "the wall still runs into the wall it meets");
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
