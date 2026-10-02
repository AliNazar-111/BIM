using BIMDesigner.Core.Annotation;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Infrastructure.Serialization;

namespace BIMDesigner.Tests;

/// <summary>
/// Dimensions to a wall's centreline, its faces, the middle of its core and its core faces -
/// measured square across, following the wall as it moves or changes type.
/// </summary>
public class DimensionToWallFaceTests
{
    /// <summary>Two walls of the exterior type, their centrelines 4 m apart, both drawn left to right.</summary>
    private static (BimDocument Document, Wall Lower, Wall Upper, WallType Type) TwoWalls()
    {
        var document = BimDocument.CreateDefault();
        var type = document.TypesOf<WallType>().First(t => t.Name.StartsWith("Exterior"));
        var lower = new Wall { Start = new Point2D(0, 0), End = new Point2D(6000, 0), TypeId = type.Id, LevelId = document.Levels[0].Id, UnconnectedHeight = 3000 };
        var upper = new Wall { Start = new Point2D(0, 4000), End = new Point2D(6000, 4000), TypeId = type.Id, LevelId = document.Levels[0].Id, UnconnectedHeight = 3000 };
        document.Add(lower);
        document.Add(upper);
        return (document, lower, upper, type);
    }

    private static Dimension Between(BimDocument document, DimensionReference start, DimensionReference end)
    {
        var dimension = new Dimension { Start = start, End = end, LevelId = document.Levels[0].Id };
        document.Add(dimension);
        return dimension;
    }

    [Fact]
    public void BetweenTwoWallsItMeasuresSquareAcrossToTheLinesPicked()
    {
        var (document, lower, upper, type) = TwoWalls();
        var structure = type.Structure;
        var half = structure.TotalWidth / 2;

        // Picked at different places along the walls, the measurement is still straight across.
        var centres = Between(document,
            DimensionReference.ToWallLine(lower, DimensionAnchor.WallCentreline, new Point2D(1000, 0)),
            DimensionReference.ToWallLine(upper, DimensionAnchor.WallCentreline, new Point2D(4500, 4000)));
        Assert.Equal(4000, centres.Measure(document), 6);
        Assert.Equal(centres.StartPoint(document).X, centres.EndPoint(document).X, 6);

        // The lower wall's exterior faces up toward the upper wall, whose interior faces down.
        var clear = Between(document,
            DimensionReference.ToWallLine(lower, DimensionAnchor.WallExteriorFace, new Point2D(2000, 100)),
            DimensionReference.ToWallLine(upper, DimensionAnchor.WallInteriorFace, new Point2D(2500, 3900)));
        Assert.Equal(4000 - 2 * half, clear.Measure(document), 6);

        var cores = Between(document,
            DimensionReference.ToWallLine(lower, DimensionAnchor.WallCoreExterior, new Point2D(2000, 0)),
            DimensionReference.ToWallLine(upper, DimensionAnchor.WallCoreInterior, new Point2D(2000, 4000)));
        Assert.Equal(4000 - (half - structure.ExteriorWidth) - (half - structure.InteriorWidth), cores.Measure(document), 6);

        var coreCentres = Between(document,
            DimensionReference.ToWallLine(lower, DimensionAnchor.WallCoreCentre, new Point2D(2000, 0)),
            DimensionReference.ToWallLine(upper, DimensionAnchor.WallCoreCentre, new Point2D(2000, 4000)));
        Assert.Equal(4000, coreCentres.Measure(document), 6);
        Assert.True(coreCentres.IsAssociative(document));
    }

    [Fact]
    public void ItFollowsTheWallWhenItMovesOrChangesType()
    {
        var (document, lower, upper, type) = TwoWalls();
        var clear = Between(document,
            DimensionReference.ToWallLine(lower, DimensionAnchor.WallExteriorFace, new Point2D(2000, 0)),
            DimensionReference.ToWallLine(upper, DimensionAnchor.WallInteriorFace, new Point2D(2000, 4000)));

        new Core.Documents.Commands.MoveElementsCommand(new[] { upper }, new Vector2D(0, 500)).Redo();
        Assert.Equal(4500 - type.Structure.TotalWidth, clear.Measure(document), 6);

        var thinner = document.TypesOf<WallType>().Where(t => t.Structure.TotalWidth > 0).OrderBy(t => t.Structure.TotalWidth).First();
        upper.TypeId = thinner.Id;
        Assert.Equal(4500 - type.Structure.TotalWidth / 2 - thinner.Structure.TotalWidth / 2, clear.Measure(document), 6);
    }

    [Fact]
    public void AClickOnAWallTakesTheLineThatIsPreferred()
    {
        var (document, lower, _, type) = TwoWalls();
        var half = type.Structure.TotalWidth / 2;
        var nearTop = new Point2D(3000, half - 5);
        var nearBottom = new Point2D(3000, -half + 5);

        Assert.Equal(DimensionAnchor.WallCentreline, DimensionReference.ToWall(document, lower, nearTop, DimensionPreference.WallCentrelines).Anchor);
        Assert.Equal(DimensionAnchor.WallExteriorFace, DimensionReference.ToWall(document, lower, nearTop, DimensionPreference.WallFaces).Anchor);
        Assert.Equal(DimensionAnchor.WallInteriorFace, DimensionReference.ToWall(document, lower, nearBottom, DimensionPreference.WallFaces).Anchor);
        Assert.Equal(DimensionAnchor.WallCoreCentre, DimensionReference.ToWall(document, lower, nearTop, DimensionPreference.CentreOfCore).Anchor);
        Assert.Equal(DimensionAnchor.WallCoreExterior, DimensionReference.ToWall(document, lower, nearTop, DimensionPreference.FacesOfCore).Anchor);
        Assert.Equal(DimensionAnchor.WallCoreInterior, DimensionReference.ToWall(document, lower, nearBottom, DimensionPreference.FacesOfCore).Anchor);
    }

    [Fact]
    public void FromAPointToAFaceItMeasuresSquareToTheFace()
    {
        var (document, lower, _, type) = TwoWalls();
        var dimension = Between(document,
            DimensionReference.ToPoint(new Point2D(1500, 2000)),
            DimensionReference.ToWallLine(lower, DimensionAnchor.WallExteriorFace, new Point2D(4000, 0)));

        Assert.Equal(2000 - type.Structure.TotalWidth / 2, dimension.Measure(document), 6);
        Assert.Equal(1500, dimension.EndPoint(document).X, 6);
    }

    [Fact]
    public void WhatItMeasuresToIsSaved()
    {
        var (document, lower, upper, _) = TwoWalls();
        var dimension = Between(document,
            DimensionReference.ToWallLine(lower, DimensionAnchor.WallCoreExterior, new Point2D(2000, 0)),
            DimensionReference.ToWallLine(upper, DimensionAnchor.WallInteriorFace, new Point2D(2000, 4000)));

        var loaded = ProjectFile.FromJson(ProjectFile.ToJson(document));
        var again = loaded.Elements.OfType<Dimension>().Single();
        Assert.Equal(DimensionAnchor.WallCoreExterior, again.Start.Anchor);
        Assert.Equal(DimensionAnchor.WallInteriorFace, again.End.Anchor);
        Assert.Equal(dimension.Measure(document), again.Measure(loaded), 6);
    }
}
