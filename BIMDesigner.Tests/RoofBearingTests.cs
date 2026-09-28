using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Infrastructure.Serialization;

namespace BIMDesigner.Tests;

/// <summary>
/// Rafter or Truss (Revit's instance property of a roof picked from walls): where the slope
/// starts. A truss bears on the wall's outside face, rafters on its inside face - or, with
/// Extend to wall core, on the core's faces. The roof's edges stay where they are; the roof
/// rises or falls to start its slope there.
/// </summary>
public class RoofBearingTests
{
    private const double Overhang = 500;

    /// <summary>A 10 m x 6 m box of exterior walls, a hip roof picked off their outsides with an overhang.</summary>
    private static (BimDocument Document, Roof Roof, double Thickness) PickedRoof(bool toCore = false)
    {
        var document = BimDocument.CreateDefault();
        var type = document.TypesOf<WallType>().First(t => t.Name.StartsWith("Exterior"));
        var corners = new[] { new Point2D(0, 0), new Point2D(10000, 0), new Point2D(10000, 6000), new Point2D(0, 6000) };

        var lines = new List<RoofSketchLine>();
        for (var i = 0; i < 4; i++)
        {
            var wall = new Wall
            {
                Start = corners[i], End = corners[(i + 1) % 4], TypeId = type.Id,
                LevelId = document.Levels[0].Id, UnconnectedHeight = 3000
            };
            document.Add(wall);

            var outside = wall.Start.MidpointTo(wall.End) + wall.Direction.PerpendicularLeft() * -600;
            var line = RoofSketch.PickWall(document, wall, outside, Overhang, toCore, true, 30)!;
            lines.Add(line);
            RoofSketch.CloseCorners(lines, line, reach: 2000);
        }

        var check = RoofSketch.Check(lines);
        var roof = new Roof { TypeId = document.TypesOf<RoofType>().First().Id, LevelId = document.Levels[0].Id, HeightOffset = 3000 };
        roof.SetBoundary(check.Boundary);
        roof.SetEdges(check.Edges);
        document.Add(roof);

        return (document, roof, type.Structure.TotalWidth);
    }

    private static readonly double Rise = Math.Tan(Math.PI / 6);

    [Fact]
    public void ATrussBearsOnTheOutsideOfTheWall()
    {
        var (document, roof, thickness) = PickedRoof();
        Assert.Equal(RoofBearing.Truss, roof.Bearing);

        // On the south wall's outside face the underside is at the plate - the roof's base.
        var surface = roof.Surface(document);
        Assert.Equal(3000, surface.HeightAt(new Point2D(5000, -thickness / 2)), precision: 3);

        // And the eave, the overhang further out, is lower by the overhang times the pitch.
        Assert.Equal(-Overhang * Rise, roof.EaveHeight(document, roof.Edges[0]), precision: 3);
    }

    [Fact]
    public void RaftersBearOnTheInsideOfTheWall()
    {
        var (document, roof, thickness) = PickedRoof();
        roof.Bearing = RoofBearing.Rafter;
        var surface = roof.Surface(document);

        // The plate is now on the inside face, so the roof is lower at the outside face by the
        // wall's thickness times the pitch - and its eave by the overhang on top of that.
        Assert.Equal(3000, surface.HeightAt(new Point2D(5000, thickness / 2)), precision: 3);
        Assert.Equal(3000 - thickness * Rise, surface.HeightAt(new Point2D(5000, -thickness / 2)), precision: 3);
        Assert.Equal(-(Overhang + thickness) * Rise, roof.EaveHeight(document, roof.Edges[0]), precision: 3);

        // The roof's outline is where it was drawn.
        Assert.Equal(PickedRoof().Roof.Area, roof.Area, precision: 3);
    }

    [Fact]
    public void WithExtendToCoreRaftersBearOnTheInsideOfTheCore()
    {
        var (document, roof, _) = PickedRoof(toCore: true);
        roof.Bearing = RoofBearing.Rafter;

        var structure = document.GetWallType(document.Walls.First())!.Structure;
        var core = structure.TotalWidth - structure.ExteriorWidth - structure.InteriorWidth;

        Assert.Equal(Overhang + core, roof.BearingInset(document, roof.Edges[0]), precision: 6);
    }

    [Fact]
    public void RafterOrTrussIsAPropertyOfRoofsPickedFromWalls()
    {
        var (document, roof, _) = PickedRoof();
        var parameter = roof.GetInstanceParameters(document).Single(p => p.Name == "Rafter or Truss");

        Assert.Equal("Truss", parameter.DisplayValue);
        Assert.True(parameter.TrySet("Rafter"));
        Assert.Equal(RoofBearing.Rafter, roof.Bearing);

        // Saved and copied with the roof.
        var reloaded = ProjectFile.FromJson(ProjectFile.ToJson(document)).Elements.OfType<Roof>().Single();
        Assert.Equal(RoofBearing.Rafter, reloaded.Bearing);
        Assert.Equal(RoofBearing.Rafter, ((Roof)ElementCopy.Clone(roof)!).Bearing);

        // A roof drawn with lines bears on nothing, so it has no such choice.
        var drawn = new Roof { TypeId = roof.TypeId, LevelId = roof.LevelId };
        drawn.SetBoundary(new[] { new Point2D(0, 0), new Point2D(5000, 0), new Point2D(5000, 5000), new Point2D(0, 5000) });
        drawn.SetEdges(Enumerable.Range(0, 4).Select(_ => new RoofEdge { DefinesSlope = true }));
        Assert.DoesNotContain(drawn.GetInstanceParameters(document), p => p.Name == "Rafter or Truss");
    }
}
