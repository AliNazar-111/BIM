using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;

namespace BIMDesigner.Tests;

/// <summary>
/// A curtain wall's panels as things in their own right: each one clicked in the 3D view and
/// given its own properties, while the wall keeps properties of its own for all of them.
/// </summary>
public class CurtainPanelTests
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
    public void EachPanelIsItsOwnPieceOfTheModelAndFindsItsWayBack()
    {
        var (document, wall) = Storefront();
        var layout = CurtainLayout.Of(document, wall)!;

        // Every pane is its own mesh with its own id, which is what a click can pick out, and
        // every one of them still belongs to the wall.
        var panes = ModelMeshBuilder.BuildWall(document, wall).Where(m => m.Kind == MeshKind.Glazing).ToList();
        Assert.Equal(layout.Cells.Count, panes.Count);
        Assert.Equal(layout.Cells.Count, panes.Select(m => m.ElementId).Distinct().Count());
        Assert.All(panes, mesh => Assert.Equal(wall.Id, mesh.OwnerId));

        // A click on one of them finds the panel it stands for.
        var picked = CurtainPanel.Find(document, panes[3].ElementId)!;
        Assert.Equal(wall.Id, picked.HostWallId);
        Assert.Equal(panes[3].ElementId, picked.Id);
        Assert.NotNull(picked.Cell(document));

        // The mullions are the wall itself, so clicking the frame still selects the wall.
        Assert.All(ModelMeshBuilder.BuildWall(document, wall).Where(m => m.Kind == MeshKind.Mullion),
            mesh => Assert.Equal(wall.Id, mesh.ElementId));
    }

    [Fact]
    public void EveryPieceOfTheWallSelectsTheRightThing()
    {
        var (document, wall) = Storefront();
        var door = CurtainDoors.TypesFor(document)[0];
        var placement = CurtainDoors.Place(document, wall, door, along: 3000)!;
        new SetCurtainLayoutCommand(wall, placement.Grid, placement.Panels).Redo();

        // What a click in the 3D view resolves to, for every piece of the wall there is.
        foreach (var mesh in ModelMeshBuilder.BuildWall(document, wall))
        {
            var picked = (object?)document.Elements.FirstOrDefault(e => e.Id == mesh.ElementId)
                         ?? CurtainPanel.Find(document, mesh.ElementId);

            // The mullions are the wall; a panel - glass, solid, or the door filling one - is
            // itself. Nothing is left over that picks up nothing at all.
            if (mesh.Kind == MeshKind.Mullion) Assert.Same(wall, picked);
            else Assert.True(picked is CurtainPanel, $"{mesh.Kind} {mesh.Description} picked {picked?.GetType().Name ?? "nothing"}");

            // And whatever it is, it belongs to the wall, so selecting the wall lights it up.
            Assert.Equal(wall.Id, mesh.OwnerId);
        }

        // The door's own pieces - its frame, its leaves, its glass, its handles - all belong
        // to the panel it fills, not to the wall.
        var panel = CurtainPanel.At(document, wall, placement.Cell.Column, 0)!;
        var doorMeshes = ModelMeshBuilder.BuildWall(document, wall).Where(m => m.Description.StartsWith("Door")).ToList();
        Assert.NotEmpty(doorMeshes);
        Assert.All(doorMeshes, mesh => Assert.Equal(panel.Id, mesh.ElementId));
    }

    [Fact]
    public void APanelHasPropertiesOfItsOwn()
    {
        var (document, wall) = Storefront();
        var panel = CurtainPanel.At(document, wall, column: 1, row: 0)!;

        var parameters = panel.GetInstanceParameters(document).ToList();
        Assert.Equal("Column 2, row 1", parameters.Single(p => p.Name == "Grid Position").DisplayValue);
        Assert.Equal("Glazed", parameters.Single(p => p.Name == "Panel").DisplayValue);
        Assert.Equal("Clear", parameters.Single(p => p.Name == "Glass").DisplayValue);

        // Its size is the clear opening inside the mullions round it, not the whole bay.
        var cell = panel.Cell(document)!;
        Assert.Equal(cell.ClearTo - cell.ClearFrom, (double)parameters.Single(p => p.Name == "Width").Value!, precision: 6);
        Assert.Equal(cell.ClearTop - cell.ClearBottom, (double)parameters.Single(p => p.Name == "Height").Value!, precision: 6);
    }

    [Fact]
    public void ChangingAPanelChangesOnlyThatPanel()
    {
        var (document, wall) = Storefront();
        var panel = CurtainPanel.At(document, wall, column: 1, row: 1)!;

        Assert.True(panel.GetInstanceParameters(document).Single(p => p.Name == "Glass").TrySet("Frosted"));

        var layout = CurtainLayout.Of(document, wall)!;
        Assert.Equal(CurtainGlass.Frosted, layout.Cells.Single(c => c.Column == 1 && c.Row == 1).Glass);
        Assert.All(layout.Cells.Where(c => c.Column != 1 || c.Row != 1), c => Assert.Equal(CurtainGlass.Clear, c.Glass));

        // Set back to what the rest of the wall is, the panel stops being remembered at all.
        Assert.True(panel.GetInstanceParameters(document).Single(p => p.Name == "Glass").TrySet("Clear"));
        Assert.Null(wall.CurtainPanels);
    }

    [Fact]
    public void AParameterReportsItsNewValueAtOnce()
    {
        var (document, wall) = Storefront();
        var panel = CurtainPanel.At(document, wall, 1, 0)!;

        // The same parameter object, read back after its own write: this is what the palette
        // does to decide anything changed, and what the views and the undo stack wait on.
        var glass = panel.GetInstanceParameters(document).Single(p => p.Name == "Glass");
        Assert.True(glass.TrySet("Tinted"));
        Assert.Equal("Tinted", glass.DisplayValue);

        var kind = panel.GetInstanceParameters(document).Single(p => p.Name == "Panel");
        Assert.True(kind.TrySet("Solid"));
        Assert.Equal("Solid", kind.DisplayValue);

        var door = panel.GetInstanceParameters(document).Single(p => p.Name == "Door or Window");
        var type = CurtainDoors.TypesFor(document)[0];
        Assert.True(door.TrySet(type.Name));
        Assert.Equal(type.Name, door.DisplayValue);
    }

    [Fact]
    public void TheWholeWallCanBeReglazedAtOnce()
    {
        var (document, wall) = Storefront();

        // One panel of its own first, so it can be seen to keep what it was given.
        CurtainPanel.At(document, wall, 0, 0)!.GetInstanceParameters(document).Single(p => p.Name == "Glass").TrySet("Spandrel");

        var glass = wall.GetInstanceParameters(document).Single(p => p.Name == "Glass");
        Assert.Equal("Clear", glass.DisplayValue);
        Assert.True(glass.TrySet("Tinted"));

        var layout = CurtainLayout.Of(document, wall)!;
        Assert.Equal(CurtainGlass.Spandrel, layout.Cells.Single(c => c.Column == 0 && c.Row == 0).Glass);
        Assert.All(layout.Cells.Where(c => c.Column != 0 || c.Row != 0), c => Assert.Equal(CurtainGlass.Tinted, c.Glass));
    }

    [Fact]
    public void APanelCanBeMadeADoorOrSolidFromItsProperties()
    {
        var (document, wall) = Storefront();
        var door = CurtainDoors.TypesFor(document)[0];

        var bottom = CurtainPanel.At(document, wall, column: 2, row: 0)!;
        Assert.True(bottom.GetInstanceParameters(document).Single(p => p.Name == "Door or Window").TrySet(door.Name));

        var cell = bottom.Cell(document)!;
        Assert.Equal(CurtainPanelKind.Door, cell.Kind);
        Assert.Equal(door.Id, cell.OpeningTypeId);
        Assert.Equal(0, cell.ClearBottom);

        // A panel off the floor cannot be a door, as everywhere else.
        var upper = CurtainPanel.At(document, wall, column: 2, row: 1)!;
        upper.GetInstanceParameters(document).Single(p => p.Name == "Door or Window").TrySet(door.Name);
        Assert.NotEqual(CurtainPanelKind.Door, upper.Cell(document)!.Kind);

        Assert.True(upper.GetInstanceParameters(document).Single(p => p.Name == "Panel").TrySet("Solid"));
        Assert.Equal(CurtainPanelKind.Solid, upper.Cell(document)!.Kind);
    }

    [Fact]
    public void ADoorPanelCanBeMirrored()
    {
        var (document, wall) = Storefront();
        var type = CurtainDoors.TypesFor(document).First(t => t.Operation == DoorOperation.Swing);

        var panel = CurtainPanel.At(document, wall, column: 1, row: 0)!;
        panel.GetInstanceParameters(document).Single(p => p.Name == "Door or Window").TrySet(type.Name);

        double HandleAlong()
        {
            // The handle is on the side away from the hinges, so it says which way it is hung.
            var handle = ModelMeshBuilder.BuildWall(document, wall)
                .Single(m => m.ElementId == panel.Id && m.Description == "Hardware")
                .Bounds()!.Value;

            return (handle.Min.X + handle.Max.X) / 2;
        }

        var before = HandleAlong();

        var hand = panel.GetInstanceParameters(document).Single(p => p.Name == "Flip Hand");
        Assert.True(hand.TrySet(true));
        Assert.True(hand.Value is true);
        Assert.NotEqual(before, HandleAlong(), precision: 3);

        // Flipped back, it is where it started.
        Assert.True(panel.GetInstanceParameters(document).Single(p => p.Name == "Flip Hand").TrySet(false));
        Assert.Equal(before, HandleAlong(), precision: 3);

        // Which way it opens is the other mirror, and it is kept on the panel too.
        Assert.True(panel.GetInstanceParameters(document).Single(p => p.Name == "Flip Facing").TrySet(true));
        Assert.True(panel.Cell(document)!.FlipFacing);
    }

    [Fact]
    public void ADoorPanelIsGlazedWithWhateverThePanelIs()
    {
        var (document, wall) = Storefront();
        var type = CurtainDoors.TypesFor(document)[0];

        var panel = CurtainPanel.At(document, wall, column: 1, row: 0)!;
        panel.GetInstanceParameters(document).Single(p => p.Name == "Door or Window").TrySet(type.Name);

        Mesh3D Glazing() => ModelMeshBuilder.BuildWall(document, wall)
            .Single(m => m.ElementId == panel.Id && m.Kind == MeshKind.Glazing);

        var clear = Glazing();

        // The door's own glass follows the panel, so a tinted shopfront has a tinted door.
        Assert.True(panel.GetInstanceParameters(document).Single(p => p.Name == "Glass").TrySet("Tinted"));

        var tinted = Glazing();
        Assert.Equal(CurtainGlassLook.ColourOf(CurtainGlass.Tinted, default), tinted.Colour);
        Assert.NotEqual(clear.Colour, tinted.Colour);
        Assert.True(tinted.Opacity > clear.Opacity);
    }

    [Fact]
    public void APanelCanBeAWindowAnywhereInTheWall()
    {
        var (document, wall) = Storefront();
        var type = document.TypesOf<WindowType>().First(t => t.Operation == WindowOperation.Casement);

        // A window does not stand on the floor, so it goes in a panel off the ground too.
        var panel = CurtainPanel.At(document, wall, column: 2, row: 1)!;
        Assert.True(panel.GetInstanceParameters(document).Single(p => p.Name == "Door or Window").TrySet(type.Name));

        var cell = panel.Cell(document)!;
        Assert.Equal(CurtainPanelKind.Window, cell.Kind);
        Assert.Equal(type.Id, cell.OpeningTypeId);

        // It keeps the mullions all round it, unlike a door, which loses the one underneath.
        var layout = CurtainLayout.Of(document, wall)!;
        Assert.Contains(layout.Mullions, m => !m.IsVertical && m.From < cell.To && m.To > cell.From && m.Bottom < cell.Bottom + 1);

        // And it is built as that window type: sashes, a sill, glass.
        var meshes = ModelMeshBuilder.BuildWall(document, wall).Where(m => m.ElementId == panel.Id).ToList();
        Assert.Contains(meshes, m => m.Description == "Sash" && m.TriangleCount > 0);
        Assert.Contains(meshes, m => m.Kind == MeshKind.Glazing && m.TriangleCount > 0);
        Assert.All(meshes, m => Assert.Equal(wall.Id, m.OwnerId));
    }

    [Fact]
    public void AWindowPanelFillsItsBayAndIsSaved()
    {
        var (document, wall) = Storefront();
        var type = document.TypesOf<WindowType>().First();
        var panel = CurtainPanel.At(document, wall, 1, 1)!;
        panel.GetInstanceParameters(document).Single(p => p.Name == "Door or Window").TrySet(type.Name);

        // The window is the size of the panel, not of its type.
        var cell = panel.Cell(document)!;
        var bounds = ModelMeshBuilder.BuildWall(document, wall)
            .Where(m => m.ElementId == panel.Id)
            .Select(m => m.Bounds()!.Value)
            .Aggregate((a, b) => (
                new Point3D(Math.Min(a.Min.X, b.Min.X), Math.Min(a.Min.Y, b.Min.Y), Math.Min(a.Min.Z, b.Min.Z)),
                new Point3D(Math.Max(a.Max.X, b.Max.X), Math.Max(a.Max.Y, b.Max.Y), Math.Max(a.Max.Z, b.Max.Z))));

        Assert.True(bounds.Item1.X >= cell.ClearFrom - 60, $"the window starts at {bounds.Item1.X:0}, the panel at {cell.ClearFrom:0}");
        Assert.True(bounds.Item2.X <= cell.ClearTo + 60);

        var path = Path.Combine(Path.GetTempPath(), "bimtest-" + Guid.NewGuid().ToString("N") + BIMDesigner.Infrastructure.Serialization.ProjectFile.Extension);
        try
        {
            BIMDesigner.Infrastructure.Serialization.ProjectFile.Save(document, path);
            var copy = BIMDesigner.Infrastructure.Serialization.ProjectFile.Load(path)
                .Walls.Single(w => w.Id == wall.Id).CurtainPanels!.Single();

            Assert.Equal(CurtainPanelKind.Window, copy.Kind);
            Assert.Equal(type.Id, copy.OpeningTypeId);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ThePanelsOfEachWallAreItsOwn()
    {
        var (document, first) = Storefront();
        var second = new Wall
        {
            Start = new Point2D(0, 4000), End = new Point2D(6000, 4000),
            TypeId = first.TypeId, LevelId = first.LevelId, UnconnectedHeight = 3000
        };
        document.Add(second);

        // Two walls of the same type and size: the same cell of each is a different panel.
        Assert.NotEqual(CurtainPanel.IdOf(first.Id, 1, 0), CurtainPanel.IdOf(second.Id, 1, 0));

        // And no panel takes its own wall's id, which would make clicking it select the wall.
        for (var column = 0; column < 40; column++)
        for (var row = 0; row < 12; row++)
            Assert.NotEqual(first.Id, CurtainPanel.IdOf(first.Id, column, row));
        Assert.Equal(second.Id, CurtainPanel.Find(document, CurtainPanel.IdOf(second.Id, 1, 0))!.HostWallId);

        // And a cell the grid does not have is no panel at all.
        Assert.Null(CurtainPanel.At(document, first, column: 40, row: 0));
    }

    [Fact]
    public void APanelIsFoundAgainAfterTheGridMoves()
    {
        var (document, wall) = Storefront();
        CurtainPanel.At(document, wall, 1, 0)!.GetInstanceParameters(document).Single(p => p.Name == "Glass").TrySet("Tinted");

        // The wall is given its own grid with a line taken out: the panel at that place is
        // still the panel at that place, and still frosted glass's neighbour.
        new SetCurtainLayoutCommand(wall, new CurtainGrid(new[] { 2000.0, 4000 }, new[] { 2100.0 }), wall.CurtainPanels).Redo();

        var panel = CurtainPanel.At(document, wall, 1, 0)!;
        Assert.Equal(CurtainGlass.Tinted, panel.Cell(document)!.Glass);
        Assert.Equal(2000, panel.Cell(document)!.From, precision: 6);
    }
}
