using System.IO;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;
using BIMDesigner.Infrastructure.Interoperability;
using BIMDesigner.Infrastructure.Serialization;

namespace BIMDesigner.Tests;

/// <summary>
/// Structural columns and footings: their sections, the loads the model brings down onto them,
/// and the rules they answer to - Approved Documents A and B in England, the IRC in the US.
/// </summary>
public class StructureTests
{
    /// <summary>A house 10 m by 8 m of outside walls 330 thick, a storey high or two, a gable roof over it, no footings yet.</summary>
    private static (BimDocument Document, List<Wall> Walls, Roof Roof) House(int storeys = 1)
    {
        var (document, roof) = EaveTests.House(hip: false);
        var walls = document.Walls.ToList();
        if (storeys == 2)
        {
            var type = document.GetWallType(walls[0])!;
            foreach (var wall in walls)
                document.Add(new Wall { Start = wall.Start, End = wall.End, TypeId = type.Id, LevelId = document.Levels[1].Id, UnconnectedHeight = 3000 });
            roof.HeightOffset = 6000;
        }

        return (document, walls, roof);
    }

    private static ColumnType Type(BimDocument document, string name) => document.TypesOf<ColumnType>().Single(type => type.Name == name);

    private static Column Stand(BimDocument document, string type, Point2D at, Guid? top = null)
    {
        var columnType = Type(document, type);
        var column = new Column { TypeId = columnType.Id, LevelId = document.Levels[0].Id, Location = at, Structural = columnType.Structural, TopLevelId = top };
        document.Add(column);
        return column;
    }

    [Fact]
    public void StructuralSectionsAreMeasuredAsSteelTablesGiveThem()
    {
        var document = BimDocument.CreateDefault();
        Assert.All(document.TypesOf<ColumnType>().Where(type => type.Structural), type => Assert.Equal(BuiltInCategory.StructuralColumns, type.Category));
        Assert.Contains(document.TypesOf<ColumnType>(), type => !type.Structural);

        // A UC 203 x 203 x 46: 58.7 cm² in the tables, a little more than its flanges and web without their root radii.
        var uc = Type(document, "UC 203 x 203 x 46").Profile.Properties();
        Assert.InRange(uc.Area, 5700, 5870);
        Assert.InRange(uc.LeastRadiusOfGyration, 50, 52);

        // 3 in Schedule 40 pipe, the least the IRC takes: 2.23 in², a radius of gyration of 1.16 in.
        var pipe = Type(document, "CHS 88.9 x 5.5 - 3 in Schedule 40 Pipe").Profile.Properties();
        Assert.InRange(pipe.Area, StructureRules.SchedulePipe.Area - 15, StructureRules.SchedulePipe.Area + 15);
        Assert.InRange(StructureRules.SchedulePipe.Area / 645.16, 2.20, 2.26);
        Assert.InRange(StructureRules.SchedulePipe.Radius / 25.4, 1.15, 1.18);

        // A hollow section is a tube: its hole is out of it.
        var shs = Type(document, "SHS 100 x 100 x 5");
        Assert.Equal(100 * 100 - 90 * 90, shs.SectionArea, 0);
        Assert.Single(shs.Profile.Holes);

        // A structural column is scheduled as one.
        var column = Stand(document, "UC 203 x 203 x 46", new Point2D(0, 0));
        Assert.Equal(BuiltInCategory.StructuralColumns, column.Category);
    }

    [Fact]
    public void AStripFootingIsAsWideAsTableTenAsksForItsLoadAndGround()
    {
        var (document, walls, _) = House(storeys: 2);
        var south = walls[0];

        // Two storeys of brick and block and a roof's slope come to between 20 and 30 kN a metre.
        var load = Loads.OnWall(document, south);
        Assert.InRange(load, 20, 30);
        Assert.True(load > Loads.OnWall(document, walls[1]), "the eave wall carries the roof; the gable wall does not");

        // On firm clay (type IV) up to 30 kN/m wants 350: the narrowest strip, 450, does.
        Assert.Equal((350.0, (string?)null), StructureRules.Table10(GroundType.FirmClay, load, 330));
        var footing = Footings.Fit(document, south);
        Assert.Equal("Strip Footing - 450 x 225", document.FindType<FootingType>(footing.TypeId)!.Name);
        Assert.Equal(450, Footings.DepthBelowGround(document, footing)!.Value, 0);
        document.Add(footing);
        Assert.Empty(StructureRules.Problems(document, footing, ClearanceStandard.ApprovedDocumentM));

        // On soft clay (type VI) it wants 650: a 450 strip is too narrow, and the fit is the 750.
        document.ProjectInformation.Ground = GroundType.SoftClay;
        Assert.Contains(StructureRules.Problems(document, footing, ClearanceStandard.ApprovedDocumentM), problem => problem.Rule.Contains("650 wide"));
        Assert.Equal("Strip Footing - 750 x 300", document.FindType<FootingType>(Footings.Fit(document, south).TypeId)!.Name);

        // Past 30 kN/m soft ground is beyond Section 2E; very soft wants specialist advice; rock, the wall's own width.
        Assert.NotNull(StructureRules.Table10(GroundType.SoftClay, 35, 330).Outside);
        Assert.NotNull(StructureRules.Table10(GroundType.VerySoft, 10, 330).Outside);
        Assert.Equal(330, StructureRules.Table10(GroundType.Rock, 60, 330).Width);

        // A medium shrinkage clay takes it down to 0.9 m; off its wall's middle, it is not central.
        document.ProjectInformation.Ground = GroundType.FirmClay;
        document.ProjectInformation.ClayShrinkage = ClayShrinkage.Medium;
        Assert.Contains(StructureRules.Problems(document, footing, ClearanceStandard.ApprovedDocumentM), problem => problem.Rule.Contains("0.90 m"));
        Assert.Equal(900, Footings.DepthBelowGround(document, Footings.Fit(document, south))!.Value, 0);
        footing.Eccentricity = 100;
        Assert.Contains(StructureRules.Problems(document, footing, ClearanceStandard.ApprovedDocumentM), problem => problem.Rule.Contains("2E2a"));
    }

    [Fact]
    public void AUsFootingIsSizedByTheIrcsTables()
    {
        // Light frame with brick veneer, one storey over a crawl space, 20 psf, 1,500 psf soil: 15 by 6 in.
        Assert.Equal((15.0, 6.0), StructureRules.IrcFooting(IrcWallConstruction.BrickVeneer, 20, 1, IrcFoundation.CrawlSpace, 1500));
        Assert.Equal((12.0, 6.0), StructureRules.IrcFooting(IrcWallConstruction.LightFrame, 25, 2, IrcFoundation.SlabOnGrade, 2000));
        Assert.Equal((34.0, 13.0), StructureRules.IrcFooting(IrcWallConstruction.Masonry, 70, 3, IrcFoundation.Basement, 1500));
        Assert.Null(StructureRules.IrcFooting(IrcWallConstruction.LightFrame, 80, 1, IrcFoundation.SlabOnGrade, 2000));

        // Width between the soils interpolated (note a): 2 story basement light frame, 19 at 1,500 and 14 at 2,000.
        Assert.Equal(16.5, StructureRules.IrcFooting(IrcWallConstruction.LightFrame, 20, 2, IrcFoundation.Basement, 1750)!.Value.Width, 6);

        // Notes c and d: 2 in wider and 1 in thicker each 4 ft over 32 ft; narrower each whole 4 ft under, to 12 by 6.
        Assert.Equal((19.0, 8.0), StructureRules.ForWidth((15, 6), 40));
        Assert.Equal((13.0, 6.0), StructureRules.ForWidth((15, 6), 26.2));

        var (document, walls, _) = House();
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.Irc;
        var south = walls[0];
        Assert.Equal(IrcWallConstruction.BrickVeneer, StructureRules.ConstructionOf(document, south));
        Assert.Equal(IrcFoundation.CrawlSpace, StructureRules.FoundationOf(document, south));

        // 16 by 8 in projects too little past a 13 in wall; 24 by 10 does, 12 in down at the least.
        var footing = new WallFooting
        {
            WallId = south.Id, LevelId = south.LevelId, Depth = 400,
            TypeId = document.TypesOf<FootingType>().Single(type => type.Name == "Footing - 16 x 8 in").Id
        };
        document.Add(footing);
        Assert.Contains(StructureRules.Problems(document, footing, ClearanceStandard.Irc), problem => problem.Rule.Contains("project 2 in"));
        var fit = Footings.Fit(document, south);
        Assert.Equal("Footing - 24 x 10 in", document.FindType<FootingType>(fit.TypeId)!.Name);
        Assert.True(Footings.DepthBelowGround(document, fit) >= 305);

        // Below a frost line of 900, not above it.
        document.ProjectInformation.FrostDepth = 900;
        Assert.Contains(StructureRules.Problems(document, fit, ClearanceStandard.Irc), problem => problem.Rule.Contains("R403.1.4.1"));
        Assert.True(Footings.DepthBelowGround(document, Footings.Fit(document, south)) >= 900);
    }

    [Fact]
    public void APadIsSizedForTheFloorItsColumnHoldsUp()
    {
        var (document, _, _) = House();
        var floor = new Floor { TypeId = document.TypesOf<FloorType>().First().Id, LevelId = document.Levels[1].Id };
        floor.SetBoundary(new[] { new Point2D(0, 0), new Point2D(10000, 0), new Point2D(10000, 8000), new Point2D(0, 8000) });
        document.Add(floor);
        var column = Stand(document, "UC 203 x 203 x 46", new Point2D(5000, 4000), document.Levels[1].Id);

        // Half way to the walls every way round it: 5 m by 4 m of floor.
        var weight = Loads.Weight(document, document.FindType<FloorType>(floor.TypeId)!.Structure);
        var expected = 20 * (weight + 1.5);
        Assert.InRange(Loads.OnColumn(document, column), expected, expected + 3);

        // On the ground with nothing under it; holding up a floor, unprotected.
        var problems = StructureRules.Problems(document, column, ClearanceStandard.ApprovedDocumentM);
        Assert.Contains(problems, problem => problem.Said.Contains("no footing"));
        Assert.Contains(problems, problem => problem.Rule.Contains("30 minutes"));
        column.TypeId = Type(document, "UC 203 x 203 x 46, Intumescent 30 min").Id;

        // The smallest pad that bears it on firm clay, as Table 10 bears its strips.
        var pad = Footings.Fit(document, column);
        document.Add(pad);
        Assert.Empty(StructureRules.Problems(document, pad, ClearanceStandard.ApprovedDocumentM));
        Assert.Empty(StructureRules.Problems(document, column, ClearanceStandard.ApprovedDocumentM));
        var area = document.FindType<FootingType>(pad.TypeId)!;
        Assert.True(area.Width * area.Length / 1e6 >= StructureRules.PadLoad(document, pad) / StructureRules.Table10Pressure(GroundType.FirmClay)!.Value);

        // A 600 pad is too small for it, here and under the IRC.
        pad.TypeId = document.TypesOf<FootingType>().Single(type => type.Name == "Pad Footing - 600 x 600 x 300").Id;
        Assert.Contains(StructureRules.Problems(document, pad, ClearanceStandard.ApprovedDocumentM), problem => problem.Rule.Contains("Table 10"));
        Assert.Contains(StructureRules.Problems(document, pad, ClearanceStandard.Irc), problem => problem.Rule.Contains("R401.4.1"));

        // Its pier stands from the pad up to the column; a section through it cuts both.
        Assert.Equal(2, Footings.Meshes(document, pad).Count);
        var marker = new SectionMarker { Name = "A", Start = new Point2D(5000, 2000), End = new Point2D(5000, 6000), ViewDepth = 300 };
        Assert.Contains(SectionProjection.Build(document, marker).Pieces, piece => piece.ElementId == pad.Id);
    }

    [Fact]
    public void TheIrcsColumnRules()
    {
        var document = BimDocument.CreateDefault();
        var standard = ClearanceStandard.Irc;

        // 3 in Schedule 40 pipe is the least; anything slighter is not.
        var pipe = Stand(document, "CHS 88.9 x 5.5 - 3 in Schedule 40 Pipe", new Point2D(0, 0));
        Assert.DoesNotContain(StructureRules.Problems(document, pipe, standard), problem => problem.Rule.Contains("R407.3"));
        var slight = Type(document, "SHS 100 x 100 x 5").Duplicate("SHS 60 x 60 x 3");
        (slight.Width, slight.Depth, slight.WallThickness) = (60, 60, 3);
        document.AddType(slight);
        pipe.TypeId = slight.Id;
        Assert.Contains(StructureRules.Problems(document, pipe, standard), problem => problem.Rule.Contains("Schedule 40"));

        // Painted against rust, or flagged.
        slight.RustProtected = false;
        Assert.Contains(StructureRules.Problems(document, pipe, standard), problem => problem.Rule.Contains("R407.2"));

        // A 4 by 4 at the least in wood; out in the weather untreated, it wants treating.
        var post = Stand(document, "Timber Post C24 100 x 100", new Point2D(3000, 0));
        Assert.DoesNotContain(StructureRules.Problems(document, post, standard), problem => problem.Rule.Contains("4 by 4"));
        Assert.Contains(StructureRules.Problems(document, post, standard), problem => problem.Rule.Contains("R317.1"));
        Assert.DoesNotContain(StructureRules.Problems(document, Stand(document, "Timber Post 4x4, Treated", new Point2D(6000, 0)), standard), problem => problem.Rule.Contains("R317.1"));
        var thin = Type(document, "Timber Post C24 100 x 100").Duplicate("Timber Post 75 x 75");
        (thin.Width, thin.Depth) = (75, 75);
        document.AddType(thin);
        post.TypeId = thin.Id;
        Assert.Contains(StructureRules.Problems(document, post, standard), problem => problem.Rule.Contains("4 by 4"));
    }

    [Fact]
    public void FootingsAndStructuralColumnsAreSavedCopiedAndExported()
    {
        var (document, walls, _) = House();
        document.ProjectInformation.Ground = GroundType.StiffClay;
        document.ProjectInformation.ClayShrinkage = ClayShrinkage.High;
        document.ProjectInformation.FrostDepth = 1200;
        document.ProjectInformation.GroundSnowLoad = 50;
        var footing = Footings.Fit(document, walls[0]);
        footing.Eccentricity = 30;
        document.Add(footing);
        var column = Stand(document, "UC 152 x 152 x 30", new Point2D(5000, 4000));
        var pad = Footings.Fit(document, column);
        document.Add(pad);

        var path = Path.Combine(Path.GetTempPath(), $"bimtest-{Guid.NewGuid():N}{ProjectFile.Extension}");
        var ifc = Path.ChangeExtension(path, ".ifc");
        try
        {
            ProjectFile.Save(document, path);
            var opened = ProjectFile.Load(path);
            Assert.Equal((GroundType.StiffClay, ClayShrinkage.High, 1200.0, 50.0),
                (opened.ProjectInformation.Ground, opened.ProjectInformation.ClayShrinkage, opened.ProjectInformation.FrostDepth, opened.ProjectInformation.GroundSnowLoad));
            var strip = opened.Elements.OfType<WallFooting>().Single();
            Assert.Equal((footing.WallId, footing.TypeId, footing.Depth, 30.0), (strip.WallId, strip.TypeId, strip.Depth, strip.Eccentricity));
            Assert.Equal(column.Id, opened.Elements.OfType<PadFooting>().Single().ColumnId);
            var back = opened.Elements.OfType<Column>().Single();
            Assert.True(back.Structural);
            var type = opened.FindType<ColumnType>(back.TypeId)!;
            Assert.Equal((true, ColumnShape.IBeam, 9.4, 6.5), (type.Structural, type.Shape, type.FlangeThickness, type.WebThickness));

            IfcExport.Save(document, ifc);
            var text = File.ReadAllText(ifc);
            Assert.Contains("IFCFOOTING", text);
            Assert.Contains(".STRIP_FOOTING.", text);
            Assert.Contains(".PAD_FOOTING.", text);
            Assert.Contains("IFCCOLUMN", text);
        }
        finally
        {
            File.Delete(path);
            File.Delete(ifc);
        }

        // A wall copied takes its footing; deleted, it takes it with it.
        var copies = ElementCopy.Duplicate(document, new Element[] { walls[0] });
        var copiedWall = copies.OfType<Wall>().Single();
        Assert.Equal(copiedWall.Id, copies.OfType<WallFooting>().Single().WallId);
    }
}
