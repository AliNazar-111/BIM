using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;
using BIMDesigner.Core.Mep;

namespace BIMDesigner.Tests;

/// <summary>
/// The walls and floors between the dwelling units of US homes under the 2021 IRC (round 140): townhouses by R302.2 - a common wall 2 hours, 1
/// with sprinklers, or a double wall of two 1-hour walls; nothing in a common wall's cavity; from the foundation to the roof sheathing; a parapet
/// 30 in over the roof or a Class C covering on a fire-retardant deck 4 ft either side; each unit standing on its own - and two-family dwellings
/// by R302.3, walls and floors 1 hour, 1/2 with sprinklers, what holds up the floor as long; pipes through them firestopped (R302.4); ratings
/// written in hours.
/// </summary>
[Collection("Wpf")]
public class UnitSeparationTests
{
    private const string Siding = "Timber Frame - Siding 183mm", OneHour = "Unit Separation Wall - 2x4 Studs, 5/8 in Type X Each Side, 1 hr",
        TwoHour = "Townhouse Area Separation Wall - 2 hr Gypsum Liner Panels", Shingles = "Roof - Asphalt Shingles on Plywood, 2x8 Rafters",
        FrtShingles = "Roof - Asphalt Shingles on FRT Plywood, 2x8 Rafters", Partition = "Interior - Partition 125mm";

    private static Guid TypeOf<T>(BimDocument document, string name) where T : Core.Elements.ElementType => document.TypesOf<T>().First(each => each.Name == name).Id;

    private static Wall Wall(BimDocument document, Core.Datums.Level level, double x0, double y0, double x1, double y1, string type, double height = 3000)
    {
        var wall = new Wall { Start = new Point2D(x0, y0), End = new Point2D(x1, y1), TypeId = TypeOf<WallType>(document, type), LevelId = level.Id, UnconnectedHeight = height };
        document.Add(wall);
        return wall;
    }

    /// <summary>
    /// A US row of units 6 m wide and 10 m deep along x, on two storeys, outside walls of siding; between each unit and the next a wall of a type
    /// at x 6 m, 12 m and so on on each storey - or a pair 140 apart for a double wall; a room in each unit on each storey named into its
    /// dwelling; each unit's own first floor; a flat roof over the lot 3 m over the first floor.
    /// </summary>
    private static (BimDocument Document, List<Core.Datums.Level> Levels, List<Wall> Between, Roof Roof) Row(string use, int units, string between, bool doubled = false, string roofType = Shingles)
    {
        var document = BimDocument.CreateDefault();
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.Irc;
        document.ProjectInformation.BuildingType = use;
        var levels = document.Levels.OrderBy(level => level.Elevation).Take(2).ToList();
        var length = 6000.0 * units;
        var walls = new List<Wall>();
        foreach (var level in levels)
        {
            Wall(document, level, 0, 0, length, 0, Siding);
            Wall(document, level, length, 0, length, 10000, Siding);
            Wall(document, level, length, 10000, 0, 10000, Siding);
            Wall(document, level, 0, 10000, 0, 0, Siding);
            for (var i = 1; i < units; i++)
                if (doubled)
                {
                    walls.Add(Wall(document, level, 6000 * i - 70, 0, 6000 * i - 70, 10000, between));
                    walls.Add(Wall(document, level, 6000 * i + 70, 0, 6000 * i + 70, 10000, between));
                }
                else
                    walls.Add(Wall(document, level, 6000 * i, 0, 6000 * i, 10000, between));
            for (var i = 0; i < units; i++)
                document.Add(new Room { LevelId = level.Id, Location = new Point2D(6000 * i + 3000, 5000), Name = level == levels[0] ? "Living" : "Bedroom", Dwelling = $"Unit {i + 1}" });
        }

        for (var i = 0; i < units; i++)
        {
            var floor = new Floor { LevelId = levels[1].Id, TypeId = TypeOf<FloorType>(document, "Floor - Timber 220mm") };
            floor.SetBoundary(new[] { new Point2D(6000 * i + 150, 0), new Point2D(6000 * i + 5850, 0), new Point2D(6000 * i + 5850, 10000), new Point2D(6000 * i + 150, 10000) });
            document.Add(floor);
        }

        var roof = new Roof { LevelId = levels[1].Id, TypeId = TypeOf<RoofType>(document, roofType), HeightOffset = 3000 };
        roof.SetBoundary(new[] { new Point2D(-300, -300), new Point2D(length + 300, -300), new Point2D(length + 300, 10300), new Point2D(-300, 10300) });
        document.Add(roof);
        return (document, levels, walls, roof);
    }

    private static IReadOnlyList<PlacementProblem> Problems(BimDocument document, Wall wall) => WallRules.Problems(document, wall, document.ProjectInformation.ClearanceStandard);

    private static IReadOnlyList<PlacementProblem> Problems(BimDocument document, Slab slab) => SlabRules.Problems(document, slab, document.ProjectInformation.ClearanceStandard);

    [Fact]
    public void RatingsWrittenInHoursAndTheBuildingsKindAreRead()
    {
        Assert.Equal(60, Doors.FireMinutes("1 hr"));
        Assert.Equal(120, Doors.FireMinutes("2-hour"));
        Assert.Equal(30, Doors.FireMinutes("1/2 hour"));
        Assert.Equal(90, Doors.FireMinutes("1.5 h"));
        Assert.Equal(60, Doors.FireMinutes("60 min"));
        Assert.Equal(30, Doors.FireMinutes("30 min (FD30S)"));
        Assert.Equal(30, Doors.FireMinutes("E 30"));
        Assert.Equal(0, Doors.FireMinutes(string.Empty));

        Assert.True(UsRoofClass.TryParse("Class A", out var a) && a == UsRoofClass.A);
        Assert.True(UsRoofClass.TryParse("c (ASTM E108)", out var c) && c == UsRoofClass.C);
        Assert.False(UsRoofClass.TryParse("BROOF(t4)", out _));
        Assert.Null(UsRoofClass.Typical("Asphalt Shingles", true));
        Assert.Equal(UsRoofClass.A, UsRoofClass.Typical("Roof Slate", false)!.Value.Class);
        Assert.Null(UsRoofClass.Typical("Standing Seam Metal", false));

        var document = BimDocument.CreateDefault();
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.Irc;
        foreach (var (use, kind) in new[] { ("Townhouse", UnitKind.Townhouse), ("Row houses", UnitKind.Townhouse), ("Duplex", UnitKind.TwoFamily), ("Two-family dwelling", UnitKind.TwoFamily), ("Detached house", UnitKind.None) })
        {
            document.ProjectInformation.BuildingType = use;
            Assert.Equal(BuildingUse.House, FireDesign.UseOf(document));
            Assert.Equal(kind, UnitSeparation.Kind(document));
        }

        // A family centre is no home; in England none of this is asked.
        document.ProjectInformation.BuildingType = "Family centre";
        Assert.Equal(BuildingUse.Other, FireDesign.UseOf(document));
        document.ProjectInformation.BuildingType = "Townhouse";
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.ApprovedDocumentM;
        Assert.Equal(UnitKind.None, UnitSeparation.Kind(document));
    }

    [Fact]
    public void ATownhousesCommonWallIsTwoHoursOrOneWithSprinklers()
    {
        // Three units, the walls between them 1 hour: a common wall asks 2 hours (R302.2.2 item 2).
        var (document, _, between, _) = Row("Townhouse", 3, OneHour);
        Assert.Equal(UnitKind.Townhouse, UnitSeparation.Kind(document));
        var said = Assert.Single(Problems(document, between[0]), problem => problem.Rule.Contains("(R302.2.2)"));
        Assert.Equal("it separates two townhouse units as their common wall, rated 1 hour.", said.Said);

        // Sprinklered to P2904: 1 hour does (item 1).
        document.ProjectInformation.Sprinklered = true;
        Assert.DoesNotContain(Problems(document, between[0]), problem => problem.Rule.Contains("(R302.2.2)") && problem.Said.Contains("rated"));

        // Without sprinklers, the 2-hour area separation wall.
        document.ProjectInformation.Sprinklered = false;
        foreach (var wall in between) wall.TypeId = TypeOf<WallType>(document, TwoHour);
        Assert.DoesNotContain(Problems(document, between[0]), problem => problem.Said.Contains("rated"));

        // Rooms named into three dwellings tell a townhouse with no Building Type saying so; an ordinary partition between them is a wall between units.
        document.ProjectInformation.BuildingType = "House";
        Assert.Equal(UnitKind.Townhouse, UnitSeparation.Kind(document));
        between[0].TypeId = TypeOf<WallType>(document, Partition);
        Assert.True(UnitSeparation.Separates(document, between[0]));
        Assert.Contains(Problems(document, between[0]), problem => problem.Said.StartsWith("it separates two townhouse units as their common wall, ") && problem.Rule.Contains("(R302.2.2)"));
    }

    [Fact]
    public void ADoubleWallIsTwoOneHourWallsAndItsUnitsStandOnTheirOwn()
    {
        // Each unit its own 1-hour wall, 140 apart: R302.2.1, nothing more asked of the rating.
        var (document, levels, between, _) = Row("Townhouse", 3, OneHour, doubled: true);
        Assert.Equal(UnitKind.Townhouse, UnitSeparation.Kind(document));
        Assert.Same(between[1], UnitSeparation.Partner(document, between[0]));
        Assert.Equal(60, UnitSeparation.Asked(document, between[0]).Minutes);
        Assert.DoesNotContain(Problems(document, between[0]), problem => problem.Said.Contains("rated"));
        Assert.DoesNotContain(between.SelectMany(wall => Problems(document, wall)), problem => problem.Rule.Contains("(R302.2.6)"));

        // One floor run on across a double wall into the next unit: each unit not standing on its own (R302.2.6) - unless sprinklered (exception 6).
        var across = new Floor { LevelId = levels[1].Id, TypeId = TypeOf<FloorType>(document, "Floor - Timber 220mm") };
        across.SetBoundary(new[] { new Point2D(3000, 1000), new Point2D(9000, 1000), new Point2D(9000, 9000), new Point2D(3000, 9000) });
        document.Add(across);
        var independence = Assert.Single(between.Take(2).SelectMany(wall => Problems(document, wall)), problem => problem.Rule.Contains("(R302.2.6)"));
        Assert.StartsWith("it is one of a double wall between townhouse units, and the Floor - Timber 220mm runs on across both into the next unit", independence.Said);
        document.ProjectInformation.Sprinklered = true;
        Assert.DoesNotContain(between.SelectMany(wall => Problems(document, wall)), problem => problem.Rule.Contains("(R302.2.6)"));
    }

    [Fact]
    public void AWallBetweenUnitsRunsFromTheFoundationToTheRoofSheathing()
    {
        var (document, levels, between, roof) = Row("Townhouse", 3, TwoHour, roofType: FrtShingles);
        Assert.DoesNotContain(between.SelectMany(wall => Problems(document, wall)), problem => problem.Rule.Contains("(R302.2.3)"));

        // The first storey's wall at 6 m taken away: the one over it stops short of the foundation.
        var upper = between.Single(wall => wall.LevelId == levels[1].Id && Math.Abs(wall.Start.X - 6000) < 1);
        document.Remove(between.Single(wall => wall.LevelId == levels[0].Id && Math.Abs(wall.Start.X - 6000) < 1));
        Assert.Contains(Problems(document, upper), problem => problem.Said.StartsWith("it stops ") && problem.Said.Contains("above the foundation") && problem.Rule.Contains("(R302.2.3)"));

        // The second storey's wall at 12 m 300 short of the roof over it: said; attached to the roof, it reaches it.
        var short_ = between.Single(wall => wall.LevelId == levels[1].Id && Math.Abs(wall.Start.X - 12000) < 1);
        short_.UnconnectedHeight = 2700;
        Assert.Contains(Problems(document, short_), problem => problem.Said.StartsWith("it separates townhouse units and stops ") && problem.Rule.Contains("(R302.2.3)"));
        short_.TopAttachedTo = roof.Id;
        Assert.DoesNotContain(Problems(document, short_), problem => problem.Rule.Contains("(R302.2.3)"));
    }

    [Fact]
    public void ACommonWallMeetsTheRoofWithAParapetOrAFireRetardantDeck()
    {
        // Shingles, Class A as listed, on plain plywood over the wall, no parapet: R302.2.4.
        var (document, levels, between, roof) = Row("Townhouse", 3, TwoHour);
        var wall = between.Single(each => each.LevelId == levels[1].Id && Math.Abs(each.Start.X - 6000) < 1);
        var said = Assert.Single(Problems(document, wall), problem => problem.Rule.Contains("(R302.2.4)"));
        Assert.Equal("it separates townhouse units under the roof with no parapet 30 in (762) above it, and its deck, the Plywood Sheathing, is neither noncombustible nor fire-retardant-treated, with no 5/8 in Type X gypsum board under it.", said.Said);

        // On fire-retardant-treated plywood: the exception met.
        roof.TypeId = TypeOf<RoofType>(document, FrtShingles);
        Assert.DoesNotContain(Problems(document, wall), problem => problem.Rule.Contains("(R302.2.4)"));

        // ... but not with a chimney 600 from the wall, within 4 ft.
        var chimney = new Chimney { LevelId = levels[0].Id, Location = new Point2D(6900, 5000), TypeId = document.TypesOf<ChimneyType>().First().Id };
        document.Add(chimney);
        Assert.Contains(Problems(document, wall), problem => problem.Rule.Contains("(R302.2.4)") && problem.Said.EndsWith("a chimney within 4 ft (1219) of the wall."));
        document.Remove(chimney);

        // A covering of no class given, the deck fire-retardant: said.
        var shingles = document.Materials.First(material => material.Name == "Asphalt Shingles");
        shingles.RoofCovering = string.Empty;
        Assert.Contains(Problems(document, wall), problem => problem.Said.Contains("its covering, the Asphalt Shingles, has no class given (ASTM E108 or UL 790)"));
        shingles.RoofCovering = "Class A";

        // Carried up as a parapet 30 in over the roof's top (205 thick, its top at 6205) on plain plywood: high enough, but faced in gypsum (R302.2.5).
        roof.TypeId = TypeOf<RoofType>(document, Shingles);
        wall.UnconnectedHeight = 4000;
        Assert.DoesNotContain(Problems(document, wall), problem => problem.Rule.Contains("(R302.2.4)"));
        var parapet = Assert.Single(Problems(document, wall), problem => problem.Rule.Contains("(R302.2.5)"));
        Assert.Equal("it is carried up as a parapet between townhouse units, and its face of Gypsum Plasterboard is not noncombustible.", parapet.Said);

        // 30 in it must be: 3 in short of it, no parapet - and the plywood said again.
        wall.UnconnectedHeight = 3000 + 205 + 762 - 76;
        Assert.Contains(Problems(document, wall), problem => problem.Rule.Contains("(R302.2.4)"));
    }

    [Fact]
    public void NothingRunsInACommonWallsCavityAndPipesThroughItAreFirestopped()
    {
        var (document, levels, between, _) = Row("Townhouse", 3, TwoHour, roofType: FrtShingles);
        var wall = between.Single(each => each.LevelId == levels[0].Id && Math.Abs(each.Start.X - 6000) < 1);

        // A cold water pipe up inside the wall: R302.2.2; a sprinkler's water-filled pipe is allowed.
        var riser = new Pipe { Start = new Point2D(6000, 4000), End = new Point2D(6000, 4000), StartOffset = 100, EndOffset = 2800, LevelId = levels[0].Id, Kind = SystemKind.ColdWater, Diameter = 15 };
        document.Add(riser);
        var said = Assert.Single(Problems(document, wall), problem => problem.Rule.Contains("plumbing or mechanical equipment"));
        Assert.Equal("a cold water pipe runs in its cavity, the common wall between townhouse units.", said.Said);
        riser.Kind = SystemKind.Sprinkler;
        Assert.DoesNotContain(Problems(document, wall), problem => problem.Rule.Contains("plumbing or mechanical equipment"));
        document.Remove(riser);

        // A pipe straight through the wall, unsealed: R302.4.1; firestopped, nothing said.
        var through = new Pipe { Start = new Point2D(5000, 3000), End = new Point2D(7000, 3000), StartOffset = 1000, EndOffset = 1000, LevelId = levels[0].Id, Kind = SystemKind.ColdWater, Diameter = 15 };
        document.Add(through);
        Assert.DoesNotContain(Problems(document, wall), problem => problem.Rule.Contains("plumbing or mechanical equipment"));
        Assert.Contains(Penetrations.Problems(document), problem => ReferenceEquals(problem.Element, through) && problem.Text.StartsWith("it passes through the wall between dwelling units unsealed - the IRC (R302.4.1)"));
        through.Seal = PipeSeal.FireStopped;
        Assert.DoesNotContain(Penetrations.Problems(document), problem => ReferenceEquals(problem.Element, through) && problem.Text.Contains("R302.4"));
    }

    [Fact]
    public void ADuplexIsSeparatedByOneHourWallsAndFloorsAndWhatHoldsThemUp()
    {
        // Side by side: a partition between the two units, rooms named into them - R302.3 asks 1 hour, 1/2 with sprinklers.
        var (document, levels, between, _) = Row("Duplex", 2, Partition, roofType: FrtShingles);
        Assert.Equal(UnitKind.TwoFamily, UnitSeparation.Kind(document));
        var said = Assert.Single(Problems(document, between[0]), problem => problem.Rule.Contains("(R302.3)") && problem.Said.Contains("rated"));
        Assert.StartsWith("it separates the two dwelling units of a two-family dwelling, ", said.Said);
        foreach (var wall in between) wall.TypeId = TypeOf<WallType>(document, OneHour);
        Assert.DoesNotContain(Problems(document, between[0]), problem => problem.Said.Contains("rated"));

        // One over the other: the lower storey one unit, the upper another; the timber floor between them rated 30 minutes asks 1 hour.
        var stacked = BimDocument.CreateDefault();
        stacked.ProjectInformation.ClearanceStandard = ClearanceStandard.Irc;
        stacked.ProjectInformation.BuildingType = "Two-family dwelling";
        var storeys = stacked.Levels.OrderBy(level => level.Elevation).Take(2).ToList();
        Wall? outside = null;
        foreach (var level in storeys)
        {
            outside ??= Wall(stacked, level, 0, 0, 8000, 0, Siding);
            if (level != storeys[0]) Wall(stacked, level, 0, 0, 8000, 0, Siding);
            Wall(stacked, level, 8000, 0, 8000, 10000, Siding);
            Wall(stacked, level, 8000, 10000, 0, 10000, Siding);
            Wall(stacked, level, 0, 10000, 0, 0, Siding);
            stacked.Add(new Room { LevelId = level.Id, Location = new Point2D(4000, 5000), Name = "Living", Dwelling = level == storeys[0] ? "Lower" : "Upper" });
        }

        var floor = new Floor { LevelId = storeys[1].Id, TypeId = TypeOf<FloorType>(stacked, "Floor - Timber 220mm") };
        floor.SetBoundary(new[] { new Point2D(0, 0), new Point2D(8000, 0), new Point2D(8000, 10000), new Point2D(0, 10000) });
        stacked.Add(floor);
        Assert.True(UnitSeparation.Separates(stacked, floor));
        var floorSaid = Assert.Single(Problems(stacked, floor), problem => problem.Rule.Contains("(R302.3)"));
        Assert.Equal("it separates the two dwelling units, rated 1/2 hour.", floorSaid.Said);

        // The outside walls holding it up, 30 minutes: R302.3.1 asks them as long as the floor.
        Assert.Contains(Problems(stacked, outside!), problem => problem.Rule.Contains("(R302.3.1)") && problem.Said == "it holds up the floor between the two dwelling units, rated 1/2 hour.");

        // The 1-hour floor, and sprinklers: 1/2 hour asked of the walls under it, which they have.
        floor.TypeId = TypeOf<FloorType>(stacked, "Floor - 2x10 Joists, 5/8 in Type X Ceiling, 1 hr");
        Assert.DoesNotContain(Problems(stacked, floor), problem => problem.Rule.Contains("(R302.3)"));
        stacked.ProjectInformation.Sprinklered = true;
        Assert.DoesNotContain(Problems(stacked, outside!), problem => problem.Rule.Contains("(R302.3.1)"));
    }

    [Fact]
    public void ADuplexWallMayStopAtATypeXCeilingUnderAnAtticWithADraftStop()
    {
        // The roof 1.5 m over the wall's top: short of the roof sheathing (R302.3).
        var (document, levels, between, roof) = Row("Duplex", 2, OneHour, roofType: FrtShingles);
        roof.HeightOffset = 4500;
        var wall = between.Single(each => each.LevelId == levels[1].Id);
        Assert.Contains(Problems(document, wall), problem => problem.Said.StartsWith("it separates the dwelling units and stops ") && problem.Rule.Contains("(R302.3)"));

        // A ceiling of 5/8 in Type X either side at the wall's top, and a draft stop over the wall into the attic: exception 2.
        var typeX = document.Materials.First(material => material.Name == "Gypsum Board, Type X 5/8 in");
        var ceilingType = new CeilingType("Ceiling - 5/8 in Type X", new CompoundStructure(new MaterialLayer(LayerFunction.Finish2, typeX.Id, 15.9)));
        document.AddType(ceilingType);
        var ceiling = new Ceiling { LevelId = levels[1].Id, TypeId = ceilingType.Id, HeightOffset = 3000 };
        ceiling.SetBoundary(new[] { new Point2D(0, 0), new Point2D(12000, 0), new Point2D(12000, 10000), new Point2D(0, 10000) });
        document.Add(ceiling);
        Assert.Contains(Problems(document, wall), problem => problem.Rule.Contains("(R302.3)") && problem.Said.Contains("below the roof sheathing"));
        document.Add(new CavityBarrier { LevelId = levels[1].Id, Start = new Point2D(6000, 0), End = new Point2D(6000, 10000) });
        Assert.DoesNotContain(Problems(document, wall), problem => problem.Rule.Contains("(R302.3)") && problem.Said.Contains("below the roof sheathing"));
    }
}
