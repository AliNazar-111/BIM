using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;
using BIMDesigner.Core.Parameters;
using BIMDesigner.Core.Schedules;
using BIMDesigner.Core.Sheets;
using BIMDesigner.Core.Views;
using BIMDesigner.Infrastructure.Serialization;

namespace BIMDesigner.Tests;

/// <summary>
/// Phasing - what a view shows of the building at its phase, and how; view filters picking
/// things out by their parameters; design options side by side; the project's own parameters;
/// and the classification its types are coded to.
/// </summary>
public class PhasingFilterOptionTests
{
    private static Wall Wall(BimDocument document, double y, WallFunction function = WallFunction.Exterior, DesignPhase created = DesignPhase.New, DesignPhase? demolished = null)
    {
        var type = document.TypesOf<WallType>().First(t => t.Function == function);
        var wall = new Wall
        {
            Start = new Point2D(0, y), End = new Point2D(5000, y), TypeId = type.Id, LevelId = document.Levels[0].Id, UnconnectedHeight = 3000,
            PhaseCreated = created, PhaseDemolished = demolished
        };
        document.Add(wall);
        return wall;
    }

    [Fact]
    public void AnElementIsNewExistingDemolishedOrTemporaryInAPhase()
    {
        static PhaseStatus? Status(DesignPhase created, DesignPhase? demolished, DesignPhase view) =>
            Phasing.StatusIn(new Wall { PhaseCreated = created, PhaseDemolished = demolished }, view);

        Assert.Equal(PhaseStatus.New, Status(DesignPhase.New, null, DesignPhase.New));
        Assert.Equal(PhaseStatus.Existing, Status(DesignPhase.Existing, null, DesignPhase.New));
        Assert.Equal(PhaseStatus.Demolished, Status(DesignPhase.Existing, DesignPhase.New, DesignPhase.New));
        Assert.Equal(PhaseStatus.Temporary, Status(DesignPhase.New, DesignPhase.New, DesignPhase.New));
        Assert.Equal(PhaseStatus.Temporary, Status(DesignPhase.Temporary, null, DesignPhase.New));

        // Not there: not built yet, or gone before.
        Assert.Null(Status(DesignPhase.New, null, DesignPhase.Existing));
        Assert.Null(Status(DesignPhase.Future, null, DesignPhase.New));
        Assert.Null(Status(DesignPhase.Existing, DesignPhase.New, DesignPhase.Future));
        Assert.Null(Status(DesignPhase.Temporary, null, DesignPhase.Future));

        // The filters, as Revit's are: a demolition plan shows what stays and what goes, not the new.
        Assert.Equal(PhaseDisplay.NotDisplayed, Phasing.Display(PhaseFilter.ShowPreviousAndDemolition, PhaseStatus.New));
        Assert.Equal(PhaseDisplay.Overridden, Phasing.Display(PhaseFilter.ShowPreviousAndDemolition, PhaseStatus.Demolished));
        Assert.Equal(PhaseDisplay.ByCategory, Phasing.Display(PhaseFilter.ShowComplete, PhaseStatus.Existing));
        Assert.Equal(PhaseDisplay.NotDisplayed, Phasing.Display(PhaseFilter.ShowComplete, PhaseStatus.Demolished));
        Assert.Equal(PhaseDisplay.Overridden, Phasing.Display(PhaseFilter.ShowAll, PhaseStatus.Existing));
        Assert.Equal(PhaseDisplay.NotDisplayed, Phasing.Display(PhaseFilter.ShowNew, PhaseStatus.Existing));
        Assert.True(Phasing.TryParse("Show Previous + Demo", out var parsed));
        Assert.Equal(PhaseFilter.ShowPreviousAndDemolition, parsed);
    }

    [Fact]
    public void AViewShowsTheBuildingAtItsPhaseThroughItsFilter()
    {
        var document = BimDocument.CreateDefault();
        var plan = ViewReference.FloorPlan(document.Levels[0].Id);
        var existing = Wall(document, 0, created: DesignPhase.Existing);
        var demolished = Wall(document, 2000, created: DesignPhase.Existing, demolished: DesignPhase.New);
        var built = Wall(document, 4000);
        var settings = document.ViewSettings;

        // Show All: every one there, the old faded, what goes red and dashed.
        var shows = settings.FilterFor(document, plan);
        Assert.True(shows(existing) && shows(demolished) && shows(built));
        var style = settings.StyleFor(document, plan);
        Assert.True(style(existing).Halftone);
        Assert.Equal((Phasing.DemolishedColour, true), (style(demolished).Colour!.Value, style(demolished).Dashed));
        Assert.True(style(built).IsPlain);

        // The finished building: what goes is gone, the old as itself.
        settings.SetPhaseFilter(plan, PhaseFilter.ShowComplete);
        shows = settings.FilterFor(document, plan);
        Assert.False(shows(demolished));
        Assert.True(shows(existing) && shows(built));
        Assert.True(settings.StyleFor(document, plan)(existing).IsPlain);

        // A demolition plan: no new work.
        settings.SetPhaseFilter(plan, PhaseFilter.ShowPreviousAndDemolition);
        shows = settings.FilterFor(document, plan);
        Assert.False(shows(built));
        Assert.True(shows(existing) && shows(demolished));

        // The existing phase: the building as it was, new work not built yet, what goes still standing.
        settings.SetPhaseFilter(plan, PhaseFilter.ShowAll);
        settings.SetPhase(plan, DesignPhase.Existing);
        shows = settings.FilterFor(document, plan);
        Assert.False(shows(built));
        Assert.True(shows(existing) && shows(demolished));
        Assert.True(settings.StyleFor(document, plan)(demolished).IsPlain);

        // A tag goes with what it labels.
        var tag = new BIMDesigner.Core.Annotation.Tag { TargetId = built.Id, LevelId = built.LevelId };
        document.Add(tag);
        Assert.False(settings.FilterFor(document, plan)(tag));
    }

    [Fact]
    public void AFilterPicksThingsOutByTheirParametersAndAViewHidesOrColoursThem()
    {
        var document = BimDocument.CreateDefault();
        var plan = ViewReference.FloorPlan(document.Levels[0].Id);
        var outside = Wall(document, 0);
        var inside = Wall(document, 3000, WallFunction.Interior);
        var filter = new ParameterFilter { Name = "Outside walls" };
        filter.Categories.Add(BuiltInCategory.Walls);
        filter.Rules.Add(new ScheduleFilter("Function", ScheduleOperator.Equals, "Exterior"));
        document.ParameterFilters.Add(filter);
        Assert.True(filter.Picks(document, outside));
        Assert.False(filter.Picks(document, inside));

        var red = new ColourRgb(0xD0, 0x20, 0x20);
        document.ViewSettings.SetFilters(plan, new[] { new FilterOverride(filter.Id, Colour: red) });
        Assert.Equal(red, document.ViewSettings.StyleFor(document, plan)(outside).Colour);
        Assert.True(document.ViewSettings.StyleFor(document, plan)(inside).IsPlain);

        document.ViewSettings.SetFilters(plan, new[] { new FilterOverride(filter.Id, Visible: false) });
        Assert.False(document.ViewSettings.FilterFor(document, plan)(outside));
        Assert.True(document.ViewSettings.FilterFor(document, plan)(inside));

        // A template carries the filters and the phase filter to the views it governs.
        var template = ViewTemplates.FromView(document, plan)!;
        document.Add(template);
        Assert.Single(template.Filters);
        var other = ViewReference.FloorPlan(document.Levels[1].Id);
        new UndoStack().Execute(ViewTemplates.Apply(document, template, other, govern: true));
        Assert.Equal(document.ViewSettings.FiltersOf(plan), document.ViewSettings.FiltersOf(other));
        Assert.True(ViewTemplates.Locks(document, other, ViewTemplateSetting.Filters));
    }

    [Fact]
    public void DesignOptionsAreShownOneASetAndThePrimaryAccepted()
    {
        var document = BimDocument.CreateDefault();
        var plan = ViewReference.FloorPlan(document.Levels[0].Id);
        var set = new DesignOptionSet { Name = "Kitchen" };
        var a = new DesignOption { Name = "Island", IsPrimary = true };
        var b = new DesignOption { Name = "Galley" };
        set.Options.AddRange(new[] { a, b });
        document.DesignOptionSets.Add(set);

        var main = Wall(document, 0);

        // Drawn while an option is worked on, a thing is in that option.
        document.EditingDesignOption = a.Id;
        var island = Wall(document, 2000);
        document.EditingDesignOption = b.Id;
        var galley = Wall(document, 4000);
        document.EditingDesignOption = null;
        Assert.Equal((a.Id, b.Id), (island.DesignOptionId!.Value, galley.DesignOptionId!.Value));
        Assert.Null(main.DesignOptionId);
        Assert.Equal("Kitchen: Island (primary)", DesignOptions.Describe(document, island.DesignOptionId));

        var shows = document.ViewSettings.FilterFor(document, plan);
        Assert.True(shows(main) && shows(island));
        Assert.False(shows(galley));

        document.ViewSettings.SetOptionShown(plan, set.Id, b.Id);
        shows = document.ViewSettings.FilterFor(document, plan);
        Assert.True(shows(main) && shows(galley));
        Assert.False(shows(island));

        // The primary accepted: its walls the main model's, the other option's gone - one step to undo.
        var history = new UndoStack();
        history.Execute(DesignOptions.AcceptPrimary(document, set));
        Assert.Null(island.DesignOptionId);
        Assert.DoesNotContain(galley, document.Elements);
        Assert.Empty(document.DesignOptionSets);
        history.Undo();
        Assert.Contains(galley, document.Elements);
        Assert.Equal(a.Id, island.DesignOptionId);
        Assert.Single(document.DesignOptionSets);

        // Brought back by an undo, a thing keeps the option it had - whatever is being worked on now.
        document.EditingDesignOption = b.Id;
        history.Execute(new DeleteElementCommand(document, main));
        history.Undo();
        Assert.Null(main.DesignOptionId);
    }

    [Fact]
    public void TheProjectsOwnParametersAreOnTheirCategoriesAndShared()
    {
        var document = BimDocument.CreateDefault();
        var wall = Wall(document, 0);
        var note = new ProjectParameter { Name = "Acoustic Note", DataType = ParameterDataType.Text };
        note.Categories.Add(BuiltInCategory.Walls);
        var span = new ProjectParameter { Name = "Lintel Bearing", DataType = ParameterDataType.Length, Binding = ParameterBinding.Type };
        span.Categories.Add(BuiltInCategory.Walls);
        document.ProjectParameters.AddRange(new[] { note, span });

        var instance = wall.GetInstanceParameters(document).Single(p => p.Name == "Acoustic Note");
        Assert.True(instance.TrySetFromText("Flanking: check"));
        Assert.Equal("Flanking: check", wall.ParameterValues[note.Id]);
        var type = document.GetWallType(wall)!;
        var bearing = type.AllTypeParameters(document).Single(p => p.Name == "Lintel Bearing");
        Assert.True(bearing.TrySetFromText("150"));
        Assert.Equal(150.0, type.ParameterValues[span.Id]);
        Assert.DoesNotContain(document.TypesOf<DoorType>().First().AllTypeParameters(document), p => p.Name == "Lintel Bearing");

        // In schedules, as any parameter is.
        Assert.Contains("Acoustic Note", Schedule.AvailableFields(document, BuiltInCategory.Walls));
        var result = Schedule.Run(document, new ScheduleDefinition("Walls", BuiltInCategory.Walls, "Mark", "Acoustic Note", "Lintel Bearing"));
        Assert.Equal(("Flanking: check", "150 mm"), (result.Rows[0].Text("Acoustic Note"), result.Rows[0].Text("Lintel Bearing")));

        // Copied with what it is on.
        var copy = (Wall)ElementCopy.Clone(wall)!;
        Assert.Equal("Flanking: check", copy.ParameterValues[note.Id]);

        // A shared parameter file written and read back, the GUIDs kept.
        var file = ProjectParameters.WriteSharedFile(new[] { note, span });
        Assert.Contains("*PARAM\tGUID\tNAME\tDATATYPE", file);
        var read = ProjectParameters.ReadSharedFile(file, out var problems);
        Assert.Empty(problems);
        Assert.Equal(new[] { (note.Id, "Acoustic Note", ParameterDataType.Text), (span.Id, "Lintel Bearing", ParameterDataType.Length) },
            read.Select(p => (p.Id, p.Name, p.DataType)));
        Assert.All(read, p => Assert.True(p.Shared));

        var foreign = "*GROUP\tID\tNAME\nGROUP\t1\tStructure\n*PARAM\tGUID\tNAME\tDATATYPE\tDATACATEGORY\tGROUP\tVISIBLE\tDESCRIPTION\tUSERMODIFIABLE\n"
                      + "PARAM\t6b0c1c3e-4a7d-4d61-9a0e-3c6b0f1d2e44\tAxial Force\tFORCE\t\t1\t1\tTo the engineer\t1\n";
        var force = Assert.Single(ProjectParameters.ReadSharedFile(foreign, out problems));
        Assert.Equal((ParameterDataType.Text, "Structure", "To the engineer"), (force.DataType, force.SharedGroup, force.Description));
        Assert.Single(problems);
    }

    [Fact]
    public void TypesAndRoomsAreCodedToTheClassification()
    {
        var document = BimDocument.CreateDefault();
        Assert.Equal("External walls", document.Classification.Find("EF_25_10_25")!.Title);
        var type = document.TypesOf<WallType>().First(t => t.Function == WallFunction.Exterior);
        var classification = type.AllTypeParameters(document).Single(p => p.Name == "Classification");
        Assert.Contains("EF_25_10_25", classification.AllowedValues!);
        Assert.True(classification.TrySetFromText("EF_25_10_25"));
        Assert.Equal("External walls", type.AllTypeParameters(document).Single(p => p.Name == "Classification Title").DisplayValue);
        Assert.Equal("EF_25_10_25: External walls", document.Classification.Describe(type.ClassificationCode));

        var table = ClassificationTable.Parse("Code,Title\nPr_20_93_96,\"Windows, timber\"\nSL_20_15_59,Office spaces\n", "Uniclass 2015");
        Assert.Equal(new[] { ("Pr_20_93_96", "Windows, timber"), ("SL_20_15_59", "Office spaces") }, table.Entries.Select(e => (e.Code, e.Title)));
    }

    [Fact]
    public void PhasesFiltersOptionsParametersAndClassificationAreKept()
    {
        var document = BimDocument.CreateDefault();
        var plan = ViewReference.FloorPlan(document.Levels[0].Id);
        var wall = Wall(document, 0, created: DesignPhase.Existing, demolished: DesignPhase.New);
        var floor = new Floor { TypeId = document.TypesOf<FloorType>().First().Id, LevelId = document.Levels[0].Id, PhaseCreated = DesignPhase.Existing };
        floor.SetBoundary(new[] { new Point2D(0, 0), new Point2D(4000, 0), new Point2D(4000, 4000), new Point2D(0, 4000) });
        document.Add(floor);

        var filter = new ParameterFilter { Name = "Fire walls", MatchAny = true };
        filter.Categories.Add(BuiltInCategory.Walls);
        filter.Rules.Add(new ScheduleFilter("Fire Rating", ScheduleOperator.Contains, "60"));
        document.ParameterFilters.Add(filter);
        var set = new DesignOptionSet { Name = "Porch" };
        set.Options.Add(new DesignOption { Name = "Open", IsPrimary = true });
        set.Options.Add(new DesignOption { Name = "Enclosed" });
        document.DesignOptionSets.Add(set);
        wall.DesignOptionId = set.Options[1].Id;

        document.ViewSettings.SetPhase(plan, DesignPhase.Existing);
        document.ViewSettings.SetPhaseFilter(plan, PhaseFilter.ShowPreviousAndDemolition);
        document.ViewSettings.SetFilters(plan, new[] { new FilterOverride(filter.Id, Colour: new ColourRgb(1, 2, 3), Halftone: true) });
        document.ViewSettings.SetOptionShown(plan, set.Id, set.Options[1].Id);

        var parameter = new ProjectParameter { Name = "Checked", DataType = ParameterDataType.YesNo };
        parameter.Categories.Add(BuiltInCategory.Floors);
        document.ProjectParameters.Add(parameter);
        floor.ParameterValues[parameter.Id] = true;
        var type = document.TypesOf<FloorType>().First();
        type.ClassificationCode = "EF_30_20_34";

        var reopened = ProjectFile.FromJson(ProjectFile.ToJson(document));
        var wallBack = reopened.Walls.Single();
        Assert.Equal((DesignPhase.Existing, (DesignPhase?)DesignPhase.New, (Guid?)set.Options[1].Id), (wallBack.PhaseCreated, wallBack.PhaseDemolished, wallBack.DesignOptionId));
        Assert.Equal(DesignPhase.Existing, reopened.Elements.OfType<Floor>().Single().PhaseCreated);
        Assert.Equal(true, reopened.Elements.OfType<Floor>().Single().ParameterValues[parameter.Id]);
        Assert.Equal((DesignPhase.Existing, PhaseFilter.ShowPreviousAndDemolition), (reopened.ViewSettings.PhaseOf(plan), reopened.ViewSettings.PhaseFilterOf(plan)));
        Assert.Equal(new FilterOverride(filter.Id, true, new ColourRgb(1, 2, 3), true), Assert.Single(reopened.ViewSettings.FiltersOf(plan)));
        Assert.Equal(set.Options[1].Id, reopened.ViewSettings.OptionShownIn(plan, set.Id));
        var filterBack = Assert.Single(reopened.ParameterFilters);
        Assert.Equal(("Fire walls", true, "Fire Rating"), (filterBack.Name, filterBack.MatchAny, filterBack.Rules[0].Field));
        Assert.Equal(new[] { "Open", "Enclosed" }, Assert.Single(reopened.DesignOptionSets).Options.Select(o => o.Name));
        Assert.Equal(("Checked", ParameterDataType.YesNo), (reopened.ProjectParameters[0].Name, reopened.ProjectParameters[0].DataType));
        Assert.Equal("EF_30_20_34", reopened.FindType<FloorType>(type.Id)!.ClassificationCode);
        Assert.Equal(document.Classification.Entries.Count, reopened.Classification.Entries.Count);

        // Saved again, the same - but for when it was saved.
        static string Undated(string json) => System.Text.RegularExpressions.Regex.Replace(json, "\"savedUtc\": \"[^\"]*\"", string.Empty);
        Assert.Equal(Undated(ProjectFile.ToJson(reopened)), Undated(ProjectFile.ToJson(ProjectFile.FromJson(ProjectFile.ToJson(reopened)))));
    }
}
