using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;
using BIMDesigner.Infrastructure.Interoperability;
using Xbim.Ifc4.ProductExtension;
using Xbim.Ifc4.SharedBldgElements;

namespace BIMDesigner.Tests;

/// <summary>Curved walls in the views built from the model: sections, rooms, IFC.</summary>
public class CurvedWallViewTests
{
    private static (BimDocument Document, WallType Exterior) Project()
    {
        var document = BimDocument.CreateDefault();
        return (document, document.TypesOf<WallType>().Single(t => t.Name.StartsWith("Exterior")));
    }

    /// <summary>
    /// A D-shaped room: a straight wall across the top from (3000, 0) to (-3000, 0), closed by a
    /// half circle of radius 3000 round the bottom.
    /// </summary>
    private static (BimDocument Document, WallType Type, Wall Arc, Wall Straight) DShapedRoom()
    {
        var (document, exterior) = Project();

        var arc = new Wall
        {
            Start = new Point2D(-3000, 0), End = new Point2D(3000, 0), Bulge = 1,
            TypeId = exterior.Id, LevelId = document.Levels[0].Id
        };
        var straight = new Wall
        {
            Start = new Point2D(3000, 0), End = new Point2D(-3000, 0),
            TypeId = exterior.Id, LevelId = document.Levels[0].Id
        };

        document.Add(arc);
        document.Add(straight);
        return (document, exterior, arc, straight);
    }

    [Fact]
    public void ASectionAcrossACurveCutsItTwice()
    {
        var (document, _, arc, _) = DShapedRoom();
        var marker = new SectionMarker
        {
            Name = "A",
            Start = new Point2D(-4000, -1500),
            End = new Point2D(4000, -1500),
            LevelId = document.Levels[0].Id
        };
        document.Add(marker);

        var drawing = SectionProjection.Build(document, marker);
        var middles = drawing.Pieces
            .Where(piece => piece.ElementId == arc.Id && piece.Depth == SectionDepth.Cut)
            .Select(piece => (piece.Bounds.Left + piece.Bounds.Right) / 2)
            .ToList();

        // The line enters the circle on the left and leaves on the right, about 2600 mm either
        // side of the centre - which is 4000 along the 8 m line - and crosses open room between.
        var expected = Math.Sqrt(3000.0 * 3000 - 1500.0 * 1500);
        Assert.Contains(middles, x => Math.Abs(x - (4000 - expected)) < 400);
        Assert.Contains(middles, x => Math.Abs(x - (4000 + expected)) < 400);
        Assert.All(middles, x => Assert.True(Math.Abs(x - 4000) > 2000, $"A cut at {x:0} is in the room."));
    }

    [Fact]
    public void ARoomTakesTheShapeOfACurvedWall()
    {
        var (document, type, _, _) = DShapedRoom();

        var result = RoomBoundary.Trace(document, document.Levels[0].Id, new Point2D(0, -1000));
        Assert.True(result.IsEnclosed);

        // Measured to the inner faces: half a circle of radius 3000 less half the wall, less
        // the half of the straight wall that sits inside it.
        var inner = 3000 - type.Width / 2;
        var expected = Math.PI * inner * inner / 2 - 2 * inner * type.Width / 2;

        Assert.Equal(expected, Polygon2D.Area(result.Polygon), expected * 0.01);
    }

    [Fact]
    public void ACurvedWallWithADoorExportsToIfc()
    {
        var (document, _, arc, _) = DShapedRoom();
        document.Add(new Door
        {
            TypeId = document.TypesOf<DoorType>().First().Id,
            LevelId = arc.LevelId,
            HostWallId = arc.Id,
            DistanceAlongWall = arc.Length / 2
        });

        using var model = IfcExport.Build(document);

        Assert.Equal(2, model.Instances.OfType<IfcWall>().Count());
        Assert.Single(model.Instances.OfType<IfcDoor>());
        Assert.Single(model.Instances.OfType<IfcOpeningElement>());
    }

    [Fact]
    public void BendingAWallAndUndoingIt()
    {
        var (document, exterior) = Project();
        var wall = new Wall
        {
            Start = new Point2D(0, 0), End = new Point2D(6000, 0), TypeId = exterior.Id, LevelId = document.Levels[0].Id
        };
        document.Add(wall);

        var bend = new BendWallCommand(wall, 0, WallCurve.BulgeThrough(wall.Start, wall.End, new Point2D(3000, 1000)));
        bend.Redo();

        Assert.True(wall.IsCurved);
        Assert.True(wall.LocationCurve.DistanceTo(new Point2D(3000, 1000)) < 1e-6);
        Assert.Equal("Curve Wall", bend.Name);

        bend.Undo();
        Assert.False(wall.IsCurved);
        Assert.Equal(6000, wall.Length, precision: 9);
    }

    [Fact]
    public void RandomRoomsWithCurvedSidesAlwaysDrawAsWalls()
    {
        var random = new Random(11);

        for (var attempt = 0; attempt < 400; attempt++)
        {
            var document = BimDocument.CreateDefault();
            var types = document.TypesOf<WallType>().ToList();
            var count = random.Next(3, 6);
            var points = Enumerable.Range(0, count)
                .Select(i =>
                {
                    var angle = 2 * Math.PI * i / count + random.NextDouble() * 0.4;
                    var radius = 3000 + random.NextDouble() * 2000;
                    return new Point2D(Math.Round(radius * Math.Cos(angle)), Math.Round(radius * Math.Sin(angle)));
                })
                .ToList();

            for (var i = 0; i < count; i++)
            {
                document.Add(new Wall
                {
                    Start = points[i],
                    End = points[(i + 1) % count],
                    Bulge = random.Next(3) == 0 ? 0 : (random.NextDouble() - 0.5) * 0.8,
                    Flipped = random.Next(2) == 0,
                    TypeId = types[random.Next(types.Count)].Id,
                    LevelId = document.Levels[0].Id
                });
            }

            foreach (var wall in document.Walls)
            {
                var type = document.GetWallType(wall)!;
                var half = type.Width / 2;

                foreach (var slice in WallSlices.Solid(document, wall, type))
                foreach (var (_, start, end) in type.Structure.GetLayerOffsets().Append((null!, 0, type.Width)))
                {
                    var outline = WallJoins.GetBandOutline(wall, type, half - start, half - end, slice.CutFrom, slice.CutTo);

                    Assert.False(OutlineChecks.IsSelfIntersecting(outline),
                        $"Room {string.Join(" ", points)}: wall {wall.Start}-{wall.End} bulge {wall.Bulge:0.00} folds over itself.");
                    Assert.True(Polygon2D.Area(outline) > 0.5);
                }
            }
        }
    }
}
