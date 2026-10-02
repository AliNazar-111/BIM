using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;
using BIMDesigner.Core.Views;

namespace BIMDesigner.Tests;

/// <summary>
/// Where a wall stops against another, their layers are cleaned up by priority, as Revit does:
/// a layer passes through the other wall's layers of lower priority and stops at one of equal or
/// higher; the core passes everything outside the other's core, and nothing passes that.
/// </summary>
public class WallLayerPriorityTests
{
    /// <summary>
    /// An exterior wall along the X axis, drawn right to left so its plaster faces up, and a
    /// partition coming down to meet it at x = 3000.
    /// </summary>
    private static (BimDocument Document, Wall Exterior, Wall Partition, WallType ExteriorType, WallType PartitionType) Tee()
    {
        var document = BimDocument.CreateDefault();
        var exteriorType = document.TypesOf<WallType>().First(t => t.Name.StartsWith("Exterior"));
        var partitionType = document.TypesOf<WallType>().First(t => t.Name.StartsWith("Interior - Partition"));
        var level = document.Levels[0].Id;

        var exterior = new Wall { Start = new Point2D(6000, 0), End = new Point2D(0, 0), TypeId = exteriorType.Id, LevelId = level, UnconnectedHeight = 3000 };
        var partition = new Wall { Start = new Point2D(3000, 3000), End = new Point2D(3000, 0), TypeId = partitionType.Id, LevelId = level, UnconnectedHeight = 2700 };
        document.Add(exterior);
        document.Add(partition);
        return (document, exterior, partition, exteriorType, partitionType);
    }

    [Fact]
    public void APartitionsCoreRunsThroughThePlasterToTheBlockworkAndItsBoardsStopAtTheFace()
    {
        var (document, exterior, partition, exteriorType, partitionType) = Tee();
        var half = exteriorType.Width / 2;
        var plaster = exteriorType.Structure.InteriorWidth;
        var (startCut, endCut) = WallJoins.GetEndCuts(document, partition, partitionType);
        Assert.Equal(WallEndCondition.Butt, endCut.Condition);

        var partitionHalf = partitionType.Width / 2;
        foreach (var (layer, start, end) in partitionType.Structure.GetLayerOffsets())
        {
            var piece = Assert.Single(WallJoins.LayerPieces(partition, partitionType, partitionHalf - start, partitionHalf - end, startCut, endCut,
                Array.Empty<WallJoins.WallIntrusion>()));
            var reaches = piece.Min(point => point.Y);

            if (layer.Function == LayerFunction.Structure) Assert.Equal(half - plaster, reaches, 6);
            else Assert.Equal(half, reaches, 6);
        }
    }

    [Fact]
    public void ThePlasterGivesWayWhereThePartitionsCoreComesThrough()
    {
        var (document, exterior, _, exteriorType, partitionType) = Tee();
        var structure = exteriorType.Structure;
        var half = exteriorType.Width / 2;
        var (startCut, endCut) = WallJoins.GetEndCuts(document, exterior, exteriorType);
        var intrusions = WallJoins.Intrusions(document, exterior, exteriorType);
        var core = Assert.Single(intrusions);
        Assert.Equal(2700, core.Top, 6);

        var coreHalf = partitionType.Structure.CoreWidth / 2;
        foreach (var (layer, start, end) in structure.GetLayerOffsets())
        {
            var pieces = WallJoins.LayerPieces(exterior, exteriorType, half - start, half - end, startCut, endCut, intrusions);
            if (layer.Function == LayerFunction.Finish2)
            {
                // The plaster in two, either side of the studs.
                Assert.Equal(2, pieces.Count);
                var edges = pieces.Select(piece => (piece.Min(p => p.X), piece.Max(p => p.X))).OrderBy(range => range.Item1).ToList();
                Assert.Equal(3000 - coreHalf, edges[0].Item2, 6);
                Assert.Equal(3000 + coreHalf, edges[1].Item1, 6);
            }
            else
            {
                Assert.Single(pieces);
            }
        }
    }

    [Fact]
    public void In3DThePlasterIsOpenWhereTheStudsComeThroughAndOnlyAsHighAsThePartition()
    {
        var (document, exterior, partition, exteriorType, _) = Tee();
        var meshes = ModelMeshBuilder.Build(document);
        var plaster = meshes.Single(mesh => mesh.ElementId == exterior.Id && mesh.Description == "Cement Plaster");
        var studs = meshes.Single(mesh => mesh.ElementId == partition.Id && mesh.Description == "Air Cavity");
        var inPlaster = new Point2D(3000, exteriorType.Width / 2 - 5);

        Assert.True(ChimneyAndDrainPipeTests.Covers(studs, inPlaster), "The studs stop at the face.");
        Assert.False(Covers(plaster, inPlaster, 0), "The plaster runs on across the studs.");
        Assert.True(Covers(plaster, inPlaster, 3000), "Above the partition the plaster is whole.");
    }

    /// <summary>Whether a mesh has a level face over a point at a height.</summary>
    private static bool Covers(Mesh3D mesh, Point2D point, double z)
    {
        for (var i = 0; i + 2 < mesh.Indices.Count; i += 3)
        {
            var (a, b, c) = (mesh.Positions[mesh.Indices[i]], mesh.Positions[mesh.Indices[i + 1]], mesh.Positions[mesh.Indices[i + 2]]);
            if (new[] { a.Z, b.Z, c.Z }.Any(h => Math.Abs(h - z) > 1e-6)) continue;
            var triangle = new[] { new Point2D(a.X, a.Y), new Point2D(b.X, b.Y), new Point2D(c.X, c.Y) };
            if (Math.Abs(Polygon2D.SignedArea(triangle)) > 1 && Polygon2D.Contains(triangle, point)) return true;
        }

        return false;
    }

    [Fact]
    public void ALayerPassesOnlyThroughLayersOfLowerPriority()
    {
        var material = Guid.NewGuid();
        var mine = new CompoundStructure(
            new MaterialLayer(LayerFunction.Finish1, material, 10),
            new MaterialLayer(LayerFunction.Substrate, material, 12),
            new MaterialLayer(LayerFunction.Structure, material, 100),
            new MaterialLayer(LayerFunction.Finish2, material, 10));
        var theirs = new CompoundStructure(
            new MaterialLayer(LayerFunction.Finish2, material, 15),
            new MaterialLayer(LayerFunction.Membrane, material, 0),
            new MaterialLayer(LayerFunction.ThermalAir, material, 50),
            new MaterialLayer(LayerFunction.Structure, material, 140),
            new MaterialLayer(LayerFunction.Finish1, material, 100));

        var depths = WallJoins.LayerDepths(mine, theirs, theirExteriorIsNear: true).Select(entry => entry.Depth).ToList();

        // Finish 1 passes the plaster (Finish 2) and the membrane but stops at the insulation;
        // the substrate passes both; the core goes to their core; Finish 2 stops at the face.
        Assert.Equal(new[] { 15.0, 65.0, 65.0, 0.0 }, depths);

        // From the other side, their brick (Finish 1) is near: the core goes through it to the
        // blockwork, Finish 1 meets Finish 1 at the face, and nothing else passes it.
        var fromOutside = WallJoins.LayerDepths(mine, theirs, theirExteriorIsNear: false).Select(entry => entry.Depth).ToList();
        Assert.Equal(new[] { 0.0, 100.0, 100.0, 0.0 }, fromOutside);
    }

    [Fact]
    public void TwoPartitionsMeetCoreToCore()
    {
        var document = BimDocument.CreateDefault();
        var type = document.TypesOf<WallType>().First(t => t.Name.StartsWith("Interior - Partition"));
        var level = document.Levels[0].Id;
        var run = new Wall { Start = new Point2D(0, 0), End = new Point2D(6000, 0), TypeId = type.Id, LevelId = level, UnconnectedHeight = 2700 };
        var branch = new Wall { Start = new Point2D(3000, 2000), End = new Point2D(3000, 0), TypeId = type.Id, LevelId = level, UnconnectedHeight = 2700 };
        document.Add(run);
        document.Add(branch);

        var (_, endCut) = WallJoins.GetEndCuts(document, branch, type);
        var core = endCut.LayerDepths.Single(entry => entry.Depth > 0);
        Assert.Equal(type.Structure.ExteriorWidth, core.Depth, 6);
        Assert.Single(WallJoins.Intrusions(document, run, type));
    }
}
