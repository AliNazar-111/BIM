using BIMDesigner.Core;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Mep;

namespace BIMDesigner.Tests;

/// <summary>
/// Fire sprinklers (round M7): every room of a sprinklered home covered - but those the IRC (P2904.1.1)
/// and BS 9251 leave out - each sprinkler kept from heat and lights as the IRC asks, and the pipes from
/// the valve set sized to give the most demanding room's sprinklers their listed flow.
/// </summary>
public class SprinklerTests
{
    private static ComponentType Type(BimDocument document, string name) => document.TypesOf<ComponentType>().First(type => type.Name == name);

    private static Component Put(BimDocument document, string type, double x, double y, double? elevation = null)
    {
        var family = Type(document, type);
        var component = new Component
        {
            TypeId = family.Id, TypeKind = family.Kind, LevelId = document.Levels[0].Id, Location = new Point2D(x, y), Elevation = elevation ?? family.DefaultElevation
        };
        document.Add(component);
        return component;
    }

    /// <summary>A room walled round, its inside faces from one corner to the other.</summary>
    private static Room Box(BimDocument document, double x0, double y0, double x1, double y1, string name)
    {
        var level = document.Levels[0].Id;
        var wallType = document.TypesOf<WallType>().First();
        var half = wallType.Structure.TotalWidth / 2;
        var corners = new[] { new Point2D(x0 - half, y0 - half), new Point2D(x1 + half, y0 - half), new Point2D(x1 + half, y1 + half), new Point2D(x0 - half, y1 + half) };
        for (var i = 0; i < 4; i++)
            document.Add(new Wall { Start = corners[i], End = corners[(i + 1) % 4], TypeId = wallType.Id, LevelId = level, UnconnectedHeight = 2400 });
        var room = new Room { LevelId = level, Location = new Point2D((x0 + x1) / 2, (y0 + y1) / 2), Name = name, Number = name[..1] };
        document.Add(room);
        return room;
    }

    private static Point3D Top(BimDocument document, Component fitting) =>
        Connectors.Placed(document, fitting).First(placed => placed.Connector.Kind == SystemKind.Sprinkler).At;

    private static Pipe Run(BimDocument document, Point3D from, Point3D to, MepSystem system, double diameter = 22)
    {
        var pipe = new Pipe
        {
            TypeId = document.TypesOf<PipeType>().First(type => type.Material == PipeMaterial.Copper).Id, LevelId = document.Levels[0].Id,
            Start = new Point2D(from.X, from.Y), End = new Point2D(to.X, to.Y), StartOffset = from.Z, EndOffset = to.Z,
            Diameter = diameter, Kind = SystemKind.Sprinkler, SystemId = system.Id
        };
        document.Add(pipe);
        return pipe;
    }

    [Fact]
    public void HazenWilliamsIsTheSameInLitresAndGallons()
    {
        // 26 gpm through 3/4" copper's 0.785 in bore, C 150: 4.52 Q^1.85 / (C^1.85 d^4.87) psi a foot.
        var psiPerFoot = 4.52 * Math.Pow(26, 1.85) / (Math.Pow(150, 1.85) * Math.Pow(0.785, 4.87));
        var barPerMetre = Sprinklers.Loss(26 * Units.LitresPerGallon, 0.785 * Units.MillimetresPerInch, 150);
        Assert.Equal(psiPerFoot, barPerMetre * Sprinklers.PsiPerBar * 0.3048, 3);

        // NFPA 13's elbow and tee, scaled for C 150; a K 4.9 sprinkler's 13 gpm at 7 psi.
        Assert.Equal(2 * 0.3048 * 1.51, Sprinklers.Equivalent(PipeFitting.Elbow, 20, 150), 6);
        Assert.Equal(5 * 0.3048, Sprinklers.Equivalent(PipeFitting.Tee, 26, 120), 6);
        var pendent = Type(BimDocument.CreateDefault(), "Sprinkler - Residential Pendent");
        Assert.Equal(7.0, Sprinklers.PressureFor(pendent, Sprinklers.ListedFlow(pendent)) * Sprinklers.PsiPerBar, 1);
    }

    [Fact]
    public void EveryRoomOfASprinkleredHomeHasItsSprinklersButASmallBathroom()
    {
        var document = BimDocument.CreateDefault();
        document.ProjectInformation.Sprinklered = true;
        var living = Box(document, 0, 0, 4000, 3000, "Living Room");
        var bathroom = Box(document, 5000, 0, 7000, 2000, "Bathroom");
        var long1 = Box(document, 0, 4000, 9000, 7000, "Kitchen Diner");
        bool Wanting(Room room, string said) => MepSystems.Problems(document).Any(problem => problem.Element == room && problem.Text.Contains(said));

        Assert.True(Wanting(living, "no sprinkler - BS 9251"));
        Assert.False(Wanting(bathroom, "no sprinkler"));

        // One sprinkler in the middle of the living room covers it, 16 ft square; one in the end of the kitchen diner does not.
        Put(document, "Sprinkler - Residential Pendent", 2000, 1500);
        Put(document, "Sprinkler - Residential Pendent", 2000, 5500);
        Assert.False(Wanting(living, "sprinkler"));
        Assert.True(Wanting(long1, "beyond its sprinklers' coverage"));
        Put(document, "Sprinkler - Residential Pendent", 6500, 5500);
        Assert.False(Wanting(long1, "coverage"));

        // In the US: a bathroom of 55 sq ft or less, and a garage, left out.
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.Irc;
        var garage = Box(document, 10000, 0, 16000, 6000, "Garage");
        Assert.False(Wanting(bathroom, "sprinkler"));
        Assert.False(Wanting(garage, "sprinkler"));
        Assert.Equal("a garage, carport, porch or mud room (P2904.1.1)", Sprinklers.Exempt(document, garage, garage.GetBoundary(document).Polygon));
    }

    [Fact]
    public void AUsSprinklerIsKeptFromAFireplaceAndAwayFromACeilingLight()
    {
        var document = BimDocument.CreateDefault();
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.Irc;
        Box(document, 0, 0, 5000, 4000, "Family Room");
        Put(document, "Fireplace - Gas Direct Vent 36 in", 2500, 300);
        var head = Put(document, "Sprinkler - Residential Pendent", 2500, 900);
        IEnumerable<string> Said() => MepSystems.Problems(document).Where(problem => problem.Element == head).Select(problem => problem.Text);

        // Over the fireplace's front, 52 in up from its top: an ordinary temperature sprinkler, within Table P2904.2.2's 60 in.
        Assert.Contains(Said(), said => said.Contains("an intermediate temperature one (175 to 225 °F) within 60 in"));
        head.TypeId = Type(document, "Sprinkler - Residential Pendent, Intermediate").Id;
        Assert.DoesNotContain(Said(), said => said.Contains("Table P2904.2.2"));

        // A ceiling light 2 ft from it obstructs it.
        Put(document, "Ceiling Light - Flush", 2500, 1500);
        Assert.Contains(Said(), said => said.Contains("P2904.2.4.2.1"));
    }

    /// <summary>A long room with the valve set at one end and two sprinklers 20 m off at the other, all in 22 mm copper.</summary>
    private static (BimDocument Document, MepSystem System, Component Valve, Pipe Main) Long(double supply)
    {
        var document = BimDocument.CreateDefault();
        Box(document, -1000, -2000, 23000, 2000, "Hall");
        var valve = Put(document, "Sprinkler Valve Set - 1 in", 0, 0);
        Type(document, "Sprinkler Valve Set - 1 in").SupplyPressure = supply;
        var a = Put(document, "Sprinkler - Residential Pendent", 20000, 1000);
        var b = Put(document, "Sprinkler - Residential Pendent", 20000, -1000);
        var (system, refusal) = MepSystems.Make(document, SystemKind.Sprinkler, new[] { valve, a, b });
        Assert.True(system is not null, refusal);
        document.Add(system!);

        var start = Top(document, valve);
        var up = new Point3D(start.X, start.Y, Top(document, a).Z);
        var tee = new Point3D(20000, 0, up.Z);
        Run(document, start, up, system!);
        var main = Run(document, up, tee, system!);
        Run(document, tee, Top(document, a), system!);
        Run(document, tee, Top(document, b), system!);
        return (document, system!, valve, main);
    }

    [Fact]
    public void ASprinklerSystemIsSizedSoItsSupplyMeetsItsMostDemandingRoom()
    {
        var (document, system, valve, main) = Long(2.0);
        var network = PipeNetworks.Trace(document, system);
        var worst = Sprinklers.Demands(document, network)[0];

        // Both sprinklers in the hall open, each at its listed 13 gpm (P2904.4.2): 98 l/min down the main.
        Assert.Equal(2, worst.Flowing.Count);
        Assert.Equal(2 * 13 * Units.LitresPerGallon, worst.Pipes[main].Flow, 6);
        Assert.Contains(MepSystems.Problems(document), problem => problem.Element == valve && problem.Text.Contains("its supply gives 2.00 bar"));

        // Sized: the main taken up until the supply meets it.
        foreach (var (pipe, diameter) in PipeNetworks.Sized(document, new[] { system })) pipe.Diameter = diameter;
        Assert.True(main.Diameter > 22);
        Assert.DoesNotContain(MepSystems.Problems(document), problem => problem.Element == valve);
        Assert.Contains("design flow 98 l/min for 10 minutes (984 l)", Sprinklers.Summary(document, system));
        Assert.Contains("l/min when the Hall's sprinklers open", PipeNetworks.Describe(document, main));

        // With no supply pressure given, it is asked for.
        Type(document, "Sprinkler Valve Set - 1 in").SupplyPressure = 0;
        Assert.Contains(MepSystems.Problems(document), problem => problem.Element == valve && problem.Text.Contains("supply's pressure is not given"));

        // In the US the project's water pressure is taken, and a home of one storey under 2,000 sq ft runs 7 minutes.
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.Irc;
        document.ProjectInformation.WaterPressure = 60;
        Assert.Equal(60 / Sprinklers.PsiPerBar, Sprinklers.Supply(document, system)!.Value, 6);
        Assert.Equal(7, Sprinklers.Minutes(document));
        Assert.Contains("design flow 26 gpm for 7 minutes (182 gal)", Sprinklers.Summary(document, system));
    }
}
