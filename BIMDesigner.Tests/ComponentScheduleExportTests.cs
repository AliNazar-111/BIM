using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Schedules;
using BIMDesigner.Infrastructure.Interoperability;
using Xbim.Ifc;
using Xbim.Ifc4.ElectricalDomain;
using Xbim.Ifc4.Interfaces;
using Xbim.Ifc4.Kernel;
using Xbim.Ifc4.MeasureResource;
using Xbim.Ifc4.PlumbingFireProtectionDomain;
using Xbim.Ifc4.PropertyResource;
using Xbim.Ifc4.SharedBldgElements;
using Xbim.Ifc4.SharedFacilitiesElements;

namespace BIMDesigner.Tests;

/// <summary>
/// Furniture and sanitary fittings counted room by room - how many of each type, what each costs,
/// what they come to - and exported to IFC as the furniture, sanitary terminals and lights they are.
/// </summary>
public class ComponentScheduleExportTests
{
    /// <summary>A house split into a kitchen and a bathroom; four chairs and a table in the kitchen, a WC, basin and bath in the bathroom.</summary>
    private static BimDocument Furnished()
    {
        var (document, _, _) = DormerToolTests.House(10000, 8000, 40);
        var level = document.Levels[0].Id;
        document.Add(new Wall { Start = new Point2D(6000, 0), End = new Point2D(6000, 8000), TypeId = document.TypesOf<WallType>().First().Id, LevelId = level, UnconnectedHeight = 3000 });
        document.Add(new Room { LevelId = level, Location = new Point2D(3000, 4000), Name = "Kitchen", Number = "1" });
        document.Add(new Room { LevelId = level, Location = new Point2D(8000, 4000), Name = "Bathroom", Number = "2" });

        void Put(string name, double x, double y)
        {
            var type = document.TypesOf<ComponentType>().First(t => t.Name.StartsWith(name));
            document.Add(new Component { TypeId = type.Id, TypeKind = type.Kind, LevelId = level, Location = new Point2D(x, y) });
        }

        foreach (var x in new[] { 2000.0, 2600, 3200, 3800 }) Put("Chair - Dining", x, 3000);
        Put("Table - Dining", 3000, 4000);
        Put("WC", 7000, 2000);
        Put("Basin - Pedestal", 8000, 2000);
        Put("Bath", 8500, 6000);
        Put("Wall Light", 4000, 6000);
        Put("Tree", 20000, 20000);
        return document;
    }

    [Fact]
    public void FurnitureIsCountedRoomByRoomWithWhatEachCostsAndWhatTheyComeTo()
    {
        var document = Furnished();
        var furniture = ScheduleDefinition.Defaults().Single(definition => definition.Name == "Furniture Schedule");
        Assert.False(furniture.Itemize);

        var result = Schedule.Run(document, furniture);
        var chairs = Assert.Single(result.Rows, row => row.Text("Type Name").StartsWith("Chair - Dining"));
        Assert.Equal("1 Kitchen", chairs.Text("Room"));
        Assert.Equal(4, Schedule.Numeric(chairs[ScheduleDefinition.CountField]));
        Assert.Equal(85, Schedule.Numeric(chairs["Cost"]));
        Assert.Equal(340, Schedule.Numeric(chairs[ScheduleDefinition.TotalCostField]));

        // The kitchen's subtotal: five pieces, 720; the unit cost is not added up.
        var kitchen = Assert.Single(result.Groups, group => group.Heading == "1 Kitchen");
        Assert.Equal(5, kitchen.Subtotals[ScheduleDefinition.CountField]);
        Assert.Equal(720, kitchen.Subtotals[ScheduleDefinition.TotalCostField]);
        Assert.False(kitchen.Subtotals.ContainsKey("Cost"));

        // A row standing for several cannot be typed into.
        Assert.All(result.Columns, column => Assert.True(column.IsReadOnly));
    }

    [Fact]
    public void SanitaryFittingsAreCountedInTheirRoom()
    {
        var result = Schedule.Run(Furnished(), ScheduleDefinition.Defaults().Single(definition => definition.Name == "Plumbing Fixture Schedule"));
        Assert.Equal(3, result.Rows.Count);
        Assert.All(result.Rows, row => Assert.Equal("2 Bathroom", row.Text("Room")));
        Assert.Equal(3, result.Totals[ScheduleDefinition.CountField]);
        Assert.Equal(260 + 190 + 340, result.Totals[ScheduleDefinition.TotalCostField]);
    }

    [Fact]
    public void ComponentsAreExportedAsTheFurnitureSanitaryTerminalsAndLightsTheyAre()
    {
        var document = Furnished();
        using var model = IfcExport.Build(document);

        var furniture = model.Instances.OfType<IfcFurniture>().ToList();
        Assert.Equal(4, furniture.Count(item => item.PredefinedType == IfcFurnitureTypeEnum.CHAIR));
        Assert.Single(furniture, item => item.PredefinedType == IfcFurnitureTypeEnum.TABLE);

        var sanitary = model.Instances.OfType<IfcSanitaryTerminal>().Select(item => item.PredefinedType).ToList();
        Assert.Contains(IfcSanitaryTerminalTypeEnum.TOILETPAN, sanitary);
        Assert.Contains(IfcSanitaryTerminalTypeEnum.WASHHANDBASIN, sanitary);
        Assert.Contains(IfcSanitaryTerminalTypeEnum.BATH, sanitary);
        Assert.Single(model.Instances.OfType<IfcLightFixture>());
        Assert.Contains(model.Instances.OfType<IfcBuildingElementProxy>(), proxy => proxy.Name == "Tree - Small");

        // Each with its body, its size and the room it is in.
        var chair = furniture.First(item => item.PredefinedType == IfcFurnitureTypeEnum.CHAIR);
        Assert.NotNull(chair.Representation);
        var set = model.Instances.OfType<IfcRelDefinesByProperties>()
            .Where(relation => relation.RelatedObjects.Contains(chair))
            .Select(relation => relation.RelatingPropertyDefinition)
            .OfType<IfcPropertySet>()
            .Single(candidate => candidate.Name == "Component");
        var room = set.HasProperties.OfType<IfcPropertySingleValue>().Single(property => property.Name == "Room");
        Assert.Equal("1 Kitchen", ((IfcLabel)room.NominalValue!).Value);
    }
}
