using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;
using BIMDesigner.Infrastructure.Serialization;

namespace BIMDesigner.Tests;

/// <summary>
/// Outside: trees by their water demand and stems, kept as NHBC and BS 5837 ask from the
/// building; parking bays at the sizes and slopes Approved Document M and the ADA ask; a
/// wheelchair user and a turning circle to try a room with.
/// </summary>
public class OutsideTests
{
    private static ComponentType Type(BimDocument document, string name) =>
        document.TypesOf<ComponentType>().Single(type => type.Name == name);

    /// <summary>A house's wall along y = 0 from x = 0 to 10 m, and nothing else.</summary>
    private static BimDocument WithWall()
    {
        var document = BimDocument.CreateDefault();
        document.Add(new Wall { Start = new Point2D(0, 0), End = new Point2D(10000, 0), TypeId = document.TypesOf<WallType>().First().Id, LevelId = document.Levels[0].Id, UnconnectedHeight = 2700 });
        return document;
    }

    private static Component Put(BimDocument document, string name, double x, double y, double rotation = 0)
    {
        var type = Type(document, name);
        var piece = new Component { TypeId = type.Id, TypeKind = type.Kind, LevelId = document.Levels[0].Id, Location = new Point2D(x, y), Rotation = rotation };
        document.Add(piece);
        return piece;
    }

    [Fact]
    public void TreesCarryTheirWaterDemandAndStemsAndAreSavedWithThem()
    {
        var document = BimDocument.CreateDefault();
        var oak = Type(document, "Tree - Oak, English");
        Assert.Equal((TreeWaterDemand.High, 20000.0, 800.0), (oak.WaterDemand, oak.Height, oak.StemDiameter));
        Assert.Equal(TreeWaterDemand.Low, Type(document, "Tree - Birch, Silver").WaterDemand);
        Assert.Equal(TreeWaterDemand.Moderate, Type(document, "Tree - Ash").WaterDemand);
        Assert.Equal(TreeWaterDemand.Unknown, Type(document, "Tree - Small").WaterDemand);
        Assert.Contains(oak.GetTypeParameters(document), parameter => parameter.Name == "Water Demand");

        var loaded = ProjectFile.FromJson(ProjectFile.ToJson(document));
        Assert.Equal((TreeWaterDemand.High, 800.0), (Type(loaded, "Tree - Oak, English").WaterDemand, Type(loaded, "Tree - Oak, English").StemDiameter));
        Assert.Equal(15, Type(loaded, "Extract Fan - 100 (15 l/s)").ExtractRate);
        Assert.Equal(90, Type(loaded, "Wall Light").Efficacy);
    }

    [Fact]
    public void ATreeIsKeptOutOfTheBuildingsWayByItsZoneOfInfluence()
    {
        var document = WithWall();
        var standard = ClearanceStandard.ApprovedDocumentM;

        // An oak 10 m off: inside the 25 m its roots may dry a clay soil.
        var oak = Put(document, "Tree - Oak, English", 5000, 10000 + 100);
        var near = Assert.Single(Outside.Problems(document, oak, standard));
        Assert.Contains("inside the 25.0 m", near.Said);
        Assert.Contains("1.25 times its mature height", near.Rule);

        // A birch as far off is beyond its 7 m.
        var birch = Put(document, "Tree - Birch, Silver", 5000, 10000 + 100);
        Assert.Empty(Outside.Problems(document, birch, standard));

        // A tree of no named species is taken as high demand: 1.25 times its 4 m.
        var small = Put(document, "Tree - Small", 5000, 4000 + 100);
        Assert.Contains("high (as one of no named species is taken)", Assert.Single(Outside.Problems(document, small, standard)).Rule);

        // Not asked in the US.
        Assert.Empty(Outside.Problems(document, oak, ClearanceStandard.Irc));
    }

    [Fact]
    public void AKeptTreesRootProtectionAreaAndATallShrubAreKeptClear()
    {
        var document = WithWall();

        // A holly kept on the site, 2 m off: inside its 3 m root protection area.
        var holly = Put(document, "Tree - Holly", 5000, 2000 + 100);
        holly.PhaseCreated = DesignPhase.Existing;
        var root = Assert.Single(Outside.Problems(document, holly, ClearanceStandard.Irc));
        Assert.Contains("inside its 3.0 m root protection area", root.Said);
        Assert.Contains("critical root zone", root.Rule);
        Assert.Contains(Outside.Problems(document, holly, ClearanceStandard.ApprovedDocumentM), problem => problem.Rule.Contains("BS 5837"));

        // A planted shrub of 1 m anywhere; one of 2.5 m kept 0.75 times its height off.
        Assert.Empty(Outside.Problems(document, Put(document, "Shrub - 1200", 2000, 500), ClearanceStandard.ApprovedDocumentM));
        var tall = Type(document, "Shrub - 1200").Duplicate("Shrub - 2500");
        tall.Height = 2500;
        document.AddType(tall);
        Assert.Contains(Outside.Problems(document, Put(document, "Shrub - 2500", 2000, 1000), ClearanceStandard.ApprovedDocumentM),
            problem => problem.Rule.Contains("Table 6"));
    }

    [Fact]
    public void AParkingBayIsAsBigAndAsLevelAsTheRulesAsk()
    {
        var document = BimDocument.CreateDefault();
        var standard = ClearanceStandard.ApprovedDocumentM;

        // Ground falling 1 in 15 down the bays, then 1 in 40.
        var ground = new Toposurface { LevelId = document.Levels[0].Id };
        void Slope(double fall) => ground.SetPoints(new[]
        {
            new Point3D(-10000, -10000, 0), new Point3D(20000, -10000, 0),
            new Point3D(20000, 20000, -30000 / fall), new Point3D(-10000, 20000, -30000 / fall)
        });
        Slope(15);
        document.Add(ground);

        var bay = Put(document, "Parking Bay - 2400 x 4800", 2000, 5000);
        Assert.Contains(Outside.Problems(document, bay, standard), problem => problem.Said == "the ground under it falls 1 in 15.");
        Slope(40);
        Assert.Empty(Outside.Problems(document, bay, standard));

        // An accessible bay wants it level: 1 in 60.
        var accessible = Put(document, "Parking Bay - Accessible 3600 x 6000", 8000, 5000);
        Assert.Contains(Outside.Problems(document, accessible, standard), problem => problem.Rule.Contains("1 in 60"));
        Assert.Equal((1200.0, 1200.0), ComponentModel.AccessibleBayOf(Type(document, "Parking Bay - Accessible 3600 x 6000")));
        Assert.Equal((1524.0, 0.0), ComponentModel.AccessibleBayOf(Type(document, "Parking Space - Accessible 96 + 60 in")));

        // Its lines and hatching all within it.
        foreach (var name in new[] { "Parking Bay - Accessible 3600 x 6000", "Parking Space - Accessible 96 + 60 in" })
        {
            var type = Type(document, name);
            var frame = new ComponentModel.Frame(new Point2D(0, 0), Vector2D.UnitX, 0);
            Assert.All(ComponentModel.PlanOutlines(type, frame).SelectMany(outline => outline), point =>
            {
                Assert.InRange(point.X, -type.Width / 2 - 0.5, type.Width / 2 + 0.5);
                Assert.InRange(point.Y, -type.Depth / 2 - 0.5, type.Depth / 2 + 0.5);
            });
        }

        // An accessible bay narrowed to 3 m is too small.
        var narrow = Type(document, "Parking Bay - Accessible 3600 x 6000").Duplicate("Parking Bay - Accessible 3000 x 6000");
        narrow.Width = 3000;
        document.AddType(narrow);
        Assert.Contains(Outside.Problems(document, Put(document, "Parking Bay - Accessible 3000 x 6000", 14000, 5000), standard), problem => problem.Said.EndsWith("too small."));
    }

    [Fact]
    public void ABayBetweenTwoWallsCannotBeWidenedTo3300()
    {
        var document = BimDocument.CreateDefault();
        var wallType = document.TypesOf<WallType>().First();
        var half = wallType.Structure.TotalWidth / 2;
        foreach (var x in new[] { -1300 - half, 1300 + half })
            document.Add(new Wall { Start = new Point2D(x, 0), End = new Point2D(x, 6000), TypeId = wallType.Id, LevelId = document.Levels[0].Id, UnconnectedHeight = 2400 });

        var bay = Put(document, "Parking Bay - 2400 x 4800", 0, 3000);
        Assert.Contains(Outside.Problems(document, bay, ClearanceStandard.ApprovedDocumentM), problem => problem.Rule.Contains("2.12a"));
        bay.Location = new Point2D(-1300 + 1200, 3000);
        Assert.Contains(Outside.Problems(document, bay, ClearanceStandard.ApprovedDocumentM), problem => problem.Rule.Contains("2.12a"));
    }

    [Fact]
    public void ATurningCircleIsBlockedByWhatStandsInItButNotByAPerson()
    {
        var document = WithWall();
        var circle = Put(document, "Turning Circle - 1500", 5000, 1000);
        var zone = Assert.Single(Clearances.Of(document, circle, ClearanceStandard.ApprovedDocumentM3));
        Assert.Empty(zone.In);
        Assert.Contains("1500 turning circle", zone.Zone.Rule);

        Put(document, "Person - Wheelchair User", 5000, 1000);
        Assert.Empty(Assert.Single(Clearances.Of(document, circle, ClearanceStandard.ApprovedDocumentM3)).In);

        Put(document, "Armchair", 5000, 1400);
        Assert.Contains("Armchair", Assert.Single(Clearances.Of(document, circle, ClearanceStandard.ApprovedDocumentM3)).In);
        Assert.Contains("60 in", Assert.Single(Clearances.Of(document, circle, ClearanceStandard.Ada)).Zone.Rule);
    }
}
