using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;
using BIMDesigner.Infrastructure.Serialization;

namespace BIMDesigner.Tests;

/// <summary>
/// Curtain wall doors: a panel replaced by a door of one of the project's door types, which is
/// how Revit puts a door in a curtain wall.
/// </summary>
public class CurtainWallDoorTests
{
    /// <summary>A 6 m by 3 m storefront along the X axis, and a door type to put in it.</summary>
    private static (BimDocument Document, Wall Wall, DoorType Type) Storefront(DoorType type)
    {
        var document = BimDocument.CreateDefault();
        var curtain = document.TypesOf<CurtainWallType>().Single(t => t.Name.Contains("Storefront"));
        var wall = new Wall
        {
            Start = new Point2D(0, 0), End = new Point2D(6000, 0),
            TypeId = curtain.Id, LevelId = document.Levels[0].Id, UnconnectedHeight = 3000
        };
        document.Add(wall);
        document.AddType(type);
        return (document, wall, type);
    }

    private static List<Mesh3D> PanelMeshes(BimDocument document, Wall wall, DoorType type, params string[] descriptions)
    {
        new SetCurtainLayoutCommand(wall, null, new[] { new CurtainPanelOverride(1, 0, CurtainPanelKind.Door, type.Id) }).Redo();
        return ModelMeshBuilder.BuildWall(document, wall).Where(m => descriptions.Contains(m.Description)).ToList();
    }

    [Fact]
    public void ThePanelIsBuiltAsADoorOfItsOwnType()
    {
        var (document, wall, type) = Storefront(new DoorType("Curtain Entrance", 1000, 2100)
        {
            LeafDesign = DoorLeafDesign.FrenchGlazed, Operation = DoorOperation.DoubleSwing, LeafCount = 2
        });

        var meshes = PanelMeshes(document, wall, type, "Door leaf", "Glazing");
        var leaf = meshes.Single(m => m.Description == "Door leaf");

        // The leaf is the door type's own design - glazing bars and all - not one fixed panel.
        Assert.True(leaf.TriangleCount > 0);
        Assert.Contains(meshes, m => m.Description == "Glazing" && m.TriangleCount > 0);

        // It belongs to the wall: a curtain panel is part of the wall, not an element of its own.
        Assert.Equal(wall.Id, leaf.ElementId);

        // And it fills the panel: mullion to mullion in the second bay, standing on the floor.
        var bounds = leaf.Bounds()!.Value;
        Assert.True(bounds.Min.X >= 1525 - 1e-6 && bounds.Max.X <= 2975 + 1e-6, $"leaf runs {bounds.Min.X:0}..{bounds.Max.X:0}");
        Assert.True(bounds.Min.Z <= 1e-6);
    }

    [Fact]
    public void TheDesignChosenIsTheDesignBuilt()
    {
        int Triangles(DoorLeafDesign design)
        {
            var (document, wall, type) = Storefront(new DoorType("Curtain Entrance", 1000, 2100) { LeafDesign = design });
            return PanelMeshes(document, wall, type, "Door leaf").Sum(m => m.TriangleCount);
        }

        Assert.True(Triangles(DoorLeafDesign.Louvred) > Triangles(DoorLeafDesign.Flush));
    }

    [Fact]
    public void APanelWithNoTypeNamedKeepsTheStorefrontLeaf()
    {
        var (document, wall, _) = Storefront(new DoorType("Curtain Entrance", 1000, 2100));
        new SetCurtainLayoutCommand(wall, null, new[] { new CurtainPanelOverride(1, 0, CurtainPanelKind.Door) }).Redo();

        var meshes = ModelMeshBuilder.BuildWall(document, wall).ToList();
        Assert.Contains(meshes, m => m.Kind == MeshKind.DoorLeaf && m.TriangleCount > 0);
        Assert.DoesNotContain(meshes, m => m.Description == "Door leaf");
    }

    [Fact]
    public void ThePanelStandsOnTheFloorWithNoMullionUnderIt()
    {
        var (document, wall, type) = Storefront(new DoorType("Curtain Entrance", 1000, 2100));
        new SetCurtainLayoutCommand(wall, null, new[] { new CurtainPanelOverride(1, 0, CurtainPanelKind.Door, type.Id) }).Redo();

        var layout = CurtainLayout.Of(document, wall)!;
        var cell = layout.Cells.Single(c => c.Column == 1 && c.Row == 0);
        Assert.Equal(type.Id, cell.DoorTypeId);
        Assert.Equal(0, cell.ClearBottom);
        Assert.DoesNotContain(layout.Mullions, m => !m.IsVertical && m.Bottom == 0 && m.From > cell.From && m.To < cell.To);
    }

    [Fact]
    public void ADoorCutsItsOwnBayInACurtainWallWithNoGrid()
    {
        var document = BimDocument.CreateDefault();
        var plain = new CurtainWallType("Plain") { VerticalLayout = CurtainGridLayout.None, HorizontalLayout = CurtainGridLayout.None };
        document.AddType(plain);

        var wall = new Wall
        {
            Start = new Point2D(0, 0), End = new Point2D(6000, 0),
            TypeId = plain.Id, LevelId = document.Levels[0].Id, UnconnectedHeight = 3000
        };
        document.Add(wall);

        var type = new DoorType("Curtain Entrance", 1000, 2100);
        document.AddType(type);

        // The whole wall is one panel; the door must not swallow it.
        Assert.Single(CurtainLayout.Of(document, wall)!.Cells);

        var placement = CurtainDoors.Place(document, wall, type, along: 3000)!;
        Assert.True(placement.AddedLines);
        Assert.Equal(1000, placement.Cell.ClearTo - placement.Cell.ClearFrom, precision: 6);
        Assert.Equal(2100, placement.Cell.ClearTop - placement.Cell.ClearBottom, precision: 6);
        Assert.Equal(0, placement.Cell.ClearBottom);

        // Glass either side of it and a transom light over it.
        new SetCurtainLayoutCommand(wall, placement.Grid, placement.Panels).Redo();
        var layout = CurtainLayout.Of(document, wall)!;
        Assert.Single(layout.Cells, c => c.Kind == CurtainPanelKind.Door);
        Assert.Equal(5, layout.Cells.Count(c => c.Kind == CurtainPanelKind.Glazed));
    }

    [Fact]
    public void ABayThatIsAlreadyADoorwayIsTakenAsItIs()
    {
        var (document, wall, type) = Storefront(new DoorType("Curtain Entrance", 1000, 2100));

        // A 1.5 m by 1.5 m storefront bay is wide enough for a door but too short: the jambs
        // stay where they are, and the transom rises to the door's head.
        var placement = CurtainDoors.Place(document, wall, type, along: 2000)!;
        Assert.Equal((1525.0, 2975.0), (placement.Cell.ClearFrom, placement.Cell.ClearTo));
        Assert.Equal(2100, placement.Cell.ClearTop, precision: 6);
        Assert.Equal(0, placement.Cell.ClearBottom);
    }

    [Fact]
    public void ThePanelsAlreadyThereKeepTheirBays()
    {
        var (document, wall, type) = Storefront(new DoorType("Curtain Entrance", 1000, 2100));
        new SetCurtainLayoutCommand(wall, null, new[] { new CurtainPanelOverride(3, 1, CurtainPanelKind.Solid) }).Redo();

        var placement = CurtainDoors.Place(document, wall, type, along: 500)!;
        new SetCurtainLayoutCommand(wall, placement.Grid, placement.Panels).Redo();

        // The solid panel is still in the last bay at the top, wherever the new lines put it.
        var layout = CurtainLayout.Of(document, wall)!;
        var solid = layout.Cells.Single(c => c.Kind == CurtainPanelKind.Solid);
        Assert.True(solid.From >= 4500 && solid.Bottom >= 2100, $"the solid panel moved to {solid.From:0}, {solid.Bottom:0}");
        Assert.Single(layout.Cells, c => c.Kind == CurtainPanelKind.Door);
    }

    [Fact]
    public void TheDoorTypeOnAPanelIsSaved()
    {
        var (document, wall, type) = Storefront(new DoorType("Curtain Entrance", 1000, 2100) { LeafDesign = DoorLeafDesign.Glazed });
        new SetCurtainLayoutCommand(wall, null, new[] { new CurtainPanelOverride(1, 0, CurtainPanelKind.Door, type.Id) }).Redo();

        var path = Path.Combine(Path.GetTempPath(), $"bimtest-{Guid.NewGuid():N}{ProjectFile.Extension}");
        try
        {
            ProjectFile.Save(document, path);
            var loaded = ProjectFile.Load(path);
            var panel = loaded.Walls.Single(w => w.Id == wall.Id).CurtainPanels!.Single();

            Assert.Equal(CurtainPanelKind.Door, panel.Kind);
            Assert.Equal(type.Id, panel.DoorTypeId);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
