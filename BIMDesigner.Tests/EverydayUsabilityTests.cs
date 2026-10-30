using System.Windows;
using BIMDesigner.Core;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Mep;
using BIMDesigner.Core.Views;
using BIMDesigner.Infrastructure.Serialization;
using BIMDesigner.UI;
using BIMDesigner.UI.Controls;
using Window = BIMDesigner.Core.Architecture.Window;

namespace BIMDesigner.Tests;

/// <summary>
/// What is used every hour: a length typed while a line is drawn; the history gone back through
/// several steps at once; every instance of a type selected; the types and materials nothing uses
/// purged, and kept out as the project opens again; and everything wrong with the model in one list.
/// </summary>
[Collection("Wpf")]
public class EverydayUsabilityTests
{
    private static void OnUiThread(Action<BimDocument, PlanView> action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (Application.Current is null) new App().InitializeComponent();
                var document = BimDocument.CreateDefault();
                var plan = new PlanView { Document = document, ActiveLevelId = document.Levels[0].Id, History = new UndoStack() };
                plan.ActiveWallTypeId = document.TypesOf<WallType>().First(t => t.Name.StartsWith("Exterior")).Id;
                action(document, plan);
            }
            catch (Exception exception) { failure = exception; }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Exception("The plan failed on the UI thread.", failure);
    }

    private static void Near(Point2D expected, Point2D actual) =>
        Assert.True(expected.DistanceTo(actual) < 0.01, $"Expected {expected}, was {actual}.");

    [Fact]
    public void ALengthTypedWhileAWallIsDrawnPutsItsEndThatFarTheWayTheCursorPoints()
    {
        OnUiThread((document, plan) =>
        {
            plan.SetTool(PlanTool.Wall);
            plan.PlaceWallPointAt(new Point2D(0, 0));

            // Typed as it comes, a figure at a time; the cursor a little off square is taken as square.
            Assert.True(plan.BeginTypedLength("4"));
            plan.TypeIntoLength("200");
            Assert.Equal("4200", plan.TypedLength);
            Assert.True(plan.PlaceTypedLength(toward: new Point2D(3000, 80)));
            var first = Assert.Single(document.Walls);
            Near(new Point2D(0, 0), first.Start);
            Near(new Point2D(4200, 0), first.End);
            Assert.False(plan.TypingLength);

            // The next carries on from there: in metres, up the screen.
            Assert.True(plan.PlaceTypedLength("3 m", toward: new Point2D(4210, 2000)));
            Near(new Point2D(4200, 3000), document.Walls.Last().End);

            // Off square, it goes the way the cursor points.
            Assert.True(plan.PlaceTypedLength("1000", toward: new Point2D(4200 + 3000, 3000 + 1000)));
            var slanted = document.Walls.Last();
            Assert.Equal(1000, slanted.Length, 3);
            Assert.Equal(Math.Atan2(1000, 3000), Math.Atan2(slanted.End.Y - slanted.Start.Y, slanted.End.X - slanted.Start.X), 6);

            // What is not a length puts nothing down, and is there to correct.
            plan.BeginTypedLength("4x");
            Assert.False(plan.PlaceTypedLength());
            Assert.Equal(3, document.Walls.Count());
            Assert.True(plan.TypingLength);

            // With no wall begun there is nothing to type a length for.
            plan.CancelPendingOperation();
            Assert.False(plan.TypingLength);
            Assert.False(plan.BeginTypedLength("4"));
        });
    }

    [Fact]
    public void ALengthTypedInFeetAndInchesOrForABeamGoesTheSameWay()
    {
        OnUiThread((document, plan) =>
        {
            using var units = Units.Use(UnitSystem.Imperial);
            plan.SetTool(PlanTool.Wall);
            plan.PlaceWallPointAt(new Point2D(0, 0));
            Assert.True(plan.PlaceTypedLength("12'6\"", toward: new Point2D(-5000, 0)));
            Near(new Point2D(-3810, 0), document.Walls.Single().End);

            // A bare figure in a US project is in feet, as its drawings are.
            Assert.True(plan.PlaceTypedLength("10", toward: new Point2D(-3810, -100)));
            Near(new Point2D(-3810, -3048), document.Walls.Last().End);
            plan.CancelPendingOperation();

            plan.SetTool(PlanTool.Beam);
            Assert.True(plan.ClickBeam(new Point2D(0, 0)));
            Assert.True(plan.PlaceTypedLength("16'", toward: new Point2D(30, 9000)));
            var beam = Assert.Single(document.All<Beam>());
            Near(new Point2D(0, 4876.8), beam.End);
        });
    }

    [Fact]
    public void TheHistoryListsItsStepsLatestFirstAndGoesBackSeveralAtOnce()
    {
        var history = new UndoStack();
        var value = 0;
        foreach (var step in new[] { "Draw Wall", "Move", "Delete" })
        {
            var before = value;
            history.Execute(new DelegateCommand(step, () => value = before + 1, () => value = before));
        }

        Assert.Equal(new[] { "Delete", "Move", "Draw Wall" }, history.UndoNames);
        history.Undo(2);
        Assert.Equal(1, value);
        Assert.Equal(new[] { "Draw Wall" }, history.UndoNames);
        Assert.Equal(new[] { "Move", "Delete" }, history.RedoNames);

        // More than there are goes as far as there is.
        history.Redo(5);
        Assert.Equal(3, value);
        Assert.Empty(history.RedoNames);
        history.Undo(10);
        Assert.Equal(0, value);
        Assert.False(history.CanUndo);
    }

    [Fact]
    public void SelectAllInstancesTakesInEveryOneOfTheTypeHereOrInTheWholeProject()
    {
        OnUiThread((document, plan) =>
        {
            var (ground, first) = (document.Levels[0].Id, document.Levels[1].Id);
            var exterior = document.TypesOf<WallType>().First(t => t.Name.StartsWith("Exterior"));
            var other = document.TypesOf<WallType>().First(t => t.Id != exterior.Id);
            Wall Add(Guid level, WallType type, double y)
            {
                var wall = new Wall { Start = new Point2D(0, y), End = new Point2D(4000, y), TypeId = type.Id, LevelId = level, UnconnectedHeight = 3000 };
                document.Add(wall);
                return wall;
            }

            var picked = Add(ground, exterior, 0);
            Add(ground, exterior, 2000);
            Add(ground, other, 4000);
            Add(first, exterior, 0);
            document.Add(new Room { LevelId = ground, Location = new Point2D(-5000, 0), Name = "Hall" });
            document.Add(new Room { LevelId = ground, Location = new Point2D(-9000, 0), Name = "Store" });
            document.Add(new Room { LevelId = first, Location = new Point2D(-9000, 0), Name = "Landing" });

            plan.Select(picked);
            Assert.Equal(2, plan.SelectAllInstances(wholeProject: false));
            Assert.All(plan.SelectedElements, element => Assert.Equal(exterior.Id, element.TypeId));
            plan.Select(picked);
            Assert.Equal(3, plan.SelectAllInstances(wholeProject: true));

            // A room has no type: every room is of its kind.
            plan.Select(document.All<Room>().First());
            Assert.Equal(2, plan.SelectAllInstances(wholeProject: false));
            Assert.Equal(3, plan.SelectAllInstances(wholeProject: true));

            // It is on the menu of whatever is right-clicked.
            Assert.Contains(plan.MenuFor(picked), entry => entry.Header == "Select All Instances: In Entire Project");
        });
    }

    /// <summary>A house of four walls of one type, a door and a window: most of the template unused.</summary>
    private static BimDocument House()
    {
        var document = BimDocument.CreateDefault();
        var level = document.Levels[0].Id;
        var exterior = document.TypesOf<WallType>().First(type => type.Function == WallFunction.Exterior);
        var corners = new[] { new Point2D(0, 0), new Point2D(8000, 0), new Point2D(8000, 6000), new Point2D(0, 6000) };
        var walls = new List<Wall>();
        for (var i = 0; i < 4; i++)
        {
            var wall = new Wall { Start = corners[i], End = corners[(i + 1) % 4], TypeId = exterior.Id, LevelId = level, UnconnectedHeight = 3000 };
            walls.Add(wall);
            document.Add(wall);
        }

        document.Add(new Door { TypeId = document.TypesOf<DoorType>().First(type => type.Function == DoorFunction.Exterior).Id, LevelId = level, HostWallId = walls[0].Id, DistanceAlongWall = 2000, Mark = "D1" });
        document.Add(new Window { TypeId = document.TypesOf<WindowType>().First().Id, LevelId = level, HostWallId = walls[2].Id, DistanceAlongWall = 3000, Mark = "W1" });
        return document;
    }

    [Fact]
    public void PurgeTakesOutWhatNothingUsesAndLeavesNothingKeptPointingAtWhatIsGone()
    {
        var document = House();
        var used = document.Elements.Select(element => element.TypeId).Where(id => id != Guid.Empty).ToHashSet();
        var unused = Purging.Find(document);
        Assert.NotEmpty(unused.Types);
        Assert.NotEmpty(unused.Materials);
        Assert.DoesNotContain(unused.Types, type => used.Contains(type.Id));

        // The materials the walls are built of, and the fire stopping the program builds with, stay.
        var walling = document.FindType<WallType>(document.Walls.First().TypeId)!.Structure.Layers.Select(layer => layer.MaterialId).ToHashSet();
        Assert.DoesNotContain(unused.Materials, material => walling.Contains(material.Id) || material.Name == Core.Views.ModelMeshBuilder.FireStopMaterial);

        var typesBefore = document.ElementTypes.ToList();
        var materialsBefore = document.Materials.ToList();
        var history = new UndoStack();
        var purge = Purging.Purge(document, unused.Types, unused.Materials);
        history.Execute(purge);
        Assert.Equal(unused.Count, purge.Types.Count + purge.Materials.Count);

        // A type of every kind is left for its tool; nothing left refers to what has gone; and what is left is all used.
        foreach (var kind in typesBefore.GroupBy(type => type.GetType())) Assert.Contains(document.ElementTypes, type => type.GetType() == kind.Key);
        foreach (var wallType in document.TypesOf<WallType>())
            Assert.All(wallType.Structure.Layers, layer => Assert.NotNull(document.FindMaterial(layer.MaterialId)));
        foreach (var curtain in document.TypesOf<CurtainWallType>())
            foreach (var slot in new[] { curtain.InteriorVertical, curtain.InteriorHorizontal, curtain.BorderVerticalStart, curtain.BorderVerticalEnd, curtain.BorderHorizontalBottom, curtain.BorderHorizontalTop })
                if (slot is { } id && id != Guid.Empty) Assert.NotNull(document.FindType<MullionType>(id));
        foreach (var stacked in document.TypesOf<StackedWallType>())
            Assert.All(stacked.Tiers, tier => Assert.NotNull(document.FindType<WallType>(tier.WallTypeId)));
        Assert.Equal(0, Purging.Find(document).Count);
        Assert.NotEmpty(Core.Views.ModelMeshBuilder.Build(document));

        // Undone, every one is back where it was in the lists.
        history.Undo();
        Assert.Equal(typesBefore, document.ElementTypes.ToList());
        Assert.Equal(materialsBefore, document.Materials.ToList());
        Assert.Empty(document.PurgedLibrary);

        // A type the tool is set to draw with stays, used or not.
        var tool = unused.Types.First();
        Assert.DoesNotContain(Purging.Find(document, new HashSet<Guid> { tool.Id }).Types, type => type.Id == tool.Id);
    }

    [Fact]
    public void WhatWasPurgedStaysOutAsTheProjectOpensUntilTheLibraryIsBroughtBack()
    {
        var document = House();
        var unusedDoor = Purging.Find(document).Types.OfType<DoorType>().First(type => !type.CurtainPanel);
        new UndoStack().Execute(Purging.Purge(document, new[] { unusedDoor }, Array.Empty<Core.Materials.Material>()));
        Assert.Null(document.FindType<DoorType>(unusedDoor.Id));

        // Saved and opened again: the library does not put it back, as it does what a project has never had.
        var reopened = ProjectFile.FromJson(ProjectFile.ToJson(document));
        Assert.DoesNotContain(reopened.TypesOf<DoorType>(), type => type.Name == unusedDoor.Name);
        Assert.Contains(Purging.KeyOf(unusedDoor), reopened.PurgedLibrary);

        // Brought back on asking, as one step to undo.
        var history = new UndoStack();
        history.Execute(Purging.RestoreLibrary(reopened));
        Assert.Contains(reopened.TypesOf<DoorType>(), type => type.Name == unusedDoor.Name);
        Assert.Empty(reopened.PurgedLibrary);
        history.Undo();
        Assert.DoesNotContain(reopened.TypesOf<DoorType>(), type => type.Name == unusedDoor.Name);
        Assert.Contains(Purging.KeyOf(unusedDoor), reopened.PurgedLibrary);
    }

    [Fact]
    public void WarningsListWhatIsDrawnWrongAndWhatTheRegulationsAskThatIsNotMet()
    {
        var document = House();
        var level = document.Levels[0].Id;
        var wall = document.Walls.First();

        // Clean as it is, for the model itself.
        Assert.DoesNotContain(Warnings.Of(document), warning => warning.Kind == WarningKind.Model);

        // The same wall twice, one run over another, a room out in the open, two rooms in one, a mark twice, a door left with no wall.
        var twin = new Wall { Start = wall.Start, End = wall.End, TypeId = wall.TypeId, LevelId = level, UnconnectedHeight = 3000 };
        document.Add(twin);
        var door = document.All<Door>().Single();
        var twinDoor = new Door { TypeId = door.TypeId, LevelId = level, HostWallId = twin.Id, DistanceAlongWall = door.DistanceAlongWall };
        document.Add(twinDoor);
        var partition = document.TypesOf<WallType>().First(type => type.Function == WallFunction.Interior);
        var a = new Wall { Start = new Point2D(4000, 0), End = new Point2D(4000, 4000), TypeId = partition.Id, LevelId = level, UnconnectedHeight = 3000 };
        var b = new Wall { Start = new Point2D(4000, 3000), End = new Point2D(4000, 6000), TypeId = partition.Id, LevelId = level, UnconnectedHeight = 3000 };
        document.Add(a);
        document.Add(b);
        document.Add(new Room { LevelId = level, Location = new Point2D(-5000, -5000), Name = "Garden" });
        document.Add(new Room { LevelId = level, Location = new Point2D(1500, 1500), Name = "Living", Number = "1" });
        document.Add(new Room { LevelId = level, Location = new Point2D(2500, 4500), Name = "Dining", Number = "1" });
        var window = document.All<Window>().Single();
        window.Mark = "D1";
        var orphan = new Door { TypeId = document.TypesOf<DoorType>().First().Id, LevelId = level, HostWallId = Guid.NewGuid(), DistanceAlongWall = 1000 };
        document.Add(orphan);

        var model = Warnings.Of(document).Where(warning => warning.Kind == WarningKind.Model).ToList();
        string Said(Warning warning) => $"{warning.Heading}: {warning.Said}";
        Assert.Contains(model, warning => warning.Heading == "Identical instances in the same place" && warning.Elements.Contains(twin) && warning.Elements.Contains(wall));

        // A door copied with its wall is the same door twice, though in another wall.
        Assert.Contains(model, warning => warning.Heading == "Identical instances in the same place" && warning.Elements.Contains(door) && warning.Elements.Contains(twinDoor));
        Assert.Contains(model, warning => warning.Heading == "Walls overlap" && warning.Elements.Contains(a) && warning.Elements.Contains(b) && warning.Said.Contains("1.00 m"));
        Assert.Contains(model, warning => warning.Heading == "Rooms not enclosed" && warning.Said.StartsWith("Garden"));
        Assert.Contains(model, warning => warning.Heading == "Rooms in the same space" && warning.Elements.Count == 2);
        Assert.Contains(model, warning => warning.Heading == "Room numbers used twice");
        Assert.DoesNotContain(model, warning => warning.Heading == "Marks used twice");
        Assert.Contains(model, warning => warning.Heading == "Doors and windows with no wall" && warning.Elements.Contains(orphan));
        Assert.True(model.Count >= 6, string.Join(Environment.NewLine, model.Select(Said)));

        // Marks twice among one kind of element.

        orphan.Mark = "D1";
        Assert.Contains(Warnings.Of(document), warning => warning.Heading == "Marks used twice" && warning.Said.Contains("D1"));
    }

    [Fact]
    public void ARoomsShortfallsAreTheClausesOfItsPropertiesThatCiteWhatARuleAsks()
    {
        var document = House();
        var level = document.Levels[0].Id;
        var room = new Room { LevelId = level, Location = new Point2D(2000, 3000), Name = "Kitchen", Number = "2" };
        document.Add(room);
        var cooker = document.TypesOf<ComponentType>().First(type => type.Form == ComponentForm.Cooker);
        document.Add(new Component { TypeId = cooker.Id, TypeKind = cooker.Kind, LevelId = level, Location = new Point2D(2000, 5600) });

        // No hood over the hob: said, as the room's Kitchen row says it - not what it meets.
        var shortfalls = Warnings.RoomShortfalls(document, room).ToList();
        Assert.Contains(shortfalls, said => said.StartsWith("Kitchen - no hood over the hob") && said.Contains("asks"));
        Assert.All(shortfalls, said => Assert.Contains(" asks", said));
        Assert.Contains(Warnings.Of(document), warning => warning.Kind == WarningKind.Regulations && warning.Elements.Contains(room) && warning.Said.StartsWith("2 Kitchen: Kitchen - no hood"));

        // A hood put over it: that one is met, and no longer said.
        var hood = document.TypesOf<ComponentType>().First(type => type.Form == ComponentForm.CookerHood);
        document.Add(new Component { TypeId = hood.Id, TypeKind = hood.Kind, LevelId = level, Location = new Point2D(2000, 5700) });
        Assert.DoesNotContain(Warnings.RoomShortfalls(document, room), said => said.Contains("no hood"));

        // The services' own, as the system browser lists them.
        Assert.Equal(MepSystems.Problems(document).Count, Warnings.Of(document).Count(warning => warning.Kind == WarningKind.Services));
    }
}
