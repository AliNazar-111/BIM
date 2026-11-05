using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;
using BIMDesigner.Core.Rendering;
using BIMDesigner.Core.Views;
using BIMDesigner.Infrastructure.Serialization;

namespace BIMDesigner.Tests;

/// <summary>
/// Renderings at night (round 26): the model's light fittings come on at dusk or when asked, each
/// giving its lumens - a ceiling light downward as Lambert has it - the eye adapted to the dark, the
/// fitting itself glowing, and the setting kept in the project.
/// </summary>
public class NightLightingTests
{
    private static (BimDocument Document, Component Light) Lit()
    {
        var document = BimDocument.CreateDefault();
        var type = document.TypesOf<ComponentType>().First(each => each.Name == "Ceiling Light - Flush");
        var light = new Component { TypeId = type.Id, TypeKind = BuiltInCategory.LightingFixtures, LevelId = document.Levels[0].Id, Location = new Point2D(0, 0), Elevation = 2400 };
        document.Add(light);
        return (document, light);
    }

    [Fact]
    public void TheLightsComeOnAtDuskOrWhenAsked()
    {
        var (document, light) = Lit();
        var settings = document.ProjectInformation.Render;

        // By day they are off, the picture as bright as the light.
        settings.Time = new TimeOnly(13, 0);
        var day = RenderLight.For(document, settings);
        Assert.Empty(day.Lamps);
        Assert.Equal(1, day.Adaptation);

        // At night they are on, a ceiling light shining down from just under it, the eye adapted to the dark.
        settings.Time = new TimeOnly(23, 30);
        var night = RenderLight.For(document, settings);
        var lamp = Assert.Single(night.Lamps);
        Assert.Equal(light.Id, lamp.ElementId);
        Assert.True(lamp.Downward);
        Assert.True(lamp.Lumens > 100, $"{lamp.Lumens} lm");
        Assert.InRange(lamp.At.Z, 2000, 2400);
        Assert.Equal(1024, night.Adaptation);
        Assert.True(night.SkyStrength < 0.01, $"the sky at night {night.SkyStrength}");

        // Off when asked, even at night; on by day when asked, without the eye's adapting.
        settings.Lights = RenderLights.Off;
        Assert.Empty(RenderLight.For(document, settings).Lamps);
        settings.Lights = RenderLights.On;
        settings.Time = new TimeOnly(13, 0);
        var asked = RenderLight.For(document, settings);
        Assert.Single(asked.Lamps);
        Assert.Equal(1, asked.Adaptation);
    }

    [Fact]
    public void ALampLightsTheFloorUnderItAndNotFarOff()
    {
        var (document, light) = Lit();
        var settings = document.ProjectInformation.Render;
        settings.Time = new TimeOnly(23, 30);
        var night = RenderLight.For(document, settings);

        // Seen from overhead, 20 m across: the ground under the lamp lit, the corners dark.
        var scene = RenderScene.Build(Array.Empty<Mesh3D>(), new Dictionary<string, Material>(), ground: true, glowing: new HashSet<Guid> { light.Id });
        var camera = new Camera3D(new Point3D(0, 0, 50000), new Point3D(0, 0, 0), Perspective: false) { FieldOfView = 2 * Math.Atan(0.2) * 180 / Math.PI };
        var job = new RenderJob(scene, camera, night, 100, 100, 0);
        job.Pass(8);
        var pixels = job.Bgra();
        double Mean(int from, int to, int columnFrom, int columnTo) =>
            Enumerable.Range(from, to - from).SelectMany(row => Enumerable.Range(columnFrom, columnTo - columnFrom).Select(column => (double)pixels[4 * (row * 100 + column) + 1])).Average();
        var under = Mean(46, 54, 46, 54);
        var corner = Mean(0, 8, 0, 8);
        Assert.True(under > 40, $"under the lamp {under:0}");
        Assert.True(corner < under / 4, $"in the corner {corner:0}, under the lamp {under:0}");
    }

    [Fact]
    public void TheLightsSettingIsKeptInTheProject()
    {
        var (document, _) = Lit();
        document.ProjectInformation.Render.Lights = RenderLights.On;
        var path = Path.Combine(Path.GetTempPath(), $"bimtest-{Guid.NewGuid():N}{ProjectFile.Extension}");
        try
        {
            ProjectFile.Save(document, path);
            Assert.Equal(RenderLights.On, ProjectFile.Load(path).ProjectInformation.Render.Lights);
            document.ProjectInformation.Render.Lights = RenderLights.Auto;
            ProjectFile.Save(document, path);
            Assert.Equal(RenderLights.Auto, ProjectFile.Load(path).ProjectInformation.Render.Lights);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
