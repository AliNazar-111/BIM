using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;
using BIMDesigner.Infrastructure.Interoperability;
using BIMDesigner.Infrastructure.Serialization;
using Xbim.Ifc4.Interfaces;
using Xbim.Ifc4.SharedBldgElements;

namespace BIMDesigner.Tests;

/// <summary>
/// Roofs by extrusion (Revit's Roof by Extrusion): a profile drawn square-on and pushed back
/// through the building - the way to a gambrel, a barrel vault, any roof the same all along.
/// </summary>
public class RoofExtrusionTests
{
    private static readonly double Rise30 = Math.Tan(30 * Math.PI / 180);

    /// <summary>
    /// A roof over a 6 m span running 10 m back: the profile along X from the origin, the roof
    /// pushed out along Y. Based 3 m up.
    /// </summary>
    private static (BimDocument Document, Roof Roof) Extruded(RoofForm form, double rise = 3000 * 0.5773502691896257)
    {
        var document = BimDocument.CreateDefault();
        var roof = new Roof
        {
            TypeId = document.TypesOf<RoofType>().First().Id,
            LevelId = document.Levels[0].Id,
            HeightOffset = 3000
        };

        roof.SetExtrusion(new RoofExtrusion(
            new Point2D(0, 0), new Vector2D(1, 0), RoofExtrusion.Preset(form, 6000, rise), 0, 10000));

        document.Add(roof);
        return (document, roof);
    }

    [Fact]
    public void AnExtrudedGableRunsTheProfileBackThroughTheRoof()
    {
        var (document, roof) = Extruded(RoofForm.Gable);
        var surface = roof.Surface(document);

        Assert.True(roof.IsExtrusion);
        Assert.Equal(RoofForm.Gable, roof.Form);

        // Its outline in plan is the profile's width by the extrusion's depth.
        Assert.Equal(60e6, roof.Area, precision: 0);
        Assert.Equal(2, surface.Facets.Count);

        // One ridge, over the middle of the span, running the whole depth.
        var ridge = Assert.Single(surface.BreakLines);
        Assert.Equal(3000, ridge.From.X, precision: 3);
        Assert.Equal(10000, ridge.From.DistanceTo(ridge.To), precision: 3);

        // The underside follows the profile: at the eave, the base; under the ridge, the rise.
        Assert.Equal(3000, surface.HeightAt(new Point2D(0, 5000)), precision: 3);
        Assert.Equal(3000 + 3000 * Rise30, surface.HeightAt(new Point2D(3000, 5000)), precision: 3);
        Assert.Equal(3000 + 1500 * Rise30, surface.HeightAt(new Point2D(4500, 2000)), precision: 3);
    }

    [Fact]
    public void AGambrelHasTwoPitchesEachSide()
    {
        var (document, roof) = Extruded(RoofForm.Gambrel, rise: 3000);
        var surface = roof.Surface(document);

        Assert.Equal(RoofForm.Gambrel, roof.Form);
        Assert.Equal(4, surface.Facets.Count);

        // The ridge and the two breaks where the steep pitch gives way to the shallow one.
        Assert.Equal(3, surface.BreakLines.Count);

        var steep = surface.Facets.Max(facet => facet.Plane.SlopeDegrees);
        var shallow = surface.Facets.Min(facet => facet.Plane.SlopeDegrees);
        Assert.True(steep > shallow + 20, $"The pitches were {steep:0}° and {shallow:0}°: not a gambrel.");
    }

    [Fact]
    public void ABarrelVaultIsAnArchOfShortFaces()
    {
        var (document, roof) = Extruded(RoofForm.Barrel, rise: 1500);
        var surface = roof.Surface(document);

        Assert.Equal(RoofForm.Barrel, roof.Form);
        Assert.True(surface.Facets.Count >= 6);

        // Springing from the base at each side, up to the rise at the crown.
        Assert.Equal(3000, surface.HeightAt(new Point2D(0, 5000)), precision: 3);
        Assert.Equal(4500, surface.HeightAt(new Point2D(3000, 5000)), precision: 0);
    }

    [Fact]
    public void AProfileMustFaceUpward()
    {
        Assert.Contains("upright", new RoofExtrusion(
            new Point2D(0, 0), new Vector2D(1, 0),
            new[] { new Point2D(0, 0), new Point2D(0, 1000), new Point2D(3000, 1500) }, 0, 5000).Problem());

        Assert.Contains("folds back", new RoofExtrusion(
            new Point2D(0, 0), new Vector2D(1, 0),
            new[] { new Point2D(0, 0), new Point2D(3000, 1500), new Point2D(2000, 2000) }, 0, 5000).Problem());

        Assert.Contains("depth", new RoofExtrusion(
            new Point2D(0, 0), new Vector2D(1, 0),
            new[] { new Point2D(0, 0), new Point2D(3000, 1500) }, 2000, 2000).Problem());
    }

    [Fact]
    public void TheBuildUpIsMitredSoTheTopDoesNotStepAtABreak()
    {
        var (document, roof) = Extruded(RoofForm.Gambrel, rise: 3000);
        var pieces = RoofSolid.Pieces(document, roof);
        Assert.NotEmpty(pieces);

        // Along a line across the roof, each layer's top is continuous from piece to piece:
        // where one piece stops the next starts at the same height. A build-up offset
        // vertically instead would step at the break by the difference in its depth.
        foreach (var layer in pieces.GroupBy(piece => piece.Layer))
        {
            var ordered = layer.OrderBy(piece => piece.Outline.Min(point => point.X)).ToList();

            for (var i = 0; i + 1 < ordered.Count; i++)
            {
                var join = new Point2D(ordered[i].Outline.Max(point => point.X), 5000);
                Assert.Equal(ordered[i].Top.HeightAt(join), ordered[i + 1].Top.HeightAt(join), precision: 3);
                Assert.Equal(ordered[i].Bottom.HeightAt(join), ordered[i + 1].Bottom.HeightAt(join), precision: 3);
            }
        }

        // And the 3D model stands on the base at the eaves and rises above the crown.
        var bounds = ModelMeshBuilder.Bounds(ModelMeshBuilder.Build(document).Where(mesh => mesh.ElementId == roof.Id))!.Value;
        Assert.Equal(3000, bounds.Min.Z, precision: 3);
        Assert.True(bounds.Max.Z > 6000);
    }

    [Fact]
    public void AnOverhangCarriesTheEavesOutAndDownAtTheirPitch()
    {
        var profile = RoofExtrusion.Preset(RoofForm.Gable, 6000, 3000 * Rise30, overhang: 500);

        Assert.Equal(-500, profile[0].X, precision: 6);
        Assert.Equal(-500 * Rise30, profile[0].Y, precision: 6);
        Assert.Equal(6500, profile[^1].X, precision: 6);
    }

    [Fact]
    public void AGableWallAttachedUnderAnExtrudedRoofRisesToIt()
    {
        var (document, roof) = Extruded(RoofForm.Gable);
        var wallType = document.TypesOf<WallType>().First(t => t.Name.StartsWith("Exterior"));

        // A wall across the span at one end of the roof, 3 m high, attached to it.
        var end = new Wall
        {
            Start = new Point2D(0, 500), End = new Point2D(6000, 500),
            TypeId = wallType.Id, LevelId = document.Levels[0].Id, TopLevelId = document.Levels[1].Id,
            TopAttachedTo = roof.Id
        };

        document.Add(end);

        var profile = WallProfile.Of(document, end);
        Assert.NotNull(profile);
        Assert.Equal(3000 + 3000 * Rise30, profile!.Max(point => point.Y), precision: 1);
    }

    [Fact]
    public void MovingAndMirroringCarryTheExtrusion()
    {
        var (_, roof) = Extruded(RoofForm.Gable);

        ElementTransforms.Move(roof, new Vector2D(1000, 2000));
        Assert.Equal(new Point2D(1000, 2000), roof.Extrusion!.Origin);
        Assert.Equal(60e6, roof.Area, precision: 0);
        Assert.Contains(roof.Boundary, point => point.DistanceTo(new Point2D(1000, 2000)) < 1e-6);

        // Mirrored about the Y axis: the same roof, the other side of it.
        ElementTransforms.Mirror(roof, Line2D.Through(new Point2D(0, 0), new Point2D(0, 1)));
        Assert.Equal(60e6, roof.Area, precision: 0);
        Assert.All(roof.Boundary, point => Assert.True(point.X <= 1e-6));
        Assert.All(roof.Boundary, point => Assert.InRange(point.Y, 2000 - 1e-6, 12000 + 1e-6));
    }

    [Fact]
    public void AnExtrudedRoofIsSavedCopiedAndExportedAsWhatItIs()
    {
        var (document, roof) = Extruded(RoofForm.Gambrel, rise: 3000);

        var reloaded = ProjectFile.FromJson(ProjectFile.ToJson(document)).Elements.OfType<Roof>().Single();
        Assert.True(reloaded.IsExtrusion);
        Assert.Equal(RoofForm.Gambrel, reloaded.Form);
        Assert.Equal(roof.Extrusion!.Profile, reloaded.Extrusion!.Profile);
        Assert.Equal(10000, reloaded.Extrusion.End, precision: 6);

        var copy = (Roof)ElementCopy.Clone(roof)!;
        Assert.True(copy.IsExtrusion);
        Assert.Equal(roof.Area, copy.Area, precision: 0);

        using var model = IfcExport.Build(document);
        var exported = model.Instances.OfType<IfcRoof>().Single();
        Assert.Equal(IfcRoofTypeEnum.GAMBREL_ROOF, exported.PredefinedType);
    }

    [Fact]
    public void TheExtrusionsDepthIsAProperty()
    {
        var (document, roof) = Extruded(RoofForm.Gable);
        var end = roof.GetInstanceParameters(document).Single(p => p.Name == "Extrusion End");

        Assert.True(end.TrySet(8000.0));
        Assert.Equal(48e6, roof.Area, precision: 0);

        // An end before the start would turn the roof inside out.
        Assert.False(end.TrySet(-100.0));
    }

    [Fact]
    public void EditingTheProfileIsOneThingToUndo()
    {
        var (_, roof) = Extruded(RoofForm.Gable);
        var gable = roof.Extrusion!;

        var command = new SetRoofExtrusionCommand(roof, gable.With(profile: RoofExtrusion.Preset(RoofForm.Gambrel, 6000, 3000)));
        command.Redo();
        Assert.Equal(RoofForm.Gambrel, roof.Form);

        command.Undo();
        Assert.Equal(RoofForm.Gable, roof.Form);
    }
}
