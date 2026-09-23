using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;
using BIMDesigner.Infrastructure.Serialization;

namespace BIMDesigner.Tests;

/// <summary>
/// What each pane of a curtain wall is glazed with: clear, tinted, frosted, laminated or an
/// opaque spandrel panel, and how each one reads in 3D, in section and on the plan.
/// </summary>
public class CurtainGlassTests
{
    private static (BimDocument Document, Wall Wall) Storefront()
    {
        var document = BimDocument.CreateDefault();
        var type = document.TypesOf<CurtainWallType>().Single(t => t.Name.Contains("Storefront"));
        var wall = new Wall
        {
            Start = new Point2D(0, 0), End = new Point2D(6000, 0),
            TypeId = type.Id, LevelId = document.Levels[0].Id, UnconnectedHeight = 3000
        };
        document.Add(wall);
        return (document, wall);
    }

    [Fact]
    public void EachPaneIsGlazedWithWhatItIsGiven()
    {
        var (document, wall) = Storefront();
        new SetCurtainLayoutCommand(wall, null, new[]
        {
            new CurtainPanelOverride(0, 0, CurtainPanelKind.Glazed, null, CurtainGlass.Frosted),
            new CurtainPanelOverride(1, 0, CurtainPanelKind.Glazed, null, CurtainGlass.Spandrel),
            new CurtainPanelOverride(2, 0, CurtainPanelKind.Glazed, null, CurtainGlass.Laminated)
        }).Redo();

        var layout = CurtainLayout.Of(document, wall)!;
        Assert.Equal(CurtainGlass.Frosted, layout.Cells.Single(c => c.Column == 0 && c.Row == 0).Glass);
        Assert.Equal(CurtainGlass.Clear, layout.Cells.Single(c => c.Column == 3 && c.Row == 0).Glass);

        // Each pane is built in its own colour, seen through as far as it should be.
        var panes = ModelMeshBuilder.BuildWall(document, wall).Where(m => m.Kind == MeshKind.Glazing).ToList();
        Assert.Equal(8, panes.Count);

        var spandrel = panes.First(m => m.Description == "Glass, Spandrel");
        var clear = panes.First(m => m.Opacity < 0.4);
        Assert.Equal(1, spandrel.Opacity);
        Assert.True(clear.Opacity < spandrel.Opacity);
        Assert.NotEqual(clear.Colour, spandrel.Colour);
        Assert.True(panes.First(m => m.Description == "Glass, Frosted").Opacity > clear.Opacity);
    }

    [Fact]
    public void LaminatedGlassIsThickerThanASinglePane()
    {
        var (document, wall) = Storefront();
        new SetCurtainLayoutCommand(wall, null, new[]
        {
            new CurtainPanelOverride(0, 0, CurtainPanelKind.Glazed, null, CurtainGlass.Laminated)
        }).Redo();

        var panes = ModelMeshBuilder.BuildWall(document, wall).Where(m => m.Kind == MeshKind.Glazing).ToList();
        double Thickness(Mesh3D mesh) => mesh.Bounds()!.Value is var b ? b.Max.Y - b.Min.Y : 0;

        Assert.True(
            Thickness(panes.First(m => m.Description == "Glass, Laminated")) >
            Thickness(panes.First(m => m.Description != "Glass, Laminated")));
    }

    [Fact]
    public void ASpandrelPanelIsCutInItsOwnColour()
    {
        var (document, wall) = Storefront();
        new SetCurtainLayoutCommand(wall, null, new[]
        {
            new CurtainPanelOverride(0, 0, CurtainPanelKind.Glazed, null, CurtainGlass.Spandrel)
        }).Redo();

        var marker = new SectionMarker
        {
            Start = new Point2D(500, -2000), End = new Point2D(500, 2000),
            LevelId = document.Levels[0].Id
        };
        document.Add(marker);

        var glazing = SectionProjection.Build(document, marker).Pieces
            .Where(p => p.Part == SectionPart.Glazing && p.Depth == SectionDepth.Cut)
            .ToList();

        // The cut passes through the spandrel panel and the clear one over it: each is drawn
        // in its own colour rather than both as glass.
        Assert.Equal(2, glazing.Count);
        Assert.Contains(glazing, piece => piece.Fill == CurtainGlassLook.ColourOf(CurtainGlass.Spandrel, default));
        Assert.Contains(glazing, piece => piece.Fill != CurtainGlassLook.ColourOf(CurtainGlass.Spandrel, default));
    }

    [Fact]
    public void TheGlassOnAPanelIsSaved()
    {
        var (document, wall) = Storefront();
        new SetCurtainLayoutCommand(wall, null, new[]
        {
            new CurtainPanelOverride(2, 1, CurtainPanelKind.Glazed, null, CurtainGlass.Tinted)
        }).Redo();

        var path = Path.Combine(Path.GetTempPath(), $"bimtest-{Guid.NewGuid():N}{ProjectFile.Extension}");
        try
        {
            ProjectFile.Save(document, path);
            var panel = ProjectFile.Load(path).Walls.Single(w => w.Id == wall.Id).CurtainPanels!.Single();

            Assert.Equal(CurtainPanelKind.Glazed, panel.Kind);
            Assert.Equal(CurtainGlass.Tinted, panel.Glass);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
