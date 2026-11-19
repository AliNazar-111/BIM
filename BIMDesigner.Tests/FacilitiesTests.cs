using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Tests;

/// <summary>
/// Audience seating, counters and hearing enhancement in a building other than a dwelling by Approved Document M Volume 2 (round 108): wheelchair
/// spaces by Table 3, in pairs, 900 by 1400; a reception counter's low section, the space in front of it and its hearing loop; a bar's low part;
/// a meeting room's hearing loop.
/// </summary>
[Collection("Wpf")]
public class FacilitiesTests
{
    /// <summary>A room drawn on its own, walled round, in a conference centre.</summary>
    private static (BimDocument Document, Room Room) Box(string name, double width = 12000, double depth = 10000)
    {
        var document = BimDocument.CreateDefault();
        document.ProjectInformation.BuildingType = "Conference Centre";
        var corners = new[] { new Point2D(0, 0), new Point2D(width, 0), new Point2D(width, depth), new Point2D(0, depth) };
        var type = document.TypesOf<WallType>().First(each => each.Name == "Exterior - Brick on Block 330mm").Id;
        for (var i = 0; i < 4; i++) document.Add(new Wall { Start = corners[i], End = corners[(i + 1) % 4], TypeId = type, LevelId = document.Levels[0].Id, UnconnectedHeight = 3000 });
        var room = new Room { LevelId = document.Levels[0].Id, Location = new Point2D(width / 2, depth / 2), Name = name };
        document.Add(room);
        return (document, room);
    }

    private static Component Place(BimDocument document, string type, double x, double y, double across = 0)
    {
        var kind = document.TypesOf<ComponentType>().First(each => each.Name == type);
        var piece = new Component { TypeId = kind.Id, TypeKind = kind.Kind, LevelId = document.Levels[0].Id, Location = new Point2D(x, y), Elevation = kind.DefaultElevation };
        piece.Rotation = across;
        document.Add(piece);
        return piece;
    }

    [Fact]
    public void Table3AsksWheelchairSpacesByTheSeats()
    {
        Assert.Equal((1, 6), Facilities.Asked(20));
        Assert.Equal((2, 6), Facilities.Asked(120));
        Assert.Equal((6, 6), Facilities.Asked(600));
        Assert.Equal((7, 7), Facilities.Asked(650));
        Assert.Null(Facilities.Asked(10000));

        // A lecture theatre of 120 seats - 12 rows of 10 - no wheelchair space and no loop: 2 permanent and 6 in all asked (4.12c), a loop (4.12m).
        var (document, room) = Box("Lecture Theatre");
        for (var row = 0; row < 12; row++)
        for (var seat = 0; seat < 10; seat++)
            Place(document, "Seat - Auditorium, Tip-up 500", 1500 + seat * 550, 1500 + row * 650);
        var said = Facilities.Verdict(document, room)!;
        Assert.StartsWith("Audience seating: 120 seats, 0 wheelchair spaces;", said);
        Assert.Contains("0 wheelchair spaces, 0 permanent, for 120 seats - Approved Document M Volume 2 (4.12c, Table 3) asks 2 permanent, 1% of the seats rounded up, and 6 in all with removable ones", said);
        Assert.Contains("no hearing enhancement system in it - Approved Document M Volume 2 (4.12m, 4.36b)", said);

        // Two permanent spaces side by side at the end of the front row, four removable at the back, and the loop's amplifier on the wall: met.
        Place(document, "Wheelchair Space - 900 x 1400", 7500, 2000);
        Place(document, "Wheelchair Space - 900 x 1400", 8450, 2000);
        foreach (var x in new[] { 7500.0, 8450, 9400, 10350 }) Place(document, "Wheelchair Space - Removable Seats 900 x 1400", x, 8600);
        Place(document, "Induction Loop - Room Amplifier", 11500, 5000);
        Assert.Equal("Audience seating: 120 seats, 6 wheelchair spaces, 2 permanent", Facilities.Verdict(document, room));

        // The permanent pair moved apart, each a long way from the other: no pair with a seat beside it (4.12d).
        var spaces = document.All<Component>().Where(piece => document.FindType<ComponentType>(piece.TypeId)?.Form == ComponentForm.WheelchairSpace).ToList();
        foreach (var (space, i) in spaces.Select((space, i) => (space, i))) space.Location = new Point2D(7500, 1500 + i * 1500);
        Assert.Contains("no two wheelchair spaces side by side with a seat beside them - Approved Document M Volume 2 (4.12d, Diagram 13)", Facilities.Verdict(document, room));

        // A space 800 wide: 900 by 1400 asked (4.12g).
        var narrow = document.TypesOf<ComponentType>().First(type => type.Name == "Wheelchair Space - 900 x 1400");
        narrow.Width = 800;
        Assert.Contains("is 800 by 1400 - Approved Document M Volume 2 (4.12g) asks 900 wide and 1400 deep", Facilities.Verdict(document, room));

        // Out to IFC: the seats as chairs, the loop as an amplifier.
        using var model = BIMDesigner.Infrastructure.Interoperability.IfcExport.Build(document);
        Assert.Contains(model.Instances.OfType<Xbim.Ifc4.Interfaces.IIfcAudioVisualAppliance>(), each => each.PredefinedType == Xbim.Ifc4.Interfaces.IfcAudioVisualApplianceTypeEnum.AMPLIFIER);
        Assert.Equal(120, model.Instances.OfType<Xbim.Ifc4.Interfaces.IIfcFurniture>().Count(each => each.PredefinedType == Xbim.Ifc4.Interfaces.IfcFurnitureTypeEnum.CHAIR));
    }

    [Fact]
    public void AReceptionCounterHasALowSectionSpaceBeforeItAndALoop()
    {
        // A reception counter at 1050 only, no loop: a low section (3.6e) and a loop (3.6f) asked.
        var (document, room) = Box("Reception", 8000, 6000);
        Place(document, "Reception Counter - 1800 at 1050", 2500, 1500, 180);
        var said = Facilities.Verdict(document, room)!;
        Assert.StartsWith("Reception counter: 1 section;", said);
        Assert.Contains("no section of its counter 1500 wide at least, 760 high at most, with a knee recess under it - Approved Document M Volume 2 (3.6e)", said);
        Assert.Contains("no hearing enhancement system at it - Approved Document M Volume 2 (3.6f)", said);

        // The lowered section with its knee recess beside it, both facing into the room, and a loop: met - its 1200 by 1800 in front of it (3.6d) clear.
        var low = Place(document, "Reception Counter - Lowered 1500 at 760, Knee Recess", 4200, 1500, 180);
        Place(document, "Induction Loop - Room Amplifier", 7600, 3000);
        Assert.Equal("Reception counter: 2 sections, the lowest 760 high", Facilities.Verdict(document, room));
        var standard = document.ProjectInformation.ClearanceStandard;
        var zone = Assert.Single(Clearances.Of(document, low, standard));
        Assert.Equal("the manoeuvring space in front of the counter", zone.Zone.What);
        Assert.StartsWith("Approved Document M Volume 2 (3.6d) asks 1200 deep and 1800 wide clear", zone.Zone.Rule);
        Assert.Empty(zone.In);

        // A chair stood in it: in the way. The knee recess taken out of its type: 1400 by 2200 asked.
        Place(document, "Chair - Dining", 4200, 1500 + 350 + 600);
        Assert.NotEmpty(Assert.Single(Clearances.Of(document, low, standard)).In);
        document.FindType<ComponentType>(low.TypeId)!.KneeRecess = 0;
        Assert.StartsWith("Approved Document M Volume 2 (3.6d) asks 1400 deep and 2200 wide", Assert.Single(Clearances.Of(document, low, standard)).Zone.Rule);

        // Kept and opened again: the knee recess.
        var opened = BIMDesigner.Infrastructure.Serialization.ProjectFile.FromJson(BIMDesigner.Infrastructure.Serialization.ProjectFile.ToJson(document));
        Assert.Equal(400, opened.TypesOf<ComponentType>().Single(type => type.Name == "Bar Counter - Lowered 1200 at 850").KneeRecess);
        Assert.Equal(0, opened.TypesOf<ComponentType>().Single(type => type.Name == "Reception Counter - Lowered 1500 at 760, Knee Recess").KneeRecess);
    }

    [Fact]
    public void ABarHasPartOfItsCounterAt850AndAMeetingRoomItsLoop()
    {
        // A bar counter at 1050 only: part at 850 asked (4.16b); no loop asked of a bar.
        var (document, room) = Box("Bar", 8000, 6000);
        Place(document, "Bar Counter - 2400 at 1050", 3000, 1500);
        var said = Facilities.Verdict(document, room)!;
        Assert.Contains("no part of its counter 850 high or lower, the lowest 1050 - Approved Document M Volume 2 (4.16b)", said);
        Assert.DoesNotContain("hearing enhancement", said);
        var lowBar = Place(document, "Bar Counter - Lowered 1200 at 850", 4800, 1500, 180);
        Assert.Equal("Bar or serving counter: 2 sections, the lowest 850 high", Facilities.Verdict(document, room));

        // No reception point's space in front of it (3.6d is a reception's, not a bar's).
        Assert.Empty(Clearances.Of(document, lowBar, document.ProjectInformation.ClearanceStandard));

        // A meeting room with no loop: one asked (4.36b); with one, nothing to say; in a house, nothing asked.
        var (office, meeting) = Box("Meeting Room", 6000, 4000);
        Assert.Equal("Meeting room; no hearing enhancement system in it - Approved Document M Volume 2 (4.12m, 4.36b) asks one in rooms for meetings, lectures, classes, performances, spectator sport or films: an induction loop, infrared or radio system", Facilities.Verdict(office, meeting));
        Place(office, "Induction Loop - Room Amplifier", 5600, 2000);
        Assert.Null(Facilities.Verdict(office, meeting));
        office.ProjectInformation.BuildingType = "House";
        Assert.Null(Facilities.Verdict(office, meeting));
    }
}
