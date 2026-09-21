using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Tests;

/// <summary>
/// A sliding panel has to park on unbroken wall. These cover how much wall is actually
/// available beside an opening - the thing a plan is read to check, because that stretch
/// can then carry nothing else.
/// </summary>
public class SlidingDoorTests
{
    private static (BimDocument Document, Wall Wall, Guid LevelId) Project(double length = 10000)
    {
        var document = BimDocument.CreateDefault();
        var levelId = document.Levels.First().Id;
        var type = document.TypesOf<WallType>().First();

        var wall = new Wall
        {
            Start = new Point2D(0, 0),
            End = new Point2D(length, 0),
            TypeId = type.Id,
            LevelId = levelId
        };

        document.Add(wall);
        return (document, wall, levelId);
    }

    private static Opening AddOpening(
        BimDocument document, Wall wall, OpeningType type, double distance, bool isDoor = true)
    {
        Opening opening = isDoor ? new Door() : new Window();
        opening.HostWallId = wall.Id;
        opening.DistanceAlongWall = distance;
        opening.TypeId = type.Id;
        opening.LevelId = wall.LevelId;
        document.Add(opening);
        return opening;
    }

    [Fact]
    public void AnOpeningInAnOtherwiseEmptyWallHasWallBothSides()
    {
        var (document, wall, _) = Project();
        var door = document.TypesOf<DoorType>().First(t => Math.Abs(t.Width - 900) < 1);
        var opening = AddOpening(document, wall, door, 5000);

        var (from, to) = opening.GetSpan(door);

        Assert.Equal(4550, WallOpenings.ClearRunBeside(document, wall, from, to, towardEnd: false), precision: 3);
        Assert.Equal(4550, WallOpenings.ClearRunBeside(document, wall, from, to, towardEnd: true), precision: 3);
    }

    /// <summary>The reported case: a panel cannot slide across a window.</summary>
    [Fact]
    public void AWindowBesideTheDoorCutsTheRunShort()
    {
        var (document, wall, _) = Project();
        var door = document.TypesOf<DoorType>().First(t => Math.Abs(t.Width - 900) < 1);
        var window = document.TypesOf<WindowType>().First(t => Math.Abs(t.Width - 1200) < 1);

        // Door centred at 3000, so it spans 2550 to 3450.
        var opening = AddOpening(document, wall, door, 3000);

        // Window centred at 4500, so it starts at 3900 - only 450 mm of wall between them.
        AddOpening(document, wall, window, 4500, isDoor: false);

        var (from, to) = opening.GetSpan(door);

        Assert.Equal(450, WallOpenings.ClearRunBeside(document, wall, from, to, towardEnd: true), precision: 3);

        // The other way is still clear all the way to the start of the wall.
        Assert.Equal(2550, WallOpenings.ClearRunBeside(document, wall, from, to, towardEnd: false), precision: 3);
    }

    [Fact]
    public void AnOpeningHardAgainstTheEndOfTheWallHasNoRunThatWay()
    {
        var (document, wall, _) = Project(2000);
        var door = document.TypesOf<DoorType>().First(t => Math.Abs(t.Width - 900) < 1);

        // Centred at 450, so it starts exactly at the wall's start.
        var opening = AddOpening(document, wall, door, 450);
        var (from, to) = opening.GetSpan(door);

        Assert.Equal(0, WallOpenings.ClearRunBeside(document, wall, from, to, towardEnd: false), precision: 3);
        Assert.Equal(1100, WallOpenings.ClearRunBeside(document, wall, from, to, towardEnd: true), precision: 3);
    }

    [Fact]
    public void TwoDoorsBackToBackShareTheWallBetweenThem()
    {
        var (document, wall, _) = Project();
        var door = document.TypesOf<DoorType>().First(t => Math.Abs(t.Width - 900) < 1);

        var first = AddOpening(document, wall, door, 2000);   // spans 1550 to 2450
        AddOpening(document, wall, door, 4000);               // spans 3550 to 4450

        var (from, to) = first.GetSpan(door);

        Assert.Equal(1100, WallOpenings.ClearRunBeside(document, wall, from, to, towardEnd: true), precision: 3);
    }

    [Fact]
    public void TheTemplateOffersBothASingleAndATwinSlider()
    {
        var document = BimDocument.CreateDefault();
        var sliders = document.TypesOf<DoorType>()
            .Where(type => type.Operation == DoorOperation.Sliding)
            .ToList();

        // A twin slider parks over its own fixed leaf, so it needs no wall beside it; a
        // single one does. They are drawn differently because they behave differently.
        Assert.Contains(sliders, type => type.LeafCount == 1);
        Assert.Contains(sliders, type => type.LeafCount >= 2);
    }
}
