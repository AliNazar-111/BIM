using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;
using BIMDesigner.Infrastructure.Interoperability;
using BIMDesigner.Infrastructure.Serialization;
using Xbim.Ifc4.SharedBldgElements;

namespace BIMDesigner.Tests;

/// <summary>Walls with an edited elevation profile.</summary>
public class WallProfileTests
{
    /// <summary>A gable end: 6 m long, 3 m at the eaves, 4.5 m at the ridge halfway along.</summary>
    private static readonly Point2D[] Gable =
    {
        new(0, 0), new(6000, 0), new(6000, 3000), new(3000, 4500), new(0, 3000)
    };

    /// <summary>A wall with a notch 2 m wide cut down from the top to 1 m above the floor.</summary>
    private static readonly Point2D[] Notched =
    {
        new(0, 0), new(6000, 0), new(6000, 3000), new(4000, 3000),
        new(4000, 1000), new(2000, 1000), new(2000, 3000), new(0, 3000)
    };

    /// <summary>A wall standing on two feet, open underneath between 2 m and 4 m up to 1 m high.</summary>
    private static readonly Point2D[] Bridged =
    {
        new(0, 0), new(2000, 0), new(2000, 1000), new(4000, 1000),
        new(4000, 0), new(6000, 0), new(6000, 3000), new(0, 3000)
    };

    private static (BimDocument Document, WallType Type, Wall Wall) Project(IReadOnlyList<Point2D>? profile)
    {
        var document = BimDocument.CreateDefault();
        var type = document.TypesOf<WallType>().Single(t => t.Name.StartsWith("Exterior"));
        var wall = new Wall
        {
            Start = new Point2D(0, 0), End = new Point2D(6000, 0),
            TypeId = type.Id, LevelId = document.Levels[0].Id, UnconnectedHeight = 3000
        };
        document.Add(wall);

        if (profile is not null) new SetWallProfileCommand(wall, profile).Redo();
        return (document, type, wall);
    }

    [Fact]
    public void AGableIsTwoStripsWithSlopingTops()
    {
        var strips = WallProfile.Strips(Gable, 6000);

        Assert.Equal(2, strips.Count);
        Assert.Equal(22.5e6, strips.Sum(s => s.Area), precision: 3);
        Assert.Equal(3750, strips[0].Top(1500), precision: 9);

        var span = Assert.Single(WallProfile.HeightsAt(Gable, 1500));
        Assert.Equal((0.0, 3750.0), span);

        var run = Assert.Single(WallProfile.RunsAt(Gable, 4000, 6000));
        Assert.Equal(2000, run.From, precision: 9);
        Assert.Equal(4000, run.To, precision: 9);
    }

    [Fact]
    public void DoorsAndWindowsAreTakenOutOfTheStrips()
    {
        var withDoor = WallProfile.Strips(Gable, 6000, new[] { (1000.0, 2000.0, 0.0, 2100.0) });
        Assert.Equal(22.5e6 - 1000 * 2100, withDoor.Sum(s => s.Area), precision: 3);

        // A window whose head is above the eaves, under the slope. Between 500 and 1500 along
        // the slope rises from 3250 to 3750, crossing the 3500 head at 1000: before that the
        // window takes up to the slope, after it up to its head.
        var tall = WallProfile.Strips(Gable, 6000, new[] { (500.0, 1500.0, 1000.0, 3500.0) });
        var underSlope = 500 * (3250.0 + 3500) / 2 - 500 * 1000.0;
        var underHead = 500 * 2500.0;
        Assert.Equal(22.5e6 - underSlope - underHead, tall.Sum(s => s.Area), precision: 3);
    }

    [Fact]
    public void ANotchAndAnArchLeaveGapsWhereTheyShould()
    {
        Assert.Equal(new[] { (0.0, 2000.0), (4000.0, 6000.0) }, WallProfile.RunsAt(Notched, 2000, 6000));
        Assert.Equal(new[] { (0.0, 1000.0) }, WallProfile.HeightsAt(Notched, 3000));
        Assert.Equal(new[] { (1000.0, 3000.0) }, WallProfile.HeightsAt(Bridged, 3000));
    }

    [Fact]
    public void BadOutlinesAreRefused()
    {
        Assert.Null(WallProfile.Problem(Gable, 6000));
        Assert.NotNull(WallProfile.Problem(Gable.Take(2).ToList(), 6000));
        Assert.NotNull(WallProfile.Problem(Gable, 5000));
        Assert.NotNull(WallProfile.Problem(new[] { new Point2D(0, 0), new Point2D(1000, 1000), new Point2D(1000, 0), new Point2D(0, 1000) }, 6000));
        Assert.NotNull(WallProfile.Problem(new[] { new Point2D(0, 0), new Point2D(1000, 0), new Point2D(2000, 0) }, 6000));
    }

    [Fact]
    public void ItsQuantitiesComeFromTheOutline()
    {
        var (document, type, wall) = Project(Gable);

        Assert.Equal(22.5e6, wall.GetArea(document), precision: 3);
        Assert.Equal(22.5e6 * type.Width, wall.GetVolume(document), precision: 1);
        Assert.Equal("Edited", wall.GetInstanceParameters(document).Single(p => p.Definition.Name == "Profile").Value);
    }

    [Fact]
    public void ThreeDRisesToTheRidge()
    {
        var (document, _, wall) = Project(Gable);
        var points = ModelMeshBuilder.BuildWall(document, wall).Where(m => m.Kind == MeshKind.Wall).SelectMany(m => m.Positions).ToList();

        var ridge = points.MaxBy(p => p.Z);
        Assert.Equal(4500, ridge.Z, precision: 6);
        Assert.Equal(3000, ridge.X, precision: 6);

        // At the free ends the wall is only eaves high.
        Assert.Equal(3000, points.Where(p => Math.Abs(p.X) < 1e-6).Max(p => p.Z), precision: 6);
    }

    [Fact]
    public void SectionsCutAndSeeTheOutline()
    {
        var (document, _, wall) = Project(Gable);

        var across = new SectionMarker
        {
            Name = "A", Start = new Point2D(1500, -2000), End = new Point2D(1500, 2000), LevelId = wall.LevelId
        };
        document.Add(across);
        var cut = SectionProjection.Build(document, across).Pieces.Where(p => p.ElementId == wall.Id && p.Depth == SectionDepth.Cut).ToList();
        Assert.NotEmpty(cut);
        Assert.All(cut, p => Assert.Equal(3750, p.Bounds.Top, precision: 6));

        // Looking at the wall face-on from in front of it, the gable is seen whole.
        var facing = new SectionMarker
        {
            Name = "B", Start = new Point2D(-1000, -3000), End = new Point2D(7000, -3000), LevelId = wall.LevelId
        };
        document.Add(facing);
        var seen = SectionProjection.Build(document, facing).Pieces.Single(p => p.ElementId == wall.Id && p.Part == SectionPart.WallFace);
        Assert.Equal(5, seen.Shape!.Count);
        Assert.Equal(4500, seen.Bounds.Top, precision: 6);
    }

    [Fact]
    public void ThePlanIsCutWhereTheWallReachesTheCutHeight()
    {
        var (document, type, wall) = Project(Notched);

        Assert.Single(WallSlices.Solid(document, wall, type));
        Assert.Single(WallSlices.InPlan(document, wall, type, 1200), s => s.To <= 2000 + 1e-6);
        Assert.Equal(2, WallSlices.InPlan(document, wall, type, 1200).Count);
        Assert.Single(WallSlices.InPlan(document, wall, type, 800));
    }

    [Fact]
    public void ASkirtingStopsWhereTheWallIsOpenUnderneath()
    {
        var (document, type, wall) = Project(Bridged);
        type.Sweeps.Add(new WallSweep(SweepKind.Sweep, SweepProfile.Skirting, WallSide.Interior, 20, 100, 0, false, document.Materials.First().Id));

        var runs = WallSweeps.Runs(document, wall, type, WallSide.Interior, 0, 100);
        Assert.Equal(2, runs.Count);
        Assert.Equal(2000, runs[0].To, precision: 6);
        Assert.Equal(4000, runs[1].From, precision: 6);
    }

    [Fact]
    public void TheFarEndStaysOnTheEndWhenTheWallIsLengthened()
    {
        var (document, _, wall) = Project(Gable);
        wall.End = new Point2D(8000, 0);

        var profile = WallProfile.Of(document, wall)!;
        Assert.Contains(new Point2D(8000, 3000), profile);
        Assert.Contains(new Point2D(3000, 4500), profile);
        Assert.Equal(8000, WallProfile.Strips(profile, wall.Length).Max(s => s.To), precision: 9);
    }

    [Fact]
    public void SplittingAProfiledWallSharesTheOutline()
    {
        var (document, _, wall) = Project(Gable);
        var split = new SplitWallCommand(document, wall, new Point2D(3000, 0));
        split.Redo();

        Assert.Equal(22.5e6, wall.GetArea(document) + split.Remainder.GetArea(document), precision: 1);
        Assert.Equal(4500, WallProfile.Top(WallProfile.Of(document, split.Remainder)!), precision: 9);

        split.Undo();
        Assert.Equal(22.5e6, wall.GetArea(document), precision: 3);
    }

    [Fact]
    public void EditingIsUndoableAndOnlyStraightUprightWallsTakeIt()
    {
        var (document, _, wall) = Project(null);
        var edit = new SetWallProfileCommand(wall, Gable);

        edit.Redo();
        Assert.NotNull(WallProfile.Of(document, wall));
        edit.Undo();
        Assert.Null(wall.Profile);

        edit.Redo();
        wall.Bulge = 0.3;
        Assert.Null(WallProfile.Of(document, wall));
        Assert.StartsWith("Edited (not in use", (string)wall.GetInstanceParameters(document).Single(p => p.Definition.Name == "Profile").Value!);

        wall.Bulge = 0;
        new SetWallProfileCommand(wall, null).Redo();
        Assert.Equal(6000 * 3000, wall.GetArea(document), precision: 3);
    }

    [Fact]
    public void AProfileIsSavedAndExported()
    {
        var (document, _, wall) = Project(Gable);
        document.Add(new Door
        {
            TypeId = document.TypesOf<DoorType>().First().Id, LevelId = wall.LevelId, HostWallId = wall.Id, DistanceAlongWall = 1500
        });

        var path = Path.Combine(Path.GetTempPath(), $"bimtest-{Guid.NewGuid():N}{ProjectFile.Extension}");
        try
        {
            ProjectFile.Save(document, path);
            var reloaded = ProjectFile.Load(path);
            var again = reloaded.Walls.Single(w => w.Id == wall.Id);
            Assert.Equal(Gable, again.Profile);
            Assert.Equal(6000, again.ProfileLength, precision: 9);
        }
        finally
        {
            File.Delete(path);
        }

        using var model = IfcExport.Build(document);
        Assert.Single(model.Instances.OfType<IfcWall>());
        Assert.Single(model.Instances.OfType<IfcDoor>());
    }
}
