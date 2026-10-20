using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Parameters;
using BIMDesigner.Core.Views;
using BIMDesigner.Infrastructure.Interoperability;
using BIMDesigner.Infrastructure.Serialization;
using Xbim.Ifc4.Interfaces;
using Xbim.Ifc4.SharedBldgElements;
using Xbim.Ifc4.UtilityResource;

namespace BIMDesigner.Tests;

/// <summary>
/// Windows divided into lights: mullions between lights side by side, a transom with fanlights
/// over, each light fixed or opening - and the rules taken light by light: escape through one
/// light, air through every opener, each pane a pane for safety glass, guarding by the lowest opener.
/// </summary>
public class WindowLightTests
{
    private static WindowType Type(BimDocument document, string name) => document.TypesOf<WindowType>().Single(type => type.Name.StartsWith(name));

    /// <summary>A window of the type named in a 6 m outside wall on the ground floor, 3 m along it.</summary>
    private static (BimDocument Document, Wall Wall, Window Window) Placed(string name, double sill = 900, bool us = false)
    {
        var document = BimDocument.CreateDefault();
        if (us) document.ProjectInformation.ClearanceStandard = ClearanceStandard.Irc;
        var wall = new Wall
        {
            Start = new Point2D(0, 0), End = new Point2D(6000, 0), LevelId = document.Levels[0].Id, UnconnectedHeight = 3000,
            TypeId = document.TypesOf<WallType>().First(type => type.Function == WallFunction.Exterior && type.Log is null).Id
        };
        document.Add(wall);
        var window = new Window { TypeId = Type(document, name).Id, LevelId = wall.LevelId, HostWallId = wall.Id, DistanceAlongWall = 3000, SillHeight = sill };
        document.Add(window);
        return (document, wall, window);
    }

    [Fact]
    public void AnUndividedWindowIsOneLightAsItAlwaysWas()
    {
        var document = BimDocument.CreateDefault();
        var casement = Type(document, "Casement - 1200 x 1200");
        Assert.False(WindowLights.Divided(casement));
        var light = Assert.Single(WindowLights.Of(casement));
        Assert.Equal((60.0, 1140.0, 60.0, 1140.0), (light.From, light.To, light.Bottom, light.Top));
        Assert.Equal((1080.0, 1080.0, true, true), WindowRules.Opening(casement));
        Assert.Empty(WindowLights.Mullions(casement));

        // A bay keeps its own three faces, whatever it is told.
        var bay = Type(document, "Bay");
        bay.Lights = 3;
        Assert.False(WindowLights.Divided(bay));
    }

    [Fact]
    public void AThreeLightCasementHasTwoMullionsAndItsOuterLightsHungApart()
    {
        var document = BimDocument.CreateDefault();
        var type = Type(document, "Casement 3-Light");
        var lights = WindowLights.Of(type);
        Assert.Equal(3, lights.Count);

        // 1770 less the frame each side and two 70 mullions, in three: 503 each.
        var each = (1770 - 120 - 140) / 3.0;
        Assert.All(lights, light => Assert.Equal(each, light.Width, precision: 6));
        Assert.Equal(new[] { 60 + each + 35, 60 + 2 * each + 105 }, WindowLights.Mullions(type).Select(m => Math.Round(m, 6)).ToArray(), new ToleranceComparer());
        Assert.Equal("OFO", WindowLights.Pattern(type));
        Assert.Equal(new[] { true, false, true }, lights.Select(light => light.Opens));
        Assert.True(lights[0].HingeAtStart);
        Assert.False(lights[2].HingeAtStart);

        // Its clear opening is one light's; its air both openers'; its glass all three.
        var (width, height, fully, opens) = WindowRules.Opening(type);
        Assert.Equal(each, width, precision: 6);
        Assert.Equal((1080.0, true, true), (height, fully, opens));
        Assert.Equal(2 * each * 1080 / 1e6, WindowRules.OpenArea(type), precision: 6);
        Assert.Equal(3 * each * 1080 / 1e6, WindowRules.GlassArea(type), precision: 6);
        Assert.True(WindowRules.Escapes(type, 900, us: false, ground: false));
    }

    [Fact]
    public void AMullionStandsInTheWayOfEscape()
    {
        var document = BimDocument.CreateDefault();
        var whole = Type(document, "Casement - 1200 x 1200").Duplicate("Casement 1000");
        whole.Width = 1000;
        Assert.True(WindowRules.Escapes(whole, 900, us: false, ground: false));

        // The same hole as a pair with a mullion between: 405 clear each, under the 450 asked.
        var pair = whole.Duplicate("Casement 2-light 1000");
        pair.Lights = 2;
        pair.LightPattern = "OO";
        Assert.Equal((1000 - 120 - 70) / 2.0, WindowRules.Opening(pair).Width, precision: 6);
        Assert.False(WindowRules.Escapes(pair, 900, us: false, ground: false));
    }

    [Fact]
    public void FanlightsOverATransomOpenPartWayForAir()
    {
        var document = BimDocument.CreateDefault();
        var type = Type(document, "Casement 2-Light, Top-Hung Fanlights");
        Assert.Equal(950, WindowLights.TransomAt(type));
        var lights = WindowLights.Of(type);
        Assert.Equal(4, lights.Count);

        var each = (1200 - 120 - 70) / 2.0;
        var (below, over) = (950 - 35 - 60.0, 1350 - 60 - 985.0);
        Assert.All(lights.Where(light => light.Fanlight), light => Assert.Equal((WindowOperation.Awning, 985.0, 1290.0), (light.Operation, light.Bottom, light.Top)));
        Assert.Equal((each * below / 1e6, 2 * each * over / 1e6), WindowRules.OpenAreas(type), new PairComparer());

        // Below it the lower opener is still the way out: 505 by 855.
        Assert.True(WindowRules.Escapes(type, 900, us: false, ground: false));

        // A transom with no room for a light under it is no transom.
        type.TransomHeight = 200;
        Assert.Null(WindowLights.TransomAt(type));
    }

    [Fact]
    public void AFixedLightWithAFanlightOpensOnlyHighUp()
    {
        var document = BimDocument.CreateDefault();
        var type = Type(document, "Fixed with Top-Hung Fanlight");
        Assert.True(WindowLights.Opens(type));
        Assert.Equal(1135, WindowLights.LowestOpening(type));
        Assert.False(WindowRules.Opening(type).Fully);
        Assert.False(WindowRules.Escapes(type, 300, us: false, ground: false));
        Assert.Equal((0.0, 780 * 305 / 1e6), WindowRules.OpenAreas(type), new PairComparer());
    }

    [Fact]
    public void LowDownItsGlassIsTheGuardingAndItsFanlightNeedsNone()
    {
        // Upstairs over the ground: an opener low down wants guarding; a fanlight high up does not, its fixed glass under it laminated.
        var document = BimDocument.CreateDefault();
        var first = document.Levels[1];
        var floor = new Floor { LevelId = first.Id, TypeId = document.TypesOf<FloorType>().First().Id };
        floor.SetBoundary(new[] { new Point2D(0, -5000), new Point2D(6000, -5000), new Point2D(6000, 0), new Point2D(0, 0) });
        document.Add(floor);
        var wall = new Wall
        {
            Start = new Point2D(0, 0), End = new Point2D(6000, 0), LevelId = first.Id, UnconnectedHeight = 3000,
            TypeId = document.TypesOf<WallType>().First(type => type.Function == WallFunction.Exterior && type.Log is null).Id
        };
        document.Add(wall);

        var fanlight = Type(document, "Fixed with Top-Hung Fanlight");
        var window = new Window { TypeId = fanlight.Id, LevelId = wall.LevelId, HostWallId = wall.Id, DistanceAlongWall = 3000, SillHeight = 300 };
        document.Add(window);
        var said = Assert.Single(OpeningGuarding.Problems(document, window));
        Assert.DoesNotContain("It opens", said);
        Assert.Contains("toughened laminated", said);

        fanlight.GlazingType = "Double glazed, laminated";
        Assert.Empty(OpeningGuarding.Problems(document, window));

        window.TypeId = Type(document, "Casement 3-Light").Id;
        Assert.Contains("It opens", Assert.Single(OpeningGuarding.Problems(document, window)));
    }

    [Fact]
    public void TheIrcCountsEachPaneOnItsOwnForSafetyGlass()
    {
        // Mulled double-hungs 60 in square, 12 in off the floor: each light over 9 sq ft, low and tall.
        var (document, _, window) = Placed("Double Hung Twin", sill: 300, us: true);
        Assert.Contains(WindowRules.Problems(document, window, ClearanceStandard.Irc), problem => problem.Rule.Contains("R308.4.3"));

        // Three lights 503 by 1080 - 0.54 m² each - are none of them a hazard, though together they are 1.6 m².
        window.TypeId = Type(document, "Casement 3-Light").Id;
        Assert.DoesNotContain(WindowRules.Problems(document, window, ClearanceStandard.Irc), problem => problem.Rule.Contains("R308.4.3"));
    }

    [Fact]
    public void ItsLightsAreLimitedToWhatItsWidthHolds()
    {
        var document = BimDocument.CreateDefault();
        var type = Type(document, "Casement - 1200 x 1200").Duplicate("test");
        Assert.Equal(4, WindowLights.MostLights(type));

        var lights = type.GetTypeParameters(document).Single(p => p.Definition == WindowTypeParameters.Lights);
        Assert.False(lights.TrySet(5));
        Assert.True(lights.TrySet(3));
        Assert.Equal(3, type.Lights);

        var pattern = type.GetTypeParameters(document).Single(p => p.Definition == WindowTypeParameters.LightPattern);
        Assert.Equal("OFO", pattern.Value);
        Assert.False(pattern.TrySet("OX"));
        Assert.True(pattern.TrySet("FOF"));
        Assert.Equal(new[] { false, true, false }, WindowLights.Of(type).Select(light => light.Opens));

        var duplicate = type.Duplicate("copy");
        Assert.Equal((3, "FOF"), (duplicate.Lights, duplicate.LightPattern));
    }

    [Fact]
    public void In3DItsMullionsAndTransomAreFrame()
    {
        var (document, _, window) = Placed("Casement 2-Light, Top-Hung Fanlights");
        var frame = ModelMeshBuilder.Build(document).Single(mesh => mesh.ElementId == window.Id && mesh.Description == "Window frame");

        // The mullion's faces, 600 - 35 and 600 + 35 along the window from its start at 2400.
        Assert.Contains(frame.Positions, p => Math.Abs(p.X - (2400 + 565)) < 0.5 && Math.Abs(p.Z - (900 + 60)) < 0.5);
        Assert.Contains(frame.Positions, p => Math.Abs(p.X - (2400 + 635)) < 0.5);

        // The transom's top and bottom, 950 ± 35 up from the sill.
        Assert.Contains(frame.Positions, p => Math.Abs(p.Z - (900 + 985)) < 0.5);
        Assert.Contains(frame.Positions, p => Math.Abs(p.Z - (900 + 915)) < 0.5);
    }

    [Fact]
    public void ItsLightsAreSavedAndExported()
    {
        var (document, _, window) = Placed("Casement 3-Light");
        var type = document.FindType<WindowType>(window.TypeId)!;
        type.TransomHeight = 850;
        type.Fanlight = FanlightOperation.TopHung;
        type.MullionWidth = 84;

        var path = Path.Combine(Path.GetTempPath(), $"bimtest-{Guid.NewGuid():N}{ProjectFile.Extension}");
        try
        {
            ProjectFile.Save(document, path);
            var again = ProjectFile.Load(path).FindType<WindowType>(type.Id)!;
            Assert.Equal((3, "OFO", 850.0, FanlightOperation.TopHung, 84.0), (again.Lights, again.LightPattern, again.TransomHeight, again.Fanlight, again.MullionWidth));
        }
        finally
        {
            File.Delete(path);
        }

        // Three across with fanlights over: six panes, which IFC has no word for but its own.
        IfcWindow Exported(Xbim.Ifc.IfcStore model) =>
            model.Instances.OfType<IfcWindow>().Single(w => w.GlobalId.ToString() == IfcGloballyUniqueId.ConvertToBase64(window.Id).ToString());
        using (var model = IfcExport.Build(document))
        {
            Assert.Equal(IfcWindowTypePartitioningEnum.USERDEFINED, Exported(model).PartitioningType);
            Assert.Contains("3 lights", Exported(model).UserDefinedPartitioningType?.ToString());
        }

        type.TransomHeight = 0;
        using (var model = IfcExport.Build(document))
            Assert.Equal(IfcWindowTypePartitioningEnum.TRIPLE_PANEL_VERTICAL, Exported(model).PartitioningType);
    }

    private sealed class ToleranceComparer : IEqualityComparer<double>
    {
        public bool Equals(double x, double y) => Math.Abs(x - y) < 1e-4;
        public int GetHashCode(double obj) => 0;
    }

    private sealed class PairComparer : IEqualityComparer<(double, double)>
    {
        public bool Equals((double, double) x, (double, double) y) => Math.Abs(x.Item1 - y.Item1) < 1e-6 && Math.Abs(x.Item2 - y.Item2) < 1e-6;
        public int GetHashCode((double, double) obj) => 0;
    }
}
