using System.Windows;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;
using BIMDesigner.UI;
using BIMDesigner.UI.Controls;
using BimWindow = BIMDesigner.Core.Architecture.Window;

namespace BIMDesigner.Tests;

/// <summary>
/// A door or window stays inside the wall it is in: brought down, and made shorter if need be,
/// wherever the wall is not a rectangle as tall as it needs - a dormer's gable, a wall under a
/// slope - rather than poking out of its top. It keeps the sill and height it was given, so it
/// follows the wall as the wall changes.
/// </summary>
[Collection("Wpf")]
public class OpeningFitTests
{
    private static void OnUiThread(Action action)
    {
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                if (Application.Current is null) new App().InitializeComponent();
                action();
            }
            catch (Exception exception) { failure = exception; }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null) throw new Exception("The plan failed on the UI thread.", failure);
    }

    private static (BimDocument Document, Wall Wall) WallOf(double height)
    {
        var document = BimDocument.CreateDefault();
        var type = document.TypesOf<WallType>().First(t => t.Name.StartsWith("Exterior"));
        var wall = new Wall
        {
            Start = new Point2D(0, 0), End = new Point2D(6000, 0),
            TypeId = type.Id, LevelId = document.Levels[0].Id, UnconnectedHeight = height
        };
        document.Add(wall);
        return (document, wall);
    }

    /// <summary>A 10 m square house under a 40 degree roof with a gable dormer on its front slope, and the dormer's front wall.</summary>
    private static (BimDocument Document, Wall Front) Dormer()
    {
        var document = BimDocument.CreateDefault();
        var level = document.Levels[0].Id;
        var wallType = document.TypesOf<WallType>().First(t => t.Name.StartsWith("Exterior"));

        var corners = new[] { new Point2D(0, 0), new Point2D(10000, 0), new Point2D(10000, 10000), new Point2D(0, 10000) };
        for (var i = 0; i < 4; i++)
            document.Add(new Wall { Start = corners[i], End = corners[(i + 1) % 4], TypeId = wallType.Id, LevelId = level, UnconnectedHeight = 3000 });

        var main = new Roof { TypeId = document.TypesOf<RoofType>().First().Id, LevelId = level, HeightOffset = 3000 };
        main.SetBoundary(corners);
        main.SetEdges(new[]
        {
            new RoofEdge { DefinesSlope = true, SlopeDegrees = 40 }, new RoofEdge(),
            new RoofEdge { DefinesSlope = true, SlopeDegrees = 40 }, new RoofEdge()
        });
        document.Add(main);

        var before = document.Walls.Count();
        Assert.NotNull(Dormers.Add(document, main, new Point2D(5000, 800), DormerSettings.Default, wallType.Id, out _, out _));
        return (document, document.Walls.Skip(before).ElementAt(1));
    }

    /// <summary>Whether an opening, where it actually is, is inside its wall's outline all the way across.</summary>
    private static void AssertInside(BimDocument document, Wall wall, Opening opening)
    {
        var type = document.FindType<OpeningType>(opening.TypeId)!;
        var outline = WallProfile.Of(document, wall) ?? WallProfile.Rectangle(wall.Length, wall.GetHeight(document));
        var (from, to) = opening.GetSpan(type);
        var (sill, height) = opening.Placed(document, type);

        for (var x = from; x <= to + 1e-6; x += (to - from) / 10)
        {
            var (bottom, top) = WallProfile.HeightsAt(outline, Math.Clamp(x, 1, wall.Length - 1))[0];
            Assert.True(sill >= bottom - 1e-6, $"At {x:0} the sill {sill:0} is below the wall's foot, {bottom:0}.");
            Assert.True(sill + height <= top + 1e-6, $"At {x:0} the head {sill + height:0} is above the wall's top, {top:0}.");
        }
    }

    private static BimWindow WindowIn(BimDocument document, Wall wall, double along, double sill = 900)
    {
        var type = document.TypesOf<WindowType>().First(t => t.Height == 1200 && t.Width <= 1800);
        var window = new BimWindow { HostWallId = wall.Id, LevelId = wall.LevelId, TypeId = type.Id, SillHeight = sill, DistanceAlongWall = along };
        document.Add(window);
        return window;
    }

    [Fact]
    public void AWindowThatFitsIsWhereItWasPut()
    {
        var (document, wall) = WallOf(3000);
        var window = WindowIn(document, wall, 3000);

        Assert.Equal((900.0, 1200.0), window.Placed(document, document.FindType<OpeningType>(window.TypeId)));
    }

    [Fact]
    public void AWindowInALowWallIsBroughtDownUnderItsTop()
    {
        var (document, wall) = WallOf(1800);
        var window = WindowIn(document, wall, 3000);

        var (sill, height) = window.Placed(document, document.FindType<OpeningType>(window.TypeId));

        // Its whole height kept, with a little wall left over its head.
        Assert.Equal(1200, height, 6);
        Assert.Equal(1800 - WallOpenings.FitMargin - 1200, sill, 6);

        // What it was given is kept, and it goes back up when there is room again.
        Assert.Equal(900, window.SillHeight, 6);
        wall.UnconnectedHeight = 3000;
        Assert.Equal((900.0, 1200.0), window.Placed(document, document.FindType<OpeningType>(window.TypeId)));
    }

    [Fact]
    public void AWindowAlreadyInADormersGableIsDrawnInsideIt()
    {
        // As a window put in before it had anything to fit: 900 up, which comes out of the
        // top of the dormer's front wall.
        var (document, front) = Dormer();
        var window = WindowIn(document, front, front.Length / 2);
        var type = document.FindType<OpeningType>(window.TypeId)!;
        var outline = WallProfile.Of(document, front)!;
        Assert.True(900 + type.Height > WallProfile.HeightsAt(outline, front.Length / 2 - type.Width / 2)[0].Top);

        AssertInside(document, front, window);
        var (sill, _) = window.Placed(document, type);
        Assert.True(sill < 900);
        Assert.True(sill >= WallOpenings.FitMargin - 1e-6, "A window keeps a little wall under its sill.");

        // Its glass and frame in 3D, and what Properties says, are where it is.
        var top = front.GetBaseElevation(document) + WallProfile.Top(outline);
        var parts = ModelMeshBuilder.Build(document).Where(mesh => mesh.ElementId == window.Id).SelectMany(mesh => mesh.Positions).ToList();
        Assert.NotEmpty(parts);
        Assert.True(parts.Max(point => point.Z) < top, "The window stands out of the top of the dormer.");

        var parameters = window.GetInstanceParameters(document).ToList();
        Assert.Equal(sill, (double)parameters.Single(p => p.Name == "Sill Height").Value!, 6);
    }

    [Fact]
    public void ADoorIsMadeShorterRatherThanLowered()
    {
        var (document, wall) = WallOf(1900);
        var type = document.TypesOf<DoorType>().First(t => !t.CurtainPanel && t.Height > 1900);
        var door = new Door { HostWallId = wall.Id, TypeId = type.Id, SillHeight = 0 };

        var (sill, height) = door.PlacedIn(document, wall, type, 3000)!.Value;

        Assert.Equal(0, sill, 6);
        Assert.Equal(1900 - WallOpenings.FitMargin, height, 6);
    }

    [Fact]
    public void AWallTooLowForAnyWindowTakesNone()
    {
        var (document, wall) = WallOf(400);
        var type = document.TypesOf<WindowType>().First();
        var window = new BimWindow { HostWallId = wall.Id, TypeId = type.Id, SillHeight = 900 };

        Assert.Null(window.PlacedIn(document, wall, type, 3000));
    }

    [Fact]
    public void MovedIntoALowerWallItFitsThere()
    {
        var (document, high) = WallOf(3000);
        var low = new Wall
        {
            Start = new Point2D(0, 5000), End = new Point2D(6000, 5000),
            TypeId = high.TypeId, LevelId = high.LevelId, UnconnectedHeight = 1800
        };
        document.Add(low);
        var window = WindowIn(document, high, 3000);

        var command = new RehostOpeningCommand(window, low, 2000);
        command.Redo();
        AssertInside(document, low, window);

        command.Undo();
        Assert.Equal((900.0, 1200.0), window.Placed(document, document.FindType<OpeningType>(window.TypeId)));
    }

    [Fact]
    public void AWindowClickedIntoADormerGoesInsideIt()
    {
        OnUiThread(() =>
        {
            var (document, front) = Dormer();
            // The widest window there is: wider than the dormer's front has room for.
            var type = document.TypesOf<WindowType>().OrderByDescending(t => t.Width).First();
            var plan = new PlanView { Document = document, ActiveLevelId = front.LevelId, ActiveWindowTypeId = type.Id };
            plan.SetTool(PlanTool.Window);

            Assert.True(plan.PlaceOpeningIn3D(front.Id, new Point3D(5000, 800, front.GetBaseElevation(document) + 1500)));

            var window = document.Elements.OfType<BimWindow>().Single();
            Assert.Equal(front.Id, window.HostWallId);
            AssertInside(document, front, window);

            // Made smaller to suit the dormer, keeping its proportions, with wall left above it
            // and below it - not just squashed under the gable.
            Assert.NotNull(window.WidthOverride);
            Assert.True(window.WidthOverride < type.Width);
            Assert.Equal(type.Width / type.Height, window.WidthOverride!.Value / window.HeightOverride!.Value, 1);
            Assert.True(window.SillHeight >= WallOpenings.FitMargin - 1e-6);

            var outline = WallProfile.Of(document, front)!;
            var (from, to) = window.GetSpan(type);
            foreach (var x in new[] { from, to })
                Assert.True(window.SillHeight + window.HeightOf(type) <= WallProfile.HeightsAt(outline, x)[0].Top - WallOpenings.FitMargin + 1);
        });
    }

    [Fact]
    public void AWindowClickedInACornerIsKeptClearOfTheWallItMeets()
    {
        OnUiThread(() =>
        {
            var document = BimDocument.CreateDefault();
            var wallType = document.TypesOf<WallType>().First(t => t.Name.StartsWith("Exterior"));
            var corners = new[] { new Point2D(0, 0), new Point2D(6000, 0), new Point2D(6000, 4000), new Point2D(0, 4000) };
            var walls = Enumerable.Range(0, 4).Select(i => new Wall
            {
                Start = corners[i], End = corners[(i + 1) % 4], TypeId = wallType.Id, LevelId = document.Levels[0].Id, UnconnectedHeight = 3000
            }).ToList();
            foreach (var wall in walls) document.Add(wall);

            var type = document.TypesOf<WindowType>().First(t => t.Height == 1200);
            var plan = new PlanView { Document = document, ActiveLevelId = walls[0].LevelId, ActiveWindowTypeId = type.Id };
            plan.SetTool(PlanTool.Window);
            Assert.True(plan.PlaceOpeningIn3D(walls[0].Id, new Point3D(0, 0, 1500)));

            // Its full size, clear of the side wall's inside face.
            var window = document.Elements.OfType<BimWindow>().Single();
            Assert.Null(window.WidthOverride);
            Assert.True(window.GetSpan(type).From >= wallType.Structure.TotalWidth / 2 + WallOpenings.FitMargin - 1);
        });
    }

    [Fact]
    public void ADormersCornersAreClosedWhateverTheWindowDoesToItsFront()
    {
        // A window whose head crosses the gable a hand's breadth from each corner: the strip of
        // wall beside it there is shorter than the corner's mitre reaches, and must still be built.
        var (document, front) = Dormer();
        var window = WindowIn(document, front, front.Length / 2, sill: 100);
        var type = document.FindType<OpeningType>(window.TypeId)!;
        var outline = WallProfile.Of(document, front)!;
        var eave = WallProfile.HeightsAt(outline, 1)[0].Top;
        window.HeightOverride = eave + 60 - window.SillHeight;
        Assert.Equal(window.SillHeight + window.HeightOf(type), window.Placed(document, type).Sill + window.Placed(document, type).Height, 3);

        var wallType = document.GetWallType(front)!;
        var brick = ModelMeshBuilder.BuildWall(document, front).First(mesh => mesh.ElementId == front.Id && mesh.Kind == MeshKind.Wall);
        var reach = brick.Positions.Min(point => front.Locate(wallType.Structure, new Point2D(point.X, point.Y)).Along);

        // Out to the corner, past the wall's own end, where the side wall's face is.
        Assert.True(reach < -wallType.Structure.TotalWidth / 2 + 1, $"The front wall stops {reach:0} along, short of its corner.");
    }

    [Fact]
    public void ADormerIsPickedWholeIn3DAndAPartOnASecondClick()
    {
        OnUiThread(() =>
        {
            var (document, front) = Dormer();
            var plan = new PlanView { Document = document, ActiveLevelId = front.LevelId };
            var dormer = Dormers.Of(document, front)!;

            plan.SelectPicked(front);
            Assert.Equal(Dormers.Parts(document, dormer), plan.SelectedElements);

            plan.SelectPicked(front);
            Assert.Equal(new Element[] { front }, plan.SelectedElements);

            // Anything else is picked as it always was.
            var house = document.Walls.First();
            plan.SelectPicked(house);
            Assert.Equal(new Element[] { house }, plan.SelectedElements);
        });
    }
}
