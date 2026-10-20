using System.Text;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Sheets;
using BIMDesigner.Core.Views;
using BIMDesigner.Infrastructure.Interoperability;

namespace BIMDesigner.Tests;

/// <summary>
/// What the views lacked: a walkthrough saved as a video, things picked in an interior elevation,
/// a section's callout cropped by its corners, and templates governing sections.
/// </summary>
public class ViewGapTests
{
    [Fact]
    public void AVideoIsAMotionJpegAviWithEveryFrameIndexed()
    {
        using var stream = new MemoryStream();
        using (var video = new AviWriter(stream, 640, 360, 15))
        {
            video.Add(new byte[] { 0xFF, 0xD8, 1, 2, 3, 0xFF, 0xD9 });
            video.Add(new byte[] { 0xFF, 0xD8, 4, 0xFF, 0xD9 });
            video.Add(new byte[] { 0xFF, 0xD8, 5, 6, 0xFF, 0xD9 });
            Assert.Equal(3, video.Frames);
        }

        var bytes = stream.ToArray();
        string Tag(int at) => Encoding.ASCII.GetString(bytes, at, 4);
        int Int(int at) => BitConverter.ToInt32(bytes, at);

        Assert.Equal(("RIFF", "AVI ", "LIST", "hdrl", "avih"), (Tag(0), Tag(8), Tag(12), Tag(20), Tag(24)));
        Assert.Equal(bytes.Length - 8, Int(4));

        // 15 frames a second, three of them, 640 by 360, Motion-JPEG.
        Assert.Equal(66667, Int(32));
        Assert.Equal(3, Int(48));
        Assert.Equal((640, 360), (Int(64), Int(68)));
        Assert.Contains("MJPG", Encoding.ASCII.GetString(bytes));

        // The index at the end: one key frame for each.
        var index = Encoding.ASCII.GetString(bytes).LastIndexOf("idx1", StringComparison.Ordinal);
        Assert.Equal(3 * 16, Int(index + 4));
        Assert.Equal(index + 8 + 3 * 16, bytes.Length);
    }

    /// <summary>A room 6 m by 4 m, walled round, with a marker in the middle of it.</summary>
    private static (BimDocument Document, Wall North, InteriorElevationMarker Marker) Room()
    {
        var document = BimDocument.CreateDefault();
        var type = document.TypesOf<WallType>().First(t => t.Function == WallFunction.Exterior);
        var corners = new[] { new Point2D(0, 0), new Point2D(6000, 0), new Point2D(6000, 4000), new Point2D(0, 4000) };
        var walls = new List<Wall>();
        for (var i = 0; i < 4; i++)
        {
            var wall = new Wall { Start = corners[i], End = corners[(i + 1) % 4], TypeId = type.Id, LevelId = document.Levels[0].Id, UnconnectedHeight = 3000 };
            document.Add(wall);
            walls.Add(wall);
        }

        var marker = new InteriorElevationMarker { LevelId = document.Levels[0].Id, Location = new Point2D(3000, 2000) };
        document.Add(marker);
        return (document, walls[2], marker);
    }

    [Fact]
    public void AClickInAnInteriorElevationPicksTheWallLookedAt()
    {
        var (document, north, marker) = Room();
        var drawing = Projections.Of(document, ViewReference.InteriorElevation(marker.Id, 0));

        // Straight ahead, half way up: the north wall. Outside the crop: nothing.
        Assert.Equal(north.Id, drawing.ElementAt(new Point2D(0, 1500)));
        Assert.Null(drawing.ElementAt(new Point2D(10000, 1500)));
        Assert.Contains(drawing.Lines, line => line.ElementId == north.Id);
    }

    [Fact]
    public void ASectionsCalloutIsCroppedByItsCornersAndATemplateGovernsTheSection()
    {
        var (document, _, _) = Room();
        var marker = new SectionMarker { LevelId = document.Levels[0].Id, Start = new Point2D(3000, -1000), End = new Point2D(3000, 5000) };
        document.Add(marker);
        var section = ViewReference.Section(marker.Id);
        var (callout, command) = Callouts.Draw(document, section, marker.LevelId, new Point2D(0, 0), new Point2D(1000, 1000));
        command.Redo();

        // A section's callout has no handles on the plan, but its corner crops it all the same.
        Assert.Empty(ElementHandles.Of(document, callout));
        ElementHandles.Drag(document, callout, new ElementHandle("corner:2", new Point2D(1000, 1000), 0, false, HandleShape.Corner), new Point2D(2500, 3000), 0);
        Assert.Equal(new BoundingBox2D(0, 0, 2500, 3000), callout.Region);

        // A section template governing the section and its callout: their scale set by it, the walls it hides hidden.
        var template = new ViewTemplate { For = ViewTemplateKind.Section, Scale = 50 };
        template.HiddenWallFunctions.Add(WallFunction.Exterior);
        document.Add(template);
        Assert.Same(template, Assert.Single(ViewTemplates.For(document, Callouts.Reference(callout))));

        var history = new UndoStack();
        history.Execute(ViewTemplates.Apply(document, template, section, govern: true));
        Assert.Equal(50, document.ViewSettings.ScaleOf(section));
        Assert.True(ViewTemplates.Locks(document, section, ViewTemplateSetting.Scale));
        Assert.False(ViewTemplates.Locks(document, section, ViewTemplateSetting.DetailLevel));
        Assert.DoesNotContain(SectionProjection.Build(document, marker, document.ViewSettings.FilterFor(document, section)).Pieces, piece => document.FindElement(piece.ElementId) is Wall);
    }
}
