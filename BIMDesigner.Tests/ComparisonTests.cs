using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Coordination;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Infrastructure.Interoperability;
using BIMDesigner.Infrastructure.Serialization;

namespace BIMDesigner.Tests;

/// <summary>
/// Two issues of a model compared: what was added, removed, moved or changed, matched by id - nothing
/// at all when a project is set against its own saved copy - each change said setting by setting, its
/// type's changes carried to it; two IFC files of one model matched by their GlobalIds; and a linked
/// model's reload set against the issue it replaced.
/// </summary>
[Collection("Wpf")]
public class ComparisonTests
{
    public ComparisonTests() => ModelLinks.Reader = LinkFiles.Read;

    /// <summary>A house of four walls, a partition, a door, a window, two rooms and a floor.</summary>
    private static BimDocument House()
    {
        var document = BimDocument.CreateDefault();
        var level = document.Levels[0].Id;
        var exterior = document.TypesOf<WallType>().First(type => type.Function == WallFunction.Exterior);
        var interior = document.TypesOf<WallType>().First(type => type.Function == WallFunction.Interior);
        var corners = new[] { new Point2D(0, 0), new Point2D(10000, 0), new Point2D(10000, 8000), new Point2D(0, 8000) };
        var walls = new List<Wall>();
        for (var i = 0; i < 4; i++)
        {
            var wall = new Wall { Start = corners[i], End = corners[(i + 1) % 4], TypeId = exterior.Id, LevelId = level, UnconnectedHeight = 3000 };
            walls.Add(wall);
            document.Add(wall);
        }

        document.Add(new Wall { Start = new Point2D(5000, 0), End = new Point2D(5000, 8000), TypeId = interior.Id, LevelId = level, UnconnectedHeight = 3000 });
        document.Add(new Door { TypeId = document.TypesOf<DoorType>().First(type => type.Function == DoorFunction.Exterior).Id, LevelId = level, HostWallId = walls[0].Id, DistanceAlongWall = 2500, Mark = "D1" });
        document.Add(new Window { TypeId = document.TypesOf<WindowType>().First().Id, LevelId = level, HostWallId = walls[2].Id, DistanceAlongWall = 3000, Mark = "W1" });
        document.Add(new Room { LevelId = level, Location = new Point2D(2500, 4000), Name = "Living", Number = "1" });
        document.Add(new Room { LevelId = level, Location = new Point2D(7500, 4000), Name = "Kitchen", Number = "2" });
        var floor = new Floor { TypeId = document.TypesOf<FloorType>().First().Id, LevelId = level };
        floor.SetBoundary(corners);
        document.Add(floor);
        return document;
    }

    private static string Folder()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"compare-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        return folder;
    }

    [Fact]
    public void AProjectSetAgainstItsOwnSavedCopyHasNothingChanged()
    {
        var folder = Folder();
        try
        {
            var house = House();
            var path = Path.Combine(folder, "house.bimx");
            ProjectFile.Save(house, path);
            var saved = ProjectFile.Load(path);
            var comparison = Comparisons.Compare(saved, house, "house.bimx", "This project", DateTime.Now);
            Assert.True(comparison.Same, string.Join(Environment.NewLine, comparison.Elements.Concat(comparison.Types).Concat(comparison.Levels).Select(change => $"{change.Kind} {change.Name}: {change.Summary}")));
            Assert.Equal(house.Elements.Count(Comparisons.Compared), comparison.Unchanged);
            Assert.Equal("Nothing has changed", comparison.Said());
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void WhatWasAddedRemovedMovedAndChangedIsFoundAndSaid()
    {
        var before = House();
        var after = ProjectFile.Load(Saved(before, out var folder));
        try
        {
            var level = after.Levels[0].Id;
            var partition = after.All<Wall>().Single(wall => wall.Start.X == 5000);
            partition.Start += new Vector2D(600, 0);
            partition.End += new Vector2D(600, 0);
            var door = after.All<Door>().Single();
            after.Remove(door);
            var window = new Window { TypeId = after.TypesOf<WindowType>().First().Id, LevelId = level, HostWallId = after.All<Wall>().First().Id, DistanceAlongWall = 7000, Mark = "W2" };
            after.Add(window);
            var south = after.All<Wall>().First();
            south.UnconnectedHeight = 3300;

            var comparison = Comparisons.Compare(before, after, "Before", "After", DateTime.Now);

            var added = Assert.Single(comparison.Elements, change => change.Kind == ChangeKind.Added);
            Assert.Equal(window.Id, added.Id);
            var removed = Assert.Single(comparison.Elements, change => change.Kind == ChangeKind.Removed);
            Assert.Equal(door.Id, removed.Id);
            Assert.Contains("(D1)", removed.Name);

            // Moved, alike at both ends: how far and which way.
            var moved = comparison.Of(partition.Id)!;
            Assert.Equal(ChangeKind.Moved, moved.Kind);
            Assert.Equal("Moved 600 mm east", moved.Movement);

            // Changed: the setting as Properties reads it, before and after.
            var raised = comparison.Of(south.Id)!;
            Assert.Equal(ChangeKind.Changed, raised.Kind);
            Assert.Contains(raised.Settings, setting => setting.Name == "Unconnected Height" && setting.Before == "3.00 m" && setting.After == "3.30 m");

            // Nothing else: the rooms either side of the partition are larger and smaller, so changed; the rest as they were.
            Assert.All(comparison.Elements.Where(change => change.Kind == ChangeKind.Changed && change.Id != south.Id), change => Assert.Equal("Rooms", change.Category));
            Assert.Equal("1 added, 1 removed, 3 changed, 1 moved", comparison.Said());
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void AnElementsTypeChangedIsSaidOnItAndAmongTheTypes()
    {
        var before = House();
        var after = ProjectFile.Load(Saved(before, out var folder));
        try
        {
            var partition = after.All<Wall>().Single(wall => wall.Start.X == 5000);
            var type = after.FindType<WallType>(partition.TypeId)!;
            type.Cost = 99m;

            var comparison = Comparisons.Compare(before, after, "Before", "After", DateTime.Now);
            var changed = Assert.Single(comparison.Types);
            Assert.Equal(type.Name, changed.Name);
            Assert.Contains(changed.Settings, setting => setting.Name == "Cost" && setting.Before == "85.00" && setting.After == "99.00");
            var wall = comparison.Of(partition.Id)!;
            Assert.Equal(ChangeKind.Changed, wall.Kind);
            Assert.StartsWith($"Its type, {type.Name}, changed", wall.TypeChange);

            // Another type given it instead: said as its Type, before and after.
            var other = after.TypesOf<WallType>().First(each => each.Id != type.Id && each.Function == WallFunction.Interior);
            partition.TypeId = other.Id;
            var retyped = Comparisons.Compare(before, after, "Before", "After", DateTime.Now).Of(partition.Id)!;
            Assert.Contains(retyped.Settings, setting => setting.After == other.Name && setting.Before == type.Name);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void TwoIfcFilesOfOneModelAreMatchedByTheirGlobalIds()
    {
        var folder = Folder();
        try
        {
            var house = House();
            var first = Path.Combine(folder, "first.ifc");
            IfcExport.Save(house, first);
            var partition = house.All<Wall>().Single(wall => wall.Start.X == 5000);
            partition.End += new Vector2D(-400, 0);
            var second = Path.Combine(folder, "second.ifc");
            IfcExport.Save(house, second);

            var a = IfcImport.Open(first).Document;
            var b = IfcImport.Open(second).Document;
            var comparison = Comparisons.Compare(a, b, "first.ifc", "second.ifc", DateTime.Now);
            Assert.DoesNotContain(comparison.Elements, change => change.Kind is ChangeKind.Added or ChangeKind.Removed);
            Assert.Contains(comparison.Elements, change => change.Kind == ChangeKind.Changed && change.Category == "Walls" && change.Movement!.StartsWith("Reshaped"));
            Assert.Empty(comparison.Types);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void ALinkedModelReloadedIsSetAgainstTheIssueItReplaced()
    {
        var folder = Folder();
        try
        {
            var structure = House();
            var path = Path.Combine(folder, "structure.bimx");
            ProjectFile.Save(structure, path);
            var host = BimDocument.CreateDefault();
            var link = new ModelLink { Path = path };
            host.Add(link);
            ModelLinks.Load(link);
            Assert.Null(ModelLinks.SinceLastIssue(link));

            // Read again as it was: there is still no issue before it.
            ModelLinks.Load(link);
            Assert.Null(ModelLinks.SinceLastIssue(link));

            // The engineer's next issue: a column of walls more.
            structure.Add(new Wall { Start = new Point2D(0, 12000), End = new Point2D(4000, 12000), TypeId = structure.All<Wall>().First().TypeId, LevelId = structure.Levels[0].Id, UnconnectedHeight = 3000 });
            ProjectFile.Save(structure, path);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(5));
            ModelLinks.Load(link);
            var comparison = ModelLinks.SinceLastIssue(link)!;
            Assert.Equal("1 added", comparison.Said());
            Assert.NotNull(ModelLinks.PreviousOf(link));

            // Its meshes in this project carry their own elements' ids, to be coloured by what changed.
            Assert.Contains(ModelLinks.Meshes(host, link), mesh => mesh.SourceId == comparison.Elements[0].Id);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void AComparisonIsWrittenUpToSendOn()
    {
        var before = House();
        var after = ProjectFile.Load(Saved(before, out var folder));
        try
        {
            after.Remove(after.All<Door>().Single());
            after.All<Wall>().First().UnconnectedHeight = 3300;
            var comparison = Comparisons.Compare(before, after, "house.bimx, saved 1 Oct 2026 09:00", "This project as it is now", new DateTime(2026, 10, 29, 12, 0, 0));
            var report = UI.Export.ComparisonReport.Build(comparison, "Plot 7", "1.0", comparison.At);
            var words = string.Concat(report.Drawings().Select(Words));
            foreach (var expected in new[] { "Model Comparison", "Plot 7", "house.bimx", "Removed (1)", "(D1)", "Changed", "Unconnected Height: 3.00 m → 3.30 m" })
                Assert.Contains(Bare(expected), words);
            Assert.Equal("Plot 7 - Comparison 2026-10-29.pdf", UI.Export.ComparisonReport.SuggestedFileName("Plot 7", comparison.At));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static string Bare(string text) => System.Text.RegularExpressions.Regex.Replace(text, @"\s+", string.Empty);

    /// <summary>The words a page's drawing writes, run together without their spaces.</summary>
    private static string Words(System.Windows.Media.Drawing drawing)
    {
        var said = new System.Text.StringBuilder();
        void Walk(System.Windows.Media.Drawing each)
        {
            switch (each)
            {
                case System.Windows.Media.DrawingGroup group:
                    foreach (var child in group.Children) Walk(child);
                    break;
                case System.Windows.Media.GlyphRunDrawing glyphs when glyphs.GlyphRun?.Characters is { } characters:
                    said.Append(characters.ToArray()).Append(' ');
                    break;
            }
        }

        Walk(drawing);
        return Bare(said.ToString());
    }

    private static string Saved(BimDocument document, out string folder)
    {
        folder = Folder();
        var path = Path.Combine(folder, "issue.bimx");
        ProjectFile.Save(document, path);
        return path;
    }
}
