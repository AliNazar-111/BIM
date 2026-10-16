using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;
using PartFinish = BIMDesigner.Core.Views.ComponentModel.PartFinish;

namespace BIMDesigner.Tests;

/// <summary>
/// The kitchen laid out as planners and the codes lay one out: wall units with doors, tall units,
/// a hood hung over the hob; the hob kept from under an opening window, with worktop beside it
/// and nothing that can burn low over it; the US's landing areas; and the room's work triangle,
/// its worktop for a wheelchair user and its extract.
/// </summary>
public class KitchenPlanningTests
{
    private static ComponentType Type(BimDocument document, string name) =>
        document.TypesOf<ComponentType>().Single(type => type.Name == name);

    /// <summary>A kitchen 4 m by 3 m inside, walled all round, from (0, 0); its back wall along y = 3000.</summary>
    private static (BimDocument Document, Room Room, Wall Back) Kitchen()
    {
        var document = BimDocument.CreateDefault();
        var level = document.Levels[0].Id;
        var wallType = document.TypesOf<WallType>().First();
        var half = wallType.Structure.TotalWidth / 2;
        var corners = new[] { new Point2D(-half, -half), new Point2D(4000 + half, -half), new Point2D(4000 + half, 3000 + half), new Point2D(-half, 3000 + half) };
        var walls = new List<Wall>();
        for (var i = 0; i < 4; i++)
        {
            var wall = new Wall { Start = corners[i], End = corners[(i + 1) % 4], TypeId = wallType.Id, LevelId = level, UnconnectedHeight = 2400 };
            document.Add(wall);
            walls.Add(wall);
        }

        var room = new Room { LevelId = level, Location = new Point2D(2000, 1500), Name = "Kitchen", Number = "1" };
        document.Add(room);
        return (document, room, walls[2]);
    }

    /// <summary>A piece set against the back wall with its middle at <paramref name="x"/>.</summary>
    private static Component Run(BimDocument document, string name, double x)
    {
        var type = Type(document, name);
        var piece = new Component { TypeId = type.Id, TypeKind = type.Kind, LevelId = document.Levels[0].Id, Location = new Point2D(x, 2700) };
        ComponentPlacing.FitToWalls(document, piece);
        document.Add(piece);
        return piece;
    }

    /// <summary>A piece hung on the back wall over <paramref name="x"/> at an elevation.</summary>
    private static Component Hang(BimDocument document, Wall back, string name, double x, double elevation)
    {
        var type = Type(document, name);
        var piece = new Component { TypeId = type.Id, TypeKind = type.Kind, LevelId = document.Levels[0].Id };
        Assert.True(piece.HostOn(document, back, new Point2D(x, 2950)));
        piece.Elevation = elevation;
        document.Add(piece);
        return piece;
    }

    [Fact]
    public void WallUnitsHaveDoorsAndTheLibraryHasTallUnitsAHoodADryerAndAnIsland()
    {
        var document = BimDocument.CreateDefault();

        var wallUnit = Type(document, "Wall Unit - 600");
        Assert.Equal((ComponentForm.WallUnit, 720.0, 350.0), (wallUnit.Form, wallUnit.Height, wallUnit.Depth));
        Assert.Equal(2, ComponentModel.Parts(Type(document, "Wall Unit - 1000")).Count(part => part.Back == 350 / 2.0 - 40 && part.Front == 350 / 2.0 - 22));

        // Its handles along the bottom of its doors, where a hand reaches up to them.
        Assert.All(ComponentModel.Parts(wallUnit).Where(part => part.Finish == PartFinish.Metal), part => Assert.True(part.Top <= 60));

        Assert.Equal(2120, Type(document, "Tall Unit - Larder 600").Height);
        var (ovenBottom, ovenTop, _) = ComponentModel.OvenHousingOf(Type(document, "Tall Unit - Oven Housing 600"));
        Assert.Equal((850.0, 1450.0), (ovenBottom, ovenTop));
        Assert.False(Type(document, "Island - 2400 x 1000").AgainstWall);
        Assert.Equal("TD", ComponentModel.PlanLabel(Type(document, "Tumble Dryer - 600")));

        var hood = Type(document, "Cooker Hood - Chimney 600");
        Assert.Equal((ComponentPlacement.WorkPlaneBased, 1600.0), (hood.Placement, hood.DefaultElevation));

        // A project whose wall unit was open shelves has it given its doors.
        var older = BimDocument.CreateDefault();
        Type(older, "Wall Unit - 600").Form = ComponentForm.Shelving;
        older.EnsureDefaultTypes();
        Assert.Equal(ComponentForm.WallUnit, Type(older, "Wall Unit - 600").Form);
        Assert.Equal(ComponentForm.Shelving, Type(older, "Bookcase - 900 x 1800").Form);
    }

    [Fact]
    public void AHobWantsWorktopBesideItAndNothingLowOverItButAHoodAt700()
    {
        var (document, _, back) = Kitchen();
        var standard = ClearanceStandard.ApprovedDocumentM;
        Run(document, "Base Unit - 600", 1000);
        var cooker = Run(document, "Oven and Hob - 600", 1600);
        Run(document, "Base Unit - 600", 2200);
        Assert.Empty(Kitchens.Problems(document, cooker, standard));
        Assert.Equal(600, Kitchens.Worktop(document, cooker, 1), 6);

        // A wall unit over it at the height over a worktop is too low over a hob.
        var unit = Hang(document, back, "Wall Unit - 600", 1600, 1400);
        var low = Assert.Single(Kitchens.Problems(document, cooker, standard));
        Assert.Contains("Wall Unit - 600 over it is 500 above the hob", low.Said);
        Assert.Contains("650", low.Rule);
        document.Remove(unit);

        // A hood put over it hangs 700 over it - the middle of Approved Document F's 650 to 750.
        var hoodType = Type(document, "Cooker Hood - Chimney 600");
        var hood = Hang(document, back, "Cooker Hood - Chimney 600", 1600, 0);
        hood.Elevation = ComponentPlacing.OverHob(document, hood, hoodType)!.Value;
        Assert.Equal(900 + 700, ComponentHosting.BaseElevation(document, hood, hoodType), 6);
        Assert.Empty(Kitchens.Problems(document, cooker, standard));

        // Too low, it is said so.
        hood.Elevation = 900 + 550;
        Assert.Contains(Kitchens.Problems(document, cooker, standard), problem => problem.Said == "its hood is 550 over it.");
    }

    [Fact]
    public void AHobBesideATallFridgeOrUnderAWindowThatOpensIsCalledOut()
    {
        var (document, _, back) = Kitchen();
        var standard = ClearanceStandard.ApprovedDocumentM;
        Run(document, "Fridge Freezer - 600", 1000);
        var cooker = Run(document, "Oven and Hob - 600", 1600);
        Run(document, "Base Unit - 600", 2200);

        var tooShort = Assert.Single(Kitchens.Problems(document, cooker, standard));
        Assert.Equal("the worktop beside it is 0 on its left and 600 on its right.", tooShort.Said);

        // Under a casement in the wall behind it.
        var window = new Window
        {
            TypeId = document.TypesOf<WindowType>().First(type => type.Operation == WindowOperation.Casement).Id,
            LevelId = back.LevelId, HostWallId = back.Id, DistanceAlongWall = back.Start.DistanceTo(new Point2D(1600, back.Start.Y)), SillHeight = 1050, Mark = "W1"
        };
        document.Add(window);
        Assert.Contains(Kitchens.Problems(document, cooker, standard), problem => problem.Said == "it stands under window W1, which opens.");

        // A fixed light does not open.
        window.TypeId = document.TypesOf<WindowType>().First(type => type.Operation == WindowOperation.Fixed).Id;
        Assert.DoesNotContain(Kitchens.Problems(document, cooker, standard), problem => problem.Said.Contains("window"));

        // Among the clearances, as one more warning.
        Assert.Contains(Clearances.Blocked(document, back.LevelId, standard), item => item.Message.StartsWith("Oven and Hob - 600: the worktop beside it"));
    }

    [Fact]
    public void InTheUsTheSinkFridgeAndDishwasherWantTheirLandingAreas()
    {
        var (document, _, _) = Kitchen();
        var standard = ClearanceStandard.Irc;
        var sink = Run(document, "Sink Unit - 1000", 900);
        var dishwasher = Run(document, "Dishwasher - 600", 3600);
        var fridge = Run(document, "Fridge Freezer - 600", 2000);

        // 24 in one side and 18 the other of the sink: nothing either side yet.
        Assert.Contains(Kitchens.Problems(document, sink, standard), problem => problem.Rule.Contains("Guideline 11"));
        Assert.Contains(Kitchens.Problems(document, fridge, standard), problem => problem.Rule.Contains("Guideline 16"));
        Assert.Contains(Kitchens.Problems(document, dishwasher, standard), problem => problem.Said.StartsWith("it is "));

        // None of it asked under Approved Document M.
        Assert.Empty(Kitchens.Problems(document, dishwasher, ClearanceStandard.ApprovedDocumentM));

        // Laid out with worktop beside each, the dishwasher by the sink: a unit, the dishwasher,
        // the sink, a unit, the fridge and a unit.
        var (kitchen, _, _) = Kitchen();
        var pieces = new[] { ("Base Unit - 600", 300.0), ("Dishwasher - 600", 900), ("Sink Unit - 1000", 1700), ("Base Unit - 600", 2500), ("Fridge Freezer - 600", 3100), ("Base Unit - 600", 3700) }
            .Select(piece => Run(kitchen, piece.Item1, piece.Item2)).ToList();
        Assert.All(pieces, piece => Assert.Empty(Kitchens.Problems(kitchen, piece, standard)));
    }

    [Fact]
    public void TheKitchensWorkTriangleWorktopAndExtractAreInItsRoom()
    {
        var (document, room, _) = Kitchen();
        Run(document, "Fridge Freezer - 600", 400);
        Run(document, "Base Unit - 600", 1000);
        Run(document, "Sink Unit - 1000", 1800);
        Run(document, "Base Unit - 600", 2600);
        Run(document, "Oven and Hob - 600", 3200);

        var verdict = Kitchens.Verdict(document, room)!;
        Assert.StartsWith("Kitchen: work triangle 5.6 m (sink to hob 1.4, hob to fridge 2.8, fridge to sink 1.4)", verdict);
        Assert.Contains("Guideline 3", verdict);
        Assert.Contains("no hood over the hob - Approved Document F (Table 1.1) asks for 60 l/s", verdict);
        Assert.Contains(room.GetInstanceParameters(document), parameter => parameter.Name == "Kitchen");

        // For a wheelchair user, as long a worktop as Table 3.3 asks for the dwelling's bedspaces: a double and a single, three.
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.ApprovedDocumentM3;
        foreach (var name in new[] { "Bed - Double 1350 x 1900", "Bed - Single 900 x 1900" })
            document.Add(new Component { TypeId = Type(document, name).Id, LevelId = document.Levels[0].Id, Location = new Point2D(20000, 20000) });
        Assert.Equal(3, Kitchens.Bedspaces(document));
        Assert.Contains("worktop 3.40 m - Approved Document M (M4(3)) Table 3.3 asks for 4.73 m for 3 bedspaces", Kitchens.Verdict(document, room));
    }
}
