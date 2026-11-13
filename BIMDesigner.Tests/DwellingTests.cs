using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Mep;
using BIMDesigner.Core.Views;
using BIMDesigner.Infrastructure.Interoperability;
using BIMDesigner.Infrastructure.Serialization;
using Xbim.Ifc4.Interfaces;

namespace BIMDesigner.Tests;

/// <summary>
/// Dwellings (round 77): rooms named into the flats of a block or the houses of a terrace, coloured and measured by them,
/// and a foul drain's flow by how many dwellings it serves, as Approved Document H Table 5 gives it, its size by 2.30.
/// </summary>
[Collection("Wpf")]
public class DwellingTests
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

    private static Pipe Run(BimDocument document, Point3D from, Point3D to, MepSystem system, double bore)
    {
        var type = document.TypesOf<PipeType>().First(each => each.Material == PipeMaterial.PvcU);
        var pipe = new Pipe
        {
            TypeId = type.Id, LevelId = document.Levels[0].Id, Start = new Point2D(from.X, from.Y), End = new Point2D(to.X, to.Y), StartOffset = from.Z, EndOffset = to.Z,
            Diameter = Outside(bore), Kind = system.Kind, SystemId = system.Id
        };
        document.Add(pipe);
        return pipe;
    }

    private static double Outside(double bore) => PipeSizes.Of(PipeMaterial.PvcU).First(size => size.Inside == bore).Outside;

    /// <summary>
    /// A row of flats, each a 3 m square cell of room separators from x = 0 eastward, its bathroom given the dwelling "Flat n"
    /// (or none) and a fitting in it; each fitting's waste straight down to a drain along y = -500 falling 1:40 east to the
    /// sewer's outfall at x = 40 m. The drain's lengths, west to east: the first between the first two flats' branches, the
    /// last from the last flat's to the outfall - the one before it carrying all but the last flat.
    /// </summary>
    private static (BimDocument Document, MepSystem Foul, List<Pipe> Drain, List<Room> Rooms) Block(int flats, string fitting = "WC - Close Coupled", double bore = 100, bool named = true)
    {
        var document = BimDocument.CreateDefault();
        var level = document.Levels[0].Id;
        void Line(double x1, double y1, double x2, double y2) => document.Add(new RoomSeparator { LevelId = level, Start = new Point2D(x1, y1), End = new Point2D(x2, y2) });

        var rooms = new List<Room>();
        var fittings = new List<Component>();
        for (var i = 0; i < flats; i++)
        {
            var west = 3000.0 * i;
            Line(west, 0, west + 3000, 0);
            Line(west, 3000, west + 3000, 3000);
            Line(west, 0, west, 3000);
            if (i == flats - 1) Line(west + 3000, 0, west + 3000, 3000);
            var room = new Room { LevelId = level, Location = new Point2D(west + 1500, 2500), Name = "Bathroom", Number = $"{i + 1}", Dwelling = named ? $"Flat {i + 1}" : string.Empty };
            document.Add(room);
            rooms.Add(room);
            fittings.Add(Put(document, fitting, west + 1500, 1500, rotation: 180));
        }

        var outfall = Put(document, "Outfall - Foul Sewer", 40000, -500, 0);
        var (foul, refusal) = MepSystems.Make(document, SystemKind.Foul, new[] { outfall }.Concat(fittings).ToArray());
        Assert.True(foul is not null, refusal);
        document.Add(foul!);

        double Invert(double x) => -1000 - (x - 1500) / 40;
        var drain = new List<Pipe>();
        Point3D? last = null;
        foreach (var piece in fittings)
        {
            var waste = Connectors.Placed(document, piece).First(placed => placed.Connector.Kind == SystemKind.Foul).At;
            var joins = new Point3D(waste.X, -500, Invert(waste.X));
            Run(document, waste, joins, foul!, Math.Max(bore, 75));
            if (last is { } from) drain.Add(Run(document, from, joins, foul!, bore));
            last = joins;
        }

        drain.Add(Run(document, last!.Value, new Point3D(40000, -500, Invert(40000)), foul!, bore));
        return (document, foul!, drain, rooms);
    }

    [Fact]
    public void Table5GivesTheFlowByHowManyDwellings()
    {
        // Straight off Approved Document H Table 5 at its rows; between them on the straight line - three dwellings
        // 2.5 + (3.5 - 2.5) × 2/4 = 3.0, seven 3.5 + 0.6 × 2/5 = 3.74, twelve 4.1 + 0.5 × 2/5 = 4.3; past thirty its last.
        Assert.Equal(new[] { 2.5, 2.5, 3.0, 3.5, 3.74, 4.1, 4.3, 4.6, 5.1, 5.4, 5.8, 5.8 },
            new[] { 0, 1, 3, 5, 7, 10, 12, 15, 20, 25, 30, 45 }.Select(n => Math.Round(Dwellings.Flow(n), 6)));

        // Each dwelling named once, whatever its case or spaces; rooms of none count as one only where nothing else is named.
        Assert.Equal(1, Dwellings.Count(new[] { "", "" }));
        Assert.Equal(1, Dwellings.Count(new[] { "Flat 1", "flat 1", "" }));
        Assert.Equal(2, Dwellings.Count(new[] { "Flat 1", "Flat 2", "" }));
        Assert.Equal(0, Dwellings.Count(Array.Empty<string>()));
    }

    [Fact]
    public void ADrainCarriesTheFlowOfTheFlatsItServesAndASewerForMoreThanTenIs150()
    {
        var (document, foul, drain, _) = Block(12);
        var loads = PipeNetworks.Trace(document, foul).Loads;

        // Each length of the drain serves the flats west of it: the fifth five flats, 3.5 l/s; the last all twelve, 4.3 l/s.
        Assert.Equal(Enumerable.Range(1, 11).Append(12), drain.Select(pipe => loads[pipe].Dwellings));
        Assert.Equal(3.5, Drainage.Flow(SystemKind.Foul, loads[drain[4]])!.Value, 9);
        Assert.Equal(4.3, Drainage.Flow(SystemKind.Foul, loads[drain[^1]])!.Value, 9);

        // Its long section, head to outfall, length by length with what each carries.
        var section = DrainSections.Through(document, drain[^1])!;
        Assert.Equal(drain, section.Pipes.Select(length => length.Pipe));
        Assert.Equal((4.3, 12), (Math.Round(section.Pipes[^1].Flow!.Value, 6), section.Pipes[^1].Dwellings));
        Assert.Equal((2.5, 1), (section.Pipes[0].Flow!.Value, section.Pipes[0].Dwellings));

        // At 100: the lengths serving eleven and twelve are sewers wanting 150 (2.30); the one serving ten is not.
        var problems = MepSystems.Problems(document);
        bool Wants150(Pipe pipe) => problems.Any(problem => problem.Element == pipe && problem.Text.Contains("(2.30) asks one serving more than 10 dwellings 150 at least"));
        Assert.Equal(new[] { false, true, true }, new[] { drain[^3], drain[^2], drain[^1] }.Select(Wants150));
        Assert.Contains(problems, problem => problem.Element == drain[^1] && problem.Text.StartsWith("a sewer serving 12 dwellings at 100"));

        // At 150 they are big enough - and at 1:40 steeper than Table 6's 1:150 for 150 with five WCs or more.
        foreach (var pipe in drain.TakeLast(2)) pipe.Diameter = Outside(150);
        problems = MepSystems.Problems(document);
        Assert.DoesNotContain(problems, problem => problem.Text.Contains("(2.30)"));
        Assert.DoesNotContain(problems, problem => problem.Element is Pipe pipe && drain.Contains(pipe) && problem.Text.Contains("laid no flatter"));
    }

    [Fact]
    public void RoomsOfNoDwellingAreOneAndASewerOfTwoFlatsWithNoWcIs100()
    {
        // Rooms named into no dwelling - a house, or a terrace not told apart - are one dwelling's: 2.5 l/s, no sewer.
        var (document, foul, drain, _) = Block(12, named: false);
        var loads = PipeNetworks.Trace(document, foul).Loads;
        Assert.All(drain, pipe => Assert.Equal(1, loads[pipe].Dwellings));
        Assert.Equal(2.5, Drainage.Flow(SystemKind.Foul, loads[drain[^1]])!.Value, 9);
        Assert.DoesNotContain(MepSystems.Problems(document), problem => problem.Text.Contains("(2.30)"));

        // Two flats' basins on a 75 drain: 75 is enough for a foul drain with no WC (2.33), but it is a sewer, wanting 100 (2.30).
        (document, foul, drain, _) = Block(2, "Basin - Pedestal", bore: 75);
        loads = PipeNetworks.Trace(document, foul).Loads;
        Assert.Equal(new[] { 1, 2 }, drain.Select(pipe => loads[pipe].Dwellings));
        var problems = MepSystems.Problems(document);
        Assert.Contains(problems, problem => problem.Element == drain[^1] && problem.Text == "a sewer serving 2 dwellings at 75 - Approved Document H (2.30) asks 100 at least");
        Assert.DoesNotContain(problems, problem => problem.Element == drain[0] && problem.Text.Contains("(2.30)"));
        Assert.DoesNotContain(problems, problem => problem.Text.Contains("(2.33)"));
    }

    [Fact]
    public void ARoomsDwellingIsAParameterColouredAndMeasuredByAndKeptInTheFile()
    {
        var (document, _, _, rooms) = Block(3);

        // A second room of Flat 1 - the next cell east of the row - and the first flat measured as both its rooms: 2 × 3 m × 3 m = 18 m².
        var level = document.Levels[0].Id;
        foreach (var (x1, y1, x2, y2) in new[] { (9000.0, 0.0, 12000.0, 0.0), (9000.0, 3000.0, 12000.0, 3000.0), (12000.0, 0.0, 12000.0, 3000.0) })
            document.Add(new RoomSeparator { LevelId = level, Start = new Point2D(x1, y1), End = new Point2D(x2, y2) });
        var hall = new Room { LevelId = level, Location = new Point2D(10500, 1500), Name = "Hall", Number = "4" };
        document.Add(hall);
        Assert.True(hall.GetInstanceParameters(document).Single(parameter => parameter.Name == "Dwelling").TrySet("  flat 1 "));
        Assert.Equal("flat 1", hall.Dwelling);
        Assert.Equal(18e6, (double)rooms[0].GetInstanceParameters(document).Single(parameter => parameter.Name == "Dwelling Area").Value!, 0);
        Assert.Equal(9e6, (double)rooms[1].GetInstanceParameters(document).Single(parameter => parameter.Name == "Dwelling Area").Value!, 0);

        // Two named the same whatever their case: the project's dwellings, in order, with their rooms.
        var named = Dwellings.Named(document);
        Assert.Equal(new[] { "Flat 1", "Flat 2", "Flat 3" }, named.Select(dwelling => dwelling.Name));
        Assert.Equal(new[] { rooms[0], hall }, named[0].Rooms);

        // Listed as they are read: Flat 2 before Flat 10, numbers by their value, case aside.
        Assert.Equal(new[] { "Block A", "block b", "Flat 1", "Flat 2", "Flat 9", "Flat 10", "Flat 10a", "Flat 12" },
            new[] { "Flat 10", "Flat 2", "Flat 12", "block b", "Flat 10a", "Flat 1", "Block A", "Flat 9" }.Order(BIMDesigner.Core.NaturalOrder.Instance));

        // A copy of a room is in the same flat until it is named into another.
        Assert.Equal("Flat 2", Assert.IsType<Room>(ElementCopy.Clone(rooms[1])).Dwelling);

        // Coloured by it.
        Assert.Equal("Dwelling", RoomColours.Name(RoomColourBy.Dwelling));
        Assert.Equal("Flat 2", RoomColours.ValueOf(document, rooms[1], RoomColourBy.Dwelling));

        // Sized by it: a king bed in Flat 1's hall makes it a one-bedroom flat for two, its rooms' 18 m² against Table 1's 50, with no store;
        // Flat 2 has no bed to size it by. The project's verdict gives each flat that has one.
        Put(document, "Bed - King 1500 x 2000", 10500, 1200);
        string Size(Room room) => (string)room.GetInstanceParameters(document).Single(parameter => parameter.Name == "Dwelling Size (NDSS)").Value!;
        const string flat1 = "1 bedroom, 2 bedspaces, 1 storey: rooms 18.0 m² - the Nationally Described Space Standard asks 50 m² of gross internal area, which takes in the walls between the rooms - short if they do not make up the 32.0 m²; built-in storage 0.0 m² in rooms called a store or cupboard - it asks 1.5 m²";
        Assert.Equal(flat1, Size(rooms[0]));
        Assert.Equal(flat1, Size(hall));
        Assert.Equal("No bedroom with a bed in it to size it by", Size(rooms[1]));
        Assert.Equal($"Flat 1: {flat1}", Dwellings.Verdict(document));

        // Kept in the file.
        var path = Path.Combine(Path.GetTempPath(), $"dwellings-{Guid.NewGuid():N}.bimx");
        try
        {
            ProjectFile.Save(document, path);
            var opened = ProjectFile.Load(path);
            Assert.Equal(new[] { "Flat 1", "Flat 2", "Flat 3", "flat 1" }, opened.All<Room>().OrderBy(room => room.Number).Select(room => room.Dwelling));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void DwellingsGoOutAsZonesToIfcAndCobieAndComeBackFromIfc()
    {
        // Three rooms: the first two Flat 1 (named in two cases), the third Flat 3.
        var (document, _, _, rooms) = Block(3);
        rooms[1].Dwelling = "flat 1";

        // Each an IfcZone of its rooms' spaces, called a dwelling; opened again, each room back in its flat.
        using (var model = IfcExport.Build(document))
        {
            var zones = model.Instances.OfType<IIfcZone>().ToList();
            Assert.Equal(new[] { ("Flat 1", 2), ("Flat 3", 1) },
                zones.Select(zone => (zone.Name.ToString()!, zone.IsGroupedBy.SelectMany(relation => relation.RelatedObjects).OfType<IIfcSpace>().Count())));
            Assert.All(zones, zone => Assert.Equal("Dwelling", zone.ObjectType.ToString()));
            var back = IfcImport.Read(model, "Flats").Document;
            Assert.Equal(new[] { "Flat 1", "Flat 1", "Flat 3" }, back.All<Room>().OrderBy(room => room.Number).Select(room => room.Dwelling));
        }

        // In COBie, a Zone of each, its spaces by their names.
        var path = Path.Combine(Path.GetTempPath(), $"dwellings-{Guid.NewGuid():N}.xlsx");
        try
        {
            CobieExport.Save(document, path, new CobieOptions());
            var zones = CobieClassificationTests.Read(path)["Zone"].Skip(1).Where(row => row[3] == "Dwelling").Select(row => (row[0], row[4]));
            Assert.Equal(new[] { ("Flat 1", "1,2"), ("Flat 3", "3") }, zones);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
