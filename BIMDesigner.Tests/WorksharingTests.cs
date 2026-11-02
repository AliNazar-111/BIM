using System.Text.Json.Nodes;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Sheets;
using BIMDesigner.Infrastructure.Collaboration;
using BIMDesigner.Infrastructure.Serialization;

namespace BIMDesigner.Tests;

/// <summary>
/// Worksharing: a project made a central model, two people each working in a local copy of it - what
/// one edits borrowed, refused to the other, sent on synchronising and brought back on reloading -
/// worksets made, made someone's own, asked for and granted, hidden in a view; and the three-way merge.
/// </summary>
public class WorksharingTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"share-{Guid.NewGuid():N}");

    public WorksharingTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    /// <summary>A central model of four walls round a 10 by 8 m box and a partition; its file.</summary>
    private string Central()
    {
        var document = BimDocument.CreateDefault();
        var type = document.TypesOf<WallType>().First(each => each.Function == WallFunction.Exterior);
        var corners = new[] { new Point2D(0, 0), new Point2D(10000, 0), new Point2D(10000, 8000), new Point2D(0, 8000) };
        for (var i = 0; i < 4; i++)
            document.Add(new Wall { Start = corners[i], End = corners[(i + 1) % 4], TypeId = type.Id, LevelId = document.Levels[0].Id, UnconnectedHeight = 3000, Mark = $"W{i + 1}" });
        var central = Path.Combine(_folder, "office.bimx");
        WorkshareSession.Enable(document, central);
        return central;
    }

    /// <summary>A person's local copy, with every edit made through their history checked and borrowed.</summary>
    private (WorkshareSession Session, UndoStack History, List<string> Refusals) Person(string central, string user)
    {
        var session = WorkshareSession.CreateLocal(central, user, Path.Combine(_folder, user));
        var history = new UndoStack();
        var refusals = new List<string>();
        history.Hold = _ => (null, session.Borrow());
        history.Refused += (_, why) => refusals.Add(why);
        return (session, history, refusals);
    }

    private static Wall Wall(WorkshareSession session, string mark) => session.Document.Walls.Single(wall => wall.Mark == mark);

    private static IUndoableCommand Move(WorkshareSession session, string mark, double by) =>
        new MoveElementsCommand(new Element[] { Wall(session, mark) }, new Vector2D(0, by), "Move", session.Document);

    [Fact]
    public void WhatOnePersonEditsIsBorrowedAndRefusedToTheOther()
    {
        var central = Central();
        var (ann, annHistory, annRefused) = Person(central, "ann");
        var (bob, bobHistory, bobRefused) = Person(central, "bob");

        annHistory.Execute(Move(ann, "W1", -500));
        Assert.Empty(annRefused);
        var registry = ShareRegistry.Read(central);
        Assert.Equal("ann", registry.Owners[Wall(ann, "W1").Id]);
        Assert.Single(registry.Owners);

        // Bob cannot move what Ann has borrowed: refused, taken back, and who has it said.
        bobHistory.Execute(Move(bob, "W1", 300));
        Assert.Contains("ann's", Assert.Single(bobRefused));
        Assert.Equal(0, Wall(bob, "W1").Start.Y);
        Assert.False(bobHistory.CanUndo);

        // But he can move another.
        bobHistory.Execute(Move(bob, "W3", 400));
        Assert.Single(bobRefused);
        Assert.Equal("bob", ShareRegistry.Read(central).Owners[Wall(bob, "W3").Id]);
    }

    [Fact]
    public void SynchronisingSendsOnesOwnAndBringsBackTheOthersAndReloadingKeepsOnesOwn()
    {
        var central = Central();
        var (ann, annHistory, _) = Person(central, "ann");
        var (bob, bobHistory, bobRefused) = Person(central, "bob");

        annHistory.Execute(Move(ann, "W1", -500));
        bobHistory.Execute(Move(bob, "W3", 400));

        // Nothing changed, nothing sent.
        var (carol, _, _) = Person(central, "carol");
        Assert.Equal(0, carol.Synchronize("nothing", relinquish: true).Sent);

        var annSync = ann.Synchronize("Moved the south wall", relinquish: true);
        Assert.Equal(1, annSync.Sent);
        Assert.Empty(annSync.Conflicts);
        Assert.Equal(2, annSync.Version);
        Assert.False(ShareRegistry.Read(central).Owners.ContainsKey(Wall(ann, "W1").Id));
        Assert.Equal(-500, Wall(ProjectFile.Load(central).Walls.Single(wall => wall.Mark == "W1")), 6);
        Assert.Contains(ShareRegistry.Read(central).History, record => record.User == "ann" && record.Comment == "Moved the south wall");

        // Bob, behind: Ann's wall cannot be borrowed until he has brought it up to date.
        Assert.True(bob.CentralIsNewer);
        var bobHistory2 = new UndoStack();
        bobHistory2.Hold = _ => (null, bob.Borrow());
        var stale = new List<string>();
        bobHistory2.Refused += (_, why) => stale.Add(why);
        bobHistory2.Execute(Move(bob, "W1", 100));
        Assert.Contains("Reload Latest", Assert.Single(stale));

        // Reloaded: Ann's move brought in, his own kept, nothing sent.
        var reload = bob.ReloadLatest();
        Assert.Equal(1, reload.Received);
        Assert.Equal(-500, Wall(bob, "W1").Start.Y, 6);
        Assert.Equal(8400, Wall(bob, "W3").Start.Y, 6);
        Assert.Equal(-500, Wall(ProjectFile.Load(central).Walls.Single(wall => wall.Mark == "W1")), 6);
        Assert.Equal(8000, ProjectFile.Load(central).Walls.Single(wall => wall.Mark == "W3").Start.Y, 6);

        var bobSync = bob.Synchronize("Moved the north wall", relinquish: true);
        Assert.Equal(1, bobSync.Sent);
        var both = ProjectFile.Load(central);
        Assert.Equal((-500.0, 8400.0), (both.Walls.Single(wall => wall.Mark == "W1").Start.Y, both.Walls.Single(wall => wall.Mark == "W3").Start.Y));
        Assert.Empty(ShareRegistry.Read(central).Owners);
    }

    private static double Wall(Wall wall) => wall.Start.Y;

    [Fact]
    public void AWorksetMadeOnesOwnIsAskedForAndGranted()
    {
        var central = Central();
        var (ann, annHistory, _) = Person(central, "ann");
        var (bob, bobHistory, bobRefused) = Person(central, "bob");

        Assert.Contains("yours", ann.MakeEditable(Worksharing.DefaultWorkset));
        Assert.Contains("ann's", bob.MakeEditable(Worksharing.DefaultWorkset));
        bobHistory.Execute(Move(bob, "W2", 200));
        Assert.Contains("ann's", Assert.Single(bobRefused));

        // Bob asks for the wall; Ann - who has changed nothing - grants it.
        Assert.Contains("Asked ann", bob.Request(Wall(bob, "W2").Id, Worksharing.DefaultWorkset, "wall W2"));
        var request = Assert.Single(ann.Requests());
        Assert.Contains("bob's now", ann.Answer(request.Id, grant: true));
        bobHistory.Execute(Move(bob, "W2", 200));
        Assert.Single(bobRefused);
        Assert.Contains(bob.Requests(), answered => answered.Answer == "Granted");

        // Something changed and not yet sent is not let go.
        annHistory.Execute(Move(ann, "W4", 100));
        Assert.Contains("synchronise", ann.Relinquish());
    }

    [Fact]
    public void WorksetsAreMadeRenamedTakenByNewElementsHiddenInAViewAndKept()
    {
        var document = BimDocument.CreateDefault();
        WorkshareSession.Enable(document, Path.Combine(_folder, "central.bimx"));
        var history = new UndoStack();
        history.Execute(Worksets.Create(document, "Interiors", out _)!);
        Assert.Null(Worksets.Create(document, "interiors", out var why));
        Assert.Contains("already", why);
        history.Execute(Worksets.SetActive(document, "Interiors"));

        var wall = new Wall { Start = new Point2D(0, 0), End = new Point2D(4000, 0), TypeId = document.TypesOf<WallType>().First().Id, LevelId = document.Levels[0].Id, UnconnectedHeight = 2700 };
        history.Execute(new AddElementCommand(document, wall, "Wall"));
        Assert.Equal("Interiors", wall.Workset);
        Assert.Contains("Interiors", Worksets.Choices(document));

        history.Execute(Worksets.Rename(document, document.Worksharing.Find("Interiors")!, "Fit-Out", out _)!);
        Assert.Equal(("Fit-Out", "Fit-Out"), (wall.Workset, document.Worksharing.Active));
        Assert.Null(Worksets.Rename(document, document.Worksharing.Find(Worksharing.Standards)!, "Mine", out _));

        var plan = ViewReference.FloorPlan(document.Levels[0].Id);
        Assert.True(document.ViewSettings.FilterFor(document, plan)(wall));
        document.ViewSettings.SetWorksetShown(plan, "Fit-Out", false);
        Assert.False(document.ViewSettings.FilterFor(document, plan)(wall));

        var kept = ProjectFile.FromJson(ProjectFile.ToJson(document));
        Assert.Equal(("Fit-Out", true), (kept.Worksharing.Active, kept.Worksharing.Enabled));
        Assert.False(kept.ViewSettings.WorksetShownIn(ViewReference.FloorPlan(kept.Levels[0].Id), "Fit-Out"));
        Assert.Equal("Fit-Out", kept.Walls.Single().Workset);
    }

    [Fact]
    public void TheMergeTakesEachSidesOwnChangesAndKeepsTheCentralsWhereBothChangedOneThing()
    {
        var common = JsonNode.Parse("""{"walls":[{"id":"a","x":1},{"id":"b","x":2}],"name":"Old","tags":[1,2]}""");
        var mine = JsonNode.Parse("""{"walls":[{"id":"a","x":5},{"id":"b","x":2},{"id":"c","x":9}],"name":"Mine","tags":[1,2]}""");
        var theirs = JsonNode.Parse("""{"walls":[{"id":"b","x":7}],"name":"Theirs","tags":[1,2,3]}""");

        var result = JsonMerge.Merge(common, mine, theirs);
        var walls = result.Merged!["walls"]!.AsArray().ToDictionary(wall => wall!["id"]!.GetValue<string>(), wall => wall!["x"]!.GetValue<int>());

        // Mine changed a, theirs took it away: a clash, theirs kept. b theirs, c mine; the name both changed: theirs.
        Assert.Equal(new Dictionary<string, int> { ["b"] = 7, ["c"] = 9 }, walls);
        Assert.Equal("Theirs", result.Merged["name"]!.GetValue<string>());
        Assert.Equal(3, result.Merged["tags"]!.AsArray().Count);
        Assert.Equal(new[] { "$.walls[\"a\"]", "$.name" }, result.Conflicts);
    }
}
