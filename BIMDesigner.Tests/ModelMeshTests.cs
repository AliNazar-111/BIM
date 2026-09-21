using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;

namespace BIMDesigner.Tests;

/// <summary>
/// Tests over the 3D form of the model (specification section 6.1).
///
/// These check what can be measured about a mesh without looking at it: where it starts and
/// stops, how much surface it has, and whether the holes are where the doors are. A 3D view
/// is the one people trust most at a glance, so it is the one that most needs to be right.
/// </summary>
public class ModelMeshTests
{
    private const double Tolerance = 1e-6;

    private static BimDocument Project() => BimDocument.CreateDefault();

    private static Wall AddWall(BimDocument document, string typePrefix = "Generic", Guid? levelId = null)
    {
        var wall = new Wall
        {
            Start = new Point2D(0, 0),
            End = new Point2D(6000, 0),
            TypeId = document.TypesOf<WallType>().Single(t => t.Name.StartsWith(typePrefix)).Id,
            LevelId = levelId ?? document.Levels[0].Id,
            UnconnectedHeight = 3000
        };

        document.Add(wall);
        return wall;
    }

    /// <summary>Total area of a mesh's triangles, which is how "is there a hole" is measured.</summary>
    private static double SurfaceArea(Mesh3D mesh)
    {
        var area = 0.0;

        for (var i = 0; i < mesh.Indices.Count; i += 3)
        {
            var a = mesh.Positions[mesh.Indices[i]];
            var b = mesh.Positions[mesh.Indices[i + 1]];
            var c = mesh.Positions[mesh.Indices[i + 2]];

            var ux = b.X - a.X; var uy = b.Y - a.Y; var uz = b.Z - a.Z;
            var vx = c.X - a.X; var vy = c.Y - a.Y; var vz = c.Z - a.Z;

            var cx = uy * vz - uz * vy;
            var cy = uz * vx - ux * vz;
            var cz = ux * vy - uy * vx;

            area += Math.Sqrt(cx * cx + cy * cy + cz * cz) / 2;
        }

        return area;
    }

    // ---- triangulation ----------------------------------------------------------

    [Fact]
    public void ASquareTriangulatesIntoTwoTrianglesCoveringItsArea()
    {
        var square = new[] { new Point2D(0, 0), new Point2D(10, 0), new Point2D(10, 10), new Point2D(0, 10) };

        var triangles = Polygon2D.Triangulate(square);

        Assert.Equal(2, triangles.Count);
        Assert.Equal(100, triangles.Sum(t => TriangleArea(square, t)), Tolerance);
    }

    [Fact]
    public void AnLShapeTriangulatesWithoutCoveringTheMissingCorner()
    {
        var shape = new[]
        {
            new Point2D(0, 0), new Point2D(6, 0), new Point2D(6, 2),
            new Point2D(2, 2), new Point2D(2, 6), new Point2D(0, 6)
        };

        var triangles = Polygon2D.Triangulate(shape);

        // A fan from the first corner would add a triangle across the bite; ear clipping must
        // cover exactly the L's own area.
        Assert.Equal(shape.Length - 2, triangles.Count);
        Assert.Equal(Polygon2D.Area(shape), triangles.Sum(t => TriangleArea(shape, t)), Tolerance);
    }

    [Fact]
    public void AClockwiseOutlineTriangulatesJustTheSame()
    {
        var clockwise = new[] { new Point2D(0, 10), new Point2D(10, 10), new Point2D(10, 0), new Point2D(0, 0) };

        var triangles = Polygon2D.Triangulate(clockwise);

        Assert.Equal(100, triangles.Sum(t => TriangleArea(clockwise, t)), Tolerance);
    }

    private static double TriangleArea(IReadOnlyList<Point2D> points, (int A, int B, int C) t) =>
        Math.Abs((points[t.B] - points[t.A]).Cross(points[t.C] - points[t.A])) / 2;

    // ---- extrusion --------------------------------------------------------------

    [Fact]
    public void AnExtrudedBoxHasTheSurfaceOfABox()
    {
        var mesh = new Mesh3D(Guid.NewGuid(), Guid.Empty, MeshKind.Wall, default, "box");
        mesh.AddExtrusion(
            new[] { new Point2D(0, 0), new Point2D(200, 0), new Point2D(200, 100), new Point2D(0, 100) },
            0, 50);

        // Two 200x100 caps, two 200x50 sides and two 100x50 sides.
        var expected = 2 * (200 * 100) + 2 * (200 * 50) + 2 * (100 * 50);

        Assert.Equal(expected, SurfaceArea(mesh), precision: 3);
        Assert.Equal(12, mesh.TriangleCount);
    }

    [Fact]
    public void AnExtrudedBoxKnowsItsTwelveEdges()
    {
        var mesh = new Mesh3D(Guid.NewGuid(), Guid.Empty, MeshKind.Wall, default, "box");
        mesh.AddExtrusion(
            new[] { new Point2D(0, 0), new Point2D(200, 0), new Point2D(200, 100), new Point2D(0, 100) },
            0, 50);

        // Four along the bottom, four along the top, four verticals: the lines a drawing of a
        // box would have. Without them a shaded box reads as one grey mass.
        Assert.Equal(12, mesh.Edges.Count);

        Assert.Equal(4, mesh.Edges.Count(e => e.From.Z == 0 && e.To.Z == 0));
        Assert.Equal(4, mesh.Edges.Count(e => e.From.Z == 50 && e.To.Z == 50));
        Assert.Equal(4, mesh.Edges.Count(e => Math.Abs(e.From.Z - e.To.Z) > 1e-9));
    }

    [Fact]
    public void EveryLayerOfAWallContributesItsOwnLines()
    {
        var document = Project();
        var wall = AddWall(document, "Exterior");
        var type = document.GetWallType(wall)!;

        var meshes = ModelMeshBuilder.Build(document).Where(m => m.ElementId == wall.Id).ToList();

        // A layer boundary is a visible line on the end of a wall, and each layer is its own
        // solid, so each brings its own.
        Assert.Equal(type.Structure.Layers.Count, meshes.Count);
        Assert.All(meshes, mesh => Assert.Equal(12, mesh.Edges.Count));
    }

    [Fact]
    public void ANothingHighExtrusionAddsNothing()
    {
        var mesh = new Mesh3D(Guid.NewGuid(), Guid.Empty, MeshKind.Wall, default, "flat");
        mesh.AddExtrusion(new[] { new Point2D(0, 0), new Point2D(1, 0), new Point2D(1, 1) }, 100, 100);

        Assert.True(mesh.IsEmpty);
    }

    // ---- walls ------------------------------------------------------------------

    [Fact]
    public void AWallStandsFromItsBaseToItsTop()
    {
        var document = Project();
        var wall = AddWall(document);
        wall.BaseOffset = 150;
        wall.UnconnectedHeight = 2700;

        var meshes = ModelMeshBuilder.Build(document).Where(m => m.ElementId == wall.Id).ToList();
        var bounds = ModelMeshBuilder.Bounds(meshes)!.Value;

        Assert.Equal(150, bounds.Min.Z, precision: 6);
        Assert.Equal(2850, bounds.Max.Z, precision: 6);
    }

    [Fact]
    public void AWallReachingALevelIsAsTallAsTheLevelIsHigh()
    {
        var document = Project();
        var wall = AddWall(document);
        wall.TopLevelId = document.Levels[1].Id;

        document.Levels[1].Elevation = 3600;

        var bounds = ModelMeshBuilder.Bounds(ModelMeshBuilder.Build(document).Where(m => m.ElementId == wall.Id))!.Value;

        Assert.Equal(3600, bounds.Max.Z, precision: 6);
    }

    [Fact]
    public void AWallOnTheFirstFloorStandsOnTheFirstFloor()
    {
        var document = Project();
        var first = document.Levels[1];
        var wall = AddWall(document, levelId: first.Id);

        var bounds = ModelMeshBuilder.Bounds(ModelMeshBuilder.Build(document).Where(m => m.ElementId == wall.Id))!.Value;

        // A copied storey sitting on the ground instead of on its own level is exactly the
        // kind of mistake a 3D view exists to reveal - so the 3D view must not make it itself.
        Assert.Equal(first.Elevation, bounds.Min.Z, precision: 6);
        Assert.Equal(first.Elevation + 3000, bounds.Max.Z, precision: 6);
        Assert.All(ModelMeshBuilder.Build(document).Where(m => m.ElementId == wall.Id),
            mesh => Assert.Equal(first.Id, mesh.LevelId));
    }

    [Fact]
    public void EveryLayerOfAWallIsItsOwnMeshInItsOwnMaterial()
    {
        var document = Project();
        var wall = AddWall(document, "Exterior");
        var type = document.GetWallType(wall)!;

        var meshes = ModelMeshBuilder.Build(document).Where(m => m.ElementId == wall.Id).ToList();

        Assert.Equal(type.Structure.Layers.Count, meshes.Count);

        var expectedColours = type.Structure.Layers
            .Select(layer => document.FindMaterial(layer.MaterialId)!.SurfaceColour)
            .ToList();

        Assert.Equal(expectedColours, meshes.Select(m => m.Colour));
    }

    [Fact]
    public void TheLayersOfAWallAddUpToTheWallsThickness()
    {
        var document = Project();
        var wall = AddWall(document, "Exterior");
        var type = document.GetWallType(wall)!;

        var bounds = ModelMeshBuilder.Bounds(ModelMeshBuilder.Build(document).Where(m => m.ElementId == wall.Id))!.Value;

        // The wall runs along X, so its thickness is its extent in Y.
        Assert.Equal(type.Width, bounds.Max.Y - bounds.Min.Y, precision: 6);
        Assert.Equal(6000, bounds.Max.X - bounds.Min.X, precision: 6);
    }

    // ---- openings ---------------------------------------------------------------

    [Fact]
    public void ADoorLeavesAHoleInTheWall()
    {
        var document = Project();
        var wall = AddWall(document);

        var solid = SurfaceArea(ModelMeshBuilder.Build(document).Single(m => m.ElementId == wall.Id));

        var doorType = document.TypesOf<DoorType>().First(t => t.Width == 900);
        document.Add(new Door
        {
            TypeId = doorType.Id,
            LevelId = wall.LevelId,
            HostWallId = wall.Id,
            DistanceAlongWall = 3000
        });

        var opened = SurfaceArea(ModelMeshBuilder.Build(document).Single(m => m.ElementId == wall.Id));

        // Both faces lose the 900 x 2100 doorway; the reveals add back far less than that.
        Assert.True(opened < solid - 900 * 2100,
            "a wall with a door in it must have less face than the same wall without one");
    }

    [Fact]
    public void AWindowLeavesWallBelowTheSillAndAboveTheHead()
    {
        var document = Project();
        var wall = AddWall(document);

        var windowType = document.TypesOf<WindowType>().First(t => t.Height == 1200);
        var window = new Window
        {
            TypeId = windowType.Id,
            LevelId = wall.LevelId,
            HostWallId = wall.Id,
            DistanceAlongWall = 3000,
            SillHeight = 900
        };
        document.Add(window);

        var meshes = ModelMeshBuilder.Build(document);
        var wallMesh = meshes.Single(m => m.ElementId == wall.Id);

        // Somewhere across the opening there is a top face at the sill and a bottom face at the
        // head. The pieces are cut square at the jambs, so their corners sit exactly on them.
        static bool AcrossOpening(Point3D p) => p.X >= 2400 - Tolerance && p.X <= 3600 + Tolerance;

        Assert.Contains(wallMesh.Positions, p => Math.Abs(p.Z - 900) < Tolerance && AcrossOpening(p));
        Assert.Contains(wallMesh.Positions, p => Math.Abs(p.Z - 2100) < Tolerance && AcrossOpening(p));

        // And nothing of the wall remains inside the hole itself.
        Assert.DoesNotContain(wallMesh.Positions,
            p => p.X > 2400 + Tolerance && p.X < 3600 - Tolerance && p.Z > 900 + Tolerance && p.Z < 2100 - Tolerance);

        // And the glass fills exactly the hole.
        var glass = Assert.Single(meshes, m => m.ElementId == window.Id);
        Assert.Equal(MeshKind.Glazing, glass.Kind);
        Assert.True(glass.Opacity < 1);

        var glassBounds = glass.Bounds()!.Value;
        Assert.Equal(900, glassBounds.Min.Z, precision: 6);
        Assert.Equal(2100, glassBounds.Max.Z, precision: 6);
        Assert.Equal(1200, glassBounds.Max.X - glassBounds.Min.X, precision: 6);
    }

    [Fact]
    public void ADoorLeafFillsItsDoorway()
    {
        var document = Project();
        var wall = AddWall(document);

        var doorType = document.TypesOf<DoorType>().First(t => t.Width == 900);
        var door = new Door
        {
            TypeId = doorType.Id,
            LevelId = wall.LevelId,
            HostWallId = wall.Id,
            DistanceAlongWall = 3000
        };
        document.Add(door);

        var leaf = Assert.Single(ModelMeshBuilder.Build(document), m => m.ElementId == door.Id);
        var bounds = leaf.Bounds()!.Value;

        Assert.Equal(MeshKind.DoorLeaf, leaf.Kind);
        Assert.Equal(0, bounds.Min.Z, precision: 6);
        Assert.Equal(doorType.Height, bounds.Max.Z, precision: 6);
        Assert.Equal(2550, bounds.Min.X, precision: 6);
        Assert.Equal(3450, bounds.Max.X, precision: 6);
    }

    // ---- slabs ------------------------------------------------------------------

    [Fact]
    public void AFloorHangsBelowItsLevelByItsThickness()
    {
        var document = Project();
        var type = document.TypesOf<FloorType>().Single(t => t.Name.Contains("Screed"));

        var floor = new Floor { TypeId = type.Id, LevelId = document.Levels[1].Id };
        floor.SetBoundary(new[]
        {
            new Point2D(0, 0), new Point2D(6000, 0), new Point2D(6000, 4000), new Point2D(0, 4000)
        });
        document.Add(floor);

        var meshes = ModelMeshBuilder.Build(document).Where(m => m.ElementId == floor.Id).ToList();
        var bounds = ModelMeshBuilder.Bounds(meshes)!.Value;

        Assert.Equal(type.Structure.Layers.Count, meshes.Count);
        Assert.Equal(3000, bounds.Max.Z, precision: 6);
        Assert.Equal(3000 - type.Thickness, bounds.Min.Z, precision: 6);
    }

    [Fact]
    public void FloorsCeilingsAndRoofsAreKeptApartSoEachCanBeHidden()
    {
        var document = Project();
        var level = document.Levels[0].Id;
        var outline = new[]
        {
            new Point2D(0, 0), new Point2D(6000, 0), new Point2D(6000, 4000), new Point2D(0, 4000)
        };

        var floor = new Floor { TypeId = document.TypesOf<FloorType>().First().Id, LevelId = level };
        var ceiling = new Ceiling { TypeId = document.TypesOf<CeilingType>().First().Id, LevelId = level, HeightOffset = 2700 };
        var roof = new Roof { TypeId = document.TypesOf<RoofType>().First().Id, LevelId = level, HeightOffset = 3300 };

        foreach (var slab in new Slab[] { floor, ceiling, roof })
        {
            slab.SetBoundary(outline);
            document.Add(slab);
        }

        var meshes = ModelMeshBuilder.Build(document);

        // Lifting the roof off to see in must not take the floors with it.
        Assert.All(meshes.Where(m => m.ElementId == floor.Id), m => Assert.Equal(MeshKind.Floor, m.Kind));
        Assert.All(meshes.Where(m => m.ElementId == ceiling.Id), m => Assert.Equal(MeshKind.Ceiling, m.Kind));
        Assert.All(meshes.Where(m => m.ElementId == roof.Id), m => Assert.Equal(MeshKind.Roof, m.Kind));
    }

    [Fact]
    public void AGroundFloorSlabLiesBelowGroundLevelWhileItsWallsStandOnIt()
    {
        var document = Project();
        var level = document.Levels[0];
        var wall = AddWall(document);

        var floor = new Floor { TypeId = document.TypesOf<FloorType>().Single(t => t.Name.Contains("Screed")).Id, LevelId = level.Id };
        floor.SetBoundary(new[]
        {
            new Point2D(0, 100), new Point2D(6000, 100), new Point2D(6000, 4000), new Point2D(0, 4000)
        });
        document.Add(floor);

        var meshes = ModelMeshBuilder.Build(document);
        var slab = ModelMeshBuilder.Bounds(meshes.Where(m => m.ElementId == floor.Id))!.Value;
        var walls = ModelMeshBuilder.Bounds(meshes.Where(m => m.ElementId == wall.Id))!.Value;

        // This is what made the 3D view show a band under the building: the walls start at the
        // level and the slab's build-up hangs below it. Both are right. The viewer is what has
        // to put the ground at ground level so the slab edge is buried, as it is on site.
        Assert.Equal(level.Elevation, walls.Min.Z, precision: 6);
        Assert.Equal(level.Elevation, slab.Max.Z, precision: 6);
        Assert.True(slab.Min.Z < level.Elevation);
    }

    [Fact]
    public void AnLShapedFloorIsNotFilledInAcrossItsMissingCorner()
    {
        var document = Project();
        var type = document.TypesOf<FloorType>().First();

        var floor = new Floor { TypeId = type.Id, LevelId = document.Levels[0].Id };
        floor.SetBoundary(new[]
        {
            new Point2D(0, 0), new Point2D(6000, 0), new Point2D(6000, 2000),
            new Point2D(3000, 2000), new Point2D(3000, 4000), new Point2D(0, 4000)
        });
        document.Add(floor);

        var top = ModelMeshBuilder.Build(document).First(m => m.ElementId == floor.Id);

        // Take just the upper cap - the triangles whose corners are all at the top surface.
        var capArea = 0.0;
        for (var i = 0; i < top.Indices.Count; i += 3)
        {
            var a = top.Positions[top.Indices[i]];
            var b = top.Positions[top.Indices[i + 1]];
            var c = top.Positions[top.Indices[i + 2]];

            if (Math.Abs(a.Z) > Tolerance || Math.Abs(b.Z) > Tolerance || Math.Abs(c.Z) > Tolerance) continue;

            capArea += Math.Abs((b.X - a.X) * (c.Y - a.Y) - (c.X - a.X) * (b.Y - a.Y)) / 2;
        }

        Assert.Equal(floor.Area, capArea, precision: 1);
    }

    // ---- the model as a whole ---------------------------------------------------

    [Fact]
    public void AnEmptyProjectHasNothingToDraw()
    {
        Assert.Empty(ModelMeshBuilder.Build(Project()));
        Assert.Null(ModelMeshBuilder.Bounds(ModelMeshBuilder.Build(Project())));
    }

    [Fact]
    public void RaisingALevelRaisesItsStoreyIn3DWithNothingElseToUpdate()
    {
        var document = Project();
        var history = new BIMDesigner.Core.Documents.Commands.UndoStack();
        var first = document.Levels[1];
        var wall = AddWall(document, levelId: first.Id);

        history.Execute(new BIMDesigner.Core.Documents.Commands.MoveLevelCommand(document, first, 4000));

        var bounds = ModelMeshBuilder.Bounds(ModelMeshBuilder.Build(document).Where(m => m.ElementId == wall.Id))!.Value;
        Assert.Equal(4000, bounds.Min.Z, precision: 6);
    }
}
