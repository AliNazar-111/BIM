using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;
using BIMDesigner.Infrastructure.Serialization;

namespace BIMDesigner.Tests;

/// <summary>
/// Stud framing inside walls: plates, studs at their spacing, each opening framed with kings,
/// jacks, a header, a sill and cripples, corner studs and noggings - built in 3D and listed to cut.
/// </summary>
public class WallFramingTests
{
    /// <summary>A partition 3600 long and 2400 high, framed in 38 x 89 at 600 centres, two head plates, one row of noggings.</summary>
    private static (BimDocument Document, Wall Wall, WallFraming Framing) Framed(Action<BimDocument, Wall>? openings = null)
    {
        var document = BimDocument.CreateDefault();
        var type = document.TypesOf<WallType>().First(t => t.Name.StartsWith("Interior - Partition"));
        var wall = new Wall { Start = new Point2D(0, 0), End = new Point2D(3600, 0), TypeId = type.Id, LevelId = document.Levels[0].Id, UnconnectedHeight = 2400 };
        document.Add(wall);
        openings?.Invoke(document, wall);

        var framing = new WallFraming { HostId = wall.Id, LevelId = wall.LevelId, Section = "38 x 89 (2x4)", Spacing = 600, TopPlates = 2, NoggingRows = 1 };
        document.Add(framing);
        return (document, wall, framing);
    }

    private static List<FramingMember> Of(IEnumerable<FramingMember> members, FramingMemberKind kind) => members.Where(m => m.Kind == kind).OrderBy(m => m.From).ToList();

    [Fact]
    public void APlainWallHasPlatesStudsAtTheSpacingAndNoggingsBetween()
    {
        var (document, _, framing) = Framed();
        var members = WallFramings.Layout(document, framing);

        Assert.Single(Of(members, FramingMemberKind.BottomPlate));
        Assert.Equal(2, Of(members, FramingMemberKind.TopPlate).Count);

        // Studs at 0, 600 ... 3600: seven, the end ones flush with the ends, each 2400 less the plates.
        var studs = Of(members, FramingMemberKind.Stud);
        Assert.Equal(7, studs.Count);
        Assert.Equal(0, studs[0].From, 6);
        Assert.Equal(3600, studs[^1].To, 6);
        Assert.Equal(600, (studs[1].From + studs[1].To) / 2, 6);
        Assert.All(studs, stud => Assert.Equal(2400 - 3 * 38, stud.Length, 6));

        // A row of noggings at mid-height between each pair.
        var noggings = Of(members, FramingMemberKind.Nogging);
        Assert.Equal(6, noggings.Count);
        Assert.Equal(600 - 38, noggings[1].Length, 6);
        Assert.Equal(38 + (2400 - 3 * 38) / 2.0, (noggings[0].Bottom + noggings[0].Top) / 2, 6);
    }

    [Fact]
    public void AWindowIsFramedWithKingsJacksAHeaderASillAndCripples()
    {
        var (document, _, framing) = Framed((document, wall) => document.Add(new Window
        {
            TypeId = document.TypesOf<WindowType>().First().Id, LevelId = wall.LevelId, HostWallId = wall.Id, DistanceAlongWall = 1800, SillHeight = 900
        }));
        var window = document.Elements.OfType<Window>().Single();
        var type = document.FindType<WindowType>(window.TypeId)!;
        var (from, to) = window.GetSpan(type);
        var head = 900 + type.Height;
        var members = WallFramings.Layout(document, framing);

        var kings = Of(members, FramingMemberKind.KingStud);
        var jacks = Of(members, FramingMemberKind.JackStud);
        Assert.Equal(new[] { from - 76, to + 38 }, kings.Select(k => k.From));
        Assert.Equal(new[] { from - 38, to }, jacks.Select(j => j.From));
        Assert.All(jacks, jack => Assert.Equal(head, jack.Top, 6));

        // The header bears on the jacks, an inch deep for every foot of span: the smallest made, 140.
        var header = Assert.Single(Of(members, FramingMemberKind.Header));
        Assert.Equal((from - 38, to + 38, head), (header.From, header.To, header.Bottom));
        Assert.Equal(WallFramings.HeaderDepth(FramingMaterial.Timber, to - from + 76), header.Top - header.Bottom, 6);

        var sill = Assert.Single(Of(members, FramingMemberKind.Sill));
        Assert.Equal(900, sill.Top, 6);

        // The stud that would have been at 1800 is cripples, under the sill and over the header.
        var cripples = Of(members, FramingMemberKind.CrippleStud);
        Assert.Contains(cripples, c => Math.Abs(c.Top - (900 - 38)) < 1e-6 && Math.Abs(c.Bottom - 38) < 1e-6);
        Assert.DoesNotContain(Of(members, FramingMemberKind.Stud), stud => stud.To > from - 76 && stud.From < to + 76);

        // No nogging runs through the window.
        Assert.DoesNotContain(Of(members, FramingMemberKind.Nogging), n => n.From < to && n.To > from && n.Bottom > 900 && n.Top < head);
    }

    [Fact]
    public void TheSolePlateStopsAtADoor()
    {
        var (document, _, framing) = Framed((document, wall) => document.Add(new Door
        {
            TypeId = document.TypesOf<DoorType>().First().Id, LevelId = wall.LevelId, HostWallId = wall.Id, DistanceAlongWall = 1800
        }));
        var door = document.Elements.OfType<Door>().Single();
        var (from, to) = door.GetSpan(document.FindType<DoorType>(door.TypeId)!);

        var plates = Of(WallFramings.Layout(document, framing), FramingMemberKind.BottomPlate);
        Assert.Equal(2, plates.Count);
        Assert.Equal(from, plates[0].To, 6);
        Assert.Equal(to, plates[1].From, 6);
        Assert.Empty(Of(WallFramings.Layout(document, framing), FramingMemberKind.Sill));
    }

    [Fact]
    public void ACornerGetsASecondStud()
    {
        var (document, wall, framing) = Framed();
        var type = document.GetWallType(wall)!;
        document.Add(new Wall { Start = new Point2D(3600, 0), End = new Point2D(3600, 3000), TypeId = type.Id, LevelId = wall.LevelId, UnconnectedHeight = 2400 });

        var corner = Assert.Single(Of(WallFramings.Layout(document, framing), FramingMemberKind.CornerStud));
        Assert.Equal(3600 - 76, corner.From, 6);
    }

    [Fact]
    public void In3DTheCoreGivesWayToTheFrameAndSteelStudsAreCSections()
    {
        var (document, wall, framing) = Framed();
        var meshes = ModelMeshBuilder.Build(document);

        // The partition's core is its stud cavity: gone, the plasterboard either side left on.
        Assert.DoesNotContain(meshes, mesh => mesh.ElementId == wall.Id && mesh.Description == "Air Cavity");
        Assert.Equal(2, meshes.Count(mesh => mesh.ElementId == wall.Id && mesh.Description == "Gypsum Plasterboard"));
        var frame = Assert.Single(meshes, mesh => mesh.ElementId == framing.Id);
        Assert.Equal(MeshKind.Framing, frame.Kind);

        // In steel: a single track over C-studs, the studs no thicker than their gauge in the middle.
        framing.Material = FramingMaterial.Steel;
        framing.Section = "362S162-54 (92 x 41)";
        framing.TopPlates = 1;
        var steel = WallFramings.Mesh(document, framing)!;
        Assert.Contains("Steel", steel.Description);
        // Its lips turn in 12 mm from the flanges: a corner a box would not have.
        Assert.Contains(steel.Positions, point => Math.Abs(Math.Abs(point.Y) - (92 / 2.0 - 12)) < 1e-6);
    }

    [Fact]
    public void TheCutListCountsEachLengthOfEachSection()
    {
        var (document, _, framing) = Framed();
        var rows = WallFramings.CutList(document, new[] { framing });

        var studs = Assert.Single(rows, row => row.Member == "Stud");
        Assert.Equal((7, 2286.0, "38 x 89 (2x4)"), (studs.Quantity, studs.Length, studs.Section));
        Assert.Equal(7 * 2286, studs.TotalLength, 6);

        var plates = Assert.Single(rows, row => row.Member == "Top Plate");
        Assert.Equal((2, 3600.0), (plates.Quantity, plates.Length));

        var csv = BIMDesigner.UI.MainWindow.CutListCsv(rows);
        Assert.StartsWith("Member,Section,Length (mm)", csv);
        Assert.Contains("Stud,38 x 89 (2x4),2286,7,16002", csv);
    }

    [Fact]
    public void ItIsSavedAndGoesWithItsWall()
    {
        var (document, wall, framing) = Framed();
        framing.Spacing = 400;
        framing.NoggingRows = 2;

        var loaded = ProjectFile.FromJson(ProjectFile.ToJson(document));
        var again = loaded.Elements.OfType<WallFraming>().Single();
        Assert.Equal((400.0, 2, "38 x 89 (2x4)"), (again.Spacing, again.NoggingRows, again.Section));
        Assert.Equal(WallFramings.Layout(document, framing).Count, WallFramings.Layout(loaded, again).Count);

        new DeleteElementsCommand(document, new[] { wall }).Redo();
        Assert.Empty(document.Elements.OfType<WallFraming>());
    }

    [Fact]
    public void ACurvedOrCurtainWallCannotBeFramed()
    {
        var document = BimDocument.CreateDefault();
        var type = document.TypesOf<WallType>().First(t => t.Name.StartsWith("Interior - Partition"));
        var curved = new Wall { Start = new Point2D(0, 0), End = new Point2D(3000, 0), Bulge = 0.3, TypeId = type.Id, LevelId = document.Levels[0].Id };
        document.Add(curved);
        Assert.NotNull(WallFramings.WhyNot(document, curved));

        var straight = new Wall { Start = new Point2D(0, 5000), End = new Point2D(3000, 5000), TypeId = type.Id, LevelId = document.Levels[0].Id };
        document.Add(straight);
        Assert.Null(WallFramings.WhyNot(document, straight));
        Assert.Equal("47 x 100 C24", WallFramings.New(document, straight).Section);
    }
}
