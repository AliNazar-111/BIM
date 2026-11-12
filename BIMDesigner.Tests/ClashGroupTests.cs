using System.IO.Compression;
using System.Xml.Linq;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Coordination;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Infrastructure.Interoperability;
using BIMDesigner.Infrastructure.Serialization;

namespace BIMDesigner.Tests;

/// <summary>
/// Clashes put together: by the element they share - the most shared first - by storey, by storey and
/// grid, by their two types, by nearness; a clash found later joining its group; a group's status its
/// least advanced clash's and set on each of them; groups made and taken apart by hand, kept with the
/// project, undone, and handed on as one BCF topic each.
/// </summary>
public class ClashGroupTests
{
    /// <summary>A project with walls standing in for what clashes - each marked so it can be told apart - and a test to hold clashes made by hand.</summary>
    private static (BimDocument Document, ClashTest Test, Dictionary<string, Guid> Ids) Scene(params string[] marks)
    {
        var document = BimDocument.CreateDefault();
        var type = document.TypesOf<WallType>().First().Id;
        var ids = new Dictionary<string, Guid>();
        var x = 0.0;
        foreach (var mark in marks)
        {
            var wall = new Wall { Start = new Point2D(x, 0), End = new Point2D(x + 1000, 0), TypeId = type, LevelId = document.Levels[0].Id, Mark = mark };
            document.Add(wall);
            ids[mark] = wall.Id;
            x += 2000;
        }

        var test = new ClashTest { Name = "Ducts into beams" };
        test.SelectionA.Add(BuiltInCategory.Ducts);
        test.SelectionB.Add(BuiltInCategory.StructuralFraming);
        return (document, test, ids);
    }

    private static Clash Add(ClashTest test, Guid a, Guid b, double x, string level = "Ground Floor", string grid = "", ClashStatus status = ClashStatus.New)
    {
        var clash = new Clash
        {
            Id = ClashDetective.IdOf(test.Id, a, b), Name = $"Clash{test.Results.Count + 1}", ElementA = a, ElementB = b, Point = new Point3D(x, 0, 2500),
            Distance = -40, Level = level, GridLocation = grid, Status = status, Found = new DateTime(2026, 11, 12)
        };
        test.Results.Add(clash);
        return clash;
    }

    [Fact]
    public void ClashesSharingAnElementAreGroupedByTheMostSharedFirstAndALaterOneJoins()
    {
        // A duct through three beams, a pipe through the third and a fourth: the duct's three first, then the pipe's two.
        var (document, test, id) = Scene("D1", "P1", "B1", "B2", "B3", "B4", "B5");
        Add(test, id["D1"], id["B1"], 0);
        Add(test, id["D1"], id["B2"], 2000);
        Add(test, id["D1"], id["B3"], 4000);
        Add(test, id["P1"], id["B3"], 4200);
        Add(test, id["P1"], id["B4"], 6000);

        Assert.Equal(5, ClashGroups.Group(document, test, ClashGrouping.Element, 3000));
        Assert.Equal(2, test.Groups.Count);
        var (duct, pipe) = (test.Groups[0], test.Groups[1]);
        Assert.EndsWith("(D1)", duct.Name);
        Assert.EndsWith("(P1)", pipe.Name);
        Assert.Equal(new[] { "Clash1", "Clash2", "Clash3" }, ClashGroups.Members(test, duct).Select(clash => clash.Name));
        Assert.Equal(new[] { "Clash4", "Clash5" }, ClashGroups.Members(test, pipe).Select(clash => clash.Name));

        // Found later through a fifth beam: into the duct's group, not a group of one.
        var later = Add(test, id["D1"], id["B5"], 8000);
        Assert.Equal(1, ClashGroups.Group(document, test, ClashGrouping.Element, 3000));
        Assert.Equal(duct.Id, later.GroupId);
        Assert.Equal(2, test.Groups.Count);
    }

    [Fact]
    public void ClashesAreGroupedByStoreyByGridByTypesAndByNearness()
    {
        var (document, test, id) = Scene("D1", "D2", "B1", "B2", "B3");
        Add(test, id["D1"], id["B1"], 0, "Ground Floor", "A-1");
        Add(test, id["D1"], id["B2"], 2000, "Ground Floor", "A-1");
        Add(test, id["D2"], id["B3"], 20_000, "Ground Floor", "C-1");
        Add(test, id["D2"], id["B1"], 21_000, "First Floor", "C-1");

        // By storey: the ground floor's three; the first floor's one left on its own.
        ClashGroups.Group(document, test, ClashGrouping.Level, 3000);
        Assert.Equal("Ground Floor", Assert.Single(test.Groups).Name);
        Assert.Equal(3, ClashGroups.Members(test, test.Groups[0]).Count);

        // By storey and grid: A-1's two together; the rest each alone on its storey at its crossing.
        foreach (var group in test.Groups.ToList()) ClashGroups.Ungroup(test, group);
        ClashGroups.Group(document, test, ClashGrouping.GridLocation, 3000);
        Assert.Equal("Ground Floor at A-1", Assert.Single(test.Groups).Name);

        // By types: all four between the same two wall types - one group, whichever way round.
        foreach (var group in test.Groups.ToList()) ClashGroups.Ungroup(test, group);
        ClashGroups.Group(document, test, ClashGrouping.Types, 3000);
        var type = document.TypesOf<WallType>().First().Name;
        Assert.Equal($"{type} against {type}", Assert.Single(test.Groups).Name);
        Assert.Equal(4, ClashGroups.Members(test, test.Groups[0]).Count);

        // By nearness, 3 m: 0 and 2 m together; 20 and 21 m together; then one at 4.5 m joins the first through the one at 2 m.
        foreach (var group in test.Groups.ToList()) ClashGroups.Ungroup(test, group);
        ClashGroups.Group(document, test, ClashGrouping.Proximity, 3000);
        Assert.Equal(new[] { 2, 2 }, test.Groups.Select(group => ClashGroups.Members(test, group).Count));
        Assert.Equal("Ground Floor near A-1", test.Groups[0].Name);
        var near = Add(test, id["D2"], id["B2"], 4500);
        ClashGroups.Group(document, test, ClashGrouping.Proximity, 3000);
        Assert.Equal(test.Groups[0].Id, near.GroupId);
    }

    [Fact]
    public void AGroupStandsAtItsLeastAdvancedClashAndWhatIsSetOnItIsSetOnEach()
    {
        var (document, test, id) = Scene("D1", "B1", "B2", "B3");
        var (one, two, three) = (Add(test, id["D1"], id["B1"], 0), Add(test, id["D1"], id["B2"], 2000, status: ClashStatus.Approved), Add(test, id["D1"], id["B3"], 4000, status: ClashStatus.Resolved));
        (one.Priority, two.Priority) = (ClashPriority.Low, ClashPriority.High);
        (one.DueDate, two.DueDate) = (new DateOnly(2026, 12, 4), new DateOnly(2026, 11, 30));
        (one.AssignedTo, two.AssignedTo, three.AssignedTo) = ("services", "structure", "services");
        var group = ClashGroups.GroupByHand(test, new[] { one.Id, two.Id, three.Id }, "Duct D1")!;
        Assert.Null(ClashGroups.GroupByHand(test, new[] { one.Id }));

        // New, approved, resolved: new, open while the one is; the highest priority, the soonest date, who has the most.
        Assert.Equal(ClashStatus.New, ClashGroups.Status(test, group));
        Assert.True(ClashGroups.IsOpen(test, group));
        Assert.Equal((ClashPriority.High, new DateOnly(2026, 11, 30), "services"), (ClashGroups.Priority(test, group), ClashGroups.Due(test, group), ClashGroups.AssignedTo(test, group)));

        // Reviewed: each open or approved one reviewed - the one no longer found stays resolved; approved, all of them are.
        ClashGroups.SetStatus(test, group, ClashStatus.Reviewed);
        Assert.Equal(new[] { ClashStatus.Reviewed, ClashStatus.Reviewed, ClashStatus.Resolved }, new[] { one, two, three }.Select(clash => clash.Status));
        ClashGroups.SetStatus(test, group, ClashStatus.Approved);
        Assert.All(new[] { one, two, three }, clash => Assert.Equal(ClashStatus.Approved, clash.Status));
        Assert.False(ClashGroups.IsOpen(test, group));

        // Taken out one by one: the group goes with its last clash.
        ClashGroups.TakeOut(test, new[] { one.Id, two.Id });
        Assert.Single(test.Groups);
        ClashGroups.TakeOut(test, new[] { three.Id });
        Assert.Empty(test.Groups);
        Assert.All(test.Results, clash => Assert.Null(clash.GroupId));
    }

    /// <summary>The house with a desk pushed into its front wall at each of three places, and one into its side wall.</summary>
    private static (BimDocument Document, ClashTest Test) DesksInWalls()
    {
        var document = ConstructionTests.House();
        var desk = document.Elements.OfType<Component>().Single();
        desk.Location = new Point2D(3000, 200);
        foreach (var at in new[] { new Point2D(6000, 200), new Point2D(8500, 200) })
            document.Add(new Component { TypeId = desk.TypeId, TypeKind = desk.TypeKind, LevelId = desk.LevelId, Location = at });
        document.Add(new Component { TypeId = desk.TypeId, TypeKind = desk.TypeKind, LevelId = desk.LevelId, Location = new Point2D(500, 4000) });
        return (document, document.ClashTests.Single(each => each.Name.Contains("Fittings and furniture through walls")));
    }

    [Fact]
    public void ATestGroupsWhatItFindsEachTimeItRuns()
    {
        var (document, test) = DesksInWalls();
        test.Grouping = ClashGrouping.Element;
        ClashDetective.Run(document, test, new DateTime(2026, 11, 12, 9, 0, 0));
        Assert.Equal(4, test.Results.Count);
        var front = document.Walls.First(wall => wall.LevelId == document.Levels[0].Id);
        var group = Assert.Single(test.Groups);
        Assert.Equal(ClashDetective.NameOf(document, front.Id), group.Name);
        Assert.Equal(3, ClashGroups.Members(test, group).Count);
        Assert.Single(test.Results, clash => clash.GroupId is null);

        // A fourth desk into the front wall: run again, it joins the wall's group; the group keeps its id and its comment.
        group.Comments.Add(new ClashComment(new DateTime(2026, 11, 12, 10, 0, 0), "AN", "Move the desks off the front wall."));
        var desk = document.Elements.OfType<Component>().First();
        document.Add(new Component { TypeId = desk.TypeId, TypeKind = desk.TypeKind, LevelId = desk.LevelId, Location = new Point2D(1500, 200) });
        ClashDetective.Run(document, test, new DateTime(2026, 11, 13, 9, 0, 0));
        Assert.Equal(group.Id, Assert.Single(test.Groups).Id);
        Assert.Equal(4, ClashGroups.Members(test, test.Groups[0]).Count);
        Assert.Single(test.Groups[0].Comments);

        // Every desk pulled back: all resolved, the group with them - kept on record, no longer open.
        var place = 0;
        foreach (var each in document.Elements.OfType<Component>().ToList()) each.Location = new Point2D(1500 + 1800 * place++, 4000);
        ClashDetective.Run(document, test, new DateTime(2026, 11, 14, 9, 0, 0));
        Assert.Equal(ClashStatus.Resolved, ClashGroups.Status(test, test.Groups[0]));
    }

    [Fact]
    public void GroupsAreKeptWithTheProjectAndUndone()
    {
        var (document, test) = DesksInWalls();
        test.Grouping = ClashGrouping.Element;
        ClashDetective.Run(document, test, new DateTime(2026, 11, 12, 9, 0, 0));
        test.Groups[0].Comments.Add(new ClashComment(new DateTime(2026, 11, 12, 10, 0, 0), "AN", "One fix for all three."));

        var back = ProjectFile.FromJson(ProjectFile.ToJson(document)).ClashTests.Single(each => each.Id == test.Id);
        Assert.Equal(ClashGrouping.Element, back.Grouping);
        var group = Assert.Single(back.Groups);
        Assert.Equal((test.Groups[0].Id, test.Groups[0].Name, test.Groups[0].Key), (group.Id, group.Name, group.Key));
        Assert.Equal("One fix for all three.", Assert.Single(group.Comments).Text);
        Assert.Equal(test.Results.Select(clash => clash.GroupId), back.Results.Select(clash => clash.GroupId));

        // Taken apart as a step: undone, the group is back with its clashes in it.
        var history = new BIMDesigner.Core.Documents.Commands.UndoStack();
        history.Execute(CoordinationEditCommand.Make(document, "Ungroup", () => ClashGroups.Ungroup(test, test.Groups[0])));
        Assert.Empty(document.ClashTests.Single(each => each.Id == test.Id).Groups);
        history.Undo();
        var restored = document.ClashTests.Single(each => each.Id == test.Id);
        Assert.Equal(3, ClashGroups.Members(restored, Assert.Single(restored.Groups)).Count);
    }

    [Fact]
    public void AGroupGoesOutAsOneBcfTopicWithEveryElementInIt()
    {
        var (document, test) = DesksInWalls();
        test.Grouping = ClashGrouping.Element;
        ClashDetective.Run(document, test, new DateTime(2026, 11, 12, 9, 0, 0));
        var group = test.Groups[0];
        var alone = test.Results.Single(clash => clash.GroupId is null);

        using var stream = new MemoryStream();
        ClashReports.WriteBcf(stream, document, new[] { test }, "AN");
        stream.Position = 0;
        using var zip = new ZipArchive(stream);
        var topics = zip.Entries.Where(entry => entry.FullName.EndsWith("/markup.bcf")).Select(entry => entry.FullName.Split('/')[0]).ToList();
        Assert.Equal(new[] { group.Id.ToString(), alone.Id.ToString() }.OrderBy(name => name), topics.OrderBy(name => name));

        var markup = XDocument.Load(zip.GetEntry($"{group.Id}/markup.bcf")!.Open()).Root!;
        var topic = markup.Element("Topic")!;
        Assert.Equal($"{test.Name}: {group.Name}", topic.Element("Title")!.Value);
        Assert.StartsWith("3 clashes: ", topic.Element("Description")!.Value);
        Assert.Contains("Group", topic.Elements("Labels").Select(label => label.Value));

        // The wall once and the three desks: four elements selected.
        var viewpoint = XDocument.Load(zip.GetEntry($"{group.Id}/viewpoint.bcfv")!.Open()).Root!;
        Assert.Equal(4, viewpoint.Element("Components")!.Element("Selection")!.Elements("Component").Count());

        // In the spreadsheet and the report, each clash with its group.
        Assert.Contains(group.Name, ClashReports.Html(document, new[] { test }));
    }
}
