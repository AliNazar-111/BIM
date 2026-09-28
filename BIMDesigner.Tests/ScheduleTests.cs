using BIMDesigner.Core;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Schedules;
using BIMDesigner.Infrastructure.Serialization;

namespace BIMDesigner.Tests;

public class ScheduleTests
{
    private static (BimDocument Document, WallType Type, Guid LevelId) Project()
    {
        var document = BimDocument.CreateDefault();
        return (document,
            document.TypesOf<WallType>().Single(t => t.Name == "Generic - 200mm"),
            document.Levels.First().Id);
    }

    private static Wall AddWall(
        BimDocument document, WallType type, Guid levelId, double length, string mark = "")
    {
        var wall = new Wall
        {
            Start = new Point2D(0, 0),
            End = new Point2D(length, 0),
            TypeId = type.Id,
            LevelId = levelId,
            Mark = mark
        };

        document.Add(wall);
        return wall;
    }

    [Fact]
    public void AScheduleReportsOneRowPerElementOfItsCategory()
    {
        var (document, type, levelId) = Project();
        AddWall(document, type, levelId, 5000, "W1");
        AddWall(document, type, levelId, 3000, "W2");

        var result = Core.Schedules.Schedule.Run(document,
            new ScheduleDefinition("Walls", BuiltInCategory.Walls, "Mark", "Length"));

        Assert.Equal(2, result.Count);
        Assert.Equal(new[] { "Mark", "Length" }, result.Columns.Select(c => c.Field));
    }

    [Fact]
    public void OtherCategoriesAreLeftOut()
    {
        var (document, type, levelId) = Project();
        AddWall(document, type, levelId, 5000);
        document.Add(new Room { Location = new Point2D(0, 0), LevelId = levelId });

        var walls = Core.Schedules.Schedule.Run(document,
            new ScheduleDefinition("Walls", BuiltInCategory.Walls, "Mark"));

        Assert.Single(walls.Rows);
        Assert.IsType<Wall>(walls.Rows[0].Element);
    }

    [Fact]
    public void AScheduleReadsBothInstanceAndTypeParameters()
    {
        var (document, type, levelId) = Project();
        AddWall(document, type, levelId, 5000, "W1");

        var result = Core.Schedules.Schedule.Run(document,
            new ScheduleDefinition("Walls", BuiltInCategory.Walls, "Mark", "Type Name", "Fire Rating"));

        var row = result.Rows.Single();

        Assert.Equal("W1", row.Text("Mark"));                    // instance
        Assert.Equal("Generic - 200mm", row.Text("Type Name"));  // type
        Assert.Equal(type.FireRating, row.Text("Fire Rating"));  // type
    }

    // ---- totals ----------------------------------------------------------------

    [Fact]
    public void NumericColumnsAreTotalled()
    {
        var (document, type, levelId) = Project();
        AddWall(document, type, levelId, 5000);
        AddWall(document, type, levelId, 3000);

        var result = Core.Schedules.Schedule.Run(document,
            new ScheduleDefinition("Walls", BuiltInCategory.Walls, "Length", "Area"));

        Assert.Equal(8000, result.Totals["Length"], precision: 3);

        // Both walls are 3 m high, so 8 m of wall is 24 m² of elevation.
        Assert.Equal(8000d * 3000, result.Totals["Area"], precision: 3);
    }

    /// <summary>
    /// Adding up five wall heights gives 15 m of nothing. A schedule that prints a
    /// meaningless total teaches its reader to distrust the ones that mean something.
    /// </summary>
    [Fact]
    public void AFieldCanBeExcludedFromTotals()
    {
        var (document, type, levelId) = Project();
        AddWall(document, type, levelId, 5000);
        AddWall(document, type, levelId, 3000);

        var definition = new ScheduleDefinition("Walls", BuiltInCategory.Walls, "Length", "Height")
            .WithoutTotals("Height");

        var result = Core.Schedules.Schedule.Run(document, definition);

        Assert.True(result.Columns.Single(c => c.Field == "Length").IsTotalled);
        Assert.False(result.Columns.Single(c => c.Field == "Height").IsTotalled);

        Assert.Equal(8000, result.Totals["Length"], precision: 3);
        Assert.DoesNotContain("Height", result.Totals.Keys);
    }

    [Fact]
    public void TheDefaultWallScheduleDoesNotTotalHeights()
    {
        var definition = ScheduleDefinition.Defaults().Single(d => d.Name == "Wall Schedule");

        Assert.Contains("Height", definition.NotTotalled);
        Assert.DoesNotContain("Length", definition.NotTotalled);
    }

    [Fact]
    public void TextColumnsAreNotTotalled()
    {
        var (document, type, levelId) = Project();
        AddWall(document, type, levelId, 5000, "W1");

        var result = Core.Schedules.Schedule.Run(document,
            new ScheduleDefinition("Walls", BuiltInCategory.Walls, "Mark", "Length"));

        Assert.False(result.Columns.Single(c => c.Field == "Mark").IsTotalled);
        Assert.True(result.Columns.Single(c => c.Field == "Length").IsTotalled);
        Assert.DoesNotContain("Mark", result.Totals.Keys);
    }

    // ---- sorting, filtering, grouping -------------------------------------------

    [Fact]
    public void LengthsSortAsNumbersNotAsText()
    {
        var (document, type, levelId) = Project();
        AddWall(document, type, levelId, 10000, "long");
        AddWall(document, type, levelId, 2000, "short");

        var result = Core.Schedules.Schedule.Run(document,
            new ScheduleDefinition("Walls", BuiltInCategory.Walls, "Mark", "Length") { SortBy = "Length" });

        // Sorted as text, "10.00 m" would come before "2.00 m" - exactly the sort of error a
        // schedule hides rather than shows.
        Assert.Equal(new[] { "short", "long" }, result.Rows.Select(r => r.Text("Mark")));
    }

    [Fact]
    public void AFilterNarrowsTheSchedule()
    {
        var (document, type, levelId) = Project();
        AddWall(document, type, levelId, 5000, "keep");
        AddWall(document, type, levelId, 3000, "drop");

        var definition = new ScheduleDefinition("Walls", BuiltInCategory.Walls, "Mark", "Length");
        definition.Filters.Add(new ScheduleFilter("Mark", ScheduleOperator.Equals, "keep"));

        var result = Core.Schedules.Schedule.Run(document, definition);

        Assert.Single(result.Rows);
        Assert.Equal("keep", result.Rows[0].Text("Mark"));
    }

    [Fact]
    public void AGreaterThanFilterComparesNumbersNumerically()
    {
        var (document, type, levelId) = Project();
        AddWall(document, type, levelId, 10000, "long");
        AddWall(document, type, levelId, 2000, "short");

        var definition = new ScheduleDefinition("Walls", BuiltInCategory.Walls, "Mark", "Length");
        definition.Filters.Add(new ScheduleFilter("Length", ScheduleOperator.GreaterThan, "5000"));

        var result = Core.Schedules.Schedule.Run(document, definition);

        Assert.Single(result.Rows);
        Assert.Equal("long", result.Rows[0].Text("Mark"));
    }

    [Fact]
    public void GroupingSplitsTheRowsAndSubtotalsEachGroup()
    {
        var document = BimDocument.CreateDefault();
        var levelId = document.Levels.First().Id;
        var generic = document.TypesOf<WallType>().Single(t => t.Name == "Generic - 200mm");
        var partition = document.TypesOf<WallType>().Single(t => t.Name.Contains("Partition"));

        AddWall(document, generic, levelId, 5000);
        AddWall(document, generic, levelId, 3000);
        AddWall(document, partition, levelId, 2000);

        var result = Core.Schedules.Schedule.Run(document,
            new ScheduleDefinition("Walls", BuiltInCategory.Walls, "Type Name", "Length")
            {
                GroupBy = "Type Name"
            });

        Assert.Equal(2, result.Groups.Count);

        var genericGroup = result.Groups.Single(g => g.Heading == "Generic - 200mm");
        Assert.Equal(2, genericGroup.Rows.Count);
        Assert.Equal(8000, genericGroup.Subtotals["Length"], precision: 3);

        // Subtotals must add up to the total, or the document contradicts itself.
        Assert.Equal(result.Totals["Length"],
            result.Groups.Sum(g => g.Subtotals["Length"]), precision: 3);
    }

    // ---- bidirectional editing --------------------------------------------------

    /// <summary>
    /// Section 6.4: changing a schedule cell updates the model. The cells are the element's
    /// own parameters, so this is not a separate write path that could drift.
    /// </summary>
    [Fact]
    public void EditingAScheduleCellChangesTheElement()
    {
        var (document, type, levelId) = Project();
        var wall = AddWall(document, type, levelId, 5000, "W1");

        var result = Core.Schedules.Schedule.Run(document,
            new ScheduleDefinition("Walls", BuiltInCategory.Walls, "Mark", "Unconnected Height"));

        var row = result.Rows.Single();
        Assert.True(row["Mark"]!.TrySetFromText("W-99"));
        Assert.True(row["Unconnected Height"]!.TrySetFromText("2.70 m"));

        Assert.Equal("W-99", wall.Mark);
        Assert.Equal(2700, wall.UnconnectedHeight, precision: 3);
    }

    [Fact]
    public void ComputedColumnsCannotBeEditedFromTheSchedule()
    {
        var (document, type, levelId) = Project();
        AddWall(document, type, levelId, 5000);

        var result = Core.Schedules.Schedule.Run(document,
            new ScheduleDefinition("Walls", BuiltInCategory.Walls, "Area", "Volume"));

        Assert.All(result.Columns, column =>
            Assert.True(column.IsReadOnly, $"{column.Field} is derived and must not be typed into"));

        // A length can be typed, as it can in the properties: the wall's end moves to make it so.
        var lengths = Core.Schedules.Schedule.Run(document, new ScheduleDefinition("Walls", BuiltInCategory.Walls, "Length"));
        Assert.True(lengths.Rows.Single()["Length"]!.TrySetFromText("4000"));
        Assert.Equal(4000, document.Walls.Single().Length, precision: 6);
    }

    // ---- material takeoff --------------------------------------------------------

    [Fact]
    public void TakeoffReportsEachLayerOfEachElement()
    {
        var document = BimDocument.CreateDefault();
        var levelId = document.Levels.First().Id;
        var exterior = document.TypesOf<WallType>().Single(t => t.Name.Contains("Brick on Block"));

        AddWall(document, exterior, levelId, 5000);

        var lines = MaterialTakeoff.Lines(document);

        // The 330 mm exterior wall has five layers, so one wall gives five takeoff lines.
        Assert.Equal(5, lines.Count);
        Assert.Contains(lines, line => line.Material == "Concrete Block");
    }

    [Fact]
    public void TakeoffVolumeIsFaceAreaTimesLayerThickness()
    {
        var document = BimDocument.CreateDefault();
        var levelId = document.Levels.First().Id;
        var exterior = document.TypesOf<WallType>().Single(t => t.Name.Contains("Brick on Block"));

        var wall = AddWall(document, exterior, levelId, 5000);
        var area = wall.GetArea(document);

        var block = MaterialTakeoff.Lines(document).Single(line => line.Material == "Concrete Block");

        // The blockwork is the 140 mm structural layer, not the wall's whole 330 mm.
        Assert.Equal(area * 140, block.Volume, precision: 3);
        Assert.True(block.Volume < wall.GetVolume(document),
            "a takeoff must report the layer, not the gross wall");
    }

    [Fact]
    public void TakeoffRollsUpPerMaterialAcrossWallsAndSlabs()
    {
        var document = BimDocument.CreateDefault();
        var levelId = document.Levels.First().Id;
        var exterior = document.TypesOf<WallType>().Single(t => t.Name.Contains("Brick on Block"));
        var floorType = document.TypesOf<FloorType>().Single(t => t.Name.Contains("Screed"));

        AddWall(document, exterior, levelId, 5000);

        var floor = new Floor { TypeId = floorType.Id, LevelId = levelId };
        floor.SetBoundary(new[]
        {
            new Point2D(0, 0), new Point2D(6000, 0), new Point2D(6000, 4000), new Point2D(0, 4000)
        });
        document.Add(floor);

        var totals = MaterialTakeoff.Totals(document);

        // Concrete appears in the floor's structural layer, so it must be reported.
        Assert.Contains(totals, total => total.Material == "Concrete, Cast In Situ");
        Assert.All(totals, total => Assert.True(total.Volume > 0));
    }

    // ---- export ------------------------------------------------------------------

    [Fact]
    public void CsvCarriesTheHeadingsRowsAndTotal()
    {
        var (document, type, levelId) = Project();
        AddWall(document, type, levelId, 5000, "W1");

        var result = Core.Schedules.Schedule.Run(document,
            new ScheduleDefinition("Wall Schedule", BuiltInCategory.Walls, "Mark", "Length"));

        var csv = ScheduleCsv.Write(result);

        Assert.Contains("Wall Schedule", csv);
        Assert.Contains("Mark,Length", csv);
        Assert.Contains("W1", csv);
        Assert.Contains("Total (1)", csv);
    }

    [Fact]
    public void CsvQuotesValuesContainingCommas()
    {
        var (document, type, levelId) = Project();
        AddWall(document, type, levelId, 5000, "W1, ground");

        var result = Core.Schedules.Schedule.Run(document,
            new ScheduleDefinition("Walls", BuiltInCategory.Walls, "Mark"));

        // Unquoted, this row would silently become two columns in a spreadsheet.
        Assert.Contains("\"W1, ground\"", ScheduleCsv.Write(result));
    }

    [Fact]
    public void EveryDefaultScheduleRunsAgainstAFreshProject()
    {
        var document = BimDocument.CreateDefault();

        foreach (var definition in ScheduleDefinition.Defaults())
        {
            var result = Core.Schedules.Schedule.Run(document, definition);

            // Empty is fine; throwing or losing its columns is not.
            Assert.Equal(definition.Fields.Count, result.Columns.Count);
        }
    }
}
