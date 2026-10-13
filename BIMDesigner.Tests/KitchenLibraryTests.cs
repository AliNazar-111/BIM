using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Schedules;
using BIMDesigner.Core.Views;
using BIMDesigner.Infrastructure.Interoperability;
using BIMDesigner.Infrastructure.Serialization;
using Xbim.Ifc4.ElectricalDomain;
using Xbim.Ifc4.Interfaces;
using Xbim.Ifc4.Kernel;
using Xbim.Ifc4.PlumbingFireProtectionDomain;
using Xbim.Ifc4.PropertyResource;
using Part = BIMDesigner.Core.Views.ComponentModel.Part;
using PartFinish = BIMDesigner.Core.Views.ComponentModel.PartFinish;

namespace BIMDesigner.Tests;

/// <summary>
/// The kitchen in the component library: a fridge-freezer, an oven under a hob, a dishwasher
/// and a washing machine under the worktop, and a sink set into its unit's worktop - each
/// shaped as it is, standing in a run with the units, lettered on the plan, counted in the
/// appliance schedule and exported to IFC as what it is.
/// </summary>
public class KitchenLibraryTests
{
    private static ComponentType Type(BimDocument document, string name) =>
        document.TypesOf<ComponentType>().Single(type => type.Name == name);

    /// <summary>The parts of a form in one finish.</summary>
    private static List<Part> In(ComponentType type, PartFinish finish) =>
        ComponentModel.Parts(type).Where(part => part.Finish == finish).ToList();

    [Fact]
    public void TheKitchenIsInTheLibraryAtTheSizesAppliancesComeIn()
    {
        var document = BimDocument.CreateDefault();

        var fridge = Type(document, "Fridge Freezer - 600");
        Assert.Equal((BuiltInCategory.SpecialtyEquipment, ComponentForm.Fridge, 600.0, 1850.0), (fridge.Kind, fridge.Form, fridge.Width, fridge.Height));

        // Under the 900 worktop, 600 wide to take the place of a unit.
        foreach (var name in new[] { "Oven and Hob - 600", "Dishwasher - 600", "Washing Machine - 600", "Fridge - Under Counter 600" })
        {
            var type = Type(document, name);
            Assert.Equal(BuiltInCategory.SpecialtyEquipment, type.Kind);
            Assert.Equal((600.0, 900.0), (type.Width, type.Height));
        }

        var sink = Type(document, "Sink Unit - 1000");
        Assert.Equal((BuiltInCategory.PlumbingFixtures, ComponentForm.Sink), (sink.Kind, sink.Form));

        // All of them back onto a wall when put down near one.
        Assert.All(document.TypesOf<ComponentType>().Where(type => type.Kind == BuiltInCategory.SpecialtyEquipment || type.Form == ComponentForm.Sink),
            type => Assert.True(type.AgainstWall, type.Name));
    }

    [Fact]
    public void AProjectFromBeforeTheKitchenGainsIt()
    {
        var older = BimDocument.CreateDefault();
        foreach (var type in older.TypesOf<ComponentType>().Where(type => type.Form is ComponentForm.Fridge or ComponentForm.Sink).ToList())
            older.RemoveType(type);

        var loaded = ProjectFile.FromJson(ProjectFile.ToJson(older));
        Assert.Contains(loaded.TypesOf<ComponentType>(), type => type.Name == "Fridge Freezer - 600");
        Assert.Contains(loaded.TypesOf<ComponentType>(), type => type.Name == "Sink Unit - 1000" && type.Form == ComponentForm.Sink);
    }

    [Fact]
    public void ASinkIsSetIntoTheWorktopWithItsBowlSunkIntoTheUnit()
    {
        var document = BimDocument.CreateDefault();
        var sink = Type(document, "Sink Unit - 1000");
        var (rim, bowls, drainer, _) = ComponentModel.Sink(sink);

        // One bowl and a drainer beside it, inside the rim.
        var bowl = Assert.Single(bowls);
        Assert.NotNull(drainer);
        Assert.True(bowl.From > rim.From && drainer!.Value.To < rim.To);

        // Nothing of the worktop or the unit is where the bowl is, above its floor: it is a hole.
        var floor = In(sink, PartFinish.Metal).Where(part => part.From <= bowl.From + 1 && part.To >= bowl.To - 1 && part.Top < sink.Height).Max(part => part.Top);
        Assert.Equal(sink.Height - 200, floor, 6);
        Assert.DoesNotContain(In(sink, PartFinish.Body), part =>
            part.Top > floor + 1 && part.From < bowl.Across && part.To > bowl.Across && part.Back < bowl.Forward && part.Front > bowl.Forward);

        // The rim lies on the worktop.
        Assert.Contains(In(sink, PartFinish.Metal), part => part.Bottom == sink.Height);

        // A 600 unit has a bowl and no drainer; a 1200 one a bowl and a half and a drainer.
        Assert.Null(ComponentModel.Sink(Type(document, "Sink Unit - 600")).Drainer);
        var wide = sink.Duplicate("Sink Unit - 1200");
        wide.Width = 1200;
        Assert.Equal(2, ComponentModel.Sink(wide).Bowls.Count);
    }

    [Fact]
    public void AHobHasFourBurnersAndARangeSixOverTwoOvens()
    {
        var document = BimDocument.CreateDefault();
        var cooker = Type(document, "Oven and Hob - 600");
        var range = Type(document, "Range Cooker - 1000");

        Assert.Equal(4, ComponentModel.Hob(cooker).Burners.Count);
        Assert.Equal(6, ComponentModel.Hob(range).Burners.Count);

        // The oven doors' windows: one, and two side by side.
        bool Window(Part part) => part.Shape == ComponentModel.PartShape.Box && part.Top < cooker.Height - 100 && part.Top - part.Bottom > 200;
        Assert.Single(In(cooker, PartFinish.Dark), Window);
        Assert.Equal(2, In(range, PartFinish.Dark).Count(Window));

        // In plan: its hob and a ring for each burner.
        var outlines = ComponentModel.PlanOutlines(cooker, new ComponentModel.Frame(new Point2D(0, 0), Vector2D.UnitX, 0));
        Assert.Equal(1 + 1 + 4, outlines.Count);
    }

    [Fact]
    public void ADishwasherAWashingMachineAndAnUnderCounterFridgeStandUnderTheWorktop()
    {
        var document = BimDocument.CreateDefault();
        foreach (var name in new[] { "Dishwasher - 600", "Washing Machine - 600", "Fridge - Under Counter 600" })
        {
            var type = Type(document, name);
            var worktop = Assert.Single(In(type, PartFinish.Body));
            Assert.Equal((type.Height - 40, type.Height), (worktop.Bottom, worktop.Top));
            Assert.Equal((-type.Width / 2, type.Width / 2), (worktop.From, worktop.To));

            // The machine under it, 850 or less, as UK under-counter ones are.
            Assert.True(In(type, PartFinish.White).Max(part => part.Top) <= 850, name);
            Assert.True(ComponentPlacing.HasWorktop(type), name);
        }

        Assert.False(ComponentPlacing.HasWorktop(Type(document, "Fridge Freezer - 600")));
    }

    [Fact]
    public void AFridgeFreezerHasTheFridgeOverTheFreezerAndAnAmericanOneSideBySide()
    {
        var document = BimDocument.CreateDefault();
        var doors = (ComponentType type) => ComponentModel.Parts(type)
            .Where(part => part.Finish == PartFinish.Body && part.Front == type.Depth / 2 - 30)
            .ToList();

        var upright = doors(Type(document, "Fridge Freezer - 600"));
        Assert.Equal(2, upright.Count);
        Assert.True(upright.All(door => door.To - door.From == 600), "One above the other, each the full width.");

        var american = doors(Type(document, "Fridge Freezer - American 910"));
        Assert.Equal(2, american.Count);
        Assert.True(american.All(door => door.Top == 1790), "Side by side, each the full height.");

        Assert.Equal("FF", ComponentModel.PlanLabel(Type(document, "Fridge Freezer - 600")));
        Assert.Equal("F", ComponentModel.PlanLabel(Type(document, "Fridge - Under Counter 600")));
        Assert.Equal("DW", ComponentModel.PlanLabel(Type(document, "Dishwasher - 600")));
        Assert.Equal("WM", ComponentModel.PlanLabel(Type(document, "Washing Machine - 600")));
    }

    [Fact]
    public void TheWashingMachinesDoorIsARoundDiscFacingTheWayItFaces()
    {
        var document = BimDocument.CreateDefault();
        var type = Type(document, "Washing Machine - 600");

        // Turned to face along x: its door lies across y and up, and is thin along x.
        var machine = new Component { TypeId = type.Id, TypeKind = type.Kind, LevelId = document.Levels[0].Id, Location = new Point2D(0, 0), Rotation = 90 };
        document.Add(machine);
        Assert.Equal(1, machine.Facing.X, 6);

        var meshes = ModelMeshBuilder.BuildComponent(document, machine);
        var steel = Assert.Single(meshes, mesh => mesh.Description == "Stainless Steel");
        var (min, max) = steel.Bounds()!.Value;
        Assert.InRange(max.X - min.X, 20, 30);
        Assert.InRange(max.Y - min.Y, 400, 500);
        Assert.InRange(max.X, type.Depth / 2 - 16, type.Depth / 2 - 14);

        // White enamel, black and stainless steel, each in its own colour, and the worktop in the type's.
        Assert.Equal(new[] { type.Name, "Enamel, White", "Black", "Stainless Steel" }.Order(), meshes.Select(mesh => mesh.Description).Order());
        Assert.Equal(type.Colour, meshes.Single(mesh => mesh.Description == type.Name).Colour);
    }

    [Fact]
    public void AppliancesAndASinkUnitCloseUpIntoTheRunAndAWallUnitHangsOverTheSink()
    {
        var (document, _, _) = DormerToolTests.House(10000, 8000, 40);
        var level = document.Levels[0].Id;

        Component Put(string name, double x)
        {
            var type = Type(document, name);
            var piece = new Component { TypeId = type.Id, TypeKind = type.Kind, LevelId = level, Location = new Point2D(x, 500) };
            ComponentPlacing.FitToWalls(document, piece);
            document.Add(piece);
            return piece;
        }

        var unit = Put("Base Unit - 600", 2000);
        var dishwasher = Put("Dishwasher - 600", 2700);
        var sink = Put("Sink Unit - 1000", 3600);
        var fridge = Put("Fridge Freezer - 600", 1350);

        Assert.Equal(600, dishwasher.Location.X - unit.Location.X, 6);
        Assert.Equal(800, sink.Location.X - dishwasher.Location.X, 6);
        Assert.Equal(600, unit.Location.X - fridge.Location.X, 6);
        Assert.True(new[] { dishwasher, sink, fridge }.All(piece => Math.Abs(piece.Facing.Y - 1) < 1e-6), "Each faces the room.");

        // A wall unit over the sink hangs 500 over its worktop.
        var wall = document.Walls.First(w => Math.Abs(w.Start.Y) < 1 && Math.Abs(w.End.Y) < 1);
        var wallUnit = new Component { TypeId = Type(document, "Wall Unit - 600").Id, TypeKind = BuiltInCategory.Casework, LevelId = level };
        Assert.True(wallUnit.HostOn(document, wall, new Point2D(sink.Location.X, 100)));
        var wallUnitType = Type(document, "Wall Unit - 600");
        wallUnit.Elevation = ComponentPlacing.OverWorktop(document, wallUnit, wallUnitType)!.Value;
        Assert.Equal(900 + ComponentPlacing.AboveWorktop, ComponentHosting.BaseElevation(document, wallUnit, wallUnitType), 6);
    }

    [Fact]
    public void AppliancesAreScheduledRoomByRoomAndExportedAsWhatTheyAre()
    {
        var (document, _, _) = DormerToolTests.House(10000, 8000, 40);
        var level = document.Levels[0].Id;
        document.Add(new Room { LevelId = level, Location = new Point2D(5000, 4000), Name = "Kitchen", Number = "1" });

        var x = 1000.0;
        foreach (var name in new[] { "Fridge Freezer - 600", "Oven and Hob - 600", "Dishwasher - 600", "Washing Machine - 600", "Sink Unit - 1000", "Dishwasher - 600" })
        {
            var type = Type(document, name);
            document.Add(new Component { TypeId = type.Id, TypeKind = type.Kind, LevelId = level, Location = new Point2D(x, 2000) });
            x += 1100;
        }

        var schedule = Schedule.Run(document, ScheduleDefinition.Defaults().Single(definition => definition.Name == "Appliance Schedule"));
        var dishwashers = Assert.Single(schedule.Rows, row => row.Text("Type Name") == "Dishwasher - 600");
        Assert.Equal("1 Kitchen", dishwashers.Text("Room"));
        Assert.Equal(2, Schedule.Numeric(dishwashers[ScheduleDefinition.CountField]));
        Assert.Equal(5, schedule.Totals[ScheduleDefinition.CountField]);

        using var model = IfcExport.Build(document);
        var appliances = model.Instances.OfType<IfcElectricAppliance>().Select(item => item.PredefinedType).ToList();
        Assert.Contains(IfcElectricApplianceTypeEnum.FRIDGE_FREEZER, appliances);
        Assert.Contains(IfcElectricApplianceTypeEnum.ELECTRICCOOKER, appliances);
        Assert.Contains(IfcElectricApplianceTypeEnum.WASHINGMACHINE, appliances);
        Assert.Equal(2, appliances.Count(item => item == IfcElectricApplianceTypeEnum.DISHWASHER));
        Assert.Single(model.Instances.OfType<IfcSanitaryTerminal>(), item => item.PredefinedType == IfcSanitaryTerminalTypeEnum.SINK);

        var fridge = model.Instances.OfType<IfcElectricAppliance>().First(item => item.PredefinedType == IfcElectricApplianceTypeEnum.FRIDGE_FREEZER);
        Assert.Contains(model.Instances.OfType<IfcRelDefinesByProperties>(), relation =>
            relation.RelatedObjects.Contains(fridge) && relation.RelatingPropertyDefinition is IfcPropertySet { Name: var set } && set == "Pset_ElectricApplianceTypeCommon");
    }

    [Fact]
    public void ASectionThroughTheSinkCutsItsBowl()
    {
        var document = BimDocument.CreateDefault();
        var type = Type(document, "Sink Unit - 1000");
        var sink = new Component { TypeId = type.Id, TypeKind = type.Kind, LevelId = document.Levels[0].Id, Location = new Point2D(0, 0) };
        document.Add(sink);

        var bowl = ComponentModel.Sink(type).Bowls[0];
        var marker = new SectionMarker { Start = new Point2D(bowl.Across, -1000), End = new Point2D(bowl.Across, 1000), LevelId = sink.LevelId };
        var drawing = SectionProjection.Build(document, marker);

        var steel = drawing.Pieces.Where(piece => piece.ElementId == sink.Id && piece.Description == "Stainless Steel").ToList();
        Assert.NotEmpty(steel);
        Assert.Contains(steel, piece => Math.Abs(piece.Bounds.Bottom - (type.Height - 203)) < 2);
    }
}
