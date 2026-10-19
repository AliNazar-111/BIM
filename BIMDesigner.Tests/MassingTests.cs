using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Infrastructure.Serialization;

namespace BIMDesigner.Tests;

/// <summary>
/// Masses - the shape of a building before it is one - and the walls, floors and roofs made from
/// their faces, brought back to them when the mass changes.
/// </summary>
public class MassingTests
{
    private static (BimDocument Document, Mass Mass) Box(double width = 20000, double depth = 10000, double height = 9000, MassTop top = MassTop.Flat)
    {
        var document = BimDocument.CreateDefault();
        var mass = new Mass { LevelId = document.Levels[0].Id, Height = height, Top = top };
        mass.SetBoundary(new[] { new Point2D(0, 0), new Point2D(width, 0), new Point2D(width, depth), new Point2D(0, depth) });
        document.Add(mass);
        return (document, mass);
    }

    private static Guid WallType(BimDocument document) => document.TypesOf<WallType>().First(type => type.Function == WallFunction.Exterior && type.Log is null).Id;

    [Fact]
    public void ABoxHasItsSixFacesItsVolumeAndItsFloors()
    {
        var (document, mass) = Box();
        var faces = Masses.Faces(document, mass);
        Assert.Equal(4, faces.Count(face => face.Kind == MassFaceKind.Side));
        Assert.Single(faces, face => face.Kind == MassFaceKind.Top);
        Assert.Equal(20 * 10 * 9e9, Masses.Volume(mass), 0);
        Assert.Equal((2 * 200 + 2 * 20 * 9 + 2 * 10 * 9) * 1e6, Masses.SurfaceArea(document, mass), 0);

        // Mass floors at every level it rises through: its footprint at each.
        var levels = Masses.LevelsWithin(document, mass);
        Assert.NotEmpty(levels);
        mass.FloorLevels.AddRange(levels);
        Assert.Equal(levels.Count * 200e6, Masses.GrossFloorArea(document, mass), 0);
        var floors = Masses.FloorsByFace(document, mass, document.TypesOf<FloorType>().First().Id);
        Assert.Equal(levels.Count, floors.Count);
        Assert.All(floors, floor => Assert.Equal(200e6, floor.Area, 0));
    }

    [Fact]
    public void AWallByFaceStandsOnTheSideItsOutsideOutward()
    {
        var (document, mass) = Box();
        var wall = Assert.Single(Masses.WallsByFace(document, mass, 0, WallType(document), out _)!);

        // Along the south side, its outside face to the south, as high as the mass.
        Assert.Equal(WallLocationLine.FinishFaceExterior, wall.LocationLine);
        Assert.Equal(new Point2D(20000, 0), wall.Start);
        Assert.Equal(new Point2D(0, 0), wall.End);
        Assert.True(wall.ExteriorNormal.Dot(new Vector2D(0, -1)) > 0.99);
        Assert.Equal(9000, wall.UnconnectedHeight);
    }

    [Fact]
    public void AGableTopShapesItsEndWallsAndMakesItsRoof()
    {
        var (document, mass) = Box(12000, 8000, 3000, MassTop.Gable);
        mass.Pitch = 35;
        var ridge = 3000 + 4000 * Math.Tan(35 * Math.PI / 180);
        Assert.Equal(ridge, Masses.Peak(document, mass), 1);

        // The long sides are eaves; an end rises to its gable, its profile the shape of it.
        var eave = Assert.Single(Masses.WallsByFace(document, mass, 0, WallType(document), out _)!);
        Assert.Null(eave.Profile);
        var end = Assert.Single(Masses.WallsByFace(document, mass, 1, WallType(document), out _)!);
        Assert.NotNull(end.Profile);
        Assert.Equal(ridge, end.Profile!.Max(point => point.Y), 1);

        // Its roof by face sits on its sloping top.
        var roof = Masses.RoofByFace(document, mass, document.TypesOf<RoofType>().First().Id, out _)!;
        document.Add(roof);
        Assert.Equal(new[] { true, false, true, false }, roof.Edges.Select(edge => edge.DefinesSlope));
        Assert.Equal(ridge, roof.Surface(document).RidgeElevation, 1);

        // Cut above its eaves, only the part under the ridge is left.
        var section = Assert.Single(Masses.Section(document, mass, 3000 + 2000 * Math.Tan(35 * Math.PI / 180)));
        Assert.InRange(Polygon2D.Area(section), 12000 * 4000 - 1000, 12000 * 4000 + 1000);
    }

    [Fact]
    public void ATaperedMassLeansItsWallsAndAPyramidIsARoof()
    {
        var (document, mass) = Box(10000, 10000, 10000, MassTop.Tapered);
        mass.TopScale = 0.5;

        // Its sides drawn in 2500 over 10 m: walls leaning in 14°.
        var wall = Assert.Single(Masses.WallsByFace(document, mass, 0, WallType(document), out _)!);
        Assert.Equal(WallCrossSection.Slanted, wall.CrossSection);
        Assert.Equal(-Math.Atan2(2500, 10000) * 180 / Math.PI, wall.SlantAngle, 6);
        Assert.Equal(1e8 * 1e4 * (1 + 0.5 + 0.25) / 3, Masses.Volume(mass), 0);

        // To a point, it carries no walls - but makes a roof, pitched up every side.
        mass.TopScale = 0;
        Assert.Null(Masses.WallsByFace(document, mass, 0, WallType(document), out var why));
        Assert.Contains("point", why);
        var roof = Masses.RoofByFace(document, mass, document.TypesOf<RoofType>().First().Id, out _)!;
        Assert.All(roof.Edges, edge => Assert.Equal(Math.Atan2(10000, 5000) * 180 / Math.PI, edge.SlopeDegrees, 6));
    }

    [Fact]
    public void ARoundMassIsWalledInTwoHalvesAndConedAsARoof()
    {
        var document = BimDocument.CreateDefault();
        var mass = new Mass { LevelId = document.Levels[0].Id, Height = 6000 };
        mass.SetCircle(new Point2D(0, 0), 5000);
        document.Add(mass);

        var walls = Masses.WallsByFace(document, mass, 0, WallType(document), out _)!;
        Assert.Equal(2, walls.Count);
        Assert.All(walls, wall => Assert.Equal(5000, wall.LocationCurve.Radius, 6));
        Assert.All(walls, wall => Assert.True(wall.ExteriorNormalAt(wall.Length / 2).Dot(wall.LocationCurve.PointAt(wall.Length / 2) - new Point2D(0, 0)) > 0));

        mass.Top = MassTop.Tapered;
        mass.TopScale = 0;
        var cone = Masses.RoofByFace(document, mass, document.TypesOf<RoofType>().First().Id, out _)!;
        Assert.Single(cone.Edges.Select(edge => edge.ArcId).Distinct());
    }

    [Fact]
    public void WhatWasMadeByFaceIsBroughtBackToTheMassAndSaved()
    {
        var (document, mass) = Box();
        var wall = Masses.WallsByFace(document, mass, 0, WallType(document), out _)![0];
        document.Add(wall);
        mass.Links.Add(new MassLink(wall.Id, MassLinkKind.Wall, 0, null));

        mass.Height = 12000;
        var update = Masses.UpdateToFace(document, mass, out var changed)!;
        Assert.Equal(1, changed);
        update.Redo();
        Assert.Equal(12000, wall.UnconnectedHeight);
        update.Undo();
        Assert.Equal(9000, wall.UnconnectedHeight);

        var reopened = ProjectFile.FromJson(ProjectFile.ToJson(document)).Elements.OfType<Mass>().Single();
        Assert.Equal(mass.Boundary, reopened.Boundary);
        Assert.Equal(12000, reopened.Height);
        Assert.Equal(wall.Id, Assert.Single(reopened.Links).ElementId);
    }
}

/// <summary>Masses drawn and built from on the plan, as a user does it.</summary>
[Collection("Wpf")]
public class MassingPlanTests
{
    [Fact]
    public void AMassIsDrawnAndItsFacesMadeIntoTheBuilding()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (System.Windows.Application.Current is null) new BIMDesigner.UI.App().InitializeComponent();
                var document = BimDocument.CreateDefault();
                var plan = new BIMDesigner.UI.Controls.PlanView { Document = document, ActiveLevelId = document.Levels[0].Id, History = new BIMDesigner.Core.Documents.Commands.UndoStack() };

                // A box 20 m by 10 m, 6 m high, by two corners.
                plan.SetTool(BIMDesigner.UI.Controls.PlanTool.Mass);
                (plan.ActiveMassFootprint, plan.ActiveMassHeight) = (BIMDesigner.UI.Controls.MassFootprint.Box, 6000);
                Assert.True(plan.MassClick(new Point2D(0, 0)));
                Assert.True(plan.MassClick(new Point2D(20000, 10000)));
                var mass = Assert.Single(document.Elements.OfType<Mass>());
                Assert.Equal(200e6, Polygon2D.Area(mass.Boundary), 0);

                // A wall on its south face, floors at its levels, a roof on its top.
                plan.SetTool(BIMDesigner.UI.Controls.PlanTool.WallByFace);
                Assert.True(plan.ByFaceAt(new Point2D(10000, 20)));
                var wall = Assert.Single(document.Walls);
                plan.SetTool(BIMDesigner.UI.Controls.PlanTool.FloorByFace);
                Assert.True(plan.ByFaceAt(new Point2D(10000, 5000)));
                Assert.NotEmpty(document.Elements.OfType<Floor>());
                Assert.NotEmpty(mass.FloorLevels);

                // Its mass floors were put on with them, in the same step.
                plan.History!.Undo();
                Assert.Empty(document.Elements.OfType<Floor>());
                Assert.Empty(mass.FloorLevels);
                plan.History.Redo();
                Assert.NotEmpty(document.Elements.OfType<Floor>());
                plan.SetTool(BIMDesigner.UI.Controls.PlanTool.RoofByFace);
                Assert.True(plan.ByFaceAt(new Point2D(10000, 5000)));
                var roof = Assert.Single(document.Elements.OfType<Roof>());
                Assert.Equal(6000, roof.HeightOffset, 6);

                // In 3D: a click on its east face makes a wall there.
                plan.SetTool(BIMDesigner.UI.Controls.PlanTool.WallByFace);
                Assert.True(plan.ByFaceIn3D(mass.Id, new BIMDesigner.Core.Geometry.Point3D(20000, 5000, 3000)));
                Assert.Equal(2, document.Walls.Count());

                // Made taller, everything made from it is brought back to it - one step to undo.
                mass.Height = 9000;
                plan.SetTool(BIMDesigner.UI.Controls.PlanTool.Select);
                plan.Select(mass);
                Assert.True(plan.UpdateMassToFace());
                Assert.Equal(9000, wall.UnconnectedHeight);
                Assert.Equal(9000, roof.HeightOffset, 6);
                plan.History!.Undo();
                Assert.Equal(6000, wall.UnconnectedHeight);
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Exception("The plan failed on the UI thread.", failure);
    }
}
