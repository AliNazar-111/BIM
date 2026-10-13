using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Infrastructure.Serialization;

namespace BIMDesigner.Tests;

/// <summary>
/// What stands on a storey stands on its floor's top: walls, columns and furniture on a storey
/// whose floor - or the flat roof over the storey below - is built up above its level are set
/// on top of the slab, not buried in it, and follow it however thick it is made.
/// </summary>
public class FloorSurfacesTests
{
    /// <summary>A storey of walls 3 m high, 12 x 10 m, under a 320 mm flat roof that is the floor of the storey above.</summary>
    private static (BimDocument Document, Roof Deck, Guid First) Building(bool deck = true)
    {
        var document = BimDocument.CreateDefault();
        var ground = document.Levels[0];
        var first = document.Levels.OrderBy(level => level.Elevation).First(level => level.Elevation > 2000);
        var wallType = document.TypesOf<WallType>().First(t => t.Name.StartsWith("Exterior"));

        var corners = new[] { new Point2D(0, 0), new Point2D(12000, 0), new Point2D(12000, 10000), new Point2D(0, 10000) };
        for (var i = 0; i < 4; i++)
            document.Add(new Wall { Start = corners[i], End = corners[(i + 1) % 4], TypeId = wallType.Id, LevelId = ground.Id, UnconnectedHeight = first.Elevation });

        var flat = document.TypesOf<RoofType>().First(t => t.Name.Contains("Flat"));
        var roof = new Roof { TypeId = flat.Id, LevelId = ground.Id, HeightOffset = first.Elevation };
        roof.SetBoundary(corners);
        roof.SetEdges(corners.Select(_ => new RoofEdge()).ToList());
        if (deck) document.Add(roof);

        return (document, roof, first.Id);
    }

    private static void Add(BimDocument document, params Core.Elements.Element[] elements) =>
        FloorSurfaces.Standing(document, new AddElementsCommand(document, elements, "Add")).Redo();

    private static Wall Upstairs(BimDocument document, Guid first) => new()
    {
        Start = new Point2D(2000, 3000), End = new Point2D(9000, 3000), LevelId = first, UnconnectedHeight = 3000,
        TypeId = document.TypesOf<WallType>().First(t => t.Name.StartsWith("Exterior")).Id
    };

    private static Component Chair(BimDocument document, Guid first, double x = 5000) => new()
    {
        TypeId = document.TypesOf<ComponentType>().First(t => !t.IsHosted).Id, LevelId = first, Location = new Point2D(x, 5000)
    };

    private static double Foot(BimDocument document, Component component) =>
        ComponentHosting.BaseElevation(document, component, document.FindType<ComponentType>(component.TypeId)!);

    [Fact]
    public void AWallDrawnUpstairsStandsOnTheFlatRoofUnderItNotInIt()
    {
        var (document, deck, first) = Building();
        var level = document.FindLevel(first)!.Elevation;
        var top = deck.GetTopElevation(document);
        Assert.True(top > level + 100, "The deck is built up above the level.");

        var wall = Upstairs(document, first);
        Add(document, wall);

        // On the deck's top, as tall as it was drawn: the storey keeps its own height.
        Assert.Equal(deck.Id, wall.BaseAttachedTo);
        Assert.Equal(top, wall.GetBaseElevation(document), 6);
        Assert.Equal(top + 3000, wall.GetTopElevation(document), 6);
    }

    [Fact]
    public void FurnitureAndColumnsUpstairsStandOnItToo()
    {
        var (document, deck, first) = Building();
        var top = deck.GetTopElevation(document);

        var chair = Chair(document, first);
        var column = new Column { TypeId = document.TypesOf<ColumnType>().First().Id, LevelId = first, Location = new Point2D(7000, 5000) };
        Add(document, chair, column);

        Assert.Equal(deck.Id, chair.StandsOn);
        Assert.Equal(0, chair.Elevation, 6);
        Assert.Equal(top, Foot(document, chair), 6);
        Assert.Equal(top, column.GetBaseElevation(document), 6);

        // A copy of the chair put down beside it stands on the deck just the same.
        var copy = (Component)ElementCopy.Clone(chair)!;
        copy.Location = new Point2D(6000, 5000);
        Add(document, copy);
        Assert.Equal(top, Foot(document, copy), 6);
    }

    [Fact]
    public void TheyFollowTheDeckHoweverThickItIsMadeAndWhereverItGoes()
    {
        var (document, deck, first) = Building();
        var level = document.FindLevel(first)!.Elevation;
        var wall = Upstairs(document, first);
        var chair = Chair(document, first);
        var column = new Column { TypeId = document.TypesOf<ColumnType>().First().Id, LevelId = first, Location = new Point2D(7000, 5000) };
        Add(document, wall, chair, column);

        void OnTheDeck()
        {
            var top = deck.GetTopElevation(document);
            Assert.Equal(top, wall.GetBaseElevation(document), 6);
            Assert.Equal(top, Foot(document, chair), 6);
            Assert.Equal(top, column.GetBaseElevation(document), 6);
            Assert.Equal(top + 3000, wall.GetTopElevation(document), 6);
        }

        // A thicker deck - a type built up to 600 mm.
        var type = document.FindType<RoofType>(deck.TypeId)!;
        var thickest = type.Structure.Layers.MaxBy(layer => layer.Thickness)!;
        thickest.Thickness += 600 - type.Thickness;
        Assert.True(deck.GetTopElevation(document) > level + 500);
        OnTheDeck();

        // Raised on its level.
        deck.HeightOffset += 150;
        OnTheDeck();

        // Saved and opened again, still standing on it.
        var loaded = ProjectFile.FromJson(ProjectFile.ToJson(document));
        var loadedChair = loaded.Elements.OfType<Component>().Single();
        Assert.Equal(deck.Id, loadedChair.StandsOn);
        Assert.Equal(Foot(document, chair), Foot(loaded, loadedChair), 6);

        // Taken away, they stand at their level again.
        new DeleteElementsCommand(document, new[] { deck }).Redo();
        Assert.Null(chair.StandsOn);
        Assert.Equal(level, Foot(document, chair), 6);
        Assert.Equal(level, wall.GetBaseElevation(document), 6);
    }

    [Fact]
    public void AFlatRoofPutInUnderWhatIsUpstairsLiftsItOntoItsTopInOneStep()
    {
        var (document, deck, first) = Building(deck: false);
        var level = document.FindLevel(first)!.Elevation;
        var upstairs = Upstairs(document, first);
        var chair = Chair(document, first);
        document.Add(upstairs);
        document.Add(chair);
        var downstairs = document.Walls.Where(wall => wall.LevelId != first).ToList();

        var command = FloorSurfaces.Standing(document, new AddElementCommand(document, deck, "Create Roof"));
        command.Redo();
        var top = deck.GetTopElevation(document);
        Assert.Equal(top, upstairs.GetBaseElevation(document), 6);
        Assert.Equal(top + 3000, upstairs.GetTopElevation(document), 6);
        Assert.Equal(top, Foot(document, chair), 6);
        Assert.All(downstairs, wall => Assert.Equal(0, wall.GetBaseElevation(document), 6));

        command.Undo();
        Assert.Null(upstairs.BaseAttachedTo);
        Assert.Null(upstairs.TopLevelId);
        Assert.Equal(level, upstairs.GetBaseElevation(document), 6);
        Assert.Null(chair.StandsOn);
    }

    [Fact]
    public void AFloorWhoseTopIsAtItsLevelChangesNothing()
    {
        var (document, _, first) = Building(deck: false);
        var level = document.FindLevel(first)!.Elevation;
        var floor = new Floor { TypeId = document.TypesOf<FloorType>().First().Id, LevelId = first };
        floor.SetBoundary(new[] { new Point2D(0, 0), new Point2D(12000, 0), new Point2D(12000, 10000), new Point2D(0, 10000) });
        Add(document, floor);

        var wall = Upstairs(document, first);
        Add(document, wall);
        Assert.Null(wall.BaseAttachedTo);
        Assert.Equal(level, wall.GetBaseElevation(document), 6);
    }

    [Fact]
    public void AttachBasePutsFurnitureRaisedByHandDownOnTheDeck()
    {
        var (document, deck, first) = Building();
        var chair = Chair(document, first);
        chair.Elevation = 400;
        document.Add(chair);

        FloorSurfaces.PutOnFloors(document, new[] { chair })!.Redo();
        Assert.Equal(deck.Id, chair.StandsOn);
        Assert.Equal(deck.GetTopElevation(document), Foot(document, chair), 6);
    }

    [Fact]
    public void AttachBaseStandsAWallAlreadyBuriedInTheDeckOnItsTop()
    {
        var (document, deck, first) = Building();
        var wall = Upstairs(document, first);
        document.Add(wall);

        Assert.Same(deck, WallAttachments.FloorBelow(document, wall));
    }
}

/// <summary>
/// The floor between two storeys, laid on the walls of the one below, carries the level of the
/// one above: the level is its top, so each storey keeps its own height and the slab its own
/// thickness, whatever it is made.
/// </summary>
public class StoreyOnFloorTests
{
    private static (BimDocument Document, Roof Deck, BIMDesigner.Core.Datums.Level First, Wall Upstairs, Roof Over) TwoStoreys()
    {
        var document = BimDocument.CreateDefault();
        var ground = document.Levels[0];
        var first = document.Levels.OrderBy(level => level.Elevation).First(level => level.Elevation > 2000);
        var wallType = document.TypesOf<WallType>().First(t => t.Name.StartsWith("Exterior"));
        var corners = new[] { new Point2D(0, 0), new Point2D(12000, 0), new Point2D(12000, 10000), new Point2D(0, 10000) };
        foreach (var level in new[] { ground, first })
            for (var i = 0; i < 4; i++)
                document.Add(new Wall { Start = corners[i], End = corners[(i + 1) % 4], TypeId = wallType.Id, LevelId = level.Id, UnconnectedHeight = 3000 });

        // The roof over the first floor, set 3 m above it.
        var flat = document.TypesOf<RoofType>().First(t => t.Name.Contains("Flat"));
        var over = new Roof { TypeId = flat.Id, LevelId = first.Id, HeightOffset = 3000 };
        over.SetBoundary(corners);
        over.SetEdges(corners.Select(_ => new RoofEdge()).ToList());
        document.Add(over);

        // The deck laid on the ground floor's walls: the first floor's floor.
        var deck = new Roof { TypeId = flat.Id, LevelId = ground.Id, HeightOffset = first.Elevation };
        deck.SetBoundary(corners);
        deck.SetEdges(corners.Select(_ => new RoofEdge()).ToList());
        var upstairs = document.Walls.First(wall => wall.LevelId == first.Id);
        return (document, deck, first, upstairs, over);
    }

    [Fact]
    public void ADeckLaidOnTheStoreyBelowCarriesTheLevelAboveSoEachStoreyKeepsItsHeight()
    {
        var (document, deck, first, upstairs, over) = TwoStoreys();
        var command = FloorSurfaces.Standing(document, new AddElementCommand(document, deck, "Create Roof"));
        command.Redo();

        var top = deck.GetTopElevation(document);
        Assert.Equal(deck.Id, first.StandsOn);
        Assert.Equal(top, first.Elevation, 6);

        // Each storey 3 m of wall, the deck its own thickness between; the roof over it rises too.
        var downstairs = document.Walls.First(wall => wall.LevelId != first.Id);
        Assert.Equal(3000, downstairs.GetHeight(document), 6);
        Assert.Equal(3000, upstairs.GetHeight(document), 6);
        Assert.Equal(top, upstairs.GetBaseElevation(document), 6);
        Assert.Equal(top + 3000, over.GetBottomElevation(document), 6);

        // However thick it is made, the storey above starts on its top.
        var type = document.FindType<RoofType>(deck.TypeId)!;
        type.Structure.Layers.MaxBy(layer => layer.Thickness)!.Thickness += 300;
        Assert.Equal(deck.GetTopElevation(document), first.Elevation, 6);
        Assert.Equal(first.Elevation + 3000, over.GetBottomElevation(document), 6);

        // Undone, the level is where it was.
        command.Undo();
        Assert.Null(first.StandsOn);
        Assert.Equal(3000, first.Elevation, 6);
    }

    [Fact]
    public void AttachBaseSetsTheStoreyOnADeckAlreadyThereAndItIsSaved()
    {
        var (document, deck, first, upstairs, _) = TwoStoreys();
        document.Add(deck);
        Assert.Null(first.StandsOn);

        var (level, floor) = FloorSurfaces.StoreyFloor(document, upstairs)!.Value;
        Assert.Same(first, level);
        Assert.Same(deck, floor);
        FloorSurfaces.SetOn(document, new[] { level }, floor).Redo();
        Assert.Equal(deck.GetTopElevation(document), first.Elevation, 6);

        var loaded = ProjectFile.FromJson(ProjectFile.ToJson(document));
        var again = loaded.Levels.Single(candidate => candidate.Id == first.Id);
        Assert.Equal(deck.Id, again.StandsOn);
        Assert.Equal(first.Elevation, again.Elevation, 6);
    }

    [Fact]
    public void DeletingTheDeckLeavesTheStoreyWhereItIs()
    {
        var (document, deck, first, _, _) = TwoStoreys();
        FloorSurfaces.Standing(document, new AddElementCommand(document, deck, "Create Roof")).Redo();
        var at = first.Elevation;

        var delete = new DeleteElementsCommand(document, new[] { deck });
        delete.Redo();
        Assert.Null(first.StandsOn);
        Assert.Equal(at, first.Elevation, 6);

        delete.Undo();
        Assert.Equal(deck.Id, first.StandsOn);
    }

    [Fact]
    public void AGlazedGableStandingOnTheDeckStillRisesToTheRoof()
    {
        var (document, deck, first, _, _) = TwoStoreys();
        document.Add(deck);
        var curtainType = document.TypesOf<CurtainWallType>().First();
        var gable = new Wall { Start = new Point2D(0, 10000), End = new Point2D(0, 0), TypeId = curtainType.Id, LevelId = first.Id, UnconnectedHeight = 3000 };
        document.Add(gable);

        // A pitched roof over the first floor, its ridge across the gable.
        var pitched = new Roof { TypeId = document.TypesOf<RoofType>().First(t => !t.Name.Contains("Flat")).Id, LevelId = first.Id, HeightOffset = 3000 };
        pitched.SetBoundary(new[] { new Point2D(-500, -500), new Point2D(12500, -500), new Point2D(12500, 10500), new Point2D(-500, 10500) });
        pitched.SetEdges(new[]
        {
            new RoofEdge { DefinesSlope = true, SlopeDegrees = 35 }, new RoofEdge(),
            new RoofEdge { DefinesSlope = true, SlopeDegrees = 35 }, new RoofEdge()
        });
        document.Add(pitched);

        gable.BaseAttachedTo = deck.Id;
        gable.TopAttachedTo = pitched.Id;
        var top = WallProfile.CurtainTop(document, gable);
        Assert.NotNull(top);
        Assert.True(top!.Max(point => point.Y) - top.Min(point => point.Y) > 2000, "The glass stops flat instead of rising to the ridge.");
    }

    [Fact]
    public void AWallHangingFromTheWallBelowIsLetGoToStandOnTheFloor()
    {
        var (document, deck, first, upstairs, _) = TwoStoreys();
        var below = document.Walls.First(wall => wall.LevelId != first.Id && wall.Start == upstairs.Start);
        upstairs.BaseAttachedTo = below.Id;

        var command = FloorSurfaces.Standing(document, new AddElementCommand(document, deck, "Create Roof"));
        command.Redo();
        Assert.Null(upstairs.BaseAttachedTo);
        Assert.Equal(deck.GetTopElevation(document), upstairs.GetBaseElevation(document), 6);

        command.Undo();
        Assert.Equal(below.Id, upstairs.BaseAttachedTo);

        // On a storey already standing on the deck, one hung from below is known for what it is.
        command.Redo();
        upstairs.BaseAttachedTo = below.Id;
        Assert.True(WallAttachments.BelowItsFloor(document, upstairs));
        new AttachWallsCommand(new[] { (upstairs, false, (Guid?)null) }, "Attach Wall Bases").Redo();
        Assert.Equal(first.Elevation, upstairs.GetBaseElevation(document), 6);
    }

    [Fact]
    public void ASmallRoofAtTheLevelsHeightDoesNotCarryTheStorey()
    {
        var (document, _, first, _, _) = TwoStoreys();
        var flat = document.TypesOf<RoofType>().First(t => t.Name.Contains("Flat"));
        var porch = new Roof { TypeId = flat.Id, LevelId = document.Levels[0].Id, HeightOffset = first.Elevation };
        porch.SetBoundary(new[] { new Point2D(4000, -1500), new Point2D(7000, -1500), new Point2D(7000, 0), new Point2D(4000, 0) });
        porch.SetEdges(porch.Boundary.Select(_ => new RoofEdge()).ToList());

        FloorSurfaces.Standing(document, new AddElementCommand(document, porch, "Create Roof")).Redo();
        Assert.Null(first.StandsOn);
        Assert.Equal(3000, first.Elevation, 6);
    }
}
