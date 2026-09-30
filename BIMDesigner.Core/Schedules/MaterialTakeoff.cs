using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Materials;

namespace BIMDesigner.Core.Schedules;

/// <summary>How much of one material one element contains.</summary>
public sealed class TakeoffLine
{
    public required string Category { get; init; }

    public required string TypeName { get; init; }

    public required string Material { get; init; }

    /// <summary>Layer thickness in millimetres.</summary>
    public required double Thickness { get; init; }

    /// <summary>Face area of the layer in mm² - what a finishes quantity is measured in.</summary>
    public required double Area { get; init; }

    /// <summary>Volume of the layer in mm³ - what a concrete or masonry order is based on.</summary>
    public required double Volume { get; init; }

    public required decimal Cost { get; init; }
}

/// <summary>One material's total across the whole project.</summary>
public sealed class TakeoffTotal
{
    public required string Material { get; init; }

    public required double Area { get; init; }

    public required double Volume { get; init; }

    public required decimal Cost { get; init; }

    public required int Elements { get; init; }
}

/// <summary>
/// Quantities by material rather than by element (specification section 6.4, "material
/// takeoffs: area/volume per material").
///
/// An element schedule answers "how many walls?"; a takeoff answers "how much blockwork?" -
/// which is the question that gets priced and ordered. It works by walking each layered
/// build-up, so it reports the blockwork inside a cavity wall rather than the wall's gross
/// volume, and it reaches slabs and walls alike because both share one layer system.
/// </summary>
public static class MaterialTakeoff
{
    public static IReadOnlyList<TakeoffLine> Lines(BimDocument document)
    {
        var lines = new List<TakeoffLine>();

        foreach (var wall in document.Walls)
        {
            var type = document.GetWallType(wall);
            if (type is null) continue;

            // Face area, so each layer is counted over the wall's elevation.
            var area = wall.GetArea(document);
            AddLayers(document, lines, "Walls", type.Name, type.Structure, area);
        }

        foreach (var slab in document.Elements.OfType<Slab>())
        {
            var type = document.FindType<SlabType>(slab.TypeId);
            if (type is null) continue;

            // A pitched roof is bought by its sloping surface, not by its shadow on the ground:
            // a 35° roof is a fifth more tiles than its footprint says.
            var area = slab is Roof roof ? roof.SlopingArea(document) : Shafts.NetArea(document, slab);

            AddLayers(document, lines, slab.Category.ToString(), type.Name, type.Structure, area);
        }

        return lines;
    }

    private static void AddLayers(
        BimDocument document, List<TakeoffLine> lines,
        string category, string typeName, CompoundStructure structure, double area)
    {
        foreach (var layer in structure.Layers)
        {
            var material = document.FindMaterial(layer.MaterialId);
            var volume = area * layer.Thickness;

            lines.Add(new TakeoffLine
            {
                Category = category,
                TypeName = typeName,
                Material = material?.Name ?? "<missing material>",
                Thickness = layer.Thickness,
                Area = area,
                Volume = volume,
                // Material cost is per cubic metre, and volumes here are cubic millimetres.
                Cost = (decimal)Units.CubicMmToCubicMetres(volume) * (material?.CostPerCubicMetre ?? 0)
            });
        }
    }

    /// <summary>The same quantities rolled up per material, which is what gets ordered.</summary>
    public static IReadOnlyList<TakeoffTotal> Totals(BimDocument document) =>
        Lines(document)
            .GroupBy(line => line.Material, StringComparer.OrdinalIgnoreCase)
            .Select(group => new TakeoffTotal
            {
                Material = group.Key,
                Area = group.Sum(line => line.Area),
                Volume = group.Sum(line => line.Volume),
                Cost = group.Sum(line => line.Cost),
                Elements = group.Count()
            })
            .OrderByDescending(total => total.Volume)
            .ToList();
}
