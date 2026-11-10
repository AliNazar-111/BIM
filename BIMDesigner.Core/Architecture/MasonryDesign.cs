using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Materials;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// A masonry unit as design takes it: its strength, N/mm², K of the UK National Annex's Table NA.4 for
/// general purpose mortar, what kind it is for its flexural and shear strengths, its mortar, and a clay
/// unit's water absorption, percent; its group and the category of its maker's control; and whether it is a
/// calcium silicate or concrete brick, brick-sized, whose bending strengths are their own.
/// </summary>
public sealed record MasonryUnit(string Name, double Fb, double K, bool Clay, MortarClass Mortar = MortarClass.M4, double Absorption = 10,
    MasonryGroup Group = MasonryGroup.Group1, UnitCategory Category = UnitCategory.CategoryII, bool BrickSized = false);

/// <summary>One wall's checks: the worst of each over every ultimate combination, and how it stands.</summary>
public sealed record WallCheck(AnalyticalPanel Panel, string Grade, string By, IReadOnlyList<StrengthCheck> Checks, double EffectiveHeight, double EffectiveThickness, string Limits)
{
    public StrengthCheck? Worst => Checks.Count == 0 ? null : Checks.MaxBy(check => check.Ratio);

    public double Ratio => Worst?.Ratio ?? 0;

    public bool Enough => Ratio <= 1 + 1e-9;
}

/// <summary>
/// Masonry walls checked from what the analysis finds in them, by BS EN 1996-1-1 and its UK National
/// Annex: compression at the top and foot and mid height with their eccentricities (6.1.2.2,
/// Annex G), slenderness, bending each way under the wind (6.3) and shear (6.2), over bands of about a
/// metre of wall so a peak at the corner of a window does not stand for the wall. γM by Table NA.1 from the
/// category of the unit and the project's class of execution control, the worse of each unless set. A material's
/// strength, mortar and group are its own, or failing them what it is called has. TMS 402's values could not be
/// confirmed, so a US project's masonry is not checked.
/// </summary>
public static class MasonryDesign
{
    /// <summary>γM for Category II units and class 2 of execution control, the worse of each: what is taken where nothing is set.</summary>
    public const double GammaCompression = 3.0, GammaFlexure = 2.7, GammaShear = 2.5;

    /// <summary>
    /// γM by the UK National Annex's Table NA.1, as remembered: in compression 2.3 and 2.7 for Category I units in class 1 and class 2 of execution control, 2.6 and 3.0 for Category II;
    /// in flexural tension 2.3 and 2.7 and in shear 2.5 for either.
    /// </summary>
    public static (double Compression, double Flexure, double Shear) Gammas(UnitCategory category, int execution) =>
        (category == UnitCategory.CategoryI ? (execution == 1 ? 2.3 : 2.7) : (execution == 1 ? 2.6 : 3.0), execution == 1 ? 2.3 : 2.7, 2.5);

    /// <summary>
    /// The unit a material is. By its name: clay brick of 20 N/mm², calcium silicate or concrete brick of 20, dense concrete block of 7.3, aircrete block of 3.6. K by the UK National
    /// Annex's Table NA.4 for general purpose mortar, as the Concrete Centre's guide 2 (Vertical resistance, Revision 2) prints it: clay 0.50 in Group 1 and 0.40 in Group 2, calcium silicate
    /// the same, aggregate concrete 0.75 and 0.70, aircrete 0.75; each strength as declared, not normalised for its shape, on the safe side for a block on edge. A strength, mortar, water
    /// absorption, group or category of its own stand in for what its name has.
    /// </summary>
    public static MasonryUnit UnitOf(string material, double strength = 0, MortarClass mortar = MortarClass.M4, double absorption = 0,
        MasonryGroup group = MasonryGroup.Group1, UnitCategory category = UnitCategory.CategoryII)
    {
        var name = material.ToLowerInvariant();
        var usual = absorption > 0 ? absorption : 10;
        var second = group == MasonryGroup.Group2;
        if (name.Contains("aircrete") || name.Contains("aerated")) return new MasonryUnit("Aircrete block", strength > 0 ? strength : 3.6, 0.75, false, mortar, usual, MasonryGroup.Group1, category);
        if (name.Contains("calcium silicate") || name.Contains("sand lime") || name.Contains("sandlime"))
            return new MasonryUnit("Calcium silicate brick", strength > 0 ? strength : 20, second ? 0.40 : 0.50, false, mortar, usual, group, category, true);
        if (name.Contains("brick") && name.Contains("concrete"))
            return new MasonryUnit("Concrete brick", strength > 0 ? strength : 20, second ? 0.70 : 0.75, false, mortar, usual, group, category, true);
        if (name.Contains("brick")) return new MasonryUnit("Clay brick", strength > 0 ? strength : 20, second ? 0.40 : 0.50, true, mortar, usual, group, category);
        return new MasonryUnit("Concrete block", strength > 0 ? strength : 7.3, second ? 0.70 : 0.75, false, mortar, usual, group, category);
    }

    /// <summary>The unit a material is, with what is set on it.</summary>
    public static MasonryUnit UnitOf(Material material) => UnitOf(material.Name, material.UnitStrength, material.Mortar, material.WaterAbsorption, material.Group, material.Category);

    /// <summary>A mortar's strength, N/mm²: no more than twice the unit's (NA.2.4).</summary>
    public static double MortarStrength(MasonryUnit unit) => Math.Min(unit.Mortar switch
    {
        MortarClass.M2 => 2,
        MortarClass.M6 => 6,
        MortarClass.M12 => 12,
        _ => 4
    }, 2 * unit.Fb);

    /// <summary>Equation 3.1: fk = K fb^0.7 fm^0.3, N/mm².</summary>
    public static double Characteristic(MasonryUnit unit) => unit.K * Math.Pow(unit.Fb, 0.7) * Math.Pow(MortarStrength(unit), 0.3);

    /// <summary>
    /// Table NA.6, N/mm². Clay by its water absorption and mortar. Concrete and aircrete units in M12, M6 or M4, then M2: of 2.9 to 7.3
    /// N/mm² by leaf thickness, up to 100 mm and from 250, and between by straight lines (Note 3); of 10.4 and 17.5 at any thickness,
    /// a strength between two rows taken at the lower.
    /// </summary>
    public static (double Fxk1, double Fxk2) FlexuralStrengths(MasonryUnit unit, double thickness)
    {
        var (stronger, middle) = (unit.Mortar == MortarClass.M12, unit.Mortar is MortarClass.M6 or MortarClass.M4);
        double Column(double high, double mid, double low) => stronger ? high : middle ? mid : low;
        if (unit.Clay)
            return unit.Absorption < 7 ? (Column(0.7, 0.5, 0.4), Column(2.0, 1.5, 1.2))
                : unit.Absorption <= 12 ? (Column(0.5, 0.4, 0.35), Column(1.5, 1.1, 1.0))
                : (Column(0.4, 0.3, 0.25), Column(1.1, 0.9, 0.8));

        // Calcium silicate and aggregate concrete brick-sized units, 0.3 and 0.9 in M12 to M4, 0.2 and 0.6 in M2 (guide 3, Table 1).
        var weak = unit.Mortar == MortarClass.M2;
        if (unit.BrickSized) return (weak ? 0.2 : 0.3, weak ? 0.6 : 0.9);
        if (unit.Fb >= 17.5) return (weak ? 0.2 : 0.25, weak ? 0.7 : 0.9);
        if (unit.Fb >= 10.4) return (weak ? 0.2 : 0.25, weak ? 0.6 : 0.75);

        // Across the strengths, then the thickness.
        double Along(double at29, double at36, double at73) => unit.Fb <= 2.9 ? at29
            : unit.Fb <= 3.6 ? at29 + (unit.Fb - 2.9) / 0.7 * (at36 - at29)
            : unit.Fb <= 7.3 ? at36 + (unit.Fb - 3.6) / 3.7 * (at73 - at36)
            : at73;
        var (thin, thick) = weak ? (Along(0.4, 0.4, 0.5), Along(0.2, 0.2, 0.3)) : (Along(0.4, 0.45, 0.6), Along(0.25, 0.25, 0.35));
        var (bedThin, bedThick) = weak ? (0.2, 0.1) : (0.25, 0.15);
        var share = Math.Clamp((thickness - 100) / 150, 0, 1);
        return (bedThin + share * (bedThick - bedThin), thin + share * (thick - thin));
    }

    /// <summary>Table NA.5: fvko by the mortar, for clay and for the rest.</summary>
    public static double InitialShear(MasonryUnit unit) => unit.Mortar switch
    {
        MortarClass.M12 => unit.Clay ? 0.30 : 0.20,
        MortarClass.M2 => 0.10,
        _ => unit.Clay ? 0.20 : 0.15
    };

    /// <summary>The effective thickness of a cavity wall, 5.5.1.3(3) with ktef 1: its outer leaf counted no thicker than the loaded one.</summary>
    public static double EffectiveThickness(double outer, double loaded)
    {
        var counted = Math.Min(outer, loaded);
        return outer <= 0 ? loaded : Math.Pow(counted * counted * counted + loaded * loaded * loaded, 1.0 / 3);
    }

    /// <summary>
    /// ρ of 5.5.1.2: ρ2, 0.75 between concrete floors and 1.0 with a timber one, for a wall held top and foot; ρ3 with
    /// one vertical edge stiffened, ρ4 with two; a wall long for its thickness, or with no edges held, stays at ρ2.
    /// </summary>
    public static double Rho(double rho2, double height, double length, double thickness, int edges)
    {
        if (edges >= 2 && length < 30 * thickness)
            return height <= 1.15 * length ? rho2 / (1 + Math.Pow(rho2 * height / length, 2)) : 0.5 * length / height;
        if (edges == 1 && length < 15 * thickness)
            return height <= 3.5 * length ? rho2 / (1 + Math.Pow(rho2 * height / (3 * length), 2)) : Math.Max(1.5 * length / height, 0.3);
        return rho2;
    }

    /// <summary>Φi = 1 - 2 ei / t at the top or foot of a wall, 6.4.</summary>
    public static double PhiEnd(double eccentricity, double thickness) => Math.Max(0, 1 - 2 * eccentricity / thickness);

    /// <summary>Φm at mid height, Annex G with E = 1000 fk: A1 e^(-u²/2), A1 = 1 - 2 emk/t, u = (hef/tef - 2) / (23 - 37 emk/t).</summary>
    public static double PhiMiddle(double eccentricity, double thickness, double slenderness)
    {
        var share = eccentricity / thickness;
        var a1 = 1 - 2 * share;
        if (a1 <= 0) return 0;
        var u = (slenderness - 2) / (23 - 37 * share);
        return a1 * Math.Exp(-u * u / 2);
    }

    /// <summary>What a stretch of wall has to carry against what it can, N per mm and N mm per mm of what the analysis found.</summary>
    public static IEnumerable<StrengthCheck> Checks(MasonryUnit loaded, double t, MasonryUnit? outer, double outerT, double hef, double tef, WallStretch band,
        (double Compression, double Flexure, double Shear)? gammas = null, double area = double.PositiveInfinity, bool bending = true)
    {
        var (gammaC, gammaF, _) = gammas ?? (GammaCompression, GammaFlexure, GammaShear);

        // Sections of small plan area, under 0.1 m², by (0.7 + 3A) (6.1.2.1(3)).
        var small = area < 0.1 ? 0.7 + 3 * area : 1;
        var (fk, fd) = (Characteristic(loaded), Characteristic(loaded) / gammaC * small);
        var slenderness = hef / tef;
        var initial = hef / 450;
        var compression = Math.Max(0, -band.Vertical);
        var stress = compression / t;

        if (compression > 0)
        {
            // A rigid joint overstates what a floor can put on a wall; past 0.45 t the load is taken on a stress block at its face no deeper than 0.1 t (Annex C(4) and (5), for any calculation, 5.5.1.1).
            // The wind is left out of it, ehm taken as nothing: the wind is designed for by flexure, 6.3, its strength helped by what bears down (fxd1,app), as BS EN 1996-1-1 allows in place of
            // eccentricity - taken here too it would be counted twice.
            var eccentric = Math.Min(Math.Max(Math.Abs(band.GravityMoment) / compression + initial, 0.05 * t), 0.45 * t);
            if (band.End)
                yield return new StrengthCheck("Load at its top or foot", compression, PhiEnd(eccentric, t) * t * fd, "kN/m", "6.1.2.2", $"Φi {PhiEnd(eccentric, t):0.00}, e {eccentric:0.0} mm");
            else
                yield return new StrengthCheck("Load at mid height", compression, PhiMiddle(eccentric, t, slenderness) * t * fd, "kN/m", "6.1.2.2, Annex G", $"Φm {PhiMiddle(eccentric, t, slenderness):0.00}, e {eccentric:0.0} mm");
        }

        if (!bending) yield break;

        // Wind, where the wall has openings and the plate's moments stand for it: each leaf takes its share, the loaded one helped by the weight on it (6.16).
        var (fxk1, fxk2) = FlexuralStrengths(loaded, t);
        var (zLoaded, zOuter) = (t * t / 6, outerT * outerT / 6);
        var (outerK1, outerK2) = outer is null ? (0.0, 0.0) : FlexuralStrengths(outer, outerT);
        var apparent = fxk1 / gammaF + Math.Min(stress, 0.2 * fd);
        yield return new StrengthCheck("Bending up its height", Math.Abs(band.MomentVertical) / 1000, (apparent * zLoaded + outerK1 / gammaF * zOuter) / 1000, "kNm/m", "6.3.1", $"fxk1 {fxk1:0.00}");
        yield return new StrengthCheck("Bending along its length", Math.Abs(band.MomentHorizontal) / 1000, (fxk2 / gammaF * zLoaded + outerK2 / gammaF * zOuter) / 1000, "kNm/m", "6.3.1", $"fxk2 {fxk2:0.00}");

    }

    /// <summary>
    /// A wall's moments as they stand once a joint has cracked. Past 0.45 t at its top or foot the rigid joint the plates make is gone (Annex C(4)), and what it was putting on the middle of the wall
    /// with it: there the eccentricity is no more than the average of its ends', as each now stands, a wall bending between them. The moment it was bending with goes too, and what the wind puts on it stays.
    /// </summary>
    public static IEnumerable<WallStretch> Relieved(IReadOnlyList<WallStretch> bands, double t)
    {
        foreach (var stretch in bands.GroupBy(band => band.Stretch))
        {
            var ends = stretch.Where(band => band.End && band.Vertical < 0).ToList();
            double Off(WallStretch band) => Math.Abs(band.GravityMoment) / -band.Vertical;
            var cracked = ends.Any(band => Off(band) > 0.45 * t);
            var most = cracked ? ends.Average(band => Math.Min(Off(band), 0.45 * t)) : double.PositiveInfinity;
            foreach (var band in stretch)
            {
                if (!cracked || band.Vertical >= 0)
                {
                    yield return band;
                    continue;
                }

                var left = Math.Sign(band.GravityMoment) * Math.Min(Math.Abs(band.GravityMoment), (band.End ? 0.45 * t : most) * -band.Vertical);
                yield return band with { GravityMoment = left, MomentVertical = band.MomentVertical - band.GravityMoment + left };
            }
        }
    }

    /// <summary>
    /// A wall's shear in its own plane by BS EN 1996-1-1 6.2: all of it at a level against VRd = fvd t lc, lc the length in compression, openings left out, and fvd from the average stress over
    /// that length (3.5), no more than 0.065 fb (NA.2.5). It is the wall that carries it, not a metre of it beside a window, where a plate's shear peaks.
    /// </summary>
    public static StrengthCheck WholeShear(MasonryUnit unit, double t, IEnumerable<WallStretch> level, double gammaShear = GammaShear)
    {
        var stretches = level.ToList();
        var compressed = stretches.Where(stretch => stretch.Vertical < 0).ToList();
        var length = compressed.Sum(stretch => stretch.Length);
        var stress = length > 0 ? compressed.Sum(stretch => -stretch.Vertical * stretch.Length) / (length * t) : 0;
        var fvk = Math.Min(InitialShear(unit) + 0.4 * stress, 0.065 * unit.Fb);
        return new StrengthCheck("Shear in its plane", Math.Abs(stretches.Sum(stretch => stretch.Shear * stretch.Length)) / 1000, fvk / gammaShear * t * length / 1000, "kN", "6.2, 3.6.2", $"fvk {fvk:0.00}, {length / 1000:0.0} m in compression");
    }

    /// <summary>
    /// A panel with no openings under the wind by BS EN 1996-1-1 6.3.1 and Annex E: MEd1 = μ α2 WEd l² with its plane of failure along the bed joints and MEd2 = α2 WEd l² across them, α2 for
    /// its edges, μ = fxk1 / fxk2; against MRd1 = (fxk1 / γM + σd) Z, σd no more than 0.2 fk / γM, and MRd2 = fxk2 / γM Z, a cavity wall's leaves together by their capacities, the loaded
    /// leaf's μ. h and l in mm, the wind kN/m², σd N/mm².
    /// </summary>
    public static IEnumerable<StrengthCheck> Panel(MasonryUnit loaded, double t, MasonryUnit? outer, double outerT, double height, double length, PanelEdge top, PanelEdge left, PanelEdge right,
        double wind, double stress, (double Compression, double Flexure, double Shear) gammas, string combination = "")
    {
        var (fxk1, fxk2) = FlexuralStrengths(loaded, t);
        var mu = fxk1 / fxk2;
        var (alpha, said) = LateralPanels.Alpha(top, PanelEdge.Simple, left, right, mu, height, length);
        var squared = length * length / 1e6;
        var (zLoaded, zOuter) = (t * t / 6 * 1000, outerT * outerT / 6 * 1000);
        var (outerK1, outerK2) = outer is null ? (0.0, 0.0) : FlexuralStrengths(outer, outerT);
        var fd = Characteristic(loaded) / gammas.Compression;
        var apparent = fxk1 / gammas.Flexure + Math.Min(Math.Max(0, stress), 0.2 * fd);
        yield return new StrengthCheck("Wind, plane along the bed joints", Math.Clamp(mu, 0.05, 1) * alpha * wind * squared, (apparent * zLoaded + outerK1 / gammas.Flexure * zOuter) / 1e6, "kNm/m",
            "6.3.1, Annex E", $"MEd1 = μ α2 W l², {said}, α2 {alpha:0.000}, fxk1 {fxk1:0.00} with σd {Math.Min(Math.Max(0, stress), 0.2 * fd):0.00}", combination);
        yield return new StrengthCheck("Wind, plane across the bed joints", alpha * wind * squared, (fxk2 / gammas.Flexure * zLoaded + outerK2 / gammas.Flexure * zOuter) / 1e6, "kNm/m",
            "6.3.1, Annex E", $"MEd2 = α2 W l², fxk2 {fxk2:0.00}", combination);
    }

    /// <summary>Every bearing wall's checks: masonry, or timber and concrete by their own rules. A masonry wall in a US project is said not to be, and any other.</summary>
    public static IReadOnlyList<WallCheck> Of(BimDocument document, SecondOrderAnalysis second)
    {
        var analysis = second.Analysis;
        var us = document.ProjectInformation.ClearanceStandard.IsUs();
        var results = new List<WallCheck>();
        foreach (var panel in analysis.Model.Panels.Where(panel => panel.Kind == AnalyticalPanelKind.Wall))
        {
            var quality = panel.Material.Quality;
            if (panel.Material.Kind == AnalyticalMaterialKind.Timber)
            {
                results.Add(TimberWallDesign.Check(document, second, panel, us));
                continue;
            }

            if (panel.Material.Kind == AnalyticalMaterialKind.Concrete)
            {
                results.Add(ConcreteWallDesign.Check(document, second, panel, us));
                continue;
            }

            if (panel.Material.Kind != AnalyticalMaterialKind.Masonry)
            {
                results.Add(new WallCheck(panel, quality, string.Empty, Array.Empty<StrengthCheck>(), 0, panel.Thickness, $"Not checked: a {panel.Material.Kind.ToString().ToLowerInvariant()} wall is not worked out here."));
                continue;
            }

            if (us)
            {
                results.Add(new WallCheck(panel, quality, string.Empty, Array.Empty<StrengthCheck>(), 0, panel.Thickness, "Not checked: TMS 402's strengths could not be confirmed, so they are not used."));
                continue;
            }

            results.Add(Check(document, second, panel));
        }

        return results;
    }

    /// <summary>The unit of the wall's thickest structural layer: its own material, with what is set on it.</summary>
    public static MasonryUnit LoadedUnitOf(BimDocument document, AnalyticalPanel panel) => LoadedUnit(document, panel);

    /// <summary>A wall's other leaf, for what joins it to this one.</summary>
    public static (MasonryUnit? Unit, double Thickness) OuterLeafOf(BimDocument document, AnalyticalPanel panel) => OuterLeaf(document, panel);

    private static MasonryUnit LoadedUnit(BimDocument document, AnalyticalPanel panel)
    {
        var material = panel.Element is Wall wall && document.GetWallType(wall) is { } type
            ? type.Structure.Layers.Where(layer => layer.Function == LayerFunction.Structure).OrderByDescending(layer => layer.Thickness).Select(layer => document.FindMaterial(layer.MaterialId)).FirstOrDefault()
            : null;
        var unit = material is null ? UnitOf(panel.Material.Name) : UnitOf(material);

        // A wall of bricks thicker than one, no more than 112.5 wide, has a mortar joint along it through some of its length: K by 0.8 (3.6.1.2(4), Figure 2 of guide 2).
        return (unit.Clay || unit.BrickSized) && panel.Thickness > 112.5 ? unit with { K = 0.8 * unit.K, Name = $"{unit.Name}, a joint along its face" } : unit;
    }

    /// <summary>A wall's other leaf: the thickest masonry in it beyond its structure, at least 75 mm (NA.2.15).</summary>
    private static (MasonryUnit? Unit, double Thickness) OuterLeaf(BimDocument document, AnalyticalPanel panel)
    {
        var (unit, thickness, _) = OuterLeafWeighed(document, panel);
        return (unit, thickness);
    }

    /// <summary>A wall's other leaf with its density, kg/m³: its material's, or 1,900 where none is set.</summary>
    private static (MasonryUnit? Unit, double Thickness, double Density) OuterLeafWeighed(BimDocument document, AnalyticalPanel panel)
    {
        if (panel.Element is not Wall wall || document.GetWallType(wall) is not { } type) return (null, 0, 0);
        var leaf = type.Structure.Layers.Where(layer => layer.Function != LayerFunction.Structure && layer.Thickness >= 75)
            .Select(layer => (Layer: layer, Material: document.FindMaterial(layer.MaterialId)))
            .Where(entry => entry.Material is not null && AnalyticalModel.MaterialOf(document, entry.Material).Kind == AnalyticalMaterialKind.Masonry)
            .OrderByDescending(entry => entry.Layer.Thickness).FirstOrDefault();
        return leaf.Material is null ? (null, 0, 0) : (UnitOf(leaf.Material), leaf.Layer.Thickness, leaf.Material.Density > 0 ? leaf.Material.Density : 1900);
    }

    /// <summary>How a wall is held: the height it stands between what holds it, whether that is a floor at its top, concrete at both, and the vertical edges stiffened.</summary>
    public static (double Height, bool Held, bool Concrete, int Edges) Holding(AnalyticalModel model, AnalyticalPanel wall, double effectiveThickness = 0)
    {
        var (height, held, concrete, left, right) = Edges(model, wall, effectiveThickness);
        return (height, held, concrete, (left ? 1 : 0) + (right ? 1 : 0));
    }

    /// <summary>
    /// A wall's edges under the wind for Annex E: its top simply supported where something holds it and free where nothing does, its foot simply supported; each vertical edge free unless a wall
    /// stiffens it, simply supported where one does and continuous where the wall itself runs on past it, in its own plane. A wall with openings has none of these: its edges are its own.
    /// </summary>
    public static (PanelEdge Top, PanelEdge Left, PanelEdge Right) PanelEdges(AnalyticalModel model, AnalyticalPanel wall, double effectiveThickness = 0)
    {
        var (_, held, _, left, right) = Edges(model, wall, effectiveThickness);
        var flat = wall.FlatOutline;
        var (bottom, top) = (flat.Min(point => point.Y), flat.Max(point => point.Y));
        var (start, end) = (flat.Min(point => point.X), flat.Max(point => point.X));
        PanelEdge Side(bool stiffened, double along)
        {
            if (!stiffened) return PanelEdge.Free;
            var at = new Geometry.Point2D(wall.Origin.X + wall.U.X * along, wall.Origin.Y + wall.U.Y * along);
            var runsOn = model.Panels.Any(other => other.Kind == AnalyticalPanelKind.Wall && other != wall && Math.Abs(AnalyticalModel.Dot(other.U, wall.U)) > 0.95
                && other.Outline.Any(node => Math.Abs(node.At.X - at.X) + Math.Abs(node.At.Y - at.Y) <= 50 && node.At.Z >= bottom - 1 && node.At.Z <= top + 1)
                && other.FlatOutline.Max(point => point.X) - other.FlatOutline.Min(point => point.X) > 300);
            return runsOn ? PanelEdge.Continuous : PanelEdge.Simple;
        }

        return (held ? PanelEdge.Simple : PanelEdge.Free, Side(left, start), Side(right, end));
    }

    private static (double Height, bool Held, bool Concrete, bool Left, bool Right) Edges(AnalyticalModel model, AnalyticalPanel wall, double effectiveThickness)
    {
        var flat = wall.FlatOutline;
        var (bottom, top) = (flat.Min(point => point.Y), flat.Max(point => point.Y));
        var (start, end) = (flat.Min(point => point.X), flat.Max(point => point.X));
        var (height, length) = (top - bottom, end - start);

        // What holds its top: the highest plate or diaphragm it reaches.
        bool Crosses(AnalyticalPanel plate, double z)
        {
            var at = plate.Flat(new Geometry.Point3D(wall.Origin.X + wall.U.X * (start + end) / 2, wall.Origin.Y + wall.U.Y * (start + end) / 2, z));
            var outline = plate.FlatOutline;
            return Geometry.Polygon2D.Contains(outline, at) || Enumerable.Range(0, outline.Count).Any(i => Geometry.Line2D.DistanceFromSegment(at, outline[i], outline[(i + 1) % outline.Count]) <= 100);
        }

        var plates = model.Panels.Where(plate => plate.Kind is AnalyticalPanelKind.Floor or AnalyticalPanelKind.Roof).Concat(model.Diaphragms)
            .Where(plate => plate.Origin.Z > bottom + 300 && plate.Origin.Z <= top + 600 && Crosses(plate, plate.Origin.Z)).OrderByDescending(plate => plate.Origin.Z).ToList();
        var held = plates.FirstOrDefault();
        var clear = held is null ? height : Math.Min(held.Origin.Z, top) - bottom;
        var below = model.Panels.Where(plate => plate.Kind is AnalyticalPanelKind.Floor or AnalyticalPanelKind.GroundSlab or AnalyticalPanelKind.Raft).Any(plate => Math.Abs(plate.Origin.Z - bottom) <= 600 && Crosses(plate, plate.Origin.Z));
        var footing = model.Supports.Any(support => support.Panel == wall) || Math.Abs(bottom - flat.Min(point => point.Y)) < 1 && model.Panels.All(other => other.Kind != AnalyticalPanelKind.Wall || other == wall || Math.Abs(other.Outline.Max(node => node.At.Z) - bottom) > 50);
        var concrete = held is { Kind: not AnalyticalPanelKind.Diaphragm } && (below || footing);

        // Its edges, stiffened by a wall meeting them across it, where no large opening stands in the way (5.5.1.2(9)).
        var area = Geometry.Polygon2D.Area(flat);
        var opened = wall.Cuts.Any(opening =>
        {
            var points = opening.Select(wall.Flat).ToList();
            var (wide, high) = (points.Max(p => p.X) - points.Min(p => p.X), points.Max(p => p.Y) - points.Min(p => p.Y));
            return wide > length / 4 || high > clear / 4 || wide * high > area / 10;
        });

        // A wall stiffening this one at least a fifth of its height long and 0.3 of its effective thickness thick (5.5.1.2(10)), or a stiffener found for it, which is held to the same.
        var needed = 0.3 * (effectiveThickness > 0 ? effectiveThickness : wall.Thickness);
        bool Stiffened(double along)
        {
            if (opened) return false;
            var plan = new Geometry.Point2D(wall.Origin.X + wall.U.X * along, wall.Origin.Y + wall.U.Y * along);
            return model.Panels.Any(other => other.Kind == AnalyticalPanelKind.Wall && other != wall && Math.Abs(AnalyticalModel.Dot(other.U, wall.U)) < 0.3 && other.Thickness >= needed - 1e-6
                                             && other.FlatOutline.Max(point => point.X) - other.FlatOutline.Min(point => point.X) >= clear / 5 - 1 &&
                                             other.Outline.Any(node => Math.Abs(node.At.X - plan.X) + Math.Abs(node.At.Y - plan.Y) <= 50 && node.At.Z >= bottom - 1 && node.At.Z <= top + 1))
                   || model.Stiffeners.Any(cross => cross.Host == wall.Element && Math.Abs(cross.At.X - plan.X) + Math.Abs(cross.At.Y - plan.Y) <= 50);
        }

        return (clear, held is not null, concrete, Stiffened(start), Stiffened(end));
    }

    private static WallCheck Check(BimDocument document, SecondOrderAnalysis second, AnalyticalPanel panel)
    {
        var model = second.Analysis.Model;
        var unit = LoadedUnit(document, panel);
        var t = panel.Thickness;
        var (outer, outerT, density) = OuterLeafWeighed(document, panel);
        var tef = EffectiveThickness(outerT, t);
        var (height, held, concrete, edges) = Holding(model, panel, tef);
        var flat = panel.FlatOutline;
        var length = flat.Max(point => point.X) - flat.Min(point => point.X);
        var execution = document.ProjectInformation.MasonryExecutionClass;
        var gammas = Gammas(unit.Category, execution);
        var bands = second.WallStretches(panel, height);

        // ρ2 0.75 between concrete floors, unless the load at the top is more than 0.25 t off its centre (5.5.1.2(11)): then 1.0.
        var offCentre = bands.SelectMany(entry => entry.Bands).Where(band => band.End && band.Vertical < 0).Select(band => Math.Abs(band.GravityMoment) / -band.Vertical).DefaultIfEmpty(0).Max();
        var rho2 = concrete && offCentre <= 0.25 * t ? 0.75 : 1.0;
        var hef = held ? Rho(rho2, height, length, t, edges) * height : 2 * height;

        // Where the wall has no openings, its bending under the wind by Annex E's coefficients for its edges; where it has, the plate's own moments, the coefficients being for whole panels.
        var whole = panel.Cuts.Count == 0;
        var (top, left, right) = PanelEdges(model, panel, tef);
        IEnumerable<StrengthCheck> Under(LoadCombination combination, IReadOnlyList<WallStretch> stretches)
        {
            var areas = stretches.GroupBy(band => band.Level).ToDictionary(level => level.Key, level => level.Sum(band => band.Length) * t / 1e6);
            foreach (var band in Relieved(stretches, t))
                foreach (var check in Checks(unit, t, outer, outerT, hef, tef, band, gammas, areas.GetValueOrDefault(band.Level, double.PositiveInfinity), !whole))
                    yield return check;
            foreach (var level in stretches.GroupBy(band => band.Level)) yield return WholeShear(unit, t, level, gammas.Shear);
            if (!whole) yield break;
            var wind = TimberWallDesign.WindOn(second.Analysis.Loading, panel, combination);
            if (wind <= 1e-6) yield break;
            var middle = stretches.Where(band => !band.End).Select(band => -band.Vertical / t).DefaultIfEmpty(0).Min();
            foreach (var check in Panel(unit, t, outer, outerT, height, length, top, left, right, wind, middle, gammas)) yield return check;
        }

        var checks = MemberStrength.Worst(bands.Select(entry => (entry.Combination.Name, Under(entry.Combination, entry.Bands)))).ToList();
        checks.Insert(0, new StrengthCheck("Slenderness", hef / tef, 27, "hef/tef", "5.5.1.4", held ? $"ρ {hef / height:0.00}, {edges} edge{(edges == 1 ? "" : "s")} stiffened" : "nothing holds its top: it stands as a cantilever, at twice its height", string.Empty));

        // The outer leaf under its own weight, all of it from its foot to the top of the wall in its line: at its foot and mid height with the least eccentricity, 0.05 t, by its own
        // strength, the cavity wall's slenderness (5.5.1.3(1), 6.1.2.1).
        if (outer is not null)
        {
            var above = model.Panels.Where(other => other.Kind == AnalyticalPanelKind.Wall && Math.Abs(AnalyticalModel.Dot(other.U, panel.U)) > 0.95
                    && Math.Abs((other.Origin.X - panel.Origin.X) * -panel.U.Y + (other.Origin.Y - panel.Origin.Y) * panel.U.X) < 50 && other.Outline.Max(node => node.At.Z) > panel.Outline.Min(node => node.At.Z))
                .Select(other => other.Outline.Max(node => node.At.Z)).DefaultIfEmpty(panel.Outline.Max(node => node.At.Z)).Max() - panel.Outline.Min(node => node.At.Z);
            var weight = 1.35 * density * 9.81 * outerT / 1e3 * above / 1e3 / 1e3;
            var outerGammas = Gammas(outer.Category, execution);
            var phi = Math.Min(PhiEnd(0.05 * outerT, outerT), PhiMiddle(0.05 * outerT, outerT, hef / tef));
            checks.Add(new StrengthCheck("Outer leaf under its own weight", weight, phi * outerT * Characteristic(outer) / outerGammas.Compression, "kN/m", "6.1.2.1, 5.5.1.3(1)",
                $"{above / 1000:0.0} m of it at {density:0} kg/m³ and 1.35, Φ {phi:0.00}"));
        }

        var grade = $"{unit.Name} {unit.Fb:0.0} in {unit.Mortar}, fk {Characteristic(unit):0.0}, {(unit.Group == MasonryGroup.Group1 ? "Group 1" : "Group 2")}{(outer is null ? "" : $", with a {outer.Name.ToLowerInvariant()} leaf of {outerT:0}")}";
        return new WallCheck(panel, grade, "BS EN 1996-1-1 and its UK National Annex", checks, hef, tef,
            $"γM {gammas.Compression:0.0} in compression, {gammas.Flexure:0.0} in flexure, {gammas.Shear:0.0} in shear: {(unit.Category == UnitCategory.CategoryI ? "Category I" : "Category II")} units, class {execution} of execution control (Table NA.1). " +
            (whole ? $"Its bending under the wind by Annex E, its top {Said(top)}, its foot simply supported, its ends {Said(left)} and {Said(right)}; " : "Its bending under the wind from the plate's moments, Annex E's coefficients being for panels with no openings; ") +
            "units' strengths, mortars and groups as set on their materials, or as their names have them; Annex F's limiting sizes for panels under the wind are not checked.");
    }

    private static string Said(PanelEdge edge) => edge switch { PanelEdge.Free => "free", PanelEdge.Simple => "simply supported", _ => "continuous" };
}

/// <summary>A stretch of wall about a metre long and a band of its height: its average forces in its plane, N per mm, compression negative, and moments, N mm per mm, up it and along it, with what is left of the moment up it without the wind and what of its shear the wind alone puts there.</summary>
public readonly record struct WallStretch(bool End, double Vertical, double Shear, double MomentVertical, double MomentHorizontal, double GravityMoment, int Level = 0, double Length = 1000, int Stretch = 0, double Lateral = 0);
