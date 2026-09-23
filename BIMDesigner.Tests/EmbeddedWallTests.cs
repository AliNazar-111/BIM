using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;
using BIMDesigner.Infrastructure.Interoperability;
using Xbim.Ifc4.SharedBldgElements;

namespace BIMDesigner.Tests;

/// <summary>Curtain walls set into other walls cut their own opening there.</summary>
public class EmbeddedWallTests
{
    /// <summary>A 10 m brick wall 3 m high, with a 4 m storefront 2.4 m high drawn along its middle.</summary>
    private static (BimDocument Document, WallType HostType, Wall Host, Wall Shopfront) Shopfront()
    {
        var document = BimDocument.CreateDefault();
        var hostType = document.TypesOf<WallType>().Single(t => t.Name.StartsWith("Exterior"));
        var host = new Wall
        {
            Start = new Point2D(0, 0), End = new Point2D(10000, 0),
            TypeId = hostType.Id, LevelId = document.Levels[0].Id, UnconnectedHeight = 3000
        };
        var shopfront = new Wall
        {
            Start = new Point2D(3000, 0), End = new Point2D(7000, 0),
            TypeId = document.TypesOf<CurtainWallType>().Single(t => t.Name.Contains("Storefront")).Id,
            LevelId = document.Levels[0].Id, UnconnectedHeight = 2400
        };
        document.Add(host);
        document.Add(shopfront);
        return (document, hostType, host, shopfront);
    }

    [Fact]
    public void AStorefrontInsideAWallCutsItsOwnOpening()
    {
        var (document, _, host, shopfront) = Shopfront();

        var (found, hole) = CurtainEmbedding.Of(document, shopfront)!.Value;
        Assert.Same(host, found);
        Assert.Equal((3000.0, 7000.0, 0.0, 2400.0), (hole.From, hole.To, hole.Sill, hole.Head));

        Assert.Equal(new[] { (0.0, 3000.0), (7000.0, 10000.0) }, WallOpenings.GetSolidRuns(document, host));
    }

    [Fact]
    public void OnlyATypeThatEmbedsAndAWallThatFitsCutsAnOpening()
    {
        var (document, _, host, shopfront) = Shopfront();

        // Off to one side of the host, it is just another wall.
        shopfront.Start = new Point2D(3000, 1000);
        shopfront.End = new Point2D(7000, 1000);
        Assert.Null(CurtainEmbedding.Of(document, shopfront));
        Assert.Single(WallOpenings.GetSolidRuns(document, host));

        // Back inside, but a type that does not embed.
        shopfront.Start = new Point2D(3000, 50);
        shopfront.End = new Point2D(7000, 50);
        Assert.NotNull(CurtainEmbedding.Of(document, shopfront));
        document.FindType<CurtainWallType>(shopfront.TypeId)!.AutomaticallyEmbed = false;
        Assert.Null(CurtainEmbedding.Of(document, shopfront));

        // Running past the end of the host.
        document.FindType<CurtainWallType>(shopfront.TypeId)!.AutomaticallyEmbed = true;
        shopfront.End = new Point2D(12000, 50);
        Assert.Null(CurtainEmbedding.Of(document, shopfront));
    }

    [Fact]
    public void In3DTheHostStandsAboveAndBesideTheShopfront()
    {
        var (document, _, host, _) = Shopfront();
        var meshes = ModelMeshBuilder.BuildWall(document, host).Where(m => m.Kind == MeshKind.Wall).ToList();

        // Triangles within the opening, below its head, are none of the host's.
        foreach (var mesh in meshes)
        for (var i = 0; i + 2 < mesh.Indices.Count; i += 3)
        {
            var tri = new[] { mesh.Positions[mesh.Indices[i]], mesh.Positions[mesh.Indices[i + 1]], mesh.Positions[mesh.Indices[i + 2]] };
            var centre = new Point3D(tri.Average(p => p.X), tri.Average(p => p.Y), tri.Average(p => p.Z));
            Assert.False(centre.X > 3000 + 1 && centre.X < 7000 - 1 && centre.Z < 2400 - 1,
                $"The host is still there in the opening at {centre}.");
        }

        // But it is there over the opening: its underside is the head of the opening.
        Assert.Contains(meshes.SelectMany(m => m.Positions), p => p.X >= 3000 && p.X <= 7000 && Math.Abs(p.Z - 2400) < 1e-6);
    }

    [Fact]
    public void ASectionThroughTheOpeningShowsTheHostOnlyAboveIt()
    {
        var (document, _, host, shopfront) = Shopfront();
        var marker = new SectionMarker { Name = "A", Start = new Point2D(5200, -2000), End = new Point2D(5200, 2000), LevelId = host.LevelId };
        document.Add(marker);

        var pieces = SectionProjection.Build(document, marker).Pieces;
        Assert.All(pieces.Where(p => p.ElementId == host.Id && p.Depth == SectionDepth.Cut), p => Assert.True(p.Bounds.Bottom >= 2400 - 1e-6));
        Assert.Contains(pieces, p => p.ElementId == shopfront.Id && p.Part == SectionPart.Glazing);
    }

    [Fact]
    public void TheShopfrontIsNotCutBackByTheWallItSitsIn()
    {
        var (document, _, _, shopfront) = Shopfront();

        var glass = ModelMeshBuilder.Bounds(
            ModelMeshBuilder.BuildWall(document, shopfront).Where(m => m.Kind == MeshKind.Glazing))!.Value;
        Assert.Equal(3050, glass.Min.X, precision: 6);
        Assert.Equal(6950, glass.Max.X, precision: 6);
    }

    [Fact]
    public void MovingTheShopfrontMovesTheOpening()
    {
        var (document, _, host, shopfront) = Shopfront();
        var move = new MoveWallCommand(shopfront, shopfront.Start, shopfront.End, new Point2D(1000, 0), new Point2D(5000, 0), "Move");

        move.Redo();
        Assert.Equal(new[] { (0.0, 1000.0), (5000.0, 10000.0) }, WallOpenings.GetSolidRuns(document, host));

        move.Undo();
        Assert.Equal(new[] { (0.0, 3000.0), (7000.0, 10000.0) }, WallOpenings.GetSolidRuns(document, host));
    }

    [Fact]
    public void ARoomBehindTheShopfrontIsStillEnclosed()
    {
        var (document, hostType, _, _) = Shopfront();
        var level = document.Levels[0].Id;
        foreach (var (a, b) in new[] { ((10000.0, 0.0), (10000.0, -5000.0)), ((10000.0, -5000.0), (0.0, -5000.0)), ((0.0, -5000.0), (0.0, 0.0)) })
            document.Add(new Wall { Start = new Point2D(a.Item1, a.Item2), End = new Point2D(b.Item1, b.Item2), TypeId = hostType.Id, LevelId = level });

        var result = RoomBoundary.Trace(document, level, new Point2D(5000, -2500));
        Assert.True(result.IsEnclosed);
    }

    [Fact]
    public void TheHostExportsWithTheHoleInIt()
    {
        var (document, _, _, _) = Shopfront();

        using var model = IfcExport.Build(document);

        Assert.Single(model.Instances.OfType<IfcWall>());
        Assert.Single(model.Instances.OfType<IfcCurtainWall>());
    }
}
