using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;

namespace BIMDesigner.Core.Materials;

/// <summary>What the Materials window changes of a material: its name, its cut colour, and its face.</summary>
public readonly record struct MaterialLook(string Name, ColourRgb CutColour, SurfaceFinish Finish)
{
    public static MaterialLook Of(Material material) => new(material.Name, material.CutColour, material.Finish);

    public void ApplyTo(Material material) => (material.Name, material.CutColour, material.Finish) = (Name, CutColour, Finish);
}

/// <summary>A material's name, colours and face changed as one step to undo.</summary>
public sealed class SetMaterialCommand : IUndoableCommand
{
    private readonly Material _material;
    private readonly MaterialLook _before;
    private readonly MaterialLook _after;

    public SetMaterialCommand(Material material, MaterialLook after)
    {
        (_material, _before, _after) = (material, MaterialLook.Of(material), after);
    }

    public string Name => $"Edit Material {_before.Name}";

    public void Redo() => _after.ApplyTo(_material);

    public void Undo() => _before.ApplyTo(_material);
}

/// <summary>A material added to the project - a new one, or a copy of another to change - as one step to undo.</summary>
public sealed class AddMaterialCommand(BimDocument document, Material material) : IUndoableCommand
{
    public string Name => $"New Material {material.Name}";

    public void Redo() => document.AddMaterial(material);

    public void Undo() => document.RemoveMaterial(material);
}

public static class MaterialCopies
{
    /// <summary>A copy of a material under a name of its own: every property its own to change.</summary>
    public static Material Copy(Material original, string name) => new(name)
    {
        Density = original.Density,
        ThermalConductivity = original.ThermalConductivity,
        SurfaceColour = original.SurfaceColour,
        CutColour = original.CutColour,
        Manufacturer = original.Manufacturer,
        ClassificationCode = original.ClassificationCode,
        Keynote = original.Keynote,
        CostPerCubicMetre = original.CostPerCubicMetre,
        Finish = original.Finish
    };

    /// <summary>A name like the one given that no material of the project has yet: "Brick, Common 2".</summary>
    public static string FreeName(BimDocument document, string name)
    {
        bool Taken(string candidate) => document.Materials.Any(material => string.Equals(material.Name, candidate, StringComparison.OrdinalIgnoreCase));
        if (!Taken(name)) return name;
        for (var i = 2; ; i++)
            if (!Taken($"{name} {i}")) return $"{name} {i}";
    }
}
