using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// A US home's outside wall coverings near the ground, by the 2021 IRC's model text (round 141): wood siding - board and batten, lap siding,
/// shingles and shakes - and its sheathing and framing 6 in (152) clear of the ground, 2 in (51) of a paved area, or else naturally durable or
/// preservative-treated (R317.1 item 5); stucco's weep screed, at the foundation plate line, 4 in (102) over the earth and 2 in over a paved
/// area (R703.7.2.1); and wood shakes and shingles over 1/2 in wood-based sheathing (R703.6.1).
///
/// The wall's outside face is its type's first layer; the ground the modelled terrain's - a region laid on it of path, paving or driveway being
/// paved - along its outside face, 300 out, at its highest; nothing asked where no ground is modelled. Naturally durable wood is the heartwood of
/// redwood, cedar, black locust or black walnut, told by the name, as is wood preservative-treated (AWPA U1).
///
/// Not done: R703.6.1's furring and exposures by Table R703.6.1, R703.3's siding thicknesses and fasteners, stucco's lath and coats (R703.7.1,
/// R703.7.2), water-resistive barriers (R703.2).
/// </summary>
public static class WallCoverings
{
    /// <summary>R317.1's clearances of wood siding from the ground and from a paved area, R703.7.2.1's of a weep screed, mm; R703.6.1's sheathing.</summary>
    public const double WoodFromGround = 152, FromPaving = 51, ScreedFromEarth = 102, ShingleSheathing = 11.5;

    /// <summary>How far out from a wall's outside face its ground is read, mm.</summary>
    public const double Out = 300;

    private static bool Has(string name, params string[] words) => words.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether a material is wood siding as R317.1 reads it: boards, battens, shingles, shakes, plywood - not vinyl, fiber cement, metal or a composite.</summary>
    public static bool Wood(Material material) =>
        !Has(material.Name, "vinyl", "fiber cement", "fibre cement", "aluminum", "aluminium", "steel", "composite", "asphalt", "pvc", "plastic")
        && Has(material.Name, "wood", "timber", "cedar", "pine", "redwood", "larch", "oak", "spruce", "batten", "shingle", "shake", "plywood", "osb", "softwood");

    /// <summary>Whether wood is naturally durable or preservative-treated, by its name: the heartwood of redwood, cedar, black locust or black walnut; treated.</summary>
    public static bool Durable(Material material) =>
        Has(material.Name, "cedar", "redwood", "locust", "walnut", "treated", "preservative", "pressure");

    /// <summary>Whether a material is stucco - exterior cement plaster - by its name.</summary>
    public static bool Stucco(Material material) => Has(material.Name, "stucco", "exterior plaster", "cement plaster");

    /// <summary>A wall type's outside face: its first layer with substance, and its material.</summary>
    public static (MaterialLayer Layer, Material Material)? Face(BimDocument document, WallType type) =>
        type.Structure.Layers.Where(layer => layer.Function != LayerFunction.Membrane && layer.Thickness > 0)
            .Select(layer => (Layer: layer, Material: document.FindMaterial(layer.MaterialId))).Where(entry => entry.Material is not null)
            .Select(entry => ((MaterialLayer, Material)?)(entry.Layer, entry.Material!)).FirstOrDefault();

    /// <summary>The ground along a wall's outside face at its highest, mm, and whether it is paved there; null where no ground is modelled.</summary>
    public static (double Ground, bool Paved)? GroundOutside(BimDocument document, Wall wall, WallType type)
    {
        var outward = SpaceSeparation.Outward(document, wall);
        var reach = type.Structure.TotalWidth / 2 + Out;
        (double Ground, bool Paved)? highest = null;
        for (var i = 1; i <= 9; i++)
        {
            var at = wall.Start + (wall.End - wall.Start) * (i / 10.0) + outward * reach;
            if (Terrains.GroundAt(document, at) is not { } ground) continue;
            var paved = document.All<GroundRegion>().Any(region => region.Surface is GroundSurface.Path or GroundSurface.Paving or GroundSurface.Driveway
                                                                   && region.Boundary.Count >= 3 && Polygon2D.Contains(region.Boundary, at));
            if (highest is null || ground > highest.Value.Ground) highest = (ground, paved);
        }

        return highest;
    }

    /// <summary>What a US home's outside wall covering falls short of near the ground, and a shingled wall's sheathing: said and the rule.</summary>
    public static IEnumerable<(string Said, string Rule)> Problems(BimDocument document, Wall wall, WallType type)
    {
        if (!document.ProjectInformation.ClearanceStandard.IsUs() || type.Function != WallFunction.Exterior || document.IsCurtainWall(wall)) yield break;
        if (Face(document, type) is not var (face, material)) yield break;
        var wood = Wood(material);
        var stucco = !wood && Stucco(material);

        // Wood shakes and shingles over 1/2 in wood-based sheathing (R703.6.1).
        if (wood && Has(material.Name, "shingle", "shake"))
        {
            var layers = type.Structure.Layers.Where(layer => layer.Function != LayerFunction.Membrane && layer.Thickness > 0).ToList();
            var behind = layers.Count > 1 ? document.FindMaterial(layers[1].MaterialId) : null;
            if (behind is null || !Wood(behind) || layers[1].Thickness < ShingleSheathing - 1e-6)
                yield return ($"it is clad in {material.Name} over {(behind is null ? "nothing" : $"{Units.FormatLength(layers[1].Thickness)} of {behind.Name}")}.",
                    "The IRC (R703.6.1) lays wood shakes and shingles over nominal 1/2 in (12.7) wood-based sheathing, or on furring strips over 1/2 in nonwood sheathing, a water-resistive barrier over it.");
        }

        if (!wood && !stucco) yield break;
        if (GroundOutside(document, wall, type) is not var (ground, paved)) yield break;
        var clear = wall.GetBaseElevation(document) - ground;
        if (wood && !Durable(material))
        {
            var asked = paved ? FromPaving : WoodFromGround;
            if (clear < asked - 0.5)
                yield return ($"its {material.Name} comes {(clear <= 0 ? "down to the ground or below it" : $"within {Units.FormatLength(clear)} of the {(paved ? "paving" : "ground")}")} outside, and is neither naturally durable nor preservative-treated.",
                    "The IRC (R317.1 item 5) asks wood siding, sheathing and wall framing outside a building to clear the ground by 6 in (152) and concrete steps, porch and patio slabs by 2 in (51) - or to be naturally durable wood, the heartwood of redwood, cedar, black locust or black walnut, or preservative-treated to AWPA U1.");
        }

        if (stucco)
        {
            var asked = paved ? FromPaving : ScreedFromEarth;
            if (clear < asked - 0.5)
                yield return ($"its {material.Name}'s weep screed, at the foundation plate line, is {(clear <= 0 ? "at the ground or below it" : $"{Units.FormatLength(clear)} over the {(paved ? "paving" : "earth")}")} outside.",
                    "The IRC (R703.7.2.1) sets the weep screed of stucco on a stud wall at or below the foundation plate line, 4 in (102) over the earth or 2 in (51) over a paved area, letting trapped water drain out.");
        }
    }
}
