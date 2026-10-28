using System.IO.Compression;
using System.Xml.Linq;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Coordination;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;
using BIMDesigner.Infrastructure.Interoperability;
using BIMDesigner.Infrastructure.Serialization;
using Xbim.Ifc4.UtilityResource;

namespace BIMDesigner.Tests;

/// <summary>
/// Issues and BCF: an element's GUID as IFC writes it, a view's camera as BCF has it, issues out
/// as BCF 2.1 and 3.0 and back, files as other programs write them read, the coordinates of a model
/// brought in from a national grid given back, issues merged, and all kept in the project.
/// </summary>
public class BcfTests
{
    [Fact]
    public void AnElementsGuidIsWrittenAsIfcWritesIt()
    {
        var random = new Random(3);
        for (var i = 0; i < 300; i++)
        {
            var bytes = new byte[16];
            random.NextBytes(bytes);
            var id = new Guid(bytes);
            var compressed = IfcGuids.Compress(id);
            Assert.Equal(IfcGloballyUniqueId.ConvertToBase64(id).ToString(), compressed);
            Assert.True(IfcGuids.TryExpand(compressed, out var back));
            Assert.Equal(id, back);
        }

        Assert.Equal("0000000000000000000000", IfcGuids.Compress(Guid.Empty));
        Assert.False(IfcGuids.TryExpand("not a guid", out _));
        Assert.False(IfcGuids.TryExpand("4ZZZZZZZZZZZZZZZZZZZZZ", out _));
        Assert.False(IfcGuids.TryExpand("0000000000000000000#00", out _));
    }

    [Fact]
    public void AViewsCameraIsTakenAsBcfHasItAndBack()
    {
        // 45° across a picture 16:9: 26.99° up it.
        var camera = new Camera3D(new Point3D(-8000, -12000, 6000), new Point3D(6000, 4500, 1500)) { FieldOfView = 45 };
        var bcf = IssueCamera.From(camera, 16.0 / 9);
        Assert.Equal(2 * Math.Atan(Math.Tan(22.5 * Math.PI / 180) * 9 / 16) * 180 / Math.PI, bcf.FieldOfView, 9);
        Assert.Equal(1, Math.Sqrt(bcf.Direction.X * bcf.Direction.X + bcf.Direction.Y * bcf.Direction.Y + bcf.Direction.Z * bcf.Direction.Z), 9);
        var back = bcf.ToCamera(camera.Distance);
        Assert.Equal(45, back.FieldOfView, 9);
        Assert.Equal(camera.Target.X, back.Target.X, 6);
        Assert.Equal(camera.Target.Z, back.Target.Z, 6);

        // Orthographic: how tall a slice it shows, and as wide again as the view was.
        var ortho = new Camera3D(new Point3D(0, -30000, 2000), new Point3D(0, 0, 2000), Perspective: false) { FieldOfView = 30 };
        var flat = IssueCamera.From(ortho, 2);
        Assert.Equal(ortho.Width / 2, flat.ViewHeight, 6);
        Assert.Equal(ortho.Width, flat.ToCamera(12000).Width, 6);
    }

    /// <summary>A project with a wall and a door, and an issue about them with two views and what has been said.</summary>
    private static (BimDocument Document, Issue Issue, Wall Wall, Door Door) Raised()
    {
        var document = BimDocument.CreateDefault();
        var level = document.Levels[0];
        var wall = new Wall { Start = new Point2D(0, 0), End = new Point2D(6000, 0), TypeId = document.TypesOf<WallType>().First().Id, LevelId = level.Id };
        document.Add(wall);
        var door = new Door { HostWallId = wall.Id, TypeId = document.TypesOf<DoorType>().First().Id, LevelId = level.Id, DistanceAlongWall = 3000 };
        document.Add(door);

        var issue = new Issue
        {
            Title = "Door clashes with the radiator", Description = "Move the radiator 300 mm east.", Type = "Clash", Status = "In Progress", Priority = "High",
            Author = "an@example.com", Created = new DateTime(2026, 10, 1, 9, 30, 0, DateTimeKind.Utc), ModifiedBy = "bo@example.com", Modified = new DateTime(2026, 10, 2, 14, 0, 0, DateTimeKind.Utc),
            Due = new DateTime(2026, 11, 1, 17, 0, 0, DateTimeKind.Utc), AssignedTo = "mep@example.com", Stage = "Stage 4", Index = 7
        };
        issue.Labels.AddRange(new[] { "Ground floor", "Heating" });
        issue.ReferenceLinks.Add("https://example.com/issue/7");
        issue.RelatedTopics.Add(Guid.Parse("c0ffee00-0000-4000-8000-000000000001"));

        var camera = new Camera3D(new Point3D(-4000, -9000, 4500), new Point3D(3000, 0, 1000)) { FieldOfView = 50 };
        var first = Issues.ViewOf(camera, 16.0 / 9, new[] { wall.Id, door.Id }, new Box3D(new Point3D(-500, -1500, -200), new Point3D(6500, 1500, 3200)), new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 });
        first.Colours.Add(new IssueColour("FFFF0000", new[] { IfcGuids.Compress(door.Id) }));
        var second = Issues.ViewOf(new Camera3D(new Point3D(3000, -20000, 1500), new Point3D(3000, 0, 1500), Perspective: false) { FieldOfView = 20 }, 1.5, Array.Empty<Guid>(), null, null);
        issue.Viewpoints.AddRange(new[] { first, second });
        issue.Comments.Add(new IssueComment { Date = new DateTime(2026, 10, 1, 9, 31, 0, DateTimeKind.Utc), Author = "an@example.com", Text = "See the view.", Viewpoint = first.Id });
        issue.Comments.Add(new IssueComment { Date = new DateTime(2026, 10, 2, 14, 0, 0, DateTimeKind.Utc), Author = "bo@example.com", Text = "On it." });
        document.Issues.Add(issue);
        return (document, issue, wall, door);
    }

    private static BcfRead RoundTrip(BimDocument document, string version, out ZipArchive zip)
    {
        var stream = new MemoryStream();
        BcfFile.Write(stream, document, document.Issues, version);
        stream.Position = 0;
        zip = new ZipArchive(new MemoryStream(stream.ToArray()));
        return BcfFile.Read(stream, document.ProjectInformation.SurveyOffset);
    }

    private static void AssertSame(Issue expected, Issue actual, bool clampedAngle)
    {
        Assert.Equal((expected.Id, expected.Title, expected.Description, expected.Type, expected.Status, expected.Priority), (actual.Id, actual.Title, actual.Description, actual.Type, actual.Status, actual.Priority));
        Assert.Equal((expected.Author, expected.Created, expected.ModifiedBy, expected.Modified, expected.Due), (actual.Author, actual.Created, actual.ModifiedBy, actual.Modified, actual.Due));
        Assert.Equal((expected.AssignedTo, expected.Stage, expected.Index), (actual.AssignedTo, actual.Stage, actual.Index));
        Assert.Equal(expected.Labels, actual.Labels);
        Assert.Equal(expected.ReferenceLinks, actual.ReferenceLinks);
        Assert.Equal(expected.RelatedTopics, actual.RelatedTopics);
        Assert.Equal(expected.Comments.Select(c => (c.Id, c.Date, c.Author, c.Text, c.Viewpoint)), actual.Comments.Select(c => (c.Id, c.Date, c.Author, c.Text, c.Viewpoint)));
        Assert.Equal(expected.Viewpoints.Count, actual.Viewpoints.Count);
        for (var i = 0; i < expected.Viewpoints.Count; i++)
        {
            var (a, b) = (expected.Viewpoints[i], actual.Viewpoints[i]);
            Assert.Equal(a.Id, b.Id);
            Assert.Equal(a.Selected, b.Selected);
            Assert.Equal(a.Snapshot, b.Snapshot);
            Assert.Equal(a.Box(), b.Box() is { } box ? new Box3D(Round(box.Min), Round(box.Max)) : null);
            Assert.Equal(a.Colours.Select(c => (c.Colour, string.Join(",", c.Components))), b.Colours.Select(c => (c.Colour, string.Join(",", c.Components))));
            var (x, y) = (a.Camera!.ToCamera(10000), b.Camera!.ToCamera(10000));
            Assert.Equal(x.Perspective, y.Perspective);
            Assert.Equal(x.Eye.X, y.Eye.X, 3);
            Assert.Equal(x.Eye.Y, y.Eye.Y, 3);
            Assert.Equal(x.Eye.Z, y.Eye.Z, 3);
            Assert.Equal(x.Target.X, y.Target.X, 3);
            if (!clampedAngle || !x.Perspective) Assert.Equal(x.FieldOfView, y.FieldOfView, 6);
        }
    }

    private static Point3D Round(Point3D p) => new(Math.Round(p.X, 6), Math.Round(p.Y, 6), Math.Round(p.Z, 6));

    [Fact]
    public void IssuesGoOutAsBcf21AndComeBackAsTheyWere()
    {
        var (document, issue, _, _) = Raised();
        var read = RoundTrip(document, "2.1", out var zip);
        Assert.Equal("2.1", read.Version);
        Assert.Empty(read.Notes);
        AssertSame(issue, Assert.Single(read.Issues), clampedAngle: true);

        // As 2.1 lays it out: comments and viewpoints beside the topic, each label an element of its own, the first view viewpoint.bcfv.
        var folder = issue.Id.ToString();
        var names = zip.Entries.Select(entry => entry.FullName).ToList();
        Assert.Contains($"{folder}/viewpoint.bcfv", names);
        Assert.Contains($"{folder}/snapshot.png", names);
        Assert.Contains($"{folder}/viewpoint-{issue.Viewpoints[1].Id}.bcfv", names);
        Assert.DoesNotContain("extensions.xml", names);
        var markup = XDocument.Load(zip.GetEntry($"{folder}/markup.bcf")!.Open()).Root!;
        Assert.Equal(new[] { "Topic", "Comment", "Comment", "Viewpoints", "Viewpoints" }, markup.Elements().Select(e => e.Name.LocalName));
        Assert.Equal(2, markup.Element("Topic")!.Elements("Labels").Count());

        // 2.1's field of view across the picture: 50°; the box as six planes facing out of it.
        var view = XDocument.Load(zip.GetEntry($"{folder}/viewpoint.bcfv")!.Open()).Root!;
        Assert.Equal(50, double.Parse(view.Element("PerspectiveCamera")!.Element("FieldOfView")!.Value, System.Globalization.CultureInfo.InvariantCulture), 6);
        Assert.Null(view.Element("PerspectiveCamera")!.Element("AspectRatio"));
        Assert.Equal(6, view.Element("ClippingPlanes")!.Elements("ClippingPlane").Count());
        Assert.NotNull(view.Element("Components")!.Element("ViewSetupHints"));
    }

    [Fact]
    public void IssuesGoOutAsBcf30AndComeBackAsTheyWere()
    {
        var (document, issue, _, _) = Raised();
        var read = RoundTrip(document, "3.0", out var zip);
        Assert.Equal("3.0", read.Version);
        AssertSame(issue, Assert.Single(read.Issues), clampedAngle: false);

        // As 3.0 lays it out: the lists the values come from, comments and viewpoints inside the topic, labels in a group.
        var names = zip.Entries.Select(entry => entry.FullName).ToList();
        Assert.Contains("extensions.xml", names);
        var extensions = XDocument.Load(zip.GetEntry("extensions.xml")!.Open()).Root!;
        Assert.Contains("In Progress", extensions.Element("TopicStatuses")!.Elements("TopicStatus").Select(e => e.Value));
        Assert.Contains("mep@example.com", extensions.Element("Users")!.Elements("User").Select(e => e.Value));
        var topic = XDocument.Load(zip.GetEntry($"{issue.Id}/markup.bcf")!.Open()).Root!.Element("Topic")!;
        Assert.Equal(2, topic.Element("Labels")!.Elements("Label").Count());
        Assert.Equal(2, topic.Element("Comments")!.Elements("Comment").Count());
        Assert.Equal(2, topic.Element("Viewpoints")!.Elements("ViewPoint").Count());

        // 3.0's field of view up the picture, with its aspect ratio; the visibility's hints inside it.
        var view = XDocument.Load(zip.GetEntry($"{issue.Id}/viewpoint.bcfv")!.Open()).Root!;
        var camera = view.Element("PerspectiveCamera")!;
        Assert.Equal(issue.Viewpoints[0].Camera!.FieldOfView, double.Parse(camera.Element("FieldOfView")!.Value, System.Globalization.CultureInfo.InvariantCulture), 6);
        Assert.Equal(16.0 / 9, double.Parse(camera.Element("AspectRatio")!.Value, System.Globalization.CultureInfo.InvariantCulture), 6);
        Assert.NotNull(view.Element("Components")!.Element("Visibility")!.Element("ViewSetupHints"));
        Assert.NotNull(view.Element("Components")!.Element("Coloring")!.Element("Color")!.Element("Components"));
    }

    [Fact]
    public void ABcfFileAsAnotherProgramWritesItIsRead()
    {
        // BCF 2.1 with a namespace or two, backslashes in its paths, a topic of its own and a view with no Index.
        var topic = Guid.Parse("5b0f6c84-3fd1-4f39-97e1-0b2c3a1d9a10");
        var view = Guid.Parse("8a6e2a0b-0bd8-4a5f-9d75-3e8e5f0d6c21");
        var wall = Guid.Parse("1a2b3c4d-1111-4222-8333-444455556666");
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Put(string path, string text)
            {
                using var writer = new StreamWriter(zip.CreateEntry(path).Open());
                writer.Write(text);
            }

            Put("bcf.version", "<?xml version=\"1.0\"?><Version VersionId=\"2.1\" xsi:noNamespaceSchemaLocation=\"version.xsd\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\"><DetailedVersion>2.1</DetailedVersion></Version>");
            Put($"{topic}\\markup.bcf", $"""
                <?xml version="1.0" encoding="utf-8"?>
                <Markup xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
                  <Header><File IfcProject="0M6o7Znnv7hxsbWgeu7oQq" isExternal="true"><Filename>House.ifc</Filename><Date>2026-09-30T10:00:00</Date></File></Header>
                  <Topic Guid="{topic}" TopicType="Error" TopicStatus="Active">
                    <Title>Wall too thin for fire separation</Title>
                    <Priority>Critical</Priority>
                    <Labels>Fire</Labels>
                    <Labels>Architecture</Labels>
                    <CreationDate>2026-09-30T11:15:00+02:00</CreationDate>
                    <CreationAuthor>checker@example.com</CreationAuthor>
                    <AssignedTo>architect@example.com</AssignedTo>
                    <Description>Needs 60 minutes.</Description>
                  </Topic>
                  <Comment Guid="11111111-2222-4333-8444-555555555555">
                    <Date>2026-09-30T11:16:00Z</Date><Author>checker@example.com</Author><Comment>Please look.</Comment><Viewpoint Guid="{view}"/>
                  </Comment>
                  <Viewpoints Guid="{view}"><Viewpoint>viewpoint.bcfv</Viewpoint><Snapshot>snapshot.jpg</Snapshot></Viewpoints>
                </Markup>
                """);
            Put($"{topic}\\viewpoint.bcfv", $"""
                <?xml version="1.0" encoding="utf-8"?>
                <VisualizationInfo Guid="{view}">
                  <Components>
                    <ViewSetupHints SpacesVisible="false" SpaceBoundariesVisible="false" OpeningsVisible="false" />
                    <Selection><Component IfcGuid="{IfcGuids.Compress(wall)}"><OriginatingSystem>Checker</OriginatingSystem></Component></Selection>
                    <Visibility DefaultVisibility="true" />
                    <Coloring><Color Color="FF0000"><Component IfcGuid="{IfcGuids.Compress(wall)}" /></Color></Coloring>
                  </Components>
                  <OrthogonalCamera>
                    <CameraViewPoint><X>3</X><Y>-20</Y><Z>1.5</Z></CameraViewPoint>
                    <CameraDirection><X>0</X><Y>1</Y><Z>0</Z></CameraDirection>
                    <CameraUpVector><X>0</X><Y>0</Y><Z>1</Z></CameraUpVector>
                    <ViewToWorldScale>12.5</ViewToWorldScale>
                  </OrthogonalCamera>
                </VisualizationInfo>
                """);
            using (var picture = zip.CreateEntry($"{topic}\\snapshot.jpg").Open()) picture.Write(new byte[] { 0xFF, 0xD8, 0xFF });
        }

        stream.Position = 0;
        var read = BcfFile.Read(stream);
        var issue = Assert.Single(read.Issues);
        Assert.Equal((topic, "Error", "Active", "Critical"), (issue.Id, issue.Type, issue.Status, issue.Priority));
        Assert.Equal(new[] { "Fire", "Architecture" }, issue.Labels);
        Assert.Equal(new DateTime(2026, 9, 30, 9, 15, 0, DateTimeKind.Utc), issue.Created);
        Assert.True(issue.IsOpen);
        var comment = Assert.Single(issue.Comments);
        Assert.Equal((view, "Please look."), (comment.Viewpoint!.Value, comment.Text));
        var viewpoint = Assert.Single(issue.Viewpoints);
        Assert.Equal(new[] { IfcGuids.Compress(wall) }, viewpoint.Selected);
        Assert.Equal("jpg", viewpoint.SnapshotType);
        Assert.Equal(new byte[] { 0xFF, 0xD8, 0xFF }, viewpoint.Snapshot);

        // A 2.1 file's scale taken as square, as 3.0 says: 12.5 m each way, from 3 m east and 20 m south, in mm.
        var camera = viewpoint.Camera!;
        Assert.False(camera.Perspective);
        Assert.Equal((3000.0, -20000.0, 1500.0), (camera.Eye.X, camera.Eye.Y, camera.Eye.Z));
        Assert.Equal((12500.0, 1.0), (camera.ViewHeight, camera.AspectRatio));
        Assert.Equal(12500, camera.ToCamera(20000).Width, 6);
    }

    [Fact]
    public void AModelFromANationalGridIsGivenBackItsCoordinates()
    {
        // Set out 530 km east and 180 km north on the grid, brought to the origin.
        var (document, issue, wall, door) = Raised();
        document.ProjectInformation.SurveyOffset = new Vector2D(530_000_000, 180_000_000);
        var read = RoundTrip(document, "3.0", out var zip);
        var view = XDocument.Load(zip.GetEntry($"{issue.Id}/viewpoint.bcfv")!.Open()).Root!;
        var eye = view.Element("PerspectiveCamera")!.Element("CameraViewPoint")!;
        Assert.Equal(530_000 - 4, double.Parse(eye.Element("X")!.Value, System.Globalization.CultureInfo.InvariantCulture), 6);
        Assert.Equal(180_000 - 9, double.Parse(eye.Element("Y")!.Value, System.Globalization.CultureInfo.InvariantCulture), 6);
        Assert.Equal(-4000, Assert.Single(read.Issues).Viewpoints[0].Camera!.Eye.X, 3);

        // The IFC written from here is set out there too; brought in again it comes back to the origin, its offset kept and each element its own id.
        using var model = IfcExport.Build(document);
        var imported = IfcImport.Read(model).Document;
        Assert.Equal(document.ProjectInformation.SurveyOffset, imported.ProjectInformation.SurveyOffset);
        var walls = imported.Walls.ToList();
        Assert.Contains(walls, each => each.Id == wall.Id && Math.Abs(each.Start.X) < 1 && Math.Abs(each.End.X - 6000) < 1);
        Assert.NotNull(imported.FindElement(door.Id));

        // So an issue raised on the original IFC finds its elements in the model brought in from it.
        Assert.Equal(new[] { wall.Id, door.Id }, Issues.Selected(imported, issue.Viewpoints[0]).Select(element => element.Id));
    }

    [Fact]
    public void IssuesBroughtInAreMergedWithThoseHere()
    {
        var (document, issue, _, _) = Raised();
        var other = new Issue { Title = "Another", Created = DateTime.UtcNow };

        // The same issue as another program has it now: closed, with a comment of its own.
        var theirs = issue.Copy();
        theirs.Status = "Closed";
        theirs.Comments.RemoveAt(1);
        theirs.Comments.Add(new IssueComment { Date = new DateTime(2026, 10, 3, 8, 0, 0, DateTimeKind.Utc), Author = "mep@example.com", Text = "Moved." });
        theirs.Viewpoints.RemoveAt(1);

        var merged = Issues.Merge(document.Issues, new[] { theirs, other });
        Assert.Equal(2, merged.Count);
        var same = merged[0];
        Assert.Equal("Closed", same.Status);
        Assert.False(same.IsOpen);
        Assert.Equal(new[] { "See the view.", "On it.", "Moved." }, same.Comments.Select(comment => comment.Text));
        Assert.Equal(2, same.Viewpoints.Count);
        Assert.Same(other, merged[1]);

        // As one step to undo.
        var command = new SetIssuesCommand(document, merged, "Import BCF");
        command.Redo();
        Assert.Equal(2, document.Issues.Count);
        command.Undo();
        Assert.Same(issue, Assert.Single(document.Issues));
    }

    [Fact]
    public void IssuesAreKeptInTheProject()
    {
        var (document, issue, _, _) = Raised();
        document.ProjectInformation.SurveyOffset = new Vector2D(1000, -2000);
        var read = ProjectFile.FromJson(ProjectFile.ToJson(document));
        Assert.Equal(document.ProjectInformation.SurveyOffset, read.ProjectInformation.SurveyOffset);
        var kept = Assert.Single(read.Issues);
        AssertSame(issue, kept, clampedAngle: false);
        Assert.Equal(issue.Viewpoints[0].Camera, kept.Viewpoints[0].Camera);
        Assert.Equal(issue.Viewpoints[0].Clipping, kept.Viewpoints[0].Clipping);
    }

    [Fact]
    public void AClashBecomesAnIssueAboutItsTwoElements()
    {
        var document = BimDocument.CreateDefault();
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
        var test = new ClashTest { Name = "Pipes v Beams" };
        var clash = new Clash { Id = Guid.NewGuid(), Name = "Clash1", ElementA = a, ElementB = b, Point = new Point3D(1000, 2000, 2500), Found = new DateTime(2026, 10, 1), Description = "Pipe through beam" };
        clash.Comments.Add(new ClashComment(new DateTime(2026, 10, 2), "AN", "Drop the pipe."));
        var issue = Issues.FromClash(test, clash, "AN");
        Assert.Equal((clash.Id, "Clash", "New", "Pipes v Beams: Clash1"), (issue.Id, issue.Type, issue.Status, issue.Title));
        var view = Assert.Single(issue.Viewpoints);
        Assert.Equal(new[] { IfcGuids.Compress(a), IfcGuids.Compress(b) }, view.Selected);
        Assert.Equal(2, view.Colours.Count);
        Assert.Equal(view.Id, Assert.Single(issue.Comments).Viewpoint);

        // Its camera 6 m off, looking at the clash.
        var camera = view.Camera!.ToCamera(6000);
        Assert.Equal(clash.Point.X, camera.Target.X, 6);
        Assert.Equal(clash.Point.Z, camera.Target.Z, 6);
    }
}
