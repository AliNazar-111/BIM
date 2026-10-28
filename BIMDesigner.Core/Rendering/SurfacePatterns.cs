using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;

namespace BIMDesigner.Core.Rendering;

/// <summary>
/// What a material's face is laid as: its pattern's usual units and joints, a pattern guessed from
/// the material's name where none is given - a project made before patterns, a ground region with
/// no material of its own - and the finish a mesh of the model is drawn with.
/// </summary>
public static class SurfacePatterns
{
    /// <summary>A pattern's usual unit, mm - its length and height, its joint - and its joint's colour where it is a mortar or grout rather than a shadow.</summary>
    public static (double Length, double Height, double Joint, ColourRgb? JointColour) Units(SurfacePattern pattern) => pattern switch
    {
        // A brick 215 x 65 and a block 440 x 215 with 10 mm joints; coursed stone.
        SurfacePattern.Brick => (215, 65, 10, new ColourRgb(0xCF, 0xC6, 0xB5)),
        SurfacePattern.Block => (440, 215, 10, new ColourRgb(0xBD, 0xB9, 0xAE)),
        SurfacePattern.Stone => (450, 225, 10, new ColourRgb(0xC9, 0xC2, 0xB2)),

        // Boards 150 mm wide, laid 3.6 m long; tiles 300 square, a 3 mm grout; 600 mm paving slabs.
        SurfacePattern.Boards => (3600, 150, 4, null),
        SurfacePattern.Tiles => (300, 300, 3, new ColourRgb(0xE6, 0xE5, 0xE0)),
        SurfacePattern.Paving => (600, 600, 6, new ColourRgb(0x8E, 0x8A, 0x82)),

        // Roofing by its width across and its gauge up the slope: a 500 x 250 slate at a 75 mm head lap,
        // a plain tile at 100 mm, an interlocking tile at 345 mm.
        SurfacePattern.Slate => (250, 212, 4, null),
        SurfacePattern.PlainTiles => (165, 100, 3, null),
        SurfacePattern.InterlockingTiles => (300, 345, 3, null),

        // Standing seams at 500 mm centres, 25 mm wide.
        SurfacePattern.StandingSeam => (500, 0, 25, null),
        _ => (0, 0, 0, null)
    };

    /// <summary>A pattern with its usual units, for the colour given.</summary>
    public static SurfaceFinish Standard(SurfacePattern pattern, ColourRgb colour, double reflectance = 0)
    {
        var (length, height, joint, jointColour) = Units(pattern);
        return new SurfaceFinish(colour, pattern, length, height, joint, jointColour ?? Shade(colour, 0.4), reflectance);
    }

    /// <summary>The finish a material's name suggests: brick laid as brick, a slate roof as slates, glass and paint as their colour.</summary>
    public static SurfaceFinish Guess(string name, ColourRgb colour)
    {
        var n = name.ToLowerInvariant();
        bool Has(params string[] words) => words.Any(n.Contains);

        if (Has("glass", "glazing")) return Standard(SurfacePattern.None, colour, 0.04);
        if (Has("insulation", "cavity", "wool", "fire stop", "membrane", "pir ", "vapour")) return Standard(SurfacePattern.None, colour);
        if (Has("composite", "decking")) return Standard(SurfacePattern.Boards, colour);
        if (Has("pvc", "plastic", "upvc")) return Standard(SurfacePattern.None, colour, 0.04);
        if (Has("paint", "emulsion", "plasterboard", "plaster", "render", "fibre cement")) return Standard(SurfacePattern.Render, colour, Has("gloss") ? 0.06 : 0);
        if (Has("brick")) return Standard(SurfacePattern.Brick, colour);
        if (Has("block", "aircrete", "breeze")) return Standard(SurfacePattern.Block, colour);
        if (Has("stone", "ashlar", "limestone", "sandstone", "granite")) return Standard(SurfacePattern.Stone, colour);
        if (Has("slate")) return Standard(SurfacePattern.Slate, colour, 0.02);
        if (Has("interlocking")) return Standard(SurfacePattern.InterlockingTiles, colour);
        if (Has("plain") && Has("tile")) return Standard(SurfacePattern.PlainTiles, colour);
        if (Has("mosaic")) return Standard(SurfacePattern.Tiles, colour, 0.08) with { UnitLength = 25, UnitHeight = 25, JointWidth = 2 };
        if (Has("tile")) return Standard(SurfacePattern.Tiles, colour, Has("gloss") ? 0.08 : 0.04);
        if (Has("zinc", "standing seam", "colour coated", "copper", "lead")) return Standard(SurfacePattern.StandingSeam, colour, Has("patina", "weathered") ? 0.02 : 0.12);
        if (Has("aluminium", "steel", "metal", "iron", "chrome", "brass")) return Standard(SurfacePattern.Metal, colour, Has("primed", "coated", "powder", "painted") ? 0.04 : 0.25);
        if (Has("glulam")) return Standard(SurfacePattern.Boards, colour) with { UnitLength = 6000, UnitHeight = 45, JointWidth = 1 };
        if (Has("timber", "wood", "cedar", "oak", "larch", "pine", "softwood", "hardwood", "composite", "rafters", "board")) return Standard(SurfacePattern.Boards, colour);
        if (Has("grass", "lawn", "turf")) return Standard(SurfacePattern.Grass, colour);
        if (Has("gravel", "shingle", "pebble")) return Standard(SurfacePattern.Gravel, colour);
        if (Has("paving", "path", "flag", "patio")) return Standard(SurfacePattern.Paving, colour);
        if (Has("planting", "soil", "bark", "mulch", "earth")) return Standard(SurfacePattern.Planting, colour);
        if (Has("concrete", "screed", "cast in situ", "cement")) return Standard(SurfacePattern.Concrete, colour);
        return Standard(SurfacePattern.None, colour);
    }

    /// <summary>A material given the finish its name suggests, where it has none of its own yet: one made before patterns.</summary>
    public static void Settle(Material material)
    {
        if (material.Pattern != SurfacePattern.None || material.UnitLength > 0 || material.Reflectance > 0) return;
        var guess = Guess(material.Name, material.SurfaceColour);
        material.Finish = guess with { Colour = material.SurfaceColour };
    }

    /// <summary>Every material of a project given the finish its name suggests where it has none.</summary>
    public static void Settle(BimDocument document)
    {
        foreach (var material in document.Materials) Settle(material);
    }

    /// <summary>
    /// The finish a mesh of the model is drawn with: its material's, found by the name the mesh is
    /// described by, in the mesh's own colour - a view's filter or phase may have changed it - or the
    /// finish the description suggests where it names no material: a lawn, gravel.
    /// </summary>
    public static SurfaceFinish Of(Mesh3D mesh, IReadOnlyDictionary<string, Material> materials)
    {
        if (mesh.Kind == MeshKind.Glazing) return Standard(SurfacePattern.None, mesh.Colour, 0.04);
        var finish = materials.TryGetValue(mesh.Description, out var material) ? material.Finish : Guess(mesh.Description, mesh.Colour);
        return finish with { Colour = mesh.Colour };
    }

    /// <summary>A project's materials by name, the first of each name, for <see cref="Of(Mesh3D, IReadOnlyDictionary{string, Material})"/>.</summary>
    public static IReadOnlyDictionary<string, Material> ByName(BimDocument document)
    {
        var byName = new Dictionary<string, Material>(StringComparer.OrdinalIgnoreCase);
        foreach (var material in document.Materials) byName.TryAdd(material.Name, material);
        return byName;
    }

    /// <summary>A colour darker or lighter by a factor.</summary>
    public static ColourRgb Shade(ColourRgb colour, double factor) => new(
        (byte)Math.Clamp(Math.Round(colour.R * factor), 0, 255),
        (byte)Math.Clamp(Math.Round(colour.G * factor), 0, 255),
        (byte)Math.Clamp(Math.Round(colour.B * factor), 0, 255));
}
