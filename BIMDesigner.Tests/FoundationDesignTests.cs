using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Infrastructure.Interoperability;
using Xbim.Ifc4.GeometricModelResource;
using Xbim.Ifc4.GeometryResource;
using Xbim.Ifc4.Interfaces;
using Xbim.Ifc4.Kernel;
using Xbim.Ifc4.ProductExtension;
using Xbim.Ifc4.UtilityResource;
using Xbim.Ifc4.StructuralElementsDomain;

namespace BIMDesigner.Tests;

/// <summary>
/// The foundations finished (round 26): each bar written to IFC as a reinforcing bar swept along its
/// centreline, the void former under a ground beam in shrinkable clay, the ground's pressure under a raft
/// against BS 8004's and the IRC's presumed values, and a ground beam's bars worked out from the moments
/// the analysis finds in it by BS EN 1992-1-1 and ACI 318-19.
/// </summary>
public class FoundationDesignTests
{
    /// <summary>The 10 by 8 m house of bearing walls 330 thick, on the ground given.</summary>
    private static (BimDocument Document, List<Wall> Walls) House(GroundType ground)
    {
        var (document, _) = EaveTests.House(hip: false);
        document.ProjectInformation.Ground = ground;
        var walls = document.Walls.ToList();
        foreach (var wall in walls) wall.StructuralUsage = StructuralUsage.Bearing;
        return (document, walls);
    }

    /// <summary>A ground beam under each wall and the piles under them, the corners shared.</summary>
    private static List<GroundBeam> OnPiles(BimDocument document, IEnumerable<Wall> walls)
    {
        var beams = new List<GroundBeam>();
        foreach (var wall in walls)
        {
            var (beam, piles) = Piles.Fit(document, wall, 300, 3000);
            document.Add(beam);
            foreach (var pile in piles) document.Add(pile);
            beams.Add(beam);
        }

        return beams;
    }

    [Fact]
    public void EachBarIsWrittenToIfcSweptAlongItsCentrelineAsPartOfItsMember()
    {
        var (document, walls) = House(GroundType.StiffClay);
        document.ProjectInformation.ClayShrinkage = ClayShrinkage.High;
        var beam = OnPiles(document, walls.Take(1)).Single();
        var raft = Rafts.Under(document, document.Levels[0].Id, new Point2D(5000, 4000))!;
        raft.EdgeBars = "6 H16";
        document.Add(raft);

        // The beam's bars: 2 H16 on top, 3 H16 below and H8 links at 200, each link four corners round.
        var bars = Reinforcement.Bars(document, beam);
        Assert.Equal(2, bars.Count(bar => !bar.Link && bar.Diameter == 16 && bar.Points[0].Z > Piles.Heights(document, beam).Top - 150));
        Assert.Equal(5, bars.Count(bar => !bar.Link && bar.Size == "H16"));
        var links = bars.Where(bar => bar.Link).ToList();
        Assert.Equal(Reinforcement.AcrossAt(document.Find<Wall>(beam.WallId)!.Length + beam.Width, 200, Reinforcement.CoverAgainstGround), links.Count);
        Assert.All(links, link => Assert.Equal(4, link.Points.Count));
        var (bottom, top) = Piles.Heights(document, beam);
        Assert.Equal(bottom + Reinforcement.CoverAgainstGround + 4, links[0].Points[0].Z, 6);
        Assert.Equal(top - Reinforcement.CoverAgainstGround - 4, links[0].Points[2].Z, 6);

        // Each a bar in IFC: a disc of its radius along a polyline, closed for a link, aggregated into the beam.
        using var model = IfcExport.Build(document);
        var expected = IfcGloballyUniqueId.ConvertToBase64(beam.Id).ToString();
        var host = model.Instances.OfType<IIfcFooting>().Single(footing => footing.GlobalId.ToString() == expected);
        var parts = model.Instances.OfType<IfcRelAggregates>().Where(relation => relation.RelatingObject == host).SelectMany(relation => relation.RelatedObjects).OfType<IfcReinforcingBar>().ToList();
        Assert.Equal(bars.Count, parts.Count);
        var ligature = parts.First(part => part.PredefinedType == IfcReinforcingBarTypeEnum.LIGATURE);
        Assert.Equal(8.0, (double)ligature.NominalDiameter!.Value, 6);
        Assert.Equal("B500B", ligature.SteelGrade?.ToString());
        var disk = ligature.Representation!.Representations.Single().Items.OfType<IfcSweptDiskSolid>().Single();
        Assert.Equal(4.0, (double)disk.Radius, 6);
        var line = Assert.IsType<IfcPolyline>(disk.Directrix);
        Assert.Equal(5, line.Points.Count);
        Assert.Equal(line.Points[0].Coordinates.Select(c => (double)c), line.Points[4].Coordinates.Select(c => (double)c));
        Assert.Contains(parts, part => part.PredefinedType == IfcReinforcingBarTypeEnum.MAIN && part.Name == "H16");
        Assert.Equal(parts.Count, parts.Select(part => part.GlobalId.ToString()).Distinct().Count());

        // The bars are parts of the beam, not on the storey; the raft has its own.
        Assert.DoesNotContain(model.Instances.OfType<IfcRelContainedInSpatialStructure>().SelectMany(relation => relation.RelatedElements), element => element is IfcReinforcingBar);
        var raftId = IfcGloballyUniqueId.ConvertToBase64(raft.Id).ToString();
        var slab = model.Instances.OfType<IIfcSlab>().Single(each => each.GlobalId.ToString() == raftId);
        Assert.Equal(Reinforcement.Bars(document, raft).Count,
            model.Instances.OfType<IfcRelAggregates>().Where(relation => relation.RelatingObject == slab).SelectMany(relation => relation.RelatedObjects).OfType<IfcReinforcingBar>().Count());
    }

    [Fact]
    public void AVoidFormerLiesUnderAGroundBeamInShrinkableClay()
    {
        var (document, walls) = House(GroundType.StiffClay);
        document.ProjectInformation.ClayShrinkage = ClayShrinkage.High;
        var beam = OnPiles(document, walls.Take(1)).Single();
        var (bottom, _) = Piles.Heights(document, beam);

        // 150 under it, its outline - drawn and cut in section, but not written as the beam's concrete.
        var former = Piles.Meshes(document, beam).Single(mesh => mesh.Description == Piles.VoidFormer);
        var (low, high) = former.Bounds()!.Value;
        Assert.Equal(bottom - 150, low.Z, 6);
        Assert.Equal(bottom, high.Z, 6);

        beam.Void = 0;
        Assert.DoesNotContain(Piles.Meshes(document, beam), mesh => mesh.Description == Piles.VoidFormer);
    }

    [Fact]
    public void TheGroundIsPresumedToBearWhatBs8004AndTheIrcSay()
    {
        var document = BimDocument.CreateDefault();
        var project = document.ProjectInformation;
        foreach (var (ground, uk) in new (GroundType, double?)[]
                 {
                     (GroundType.Gravel, 200), (GroundType.Sand, 100), (GroundType.StiffClay, 150), (GroundType.FirmClay, 75),
                     (GroundType.Rock, null), (GroundType.LooseSand, null), (GroundType.SoftClay, null), (GroundType.VerySoft, null)
                 })
        {
            project.Ground = ground;
            var (allowable, said) = StructuralAnalysis.AllowableBearing(document);
            Assert.Equal(uk, allowable);
            Assert.Contains(uk is null ? "site investigation" : "BS 8004:1986 Table 1", said);
        }

        // In the US, the IRC's presumptive values in psf: clay 1,500 - 71.8 kPa; soft clay, a soils investigation.
        project.ClearanceStandard = ClearanceStandard.Irc;
        project.Ground = GroundType.FirmClay;
        Assert.Equal(71.82, StructuralAnalysis.AllowableBearing(document).Allowable!.Value, 2);
        project.Ground = GroundType.Gravel;
        Assert.Equal(143.64, StructuralAnalysis.AllowableBearing(document).Allowable!.Value, 2);
        project.Ground = GroundType.SoftClay;
        Assert.Contains("soils investigation", StructuralAnalysis.AllowableBearing(document).Said);
    }

    [Fact]
    public void TheGroundUnderARaftPushesBackWithWhatTheBuildingWeighs()
    {
        var (document, _) = House(GroundType.StiffClay);
        var raft = Rafts.Under(document, document.Levels[0].Id, new Point2D(5000, 4000))!;
        document.Add(raft);
        var model = AnalyticalModel.Of(document);
        var analysis = StructuralAnalysis.Run(document, model, StructuralLoading.Of(document, model));
        Assert.Empty(analysis.Problems);

        var bearing = Assert.Single(analysis.BearingChecks(document));
        Assert.Equal(150, bearing.Allowable);

        // The average over it times its area is everything but the wind; the most is under the walls, more than the average.
        var area = Polygon2D.Area(bearing.Panel.FlatOutline) / 1e6;
        var weight = -analysis.TotalLoad(analysis.Total).Z;
        Assert.InRange(bearing.Average * area, 0.98 * weight, 1.02 * weight);
        Assert.True(bearing.Greatest > bearing.Average, $"greatest {bearing.Greatest:0.0} kPa, average {bearing.Average:0.0}");
        Assert.Equal(bearing.Greatest, bearing.Settlement * bearing.Panel.Subgrade!.Value, 6);
        Assert.True(bearing.Ratio < 1, $"{bearing.Greatest:0} kPa under a house on stiff clay");
    }

    [Fact]
    public void ASectionsSteelIsWorkedOutByTheEurocodeAndAci318()
    {
        // 100 kNm on 450 wide, d 509: K 0.031, z capped at 0.95d = 483.6, As = M / 0.87 fyk z = 475 mm².
        var (needed, problem) = StructuralAnalysis.Eurocode(100, 450, 509);
        Assert.Null(problem);
        Assert.Equal(475.4, needed, 1);

        // Little moment: the least steel, 0.26 fctm / fyk of b d.
        Assert.Equal(StructureRules.UkLeastSteel * 450 * 509, StructuralAnalysis.Eurocode(5, 450, 509).Needed, 6);

        // Too much for it without compression steel.
        Assert.Contains("K'", StructuralAnalysis.Eurocode(800, 450, 509).Problem);

        // ACI: 200 kNm on 16 in wide, d 500 mm, f'c 3,000 psi and Grade 60 - As 1,151 mm², 0.9 As fy (d - a/2) back to 200 kNm; at 100 kNm, 200/fy b d governs.
        var (us, usProblem) = StructuralAnalysis.Aci(200, 406.4, 500);
        Assert.Null(usProblem);
        Assert.Equal(1151.1, us, 1);
        var a = us * 60000 * 0.00689476 / (0.85 * 3000 * 0.00689476 * 406.4);
        Assert.Equal(200, 0.9 * us * 60000 * 0.00689476 * (500 - a / 2) / 1e6, 3);
        Assert.Equal(StructureRules.UsBeamLeastSteel * 406.4 * 500, StructuralAnalysis.Aci(100, 406.4, 500).Needed, 6);
        Assert.NotNull(StructuralAnalysis.Aci(1200, 406.4, 500).Problem);
    }

    [Fact]
    public void AGroundBeamsBarsAreWorkedOutFromItsMoments()
    {
        var (document, walls) = House(GroundType.FirmClay);
        var beams = OnPiles(document, walls);
        var model = AnalyticalModel.Of(document);
        var analysis = StructuralAnalysis.Run(document, model, StructuralLoading.Of(document, model));
        Assert.Empty(analysis.Problems);

        var designs = analysis.GroundBeamDesigns(document);
        Assert.Equal(beams.Count, designs.Count);
        foreach (var design in designs)
        {
            // Sagging between the piles, hogging over them; each face at least its least steel.
            Assert.True(design.Sagging > 0 || design.Hogging > 0, $"{design.Member.Name}: no bending at all");
            Assert.Equal(StructureRules.UkLeastSteel * design.Beam.Width * design.BottomDepth, design.BottomLeast, 6);
            Assert.True(design.BottomNeeded >= design.BottomLeast - 1e-6 && design.TopNeeded >= design.TopLeast - 1e-6);
            Assert.Equal(3 * Math.PI * 16 * 16 / 4, design.BottomGiven, 6);
            Assert.Contains("BS EN 1992-1-1", design.By);
            Assert.Equal(design.Beam.Depth - 75 - 8 - 8, design.BottomDepth, 6);

            // The moments are the envelope of the ultimate combinations: none of them finds more.
            foreach (var combination in analysis.Loading.Combinations.Where(combination => combination.State == LimitState.Ultimate))
                Assert.True(Math.Abs(analysis.MemberOf(design.Member, combination).MomentY) <= Math.Max(design.Sagging, design.Hogging) + 1e-6);
        }

        // The walls on them span between the piles with them, as deep beams: said so in the notes.
        Assert.Contains(analysis.Notes, note => note.Contains("composite", StringComparison.Ordinal));

        // Bars below the least steel are said to be short.
        foreach (var beam in beams) (beam.TopBars, beam.BottomBars) = ("2 H6", "2 H6");
        Assert.All(analysis.GroundBeamDesigns(document), design => Assert.False(design.Enough));
    }
}
