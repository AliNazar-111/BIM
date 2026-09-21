using BIMDesigner.Core.Annotation;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;
using BIMDesigner.Core.Sheets;
using BIMDesigner.Core.Views;
using BIMDesigner.Infrastructure.Serialization;

using CoreWindow = BIMDesigner.Core.Architecture.Window;

namespace BIMDesigner.Tests;

/// <summary>
/// An audit of what survives being saved and reopened.
///
/// The individual element tests each check their own round trip, but they check the fields
/// their author remembered. This one works the other way round: it builds a project with one
/// of everything and every field set to something unusual, then compares <em>every parameter
/// every element reports</em> before and after. A field left out of the file format shows up
/// here without anyone having to think of it.
///
/// Losing a field on save is the worst class of defect in this application: it is silent, it
/// is discovered later, and by then the original value is gone.
/// </summary>
public class RoundTripAuditTests
{
    /// <summary>A project with one of everything, deliberately not left at its defaults.</summary>
    private static BimDocument Populated()
    {
        var document = BimDocument.CreateDefault();

        document.ProjectInformation.Name = "Audit House";
        document.ProjectInformation.Number = "2026-014";
        document.ProjectInformation.Client = "A Client";
        document.ProjectInformation.Address = "2 Test Street";
        document.ProjectInformation.BuildingType = "Dwelling";

        var ground = document.Levels[0];
        var first = document.Levels[1];
        ground.Name = "Lower Ground";
        first.Elevation = 3300;

        var wallType = document.TypesOf<WallType>().Single(t => t.Name.StartsWith("Exterior"));

        var wall = new Wall
        {
            Start = new Point2D(0, 0),
            End = new Point2D(6000, 0),
            TypeId = wallType.Id,
            LevelId = ground.Id,
            TopLevelId = first.Id,
            TopOffset = -150,
            BaseOffset = 75,
            UnconnectedHeight = 2850,
            LocationLine = WallLocationLine.CoreFaceExterior,
            Flipped = true,
            RoomBounding = false,
            StructuralUsage = StructuralUsage.Bearing,
            Mark = "W-99",
            Comments = "wall comment",
            Workset = "Shell",
            PhaseCreated = DesignPhase.Existing,
            PhaseDemolished = DesignPhase.Future
        };
        document.Add(wall);

        // A second wall, so rooms and dimensions have something to refer to.
        var partner = new Wall
        {
            Start = new Point2D(6000, 0),
            End = new Point2D(6000, 4000),
            TypeId = wallType.Id,
            LevelId = ground.Id
        };
        document.Add(partner);

        document.Add(new Door
        {
            TypeId = document.TypesOf<DoorType>().First(t => t.Width == 900).Id,
            LevelId = ground.Id,
            HostWallId = wall.Id,
            DistanceAlongWall = 2200,
            SillHeight = 25,
            FlipFacing = true,
            FlipHand = true,
            SwingAngle = 45,
            Mark = "D-99",
            Comments = "door comment",
            Workset = "Fit-out",
            PhaseCreated = DesignPhase.Temporary
        });

        document.Add(new CoreWindow
        {
            TypeId = document.TypesOf<WindowType>().First(t => t.Width == 1200).Id,
            LevelId = ground.Id,
            HostWallId = wall.Id,
            DistanceAlongWall = 4500,
            SillHeight = 850,
            FlipFacing = true,
            Mark = "N-99"
        });

        Point2D[] outline = { new(0, 0), new(6000, 0), new(6000, 4000), new(0, 4000) };

        var floor = new Floor
        {
            TypeId = document.TypesOf<FloorType>().First().Id,
            LevelId = ground.Id,
            HeightOffset = -20,
            Mark = "F-99",
            Comments = "floor comment"
        };
        floor.SetBoundary(outline);
        document.Add(floor);

        var ceiling = new Ceiling
        {
            TypeId = document.TypesOf<CeilingType>().First().Id,
            LevelId = ground.Id,
            HeightOffset = 2450
        };
        ceiling.SetBoundary(outline);
        document.Add(ceiling);

        var roof = new Roof
        {
            TypeId = document.TypesOf<RoofType>().First().Id,
            LevelId = first.Id,
            HeightOffset = 300
        };
        roof.SetBoundary(outline);
        document.Add(roof);

        document.Add(new Room
        {
            Location = new Point2D(3000, 2000),
            LevelId = ground.Id,
            Name = "Kitchen",
            Number = "K-01",
            Department = "Domestic",
            Occupancy = "Cooking",
            OccupantCount = 4,
            BaseOffset = 50,
            UpperLimitOffset = 2600,
            FloorFinish = "Tile",
            WallFinish = "Paint",
            CeilingFinish = "Skim",
            BaseFinish = "Skirting",
            Comments = "room comment"
        });

        var grid = new Grid
        {
            Start = new Point2D(-1000, 0),
            End = new Point2D(-1000, 5000),
            Name = "G9",
            BubbleAtStart = false,
            BubbleAtEnd = true,
            Comments = "grid comment"
        };
        document.Add(grid);

        document.Add(new Dimension
        {
            Start = DimensionReference.ToWall(wall, atStart: true),
            End = DimensionReference.ToWall(wall, atStart: false),
            LevelId = ground.Id,
            Offset = 1250,
            Override = "SEE PLAN",
            Comments = "dimension comment"
        });

        document.Add(new Tag
        {
            TargetId = wall.Id,
            LevelId = ground.Id,
            Field = "Fire Rating",
            Position = new Point2D(2500, 900),
            ShowLeader = false,
            Comments = "tag comment"
        });

        document.Add(new TextNote
        {
            Text = "Check on site",
            Position = new Point2D(1000, 5000),
            LeaderEnd = new Point2D(1500, 4200),
            LevelId = ground.Id,
            Comments = "note comment"
        });

        var section = new SectionMarker
        {
            Start = new Point2D(3000, -2000),
            End = new Point2D(3000, 6000),
            LevelId = ground.Id,
            Name = "Z",
            Flipped = true,
            ViewDepth = 14500,
            Comments = "section comment"
        };
        document.Add(section);

        var sheet = new Sheet
        {
            Number = "A-207",
            Name = "Audit Sheet",
            PaperSize = PaperSize.A2,
            Orientation = PaperOrientation.Portrait,
            DrawnBy = "AB",
            CheckedBy = "CD",
            Revision = "P7",
            IssuedOn = new DateTime(2026, 3, 4),
            Comments = "sheet comment"
        };

        sheet.Add(new Viewport
        {
            View = ViewReference.FloorPlan(ground.Id),
            Centre = new Point2D(140, 260),
            Scale = new ViewScale(50),
            ShowTitle = false,
            TitleOverride = "Proposed Lower Ground"
        });

        sheet.Add(new Viewport
        {
            View = ViewReference.Section(section.Id),
            Centre = new Point2D(140, 110),
            Scale = new ViewScale(200)
        });

        document.Add(sheet);

        return document;
    }

    /// <summary>Every parameter an element reports, as the user would see it.</summary>
    private static Dictionary<string, string> Snapshot(BimDocument document, Element element) =>
        element.GetInstanceParameters(document)
            .ToDictionary(parameter => parameter.Name, parameter => parameter.DisplayValue);

    // ---- elements ----------------------------------------------------------------

    [Fact]
    public void EveryParameterOfEveryElementSurvivesSavingAndReopening()
    {
        var document = Populated();
        var reloaded = ProjectFile.FromJson(ProjectFile.ToJson(document));

        Assert.Equal(document.Elements.Count, reloaded.Elements.Count);

        foreach (var original in document.Elements)
        {
            var match = reloaded.Elements.SingleOrDefault(element => element.Id == original.Id);

            Assert.True(match is not null, $"{original.Category} {original.Id} did not come back at all.");
            Assert.Equal(original.GetType(), match!.GetType());

            var before = Snapshot(document, original);
            var after = Snapshot(reloaded, match);

            foreach (var (name, value) in before)
            {
                Assert.True(after.ContainsKey(name), $"{original.Category}: parameter '{name}' is missing.");
                Assert.True(value == after[name],
                    $"{original.Category}: '{name}' was '{value}' and came back as '{after[name]}'.");
            }
        }
    }

    [Fact]
    public void EveryParameterOfEveryTypeSurvivesSavingAndReopening()
    {
        var document = Populated();
        var reloaded = ProjectFile.FromJson(ProjectFile.ToJson(document));

        Assert.Equal(document.ElementTypes.Count, reloaded.ElementTypes.Count);

        foreach (var original in document.ElementTypes)
        {
            var match = reloaded.ElementTypes.SingleOrDefault(type => type.Id == original.Id);
            Assert.True(match is not null, $"Type '{original.Name}' did not come back.");

            var before = original.GetTypeParameters().ToDictionary(p => p.Name, p => p.DisplayValue);
            var after = match!.GetTypeParameters().ToDictionary(p => p.Name, p => p.DisplayValue);

            foreach (var (name, value) in before)
            {
                Assert.True(after.ContainsKey(name), $"{original.Name}: type parameter '{name}' is missing.");
                Assert.True(value == after[name],
                    $"{original.Name}: '{name}' was '{value}' and came back as '{after[name]}'.");
            }
        }
    }

    // ---- everything else the file has to carry ------------------------------------

    [Fact]
    public void ProjectInformationSurvives()
    {
        var document = Populated();
        var reloaded = ProjectFile.FromJson(ProjectFile.ToJson(document));

        var before = document.ProjectInformation;
        var after = reloaded.ProjectInformation;

        Assert.Equal(before.Name, after.Name);
        Assert.Equal(before.Number, after.Number);
        Assert.Equal(before.Client, after.Client);
        Assert.Equal(before.Address, after.Address);
        Assert.Equal(before.BuildingType, after.BuildingType);
    }

    [Fact]
    public void LevelsAndMaterialsSurvive()
    {
        var document = Populated();
        var reloaded = ProjectFile.FromJson(ProjectFile.ToJson(document));

        Assert.Equal(
            document.Levels.Select(level => (level.Id, level.Name, level.Elevation)),
            reloaded.Levels.Select(level => (level.Id, level.Name, level.Elevation)));

        foreach (var original in document.Materials)
        {
            var match = reloaded.FindMaterial(original.Id);
            Assert.True(match is not null, $"Material '{original.Name}' did not come back.");

            Assert.Equal(original.Name, match!.Name);
            Assert.Equal(original.Density, match.Density);
            Assert.Equal(original.ThermalConductivity, match.ThermalConductivity);
            Assert.Equal(original.SurfaceColour, match.SurfaceColour);
            Assert.Equal(original.CutColour, match.CutColour);
            Assert.Equal(original.CostPerCubicMetre, match.CostPerCubicMetre);
            Assert.Equal(original.Manufacturer, match.Manufacturer);
            Assert.Equal(original.ClassificationCode, match.ClassificationCode);
        }
    }

    [Fact]
    public void SheetsKeepTheirViewportsExactly()
    {
        var document = Populated();
        var reloaded = ProjectFile.FromJson(ProjectFile.ToJson(document));

        var before = document.Elements.OfType<Sheet>().Single();
        var after = reloaded.Elements.OfType<Sheet>().Single();

        Assert.Equal(before.Viewports.Count, after.Viewports.Count);

        foreach (var (original, match) in before.Viewports.Zip(after.Viewports))
        {
            Assert.Equal(original.Id, match.Id);
            Assert.Equal(original.View.Kind, match.View.Kind);
            Assert.Equal(original.View.TargetId, match.View.TargetId);
            Assert.Equal(original.Centre, match.Centre);
            Assert.Equal(original.Scale.Denominator, match.Scale.Denominator);
            Assert.Equal(original.ShowTitle, match.ShowTitle);
            Assert.Equal(original.TitleOverride, match.TitleOverride);
        }
    }

    [Fact]
    public void SlabOutlinesComeBackPointForPoint()
    {
        var document = Populated();
        var reloaded = ProjectFile.FromJson(ProjectFile.ToJson(document));

        foreach (var original in document.Elements.OfType<Slab>())
        {
            var match = reloaded.Elements.OfType<Slab>().Single(slab => slab.Id == original.Id);
            Assert.Equal(original.Boundary, match.Boundary);
        }
    }

    [Fact]
    public void ASecondRoundTripChangesNothing()
    {
        var document = Populated();

        var once = ProjectFile.ToJson(document);
        var twice = ProjectFile.ToJson(ProjectFile.FromJson(once));

        // Saving, opening and saving again must produce the same file. Anything that drifts
        // here would drift a little further with every open, and the drift compounds silently.
        Assert.Equal(StripTimestamp(once), StripTimestamp(twice));
    }

    private static string StripTimestamp(string json) =>
        string.Join('\n', json.Split('\n').Where(line => !line.Contains("\"savedUtc\"")));
}
