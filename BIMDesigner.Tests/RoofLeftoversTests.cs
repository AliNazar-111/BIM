using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Energy;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Mep;
using BIMDesigner.Infrastructure.Serialization;

namespace BIMDesigner.Tests;

/// <summary>
/// The roofs' leftovers: an edge locked to a floor's or another roof's edge following it; sloped
/// glazing - glass between bars on each face of a roof, laid out, quantified and counted as glazing.
/// </summary>
public class RoofLeftoversTests
{
    private static Point2D[] Box(double x0, double y0, double x1, double y1) =>
        new[] { new Point2D(x0, y0), new Point2D(x1, y0), new Point2D(x1, y1), new Point2D(x0, y1) };

    /// <summary>A 6 by 4 m floor, and a flat roof over it 500 out all round, its front edge locked 500 out from the floor's front edge.</summary>
    private static (BimDocument Document, Floor Floor, Roof Roof) LockedToAFloor()
    {
        var document = BimDocument.CreateDefault();
        var level = document.Levels[0].Id;
        var floor = new Floor { LevelId = level, TypeId = document.TypesOf<FloorType>().First().Id };
        floor.SetBoundary(Box(0, 0, 6000, 4000));
        document.Add(floor);

        var roof = new Roof { LevelId = level, TypeId = document.TypesOf<RoofType>().First().Id, HeightOffset = 3000 };
        roof.SetBoundary(Box(-500, -500, 6500, 4500));
        // To the left of the floor's front edge, (0,0) to (6000,0), is up the plan: 500 out is -500 to its left.
        roof.SetEdges(new[]
        {
            new RoofEdge { SlabId = floor.Id, SlabEdgeIndex = 0, SlabCorners = 4, SlabOffset = -500 }, new RoofEdge(), new RoofEdge(), new RoofEdge()
        });
        document.Add(roof);
        return (document, floor, roof);
    }

    [Fact]
    public void AnEdgeLockedToAFloorsEdgeFollowsItAsTheFloorMovesAndGainsACorner()
    {
        var (document, floor, roof) = LockedToAFloor();
        Assert.False(RoofSketch.FollowWalls(document));

        // The floor 1 m up the plan: the roof's front edge with it, still 500 out; its other edges, drawn, stay.
        floor.SetBoundary(Box(0, 1000, 6000, 5000));
        Assert.True(RoofSketch.FollowWalls(document));
        Assert.Equal(new[] { new Point2D(-500, 500), new Point2D(6500, 500), new Point2D(6500, 4500), new Point2D(-500, 4500) }, roof.Boundary);

        // Drawn again from another corner with a corner more on its back edge - every edge's place in the outline
        // changed: the front edge found again as the one running the same way nearest where the locked edge is.
        floor.SetBoundary(new[] { new Point2D(6000, 1000), new Point2D(6000, 5000), new Point2D(3000, 5000), new Point2D(0, 5000), new Point2D(0, 1000) });
        Assert.False(RoofSketch.FollowWalls(document));
        floor.SetBoundary(new[] { new Point2D(6000, 1500), new Point2D(6000, 5000), new Point2D(3000, 5000), new Point2D(0, 5000), new Point2D(0, 1500) });
        Assert.True(RoofSketch.FollowWalls(document));
        Assert.Equal(1000, roof.Boundary[0].Y, 6);

        // The floor gone, the edge stays where it is.
        document.Remove(floor);
        Assert.False(RoofSketch.FollowWalls(document));
        Assert.Equal(1000, roof.Boundary[0].Y, 6);
    }

    [Fact]
    public void AnEdgeLockedToAnotherRoofsEdgeFollowsThatRoof()
    {
        var (document, _, main) = LockedToAFloor();
        var level = document.Levels[0].Id;

        // A lean-to against the main roof's right-hand edge, (6500,-500) to (6500,4500), locked to it by its id.
        var lean = new Roof { LevelId = level, TypeId = main.TypeId, HeightOffset = 3000 };
        lean.SetBoundary(Box(6500, -500, 9500, 4500));
        lean.SetEdges(new[]
        {
            new RoofEdge { DefinesSlope = true }, new RoofEdge(), new RoofEdge(),
            new RoofEdge { SlabId = main.Id, SlabEdgeId = main.Edges[1].Id, SlabEdgeIndex = 1, SlabCorners = 4 }
        });
        document.Add(lean);

        // The main roof's outline turned to start from another corner and its right-hand edge moved 1 m out: the
        // lean-to's edge goes with it, found by the edge's own id.
        var edges = main.Edges.Select(edge => edge.Copy()).ToList();
        main.SetBoundary(new[] { new Point2D(7500, -500), new Point2D(7500, 4500), new Point2D(-500, 4500), new Point2D(-500, -500) });
        main.SetEdges(new[] { edges[1], edges[2], edges[3], edges[0] });
        Assert.True(RoofSketch.FollowWalls(document));
        Assert.Equal(7500, lean.Boundary.Min(point => point.X), 6);
        Assert.Equal(9500, lean.Boundary.Max(point => point.X), 6);
    }

    [Fact]
    public void ARoofCopiedWithoutTheFloorItIsLockedToStaysWhereItIsPut()
    {
        var (document, floor, roof) = LockedToAFloor();

        var alone = (Roof)ElementCopy.Duplicate(document, new[] { roof }).Single();
        Assert.Null(alone.Edges[0].SlabId);

        var both = ElementCopy.Duplicate(document, new Element[] { floor, roof });
        var (copiedFloor, copiedRoof) = (both.OfType<Floor>().Single(), both.OfType<Roof>().Single());
        Assert.Equal(copiedFloor.Id, copiedRoof.Edges[0].SlabId);
        Assert.Equal(floor.Id, roof.Edges[0].SlabId);
    }

    [Fact]
    public void ALockedEdgeAndASlopedGlazingTypeAreKeptWithTheProject()
    {
        var (document, floor, _) = LockedToAFloor();
        var glazing = document.TypesOf<RoofType>().Single(type => type.SlopedGlazing);
        glazing.UpBarSpacing = 1200;
        glazing.SolarHeatGainCoefficient = 0.38;

        var reopened = ProjectFile.FromJson(ProjectFile.ToJson(document));
        var edge = reopened.All<Roof>().Single().Edges[0];
        Assert.Equal((floor.Id, 0, 4, -500.0), (edge.SlabId!.Value, edge.SlabEdgeIndex, edge.SlabCorners, edge.SlabOffset));
        var kept = reopened.TypesOf<RoofType>().Single(type => type.SlopedGlazing);
        Assert.Equal((1200.0, 2000.0, 60.0, 150.0, 0.38, glazing.GlazingBarMaterialId), (kept.UpBarSpacing, kept.CrossBarSpacing, kept.GlazingBarWidth, kept.GlazingBarDepth, kept.SolarHeatGainCoefficient, kept.GlazingBarMaterialId));
        Assert.False(reopened.TypesOf<RoofType>().First(type => !type.SlopedGlazing).SlopedGlazing);
    }

    private static (BimDocument Document, Roof Roof, RoofType Glazing) Glazed(double width, double depth, double pitch)
    {
        var (document, roof, _) = DormerToolTests.House(width, depth, pitch);
        var glazing = document.TypesOf<RoofType>().Single(type => type.SlopedGlazing);
        roof.TypeId = glazing.Id;
        return (document, roof, glazing);
    }

    [Fact]
    public void SlopedGlazingIsLaidFaceByFaceInItsOwnSlope()
    {
        // A 6 by 4 m gable roof at 30°, eaves front and back: two faces 6 m along and 2 m up in plan,
        // 2 / cos 30° = 2.309 m up the slope.
        var (document, roof, glazing) = Glazed(6000, 4000, 30);
        var stretch = 1 / Math.Cos(30 * Math.PI / 180);
        var faces = GlazedRoofs.Faces(document, roof);
        Assert.Equal(2, faces.Count);

        // Bars up the slope at 1 m centred along the 6 m eave: none on the verges, five between - x 1 to 5 m.
        // Across the slope at 2 m up the face from the eave: one, 1.732 m in plan from it, the last 0.577 m to the
        // ridge less than a spacing. Round each face its edges; the ridge one bar for both.
        foreach (var face in faces)
        {
            var up = face.Bars.Where(bar => bar.Run == GlazingBarRun.UpSlope).ToList();
            Assert.Equal(new[] { 1000.0, 2000, 3000, 4000, 5000 }, up.Select(bar => Math.Round(bar.From.X, 3)).OrderBy(x => x));
            Assert.All(up, bar => Assert.Equal(2000 * stretch, bar.Length, 3));
            var across = Assert.Single(face.Bars, bar => bar.Run == GlazingBarRun.AcrossSlope);
            Assert.Equal(6000, across.Length, 3);
            Assert.Equal(2000 * Math.Cos(30 * Math.PI / 180), Math.Min(across.From.Y, 4000 - across.From.Y), 3);
        }

        Assert.Equal(7, faces.Sum(face => face.Bars.Count(bar => bar.Run == GlazingBarRun.Edge)));

        // By hand: edges 3 x 6 m (two eaves and the ridge) and 4 verges of 2.309 m, 27.238 m; up-slope bars 10 x 2.309 m,
        // 23.094 m; across 2 x 6 m. 62.332 m in all. Glass 2 x 6 x 2.309 = 27.713 m², 24 panes of 1 x 2 m at most.
        Assert.Equal(18 + 4 * 2 * stretch + 10 * 2 * stretch + 12, GlazedRoofs.BarLength(document, roof), 3);
        Assert.Equal(2 * 6 * 2 * stretch, GlazedRoofs.Area(document, roof), 3);
        var (panes, largest) = GlazedRoofs.Panes(document, roof);
        Assert.Equal(24, panes);
        Assert.Equal(2.0, largest, 3);

        // Its frame factor: the bars' 60 mm over the glass, edges half - (27.238 x 0.5 + 35.094) x 0.06 / 27.713, 10.55% bar.
        var bars = (0.5 * (18 + 8 * stretch) + 20 * stretch + 12) * 0.06;
        Assert.Equal(1 - bars / (24 * stretch), GlazedRoofs.GlassShare(document, roof), 6);

        // Its quantities on the roof, and its U the glazing's own, not its glass's layers'.
        var parameters = roof.GetInstanceParameters(document).ToDictionary(parameter => parameter.Definition.Name);
        Assert.Equal(24, Convert.ToInt32(parameters["Panes"].Value));
        Assert.Equal(glazing.HeatTransferCoefficient, SlabRules.UValue(document, roof));
        Assert.Equal(1.8, glazing.HeatTransferCoefficient);

        // In 3D: the glass seen through, and the bars.
        var meshes = Core.Views.ModelMeshBuilder.Build(document).Where(mesh => mesh.ElementId == roof.Id).ToList();
        Assert.Contains(meshes, mesh => mesh.Kind == MeshKind.Glazing);
        Assert.Contains(meshes, mesh => mesh.Kind == MeshKind.Mullion);
        Assert.DoesNotContain(meshes, mesh => mesh.Kind == MeshKind.Roof);
    }

    [Fact]
    public void SlopedGlazingLosesHeatAndLetsInSunAsGlazingFaceByFace()
    {
        // One room under a 40° gable glazed all over, eaves front and back.
        var (document, roof, glazing) = Glazed(10000, 8000, 40);
        var level = document.Levels[0].Id;
        var floor = new Floor { LevelId = level, TypeId = document.TypesOf<FloorType>().First(type => type.Name == "Floor - Screed on Slab 250mm").Id };
        floor.SetBoundary(Box(0, 0, 10000, 8000));
        document.Add(floor);
        var room = new Room { LevelId = level, Location = new Point2D(5000, 4000), Name = "Garden Room", Number = "1" };
        document.Add(room);

        // Each face's part over the room, on the face: half the room's plan by 1 / cos 40° each, at the glazing's U, falling south and north.
        var lines = HeatLoss.Of(document)!.Of(room)!.Lines;
        Assert.DoesNotContain(lines, line => line.Kind == HeatSurface.Roof);
        var glass = lines.Where(line => line.Through == roof).ToList();
        Assert.Equal(2, glass.Count);
        Assert.All(glass, line => Assert.Equal((HeatSurface.Window, 1.8), (line.Kind, line.Bare)));
        Assert.All(glass, line => Assert.Equal(40, line.Tilt!.Value, 6));
        Assert.Equal(new[] { 0.0, 180 }, glass.Select(line => Math.Round(line.Facing!.Value, 6)).OrderBy(facing => facing));
        var plan = Polygon2D.Area(room.GetBoundary(document).Polygon) / 1e6;
        Assert.Equal(plan / Math.Cos(40 * Math.PI / 180), glass.Sum(line => line.Area), 3);

        // SAP takes each as a roof window at its pitch, its g the glazing's and its frame factor the glass between the bars.
        var windows = SapSurvey.Of(document).Home.Elements.Where(element => element.Source == roof).ToList();
        Assert.Equal(2, windows.Count);
        Assert.All(windows, window => Assert.Equal((SapSurface.RoofWindow, 40.0, glazing.SolarHeatGainCoefficient), (window.Kind, Math.Round(window.Tilt, 6), window.SolarTransmittance)));
        Assert.All(windows, window => Assert.Equal(GlazedRoofs.GlassShare(document, roof), window.FrameFactor, 6));
    }

    [Fact]
    public void SlopedGlazingsBarsAreSetOnItsTypeWithinWhatCanBeLaid()
    {
        var document = BimDocument.CreateDefault();
        var plain = document.TypesOf<RoofType>().First(type => !type.SlopedGlazing);
        Assert.DoesNotContain(plain.GetTypeParameters(document), parameter => parameter.Definition == GlazedRoofParameters.UpBarSpacing);

        var glazing = document.TypesOf<RoofType>().Single(type => type.SlopedGlazing);
        var spacing = glazing.GetTypeParameters(document).Single(parameter => parameter.Definition == GlazedRoofParameters.UpBarSpacing);
        Assert.False(spacing.TrySet(200.0));
        Assert.True(spacing.TrySet(1250.0));
        Assert.Equal(1250, glazing.UpBarSpacing);
        Assert.NotNull(RoofType.Unlaid(1000, 2000, 300, 150));
        Assert.Null(RoofType.Unlaid(1000, 2000, 60, 150));
    }
}
