using ACadSharp.Entities;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Coordination;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Infrastructure.Interoperability;
using BIMDesigner.Infrastructure.Serialization;
using CSMath;

namespace BIMDesigner.Tests;

/// <summary>
/// Models and drawings linked in: another project or an IFC file read from its own file, put by origin or
/// survey point, drawn in 3D and cut on the plans, clash tested against, found again beside the project and
/// said when its file has changed; a DWG or DXF linked rather than imported, read again as the project opens.
/// </summary>
[Collection("Wpf")]
public class ModelLinkTests
{
    public ModelLinkTests() => ModelLinks.Reader = LinkFiles.Read;

    /// <summary>A project of one wall, 5 m along x from its origin, 3 m high - the structure's model, say - saved where asked.</summary>
    private static string Structure(string folder, string name = "structure.bimx")
    {
        var document = BimDocument.CreateDefault();
        var type = document.TypesOf<WallType>().First(type => type.Function == WallFunction.Exterior);
        document.Add(new Wall { Start = new Point2D(0, 0), End = new Point2D(5000, 0), TypeId = type.Id, LevelId = document.Levels[0].Id, UnconnectedHeight = 3000 });
        var path = Path.Combine(folder, name);
        ProjectFile.Save(document, path);
        return path;
    }

    private static string Folder()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"links-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        return folder;
    }

    [Fact]
    public void ALinkedModelIsPutByItsOriginOrItsSurveyPointMovedAndTurnedAsSet()
    {
        var host = BimDocument.CreateDefault();
        var linked = BimDocument.CreateDefault();
        var link = new ModelLink { Offset = new Vector2D(1000, 2000), Elevation = 300, Rotation = 90 };
        var place = ModelLinks.Placement(host, link, linked);

        // Turned a quarter about its origin, then moved.
        var moved = place(new Point3D(1000, 0, 0));
        Assert.Equal(1000, moved.X, 6);
        Assert.Equal(3000, moved.Y, 6);
        Assert.Equal(300, moved.Z, 6);

        // By survey point: its own place on the national grid against this one's.
        (linked.ProjectInformation.SurveyOffset, host.ProjectInformation.SurveyOffset) = (new Vector2D(530_000_000, 180_000_000), new Vector2D(529_990_000, 179_995_000));
        (link.Positioning, link.Offset, link.Rotation, link.Elevation) = (LinkPositioning.BySurveyPoint, new Vector2D(0, 0), 0, 0);
        var surveyed = ModelLinks.Placement(host, link, linked)(new Point3D(0, 0, 0));
        Assert.Equal((10_000.0, 5_000.0), (surveyed.X, surveyed.Y));
    }

    [Fact]
    public void ALinkedProjectIsDrawnInThreeDCutOnThePlanAndClashTestedAgainst()
    {
        var folder = Folder();
        try
        {
            var host = BimDocument.CreateDefault();
            var link = new ModelLink { Path = Structure(folder), Offset = new Vector2D(0, 1000) };
            host.Add(link);

            // Read once; its meshes where it puts them, picked as the link in 3D, its own elements' for a clash test.
            var model = ModelLinks.Of(link)!;
            Assert.Null(model.Error);
            Assert.StartsWith("1 elements, read", ModelLinks.Status(link));
            var meshes = ModelLinks.Meshes(host, link);
            Assert.NotEmpty(meshes);
            Assert.All(meshes, mesh => Assert.Equal(link.Id, mesh.ElementId));
            var wall = model.Document!.All<Wall>().Single();
            Assert.Contains(ModelLinks.ElementMeshes(host, link), mesh => mesh.ElementId == wall.Id);
            Assert.All(meshes.SelectMany(mesh => mesh.Positions), point => Assert.InRange(point.Y, 1000 - 400, 1000 + 400));

            // Cut on the plan at 1.2 m: its wall's two faces, along x at y = 1000.
            var (_, lines) = Assert.Single(ModelLinks.CutAt(host, host.Levels[0].Elevation + StackedWallType.PlanCutHeight));
            var along = lines.Where(line => Math.Abs(line.A.Y - line.B.Y) < 1 && Math.Abs(line.A.Y - 1000) < 400).Sum(line => line.A.DistanceTo(line.B));
            Assert.True(along >= 2 * 5000 - 10, $"{along:0} mm of lines along it - each of its layers' faces");

            // A wall of this project through it: no clash among this project's own, one against the link.
            var type = host.TypesOf<WallType>().First(each => each.Function == WallFunction.Exterior);
            host.Add(new Wall { Start = new Point2D(2500, -1000), End = new Point2D(2500, 3000), TypeId = type.Id, LevelId = host.Levels[0].Id, UnconnectedHeight = 3000 });
            var test = new ClashTest { Name = "Walls against the structure", Kind = ClashKind.Hard };
            test.SelectionA.Add(BuiltInCategory.Walls);
            test.SelectionB.Add(BuiltInCategory.Walls);
            Assert.Empty(ClashDetective.Run(host, test, DateTime.Now));
            test.AgainstLinks = true;
            var clash = Assert.Single(ClashDetective.Run(host, test, DateTime.Now));
            Assert.Equal(wall.Id, clash.ElementB);
            Assert.Contains("in structure.bimx", clash.Description);

            // Unloaded, it is neither drawn nor read.
            link.Shown = false;
            Assert.Empty(ModelLinks.Meshes(host, link));
            Assert.Equal("Unloaded", ModelLinks.Status(link));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void ALinksFileWrittenSinceItWasReadIsSaidToHaveChangedAndIsFoundBesideTheProject()
    {
        var folder = Folder();
        try
        {
            var link = new ModelLink { Path = Structure(folder) };
            var host = BimDocument.CreateDefault();
            host.Add(link);
            ModelLinks.Load(link);
            Assert.False(ModelLinks.Changed(link));
            File.SetLastWriteTimeUtc(link.Path, DateTime.UtcNow.AddMinutes(5));
            Assert.True(ModelLinks.Changed(link));
            Assert.Contains("its file has changed since", ModelLinks.Status(link));
            ModelLinks.Load(link);
            Assert.False(ModelLinks.Changed(link));

            // Saved with the project, its path from the project's folder too; both moved together, it is found again.
            link.Positioning = LinkPositioning.BySurveyPoint;
            link.Rotation = 30;
            var project = Path.Combine(folder, "house.bimx");
            ProjectFile.Save(host, project);
            Assert.Equal("structure.bimx", link.RelativePath);
            var moved = Folder();
            try
            {
                File.Move(project, Path.Combine(moved, "house.bimx"));
                File.Move(link.Path, Path.Combine(moved, "structure.bimx"));
                var opened = ProjectFile.Load(Path.Combine(moved, "house.bimx"));
                var found = Assert.Single(opened.All<ModelLink>());
                Assert.Equal(Path.Combine(moved, "structure.bimx"), found.Path);
                Assert.Equal((LinkPositioning.BySurveyPoint, 30.0), (found.Positioning, found.Rotation));
            }
            finally
            {
                Directory.Delete(moved, recursive: true);
            }
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void AnIfcFileIsLinkedAsWellAsAProject()
    {
        var folder = Folder();
        try
        {
            var source = ProjectFile.Load(Structure(folder));
            var ifc = Path.Combine(folder, "structure.ifc");
            IfcExport.Save(source, ifc);
            var host = BimDocument.CreateDefault();
            var link = new ModelLink { Path = ifc };
            host.Add(link);
            var model = ModelLinks.Of(link)!;
            Assert.Null(model.Error);
            Assert.Single(model.Document!.All<Wall>());
            Assert.NotEmpty(ModelLinks.Meshes(host, link));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void ALinkedDrawingIsReadAgainFromItsFileAsTheProjectOpens()
    {
        var folder = Folder();
        try
        {
            // A DXF of one line; linked onto the ground floor.
            static void Draw(string path, double length)
            {
                var drawing = new ACadSharp.CadDocument();
                drawing.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(length, 0, 0) });
                ACadSharp.IO.DxfWriter.Write(path, drawing, false);
            }

            var dxf = Path.Combine(folder, "survey.dxf");
            Draw(dxf, 1000);
            var host = BimDocument.CreateDefault();
            var underlay = new CadUnderlay { LevelId = host.Levels[0].Id, Source = dxf, Drawing = CadImport.Read(dxf).Drawing, Linked = true };
            host.Add(underlay);
            var project = Path.Combine(folder, "house.bimx");
            ProjectFile.Save(host, project);

            // The surveyor's drawing issued again, longer: read again as the project opens; an imported one is not.
            Draw(dxf, 4000);
            var opened = ProjectFile.Load(project).All<CadUnderlay>().Single();
            Assert.True(opened.Linked);
            Assert.Equal(4000, opened.Drawing.Bounds.Width, 0);

            var command = new ReloadUnderlayCommand(opened, CadImport.Read(dxf).Drawing, dxf);
            command.Undo();
            Assert.True(opened.Linked);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
