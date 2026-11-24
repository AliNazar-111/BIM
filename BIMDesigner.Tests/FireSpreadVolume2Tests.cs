using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;
using BIMDesigner.Core.Mep;

namespace BIMDesigner.Tests;

/// <summary>
/// Fire spread over the outside walls of a building other than a home and through its fire-separating elements (round 125), by Approved Document
/// B Volume 2 Sections 10 and 12: Table 12.1 by purpose group, 12.6 and 12.7, Regulation 7(1A) and 7(2); pipes by 10.2 to 10.5 and Table 10.1,
/// ducts by 10.12 to 10.22.
/// </summary>
public class FireSpreadVolume2Tests
{
    private const string Partition = "Interior - Partition 125mm";

    private static Wall Wall(BimDocument document, Guid level, Point2D from, Point2D to, string type, double height = 3000)
    {
        var wall = new Wall { Start = from, End = to, TypeId = document.TypesOf<WallType>().First(each => each.Name == type).Id, LevelId = level, UnconnectedHeight = height };
        document.Add(wall);
        return wall;
    }

    /// <summary>A copy of the brick wall type faced outside in a material of a name and class, so thick.</summary>
    private static WallType Faced(BimDocument document, string material, string fire, double thickness = 20)
    {
        var face = new Material(material) { Density = 1500, ReactionToFire = fire };
        document.AddMaterial(face);
        var copy = (WallType)document.TypesOf<WallType>().First(each => each.Name == "Exterior - Brick on Block 330mm").Duplicate($"Exterior - {material}");
        var layers = copy.Structure.Layers.ToList();
        layers.Insert(0, new MaterialLayer(LayerFunction.Finish1, face.Id, thickness));
        copy.Structure.ReplaceLayers(layers);
        document.AddType(copy);
        return copy;
    }

    /// <summary>A building of a use, 10 m by 6 m of outside walls of a type so high, a room in it, its boundary 5 m off all round.</summary>
    private static (BimDocument Document, List<Wall> Walls) Building(string use, WallType? type = null, double height = 3000)
    {
        var document = BimDocument.CreateDefault();
        document.ProjectInformation.BuildingType = use;
        var level = document.Levels[0].Id;
        var name = (type ?? document.TypesOf<WallType>().First(each => each.Name == "Exterior - Brick on Block 330mm")).Name;
        var corners = new[] { new Point2D(0, 0), new Point2D(10000, 0), new Point2D(10000, 6000), new Point2D(0, 6000) };
        var walls = Enumerable.Range(0, 4).Select(i => Wall(document, level, corners[i], corners[(i + 1) % 4], name, height)).ToList();
        document.Add(new Room { LevelId = level, Location = new Point2D(5000, 3000), Name = "Room" });
        var line = new PropertyLine { LevelId = level };
        line.SetBoundary(new[] { new Point2D(-5165, -5165), new Point2D(15165, -5165), new Point2D(15165, 11165), new Point2D(-5165, 11165) });
        document.Add(line);
        return (document, walls);
    }

    private static List<(string Said, string Rule)> Outside(BimDocument document, Wall wall) => Combustibility.Problems(document, wall, document.ProjectInformation.ClearanceStandard).ToList();

    [Fact]
    public void AHotelsOutsideWallsAreAResidentialBuildingsByTable121()
    {
        // A hotel 12 m high faced in render of class D-s3, d2 5 m from the boundary: a residential purpose group more than 11 m - A2-s1, d0 (12.5).
        var (hotel, walls) = Building("Hotel", null, 12000);
        var faced = Faced(hotel, "Render, Acrylic", "D-s3, d2", 10);
        walls[0].TypeId = faced.Id;
        var problem = Assert.Single(Outside(hotel, walls[0]));
        Assert.Equal("Approved Document B Volume 2 (12.5, Table 12.1) asks the outside surface of a residential building (purpose group 2) more than 11 m high to be class A2-s1, d0 or better - or steel sheet 0.5 mm thick with an organic coating 0.2 mm at most.", problem.Rule);

        // An office of the same: 18 m or less, 1 m or more from the boundary - no provision.
        hotel.ProjectInformation.BuildingType = "Office";
        Assert.Empty(Outside(hotel, walls[0]));
    }

    [Fact]
    public void APlaceOfAssemblyIsFacedCs3d2To10MetresUp()
    {
        // A library, 3 m high, its wall faced in a board of class D-s3, d2 5 m from the boundary: up to 10 m above the ground, C-s3, d2 (Table 12.1).
        var (library, walls) = Building("Library");
        walls[0].TypeId = Faced(library, "Board, Fibre Composite", "D-s3, d2").Id;
        Assert.Contains(Outside(library, walls[0]), problem => problem.Rule.StartsWith("Approved Document B Volume 2 (12.5, Table 12.1) asks the outside surface of a place of assembly and recreation 18 m high or less, up to 10 m above the ground to be class C-s3, d2 or better - or timber cladding 9 mm thick", StringComparison.Ordinal));

        // Cedar 19 mm: timber cladding 9 mm thick at least (note 3) - met.
        walls[0].TypeId = library.TypesOf<WallType>().First(each => each.Name == "Timber Frame - Siding 183mm").Id;
        Assert.Empty(Outside(library, walls[0]));

        // An office: no provision 1 m or more from the boundary.
        library.ProjectInformation.BuildingType = "Office";
        walls[0].TypeId = Faced(library, "Board, Fibre Composite 2", "D-s3, d2").Id;
        Assert.Empty(Outside(library, walls[0]));
    }

    [Fact]
    public void AHotelWithAStorey18MetresUpIsARelevantBuilding()
    {
        // A room on a level 18 m up: a relevant building (Regulation 7(2), Volume 2 12.15) - every material A2-s1, d0 or A1, the brick's
        // insulation of no class given among them.
        var (hotel, walls) = Building("Hotel");
        var high = new Level { Name = "Level 6", Elevation = 18000 };
        hotel.AddLevel(high);
        hotel.Add(new Room { LevelId = high.Id, Location = new Point2D(5000, 3000), Name = "Bedroom" });
        var problem = Assert.Single(Outside(hotel, walls[0]));
        Assert.Contains("a hotel, hostel, care home, hospital, student accommodation - with a storey 18 m or more above the ground, a relevant building (Approved Document B Volume 2 12.15)", problem.Rule);

        // An office of the same height: not one - its insulation A2-s3, d2 by 12.6 instead.
        hotel.ProjectInformation.BuildingType = "Office";
        Assert.DoesNotContain(Outside(hotel, walls[0]), each => each.Rule.Contains("Regulation 7(2)"));
    }

    [Fact]
    public void AMetalCompositeOfACombustibleCoreIsInNoBuilding()
    {
        var (office, walls) = Building("Office");
        var acm = Faced(office, "Aluminium Composite Panel, PE Core", string.Empty, 4);
        walls[0].TypeId = acm.Id;
        Assert.Contains(Outside(office, walls[0]), problem => problem.Said == "its Aluminium Composite Panel, PE Core, of no class given, a metal composite material."
                                                              && problem.Rule.StartsWith("Regulation 7(1A) prohibits relevant metal composite materials", StringComparison.Ordinal));
        office.Materials.First(each => each.Name == "Aluminium Composite Panel, PE Core").ReactionToFire = "A2-s1, d0";
        Assert.DoesNotContain(Outside(office, walls[0]), problem => problem.Rule.StartsWith("Regulation 7(1A)", StringComparison.Ordinal));
    }

    /// <summary>An office 10 m by 6 m, a stair at its west end behind a 30 minute partition at x 3 m, a riser at its east end behind another at x 9 m, the office between.</summary>
    private static (BimDocument Document, Wall StairWall, Wall RiserWall) Office(string use = "Office")
    {
        var (document, _) = Building(use);
        document.All<Room>().First().Name = "Office";
        var level = document.Levels[0].Id;
        var stairWall = Wall(document, level, new Point2D(3000, 0), new Point2D(3000, 6000), Partition);
        var riserWall = Wall(document, level, new Point2D(9000, 0), new Point2D(9000, 6000), Partition);
        document.Add(new Room { LevelId = level, Location = new Point2D(1500, 3000), Name = "Stair" });
        document.Add(new Room { LevelId = level, Location = new Point2D(9500, 3000), Name = "Riser" });
        document.All<Room>().First(room => room.Name == "Office").Location = new Point2D(6000, 3000);
        return (document, stairWall, riserWall);
    }

    private static Pipe Pipe(BimDocument document, PipeMaterial material, double diameter, Point2D from, Point2D to, PipeSeal seal, SystemKind kind = SystemKind.ColdWater)
    {
        var pipe = new Pipe
        {
            TypeId = document.TypesOf<PipeType>().First(type => type.Material == material).Id, LevelId = document.Levels[0].Id, Start = from, End = to,
            StartOffset = 2500, EndOffset = 2500, Diameter = diameter, Kind = kind, Seal = seal
        };
        document.Add(pipe);
        return pipe;
    }

    private static List<string> Said(BimDocument document, Pipe pipe) => Penetrations.Problems(document).Where(problem => problem.Element == pipe).Select(problem => problem.Text).ToList();

    [Fact]
    public void APipeThroughAProtectedStairwaysEnclosureIsSealedByTable101()
    {
        var (document, stairWall, riserWall) = Office();
        Assert.Equal(("enclosure of a protected stairway", false), FireSeparating.Of(document, stairWall));
        Assert.Equal(("enclosure of a protected shaft", true), FireSeparating.Of(document, riserWall));

        // A 110 PVC-U waste through the stair's partition, unsealed: wanting (10.1 to 10.5).
        var waste = Pipe(document, PipeMaterial.PvcU, 110, new Point2D(2000, 3000), new Point2D(5000, 3000), PipeSeal.None, SystemKind.Foul);
        Assert.Contains(Said(document, waste), text => text.StartsWith("it passes through the enclosure of a protected stairway with nothing keeping it fire-separating - Approved Document B Volume 2 (10.1 to 10.5)", StringComparison.Ordinal));

        // Fire-stopped alone: PVC-U is 40 mm there - 110 only through a protected shaft's enclosure (Table 10.1).
        waste.Seal = PipeSeal.FireStopped;
        Assert.Contains(Said(document, waste), text => text.Contains("Approved Document B Volume 2 (10.4, Table 10.1) fire-stops a pipe of PVC-U up to 40 mm alone, 110 only through a protected shaft's enclosure"));
        Assert.Equal(40, Penetrations.Volume2Limit(document, waste, false));
        Assert.Equal(110, Penetrations.Volume2Limit(document, waste, true));

        // Through the riser's enclosure, fire-stopped: within 110. A proprietary seal through the stair's: any size (10.3).
        var stack = Pipe(document, PipeMaterial.PvcU, 110, new Point2D(7000, 2000), new Point2D(9700, 2000), PipeSeal.FireStopped, SystemKind.Foul);
        Assert.DoesNotContain(Said(document, stack), text => text.Contains("Table 10.1"));
        waste.Seal = PipeSeal.ProprietarySeal;
        Assert.Empty(Said(document, waste).Where(text => text.Contains("Volume 2")));

        // A copper pipe of 22 fire-stopped: within 160.
        var copper = Pipe(document, PipeMaterial.Copper, 22, new Point2D(2000, 4000), new Point2D(5000, 4000), PipeSeal.FireStopped);
        Assert.DoesNotContain(Said(document, copper), text => text.Contains("Volume 2"));
    }

    private static Component Put(BimDocument document, string name, double x, double y, double elevation)
    {
        var type = document.TypesOf<ComponentType>().First(each => each.Name == name);
        var piece = new Component { TypeId = type.Id, TypeKind = type.Kind, LevelId = document.Levels[0].Id, Location = new Point2D(x, y), Elevation = elevation };
        document.Add(piece);
        return piece;
    }

    /// <summary>A system of a kind - a heat recovery unit in the office and a valve in the stair - its duct across the stair's partition at 2.6 m.</summary>
    private static Duct Ducted(BimDocument document, SystemKind kind)
    {
        var unit = Put(document, "Heat Recovery Unit - 75 l/s", 6000, 3000, 2400);
        var valve = Put(document, kind == SystemKind.ExtractAir ? "Extract Valve - 125" : "Supply Valve - 125", 1500, 3000, 2900);
        var system = MepSystems.Make(document, kind, new[] { unit, valve }).System!;
        document.Add(system);
        var duct = new Duct
        {
            TypeId = document.TypesOf<DuctType>().First(type => type.Material == DuctMaterial.GalvanisedSteel).Id, LevelId = document.Levels[0].Id,
            Start = new Point2D(1500, 3000), End = new Point2D(6000, 3000), StartOffset = 2600, EndOffset = 2600, Width = 125, Height = 125, Kind = kind, SystemId = system.Id
        };
        document.Add(duct);
        return duct;
    }

    private static List<string> Said(BimDocument document, Duct duct) => MepSystems.Problems(document).Where(problem => problem.Element == duct).Select(problem => problem.Text).ToList();

    [Fact]
    public void ADuctThroughAProtectedStairwaysEnclosureIsDamperedByVolume2()
    {
        // A supply duct through the stair's partition, unprotected: wanting (10.12).
        var (document, _, _) = Office();
        var supply = Ducted(document, SystemKind.SupplyAir);
        Assert.Contains(Said(document, supply), text => text.StartsWith("it passes through the enclosure of a protected stairway unprotected - Approved Document B Volume 2 (10.12)", StringComparison.Ordinal));

        // A fire damper of 60 minutes: met for a supply in an office.
        supply.FireProtection = FireProtection.FireDamper;
        supply.DamperRating = 60;
        Assert.DoesNotContain(Said(document, supply), text => text.Contains("Volume 2 (10."));

        // An extract through it on a fire damper alone: not method 1 through a protected escape route's enclosure (10.15).
        var (other, _, _) = Office();
        var extract = Ducted(other, SystemKind.ExtractAir);
        extract.FireProtection = FireProtection.FireDamper;
        extract.DamperRating = 60;
        Assert.Contains(Said(other, extract), text => text.Contains("Approved Document B Volume 2 (10.15)"));

        // In a hotel, where people sleep, a fire damper wants releasing by smoke too (10.22).
        var (hotel, _, _) = Office("Hotel");
        var hotelSupply = Ducted(hotel, SystemKind.SupplyAir);
        hotelSupply.FireProtection = FireProtection.FireDamper;
        hotelSupply.DamperRating = 60;
        Assert.Contains(Said(hotel, hotelSupply), text => text.Contains("Approved Document B Volume 2 (10.22)"));
    }
}
