using System.Reflection;
using System.Windows;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;
using BIMDesigner.UI;
using BIMDesigner.UI.Controls;
using Window = BIMDesigner.Core.Architecture.Window;

namespace BIMDesigner.Tests;

/// <summary>
/// People who walk and see: walking as an adult, a child or a wheelchair user, each stopped where
/// they would be; the area a person sees; lines of sight; and the view out as BS EN 17037 grades it.
/// </summary>
[Collection("Wpf")]
public class PeopleSightTests
{
    private static ComponentType Person(BimDocument document, string name) => document.TypesOf<ComponentType>().Single(type => type.Name.StartsWith(name));

    /// <summary>The two-storey house, someone standing on the ground floor, away from the desk.</summary>
    private static (BimDocument Document, Component Someone) House(string who = "Person - Adult", double x = 3000, double y = 4000)
    {
        var document = ConstructionTests.House();
        var type = Person(document, who);
        var someone = new Component { TypeId = type.Id, TypeKind = type.Kind, LevelId = document.Levels[0].Id, Location = new Point2D(x, y) };
        document.Add(someone);
        return (document, someone);
    }

    [Fact]
    public void EachWalkerHasTheirOwnEyesStepsSlopesAndWidth()
    {
        var wheelchair = Walker.For(WalkerKind.WheelchairUser, us: false);
        Assert.Equal((1200.0, 15.0, 1.0 / 12, 700.0), (wheelchair.EyeHeight, wheelchair.StepUp, wheelchair.Steepest, wheelchair.Width));
        Assert.Equal(12.7, Walker.For(WalkerKind.WheelchairUser, us: true).StepUp);
        Assert.Equal(Views3D.EyeHeight, Walker.For(WalkerKind.Adult, false).EyeHeight);

        // A person in the model sees from 0.93 of their height - a child from lower down - a wheelchair user from 1200.
        var document = BimDocument.CreateDefault();
        Assert.Equal(Math.Round(1750 * 0.93), Walker.Of(Person(document, "Person - Adult"), false).EyeHeight);
        Assert.Equal(WalkerKind.Child, Walker.Of(Person(document, "Person - Child"), false).Kind);
        Assert.Equal(1200, Walker.Of(Person(document, "Person - Wheelchair User"), false).EyeHeight);
    }

    [Fact]
    public void APersonSeesTheRoomTheyAreInAndOutThroughItsWindow()
    {
        var (document, someone) = House();
        var (area, squareMetres, _, openings) = Sight.Of(document, someone);
        Assert.True(area.Count > 100);

        // The ground floor is 10 by 8 between wall centres: inside its walls a little less - and,
        // through the window in front, a wedge of what is outside it as well.
        Assert.InRange(squareMetres, 70, 400);
        Assert.Contains(openings, opening => opening is Window);

        // Without the window, only the room.
        var window = document.Elements.OfType<Window>().Single();
        document.Remove(window);
        var (_, shut, _, _) = Sight.Of(document, someone);
        Assert.InRange(shut, 60, 80);
        Assert.True(shut < squareMetres);
    }

    [Fact]
    public void ALineOfSightIsStoppedByAWallAndNotByGlass()
    {
        var (document, _) = House();
        var meshes = ModelMeshBuilder.Build(document);
        var window = document.Elements.OfType<Window>().Single();
        var eye = new Point3D(3000, 2000, 1500);

        // Out through the window's glass: clear - its frame left out of the reckoning.
        Assert.True(Sight.Line(meshes, eye, new Point3D(3000, -3000, 1500), new HashSet<Guid> { window.Id }).Clear);

        // Out through the wall beside it: the wall is in the way.
        var through = Sight.Line(meshes, eye, new Point3D(6000, -3000, 1500));
        Assert.False(through.Clear);
        Assert.Equal(window.HostWallId, through.Blocker);
    }

    [Fact]
    public void TheViewOutIsGradedAsBsEn17037Grades()
    {
        var (document, _) = House();
        var level = document.Levels[0].Id;

        // Two metres in from the window, seated: 30° of glass, the view open to the horizon - sky, landscape and ground.
        var near = ViewOut.From(document, level, new Point2D(3000, 2000), ViewOut.Seated);
        Assert.Equal(1, near.Windows);
        Assert.InRange(near.SightAngle, 28, 34);
        Assert.True(near.Sky && near.Landscape && near.Ground);
        Assert.Equal(ViewLevel.Medium, near.Level);

        // From the middle of the room the window takes up too little of the view: below the minimum's 14°.
        Assert.True(ViewOut.From(document, level, new Point2D(5000, 4000), ViewOut.Seated).SightAngle < 14);

        // A building 8 m off, 6 m high: the view goes 8 m out, and its top hides the sky - minimum.
        var other = new Wall
        {
            Start = new Point2D(-5000, -8300), End = new Point2D(15000, -8300), LevelId = level, UnconnectedHeight = 6000,
            TypeId = document.TypesOf<WallType>().First(type => type.Function == WallFunction.Exterior && type.Log is null).Id
        };
        document.Add(other);
        var blocked = ViewOut.From(document, level, new Point2D(3000, 2000), ViewOut.Seated);
        Assert.InRange(blocked.OutsideDistance, 7000, 9000);
        Assert.False(blocked.Sky);
        Assert.Equal(ViewLevel.Minimum, blocked.Level);

        // Worked out alone, only what could be in the way is built - and it comes to what the whole model gives.
        Assert.Equal(ViewOut.From(document, level, new Point2D(3000, 2000), ViewOut.Seated, ModelMeshBuilder.Build(document)), blocked);

        Assert.Equal(ViewLevel.High, ViewOut.Grade(60, 60000, true, true, true));
        Assert.Equal(ViewLevel.None, ViewOut.Grade(60, 60000, true, false, true));
    }

    [Fact]
    public void ALivingRoomsWindowIsLowEnoughToSeeOutOfSittingDown()
    {
        var (document, _) = House();
        var room = new Room { LevelId = document.Levels[0].Id, Location = new Point2D(4000, 3000), Name = "Living Room", Number = "1" };
        document.Add(room);
        Assert.Contains("850", WindowRules.Verdict(document, room) ?? string.Empty);

        document.Elements.OfType<Window>().Single().SillHeight = 700;
        Assert.DoesNotContain("850", WindowRules.Verdict(document, room) ?? string.Empty);
        Assert.Contains("BS EN 17037", RoomParameters.ViewOut.Name);
        Assert.False(string.IsNullOrEmpty((string?)room.GetInstanceParameters(document).Single(p => p.Definition == RoomParameters.ViewOut).Value));
    }

    [Fact]
    public void APersonsPropertiesSayWhatTheySee()
    {
        var (document, someone) = House("Person - Wheelchair User", 3000, 2000);
        var parameters = someone.GetInstanceParameters(document).ToDictionary(p => p.Definition.Name);
        Assert.Equal(1200.0, parameters["Eye Height"].Value);
        Assert.Contains("m² in sight", (string)parameters["What They See"].Value!);
        Assert.StartsWith("Medium", (string)parameters["View Out (BS EN 17037)"].Value!);
    }

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
        if (failure is not null) throw new Exception("Failed on the UI thread.", failure);
    }

    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

    private static void Stride(ModelView view, double x, double y) =>
        typeof(ModelView).GetMethod("Step", Hidden)!.Invoke(view, new object[] { new Vector2D(x, y) });

    [Fact]
    public void AWheelchairUserIsStoppedByAStepThatSomeoneOnFootTakes()
    {
        OnUiThread(() =>
        {
            var (document, _) = House();
            // A raised platform 150 high over the far half of the ground floor.
            var platform = new Floor { LevelId = document.Levels[0].Id, TypeId = document.TypesOf<FloorType>().First().Id, HeightOffset = 150 };
            platform.SetBoundary(new[] { new Point2D(5000, 500), new Point2D(9500, 500), new Point2D(9500, 7500), new Point2D(5000, 7500) });
            document.Add(platform);
            var view = new ModelView { Width = 400, Height = 300, Document = document };
            view.Measure(new Size(400, 300));
            view.Arrange(new Rect(0, 0, 400, 300));
            var floor = document.Elements.OfType<Floor>().Single(each => each.LevelId == document.Levels[0].Id && each.HeightOffset == 0).GetTopElevation(document);

            string? said = null;
            view.WalkStopped += (_, why) => said = why;
            view.Walker = Walker.For(WalkerKind.WheelchairUser, false);
            Assert.Null(view.StartWalkingAt(new Point2D(4800, 4000), floor, 0));
            for (var i = 0; i < 20; i++) Stride(view, 25, 0);
            Assert.Equal(floor, view.WalkerAt!.Value.Feet.Z, 3);
            Assert.True(view.WalkerAt!.Value.Feet.X < 5000);
            Assert.Contains("step up of 150 mm", said);
            Assert.Equal(floor + 1200, ((Point3D)typeof(ModelView).GetField("_eye", Hidden)!.GetValue(view)!).Z, 3);

            // On foot: up it.
            view.StopWalking();
            view.Walker = Walker.For(WalkerKind.Adult, false);
            view.StartWalkingAt(new Point2D(4800, 4000), floor, 0);
            for (var i = 0; i < 20; i++) Stride(view, 25, 0);
            Assert.Equal(floor + 150, view.WalkerAt!.Value.Feet.Z, 3);
        });
    }

    [Fact]
    public void WalkingAsAPersonLeavesThemWhereTheWalkEnds()
    {
        OnUiThread(() =>
        {
            var (document, _) = House();
            var window = new MainWindow();
            typeof(MainWindow).GetMethod("LoadDocument", Hidden)!.Invoke(window, new object?[] { document, null, false });
            var plan = (PlanView)typeof(MainWindow).GetField("Plan", Hidden)!.GetValue(window)!;
            var shown = plan.Document!;
            var person = shown.Elements.OfType<Component>().Single(each => Walker.IsPerson(shown.FindType<ComponentType>(each.TypeId)));
            plan.Select(person);

            typeof(MainWindow).GetMethod("OnWalkAsThem", Hidden)!.Invoke(window, new object[] { window, new RoutedEventArgs() });
            var model = (ModelView)typeof(MainWindow).GetField("Model3D", Hidden)!.GetValue(window)!;
            Assert.True(model.IsWalking);
            Assert.Equal(Math.Round(1750 * 0.93), model.Walker.EyeHeight);

            // Two metres towards the front of the house, then stop: they stand there now - a step to undo.
            for (var i = 0; i < 40; i++) Stride(model, 0, -50);
            model.StopWalking();
            Assert.Equal(new Point2D(3000, 2000).X, person.Location.X, 0);
            Assert.InRange(person.Location.Y, 1900, 2100);
            ((UndoStack)typeof(MainWindow).GetField("_history", Hidden)!.GetValue(window)!).Undo();
            Assert.Equal(4000, person.Location.Y, 3);
        });
    }
}
