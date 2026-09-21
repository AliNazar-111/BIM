namespace BIMDesigner.Core.Materials;

/// <summary>
/// What a layer does in an assembly (specification section 3.1). The numeric value is the
/// join priority: when two walls meet, layers of equal function connect through, and lower
/// numbers win over higher ones. Structure is priority 1 and therefore joins first.
/// </summary>
public enum LayerFunction
{
    Structure = 1,
    Substrate = 2,
    ThermalOrAir = 3,
    Membrane = 4,
    FinishExterior = 5,
    FinishInterior = 6
}

/// <summary>One layer of a compound assembly.</summary>
public sealed class MaterialLayer
{
    public MaterialLayer(LayerFunction function, Guid materialId, double thickness, bool wraps = true)
    {
        if (thickness <= 0)
            throw new ArgumentOutOfRangeException(nameof(thickness), "A layer must have a positive thickness.");

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
