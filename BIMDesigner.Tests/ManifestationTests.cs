using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Infrastructure.Serialization;

namespace BIMDesigner.Tests;

/// <summary>
/// Manifestation of glazing, Approved Document K Section 7 (round 155): in a building other than a dwelling, large uninterrupted areas of
/// transparent glazing people may walk into marked at two levels, 850 to 1000 and 1400 to 1600 above the floor (7.4, Diagram 7.2) - or broken
/// by rails in both, or in panes less than 400 wide, or a framed door, or a frameless door with a large handle (7.3b, Diagram 7.1).
/// </summary>
[Collection("Wpf")]
public class ManifestationTests
{
    private static Guid TypeOf<T>(BimDocument document, string name) where T : ElementType => document.TypesOf<T>().First(each => each.Name == name).Id;

    private static BimDocument Office()
    {
        var document = BimDocument.CreateDefault();
        document.ProjectInformation.BuildingType = "Office";
        return document;
    }

    [Fact]
    public void APaneWantsMarkingWhereItsGlassRunsAcrossABand()
    {
        // A door-height pane a metre wide: across both bands, unmarked.
        Assert.NotNull(Manifestation.Unmarked(new[] { (1000.0, 50.0, 2000.0) }));

        // Less than 400 wide between frames (Diagram 7.1a); or a window whose glass starts above 850 - no door-height glazing.
        Assert.Null(Manifestation.Unmarked(new[] { (390.0, 50.0, 2000.0) }));
        Assert.Null(Manifestation.Unmarked(new[] { (1000.0, 900.0, 2000.0) }));

        // Rails in both bands (Diagram 7.1b): below 850, between 1000 and 1400, over 1600 - nothing runs across a band.
        Assert.Null(Manifestation.Unmarked(new[] { (1000.0, 50.0, 850.0), (1000.0, 1000.0, 1400.0), (1000.0, 1600.0, 2100.0) }));

        // A rail in the lower band only: the pane over it runs across the upper.
        Assert.Equal((1000.0, 950.0, 2100.0), Manifestation.Unmarked(new[] { (1000.0, 50.0, 900.0), (1000.0, 950.0, 2100.0) }));

        // Obscured, frosted, etched or a spandrel is not transparent glazing.
        Assert.False(Manifestation.Transparent("Double glazed, obscured"));
        Assert.False(Manifestation.Transparent("Frosted laminated"));
        Assert.True(Manifestation.Transparent("Double glazed, low-E"));
    }

    /// <summary>A wall 6 m long along x of a type, on the ground storey, 3 m high; a room either side of it.</summary>
    private static Wall Wall(BimDocument document, Guid type)
    {
        var level = document.Levels.OrderBy(each => each.Elevation).First();
        var wall = new Wall { Start = new Point2D(0, 0), End = new Point2D(6000, 0), TypeId = type, LevelId = level.Id, UnconnectedHeight = 3000 };
        document.Add(wall);
        return wall;
    }

    [Fact]
    public void AWindowLowInAnOfficeWallIsMarked()
    {
        // A fixed picture window 2400 wide, its sill 150 up: its glass 210 to 1590 - unmarked.
        var document = Office();
        var wall = Wall(document, TypeOf<WallType>(document, "Exterior - Brick on Block 330mm"));
        var type = document.TypesOf<WindowType>().Single(each => each.Name == "Fixed - 2400 x 1500");
        var window = new Window { HostWallId = wall.Id, LevelId = wall.LevelId, DistanceAlongWall = 3000, TypeId = type.Id, SillHeight = 150 };
        document.Add(window);
        bool Asked() => WindowRules.Problems(document, window, ClearanceStandard.ApprovedDocumentM).Any(problem => problem.Rule.Contains("K5.2"));
        Assert.True(Asked());

        // Marked; or its sill at 900, above the lower band; or obscured glass: nothing asked.
        type.Manifestation = true;
        Assert.False(Asked());
        type.Manifestation = false;
        window.SillHeight = 900;
        Assert.False(Asked());
        window.SillHeight = 150;
        type.GlazingType = "Double glazed, obscured";
        Assert.False(Asked());
        type.GlazingType = "Double glazed";

        // In a house - K5.2 does not apply to dwellings - or under the US's codes, nothing asked.
        document.ProjectInformation.BuildingType = "House";
        Assert.False(Asked());
        document.ProjectInformation.BuildingType = "Office";
        Assert.False(WindowRules.Problems(document, window, ClearanceStandard.Ada).Any(problem => problem.Rule.Contains("K5.2")));
    }

    [Fact]
    public void AGlassDoorInASlimFrameIsMarkedOrHasALargeHandle()
    {
        var document = Office();
        var wall = Wall(document, TypeOf<WallType>(document, "Exterior - Brick on Block 330mm"));
        var type = document.TypesOf<DoorType>().Single(each => each.Name == "Automatic Sliding Entrance - 2000 x 2200");
        var door = new Door { HostWallId = wall.Id, LevelId = wall.LevelId, DistanceAlongWall = 3000, TypeId = type.Id };
        document.Add(door);
        bool Asked() => Doors.Problems(document, door, ClearanceStandard.ApprovedDocumentM).Any(problem => problem.Rule.Contains("K5.2"));
        Assert.True(Asked());

        // A large pull handle on each pane (Diagram 7.1d), or marked: nothing asked.
        type.HardwareSet = "Pull handles 600 long, overhead closer";
        Assert.False(Asked());
        type.HardwareSet = string.Empty;
        type.Manifestation = true;
        Assert.False(Asked());

        // A glazed door in its stiles and rails - a substantial frame (Diagram 7.1c) - asks nothing.
        type.Manifestation = false;
        type.LeafDesign = DoorLeafDesign.Glazed;
        Assert.False(Asked());
    }

    [Fact]
    public void AGlazedScreenIsMarkedAndTheTypeKeepsItsManifestation()
    {
        // A glazed screen 6 m long along the open office: one pane floor to head - unmarked.
        var document = Office();
        var screen = document.TypesOf<CurtainWallType>().Single(each => each.Name == "Curtain Wall - Glazed");
        var wall = Wall(document, screen.Id);
        bool Asked() => WallRules.Problems(document, wall, ClearanceStandard.ApprovedDocumentM).Any(problem => problem.Rule.Contains("K5.2"));
        Assert.True(Asked());
        screen.Manifestation = true;
        Assert.False(Asked());

        // Kept in the file; copied with the type; a door type's and a window type's too.
        var door = document.TypesOf<DoorType>().Single(each => each.Name == "Automatic Sliding Entrance - 2000 x 2200");
        door.Manifestation = true;
        var reopened = ProjectFile.FromJson(ProjectFile.ToJson(document));
        Assert.True(reopened.TypesOf<CurtainWallType>().Single(each => each.Name == "Curtain Wall - Glazed").Manifestation);
        Assert.True(reopened.TypesOf<DoorType>().Single(each => each.Name == "Automatic Sliding Entrance - 2000 x 2200").Manifestation);
        Assert.False(reopened.TypesOf<WindowType>().Single(each => each.Name == "Fixed - 2400 x 1500").Manifestation);
        Assert.True(((CurtainWallType)screen.Duplicate("Screen copy")).Manifestation);
        Assert.Contains(door.GetTypeParameters(), parameter => parameter.Definition.Name == "Manifestation (Approved Document K 7.4)");
    }
}
