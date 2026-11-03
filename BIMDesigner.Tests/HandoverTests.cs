using System.IO;
using System.Windows;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Handover;
using BIMDesigner.Infrastructure.Interoperability;
using BIMDesigner.Infrastructure.Serialization;
using BIMDesigner.UI;
using BIMDesigner.UI.Export;
using Xbim.Ifc4.Interfaces;
using BimWindow = BIMDesigner.Core.Architecture.Window;

namespace BIMDesigner.Tests;

/// <summary>
/// The handover (round 23): the home's assets found from the model and tagged; each kept up by the tasks
/// the law, the standards and the makers ask, in England and the US; the maintenance plan run from the
/// day each was put in and the work recorded; lives, warranties and replacements; all of it saved, and
/// written out to COBie, to IFC4 and as the operation and maintenance manual and the Home User Guide.
/// </summary>
[Collection("Wpf")]
public class HandoverTests
{
    private static ComponentType Form(BimDocument document, ComponentForm form) => document.TypesOf<ComponentType>().First(type => type.Form == form);

    private static Component Put(BimDocument document, ComponentForm form, double x, double y, Guid? level = null)
    {
        var family = Form(document, form);
        var component = new Component
        {
            TypeId = family.Id, TypeKind = family.Kind, LevelId = level ?? document.Levels[0].Id, Location = new Point2D(x, y), Elevation = family.DefaultElevation
        };
        document.Add(component);
        return component;
    }

    /// <summary>A 6 by 4 m home of one room, its kitchen: a door and a window in its walls, a boiler, two smoke alarms, a carbon monoxide alarm, a consumer unit, a basin - and a sofa.</summary>
    private static (BimDocument Document, List<Wall> Walls, Dictionary<string, Element> Things) Home(bool us = false)
    {
        var document = BimDocument.CreateDefault();
        if (us) document.ProjectInformation.ClearanceStandard = ClearanceStandard.Irc;
        var level = document.Levels[0].Id;
        var outside = document.TypesOf<WallType>().First(type => type.Function == WallFunction.Exterior);
        var corners = new[] { new Point2D(0, 0), new Point2D(6000, 0), new Point2D(6000, 4000), new Point2D(0, 4000) };
        var walls = new List<Wall>();
        for (var i = 0; i < 4; i++)
        {
            var wall = new Wall { Start = corners[i], End = corners[(i + 1) % 4], TypeId = outside.Id, LevelId = level, UnconnectedHeight = 2700 };
            document.Add(wall);
            walls.Add(wall);
        }

        document.Add(new Room { LevelId = level, Location = new Point2D(3000, 2000), Name = "Kitchen", Number = "1" });
        var door = new Door { TypeId = document.TypesOf<DoorType>().First(type => type.Function == DoorFunction.Exterior).Id, LevelId = level, HostWallId = walls[0].Id, DistanceAlongWall = 1500 };
        document.Add(door);
        var window = new BimWindow { TypeId = document.TypesOf<WindowType>().First().Id, LevelId = level, HostWallId = walls[2].Id, DistanceAlongWall = 3000 };
        document.Add(window);
        var things = new Dictionary<string, Element>
        {
            ["door"] = door, ["window"] = window,
            ["boiler"] = Put(document, ComponentForm.Boiler, 500, 3600),
            ["smoke1"] = Put(document, ComponentForm.SmokeAlarm, 1500, 2000),
            ["smoke2"] = Put(document, ComponentForm.SmokeAlarm, 4500, 2000),
            ["co"] = Put(document, ComponentForm.CarbonMonoxideAlarm, 1200, 3000),
            ["board"] = Put(document, ComponentForm.ConsumerUnit, 5800, 300),
            ["basin"] = Put(document, ComponentForm.Basin, 3000, 3600),
            ["sofa"] = Put(document, ComponentForm.Sofa, 3000, 1200)
        };
        return (document, walls, things);
    }

    private static Asset Of(BimDocument document, Element element) => Assets.Of(document).Single(asset => asset.Element == element);

    [Fact]
    public void TheHomesAssetsAreFoundTaggedAndPlaced()
    {
        var (document, _, things) = Home();
        var assets = Assets.Of(document);

        // What is kept up - not the sofa.
        Assert.DoesNotContain(assets, asset => asset.Element == things["sofa"]);
        Assert.Equal(AssetKind.GasBoiler, Of(document, things["boiler"]).Kind);
        Assert.Equal(AssetKind.Door, Of(document, things["door"]).Kind);
        Assert.Equal(AssetKind.Window, Of(document, things["window"]).Kind);

        // Tagged by kind and number, in order along the storey; the room each is in found.
        Assert.Equal("SA-01", Of(document, things["smoke1"]).Tag);
        Assert.Equal("SA-02", Of(document, things["smoke2"]).Tag);
        Assert.Equal("BLR-01", Of(document, things["boiler"]).Tag);
        Assert.Equal("1 Kitchen", Of(document, things["boiler"]).Room);
        Assert.Equal("1 Kitchen", Of(document, things["window"]).Room);

        // A mark is its tag; one only a number - as doors and windows are numbered - takes its kind's letters.
        things["boiler"].Mark = "B1";
        Assert.Equal("B1", Of(document, things["boiler"]).Tag);
        things["window"].Mark = "1";
        Assert.Equal("W-1", Of(document, things["window"]).Tag);
        things["window"].Mark = string.Empty;

        // Tag Assets writes the rest in, as one step to undo.
        var history = new UndoStack();
        history.Execute(Assets.TagCommand(document)!);
        Assert.Equal("SA-01", things["smoke1"].Mark);
        Assert.Equal("B1", things["boiler"].Mark);
        Assert.Null(Assets.TagCommand(document));
        history.Undo();
        Assert.Equal(string.Empty, things["smoke1"].Mark);

        // What a handover still wants of it; filled in through its parameters.
        var boiler = Of(document, things["boiler"]);
        Assert.Contains("serial number", Assets.Missing(document, boiler));
        Assert.DoesNotContain("serial number", Assets.Missing(document, Of(document, things["window"])));
        var serial = things["boiler"].GetInstanceParameters(document).Single(parameter => parameter.Definition == AssetParameters.SerialNumber);
        Assert.True(serial.TrySet("GC 47-311-01 / 2611"));
        var installed = things["boiler"].GetInstanceParameters(document).Single(parameter => parameter.Definition == AssetParameters.InstalledOn);
        Assert.True(installed.TrySet("2026-11-03"));
        Assert.Equal(new DateOnly(2026, 11, 3), things["boiler"].Asset.InstalledOn);

        // A day as England writes it is read on a computer set to the US's way, and nonsense is no day.
        Assert.Equal(new DateOnly(2026, 1, 15), Assets.ParseDate("15/01/2026"));
        Assert.Null(Assets.ParseDate("next Tuesday"));
        Assert.Equal("GC 47-311-01 / 2611", things["boiler"].Asset.SerialNumber);

        // The type's model, warranty and life, in its type properties; a copy is another asset.
        var type = (ElementType)Form(document, ComponentForm.Boiler);
        Assert.Contains(type.AllTypeParameters(document), parameter => parameter.Definition == AssetParameters.ModelNumber);
        Assert.DoesNotContain(document.TypesOf<WallType>().First().AllTypeParameters(document), parameter => parameter.Definition == AssetParameters.ModelNumber);
        var copy = ElementCopy.Clone(things["boiler"])!;
        Assert.True(copy.Asset.IsEmpty);
    }

    [Fact]
    public void EachIsKeptUpAsTheLawTheStandardsAndTheMakersAsk()
    {
        var (document, _, things) = Home();
        IReadOnlyList<MaintenanceTask> Tasks(string thing) => MaintenanceLibrary.For(document, Of(document, things[thing]));

        // A gas boiler: serviced yearly as its maker asks, its pressure looked at monthly.
        var service = Assert.Single(Tasks("boiler"), task => task.Id == "boiler-service");
        Assert.Equal(Interval.Years(1), service.Every);
        Assert.Equal(MaintenanceBasis.Maker, service.Basis);
        Assert.Contains("Gas Safe", service.By);
        Assert.DoesNotContain(Tasks("boiler"), task => task.Id == "gas-safety-check");

        // Smoke alarms tested monthly by BS 5839-6 Table 3; RCDs six-monthly by BS 7671 514.12.2; the wiring every 10 years by Guidance Note 3.
        var test = Assert.Single(Tasks("smoke1"), task => task.Id == "alarm-test");
        Assert.Equal(Interval.Months(1), test.Every);
        Assert.Contains("BS 5839-6:2019", test.Source);
        Assert.Equal(Interval.Months(6), Tasks("board").Single(task => task.Id == "rcd-test").Every);
        Assert.Contains("514.12.2", Tasks("board").Single(task => task.Id == "rcd-test").Source);
        Assert.Equal(Interval.Years(10), Tasks("board").Single(task => task.Id == "eicr").Every);
        Assert.Equal(Interval.Weeks(1), Tasks("co").Single(task => task.Id == "co-test").Every);

        // Let: the landlord's duties - the gas safety check yearly, the wiring every 5 years, the alarms at each new tenancy.
        document.Handover.Let = true;
        var gas = Assert.Single(Tasks("boiler"), task => task.Id == "gas-safety-check");
        Assert.Equal(MaintenanceBasis.Law, gas.Basis);
        Assert.Contains("reg. 36", gas.Source);
        var eicr = Tasks("board").Single(task => task.Id == "eicr");
        Assert.Equal(Interval.Years(5), eicr.Every);
        Assert.Equal(MaintenanceBasis.Law, eicr.Basis);
        var tenancy = Assert.Single(Tasks("smoke1"), task => task.Id == "alarm-tenancy");
        Assert.Null(tenancy.Every);
        Assert.Contains("tenancy", tenancy.When);

        // In the US: GFCIs tested monthly, smoke alarms replaced at 10 years by NFPA 72, no landlord's duties of England's.
        var (us, _, inUs) = Home(us: true);
        us.Handover.Let = true;
        var board = MaintenanceLibrary.For(us, Assets.Of(us).Single(asset => asset.Element == inUs["board"]));
        Assert.Equal(Interval.Months(1), Assert.Single(board).Every);
        Assert.Equal("gfci-test", board[0].Id);
        var replace = MaintenanceLibrary.For(us, Assets.Of(us).Single(asset => asset.Element == inUs["smoke1"])).Single(task => task.Id == "alarm-replace");
        Assert.Equal(MaintenanceBasis.Standard, replace.Basis);
        Assert.Contains("NFPA 72, 10.4.6", replace.Source);
        Assert.DoesNotContain(MaintenanceLibrary.For(us, Assets.Of(us).Single(asset => asset.Element == inUs["boiler"])), task => task.Id == "gas-safety-check");
        Assert.Equal("Panelboard", Assets.Describe(AssetKind.ConsumerUnit, true));
    }

    [Fact]
    public void FireDoorsInFlatsOverElevenMetresAreCheckedByRegulationTen()
    {
        var (document, walls, _) = Home();
        var fireType = document.TypesOf<DoorType>().First();
        fireType.FireRating = "FD30S";
        var door = new Door { TypeId = fireType.Id, LevelId = document.Levels[0].Id, HostWallId = walls[1].Id, DistanceAlongWall = 2000 };
        document.Add(door);
        var asset = Of(document, door);
        Assert.Equal(AssetKind.FireDoor, asset.Kind);
        Assert.Equal(Interval.Months(6), MaintenanceLibrary.For(document, asset).Single(task => task.Id == "fire-door-check").Every);
        Assert.DoesNotContain(MaintenanceLibrary.For(document, asset), task => task.Id == "fire-door-reg10");

        // Flats with a storey with rooms over 11 m up: the responsible person's three-monthly checks.
        document.Handover.Flats = true;
        var high = new Core.Datums.Level { Name = "Level 5", Elevation = 12000 };
        document.Levels.Add(high);
        document.Add(new Room { LevelId = high.Id, Location = new Point2D(3000, 2000), Name = "Flat 51" });
        Assert.True(MaintenanceLibrary.Over11Metres(document));
        var reg10 = MaintenanceLibrary.For(document, asset).Single(task => task.Id == "fire-door-reg10");
        Assert.Equal(MaintenanceBasis.Law, reg10.Basis);
        Assert.Equal(Interval.Months(3), reg10.Every);
        Assert.False(Assets.IsFireRated("None"));
        Assert.True(Assets.IsFireRated("30 min"));
    }

    [Fact]
    public void ThePlanRunsFromWhenEachWasPutInAndWhatWasDone()
    {
        var (document, _, things) = Home();
        var handed = new DateOnly(2026, 1, 15);
        document.Handover.HandedOver = handed;
        var today = new DateOnly(2026, 11, 3);

        DueTask Due(Element element, string task) => MaintenancePlan.Of(document, today).Single(item => item.Asset.Element == element && item.Task.Id == task);

        // From the handover, a year on - and the monthly alarm test long overdue, never done.
        Assert.Equal(new DateOnly(2027, 1, 15), Due(things["boiler"], "boiler-service").Next);
        Assert.True(Due(things["smoke1"], "alarm-test").OverdueOn(today));
        Assert.Contains(MaintenancePlan.Overdue(document, today), item => item.Task.Id == "alarm-test");

        // An asset put in later runs from its own day.
        things["board"].Asset = things["board"].Asset with { InstalledOn = new DateOnly(2026, 6, 1) };
        Assert.Equal(new DateOnly(2026, 12, 1), Due(things["board"], "rcd-test").Next);

        // Recorded done, it is due its interval on from then - one step to undo.
        var history = new UndoStack();
        history.Record(MaintenancePlan.Record(document, new[] { Due(things["smoke1"], "alarm-test") }, today, "J Smith", "Both sounded"));
        Assert.Equal(new DateOnly(2026, 12, 3), Due(things["smoke1"], "alarm-test").Next);
        Assert.Equal(today, Due(things["smoke1"], "alarm-test").Last);
        Assert.False(Due(things["smoke1"], "alarm-test").OverdueOn(today));
        history.Undo();
        Assert.Empty(document.Handover.Done);
        history.Redo();
        Assert.Single(document.Handover.Done);

        // A year ahead: the monthly test eleven times more - done today - the weekly one 52 or 53.
        var year = MaintenancePlan.Between(document, today, today.AddYears(1).AddDays(-1), today);
        Assert.Equal(11, year.Count(entry => entry.Item.Asset.Element == things["smoke1"] && entry.Item.Task.Id == "alarm-test"));
        Assert.InRange(year.Count(entry => entry.Item.Task.Id == "co-test"), 52, 53);

        // Overdue maintenance is a warning once the home is handed over.
        Assert.Contains(Warnings.Of(document), warning => warning.Heading == "Maintenance overdue");
    }

    [Fact]
    public void LivesWarrantiesAndReplacements()
    {
        var (document, _, things) = Home();
        document.Handover.HandedOver = new DateOnly(2026, 1, 15);
        var boiler = Of(document, things["boiler"]);

        // The study's 21 years for a gas boiler, until its type says otherwise.
        var life = Assets.LifeOf(document, boiler);
        Assert.Equal(21, life.Years);
        Assert.Contains("NAHB", life.Source);
        Assert.Equal(new DateOnly(2047, 1, 15), Assets.ReplaceBy(document, boiler));
        Assert.Equal(30, Assets.LifeOf(document, Of(document, things["door"])).Years);
        things["door"].TypeId = document.TypesOf<DoorType>().First(each => each.Name.StartsWith("Front Door", StringComparison.Ordinal)).Id;
        Assert.True(Assets.LifeOf(document, Of(document, things["door"])).Lifetime);
        var type = boiler.Type!;
        type.ExpectedLife = 15;
        type.PartsWarranty = 10;
        Assert.Equal(15, Assets.LifeOf(document, boiler).Years);
        Assert.Equal(new DateOnly(2036, 1, 15), Assets.WarrantyEnds(document, boiler));

        // Its replacement falls in 2041, at its type's cost.
        type.Cost = 2400;
        var replacements = MaintenancePlan.Replacements(document, 2026, 30);
        var due = replacements.Single(year => year.Year == 2041);
        Assert.Contains(due.Assets, entry => entry.Asset.Element == things["boiler"] && entry.Cost == 2400);
        Assert.Contains(replacements, year => year.Year == 2036 && year.Assets.Any(entry => entry.Asset.Kind == AssetKind.SmokeAlarm));
    }

    [Fact]
    public void ItIsSavedAndWrittenToCobieAndIfc()
    {
        var (document, _, things) = Home();
        document.Handover.HandedOver = new DateOnly(2026, 1, 15);
        document.Handover.Let = true;
        var type = Form(document, ComponentForm.Boiler);
        type.Manufacturer = "warranty@boilers.example";
        type.ModelNumber = "Combi 30i";
        type.PartsWarranty = 10;
        type.LabourWarranty = 2;
        type.ExpectedLife = 18;
        things["boiler"].Asset = new AssetRecord("SN-0042", "5012345678900", new DateOnly(2026, 1, 10));
        document.Handover.Done.Add(new MaintenanceRecord(Guid.NewGuid(), things["boiler"].Id, "boiler-service", new DateOnly(2026, 10, 1), "Gas Safe 123456", "Combustion fine"));

        var path = Path.Combine(Path.GetTempPath(), $"handover-{Guid.NewGuid():N}.bimx");
        try
        {
            ProjectFile.Save(document, path);
            var opened = ProjectFile.Load(path);
            var boiler = opened.FindElement(things["boiler"].Id)!;
            Assert.Equal("SN-0042", boiler.Asset.SerialNumber);
            Assert.Equal("5012345678900", boiler.Asset.BarCode);
            Assert.Equal(new DateOnly(2026, 1, 10), boiler.Asset.InstalledOn);
            var openedType = opened.ElementTypes.Single(each => each.Id == type.Id);
            Assert.Equal("Combi 30i", openedType.ModelNumber);
            Assert.Equal(10, openedType.PartsWarranty);
            Assert.Equal(18, openedType.ExpectedLife);
            Assert.Equal(new DateOnly(2026, 1, 15), opened.Handover.HandedOver);
            Assert.True(opened.Handover.Let);
            Assert.Equal("Combustion fine", Assert.Single(opened.Handover.Done).Notes);
        }
        finally
        {
            File.Delete(path);
        }

        // COBie: the type's model, warranty and life; the component's serial number and dates; a job for each task, its trade a resource.
        var sheets = CobieExport.Build(document, new CobieOptions { ContactEmail = "design@example.co.uk", CreatedOn = new DateTime(2026, 11, 3, 9, 0, 0) });
        string Cell(XlsxSheet sheet, object?[] row, string column) => row[sheet.Columns.ToList().FindIndex(each => each.Heading.TrimStart('*', '>', '#', ' ') == column)]?.ToString() ?? string.Empty;
        var types = sheets.Single(sheet => sheet.Name == "Type");
        var boilerType = types.Rows.Single(row => Cell(types, row, "ModelNumber") == "Combi 30i");
        Assert.Equal("10", Cell(types, boilerType, "WarrantyDurationParts"));
        Assert.Equal("18", Cell(types, boilerType, "ExpectedLife"));
        Assert.Equal("warranty@boilers.example", Cell(types, boilerType, "WarrantyGuarantorParts"));
        var components = sheets.Single(sheet => sheet.Name == "Component");
        var row = components.Rows.Single(each => Cell(components, each, "SerialNumber") == "SN-0042");
        Assert.Equal("2026-01-10T00:00:00", Cell(components, row, "InstallationDate"));
        Assert.Equal("BLR-01", Cell(components, row, "TagNumber"));
        var jobs = sheets.Single(sheet => sheet.Name == "Job");
        var gasCheck = jobs.Rows.Single(each => Cell(jobs, each, "Name") == "Gas safety check");
        Assert.Equal("1", Cell(jobs, gasCheck, "Frequency"));
        Assert.Equal("year", Cell(jobs, gasCheck, "FrequencyUnit"));
        Assert.Contains("reg. 36", Cell(jobs, gasCheck, "Description"));
        var resources = sheets.Single(sheet => sheet.Name == "Resource");
        Assert.Contains(resources.Rows, each => Cell(resources, each, "Name") == "A Gas Safe registered engineer");

        // IFC4: the maker, serial number, warranty and life on the product, it an IfcAsset, and the plan's tasks recurring.
        using var ifc = IfcExport.Build(document);
        var product = ifc.Instances.OfType<IIfcProduct>().Single(each => each.GlobalId == Xbim.Ifc4.UtilityResource.IfcGloballyUniqueId.ConvertToBase64(things["boiler"].Id));
        string? Value(string set, string name) => product.IsDefinedBy.Select(relation => relation.RelatingPropertyDefinition).OfType<IIfcPropertySet>()
            .Where(each => each.Name == set).SelectMany(each => each.HasProperties).OfType<IIfcPropertySingleValue>().FirstOrDefault(each => each.Name == name)?.NominalValue?.ToString();
        Assert.Equal("SN-0042", Value("Pset_ManufacturerOccurrence", "SerialNumber"));
        Assert.Equal("Combi 30i", Value("Pset_ManufacturerTypeInformation", "ModelReference"));
        Assert.Equal("P120M", Value("Pset_Warranty", "WarrantyPeriod"));
        Assert.Equal("P216M", Value("Pset_ServiceLife", "ServiceLifeDuration"));
        Assert.Contains(ifc.Instances.OfType<IIfcAsset>(), asset => asset.Identification == "BLR-01");
        var plan = ifc.Instances.OfType<IIfcWorkSchedule>().Single(each => each.Name == "Maintenance plan");
        var tasks = ifc.Instances.OfType<IIfcTask>().Where(task => task.PredefinedType == IfcTaskTypeEnum.MAINTENANCE).ToList();
        Assert.Contains(tasks, task => task.Name == "Gas safety check" && task.TaskTime is IIfcTaskTimeRecurring { Recurrence.RecurrenceType: IfcRecurrenceTypeEnum.YEARLY_BY_DAY_OF_MONTH });
        Assert.Contains(tasks, task => task.Name == "Test the alarm" && task.TaskTime is IIfcTaskTimeRecurring { Recurrence.RecurrenceType: IfcRecurrenceTypeEnum.MONTHLY_BY_DAY_OF_MONTH });
        Assert.Contains(ifc.Instances.OfType<IIfcRelAssignsToControl>(), relation => relation.RelatingControl == plan);
        var alarms = tasks.Single(task => task.Name == "Test the alarm" && task.Identification == "alarm-test");
        Assert.Equal(2, ifc.Instances.OfType<IIfcRelAssignsToProcess>().Single(relation => relation.RelatingProcess == alarms).RelatedObjects.Count());
    }

    private static T OnUiThread<T>(Func<T> work)
    {
        T result = default!;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (Application.Current is null) new App().InitializeComponent();
                result = work();
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Exception("It failed on the UI thread.", failure);
        return result;
    }

    [Fact]
    public void TheManualAndTheGuideAreWrittenForTheHome()
    {
        var (pages, guidePages, path) = OnUiThread(() =>
        {
            var (document, _, _) = Home();
            document.ProjectInformation.Name = "Plot 7";
            document.Handover.HandedOver = new DateOnly(2026, 1, 15);
            var manual = HandoverReport.Manual(document, "test", new DateTime(2026, 11, 3))!;
            var guide = HandoverReport.Guide(document, "test", new DateTime(2026, 11, 3))!;
            var file = Path.Combine(Path.GetTempPath(), $"om-{Guid.NewGuid():N}.pdf");
            HandoverReport.ToPdf(manual, file);
            Assert.Null(HandoverReport.WhyNot(document));
            Assert.NotNull(HandoverReport.WhyNot(BimDocument.CreateDefault()));
            return (manual.PageCount, guide.PageCount, file);
        });
        try
        {
            Assert.InRange(guidePages, 1, pages - 1);
            Assert.True(pages >= 5);
            var text = File.ReadAllBytes(path);
            Assert.True(text.Length > 20_000);
            Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(text, 0, 4));
        }
        finally
        {
            File.Delete(path);
        }

        var named = BimDocument.CreateDefault();
        named.ProjectInformation.Name = "Plot 7";
        Assert.Equal("Plot 7 - Home User Guide.pdf", HandoverReport.SuggestedFileName(named, guide: true));
    }
}
