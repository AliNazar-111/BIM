using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;
using BIMDesigner.Infrastructure.Serialization;

namespace BIMDesigner.Tests;

/// <summary>
/// How a roof's eaves are cut (Revit's Rafter Cut and Fascia Depth), and where it is cut off
/// (Cutoff Level and Cutoff Offset).
/// </summary>
public class RoofEaveTests
{
    private static readonly double Angle = 30 * Math.PI / 180;

    /// <summary>A 10 m x 6 m gable roof at 30°, based 3 m up, sloping from the long sides.</summary>
    private static (BimDocument Document, Roof Roof, double Thickness) Gable()
    {
        var document = BimDocument.CreateDefault();
        var type = document.TypesOf<RoofType>().First();

        var roof = new Roof { TypeId = type.Id, LevelId = document.Levels[0].Id, HeightOffset = 3000 };
        roof.SetBoundary(new[]
        {
            new Point2D(0, 0), new Point2D(10000, 0), new Point2D(10000, 6000), new Point2D(0, 6000)
        });

        roof.SetEdges(new[]
        {
            new RoofEdge { DefinesSlope = true, SlopeDegrees = 30 },
            new RoofEdge(),
            new RoofEdge { DefinesSlope = true, SlopeDegrees = 30 },
            new RoofEdge()
        });

        document.Add(roof);
        return (document, roof, type.Thickness);
    }

    private static (double Low, double High) Bounds(BimDocument document, Roof roof)
    {
        var meshes = ModelMeshBuilder.Build(document).Where(mesh => mesh.ElementId == roof.Id).ToList();
        var bounds = ModelMeshBuilder.Bounds(meshes)!.Value;
        return (bounds.Min.Z, bounds.Max.Z);
    }

    /// <summary>The roof as a section across its width cuts it: every piece's corners at the edge given.</summary>
    private static List<(double X, double Y)> CornersAt(BimDocument document, Roof roof, double across)
    {
        var marker = new SectionMarker
        {
            Name = "A", Start = new Point2D(5000, -1000), End = new Point2D(5000, 7000),
            LevelId = document.Levels[0].Id, ViewDepth = 20000
        };

        document.Add(marker);

        return SectionProjection.Build(document, marker).Pieces
            .Where(piece => piece.ElementId == roof.Id)
            .SelectMany(piece => piece.Shape!)
            .Where(corner => Math.Abs(corner.X - across) < 1e-3)
            .ToList();
    }

    [Fact]
    public void APlumbCutEaveEndsStraightDownThroughTheWholeRoof()
    {
        var (document, roof, thickness) = Gable();
        Assert.Equal(RafterCut.PlumbCut, roof.RafterCut);

        // At the edge the section runs from the underside to the top, upright.
        var edge = CornersAt(document, roof, 1000);   // the south edge is 1 m along the cut line
        Assert.Equal(3000, edge.Min(corner => corner.Y), precision: 3);
        Assert.Equal(3000 + thickness / Math.Cos(Angle), edge.Max(corner => corner.Y), precision: 3);
    }

    [Fact]
    public void ATwoCutPlumbEaveIsUprightForTheFasciaDepthThenLevel()
    {
        var (document, roof, thickness) = Gable();
        roof.RafterCut = RafterCut.TwoCutPlumb;
        roof.FasciaDepth = 150;

        var top = 3000 + thickness / Math.Cos(Angle);

        // The upright face at the edge is only the fascia depth deep...
        var edge = CornersAt(document, roof, 1000);
        Assert.Equal(top - 150, edge.Min(corner => corner.Y), precision: 3);
        Assert.Equal(top, edge.Max(corner => corner.Y), precision: 3);

        // ...and below it the eave is cut level: nothing of the roof is lower than that anywhere.
        Assert.Equal(top - 150, Bounds(document, roof).Low, precision: 3);
    }

    [Fact]
    public void ATwoCutSquareEaveIsCutSquareToTheSlopeThenLevel()
    {
        var (document, roof, thickness) = Gable();
        roof.RafterCut = RafterCut.TwoCutSquare;
        roof.FasciaDepth = 150;

        var top = 3000 + thickness / Math.Cos(Angle);

        // Square to the slope from the top of the edge: the roof comes to a line there, and
        // the square cut reaches down the fascia depth measured along it, then goes level.
        var edge = CornersAt(document, roof, 1000);
        Assert.All(edge, corner => Assert.Equal(top, corner.Y, precision: 3));

        Assert.Equal(top - 150 * Math.Cos(Angle), Bounds(document, roof).Low, precision: 3);

        // The square cut runs in from the edge by the fascia depth times the sine of the pitch.
        var inset = CornersAt(document, roof, 1000 + 150 * Math.Sin(Angle));
        Assert.Contains(inset, corner => Math.Abs(corner.Y - (top - 150 * Math.Cos(Angle))) < 1e-3);
    }

    [Fact]
    public void TheRidgeIsUntouchedByTheEaveCut()
    {
        var (document, roof, _) = Gable();
        var plumbHigh = Bounds(document, roof).High;

        roof.RafterCut = RafterCut.TwoCutSquare;
        Assert.Equal(plumbHigh, Bounds(document, roof).High, precision: 3);
    }

    [Fact]
    public void TheFasciaCannotBeDeeperThanTheRoof()
    {
        var (document, roof, thickness) = Gable();
        var fascia = roof.GetInstanceParameters(document).Single(p => p.Name == "Fascia Depth");

        Assert.False(fascia.TrySet(thickness + 10));
        Assert.False(fascia.TrySet(-5));
        Assert.True(fascia.TrySet(100));
        Assert.Equal(100, roof.FasciaDepth, precision: 6);
    }

    [Fact]
    public void TheRafterCutIsChosenByName()
    {
        var (document, roof, _) = Gable();
        var cut = roof.GetInstanceParameters(document).Single(p => p.Name == "Rafter Cut");

        Assert.Contains("Two Cut Square", cut.AllowedValues!);
        Assert.True(cut.TrySet("Two Cut Plumb"));
        Assert.Equal(RafterCut.TwoCutPlumb, roof.RafterCut);
    }

    [Fact]
    public void ACutoffCanBeHungOnALevel()
    {
        var (document, roof, _) = Gable();
        var first = document.Levels[1];

        roof.CutoffLevelId = first.Id;
        roof.CutoffOffset = 1000;

        // A metre above the first floor, which is 4 m up: the roof stops there.
        Assert.Equal(first.Elevation + 1000, roof.Surface(document).RidgeElevation, precision: 3);

        var level = roof.GetInstanceParameters(document).Single(p => p.Name == "Cutoff Level");
        Assert.Equal(first.Name, level.Value);

        // None takes the cutoff off altogether.
        Assert.True(level.TrySet(Roof.NoCutoff));
        Assert.Null(roof.CutoffElevation(document));
        Assert.Equal(3000 + 3000 * Math.Tan(Angle), roof.Surface(document).RidgeElevation, precision: 3);
    }

    [Fact]
    public void ACutoffWithNoLevelIsMeasuredFromTheRoofsBase()
    {
        var (document, roof, _) = Gable();
        roof.CutoffOffset = 800;

        Assert.Equal(3800, roof.CutoffElevation(document)!.Value, precision: 6);
        Assert.Equal(Roof.RoofBase, roof.GetInstanceParameters(document).Single(p => p.Name == "Cutoff Level").Value);
    }

    [Fact]
    public void EaveCutsAndCutoffLevelsAreSavedAndCopied()
    {
        var (document, roof, _) = Gable();
        roof.RafterCut = RafterCut.TwoCutSquare;
        roof.FasciaDepth = 120;
        roof.CutoffLevelId = document.Levels[1].Id;
        roof.CutoffOffset = 900;

        var reloaded = ProjectFile.FromJson(ProjectFile.ToJson(document)).Elements.OfType<Roof>().Single();
        Assert.Equal(RafterCut.TwoCutSquare, reloaded.RafterCut);
        Assert.Equal(120, reloaded.FasciaDepth, precision: 6);
        Assert.Equal(document.Levels[1].Id, reloaded.CutoffLevelId);
        Assert.Equal(900, reloaded.CutoffOffset, precision: 6);

        var copy = (Roof)ElementCopy.Clone(roof)!;
        Assert.Equal(RafterCut.TwoCutSquare, copy.RafterCut);
        Assert.Equal(120, copy.FasciaDepth, precision: 6);
        Assert.Equal(roof.CutoffLevelId, copy.CutoffLevelId);
    }
}
