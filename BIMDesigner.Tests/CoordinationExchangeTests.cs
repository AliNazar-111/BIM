using System.IO.Compression;
using System.Xml.Linq;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Coordination;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Infrastructure.Interoperability;
using Xbim.Ifc4.Kernel;
using Xbim.Ifc4.ProcessExtension;
using Xbim.Ifc4.UtilityResource;

namespace BIMDesigner.Tests;

/// <summary>Clashes as BCF and reports, the programme as Microsoft Project XML and in IFC4, the cost plan as a spreadsheet.</summary>
public class CoordinationExchangeTests
{
    private static (BimDocument Document, ClashTest Test) Clashing()
    {
        var document = ConstructionTests.House();
        var desk = document.Elements.OfType<Component>().Single();
        var wall = document.Walls.First(each => each.LevelId == document.Levels[0].Id);
        desk.Location = new Point2D(3000, 200);
        var test = document.ClashTests.Single(each => each.Name.Contains("Fittings and furniture through walls"));
        ClashDetective.Run(document, test, new DateTime(2026, 10, 21, 9, 0, 0));
        _ = wall;
        return (document, test);
    }

    [Fact]
    public void ClashesGoOutAsBcfTopicsTheIfcOpensAgainst()
    {
        var (document, test) = Clashing();
        var clash = Assert.Single(test.Results);
        clash.AssignedTo = "architect@example.com";
        clash.Comments.Add(new ClashComment(new DateTime(2026, 10, 21, 10, 0, 0), "AN", "Pull the desk back."));

        using var stream = new MemoryStream();
        ClashReports.WriteBcf(stream, document, document.ClashTests, "AN", _ => new byte[] { 0x89, 0x50, 0x4E, 0x47 });
        stream.Position = 0;
        using var zip = new ZipArchive(stream);
        var names = zip.Entries.Select(entry => entry.FullName).ToList();
        Assert.Contains("bcf.version", names);
        Assert.Contains($"{clash.Id}/markup.bcf", names);
        Assert.Contains($"{clash.Id}/viewpoint.bcfv", names);
        Assert.Contains($"{clash.Id}/snapshot.png", names);

        Assert.Equal("2.1", XDocument.Load(zip.GetEntry("bcf.version")!.Open()).Root!.Attribute("VersionId")!.Value);
        var markup = XDocument.Load(zip.GetEntry($"{clash.Id}/markup.bcf")!.Open()).Root!;
        var topic = markup.Element("Topic")!;
        Assert.Equal(clash.Id.ToString(), topic.Attribute("Guid")!.Value);
        Assert.Equal("New", topic.Attribute("TopicStatus")!.Value);
        Assert.Equal(new[] { "Title", "Priority", "Index", "Labels", "CreationDate", "CreationAuthor", "AssignedTo", "Description" }, topic.Elements().Select(e => e.Name.LocalName));
        Assert.Equal("Pull the desk back.", markup.Element("Comment")!.Element("Comment")!.Value);

        // The two elements by the GUIDs the IFC gives them.
        var viewpoint = XDocument.Load(zip.GetEntry($"{clash.Id}/viewpoint.bcfv")!.Open()).Root!;
        var guids = viewpoint.Element("Components")!.Element("Selection")!.Elements("Component").Select(component => component.Attribute("IfcGuid")!.Value).ToList();
        using var model = IfcExport.Build(document);
        var products = model.Instances.OfType<IfcRoot>().Select(root => root.GlobalId.ToString()).ToHashSet();
        Assert.Equal(2, guids.Count);
        Assert.All(guids, guid => Assert.Contains(guid, products));
        Assert.InRange(double.Parse(viewpoint.Element("PerspectiveCamera")!.Element("FieldOfView")!.Value), 45, 60);
    }

    [Fact]
    public void ClashesGoOutAsAReportAndASpreadsheet()
    {
        var (document, test) = Clashing();
        var html = ClashReports.Html(document, document.ClashTests);
        Assert.Contains(test.Name, html);
        Assert.Contains("Clash1", html);
        Assert.Contains("runs", html);

        using var stream = new MemoryStream();
        ClashReports.WriteXlsx(stream, document, document.ClashTests);
        stream.Position = 0;
        using var zip = new ZipArchive(stream);
        Assert.NotNull(zip.GetEntry("xl/worksheets/sheet1.xml"));
    }

    [Fact]
    public void TheProgrammeGoesToMicrosoftProjectAndComesBack()
    {
        var document = ConstructionTests.House();
        ConstructionPlanning.Generate(document, new DateOnly(2026, 11, 2));
        var schedule = document.Construction;
        schedule.Holidays.Add(new DateOnly(2026, 12, 25));
        schedule.Tasks[3].Predecessors[0] = new TaskLink(schedule.Tasks[3].Predecessors[0].Predecessor, 2);
        schedule.Tasks[1].PercentComplete = 50;

        using var stream = new MemoryStream();
        MsProjectXml.Write(stream, document);
        stream.Position = 0;
        var xml = XDocument.Load(stream);
        XNamespace ns = "http://schemas.microsoft.com/project";
        Assert.Equal(schedule.Tasks.Count, xml.Root!.Element(ns + "Tasks")!.Elements(ns + "Task").Count());
        Assert.Equal("PT48H0M0S", xml.Root.Element(ns + "Tasks")!.Elements(ns + "Task")
            .Single(task => task.Element(ns + "Name")!.Value == schedule.Tasks.First(each => each.Duration == 6).Name).Element(ns + "Duration")!.Value);

        // Into a project with none: the same tasks, durations, links and so the same finish.
        stream.Position = 0;
        var other = ConstructionTests.House();
        Assert.Equal(schedule.Tasks.Count, MsProjectXml.Read(stream, other));
        var read = other.Construction;
        Assert.Equal(schedule.Tasks.Select(task => (task.Name, task.Duration, task.Predecessors.Count)), read.Tasks.Select(task => (task.Name, task.Duration, task.Predecessors.Count)));
        Assert.Equal(2, read.Tasks[3].Predecessors[0].Lag);
        Assert.Equal(50, read.Tasks[1].PercentComplete);
        Assert.Contains(new DateOnly(2026, 12, 25), read.Holidays);
        Assert.Equal(ConstructionPlanning.Finish(schedule), ConstructionPlanning.Finish(read));

        // Into the project it came from: each task keeps what it builds.
        stream.Position = 0;
        var rules = schedule.Tasks.Select(task => task.Categories.Count).ToList();
        MsProjectXml.Read(stream, document);
        Assert.Equal(rules, schedule.Tasks.Select(task => task.Categories.Count));
    }

    [Fact]
    public void TheProgrammeGoesIntoIfc4()
    {
        var document = ConstructionTests.House();
        ConstructionPlanning.Generate(document, new DateOnly(2026, 11, 2));
        using var model = IfcExport.Build(document);

        var plan = Assert.Single(model.Instances.OfType<IfcWorkSchedule>());
        var tasks = model.Instances.OfType<IfcTask>().ToList();
        Assert.Equal(document.Construction.Tasks.Count, tasks.Count);
        Assert.Equal(document.Construction.Tasks.Sum(task => task.Predecessors.Count), model.Instances.OfType<IfcRelSequence>().Count());
        Assert.Contains(model.Instances.OfType<IfcRelAssignsToControl>(), relation => relation.RelatingControl == plan && relation.RelatedObjects.Count == tasks.Count);

        // The ground slab's task builds the slab.
        var slab = document.Elements.OfType<Floor>().Single(floor => floor.LevelId == document.Levels[0].Id);
        var slabTask = tasks.Single(task => task.Name.ToString()!.EndsWith("Ground slab"));
        var built = model.Instances.OfType<IfcRelAssignsToProcess>().Single(relation => relation.RelatingProcess == slabTask);
        Assert.Contains(built.RelatedObjects.OfType<IfcRoot>(), root => root.GlobalId.ToString() == IfcGloballyUniqueId.ConvertToBase64(slab.Id).ToString());
        Assert.True(slabTask.TaskTime!.IsCritical);
    }

    [Fact]
    public void TheCostPlanGoesOutAsASpreadsheet()
    {
        var document = ConstructionTests.House();
        ConstructionPlanning.Generate(document, new DateOnly(2026, 11, 2));
        using var stream = new MemoryStream();
        CostReports.WriteXlsx(stream, document);
        stream.Position = 0;
        using var zip = new ZipArchive(stream);
        Assert.NotNull(zip.GetEntry("xl/worksheets/sheet3.xml"));
        var shared = new StreamReader(zip.GetEntry("xl/sharedStrings.xml")?.Open() ?? zip.GetEntry("xl/worksheets/sheet1.xml")!.Open()).ReadToEnd();
        Assert.Contains("External walls", shared);
    }
}
