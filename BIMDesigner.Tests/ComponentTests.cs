using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;
using BIMDesigner.Infrastructure.Serialization;

namespace BIMDesigner.Tests;

/// <summary>
/// Components (specification section 2.1): instances of loadable families - furniture,
/// casework, fixtures - placed freestanding on a level or fixed to the face of a wall.
/// </summary>
public class ComponentTests
{
    private static (BimDocument Document, Wall Wall) Room()
    {
        var document = BimDocument.CreateDefault();
        var type = document.TypesOf<WallType>().First(t => t.Name.StartsWith("Exterior"));
        var wall = new Wall
        {
            Start = new Point2D(0, 0), End = new Point2D(6000, 0),
            TypeId = type.Id, LevelId = document.Levels[0].Id, UnconnectedHeight = 3000
        };
        document.Add(wall);
        return (document, wall);
    }

    private static ComponentType Family(BimDocument document, string name) =>
        document.TypesOf<ComponentType>().Single(t => t.Name == name);

    [Fact]
    public void AProjectStartsWithFamiliesInEveryComponentCategory()
    {
        var document = BimDocument.CreateDefault();
        var families = document.TypesOf<ComponentType>().ToList();

        // A library to lay a plan out with, not one family of each shape.
        Assert.True(families.Count >= 12, $"only {families.Count} families");
        foreach (var category in new[]
                 {
                     BuiltInCategory.Furniture, BuiltInCategory.Casework,
                     BuiltInCategory.PlumbingFixtures, BuiltInCategory.Planting
                 })
        {
            Assert.Contains(families, family => family.Kind == category);
        }

        // Every one of them is a size something could be built to.
        Assert.All(families, family =>
        {
            Assert.True(family.Width > 0 && family.Depth > 0 && family.Height > 0, family.Name);
            Assert.NotEmpty(ComponentModel.Parts(family));
        });

        // And a project made before components existed gains them when it is opened.
        var older = BimDocument.CreateDefault();
        foreach (var family in older.TypesOf<ComponentType>().ToList()) older.RemoveType(family);
        Assert.Empty(older.TypesOf<ComponentType>());

        older.EnsureDefaultTypes();
        Assert.Equal(families.Count, older.TypesOf<ComponentType>().Count());
    }

    [Fact]
    public void AFreestandingComponentStandsOnItsLevelWhereItIsPut()
    {
        var (document, _) = Room();
        var family = Family(document, "Table - Dining 1800 x 900");

        var table = new Component
        {
            TypeId = family.Id, TypeKind = family.Kind, LevelId = document.Levels[0].Id,
            Location = new Point2D(3000, 2000)
        };
        document.Add(table);

        // It is its family's category, not a category of its own.
        Assert.Equal(BuiltInCategory.Furniture, table.Category);

        var mesh = ModelMeshBuilder.BuildComponent(document, table).Single();
        var bounds = mesh.Bounds()!.Value;

        // As big as its family says, standing on the floor, centred where it was put.
        Assert.Equal(family.Width, bounds.Max.X - bounds.Min.X, precision: 6);
        Assert.Equal(family.Depth, bounds.Max.Y - bounds.Min.Y, precision: 6);
        Assert.Equal(family.Height, bounds.Max.Z, precision: 6);
        Assert.Equal(0, bounds.Min.Z, precision: 6);
        Assert.Equal(3000, (bounds.Min.X + bounds.Max.X) / 2, precision: 6);

        // Turned a quarter, it takes up the room the other way about.
        table.Rotation = 90;
        var turned = ModelMeshBuilder.BuildComponent(document, table).Single().Bounds()!.Value;
        Assert.Equal(family.Depth, turned.Max.X - turned.Min.X, precision: 3);
        Assert.Equal(family.Width, turned.Max.Y - turned.Min.Y, precision: 3);
    }

    [Fact]
    public void AComponentStandsOnTheStoreyItIsOn()
    {
        var (document, _) = Room();
        var family = Family(document, "Desk - 1500 x 750");
        var upstairs = document.Levels[1];

        var desk = new Component
        {
            TypeId = family.Id, TypeKind = family.Kind, LevelId = document.Levels[0].Id,
            Location = new Point2D(2000, 2000)
        };
        document.Add(desk);

        var ground = ModelMeshBuilder.BuildComponent(document, desk).Single().Bounds()!.Value.Min.Z;

        // Moving it to another level is the whole of moving a freestanding component to a
        // different storey: it stands on the floor of whichever one it is on.
        var level = desk.GetInstanceParameters(document).Single(p => p.Name == "Level");
        Assert.True(level.TrySet(upstairs.Name));
        Assert.Equal(upstairs.Id, desk.LevelId);

        var above = ModelMeshBuilder.BuildComponent(document, desk).Single().Bounds()!.Value.Min.Z;
        Assert.Equal(upstairs.Elevation, above, precision: 6);
        Assert.True(above > ground);

        // And it can be lifted off its level without changing storey.
        Assert.True(desk.GetInstanceParameters(document).Single(p => p.Name == "Elevation").TrySet(150.0));
        Assert.Equal(upstairs.Elevation + 150,
            ModelMeshBuilder.BuildComponent(document, desk).Single().Bounds()!.Value.Min.Z, precision: 6);
    }

    [Fact]
    public void AFaceBasedComponentStandsAgainstTheWallCarryingIt()
    {
        var (document, wall) = Room();
        var family = Family(document, "Wall Light");
        Assert.Equal(ComponentPlacement.FaceBased, family.Placement);

        var light = new Component { TypeId = family.Id, TypeKind = family.Kind, Elevation = family.DefaultElevation };
        document.Add(light);

        // Put on the exterior side of the wall, it faces out of that face.
        Assert.True(light.HostOn(document, wall, new Point2D(2000, 400)));
        Assert.Equal(wall.Id, light.HostId);
        Assert.True(light.IsHosted);
        Assert.False(light.FlipFacing);

        var wallType = document.GetWallType(wall)!;
        var bounds = ModelMeshBuilder.BuildComponent(document, light).Single().Bounds()!.Value;

        // Clear of the face it is fixed to, on that side, at the height its family says.
        Assert.True(bounds.Min.Y >= wallType.Width / 2 - 1e-6, "the light is inside the wall");
        Assert.Equal(family.DefaultElevation, bounds.Min.Z, precision: 6);

        // On the other side it is on the other face, facing the other way.
        Assert.True(light.HostOn(document, wall, new Point2D(2000, -400)));
        Assert.True(light.FlipFacing);
        Assert.True(ModelMeshBuilder.BuildComponent(document, light).Single().Bounds()!.Value.Max.Y
                    <= -wallType.Width / 2 + 1e-6);

        // It is carried by the wall, so it does not move on its own and it goes when the wall goes.
        Assert.False(ElementTransforms.CanMove(light));
        new DeleteElementCommand(document, wall).Redo();
        Assert.DoesNotContain(document.Elements.OfType<Component>(), c => c.Id == light.Id);
    }

    [Fact]
    public void AHostedComponentFollowsItsWall()
    {
        var (document, wall) = Room();
        var family = Family(document, "Basin - Wall Hung");

        var basin = new Component { TypeId = family.Id, TypeKind = family.Kind };
        document.Add(basin);
        Assert.True(basin.HostOn(document, wall, new Point2D(1500, 300)));

        var before = basin.Location;

        // The wall is moved a metre back; the basin is still on its face, the same way along it.
        wall.Start = new Point2D(0, 1000);
        wall.End = new Point2D(6000, 1000);
        basin.FollowHost(document);

        Assert.Equal(before.X, basin.Location.X, precision: 3);
        Assert.Equal(before.Y + 1000, basin.Location.Y, precision: 3);
    }

    [Fact]
    public void AWorkPlaneBasedComponentCanBeOnAWallOrOnALevel()
    {
        var (document, wall) = Room();
        var family = Family(document, "Wall Unit - 600");
        Assert.Equal(ComponentPlacement.WorkPlaneBased, family.Placement);

        var unit = new Component
        {
            TypeId = family.Id, TypeKind = family.Kind, LevelId = document.Levels[0].Id,
            Location = new Point2D(2000, 2000)
        };
        document.Add(unit);

        // On a level it is free to be moved about; fixed to a wall it is not.
        Assert.False(unit.IsHosted);
        Assert.True(ElementTransforms.CanMove(unit));

        Assert.True(unit.HostOn(document, wall, new Point2D(2000, 300)));
        Assert.True(unit.IsHosted);
        Assert.False(ElementTransforms.CanMove(unit));

        // Taken off the wall again it stands on its level, which is what its placement allows.
        unit.HostId = Guid.Empty;
        Assert.True(ElementTransforms.CanMove(unit));
    }

    [Fact]
    public void PickNewTakesOnlyWhatThePlacementModeAllows()
    {
        var (document, wall) = Room();
        var family = Family(document, "Wall Unit - 600");
        var level = document.Levels[0].Id;

        var floor = new Floor { LevelId = level, TypeId = document.TypesOf<FloorType>().First().Id };
        floor.SetBoundary(new[]
        {
            new Point2D(0, 0), new Point2D(6000, 0), new Point2D(6000, 4000), new Point2D(0, 4000)
        });
        document.Add(floor);

        var unit = new Component
        {
            TypeId = family.Id, TypeKind = family.Kind, LevelId = level, Location = new Point2D(3000, 2000)
        };
        document.Add(unit);

        var onTheWall = new Point2D(2000, 100);
        var inTheRoom = new Point2D(3000, 2000);

        // Vertical Face takes a wall and nothing else - that is the whole of what it is for.
        Assert.NotNull(ComponentHosting.MovedTo(document, unit, family, ComponentPlacementMode.VerticalFace, onTheWall, level));
        Assert.Null(ComponentHosting.MovedTo(document, unit, family, ComponentPlacementMode.VerticalFace, inTheRoom, level));

        // Face takes either, whichever way the face looks.
        var onWall = ComponentHosting.MovedTo(document, unit, family, ComponentPlacementMode.Face, onTheWall, level)!;
        Assert.Equal(wall.Id, onWall.HostId);

        var onFloor = ComponentHosting.MovedTo(document, unit, family, ComponentPlacementMode.Face, inTheRoom, level)!;
        Assert.Equal(floor.Id, onFloor.HostId);
        Assert.Equal(ComponentHostKind.SlabFace, ComponentHosting.KindOf(document, onFloor));

        // Work Plane takes it off its host altogether and stands it on the level.
        var onLevel = ComponentHosting.MovedTo(document, unit, family, ComponentPlacementMode.WorkPlane, inTheRoom, level)!;
        Assert.False(onLevel.IsHosted);
        Assert.Equal(level, onLevel.LevelId);

        // A face-based family has no business on a work plane, and is refused there.
        var faceOnly = Family(document, "Wall Light");
        Assert.False(ComponentHosting.Allows(faceOnly, ComponentPlacementMode.WorkPlane));
        Assert.Null(ComponentHosting.MovedTo(document, unit, faceOnly, ComponentPlacementMode.WorkPlane, inTheRoom, level));
    }

    [Fact]
    public void AComponentOnASlabSitsOnItsFace()
    {
        var (document, _) = Room();
        var family = Family(document, "Wall Light");
        var level = document.Levels[0].Id;

        // A ceiling over half the room only, so the other half is floor alone: where both are
        // under the cursor the face overhead is the one taken, which is the rule being shown.
        var ceiling = new Ceiling { LevelId = level, TypeId = document.TypesOf<CeilingType>().First().Id, HeightOffset = 2400 };
        ceiling.SetBoundary(new[]
        {
            new Point2D(0, 0), new Point2D(2000, 0), new Point2D(2000, 4000), new Point2D(0, 4000)
        });
        document.Add(ceiling);

        var light = new Component { TypeId = family.Id, TypeKind = family.Kind, LevelId = level };
        document.Add(light);

        // Moved onto the ceiling, it hangs from the soffit rather than standing on top of it -
        // which is where a light on a ceiling goes.
        var moved = ComponentHosting.MovedTo(document, light, family, ComponentPlacementMode.Face, new Point2D(1000, 2000), level)!;
        Assert.Equal(ceiling.Id, moved.HostId);
        Assert.True(moved.FlipFacing);

        new RehostComponentCommand(light, moved).Redo();

        var bounds = ModelMeshBuilder.BuildComponent(document, light).Single().Bounds()!.Value;
        Assert.Equal(ceiling.GetBottomElevation(document), bounds.Max.Z, precision: 6);
        Assert.Equal(ceiling.GetBottomElevation(document) - family.Height, bounds.Min.Z, precision: 6);

        // And a floor carries one on top of it instead, where there is no ceiling over it.
        var floor = new Floor { LevelId = level, TypeId = document.TypesOf<FloorType>().First().Id };
        floor.SetBoundary(new[]
        {
            new Point2D(0, 0), new Point2D(6000, 0), new Point2D(6000, 4000), new Point2D(0, 4000)
        });
        document.Add(floor);

        var onFloor = ComponentHosting.MovedTo(document, light, family, ComponentPlacementMode.Face, new Point2D(4000, 2000), level)!;
        Assert.Equal(floor.Id, onFloor.HostId);
        Assert.False(onFloor.FlipFacing);

        new RehostComponentCommand(light, onFloor).Redo();
        Assert.Equal(floor.GetTopElevation(document),
            ModelMeshBuilder.BuildComponent(document, light).Single().Bounds()!.Value.Min.Z, precision: 6);
    }

    [Fact]
    public void PickNewIsOneStepOnTheUndoStack()
    {
        var (document, wall) = Room();
        var family = Family(document, "Wall Light");
        var level = document.Levels[0].Id;

        var light = new Component { TypeId = family.Id, TypeKind = family.Kind, LevelId = level };
        document.Add(light);
        Assert.True(light.HostOn(document, wall, new Point2D(1000, 200)));

        var was = (light.HostId, light.Location, light.Rotation, light.Elevation, light.FlipFacing);

        var other = new Wall
        {
            Start = new Point2D(0, 0), End = new Point2D(0, 4000),
            TypeId = wall.TypeId, LevelId = level, UnconnectedHeight = 3000
        };
        document.Add(other);

        var moved = ComponentHosting.MovedTo(document, light, family, ComponentPlacementMode.VerticalFace, new Point2D(100, 2000), level)!;
        var command = new RehostComponentCommand(light, moved);

        command.Redo();
        Assert.Equal(other.Id, light.HostId);

        // Undone, every part of the move comes back - where it stood, which way it faced and
        // which side of the wall it was on, not only which wall it was in.
        command.Undo();
        Assert.Equal(was, (light.HostId, light.Location, light.Rotation, light.Elevation, light.FlipFacing));
    }

    [Fact]
    public void MovingAndMirroringAComponentTakesItsFacingWithIt()
    {
        var (document, _) = Room();
        var family = Family(document, "Chair - Dining");

        var chair = new Component
        {
            TypeId = family.Id, TypeKind = family.Kind, LevelId = document.Levels[0].Id,
            Location = new Point2D(1000, 1000), Rotation = 0
        };
        document.Add(chair);

        ElementTransforms.Move(chair, new Vector2D(500, 250));
        Assert.Equal(new Point2D(1500, 1250), chair.Location);

        // Placed square, it faces down the page, the way a furniture family is drawn.
        Assert.Equal(0, chair.Facing.X, precision: 6);
        Assert.Equal(-1, chair.Facing.Y, precision: 6);

        // Mirrored about a line up the page it stands across from where it was, still facing
        // the same way: a reflection across that axis does not turn it round.
        ElementTransforms.Mirror(chair, Line2D.Through(new Point2D(2000, 0), new Point2D(2000, 1000)));

        Assert.Equal(2500, chair.Location.X, precision: 6);
        Assert.Equal(1250, chair.Location.Y, precision: 6);
        Assert.Equal(-1, chair.Facing.Y, precision: 6);

        // Mirrored about a line across the page it faces back the other way - a chair reflected
        // across a table faces the table.
        ElementTransforms.Mirror(chair, Line2D.Through(new Point2D(0, 2000), new Point2D(1000, 2000)));

        Assert.Equal(2750, chair.Location.Y, precision: 6);
        Assert.Equal(1, chair.Facing.Y, precision: 6);
        Assert.Equal(180, ((chair.Rotation % 360) + 360) % 360, precision: 3);
    }

    [Fact]
    public void ComponentsAreSaved()
    {
        var (document, wall) = Room();
        var table = Family(document, "Table - Dining 1800 x 900");
        var light = Family(document, "Wall Light");

        var free = new Component
        {
            TypeId = table.Id, TypeKind = table.Kind, LevelId = document.Levels[0].Id,
            Location = new Point2D(2500, 1800), Rotation = 45, Elevation = 25, Mark = "FF-01"
        };
        document.Add(free);

        var fixture = new Component { TypeId = light.Id, TypeKind = light.Kind, Elevation = 2000 };
        document.Add(fixture);
        Assert.True(fixture.HostOn(document, wall, new Point2D(3000, 300)));

        var path = Path.Combine(Path.GetTempPath(), $"bimtest-{Guid.NewGuid():N}{ProjectFile.Extension}");
        try
        {
            ProjectFile.Save(document, path);
            var copy = ProjectFile.Load(path);

            var savedFree = copy.Elements.OfType<Component>().Single(c => c.Id == free.Id);
            Assert.Equal(free.Location, savedFree.Location);
            Assert.Equal(45, savedFree.Rotation, precision: 6);
            Assert.Equal(25, savedFree.Elevation, precision: 6);
            Assert.Equal("FF-01", savedFree.Mark);
            Assert.Equal(BuiltInCategory.Furniture, savedFree.Category);

            var savedFixture = copy.Elements.OfType<Component>().Single(c => c.Id == fixture.Id);
            Assert.Equal(wall.Id, savedFixture.HostId);
            Assert.Equal(2000, savedFixture.Elevation, precision: 6);

            // The families come back with their sizes, so the geometry is the same geometry.
            var family = copy.FindType<ComponentType>(table.Id)!;
            Assert.Equal(table.Width, family.Width, precision: 6);
            Assert.Equal(table.Form, family.Form);
            Assert.Equal(table.Placement, family.Placement);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void EachFormIsDrawnAsSomethingYouCanTellApart()
    {
        var document = BimDocument.CreateDefault();

        foreach (var form in Enum.GetValues<ComponentForm>())
        {
            var family = new ComponentType($"Test {form}", BuiltInCategory.Furniture, form, 900, 700, 800);
            document.AddType(family);

            var component = new Component
            {
                TypeId = family.Id, TypeKind = family.Kind, LevelId = document.Levels[0].Id,
                Location = new Point2D(0, 0)
            };

            // Every form is made of something, and nothing it is made of escapes its own size.
            var parts = ComponentModel.Parts(family);
            Assert.NotEmpty(parts);
            Assert.All(parts, part =>
            {
                Assert.True(part.To > part.From, $"{form}: no width");
                Assert.True(part.Front > part.Back, $"{form}: no depth");
                Assert.True(part.Top > part.Bottom, $"{form}: no height");
                Assert.True(part.Bottom >= -1e-6, $"{form}: a part is below the floor");
            });

            var mesh = ModelMeshBuilder.BuildComponent(document, component).Single();
            Assert.False(mesh.IsEmpty, $"{form} builds nothing");

            // In plan it is its footprint, and all but the plainest have something drawn inside it.
            var outlines = ComponentModel.PlanOutlines(family, ComponentModel.FrameOf(component, 0));
            Assert.Equal(4, outlines[0].Count);
            if (form is not (ComponentForm.Box or ComponentForm.Table or ComponentForm.WallLight))
                Assert.True(outlines.Count > 1, $"{form} is drawn as a plain rectangle in plan");
        }
    }
}
