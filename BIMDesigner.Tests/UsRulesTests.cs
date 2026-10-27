using System.Reflection;
using System.Windows;
using BIMDesigner.Core;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Mep;
using BIMDesigner.Core.Parameters;
using BIMDesigner.Infrastructure.Serialization;
using BIMDesigner.UI;
using BIMDesigner.UI.Controls;

namespace BIMDesigner.Tests;

/// <summary>
/// A US project checked by its own rules where an English one has Approved Documents and British
/// Standards: drains sized by the IRC's tables with cleanouts, wet rooms extracted at M1505.4.4's
/// rates, ceilings at R305.1's heights, a flat roof at ¼ in a foot, heating pipes as ASHRAE sizes
/// them; and said in its own units - cfm, in. w.c., Btu/h, °F, psf, pounds - and offered its own
/// catalogue.
/// </summary>
[Collection("Wpf")]
public class UsRulesTests
{
    private static ComponentType Type(BimDocument document, string name) => document.TypesOf<ComponentType>().First(type => type.Name == name);

    private static Component Put(BimDocument document, string type, double x, double y, double elevation = double.NaN, double rotation = 0)
    {
        var family = Type(document, type);
        var component = new Component
        {
            TypeId = family.Id, TypeKind = family.Kind, LevelId = document.Levels[0].Id, Location = new Point2D(x, y), Rotation = rotation,
            Elevation = double.IsNaN(elevation) ? family.DefaultElevation : elevation
        };
        document.Add(component);
        return component;
    }

    private static Point3D At(double x, double y, double z) => new(x, y, z);

    private static Point3D WasteOf(BimDocument document, Component fitting) =>
        Connectors.Placed(document, fitting).First(placed => placed.Connector.Kind == SystemKind.Foul).At;

    /// <summary>A Schedule 40 PVC DWV pipe of so many inches, heights from the ground floor's level.</summary>
    private static Pipe Dwv(BimDocument document, Point3D from, Point3D to, MepSystem system, double inches)
    {
        var type = document.TypesOf<PipeType>().First(each => each.Material == PipeMaterial.PvcDwv);
        var pipe = new Pipe
        {
            TypeId = type.Id, LevelId = document.Levels[0].Id, Start = new Point2D(from.X, from.Y), End = new Point2D(to.X, to.Y), StartOffset = from.Z, EndOffset = to.Z,
            Diameter = PipeSizes.Of(PipeMaterial.PvcDwv).First(size => size.Name == IrcWaterSizing.Name(inches)).Outside, Kind = system.Kind, SystemId = system.Id
        };
        document.Add(pipe);
        return pipe;
    }

    private static MepSystem Make(BimDocument document, SystemKind kind, params Component[] picked)
    {
        var (system, refusal) = MepSystems.Make(document, kind, picked);
        Assert.True(system is not null, refusal);
        document.Add(system!);
        return system!;
    }

    /// <summary>A branch coming into a stack at a height: the stack cut there, so the branch's end meets it.</summary>
    private static Point3D Tee(BimDocument document, Pipe stack, double z)
    {
        var piece = document.All<Pipe>().First(pipe => pipe.Upright && pipe.Start == stack.Start && Math.Min(pipe.StartOffset, pipe.EndOffset) < z && z < Math.Max(pipe.StartOffset, pipe.EndOffset));
        new SplitPipeCommand(document, piece, piece.Start, z).Redo();
        return At(stack.Start.X, stack.Start.Y, z);
    }

    /// <summary>A US house's stack at (0, 0) from below the ground up to 5 m, and a building drain from its foot 12 m to the sewer at ¼ in a foot.</summary>
    private static (BimDocument Document, MepSystem Foul, Pipe Stack, Pipe Drain) Stack(double stackInches, double drainInches)
    {
        var document = BimDocument.CreateDefault();
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.Irc;
        var outfall = Put(document, "Outfall - Foul Sewer", 12000, 0, 0);
        var foul = Make(document, SystemKind.Foul, outfall);
        var foot = At(0, 0, -600 + 12000 / 48.0);
        var drain = Dwv(document, foot, At(12000, 0, -600), foul, drainInches);
        var stack = Dwv(document, foot, At(0, 0, 5000), foul, stackInches);
        return (document, foul, stack, drain);
    }

    private static void Branch(BimDocument document, MepSystem foul, Pipe stack, Component fitting, double inches, double drop = 100)
    {
        new EditSystemCommand(foul, "Add", system => system.Members.Add(fitting.Id)).Redo();
        var waste = WasteOf(document, fitting);
        var plan = new Point2D(waste.X, waste.Y).DistanceTo(stack.Start);
        Dwv(document, waste, Tee(document, stack, waste.Z - drop - plan / 48), foul, inches);
    }

    [Fact]
    public void TheUnitsSayAirflowPressureHeatAndLoadsAsAUsDrawingDoesAndMetricAsBefore()
    {
        using (Units.Use(UnitSystem.Metric))
        {
            Assert.Equal("24 l/s", Units.Airflow(23.6, "0"));
            Assert.Equal("62 Pa", Units.Pascals(62.27, "0"));
            Assert.Equal("1,000 W", Units.Watts(1000, "#,0"));
            Assert.Equal("55/45 °C", Units.FlowAndReturn(55, 45));
            Assert.Equal("U 0.18 W/m²K", Units.UValue(0.18, "0.00"));
            Assert.Equal("1.0 kN", Units.Kilonewtons(1, "0.0"));
            Assert.Equal("1.00 kPa", Units.Kilopascals(1, "0.00"));
            Assert.Equal("30.5 m", Units.Run(30480, "0.0"));
            Assert.Equal("21 mm a metre", Units.Fall(20.83, "0"));
        }

        using (Units.Use(UnitSystem.Imperial))
        {
            Assert.Equal("50 cfm", Units.Airflow(23.6, "0"));
            Assert.Equal("0.25 in. w.c.", Units.Pascals(62.27, "0"));
            Assert.Equal("3,412 Btu/h", Units.Watts(1000, "#,0"));
            Assert.Equal("131/113 °F", Units.FlowAndReturn(55, 45));
            Assert.Equal("U-factor 0.032", Units.UValue(0.18, "0.00"));
            Assert.Equal("225 lb", Units.Kilonewtons(1, "0.0"));
            Assert.Equal("20.9 psf", Units.Kilopascals(1, "0.00"));
            Assert.Equal("100 ft", Units.Run(30480, "0.0"));
            Assert.Equal("1/4\" a foot", Units.Fall(1000.0 / 48, "0"));

            // The wind set on cladding, and a fitting's type said and given in US units.
            var document = BimDocument.CreateDefault();
            document.ProjectInformation.ClearanceStandard = ClearanceStandard.Irc;
            document.ProjectInformation.WindPressure = 1;
            Assert.StartsWith("20.9 psf on all cladding, as set", Wind.Verdict(document));
            var unit = Type(document, "HRV - 150 cfm");
            var airflow = unit.GetTypeParameters(document).Single(parameter => parameter.Name == "Most Airflow (cfm)");
            Assert.Equal(150.0, (double)airflow.Value!, 6);
            Assert.True(airflow.TrySetFromText("200"));
            Assert.Equal(200 * Units.LitresPerSecondPerCfm, unit.Airflow, 6);
            Assert.Contains(Type(document, "Boiler - Gas Combi").GetTypeParameters(document), parameter => parameter.Name == "Heat Output (Btu/h)");
        }
    }

    [Fact]
    public void InTheUsDrainsAreSizedByTheIrcsTables()
    {
        // The tables as the code has them: Table P3005.4.1's branches and stacks, Table P3005.4.2's building drains by their fall.
        Assert.Equal(3, Drainage.UsCarries(1.5, upright: false, belowGround: false, fall: 1000.0 / 48));
        Assert.Equal(48, Drainage.UsCarries(3, upright: true, belowGround: false, fall: 1000.0 / 48));
        Assert.Equal(36, Drainage.UsCarries(3, upright: false, belowGround: true, fall: 1000.0 / 96));
        Assert.Equal(42, Drainage.UsCarries(3, upright: false, belowGround: true, fall: 1000.0 / 48));
        Assert.Equal(250, Drainage.UsCarries(4, upright: false, belowGround: true, fall: 1000.0 / 24));
        Assert.Equal(0, Drainage.UsCarries(2, upright: false, belowGround: true, fall: 1000.0 / 96));
        Assert.Null(Drainage.UsCarries(6, upright: false, belowGround: true, fall: 1000.0 / 96));
        Assert.Equal((1.5, 4.0), (Drainage.UsNominal(40), Drainage.UsNominal(100)));

        // Table P3201.7's traps, and the pipe a fixture's waste starts in.
        var document = BimDocument.CreateDefault();
        Assert.Equal((1.25, 1.5, 2.0, 3.0), (Drainage.UsTrap(Type(document, "Basin - Pedestal")), Drainage.UsTrap(Type(document, "Bathtub - 60 x 30 in")),
            Drainage.UsTrap(Type(document, "Washing Machine - 600")), Drainage.UsTrap(Type(document, "WC - Close Coupled"))));
        Assert.Equal("2\"", Drainage.UsDrainSize(Type(document, "Washing Machine - 600"), 40).Name);
        Assert.Equal("4\"", Drainage.UsDrainSize(null, 100).Name);

        // A WC on a 2 in soil pipe, a bathtub on a 1¼ in waste, the 4 in stack onto a 3 in building drain.
        var (house, foul, stack, drain) = Stack(stackInches: 4, drainInches: 3);
        var wc = Put(house, "WC - Close Coupled", 0, 1500, rotation: 180);
        var tub = Put(house, "Bathtub - 60 x 30 in", 0, -2000);
        Branch(house, foul, stack, wc, 2);
        Branch(house, foul, stack, tub, 1.25, drop: 300);
        var problems = MepSystems.Problems(house);
        Assert.Contains(problems, problem => problem.Element is Pipe && problem.Text.Contains("it carries a WC at 2 in - the IRC takes a water closet's drain 3 in at the least"));
        Assert.Contains(problems, problem => problem.Element == tub && problem.Text.Contains("its waste pipe is 1¼ in - IRC Table P3201.7 gives it a 1½ in trap"));
        Assert.Contains(problems, problem => problem.Element == drain && problem.Text.Contains("it is 3 in, smaller than the 4 in drain running into it") && problem.Text.Contains("(P3005.4)"));
        Assert.DoesNotContain(problems, problem => problem.Text.Contains("Approved Document H"));

        // A 1½ in stack taking a bathtub, a lavatory and a sink - 5 fixture units, past the 4 Table P3005.4.1 lets it.
        var (small, smallFoul, narrow, _) = Stack(stackInches: 1.5, drainInches: 4);
        Branch(small, smallFoul, narrow, Put(small, "Bathtub - 60 x 30 in", 0, -2000), 1.5);
        Branch(small, smallFoul, narrow, Put(small, "Basin - Pedestal", 0, 2000), 1.25);
        Branch(small, smallFoul, narrow, Put(small, "Sink Unit - 600", 2000, 0, rotation: 90), 1.5, drop: 200);
        var smallProblems = MepSystems.Problems(small);
        Assert.True(smallProblems.Any(problem => problem.Element is Pipe { Upright: true }
            && problem.Text.Contains("it carries 5 drainage fixture units at 1½ in - IRC Table P3005.4.1 takes 4 on a stack of that size; 2 in carries them")),
            string.Join(" | ", smallProblems.Select(problem => problem.Text)));
    }

    [Fact]
    public void InTheUsABuildingDrainHasItsCleanouts()
    {
        var document = BimDocument.CreateDefault();
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.Irc;
        var outfall = Put(document, "Outfall - Foul Sewer", 40000, 10000, 0);
        var foul = Make(document, SystemKind.Foul, outfall);

        // 50 m of 4 in building drain from a bare head: 40 m along, a bend, 10 m to the sewer - at ¼ in a foot.
        double Z(double along) => -2000 + (50000 - along) / 48;
        var first = Dwv(document, At(0, 0, Z(0)), At(40000, 0, Z(40000)), foul, 4);
        var second = Dwv(document, At(40000, 0, Z(40000)), At(40000, 10000, Z(50000)), foul, 4);
        var problems = MepSystems.Problems(document);
        Assert.Contains(problems, problem => problem.Element == first && problem.Text.Contains("of building drain with no cleanout along it - IRC P3005.2.1 spaces them 100 ft"));
        Assert.Contains(problems, problem => problem.Element == second && problem.Text.Contains("it turns 90° with no cleanout there or in the 40 ft") && problem.Text.Contains("P3005.2.4"));
        Assert.DoesNotContain(problems, problem => problem.Text.Contains("Table 13") || problem.Text.Contains("Approved Document H"));

        // Cleanouts at its head, 25 m along and at the bend.
        new SplitPipeCommand(document, first, new Point2D(25000, 0)).Redo();
        foreach (var x in new[] { 0.0, 25000, 40000 }) Put(document, "Rodding Eye - 100", x, 0, 0);
        Assert.DoesNotContain(MepSystems.Problems(document), problem => problem.Text.Contains("P3005.2"));

        // The house's outside wall across it 5 m from its head: a cleanout wanted within 10 ft upstream of where it leaves.
        var outside = document.TypesOf<WallType>().First(type => type.Function == WallFunction.Exterior);
        document.Add(new Wall { Start = new Point2D(5000, -3000), End = new Point2D(5000, 3000), TypeId = outside.Id, LevelId = document.Levels[0].Id, UnconnectedHeight = 3000 });
        Assert.Contains(MepSystems.Problems(document), problem => problem.Text.Contains("no cleanout where it leaves the building - IRC P3005.2.3"));
        new SplitPipeCommand(document, document.All<Pipe>().First(pipe => pipe.Start.X < 1 && !pipe.Upright), new Point2D(3000, 0)).Redo();
        Put(document, "Rodding Eye - 100", 3000, 0, 0);
        Assert.DoesNotContain(MepSystems.Problems(document), problem => problem.Text.Contains("P3005.2"));
    }

    [Fact]
    public void InTheUsAWetRoomIsExtractedContinuouslyOrWhenItsFanRuns()
    {
        var (document, rooms, _) = VentilationTests.Row("Generic - 200mm", "Bathroom");
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.Irc;

        // A fan running at 17 cfm, boosting to 32: neither the 20 cfm continuous nor the 50 when it runs.
        var fan = VentilationTests.Put(document, "Extract Fan - Continuous dMEV", rooms[0].Location.X, 2800);
        Assert.Equal(VentilationType.ContinuousExtract, Ventilation.TypeOf(document));
        var problems = MepSystems.Problems(document);
        Assert.Contains(problems, problem => problem.Element == rooms[0] && problem.Text.Contains("IRC Table M1505.4.4 asks 20 cfm continuously, or 50 cfm when it runs"));
        Assert.DoesNotContain(problems, problem => problem.Text.Contains("Approved Document F"));
        Assert.Contains("IRC Table M1505.4.4 asks 20 cfm continuously", Services.Verdict(document, rooms[0]));

        // Boosting to 50 cfm when it runs: enough.
        Type(document, "Extract Fan - Continuous dMEV").ExtractRate = 50 * Ventilation.LitresPerCfm;
        Assert.DoesNotContain(MepSystems.Problems(document), problem => problem.Text.Contains("M1505.4.4"));
        _ = fan;
    }

    [Fact]
    public void InTheUsHeatingPipesAreSizedAsAshraeSizesThemAndHeatIsInBtu()
    {
        // 0.075 kg/s at 50 °C: 15 mm copper loses more than CIBSE's 300 Pa/m, within ASHRAE's 4 ft a 100 ft.
        Assert.Equal("18 x 1.0", HeatingPipes.Required(PipeMaterial.Copper, 0.075, 50)!.Name);
        Assert.Equal("15 x 1.0", HeatingPipes.Required(PipeMaterial.Copper, 0.075, 50, us: true)!.Name);
        Assert.Equal(4 * 0.3048, HeatingPipes.FastestIn(28, us: true), 6);
        Assert.True(double.IsPositiveInfinity(HeatingPipes.FastestIn(76.1, us: true)));
    }

    [Fact]
    public void InTheUsARoomsCeilingIsAtTheIrcsHeights()
    {
        // 20 m² under a sloping ceiling: the IRC's required 70 sq ft at 5 ft, and half of it at 7 ft.
        var meets = new RoomSpace(20e6, 50e9, 1000, 3000, 10e6, 4e6, true, true);
        Assert.StartsWith("Meets it: 4.0 m² at 7 ft or more", RoomHeadroom.UsVerdict(meets, RoomHeadroom.UsCeiling));
        var short_ = meets with { AreaFullHeight = 3e6 };
        Assert.Contains("the IRC (R305.1) asks 7 ft over the room", RoomHeadroom.UsVerdict(short_, RoomHeadroom.UsCeiling));
        Assert.Contains("6 ft 8 in", RoomHeadroom.UsVerdict(short_, RoomHeadroom.UsWetCeiling));

        // A room 2 m tall in a US house: short of 7 ft, said under the IRC's heights rather than the space standard's.
        var (document, rooms, _) = VentilationTests.Row("Generic - 200mm", "Bedroom");
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.Irc;
        rooms[0].UpperLimitOffset = 2000;
        document.All<Roof>().Single().HeightOffset = 2000;
        var said = rooms[0].GetInstanceParameters(document).ToList();
        Assert.Contains(said, parameter => parameter.Name == "Area over 7 ft Headroom");
        Assert.StartsWith("Short:", (string)said.Single(parameter => parameter.Name == "Ceiling Height (IRC R305.1)").Value!);
        Assert.DoesNotContain(said, parameter => parameter.Name.Contains("Space Standard"));
    }

    [Fact]
    public void InTheUsAFlatRoofFallsAQuarterInchAFootAndAWcRoomIsOnlyDescribed()
    {
        var (document, rooms, _) = VentilationTests.Row("Generic - 200mm", "WC");
        var roof = document.All<Roof>().Single();
        VentilationTests.Put(document, "WC - Close Coupled", rooms[0].Location.X, 2600, 180);
        Assert.Contains("Approved Document G", Bathrooms.Verdict(document, rooms[0]));

        document.ProjectInformation.ClearanceStandard = ClearanceStandard.Irc;
        Assert.Equal("WC: WC", Bathrooms.Verdict(document, rooms[0]));
        roof.DrainageFall = 40;
        Assert.Contains("¼ in a foot or steeper, as the IRC asks", RoofDrainage.FallCheck(document, roof));
        roof.DrainageFall = 60;
        Assert.Contains("Falls at only 1 in 60: the IRC lays a low-slope roof covering to ¼ in a foot", RoofDrainage.FallCheck(document, roof));
    }

    [Fact]
    public void AUsProjectIsOfferedItsOwnCatalogueAndAnEnglishOneItsOwn()
    {
        var document = BimDocument.CreateDefault();
        ElementType Named(string name) => document.ElementTypes.First(type => type.Name == name);
        string[] us = { "Interior Door - 32 x 80 in", "Double Hung - 36 x 60 in", "Garage Entry Door - 36 x 80 in, 20 min", "Bed - Queen 60 x 80 in", "Bathtub - 60 x 30 in", "Parking Space - 9 x 18 ft", "Round - Galvanized Steel (in)", "PVC DWV - Schedule 40 (ASTM D2665)" };
        string[] english = { "Single - 900 x 2100", "Casement - 1200 x 1200", "Bed - King 1500 x 2000", "Bath - 1700 x 700", "Parking Bay - 2400 x 4800", "Round - Galvanised Spiral", "PVC-U Drain - BS EN 1401" };
        Assert.All(us, name => Assert.False(ProjectTemplates.Fits(document, Named(name)), name));
        Assert.All(english, name => Assert.True(ProjectTemplates.Fits(document, Named(name)), name));
        Assert.True(ProjectTemplates.Fits(document, Named("Chair - Dining")));

        document.ProjectInformation.ClearanceStandard = ClearanceStandard.Irc;
        Assert.All(us, name => Assert.True(ProjectTemplates.Fits(document, Named(name)), name));
        Assert.All(english, name => Assert.False(ProjectTemplates.Fits(document, Named(name)), name));

        // Ducts in whole inches, kept so in the file.
        var round = document.TypesOf<DuctType>().First(type => type.Name == "Round - Galvanized Steel (in)");
        Assert.Equal(new[] { "4\"", "5\"", "6\"" }, DuctSizes.Of(round).Take(3).Select(size => size.Name));
        Assert.Equal(6 * 25.4, DuctSizes.Of(round)[2].Width, 6);
        var reopened = ProjectFile.FromJson(ProjectFile.ToJson(document));
        Assert.True(reopened.TypesOf<DuctType>().First(type => type.Name == round.Name).Inches);
        Assert.False(reopened.TypesOf<DuctType>().First(type => type.Name == "Round - Galvanised Spiral").Inches);
    }

    [Fact]
    public void AUsPlanIsAskedItsOwnSettingsInItsOwnUnitsAndItsChimneysStandTo3_2_10()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (Application.Current is null) new App().InitializeComponent();
                const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
                var window = new MainWindow();
                var document = BimDocument.CreateDefault();
                document.ProjectInformation.ClearanceStandard = ClearanceStandard.Irc;
                document.ProjectInformation.Units = UnitSystem.Imperial;
                typeof(MainWindow).GetMethod("LoadDocument", hidden)!.Invoke(window, new object?[] { document, null, false });
                var plan = (PlanView)typeof(MainWindow).GetField("Plan", hidden)!.GetValue(window)!;
                var shown = (BimDocument)plan.Document!;
                List<ParameterValue> Parameters() => ((IEnumerable<ParameterValue>)typeof(MainWindow).GetMethods(hidden)
                    .First(method => method.Name == "PlanViewParameters").Invoke(window, new object[] { "First Floor" })!).ToList();

                var names = Parameters().Select(parameter => parameter.Name).ToList();
                Assert.Contains("Frost Line (US, 0 = none)", names);
                Assert.Contains("Site Altitude (ft above sea level)", names);
                Assert.DoesNotContain("Clay Shrinkage", names);
                Assert.DoesNotContain("Water Use 110 l (Optional Requirement)", names);
                Assert.DoesNotContain("Heating Design Location (UK)", names);
                Assert.True(Parameters().Single(parameter => parameter.Name == "Wind Pressure on Cladding (psf, with no wind speed)").TrySetFromText("20.9"));
                Assert.Equal(1.0, shown.ProjectInformation.WindPressure, 2);

                // A chimney put up in it stands to the IRC's 3-2-10 rule.
                var chimney = (Chimney)typeof(PlanView).GetMethod("NewChimney", hidden)!.Invoke(plan, new object[] { shown.Levels[0].Id, new Point2D(0, 0) })!;
                Assert.Equal(ChimneyRule.ThreeTwoTen, chimney.Rule);
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Exception("Failed on the UI thread.", failure);
    }
}
