using BIMDesigner.Core.Annotation;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Parameters;
using BIMDesigner.Core.Schedules;
using BIMDesigner.Core.Sheets;
using BIMDesigner.Core.Views;
using BIMDesigner.Infrastructure.Interoperability;
using BIMDesigner.Infrastructure.Serialization;

namespace BIMDesigner.Tests;

/// <summary>
/// An audit of what happens to a model that is wrong.
///
/// Models get into odd states: something is deleted that something else referred to, a wall
/// is dragged until it has no length, a project is opened that was written by an older build.
/// None of that should crash, and none of it should quietly produce a wrong number - a room
/// reporting zero area is worse than a room saying it is not enclosed.
///
/// Every case here runs the model through everything that consumes it: the file format, the
/// schedules, the section, the 3D geometry and the IFC export. Those are the five things that
/// walk the whole document, so between them they touch nearly all of it.
/// </summary>
public class RobustnessAuditTests
{
    /// <summary>
    /// Puts a document through every consumer in the application. Any of them throwing is the
    /// failure; what they produce is checked by their own tests.
    /// </summary>
    private static void AssertSurvivesEverything(BimDocument document, string what)
    {
        var reloaded = Record.Exception(() => ProjectFile.FromJson(ProjectFile.ToJson(document)));
        Assert.True(reloaded is null, $"{what}: saving and reopening threw. {reloaded?.Message}");

        foreach (var definition in ScheduleDefinition.Defaults())
        {
            var failure = Record.Exception(() => Schedule.Run(document, definition));
            Assert.True(failure is null, $"{what}: the {definition.Name} schedule threw. {failure?.Message}");
        }

        var takeoff = Record.Exception(() =>
        {
            MaterialTakeoff.Lines(document);
            MaterialTakeoff.Totals(document);
        });
        Assert.True(takeoff is null, $"{what}: material takeoff threw. {takeoff?.Message}");

        foreach (var marker in document.Elements.OfType<SectionMarker>())
        {
            var failure = Record.Exception(() => SectionProjection.Build(document, marker));
            Assert.True(failure is null, $"{what}: the section threw. {failure?.Message}");
        }

        var meshes = Record.Exception(() => ModelMeshBuilder.Build(document));
        Assert.True(meshes is null, $"{what}: building the 3D model threw. {meshes?.Message}");

        foreach (var sheet in document.Elements.OfType<Sheet>())
        foreach (var viewport in sheet.Viewports)
        {
            var failure = Record.Exception(() => viewport.PaperBounds(document));
            Assert.True(failure is null, $"{what}: measuring a viewport threw. {failure?.Message}");
        }

        var ifc = Record.Exception(() =>
        {
            using var model = IfcExport.Build(document);
        });

        Assert.True(ifc is null, $"{what}: the IFC export threw. {ifc?.Message}");
    }

    private static BimDocument WithSection(BimDocument document)
    {
        document.Add(new SectionMarker
        {
            Start = new Point2D(3000, -4000),
            End = new Point2D(3000, 8000),
            LevelId = document.Levels.FirstOrDefault()?.Id ?? Guid.Empty,
            Name = "A"
        });

        return document;
    }

    // ---- nothing at all -----------------------------------------------------------

    [Fact]
    public void AnEmptyDocumentSurvivesEverything()
    {
        AssertSurvivesEverything(new BimDocument(), "An empty document");
    }

    [Fact]
    public void ADocumentWithNoLevelsSurvivesEverything()
    {
        var document = BimDocument.CreateDefault();
        var level = document.Levels[0];

        // Elements left stranded on a level that no longer exists.
        document.Add(new Wall
        {
            Start = new Point2D(0, 0),
            End = new Point2D(6000, 0),
            TypeId = document.TypesOf<WallType>().First().Id,
            LevelId = level.Id
        });

        while (document.Levels.Count > 0) document.RemoveLevel(document.Levels[0]);

        AssertSurvivesEverything(WithSection(document), "A document with no levels");
    }

    // ---- degenerate geometry ------------------------------------------------------

    [Fact]
    public void AWallWithNoLengthSurvivesEverything()
    {
        var document = BimDocument.CreateDefault();

        document.Add(new Wall
        {
            Start = new Point2D(1000, 1000),
            End = new Point2D(1000, 1000),
            TypeId = document.TypesOf<WallType>().First().Id,
            LevelId = document.Levels[0].Id
        });

        AssertSurvivesEverything(WithSection(document), "A wall with no length");
    }

    [Fact]
    public void ASlabWithTooFewPointsSurvivesEverything()
    {
        var document = BimDocument.CreateDefault();

        var floor = new Floor
        {
            TypeId = document.TypesOf<FloorType>().First().Id,
            LevelId = document.Levels[0].Id
        };

        floor.SetBoundary(new[] { new Point2D(0, 0), new Point2D(1000, 0) });
        document.Add(floor);

        AssertSurvivesEverything(WithSection(document), "A slab with two points");
    }

    [Fact]
    public void ASectionMarkerWithNoLengthSurvivesEverything()
    {
        var document = BimDocument.CreateDefault();

        document.Add(new SectionMarker
        {
            Start = new Point2D(0, 0),
            End = new Point2D(0, 0),
            LevelId = document.Levels[0].Id,
            Name = "A"
        });

        AssertSurvivesEverything(document, "A section marker with no length");
    }

    [Fact]
    public void ARoomWithNoWallsRoundItReportsNotEnclosedRatherThanZero()
    {
        var document = BimDocument.CreateDefault();

        var room = new Room { Location = new Point2D(0, 0), LevelId = document.Levels[0].Id, Name = "Nowhere" };
        document.Add(room);

        var boundary = room.GetBoundary(document);

        // A silent zero is how a wrong area schedule reaches a client.
        Assert.False(boundary.IsEnclosed);

        // Not zero. Zero square metres is a claim about a room; this room has no measurable
        // area at all, and an area schedule has to be able to tell the two apart.
        var parameters = room.GetInstanceParameters(document).ToDictionary(p => p.Name, p => p);

        Assert.Null(parameters["Area"].Value);
        Assert.Equal(ParameterFormatter.NoValue, parameters["Area"].DisplayValue);
        Assert.Equal(ParameterFormatter.NoValue, parameters["Perimeter"].DisplayValue);
        Assert.Equal(ParameterFormatter.NoValue, parameters["Volume"].DisplayValue);
        Assert.Equal("No", parameters["Enclosed"].DisplayValue);

        AssertSurvivesEverything(WithSection(document), "A room with no walls");
    }

    // ---- broken references ---------------------------------------------------------

    [Fact]
    public void AnOpeningWhoseWallHasGoneSurvivesEverything()
    {
        var document = BimDocument.CreateDefault();
        var level = document.Levels[0].Id;

        var wall = new Wall
        {
            Start = new Point2D(0, 0),
            End = new Point2D(6000, 0),
            TypeId = document.TypesOf<WallType>().First().Id,
            LevelId = level
        };
        document.Add(wall);

        document.Add(new Door
        {
            TypeId = document.TypesOf<DoorType>().First().Id,
            LevelId = level,
            HostWallId = wall.Id,
            DistanceAlongWall = 3000
        });

        // Removed directly rather than through the delete command, which is what a corrupt or
        // hand-edited file amounts to.
        document.Remove(wall);

        AssertSurvivesEverything(WithSection(document), "An orphaned door");
    }

    [Fact]
    public void AnnotationPointingAtSomethingDeletedSurvivesEverything()
    {
        var document = BimDocument.CreateDefault();
        var level = document.Levels[0].Id;

        var wall = new Wall
        {
            Start = new Point2D(0, 0),
            End = new Point2D(6000, 0),
            TypeId = document.TypesOf<WallType>().First().Id,
            LevelId = level
        };
        document.Add(wall);

        document.Add(new Dimension
        {
            Start = DimensionReference.ToWall(wall, atStart: true),
            End = DimensionReference.ToWall(wall, atStart: false),
            LevelId = level
        });

        document.Add(new Tag { TargetId = wall.Id, Field = "Length", LevelId = level });

        document.Remove(wall);

        // A dimension whose reference has gone falls back to where it last was rather than
        // vanishing or throwing.
        var dimension = document.Elements.OfType<Dimension>().Single();
        Assert.False(dimension.IsAssociative(document));
        Assert.Equal(6000, dimension.Measure(document), 6);

        AssertSurvivesEverything(WithSection(document), "Annotation with a dead reference");
    }

    [Fact]
    public void AnElementWithNoTypeSurvivesEverything()
    {
        var document = BimDocument.CreateDefault();

        document.Add(new Wall
        {
            Start = new Point2D(0, 0),
            End = new Point2D(6000, 0),
            TypeId = Guid.NewGuid(),
            LevelId = document.Levels[0].Id
        });

        var floor = new Floor { TypeId = Guid.NewGuid(), LevelId = document.Levels[0].Id };
        floor.SetBoundary(new[]
        {
            new Point2D(0, 0), new Point2D(6000, 0), new Point2D(6000, 4000), new Point2D(0, 4000)
        });
        document.Add(floor);

        AssertSurvivesEverything(WithSection(document), "Elements with a type that is not there");
    }

    [Fact]
    public void ASheetWhoseViewsHaveGoneSurvivesEverything()
    {
        var document = BimDocument.CreateDefault();

        var marker = new SectionMarker
        {
            Start = new Point2D(0, 0),
            End = new Point2D(6000, 0),
            LevelId = document.Levels[0].Id,
            Name = "A"
        };
        document.Add(marker);

        var sheet = new Sheet { Number = "A-101", Name = "Plans" };
        sheet.Add(new Viewport { View = ViewReference.FloorPlan(document.Levels[0].Id) });
        sheet.Add(new Viewport { View = ViewReference.Section(marker.Id) });
        sheet.Add(new Viewport { View = ViewReference.Section(Guid.NewGuid()) });
        document.Add(sheet);

        document.Remove(marker);
        document.RemoveLevel(document.Levels[0]);

        Assert.All(sheet.Viewports, viewport => Assert.False(viewport.View.ExistsIn(document)));

        AssertSurvivesEverything(document, "A sheet whose views have gone");
    }

    // ---- files from other builds ---------------------------------------------------

    [Fact]
    public void AFileMissingWholeSectionsStillOpens()
    {
        var document = BimDocument.CreateDefault();

        document.Add(new Wall
        {
            Start = new Point2D(0, 0),
            End = new Point2D(6000, 0),
            TypeId = document.TypesOf<WallType>().First().Id,
            LevelId = document.Levels[0].Id
        });

        // What a project written by an older build looks like: whole arrays absent.
        var json = ProjectFile.ToJson(document)
            .Replace("\"sheets\"", "\"sheetsGone\"")
            .Replace("\"sections\"", "\"sectionsGone\"")
            .Replace("\"slabTypes\"", "\"slabTypesGone\"")
            .Replace("\"tags\"", "\"tagsGone\"");

        var reloaded = ProjectFile.FromJson(json);

        Assert.Single(reloaded.Walls);
        Assert.NotEmpty(reloaded.TypesOf<SlabType>());

        AssertSurvivesEverything(reloaded, "A file missing whole sections");
    }

    [Fact]
    public void GarbageInsteadOfAProjectIsRefusedClearly()
    {
        // Not a crash, and not a silently empty project either.
        Assert.Throws<ProjectFileException>(() => ProjectFile.FromJson("this is not a project"));
        Assert.Throws<ProjectFileException>(() => ProjectFile.FromJson("{ \"formatVersion\": 9999 }"));
    }
}
