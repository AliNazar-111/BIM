using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Tests;

/// <summary>
/// A dormer's fascias, gutters and soffits: its roof is carried back into the main roof, and
/// what runs along its edges stops where the edges go into the main roof rather than carrying
/// on through it into the room underneath.
/// </summary>
public class DormerEdgeSweepTests
{
    private static (BimDocument Document, Roof Main, Roof Dormer) Dormer(DormerShape shape)
    {
        var (document, main, wallType) = DormerToolTests.House(10000, 10000, 40);
        var settings = DormerSettings.Default with { Shape = shape, Slope = shape == DormerShape.Shed ? 15 : 35 };
        Assert.NotNull(Dormers.Add(document, main, new Point2D(5000, 800), settings, wallType.Id, out var problem, out _));
        Assert.Null(problem);

        var dormer = document.Elements.OfType<Roof>().Single(roof => !ReferenceEquals(roof, main));
        return (document, main, dormer);
    }

    private static IReadOnlyList<RoofEdgeSweep> AllRound(BimDocument document, Roof dormer)
    {
        var fascia = new Fascia { RoofId = dormer.Id, LevelId = dormer.LevelId, TypeId = document.TypesOf<FasciaType>().First().Id };
        fascia.EdgeIds.AddRange(RoofEdgeSweeps.FasciaEdges(document, dormer));
        var gutter = new Gutter { RoofId = dormer.Id, LevelId = dormer.LevelId, TypeId = document.TypesOf<GutterType>().First().Id };
        gutter.EdgeIds.AddRange(RoofEdgeSweeps.GutterEdges(document, dormer));
        var soffit = new Soffit { RoofId = dormer.Id, LevelId = dormer.LevelId, TypeId = document.TypesOf<SoffitType>().First().Id };
        soffit.EdgeIds.AddRange(RoofEdgeSweeps.SoffitEdges(document, dormer));

        var sweeps = new RoofEdgeSweep[] { fascia, gutter, soffit };
        foreach (var sweep in sweeps) document.Add(sweep);
        return sweeps;
    }

    /// <summary>
    /// How far below the main roof's underside a sweep reaches anywhere over the main roof and
    /// outside the hole cut for the dormer: into the room under the main roof, where nothing of
    /// the outside of the dormer belongs.
    /// </summary>
    private static double Inside(BimDocument document, Roof main, RoofEdgeSweep sweep)
    {
        var holes = RoofJoin.Openings(document, main);
        return RoofEdgeSweeps.Mesh(document, sweep, sweep.LevelId).Positions
            .Select(point => new { Point = point, Plan = new Point2D(point.X, point.Y) })
            .Where(p => main.Contains(p.Plan) && !holes.Any(hole => Polygon2D.Contains(hole, p.Plan)))
            .Select(p => main.UndersideAt(document, p.Plan) - p.Point.Z)
            .DefaultIfEmpty(double.NegativeInfinity)
            .Max();
    }

    [Theory]
    [InlineData(DormerShape.Gable)]
    [InlineData(DormerShape.Shed)]
    [InlineData(DormerShape.Hip)]
    public void TheyStopWhereTheDormersRoofRunsIntoTheMainRoof(DormerShape shape)
    {
        var (document, main, dormer) = Dormer(shape);

        foreach (var sweep in AllRound(document, dormer))
        {
            Assert.NotEmpty(RoofEdgeSweeps.Mesh(document, sweep, sweep.LevelId).Positions);

            var reach = Inside(document, main, sweep);
            Assert.True(reach < -20, $"{sweep.GetType().Name} reaches {reach:0} mm below the main roof's underside into the room.");
        }
    }

    [Theory]
    [InlineData(DormerShape.Gable)]
    [InlineData(DormerShape.Hip)]
    public void TheyStillRunOnIntoTheMainRoofLeavingNoGap(DormerShape shape)
    {
        var (document, main, dormer) = Dormer(shape);

        foreach (var sweep in AllRound(document, dormer).Where(sweep => sweep is not Soffit))
        {
            // Along the eaves running back up the main roof, a fascia or gutter stops only once
            // its top has gone in under the main roof's top.
            var ends = RoofEdgeSweeps.Runs(document, sweep)
                .SelectMany(run => new[] { run.Segments[0].From, run.Segments[^1].To })
                .ToList();

            Assert.Contains(ends, end => main.TopAt(document, new Point2D(end.X, end.Y)) > end.Z);
        }
    }
}
