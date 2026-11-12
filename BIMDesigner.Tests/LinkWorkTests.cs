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
        Assert.Contains(Warnings.Of(host), warning => warning.Heading == "Linked columns and walls changed");

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
