using System.IO;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Mep;
using BIMDesigner.Core.Elements;
using BIMDesigner.Infrastructure.Interoperability;
using BIMDesigner.Infrastructure.Serialization;
using Xbim.Ifc4.Interfaces;

namespace BIMDesigner.Tests;

/// <summary>
/// Cavities and cavity barriers (round 127), by Approved Document B Volume 1 Sections 2, 5 and 8 and Volume 2 Section 9: a cavity wall closed at
/// its edges, the cavity over a protected escape route's enclosure closed where it stops short of the floor over it, and the voids over the
/// ceilings of a building other than a home no larger than Table 9.1 allows.
/// </summary>
public class CavityTests
{
    private const string Brick = "Exterior - Brick on Block 330mm";
    private const string Partition = "Interior - Partition 125mm";

    private static List<Wall> Box(BimDocument document, Guid level, double x0, double y0, double x1, double y1, double height = 3000)
    {
        var corners = new[] { new Point2D(x0, y0), new Point2D(x1, y0), new Point2D(x1, y1), new Point2D(x0, y1) };
        var type = document.TypesOf<WallType>().First(each => each.Name == Brick).Id;
        return Enumerable.Range(0, 4).Select(i =>
        {
            var wall = new Wall { Start = corners[i], End = corners[(i + 1) % 4], TypeId = type, LevelId = level, UnconnectedHeight = height };
            document.Add(wall);
            return wall;
        }).ToList();
    }

    private static Wall Partitioned(BimDocument document, Guid level, double x, double y0, double y1, double height = 2400)
    {
        var wall = new Wall { Start = new Point2D(x, y0), End = new Point2D(x, y1), TypeId = document.TypesOf<WallType>().First(each => each.Name == Partition).Id, LevelId = level, UnconnectedHeight = height };
        document.Add(wall);
        return wall;
    }

    private static T Slab<T>(BimDocument document, Guid level, string type, double x1, double y1, double offset = 0) where T : Slab, new()
    {
        var slab = new T { LevelId = level, TypeId = document.TypesOf<SlabType>().First(each => each.Name == type).Id, HeightOffset = offset };
        slab.SetBoundary(new[] { new Point2D(0, 0), new Point2D(x1, 0), new Point2D(x1, y1), new Point2D(0, y1) });
        document.Add(slab);
        return slab;
    }

    [Fact]
    public void ACavityWallIsClosedAtItsEdgesAndAFramedOneByCavityBarriers()
    {
        var document = BimDocument.CreateDefault();
        document.ProjectInformation.BuildingType = "House";
        var level = document.Levels[0].Id;
        var walls = Box(document, level, 0, 0, 8000, 6000);
        document.Add(new Room { LevelId = level, Location = new Point2D(4000, 3000), Name = "Living Room" });

        // Brick, a 25 cavity, PIR, aircrete block: two leaves of brick and block 75 mm or more, closed by closers at its head and openings (Diagram 5.3).
        var brick = document.GetWallType(walls[0])!;
        Assert.Equal((25.0, true), Cavities.CavityOf(document, brick));
        Assert.Equal(CavityClosing.Closers, brick.Cavity);
        Assert.Empty(Cavities.Problems(document, walls[0], brick));

        // Left open: Diagram 5.3 still asks it closed at its top and round its openings, in any material - said among the wall's own problems.
        brick.Cavity = CavityClosing.Open;
        var (said, rule) = Assert.Single(Cavities.Problems(document, walls[0], brick));
        Assert.Equal("its 25 cavity is left open at its top and round its openings.", said);
        Assert.StartsWith("Approved Document B (Diagram 5.3) asks a cavity wall of two leaves of brick or concrete, each 75 mm thick or more, its cavity closed at the top", rule);
        Assert.Contains(WallRules.Problems(document, walls[0], ClearanceStandard.ApprovedDocumentM), problem => problem.Said == said);

        // Timber frame clad in brick: its inner leaf studs and board, not brick or concrete - closers are not enough, cavity barriers are (5.18, 5.20).
        var frame = document.TypesOf<WallType>().First(each => each.Name == "Timber Frame - Brick Clad 315mm");
        Assert.Equal((50.0, false), Cavities.CavityOf(document, frame));
        Assert.Equal(CavityClosing.Barriers, frame.Cavity);
        foreach (var wall in walls) wall.TypeId = frame.Id;
        Assert.Empty(Cavities.Problems(document, walls[0], frame));
        frame.Cavity = CavityClosing.Closers;
        (said, rule) = Assert.Single(Cavities.Problems(document, walls[0], frame));
        Assert.Equal("its 50 cavity is closed by closers, and its leaves are not both brick or concrete 75 mm thick.", said);
        Assert.StartsWith("Approved Document B (5.18, 5.20) asks this wall cavity barriers, E 30 and I 15", rule);

        // An office's: Volume 2's paragraphs. A project from before it was asked takes what its build-up would be built with.
        document.ProjectInformation.BuildingType = "Office";
        Assert.StartsWith("Approved Document B Volume 2 (9.3, 9.13)", Assert.Single(Cavities.Problems(document, walls[0], frame)).Rule);
        Assert.Equal(CavityClosing.Barriers, Cavities.Assumed(document, frame));
        Assert.Equal(CavityClosing.Closers, Cavities.Assumed(document, brick));

        // A stud partition's void is no cavity wall's cavity here.
        Assert.Null(Cavities.CavityOf(document, document.TypesOf<WallType>().First(each => each.Name == Partition)));
    }

    [Fact]
    public void AProtectedRouteEnclosureStoppingShortHasACavityBarrierOverItOrAFireResistingCeiling()
    {
        // An office 12 m by 8 m, a floor over it at 3 m - its underside 2750 - and a stair enclosed by a partition stopping at 2400.
        var office = BimDocument.CreateDefault();
        office.ProjectInformation.BuildingType = "Office";
        var (ground, first) = (office.Levels[0].Id, office.Levels[1].Id);
        Box(office, ground, 0, 0, 12000, 8000);
        var partition = Partitioned(office, ground, 4000, 0, 8000);
        office.Add(new Room { LevelId = ground, Location = new Point2D(2000, 4000), Name = "Stair", Number = "1" });
        office.Add(new Room { LevelId = ground, Location = new Point2D(8000, 4000), Name = "Office", Number = "2" });
        Slab<Floor>(office, first, "Floor - Screed on Slab 250mm", 12000, 8000);
        var type = office.GetWallType(partition)!;
        var (said, rule) = Assert.Single(Cavities.Problems(office, partition, type));
        Assert.Equal("it encloses the protected stairway and stops 350 below the floor over it, with no cavity barrier over it.", said);
        Assert.StartsWith("Approved Document B Volume 2 (9.5, Diagram 9.3) asks the cavity over a protected escape route's fire-resisting enclosure", rule);

        // A cavity barrier drawn along it, from face to face of the walls it runs between: closed. It stands on the partition, up to the floor.
        var barrier = new CavityBarrier { LevelId = ground, Start = new Point2D(4000, 165), End = new Point2D(4000, 7835) };
        office.Add(barrier);
        Assert.Empty(Cavities.Problems(office, partition, type));
        Assert.Equal((2400.0, 2750.0), Cavities.Span(office, barrier));
        Assert.Equal("Closes 350 from 2400 up to 2750", Cavities.Verdict(office, barrier));

        // Not tested to E 30 and I 15: said (9.13).
        barrier.Tested = false;
        Assert.StartsWith("it is mineral wool, polythene-sleeved, not tested to E 30 and I 15 - Approved Document B Volume 2 (9.13)", Assert.Single(Cavities.Problems(office, barrier)));

        // In place of it, a fire-resisting ceiling under the cavity throughout the storey (Diagram 9.3): plasterboard, 30 minutes, over both rooms.
        office.Remove(barrier);
        Slab<Ceiling>(office, ground, "Ceiling - Plasterboard 25mm", 12000, 8000, 2400);
        Assert.Empty(Cavities.Problems(office, partition, type));

        // A hotel's partition between two bedrooms stopping at an acoustic tile ceiling: closed over by a cavity barrier on its line (9.7).
        var hotel = BimDocument.CreateDefault();
        hotel.ProjectInformation.BuildingType = "Hotel";
        (ground, first) = (hotel.Levels[0].Id, hotel.Levels[1].Id);
        Box(hotel, ground, 0, 0, 8000, 4000);
        var between = Partitioned(hotel, ground, 4000, 0, 4000);
        hotel.Add(new Room { LevelId = ground, Location = new Point2D(2000, 2000), Name = "Bedroom", Number = "1" });
        hotel.Add(new Room { LevelId = ground, Location = new Point2D(6000, 2000), Name = "Bedroom", Number = "2" });
        Slab<Floor>(hotel, first, "Floor - Screed on Slab 250mm", 8000, 4000);
        Slab<Ceiling>(hotel, ground, "Ceiling - Acoustic Mineral Fibre Tile 15mm", 8000, 4000, 2400);
        (said, rule) = Assert.Single(Cavities.Problems(hotel, between, hotel.GetWallType(between)!));
        Assert.Equal("it encloses a partition between bedrooms and stops 350 below the floor over it, with no cavity barrier over it.", said);
        Assert.StartsWith("Approved Document B Volume 2 (9.7, 9.5, Diagram 9.3)", rule);
        hotel.Add(new CavityBarrier { LevelId = ground, Start = new Point2D(4000, 165), End = new Point2D(4000, 3835) });
        Assert.Empty(Cavities.Problems(hotel, between, hotel.GetWallType(between)!));
    }

    [Fact]
    public void AHouseWithAStoreyOver4Point5MetresClosesTheCavityOverItsProtectedStairway()
    {
        // Three storeys, the top 6 m up: its landing and a bedroom divided by a partition stopping at 2400, a floor over at 9 m.
        var house = BimDocument.CreateDefault();
        house.ProjectInformation.BuildingType = "House";
        var top = new Level { Name = "Second Floor", Elevation = 6000 };
        var over = new Level { Name = "Roof", Elevation = 9000 };
        house.AddLevel(top);
        house.AddLevel(over);
        Box(house, top.Id, 0, 0, 8000, 6000);
        var partition = Partitioned(house, top.Id, 3000, 0, 6000);
        house.Add(new Room { LevelId = top.Id, Location = new Point2D(1500, 3000), Name = "Landing" });
        house.Add(new Room { LevelId = top.Id, Location = new Point2D(5500, 3000), Name = "Bedroom" });
        Slab<Floor>(house, over.Id, "Floor - Screed on Slab 250mm", 8000, 6000);
        var (said, rule) = Assert.Single(Cavities.Problems(house, partition, house.GetWallType(partition)!));
        Assert.Equal("it encloses the protected stairway and stops 350 below the floor over it, with no cavity barrier over it.", said);
        Assert.StartsWith("Approved Document B (2.5, Diagram 2.3) asks", rule);

        // A fire-resisting ceiling, EI 30, over the whole storey: met.
        Slab<Ceiling>(house, top.Id, "Ceiling - Plasterboard 25mm", 8000, 6000, 2400);
        Assert.Empty(Cavities.Problems(house, partition, house.GetWallType(partition)!));
    }

    [Fact]
    public void TheVoidOverAnOfficesCeilingsIsDividedAsTable9Point1Asks()
    {
        // An office 50 m by 12 m, its outside walls up into the floor over it, a tiled ceiling at 2400 - the void 350 deep, 49.5 m by 11.5 m
        // inside the walls, its surfaces mineral fibre tile and concrete, A1. One room: barriers needed only on its walls, 40 m apart (9.11).
        var document = BimDocument.CreateDefault();
        document.ProjectInformation.BuildingType = "Office";
        var (ground, first) = (document.Levels[0].Id, document.Levels[1].Id);
        Box(document, ground, 0, 0, 50000, 12000);
        document.Add(new Room { LevelId = ground, Location = new Point2D(30000, 6000), Name = "Office", Number = "1" });
        Slab<Floor>(document, first, "Floor - Screed on Slab 250mm", 50000, 12000);
        var ceiling = Slab<Ceiling>(document, ground, "Ceiling - Acoustic Mineral Fibre Tile 15mm", 50000, 12000, 2400);
        var void0 = Assert.Single(Cavities.Voids(document));
        Assert.Equal((49500.0, 11500.0, 40000.0, false), (void0.Length, void0.Width, void0.Allowed, void0.Roof));
        var (said, rule) = Assert.Single(Cavities.Problems(document, ceiling));
        Assert.Equal("the cavity over it runs 49.5 m by 11.5 m between the walls up to the floor over it and the cavity barriers drawn.", said);
        Assert.Contains("over a single room, its cavity barriers on the line of the walls round it and its surfaces class C-s3, d2 or better, 40 m (9.11) at most in any direction; 9.12", rule);

        // A cavity barrier across it at 25 m: two cavities 24.5 m long, within it.
        var across = new CavityBarrier { LevelId = ground, Start = new Point2D(25000, 0), End = new Point2D(25000, 12000) };
        document.Add(across);
        Assert.Equal(new[] { 24500.0, 24500.0 }, Cavities.Voids(document).Select(each => each.Length));
        Assert.Empty(Cavities.Problems(document, ceiling));

        // A meeting room partitioned off at 20 m, its partition stopping at the ceiling: the void runs on over it, over two rooms - 20 m (Table 9.1).
        Partitioned(document, ground, 20000, 0, 12000);
        document.Add(new Room { LevelId = ground, Location = new Point2D(10000, 6000), Name = "Meeting Room", Number = "2" });
        (said, rule) = Assert.Single(Cavities.Problems(document, ceiling));
        Assert.Equal("the cavity over it runs 24.5 m by 11.5 m between the walls up to the floor over it and the cavity barriers drawn.", said);
        Assert.Contains("this one with its surfaces class C-s3, d2 or better, 20 m (Table 9.1) at most", rule);

        // Over a fire-resisting ceiling throughout the storey, 30 m (9.10c): plasterboard, 30 minutes.
        ceiling.TypeId = document.TypesOf<CeilingType>().First(each => each.Name == "Ceiling - Plasterboard 25mm").Id;
        Assert.Empty(Cavities.Problems(document, ceiling));
        Assert.Contains("30 m allowed", Cavities.Verdict(document, ceiling));

        // Back to the tiles, a barrier over the partition: 19.5 m and 24.5 m, within 20 m and the single room's 40 m.
        ceiling.TypeId = document.TypesOf<CeilingType>().First(each => each.Name == "Ceiling - Acoustic Mineral Fibre Tile 15mm").Id;
        document.Add(new CavityBarrier { LevelId = ground, Start = new Point2D(20000, 0), End = new Point2D(20000, 12000) });
        Assert.Empty(Cavities.Problems(document, ceiling));

        // The tiles declared D-s2, d0: worse than C-s3, d2 - 10 m, and no single room's 40 m - all three over it, the middle one 11.5 m across the building.
        document.Materials.First(each => each.Name == "Mineral Fibre Ceiling Tile").ReactionToFire = "D-s2, d0";
        var worse = Cavities.Problems(document, ceiling).ToList();
        Assert.Equal(new[] { 19500.0, 11500.0, 24500.0 }, Cavities.Voids(document).Select(each => each.Length));
        Assert.Equal(3, worse.Count);
        Assert.All(worse, problem => Assert.Contains("with a surface worse than class C-s3, d2 - the mineral fibre ceiling tile on top of the ceiling, D-s2, d0 - 10 m (Table 9.1)", problem.Rule));

        // A home's voids are Volume 1's, which sets no such limit.
        document.ProjectInformation.BuildingType = "House";
        Assert.Empty(Cavities.Voids(document));
    }

    [Fact]
    public void CavityBarriersAndCavityClosingAreSavedAndGoToIfc()
    {
        var document = BimDocument.CreateDefault();
        document.ProjectInformation.BuildingType = "Office";
        var (ground, first) = (document.Levels[0].Id, document.Levels[1].Id);
        var walls = Box(document, ground, 0, 0, 12000, 8000);
        document.Add(new Room { LevelId = ground, Location = new Point2D(6000, 4000), Name = "Office" });
        Slab<Floor>(document, first, "Floor - Screed on Slab 250mm", 12000, 8000);
        Slab<Ceiling>(document, ground, "Ceiling - Acoustic Mineral Fibre Tile 15mm", 12000, 8000, 2400);
        var barrier = new CavityBarrier { LevelId = ground, Start = new Point2D(6000, 165), End = new Point2D(6000, 7835), Construction = "Timber, 38 mm", Tested = false };
        document.Add(barrier);
        document.GetWallType(walls[0])!.Cavity = CavityClosing.Open;
        Assert.Equal((2400.0, 2750.0), Cavities.Span(document, barrier));

        var path = Path.Combine(Path.GetTempPath(), $"cavities-{Guid.NewGuid():N}.bimx");
        try
        {
            ProjectFile.Save(document, path);
            var opened = ProjectFile.Load(path);
            var saved = Assert.Single(opened.All<CavityBarrier>());
            Assert.Equal((barrier.Start, barrier.End, "Timber, 38 mm", false), (saved.Start, saved.End, saved.Construction, saved.Tested));
            Assert.Equal(CavityClosing.Open, opened.TypesOf<WallType>().First(each => each.Name == Brick).Cavity);
            Assert.Equal(CavityClosing.Barriers, opened.TypesOf<WallType>().First(each => each.Name == "Timber Frame - Brick Clad 315mm").Cavity);
        }
        finally
        {
            File.Delete(path);
        }

        using var model = IfcExport.Build(document);
        var proxy = Assert.Single(model.Instances.OfType<IIfcBuildingElementProxy>(), each => each.ObjectType == "Cavity barrier");
        Assert.Equal("Cavity Barrier - Timber, 38 mm", proxy.Name?.ToString());
    }

    private static Pipe Piped(BimDocument document, PipeMaterial material, double diameter, double y, double height, PipeSeal seal)
    {
        var pipe = new Pipe
        {
            TypeId = document.TypesOf<PipeType>().First(type => type.Material == material).Id, LevelId = document.Levels[0].Id, Start = new Point2D(3000, y), End = new Point2D(9000, y),
            StartOffset = height, EndOffset = height, Diameter = diameter, Kind = material == PipeMaterial.PvcU ? SystemKind.Foul : SystemKind.ColdWater, Seal = seal
        };
        document.Add(pipe);
        return pipe;
    }

    private static List<string> Through(BimDocument document, Element service) =>
        Cavities.ServiceProblems(document).Where(problem => problem.Element == service).Select(problem => problem.Text).ToList();

    [Fact]
    public void PipesAndDuctsThroughACavityBarrierAreSealedDamperedOrFireResisting()
    {
        // An office 12 m by 8 m, a tiled ceiling at 2400 under a floor at 2750, a cavity barrier across the void at 6 m.
        var document = BimDocument.CreateDefault();
        document.ProjectInformation.BuildingType = "Office";
        var (ground, first) = (document.Levels[0].Id, document.Levels[1].Id);
        Box(document, ground, 0, 0, 12000, 8000);
        document.Add(new Room { LevelId = ground, Location = new Point2D(6000, 4000), Name = "Office" });
        Slab<Floor>(document, first, "Floor - Screed on Slab 250mm", 12000, 8000);
        Slab<Ceiling>(document, ground, "Ceiling - Acoustic Mineral Fibre Tile 15mm", 12000, 8000, 2400);
        document.Add(new CavityBarrier { LevelId = ground, Start = new Point2D(6000, 165), End = new Point2D(6000, 7835) });

        // A 110 uPVC waste through it in the void, at 2600: nothing round it (9.17 b); fire-stopped alone, more than Table 10.1's 40; sealed, met.
        var waste = Piped(document, PipeMaterial.PvcU, 110, 3000, 2600, PipeSeal.None);
        Assert.Equal(new Point3D(6000, 3000, 2600), Assert.Single(Cavities.Through(document, waste)).At);
        Assert.StartsWith("it passes through a cavity barrier with nothing closing round it - Approved Document B Volume 2 (9.17 b, 10.2 to 10.5) asks", Assert.Single(Through(document, waste)));
        waste.Seal = PipeSeal.FireStopped;
        var said = Assert.Single(Through(document, waste));
        Assert.StartsWith("a pipe of ", said);
        Assert.Contains("fire-stopped through a cavity barrier - Approved Document B Volume 2 (Table 10.1, any other situation) fire-stops a pipe of", said);
        waste.Seal = PipeSeal.ProprietarySeal;
        Assert.Empty(Through(document, waste));

        // 22 copper fire-stopped: within 160. A pipe under the ceiling, at 2000, passes no barrier.
        Assert.Empty(Through(document, Piped(document, PipeMaterial.Copper, 22, 4000, 2600, PipeSeal.FireStopped)));
        var below = Piped(document, PipeMaterial.Copper, 22, 5000, 2000, PipeSeal.None);
        Assert.Empty(Cavities.Through(document, below));

        // A duct through it: unprotected, said (9.17 d, e) among the services' problems; fire-resisting duct, met.
        var duct = new Duct
        {
            TypeId = document.TypesOf<DuctType>().First(type => type.Material == DuctMaterial.GalvanisedSteel).Id, LevelId = ground,
            Start = new Point2D(3000, 6000), End = new Point2D(9000, 6000), StartOffset = 2600, EndOffset = 2600, Width = 125, Height = 125, Kind = SystemKind.SupplyAir
        };
        document.Add(duct);
        Assert.Contains(MepSystems.Problems(document), problem => problem.Element == duct
            && problem.Text.StartsWith("it passes through a cavity barrier unprotected - Approved Document B Volume 2 (9.17 d, e)", StringComparison.Ordinal));
        duct.FireProtection = FireProtection.FireResisting;
        Assert.Empty(Through(document, duct));

        // A hotel: the barrier over a partition between two bedrooms that is not fire-resisting - 9.17 not asked, only sealed against smoke (9.18).
        var hotel = BimDocument.CreateDefault();
        hotel.ProjectInformation.BuildingType = "Hotel";
        (ground, first) = (hotel.Levels[0].Id, hotel.Levels[1].Id);
        Box(hotel, ground, 0, 0, 12000, 8000);
        var partition = Partitioned(hotel, ground, 6000, 0, 8000);
        hotel.GetWallType(partition)!.FireRating = string.Empty;
        hotel.Add(new Room { LevelId = ground, Location = new Point2D(3000, 4000), Name = "Bedroom", Number = "1" });
        hotel.Add(new Room { LevelId = ground, Location = new Point2D(9000, 4000), Name = "Bedroom", Number = "2" });
        Slab<Floor>(hotel, first, "Floor - Screed on Slab 250mm", 12000, 8000);
        hotel.Add(new CavityBarrier { LevelId = ground, Start = new Point2D(6000, 165), End = new Point2D(6000, 7835) });
        var soil = Piped(hotel, PipeMaterial.PvcU, 110, 3000, 2600, PipeSeal.FireStopped);
        Assert.Empty(Through(hotel, soil));
        soil.Seal = PipeSeal.None;
        Assert.StartsWith("it passes unsealed through the cavity barrier over a partition between bedrooms - Approved Document B Volume 2 (9.18)", Assert.Single(Through(hotel, soil)));
    }

    [Fact]
    public void ACorridorsPartitionsMeetTheCeilingAndItsDivisionIsClosedOver()
    {
        // An office 30 m by 8 m: a corridor 2 m wide along its south side, its partition stopping at 2400 under a floor at 2750, divided at 15 m
        // by a wall across it that stops at 2400 too. No ceiling yet.
        var document = BimDocument.CreateDefault();
        document.ProjectInformation.BuildingType = "Office";
        var (ground, first) = (document.Levels[0].Id, document.Levels[1].Id);
        Box(document, ground, 0, 0, 30000, 8000);
        var partitionType = document.TypesOf<WallType>().First(each => each.Name == Partition).Id;
        Wall Along(double x0, double x1)
        {
            var wall = new Wall { Start = new Point2D(x0, 2000), End = new Point2D(x1, 2000), TypeId = partitionType, LevelId = ground, UnconnectedHeight = 2400 };
            document.Add(wall);
            return wall;
        }

        var (west, east) = (Along(0, 15000), Along(15000, 30000));
        var across = Partitioned(document, ground, 15000, 0, 2000);
        document.Add(new Room { LevelId = ground, Location = new Point2D(7500, 1000), Name = "Corridor", Number = "1" });
        document.Add(new Room { LevelId = ground, Location = new Point2D(22500, 1000), Name = "Corridor", Number = "2" });
        var office = new Room { LevelId = ground, Location = new Point2D(15000, 5000), Name = "Office", Number = "3" };
        document.Add(office);
        Slab<Floor>(document, first, "Floor - Screed on Slab 250mm", 30000, 8000);
        List<(string Said, string Rule)> Said(Wall wall) => Cavities.Problems(document, wall, document.GetWallType(wall)!).ToList();

        // The corridor's partition meets neither the floor nor a ceiling (2.25 a); the division is open over its top (2.27).
        var (said, rule) = Assert.Single(Said(west));
        Assert.Equal("it encloses the corridor, an escape route, and stops 350 below the floor over it with no suspended ceiling to meet.", said);
        Assert.StartsWith("Approved Document B Volume 2 (2.25 a)", rule);
        (said, rule) = Assert.Single(Said(across));
        Assert.Equal("it divides the corridor and stops 350 below the floor over it, with no cavity barrier over it.", said);
        Assert.StartsWith("Approved Document B Volume 2 (2.27, Diagram 2.9)", rule);

        // A tiled ceiling at 2400: the partitions meet it, but the void over them lets smoke round the division (2.27 Method 1).
        Slab<Ceiling>(document, ground, "Ceiling - Acoustic Mineral Fibre Tile 15mm", 30000, 8000, 2400);
        Assert.Equal("it encloses the corridor, divided across, and stops 350 below the floor over it, with no cavity barrier over it.", Assert.Single(Said(west)).Said);

        // A barrier over the division closes it; barriers along the corridor's partitions as well - Method 1.
        document.Add(new CavityBarrier { LevelId = ground, Start = new Point2D(15000, 165), End = new Point2D(15000, 2000) });
        Assert.Empty(Said(across));
        Assert.Single(Said(east));
        var alongBarriers = new[] { new CavityBarrier { LevelId = ground, Start = new Point2D(165, 2000), End = new Point2D(15000, 2000) }, new CavityBarrier { LevelId = ground, Start = new Point2D(15000, 2000), End = new Point2D(29835, 2000) } };
        foreach (var barrier in alongBarriers) document.Add(barrier);
        Assert.Empty(Said(west));
        Assert.Empty(Said(east));

        // Method 2 instead: the storey divided on the division's line - a 30 minute partition through the office, a barrier over it.
        foreach (var barrier in alongBarriers) document.Remove(barrier);
        Assert.Single(Said(west));
        Partitioned(document, ground, 15000, 2000, 8000);
        office.Location = new Point2D(7500, 5000);
        document.Add(new CavityBarrier { LevelId = ground, Start = new Point2D(15000, 2000), End = new Point2D(15000, 7835) });
        document.Add(new Room { LevelId = ground, Location = new Point2D(25000, 5000), Name = "Office", Number = "4" });
        Assert.Empty(Said(west));
        Assert.Empty(Said(east));
    }
}
