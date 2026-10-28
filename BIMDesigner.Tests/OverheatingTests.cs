using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Energy;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Infrastructure.Serialization;
using BimWindow = BIMDesigner.Core.Architecture.Window;

namespace BIMDesigner.Tests;

/// <summary>
/// Requirement O1 by Approved Document O's simplified method: the equivalent areas of Appendix D's
/// tables, the high risk location by postcode, the limits of Tables 1.1 to 1.4, and a home read off
/// its model - its glazing, cross-ventilation, and what it opens to by day and at night.
/// </summary>
[Collection("Wpf")]
public class OverheatingTests
{
    [Fact]
    public void AnOpeningsEquivalentAreaIsReadOffAppendixDsTables()
    {
        // Table D9 at 90°: 1 m high, 0.5 m wide, 0.43 m²; Table D3 at 30°: 1 by 1 m, 0.64.
        Assert.Equal(0.43, Overheating.EquivalentArea(1, 0.5, 90), 6);
        Assert.Equal(0.64, Overheating.EquivalentArea(1, 1, 30), 6);

        // Between the tables' angles, rows and columns in proportion; beyond them in proportion; under 10° in proportion to the angle.
        Assert.Equal((0.64 + 0.73) / 2, Overheating.EquivalentArea(1, 1, 35), 6);
        Assert.Equal((0.88 + 1.10) / 2, Overheating.EquivalentArea(1.125, 1, 90), 6);
        Assert.Equal(1.40 * 2, Overheating.EquivalentArea(1, 3, 90), 6);
        Assert.Equal(0.30 / 2, Overheating.EquivalentArea(1, 1, 5), 6);

        // A 0.6 m wide light on a restrictor opens under 10°: its far edge 100 mm out.
        Assert.Equal(2 * Math.Asin(0.1 / 1.2) * 180 / Math.PI, Overheating.RestrictedAngle(0.6), 9);
    }

    [Fact]
    public void TheHighRiskLocationIsFoundByThePostcode()
    {
        Assert.Equal("SW1A", Overheating.District("10 Downing Street, London SW1A 2AA"));
        Assert.Equal("M15", Overheating.District("m15 4ab"));
        Assert.Equal("LS1", Overheating.District("LS1"));
        Assert.Null(Overheating.District("No postcode here"));
        Assert.Equal(207, Overheating.HighRiskPostcodes.Count);

        var document = BimDocument.CreateDefault();
        document.ProjectInformation.Address = "10 Downing Street, London SW1A 2AA";
        Assert.True(Overheating.LocationOf(document).High);
        document.ProjectInformation.Energy.Postcode = "M15 4AB";
        Assert.True(Overheating.LocationOf(document).High);
        document.ProjectInformation.Energy.Postcode = "LS1 4AP";
        Assert.False(Overheating.LocationOf(document).High);
        document.ProjectInformation.Energy.OverheatingLocation = OverheatingLocation.High;
        Assert.True(Overheating.LocationOf(document).High);

        // Tables 1.1 to 1.4.
        Assert.Equal((15.0, 22.0), Overheating.GlazingLimit(high: true, cross: true, "south"));
        Assert.Equal((11.0, 11.0), Overheating.GlazingLimit(high: false, cross: false, "west"));
        Assert.Equal((12.0, 80.0, 4.0), Overheating.FreeAreaLimit(high: false, cross: false));
        Assert.Equal((6.0, 70.0, 13.0), Overheating.FreeAreaLimit(high: true, cross: true));
    }

    /// <summary>A living room and a bedroom side by side on the ground, a casement in each one's south wall and one in the living room's north; and a bedroom upstairs with a casement south.</summary>
    private static (BimDocument Document, List<Room> Rooms, BimWindow Upstairs) TwoStoreys()
    {
        var (document, rooms, _) = VentilationTests.Row("Generic - 200mm", "Living Room", "Bedroom 1");
        var exterior = document.Walls.Take(4).ToList();
        var south = exterior.First(wall => Math.Abs(wall.Start.Y - wall.End.Y) < 1 && wall.Start.Y < 0);
        var northWall = exterior.First(wall => Math.Abs(wall.Start.Y - wall.End.Y) < 1 && wall.Start.Y > 0);
        var casement = document.TypesOf<WindowType>().First(type => type.Name == "Casement - 1200 x 1200");
        foreach (var room in rooms)
            document.Add(new BimWindow { TypeId = casement.Id, LevelId = room.LevelId, HostWallId = south.Id, DistanceAlongWall = Math.Abs(room.Location.X - south.Start.X), SillHeight = 900 });
        document.Add(new BimWindow { TypeId = casement.Id, LevelId = rooms[0].LevelId, HostWallId = northWall.Id, DistanceAlongWall = Math.Abs(rooms[0].Location.X - northWall.Start.X), SillHeight = 900 });

        // The same box again on the first floor, a bedroom in it, its window at a sill of 900.
        var first = document.Levels.First(level => level.Name == "First Floor");
        var walls = exterior.Select(wall => new Wall { Start = wall.Start, End = wall.End, TypeId = wall.TypeId, LevelId = first.Id, UnconnectedHeight = 3000 }).ToList();
        foreach (var wall in walls) document.Add(wall);
        var upstairsSouth = walls.First(wall => Math.Abs(wall.Start.Y - wall.End.Y) < 1 && wall.Start.Y < 0);
        var bedroom = new Room { LevelId = first.Id, Location = new Point2D(2000, 1500), Name = "Bedroom 2", Number = "3" };
        document.Add(bedroom);
        var upstairs = new BimWindow { TypeId = casement.Id, LevelId = first.Id, HostWallId = upstairsSouth.Id, DistanceAlongWall = Math.Abs(2000 - upstairsSouth.Start.X), SillHeight = 900 };
        document.Add(upstairs);
        rooms.Add(bedroom);
        return (document, rooms, upstairs);
    }

    [Fact]
    public void AHomeIsCheckedByTheSimplifiedMethodFromItsModel()
    {
        var (document, rooms, upstairs) = TwoStoreys();
        var assessment = Overheating.Of(document)!;

        // Openings on the north and south: cross-ventilated. Moderate risk, no postcode given.
        Assert.False(assessment.HighRisk);
        Assert.True(assessment.CrossVentilated);

        // Each casement's glass inside its frame, three facing south and one north: most facing south.
        var glass = WindowLights.Of(document.TypesOf<WindowType>().First(type => type.Name == "Casement - 1200 x 1200")).Sum(light => light.Area) / 1e6;
        Assert.Equal(4 * glass, assessment.Glazing, 6);
        Assert.Equal("south", assessment.Facade);

        // The floor to the inside of the outside walls: each room's and, downstairs, the partition between them.
        Assert.Equal(12 + 12 + 0.2 * 3 + upstairsArea(document, rooms[2]), assessment.FloorArea, 1);
        static double upstairsArea(BimDocument document, Room room) => room.GetBoundary(document).Area / 1e6;

        // Downstairs a casement opens fully - side-hung, 90° - but a ground floor bedroom's only 100 mm at night, securely.
        var living = assessment.Openings.Where(opening => ReferenceEquals(opening.Room, rooms[0])).ToList();
        Assert.All(living, opening => Assert.Equal("opens fully", opening.Note));
        var ground = Assert.Single(assessment.Openings, opening => ReferenceEquals(opening.Room, rooms[1]));
        Assert.True(ground.NightEquivalent < ground.Equivalent / 3);

        // Upstairs, at a sill of 900 with nothing in front of it, only 100 mm by day as well (3.9).
        var first = Assert.Single(assessment.Openings, opening => ReferenceEquals(opening.Element, upstairs));
        Assert.Contains("100 mm", first.Note);
        Assert.Equal(first.Equivalent, first.NightEquivalent, 9);

        // A railing of 1.1 m across it: it opens fully.
        var guard = document.TypesOf<RailingType>().First(type => type.Height >= 1100);
        var railing = new Railing { TypeId = guard.Id, LevelId = upstairs.LevelId };
        railing.SetPath(new[] { new Point2D(1000, -400), new Point2D(3000, -400) });
        document.Add(railing);
        var guarded = Assert.Single(Overheating.Of(document)!.Openings, opening => ReferenceEquals(opening.Element, upstairs));
        Assert.Equal("opens fully", guarded.Note);
        Assert.True(guarded.Equivalent > 3 * first.Equivalent);

        // The checks: the home's and the most glazed room's glazing, its free area, each bedroom's at night.
        var checks = Overheating.Of(document)!.Checks.Select(check => check.Requirement).ToList();
        Assert.Contains("Glazing of the home (Table 1.1)", checks);
        Assert.Contains("Glazing of the most glazed room (Table 1.1)", checks);
        Assert.Contains("Free area to remove heat (Table 1.3)", checks);
        Assert.Contains(checks, requirement => requirement.StartsWith("001 Living Room") is false && requirement.Contains("Bedroom 1") && requirement.EndsWith("(Table 1.3)"));
        Assert.Contains(checks, requirement => requirement.Contains("Bedroom 2"));
    }

    [Fact]
    public void TheHighRiskLocationWantsTheSouthGlazingShadedAndNoiseRulesTheMethodOut()
    {
        var (document, _, _) = TwoStoreys();
        document.ProjectInformation.Address = "1 Example Road, London SE1 7PB";
        var assessment = Overheating.Of(document)!;
        Assert.True(assessment.HighRisk);
        Assert.False(assessment.Checks.Single(check => check.Requirement.StartsWith("Shading")).Met);

        // Glass of g 0.4 and light transmittance 0.7 shades it; so do external shutters.
        var casement = document.TypesOf<WindowType>().First(type => type.Name == "Casement - 1200 x 1200");
        (casement.SolarHeatGainCoefficient, casement.LightTransmittance) = (0.38, 0.72);
        Assert.True(Overheating.Of(document)!.Checks.Single(check => check.Requirement.StartsWith("Shading")).Met);
        (casement.SolarHeatGainCoefficient, casement.LightTransmittance) = (0.55, 0.7);
        document.ProjectInformation.Energy.Shutters = true;
        Assert.True(Overheating.Of(document)!.Checks.Single(check => check.Requirement.StartsWith("Shading")).Met);

        // Windows kept shut at night by noise: the simplified method cannot be used.
        document.ProjectInformation.Energy.NoisyAtNight = true;
        Assert.False(Overheating.Of(document)!.Checks.Single(check => check.Requirement.StartsWith("Noise")).Met);

        // All kept in the file.
        document.ProjectInformation.Energy.Postcode = "SE1";
        document.ProjectInformation.Energy.OverheatingLocation = OverheatingLocation.Moderate;
        var kept = ProjectFile.FromJson(ProjectFile.ToJson(document)).ProjectInformation.Energy;
        Assert.Equal(("SE1", OverheatingLocation.Moderate, true, true), (kept.Postcode, kept.OverheatingLocation, kept.Shutters, kept.NoisyAtNight));
    }
}
