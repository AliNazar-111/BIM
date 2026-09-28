using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;
using BIMDesigner.Infrastructure.Serialization;

namespace BIMDesigner.Tests;

/// <summary>
/// Arcs in the profile of a roof by extrusion: a barrel vault is one arc, kept as an arc, and
/// built from pieces fine enough to read as a curve - one smooth surface in 3D, with no line
/// drawn along every join between its pieces in plan.
/// </summary>
public class RoofArcTests
{
    /// <summary>A roof over a 6 m span running 10 m back, based 3 m up.</summary>
    private static (BimDocument Document, Roof Roof) Extruded(RoofProfile profile)
    {
        var document = BimDocument.CreateDefault();
        var roof = new Roof
        {
            TypeId = document.TypesOf<RoofType>().First().Id,
            LevelId = document.Levels[0].Id,
            HeightOffset = 3000
        };

        roof.SetExtrusion(new RoofExtrusion(new Point2D(0, 0), new Vector2D(1, 0), profile, 0, 10000));
        document.Add(roof);
        return (document, roof);
    }

    /// <summary>Edges of the roof's 3D model running along it - the lines drawn down its length.</summary>
    private static int LinesAlong(BimDocument document, Roof roof) =>
        ModelMeshBuilder.Build(document)
            .Where(mesh => mesh.ElementId == roof.Id)
            .SelectMany(mesh => mesh.Edges)
            .Count(edge => Math.Abs(edge.From.X - edge.To.X) < 1e-6 && Math.Abs(edge.From.Z - edge.To.Z) < 1e-6
                           && Math.Abs(edge.From.Y - edge.To.Y) > 1000);

    [Fact]
    public void ABarrelVaultIsOneArc()
    {
        var vault = RoofExtrusion.Preset(RoofForm.Barrel, 6000, 1500);

        Assert.Equal(2, vault.Points.Count);
        Assert.Equal(1500, Assert.Single(vault.Sagittas), precision: 6);

        var (document, roof) = Extruded(vault);
        var surface = roof.Surface(document);

        Assert.Equal(RoofForm.Barrel, roof.Form);

        // Built from many short pieces, springing from the base and up to the rise at the crown.
        Assert.True(surface.Facets.Count >= 20, $"Only {surface.Facets.Count} pieces: it would read as a polygon.");
        Assert.Equal(3000, surface.HeightAt(new Point2D(0, 5000)), precision: 3);
        Assert.Equal(4500, surface.HeightAt(new Point2D(3000, 5000)), precision: 3);

        // Every point it is built through lies on the one circle.
        var extrusion = roof.Extrusion!;
        var (centre, radius) = RoofExtrusion.Circle(vault.Points[0], vault.Points[1], 1500)!.Value;
        Assert.All(extrusion.Points, point => Assert.Equal(radius, point.DistanceTo(centre), precision: 6));
    }

    [Fact]
    public void AVaultIsDrawnAsOneSurfaceNotARowOfStrips()
    {
        var (document, roof) = Extruded(RoofExtrusion.Preset(RoofForm.Barrel, 6000, 1500));

        // No ridge line in plan along every join between its pieces.
        Assert.Empty(roof.Surface(document).BreakLines);

        // In 3D, lines run along the roof only at its eaves - top and underside of each layer,
        // at each side - not down every join.
        var layers = document.FindType<SlabType>(roof.TypeId)!.Structure.GetLayerOffsets().Count(layer => layer.Layer.Thickness > 0);
        Assert.Equal(4 * layers, LinesAlong(document, roof));

        // And its surfaces share their points across the joins, which is what shades them as
        // one curve rather than a stripe per piece.
        var meshes = ModelMeshBuilder.Build(document).Where(mesh => mesh.ElementId == roof.Id).ToList();
        Assert.All(meshes, mesh => Assert.True(mesh.Positions.Count < mesh.Indices.Count,
            $"{mesh.Positions.Count} points for {mesh.TriangleCount} triangles: nothing is shared."));
    }

    [Fact]
    public void AGambrelStillShowsItsBreaks()
    {
        var (document, roof) = Extruded(RoofExtrusion.Preset(RoofForm.Gambrel, 6000, 3000));

        Assert.Equal(3, roof.Surface(document).BreakLines.Count);

        // The eaves and the three breaks, top and underside of each layer.
        var layers = document.FindType<SlabType>(roof.TypeId)!.Structure.GetLayerOffsets().Count(layer => layer.Layer.Thickness > 0);
        Assert.Equal(10 * layers, LinesAlong(document, roof));
    }

    [Fact]
    public void AnArcPastAHalfCircleIsRefused()
    {
        var curled = new RoofExtrusion(new Point2D(0, 0), new Vector2D(1, 0),
            new RoofProfile(new[] { new Point2D(0, 0), new Point2D(6000, 0) }, new[] { 3200.0 }), 0, 5000);

        Assert.Contains("half circle", curled.Problem());

        // A half circle exactly would stand upright at its springing.
        var half = new RoofExtrusion(new Point2D(0, 0), new Vector2D(1, 0),
            new RoofProfile(new[] { new Point2D(0, 0), new Point2D(6000, 0) }, new[] { 3000.0 }), 0, 5000);
        Assert.NotNull(half.Problem());
    }

    [Fact]
    public void SplittingAnArcKeepsItsCurveAndJoiningItAgainRestoresIt()
    {
        var (a, b) = (new Point2D(0, 0), new Point2D(6000, 0));
        var (centre, radius) = RoofExtrusion.Circle(a, b, 1500)!.Value;

        var (at, first, second) = RoofExtrusion.SplitArc(a, b, 1500, new Point2D(1500, 2000));

        // The new point is on the arc, and each half is part of the same circle.
        Assert.Equal(radius, at.DistanceTo(centre), precision: 6);
        Assert.Equal(radius, RoofExtrusion.Circle(a, at, first)!.Value.Radius, precision: 6);
        Assert.Equal(radius, RoofExtrusion.Circle(at, b, second)!.Value.Radius, precision: 6);

        // Taking the point out again gives back the arc it was.
        Assert.Equal(1500, RoofExtrusion.SagittaThrough(a, at, b), precision: 6);
    }

    [Fact]
    public void AnOverhangingVaultCarriesItsCurveDownPastTheWalls()
    {
        var vault = RoofExtrusion.Preset(RoofForm.Barrel, 6000, 1500, overhang: 500);

        Assert.Equal(-500, vault.Points[0].X, precision: 6);
        Assert.True(vault.Points[0].Y < 0, "The eave should be below the base, following the curve down.");

        // Still one arc, the same circle, the crown where it was.
        var (document, roof) = Extruded(vault);
        Assert.Equal(4500, roof.Surface(document).HeightAt(new Point2D(3000, 5000)), precision: 3);
        Assert.InRange(roof.Surface(document).HeightAt(new Point2D(0, 5000)), 2995, 3001);
    }

    [Fact]
    public void ArcsAreSavedMovedMirroredAndGivenNewDepthsWithTheProfile()
    {
        var (document, roof) = Extruded(RoofExtrusion.Preset(RoofForm.Barrel, 6000, 1500));

        var reloaded = ProjectFile.FromJson(ProjectFile.ToJson(document)).Elements.OfType<Roof>().Single();
        Assert.Equal(1500, Assert.Single(reloaded.Extrusion!.Sagittas), precision: 6);
        Assert.Equal(RoofForm.Barrel, reloaded.Form);

        ElementTransforms.Move(roof, new Vector2D(1000, 0));
        ElementTransforms.Mirror(roof, Line2D.Through(new Point2D(0, 0), new Point2D(0, 1)));
        Assert.Equal(1500, Assert.Single(roof.Extrusion!.Sagittas), precision: 6);

        Assert.True(roof.GetInstanceParameters(document).Single(p => p.Name == "Extrusion End").TrySet(roof.Extrusion.Start + 8000));
        Assert.Equal(1500, Assert.Single(roof.Extrusion!.Sagittas), precision: 6);
        Assert.Equal(RoofForm.Barrel, roof.Form);
    }

    [Fact]
    public void AGableWallUnderAVaultRisesToTheArch()
    {
        var (document, roof) = Extruded(RoofExtrusion.Preset(RoofForm.Barrel, 6000, 1500));
        var wallType = document.TypesOf<WallType>().First(t => t.Name.StartsWith("Exterior"));

        var end = new Wall
        {
            Start = new Point2D(0, 500), End = new Point2D(6000, 500),
            TypeId = wallType.Id, LevelId = document.Levels[0].Id, TopLevelId = document.Levels[1].Id,
            TopAttachedTo = roof.Id
        };

        document.Add(end);

        var profile = WallProfile.Of(document, end)!;
        Assert.Equal(4500, profile.Max(point => point.Y), precision: 0);

        // A curve, not a triangle: a quarter of the way along it is well above a straight
        // line from the springing to the crown.
        var quarter = profile.Where(point => Math.Abs(point.X - 1500) < 200).Max(point => point.Y);
        Assert.True(quarter > 3000 + 750 + 300, $"At a quarter span the wall reached only {quarter:0}.");
    }
}
