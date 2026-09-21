using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Infrastructure.Serialization;

namespace BIMDesigner.Tests;

/// <summary>
/// Junctions of many walls, the join the user asks for at each end, clean outlines, and layers
/// wrapping round exposed ends and openings.
/// </summary>
public class WallJoinControlTests
{
    private static (BimDocument Document, WallType Partition, WallType Exterior) Project()
    {
        var document = BimDocument.CreateDefault();

        return (
            document,
            document.TypesOf<WallType>().Single(t => t.Name.StartsWith("Interior")),
            document.TypesOf<WallType>().Single(t => t.Name.StartsWith("Exterior")));
    }

    private static Wall Add(BimDocument document, WallType type, Point2D start, Point2D end)
    {
        var wall = new Wall { Start = start, End = end, TypeId = type.Id, LevelId = document.Levels[0].Id };
        document.Add(wall);
        return wall;
    }

    private static Point2D Towards(Point2D from, double degrees, double length)
    {
        var radians = degrees * Math.PI / 180;
        return new Point2D(from.X + Math.Cos(radians) * length, from.Y + Math.Sin(radians) * length);
    }

    private static WallCut EndAt(BimDocument document, Wall wall, Point2D joint)
    {
        var (start, end) = WallJoins.GetEndCuts(document, wall, document.GetWallType(wall)!);
        return wall.Start.DistanceTo(joint) <= 1 ? start : end;
    }

    // ---- junctions of many walls ------------------------------------------------

    [Fact]
    public void ACrossOfSplitWallsIsOneRunWithThePartitionsStoppingAgainstIt()
    {
        var (document, partition, exterior) = Project();
        var joint = new Point2D(5000, 5000);

        var east = Add(document, exterior, joint, new Point2D(10000, 5000));
        var west = Add(document, exterior, new Point2D(0, 5000), joint);
        var north = Add(document, partition, joint, new Point2D(5000, 10000));
        var south = Add(document, partition, new Point2D(5000, 0), joint);

        // The heavier wall carries through; the partitions stop against its faces.
        Assert.Equal(WallEndCondition.Continues, EndAt(document, east, joint).Condition);
        Assert.Equal(WallEndCondition.Continues, EndAt(document, west, joint).Condition);
        Assert.Equal(WallEndCondition.Butt, EndAt(document, north, joint).Condition);
        Assert.Equal(WallEndCondition.Butt, EndAt(document, south, joint).Condition);

        var (most, covered) = OutlineChecks.Coverage(document, document.Walls, joint, exterior.Width);
        Assert.Equal(1, most);
        Assert.True(covered);
    }

    [Fact]
    public void TwoWallsMeetingFromOppositeSidesOfAPassingWallBothStopAgainstIt()
    {
        var (document, partition, exterior) = Project();
        var joint = new Point2D(5000, 0);

        Add(document, exterior, new Point2D(0, 0), new Point2D(10000, 0));
        var above = Add(document, partition, joint, new Point2D(5000, 4000));
        var below = Add(document, partition, new Point2D(5000, -4000), joint);

        Assert.Equal(WallEndCondition.Butt, EndAt(document, above, joint).Condition);
        Assert.Equal(WallEndCondition.Butt, EndAt(document, below, joint).Condition);

        var (most, _) = OutlineChecks.Coverage(document, document.Walls, joint, exterior.Width);
        Assert.Equal(1, most);
    }

    [Fact]
    public void ManyWallsAtAPointTileTheJunctionWheneverTheyShareIt()
    {
        var random = new Random(20260921);
        var shared = 0;

        for (var attempt = 0; attempt < 300; attempt++)
        {
            var (document, partition, exterior) = Project();
            var joint = new Point2D(0, 0);
            var count = random.Next(3, 6);

            // Evenly spread and then jittered: at least 30 degrees apart, and never in line.
            var angles = new List<double>();
            var turn = random.NextDouble() * 360;
            var step = 360.0 / count;
            for (var i = 0; i < count; i++)
                angles.Add(turn + i * step + (random.NextDouble() - 0.5) * (step - 30));

            if (angles.Any(a => angles.Any(b => a != b && Separation(a, b) > 170))) continue;

            foreach (var angle in angles)
                Add(document, random.Next(2) == 0 ? partition : exterior, joint, Towards(joint, angle, 3000));

            if (!document.Walls.All(wall => EndAt(document, wall, joint).Condition == WallEndCondition.Shared))
                continue;

            shared++;
            var (most, covered) = OutlineChecks.Coverage(document, document.Walls, joint, exterior.Width);

            Assert.True(most == 1, $"{count} walls at {string.Join(", ", angles.Select(a => a.ToString("0")))}: overlap.");
            Assert.True(covered, $"{count} walls at {string.Join(", ", angles.Select(a => a.ToString("0")))}: gap.");
        }

        // Most well-spread arrangements should be shared, not fall back to overlapping.
        Assert.True(shared > 150, $"Only {shared} of 300 arrangements shared their junction.");
    }

    /// <summary>The angle between two directions, 0 to 180 degrees.</summary>
    private static double Separation(double a, double b) => Math.Abs(((a - b) % 360 + 540) % 360 - 180);

    [Theory]
    [InlineData(90)]
    [InlineData(60)]
    [InlineData(125)]
    public void TwoWallsCrossingMidwayJoinWithTheHeavierCarryingOn(int degrees)
    {
        var (document, partition, exterior) = Project();
        var crossing = new Point2D(5000, 0);

        var main = Add(document, exterior, new Point2D(0, 0), new Point2D(10000, 0));
        var light = Add(document, partition, Towards(crossing, degrees + 180, 3000), Towards(crossing, degrees, 3000));

        // The partition is drawn as two pieces either side of the main wall; the main wall whole.
        Assert.Single(WallSlices.Solid(document, main, exterior));
        var pieces = WallSlices.Solid(document, light, partition);
        Assert.Equal(2, pieces.Count);
        Assert.Equal(WallEndCondition.Butt, pieces[0].CutTo.Condition);
        Assert.Equal(WallEndCondition.Butt, pieces[1].CutFrom.Condition);

        // Filled once, and the main wall's faces open where the partition carries on.
        var outlines = document.Walls.SelectMany(wall =>
        {
            var type = document.GetWallType(wall)!;
            return WallSlices.Solid(document, wall, type).Select(slice =>
                WallJoins.GetBandOutline(wall, type, type.Width / 2, -type.Width / 2, slice.CutFrom, slice.CutTo));
        }).ToList();

        for (var x = -400; x <= 400; x += 20)
        for (var y = -400; y <= 400; y += 20)
        {
            var point = new Point2D(crossing.X + x + 0.013, crossing.Y + y + 0.029);
            Assert.True(outlines.Count(o => Polygon2D.Contains(o, point)) <= 1, $"{point} is covered twice.");
        }

        var gaps = WallJoins.FaceGaps(document, main, exterior);
        Assert.Equal(2, gaps.Count);
        Assert.Contains(gaps, gap => gap.Exterior);
        Assert.Contains(gaps, gap => !gap.Exterior);
    }

    [Fact]
    public void RandomRoomsAlwaysDrawAsWalls()
    {
        // Closed rooms of three to five walls on a coarse grid, some of them crossing
        // themselves, in every type: every piece of every layer must be a proper shape.
        var random = new Random(7);
        var checkedRooms = 0;

        for (var attempt = 0; attempt < 1500; attempt++)
        {
            var document = BimDocument.CreateDefault();
            var types = document.TypesOf<WallType>().ToList();
            var count = random.Next(3, 6);
            var points = Enumerable.Range(0, count)
                .Select(_ => new Point2D(random.Next(0, 12) * 500, random.Next(0, 12) * 500)).ToList();

            if (Enumerable.Range(0, count).Any(i => points[i].DistanceTo(points[(i + 1) % count]) < 300)) continue;

            for (var i = 0; i < count; i++)
            {
                var wall = Add(document, types[random.Next(types.Count)], points[i], points[(i + 1) % count]);
                wall.Flipped = random.Next(2) == 0;
            }

            checkedRooms++;

            foreach (var wall in document.Walls)
            {
                var type = document.GetWallType(wall)!;
                var half = type.Width / 2;

                foreach (var slice in WallSlices.Solid(document, wall, type))
                foreach (var (_, start, end) in type.Structure.GetLayerOffsets().Append((null!, 0, type.Width)))
                {
                    var outline = WallJoins.GetBandOutline(wall, type, half - start, half - end, slice.CutFrom, slice.CutTo);

                    Assert.False(OutlineChecks.IsSelfIntersecting(outline),
                        $"Room {string.Join(" ", points)}: wall {wall.Start}-{wall.End} folds over itself.");
                    Assert.True(Polygon2D.Area(outline) > 0.5,
                        $"Room {string.Join(" ", points)}: wall {wall.Start}-{wall.End} has no area.");
                }
            }
        }

        Assert.True(checkedRooms > 1000);
    }

    // ---- the join the user asks for ---------------------------------------------

    [Fact]
    public void AskingOneEndToButtMakesTheOtherRunThrough()
    {
        var (document, _, exterior) = Project();
        var corner = new Point2D(5000, 0);
        var a = Add(document, exterior, new Point2D(0, 0), corner);
        var b = Add(document, exterior, corner, new Point2D(5000, 4000));

        var set = new SetWallJoinCommand(document, a, atStart: false, WallJoinKind.Butt);
        set.Redo();

        Assert.Equal(WallJoinKind.Butt, a.EndJoin);
        Assert.Equal(WallJoinKind.RunThrough, b.StartJoin);
        Assert.Equal(WallEndCondition.Butt, EndAt(document, a, corner).Condition);
        Assert.Equal(WallEndCondition.RunsThrough, EndAt(document, b, corner).Condition);

        // A stops at B's inner face; B carries on to A's outer face and fills the corner.
        var half = exterior.Width / 2;
        var aEnd = WallJoins.EndPoints(a, exterior, half, -half, EndAt(document, a, corner), atStart: false);
        Assert.All(aEnd, point => Assert.Equal(5000 - half, point.X, precision: 6));

        var bStart = WallJoins.EndPoints(b, exterior, half, -half, EndAt(document, b, corner), atStart: true);
        Assert.All(bStart, point => Assert.Equal(-half, point.Y, precision: 6));

        var (most, covered) = OutlineChecks.Coverage(document, document.Walls, corner, exterior.Width);
        Assert.Equal(1, most);
        Assert.True(covered);

        set.Undo();
        Assert.Equal(WallJoinKind.Auto, a.EndJoin);
        Assert.Equal(WallJoinKind.Auto, b.StartJoin);
        Assert.Equal(WallEndCondition.Mitre, EndAt(document, a, corner).Condition);
    }

    [Fact]
    public void SquareOffCarriesTheWallOnWithASquareEnd()
    {
        var (document, _, exterior) = Project();
        var corner = new Point2D(5000, 0);
        var a = Add(document, exterior, new Point2D(0, 0), corner);
        var b = Add(document, exterior, corner, Towards(corner, 60, 4000));

        new SetWallJoinCommand(document, a, atStart: false, WallJoinKind.SquareOff).Redo();

        var half = exterior.Width / 2;
        var end = WallJoins.EndPoints(a, exterior, half, -half, EndAt(document, a, corner), atStart: false);

        Assert.Equal(end[0].X, end[^1].X, precision: 6);
        Assert.Equal(WallJoinKind.Butt, b.StartJoin);

        var (most, covered) = OutlineChecks.Coverage(document, document.Walls, corner, exterior.Width);
        Assert.Equal(1, most);
        Assert.True(covered);
    }

    [Fact]
    public void ADisallowedEndIsFreeAndItsNeighbourIgnoresIt()
    {
        var (document, _, exterior) = Project();
        var corner = new Point2D(5000, 0);
        var a = Add(document, exterior, new Point2D(0, 0), corner);
        var b = Add(document, exterior, corner, new Point2D(5000, 4000));

        var set = new SetWallJoinCommand(document, a, atStart: false, WallJoinKind.Disallow);
        set.Redo();

        Assert.Equal(WallEndCondition.Disallowed, EndAt(document, a, corner).Condition);
        Assert.Equal(WallEndCondition.Free, EndAt(document, b, corner).Condition);
        Assert.Equal(WallJoinKind.Auto, b.StartJoin);
    }

    [Fact]
    public void TheJoinIsAParameterOfEachEnd()
    {
        var (document, _, exterior) = Project();
        var corner = new Point2D(5000, 0);
        var a = Add(document, exterior, new Point2D(0, 0), corner);
        var b = Add(document, exterior, corner, new Point2D(5000, 4000));

        var parameter = a.GetInstanceParameters(document).Single(p => p.Name == "End Join");
        var before = parameter.Value;

        Assert.Contains("Run Through", parameter.AllowedValues!);
        Assert.True(parameter.TrySet("Run Through"));

        var change = new ParameterChangeCommand(parameter, before, parameter.Value);
        Assert.Equal(WallJoinKind.Butt, b.StartJoin);

        change.Undo();
        Assert.Equal(WallJoinKind.Auto, a.EndJoin);
        Assert.Equal(WallJoinKind.Auto, b.StartJoin);
    }

    [Fact]
    public void SplittingAWallKeepsItsFarEndJoinOnTheFarHalf()
    {
        var (document, _, exterior) = Project();
        var wall = Add(document, exterior, new Point2D(0, 0), new Point2D(6000, 0));
        wall.EndJoin = WallJoinKind.Butt;

        var split = new SplitWallCommand(document, wall, new Point2D(3000, 0));
        split.Redo();

        Assert.Equal(WallJoinKind.Auto, wall.EndJoin);
        Assert.Equal(WallJoinKind.Butt, split.Remainder.EndJoin);

        split.Undo();
        Assert.Equal(WallJoinKind.Butt, wall.EndJoin);
    }

    // ---- clean outlines -----------------------------------------------------------

    [Fact]
    public void AWallTeeingInLeavesAGapInTheFaceItStopsAgainst()
    {
        var (document, partition, exterior) = Project();
        var run = Add(document, exterior, new Point2D(0, 0), new Point2D(10000, 0));
        Add(document, partition, new Point2D(4000, 0), new Point2D(4000, 3000));

        var gap = Assert.Single(WallJoins.FaceGaps(document, run, exterior));

        // Drawn left to right the exterior is on the left, which is the side the stem is on.
        Assert.True(gap.Exterior);
        Assert.Equal(4000 - partition.Width / 2, gap.From, precision: 6);
        Assert.Equal(4000 + partition.Width / 2, gap.To, precision: 6);
    }

    [Fact]
    public void JoinedEndsAreNotDrawnButFreeOnesAre()
    {
        var (document, _, exterior) = Project();
        var a = Add(document, exterior, new Point2D(0, 0), new Point2D(5000, 0));
        Add(document, exterior, new Point2D(5000, 0), new Point2D(5000, 4000));

        var (start, end) = WallJoins.GetEndCuts(document, a, exterior);

        Assert.False(start.IsJoined);
        Assert.True(end.IsJoined);
    }

    // ---- wrapping -----------------------------------------------------------------

    /// <summary>
    /// Whether a wall's layers fill its outline exactly: every sampled point inside the whole
    /// wall is inside exactly one layer.
    /// </summary>
    private static void AssertLayersTile(Wall wall, WallType type, WallCut start, WallCut end, string what)
    {
        var half = type.Width / 2;
        var whole = WallJoins.GetBandOutline(wall, type, half, -half, start, end);
        var layers = type.Structure.GetLayerOffsets()
            .Select(l => WallJoins.GetBandOutline(wall, type, half - l.Start, half - l.End, start, end))
            .ToList();

        foreach (var layer in layers)
            Assert.False(OutlineChecks.IsSelfIntersecting(layer), $"{what}: a layer crosses itself.");

        var (min, max) = (whole.Aggregate((a, b) => new Point2D(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y))),
                          whole.Aggregate((a, b) => new Point2D(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y))));

        for (var i = 0; i <= 60; i++)
        for (var j = 0; j <= 60; j++)
        {
            var point = new Point2D(
                min.X + (max.X - min.X) * i / 60 + 0.0137,
                min.Y + (max.Y - min.Y) * j / 60 + 0.0291);

            var inWall = Polygon2D.Contains(whole, point);
            var count = layers.Count(layer => Polygon2D.Contains(layer, point));

            Assert.True(count == (inWall ? 1 : 0), $"{what}: {point} is in {count} layers.");
        }
    }

    [Theory]
    [InlineData(WallWrapping.Exterior)]
    [InlineData(WallWrapping.Interior)]
    public void LayersWrapRoundAnExposedEnd(WallWrapping wrapping)
    {
        var (document, _, exterior) = Project();
        exterior.WrapAtEnds = wrapping;
        var wall = Add(document, exterior, new Point2D(0, 0), new Point2D(1200, 0));

        var (start, end) = WallJoins.GetEndCuts(document, wall, exterior);
        Assert.Equal(wrapping, end.Wrapping);

        AssertLayersTile(wall, exterior, start, end, $"wrapped {wrapping}");

        // The wrapping finish turns the corner: its outline has more than four corners.
        var half = exterior.Width / 2;
        var (_, first, last) = wrapping == WallWrapping.Exterior
            ? exterior.Structure.GetLayerOffsets().First()
            : exterior.Structure.GetLayerOffsets().Last();

        Assert.Equal(8, WallJoins.GetBandOutline(wall, exterior, half - first, half - last, start, end).Length);
    }

    [Theory]
    [InlineData(WallWrapping.Exterior)]
    [InlineData(WallWrapping.Interior)]
    [InlineData(WallWrapping.Both)]
    [InlineData(WallWrapping.None)]
    public void LayersReturnIntoAnOpening(WallWrapping wrapping)
    {
        var (document, _, exterior) = Project();
        exterior.WrapAtInserts = wrapping;
        var wall = Add(document, exterior, new Point2D(0, 0), new Point2D(5000, 0));

        document.Add(new Door
        {
            TypeId = document.TypesOf<DoorType>().First().Id,
            LevelId = wall.LevelId,
            HostWallId = wall.Id,
            DistanceAlongWall = 2500
        });

        var slices = WallSlices.Solid(document, wall, exterior);
        Assert.Equal(2, slices.Count);

        foreach (var slice in slices)
            AssertLayersTile(wall, exterior, slice.CutFrom, slice.CutTo, $"{wrapping} at {slice.From:0}-{slice.To:0}");
    }

    [Fact]
    public void AWallWithNoCoreDoesNotWrap()
    {
        var (document, _, _) = Project();
        var generic = document.TypesOf<WallType>().Single(t => t.Name.StartsWith("Generic"));
        generic.WrapAtEnds = WallWrapping.Exterior;

        var wall = Add(document, generic, new Point2D(0, 0), new Point2D(3000, 0));
        var (start, end) = WallJoins.GetEndCuts(document, wall, generic);

        AssertLayersTile(wall, generic, start, end, "single layer");
    }

    // ---- saving -----------------------------------------------------------------------

    [Fact]
    public void JoinsAndWrappingAreSaved()
    {
        var (document, _, exterior) = Project();
        exterior.WrapAtInserts = WallWrapping.Interior;
        exterior.WrapAtEnds = WallWrapping.Exterior;

        var wall = Add(document, exterior, new Point2D(0, 0), new Point2D(5000, 0));
        wall.StartJoin = WallJoinKind.Disallow;
        wall.EndJoin = WallJoinKind.SquareOff;

        var path = Path.Combine(Path.GetTempPath(), $"bimtest-{Guid.NewGuid():N}{ProjectFile.Extension}");

        try
        {
            ProjectFile.Save(document, path);
            var reloaded = ProjectFile.Load(path);

            var type = reloaded.TypesOf<WallType>().Single(t => t.Id == exterior.Id);
            Assert.Equal(WallWrapping.Interior, type.WrapAtInserts);
            Assert.Equal(WallWrapping.Exterior, type.WrapAtEnds);

            var copy = reloaded.Walls.Single();
            Assert.Equal(WallJoinKind.Disallow, copy.StartJoin);
            Assert.Equal(WallJoinKind.SquareOff, copy.EndJoin);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
