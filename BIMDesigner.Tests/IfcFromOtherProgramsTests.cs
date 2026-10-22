using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Infrastructure.Interoperability;
using Xbim.Ifc4.Interfaces;
using Xbim.Ifc4.SharedBldgElements;
using Window = BIMDesigner.Core.Architecture.Window;

namespace BIMDesigner.Tests;

/// <summary>
/// IFC as other programs write it - read from files exported by ArchiCAD, Revit, Tekla, BlenderBIM
/// and others, each habit here as it was found in them - coming in as walls, floors, rooms, doors
/// and windows, not as shapes.
/// </summary>
public class IfcFromOtherProgramsTests
{
    private static Wall Single(BimDocument document, double x, double y) =>
        document.Walls.OrderBy(wall => Line2D.DistanceFromSegment(new Point2D(x, y), wall.Start, wall.End)).First();

    [Fact]
    public void AWallComesInOnTheMiddleOfItsLayersWhicheverWayTheyAreSetOnItsAxis()
    {
        using var ifc = new ForeignIfc();

        // Revit: the axis down the middle, the layers set from the far face back - running the negative way from +150.
        var revit = ifc.Wall(0, 0, 6000, 0, 300, 3000);
        ifc.Layers(revit, 300, IfcDirectionSenseEnum.NEGATIVE, 150);

        // ArchiCAD: the axis on one face, the layers running the negative way from it.
        var archicad = ifc.Wall(0, 5000, 6000, 5000, 300, 3000, fromAxis: -150);
        ifc.Layers(archicad, 300, IfcDirectionSenseEnum.NEGATIVE, 0);

        var document = ifc.Read().Document;
        var centre = Single(document, 3000, 0);
        Assert.Equal(0, centre.Start.Y, 1);
        Assert.Equal(0, centre.End.Y, 1);
        var face = Single(document, 3000, 4850);
        Assert.Equal(4850, face.Start.Y, 1);
        Assert.Equal(300, document.GetWallType(face)!.Width, 1);
    }

    [Fact]
    public void AWallClippedToTheRoofOverItComesInShapedToIt()
    {
        using var ifc = new ForeignIfc();

        // A gable: a wall 10 m long, cut down by the two slopes of a roof - 1.5 m at the eaves, 4 m at the ridge.
        var body = ifc.Extrusion(6000, null, (0, -150), (10000, -150), (10000, 150), (0, 150));
        var left = ifc.HalfSpace(0, 0, 1500, -2500, 0, 5000);
        var right = ifc.HalfSpace(10000, 0, 1500, 2500, 0, 5000);
        ifc.Contained(ifc.New<IfcWall>(wall =>
        {
            wall.ObjectPlacement = ifc.Placed();
            wall.Representation = ifc.Shape(ifc.Clipped(ifc.Clipped(body, left), right), ifc.Polyline((0, 0), (10000, 0)));
        }));

        var result = ifc.Read();
        var gable = Assert.Single(result.Document.Walls);
        Assert.Equal(1, result["Walls shaped to a roof"]);
        var profile = WallProfile.Of(result.Document, gable)!;
        Assert.Equal(4000, WallProfile.Top(profile), 1);
        Assert.Contains(profile, p => Math.Abs(p.X - 5000) < 1 && Math.Abs(p.Y - 4000) < 1);
        Assert.Contains(profile, p => Math.Abs(p.X) < 1 && Math.Abs(p.Y - 1500) < 1);
        Assert.Contains(profile, p => Math.Abs(p.X - 10000) < 1 && Math.Abs(p.Y - 1500) < 1);
    }

    [Fact]
    public void AWallThatIsASolidComesInAsAWallAndTheWindowStandingInItAsAWindow()
    {
        using var ifc = new ForeignIfc();
        ifc.Contained(ifc.New<IfcWall>(wall =>
        {
            wall.ObjectPlacement = ifc.Placed();
            wall.Representation = ifc.Shape(ifc.FacetedPrism(0, 2700, (0, -100), (5000, -100), (5000, 100), (0, 100)));
        }));

        // A window that does not say which wall it is in.
        ifc.Contained(ifc.New<IfcWindow>(window =>
        {
            window.Name = "Casement";
            window.ObjectPlacement = ifc.Placed();
            window.OverallWidth = 1000;
            window.OverallHeight = 1200;
            window.Representation = ifc.Shape(ifc.FacetedPrism(900, 2100, (2000, -50), (3000, -50), (3000, 50), (2000, 50)));
        }));

        var document = ifc.Read().Document;
        var wall = Assert.Single(document.Walls);
        Assert.Equal((5000.0, 200.0, 2700.0), (Math.Round(wall.Length), document.GetWallType(wall)!.Width, wall.UnconnectedHeight));
        var placed = Assert.Single(document.All<Window>());
        Assert.Equal(wall.Id, placed.HostWallId);
        Assert.Equal(2500, placed.DistanceAlongWall, 1);
        Assert.Equal(900, placed.SillHeight, 1);
    }

    [Fact]
    public void AFloorAndARoomThatAreSolidsComeInAsThemselves()
    {
        using var ifc = new ForeignIfc();
        (double, double)[] ell = { (0, 0), (6000, 0), (6000, 2000), (2000, 2000), (2000, 5000), (0, 5000) };
        ifc.Contained(ifc.New<IfcSlab>(slab =>
        {
            slab.PredefinedType = IfcSlabTypeEnum.FLOOR;
            slab.ObjectPlacement = ifc.Placed();
            slab.Representation = ifc.Shape(ifc.FacetedPrism(-200, 0, ell));
        }));
        ifc.Space("Kitchen", 2500, ell);

        var document = ifc.Read().Document;
        var floor = Assert.Single(document.All<Floor>());
        Assert.Equal(6, floor.Boundary.Count);
        Assert.Equal(6000 * 2000 + 2000 * 3000, Polygon2D.Area(floor.Boundary), 1);

        // Its tag inside the L, not in the corner it turns round.
        var room = Assert.Single(document.All<Room>());
        Assert.True(Polygon2D.Contains(floor.Boundary, room.Location));
    }

    [Fact]
    public void RoomsOpenToEachOtherArePartedWhereTheFileHasNoWall()
    {
        using var ifc = new ForeignIfc();

        // A box of four walls, two rooms in it - a kitchen open to the living room, no wall between.
        foreach (var (x0, y0, x1, y1) in new[] { (0.0, 0.0, 8000.0, 0.0), (8000, 0, 8000, 4000), (8000, 4000, 0, 4000), (0, 4000, 0, 0) })
            ifc.Wall(x0, y0, x1, y1, 200, 2700);
        ifc.Space("Kitchen", 2500, (100, 100), (4000, 100), (4000, 3900), (100, 3900));
        ifc.Space("Living", 2500, (4000, 100), (7900, 100), (7900, 3900), (4000, 3900));

        var result = ifc.Read();
        var document = result.Document;
        Assert.Equal(1, result["Room separators"]);
        var separator = Assert.Single(document.All<RoomSeparator>());
        Assert.Equal(4000, separator.Start.X, 1);

        foreach (var room in document.All<Room>())
        {
            var boundary = room.GetBoundary(document);
            Assert.True(boundary.IsEnclosed, room.Name);
            Assert.Equal(3900 * 3800, boundary.Area, 0);
        }
    }

    [Fact]
    public void RevitsAdvancedSolidsAreRead()
    {
        using var ifc = new ForeignIfc();
        ifc.Contained(ifc.New<IfcBuildingElementProxy>(proxy =>
        {
            proxy.Name = "Cabinet";
            proxy.ObjectPlacement = ifc.Placed(1000, 1000);
            proxy.Representation = ifc.Shape(ifc.AdvancedBox(0, 0, 0, 600, 400, 900));
        }));

        var document = ifc.Read().Document;
        var cabinet = Assert.Single(document.All<Component>());
        var type = document.FindType<ComponentType>(cabinet.TypeId)!;
        Assert.Equal((600.0, 400.0, 900.0), (Math.Round(type.Width), Math.Round(type.Depth), Math.Round(type.Height)));
    }

    [Fact]
    public void AShapeIsCutWhereItIsClippedAndBoxedSquareToItself()
    {
        using var ifc = new ForeignIfc();

        // A beam 3 m long cut off at 2 m.
        var beam = ifc.Extrusion(300, null, (0, 0), (3000, 0), (3000, 200), (0, 200));
        ifc.Contained(ifc.New<IfcBeam>(b =>
        {
            b.ObjectPlacement = ifc.Placed();
            b.Representation = ifc.Shape(ifc.Clipped(beam, ifc.HalfSpace(2000, 0, 0, 1, 0, 0)));
        }));

        // A bench set at 30 degrees within a frame that is not.
        var turned = ifc.Axis(5000, 5000, 0, (0, 0, 1), (Math.Cos(Math.PI / 6), Math.Sin(Math.PI / 6), 0));
        ifc.Contained(ifc.New<IfcBuildingElementProxy>(bench =>
        {
            bench.Name = "Bench";
            bench.ObjectPlacement = ifc.Placed();
            bench.Representation = ifc.Shape(ifc.Extrusion(450, turned, (0, 0), (2000, 0), (2000, 500), (0, 500)));
        }));

        var document = ifc.Read().Document;
        var types = document.All<Component>().Select(c => (Component: c, Type: document.FindType<ComponentType>(c.TypeId)!)).ToList();
        var cut = types.Single(t => t.Type.Name.StartsWith("Beam"));
        Assert.Equal(2000, Math.Max(cut.Type.Width, cut.Type.Depth), 1);

        var set = types.Single(t => t.Type.Name.Contains("Bench"));
        Assert.Equal((2000.0, 500.0), (Math.Round(Math.Max(set.Type.Width, set.Type.Depth)), Math.Round(Math.Min(set.Type.Width, set.Type.Depth))));
        Assert.Equal(0, ((set.Component.Rotation - 30) % 90 + 90) % 90, 1);
    }

    [Fact]
    public void AFlippedWallGoesOutCentredOnItsAxisAsTheStandardHasIt()
    {
        var document = BimDocument.CreateDefault();
        var type = document.TypesOf<WallType>().First(t => t.Function == WallFunction.Exterior);
        document.Add(new Wall { Start = new Point2D(0, 0), End = new Point2D(5000, 0), TypeId = type.Id, LevelId = document.Levels[0].Id, UnconnectedHeight = 3000, Flipped = true });

        using var model = IfcExport.Build(document);
        var usage = model.Instances.OfType<IIfcMaterialLayerSetUsage>().Single();
        Assert.Equal(IfcDirectionSenseEnum.NEGATIVE, usage.DirectionSense);
        Assert.Equal(type.Width / 2, (double)usage.OffsetFromReferenceLine, 3);

        var back = Assert.Single(IfcImport.Read(model).Document.Walls);
        Assert.Equal(0, back.Start.Y, 1);
        Assert.True(back.Flipped);
    }
}
