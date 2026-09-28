using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Tests;

/// <summary>
/// Roofs sketched as Revit sketches them: picked off the walls they sit on, with an overhang,
/// closed into a loop, checked before they become a roof, and kept on their walls afterwards.
/// </summary>
public class RoofSketchTests
{
    private const double Half = 165;     // the template's 330 mm exterior wall
    private const double Overhang = 500;

    /// <summary>A 10 m x 6 m box of exterior walls, 3 m high, and the document it is in.</summary>
    private static (BimDocument Document, List<Wall> Walls) Box()
    {
        var document = BimDocument.CreateDefault();
        var type = document.TypesOf<WallType>().First(t => t.Name.StartsWith("Exterior"));
        var level = document.Levels[0].Id;

        var corners = new[]
        {
            new Point2D(0, 0), new Point2D(10000, 0), new Point2D(10000, 6000), new Point2D(0, 6000)
        };

        var walls = new List<Wall>();
        for (var i = 0; i < corners.Length; i++)
        {
            var wall = new Wall
            {
                Start = corners[i], End = corners[(i + 1) % corners.Length],
                TypeId = type.Id, LevelId = level, UnconnectedHeight = 3000
            };

            document.Add(wall);
            walls.Add(wall);
        }

        return (document, walls);
    }

    /// <summary>A point just outside the middle of a wall, which is where a roof line wants picking from.</summary>
    private static Point2D Outside(Wall wall)
    {
        var middle = wall.Start.MidpointTo(wall.End);
        var outward = wall.Direction.PerpendicularLeft() * -1;   // the box is drawn anticlockwise
        return middle + outward * 600;
    }

    private static List<RoofSketchLine> PickAll(BimDocument document, IEnumerable<Wall> walls, double overhang = Overhang)
    {
        var lines = new List<RoofSketchLine>();

        foreach (var wall in walls)
        {
            var line = RoofSketch.PickWall(document, wall, Outside(wall), overhang, false, true, 30)!;
            lines.Add(line);
            RoofSketch.CloseCorners(lines, line, reach: 2000);
        }

        return lines;
    }

    [Fact]
    public void APickedLineRunsAlongTheOutsideFaceOfTheWallPlusTheOverhang()
    {
        var (document, walls) = Box();
        var south = walls[0];

        var line = RoofSketch.PickWall(document, south, Outside(south), Overhang, false, true, 30)!;

        // Along the south face, 165 mm out from the centreline, and 500 mm beyond that.
        Assert.Equal(-Half - Overhang, line.Start.Y, precision: 6);
        Assert.Equal(-Half - Overhang, line.End.Y, precision: 6);
        Assert.Equal(south.Id, line.Edge.WallId);
        Assert.True(line.Edge.DefinesSlope);

        // Picked from inside the wall, it goes on the inside face instead: the side the
        // cursor is on is the side that is meant.
        var inside = RoofSketch.PickWall(document, south, new Point2D(5000, 800), 0, false, true, 30)!;
        Assert.Equal(Half, inside.Start.Y, precision: 6);
    }

    [Fact]
    public void ExtendToCoreMeasuresTheOverhangFromTheCore()
    {
        var (document, walls) = Box();
        var south = walls[0];
        var type = document.GetWallType(south)!;

        var line = RoofSketch.PickWall(document, south, Outside(south), Overhang, true, true, 30)!;

        // The layers outside the core are not counted: the overhang starts at the core face.
        // This box is drawn anticlockwise, so its walls' exterior layers face into it and the
        // side picked from outside carries the interior finish - the tool goes by the side the
        // cursor is on, not by which layers are called exterior.
        var coreFace = Half - type.Structure.InteriorWidth;
        Assert.Equal(-coreFace - Overhang, line.Start.Y, precision: 6);

        // And from the other side, the other face's layers are the ones left out.
        var fromInside = RoofSketch.PickWall(document, south, new Point2D(5000, 800), 0, true, true, 30)!;
        Assert.Equal(Half - type.Structure.ExteriorWidth, fromInside.Start.Y, precision: 6);
    }

    [Fact]
    public void FourPickedWallsCloseIntoALoopByThemselves()
    {
        var (document, walls) = Box();
        var lines = PickAll(document, walls);

        var check = RoofSketch.Check(lines);
        Assert.True(check.IsValid, check.Problem);

        // The outline is the box grown by the wall's half thickness and the overhang all round.
        var grow = Half + Overhang;
        var expected = new[]
        {
            new Point2D(-grow, -grow), new Point2D(10000 + grow, -grow),
            new Point2D(10000 + grow, 6000 + grow), new Point2D(-grow, 6000 + grow)
        };

        Assert.Equal(4, check.Boundary.Count);
        foreach (var corner in expected)
            Assert.Contains(check.Boundary, point => point.DistanceTo(corner) < 1e-6);

        Assert.All(check.Edges, edge => Assert.True(edge.DefinesSlope));
    }

    [Fact]
    public void AnOpenSketchIsRefusedAndTheOpenEndIsPointedAt()
    {
        var (document, walls) = Box();
        var lines = PickAll(document, walls.Take(3));

        var check = RoofSketch.Check(lines);

        Assert.False(check.IsValid);
        Assert.Contains("open", check.Problem);
        Assert.NotEmpty(check.Culprits);
    }

    [Fact]
    public void ACrossedSketchIsRefused()
    {
        var edge = new RoofEdge();
        var lines = new List<RoofSketchLine>
        {
            new(new Point2D(0, 0), new Point2D(4000, 4000), edge.Copy()),
            new(new Point2D(4000, 4000), new Point2D(4000, 0), edge.Copy()),
            new(new Point2D(4000, 0), new Point2D(0, 4000), edge.Copy()),
            new(new Point2D(0, 4000), new Point2D(0, 0), edge.Copy())
        };

        var check = RoofSketch.Check(lines);

        Assert.False(check.IsValid);
        Assert.Contains("cross", check.Problem);
        Assert.Equal(2, check.Culprits.Count);
    }

    [Fact]
    public void ALoopInsideIsAnOpeningAndALoopBesideIsRefused()
    {
        var edge = new RoofEdge();
        List<RoofSketchLine> Square(double x0, double y0, double size) => new()
        {
            new(new Point2D(x0, y0), new Point2D(x0 + size, y0), edge.Copy()),
            new(new Point2D(x0 + size, y0), new Point2D(x0 + size, y0 + size), edge.Copy()),
            new(new Point2D(x0 + size, y0 + size), new Point2D(x0, y0 + size), edge.Copy()),
            new(new Point2D(x0, y0 + size), new Point2D(x0, y0), edge.Copy())
        };

        // A square inside the outline is a hole through the roof, as in Revit.
        var inside = RoofSketch.Check(Square(0, 0, 10000).Concat(Square(4000, 4000, 1000)).ToList());
        Assert.True(inside.IsValid, inside.Problem);
        Assert.Single(inside.Openings);

        // One beside it would be a second roof, which one sketch cannot make.
        var beside = RoofSketch.Check(Square(0, 0, 10000).Concat(Square(12000, 0, 1000)).ToList());
        Assert.False(beside.IsValid);
        Assert.Contains("separate loops", beside.Problem);
    }

    [Fact]
    public void AClockwiseSketchKeepsEachEdgeOnItsOwnLine()
    {
        // Drawn clockwise, with only the first line sloping: after the outline is turned
        // anticlockwise, the slope must still be on that same line.
        var slope = new RoofEdge { DefinesSlope = true };
        var flat = new RoofEdge();
        var lines = new List<RoofSketchLine>
        {
            new(new Point2D(0, 0), new Point2D(0, 6000), slope),
            new(new Point2D(0, 6000), new Point2D(10000, 6000), flat.Copy()),
            new(new Point2D(10000, 6000), new Point2D(10000, 0), flat.Copy()),
            new(new Point2D(10000, 0), new Point2D(0, 0), flat.Copy())
        };

        var check = RoofSketch.Check(lines);
        Assert.True(check.IsValid, check.Problem);
        Assert.True(Polygon2D.SignedArea(check.Boundary) > 0);

        var sloping = Enumerable.Range(0, check.Edges.Count).Single(i => check.Edges[i].DefinesSlope);
        var from = check.Boundary[sloping];
        var to = check.Boundary[(sloping + 1) % check.Boundary.Count];

        // It is still the west side, x = 0.
        Assert.Equal(0, from.X, precision: 6);
        Assert.Equal(0, to.X, precision: 6);
    }

    [Fact]
    public void TheRoofBearsOnTheWallAndTheOverhangDropsBelowIt()
    {
        var (document, walls) = Box();
        var check = RoofSketch.Check(PickAll(document, walls));

        var roof = new Roof
        {
            TypeId = document.TypesOf<RoofType>().First().Id,
            LevelId = document.Levels[0].Id,
            HeightOffset = 3000
        };

        roof.SetBoundary(check.Boundary);
        roof.SetEdges(check.Edges);
        document.Add(roof);

        // At the wall's outer face the underside is at the top of the wall: it sits on it.
        Assert.Equal(3000, roof.UndersideAt(document, new Point2D(5000, -Half)), precision: 3);

        // Out at the eave, 500 mm further, it has dropped by 500 × tan 30°.
        var drop = Overhang * Math.Tan(30 * Math.PI / 180);
        Assert.Equal(3000 - drop, roof.UndersideAt(document, new Point2D(5000, -Half - Overhang)), precision: 3);
    }

    [Fact]
    public void ARoofStandsOnItsBaseRatherThanHangingFromIt()
    {
        var document = BimDocument.CreateDefault();
        var type = document.TypesOf<RoofType>().First();
        var roof = new Roof { TypeId = type.Id, LevelId = document.Levels[0].Id, HeightOffset = 3000 };
        roof.SetBoundary(new[] { new Point2D(0, 0), new Point2D(5000, 0), new Point2D(5000, 5000), new Point2D(0, 5000) });
        document.Add(roof);

        // A flat roof based at the top of the walls has its underside there and its build-up
        // above - the other way up from a floor, as a roof is.
        Assert.Equal(3000, roof.GetBottomElevation(document), precision: 6);
        Assert.Equal(3000 + type.Thickness, roof.GetTopElevation(document), precision: 6);
    }

    [Fact]
    public void MovingAWallTakesTheRoofEdgeWithIt()
    {
        var (document, walls) = Box();
        var check = RoofSketch.Check(PickAll(document, walls));

        var roof = new Roof { TypeId = document.TypesOf<RoofType>().First().Id, LevelId = document.Levels[0].Id };
        roof.SetBoundary(check.Boundary);
        roof.SetEdges(check.Edges);
        document.Add(roof);

        var areaBefore = roof.Area;

        // Push the east wall out by a metre.
        var east = walls[1];
        east.Start += new Vector2D(1000, 0);
        east.End += new Vector2D(1000, 0);
        walls[0].End += new Vector2D(1000, 0);
        walls[2].Start += new Vector2D(1000, 0);

        Assert.True(RoofSketch.FollowWalls(document));

        // The east edge has gone with its wall: the roof is a metre longer, overhang intact.
        var grow = Half + Overhang;
        Assert.Contains(roof.Boundary, point => Math.Abs(point.X - (11000 + grow)) < 1e-6);
        Assert.Equal(areaBefore + 1000 * (6000 + 2 * grow), roof.Area, precision: 0);

        // Nothing moved the second time round.
        Assert.False(RoofSketch.FollowWalls(document));
    }

    [Fact]
    public void AFinishedRoofOpensAgainAsTheSameSketch()
    {
        var (document, walls) = Box();
        var check = RoofSketch.Check(PickAll(document, walls));

        var roof = new Roof();
        roof.SetBoundary(check.Boundary);
        roof.SetEdges(check.Edges);

        var reopened = RoofSketch.LinesOf(roof);
        var again = RoofSketch.Check(reopened);

        Assert.True(again.IsValid, again.Problem);
        Assert.Equal(roof.Boundary, again.Boundary);
        Assert.Equal(roof.Edges.Select(edge => edge.WallId), again.Edges.Select(edge => edge.WallId));
    }
}
