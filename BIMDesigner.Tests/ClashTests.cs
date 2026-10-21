using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Coordination;
using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;

namespace BIMDesigner.Tests;

/// <summary>
/// Clash tests: solids running into each other by more than the tolerance, coming nearer than
/// they should, drawn twice - what only touches, or is meant to meet, left out - and each clash
/// kept from one run to the next with its status.
/// </summary>
public class ClashTests
{
    /// <summary>A 6 m outside wall along the X axis, 300 thick or so, and a desk.</summary>
    private static (BimDocument Document, Wall Wall, Component Desk) Room()
    {
        var document = BimDocument.CreateDefault();
        var wall = new Wall
        {
            Start = new Point2D(0, 0), End = new Point2D(6000, 0), LevelId = document.Levels[0].Id, UnconnectedHeight = 2700,
            TypeId = document.TypesOf<WallType>().First(type => type.Function == WallFunction.Exterior && type.Log is null).Id
        };
        document.Add(wall);
        var type = document.TypesOf<ComponentType>().Single(type => type.Name.StartsWith("Desk - 1500"));
        var desk = new Component { TypeId = type.Id, TypeKind = type.Kind, LevelId = wall.LevelId, Location = new Point2D(3000, 2000) };
        document.Add(desk);
        return (document, wall, desk);
    }

    private static ClashTest Test(ClashKind kind, BuiltInCategory a, BuiltInCategory b, double tolerance = 10)
    {
        var test = new ClashTest { Name = "test", Kind = kind, Tolerance = tolerance };
        test.SelectionA.Add(a);
        test.SelectionB.Add(b);
        return test;
    }

    private static (Point3D Min, Point3D Max) BoundsOf(BimDocument document, Element element) =>
        ModelMeshBuilder.Bounds(ModelMeshBuilder.Build(document).Where(mesh => mesh.ElementId == element.Id || mesh.OwnerId == element.Id))!.Value;

    /// <summary>Moves the desk so its back is this far into the wall's inner face - negative, short of it.</summary>
    private static void BackInto(BimDocument document, Wall wall, Component desk, double into)
    {
        var face = BoundsOf(document, wall).Min.Y;
        var back = BoundsOf(document, desk).Max.Y;
        desk.Location = new Point2D(desk.Location.X, desk.Location.Y + (face - back) + into);
    }

    [Fact]
    public void ADeskThroughAWallIsAHardClashAndOneAgainstItIsNot()
    {
        var (document, wall, desk) = Room();
        var test = Test(ClashKind.Hard, BuiltInCategory.Furniture, BuiltInCategory.Walls);

        // Back against the wall's face: touching, no clash.
        BackInto(document, wall, desk, 0);
        Assert.Empty(ClashDetective.Run(document, test, new DateTime(2026, 10, 21)));

        // 5 mm in: under the 10 mm tolerance.
        BackInto(document, wall, desk, 5);
        Assert.Empty(ClashDetective.Run(document, test, new DateTime(2026, 10, 21)));

        // 60 mm in: a clash of 60 mm, found where it is, said as it is.
        BackInto(document, wall, desk, 60);
        var clash = Assert.Single(ClashDetective.Run(document, test, new DateTime(2026, 10, 21)));
        Assert.Equal(-60, clash.Distance, precision: 0);
        Assert.Equal((desk.Id, wall.Id), (clash.ElementA, clash.ElementB));
        Assert.Contains("runs 60 mm into", clash.Description);
        Assert.InRange(clash.Point.X, 2000, 4000);
        Assert.Equal(document.Levels[0].Name, clash.Level);
    }

    [Fact]
    public void WallsJoinedAtACornerAndADoorInItsWallAreNoClash()
    {
        var (document, wall, _) = Room();
        document.Add(new Wall
        {
            Start = new Point2D(6000, 0), End = new Point2D(6000, 4000), LevelId = wall.LevelId, UnconnectedHeight = 2700, TypeId = wall.TypeId
        });
        document.Add(new Door { TypeId = document.TypesOf<DoorType>().First().Id, LevelId = wall.LevelId, HostWallId = wall.Id, DistanceAlongWall = 2000 });

        Assert.Empty(ClashDetective.Run(document, Test(ClashKind.Hard, BuiltInCategory.Walls, BuiltInCategory.Walls), DateTime.Now));
        Assert.Empty(ClashDetective.Run(document, Test(ClashKind.Hard, BuiltInCategory.Doors, BuiltInCategory.Walls), DateTime.Now));

        // Even not left out, the door sits in the hole cut for it: it touches its reveals, no more.
        var all = Test(ClashKind.Hard, BuiltInCategory.Doors, BuiltInCategory.Walls);
        all.IgnoreHosted = false;
        Assert.Empty(ClashDetective.Run(document, all, DateTime.Now));
    }

    [Fact]
    public void AClearanceClashIsTooNearAndADuplicateIsDrawnTwice()
    {
        var (document, wall, desk) = Room();
        BackInto(document, wall, desk, -20);
        var clearance = Assert.Single(ClashDetective.Run(document, Test(ClashKind.Clearance, BuiltInCategory.Furniture, BuiltInCategory.Walls, 50), DateTime.Now));
        Assert.Equal(20, clearance.Distance, precision: 0);
        Assert.Contains("within the 50 mm", clearance.Description);
        Assert.Empty(ClashDetective.Run(document, Test(ClashKind.Clearance, BuiltInCategory.Furniture, BuiltInCategory.Walls, 10), DateTime.Now));

        var twin = new Component { TypeId = desk.TypeId, TypeKind = desk.TypeKind, LevelId = desk.LevelId, Location = desk.Location };
        document.Add(twin);
        var duplicate = Assert.Single(ClashDetective.Run(document, Test(ClashKind.Duplicate, BuiltInCategory.Furniture, BuiltInCategory.Furniture, 1), DateTime.Now));
        Assert.Contains("drawn twice", duplicate.Description);
    }

    [Fact]
    public void EachClashKeepsItsStatusFromRunToRun()
    {
        var (document, wall, desk) = Room();
        var test = Test(ClashKind.Hard, BuiltInCategory.Furniture, BuiltInCategory.Walls);
        BackInto(document, wall, desk, 100);

        var first = Assert.Single(ClashDetective.Run(document, test, new DateTime(2026, 10, 1)));
        Assert.Equal((ClashStatus.New, "Clash1"), (first.Status, first.Name));

        // Found again: active, the same clash.
        var again = Assert.Single(ClashDetective.Run(document, test, new DateTime(2026, 10, 2)));
        Assert.Same(first, again);
        Assert.Equal(ClashStatus.Active, again.Status);

        // Approved stays approved - and is no longer open.
        again.Status = ClashStatus.Approved;
        Assert.Empty(ClashDetective.Run(document, test, new DateTime(2026, 10, 3)));
        Assert.Equal(ClashStatus.Approved, test.Results.Single().Status);

        // Moved out: resolved, kept on record; back in: new again.
        BackInto(document, wall, desk, -300);
        ClashDetective.Run(document, test, new DateTime(2026, 10, 4));
        Assert.Equal(ClashStatus.Resolved, test.Results.Single().Status);
        BackInto(document, wall, desk, 100);
        var back = Assert.Single(ClashDetective.Run(document, test, new DateTime(2026, 10, 5)));
        Assert.Equal((ClashStatus.New, "Clash1", new DateTime(2026, 10, 5)), (back.Status, back.Name, back.Found));
    }

    [Fact]
    public void AClashIsPlacedOnTheGrid()
    {
        var (document, wall, desk) = Room();
        document.Add(new Grid { Name = "A", Start = new Point2D(0, -2000), End = new Point2D(0, 8000) });
        document.Add(new Grid { Name = "B", Start = new Point2D(3000, -2000), End = new Point2D(3000, 8000) });
        document.Add(new Grid { Name = "1", Start = new Point2D(-2000, 0), End = new Point2D(8000, 0) });
        document.Add(new Grid { Name = "2", Start = new Point2D(-2000, 5000), End = new Point2D(8000, 5000) });
        BackInto(document, wall, desk, 100);

        var clash = Assert.Single(ClashDetective.Run(document, Test(ClashKind.Hard, BuiltInCategory.Furniture, BuiltInCategory.Walls), DateTime.Now));
        Assert.Equal("B-1", clash.GridLocation);
        Assert.Equal("A-2", ClashDetective.GridLocation(document, new Point2D(400, 4600)));
    }

    [Fact]
    public void ANewProjectHasItsTestsAndTheyAreSaved()
    {
        var (document, wall, desk) = Room();
        Assert.Contains(document.ClashTests, test => test.Name.Contains("Fittings and furniture through walls"));
        BackInto(document, wall, desk, 100);
        var test = document.ClashTests.Single(test => test.Name.Contains("Fittings and furniture through walls"));
        var clash = Assert.Single(ClashDetective.Run(document, test, new DateTime(2026, 10, 21, 9, 30, 0)));
        clash.Status = ClashStatus.Reviewed;
        clash.AssignedTo = "Structural engineer";
        clash.Priority = ClashPriority.High;
        clash.DueDate = new DateOnly(2026, 11, 1);
        clash.Comments.Add(new ClashComment(new DateTime(2026, 10, 21, 10, 0, 0), "AN", "Move the desk off the wall."));

        var path = Path.Combine(Path.GetTempPath(), $"bimtest-{Guid.NewGuid():N}{Infrastructure.Serialization.ProjectFile.Extension}");
        try
        {
            Infrastructure.Serialization.ProjectFile.Save(document, path);
            var again = Infrastructure.Serialization.ProjectFile.Load(path);
            var saved = again.ClashTests.Single(each => each.Id == test.Id);
            Assert.Equal((test.Kind, test.Tolerance, test.SelectionA.Count, test.SelectionB.Count), (saved.Kind, saved.Tolerance, saved.SelectionA.Count, saved.SelectionB.Count));
            var kept = Assert.Single(saved.Results);
            Assert.Equal((clash.Id, ClashStatus.Reviewed, "Structural engineer", ClashPriority.High, new DateOnly(2026, 11, 1)),
                (kept.Id, kept.Status, kept.AssignedTo, kept.Priority, kept.DueDate));
            Assert.Equal("Move the desk off the wall.", Assert.Single(kept.Comments).Text);
            Assert.Equal(clash.Distance, kept.Distance, precision: 6);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
