using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using BIMDesigner.Core;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Materials;
using BIMDesigner.Core.Parameters;

namespace BIMDesigner.UI.ViewModels;

/// <summary>
/// One band of a wall type being edited: a layer made of another material between two heights.
/// It holds on to the layer's row, not its number, so it stays with the layer as layers are
/// moved. Nothing reaches the type until Apply.
/// </summary>
public sealed class BandDraftRow : INotifyPropertyChanged
{
    private readonly BimDocument _document;
    private LayerDraftRow? _layer;
    private Guid _materialId;
    private (double Value, string Text) _bottom;
    private (double? Value, string Text) _top;

    public BandDraftRow(BimDocument document, ObservableCollection<LayerDraftRow> layers, WallBand band)
    {
        _document = document;
        Layers = layers;
        _layer = band.Layer >= 0 && band.Layer < layers.Count ? layers[band.Layer] : null;
        _materialId = band.MaterialId;
        _bottom = (band.Bottom, Units.FormatLength(band.Bottom));
        _top = (band.Top, band.Top is { } top ? Units.FormatLength(top) : string.Empty);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised when anything about the band changes.</summary>
    public event EventHandler? Edited;

    /// <summary>The layers of the type being edited, to choose the band's from.</summary>
    public ObservableCollection<LayerDraftRow> Layers { get; }

    public IReadOnlyList<Material> Materials => _document.Materials.OrderBy(m => m.Name).ToList();

    public LayerDraftRow? Layer
    {
        get => _layer;
        set { if (!ReferenceEquals(value, _layer)) { _layer = value; Changed(); } }
    }

    public Guid MaterialId
    {
        get => _materialId;
        set { if (value != _materialId) { _materialId = value; Changed(); } }
    }

    /// <summary>How far up from the bottom of the wall it starts.</summary>
    public string BottomText
    {
        get => _bottom.Text;
        set
        {
            _bottom = ParameterFormatter.TryParse(ParameterDataType.Length, value, out var parsed) && parsed is double mm && mm >= 0
                ? (mm, Units.FormatLength(mm))
                : (double.NaN, value);
            Changed();
        }
    }

    /// <summary>How far up it stops; left empty, it runs to the top of the wall.</summary>
    public string TopText
    {
        get => _top.Text;
        set
        {
            if (string.IsNullOrWhiteSpace(value) || value.Trim().Equals("top", StringComparison.OrdinalIgnoreCase)) _top = (null, string.Empty);
            else
                _top = ParameterFormatter.TryParse(ParameterDataType.Length, value, out var parsed) && parsed is double mm && mm > 0
                    ? (mm, Units.FormatLength(mm))
                    : (double.NaN, value);
            Changed();
        }
    }

    /// <summary>Whether the band refers to a layer, by its row.</summary>
    public bool IsOn(LayerDraftRow row) => ReferenceEquals(_layer, row);

    /// <summary>The band as the type wants it, or null while it has no layer or a height is not a length.</summary>
    public WallBand? ToBand()
    {
        if (_layer is null || Layers.IndexOf(_layer) is var index && index < 0) return null;
        if (double.IsNaN(_bottom.Value) || _top.Value is double top && double.IsNaN(top)) return null;
        return new WallBand(Layers.IndexOf(_layer), _bottom.Value, _top.Value, _materialId);
    }

    private void Changed([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        Edited?.Invoke(this, EventArgs.Empty);
    }
}
