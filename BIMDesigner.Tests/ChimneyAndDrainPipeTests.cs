using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;
using BIMDesigner.Infrastructure.Interoperability;
using BIMDesigner.Infrastructure.Serialization;
using Xbim.Ifc4.SharedBldgElements;

namespace BIMDesigner.Tests;

/// <summary>
/// Chimneys - a stack up through the roof, as high above it as the rule asks, capped with a pot
/// on each flue - and where a flat roof's drains send their water: down a pipe inside the
/// building, or out through the edge to a hopper head and a downpipe.
/// </summary>
public class ChimneyAndDrainPipeTests
{
    private static Chimney Put(BimDocument document, Guid level, double x, double y, int flues = 1)
    {
        var chimney = new Chimney { LevelId = level, Location = new Point2D(x, y), Flues = flues };
        document.Add(chimney);
        return chimney;
    }

    [Fact]
    public void DownTheSlopeItStandsAMetreAboveTheRoofAndNoLowerThanTheRoofNearIt()
    {
        var (document, roof, _) = DormerToolTests.House(10000, 8000, 40);
        var chimney = Put(document, roof.LevelId, 2000, 1000);

        var contact = Chimneys.HighestContact(document, chimney)!.Value;
        var top = Chimneys.Top(document, chimney);
        Assert.True(top >= contact + Chimneys.AboveContact - 1e-6);

        // The roof within 2.3 m of it reaches higher up the slope: it is no lower than that.
        var uphill = roof.TopAt(document, new Point2D(2000, 1000 + 225 + Chimneys.RoofReach - 50));
        Assert.True(top >= uphill - 1, $"top {top:0}, roof 2.3 m up the slope {uphill:0}");
        Assert.True(top <= roof.RidgeElevation(document) + 1e-6 || top <= contact + Chimneys.AboveContact + 1e-6);
    }

    [Fact]
    public void NearTheRidgeItStands600AboveIt()
    {
        var (document, roof, _) = DormerToolTests.House(10000, 8000, 40);
        var chimney = Put(document, roof.LevelId, 5000, 3800);

        Assert.True(Chimneys.Top(document, chimney) >= roof.RidgeElevation(document) + Chimneys.AboveRidge - 1);
    }

    [Fact]
    public void UnderTheUsRuleItIs3FtAboveTheRoofAnd2FtAboveAnythingWithin10Ft()
    {
        var (document, roof, _) = DormerToolTests.House(10000, 8000, 40);
        var chimney = Put(document, roof.LevelId, 2000, 1000);
        chimney.Rule = ChimneyRule.ThreeTwoTen;

        var top = Chimneys.Top(document, chimney);
        Assert.True(top >= Chimneys.HighestContact(document, chimney)!.Value + Chimneys.ThreeFeet - 1e-6);
        Assert.True(top >= roof.TopAt(document, new Point2D(2000, 1000 + 225 + Chimneys.TenFeet - 100)) + Chimneys.TwoFeet - 1);
    }

    [Fact]
    public void ItCutsTheRoofAndTheFloorsItRisesThroughAndIsBuiltWithItsCapAndPots()
    {
        var (document, roof, _) = DormerToolTests.House(10000, 8000, 40);
        var floor = new Floor { LevelId = document.Levels[1].Id, TypeId = document.TypesOf<FloorType>().First().Id };
        floor.SetBoundary(new[] { new Point2D(0, 0), new Point2D(10000, 0), new Point2D(10000, 8000), new Point2D(0, 8000) });
        document.Add(floor);
        var chimney = Put(document, roof.LevelId, 2000, 1000, flues: 2);
        chimney.Width = 900;

        Assert.Contains(RoofJoin.Openings(document, roof), hole => Polygon2D.Contains(hole, chimney.Location));
        Assert.DoesNotContain(RoofSolid.Pieces(document, roof), piece => Polygon2D.Contains(piece.Outline, chimney.Location));
        Assert.True(SlabEdges.NetArea(document, floor) < floor.Area - 900 * 450 + 1);

        var meshes = Chimneys.Meshes(document, chimney);
        var top = Chimneys.Top(document, chimney);
        Mesh3D Part(string name) => meshes.Single(mesh => mesh.Description == name);
        Assert.Equal(Chimneys.Foot(document, chimney), meshes[0].Positions.Min(point => point.Z), 6);
        Assert.Equal(top + Chimneys.CapThickness + Chimneys.PotHeight, Part("Chimney Pot").Positions.Max(point => point.Z), 6);
        Assert.Equal(2, Chimneys.Flues(document, chimney).Count);

        // Open down each flue: nothing of the brickwork, the cap or the pot across a flue's middle,
        // down to the solid foot it starts above.
        var opens = Chimneys.Foot(document, chimney) + Chimneys.SolidFoot;
        foreach (var flue in Chimneys.Flues(document, chimney))
        {
            Assert.False(Covers(meshes[0], flue, opens + 1), "The stack is solid over a flue.");
            Assert.True(Covers(meshes[0], flue), "The flue is open underneath.");
            Assert.False(Covers(Part("Concrete, Cast In Situ"), flue), "The cap is solid over a flue.");
            Assert.False(Covers(Part("Chimney Pot"), flue), "A pot is solid.");
            Assert.False(Covers(Part("Flaunching"), flue), "The flaunching is over a flue.");
        }

        // The oversailing course stands out from the stack, and the flashing is let in where it meets the roof.
        Assert.True(meshes[0].Positions.Max(point => Math.Abs(point.Y - chimney.Location.Y)) > chimney.Depth / 2 + 1);
        var lead = Part("Lead Flashing");
        var contact = Chimneys.HighestContact(document, chimney)!.Value;
        Assert.InRange(lead.Positions.Max(point => point.Z), contact, contact + Chimneys.FlashingUp + Chimneys.FlashingApron * Math.Tan(40 * Math.PI / 180) + 10);
    }

    /// <summary>Whether a mesh has a level triangle over a point, seen from above - any at all, or any higher than a height.</summary>
    internal static bool Covers(Mesh3D mesh, Point2D point, double above = double.MinValue)
    {
        for (var i = 0; i + 2 < mesh.Indices.Count; i += 3)
        {
            var (a, b, c) = (mesh.Positions[mesh.Indices[i]], mesh.Positions[mesh.Indices[i + 1]], mesh.Positions[mesh.Indices[i + 2]]);
            if (Math.Max(a.Z, Math.Max(b.Z, c.Z)) < above) continue;
            var triangle = new[] { new Point2D(a.X, a.Y), new Point2D(b.X, b.Y), new Point2D(c.X, c.Y) };
            if (Math.Abs(Polygon2D.SignedArea(triangle)) > 1 && Polygon2D.Contains(triangle, point)) return true;
        }

        return false;
    }

    [Fact]
    public void ItGoesWhereItIsMovedCopiedMirroredSavedAndExported()
    {
        var (document, roof, _) = DormerToolTests.House(10000, 8000, 40);
        var chimney = Put(document, roof.LevelId, 2000, 1000);
        chimney.Angle = 30;
        chimney.ExtraHeight = 200;

        new MoveElementsCommand(new[] { chimney }, new Vector2D(500, 0)).Redo();
        Assert.Equal(2500, chimney.Location.X, 6);

        ElementTransforms.Mirror(chimney, new Line2D(new Point2D(5000, 0), new Vector2D(0, 1)));
        Assert.Equal(7500, chimney.Location.X, 6);
        Assert.Equal(150, chimney.Angle, 6);

        var copy = ElementCopy.Duplicate(document, new[] { chimney }).OfType<Chimney>().Single();
        Assert.Equal(Chimneys.Outline(document, chimney), Chimneys.Outline(document, copy));

        var again = ProjectFile.FromJson(ProjectFile.ToJson(document)).Elements.OfType<Chimney>().Single();
        Assert.Equal(chimney.Location, again.Location);
        Assert.Equal(200, again.ExtraHeight);

        using var model = IfcExport.Build(document);
        Assert.Single(model.Instances.OfType<IfcChimney>());
    }

    /// <summary>A single-storey building, 10 m by 8 m, with a flat roof on its walls at 3 m and a ground floor slab.</summary>
    private static (BimDocument Document, Roof Roof, Floor Ground) FlatRoofedBuilding()
    {
        var document = BimDocument.CreateDefault();
        var ground = document.Levels[0];
        var wallType = document.TypesOf<WallType>().First(t => t.Name.StartsWith("Exterior"));
        var corners = new[] { new Point2D(0, 0), new Point2D(10000, 0), new Point2D(10000, 8000), new Point2D(0, 8000) };
        for (var i = 0; i < 4; i++)
            document.Add(new Wall { Start = corners[i], End = corners[(i + 1) % 4], TypeId = wallType.Id, LevelId = ground.Id, UnconnectedHeight = 3000 });

        var floor = new Floor { LevelId = ground.Id, TypeId = document.TypesOf<FloorType>().First().Id };
        floor.SetBoundary(corners);
        document.Add(floor);

        var roof = new Roof { TypeId = document.TypesOf<RoofType>().First(t => t.Name.Contains("Flat")).Id, LevelId = ground.Id, HeightOffset = 3000 };
        roof.SetBoundary(corners);
        document.Add(roof);
        return (document, roof, floor);
    }

    [Fact]
    public void ADrainInTheMiddleSendsItsWaterDownInsideThroughTheFloorToTheDrainUnderIt()
    {
        var (document, roof, ground) = FlatRoofedBuilding();
        var drain = new RoofDrain { RoofId = roof.Id, LevelId = roof.LevelId, Location = new Point2D(5000, 4000) };
        document.Add(drain);

        var pipe = RoofDrainage.Pipe(document, drain)!;
        Assert.Equal(roof.TopAt(document, drain.Location) - 40, pipe.Points[0].Z, 6);
        Assert.Equal(-RoofDrainage.BelowGround, pipe.Points[1].Z, 6);
        Assert.Equal(drain.Location, pipe.Points[1].Plan);
        Assert.Null(pipe.Hopper);

        // A sleeve through the ground floor slab for it.
        Assert.Single(RoofDrainage.PipeHoles(document, ground));
        Assert.True(SlabEdges.NetArea(document, ground) < ground.Area);
        Assert.Contains(ModelMeshBuilder.Build(document), mesh => mesh.ElementId == drain.Id && mesh.Description == "Rainwater Pipe");
    }

    [Fact]
    public void ADrainByTheEdgeSendsItsWaterOutThroughItToAHopperAndDownpipe()
    {
        var (document, roof, ground) = FlatRoofedBuilding();
        var drain = new RoofDrain { RoofId = roof.Id, LevelId = roof.LevelId, Location = new Point2D(5000, 400), Discharge = DrainDischarge.ThroughEdge };
        document.Add(drain);

        var pipe = RoofDrainage.Pipe(document, drain)!;
        Assert.NotNull(pipe.Hopper);

        // Out past the outside face of the wall under the edge, down to a shoe off the ground.
        var half = document.TypesOf<WallType>().First(t => t.Name.StartsWith("Exterior")).Structure.TotalWidth / 2;
        var down = pipe.Points[2];
        Assert.True(down.Y < -half, $"the downpipe is at y {down.Y:0}, the wall's face at {-half:0}");
        Assert.Equal(Downpipes.ShoeRise - Downpipes.ShoeReach, pipe.Points[^1].Z, 3);
        Assert.Empty(RoofDrainage.PipeHoles(document, ground));
    }

    [Fact]
    public void OnlyADrainNearTheEdgeCanSendItsWaterOutThroughIt()
    {
        var (document, roof, _) = FlatRoofedBuilding();
        var drain = new RoofDrain { RoofId = roof.Id, LevelId = roof.LevelId, Location = new Point2D(5000, 4000) };
        document.Add(drain);

        var goes = drain.GetInstanceParameters(document).Single(parameter => parameter.Name == "Water Goes");
        Assert.False(goes.TrySet(RoofDrainage.DischargeName(DrainDischarge.ThroughEdge)));

        drain.Location = new Point2D(5000, 500);
        Assert.True(goes.TrySet(RoofDrainage.DischargeName(DrainDischarge.ThroughEdge)));
        Assert.Equal(DrainDischarge.ThroughEdge, drain.Discharge);
    }

    [Fact]
    public void TheToolsPutUpAChimneyAndChooseWhereADrainsWaterGoes()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var (document, roof, _) = FlatRoofedBuilding();
                var plan = new BIMDesigner.UI.Controls.PlanView { Document = document, ActiveLevelId = roof.LevelId, History = new UndoStack() };

                plan.SetTool(BIMDesigner.UI.Controls.PlanTool.RoofDrain);
                Assert.True(plan.PlaceRoofDrainAt(new Point2D(5000, 4000)));
                Assert.True(plan.PlaceRoofDrainAt(new Point2D(5000, 500)));
                var drains = document.Elements.OfType<RoofDrain>().ToList();
                Assert.Equal(DrainDischarge.Internal, drains[0].Discharge);
                Assert.Equal(DrainDischarge.ThroughEdge, drains[1].Discharge);

                plan.SetTool(BIMDesigner.UI.Controls.PlanTool.Chimney);
                Assert.True(plan.PlaceChimneyAt(new Point2D(2000, 2000)));
                var chimney = document.Elements.OfType<Chimney>().Single();
                Assert.True(Chimneys.Top(document, chimney) >= roof.TopAt(document, chimney.Location) + Chimneys.AboveContact - 1);

                plan.History!.Undo();
                Assert.Empty(document.Elements.OfType<Chimney>());
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Exception("The plan failed on the UI thread.", failure);
    }
}
