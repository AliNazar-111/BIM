using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Coordination;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Parameters;
using BIMDesigner.Core.Schedules;

namespace BIMDesigner.Tests;

/// <summary>
/// The cost plan (5D): each element measured as its rate is given and set out by NRM1's elements
/// in England or UniFormat II's in the US, the additions on the building works, the cost over the
/// programme and earned value.
/// </summary>
public class CostPlanTests
{
    [Fact]
    public void AWallIsMeasuredNetOfItsWindowAndCostedBySquareMetre()
    {
        var document = ConstructionTests.House();
        var window = document.Elements.OfType<Window>().Single();
        var wall = document.Walls.Single(each => each.Id == window.HostWallId);
        var type = document.FindType<WindowType>(window.TypeId)!;

        var area = CostPlanning.Measure(document, wall, CostUnit.Area);
        Assert.Equal(wall.Length * 3000 - type.Width * type.Height, area, precision: 3);
        var rate = document.FindType<ElementType>(wall.TypeId)!.Cost;
        Assert.Equal((decimal)(area / 1e6) * rate, CostPlanning.CostOf(document, wall));
        Assert.Equal(type.Cost, CostPlanning.CostOf(document, window));

        // A schedule's total cost is the same.
        var walls = Schedule.Run(document, ScheduleDefinition.Defaults().First(definition => definition.Category == BuiltInCategory.Walls));
        Assert.Equal((double)document.Walls.Sum(each => CostPlanning.CostOf(document, each)),
            walls.Rows.Sum(row => Schedule.Numeric(row[ScheduleDefinition.TotalCostField]) ?? 0), precision: 2);
    }

    [Fact]
    public void InEnglandTheCostPlanIsSetOutByNrm1()
    {
        var document = ConstructionTests.House();
        var lines = CostPlanning.Lines(document);
        string CodeOf<T>(Func<T, bool>? which = null) where T : Element =>
            lines.First(line => line.Element is T element && (which?.Invoke(element) ?? true)).Code;

        Assert.Equal("1.1", CodeOf<Floor>(floor => floor.LevelId == document.Levels[0].Id));
        Assert.Equal("2.2", CodeOf<Floor>(floor => floor.LevelId == document.Levels[1].Id));
        Assert.Equal("2.3", CodeOf<Roof>());
        Assert.Equal("2.5", CodeOf<Wall>());
        Assert.Equal("2.6", CodeOf<Window>());
        Assert.Equal("4.1", CodeOf<Component>());

        var plan = CostPlanning.Summary(document);
        var works = lines.Sum(line => line.Total);
        Assert.Equal(works, plan.Rows.Single(row => row.Title.StartsWith("Building works estimate")).Amount);
        var prelims = plan.Rows.Single(row => row.Code == "9").Amount;
        Assert.Equal(Math.Round(works * 0.12m, 2), prelims);
        var profit = plan.Rows.Single(row => row.Code == "10").Amount;
        Assert.Equal(Math.Round((works + prelims) * 0.05m, 2), profit);
        var risk = plan.Rows.Single(row => row.Code == "13").Amount;
        Assert.Equal(Math.Round((works + prelims + profit) * 0.05m, 2), risk);
        Assert.Equal(works + prelims + profit + risk, plan.Total);
        Assert.Equal("£", plan.Currency);

        // Spread over both floors' 80 m².
        Assert.Equal(160, plan.FloorArea, precision: 3);
        Assert.Equal(plan.Total / 160m, plan.PerSquareMetre);

        // VAT, outside NRM1's cost limit, when asked for.
        document.CostSettings.Tax = 20;
        Assert.Equal(plan.Total * 1.2m, CostPlanning.Summary(document).Total, 2);
    }

    [Fact]
    public void InTheUsItIsUniFormatII()
    {
        var document = ConstructionTests.House();
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.Irc;
        var lines = CostPlanning.Lines(document);
        Assert.Contains(lines, line => line.Element is Floor && line.Code == "A1030" && line.Title == "Slab on Grade");
        Assert.Contains(lines, line => line.Element is Wall && line.Code == "B2010");
        Assert.Contains(lines, line => line.Element is Window && line.Code == "B2020");
        Assert.Contains(lines, line => line.Element is Roof && line.Code is "B3010" or "B1020");

        var plan = CostPlanning.Summary(document);
        Assert.Contains(plan.Rows, row => row.Title == "General conditions");
        Assert.Contains(plan.Rows, row => row.Title == "Contingency");
        Assert.Contains(plan.Rows, row => row.IsTotal && row.Title == "Shell");
        Assert.Equal("$", plan.Currency);
    }

    [Fact]
    public void WhatStaysIsNotCostedAndWhatIsDemolishedIs()
    {
        var document = ConstructionTests.House();
        var desk = document.Elements.OfType<Component>().Single();
        desk.PhaseCreated = DesignPhase.Existing;
        Assert.DoesNotContain(CostPlanning.Lines(document), line => line.Element == desk);
        desk.PhaseDemolished = DesignPhase.New;
        Assert.Equal("0.2", CostPlanning.Lines(document).Single(line => line.Element == desk).Code);
    }

    [Fact]
    public void TheCostIsSpreadOverTheProgrammeAndEarnedValueMeasured()
    {
        var document = ConstructionTests.House();
        ConstructionPlanning.Generate(document, new DateOnly(2026, 11, 2));
        ConstructionPlanning.SaveBaseline(document.Construction);
        var budgets = CostPlanning.TaskBudgets(document);
        var flow = CostPlanning.CashFlow(document);
        Assert.Equal(budgets.Values.Sum(), flow[^1].Cumulative, 2);
        Assert.Equal(CostPlanning.Lines(document).Where(line => document.Construction.Tasks.Any(task => ConstructionPlanning.Elements(document, task).Contains(line.Element))).Sum(line => line.Total),
            budgets.Values.Sum(), 2);

        // The ground slab half done at its planned finish, for more than half its budget.
        var slab = document.Construction.Tasks.Single(task => task.Name.EndsWith("Ground slab"));
        slab.PercentComplete = 50;
        slab.ActualCost = budgets[slab.Id] * 0.8m;
        var status = slab.BaselineFinish!.Value;
        var value = CostPlanning.Earned(document, status);
        var footings = document.Construction.Tasks.Where(task => task.Name.EndsWith("Footings")).Sum(task => budgets[task.Id]);
        Assert.Equal(budgets[slab.Id] + footings, value.Planned, 2);
        Assert.Equal(budgets[slab.Id] * 0.5m, value.Earned, 2);
        Assert.Equal(value.Earned - value.Planned, value.ScheduleVariance);
        Assert.Equal((double)(value.Earned / value.Actual), value.CostPerformance, 6);
        Assert.Equal(value.Budget / (decimal)value.CostPerformance, value.EstimateAtCompletion, 2);
    }

    [Fact]
    public void TheAdditionsAreSaved()
    {
        var document = ConstructionTests.House();
        document.CostSettings.Preliminaries = 14;
        document.CostSettings.Risk = 7.5;
        document.CostSettings.OtherCosts = 25000;
        document.CostSettings.Currency = "€";
        var path = Path.Combine(Path.GetTempPath(), $"bimtest-{Guid.NewGuid():N}{Infrastructure.Serialization.ProjectFile.Extension}");
        try
        {
            Infrastructure.Serialization.ProjectFile.Save(document, path);
            var again = Infrastructure.Serialization.ProjectFile.Load(path);
            Assert.Equal((14.0, 7.5, 25000m, "€"), (again.CostSettings.Preliminaries, again.CostSettings.Risk, again.CostSettings.OtherCosts, again.CostSettings.Currency));
            Assert.Equal(CostPlanning.Summary(document).Total, CostPlanning.Summary(again).Total);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
