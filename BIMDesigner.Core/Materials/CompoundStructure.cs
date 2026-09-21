namespace BIMDesigner.Core.Materials;

/// <summary>
/// What a layer does in an assembly (specification section 3.1) - the six functions of the
/// compound structures in the tools this follows. The numeric value is the join priority:
/// where walls meet, layers join through in priority order, lowest first, so structure meets
/// structure before any finish is considered.
///
/// Finish 1 and Finish 2 are not "outside" and "inside". Finish 1 is the heavier finish that
/// joins before the other - brick, cladding, render - and Finish 2 the lighter one - plaster,
/// board, paint - and either may be on either face.
/// </summary>
public enum LayerFunction
{
    /// <summary>A vapour or air barrier. It has no thickness and no join priority.</summary>
    Membrane = 0,

    /// <summary>What carries the load: block, concrete, studs.</summary>
    Structure = 1,

    /// <summary>What a finish is fixed to: sheathing, a screed, plywood.</summary>
    Substrate = 2,

    /// <summary>Insulation and air cavities.</summary>
    ThermalAir = 3,

    /// <summary>The heavier finish, usually outside: brick, cladding, render.</summary>
    Finish1 = 4,

    /// <summary>The lighter finish, usually inside: plaster, plasterboard, paint.</summary>
    Finish2 = 5
}

/// <summary>How layer functions are named on screen and read back.</summary>
public static class LayerFunctions
{
    private static readonly (LayerFunction Function, string Label)[] Labels =
    {
        (LayerFunction.Structure, "Structure [1]"),
        (LayerFunction.Substrate, "Substrate [2]"),
        (LayerFunction.ThermalAir, "Thermal/Air Layer [3]"),
        (LayerFunction.Finish1, "Finish 1 [4]"),
        (LayerFunction.Finish2, "Finish 2 [5]"),
        (LayerFunction.Membrane, "Membrane Layer")
    };

    /// <summary>In the order they are offered, priority first and the membrane last.</summary>
    public static IReadOnlyList<string> Choices { get; } = Labels.Select(entry => entry.Label).ToArray();

    public static string Label(LayerFunction function) =>
        Labels.FirstOrDefault(entry => entry.Function == function).Label ?? function.ToString();

    /// <summary>
    /// Reads a function from its label, its name, or the names earlier versions saved, which
    /// said where a finish was rather than what it was.
    /// </summary>
    public static bool TryParse(string? text, out LayerFunction function)
    {
        function = LayerFunction.Structure;
        if (string.IsNullOrWhiteSpace(text)) return false;

        foreach (var (candidate, label) in Labels)
        {
            if (!string.Equals(label, text, StringComparison.OrdinalIgnoreCase)) continue;
            function = candidate;
            return true;
        }

        switch (text.Trim())
        {
            case "FinishExterior": function = LayerFunction.Finish1; return true;
            case "FinishInterior": function = LayerFunction.Finish2; return true;
            case "ThermalOrAir": function = LayerFunction.ThermalAir; return true;
        }

        return Enum.TryParse(text, ignoreCase: true, out function) && Enum.IsDefined(function);
    }
}

/// <summary>One layer of a compound assembly.</summary>
public sealed class MaterialLayer
{
    public MaterialLayer(LayerFunction function, Guid materialId, double thickness, bool wraps = true)
    {
        // A membrane is a line in the build-up, not a band; every other layer has substance.
        if (function == LayerFunction.Membrane ? thickness != 0 : !(thickness > 0))
            throw new ArgumentOutOfRangeException(nameof(thickness), function == LayerFunction.Membrane
                ? "A membrane has no thickness."
                : "A layer must have a positive thickness.");

        Function = function;
        MaterialId = materialId;
        Thickness = thickness;
        Wraps = wraps;
    }

    public LayerFunction Function { get; set; }

    public Guid MaterialId { get; set; }

    /// <summary>Millimetres.</summary>
    public double Thickness { get; set; }

    /// <summary>Whether the layer returns into openings and wall ends (section 3.1).</summary>
    public bool Wraps { get; set; }

    public MaterialLayer Clone() => new(Function, MaterialId, Thickness, Wraps);
}

/// <summary>
/// The layered assembly of a wall, floor or roof type (specification section 3.1).
///
/// Layers are ordered from the exterior face inward. The <em>core</em> is the structural
/// part of the assembly: it is what location lines such as "Core Face Exterior" refer to,
/// and what structural analysis and fire ratings care about. Everything outside the core is
/// finish and insulation.
/// </summary>
public sealed class CompoundStructure
{
    private readonly List<MaterialLayer> _layers = new();

    public CompoundStructure(params MaterialLayer[] layers)
    {
        _layers.AddRange(layers);
    }

    /// <summary>Ordered exterior face to interior face.</summary>
    public IReadOnlyList<MaterialLayer> Layers => _layers;

    public void Add(MaterialLayer layer) => _layers.Add(layer);

    public void Insert(int index, MaterialLayer layer) => _layers.Insert(index, layer);

    public bool Remove(MaterialLayer layer) => _layers.Remove(layer);

    /// <summary>Swaps in a whole new set of layers, as an edit to a type's build-up does.</summary>
    public void ReplaceLayers(IEnumerable<MaterialLayer> layers)
    {
        var replacement = layers.ToList();
        _layers.Clear();
        _layers.AddRange(replacement);
    }

    /// <summary>A copy with copies of every layer, so editing one never changes the other.</summary>
    public CompoundStructure Clone() => new(_layers.Select(layer => layer.Clone()).ToArray());

    /// <summary>Total wall thickness in millimetres - the type's "Width" parameter.</summary>
    public double TotalWidth => _layers.Sum(layer => layer.Thickness);

    /// <summary>
    /// Index of the first structural layer, or -1 when the assembly has no structural
    /// layer (a pure finish build-up), in which case the whole assembly acts as the core.
    /// </summary>
    public int CoreStartIndex => _layers.FindIndex(layer => layer.Function == LayerFunction.Structure);

    /// <summary>Index of the last structural layer, or -1. See <see cref="CoreStartIndex"/>.</summary>
    public int CoreEndIndex => _layers.FindLastIndex(layer => layer.Function == LayerFunction.Structure);

    /// <summary>Thickness of the layers outside the core, measured from the exterior face.</summary>
    public double ExteriorWidth
    {
        get
        {
            var start = CoreStartIndex;
            return start <= 0 ? 0 : _layers.Take(start).Sum(layer => layer.Thickness);
        }
    }

    /// <summary>Thickness of the structural core.</summary>
    public double CoreWidth
    {
        get
        {
            var start = CoreStartIndex;
            if (start < 0) return TotalWidth;

            return _layers
                .Skip(start)
                .Take(CoreEndIndex - start + 1)
                .Sum(layer => layer.Thickness);
        }
    }

    /// <summary>Thickness of the layers inside the core, measured to the interior face.</summary>
    public double InteriorWidth => TotalWidth - ExteriorWidth - CoreWidth;

    /// <summary>
    /// Distance of each layer from the exterior face, as (start, end) pairs in millimetres.
    /// This is what the plan view walks to draw the layers of a wall.
    /// </summary>
    public IEnumerable<(MaterialLayer Layer, double Start, double End)> GetLayerOffsets()
    {
        var offset = 0.0;
        foreach (var layer in _layers)
        {
            yield return (layer, offset, offset + layer.Thickness);
            offset += layer.Thickness;
        }
    }

    /// <summary>A single-layer assembly, the simplest possible wall type.</summary>
    public static CompoundStructure Single(Guid materialId, double thickness) =>
        new(new MaterialLayer(LayerFunction.Structure, materialId, thickness));
}
