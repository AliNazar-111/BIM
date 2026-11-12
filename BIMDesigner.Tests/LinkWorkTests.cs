using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Coordination;
using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Schedules;
using BIMDesigner.Core.Sheets;
using BIMDesigner.Core.Views;
using BIMDesigner.Infrastructure.Interoperability;
using BIMDesigner.Infrastructure.Serialization;
using Xbim.Ifc4.Interfaces;

namespace BIMDesigner.Tests;

/// <summary>
/// Linked models put to work: levels and gridlines copied from one and following it, changes to them
/// reviewed; links cut in sections, seen in elevations, scheduled, standing round the daylight, moved by
/// their offset, written beside an IFC export; and the links inside a linked model that come with it.
/// </summary>
[Collection("Wpf")]
public class LinkWorkTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"links-{Guid.NewGuid():N}");

    public LinkWorkTests()
    {
        ModelLinks.Reader = LinkFiles.Read;
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    /// <summary>The structure's model: two storeys at 0 and 3500, gridlines A and 1, and a box of walls 6 by 4 m.</summary>
    private static BimDocument Structure()
    {
        var document = BimDocument.CreateDefault();
        document.Levels[1].Elevation = 3500;
        document.Add(new Grid { Name = "A", Start = new Point2D(0, -1000), End = new Point2D(0, 5000) });
        document.Add(new Grid { Name = "1", Start = new Point2D(-1000, 0), End = new Point2D(7000, 0) });
        var type = document.TypesOf<WallType>().First(each => each.Function == WallFunction.Exterior);
        var corners = new[] { new Point2D(0, 0), new Point2D(6000, 0), new Point2D(6000, 4000), new Point2D(0, 4000) };
        for (var i = 0; i < 4; i++)
            document.Add(new Wall { Start = corners[i], End = corners[(i + 1) % 4], TypeId = type.Id, LevelId = document.Levels[0].Id, UnconnectedHeight = 3000 });
        return document;
    }

    private (BimDocument Host, ModelLink Link, string File) Linked(BimDocument linked, Vector2D offset = default, string name = "structure.bimx")
    {
        var file = Path.Combine(_folder, name);
        ProjectFile.Save(linked, file);
        var host = BimDocument.CreateDefault();
        var link = new ModelLink { Path = file, Offset = offset };
        host.Add(link);
        ModelLinks.Load(link);
        return (host, link, file);
    }

    [Fact]
    public void LevelsAndGridsCopiedFollowTheirOriginalsAndChangesAreMatchedAcceptedOrLetGo()
    {
        var structure = Structure();
        var (host, link, file) = Linked(structure, new Vector2D(1000, 0));

        var (copy, said) = CopyMonitor.Copy(host, link);
        Assert.NotNull(copy);
        var history = new UndoStack();
        history.Execute(copy!);

        // The ground floor at 0 is paired with this project's; 3500 is copied in; both gridlines, a metre east.
        Assert.Contains("paired Ground Floor", said);
        Assert.Equal(new[] { 0.0, 3000.0, 3500.0 }, host.Levels.Select(level => level.Elevation).OrderBy(e => e));
        var copiedLevel = host.Levels.Single(level => level.Elevation == 3500);
        Assert.Equal("First Floor (2)", copiedLevel.Name);
        Assert.NotNull(host.Levels.Single(level => level.Elevation == 0).Monitor);
        var gridA = host.All<Grid>().Single(grid => grid.Name == "A");
        Assert.Equal(new Point2D(1000, -1000), gridA.Start);
        Assert.Empty(CopyMonitor.Review(host));

        // Copied again, nothing more; undone, all of it.
        Assert.Null(CopyMonitor.Copy(host, link).Command);
        Assert.True(ProjectFile.FromJson(ProjectFile.ToJson(host)).All<Grid>().All(grid => grid.Monitor is { SourceName.Length: > 0 }));

        // The structure issued again: its first floor 100 higher, gridline A 500 east, gridline 1 gone.
        structure.Levels.Single(level => level.Elevation == 3500).Elevation = 3600;
        var sourceA = structure.All<Grid>().Single(grid => grid.Name == "A");
        (sourceA.Start, sourceA.End) = (sourceA.Start + new Vector2D(500, 0), sourceA.End + new Vector2D(500, 0));
        structure.Remove(structure.All<Grid>().Single(grid => grid.Name == "1"));
        ProjectFile.Save(structure, file);
        ModelLinks.Load(link);

        var findings = CopyMonitor.Review(host);
        Assert.Equal(3, findings.Count);
        var level = Assert.Single(findings, finding => finding.Datum is Level);
        Assert.Equal(MonitorChange.Moved, level.Change);
        Assert.Contains("moved up 100", level.Said.Replace(" mm", string.Empty));
        Assert.Contains(findings, finding => finding.Change == MonitorChange.Gone && finding.Name == "Grid 1");
        Assert.Contains(Warnings.Of(host), warning => warning.Heading == "Linked levels and grids changed");

        // The level moved to match; the gridline's move accepted, this project's left where it was; the gone one let go.
        history.Execute(CopyMonitor.Match(host, level)!);
        Assert.Equal(3600, copiedLevel.Elevation, 6);
        history.Execute(CopyMonitor.Accept(host, findings.Single(finding => finding.Name == "Grid A"))!);
        Assert.Equal(new Point2D(1000, -1000), gridA.Start);
        history.Execute(CopyMonitor.Stop(findings.Single(finding => finding.Change == MonitorChange.Gone)));
        Assert.Empty(CopyMonitor.Review(host));

        history.Undo();
        Assert.Single(CopyMonitor.Review(host));
    }

    /// <summary>
    /// The structure with a steel column C1 at (3000, 2000) from the ground floor to the first, and its walls of
    /// an engineer's blockwork type of their own - of a material this project has not got - marked W1 to W4.
    /// </summary>
    private static BimDocument Framed()
    {
        var document = Structure();
        var blockwork = new Core.Materials.Material("Blockwork, Engineer's 7N") { Density = 2000 };
        document.AddMaterial(blockwork);
        var type = document.TypesOf<WallType>().First(each => each.Function == WallFunction.Exterior).Duplicate("Engineer's Blockwork 215");
        type.Structure.ReplaceLayers(new[] { new Core.Materials.MaterialLayer(Core.Materials.LayerFunction.Structure, blockwork.Id, 215) });
        document.AddType(type);
        var mark = 0;
        foreach (var wall in document.Walls)
            (wall.TypeId, wall.Mark) = (type.Id, $"W{++mark}");
        var steel = document.TypesOf<ColumnType>().First(each => each.Structural && each.Shape == ColumnShape.IBeam);
        document.Add(new Column { Structural = true, TypeId = steel.Id, Location = new Point2D(3000, 2000), LevelId = document.Levels[0].Id, TopLevelId = document.Levels[1].Id, Mark = "C1" });
        return document;
    }

    [Fact]
    public void ColumnsAndWallsCopiedFollowTheirOriginalsAndTheirMovesHeightsAndTypesAreMatched()
    {
        // Linked turned a quarter and a metre east: (x, y) there is (1000 - y, x) here.
        var structure = Framed();
        var file = Path.Combine(_folder, "framed.bimx");
        ProjectFile.Save(structure, file);
        var host = BimDocument.CreateDefault();
        var link = new ModelLink { Path = file, Offset = new Vector2D(1000, 0), Rotation = 90 };
        host.Add(link);
        ModelLinks.Load(link);

        var (copy, said) = CopyMonitor.Copy(host, link, kinds: CopyMonitorKinds.Levels | CopyMonitorKinds.Columns | CopyMonitorKinds.Walls);
        var history = new UndoStack();
        history.Execute(copy!);
        Assert.Contains("1 column", said);
        Assert.Contains("4 walls", said);
        Assert.Contains("Engineer's Blockwork 215", said);
        Assert.Empty(host.All<Grid>());

        // The column where the link puts it, turned with it, from the ground floor to the first floor copied at 3500.
        var column = host.All<Column>().Single();
        Assert.Equal((new Point2D(-1000, 3000), 90.0, "C1"), (new Point2D(Math.Round(column.Location.X, 6), Math.Round(column.Location.Y, 6)), Math.Round(column.Rotation, 6), column.Mark));
        Assert.True(column.Structural);
        Assert.Equal(structure.All<Column>().Single().GetBaseElevation(structure), column.GetBaseElevation(host), 6);
        Assert.Equal(3500.0, Math.Round(column.GetTopElevation(host) - host.FindType<ColumnType>(column.TypeId)!.OffsetTop, 6));
        Assert.Equal(host.Levels.Single(level => level.Elevation == 3500).Id, column.TopLevelId);

        // The walls of the engineer's type, brought in with its blockwork; 3 m high, as drawn there.
        var walls = host.Walls.OrderBy(wall => wall.Mark).ToList();
        Assert.Equal(4, walls.Count);
        var type = host.FindType<WallType>(walls[0].TypeId)!;
        Assert.Equal("Engineer's Blockwork 215", type.Name);
        Assert.Equal("Blockwork, Engineer's 7N", host.FindMaterial(type.Structure.Layers[0].MaterialId)!.Name);
        Assert.Equal((new Point2D(1000, 0), new Point2D(1000, 6000)), (Rounded(walls[0].Start), Rounded(walls[0].End)));
        Assert.Equal(3000, walls[0].GetTopElevation(host) - walls[0].GetBaseElevation(host), 6);
        Assert.Empty(CopyMonitor.Review(host));
        Assert.Null(CopyMonitor.Copy(host, link, kinds: CopyMonitorKinds.Columns | CopyMonitorKinds.Walls).Command);
        Assert.All(ProjectFile.FromJson(ProjectFile.ToJson(host)).Walls, wall => Assert.Equal("Engineer's Blockwork 215", wall.Monitor?.TypeName));

        // Issued again: the column 500 east, W2 300 higher, W3 a partition, W4 gone.
        var source = structure.All<Column>().Single();
        source.Location += new Vector2D(500, 0);
        var sourceWalls = structure.Walls.OrderBy(wall => wall.Mark).ToList();
        sourceWalls[1].UnconnectedHeight = 3300;
        var partition = structure.TypesOf<WallType>().First(each => each.Name == "Interior - Partition 125mm");
        sourceWalls[2].TypeId = partition.Id;
        structure.Remove(sourceWalls[3]);
        ProjectFile.Save(structure, file);
        ModelLinks.Load(link);

        var findings = CopyMonitor.Review(host);
        Assert.Equal(new[] { MonitorChange.Moved, MonitorChange.Resized, MonitorChange.Retyped, MonitorChange.Gone }.OrderBy(change => change), findings.Select(finding => finding.Change).OrderBy(change => change));
        var moved = findings.Single(finding => finding.Change == MonitorChange.Moved);
        Assert.Equal("Column C1", moved.Name);
        Assert.Contains("moved 500", moved.Said.Replace(" mm", string.Empty));
        Assert.Contains("top 300 higher", findings.Single(finding => finding.Change == MonitorChange.Resized).Said.Replace(" mm", string.Empty));
        Assert.Contains(Warnings.Of(host), warning => warning.Heading == "Linked elements changed");

        // Each matched: the column to where it now is, W2 to its new top, W3 to this project's partition; W4 let go.
        foreach (var finding in findings.Where(finding => finding.Change != MonitorChange.Gone)) history.Execute(CopyMonitor.Match(host, finding)!);
        history.Execute(CopyMonitor.Stop(findings.Single(finding => finding.Change == MonitorChange.Gone)));
        Assert.Equal(new Point2D(-1000, 3500), Rounded(column.Location));
        Assert.Equal(3300, walls[1].GetTopElevation(host) - walls[1].GetBaseElevation(host), 6);
        Assert.Equal("Interior - Partition 125mm", host.FindType<WallType>(walls[2].TypeId)!.Name);
        Assert.Null(walls[3].Monitor);
        Assert.Empty(CopyMonitor.Review(host));

        // Undone, the partition back to blockwork and its change to review again.
        history.Undo();
        history.Undo();
        Assert.Equal("Engineer's Blockwork 215", host.FindType<WallType>(walls[2].TypeId)!.Name);
        Assert.Equal(2, CopyMonitor.Review(host).Count);
    }

    private static Point2D Rounded(Point2D point) => new(Math.Round(point.X, 6), Math.Round(point.Y, 6));

    /// <summary>The framed structure with a ground floor slab F1 over its box, a door D1 1500 along W1, and a window WN1 2000 along W2 with a 900 sill.</summary>
    private static BimDocument Fitted(double doorAlong = 1500, double windowSill = 900, double corner = 4000, int doorWall = 0)
    {
        var document = Framed();
        var floor = new Floor { LevelId = document.Levels[0].Id, TypeId = document.TypesOf<FloorType>().First().Id, Mark = "F1" };
        floor.SetBoundary(new[] { new Point2D(0, 0), new Point2D(6000, 0), new Point2D(6000, corner), new Point2D(0, 4000) });
        document.Add(floor);
        var walls = document.Walls.OrderBy(wall => wall.Mark).ToList();
        document.Add(new Door { TypeId = document.TypesOf<DoorType>().First().Id, LevelId = document.Levels[0].Id, HostWallId = walls[doorWall].Id, DistanceAlongWall = doorAlong, Mark = "D1" });
        document.Add(new Window { TypeId = document.TypesOf<WindowType>().First().Id, LevelId = document.Levels[0].Id, HostWallId = walls[1].Id, DistanceAlongWall = 2000, SillHeight = windowSill, Mark = "WN1" });
        return document;
    }

    [Fact]
    public void FloorsDoorsAndWindowsCopiedFollowTheirOriginals()
    {
        // Linked turned a quarter and a metre east: (x, y) there is (1000 - y, x) here.
        var file = Path.Combine(_folder, "fitted.bimx");
        ProjectFile.Save(Fitted(), file);
        var host = BimDocument.CreateDefault();
        var link = new ModelLink { Path = file, Offset = new Vector2D(1000, 0), Rotation = 90 };
        host.Add(link);
        ModelLinks.Load(link);

        var (copy, said) = CopyMonitor.Copy(host, link, kinds: CopyMonitorKinds.Walls | CopyMonitorKinds.Floors | CopyMonitorKinds.Openings);
        var history = new UndoStack();
        history.Execute(copy!);
        Assert.Contains("1 floor", said);
        Assert.Contains("2 doors and windows", said);

        // The floor's outline where the link puts it, at the ground floor; the door 1500 along the copy of W1, which runs from (1000, 0) the same way.
        var floor = host.All<Floor>().Single();
        Assert.Equal(new[] { new Point2D(1000, 0), new Point2D(1000, 6000), new Point2D(-3000, 6000), new Point2D(-3000, 0) }, floor.Boundary.Select(Rounded));
        Assert.Equal((host.Levels[0].Id, 0.0, "F1"), (floor.LevelId, Math.Round(floor.HeightOffset, 6), floor.Mark));
        var w1 = host.Walls.Single(wall => wall.Mark == "W1");
        var door = host.All<Door>().Single();
        Assert.Equal((w1.Id, 1500.0, "D1"), (door.HostWallId, Math.Round(door.DistanceAlongWall, 6), door.Mark));
        Assert.Equal(new Point2D(1000, 1500), Rounded(door.GetCentre(w1)));
        var window = host.All<Window>().Single();
        Assert.Equal((host.Walls.Single(wall => wall.Mark == "W2").Id, 900.0), (window.HostWallId, window.SillHeight));
        Assert.Empty(CopyMonitor.Review(host));
        var back = ProjectFile.FromJson(ProjectFile.ToJson(host));
        Assert.Equal(4, back.All<Floor>().Single().Monitor!.Outline!.Count);
        Assert.NotNull(back.All<Door>().Single().Monitor);

        // Issued again, made afresh: the door 600 further along, the window's sill 150 higher, a corner of the floor a metre out.
        ProjectFile.Save(Fitted(doorAlong: 2100, windowSill: 1050, corner: 5000), file);
        ModelLinks.Load(link);
        var findings = CopyMonitor.Review(host);
        Assert.Equal(3, findings.Count);
        Assert.Contains("moved 600", findings.Single(finding => finding.Datum is Door).Said.Replace(" mm", string.Empty));
        Assert.Contains("sill 150 higher", findings.Single(finding => finding.Datum is Window).Said.Replace(" mm", string.Empty));
        Assert.Contains("changed its outline", findings.Single(finding => finding.Datum is Floor).Said);
        Assert.Contains(Warnings.Of(host), warning => warning.Heading == "Linked elements changed");

        // Each matched: the door 2100 along, the window's sill 1050, the floor's corner where it now is.
        foreach (var finding in findings) history.Execute(CopyMonitor.Match(host, finding)!);
        Assert.Equal(2100, door.DistanceAlongWall, 6);
        Assert.Equal(1050, window.SillHeight, 6);
        Assert.Contains(new Point2D(-4000, 6000), floor.Boundary.Select(Rounded));
        Assert.Empty(CopyMonitor.Review(host));
        history.Undo();
        Assert.Single(CopyMonitor.Review(host));
    }

    /// <summary>The fitted structure with a ceiling CL1 at 2400 over its box, a basin B1 at (1000, 1000), a wall light L1 on W1 at 2000, and a desk.</summary>
    private static BimDocument Serviced(double basinX = 1000, double ceiling = 2400)
    {
        var document = Fitted();
        var level = document.Levels[0].Id;
        var over = new Ceiling { LevelId = level, TypeId = document.TypesOf<CeilingType>().First().Id, HeightOffset = ceiling, Mark = "CL1" };
        over.SetBoundary(new[] { new Point2D(0, 0), new Point2D(6000, 0), new Point2D(6000, 4000), new Point2D(0, 4000) });
        document.Add(over);
        var basin = document.TypesOf<ComponentType>().First(type => type.Kind == BuiltInCategory.PlumbingFixtures);
        var light = document.TypesOf<ComponentType>().First(type => type.Kind == BuiltInCategory.LightingFixtures);
        var desk = document.TypesOf<ComponentType>().First(type => type.Kind == BuiltInCategory.Furniture);
        var w1 = document.Walls.Single(wall => wall.Mark == "W1");
        document.Add(new Component { TypeId = basin.Id, TypeKind = basin.Kind, LevelId = level, Location = new Point2D(basinX, 1000), Mark = "B1" });
        document.Add(new Component { TypeId = light.Id, TypeKind = light.Kind, LevelId = level, Location = new Point2D(3000, 0), Elevation = 2000, HostId = w1.Id, Mark = "L1" });
        document.Add(new Component { TypeId = desk.Id, TypeKind = desk.Kind, LevelId = level, Location = new Point2D(4000, 2000), Mark = "DK1" });
        document.Add(new Component { TypeId = light.Id, TypeKind = light.Kind, LevelId = level, Location = new Point2D(2000, 2000), HostId = over.Id, FlipFacing = true, Mark = "DL1" });
        return document;
    }

    [Fact]
    public void CeilingsAndFixturesCopiedFollowTheirOriginals()
    {
        // Linked turned a quarter and a metre east: (x, y) there is (1000 - y, x) here.
        var file = Path.Combine(_folder, "serviced.bimx");
        ProjectFile.Save(Serviced(), file);
        var host = BimDocument.CreateDefault();
        var link = new ModelLink { Path = file, Offset = new Vector2D(1000, 0), Rotation = 90 };
        host.Add(link);
        ModelLinks.Load(link);

        var (copy, said) = CopyMonitor.Copy(host, link, kinds: CopyMonitorKinds.Walls | CopyMonitorKinds.Ceilings | CopyMonitorKinds.Fixtures);
        var history = new UndoStack();
        history.Execute(copy!);
        Assert.Contains("1 ceiling", said);
        Assert.Contains("3 fixtures", said);

        // The ceiling's outline where the link puts it, 2400 over the ground floor; the basin turned a quarter at (0, 1000); the light on the copy of W1; no desk.
        var ceiling = host.All<Ceiling>().Single();
        Assert.Equal((host.Levels[0].Id, 2400.0), (ceiling.LevelId, Math.Round(ceiling.HeightOffset, 6)));
        Assert.Contains(new Point2D(-3000, 6000), ceiling.Boundary.Select(Rounded));
        var basin = host.All<Component>().Single(component => component.Mark == "B1");
        Assert.Equal((new Point2D(0, 1000), 90.0, 0.0), (Rounded(basin.Location), Math.Round(basin.Rotation, 6), Math.Round(basin.Elevation, 6)));
        var light = host.All<Component>().Single(component => component.Mark == "L1");
        Assert.Equal((host.Walls.Single(wall => wall.Mark == "W1").Id, 2000.0), (light.HostId, Math.Round(light.Elevation, 6)));
        Assert.DoesNotContain(host.All<Component>(), component => component.Mark == "DK1");

        // The downlight fixed under the ceiling here, its foot where it was there.
        var downlight = host.All<Component>().Single(component => component.Mark == "DL1");
        Assert.Equal((ceiling.Id, true), (downlight.HostId, downlight.FlipFacing));
        Assert.Equal(downlight.Monitor!.Bottom, ComponentHosting.BaseElevation(host, downlight, host.FindType<ComponentType>(downlight.TypeId)!), 6);
        Assert.Empty(CopyMonitor.Review(host));
        Assert.NotNull(ProjectFile.FromJson(ProjectFile.ToJson(host)).All<Component>().Single(component => component.Mark == "B1").Monitor);

        // Issued again, made afresh: the basin 300 along, the ceiling 200 higher - and the downlight under it with it. Matched, each as it now is:
        // the ceiling first, as the review lists it, carrying the downlight up, so the downlight's own is nothing more.
        ProjectFile.Save(Serviced(basinX: 1300, ceiling: 2600), file);
        ModelLinks.Load(link);
        var findings = CopyMonitor.Review(host);
        Assert.Equal(3, findings.Count);
        Assert.Contains("moved 300", findings.Single(finding => finding.Datum == basin).Said.Replace(" mm", string.Empty));
        Assert.Contains("top 200 higher", findings.Single(finding => finding.Datum is Ceiling).Said.Replace(" mm", string.Empty));
        Assert.Contains("bottom 200 higher", findings.Single(finding => finding.Datum == downlight).Said.Replace(" mm", string.Empty));
        Assert.True(findings.ToList().IndexOf(findings.Single(finding => finding.Datum is Ceiling)) < findings.ToList().IndexOf(findings.Single(finding => finding.Datum == downlight)));
        Assert.Contains(Warnings.Of(host), warning => warning.Heading == "Linked elements changed");
        var foot = ComponentHosting.BaseElevation(host, downlight, host.FindType<ComponentType>(downlight.TypeId)!);
        foreach (var finding in findings) history.Execute(CopyMonitor.Match(host, finding)!);
        Assert.Equal(new Point2D(0, 1300), Rounded(basin.Location));
        Assert.Equal(2600, ceiling.HeightOffset, 6);
        Assert.Equal(foot + 200, ComponentHosting.BaseElevation(host, downlight, host.FindType<ComponentType>(downlight.TypeId)!), 6);
        Assert.Empty(CopyMonitor.Review(host));
    }

    [Fact]
    public void ADoorMovedIntoAnotherWallThereIsMovedIntoTheOneFollowingItHere()
    {
        var (host, link, file) = Linked(Fitted());
        var history = new UndoStack();
        history.Execute(CopyMonitor.Copy(host, link, kinds: CopyMonitorKinds.Walls | CopyMonitorKinds.Openings).Command!);
        var door = host.All<Door>().Single();
        var (w1, w3) = (host.Walls.Single(wall => wall.Mark == "W1"), host.Walls.Single(wall => wall.Mark == "W3"));
        Assert.Equal(w1.Id, door.HostWallId);

        // Issued again with the door 1000 along W3 - the far wall, from (6000, 4000) to (0, 4000): its middle at (5000, 4000).
        ProjectFile.Save(Fitted(doorAlong: 1000, doorWall: 2), file);
        ModelLinks.Load(link);
        var finding = Assert.Single(CopyMonitor.Review(host));
        Assert.Contains("into the wall followed here by Wall W3", finding.Said);
        history.Execute(CopyMonitor.Match(host, finding)!);
        Assert.Equal((w3.Id, 1000.0), (door.HostWallId, Math.Round(door.DistanceAlongWall, 6)));
        Assert.Equal(new Point2D(5000, 4000), Rounded(door.GetCentre(w3)));
        Assert.Empty(CopyMonitor.Review(host));
        history.Undo();
        Assert.Equal(w1.Id, door.HostWallId);
    }

    [Fact]
    public void ADoorInAWallNotFollowedHereIsLeft()
    {
        var (host, link, _) = Linked(Fitted());
        var (copy, said) = CopyMonitor.Copy(host, link, kinds: CopyMonitorKinds.Openings);
        Assert.Null(copy);
        Assert.Contains("2 doors or windows in a wall not followed here", said);
    }

    [Fact]
    public void AFrameWrittenAgainFromScratchIsFollowedByItsMarks()
    {
        var (host, link, file) = Linked(Framed());
        new UndoStack().Execute(CopyMonitor.Copy(host, link, kinds: CopyMonitorKinds.Columns | CopyMonitorKinds.Walls).Command!);

        // Made again from nothing - every element a new id - its column C1 200 north.
        var again = Framed();
        again.All<Column>().Single().Location += new Vector2D(0, 200);
        ProjectFile.Save(again, file);
        ModelLinks.Load(link);

        // Found again by their marks: the column moved, nothing gone; matched, it follows the new one.
        var finding = Assert.Single(CopyMonitor.Review(host));
        Assert.Equal((MonitorChange.Moved, "Column C1"), (finding.Change, finding.Name));
        CopyMonitor.Match(host, finding)!.Redo();
        Assert.Empty(CopyMonitor.Review(host));
        Assert.Equal(again.All<Column>().Single().Id, host.All<Column>().Single().Monitor!.SourceId);
        Assert.Null(CopyMonitor.Copy(host, link, kinds: CopyMonitorKinds.Columns | CopyMonitorKinds.Walls).Command);
    }

    [Fact]
    public void AColumnOrWallAlreadyStandingThereIsPairedAndACurtainWallIsLeft()
    {
        var structure = Framed();
        structure.Add(new Wall
        {
            Start = new Point2D(0, 6000), End = new Point2D(6000, 6000), LevelId = structure.Levels[0].Id, UnconnectedHeight = 3000,
            TypeId = structure.TypesOf<CurtainWallType>().First(type => type.Name.Contains("Storefront")).Id
        });
        var (host, link, _) = Linked(structure);

        // This project's own wall already on W1's line, as high; and its own column type of the engineer's column's name.
        var own = new Wall { Start = new Point2D(0, 0), End = new Point2D(6000, 0), LevelId = host.Levels[0].Id, UnconnectedHeight = 3000, TypeId = host.TypesOf<WallType>().First().Id };
        host.Add(own);
        var (copy, said) = CopyMonitor.Copy(host, link, kinds: CopyMonitorKinds.Walls | CopyMonitorKinds.Columns);
        new UndoStack().Execute(copy!);
        Assert.Contains("paired wall W1", said);
        Assert.Contains("left 1 curtain, stacked, elliptical or spline wall", said);
        Assert.Equal(4, host.Walls.Count());
        Assert.NotNull(own.Monitor);
        Assert.Equal("The wall W1 in structure.bimx", CopyMonitor.Said(host, own.Monitor));
        Assert.Equal(host.TypesOf<WallType>().First().Name, host.FindType<WallType>(own.TypeId)!.Name);

        // The column on the storey it stands at, this project's ground floor, standing its height there; its type this project's own of that name.
        var column = host.All<Column>().Single();
        Assert.Equal(host.Levels[0].Id, column.LevelId);
        Assert.Null(column.TopLevelId);
        Assert.Single(host.TypesOf<ColumnType>(), type => type.Name == host.FindType<ColumnType>(column.TypeId)!.Name);
        Assert.Empty(CopyMonitor.Review(host));
    }

    [Fact]
    public void ALinkedModelWrittenAgainFromScratchIsFollowedByNameWhereItsIdsAreNew()
    {
        var (host, link, file) = Linked(Structure());
        new UndoStack().Execute(CopyMonitor.Copy(host, link).Command!);

        // Made again from nothing: every level and gridline a new id, the first floor 200 higher.
        var again = Structure();
        again.Levels[1].Elevation = 3700;
        ProjectFile.Save(again, file);
        ModelLinks.Load(link);

        var finding = Assert.Single(CopyMonitor.Review(host));
        Assert.Equal(MonitorChange.Moved, finding.Change);
        CopyMonitor.Match(host, finding)!.Redo();
        Assert.Empty(CopyMonitor.Review(host));
        Assert.Equal(again.Levels[1].Id, host.Levels.Single(level => level.Elevation == 3700).Monitor!.SourceId);
        Assert.Null(CopyMonitor.Copy(host, link).Command);
    }

    [Fact]
    public void ALinkIsCutInASectionSeenInAnElevationAndStandsRoundTheDaylight()
    {
        var (host, link, _) = Linked(Structure(), new Vector2D(20000, 0));
        var marker = new SectionMarker { Name = "A", Start = new Point2D(19000, 2000), End = new Point2D(28000, 2000), LevelId = host.Levels[0].Id };
        host.Add(marker);

        var section = SectionProjection.Build(host, marker);
        Assert.Contains(section.Pieces, piece => piece.ElementId == link.Id);

        // Halftone, faded halfway to white; not, its own colour.
        var faded = ModelLinks.WithLinks(host, Array.Empty<Core.Geometry.Mesh3D>()).First();
        link.Halftone = false;
        var own = ModelLinks.WithLinks(host, Array.Empty<Core.Geometry.Mesh3D>()).First();
        Assert.True(faded.Colour.R >= own.Colour.R && faded.Colour.R >= 127);

        var elevation = Projections.Exterior(host, ElevationSide.South, ViewReference.Elevation(ElevationSide.South));
        Assert.False(elevation.Extent.IsEmpty);
        Assert.True(Core.Energy.Daylight.SceneOf(host) is not null);

        // Unloaded, gone from them all.
        link.Shown = false;
        Assert.DoesNotContain(SectionProjection.Build(host, marker).Pieces, piece => piece.ElementId == link.Id);
    }

    [Fact]
    public void AScheduleTakesInTheLinkedModelsElementsReadOnlyAndSaysWhichLink()
    {
        var (host, link, _) = Linked(Structure());
        var type = host.TypesOf<WallType>().First();
        host.Add(new Wall { Start = new Point2D(0, 0), End = new Point2D(3000, 0), TypeId = type.Id, LevelId = host.Levels[0].Id, UnconnectedHeight = 2700 });

        var schedule = new ScheduleDefinition("Walls", BuiltInCategory.Walls, "Length", ScheduleDefinition.LinkField);
        Assert.Single(Schedule.Run(host, schedule).Rows);

        schedule.IncludeLinks = true;
        var rows = Schedule.Run(host, schedule).Rows;
        Assert.Equal(5, rows.Count);
        Assert.Equal(4, rows.Count(row => row.Text(ScheduleDefinition.LinkField) == "structure.bimx"));
        Assert.All(rows.Where(row => row.Link is not null), row => Assert.True(row[ "Length"]!.IsReadOnly));
        Assert.Contains(ScheduleDefinition.LinkField, Schedule.AvailableFields(host, BuiltInCategory.Walls));
        var saved = BimDocument.CreateDefault();
        saved.Schedules.Add(schedule);
        Assert.True(ProjectFile.FromJson(ProjectFile.ToJson(saved)).Schedules.Single(each => each.Name == "Walls").IncludeLinks);
    }

    [Fact]
    public void ALinkMovedOnThePlanMovesByItsOffsetAndIsWrittenBesideAnIfcExportWhereItIs()
    {
        var (host, link, _) = Linked(Structure(), new Vector2D(1000, 2000));
        new MoveElementsCommand(new Element[] { link }, new Vector2D(500, -500), "Move", host).Redo();
        Assert.Equal(new Vector2D(1500, 1500), link.Offset);

        link.Rotation = 90;
        link.Elevation = 300;
        var linked = ModelLinks.Of(link)!.Document!;
        var origin = ModelLinks.OriginIn(host, link, linked);
        Assert.Equal((1500.0, 1500.0, 300.0, 90.0), (Math.Round(origin.X, 6), Math.Round(origin.Y, 6), origin.Z, origin.Rotation));

        using var ifc = IfcExport.Build(linked, new IfcExportOptions { Origin = origin });
        var site = ifc.Instances.OfType<IIfcSite>().Single();
        var placement = (IIfcAxis2Placement3D)((IIfcLocalPlacement)site.ObjectPlacement!).RelativePlacement;
        Assert.Equal((1500.0, 1500.0, 300.0), (placement.Location.X, placement.Location.Y, placement.Location.Z));
        Assert.Equal(1, placement.RefDirection!.Y, 6);
    }

    [Fact]
    public void ALinkAttachedInsideALinkedModelComesWithItAndAnOverlayDoesNot()
    {
        // The services' model, linked into the structure's - beside it in the same folder - and the structure into this project.
        var services = BimDocument.CreateDefault();
        var type = services.TypesOf<WallType>().First();
        var duct = new Wall { Start = new Point2D(0, 0), End = new Point2D(2000, 0), TypeId = type.Id, LevelId = services.Levels[0].Id, UnconnectedHeight = 1000 };
        services.Add(duct);
        var servicesFile = Path.Combine(_folder, "services.bimx");
        ProjectFile.Save(services, servicesFile);

        var structure = Structure();
        structure.Add(new ModelLink { Path = servicesFile, Offset = new Vector2D(0, 10000), Attachment = true });
        var structureFile = Path.Combine(_folder, "structure.bimx");
        ProjectFile.Save(structure, structureFile);

        // Both moved together to another folder: the inner link is found beside the structure there.
        var moved = Path.Combine(_folder, "moved");
        Directory.CreateDirectory(moved);
        File.Move(servicesFile, Path.Combine(moved, "services.bimx"));
        File.Move(structureFile, Path.Combine(moved, "structure.bimx"));
        var host = BimDocument.CreateDefault();
        var link = new ModelLink { Path = Path.Combine(moved, "structure.bimx") };
        host.Add(link);
        ModelLinks.Load(link);

        Assert.Contains(ModelLinks.ElementMeshes(host, link), mesh => mesh.ElementId == duct.Id && mesh.Bounds() is var (low, _) && low.Y > 9000);
        Assert.Equal(link, ModelLinks.Find(host, duct.Id)?.Link);
        Assert.Contains("services.bimx (attached, drawn here)", ModelLinks.NestedSaid(link));

        // As an overlay it stays the structure's own.
        structure.All<ModelLink>().Single().Attachment = false;
        ProjectFile.Save(structure, link.Path);
        ModelLinks.Load(link);
        Assert.DoesNotContain(ModelLinks.ElementMeshes(host, link), mesh => mesh.ElementId == duct.Id);
    }
}
