using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;

namespace BIMDesigner.Tests;

/// <summary>
/// Floors behind curtain walls, as they are built: the floor stops short of the glass, the gap
/// packed with fire stop, and the wall has a spandrel band across each floor - a transom at its
/// top and at its underside, opaque panels between - so the slab edge is not seen through it.
/// </summary>
public class CurtainFloorTests
{
    /// <summary>A curtain wall 6 m long up two storeys along y = 0, and the first floor behind it, drawn up to the wall's line.</summary>
    private static (BimDocument Document, Wall Wall, Floor First, CurtainWallType Type) Building()
    {
        var document = BimDocument.CreateDefault();
        var type = document.TypesOf<CurtainWallType>().First(candidate => candidate.Name.Contains("Storefront"));
        var wall = new Wall
        {
            Start = new Point2D(0, 0), End = new Point2D(6000, 0), TypeId = type.Id,
            LevelId = document.Levels[0].Id, UnconnectedHeight = 6000
        };
        document.Add(wall);

        var first = new Floor { LevelId = document.Levels[1].Id, TypeId = document.TypesOf<FloorType>().First().Id };
        first.SetBoundary(new[] { new Point2D(0, 0), new Point2D(6000, 0), new Point2D(6000, 5000), new Point2D(0, 5000) });
        document.Add(first);

        return (document, wall, first, type);
    }

    private static double Thickness(BimDocument document, Slab slab) => document.FindType<SlabType>(slab.TypeId)!.Thickness;

    [Fact]
    public void TheBandRunsFromTheFloorsUndersideToItsTop()
    {
        var (document, wall, first, _) = Building();

        var band = Assert.Single(CurtainSpandrels.Bands(document, wall));
        Assert.Equal(3000 - Thickness(document, first), band.Bottom, 6);
        Assert.Equal(3000, band.Top, 6);
    }

    [Fact]
    public void AndDownToTheCeilingHungUnderIt()
    {
        var (document, wall, _, _) = Building();
        var ceiling = new Ceiling { LevelId = document.Levels[0].Id, TypeId = document.TypesOf<CeilingType>().First().Id, HeightOffset = 2600 };
        ceiling.SetBoundary(new[] { new Point2D(0, 0), new Point2D(6000, 0), new Point2D(6000, 5000), new Point2D(0, 5000) });
        document.Add(ceiling);

        var band = Assert.Single(CurtainSpandrels.Bands(document, wall));
        Assert.Equal(ceiling.GetBottomElevation(document), band.Bottom, 6);
    }

    [Fact]
    public void SpandrelsGoAcrossTheBandAndTheGlassRunsFloorToCeiling()
    {
        var (document, wall, first, _) = Building();
        var history = new UndoStack();

        var command = CurtainSpandrels.Apply(document, wall, out var message);
        Assert.NotNull(command);
        history.Execute(command!);

        // A transom at the floor's top and its underside; none left just beside them.
        var bottom = 3000 - Thickness(document, first);
        var lines = wall.CurtainGrid!.Horizontals;
        Assert.Contains(lines, line => Math.Abs(line - 3000) < 1e-6);
        Assert.Contains(lines, line => Math.Abs(line - bottom) < 1e-6);
        Assert.DoesNotContain(lines, line => line > bottom - CurtainSpandrels.Sliver && line < bottom - 1e-6);

        // Every panel across the band opaque spandrel glass, the rest clear.
        var layout = CurtainLayout.Of(document, wall)!;
        var band = layout.Cells.Where(cell => cell.Bottom >= bottom - 1e-6 && cell.Top <= 3000 + 1e-6).ToList();
        Assert.NotEmpty(band);
        Assert.All(band, cell => Assert.Equal(CurtainGlass.Spandrel, cell.Glass));
        Assert.All(layout.Cells.Except(band), cell => Assert.NotEqual(CurtainGlass.Spandrel, cell.Glass));
        Assert.Contains("spandrel", message);

        history.Undo();
        Assert.Null(wall.CurtainGrid);
    }

    [Fact]
    public void ADoorInTheWallStaysWhereItWas()
    {
        var (document, wall, _, _) = Building();
        var door = new CurtainPanelOverride(1, 0, CurtainPanelKind.Door, document.TypesOf<DoorType>().First(t => t.CurtainPanel).Id);
        wall.CurtainPanels = new[] { door };
        var was = CurtainLayout.Of(document, wall)!.Cells.Single(cell => cell.Kind == CurtainPanelKind.Door);

        CurtainSpandrels.Apply(document, wall, out _)!.Redo();

        var now = CurtainLayout.Of(document, wall)!.Cells.Single(cell => cell.Kind == CurtainPanelKind.Door);
        Assert.Equal(was.From, now.From, 6);
        Assert.Equal(was.Bottom, now.Bottom, 6);
    }

    [Fact]
    public void WithNoFloorBehindItThereIsNothingToHide()
    {
        var (document, wall, first, _) = Building();
        document.Remove(first);

        Assert.Null(CurtainSpandrels.Apply(document, wall, out var why));
        Assert.Contains("No floor", why);
    }

    [Fact]
    public void TheFloorStopsShortOfTheGlassAndTheGapIsFireStopped()
    {
        var (document, wall, first, _) = Building();
        var half = document.GetWallType(wall)!.Structure.TotalWidth / 2;

        // Cut back to the wall's inner face and the safing gap beyond it.
        var regions = SlabEdges.Regions(document, first);
        Assert.Equal(6000 * (5000 - half - SlabEdges.SafingGap), regions.Sum(region => region.Area), 0);
        Assert.Equal(first.Area - 6000 * (half + SlabEdges.SafingGap), SlabEdges.NetArea(document, first), 0);

        // The gap packed with fire stop, the floor's depth.
        var stop = Assert.Single(SlabEdges.FireStops(document, first));
        Assert.Equal(6000 * SlabEdges.SafingGap, Polygon2D.Area(stop), 0);
        Assert.Contains(ModelMeshBuilder.Build(document), mesh => mesh.ElementId == first.Id && mesh.Description == ModelMeshBuilder.FireStopMaterial);

        // In section, the floor starts past the gap, and the fire stop is in it.
        var marker = new SectionMarker { Name = "A", Start = new Point2D(3000, -2000), End = new Point2D(3000, 7000), LevelId = first.LevelId, ViewDepth = 500 };
        document.Add(marker);
        var cut = SectionProjection.Build(document, marker).Pieces.Where(piece => piece.ElementId == first.Id).ToList();
        var edge = 2000 + half + SlabEdges.SafingGap;
        Assert.Equal(edge, cut.Where(piece => piece.Description != ModelMeshBuilder.FireStopMaterial).Min(piece => piece.Bounds.Left), 1);
        Assert.Contains(cut, piece => piece.Description == ModelMeshBuilder.FireStopMaterial && piece.Bounds.Right <= edge + 1);
    }

    [Fact]
    public void TheFloorTheWallStandsOnIsNotCutBack()
    {
        var (document, wall, _, _) = Building();
        var ground = new Floor { LevelId = document.Levels[0].Id, TypeId = document.TypesOf<FloorType>().First().Id };
        ground.SetBoundary(new[] { new Point2D(0, -500), new Point2D(6000, -500), new Point2D(6000, 5000), new Point2D(0, 5000) });
        document.Add(ground);

        Assert.Empty(SlabEdges.CurtainWallsAt(document, ground));
        Assert.Equal(ground.Area, SlabEdges.NetArea(document, ground), 6);
    }

    [Fact]
    public void AFlatRoofWhereAStoreyStandsIsReallyItsFloor()
    {
        var document = BimDocument.CreateDefault();
        var (ground, first) = (document.Levels[0], document.Levels[1]);
        var roof = new Roof { LevelId = ground.Id, TypeId = document.TypesOf<RoofType>().First().Id, HeightOffset = first.Elevation };
        roof.SetBoundary(new[] { new Point2D(0, 0), new Point2D(6000, 0), new Point2D(6000, 5000), new Point2D(0, 5000) });
        document.Add(roof);

        // Nothing stands on the first floor: it is a roof.
        Assert.Null(CurtainSpandrels.FloorPosingAsRoof(document, roof));

        document.Add(new Wall { Start = new Point2D(0, 0), End = new Point2D(6000, 0), TypeId = document.TypesOf<WallType>().First().Id, LevelId = first.Id, UnconnectedHeight = 3000 });
        Assert.Equal(first.Name, CurtainSpandrels.FloorPosingAsRoof(document, roof));
    }
}
