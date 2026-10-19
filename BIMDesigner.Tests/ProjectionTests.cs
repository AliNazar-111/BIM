using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Sheets;
using BIMDesigner.Core.Views;
using BIMDesigner.Infrastructure.Serialization;

namespace BIMDesigner.Tests;

/// <summary>
/// Views drawn flat from the model for sheets - 3D views and elevations - in hidden line: each edge
/// drawn as far as nothing nearer covers it; interior elevations cropped to their room.
/// </summary>
public class ProjectionTests
{
    private static Wall Wall(BimDocument document, Point2D start, Point2D end, double height)
    {
        var type = document.TypesOf<WallType>().First(t => t.Function == WallFunction.Exterior);
        var wall = new Wall { Start = start, End = end, TypeId = type.Id, LevelId = document.Levels[0].Id, UnconnectedHeight = height };
        document.Add(wall);
        return wall;
    }

    [Fact]
    public void AnElevationHidesWhatIsBehindAndShowsWhatRisesAboveIt()
    {
        // A low wall in front, a tall one behind it, its ends 1000 either side of the middle.
        var document = BimDocument.CreateDefault();
        Wall(document, new Point2D(0, 0), new Point2D(6000, 0), 3000);
        Wall(document, new Point2D(2000, 5000), new Point2D(4000, 5000), 6000);

        var south = Projections.Of(document, ViewReference.Elevation(ElevationSide.South));
        var ends = south.Lines.Where(line => Math.Abs(Math.Abs(line.From.X) - 1000) < 1 && Math.Abs(line.From.X - line.To.X) < 1).ToList();

        // The tall wall's ends show above the low one, and not below its top.
        Assert.NotEmpty(ends);
        Assert.All(ends, line => Assert.True(Math.Min(line.From.Y, line.To.Y) > 3000 - 10, $"{line}"));
        Assert.Contains(ends, line => Math.Max(line.From.Y, line.To.Y) > 5900);
        Assert.Equal(document.Levels.Count, south.Levels.Count);
        Assert.Equal("South Elevation", ViewReference.Elevation(ElevationSide.South).TitleIn(document));
    }

    [Fact]
    public void AnInteriorElevationIsCroppedToItsRoomAndKept()
    {
        var document = BimDocument.CreateDefault();
        var corners = new[] { new Point2D(0, 0), new Point2D(6000, 0), new Point2D(6000, 4000), new Point2D(0, 4000) };
        for (var i = 0; i < 4; i++) Wall(document, corners[i], corners[(i + 1) % 4], 3000);
        var marker = new InteriorElevationMarker { LevelId = document.Levels[0].Id, Location = new Point2D(3000, 2000) };
        document.Add(marker);

        // Looking north: as wide as the room inside its walls, out to the north wall's face.
        var frame = InteriorElevations.Frame(document, marker, 0);
        Assert.True(frame.InRoom);
        var thickness = document.TypesOf<WallType>().First(t => t.Function == WallFunction.Exterior).Width;
        Assert.Equal(6000 - thickness, frame.Crop.Width, 1);
        Assert.Equal(2000 - thickness / 2 + 30, frame.Crop.Max.Z, 1);
        Assert.Equal("1 - North", marker.ViewName(0));
        Assert.Equal("1 - East", marker.ViewName(1));

        var view = ViewReference.InteriorElevation(marker.Id, 0);
        var drawing = Projections.Of(document, view);
        Assert.NotNull(drawing.Crop);
        Assert.All(drawing.Lines, line => Assert.InRange(line.From.X, frame.Crop.Min.X - 1, frame.Crop.Max.X + 1));

        // An arrow turned off is no view.
        marker.LooksDown = false;
        Assert.False(ViewReference.InteriorElevation(marker.Id, 2).ExistsIn(document));
        Assert.Equal(new[] { 0, 1, 3 }, marker.Arrows);

        // Placed on a sheet, its arrow and all are kept.
        var sheet = new Sheet();
        sheet.Add(new Viewport { View = view, Centre = new Point2D(200, 200) });
        document.Add(sheet);
        var reopened = ProjectFile.FromJson(ProjectFile.ToJson(document));
        var kept = Assert.Single(reopened.Elements.OfType<InteriorElevationMarker>());
        Assert.Equal((marker.Location, false), (kept.Location, kept.LooksDown));
        Assert.Equal(view, Assert.Single(reopened.Elements.OfType<Sheet>().Single().Viewports).View);
    }

    [Fact]
    public void AMarkerTurnsAndMirrorsWithItsArrows()
    {
        var marker = new InteriorElevationMarker { Location = new Point2D(1000, 0), LooksLeft = false };
        Assert.True(marker.Look(0).Y > 0.999);
        Assert.True(marker.Look(1).X > 0.999);

        // Mirrored across the Y axis: its right arrow now looks the other way, so left and right change places.
        ElementTransforms.Mirror(marker, new Line2D(new Point2D(0, 0), new Vector2D(0, 1)));
        Assert.Equal(new Point2D(-1000, 0), marker.Location);
        Assert.True(marker.Look(0).Y > 0.999);
        Assert.False(marker.LooksRight);
        Assert.True(marker.LooksLeft);
    }

    [Fact]
    public void A3DViewGoesOnASheetAsTheDrawingItIs()
    {
        var (document, _, _) = DormerToolTests.House(10000, 8000, 40);
        document.Default3D.Style = VisualStyle.HiddenLine;
        var view = ViewReference.Model3D;

        var drawing = Projections.Of(document, view);
        Assert.NotEmpty(drawing.Lines);
        Assert.Empty(drawing.Fills);

        // The ridge is a line: two slopes of the roof meet along it.
        var top = drawing.Lines.Max(line => Math.Max(line.From.Y, line.To.Y));
        Assert.Contains(drawing.Lines, line => Math.Abs(line.From.Y - line.To.Y) > 1 || Math.Abs(line.From.Y - top) < 1);

        var viewport = new Viewport { View = view, Centre = new Point2D(300, 300) };
        var bounds = viewport.PaperBounds(document);
        Assert.True(bounds.Width > 50 && bounds.Height > 50);
        Assert.Equal("{3D}", view.TitleIn(document));

        // Shaded, its faces are painted too - none of them hidden ones.
        document.Default3D.Style = VisualStyle.Shaded;
        Assert.NotEmpty(Projections.Of(document, view).Fills);
    }
}
