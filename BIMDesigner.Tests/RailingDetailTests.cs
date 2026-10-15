using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Infrastructure.Serialization;
using static BIMDesigner.Tests.StairTests;

namespace BIMDesigner.Tests;

/// <summary>
/// Railings in detail: frameless glass and the rules for glass in guarding; guarding measured
/// from the floor it guards, a parapet under it counted in; the gaps at its ends; the loads it
/// is made for; panels, mesh, cables and fences; arcs; handrails and intermediate rails; and
/// doors, windows and curtain walls over a drop, with Juliet balconies across them.
/// </summary>
public class RailingDetailTests
{
    /// <summary>The house with a balcony 2500 out off its first floor, its first-floor outside wall standing along x = 6000.</summary>
    private static (BimDocument Document, Floor Balcony, Wall Wall) Balcony()
    {
        var (document, _) = House();
        var balcony = new Floor { LevelId = document.Levels[1].Id, TypeId = document.TypesOf<FloorType>().First().Id };
        balcony.SetBoundary(new[] { new Point2D(6000, 1000), new Point2D(8500, 1000), new Point2D(8500, 4000), new Point2D(6000, 4000) });
        document.Add(balcony);
        var wall = new Wall
        {
            Start = new Point2D(6000, 0), End = new Point2D(6000, 5000), TypeId = Exterior(document).Id, LevelId = document.Levels[1].Id, UnconnectedHeight = 3000
        };
        document.Add(wall);
        return (document, balcony, wall);
    }

    private static WallType Exterior(BimDocument document) => document.TypesOf<WallType>().First(type => type.Function == WallFunction.Exterior && type.Log is null);

    private static RailingType Type(BimDocument document, string name) => document.TypesOf<RailingType>().Single(type => type.Name == name);

    private static Railing Put(BimDocument document, string type, params (double X, double Y)[] points)
    {
        var railing = new Railing { LevelId = document.Levels[1].Id, TypeId = Type(document, type).Id, Outside = true };
        railing.SetPath(points.Select(point => new Point2D(point.X, point.Y)));
        document.Add(railing);
        return railing;
    }

    [Fact]
    public void FramelessGlassStandsInItsChannelAndIsLaminatedWhereNoRailLinksItsPanes()
    {
        var (document, _, _) = Balcony();
        var railing = Put(document, "Guard 1100 - Frameless Glass", (6175, 1050), (8450, 1050), (8450, 3950));
        var type = Railings.TypeOf(document, railing);

        // No posts: panes in a channel, at most 1200 wide.
        Assert.Empty(Railings.Posts(document, railing, type));
        Assert.Equal(2 + 3, Railings.Bays(document, railing, type).Count);
        Assert.Contains(RailingModel.Meshes(document, railing), mesh => mesh.Opacity < 1);
        Assert.Empty(Railings.Problems(document, railing));

        // Toughened only, with nothing along its top: BS 6180 asks for a rail linking the panes.
        type.GlassLaminated = false;
        Assert.Contains(Railings.Problems(document, railing), problem => problem.Contains("BS 6180"));
        type.HasTopRail = true;
        Assert.DoesNotContain(Railings.Problems(document, railing), problem => problem.Contains("BS 6180"));

        // Under the IRC and the IBC, glass in a guard is laminated whatever is along its top.
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.Ada;
        Assert.Contains(Railings.Problems(document, railing), problem => problem.Contains("laminated glass in a guard"));

        // Panes too far apart for a child's head.
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.ApprovedDocumentM;
        type.GlassLaminated = true;
        type.GlassJoint = 120;
        Assert.Contains(Railings.Problems(document, railing), problem => problem.Contains("panes are 120 apart"));
    }

    [Fact]
    public void ARailingOnAParapetIsMeasuredFromTheTerraceItGuards()
    {
        // A flat roof terrace at the first floor, with a parapet 500 high along its south edge.
        var (document, _) = House();
        var first = document.Levels[1];
        var terrace = new Roof { LevelId = document.Levels[0].Id, TypeId = document.TypesOf<RoofType>().First(type => type.Name.Contains("Flat")).Id, HeightOffset = first.Elevation };
        terrace.SetBoundary(new[] { new Point2D(0, -3000), new Point2D(6000, -3000), new Point2D(6000, 0), new Point2D(0, 0) });
        document.Add(terrace);
        var top = terrace.GetTopElevation(document);
        document.Add(new Wall
        {
            Start = new Point2D(0, -2900), End = new Point2D(6000, -2900), TypeId = Exterior(document).Id, LevelId = first.Id,
            BaseOffset = top - first.Elevation, UnconnectedHeight = 500
        });

        // A railing 600 high on it: on the parapet's top, 1100 over the terrace.
        var low = Type(document, "Guard 1100 - Steel Balusters").Duplicate("Guard 600 - On a Parapet");
        low.Height = 600;
        document.AddType(low);
        var railing = new Railing { LevelId = first.Id, TypeId = low.Id, Outside = true };
        railing.SetPath(new[] { new Point2D(100, -2900), new Point2D(5900, -2900) });
        document.Add(railing);

        Assert.Equal(top + 500, Railings.Base(document, railing, new Point2D(3000, -2900)), 1);
        Assert.Equal(1100, Railings.EffectiveHeight(document, railing), 1);
        Assert.DoesNotContain(Railings.Problems(document, railing), problem => problem.Contains("high"));

        // The same railing on the terrace itself is 600 over it: too low.
        railing.SetPath(new[] { new Point2D(100, -2500), new Point2D(5900, -2500) });
        Assert.Equal(600, Railings.EffectiveHeight(document, railing), 1);
        Assert.Contains(Railings.Problems(document, railing), problem => problem.Contains("600 high"));
    }

    [Fact]
    public void AGapAtARailingsEndIsSaidAndGuardEdgesBringsItsEndsTightToTheWall()
    {
        var (document, balcony, _) = Balcony();

        // Stopped 300 off the wall's line: its end post 110 clear of the wall's face.
        var railing = Put(document, "Guard 1100 - Steel Balusters", (8450, 1050), (6300, 1050));
        var gap = Assert.Single(Railings.EndGaps(document, railing));
        Assert.Equal(6300 - (6000 + Exterior(document).Structure.TotalWidth / 2) - 25, gap.Gap, 0);
        Assert.Contains(Railings.Problems(document, railing), problem => problem.Contains("Its end stands"));
        document.Remove(railing);

        // Guard Edges: its runs end tight to the wall.
        var made = Railings.GuardEdges(document, balcony, RailingBuilding.Dwelling, null, out _);
        Assert.NotEmpty(made);
        foreach (var run in made) document.Add(run);
        Assert.All(made, run => Assert.Empty(Railings.EndGaps(document, run)));
        Assert.Contains(made, run => run.Path.Any(point => Math.Abs(point.X - (6000 + Exterior(document).Structure.TotalWidth / 2 + 10 + 25)) < 6));
    }

    [Fact]
    public void ItIsMadeForTheLoadsItsPlaceAsksAndAFenceIsAskedForNothing()
    {
        var (document, _, _) = Balcony();
        var railing = Put(document, "Guard 1100 - Steel Balusters", (6300, 1050), (8450, 1050));
        Assert.DoesNotContain(Railings.Problems(document, railing), problem => problem.Contains("kN"));

        // Where people gather: 1.5 kN/m.
        railing.Building = RailingBuilding.Other;
        Assert.Contains(Railings.Problems(document, railing), problem => problem.Contains("1.5 kN/m"));

        // A home's inside railing, round a balcony: 0.74 kN/m, more than a stair's timber balustrade is made for.
        railing.Building = RailingBuilding.Dwelling;
        railing.TypeId = Type(document, "Guard 900 - Timber Balusters").Id;
        Assert.Contains(Railings.Problems(document, railing), problem => problem.Contains("0.74 kN/m"));
        railing.Outside = false;
        Assert.DoesNotContain(Railings.Problems(document, railing), problem => problem.Contains("kN"));

        // A fence: none of guarding's rules.
        railing.TypeId = Type(document, "Fence 1200 - Timber Picket").Id;
        Assert.Empty(Railings.Problems(document, railing));
        Assert.Equal("A fence: it guards no drop", Railings.Verdict(document, railing));
    }

    [Fact]
    public void PanelsMeshAndCablesAreBuiltAndCablesAndIntermediateRailsAreSaidToBeClimbable()
    {
        var (document, _, _) = Balcony();
        foreach (var name in new[] { "Fence 1800 - Close Board Timber", "Guard 1100 - Steel Mesh", "Guard 1100 - Stainless Cables" })
        {
            var railing = Put(document, name, (6300, 1050), (8450, 1050));
            Assert.True(RailingModel.Meshes(document, railing).Sum(mesh => mesh.Positions.Count) > 0);
        }

        var cables = document.Elements.OfType<Railing>().Last();
        Assert.Contains(Railings.Problems(document, cables), problem => problem.Contains("cables run along"));

        var rails = Type(document, "Guard 1100 - Steel Balusters");
        rails.IntermediateRails = 2;
        var railed = Put(document, "Guard 1100 - Steel Balusters", (6300, 1050), (8450, 1050));
        Assert.Contains(Railings.Problems(document, railed), problem => problem.Contains("intermediate rails"));
    }

    [Fact]
    public void AnArcIsBuiltInFacetsAndBentByItsHandleMirroredCopiedAndSaved()
    {
        var (document, _, _) = Balcony();

        // A half circle 2000 across: in facets of 15 degrees at most, its length the arc's.
        var railing = Put(document, "Guard 1100 - Glass Panel", (6500, 1500), (8500, 1500));
        railing.SetBulge(0, 1);
        Assert.InRange(Railings.Length(railing), Math.PI * 1000 * 0.99, Math.PI * 1000);
        Assert.True(Railings.Lengths(railing).Count >= 12);
        Assert.Equal(Railings.Lengths(railing).Count + 1, Railings.Posts(document, railing, Railings.TypeOf(document, railing)).Count);

        // Its middle dragged in: a shallower arc.
        var bend = Assert.Single(ElementHandles.Of(document, railing), handle => handle.Key == "bend:0");
        ElementHandles.Drag(document, railing, bend, new Point2D(7500, 1000), 0);
        Assert.True(railing.BulgeAt(0) is > 0 and < 1);

        // Mirrored, it bows the other way; copied and saved, it keeps its bow.
        var bulge = railing.BulgeAt(0);
        var copy = (Railing)ElementCopy.Clone(railing)!;
        Assert.Equal(bulge, copy.BulgeAt(0));
        var back = ProjectFile.FromJson(ProjectFile.ToJson(document)).Elements.OfType<Railing>().Single();
        Assert.Equal(bulge, back.BulgeAt(0), 9);
    }

    /// <summary>A first floor with its outside wall along y = 0, the drop to the ground outside it to the north.</summary>
    private static (BimDocument Document, Wall Wall) Upstairs(bool curtain = false)
    {
        var document = BimDocument.CreateDefault();
        var first = document.Levels[1];
        var floor = new Floor { LevelId = first.Id, TypeId = document.TypesOf<FloorType>().First().Id };
        floor.SetBoundary(new[] { new Point2D(0, -5000), new Point2D(6000, -5000), new Point2D(6000, 0), new Point2D(0, 0) });
        document.Add(floor);
        var wall = new Wall
        {
            Start = new Point2D(0, 0), End = new Point2D(6000, 0), LevelId = first.Id, UnconnectedHeight = 3000,
            TypeId = curtain ? document.TypesOf<CurtainWallType>().First(type => type.Name.Contains("Storefront")).Id : Exterior(document).Id
        };
        document.Add(wall);
        return (document, wall);
    }

    [Fact]
    public void ADoorOntoADropWantsAJulietBalconyAndHasOne()
    {
        var (document, wall) = Upstairs();
        var door = new Door { TypeId = document.TypesOf<DoorType>().First().Id, LevelId = wall.LevelId, HostWallId = wall.Id, DistanceAlongWall = 3000 };
        document.Add(door);
        Assert.True(OpeningGuarding.Drop(document, wall, 3000) > 2500);
        Assert.Contains("Juliet balcony", Assert.Single(OpeningGuarding.Problems(document, door)));

        var juliet = Railings.Juliet(document, door)!;
        document.Add(juliet);
        Assert.Empty(OpeningGuarding.Problems(document, door));
        Assert.Equal(wall.GetBaseElevation(document), Railings.Base(document, juliet, juliet.Path[0]), 1);
        Assert.Equal(1100, Railings.EffectiveHeight(document, juliet), 1);
        Assert.Equal("Meets Approved Document K", Railings.Verdict(document, juliet));
        Assert.StartsWith("Guarded by a railing across it", OpeningGuarding.Verdict(document, door));
    }

    [Fact]
    public void ALowWindowOverADropIsGuardedOrItsGlassIsLaminated()
    {
        var (document, wall) = Upstairs();
        var opens = document.TypesOf<WindowType>().First(type => type.Operation != WindowOperation.Fixed);
        var window = new Window { TypeId = opens.Id, LevelId = wall.LevelId, HostWallId = wall.Id, DistanceAlongWall = 3000, SillHeight = 300 };
        document.Add(window);
        Assert.Contains("guarding across it to 800", Assert.Single(OpeningGuarding.Problems(document, window)));

        // Fixed: its glass is the guarding - laminated, it is enough.
        var fixedType = opens.Duplicate("Fixed Light");
        fixedType.Operation = WindowOperation.Fixed;
        document.AddType(fixedType);
        window.TypeId = fixedType.Id;
        Assert.Contains("toughened laminated glass", Assert.Single(OpeningGuarding.Problems(document, window)));
        fixedType.GlazingType = "Double glazed, laminated inner pane";
        Assert.Empty(OpeningGuarding.Problems(document, window));

        // A sill at 900 needs nothing.
        window.TypeId = opens.Id;
        window.SillHeight = 900;
        Assert.Empty(OpeningGuarding.Problems(document, window));
    }

    [Fact]
    public void ACurtainWallOverADropHasItsLowGlassLaminated()
    {
        var (document, wall) = Upstairs(curtain: true);
        Assert.Contains(OpeningGuarding.CurtainProblems(document, wall), problem => problem.Contains("below 800"));
        wall.CurtainGlass = CurtainGlass.Laminated;
        Assert.Empty(OpeningGuarding.CurtainProblems(document, wall));
    }

    [Fact]
    public void AHandrailRunsAlongItsInsideOnBrackets()
    {
        var (document, _, _) = Balcony();
        var plain = Put(document, "Guard 1100 - Glass Panel", (6300, 1050), (8450, 1050));
        var railed = Put(document, "Guard 1100 - Glass with Handrail", (6300, 1050), (8450, 1050));
        int Frame(Railing railing) => RailingModel.Meshes(document, railing).First().Positions.Count;
        Assert.True(Frame(railed) > Frame(plain));

        // Its top 900 over the balcony, on its left going along - toward the balcony.
        var top = Railings.Base(document, railed, new Point2D(7000, 1050)) + 900;
        Assert.Contains(RailingModel.Meshes(document, railed).First().Positions, point => Math.Abs(point.Z - top) < 2 && point.Y > 1050 + 50);
    }

    [Fact]
    public void ItsNewSettingsAreSavedAndAnOldProjectIsGivenTheNewTypes()
    {
        var (document, wall) = Upstairs();
        var door = new Door { TypeId = document.TypesOf<DoorType>().First().Id, LevelId = wall.LevelId, HostWallId = wall.Id, DistanceAlongWall = 3000 };
        document.Add(door);
        document.Add(Railings.Juliet(document, door)!);
        var type = Type(document, "Guard 1100 - Glass with Handrail");
        type.IntermediateRails = 1;
        type.HandrailEnds = HandrailEnds.ReturnToWall;

        var back = ProjectFile.FromJson(ProjectFile.ToJson(document));
        var again = back.TypesOf<RailingType>().Single(candidate => candidate.Name == type.Name);
        Assert.Equal((RailingHandrail.Left, 1, HandrailEnds.ReturnToWall), (again.Handrail, again.IntermediateRails, again.HandrailEnds));
        Assert.Equal(door.Id, back.Elements.OfType<Railing>().Single().HostOpeningId);

        // A project saved with the first railing types only is given the new ones.
        foreach (var added in document.TypesOf<RailingType>().Where(candidate => RailingType.Added.Contains(candidate.Name)).ToList()) document.RemoveType(added);
        var old = ProjectFile.FromJson(ProjectFile.ToJson(document));
        Assert.Contains(old.TypesOf<RailingType>(), candidate => candidate.Name == "Guard 1100 - Frameless Glass");
        Assert.Contains(old.TypesOf<RailingType>(), candidate => candidate.Name == "Fence 1800 - Close Board Timber");
    }
}
