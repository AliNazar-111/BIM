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

        var door = panel.GetInstanceParameters(document).Single(p => p.Name == "Door Type");
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
        Assert.True(bottom.GetInstanceParameters(document).Single(p => p.Name == "Door Type").TrySet(door.Name));

        var cell = bottom.Cell(document)!;
        Assert.Equal(CurtainPanelKind.Door, cell.Kind);
        Assert.Equal(door.Id, cell.OpeningTypeId);
        Assert.Equal(0, cell.ClearBottom);

        // A panel off the floor cannot be a door, as everywhere else.
        var upper = CurtainPanel.At(document, wall, column: 2, row: 1)!;
        upper.GetInstanceParameters(document).Single(p => p.Name == "Door Type").TrySet(door.Name);
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
        panel.GetInstanceParameters(document).Single(p => p.Name == "Door Type").TrySet(type.Name);

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
        panel.GetInstanceParameters(document).Single(p => p.Name == "Door Type").TrySet(type.Name);

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
    public void APanelSaysWhereItIsAndSlidesAlongTheWall()
    {
        var (document, wall) = Storefront();
        var panel = CurtainPanel.At(document, wall, column: 1, row: 0)!;
        var before = panel.Cell(document)!;

        var along = panel.GetInstanceParameters(document).Single(p => p.Name == "Distance Along Wall");
        Assert.Equal((before.ClearFrom + before.ClearTo) / 2, (double)along.Value!, precision: 6);

        var height = panel.GetInstanceParameters(document).Single(p => p.Name == "Height Above Floor");
        Assert.Equal(before.ClearBottom, (double)height.Value!, precision: 6);

        // Slid along, the bay goes with it and keeps its width.
        Assert.True(along.TrySet(1800.0));
        var after = panel.Cell(document)!;
        Assert.Equal(1800, (after.ClearFrom + after.ClearTo) / 2, precision: 6);
        Assert.Equal(before.ClearTo - before.ClearFrom, after.ClearTo - after.ClearFrom, precision: 6);

        // Not so far that it runs over its neighbour.
        Assert.False(panel.GetInstanceParameters(document).Single(p => p.Name == "Distance Along Wall").TrySet(5800.0));
    }

    [Fact]
    public void APanelIsSizedByMovingTheLinesRoundIt()
    {
        var (document, wall) = Storefront();
        var panel = CurtainPanel.At(document, wall, column: 1, row: 0)!;
        var before = panel.Cell(document)!;

        var width = panel.GetInstanceParameters(document).Single(p => p.Name == "Width");
        Assert.False(width.IsReadOnly);
        Assert.True(width.TrySet(2000.0));

        var after = panel.Cell(document)!;
        Assert.Equal(2000, after.ClearTo - after.ClearFrom, precision: 6);

        // The bay grew about its own middle, so the bays either side gave up half each.
        Assert.Equal((before.From + before.To) / 2, (after.From + after.To) / 2, precision: 6);

        // And taller, which moves the transom over it.
        Assert.True(panel.GetInstanceParameters(document).Single(p => p.Name == "Height").TrySet(2100.0));
        Assert.Equal(2100, panel.Cell(document)!.ClearTop - panel.Cell(document)!.ClearBottom, precision: 6);

        // Not so wide that its neighbour is squeezed out of existence.
        Assert.False(panel.GetInstanceParameters(document).Single(p => p.Name == "Width").TrySet(5900.0));
        Assert.Equal(2000, panel.Cell(document)!.ClearTo - panel.Cell(document)!.ClearFrom, precision: 6);
    }

    [Fact]
    public void AWindowCutIntoACurtainWallIsItsOwnElement()
    {
        var (document, wall) = Storefront();
        var type = document.TypesOf<WindowType>().First(t => t.Name.StartsWith("Hopper"));

        var window = new BIMDesigner.Core.Architecture.Window
        {
            TypeId = type.Id, LevelId = wall.LevelId, HostWallId = wall.Id,
            DistanceAlongWall = 2250, SillHeight = 400
        };
        document.Add(window);

        // It is an element in its own right, so it is selected, sized and moved like any other.
        var meshes = ModelMeshBuilder.BuildWall(document, wall).Where(m => m.ElementId == window.Id).ToList();
        Assert.NotEmpty(meshes);
        Assert.Contains(meshes, m => m.Description == "Sash");
        Assert.Contains(document.Elements, e => e.Id == window.Id);

        // Measured on the sash, since the sill board stands a little below the sill itself.
        var sash = meshes.Single(m => m.Description == "Sash").Bounds()!.Value;
        Assert.True(sash.Min.Z >= 400, "the sash starts below the sill it was given");
        Assert.Equal(2250, (sash.Min.X + sash.Max.X) / 2, precision: 0);

        // Moved by its own properties, within the panel it is in, it goes where it is put.
        Assert.True(window.GetInstanceParameters(document).Single(p => p.Name == "Sill Height").TrySet(800.0));
        Assert.True(window.GetInstanceParameters(document).Single(p => p.Name == "Distance Along Wall").TrySet(2500.0));

        var moved = ModelMeshBuilder.BuildWall(document, wall)
            .Single(m => m.ElementId == window.Id && m.Description == "Sash").Bounds()!.Value;
        Assert.True(moved.Min.Z > 700, "the sash did not rise when its sill did");
        Assert.True((moved.Min.X + moved.Max.X) / 2 > 2400);
    }

    [Fact]
    public void AWindowStaysInThePanelItIsIn()
    {
        var (document, wall) = Storefront();
        var type = document.TypesOf<WindowType>().First(t => t.Name.StartsWith("Hopper"));

        var window = new BIMDesigner.Core.Architecture.Window
        {
            TypeId = type.Id, LevelId = wall.LevelId, HostWallId = wall.Id,
            DistanceAlongWall = 2250, SillHeight = 600
        };
        document.Add(window);

        var cell = CurtainLayout.Of(document, wall)!.Cells.Single(c => c.Column == 1 && c.Row == 0);

        // Pushed at the next bay, it stops at the edge of its own rather than crossing the
        // mullion into it.
        Assert.True(window.GetInstanceParameters(document).Single(p => p.Name == "Distance Along Wall").TrySet(5000.0));
        Assert.Equal(cell.ClearTo - type.Width / 2, window.DistanceAlongWall, precision: 6);

        // And the same up and down: it stays under the transom over it.
        Assert.True(window.GetInstanceParameters(document).Single(p => p.Name == "Sill Height").TrySet(2800.0));
        Assert.Equal(cell.ClearTop - type.Height, window.SillHeight, precision: 6);

        // In an ordinary wall there is no panel to stay in, so it goes where it is put.
        var plain = new Wall
        {
            Start = new Point2D(0, 5000), End = new Point2D(6000, 5000),
            TypeId = document.TypesOf<WallType>().First(t => t.Name.StartsWith("Exterior")).Id,
            LevelId = wall.LevelId, UnconnectedHeight = 3000
        };
        document.Add(plain);

        var loose = new BIMDesigner.Core.Architecture.Window
        {
            TypeId = type.Id, LevelId = plain.LevelId, HostWallId = plain.Id,
            DistanceAlongWall = 1000, SillHeight = 900
        };
        document.Add(loose);

        Assert.True(loose.GetInstanceParameters(document).Single(p => p.Name == "Distance Along Wall").TrySet(4000.0));
        Assert.Equal(4000, loose.DistanceAlongWall, precision: 6);
    }

    [Fact]
    public void TheGlassAndMullionsGiveWayToAWindowCutIntoTheWall()
    {
        var (document, wall) = Storefront();
        var type = document.TypesOf<WindowType>().First(t => t.Name.StartsWith("Casement Double"));

        double Glass() => ModelMeshBuilder.BuildWall(document, wall)
            .Where(m => m.Kind == MeshKind.Glazing && m.ElementId != Guid.Empty)
            .Sum(m => m.TriangleCount);

        var mullionsBefore = ModelMeshBuilder.BuildWall(document, wall)
            .Where(m => m.Kind == MeshKind.Mullion).Sum(m => m.TriangleCount);

        // Set across a mullion on purpose: the bar stops at the window instead of running
        // through the middle of its glass.
        document.Add(new BIMDesigner.Core.Architecture.Window
        {
            TypeId = type.Id, LevelId = wall.LevelId, HostWallId = wall.Id,
            DistanceAlongWall = 1500, SillHeight = 900
        });

        var mullionsAfter = ModelMeshBuilder.BuildWall(document, wall)
            .Where(m => m.Kind == MeshKind.Mullion).Sum(m => m.TriangleCount);

        Assert.NotEqual(mullionsBefore, mullionsAfter);

        // And no pane of the wall's own glass is left inside the window's opening.
        var layout = CurtainLayout.Of(document, wall)!;
        var holes = new[] { (1500 - type.Width / 2, 1500 + type.Width / 2, 900.0, 900 + type.Height) };
        foreach (var cell in layout.Cells)
        foreach (var pane in CurtainOpening.Panes(cell, holes))
        {
            var acrossHole = pane.From < holes[0].Item2 - 1 && pane.To > holes[0].Item1 + 1;
            var upHole = pane.Bottom < holes[0].Item4 - 1 && pane.Top > holes[0].Item3 + 1;
            Assert.False(acrossHole && upHole, $"glass left at {pane.From:0}..{pane.To:0} by {pane.Bottom:0}..{pane.Top:0}");
        }
    }

    [Fact]
    public void AWindowSavedAsAPanelBecomesAWindowCutIntoTheWall()
    {
        var (document, wall) = Storefront();
        var type = document.TypesOf<WindowType>().First(t => t.Name.StartsWith("Hopper"));
        var cell = CurtainPanel.At(document, wall, 2, 1)!.Cell(document)!;

        // A project saved when a window was something a panel could be filled with.
        var json = BIMDesigner.Infrastructure.Serialization.ProjectFile.ToJson(document)
            .Replace("\"curtainPanels\": null",
                "\"curtainPanels\": [ { \"column\": 2, \"row\": 1, \"kind\": \"Window\", \"openingTypeId\": \"" + type.Id + "\" } ]");

        var path = Path.Combine(Path.GetTempPath(), "bimtest-" + Guid.NewGuid().ToString("N") + BIMDesigner.Infrastructure.Serialization.ProjectFile.Extension);
        try
        {
            File.WriteAllText(path, json);
            var loaded = BIMDesigner.Infrastructure.Serialization.ProjectFile.Load(path);

            // It comes back as a window in its own right, where the panel had it.
            var window = loaded.Elements.OfType<BIMDesigner.Core.Architecture.Window>().Single();
            Assert.Equal(type.Id, window.TypeId);
            Assert.Equal(wall.Id, window.HostWallId);
            Assert.Equal((cell.ClearFrom + cell.ClearTo) / 2, window.DistanceAlongWall, precision: 6);
            Assert.Equal((cell.ClearBottom + cell.ClearTop) / 2 - type.Height / 2, window.SillHeight, precision: 6);

            // And the panel it was filling is a pane of glass again.
            Assert.Null(loaded.Walls.Single(w => w.Id == wall.Id).CurtainPanels);
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
