using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Tests;

/// <summary>
/// A chimney against the rules: its fireplace opening and hearth the sizes they are asked to be,
/// its flue as big as its fire wants and tall enough to draw, and its stack no taller above the
/// roof than its width allows.
/// </summary>
public class ChimneyRulesTests
{
    private static (BimDocument Document, Roof Roof, Chimney Chimney) Fire(double pitch, double x, double y)
    {
        var (document, roof, _) = DormerToolTests.House(10000, 8000, pitch);
        var chimney = new Chimney
        {
            LevelId = roof.LevelId, Location = new Point2D(x, y),
            TypeId = document.TypesOf<ChimneyType>().Single(type => type.Name == "Chimney - Masonry, Brick, Clay Liners").Id,
            Fireplace = ChimneyFireplace.Open
        };
        document.Add(chimney);
        return (document, roof, chimney);
    }

    private static (double Width, double Depth) Size(IReadOnlyList<Point2D> rectangle) =>
        (rectangle[0].DistanceTo(rectangle[1]), rectangle[1].DistanceTo(rectangle[2]));

    [Fact]
    public void AnOpenFiresOpeningAndHearthAreTheSizesAsked()
    {
        var (document, _, chimney) = Fire(40, 5000, 2900);
        var breast = Chimneys.Breast(document, chimney)!;

        // 500 by 550, the largest Approved Document J gives a 200 flue for; its hearth 150 past each side.
        Assert.Equal(500, Size(breast.Opening).Width, 1);
        Assert.Equal(550, breast.OpeningHeight);
        Assert.Equal(800, Size(breast.Hearth).Width, 1);
        Assert.Empty(ChimneyRules.Problems(document, chimney, document.ProjectInformation.ClearanceStandard));
        Assert.Equal("Its flues and stack as the rules ask", ChimneyRules.Verdict(document, chimney));

        // A 185 flue is too small for it.
        Chimneys.TypeOf(document, chimney).FlueDiameter = 185;
        Assert.Contains(ChimneyRules.Problems(document, chimney, ClearanceStandard.ApprovedDocumentM), problem => problem.Rule.Contains("200 across"));

        // In the US: 24 in square, its hearth 8 in past each side; a round flue a twelfth of the opening, 199 across.
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.Irc;
        breast = Chimneys.Breast(document, chimney)!;
        Assert.Equal(610, breast.OpeningHeight);
        Assert.Equal(610 + 2 * 203, Size(breast.Hearth).Width, 1);
        Assert.Contains(ChimneyRules.Problems(document, chimney, ClearanceStandard.Irc), problem => problem.Rule.Contains("R1003.15"));
        Chimneys.TypeOf(document, chimney).FlueDiameter = 200;
        Assert.Empty(ChimneyRules.Problems(document, chimney, ClearanceStandard.Irc));
    }

    [Fact]
    public void InTheUsAStackWiderThan30InHasACricketBehindIt()
    {
        var (document, roof, chimney) = Fire(40, 5000, 1500);
        chimney.Width = 900;
        Assert.Null(Chimneys.Cricket(document, chimney));

        // Under the IRC: a saddle against its uphill face, its level ridge half its width at the pitch above the roof there, and as long.
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.Irc;
        var (a, b, ridge, apex) = Chimneys.Cricket(document, chimney)!.Value;
        Assert.Equal(900, Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y)), 1);
        Assert.Equal(450 * Math.Tan(40 * Math.PI / 180), ridge.Z - roof.TopAt(document, new Point2D(ridge.X, ridge.Y)), 1);
        Assert.Equal(ridge.Z, apex.Z, 6);
        Assert.Equal(450, apex.Y - ridge.Y, 1);
        Assert.Contains(Chimneys.Meshes(document, chimney), mesh => mesh.Description == "Cricket");

        // 30 in across or less: none.
        chimney.Width = 700;
        Assert.Null(Chimneys.Cricket(document, chimney));
    }

    [Fact]
    public void ItsFlueIsTallEnoughToDrawAndItsStackNoTallerThanItsWidthAllows()
    {
        // On a low roof, by the ridge: 600 over it is only 4.1 m above the top of the opening.
        var (document, _, chimney) = Fire(15, 5000, 3800);
        var standard = document.ProjectInformation.ClearanceStandard;
        Assert.Contains(ChimneyRules.Problems(document, chimney, standard), problem => problem.Rule.Contains("4.5 m flue"));
        chimney.ExtraHeight = 500;
        Assert.DoesNotContain(ChimneyRules.Problems(document, chimney, standard), problem => problem.Rule.Contains("4.5 m flue"));

        // Down by the eaves of a steep roof, it stands high over where it comes through: 450 wide, it may stand 2025 above it.
        var (steep, _, low) = Fire(40, 2000, 1000);
        var problem = Assert.Single(ChimneyRules.Problems(steep, low, standard));
        Assert.Contains("4.5 times its least width, 2025", problem.Said);
        Assert.Contains(Clearances.Blocked(steep, low.LevelId, standard), blocked => blocked.Zone.Element == low);
        low.Width = low.Depth = 600;
        Assert.Empty(ChimneyRules.Problems(steep, low, standard));
    }
}
