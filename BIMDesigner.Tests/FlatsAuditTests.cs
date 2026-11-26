using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;

namespace BIMDesigner.Tests;

/// <summary>
/// What building a block of flats with a maisonette end to end turned up (round 150): a compartment wall up to the floor over it was said to
/// stop short of the floor over that; a block's common stair was taken as a private stair; a door swinging into a room behind a wall at a
/// stair's foot was said to swing across it; a smoke vent in a pitched roof over the top of the stair was not found, being over the roof's
/// level; and the library had no outside wall heavy enough inside where separating walls and floors meet it.
/// </summary>
[Collection("Wpf")]
public class FlatsAuditTests
{
    /// <summary>The floor of each storey over the ground: a separating slab over the block, the stair's well open in it.</summary>
    private static void Floors(BimDocument document, IEnumerable<SmokeControlTests.Storey> storeys)
    {
        var slab = document.TypesOf<FloorType>().First(type => type.Name == "Floor - Separating Slab, Floating 278mm");
        foreach (var storey in storeys)
        {
            var floor = new Floor { LevelId = storey.Level.Id, TypeId = slab.Id };
            floor.SetBoundary(new[] { new Point2D(0, 0), new Point2D(12000, 0), new Point2D(12000, 6000), new Point2D(0, 6000) });
            floor.SetHoles(new[] { new[] { new Point2D(1000, 500), new Point2D(2000, 500), new Point2D(2000, 4000), new Point2D(1000, 4000) } });
            document.Add(floor);
        }
    }

    [Fact]
    public void ACompartmentWallUpToTheFloorOverItReachesIt()
    {
        // Three storeys, each floor over the ground a separating slab 278 deep: the ground floor's wall between the corridor and the flat
        // runs up to the first floor's level, into the slab over it - it reaches it; it was taken to stop 2722 short of the second floor's.
        var (document, storeys) = SmokeControlTests.Block(3);
        Floors(document, storeys.Skip(1));
        var wall = document.Walls.Single(each => each.LevelId == storeys[0].Level.Id && each.Start == new Point2D(3000, 2000));
        var standard = document.ProjectInformation.ClearanceStandard;
        Assert.DoesNotContain(WallRules.Problems(document, wall, standard), problem => problem.Said.Contains("below the floor over it"));

        // Drawn 2400 high, it does stop short of the slab's underside.
        wall.UnconnectedHeight = 2400;
        var said = WallRules.Problems(document, wall, standard);
        var slab = document.TypesOf<FloorType>().First(type => type.Name == "Floor - Separating Slab, Floating 278mm").Thickness;
        Assert.Contains(said, problem => problem.Said.Contains($"stops {3000 - slab - 2400:0} below the floor over it"));
    }

    [Fact]
    public void ABlocksCommonStairIsAGeneralAccessStairAFlatsOwnPrivate()
    {
        var (document, storeys) = SmokeControlTests.Block(2);
        var stair = document.All<Stair>().Single();
        Assert.Equal(StairUse.Private, stair.Use);
        Assert.Equal(StairUse.GeneralAccess, Stairs.UseOf(document, stair));
        Assert.Equal(StairUse.GeneralAccess, Stairs.UseFor(document, storeys[0].Level.Id, new Point2D(1500, 3000)));
        Assert.Equal(StairUse.Private, Stairs.UseFor(document, storeys[0].Level.Id, new Point2D(7500, 4000)));

        // A house's stair is its own.
        var (house, _) = StairTests.House();
        Assert.Equal(StairUse.Private, Stairs.UseOf(house, StairTests.Put(house, 3000, 600)));
    }

    [Fact]
    public void ADoorBehindAWallAtAStairsFootSwingsNotAcrossIt()
    {
        // A stair rising from 1000; a wall 250 behind its foot with a door in it.
        var (document, _) = StairTests.House();
        var stair = StairTests.Put(document, 3000, 1000);
        var partition = document.TypesOf<WallType>().First(type => type.Name == "Interior - Partition 125mm");
        var wall = new Wall { Start = new Point2D(2000, 750), End = new Point2D(4000, 750), TypeId = partition.Id, LevelId = document.Levels[0].Id, UnconnectedHeight = 3000 };
        document.Add(wall);
        var door = new Door { HostWallId = wall.Id, LevelId = wall.LevelId, TypeId = document.TypesOf<DoorType>().First(type => type.Name == "Single - 900 x 2100").Id, DistanceAlongWall = 1000 };
        document.Add(door);
        bool Across() => Stairs.Problems(document, stair).Any(problem => problem.Contains("within 400 of its bottom step"));

        // Opening toward the stair, over its foot: said. Turned to open into the room behind: not, the wall between.
        Assert.True(Across());
        door.FlipFacing = true;
        Assert.False(Across());
    }

    [Fact]
    public void ASmokeVentInAPitchedRoofOverTheStairIsFoundOverTheRoofsLevel()
    {
        // Three storeys, a gable roof on the top one at 40 degrees and a roof level datum at its eaves, 9 m; an automatic vent of 1 m² over
        // the stair, 1.5 m up the slope - over the datum, as a pitched roof's windows are.
        var (document, storeys) = SmokeControlTests.Block(3);
        document.AddLevel(new Level { Name = "Roof", Elevation = 9000 });
        var roof = new Roof { LevelId = storeys[2].Level.Id, TypeId = document.TypesOf<RoofType>().First(type => type.Name == "Roof - Slate on Timber Rafters 150mm").Id, HeightOffset = 3000 };
        roof.SetBoundary(new[] { new Point2D(-300, -300), new Point2D(12300, -300), new Point2D(12300, 6300), new Point2D(-300, 6300) });
        roof.SetEdges(Enumerable.Range(0, 4).Select(i => new RoofEdge { DefinesSlope = i is 0 or 2, SlopeDegrees = 40 }));
        document.Add(roof);
        Assert.Contains(SmokeControlTests.Smoke(document), problem => problem.Said.Contains("3.28e"));

        var type = document.TypesOf<RoofWindowType>().First().Duplicate("Automatic Opening Vent");
        (type.SmokeVent, type.SmokeVentArea) = (SmokeVentKind.Automatic, 1.0);
        document.AddType(type);
        var vent = new RoofWindow { TypeId = type.Id, LevelId = roof.LevelId, RoofId = roof.Id, Location = new Point2D(1500, 1200) };
        document.Add(vent);
        var frame = RoofWindows.Frame(document, vent)!;
        Assert.True(frame.Corners.Min(frame.Top) > 9000);
        Assert.DoesNotContain(SmokeControlTests.Smoke(document), problem => problem.Said.Contains("3.28e"));
    }

    [Fact]
    public void TheLibraryHasAnOutsideWallHeavyEnoughInsideForABlockOfFlats()
    {
        // Its inner leaf 100 of 1400 kg/m³ block: 140 kg/m², past Approved Document E's 120 (2.39, 3.70); its U-value within Approved Document L's 0.26.
        var document = BimDocument.CreateDefault();
        var type = document.TypesOf<WallType>().Single(each => each.Name == "Cavity Wall - Brick on Medium Dense Block 315mm");
        var inner = type.Structure.Layers.Single(layer => layer.Function == LayerFunction.Structure);
        Assert.Equal(140, document.FindMaterial(inner.MaterialId)!.Density * inner.Thickness / 1000, 6);
        Assert.InRange(WallRules.UValue(document, type)!.Value, 0.20, Thermal.WallLimit);
    }
}
