using System.Windows.Media;
using BIMDesigner.Core;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Materials;
using BIMDesigner.Core.Parameters;

namespace BIMDesigner.UI.ViewModels;

/// <summary>
/// One layer of a wall type's assembly, shown in the property panel in the same order and
/// the same colours the plan view draws it.
/// </summary>
public sealed class LayerRow
{
    public LayerRow(MaterialLayer layer, BimDocument document)
    {
        var material = document.FindMaterial(layer.MaterialId);

        MaterialName = material?.Name ?? "<missing material>";
        FunctionName = EnumText.Humanise(layer.Function);
        Thickness = Units.FormatLength(layer.Thickness);

        var colour = material?.CutColour ?? new ColourRgb(0x6A, 0x6A, 0x6A);
        var brush = new SolidColorBrush(Color.FromRgb(colour.R, colour.G, colour.B));
        brush.Freeze();
        Swatch = brush;
    }

    public string MaterialName { get; }

    public string FunctionName { get; }

    public string Thickness { get; }

    public Brush Swatch { get; }
}
