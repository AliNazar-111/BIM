using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;

namespace BIMDesigner.Tests;

/// <summary>Fire's leftovers: steel joints by BS EN 1993-1-2 4.2.1(6), steel columns' lengths in fire, and the US by IBC Table 601, NDS Chapter 16 and ACI 216.1.</summary>
public class FireLeftoversTests
{
    [Fact]
    public void Table601RatesEachTypeOfConstruction()
    {
        Assert.Equal((3.0, 3.0, 3.0, 2.0), FireDesign.Ibc601(ConstructionType.IA));
        Assert.Equal((2.0, 2.0, 2.0, 2.0), FireDesign.Ibc601(ConstructionType.IB));
        Assert.Equal((1.0, 1.0, 1.0, 1.0), FireDesign.Ibc601(ConstructionType.IIA));
        Assert.Equal((0.0, 2.0, 0.0, 0.0), FireDesign.Ibc601(ConstructionType.IIIB));
        Assert.Equal((0.0, 0.0, 0.0, 0.0), FireDesign.Ibc601(ConstructionType.VB));

        // The char NDS Chapter 16 takes, 1.2 x 1.5 in/h t^0.813: 1.8 in at an hour.
        Assert.Equal(1.8 * 25.4, FireDesign.NdsChar(60), 6);
        Assert.Equal(1.2 * 1.5 * Math.Pow(2, 0.813) * 25.4, FireDesign.NdsChar(120), 6);

        // ACI 216.1 for siliceous aggregate: 3.5 in of wall or floor for an hour, 5.0 for two; a column 8 in and 10.
        Assert.Equal((3.5, 5.0), (FireDesign.AciThickness(1), FireDesign.AciThickness(2)));
        Assert.Equal((8.0, 10.0), (FireDesign.AciColumn(1), FireDesign.AciColumn(2)));
    }

    /// <summary>A two storey house of the walls named, its first floor of timber joists or concrete, used as said, in the US where asked.</summary>
    private static BimDocument Building(string wall, string use, bool concrete, ConstructionType type)
    {
        var document = BimDocument.CreateDefault();
        document.ProjectInformation.BuildingType = use;
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.Irc;
        document.ProjectInformation.ConstructionType = type;
        document.ProjectInformation.Ground = GroundType.StiffClay;
        var (ground, first) = (document.Levels[0].Id, document.Levels[1].Id);
        var roof = new Level { Name = "Roof", Elevation = 6000 };
        document.AddLevel(roof);
        var wallType = document.TypesOf<WallType>().First(t => t.Name.StartsWith(wall, StringComparison.Ordinal));
        var corners = new[] { new Point2D(0, 0), new Point2D(10000, 0), new Point2D(10000, 8000), new Point2D(0, 8000) };
        foreach (var (from, to) in new[] { (ground, first), (first, roof.Id) })
            for (var i = 0; i < 4; i++)
                document.Add(new Wall { Start = corners[i], End = corners[(i + 1) % 4], TypeId = wallType.Id, LevelId = from, TopLevelId = to, StructuralUsage = StructuralUsage.Bearing });
        foreach (var each in document.Walls.Where(each => each.LevelId == ground).ToList()) document.Add(Footings.Fit(document, each));
        if (concrete)
        {
            var slab = new FloorType("Slab 200", new CompoundStructure(new MaterialLayer(LayerFunction.Structure, document.Materials.First(m => m.Name.StartsWith("Concrete, Cast", StringComparison.Ordinal)).Id, 200)));
            document.AddType(slab);
            var floor = new Floor { TypeId = slab.Id, LevelId = first };
            floor.SetBoundary(corners);
            document.Add(floor);
        }
        else
        {
            var joist = new BeamType("Timber 75 x 225") { Material = BeamMaterial.Timber, Width = 75, Depth = 225 };
            document.AddType(joist);
            for (var x = 1000; x <= 9000; x += 2000)
                document.Add(new Beam { TypeId = joist.Id, LevelId = first, Start = new Point2D(x, 0), End = new Point2D(x, 8000), Usage = BeamUsage.Joist });
            var floor = new Floor { TypeId = document.TypesOf<FloorType>().First(t => t.Name.StartsWith("Floor - Timber", StringComparison.Ordinal)).Id, LevelId = first, SpanDirection = 0 };
            floor.SetBoundary(corners);
            document.Add(floor);
        }

        return document;
    }

    private static IReadOnlyList<FireCheck> Fire(BimDocument document)
    {
        var model = AnalyticalModel.Of(document);
        var analysis = StructuralAnalysis.Run(document, model, StructuralLoading.Of(document, model));
        var second = SecondOrderAnalysis.Run(document, analysis);
        var members = MemberStrength.Of(document, analysis, second);
        return FireDesign.Of(document, second, members, JointDesign.Of(document, second));
    }

    [Fact]
    public void AUsBuildingIsRatedByItsTypeOfConstruction()
    {
        // A house under the IRC wants none; an office wants its type set, and then Type VA's hour.
        var house = Building("Timber Frame", "House", false, ConstructionType.VA);
        Assert.Equal(0, FireDesign.PeriodOf(house).Minutes);
        var unset = Building("Timber Frame", "Office", false, ConstructionType.NotSet);
        Assert.Contains("Construction Type", FireDesign.PeriodOf(unset).Said);
        var office = Building("Timber Frame", "Office", false, ConstructionType.VA);
        Assert.Equal(60, FireDesign.PeriodOf(office).Minutes);
        Assert.Contains("Table 601", FireDesign.PeriodOf(office).Said);

        // Its 75 mm joists by NDS Chapter 16: 1.8 in of char from each side in an hour is more than they are wide. Its stud walls want a listed assembly's rating.
        var checks = Fire(office);
        Assert.All(checks.Where(check => check.Kind == "Joist"), check => Assert.Equal("Burnt through", check.Worst!.Name));
        Assert.All(checks.Where(check => check.Kind == "Wall"), check => Assert.True(check.Checks.Count == 0 ? check.Limits.Contains("IBC 703.3") : check.Checks.Single().Clause == "IBC Table 601", check.Name));

        // Type VB wants none at all.
        Assert.Empty(Fire(Building("Timber Frame", "Office", false, ConstructionType.VB)));
    }

    [Fact]
    public void AUsConcreteBuildingIsByAci2161()
    {
        // Type IIA: an hour for its walls and floor, 3.5 in of concrete each, which 200 mm is.
        var checks = Fire(Building("Generic", "Office", true, ConstructionType.IIA));
        var floor = checks.Single(check => check.Kind == "Floor");
        Assert.Equal(3.5 * 25.4, floor.Checks.Single().Demand, 6);
        Assert.Contains("ACI 216.1", floor.Checks.Single().Clause);
        Assert.All(checks.Where(check => check.Kind == "Wall"), check => Assert.Contains("ACI 216.1", check.Checks.Single().Clause));
        Assert.All(checks, check => Assert.True(check.Enough, check.Name));
    }

    /// <summary>Three storeys of steel office on corner columns a storey at a time, beams fixed into the columns' flanges, concrete floors, and two concrete walls at its foot standing both ways.</summary>
    private static (BimDocument Document, AnalyticalModel Model) SteelOffice()
    {
        var document = BimDocument.CreateDefault();
        document.ProjectInformation.BuildingType = "Office";
        var levels = document.Levels.OrderBy(level => level.Elevation).ToList();
        for (var i = levels.Count; i <= 3; i++)
        {
            var level = new Level { Name = $"Level {i}", Elevation = 3000 * i };
            document.AddLevel(level);
            levels.Add(level);
        }

        var post = document.TypesOf<ColumnType>().First(type => type.Name == "UC 203 x 203 x 46");
        var ub = document.TypesOf<BeamType>().First(type => type.Name.StartsWith("UB 254", StringComparison.Ordinal));
        var slab = document.TypesOf<FloorType>().First(type => type.Name.StartsWith("Floor - Screed", StringComparison.Ordinal));
        var generic = document.TypesOf<WallType>().First(type => type.Name.StartsWith("Generic", StringComparison.Ordinal));
        var corners = new[] { new Point2D(0, 0), new Point2D(6000, 0), new Point2D(6000, 6000), new Point2D(0, 6000) };
        for (var s = 0; s < 3; s++)
        {
            var columns = corners.Select(corner => new Column { TypeId = post.Id, LevelId = levels[s].Id, TopLevelId = levels[s + 1].Id, Location = corner, Structural = true }).ToList();
            foreach (var column in columns) document.Add(column);
            if (s == 0)
                foreach (var column in columns) document.Add(Footings.Fit(document, column));
            foreach (var x in new[] { 0.0, 6000 })
                document.Add(new Beam { TypeId = ub.Id, LevelId = levels[s + 1].Id, Start = new Point2D(x, 0), End = new Point2D(x, 6000), StartRelease = MemberRelease.Fixed, EndRelease = MemberRelease.Fixed });
            foreach (var y in new[] { 0.0, 6000 })
                document.Add(new Beam { TypeId = ub.Id, LevelId = levels[s + 1].Id, Start = new Point2D(0, y), End = new Point2D(6000, y) });
            var floor = new Floor { TypeId = slab.Id, LevelId = levels[s + 1].Id };
            floor.SetBoundary(corners);
            document.Add(floor);
        }

        var walls = new[] { new Wall { Start = new Point2D(2000, 2000), End = new Point2D(4000, 2000), TypeId = generic.Id, LevelId = levels[0].Id, TopLevelId = levels[1].Id, StructuralUsage = StructuralUsage.Bearing },
            new Wall { Start = new Point2D(2000, 2000), End = new Point2D(2000, 4000), TypeId = generic.Id, LevelId = levels[0].Id, TopLevelId = levels[1].Id, StructuralUsage = StructuralUsage.Bearing } };
        foreach (var wall in walls)
        {
            document.Add(wall);
            document.Add(Footings.Fit(document, wall));
        }

        return (document, AnalyticalModel.Of(document));
    }

    [Fact]
    public void ASteelColumnInACompartmentBucklesShorterInFire()
    {
        // BS EN 1993-1-2 4.2.3.2(5): braced, each storey a compartment, a column running on above and below at 0.5 L, at one end only 0.7 L.
        var (document, model) = SteelOffice();
        var columns = model.Members.Where(member => member.Kind == AnalyticalMemberKind.Column).ToList();
        AnalyticalMember At(double z) => columns.First(column => Math.Abs(Math.Min(column.Start.At.Z, column.End.At.Z) - z) < 1);
        foreach (var (z, factor) in new[] { (3000.0, 0.5), (6000.0, 0.7), (0.0, 0.7) })
        {
            var column = At(z);
            var cold = new MemberLengths(column.Length, column.Length, column.Length, column.Length);
            var (hot, said) = FireDesign.LengthsInFire(document, model, column, cold);
            Assert.Equal(factor, said, 6);
            Assert.Equal(factor * column.Length, hot.Strong, 6);
            Assert.Equal(factor * column.Length, hot.Weak, 6);
        }

        // A house's floors are not compartments: as cold.
        document.ProjectInformation.BuildingType = "House";
        Assert.Equal(1, FireDesign.LengthsInFire(document, model, At(3000), new MemberLengths(3000, 3000, 3000, 3000)).Factor);
    }

    [Fact]
    public void ASteelJointStandsInFireAsLongAsItIsUsedNoMoreThanItsMembers()
    {
        // The office's moment joints in fire by BS EN 1993-1-2 4.2.1(6): each its use at room temperature against the most of the members it joins.
        var (document, _) = SteelOffice();
        var checks = Fire(document);
        var joints = checks.Where(check => check.Kind == "Joint").ToList();
        Assert.NotEmpty(joints);
        Assert.All(joints, joint =>
        {
            var check = joint.Checks.Single();
            Assert.Equal("BS EN 1993-1-2 4.2.1(6)", check.Clause);
            Assert.True(check.Capacity > 0);
            Assert.Contains("Protected as the members it joins are", joint.Limits);
        });
        Assert.Contains(joints, joint => joint.Checks.Single().Note.StartsWith("Moment end plate", StringComparison.Ordinal));
    }
}
