using System.Windows;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Infrastructure.Serialization;
using BIMDesigner.UI;
using BIMDesigner.UI.Controls;

namespace BIMDesigner.Tests;

/// <summary>
/// The modelling tools a building is laid out with besides drawing it: chairs repeated round a
/// table; a wall's face, a column or a piece of furniture brought onto a gridline; a mass
/// reshaped by its handles; and groups - picked whole, copied as more of the same, edited once
/// for every one wherever and however each stands, broken up, and kept in the file.
/// </summary>
[Collection("Wpf")]
public class ModellingToolsTests
{
    private static void OnUi(Action act)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (Application.Current is null) new App().InitializeComponent();
                act();
            }
            catch (Exception e) { failure = e; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        if (failure is not null) throw failure;
    }

    private static (BimDocument Document, PlanView Plan, UndoStack History) Empty()
    {
        var document = BimDocument.CreateDefault();
        var history = new UndoStack();
        return (document, new PlanView { Document = document, ActiveLevelId = document.Levels[0].Id, History = history }, history);
    }

    private static Component Place(BimDocument document, string type, Point2D at, double rotation = 0)
    {
        var kind = document.TypesOf<ComponentType>().First(t => t.Name.StartsWith(type));
        var piece = new Component { TypeId = kind.Id, TypeKind = kind.Kind, LevelId = document.Levels[0].Id, Location = at, Rotation = rotation };
        document.Add(piece);
        return piece;
    }

    private static Wall AddWall(BimDocument document, Point2D from, Point2D to, string type = "Interior")
    {
        var wall = new Wall { Start = from, End = to, TypeId = document.TypesOf<WallType>().First(t => t.Name.StartsWith(type)).Id, LevelId = document.Levels[0].Id, UnconnectedHeight = 3000 };
        document.Add(wall);
        return wall;
    }

    [Fact]
    public void ChairsAreRepeatedRoundATable()
    {
        OnUi(() =>
        {
            var (document, plan, history) = Empty();
            var chair = Place(document, "Chair - Dining", new Point2D(0, 1000));

            // Four all the way round the table at the origin: a quarter turn apart, each facing in as the first does.
            plan.Select(chair);
            plan.ArrayCount = 4;
            plan.ArrayRadial = true;
            plan.ArrayAngle = 360;
            Assert.True(plan.RadialArrayAbout(new Point2D(0, 0)));
            var chairs = document.Elements.OfType<Component>().ToList();
            Assert.Equal(4, chairs.Count);
            foreach (var (x, y, turned) in new[] { (-1000.0, 0.0, 90.0), (0.0, -1000.0, 180.0), (1000.0, 0.0, 270.0) })
                Assert.Contains(chairs, c => c.Location.DistanceTo(new Point2D(x, y)) < 1 && Math.Abs(((c.Rotation % 360) + 360) % 360 - turned) < 0.01);

            // One step to undo; and a part way round puts the last that far from the first.
            history.Undo();
            Assert.Single(document.Elements.OfType<Component>());
            plan.Select(chair);
            plan.ArrayCount = 3;
            plan.ArrayAngle = 90;
            Assert.True(plan.RadialArrayAbout(new Point2D(0, 0)));
            Assert.Contains(document.Elements.OfType<Component>(), c => c.Location.DistanceTo(new Point2D(-1000, 0)) < 1);
            Assert.Contains(document.Elements.OfType<Component>(), c => c.Location.DistanceTo(new Point2D(-707.1, 707.1)) < 1);
        });
    }

    [Fact]
    public void AWallAColumnAndASofaAreBroughtOntoAGridline()
    {
        OnUi(() =>
        {
            var (document, plan, history) = Empty();
            var grid = new Grid { Start = new Point2D(2000, -3000), End = new Point2D(2000, 8000), Name = "1" };
            document.Add(grid);
            var wall = AddWall(document, new Point2D(3000, 0), new Point2D(3000, 5000));
            var half = document.GetWallType(wall)!.Structure.TotalWidth / 2;
            var column = new Column { TypeId = document.TypesOf<ColumnType>().First().Id, LevelId = document.Levels[0].Id, Location = new Point2D(2600, 6500), UnconnectedHeight = 3000 };
            document.Add(column);
            var sofa = Place(document, "Sofa - Three Seat", new Point2D(4000, -2000));

            plan.SetTool(PlanTool.Align);

            // The gridline, then the wall's face on the near side: the face comes onto the line.
            Assert.True(plan.AlignClick(new Point2D(2000, 2500)));
            Assert.True(plan.AlignClick(new Point2D(3000 - half + 5, 2500)));
            Assert.Equal(2000 + half, wall.Start.X, precision: 3);
            Assert.Equal(2000 + half, wall.End.X, precision: 3);

            // The same line kept: a column comes onto it by its middle, a sofa by its edge nearest the click.
            Assert.True(plan.AlignClick(new Point2D(2600, 6500)));
            Assert.Equal(2000, column.Location.X, precision: 3);
            var type = document.FindType<ComponentType>(sofa.TypeId)!;
            Assert.True(plan.AlignClick(new Point2D(4000 - type.Width / 2 + 20, -2000)));
            Assert.Equal(2000 + type.Width / 2, sofa.Location.X, precision: 3);

            // Each one step to undo.
            history.Undo();
            Assert.Equal(4000, sofa.Location.X, precision: 3);
        });
    }

    [Fact]
    public void AMassIsReshapedByItsHandles()
    {
        var document = BimDocument.CreateDefault();
        var mass = new Mass { LevelId = document.Levels[0].Id, Height = 9000 };
        mass.SetBoundary(new[] { new Point2D(0, 0), new Point2D(10000, 0), new Point2D(10000, 8000), new Point2D(0, 8000) });
        document.Add(mass);

        var handles = ElementHandles.Of(document, mass);
        var corner = handles.First(handle => handle.Key == "corner:2");
        var before = ElementHandles.Snapshot(mass);
        ElementHandles.Drag(document, mass, corner, new Point2D(12000, 9000), corner.Height);
        Assert.Contains(new Point2D(12000, 9000), mass.Boundary);

        // A "+" along a side adds a corner; the arrow on top raises it.
        var insert = ElementHandles.Of(document, mass).First(handle => handle.Key == "insert:0");
        ElementHandles.Drag(document, mass, insert, new Point2D(5000, -2000), insert.Height);
        Assert.Equal(5, mass.Boundary.Count);
        var raise = ElementHandles.Of(document, mass).Single(handle => handle.Key == "raise:top");
        ElementHandles.Drag(document, mass, raise, raise.At, 12000 + 300);
        Assert.Equal(12000, mass.Height, precision: 0);

        // Put back as it was, as undo does.
        ElementHandles.Restore(mass, before);
        Assert.Equal(4, mass.Boundary.Count);
        Assert.Equal(9000, mass.Height);

        // A round one by its radius.
        var round = new Mass { LevelId = document.Levels[0].Id };
        round.SetCircle(new Point2D(0, 0), 3000);
        document.Add(round);
        var radius = ElementHandles.Of(document, round).Single(handle => handle.Key == "radius");
        ElementHandles.Drag(document, round, radius, new Point2D(0, 4500), radius.Height);
        Assert.Equal(4500, round.Radius, precision: 3);
    }

    [Fact]
    public void AGroupIsPickedWholeCopiedAndEditedOnceForEveryOne()
    {
        OnUi(() =>
        {
            var (document, plan, history) = Empty();

            // A bathroom: two walls, a door in one, a basin - grouped.
            var back = AddWall(document, new Point2D(0, 3000), new Point2D(3000, 3000));
            var side = AddWall(document, new Point2D(0, 0), new Point2D(0, 3000));
            var door = new Door { HostWallId = side.Id, LevelId = side.LevelId, TypeId = document.TypesOf<DoorType>().First(t => t.Name.StartsWith("Single -")).Id, DistanceAlongWall = 1500 };
            document.Add(door);
            var basin = Place(document, "Basin - Pedestal", new Point2D(1500, 2500));
            plan.SelectMany(new Element[] { back, side, basin });
            var group = plan.GroupSelection()!;
            Assert.Equal(4, group.MemberIds.Count);
            Assert.Contains(door.Id, group.MemberIds);

            // Any of it picked picks all of it, the group first.
            plan.Select(basin);
            Assert.Same(group, plan.SelectedElements[0]);
            Assert.Equal(5, plan.SelectedElements.Count);

            // Copied across and turned a quarter: another of the same group, holding copies.
            plan.CopySelection();
            plan.PasteAt(new Point2D(10000, 1500));
            var copy = document.Elements.OfType<Group>().Single(g => !ReferenceEquals(g, group));
            Assert.Equal(group.Name, copy.Name);
            Assert.Equal(4, Groups.Members(document, copy).Count);
            plan.Select(copy);
            Assert.True(plan.RotateBy(90));
            Assert.Equal(90, copy.Rotation, precision: 6);

            // The first edited: the basin moved 500 to the right. Finished, the copy's basin moves
            // 500 the same way in its own frame - turned a quarter, that is 500 up.
            var copyBasin = Groups.Members(document, copy).OfType<Component>().Single();
            var was = copyBasin.Location;
            plan.Select(group);
            Assert.True(plan.BeginGroupEdit());
            plan.Select(basin);
            Assert.Equal(new Element[] { basin }, plan.SelectedElements);
            history.Execute(new MoveElementsCommand(new[] { basin }, new Vector2D(500, 0), document: document));
            Assert.True(plan.FinishGroupEdit());

            var remade = Groups.Members(document, copy).OfType<Component>().Single();
            Assert.Equal(was.X, remade.Location.X, precision: 3);
            Assert.Equal(was.Y + 500, remade.Location.Y, precision: 3);
            Assert.Equal(4, Groups.Members(document, copy).Count);

            // The making over is one step to undo.
            history.Undo();
            Assert.Equal(was, Groups.Members(document, copy).OfType<Component>().Single().Location);

            // Kept in the file, frame and all.
            var again = ProjectFile.FromJson(ProjectFile.ToJson(document));
            var reread = again.Elements.OfType<Group>().Single(g => g.Id == copy.Id);
            Assert.Equal((copy.Name, copy.Origin, copy.Rotation, copy.Mirrored), (reread.Name, reread.Origin, reread.Rotation, reread.Mirrored));
            Assert.Equal(copy.MemberIds, reread.MemberIds);

            // Not scaled; and ungrouped, what was in it stays on its own.
            plan.Select(group);
            Assert.False(plan.ScaleAbout(new Point2D(0, 0), 2));
            Assert.True(plan.UngroupSelection());
            Assert.DoesNotContain(group, document.Elements);
            Assert.Contains(basin, document.Elements);
            plan.Select(basin);
            Assert.Equal(new Element[] { basin }, plan.SelectedElements);
        });
    }

    [Fact]
    public void AMirroredGroupGetsAnEditMirrored()
    {
        OnUi(() =>
        {
            var (document, plan, _) = Empty();
            var basin = Place(document, "Basin - Pedestal", new Point2D(1000, 0));
            var table = Place(document, "Table - Dining 1200", new Point2D(0, 0));
            plan.SelectMany(new Element[] { basin, table });
            var group = plan.GroupSelection()!;

            // Mirrored as a copy about x = 5000: the copy's basin is to the left of its table.
            plan.Select(group);
            plan.MirrorKeepsOriginal = true;
            plan.PlaceMirrorPointAt(new Point2D(5000, -1000));
            plan.PlaceMirrorPointAt(new Point2D(5000, 1000));
            var copy = document.Elements.OfType<Group>().Single(g => !ReferenceEquals(g, group));
            Assert.True(copy.Mirrored);

            // The basin moved further out, 400 more to the right: the mirrored one goes 400 more to the left.
            plan.Select(group);
            plan.BeginGroupEdit();
            basin.Location += new Vector2D(400, 0);
            plan.FinishGroupEdit();
            var copyTable = Groups.Members(document, copy).OfType<Component>().Single(c => c.TypeId == table.TypeId);
            var copyBasin = Groups.Members(document, copy).OfType<Component>().Single(c => c.TypeId == basin.TypeId);
            Assert.Equal(copyTable.Location.X - 1400, copyBasin.Location.X, precision: 3);
        });
    }
}
