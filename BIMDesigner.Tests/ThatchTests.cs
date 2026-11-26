using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;
using BIMDesigner.Core.Rendering;

namespace BIMDesigner.Tests;

/// <summary>
/// A thatched cottage (round 151): water reed thatch in the library - 300 on insulated rafters, drawn as thatch, EROOF(t4) unless shown
/// better (Approved Document B 12.9) - and a solid fuel chimney through it standing as Approved Document J's Diagram 18 asks: 1800 above the
/// weather surface, and within 2300 of the ridge 600 above the ridge too (2.12). And what building one turned up: a floor on stone walls lined
/// inside was not carried by them, their core standing outside their line; a roof window was no purge air, and one high up a steep roof was
/// not counted over its room at all.
/// </summary>
public class ThatchTests
{
    private static RoofType Thatch(BimDocument document) => document.TypesOf<RoofType>().Single(type => type.Name == "Roof - Water Reed Thatch 300mm on Insulated Rafters");

    [Fact]
    public void WaterReedThatchIsInTheLibraryAsItIs()
    {
        var document = BimDocument.CreateDefault();
        var type = Thatch(document);
        var reed = document.FindMaterial(type.Structure.Layers[0].MaterialId)!;
        Assert.Equal("Thatch, Water Reed", reed.Name);
        Assert.Equal(300, type.Structure.Layers[0].Thickness);

        // 0.09 W/mK and 133 kg/m³: 40 kg/m² over the 300, the heaviest thatch wet.
        Assert.Equal(0.09, reed.ThermalConductivity, 6);
        Assert.Equal(40, reed.Density * 0.3, 0);

        // EROOF(t4) by Approved Document B 12.9; under Approved Document L's 0.16 with the rafters' insulation.
        var (designation, by) = RoofDesignation.Of(reed, 300)!.Value;
        Assert.Equal("EROOF(t4)", designation.ToString());
        Assert.Contains("12.9", by);
        Assert.InRange(Thermal.UValue(document, type.Structure, Thermal.Flow.Upward)!.Value, 0.10, Thermal.RoofLimit);

        // Drawn as thatch, stems down the slope in courses up it.
        var finish = SurfacePatterns.Guess(reed.Name, reed.SurfaceColour);
        Assert.Equal(SurfacePattern.Thatch, finish.Pattern);
        Assert.NotNull(SurfaceTextures.Of(finish));
    }

    [Fact]
    public void AChimneyThroughThatchStands1800AboveItAnd600AboveTheRidgeNearIt()
    {
        // A gable roof at 40 degrees over a house 10 m by 8 m, its ridge at y 4000.
        var (document, roof, _) = DormerToolTests.House(10000, 8000, 40);
        var low = new Chimney { LevelId = roof.LevelId, Location = new Point2D(2000, 1000) };
        var near = new Chimney { LevelId = roof.LevelId, Location = new Point2D(7000, 2900) };
        document.Add(low);
        document.Add(near);

        // Tiled: a metre above where it comes through, or as high as the ridge (Diagram 17).
        Assert.False(Chimneys.EasilyIgnited(document, roof));
        var ridge = roof.RidgeElevation(document);
        Assert.Equal(ridge, Chimneys.Top(document, near), 1);

        // Thatched: 1800 above where it comes through, 3 m from the ridge; within 2300 of it, 600 over the ridge (Diagram 18).
        roof.TypeId = Thatch(document).Id;
        Assert.True(Chimneys.EasilyIgnited(document, roof));
        var contact = Chimneys.HighestContact(document, low)!.Value;
        Assert.True(Chimneys.Top(document, low) >= contact + Chimneys.AboveThatch - 1e-6);
        Assert.True(Chimneys.Top(document, near) >= ridge + Chimneys.AboveRidge - 1e-6);
        Assert.True(Chimneys.Top(document, near) >= Chimneys.HighestContact(document, near)!.Value + Chimneys.AboveThatch - 1e-6);
    }

    [Fact]
    public void AFloorBearsOnStoneWallsWhoseCoreStandsOutsideTheirLine()
    {
        // Rubble stone 500 lined inside: its core 69 outside its centre line, where the floor is drawn to. Gables and two block walls between,
        // all bearing, the floor spanning across them: carried all over.
        var document = BimDocument.CreateDefault();
        var (ground, first) = (document.Levels[0], document.Levels[1]);
        void Add(double ax, double ay, double bx, double by, string type) => document.Add(new Wall
        {
            Start = new Point2D(ax, ay), End = new Point2D(bx, by), TypeId = document.TypesOf<WallType>().First(t => t.Name == type).Id, LevelId = ground.Id,
            UnconnectedHeight = 3000, StructuralUsage = StructuralUsage.Bearing
        });
        const string stone = "Stone Wall - Rubble 500mm, Insulated Lining";
        Add(0, 0, 0, 6500, stone);
        Add(0, 6500, 9000, 6500, stone);
        Add(9000, 6500, 9000, 0, stone);
        Add(9000, 0, 0, 0, stone);
        Add(4380, 0, 4380, 6500, "Block Wall - Dense 100mm");
        Add(6120, 0, 6120, 6500, "Block Wall - Dense 100mm");
        var floor = new Floor { LevelId = first.Id, TypeId = document.TypesOf<FloorType>().First(t => t.Name == "Floor - Timber 220mm").Id, SpanDirection = 0 };
        floor.SetBoundary(new[] { new Point2D(0, 0), new Point2D(9000, 0), new Point2D(9000, 6500), new Point2D(0, 6500) });
        document.Add(floor);

        var model = AnalyticalModel.Of(document);
        Assert.Contains(model.Panels, panel => panel.Kind == AnalyticalPanelKind.Wall && panel.Outline.All(node => Math.Abs(node.At.X + 69) < 1));
        Assert.DoesNotContain(StructuralLoading.Of(document, model).Problems, problem => problem.Elements.Contains(floor));
    }

    [Fact]
    public void ARoofWindowPurgesARoomsAirAndCountsOverItHighUpASteepRoof()
    {
        // A room in a 50 degree roof over the first floor, the project's roof level 3 m over that - under the roof's windows high up its slope.
        var document = BimDocument.CreateDefault();
        var first = document.Levels[1];
        document.AddLevel(new Level { Name = "Roof", Elevation = first.Elevation + 3000 });
        var stone = document.TypesOf<WallType>().First(t => t.Name == "Exterior - Brick on Block 330mm");
        var roof = new Roof { LevelId = first.Id, TypeId = document.TypesOf<RoofType>().First(t => t.Name == "Roof - Water Reed Thatch 300mm on Insulated Rafters").Id, HeightOffset = 600 };
        roof.SetBoundary(new[] { new Point2D(-450, -450), new Point2D(3950, -450), new Point2D(3950, 6950), new Point2D(-450, 6950) });
        roof.SetEdges(Enumerable.Range(0, 4).Select(i => new RoofEdge { DefinesSlope = i is 0 or 2, SlopeDegrees = 50 }));
        document.Add(roof);
        foreach (var (a, b, h) in new[] { ((0.0, 0.0), (0.0, 6500.0), 3000.0), ((0.0, 6500.0), (3500.0, 6500.0), 600.0), ((3500.0, 6500.0), (3500.0, 0.0), 3000.0), ((3500.0, 0.0), (0.0, 0.0), 600.0) })
            document.Add(new Wall { Start = new Point2D(a.Item1, a.Item2), End = new Point2D(b.Item1, b.Item2), TypeId = stone.Id, LevelId = first.Id, UnconnectedHeight = h, TopAttachedTo = roof.Id });
        var room = new Room { LevelId = first.Id, Location = new Point2D(1750, 3250), Name = "Bedroom 1" };
        document.Add(room);
        var outline = room.GetBoundary(document).Polygon;
        Assert.Contains("purge its air", WindowRules.Verdict(document, room) ?? string.Empty);

        // A top-hung roof window 2.25 m up the back slope: over the roof level, and still over the room - its sash, 1.04 m², opening fully,
        // past a twentieth of the room's 19.6 m².
        var type = document.TypesOf<RoofWindowType>().First(t => t.Name == "Roof Window - Top Hung Escape 1140 x 1180");
        var window = new RoofWindow { RoofId = roof.Id, LevelId = roof.LevelId, TypeId = type.Id, Location = new Point2D(1750, 4700) };
        document.Add(window);
        var frame = Assert.Single(WindowRules.RoofWindowsOver(document, room, outline));
        Assert.True(frame.Corners.Min(frame.Top) > first.Elevation + 3000);
        Assert.DoesNotContain("purge its air", WindowRules.Verdict(document, room) ?? string.Empty);
    }
}
