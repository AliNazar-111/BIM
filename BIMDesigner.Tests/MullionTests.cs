using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;
using BIMDesigner.Infrastructure.Serialization;

namespace BIMDesigner.Tests;

/// <summary>
/// Curtain wall mullion types: a type on each of a curtain wall type's lines, one stretch of a
/// line given another or none, which run through where they meet, corner mullions between two
/// curtain walls, and how far each bends under the wind and the glass.
/// </summary>
public class MullionTests
{
    /// <summary>A curtain wall 6 m long along the X axis, of the type named, exterior toward +Y.</summary>
    private static (BimDocument Document, CurtainWallType Type, Wall Wall) Curtain(string name, double height = 3000)
    {
        var document = BimDocument.CreateDefault();
        var type = document.TypesOf<CurtainWallType>().Single(t => t.Name.Contains(name));
        var wall = new Wall
        {
            Start = new Point2D(0, 0), End = new Point2D(6000, 0),
            TypeId = type.Id, LevelId = document.Levels[0].Id, UnconnectedHeight = height
        };
        document.Add(wall);
        return (document, type, wall);
    }

    private static MullionType Mullion(BimDocument document, string name) => document.TypesOf<MullionType>().Single(t => t.Name.Contains(name));

    [Fact]
    public void TheStickSystemFramesEachLineWithTheTypeItsSlotNames()
    {
        var (document, type, wall) = Curtain("Stick System");
        var layout = CurtainLayout.Of(document, wall)!;
        var capped = Mullion(document, "Capped");
        var box = Mullion(document, "Rectangular 50 x 150");

        // Capped mullions and transoms between the panels, plain boxes round the edge.
        Assert.All(layout.Mullions.Where(m => !m.IsBorder), m => Assert.Same(capped, m.Type));
        Assert.All(layout.Mullions.Where(m => m.IsBorder), m => Assert.Same(box, m.Type));
        Assert.Equal(new[] { capped, box }.OrderBy(t => t.Name), type.MullionTypes(document).Distinct().OrderBy(t => t.Name));

        // The wall is as deep as its deepest mullion - the capped one's 130 and its 20 mm cap, the box's 150.
        Assert.Equal(150, document.GetWallType(wall)!.Width, precision: 6);

        // In 3D the cap stands out of the face, 20 mm past the box: two materials, two meshes - and the anchors' galvanised steel a third.
        var frames = ModelMeshBuilder.BuildWall(document, wall).Where(m => m.Kind == MeshKind.Mullion && !m.Description.Contains("Galvanised")).ToList();
        Assert.Equal(2, frames.Count);
        var bounds = ModelMeshBuilder.Bounds(frames)!.Value;
        Assert.Equal(85, bounds.Max.Y, precision: 6);
        Assert.Equal(-75, bounds.Min.Y, precision: 6);
    }

    [Fact]
    public void ASlotOfNoneLeavesThatLineBareAndAnOlderTypeKeepsItsOwnMullion()
    {
        var (document, type, wall) = Curtain("Storefront");

        // A type from before mullion types: every line framed in its own 50 x 150 box.
        Assert.All(CurtainLayout.Of(document, wall)!.Mullions, m => Assert.Same(type.OwnMullion, m.Type));
        Assert.Equal(50 * 150 - 44 * 144, type.OwnMullion!.Section().Area, precision: 6);

        // No transoms between panels: butt-glazed across, framed round the edge still.
        type.InteriorHorizontal = Guid.Empty;
        var layout = CurtainLayout.Of(document, wall)!;
        Assert.DoesNotContain(layout.Mullions, m => !m.IsVertical && !m.IsBorder);
        Assert.Equal(8, layout.Mullions.Count(m => !m.IsVertical));
        Assert.Equal(8, layout.Cells.Count);

        // The verticals between, a 65 x 200 box.
        var heavy = Mullion(document, "65 x 200");
        type.InteriorVertical = heavy.Id;
        var middle = CurtainLayout.Of(document, wall)!.Mullions.Single(m => m.IsVertical && Math.Abs((m.From + m.To) / 2 - 3000) < 1);
        Assert.Same(heavy, middle.Type);
        Assert.Equal((2967.5, 3032.5), (middle.From, middle.To));
    }

    [Fact]
    public void OneStretchOfALineCanTakeAnotherTypeOrNone()
    {
        var (document, _, wall) = Curtain("Storefront");
        var heavy = Mullion(document, "65 x 200");

        // The lower stretch of the line 1500 along: none, the panels butted; the upper, the heavy box.
        new SetCurtainLayoutCommand(wall, null, null, mullions: new[]
        {
            new CurtainMullionOverride(true, 1500, 750, Guid.Empty),
            new CurtainMullionOverride(true, 1500, 2250, heavy.Id)
        }).Redo();

        var layout = CurtainLayout.Of(document, wall)!;
        var on = layout.Mullions.Where(m => m.IsVertical && m.From < 1500 && m.To > 1500).ToList();
        Assert.Same(heavy, Assert.Single(on).Type);
        Assert.Equal(1500, on[0].Bottom, precision: 6);

        // Still eight panels, the two either side of the bare stretch meeting on its line.
        Assert.Equal(8, layout.Cells.Count);
        Assert.Equal(1500, layout.Cells.Single(c => c.Column == 0 && c.Row == 0).ClearTo, precision: 6);
        Assert.Equal(1500, layout.Cells.Single(c => c.Column == 1 && c.Row == 0).ClearFrom, precision: 6);

        // Copied, the choices go with it; and a new layout given no choices keeps them.
        Assert.Equal(2, ((Wall)ElementCopy.Clone(wall)!).CurtainMullions!.Count);
        new SetCurtainLayoutCommand(wall, new CurtainGrid(new[] { 1500.0, 3000, 4500 }, new[] { 1500.0 }), null).Redo();
        Assert.Equal(2, wall.CurtainMullions!.Count);
    }

    [Fact]
    public void TheJoinConditionSaysWhetherMullionsOrTransomsRunThrough()
    {
        var (document, type, wall) = Curtain("Storefront");
        var layout = CurtainLayout.Of(document, wall)!;
        Assert.Equal(5, layout.Mullions.Count(m => m.IsVertical));
        Assert.Equal(12, layout.Mullions.Count(m => !m.IsVertical));

        // Transoms through: each runs the wall's length inside its border mullions, and the
        // mullions between stop at their faces, row by row.
        type.JoinCondition = CurtainJoin.HorizontalContinuous;
        layout = CurtainLayout.Of(document, wall)!;
        var transoms = layout.Mullions.Where(m => !m.IsVertical).ToList();
        Assert.Equal(3, transoms.Count);
        Assert.All(transoms, t => Assert.Equal((50.0, 5950.0), (t.From, t.To)));

        var between = layout.Mullions.Where(m => m.IsVertical && !m.IsBorder).ToList();
        Assert.Equal(6, between.Count);
        Assert.Contains(between, m => Math.Abs(m.Bottom - 50) < 1e-6 && Math.Abs(m.Top - 1475) < 1e-6);
        Assert.Contains(between, m => Math.Abs(m.Bottom - 1525) < 1e-6 && Math.Abs(m.Top - 2950) < 1e-6);
    }

    [Fact]
    public void WhereTwoCurtainWallsMeetOneCornerMullionStandsBuiltByOne()
    {
        var (document, type, wall) = Curtain("Stick System");
        var corner = Mullion(document, "L Corner");
        type.BorderVerticalStart = corner.Id;
        type.BorderVerticalEnd = corner.Id;
        type.TakeDepth(document);

        // The second wall turns the corner at the first's end, its glass back toward -Y.
        var other = new Wall
        {
            Start = new Point2D(6000, 0), End = new Point2D(6000, -4000),
            TypeId = type.Id, LevelId = wall.LevelId, UnconnectedHeight = 3000
        };
        document.Add(other);

        var mine = CurtainCorners.At(document, wall, atStart: false)!.Value;
        var theirs = CurtainCorners.At(document, other, atStart: true)!.Value;
        Assert.Same(corner, mine.Type);
        Assert.True(mine.Builds ^ theirs.Builds);

        // Each wall's panels stop where the corner's leg ends: 175 along its outer face, from a
        // corner 75 out past the walls' lines - 100 along the line.
        Assert.Equal(100, corner.FaceWidth, precision: 6);
        var layout = CurtainLayout.Of(document, wall)!;
        Assert.Equal(layout.Verticals[^1] - 100, layout.Cells.Where(c => c.Column == 3).Max(c => c.ClearTo), precision: 6);

        // One of them builds it: a piece of mullion right at the corner; the other has none there.
        var (builder, other2) = mine.Builds ? (wall, other) : (other, wall);
        static bool AtCorner(Mesh3D mesh) => mesh.Positions.Any(p => Math.Abs(p.X - 6000) < 90 && Math.Abs(p.Y) < 90);
        Assert.Contains(ModelMeshBuilder.BuildWall(document, builder), m => m.Kind == MeshKind.Mullion && AtCorner(m));
        Assert.DoesNotContain(ModelMeshBuilder.BuildWall(document, other2), m => m.Kind == MeshKind.Mullion && AtCorner(m));

        var outline = CurtainCorners.Outline(document, wall, false, other, true, corner);
        Assert.True(Polygon2D.SignedArea(outline) > 0);
    }

    [Theory]
    [InlineData(false, 3000, 15)]
    [InlineData(false, 6000, 25)]
    [InlineData(false, 8000, 32)]
    [InlineData(true, 3000, 17.142857)]
    [InlineData(true, 5000, 27.183333)]
    public void TheWindMayBendAMemberSoFarByItsStandard(bool us, double span, double allowed) =>
        Assert.Equal(allowed, MullionRules.WindLimit(span, us), precision: 5);

    [Theory]
    [InlineData(false, 1000, 2)]
    [InlineData(false, 2000, 3)]
    [InlineData(true, 900, 2.5)]
    [InlineData(true, 1500, 3.175)]
    public void TheGlassMaySagATransomSoFarByItsStandard(bool us, double span, double allowed) =>
        Assert.Equal(allowed, MullionRules.DeadLimit(span, us), precision: 5);

    [Fact]
    public void AMullionBendsAsABeamUnderTheWindOnItsPanels()
    {
        var (document, type, wall) = Curtain("Storefront");
        var all = MullionRules.Of(document, wall);

        // The middle mullion: the wind on half a bay either side, 1.5 m in all, over the storey's 3 m.
        var middle = all.Single(b => b.What == "mullion" && Math.Abs((b.Mullion.From + b.Mullion.To) / 2 - 3000) < 1);
        var i = type.OwnMullion!.Section().Properties().Ixx;
        Assert.Equal(3000, middle.Span, precision: 6);
        Assert.Equal(5 * (0.001 * 1500) * Math.Pow(3000, 4) / (384 * 70000 * i), middle.Deflection, precision: 6);
        Assert.Equal(15, middle.Allowed, precision: 6);

        // A transom under the glass on it: half the pane's weight a quarter in from each end.
        var sag = all.First(b => b.What == "transom under its glass");
        Assert.InRange(sag.Deflection, 0.1, 3);
        Assert.Empty(MullionRules.Problems(document, wall));

        // Inside the building the wind does not reach it: only the glass is weighed.
        type.Function = WallFunction.Interior;
        Assert.DoesNotContain(MullionRules.Of(document, wall), b => b.What != "transom under its glass");
    }

    [Fact]
    public void ATallWallsMullionsBendTooFarUntilADeeperTypeIsChosen()
    {
        // 7 m of glass held at the ground and the first floor: 4 m from there to its top.
        var (document, type, wall) = Curtain("Storefront", height: 7000);
        var problems = WallRules.Problems(document, wall, document.ProjectInformation.ClearanceStandard);
        Assert.Contains(problems, p => p.Said.Contains("bend too far") && p.Rule.Contains("CWCT"));
        Assert.Contains("Too far", MullionRules.Verdict(document, wall));

        var heavy = Mullion(document, "65 x 200");
        type.InteriorVertical = heavy.Id;
        type.BorderVerticalStart = heavy.Id;
        type.BorderVerticalEnd = heavy.Id;
        Assert.DoesNotContain(WallRules.Problems(document, wall, document.ProjectInformation.ClearanceStandard), p => p.Said.Contains("bend"));
        Assert.StartsWith("Within limits", MullionRules.Verdict(document, wall));

        // Three times the wind, and the heavy box goes too far too.
        document.ProjectInformation.WindPressure = 3;
        Assert.Contains(MullionRules.Problems(document, wall), p => p.Said.Contains("3.00 kPa"));
    }

    [Fact]
    public void MullionTypesSlotsAndChoicesAreSaved()
    {
        var (document, type, wall) = Curtain("Stick System");
        var timber = Mullion(document, "Timber");
        timber.Offset = 20;
        type.JoinCondition = CurtainJoin.HorizontalContinuous;
        wall.CurtainMullions = new[] { new CurtainMullionOverride(false, 1350, 2250, timber.Id) };
        document.ProjectInformation.WindPressure = 1.6;

        var path = Path.Combine(Path.GetTempPath(), $"bimtest-{Guid.NewGuid():N}{ProjectFile.Extension}");
        try
        {
            ProjectFile.Save(document, path);
            var reloaded = ProjectFile.Load(path);

            var again = reloaded.FindType<MullionType>(timber.Id)!;
            Assert.Equal((MullionShape.Rectangular, MullionMaterial.Timber, 60.0, 140.0, 20.0), (again.Shape, again.Material, again.FaceWidth, again.Thickness, again.Offset));
            var curtain = reloaded.FindType<CurtainWallType>(type.Id)!;
            Assert.Equal((type.InteriorVertical, type.BorderHorizontalTop, CurtainJoin.HorizontalContinuous),
                (curtain.InteriorVertical, curtain.BorderHorizontalTop, curtain.JoinCondition));
            Assert.Equal(1.6, reloaded.ProjectInformation.WindPressure, precision: 9);

            var againWall = reloaded.Walls.Single(w => w.Id == wall.Id);
            Assert.Equal(wall.CurtainMullions[0], Assert.Single(againWall.CurtainMullions!));
            Assert.Contains(CurtainLayout.Of(reloaded, againWall)!.Mullions, m => m.Type?.Id == timber.Id);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AnOlderProjectGainsTheMullionTypesAndTheStickSystemFramedInThem()
    {
        var document = BimDocument.CreateDefault();
        foreach (var mullion in document.TypesOf<MullionType>().ToList()) document.RemoveType(mullion);
        document.RemoveType(document.TypesOf<CurtainWallType>().Single(t => t.Name.Contains("Stick System")));

        document.EnsureDefaultTypes();

        Assert.Equal(11, document.TypesOf<MullionType>().Count());
        var stick = document.TypesOf<CurtainWallType>().Single(t => t.Name.Contains("Stick System"));
        Assert.NotNull(document.FindType<MullionType>(stick.InteriorVertical!.Value));
        Assert.All(document.TypesOf<MullionType>(), t => Assert.NotNull(document.FindMaterial(t.MaterialId)));
    }

    [Fact]
    public void SplitOrMadeOneTheChoicesStayWhereTheyWere()
    {
        var (document, _, wall) = Curtain("Storefront");
        var heavy = Mullion(document, "65 x 200");
        wall.CurtainMullions = new[]
        {
            new CurtainMullionOverride(true, 4500, 750, heavy.Id),
            new CurtainMullionOverride(false, 1500, 2250, Guid.Empty)
        };

        // Split 2250 along, through the bay from 1500 to 3000 whose transom is taken off: both halves keep it off.
        var split = new SplitWallCommand(document, wall, new Point2D(2250, 0));
        split.Redo();
        Assert.Contains(wall.CurtainMullions!, m => !m.Vertical && m.At > 1500 && m.At < 2250);
        Assert.Contains(split.Remainder.CurtainMullions!, m => m.Vertical && Math.Abs(m.Line - 2250) < 1e-6);
        Assert.Contains(split.Remainder.CurtainMullions!, m => !m.Vertical && m.At > 0 && m.At < 750);
        Assert.DoesNotContain(CurtainLayout.Of(document, split.Remainder)!.Mullions, m => !m.IsVertical && !m.IsBorder && m.From < 375 && m.To > 375);

        split.Undo();
        Assert.Equal(2, wall.CurtainMullions!.Count);

        // Two walls in line made one: the far one's choice moves along with its line.
        var glazed = document.TypesOf<CurtainWallType>().Single(t => t.Name.Contains("Glazed")).Id;
        var first = new Wall { Start = new Point2D(0, 5000), End = new Point2D(4000, 5000), TypeId = glazed, LevelId = wall.LevelId, UnconnectedHeight = 3000 };
        var second = new Wall { Start = new Point2D(4000, 5000), End = new Point2D(10000, 5000), TypeId = glazed, LevelId = wall.LevelId, UnconnectedHeight = 3000 };
        document.Add(first);
        document.Add(second);
        second.CurtainGrid = new CurtainGrid(new[] { 2000.0 }, Array.Empty<double>());
        second.CurtainMullions = new[] { new CurtainMullionOverride(true, 2000, 1500, heavy.Id) };

        var merge = new MergeWallsCommand(document, first, second);
        merge.Redo();
        Assert.Equal(6000, Assert.Single(first.CurtainMullions!).Line, precision: 3);
        merge.Undo();
        Assert.Null(first.CurtainMullions);
    }
}
