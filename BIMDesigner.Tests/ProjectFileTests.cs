using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Infrastructure.Serialization;

namespace BIMDesigner.Tests;

/// <summary>
/// A saved project must reopen as the same model. These tests are the contract: if the
/// format changes, they say what broke.
/// </summary>
public class ProjectFileTests
{
    private static BimDocument BuildProject()
    {
        var document = BimDocument.CreateDefault();
        document.ProjectInformation.Name = "Riverside Depot";
        document.ProjectInformation.Client = "Acme Construction";

        var exterior = document.TypesOf<WallType>().Single(t => t.Function == WallFunction.Exterior);
        var ground = document.Levels.Single(l => l.Name == "Ground Floor");
        var first = document.Levels.Single(l => l.Name == "First Floor");

        document.Add(new Wall
        {
            Start = new Point2D(0, 0),
            End = new Point2D(6000, 0),
            TypeId = exterior.Id,
            LevelId = ground.Id,
            TopLevelId = first.Id,
            TopOffset = -150,
            BaseOffset = 50,
            LocationLine = WallLocationLine.CoreFaceExterior,
            Flipped = true,
            RoomBounding = false,
            StructuralUsage = StructuralUsage.Bearing,
            Mark = "EW-01",
            Comments = "Party wall — check acoustic report",
            UnconnectedHeight = 2750
        });

        return document;
    }

    private static BimDocument RoundTrip(BimDocument document) =>
        ProjectFile.FromJson(ProjectFile.ToJson(document));

    [Fact]
    public void RoundTrip_PreservesProjectInformation()
    {
        var reloaded = RoundTrip(BuildProject());

        Assert.Equal("Riverside Depot", reloaded.ProjectInformation.Name);
        Assert.Equal("Acme Construction", reloaded.ProjectInformation.Client);
    }

    [Fact]
    public void RoundTrip_PreservesLevels()
    {
        var reloaded = RoundTrip(BuildProject());

        Assert.Equal(2, reloaded.Levels.Count);
        Assert.Equal(3000, reloaded.Levels.Single(l => l.Name == "First Floor").Elevation, precision: 6);
    }

    [Fact]
    public void RoundTrip_PreservesEveryWallInstanceParameter()
    {
        var original = BuildProject();
        var wall = original.Walls.Single();
        var reloaded = RoundTrip(original).Walls.Single();

        Assert.Equal(wall.Id, reloaded.Id);
        Assert.Equal(wall.TypeId, reloaded.TypeId);
        Assert.Equal(wall.LevelId, reloaded.LevelId);
        Assert.Equal(wall.TopLevelId, reloaded.TopLevelId);
        Assert.Equal(wall.Start, reloaded.Start);
        Assert.Equal(wall.End, reloaded.End);
        Assert.Equal(wall.LocationLine, reloaded.LocationLine);
        Assert.Equal(wall.Flipped, reloaded.Flipped);
        Assert.Equal(wall.BaseOffset, reloaded.BaseOffset, precision: 6);
        Assert.Equal(wall.TopOffset, reloaded.TopOffset, precision: 6);
        Assert.Equal(wall.UnconnectedHeight, reloaded.UnconnectedHeight, precision: 6);
        Assert.Equal(wall.RoomBounding, reloaded.RoomBounding);
        Assert.Equal(wall.StructuralUsage, reloaded.StructuralUsage);
        Assert.Equal(wall.Mark, reloaded.Mark);
        Assert.Equal(wall.Comments, reloaded.Comments);
        Assert.Equal(wall.PhaseCreated, reloaded.PhaseCreated);
    }

    [Fact]
    public void RoundTrip_PreservesWallTypeAssemblies()
    {
        var original = BuildProject();
        var reloaded = RoundTrip(original);

        foreach (var type in original.TypesOf<WallType>())
        {
            var match = reloaded.TypesOf<WallType>().Single(t => t.Id == type.Id);

            Assert.Equal(type.Name, match.Name);
            Assert.Equal(type.Width, match.Width, precision: 6);
            Assert.Equal(type.FireRating, match.FireRating);
            Assert.Equal(type.Function, match.Function);
            Assert.Equal(type.HeatTransferCoefficient, match.HeatTransferCoefficient, precision: 6);
            Assert.Equal(type.Structure.Layers.Count, match.Structure.Layers.Count);

            foreach (var (before, after) in type.Structure.Layers.Zip(match.Structure.Layers))
            {
                Assert.Equal(before.Function, after.Function);
                Assert.Equal(before.MaterialId, after.MaterialId);
                Assert.Equal(before.Thickness, after.Thickness, precision: 6);
                Assert.Equal(before.Wraps, after.Wraps);
            }
        }
    }

    [Fact]
    public void RoundTrip_KeepsMaterialReferencesResolvable()
    {
        var reloaded = RoundTrip(BuildProject());

        // A dangling material id would show as grey boxes in the plan view.
        foreach (var layer in reloaded.TypesOf<WallType>().SelectMany(t => t.Structure.Layers))
            Assert.NotNull(reloaded.FindMaterial(layer.MaterialId));
    }

    [Fact]
    public void RoundTrip_PreservesComputedQuantities()
    {
        var original = BuildProject();
        var reloaded = RoundTrip(original);

        Assert.Equal(original.Walls.Single().GetVolume(original),
            reloaded.Walls.Single().GetVolume(reloaded), precision: 6);
    }

    [Fact]
    public void RoundTrip_PreservesMaterialColours()
    {
        var original = BuildProject();
        var reloaded = RoundTrip(original);

        foreach (var material in original.Materials)
        {
            var match = reloaded.FindMaterial(material.Id);
            Assert.NotNull(match);
            Assert.Equal(material.SurfaceColour, match!.SurfaceColour);
            Assert.Equal(material.CutColour, match.CutColour);
            Assert.Equal(material.Density, match.Density, precision: 6);
        }
    }

    [Fact]
    public void Load_RejectsAFileThatIsNotAProject()
    {
        Assert.Throws<ProjectFileException>(() => ProjectFile.FromJson("this is not json"));
    }

    [Fact]
    public void Load_RefusesANewerFormatRatherThanMisreadingIt()
    {
        var json = $$"""{"formatVersion": {{ProjectFile.CurrentFormatVersion + 1}}, "walls": []}""";

        var exception = Assert.Throws<ProjectFileException>(() => ProjectFile.FromJson(json));
        Assert.Contains("newer version", exception.Message);
    }

    [Fact]
    public void Load_BringsOlderWallsUpToFinishingTheirEnds()
    {
        // Before format 2 nothing turned round an exposed end, so every wall was saved saying
        // None whatever it was made of, and every corner showed a section through the wall.
        var saved = ProjectFile.ToJson(BuildProject());
        var older = saved
            .Replace($"\"formatVersion\": {ProjectFile.CurrentFormatVersion}", "\"formatVersion\": 1")
            .Replace("\"wrappingAtEnds\": \"Exterior\"", "\"wrappingAtEnds\": \"None\"");

        Assert.All(ProjectFile.FromJson(older).TypesOf<WallType>().Where(t => t.Structure.ExteriorWidth > 0),
            type => Assert.Equal(WallWrapping.Exterior, type.WrapAtEnds));

        // A wall with nothing outside its core has nothing to turn, and is left alone.
        Assert.All(ProjectFile.FromJson(older).TypesOf<WallType>()
                .Where(t => t.Structure.ExteriorWidth == 0 && t.Structure.InteriorWidth == 0),
            type => Assert.Equal(WallWrapping.None, type.WrapAtEnds));

        // From format 2 on, what the file says is what the wall was asked for.
        var current = saved.Replace("\"wrappingAtEnds\": \"Exterior\"", "\"wrappingAtEnds\": \"None\"");
        Assert.All(ProjectFile.FromJson(current).TypesOf<WallType>(),
            type => Assert.Equal(WallWrapping.None, type.WrapAtEnds));
    }

    [Fact]
    public void Load_SurvivesUnknownEnumTextByFallingBack()
    {
        var json = ProjectFile.ToJson(BuildProject())
            .Replace("\"CoreFaceExterior\"", "\"SomeFutureLocationLine\"");

        var wall = ProjectFile.FromJson(json).Walls.Single();

        Assert.Equal(WallLocationLine.WallCentreline, wall.LocationLine);
    }

    [Fact]
    public void Save_WritesAndReadsARealFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bimtest-{Guid.NewGuid():N}{ProjectFile.Extension}");

        try
        {
            ProjectFile.Save(BuildProject(), path);

            Assert.True(File.Exists(path));
            Assert.Single(ProjectFile.Load(path).Walls);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Save_OverwritesAnExistingFileWithoutLeavingATemporaryBehind()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bimtest-{Guid.NewGuid():N}{ProjectFile.Extension}");

        try
        {
            ProjectFile.Save(BuildProject(), path);
            ProjectFile.Save(BuildProject(), path);

            Assert.False(File.Exists(path + ".tmp"));
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".tmp");
        }
    }
}
