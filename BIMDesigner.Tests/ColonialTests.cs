using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Mep;

namespace BIMDesigner.Tests;

/// <summary>
/// A US centre-hall colonial, two storeys with a porch and a garage with a bonus room over it, built in the real window (round 153): a porch
/// roof on a beam over posts, its eave carried by the beam and the beam judged post to post; a "Hall Bath" a bathroom, not a hall; and US
/// stock base cabinets 24 in wide, the counter the NKBA asks beside a sink.
/// </summary>
[Collection("Wpf")]
public class ColonialTests
{
    private static Guid TypeOf<T>(BimDocument document, string name) where T : ElementType => document.TypesOf<T>().First(each => each.Name == name).Id;

    private static BimDocument Us()
    {
        var document = BimDocument.CreateDefault();
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.Irc;
        document.ProjectInformation.BuildingType = "Detached house";
        return document;
    }

    /// <summary>
    /// A porch 7 m wide and 2 m deep before a house wall along y 0: a shed roof sloping down to the front at 14 degrees, its eave at 2743 over a
    /// glulam beam on four 6x6 posts 2333 apart, each on a pad.
    /// </summary>
    private static (BimDocument Document, Roof Roof, Beam Beam, List<Column> Posts) Porch()
    {
        var document = Us();
        var ground = document.Levels.OrderBy(level => level.Elevation).First();
        document.Add(new Wall { Start = new Point2D(-1000, 0), End = new Point2D(8000, 0), TypeId = TypeOf<WallType>(document, "Timber Frame - Siding 183mm"), LevelId = ground.Id, UnconnectedHeight = 3048, StructuralUsage = StructuralUsage.Bearing });
        var posts = new List<Column>();
        foreach (var x in new[] { 0.0, 7000.0 / 3, 14000.0 / 3, 7000 })
        {
            var post = new Column { LevelId = ground.Id, TypeId = TypeOf<ColumnType>(document, "Timber Post 6x6, Treated"), Structural = true, Location = new Point2D(x, -2000), UnconnectedHeight = 2743 - 270 };
            document.Add(post);
            document.Add(Footings.Fit(document, post));
            posts.Add(post);
        }

        var beam = new Beam { LevelId = ground.Id, TypeId = TypeOf<BeamType>(document, "Glulam GL24h 90x270"), Start = new Point2D(0, -2000), End = new Point2D(7000, -2000), StartOffset = 2743, EndOffset = 2743 };
        document.Add(beam);
        var roof = new Roof { LevelId = ground.Id, TypeId = TypeOf<RoofType>(document, "Roof - Asphalt Shingles on Plywood, 2x8 Rafters"), HeightOffset = 2743 };
        roof.SetBoundary(new[] { new Point2D(-150, -2300), new Point2D(7150, -2300), new Point2D(7150, 0), new Point2D(-150, 0) });
        roof.SetEdges(Enumerable.Range(0, 4).Select(i => new RoofEdge { DefinesSlope = i == 0, SlopeDegrees = 14 }));
        document.Add(roof);
        return (document, roof, beam, posts);
    }

    private static StructuralLoading Loading(BimDocument document) => StructuralLoading.Of(document, AnalyticalModel.Of(document));

    [Fact]
    public void APorchRoofsEaveBearsOnTheBeamOverItsPosts()
    {
        // No wall under the porch's eave: the beam along it carries the slope's weight, snow and live load.
        var (document, roof, beam, _) = Porch();
        var loading = Loading(document);
        Assert.DoesNotContain(loading.Problems, problem => problem.Elements.Contains(roof));
        Assert.Contains(loading.LineLoads, load => load.Member?.Element == beam && load.Said.Contains("of the roof at 14"));

        // Without the beam, the eave bears on nothing - beam or wall.
        document.Remove(beam);
        Assert.Contains(Loading(document).Problems, problem => problem.Elements.Contains(roof) && problem.Said.Contains("no bearing wall or beam runs under it"));
    }

    [Fact]
    public void ALeanTosSlopeIsSharedBetweenItsEaveAndTheWallAtItsTop()
    {
        // The porch's shed roof spans from the beam at its eave to the house wall it leans on: half its weight on each (round 156).
        var (document, roof, beam, _) = Porch();
        var wall = document.Walls.Single();
        var loading = Loading(document);
        double Weight(Func<LineLoad, bool> on) =>
            loading.LineLoads.Where(load => load.Case.Name is "G2" or "D2" && on(load)).Sum(load => -load.Force.Z * (load.End - load.Start) / 1000);
        var onBeam = Weight(load => load.Member?.Element == beam);
        var onWall = Weight(load => load.Panel?.Element == wall);
        Assert.True(onBeam > 0 && onWall > 0);
        Assert.Equal(onBeam, onWall, 1);
        Assert.Contains(loading.LineLoads, load => load.Member?.Element == beam && load.Said.Contains("half of it, at its eave"));
        Assert.Contains(loading.LineLoads, load => load.Panel?.Element == wall && load.Said.Contains("half of it, at its top"));

        // Its weight all told is the slope's, as before: the roof's area at its pitch by its weight.
        var type = document.FindType<SlabType>(roof.TypeId)!;
        var face = roof.Surface(document).Facets.Single();
        Assert.Equal(face.Area / 1e6 * Loads.Weight(document, type) * face.Plane.VerticalStretch, onBeam + onWall, 1);

        // With no wall at its top, it all goes to the eave.
        document.Remove(wall);
        loading = Loading(document);
        Assert.Equal(face.Area / 1e6 * Loads.Weight(document, type) * face.Plane.VerticalStretch, Weight(load => load.Member?.Element == beam), 1);
    }

    [Fact]
    public void ABeamOverPostsIsJudgedFromPostToPost()
    {
        // 7000 long on posts 2333 apart: its span 2333, well inside 18 times its 270 depth (4860).
        var (document, _, beam, posts) = Porch();
        Assert.Equal(2333, Beams.LongestSpan(document, beam), 0);
        Assert.DoesNotContain(Beams.Problems(document, beam), said => said.Contains("times its depth"));

        // Its two middle posts taken away: 7000 from end to end, more than 4860.
        document.Remove(posts[1]);
        document.Remove(posts[2]);
        Assert.Equal(7000, Beams.LongestSpan(document, beam), 0);
        Assert.Contains(Beams.Problems(document, beam), said => said.Contains("times its depth") && !said.Contains("between its supports"));

        // One taken back, off-centre: from it to the far end, 4667 - inside the rule, said between its supports when over it.
        document.Add(posts[1]);
        Assert.Equal(4667, Beams.LongestSpan(document, beam), 0);
        Assert.DoesNotContain(Beams.Problems(document, beam), said => said.Contains("times its depth"));

        // A post that stops short of the beam carries nothing.
        posts[1].UnconnectedHeight = 1500;
        Assert.Equal(7000, Beams.LongestSpan(document, beam), 0);
        Assert.Contains(Beams.Problems(document, beam), said => said.Contains("times its depth"));
    }

    /// <summary>Four partitions round a rectangle on the ground storey, and a room in it.</summary>
    private static Room Box(BimDocument document, string name, double x0, double y0, double x1, double y1)
    {
        var level = document.Levels.OrderBy(each => each.Elevation).First();
        var type = TypeOf<WallType>(document, "Interior - Partition 125mm");
        var corners = new[] { new Point2D(x0, y0), new Point2D(x1, y0), new Point2D(x1, y1), new Point2D(x0, y1) };
        for (var i = 0; i < 4; i++) document.Add(new Wall { Start = corners[i], End = corners[(i + 1) % 4], TypeId = type, LevelId = level.Id, UnconnectedHeight = 2400 });
        var room = new Room { LevelId = level.Id, Location = new Point2D((x0 + x1) / 2, (y0 + y1) / 2), Name = name };
        document.Add(room);
        return room;
    }

    [Fact]
    public void AHallBathIsABathroomNotAHall()
    {
        Assert.True(Ventilation.WetByName("Hall Bath"));
        Assert.True(Ventilation.WetByName("Laundry"));
        Assert.False(Ventilation.WetByName("Entrance Hall"));

        // A hall bath 2 m by 4 m - longer than 10 ft, narrower than 3 ft would be no matter - with its tub, and a bedroom off a hall beside it.
        var document = Us();
        var bath = Box(document, "Hall Bath", 0, 0, 2000, 4000);
        var level = document.Levels.OrderBy(each => each.Elevation).First();
        var tub = document.TypesOf<ComponentType>().First(each => each.Name == "Bathtub - 60 x 30 in");
        var piece = new Component { TypeId = tub.Id, TypeKind = tub.Kind, LevelId = level.Id, Location = new Point2D(1000, 3500) };
        ComponentPlacing.FitToWalls(document, piece);
        document.Add(piece);
        var outline = bath.GetBoundary(document).Polygon;
        Assert.Equal(LitRoom.Bathroom, Lighting.KindOf(document, bath, outline));

        // Not a hallway: R311.6 and NEC 210.52(H) ask nothing of it; it wants a bathroom's light, by 210.70(A)(1).
        Assert.Null(Circulation.Verdict(document, bath));
        var said = Services.Verdict(document, bath) ?? string.Empty;
        Assert.DoesNotContain("210.52(H)", said);
        Assert.Contains("NEC 210.70(A)(1)", said);

        // A bedroom with its alarm: the hall bath is no sleeping area's hall wanting one; the hall is.
        Box(document, "Bedroom", 2000, 0, 5000, 4000);
        var hall = Box(document, "Hall", 0, 4000, 5000, 5200);
        var alarm = document.TypesOf<ComponentType>().First(each => each.Name == "Smoke Alarm");
        document.Add(new Component { TypeId = alarm.Id, TypeKind = alarm.Kind, LevelId = level.Id, Location = new Point2D(3500, 2000), Elevation = 2355 });
        var problems = AlarmSiting.Problems(document).ToList();
        Assert.DoesNotContain(problems, problem => problem.Element == bath);
        Assert.Contains(problems, problem => problem.Element == hall && problem.Text.Contains("outside each sleeping area"));
    }

    [Fact]
    public void UsStockCabinetsGiveTheCounterTheNkbaAsksBesideASink()
    {
        // Offered in a US project, not an English one; 24 in wide, deep and 36 in to the counter.
        var us = ProjectTemplates.Create(new NewProjectSpec { Template = ProjectTemplate.UsHouse });
        var england = ProjectTemplates.Create(new NewProjectSpec { Template = ProjectTemplate.EnglandHouse });
        foreach (var name in new[] { "Base Cabinet - 24 in", "Base Cabinet - 36 in", "Sink Base - 36 in" })
        {
            Assert.True(ProjectTemplates.Fits(us, us.TypesOf<ComponentType>().Single(type => type.Name == name)));
            Assert.False(ProjectTemplates.Fits(england, england.TypesOf<ComponentType>().Single(type => type.Name == name)));
        }

        var cabinet = us.TypesOf<ComponentType>().Single(type => type.Name == "Base Cabinet - 24 in");
        Assert.Equal((610.0, 610.0, 914.0), (cabinet.Width, cabinet.Depth, cabinet.Height));

        // A sink base between a 24 in cabinet and a 600 unit against a wall: 24 in one side, 23 5/8 in the other - the NKBA's 24 and 18 met.
        // Between two 600 units: 23 5/8 in each side, short of 24 on both.
        var document = Us();
        var level = document.Levels.OrderBy(each => each.Elevation).First();
        var kitchen = Box(document, "Kitchen", 0, 0, 4000, 3000);
        Component Put(string name, double x)
        {
            var type = document.TypesOf<ComponentType>().First(each => each.Name == name);
            var piece = new Component { TypeId = type.Id, TypeKind = type.Kind, LevelId = level.Id, Location = new Point2D(x, 3000 - 62.5 - type.Depth / 2), Rotation = 0 };
            document.Add(piece);
            return piece;
        }

        var sinkType = document.TypesOf<ComponentType>().First(each => each.Name == "Sink Base - 36 in");
        var sink = Put("Sink Base - 36 in", 2000);
        var left = Put("Base Unit - 600", 2000 - 457 - 300);
        var right = Put("Base Unit - 600", 2000 + 457 + 300);
        bool Short() => Kitchens.Problems(document, sink, ClearanceStandard.Irc).Any(problem => problem.Rule.Contains("Guideline 11"));
        Assert.True(Short());
        document.Remove(right);
        Put("Base Cabinet - 24 in", 2000 + 457 + 305);
        Assert.False(Short());
        Assert.Equal(610, Math.Max(Kitchens.Worktop(document, sink, -1), Kitchens.Worktop(document, sink, 1)), 0);
        _ = (kitchen, left, sinkType);
    }
}
