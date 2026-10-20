using BIMDesigner.Core.Annotation;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Infrastructure.Serialization;

namespace BIMDesigner.Tests;

/// <summary>
/// Dimension styles, strings through many walls, angles between walls and gridlines, and the
/// radius, diameter and length along a curved wall - each measured from the model as it stands.
/// </summary>
public class DimensionStyleTests
{
    private static Wall Wall(BimDocument document, Point2D start, Point2D end, double bulge = 0)
    {
        var type = document.TypesOf<WallType>().First(t => t.Function == WallFunction.Exterior);
        var wall = new Wall { Start = start, End = end, Bulge = bulge, TypeId = type.Id, LevelId = document.Levels[0].Id, UnconnectedHeight = 3000 };
        document.Add(wall);
        return wall;
    }

    [Fact]
    public void AStyleWritesTheMeasurementItsWay()
    {
        var millimetres = new DimensionType("mm");
        Assert.Equal("2400", millimetres.Format(2400));
        Assert.Equal("2400 mm", new DimensionType("mm") { ShowUnits = true }.Format(2400));
        Assert.Equal("2.400", new DimensionType("m") { Units = DimensionUnits.Metres, Decimals = 3 }.Format(2400));
        Assert.Equal("45°", millimetres.FormatAngle(45));
        Assert.Equal("45.0°", new DimensionType("a") { AngleDecimals = 1 }.FormatAngle(45));

        // A new project has the styles; ticks in millimetres first.
        var document = BimDocument.CreateDefault();
        var first = document.TypesOf<DimensionType>().First();
        Assert.Equal((DimensionTick.Tick, DimensionUnits.Millimetres, 2.5), (first.Tick, first.Units, first.TextSize));
        Assert.Contains(document.TypesOf<DimensionType>(), type => type.Tick == DimensionTick.Arrow);
        Assert.True(TypeDuplicates.CanDuplicate(first));
    }

    [Fact]
    public void AStringMeasuresEachStretchBetweenTheWallsItPasses()
    {
        // Three walls running north, at 0, 3000 and 7000 along.
        var document = BimDocument.CreateDefault();
        var walls = new[] { 0.0, 3000, 7000 }.Select(x => Wall(document, new Point2D(x, 0), new Point2D(x, 5000))).ToList();
        var dimension = new Dimension
        {
            Start = DimensionReference.ToWallLine(walls[0], DimensionAnchor.WallCentreline, new Point2D(0, 2000)),
            End = DimensionReference.ToWallLine(walls[2], DimensionAnchor.WallCentreline, new Point2D(7000, 2000)),
            LevelId = document.Levels[0].Id
        };
        dimension.Between.Add(DimensionReference.ToWallLine(walls[1], DimensionAnchor.WallCentreline, new Point2D(3000, 2500)));
        document.Add(dimension);

        Assert.Equal(new[] { 3000.0, 4000.0 }, dimension.Segments(document).Select(s => Math.Round(s, 6)));
        Assert.Equal(7000, dimension.Measure(document), 6);

        // Move the middle wall: the stretches follow it.
        walls[1].Start = new Point2D(3500, 0);
        walls[1].End = new Point2D(3500, 5000);
        Assert.Equal(new[] { 3500.0, 3500.0 }, dimension.Segments(document).Select(s => Math.Round(s, 6)));
    }

    [Fact]
    public void AnAngleIsMeasuredBetweenTwoWallsInTheQuarterItIsPlacedIn()
    {
        var document = BimDocument.CreateDefault();
        var east = Wall(document, new Point2D(0, 0), new Point2D(6000, 0));
        var slanted = Wall(document, new Point2D(0, 0), new Point2D(4000, 4000));
        var angular = new AngularDimension
        {
            First = DimensionReference.ToWallLine(east, DimensionAnchor.WallCentreline, new Point2D(3000, 0)),
            Second = DimensionReference.ToWallLine(slanted, DimensionAnchor.WallCentreline, new Point2D(2000, 2000)),
            Through = new Point2D(2000, 800)
        };
        document.Add(angular);
        Assert.Equal(45, angular.Measure(document), 6);
        Assert.Equal("45°", angular.DisplayText(document));

        // On the other side of the slanted wall: the angle beyond it.
        angular.Through = new Point2D(-1000, 1500);
        Assert.Equal(135, angular.Measure(document), 6);

        // Parallel lines have no angle between them.
        var parallel = Wall(document, new Point2D(0, 2000), new Point2D(6000, 2000));
        angular.Second = DimensionReference.ToWallLine(parallel, DimensionAnchor.WallCentreline, new Point2D(3000, 2000));
        Assert.Null(angular.Geometry(document));
    }

    [Fact]
    public void ACurvedWallHasItsRadiusDiameterAndLengthAlongIt()
    {
        // A half circle of radius 5000.
        var document = BimDocument.CreateDefault();
        var wall = Wall(document, new Point2D(5000, 0), new Point2D(-5000, 0), bulge: 1);
        var centre = DimensionReference.ToWallLine(wall, DimensionAnchor.WallCentreline, new Point2D(0, 5000));
        var radial = new RadialDimension { Target = centre, Through = new Point2D(0, 6000) };
        document.Add(radial);

        Assert.Equal(5000, radial.Measure(document), 3);
        Assert.Equal("R 5000", radial.DisplayText(document));
        radial.Kind = RadialKind.Diameter;
        Assert.Equal("Ø 10000", radial.DisplayText(document));
        radial.Kind = RadialKind.ArcLength;
        Assert.Equal(Math.PI * 5000, radial.Measure(document), 3);

        // To its outside face: the radius out to it.
        var half = document.GetWallType(wall)!.Structure.TotalWidth / 2;
        var face = new RadialDimension { Target = DimensionReference.ToWall(document, wall, new Point2D(0, 5000 + half - 1), DimensionPreference.WallFaces) };
        Assert.Equal(5000 + half, face.Measure(document), 3);

        // A straight wall has no arc.
        var straight = Wall(document, new Point2D(0, -3000), new Point2D(5000, -3000));
        Assert.Null(new RadialDimension { Target = DimensionReference.ToWallLine(straight, DimensionAnchor.WallCentreline, new Point2D(1000, -3000)) }.Arc(document));
    }

    [Fact]
    public void StylesStringsAnglesAndRadiiAreKept()
    {
        var document = BimDocument.CreateDefault();
        var a = Wall(document, new Point2D(0, 0), new Point2D(0, 5000));
        var b = Wall(document, new Point2D(3000, 0), new Point2D(3000, 5000));
        var c = Wall(document, new Point2D(6000, 0), new Point2D(6000, 5000));
        var curved = Wall(document, new Point2D(5000, 8000), new Point2D(-5000, 8000), bulge: 1);
        var style = document.TypesOf<DimensionType>().First().Duplicate("Detail - arrows 3.5 mm");
        style.Tick = DimensionTick.Arrow;
        style.TextSize = 3.5;
        style.Keynote = "K1";
        document.AddType(style);

        var chain = new Dimension
        {
            Start = DimensionReference.ToWallLine(a, DimensionAnchor.WallCentreline, new Point2D(0, 1000)),
            End = DimensionReference.ToWallLine(c, DimensionAnchor.WallCentreline, new Point2D(6000, 1000)),
            TypeId = style.Id, LevelId = document.Levels[0].Id
        };
        chain.Between.Add(DimensionReference.ToWallLine(b, DimensionAnchor.WallCentreline, new Point2D(3000, 1000)));
        document.Add(chain);
        document.Add(new AngularDimension
        {
            First = DimensionReference.ToWallLine(a, DimensionAnchor.WallCentreline, new Point2D(0, 1000)),
            Second = DimensionReference.ToWallLine(curved, DimensionAnchor.WallCentreline, new Point2D(0, 13000)),
            Through = new Point2D(500, 500), LevelId = document.Levels[0].Id
        });
        document.Add(new RadialDimension
        {
            Kind = RadialKind.ArcLength, Target = DimensionReference.ToWallLine(curved, DimensionAnchor.WallCentreline, new Point2D(0, 13000)),
            Through = new Point2D(0, 14000), LevelId = document.Levels[0].Id
        });

        var reopened = ProjectFile.FromJson(ProjectFile.ToJson(document));
        var kept = reopened.FindType<DimensionType>(style.Id)!;
        Assert.Equal((DimensionTick.Arrow, 3.5, "K1"), (kept.Tick, kept.TextSize, kept.Keynote));
        var string_ = Assert.Single(reopened.Elements.OfType<Dimension>());
        Assert.Equal(style.Id, string_.TypeId);
        Assert.Equal(new[] { 3000.0, 3000.0 }, string_.Segments(reopened).Select(s => Math.Round(s, 6)));
        Assert.Single(reopened.Elements.OfType<AngularDimension>());
        Assert.Equal(Math.PI * 5000, Assert.Single(reopened.Elements.OfType<RadialDimension>()).Measure(reopened), 3);
    }
}

/// <summary>The dimension tools on the plan, as a user clicks them.</summary>
[Collection("Wpf")]
public class DimensionToolTests
{
    [Fact]
    public void AStringAnAngleAndARadiusAreDrawnByClicking()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (System.Windows.Application.Current is null) new BIMDesigner.UI.App().InitializeComponent();
                var document = BimDocument.CreateDefault();
                var type = document.TypesOf<WallType>().First(t => t.Function == WallFunction.Exterior);
                foreach (var x in new[] { 0.0, 3000, 7000 })
                    document.Add(new Wall { Start = new Point2D(x, 0), End = new Point2D(x, 5000), TypeId = type.Id, LevelId = document.Levels[0].Id, UnconnectedHeight = 3000 });
                document.Add(new Wall { Start = new Point2D(0, 0), End = new Point2D(7000, 0), TypeId = type.Id, LevelId = document.Levels[0].Id, UnconnectedHeight = 3000 });
                var curved = new Wall { Start = new Point2D(15000, 0), End = new Point2D(5000, 0), Bulge = -1, TypeId = type.Id, LevelId = document.Levels[0].Id, UnconnectedHeight = 3000 };
                document.Add(curved);

                var plan = new BIMDesigner.UI.Controls.PlanView { Document = document, ActiveLevelId = document.Levels[0].Id, History = new UndoStack(), DimensionString = true };
                plan.SetTool(BIMDesigner.UI.Controls.PlanTool.Dimension);
                foreach (var x in new[] { 0.0, 3000, 7000 }) Assert.True(plan.ClickDimensionString(new Point2D(x, 2500)));
                Assert.True(plan.FinishDimensionString(new Point2D(3500, 4000)));
                var string_ = Assert.Single(document.Elements.OfType<Dimension>());
                Assert.Equal(2, string_.Segments(document).Count);
                Assert.Equal(1500, Math.Abs(string_.Offset), 6);

                plan.SetTool(BIMDesigner.UI.Controls.PlanTool.AngularDimension);
                Assert.True(plan.ClickAngular(new Point2D(5000, 0)));
                Assert.True(plan.ClickAngular(new Point2D(0, 2500)));
                Assert.True(plan.ClickAngular(new Point2D(800, 800)));
                Assert.Equal(90, Assert.Single(document.Elements.OfType<AngularDimension>()).Measure(document), 6);

                plan.SetTool(BIMDesigner.UI.Controls.PlanTool.RadialDimension);
                plan.ActiveRadialKind = RadialKind.Diameter;
                Assert.False(plan.ClickRadial(new Point2D(5000, 2500)));
                Assert.True(plan.ClickRadial(new Point2D(10000, -5000)));
                Assert.True(plan.ClickRadial(new Point2D(10000, -6000)));
                Assert.Equal(10000, Assert.Single(document.Elements.OfType<RadialDimension>()).Measure(document), 3);
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Exception("The plan failed on the UI thread.", failure);
    }
}
