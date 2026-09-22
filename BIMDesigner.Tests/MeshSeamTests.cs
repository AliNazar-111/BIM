using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;

using BimWindow = BIMDesigner.Core.Architecture.Window;

namespace BIMDesigner.Tests;

/// <summary>A wall in 3D shows its real edges, not the joins between the blocks it is built from.</summary>
public class MeshSeamTests
{
    [Fact]
    public void AWindowsJambIsAnEdgeOnlyBetweenSillAndHead()
    {
        var document = BimDocument.CreateDefault();
        var type = document.TypesOf<WallType>().Single(t => t.Name.StartsWith("Generic"));
        var wall = new Wall
        {
            Start = new Point2D(0, 0), End = new Point2D(6000, 0),
            TypeId = type.Id, LevelId = document.Levels[0].Id, UnconnectedHeight = 3000
        };
        document.Add(wall);
        var windowType = document.TypesOf<WindowType>().First();
        document.Add(new BimWindow
        {
            TypeId = windowType.Id, LevelId = wall.LevelId, HostWallId = wall.Id, DistanceAlongWall = 3000, SillHeight = 900
        });

        var mesh = ModelMeshBuilder.BuildWall(document, wall).Single(m => m.ElementId == wall.Id && m.Kind == MeshKind.Wall);
        var jamb = 3000 - windowType.Width / 2;
        var head = 900 + windowType.Height;

        // The vertical lines drawn at the jamb, on either face.
        var atJamb = mesh.Edges
            .Where(e => Math.Abs(e.From.X - jamb) < 1e-6 && Math.Abs(e.To.X - jamb) < 1e-6 && Math.Abs(e.From.Z - e.To.Z) > 1)
            .Select(e => (Low: Math.Min(e.From.Z, e.To.Z), High: Math.Max(e.From.Z, e.To.Z)))
            .ToList();

        Assert.NotEmpty(atJamb);
        Assert.All(atJamb, e =>
        {
            Assert.Equal(900, e.Low, precision: 6);
            Assert.Equal(head, e.High, precision: 6);
        });

        // And no line across the top of the wall where the block over the window meets the one beside it.
        Assert.DoesNotContain(mesh.Edges, e =>
            Math.Abs(e.From.X - jamb) < 1e-6 && Math.Abs(e.To.X - jamb) < 1e-6 &&
            Math.Abs(e.From.Z - 3000) < 1e-6 && Math.Abs(e.To.Z - 3000) < 1e-6);

        // The wall's own corners are still drawn full height.
        Assert.Contains(mesh.Edges, e => Math.Abs(e.From.X) < 1e-6 && Math.Abs(e.To.X) < 1e-6 &&
                                         Math.Abs(Math.Abs(e.From.Z - e.To.Z) - 3000) < 1e-6);
    }
}
