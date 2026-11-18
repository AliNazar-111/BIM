using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Mep;

namespace BIMDesigner.Tests;

/// <summary>
/// Where a room-sealed boiler's flue comes out (round 92), by Approved Document J's tables to Diagram 34 (gas, fanned) and Diagram 41 (oil,
/// pressure jet); and LPG kept outside - a tank by Table 11 and its fire wall, cylinders by 5.20.
/// </summary>
[Collection("Wpf")]
public class FlueOutletLpgTests
{
    /// <summary>A house 8 m by 6 m of brick on block, drawn so its outsides face out, a kitchen in it; its south wall from x 8 m to 0.</summary>
    private static (BimDocument Document, Wall South) House()
    {
        var document = BimDocument.CreateDefault();
        var level = document.Levels[0].Id;
        var brick = document.TypesOf<WallType>().First(type => type.Name == "Exterior - Brick on Block 330mm");
        var corners = new[] { new Point2D(0, 0), new Point2D(0, 6000), new Point2D(8000, 6000), new Point2D(8000, 0) };
        var walls = Enumerable.Range(0, 4).Select(i => new Wall { Start = corners[i], End = corners[(i + 1) % 4], TypeId = brick.Id, LevelId = level, UnconnectedHeight = 3000 }).ToList();
        foreach (var wall in walls) document.Add(wall);
        document.Add(new Room { LevelId = level, Location = new Point2D(4000, 3000), Name = "Kitchen" });
        return (document, walls[3]);
    }

    /// <summary>A boiler hung inside on a wall at x so far along the south wall.</summary>
    private static Component Boiler(BimDocument document, Wall wall, string type, double x)
    {
        var kind = document.TypesOf<ComponentType>().First(each => each.Name == type);
        var boiler = new Component { TypeId = kind.Id, TypeKind = kind.Kind, LevelId = wall.LevelId, Location = new Point2D(x, 500), Elevation = kind.DefaultElevation };
        Assert.True(boiler.HostOn(document, wall, new Point2D(x, 500)));
        document.Add(boiler);
        return boiler;
    }

    /// <summary>A window in the south wall centred at x, its sill so high.</summary>
    private static Window Window(BimDocument document, Wall south, double x, double sill)
    {
        var window = new Window { HostWallId = south.Id, TypeId = document.TypesOf<WindowType>().First(type => type.Name == "Casement - 1200 x 1200").Id, LevelId = south.LevelId, DistanceAlongWall = 8000 - x, SillHeight = sill };
        document.Add(window);
        return window;
    }

    private static Component Place(BimDocument document, string type, double x, double y)
    {
        var kind = document.TypesOf<ComponentType>().First(each => each.Name == type);
        var piece = new Component { TypeId = kind.Id, TypeKind = kind.Kind, LevelId = document.Levels[0].Id, Location = new Point2D(x, y), Elevation = 0 };
        document.Add(piece);
        return piece;
    }

    private static List<string> Of(IEnumerable<MepProblem> problems, Core.Elements.Element element) => problems.Where(problem => problem.Element == element).Select(problem => problem.Text).ToList();

    [Fact]
    public void AGasBoilersFlueComesOutBehindItAndIsKeptFromOpenings()
    {
        // The gas combi hung at 1100, 700 high: its outlet on the south face at x 2 m, 1.8 m up - nothing near it.
        var (document, south) = House();
        var boiler = Boiler(document, south, "Boiler - Gas Combi", 2000);
        var outlet = FlueOutlets.Of(document, boiler)!;
        Assert.Equal(2000, outlet.At.X, 1);
        Assert.Equal(-165, outlet.At.Y, 1);
        Assert.Equal(1800, outlet.Height, 1);
        Assert.Empty(FlueOutlets.Problems(document));

        // A window beside it, its jamb 200 mm off: C asks 300.
        var window = Window(document, south, 1200, 900);
        Assert.Equal("its flue's outlet is 200 mm beside the Casement - 1200 x 1200 window - Approved Document J (the table to Diagram 34) keeps a fanned room-sealed gas flue's outlet 300 mm below an opening, 300 mm above one and 300 mm beside one",
            Assert.Single(Of(FlueOutlets.Problems(document), boiler)));

        // Slid 1 m along its wall, as a drag in plan slides it: on the same face, clear of the window - and back with Undo.
        var (from, turned) = (boiler.Location, boiler.Rotation);
        Assert.True(WallHung.Slide(document, boiler, from, new Vector2D(1000, -400)));
        Assert.Equal(3000, boiler.Location.X, 1);
        Assert.Equal(from.Y, boiler.Location.Y, 1);
        Assert.Empty(FlueOutlets.Problems(document));
        var slide = new SlideOnWallCommand(new[] { (boiler, from, turned, boiler.Location, boiler.Rotation) });
        slide.Undo();
        Assert.Equal(2000, boiler.Location.X, 1);
        Assert.Single(Of(FlueOutlets.Problems(document), boiler));
        slide.Redo();
        Assert.Equal(3000, boiler.Location.X, 1);
        slide.Undo();

        // 400 mm off: met. Over it, its sill 100 mm above the outlet: A asks 300 under an opening.
        window.DistanceAlongWall = 8000 - 1000;
        Assert.Empty(FlueOutlets.Problems(document));
        window.DistanceAlongWall = 8000 - 2000;
        window.SillHeight = 1900;
        Assert.StartsWith("its flue's outlet is 100 mm below the Casement - 1200 x 1200 window", Assert.Single(Of(FlueOutlets.Problems(document), boiler)));
        document.Remove(window);

        // The boundary 400 mm in front of it: J asks 600.
        var line = new PropertyLine { LevelId = south.LevelId };
        line.SetBoundary(new[] { new Point2D(-5000, -565), new Point2D(13000, -565), new Point2D(13000, 20000), new Point2D(-5000, 20000) });
        document.Add(line);
        Assert.StartsWith("the boundary is 400 mm in front of its flue's outlet", Assert.Single(Of(FlueOutlets.Problems(document), boiler)));
        document.Remove(line);

        // Another boiler hung 200 mm along: N asks 300 between outlets side by side on a wall.
        Boiler(document, south, "Boiler - Gas System", 2200);
        Assert.Contains(Of(FlueOutlets.Problems(document), boiler), said => said.StartsWith("its flue's outlet is 200 mm along and 0 mm up or down from another on the same wall", StringComparison.Ordinal));
    }

    [Fact]
    public void AnOilBoilersFlueIsKept600FromAnOpening()
    {
        // The room-sealed oil combi standing on the floor, 850 high: its outlet 850 up; a window's jamb 400 mm beside it - 600 asked (B).
        var (document, south) = House();
        var boiler = Boiler(document, south, "Boiler - Oil Combi, Room-Sealed", 2000);
        Window(document, south, 1000, 600);
        Assert.Equal("its flue's outlet is 400 mm beside the Casement - 1200 x 1200 window - Approved Document J (the table to Diagram 41) keeps an oil boiler's flue's outlet 600 mm below an opening and 600 mm beside one",
            Assert.Single(Of(FlueOutlets.Problems(document), boiler)));

        // An open-flued boiler has no outlet on the wall: nothing asked of it here.
        Assert.Null(FlueOutlets.Of(document, Boiler(document, south, "Boiler - Oil System, Open-Flued", 6000)));
    }

    [Fact]
    public void AnLpgTankIsKeptFromTheBuildingByTable11OrBehindAFireWall()
    {
        // A 0.25 t tank 1.6 by 0.8 m, 2.0 m from the south face: 2.5 m asked.
        var (document, south) = House();
        var tank = Place(document, "LPG Tank - 0.25 t", 6000, -2565);
        Assert.Equal("it stands 2.0 m from the building - Approved Document J (Table 11) asks 2.5 m for a tank of 0.25 tonnes or less, or 0.3 m behind a fire wall as high as its relief valve",
            Assert.Single(Of(LpgStorage.Problems(document), tank)));

        // A brick fire wall between, 4 m long and 1.2 m high, the tank 1.05 m: 2.0 m behind it, and well over 2.5 m around its ends.
        var brick = document.TypesOf<WallType>().First(type => type.Name == "Exterior - Brick on Block 330mm");
        var fire = new Wall { Start = new Point2D(4000, -1200), End = new Point2D(8000, -1200), TypeId = brick.Id, LevelId = south.LevelId, UnconnectedHeight = 1200 };
        document.Add(fire);
        Assert.Empty(Of(LpgStorage.Problems(document), tank));

        // Too short a wall - 1.8 m - the way round its ends under 2.5 m.
        fire.Start = new Point2D(5100, -1200);
        fire.End = new Point2D(6900, -1200);
        Assert.Contains("around the ends of its fire wall - Approved Document J (5.18, Table 11)", Assert.Single(Of(LpgStorage.Problems(document), tank)));
        document.Remove(fire);

        // A gas boiler's flue outlet on the south face 1.0 m from the tank: kept from it 2.5 m too.
        Boiler(document, south, "Boiler - Gas Combi", 6000);
        Assert.Contains(Of(LpgStorage.Problems(document), tank), said => said.StartsWith("it stands 2.0 m from a flue's outlet", StringComparison.Ordinal));

        // A gully 1 m from it: protected from the gas getting in (5.15).
        Place(document, document.TypesOf<ComponentType>().First(type => type.Form == ComponentForm.Gully).Name, 6000, -3965);
        Assert.Contains(Of(LpgStorage.Problems(document), tank), said => said.Contains("(5.15) asks drains, gullies and cellar hatches within 2.5 m"));

        // Past 1.1 tonnes: the guidance ends.
        var big = (ComponentType)document.FindType<ComponentType>(tank.TypeId)!.Duplicate("LPG Tank - 2 t");
        big.Capacity = 2000;
        document.AddType(big);
        tank.TypeId = big.Id;
        Assert.StartsWith("it holds 2.00 tonnes - Approved Document J's guidance (5.13, 5.14) goes to 1.1 tonnes", Assert.Single(Of(LpgStorage.Problems(document), tank)));
    }

    [Fact]
    public void LpgCylindersKeepTheirValvesFromAWindowOnAFirmBase()
    {
        // Two cylinders against the south wall under a window: their valves 1.3 m up, in the window's height and 35 mm off its face; and no base.
        var (document, south) = House();
        Window(document, south, 5000, 900);
        var cylinders = Place(document, "LPG Cylinders - 2 x 47 kg", 5000, -400);
        var said = Of(LpgStorage.Problems(document), cylinders);
        Assert.Contains(said, each => each.StartsWith("their valves are 0.0 m from the Casement - 1200 x 1200 window and 0 mm above or below it - Approved Document J (5.20 a)", StringComparison.Ordinal));
        Assert.Contains(said, each => each.StartsWith("no firm level base under them - Approved Document J (5.20)", StringComparison.Ordinal));

        // Moved 4 m along, on paving: met.
        cylinders.Location = new Point2D(1000, -400);
        var paving = new GroundRegion { LevelId = south.LevelId, Surface = GroundSurface.Paving };
        paving.SetBoundary(new[] { new Point2D(300, -900), new Point2D(1700, -900), new Point2D(1700, -165), new Point2D(300, -165) });
        document.Add(paving);
        Assert.Empty(Of(LpgStorage.Problems(document), cylinders));
    }
}
