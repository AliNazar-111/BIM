using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Annotation;
using BIMDesigner.Core.Materials;
using BIMDesigner.Core.Views;
using BIMDesigner.Infrastructure.Serialization;

namespace BIMDesigner.Tests;

/// <summary>
/// Architectural columns (specification section 3.2): the box-out round a structural column,
/// and the pier or pilaster that is there to be seen. Their height is what they stand between.
/// </summary>
public class ColumnTests
{
    private static BimDocument Project() => BimDocument.CreateDefault();

    private static ColumnType Type(BimDocument document, string name) =>
        document.TypesOf<ColumnType>().Single(t => t.Name == name);

    private static Column Place(BimDocument document, ColumnType type, Point2D at)
    {
        var column = new Column { TypeId = type.Id, LevelId = document.Levels[0].Id, Location = at };
        document.Add(column);
        return column;
    }

    [Fact]
    public void AProjectStartsWithColumnTypesOfEveryShape()
    {
        var document = Project();
        var types = document.TypesOf<ColumnType>().ToList();

        Assert.True(types.Count >= 4, $"only {types.Count} column types");

        // One of every shape the template can generate. Custom is what the editor draws, so
        // there is nothing for the template to put in it.
        foreach (var shape in Enum.GetValues<ColumnShape>().Where(shape => shape != ColumnShape.Custom))
            Assert.Contains(types, type => type.Shape == shape);

        Assert.All(types, type =>
        {
            Assert.True(type.Width > 0 && type.Depth > 0, type.Name);
            Assert.True(type.SectionArea > 0, type.Name);
        });

        // A project made before columns existed gains the types when it is opened.
        var older = Project();
        foreach (var type in older.TypesOf<ColumnType>().ToList()) older.RemoveType(type);
        Assert.Empty(older.TypesOf<ColumnType>());

        older.EnsureDefaultTypes();
        Assert.Equal(types.Count, older.TypesOf<ColumnType>().Count());
    }

    [Fact]
    public void ItsHeightIsWhatItStandsBetween()
    {
        var document = Project();
        var type = Type(document, "Column - 300 x 300");
        var column = Place(document, type, new Point2D(2000, 2000));

        // Unconnected, it is as high as it says.
        Assert.Equal(3000, column.GetHeight(document), precision: 6);
        Assert.Equal("Unconnected", column.DescribeTop(document));

        // Given a top level it reaches that level, and an offset moves the top with it.
        var upstairs = document.Levels[1];
        column.TopLevelId = upstairs.Id;
        Assert.Equal(upstairs.Elevation, column.GetTopElevation(document), precision: 6);
        Assert.Equal($"Up to level: {upstairs.Name}", column.DescribeTop(document));

        column.TopOffset = -400;
        Assert.Equal(upstairs.Elevation - 400, column.GetTopElevation(document), precision: 6);

        // A base offset lifts the whole column, so its height is unchanged by it.
        var before = column.GetHeight(document);
        column.BaseOffset = 150;
        Assert.Equal(150, column.GetBaseElevation(document), precision: 6);
        Assert.Equal(before - 150, column.GetHeight(document), precision: 6);
    }

    [Fact]
    public void AttachingItToACeilingCutsItToTheCeiling()
    {
        var document = Project();
        var type = Type(document, "Column - 450 x 450");
        var column = Place(document, type, new Point2D(2000, 2000));

        var ceiling = new Ceiling
        {
            LevelId = document.Levels[0].Id,
            TypeId = document.TypesOf<CeilingType>().First().Id,
            HeightOffset = 2400
        };
        ceiling.SetBoundary(new[]
        {
            new Point2D(0, 0), new Point2D(6000, 0), new Point2D(6000, 5000), new Point2D(0, 5000)
        });
        document.Add(ceiling);

        // The ceiling over it is found rather than picked, as a wall's is.
        Assert.Same(ceiling, ColumnAttachments.SlabAbove(document, column));

        var command = new AttachColumnsCommand(
            new[] { (column, true, (Guid?)ceiling.Id) }, "Attach Column Tops");
        command.Redo();

        Assert.Equal(ceiling.GetBottomElevation(document), column.GetTopElevation(document), precision: 6);
        Assert.Equal("Attached to Ceilings", column.DescribeTop(document));

        // It follows the ceiling: raised, the column grows with it, with nothing retyped.
        var was = column.GetHeight(document);
        ceiling.HeightOffset = 2700;
        Assert.Equal(was + 300, column.GetHeight(document), precision: 6);

        // And the 3D solid is cut to it too, not just the number in Properties.
        var solid = ModelMeshBuilder.BuildColumn(document, column).Single().Bounds()!.Value;
        Assert.Equal(ceiling.GetBottomElevation(document), solid.Max.Z, precision: 6);

        command.Undo();
        Assert.Equal(3000, column.GetHeight(document), precision: 6);
    }

    [Fact]
    public void AColumnJoinedToAWallIsDrawnAsThatWall()
    {
        var document = Project();
        var type = Type(document, "Column - 300 x 300");
        var wallType = document.TypesOf<WallType>().First(t => t.Name.StartsWith("Exterior"));

        var wall = new Wall
        {
            Start = new Point2D(0, 0), End = new Point2D(6000, 0),
            TypeId = wallType.Id, LevelId = document.Levels[0].Id, UnconnectedHeight = 3000
        };
        document.Add(wall);

        // Standing clear of every wall, it is its own type's fill and its own material.
        var free = Place(document, type, new Point2D(3000, 2000));
        Assert.Null(ColumnJoins.JoinedWall(document, free));
        Assert.Equal(type.CoarseScaleFillColour, ColumnJoins.CoarseFill(document, free, type));
        Assert.Equal(type.MaterialId, ColumnJoins.CutMaterial(document, free, type));

        // Built into the wall, it takes the wall's coarse fill: the two read as one piece of
        // construction rather than a column standing in front of a wall.
        var built = Place(document, type, new Point2D(3000, 0));
        Assert.Same(wall, ColumnJoins.JoinedWall(document, built));
        Assert.Equal(wallType.CoarseScaleFillColour, ColumnJoins.CoarseFill(document, built, type));
        Assert.NotEqual(type.CoarseScaleFillColour, ColumnJoins.CoarseFill(document, built, type));

        // And it is drawn in the wall's structural material, being made of the same thing.
        var core = wallType.Structure.Layers.First(layer => layer.Function == LayerFunction.Structure);
        Assert.Equal(core.MaterialId, ColumnJoins.CutMaterial(document, built, type));
    }

    [Fact]
    public void AWallsLayersWrapAtAColumnEngagedInIt()
    {
        var document = Project();
        var wallType = document.TypesOf<WallType>().Single(t => t.Name.Contains("Brick on Block"));
        var wall = new Wall
        {
            Start = new Point2D(0, 0), End = new Point2D(6000, 0),
            TypeId = wallType.Id, LevelId = document.Levels[0].Id, UnconnectedHeight = 3000
        };
        document.Add(wall);

        // With nothing in it the wall is one stretch from end to end.
        Assert.Single(WallSlices.Solid(document, wall, wallType));

        // A column right through the wall interrupts it: the wall stops either side and the
        // column fills the gap, which is what an engaged column is.
        var through = Type(document, "Column - 450 x 450");
        var engaged = Place(document, through, new Point2D(3000, 0));

        var crossings = ColumnJoins.Crossings(document, wall, wallType);
        Assert.Single(crossings);
        Assert.Equal(3000 - through.Width / 2, crossings[0].From, precision: 6);
        Assert.Equal(3000 + through.Width / 2, crossings[0].To, precision: 6);

        var slices = WallSlices.Solid(document, wall, wallType);
        Assert.Equal(2, slices.Count);

        // And the layers wrap at the sides of the gap, as they do at a doorway: the brick turns
        // into the reveal instead of leaving the cavity and the blockwork on show at the column.
        Assert.Equal(wallType.WrapAtInserts, slices[0].CutTo.Wrapping);
        Assert.NotEqual(WallWrapping.None, slices[0].CutTo.Wrapping);

        var half = wallType.Width / 2;
        var brick = wallType.Structure.GetLayerOffsets().First();
        var face = WallJoins.EndPoints(wall, wallType, half - brick.Start, half - brick.End, slices[0].CutTo, atStart: false);
        Assert.True(face.Count > 2, "the brick is cut straight through at the column rather than returning");

        // A pilaster shallower than the wall stands proud of a wall that carries on behind it,
        // so there is nothing to interrupt and nothing to wrap.
        document.Remove(engaged);
        Place(document, Type(document, "Column - 300 x 300"), new Point2D(3000, 0));

        Assert.Empty(ColumnJoins.Crossings(document, wall, wallType));
        Assert.Single(WallSlices.Solid(document, wall, wallType));
    }

    [Fact]
    public void ASectionThroughAColumnCutsItAsTheWallItIsJoinedTo()
    {
        var document = Project();
        var wallType = document.TypesOf<WallType>().Single(t => t.Name.Contains("Brick on Block"));
        var wall = new Wall
        {
            Start = new Point2D(0, 0), End = new Point2D(6000, 0),
            TypeId = wallType.Id, LevelId = document.Levels[0].Id, UnconnectedHeight = 3000
        };
        document.Add(wall);

        var type = Type(document, "Column - 450 x 450");
        var built = Place(document, type, new Point2D(3000, 0));
        var free = Place(document, type, new Point2D(1000, 2500));

        // A section cutting across both of them.
        var marker = new SectionMarker
        {
            Start = new Point2D(500, 1500), End = new Point2D(500, -1500),
            LevelId = document.Levels[0].Id
        };
        document.Add(marker);

        var across = new SectionMarker
        {
            Start = new Point2D(-500, 2500), End = new Point2D(6500, 2500),
            LevelId = document.Levels[0].Id
        };
        document.Add(across);

        // The one standing free is cut in its own material, between its own base and top.
        var freePiece = SectionProjection.Build(document, across).Pieces.Single(p => p.ElementId == free.Id);
        Assert.Equal(0, freePiece.Bounds.Bottom, precision: 6);
        Assert.Equal(3000, freePiece.Bounds.Top, precision: 6);
        Assert.Equal(type.Width, freePiece.Bounds.Width, precision: 6);

        var own = document.FindMaterial(type.MaterialId)!;
        Assert.Equal(own.CutColour, freePiece.Fill);

        // The one built into the wall is cut as that wall is: same material, so the pier reads
        // as a thicker piece of the wall rather than a column standing in it.
        var wallSection = new SectionMarker
        {
            Start = new Point2D(3000, 1500), End = new Point2D(3000, -1500),
            LevelId = document.Levels[0].Id
        };
        document.Add(wallSection);

        var builtPiece = SectionProjection.Build(document, wallSection).Pieces.Single(p => p.ElementId == built.Id);
        var core = wallType.Structure.Layers.First(layer => layer.Function == LayerFunction.Structure);
        Assert.Equal(document.FindMaterial(core.MaterialId)!.CutColour, builtPiece.Fill);
    }

    [Fact]
    public void EachShapeIsTheSectionItSaysItIs()
    {
        var document = Project();

        foreach (var (name, shape) in new[]
                 {
                     ("Column - 300 x 600", ColumnShape.Rectangular),
                     ("Column - Round 400", ColumnShape.Round),
                     ("Column - Corner 400", ColumnShape.LShaped)
                 })
        {
            var type = Type(document, name);
            Assert.Equal(shape, type.Shape);

            var column = Place(document, type, new Point2D(1000, 1000));
            var outline = column.Outline(type);

            Assert.True(outline.Count >= 4, $"{name} has no section");
            Assert.All(outline, point => Assert.True(double.IsFinite(point.X) && double.IsFinite(point.Y)));

            // Its section is inside the size its type gives, and its area is what the type says.
            Assert.True(outline.Max(p => p.X) - outline.Min(p => p.X) <= type.Width + 1e-6, name);
            // A round one is drawn as a many-sided ring, so its area is within a fraction of a
            // percent of the circle its type is; the straight-sided ones are exact.
            var drawn = Math.Abs(Polygon2D.Area(outline));
            Assert.True(Math.Abs(drawn - type.SectionArea) <= type.SectionArea * 0.02,
                $"{name}: drawn {drawn:F0} against {type.SectionArea:F0}");

            // Turned, it stands in the same place but the other way about.
            column.Rotation = 90;
            var turned = column.Outline(type);
            Assert.Equal(type.Depth, turned.Max(p => p.X) - turned.Min(p => p.X), precision: 3);

            var solid = ModelMeshBuilder.BuildColumn(document, column).Single().Bounds()!.Value;
            Assert.Equal(0, solid.Min.Z, precision: 6);
            Assert.Equal(3000, solid.Max.Z, precision: 6);
        }
    }

    [Fact]
    public void ColumnsAreSetOutAtGridIntersections()
    {
        var document = Project();
        var type = Type(document, "Column - 450 x 450");
        var level = document.Levels[0].Id;

        // Two gridlines up the page and three across: six intersections between them.
        var grids = new List<Grid>();
        foreach (var x in new[] { 0.0, 6000.0 })
            grids.Add(new Grid { Start = new Point2D(x, -1000), End = new Point2D(x, 13000), LevelId = level });

        foreach (var y in new[] { 0.0, 6000.0, 12000.0 })
            grids.Add(new Grid { Start = new Point2D(-1000, y), End = new Point2D(7000, y), LevelId = level });

        foreach (var grid in grids) document.Add(grid);

        // Parallel grids never meet, so only the crossings count.
        var crossings = ColumnGrids.Intersections(grids);
        Assert.Equal(6, crossings.Count);

        var columns = ColumnGrids.AtIntersections(document, grids, type, level);
        Assert.Equal(6, columns.Count);
        Assert.All(columns, column => Assert.Contains(crossings, point => point.DistanceTo(column.Location) < 1e-6));

        foreach (var column in columns) document.Add(column);

        // Run again, nothing is stacked on an intersection that already has a column.
        Assert.Empty(ColumnGrids.AtIntersections(document, grids, type, level));
    }

    [Fact]
    public void AColumnOnAGridComesAlongWhenTheGridMoves()
    {
        var document = Project();
        var type = Type(document, "Column - 450 x 450");
        var level = document.Levels[0].Id;

        var grid = new Grid { Start = new Point2D(3000, -1000), End = new Point2D(3000, 9000), LevelId = level };
        document.Add(grid);

        var onIt = Place(document, type, new Point2D(3000, 4000));
        var offIt = Place(document, type, new Point2D(5000, 4000));

        Assert.True(ColumnGrids.IsOn(grid, onIt, type));
        Assert.False(ColumnGrids.IsOn(grid, offIt, type));

        // Moving the grid moves the column set out on it, and leaves the other where it is.
        var move = new MoveElementsCommand(new[] { grid }, new Vector2D(500, 0), document: document);
        move.Redo();

        Assert.Equal(3500, onIt.Location.X, precision: 6);
        Assert.Equal(5000, offIt.Location.X, precision: 6);

        // One Undo puts the bay back: the grid and its column moved as one step.
        move.Undo();
        Assert.Equal(3000, onIt.Location.X, precision: 6);

        // A column told to stay put does, which is what someone does after moving one off the
        // setting out on purpose.
        onIt.MovesWithGrids = false;
        var again = new MoveElementsCommand(new[] { grid }, new Vector2D(500, 0), document: document);
        again.Redo();

        Assert.Equal(3000, onIt.Location.X, precision: 6);
        Assert.Equal(3500, grid.Start.X, precision: 6);
    }

    [Fact]
    public void ATypesOwnOffsetsMoveEveryColumnPlacedFromIt()
    {
        var document = Project();
        var type = Type(document, "Column - 300 x 300");
        var column = Place(document, type, new Point2D(2000, 2000));
        column.TopLevelId = document.Levels[1].Id;

        var was = (Base: column.GetBaseElevation(document), Top: column.GetTopElevation(document));

        // Offsets that belong to the family: a column that always stands on a 50 mm plinth and
        // runs 100 past its top level says so once, and every one placed from it does it.
        type.OffsetBase = 50;
        type.OffsetTop = 100;

        Assert.Equal(was.Base - 50, column.GetBaseElevation(document), precision: 6);
        Assert.Equal(was.Top + 100, column.GetTopElevation(document), precision: 6);

        // Another column of the same type is moved with it; one of another type is not.
        var sibling = Place(document, type, new Point2D(4000, 2000));
        Assert.Equal(-50, sibling.GetBaseElevation(document), precision: 6);
        Assert.Equal(0, Place(document, Type(document, "Column - Round 400"), new Point2D(1000, 1000))
            .GetBaseElevation(document), precision: 6);
    }

    [Fact]
    public void AttachmentStyleDecidesWhetherTheColumnStopsAtTheFaceOrRunsThroughIt()
    {
        var document = Project();
        var type = Type(document, "Column - 450 x 450");
        var column = Place(document, type, new Point2D(2000, 2000));

        var roof = new Roof
        {
            LevelId = document.Levels[0].Id,
            TypeId = document.TypesOf<RoofType>().First().Id,
            HeightOffset = 3000
        };
        roof.SetBoundary(new[]
        {
            new Point2D(0, 0), new Point2D(6000, 0), new Point2D(6000, 5000), new Point2D(0, 5000)
        });
        document.Add(roof);

        var soffit = roof.GetBottomElevation(document);
        var over = roof.GetTopElevation(document);
        Assert.True(over > soffit, "the roof has no thickness to tell the two styles apart");

        // Cut Column: it stops at the face it meets, which is what a column butting a soffit does.
        var cut = new AttachColumnsCommand(
            new[] { (column, true, (Guid?)roof.Id) }, "Attach", ColumnAttachmentStyle.CutColumn);
        cut.Redo();

        Assert.Equal(soffit, column.GetTopElevation(document), precision: 6);

        // Do Not Cut: it carries on through the roof, which is what a column doing the carrying does.
        var through = new AttachColumnsCommand(
            new[] { (column, true, (Guid?)roof.Id) }, "Attach", ColumnAttachmentStyle.DoNotCut);
        through.Redo();

        Assert.Equal(over, column.GetTopElevation(document), precision: 6);
        Assert.Equal(over, ModelMeshBuilder.BuildColumn(document, column).Single().Bounds()!.Value.Max.Z, precision: 6);

        // Undone, the style comes back with it, not only what was holding the column.
        through.Undo();
        Assert.Equal(ColumnAttachmentStyle.CutColumn, column.TopAttachmentStyle);
        Assert.Equal(soffit, column.GetTopElevation(document), precision: 6);

        // A base attached to a floor works the same way round: standing on it, or through it.
        var floor = new Floor { LevelId = document.Levels[0].Id, TypeId = document.TypesOf<FloorType>().First().Id };
        floor.SetBoundary(roof.Boundary.ToList());
        document.Add(floor);

        new AttachColumnsCommand(
            new[] { (column, false, (Guid?)floor.Id) }, "Attach", ColumnAttachmentStyle.CutColumn).Redo();
        Assert.Equal(floor.GetTopElevation(document), column.GetBaseElevation(document), precision: 6);

        new AttachColumnsCommand(
            new[] { (column, false, (Guid?)floor.Id) }, "Attach", ColumnAttachmentStyle.DoNotCut).Redo();
        Assert.Equal(floor.GetBottomElevation(document), column.GetBaseElevation(document), precision: 6);
    }

    [Fact]
    public void AnAttachedColumnCanBeHeldClearOfWhatItMeets()
    {
        var document = Project();
        var type = Type(document, "Column - 450 x 450");
        var column = Place(document, type, new Point2D(2000, 2000));

        var ceiling = new Ceiling
        {
            LevelId = document.Levels[0].Id,
            TypeId = document.TypesOf<CeilingType>().First().Id,
            HeightOffset = 2400
        };
        ceiling.SetBoundary(new[]
        {
            new Point2D(0, 0), new Point2D(6000, 0), new Point2D(6000, 5000), new Point2D(0, 5000)
        });
        document.Add(ceiling);

        // Nothing is holding it yet, so there is nothing to offset from and it is not offered.
        Assert.False(column.TopIsAttached);
        Assert.DoesNotContain(column.GetInstanceParameters(document),
            p => p.Name == ColumnParameters.OffsetFromAttachmentAtTop.Name);

        column.TopAttachedTo = ceiling.Id;
        Assert.True(column.TopIsAttached);

        var met = column.GetTopElevation(document);
        Assert.Equal(ceiling.GetBottomElevation(document), met, precision: 6);

        // Pulled back from the soffit: a column held clear of a slab it should not carry.
        Assert.True(column.GetInstanceParameters(document)
            .Single(p => p.Name == ColumnParameters.OffsetFromAttachmentAtTop.Name).TrySet(25.0));

        Assert.Equal(met - 25, column.GetTopElevation(document), precision: 6);
        Assert.Equal(met - 25, ModelMeshBuilder.BuildColumn(document, column).Single().Bounds()!.Value.Max.Z, precision: 6);
    }

    [Fact]
    public void ARoomBoundingColumnTakesItsAreaOutOfTheRoom()
    {
        var document = Project();
        var wallType = document.TypesOf<WallType>().First(t => t.Name.StartsWith("Interior"));
        var level = document.Levels[0].Id;

        var corners = new[]
        {
            new Point2D(0, 0), new Point2D(6000, 0), new Point2D(6000, 5000), new Point2D(0, 5000)
        };

        for (var i = 0; i < 4; i++)
            document.Add(new Wall
            {
                Start = corners[i], End = corners[(i + 1) % 4],
                TypeId = wallType.Id, LevelId = level, UnconnectedHeight = 3000
            });

        var empty = RoomBoundary.Trace(document, level, new Point2D(3000, 2500));
        Assert.True(empty.IsEnclosed);

        // A column standing in the room is floor the room has not got.
        var type = Type(document, "Column - 450 x 450");
        var column = Place(document, type, new Point2D(3000, 2500));

        var withColumn = RoomBoundary.Trace(document, level, new Point2D(2000, 2500));
        Assert.Contains(column, withColumn.BoundingColumns);
        Assert.Equal(empty.Area - type.SectionArea, withColumn.Area, precision: 3);
        Assert.True(withColumn.Perimeter > empty.Perimeter, "the column's sides are not on the perimeter");

        // Told not to bound the room, it is counted as floor again.
        column.RoomBounding = false;
        var ignored = RoomBoundary.Trace(document, level, new Point2D(2000, 2500));
        Assert.Empty(ignored.BoundingColumns);
        Assert.Equal(empty.Area, ignored.Area, precision: 3);

        // And one built into a wall is not counted at all: the wall has already shaped the room.
        column.RoomBounding = true;
        column.Location = new Point2D(3000, 0);
        Assert.Empty(RoomBoundary.Trace(document, level, new Point2D(2000, 2500)).BoundingColumns);
    }

    [Fact]
    public void ATypeSaysWhatItIsMadeOfAndHowItIsFilled()
    {
        var document = Project();
        var type = Type(document, "Column - 300 x 300");

        var material = type.GetTypeParameters(document).Single(p => p.Name == ColumnTypeParameters.Material.Name);
        Assert.Equal(document.FindMaterial(type.MaterialId)!.Name, material.DisplayValue);

        var concrete = document.Materials.First(m => m.Name.Contains("Concrete, Cast"));
        Assert.True(material.TrySet(concrete.Name));
        Assert.Equal(concrete.Id, type.MaterialId);

        // Set back to nothing, it falls to its category rather than keeping a stale material.
        Assert.True(type.GetTypeParameters(document).Single(p => p.Name == ColumnTypeParameters.Material.Name)
            .TrySet("<by category>"));
        Assert.Equal(Guid.Empty, type.MaterialId);

        // The coarse fill is the type's own, and rubbish typed into it is refused rather than
        // turning the column a colour nobody asked for.
        var fill = type.GetTypeParameters(document).Single(p => p.Name == ColumnTypeParameters.CoarseScaleFillColour.Name);
        Assert.Equal(type.CoarseScaleFillColour.ToString(), fill.DisplayValue);

        Assert.True(fill.TrySet("#204060"));
        Assert.Equal(new ColourRgb(0x20, 0x40, 0x60), type.CoarseScaleFillColour);

        type.GetTypeParameters(document).Single(p => p.Name == ColumnTypeParameters.CoarseScaleFillColour.Name).TrySet("nonsense");
        Assert.Equal(new ColourRgb(0x20, 0x40, 0x60), type.CoarseScaleFillColour);
    }

    [Fact]
    public void AColumnOnAWallSitsFlushWithAFaceRatherThanStraddlingIt()
    {
        var document = Project();
        var wallType = document.TypesOf<WallType>().Single(t => t.Name.Contains("Brick on Block"));
        var wall = new Wall
        {
            Start = new Point2D(0, 0), End = new Point2D(6000, 0),
            TypeId = wallType.Id, LevelId = document.Levels[0].Id, UnconnectedHeight = 3000
        };
        document.Add(wall);

        // A column wider than the wall, put on its centreline: it would stand out of both faces.
        var type = Type(document, "Column - 450 x 450");
        var column = Place(document, type, new Point2D(3000, 0));

        var half = wallType.Width / 2;
        var depth = type.Depth / 2;

        // Set against the exterior face, its back is on that face and it steps into the room.
        var outer = ColumnJoins.FlushWith(document, column, type, ColumnJoins.Face.Exterior)!.Value;
        column.Location = outer;

        var located = column.Outline(type).Select(corner => wall.Locate(wallType.Structure, corner)).ToList();
        Assert.Equal(half, located.Max(p => p.Across), precision: 3);
        Assert.Equal(half - type.Depth, located.Min(p => p.Across), precision: 3);

        // Already flush, there is nothing more to do on that face - so Align moves it across.
        Assert.Null(ColumnJoins.FlushWith(document, column, type, ColumnJoins.Face.Exterior));

        var inner = ColumnJoins.FlushWith(document, column, type, ColumnJoins.Face.Interior)!.Value;
        column.Location = inner;

        located = column.Outline(type).Select(corner => wall.Locate(wallType.Structure, corner)).ToList();
        Assert.Equal(-half, located.Min(p => p.Across), precision: 3);
        Assert.Equal(-half + type.Depth, located.Max(p => p.Across), precision: 3);

        // It stays where it was along the wall: lining up is across the wall, not along it.
        Assert.Equal(3000, wall.Locate(wallType.Structure, column.Location).Along, precision: 3);

        // The face nearer where it was put is the one it is set against, which is what makes
        // clicking on the outside of a wall put the pier outside.
        Assert.Equal(ColumnJoins.Face.Exterior, ColumnJoins.NearestFace(document, column, new Point2D(3000, 100)));
        Assert.Equal(ColumnJoins.Face.Interior, ColumnJoins.NearestFace(document, column, new Point2D(3000, -100)));

        // A column clear of every wall has no face to line up with.
        var free = Place(document, type, new Point2D(3000, 3000));
        Assert.Null(ColumnJoins.FlushWith(document, free, type, ColumnJoins.Face.Exterior));
    }

    [Fact]
    public void ASectionDrawnInTheEditorIsWhatGetsBuilt()
    {
        var document = Project();
        var type = Type(document, "Column - 450 x 450");
        var column = Place(document, type, new Point2D(2000, 2000));

        var solid = ModelMeshBuilder.BuildColumn(document, column).Single().Bounds()!.Value;
        Assert.Equal(450, solid.Max.X - solid.Min.X, precision: 3);

        // Draw: start from the square, cut a hole through the middle and add a wing on one side.
        var drawn = ColumnProfile.Rectangle(450, 450)
            .Combine(ColumnProfile.Ellipse(200, 200).Outer, BooleanOperation.Difference)
            .Combine(ColumnProfile.Rectangle(200, 120).Transformed(p => new Point2D(p.X + 300, p.Y)).Outer,
                BooleanOperation.Union);

        Assert.Single(drawn.Holes);

        var edit = new SetColumnProfileCommand(type, drawn);
        edit.Redo();

        // The type is the drawn section now, and its size is what the drawing actually reaches.
        Assert.Equal(ColumnShape.Custom, type.Shape);
        Assert.Equal(drawn.Area, type.SectionArea, precision: 3);
        Assert.Equal(400 + 225, type.Width, precision: 3);

        // The column follows its type: wider in plan, and hollow in 3D rather than solid.
        Assert.Equal(type.Width, column.Outline(type).Max(p => p.X) - column.Outline(type).Min(p => p.X), precision: 3);
        Assert.Single(column.SectionAt(type).Holes);

        var hollow = ModelMeshBuilder.BuildColumn(document, column).Single();
        Assert.Equal(type.Width, hollow.Bounds()!.Value.Max.X - hollow.Bounds()!.Value.Min.X, precision: 3);

        // The hole is a real void: nothing of the solid stands in the middle of it.
        var middle = column.SectionAt(type).Holes[0]
            .Aggregate(new Point2D(0, 0), (sum, p) => new Point2D(sum.X + p.X, sum.Y + p.Y));
        var centre = new Point2D(middle.X / column.SectionAt(type).Holes[0].Count, middle.Y / column.SectionAt(type).Holes[0].Count);
        Assert.False(PolygonBoolean.Inside(column.SectionAt(type).Loops, centre));

        // Every other column of the type changed with it, and undo puts them all back.
        var sibling = Place(document, type, new Point2D(4000, 2000));
        Assert.Single(sibling.SectionAt(type).Holes);

        edit.Undo();
        Assert.Equal(ColumnShape.Rectangular, type.Shape);
        Assert.Empty(column.SectionAt(type).Holes);
    }

    [Fact]
    public void ADrawnSectionIsSaved()
    {
        var document = Project();
        var type = Type(document, "Column - 300 x 300");

        var drawn = ColumnProfile.Rectangle(400, 400)
            .Combine(ColumnProfile.Rectangle(120, 120).Outer, BooleanOperation.Difference);

        new SetColumnProfileCommand(type, drawn).Redo();
        var column = Place(document, type, new Point2D(1500, 1500));

        var path = Path.Combine(Path.GetTempPath(), $"bimtest-{Guid.NewGuid():N}{ProjectFile.Extension}");
        try
        {
            ProjectFile.Save(document, path);
            var copy = ProjectFile.Load(path);

            var saved = copy.FindType<ColumnType>(type.Id)!;
            Assert.Equal(ColumnShape.Custom, saved.Shape);
            Assert.NotNull(saved.CustomProfile);
            Assert.Single(saved.CustomProfile!.Holes);
            Assert.Equal(drawn.Area, saved.SectionArea, precision: 3);

            // The column reopened is the same solid, hole and all.
            var reopened = copy.Elements.OfType<Column>().Single(c => c.Id == column.Id);
            Assert.Single(reopened.SectionAt(saved).Holes);
            Assert.False(ModelMeshBuilder.BuildColumn(copy, reopened).Single().IsEmpty);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void WhereAColumnSitsAgainstAWallIsAChoiceItRemembers()
    {
        var document = Project();
        var wallType = document.TypesOf<WallType>().Single(t => t.Name.Contains("Brick on Block"));
        var wall = new Wall
        {
            Start = new Point2D(0, 0), End = new Point2D(6000, 0),
            TypeId = wallType.Id, LevelId = document.Levels[0].Id, UnconnectedHeight = 3000
        };
        document.Add(wall);

        var type = Type(document, "Column - 450 x 450");
        var column = Place(document, type, new Point2D(3000, 0));
        var half = wallType.Width / 2;

        double Across() => wall.Locate(wallType.Structure, column.Location).Across;
        double Nearest() => column.Outline(type)
            .Select(corner => wall.Locate(wallType.Structure, corner).Across).Min();

        // Intersecting: straddling the line, standing out of both faces.
        Move(document, column, type, ColumnPlacement.IntersectingWall, ColumnJoins.Face.Exterior);
        Assert.Equal(0, Across(), precision: 3);

        // Embedded: inside the wall, flush with the face it was put on.
        Move(document, column, type, ColumnPlacement.EmbeddedInWall, ColumnJoins.Face.Exterior);
        Assert.Equal(half, column.Outline(type).Select(c => wall.Locate(wallType.Structure, c).Across).Max(), precision: 3);
        Assert.True(Nearest() < half, "an embedded column should be inside the wall");

        // Against: wholly outside the wall, its back on that face.
        Move(document, column, type, ColumnPlacement.AgainstWall, ColumnJoins.Face.Exterior);
        Assert.Equal(half, Nearest(), precision: 3);

        // And on the other face it is on the other side of the wall.
        Move(document, column, type, ColumnPlacement.AgainstWall, ColumnJoins.Face.Interior);
        Assert.Equal(-half, column.Outline(type).Select(c => wall.Locate(wallType.Structure, c).Across).Max(), precision: 3);

        // Freestanding asks for nothing, so nothing moves it.
        Assert.Null(ColumnWallPlacement.PositionFor(
            document, column, type, ColumnPlacement.Freestanding, ColumnJoins.Face.Interior));

        // The choice is on the column, so Properties can set it and it is remembered.
        var placement = column.GetInstanceParameters(document)
            .Single(p => p.Name == ColumnParameters.Placement.Name);

        Assert.True(placement.TrySet("Embedded In Wall"));
        Assert.Equal(ColumnPlacement.EmbeddedInWall, column.Placement);
    }

    private static void Move(
        BimDocument document, Column column, ColumnType type, ColumnPlacement placement, ColumnJoins.Face face)
    {
        column.Placement = placement;
        column.PlacementFace = face;

        if (ColumnWallPlacement.PositionFor(document, column, type, placement, face) is { } where)
            column.Location = where;
    }

    [Fact]
    public void AColumnInACornerTucksIntoBothWalls()
    {
        var document = Project();
        var wallType = document.TypesOf<WallType>().Single(t => t.Name.StartsWith("Interior"));
        var level = document.Levels[0].Id;

        foreach (var (start, end) in new[]
                 {
                     (new Point2D(0, 0), new Point2D(6000, 0)),
                     (new Point2D(0, 0), new Point2D(0, 5000))
                 })
        {
            document.Add(new Wall
            {
                Start = start, End = end, TypeId = wallType.Id, LevelId = level, UnconnectedHeight = 3000
            });
        }

        var type = Type(document, "Column - 300 x 300");
        var column = Place(document, type, new Point2D(400, 400));

        var where = ColumnWallPlacement.PositionFor(
            document, column, type, ColumnPlacement.WallCorner, ColumnJoins.Face.Interior);

        Assert.NotNull(where);
        column.Location = where!.Value;

        // Tucked into the corner the two inner faces make: touching both, inside the room.
        var half = wallType.Width / 2;
        Assert.Equal(half + type.Width / 2, column.Location.X, precision: 3);
        Assert.Equal(half + type.Depth / 2, column.Location.Y, precision: 3);

        // With no corner near it there is nothing to tuck into.
        var adrift = Place(document, type, new Point2D(4000, 3000));
        Assert.Null(ColumnWallPlacement.PositionFor(
            document, adrift, type, ColumnPlacement.WallCorner, ColumnJoins.Face.Interior));
    }

    [Fact]
    public void WhatIsBuriedInAWallIsCutOffTheColumn()
    {
        var document = Project();
        var wallType = document.TypesOf<WallType>().Single(t => t.Name.Contains("Brick on Block"));
        var wall = new Wall
        {
            Start = new Point2D(0, 0), End = new Point2D(6000, 0),
            TypeId = wallType.Id, LevelId = document.Levels[0].Id, UnconnectedHeight = 3000
        };
        document.Add(wall);

        var half = wallType.Width / 2;
        var type = Type(document, "Column - 450 x 450");

        // Standing against the wall with part of it inside: the buried part is not there.
        var column = Place(document, type, new Point2D(3000, half));
        var nominal = column.SectionAt(type);

        var clear = ColumnJoins.CutByWalls(document, column, type, nominal, 1000);

        Assert.True(clear.Area < nominal.Area, "nothing was cut off the column");
        Assert.True(clear.Area > nominal.Area * 0.4, "the whole column was cut away");

        // Nothing of what is left is inside the wall.
        Assert.All(clear.Outer, corner =>
            Assert.True(wall.Locate(wallType.Structure, corner).Across >= half - 1,
                $"a corner at {corner} is still inside the wall"));

        // The solid is cut too, not only the drawing.
        var solid = ModelMeshBuilder.BuildColumn(document, column).Single().Bounds()!.Value;
        Assert.True(solid.Min.Y >= half - 1, "the 3D column still reaches into the wall");

        // Told not to, it is left whole - the choice is the person's.
        column.CutByWalls = false;
        Assert.Equal(nominal.Area, ColumnJoins.CutByWalls(document, column, type, nominal, 1000).Area, precision: 3);
        Assert.True(ModelMeshBuilder.BuildColumn(document, column).Single().Bounds()!.Value.Min.Y < half - 1);
    }

    [Fact]
    public void APierRightThroughAWallIsNotCutByIt()
    {
        var document = Project();
        var wallType = document.TypesOf<WallType>().Single(t => t.Name.Contains("Brick on Block"));
        var wall = new Wall
        {
            Start = new Point2D(0, 0), End = new Point2D(6000, 0),
            TypeId = wallType.Id, LevelId = document.Levels[0].Id, UnconnectedHeight = 3000
        };
        document.Add(wall);

        // Wider than the wall and straddling it: this one is a pier, and the wall gives way to
        // it rather than the other way about.
        var type = Type(document, "Column - 450 x 450");
        var pier = Place(document, type, new Point2D(3000, 0));
        var nominal = pier.SectionAt(type);

        Assert.Equal(nominal.Area, ColumnJoins.CutByWalls(document, pier, type, nominal, 1000).Area, precision: 3);
        Assert.Single(ColumnJoins.Crossings(document, wall, wallType));

        // And a column nowhere near a wall is left alone whatever it is told.
        var adrift = Place(document, type, new Point2D(3000, 4000));
        var alone = adrift.SectionAt(type);
        Assert.Equal(alone.Area, ColumnJoins.CutByWalls(document, adrift, type, alone, 1000).Area, precision: 3);

        // Above the wall there is nothing to be buried in, so a tall column is whole up there.
        var tall = Place(document, type, new Point2D(1500, wallType.Width / 2));
        tall.UnconnectedHeight = 5000;

        var low = ColumnJoins.CutByWalls(document, tall, type, tall.SectionAt(type), 1000);
        var high = ColumnJoins.CutByWalls(document, tall, type, tall.SectionAt(type), 4000);

        Assert.True(low.Area < high.Area, "the column is cut where there is no wall");
        Assert.Equal(tall.SectionAt(type).Area, high.Area, precision: 3);
    }

    [Fact]
    public void ColumnsAreSaved()
    {
        var document = Project();
        var type = Type(document, "Column - Round 400");

        var column = Place(document, type, new Point2D(2500, 1500));
        column.Rotation = 30;
        column.BaseOffset = 100;
        column.TopLevelId = document.Levels[1].Id;
        column.TopOffset = -250;
        column.Mark = "C-01";

        var path = Path.Combine(Path.GetTempPath(), $"bimtest-{Guid.NewGuid():N}{ProjectFile.Extension}");
        try
        {
            ProjectFile.Save(document, path);
            var copy = ProjectFile.Load(path);
            var saved = copy.Elements.OfType<Column>().Single(c => c.Id == column.Id);

            Assert.Equal(column.Location, saved.Location);
            Assert.Equal(30, saved.Rotation, precision: 6);
            Assert.Equal(100, saved.BaseOffset, precision: 6);
            Assert.Equal(document.Levels[1].Id, saved.TopLevelId);
            Assert.Equal(-250, saved.TopOffset, precision: 6);
            Assert.Equal("C-01", saved.Mark);

            // The height comes back because what decides it comes back, not because it was saved.
            Assert.Equal(column.GetHeight(document), saved.GetHeight(copy), precision: 6);

            var savedType = copy.FindType<ColumnType>(type.Id)!;
            Assert.Equal(ColumnShape.Round, savedType.Shape);
            Assert.Equal(type.Width, savedType.Width, precision: 6);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
