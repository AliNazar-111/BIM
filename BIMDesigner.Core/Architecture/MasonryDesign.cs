using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Materials;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// A masonry unit as design takes it: its strength, N/mm², K of the UK National Annex's Table NA.4 for
/// general purpose mortar, what kind it is for its flexural and shear strengths, its mortar, and a clay
/// unit's water absorption, percent.
/// </summary>
public sealed record MasonryUnit(string Name, double Fb, double K, bool Clay, MortarClass Mortar = MortarClass.M4, double Absorption = 10);

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
/// metre of wall so a peak at the corner of a window does not stand for the wall. γM 3.0 in compression,
/// 2.7 in flexure, 2.5 in shear (Table NA.1): Category II units, class 2 of execution control. A material's
/// strength and mortar are its own, or failing them what it is called has. TMS 402's values could not be
/// confirmed, so a US project's masonry is not checked.
/// </summary>
public static class MasonryDesign
{
    public const double GammaCompression = 3.0, GammaFlexure = 2.7, GammaShear = 2.5;

    /// <summary>
    /// The unit a material is. By its name: clay brick of 20 N/mm², dense concrete block of 7.3, aircrete block of 3.6, K 0.50 for
    /// clay and 0.55 for concrete and aircrete units of Group 1 (Table NA.4), each as declared, not normalised for its shape, on
    /// the safe side for a block on edge. A strength, mortar or water absorption of its own stand in for what its name has.
    /// </summary>
    public static MasonryUnit UnitOf(string material, double strength = 0, MortarClass mortar = MortarClass.M4, double absorption = 0)
    {
        var name = material.ToLowerInvariant();
        var usual = absorption > 0 ? absorption : 10;
        if (name.Contains("aircrete") || name.Contains("aerated")) return new MasonryUnit("Aircrete block", strength > 0 ? strength : 3.6, 0.55, false, mortar, usual);
        if (name.Contains("brick")) return new MasonryUnit("Clay brick", strength > 0 ? strength : 20, 0.50, true, mortar, usual);
        return new MasonryUnit("Concrete block", strength > 0 ? strength : 7.3, 0.55, false, mortar, usual);
    }

    /// <summary>The unit a material is, with what is set on it.</summary>
    public static MasonryUnit UnitOf(Material material) => UnitOf(material.Name, material.UnitStrength, material.Mortar, material.WaterAbsorption);

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

        var weak = unit.Mortar == MortarClass.M2;
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
    public static IEnumerable<StrengthCheck> Checks(MasonryUnit loaded, double t, MasonryUnit? outer, double outerT, double hef, double tef, WallStretch band)
    {
        var (fk, fd) = (Characteristic(loaded), Characteristic(loaded) / GammaCompression);
        var slenderness = hef / tef;
        var initial = hef / 450;
        var compression = Math.Max(0, -band.Vertical);
        var stress = compression / t;

        if (compression > 0)
        {
            var eccentric = Math.Max(Math.Abs(band.GravityMoment) / compression + initial, 0.05 * t);
            if (band.End)
                yield return new StrengthCheck("Load at its top or foot", compression, PhiEnd(eccentric, t) * t * fd, "kN/m", "6.1.2.2", $"Φi {PhiEnd(eccentric, t):0.00}, e {eccentric:0.0} mm");
            else
                yield return new StrengthCheck("Load at mid height", compression, PhiMiddle(eccentric, t, slenderness) * t * fd, "kN/m", "6.1.2.2, Annex G", $"Φm {PhiMiddle(eccentric, t, slenderness):0.00}, e {eccentric:0.0} mm");
        }

        // Wind: each leaf takes its share, the loaded one helped by the weight on it (6.16).
        var (fxk1, fxk2) = FlexuralStrengths(loaded, t);
        var (zLoaded, zOuter) = (t * t / 6, outerT * outerT / 6);
        var (outerK1, outerK2) = outer is null ? (0.0, 0.0) : FlexuralStrengths(outer, outerT);
        var apparent = fxk1 / GammaFlexure + Math.Min(stress, 0.2 * fd);
        yield return new StrengthCheck("Bending up its height", Math.Abs(band.MomentVertical) / 1000, (apparent * zLoaded + outerK1 / GammaFlexure * zOuter) / 1000, "kNm/m", "6.3.1", $"fxk1 {fxk1:0.00}");
        yield return new StrengthCheck("Bending along its length", Math.Abs(band.MomentHorizontal) / 1000, (fxk2 / GammaFlexure * zLoaded + outerK2 / GammaFlexure * zOuter) / 1000, "kNm/m", "6.3.1", $"fxk2 {fxk2:0.00}");

        // Shear in its own plane: (3.5), limited to 0.065 fb (NA.2.5).
        var fvk = Math.Min(InitialShear(loaded) + 0.4 * stress, 0.065 * loaded.Fb);
        yield return new StrengthCheck("Shear in its plane", Math.Abs(band.Shear), fvk / GammaShear * t, "kN/m", "6.2, 3.6.2", $"fvk {fvk:0.00}");
    }

    /// <summary>Every bearing wall's checks, a wall not of masonry, or in a US project, said not to be.</summary>
    public static IReadOnlyList<WallCheck> Of(BimDocument document, SecondOrderAnalysis second)
    {
        var analysis = second.Analysis;
        var us = document.ProjectInformation.ClearanceStandard.IsUs();
        var results = new List<WallCheck>();
        foreach (var panel in analysis.Model.Panels.Where(panel => panel.Kind == AnalyticalPanelKind.Wall))
        {
            var quality = panel.Material.Quality;
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
        return material is null ? UnitOf(panel.Material.Name) : UnitOf(material);
    }

    /// <summary>A wall's other leaf: the thickest masonry in it beyond its structure, at least 75 mm (NA.2.15).</summary>
    private static (MasonryUnit? Unit, double Thickness) OuterLeaf(BimDocument document, AnalyticalPanel panel)
    {
        if (panel.Element is not Wall wall || document.GetWallType(wall) is not { } type) return (null, 0);
        var leaf = type.Structure.Layers.Where(layer => layer.Function != LayerFunction.Structure && layer.Thickness >= 75)
            .Select(layer => (Layer: layer, Material: document.FindMaterial(layer.MaterialId)))
            .Where(entry => entry.Material is not null && AnalyticalModel.MaterialOf(document, entry.Material).Kind == AnalyticalMaterialKind.Masonry)
            .OrderByDescending(entry => entry.Layer.Thickness).FirstOrDefault();
        return leaf.Material is null ? (null, 0) : (UnitOf(leaf.Material), leaf.Layer.Thickness);
    }

    /// <summary>How a wall is held: the height it stands between what holds it, whether that is a floor at its top, concrete at both, and the vertical edges stiffened.</summary>
    public static (double Height, bool Held, bool Concrete, int Edges) Holding(AnalyticalModel model, AnalyticalPanel wall)
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
        var opened = wall.Openings.Any(opening =>
        {
            var points = opening.Select(wall.Flat).ToList();
            var (wide, high) = (points.Max(p => p.X) - points.Min(p => p.X), points.Max(p => p.Y) - points.Min(p => p.Y));
            return wide > length / 4 || high > clear / 4 || wide * high > area / 10;
        });

        var edges = 0;
        if (!opened)
            foreach (var along in new[] { start, end })
            {
                var plan = new Geometry.Point2D(wall.Origin.X + wall.U.X * along, wall.Origin.Y + wall.U.Y * along);
                if (model.Panels.Any(other => other.Kind == AnalyticalPanelKind.Wall && other != wall && Math.Abs(AnalyticalModel.Dot(other.U, wall.U)) < 0.3 &&
                                              other.Outline.Any(node => Math.Abs(node.At.X - plan.X) + Math.Abs(node.At.Y - plan.Y) <= 50 && node.At.Z >= bottom - 1 && node.At.Z <= top + 1))
                    || model.Stiffeners.Any(cross => cross.Host == wall.Element && Math.Abs(cross.At.X - plan.X) + Math.Abs(cross.At.Y - plan.Y) <= 50))
                    edges++;
            }

        return (clear, held is not null, concrete, edges);
    }

    private static WallCheck Check(BimDocument document, SecondOrderAnalysis second, AnalyticalPanel panel)
    {
        var model = second.Analysis.Model;
        var unit = LoadedUnit(document, panel);
        var t = panel.Thickness;
        var (outer, outerT) = OuterLeaf(document, panel);
        var tef = EffectiveThickness(outerT, t);
        var (height, held, concrete, edges) = Holding(model, panel);
        var flat = panel.FlatOutline;
        var length = flat.Max(point => point.X) - flat.Min(point => point.X);
        var hef = held ? Rho(concrete ? 0.75 : 1.0, height, length, t, edges) * height : 2 * height;

        var bands = second.WallStretches(panel, height);
        var checks = MemberStrength.Worst(bands.Select(entry => (entry.Combination.Name, entry.Bands.SelectMany(band => Checks(unit, t, outer, outerT, hef, tef, band))))).ToList();
        checks.Insert(0, new StrengthCheck("Slenderness", hef / tef, 27, "hef/tef", "5.5.1.4", held ? $"ρ {hef / height:0.00}, {edges} edge{(edges == 1 ? "" : "s")} stiffened" : "nothing holds its top: it stands as a cantilever, at twice its height", string.Empty));
        var grade = $"{unit.Name} {unit.Fb:0.0} in {unit.Mortar}, fk {Characteristic(unit):0.0}{(outer is null ? "" : $", with a {outer.Name.ToLowerInvariant()} leaf of {outerT:0}")}";
        return new WallCheck(panel, grade, "BS EN 1996-1-1 and its UK National Annex", checks, hef, tef,
            "Units' strengths and mortars as set on their materials, or as their names have them; bending taken from an even plate, not masonry's own two strengths; the outer leaf's own load is not checked.");
    }
}

/// <summary>A stretch of wall about a metre long and a band of its height: its average forces in its plane, N per mm, compression negative, and moments, N mm per mm, up it and along it, with what is left of the moment up it without the wind.</summary>
public readonly record struct WallStretch(bool End, double Vertical, double Shear, double MomentVertical, double MomentHorizontal, double GravityMoment);
