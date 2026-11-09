using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;

namespace BIMDesigner.Tests;

/// <summary>A US house's walls braced by the IRC's prescriptive rules, R602.10, in Seismic Design Categories D0 to D2.</summary>
public class IrcBracingTests
{
    [Fact]
    public void TheTableRunsInStraightLinesBetweenItsRows()
    {
        // D2, the first of two storeys, Method WSP: 16.5 ft at 30 ft long and 22.0 at 40, so at 32.9 ft 16.5 + 5.5 × 0.29 = 18.1.
        Assert.Equal(16.5 + 5.5 * 0.29, IrcBracing.TableLength("D2", 1, 32.9)!.Value, 6);
        Assert.Equal(0.9, IrcBracing.TableLength("D0", 0, 5)!.Value, 6);
        Assert.Equal(30, IrcBracing.TableLength("D1", 2, 50)!.Value, 6);

        // The first of three storeys in D2 is not permitted; nor is anything for a category the table does not cover.
        Assert.Null(IrcBracing.TableLength("D2", 2, 30));
        Assert.Null(IrcBracing.TableLength("C", 0, 30));
    }

    [Fact]
    public void ItsAdjustmentsMultiply()
    {
        Assert.Equal(1, IrcBracing.Adjustment(10, 25, 10, 15, true, true).Factor!.Value, 6);

        // 11 ft walls 1.1, lines 26.3 ft apart 1.2, walls of 5.5 psf 0.85, a 24.2 psf roof over the top storey 1.2: 1.3464.
        Assert.Equal(1.1 * 1.2 * 0.85 * 1.2, IrcBracing.Adjustment(11, 26.3, 5.5, 24.2, true, true).Factor!.Value, 6);

        // Lines 32 ft apart 1.4, a 20 psf roof over a storey and the roof 1.1, no gypsum board inside 1.5: 2.31.
        Assert.Equal(1.4 * 1.1 * 1.5, IrcBracing.Adjustment(10, 32, 10, 20, false, false).Factor!.Value, 6);

        // Past the table: walls over 12 ft, lines over 35 ft apart, walls over 15 psf - a veneer - and a roof over 25 psf.
        Assert.Null(IrcBracing.Adjustment(13, 25, 10, 15, true, true).Factor);
        Assert.Null(IrcBracing.Adjustment(10, 36, 10, 15, true, true).Factor);
        var (veneer, said) = IrcBracing.Adjustment(10, 25, 40, 15, true, true);
        Assert.Null(veneer);
        Assert.Contains("BV-WSP", said, StringComparison.Ordinal);
        Assert.Null(IrcBracing.Adjustment(10, 25, 10, 30, true, true).Factor);

        // A Method WSP panel at least 48 in long to 10 ft, 53 in at 11, 58 at 12.
        Assert.Equal(48 * 25.4, IrcBracing.LeastPanel(8), 6);
        Assert.Equal(53 * 25.4, IrcBracing.LeastPanel(11), 6);
        Assert.Equal(58 * 25.4, IrcBracing.LeastPanel(12), 6);
    }

    private static (BimDocument Document, StructuralAnalysis Analysis) Braced(string wall, double ss = 1.5, Action<BimDocument>? more = null)
    {
        var document = SeismicTests.House(ClearanceStandard.Irc, wall);
        document.ProjectInformation.SeismicSs = ss;
        document.ProjectInformation.SeismicS1 = 0.6;
        more?.Invoke(document);
        var model = AnalyticalModel.Of(document);
        return (document, StructuralAnalysis.Run(document, model, StructuralLoading.Of(document, model)));
    }

    [Fact]
    public void ASidedHouseInD2HasTheBracingItWants()
    {
        // The sided frame weighs 5.5 psf, under the 8 that takes 0.85; the slate roof 24 psf on plan, 1.1 over the first storey and 1.2 over the top; lines 8 m (26.3 ft) and 10 m (32.9 ft) apart, 1.2 and 1.4.
        var (document, analysis) = Braced("Timber Frame - Siding");
        var result = IrcBracing.Of(document, analysis)!;
        Assert.Equal("D2", result.Category);
        Assert.Equal(8, result.Lines.Count);
        var ground = result.Lines.Single(line => line.Name.StartsWith("Ground Floor, along X at 0.0", StringComparison.Ordinal));
        Assert.Equal("the first of two storeys, or the second of three", ground.Location);
        Assert.Equal(IrcBracing.TableLength("D2", 1, ground.Length)!.Value, ground.Table, 6);
        Assert.Equal(1.2 * 0.85 * 1.1, ground.Factor, 6);
        Assert.Equal(ground.Table * ground.Factor, ground.Required, 6);

        // Its wall has no opening, so all of it is panel, a panel at each end.
        Assert.Equal(ground.Length, ground.Provided, 1);
        Assert.All(result.Lines, line => Assert.True(line.Enough, line.Name));
        var top = result.Lines.Single(line => line.Name.StartsWith("First Floor, along Y at 0.0", StringComparison.Ordinal));
        Assert.Equal(1.4 * 0.85 * 1.2, top.Factor, 6);
    }

    [Fact]
    public void ABrickCladHouseIsPastTheTables()
    {
        var (document, analysis) = Braced("Timber Frame - Brick");
        Assert.All(IrcBracing.Of(document, analysis)!.Lines, line =>
        {
            Assert.False(line.Enough);
            Assert.Contains(line.Faults, fault => fault.Contains("BV-WSP", StringComparison.Ordinal));
        });
    }

    [Fact]
    public void WindowsAlongItsFrontLeaveItsFirstPanelTooFarIn()
    {
        // Windows with 600 mm between them along the first 5 m of the ground floor's front wall: no length there is 48 in, so its first panel is more than 10 ft from its end.
        var (document, analysis) = Braced("Timber Frame - Siding", more: house =>
        {
            var front = house.Walls.First(wall => wall.Start.Y == 0 && wall.End.Y == 0 && wall.LevelId == house.Levels[0].Id);
            var type = house.TypesOf<WindowType>().First();
            for (var centre = 300 + type.Width / 2; centre + type.Width / 2 < 5000; centre += type.Width + 600)
                house.Add(new Window { HostWallId = front.Id, TypeId = type.Id, LevelId = front.LevelId, DistanceAlongWall = centre, SillHeight = 900 });
        });
        var front = IrcBracing.Of(document, analysis)!.Lines.Single(line => line.Name.StartsWith("Ground Floor, along X at 0.0", StringComparison.Ordinal));
        Assert.False(front.Enough);
        Assert.Contains(front.Faults, fault => fault.Contains("more than the 10 ft of R602.10.2.2", StringComparison.Ordinal));
        Assert.True(front.Provided < front.Length - 10);
    }

    [Fact]
    public void OutsideD0ToD2ItSaysWhatGoverns()
    {
        // Ss 0.5 and S1 0.6 on D: SDS 0.467, IRC category C, where a detached house is braced by the wind table, not worked out here.
        var (document, analysis) = Braced("Timber Frame - Siding", 0.5);
        var result = IrcBracing.Of(document, analysis)!;
        Assert.Equal("C", result.Category);
        Assert.Empty(result.Lines);
        Assert.Contains("R602.10.3(1)", result.Said, StringComparison.Ordinal);

        // Ss 2.0: SDS 1.333, past D2 into E, past the prescriptive rules.
        var (high, worked) = Braced("Timber Frame - Siding", 2.0);
        var past = IrcBracing.Of(high, worked)!;
        Assert.Equal("E", past.Category);
        Assert.Contains("designed", past.Said, StringComparison.Ordinal);

        // Not a house, or in England: none.
        var (office, officeAnalysis) = Braced("Timber Frame - Siding", more: house => house.ProjectInformation.BuildingType = "Office");
        Assert.Null(IrcBracing.Of(office, officeAnalysis));
    }

    [Fact]
    public void ASidedFrameIsAmongTheDefaults()
    {
        var document = BimDocument.CreateDefault();
        var sided = document.TypesOf<WallType>().Single(type => type.Name == "Timber Frame - Siding 183mm");
        Assert.InRange(Loads.Weight(document, sided.Structure) / 0.0478803, 4, 8);
    }
}
