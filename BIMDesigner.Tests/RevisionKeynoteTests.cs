using System.IO;
using System.Reflection;
using System.Windows;
using BIMDesigner.Core.Annotation;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Schedules;
using BIMDesigner.Core.Sheets;
using BIMDesigner.Core.Views;
using BIMDesigner.Infrastructure.Serialization;
using BIMDesigner.UI;
using BIMDesigner.UI.Controls;

namespace BIMDesigner.Tests;

/// <summary>
/// Revisions numbered as issued, clouded on plans and listed on the sheets that show them; and
/// keynotes read off types and materials, kept in a table and listed in a legend.
/// </summary>
public class RevisionKeynoteTests
{
    private static Wall Wall(BimDocument document, Point2D start, Point2D end)
    {
        var type = document.TypesOf<WallType>().First(t => t.Function == WallFunction.Exterior);
        var wall = new Wall { Start = start, End = end, TypeId = type.Id, LevelId = document.Levels[0].Id, UnconnectedHeight = 3000 };
        document.Add(wall);
        return wall;
    }

    private static RevisionCloud Cloud(BimDocument document, Revision revision, Guid level, double x, double y)
    {
        var cloud = new RevisionCloud { RevisionId = revision.Id, LevelId = level, TagAt = new Point2D(x + 3000, y + 3000) };
        cloud.Outline.AddRange(new[] { new Point2D(x, y), new Point2D(x + 2000, y), new Point2D(x + 2000, y + 1500), new Point2D(x, y + 1500) });
        document.Add(cloud);
        return cloud;
    }

    [Fact]
    public void RevisionsAreNumberedByTheirPlaceAmongThoseNumberedAlike()
    {
        var document = BimDocument.CreateDefault();
        var p1 = new Revision();
        var p2 = new Revision();
        var c1 = new Revision { Numbering = RevisionNumbering.Contractual };
        var p3 = new Revision();
        document.Revisions.AddRange(new[] { p1, p2, c1, p3 });
        Assert.Equal(new[] { "P01", "P02", "C01", "P03" }, document.Revisions.Select(r => Revisions.NumberOf(document, r)));

        var numeric = new Revision { Numbering = RevisionNumbering.Numeric };
        document.Revisions.Add(numeric);
        Assert.Equal("1", Revisions.NumberOf(document, numeric));

        // Letters without I and O, which read as one and nought; then two letters.
        Assert.Equal(new[] { "A", "H", "J", "N", "P", "Z", "AA", "AB" }, new[] { 1, 8, 9, 13, 14, 24, 25, 26 }.Select(Revisions.Letters));

        Assert.True(Revisions.TryParseDate("3 Oct 2026", out var date));
        Assert.Equal(new DateTime(2026, 10, 3), date);
        Assert.True(Revisions.TryParseDate("2026-10-03", out date));
        Assert.Equal("3 Oct 2026", Revisions.FormatDate(date));
        Assert.False(Revisions.TryParseDate("Tuesday-ish", out _));
    }

    [Fact]
    public void ASheetListsTheRevisionsCloudedInItsViewsAndThosePutOnIt()
    {
        var document = BimDocument.CreateDefault();
        var (ground, first) = (document.Levels[0], document.Levels[1]);
        var planning = new Revision { Description = "Planning", Date = new DateTime(2026, 9, 1) };
        var tender = new Revision { Description = "Tender", Date = new DateTime(2026, 10, 1) };
        var title = new Revision { Description = "Title corrected", Date = new DateTime(2026, 10, 9) };
        document.Revisions.AddRange(new[] { planning, tender, title });

        Cloud(document, planning, ground.Id, 0, 0);
        Cloud(document, tender, first.Id, 10000, 10000);

        var groundSheet = new Sheet { Number = "A-101", Revision = "-" };
        groundSheet.Add(new Viewport { View = ViewReference.FloorPlan(ground.Id), Centre = new Point2D(200, 200) });
        var firstSheet = new Sheet { Number = "A-102", Revision = "-" };
        firstSheet.Add(new Viewport { View = ViewReference.FloorPlan(first.Id), Centre = new Point2D(200, 200) });
        var blank = new Sheet { Number = "A-103", Revision = "T1" };
        document.Add(groundSheet);
        document.Add(firstSheet);
        document.Add(blank);

        Assert.Equal(new[] { planning }, Revisions.OnSheet(document, groundSheet));
        Assert.Equal(new[] { tender }, Revisions.OnSheet(document, firstSheet));
        Assert.Empty(Revisions.OnSheet(document, blank));

        // Put on by hand, for a change with nothing to cloud: the latest is its current revision.
        groundSheet.RevisionIds.Add(title.Id);
        Assert.Equal(new[] { planning, title }, Revisions.OnSheet(document, groundSheet));
        Assert.Equal("P03", groundSheet.CurrentRevision(document));
        Assert.Equal("T1", blank.CurrentRevision(document));

        var parameters = Schedule.ParametersOf(document, groundSheet);
        Assert.Equal("P03", parameters["Current Revision"].DisplayValue);
        Assert.Equal("9 Oct 2026", parameters["Current Revision Date"].DisplayValue);
        Assert.Equal("Title corrected", parameters["Current Revision Description"].DisplayValue);
        Assert.Equal("P01, P03", parameters["Revisions on Sheet"].DisplayValue);
        Assert.True(parameters["Sheet Issue Date"].TrySetFromText("12 Oct 2026"));
        Assert.Equal(new DateTime(2026, 10, 12), groundSheet.IssuedOn);
        Assert.False(parameters["Sheet Issue Date"].TrySetFromText("soon"));

        // A callout of the ground plan shows the cloud where its box takes it in.
        var callout = new Callout { Parent = ViewReference.FloorPlan(ground.Id), Name = "Callout 1", Region = new BoundingBox2D(-500, -500, 1000, 1000) };
        document.Add(callout);
        var detail = new Sheet { Number = "A-501" };
        detail.Add(new Viewport { View = Callouts.Reference(callout), Centre = new Point2D(200, 200) });
        document.Add(detail);
        Assert.Equal(new[] { planning }, Revisions.OnSheet(document, detail));
        callout.Region = new BoundingBox2D(5000, 5000, 6000, 6000);
        Assert.Empty(Revisions.OnSheet(document, detail));

        // The sheet list: each sheet by its number, its current revision and its date.
        var list = Schedule.Run(document, document.Schedules.Single(s => s.Name == "Sheet List"));
        Assert.Equal(new[] { "A-101", "A-102", "A-103", "A-501" }, list.Rows.Select(row => row.Text("Sheet Number")));
        Assert.Equal(new[] { "P03", "P02", "T1", "P1" }, list.Rows.Select(row => row.Text("Current Revision")));
        Assert.Equal("1 Oct 2026", list.Rows[1].Text("Current Revision Date"));
    }

    [Fact]
    public void ACloudBowsOutwardsRoundWhatItCircles()
    {
        foreach (var clockwise in new[] { false, true })
        {
            var cloud = new RevisionCloud();
            var corners = new[] { new Point2D(0, 0), new Point2D(4000, 0), new Point2D(4000, 2000), new Point2D(0, 2000) };
            cloud.Outline.AddRange(clockwise ? corners.Reverse() : corners);
            var arcs = cloud.Scallops(800);

            // Five along each long side, three - rounded - up each short one.
            Assert.Equal(5 + 3 + 5 + 3, arcs.Count);

            // Every scallop's middle is outside the box it goes round.
            foreach (var arc in arcs)
            {
                var middle = arc[arc.Count / 2];
                Assert.True(middle.X < 0 || middle.X > 4000 || middle.Y < 0 || middle.Y > 2000, $"{middle} is inside");
            }
        }
    }

    [Fact]
    public void AnIssuedRevisionAndItsCloudsAreLocked()
    {
        var document = BimDocument.CreateDefault();
        var revision = new Revision { Issued = true };
        var open = new Revision();
        document.Revisions.AddRange(new[] { revision, open });
        var cloud = Cloud(document, revision, document.Levels[0].Id, 0, 0);
        Assert.True(cloud.IsLocked(document));

        // Its revision cannot be changed from the cloud, and an issued revision is not offered to another.
        var picker = cloud.GetInstanceParameters(document).Single(p => p.Name == "Revision");
        Assert.Equal(new[] { "P01", "P02" }, picker.AllowedValues);
        picker.TrySetFromText("P02");
        Assert.Equal(revision.Id, cloud.RevisionId);

        var other = Cloud(document, open, document.Levels[0].Id, 5000, 0);
        Assert.Equal(new[] { "P02" }, other.GetInstanceParameters(document).Single(p => p.Name == "Revision").AllowedValues);

        // Hidden revisions draw nothing; tags only, their tags.
        open.Visibility = RevisionVisibility.TagsOnly;
        Assert.Equal((false, true), other.Drawn(document));
        open.Visibility = RevisionVisibility.Hidden;
        Assert.Equal((false, false), other.Drawn(document));
    }

    [Fact]
    public void AKeynoteFileIsReadKeyTextAndHeading()
    {
        var text = "# an office's keynotes\n"
                   + "A\tWalls\n"
                   + "A10\tFacing brick\tA\n"
                   + "A20\t\"Render, two coat\"\tA\n"
                   + "no tab here\n"
                   + "A10\tTwice\tA\n"
                   + "B10\tOrphan\tB\n";
        var table = KeynoteTable.Parse(text, out var problems);
        Assert.Equal(new[] { "A", "A10", "A20", "B10" }, table.Entries.Select(e => e.Key));
        Assert.Equal("Render, two coat", table.TextOf("a20"));
        Assert.Equal(new[] { "A10", "A20", "B10" }, table.Notes.Select(e => e.Key));
        Assert.Equal(3, problems.Count);
        Assert.Contains(problems, p => p.Contains("no key"));
        Assert.Contains(problems, p => p.Contains("there already"));
        Assert.Contains(problems, p => p.Contains("B, which is not a key"));

        var again = KeynoteTable.Parse(table.Write(), out var still);
        Assert.Single(still);
        Assert.Equal(table.Entries, again.Entries);

        // The starter set is headed by NRM1's group elements.
        var starter = KeynoteTable.Starter();
        Assert.Equal(new[] { "Substructure", "Superstructure", "Internal finishes" }, new[] { "1", "2", "3" }.Select(starter.TextOf));
        Assert.Equal("External walls", starter.TextOf("2.5"));
        Assert.Equal("2.5", starter.Find("2.5.01")!.Parent);
        Assert.NotEmpty(BimDocument.CreateDefault().Keynotes.Entries);
    }

    [Fact]
    public void AKeynoteReadsItsTypesItsMaterialsOrItsOwnKey()
    {
        var document = BimDocument.CreateDefault();
        var wall = Wall(document, new Point2D(0, 0), new Point2D(6000, 0));
        var type = document.GetWallType(wall)!;
        type.Keynote = "2.5.01";

        var element = new KeynoteTag { Source = KeynoteSource.Element, TargetId = wall.Id, Position = new Point2D(1000, 1500), LevelId = wall.LevelId };
        Assert.Equal("2.5.01", element.KeyIn(document));
        Assert.Equal("2.5.01", element.Label(document));
        element.Display = KeynoteDisplay.KeyAndText;
        Assert.Equal("2.5.01  Facing brick outer leaf, 102.5 mm", element.Label(document));
        element.Display = KeynoteDisplay.Text;
        Assert.Equal("Facing brick outer leaf, 102.5 mm", element.Label(document));

        // The material where it points: the outer leaf on the outside, the inner on the inside.
        var outside = wall.PointAt(type.Structure, 1000, type.Structure.TotalWidth / 2 - 10);
        var inside = wall.PointAt(type.Structure, 1000, -type.Structure.TotalWidth / 2 + 10);
        var outer = Keynotes.MaterialAt(document, wall, outside)!;
        var inner = Keynotes.MaterialAt(document, wall, inside)!;
        Assert.Equal(type.Structure.Layers[0].MaterialId, outer.Id);
        Assert.Equal(type.Structure.Layers[^1].MaterialId, inner.Id);
        outer.Keynote = "2.5.01";
        var material = new KeynoteTag { Source = KeynoteSource.Material, TargetId = wall.Id, MaterialId = outer.Id };
        Assert.Equal("2.5.01", material.KeyIn(document));

        // A key of its own; none says so.
        Assert.Equal("3.2.02", new KeynoteTag { Source = KeynoteSource.User, Key = "3.2.02" }.KeyIn(document));
        Assert.Equal("?", new KeynoteTag { Source = KeynoteSource.User }.Label(document));

        // Copied with its wall, the copy notes the copied wall.
        document.Add(element);
        var copies = ElementCopy.Duplicate(document, new Element[] { wall, element });
        var copiedWall = copies.OfType<Wall>().Single();
        Assert.Equal(copiedWall.Id, copies.OfType<KeynoteTag>().Single().TargetId);
    }

    [Fact]
    public void ALegendListsTheKeysTaggedOnItsSheetOrInTheProject()
    {
        var document = BimDocument.CreateDefault();
        var (ground, first) = (document.Levels[0], document.Levels[1]);
        document.Add(new KeynoteTag { Source = KeynoteSource.User, Key = "3.2.02", LevelId = ground.Id, Position = new Point2D(0, 0) });
        document.Add(new KeynoteTag { Source = KeynoteSource.User, Key = "2.5.01", LevelId = ground.Id, Position = new Point2D(500, 0) });
        document.Add(new KeynoteTag { Source = KeynoteSource.User, Key = "2.5.01", LevelId = ground.Id, Position = new Point2D(900, 0) });
        document.Add(new KeynoteTag { Source = KeynoteSource.User, Key = "5.8.01", LevelId = first.Id, Position = new Point2D(0, 0) });
        document.Add(new KeynoteTag { Source = KeynoteSource.User, Key = "X99", LevelId = first.Id, Position = new Point2D(0, 0) });

        var sheet = new Sheet();
        sheet.Add(new Viewport { View = ViewReference.FloorPlan(ground.Id), Centre = new Point2D(200, 200) });
        var legend = new Viewport { View = ViewReference.KeynoteLegend(bySheet: true), Centre = new Point2D(500, 200) };
        sheet.Add(legend);
        document.Add(sheet);
        Assert.Equal(string.Empty, legend.DetailNumber);

        // In the table's order: on the sheet, those on the ground floor only.
        Assert.Equal(new[] { "2.5.01", "3.2.02" }, Keynotes.Used(document, sheet).Select(e => e.Key));
        Assert.Equal(new[] { "2.5.01", "3.2.02", "5.8.01", "X99" }, Keynotes.Used(document, null).Select(e => e.Key));
        Assert.Equal("(not in the keynote table)", Keynotes.Used(document, null)[^1].Text);

        var table = legend.Table(document)!;
        Assert.Equal(new[] { "Key", "Keynote" }, table.Lines[1].Cells);
        Assert.Equal(2, table.Lines.Count(line => line.Kind == ScheduleLineKind.Row));
        var bounds = legend.PaperBounds(document);
        Assert.Equal(table.Width, bounds.Width, 6);
        Assert.Equal("Keynote Legend (by sheet)", legend.TitleIn(document));
    }

    [Fact]
    public void RevisionsCloudsKeynotesAndTheirTagsAreKept()
    {
        var document = BimDocument.CreateDefault();
        var wall = Wall(document, new Point2D(0, 0), new Point2D(6000, 0));
        var revision = new Revision { Numbering = RevisionNumbering.Alphabetic, Date = new DateTime(2026, 10, 19), Description = "Stage 4", IssuedTo = "Contractor", IssuedBy = "JS", Issued = true, Visibility = RevisionVisibility.TagsOnly };
        document.Revisions.Add(revision);
        var cloud = Cloud(document, revision, document.Levels[0].Id, 100, 200);
        var sheet = new Sheet();
        sheet.RevisionIds.Add(revision.Id);
        sheet.Add(new Viewport { View = ViewReference.KeynoteLegend(bySheet: false), Centre = new Point2D(100, 100) });
        document.Add(sheet);
        document.Keynotes.Entries.Add(new KeynoteEntry("Z1", "Our own", "2"));
        document.Materials.First().Keynote = "Z1";
        document.Add(new KeynoteTag { Source = KeynoteSource.Material, TargetId = wall.Id, MaterialId = document.Materials.First().Id, Position = new Point2D(5, 6), LeaderEnd = new Point2D(7, 8), Display = KeynoteDisplay.KeyAndText, LevelId = wall.LevelId });

        var reopened = ProjectFile.FromJson(ProjectFile.ToJson(document));
        var kept = Assert.Single(reopened.Revisions);
        Assert.Equal((revision.Id, RevisionNumbering.Alphabetic, revision.Date, "Stage 4", "Contractor", "JS", true, RevisionVisibility.TagsOnly),
            (kept.Id, kept.Numbering, kept.Date, kept.Description, kept.IssuedTo, kept.IssuedBy, kept.Issued, kept.Visibility));
        var keptCloud = Assert.Single(reopened.Elements.OfType<RevisionCloud>());
        Assert.Equal(cloud.Outline, keptCloud.Outline);
        Assert.Equal(cloud.TagAt, keptCloud.TagAt);
        Assert.Equal(new[] { revision.Id }, reopened.Elements.OfType<Sheet>().Single().RevisionIds);
        Assert.Equal(ViewKind.KeynoteLegend, reopened.Elements.OfType<Sheet>().Single().Viewports[0].View.Kind);
        Assert.Equal("Our own", reopened.Keynotes.TextOf("Z1"));
        Assert.Equal(document.Keynotes.Entries.Count, reopened.Keynotes.Entries.Count);
        Assert.Equal("Z1", reopened.FindMaterial(document.Materials.First().Id)!.Keynote);
        var tag = Assert.Single(reopened.Elements.OfType<KeynoteTag>());
        Assert.Equal(("Z1", KeynoteDisplay.KeyAndText, new Point2D(7, 8)), (tag.KeyIn(reopened), tag.Display, tag.LeaderEnd!.Value));

        // A file from before keynotes were kept opens with the starter set.
        var node = System.Text.Json.Nodes.JsonNode.Parse(ProjectFile.ToJson(document))!.AsObject();
        Assert.True(node.Remove("keynotes"));
        Assert.Equal(KeynoteTable.Starter().Entries, ProjectFile.FromJson(node.ToJsonString()).Keynotes.Entries);
    }

    [Fact]
    public void CloudsAndKeynotesMoveMirrorAndTurnWithWhatTheyMark()
    {
        var document = BimDocument.CreateDefault();
        var revision = new Revision();
        document.Revisions.Add(revision);
        var cloud = Cloud(document, revision, document.Levels[0].Id, 0, 0);
        var keynote = new KeynoteTag { Source = KeynoteSource.User, Key = "1", Position = new Point2D(100, 100), LeaderEnd = new Point2D(0, 0) };
        Assert.True(ElementTransforms.CanMove(cloud));
        Assert.True(ElementTransforms.CanMove(keynote));

        ElementTransforms.Move(cloud, new Vector2D(1000, 0));
        Assert.Equal(new Point2D(1000, 0), cloud.Outline[0]);
        Assert.Equal(new Point2D(4000, 3000), cloud.TagAt);
        ElementTransforms.Move(keynote, new Vector2D(0, 50));
        Assert.Equal((new Point2D(100, 150), new Point2D(0, 50)), (keynote.Position, keynote.LeaderEnd!.Value));
    }
}

/// <summary>Clouds and keynotes drawn on the plan by clicking, and revisions and keynotes edited in their windows.</summary>
[Collection("Wpf")]
public class RevisionWindowTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

    private static object? Call(MainWindow window, string name, params object?[] arguments) =>
        typeof(MainWindow).GetMethods(Hidden).First(method => method.Name == name && method.GetParameters().Length == arguments.Length).Invoke(window, arguments);

    private static T Field<T>(MainWindow window, string name) => (T)typeof(MainWindow).GetField(name, Hidden)!.GetValue(window)!;

    private static void OnUiThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (Application.Current is null) new App().InitializeComponent();
                action();
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Exception("The window failed on the UI thread.", failure);
    }

    [Fact]
    public void RevisionsAreMadeCloudedIssuedAndPutOnSheets()
    {
        OnUiThread(() =>
        {
            var (document, _, _) = DormerToolTests.House(10000, 8000, 40);
            var window = new MainWindow();
            Call(window, "LoadDocument", document, null, false);
            var history = Field<UndoStack>(window, "_history");
            var plan = Field<PlanView>(window, "Plan");

            // No revision yet: the first cloud makes the first, P01 dated today, as the same step - undone, both go.
            plan.SetTool(PlanTool.RevisionCloud);
            Assert.True(plan.ClickCloud(new Point2D(1000, 1000)));
            Assert.True(plan.ClickCloud(new Point2D(4000, 3000)));
            Assert.True(plan.FinishCloud());
            var first = Assert.Single(document.Revisions);
            Assert.Equal("P01", Revisions.NumberOf(document, first));
            Assert.Equal(DateTime.Today, first.Date);
            Assert.Equal(first.Id, Assert.Single(document.Elements.OfType<RevisionCloud>()).RevisionId);
            history.Undo();
            Assert.Empty(document.Revisions);
            Assert.Empty(document.Elements.OfType<RevisionCloud>());

            // Two made in the window, one step to undo.
            Assert.True((bool)Call(window, "EditRevisions", (Func<RevisionsWindow, bool>)(editor =>
            {
                var planning = editor.Add();
                planning.Description = "Planning";
                var tender = editor.Add();
                tender.Description = "Tender";
                Assert.Null(editor.Move(tender, -1));
                Assert.Equal("P01", Revisions.NumberOf(editor.Revisions, tender));
                Assert.Null(editor.Move(tender, 1));
                return true;
            }))!);
            Assert.Equal(new[] { "Planning", "Tender" }, document.Revisions.Select(r => r.Description));

            // Clouded by two corners: a rectangle, tagged, of the latest open revision.
            plan.SetTool(PlanTool.RevisionCloud);
            Assert.True(plan.ClickCloud(new Point2D(1000, 1000)));
            Assert.True(plan.ClickCloud(new Point2D(4000, 3000)));
            Assert.True(plan.FinishCloud());
            var cloud = Assert.Single(document.Elements.OfType<RevisionCloud>());
            Assert.Equal(4, cloud.Outline.Count);
            Assert.Equal(document.Revisions[1].Id, cloud.RevisionId);
            Assert.NotNull(cloud.TagAt);

            // Issued: the cloud cannot be deleted, nor the revision, and nothing more clouded for it.
            Assert.True((bool)Call(window, "EditRevisions", (Func<RevisionsWindow, bool>)(editor =>
            {
                editor.Revisions[1].Issued = true;
                Assert.Contains("issued", editor.Delete(editor.Revisions[1]));
                var fresh = new RevisionsWindow(document);
                Assert.Contains("cloud", fresh.Delete(fresh.Revisions[1]));
                return true;
            }))!);
            Assert.True(cloud.IsLocked(document));
            // Refused on a plan of its own: the window would say so in a message box.
            var bare = new PlanView { Document = document, ActiveLevelId = document.Levels[0].Id, History = new UndoStack() };
            string? refused = null;
            bare.PinnedRefused += (_, why) => refused = why;
            bare.Select(cloud);
            bare.DeleteSelected();
            Assert.Contains(cloud, document.Elements);
            Assert.Contains("issued revision", refused);
            Assert.Equal(document.Revisions[0], plan.CloudRevision());

            // On a sheet of the ground plan, with a revision put on by hand.
            var sheet = new Sheet { Number = "A-101" };
            sheet.Add(new Viewport { View = ViewReference.FloorPlan(document.Levels[0].Id), Centre = new Point2D(300, 300) });
            document.Add(sheet);
            Assert.Equal("P02", sheet.CurrentRevision(document));
            Assert.True((bool)Call(window, "RevisionsOnSheet", sheet, (Func<RevisionsOnSheetWindow, bool>)(picker =>
            {
                picker.Tick(document.Revisions[0].Id, true);
                return true;
            }))!);
            Assert.Equal(new[] { document.Revisions[0].Id }, sheet.RevisionIds);
            history.Undo();
            Assert.Empty(sheet.RevisionIds);
        });
    }

    [Fact]
    public void KeynotesAreLoadedGivenToMaterialsAndPlacedByClicking()
    {
        OnUiThread(() =>
        {
            var (document, _, _) = DormerToolTests.House(10000, 8000, 40);
            var window = new MainWindow();
            Call(window, "LoadDocument", document, null, false);
            var history = Field<UndoStack>(window, "_history");
            var plan = Field<PlanView>(window, "Plan");
            var wall = document.Walls.First();
            var type = document.GetWallType(wall)!;
            var brick = type.Structure.Layers[0].MaterialId;

            // The template's brick starts with the starter table's key for a facing brick leaf.
            var starterKey = document.FindMaterial(brick)!.Keynote;
            Assert.Equal("2.5.01", starterKey);

            var file = Path.Combine(Path.GetTempPath(), $"keynotes-{Guid.NewGuid():N}.txt");
            File.WriteAllText(file, "W\tWalls\nW1\tBrick outer leaf\tW\nW2\tBlock inner leaf\tW\n");
            try
            {
                Assert.True((bool)Call(window, "EditKeynotes", (Func<KeynotesWindow, bool>)(editor =>
                {
                    Assert.Null(editor.Load(file));
                    editor.SetMaterialKeynote(brick, "W1");
                    editor.Entries.Add(new KeynotesWindow.EntryRow { Key = "W1", Text = "Twice" });
                    Assert.Contains("twice", editor.Problems());
                    editor.Entries.RemoveAt(editor.Entries.Count - 1);
                    Assert.Null(editor.Problems());
                    return true;
                }))!);
            }
            finally { File.Delete(file); }

            Assert.Equal(new[] { "W", "W1", "W2" }, document.Keynotes.Entries.Select(e => e.Key));
            Assert.Equal(file, document.Keynotes.Source);
            Assert.Equal("W1", document.FindMaterial(brick)!.Keynote);

            // A material keynote clicked on the wall's outside, its box placed beside it.
            plan.SetTool(PlanTool.Keynote);
            plan.ActiveKeynoteSource = KeynoteSource.Material;
            var outside = wall.PointAt(type.Structure, wall.Length / 2, type.Structure.TotalWidth / 2 - 20);
            Assert.True(plan.ClickKeynote(outside));
            Assert.True(plan.ClickKeynote(outside + wall.ExteriorNormal * 1500));
            var keynote = Assert.Single(document.Elements.OfType<KeynoteTag>());
            Assert.Equal("W1", keynote.KeyIn(document));

            // Undone, the table is as it was.
            history.Undo();
            Assert.Empty(document.Elements.OfType<KeynoteTag>());
            history.Undo();
            Assert.Equal(KeynoteTable.Starter().Entries, document.Keynotes.Entries);
            Assert.Equal(starterKey, document.FindMaterial(brick)!.Keynote);
        });
    }
}
