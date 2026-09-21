using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using BIMDesigner.Core;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Materials;
using BIMDesigner.Core.Parameters;

namespace BIMDesigner.UI.ViewModels;

/// <summary>
/// One layer of a wall type being edited. Nothing here touches the type itself: the editor
/// collects these, checks them, and applies them all at once as a single undoable edit.
/// </summary>
public sealed class LayerDraftRow : INotifyPropertyChanged
{
    private readonly BimDocument _document;
    private LayerFunction _function;
    private Guid _materialId;
    private double _thickness;
    private string _thicknessText;
    private bool _wraps;
    private bool _isCore;

    public LayerDraftRow(BimDocument document, MaterialLayer layer)
    {
        _document = document;
        _function = layer.Function;
        _materialId = layer.MaterialId;
        _thickness = layer.Thickness;
        _thicknessText = Units.FormatLength(layer.Thickness);
        _wraps = layer.Wraps;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised when anything about the layer changes, so the preview can follow.</summary>
    public event EventHandler? Edited;

    public static IReadOnlyList<string> FunctionChoices => LayerFunctions.Choices;

    public IReadOnlyList<Material> Materials => _document.Materials.OrderBy(m => m.Name).ToList();

    public string FunctionName
    {
        get => LayerFunctions.Label(_function);
        set
        {
            if (!LayerFunctions.TryParse(value, out var function) || function == _function) return;

            var wasMembrane = _function == LayerFunction.Membrane;
            _function = function;

            // A membrane has no thickness, and a layer that stops being one needs some back.
            if (function == LayerFunction.Membrane) SetThickness(0);
            else if (wasMembrane) SetThickness(10);

            Changed();
            OnPropertyChanged(nameof(CanEditThickness));
        }
    }

    /// <summary>A membrane's thickness is always nothing, so it is not offered for editing.</summary>
    public bool CanEditThickness => _function != LayerFunction.Membrane;

    private void SetThickness(double millimetres)
    {
        _thickness = millimetres;
        _thicknessText = Units.FormatLength(millimetres);
        OnPropertyChanged(nameof(ThicknessText));
    }

    public LayerFunction Function => _function;

    public Guid MaterialId
    {
        get => _materialId;
        set
        {
            if (value == _materialId) return;
            _materialId = value;
            Changed();
            OnPropertyChanged(nameof(Swatch));
        }
    }

    /// <summary>What the user typed. Anything that is not a positive length is kept but flagged.</summary>
    public string ThicknessText
    {
        get => _thicknessText;
        set
        {
            if (_function == LayerFunction.Membrane)
            {
                SetThickness(0);
                return;
            }

            _thicknessText = value;

            if (ParameterFormatter.TryParse(ParameterDataType.Length, value, out var parsed) &&
                parsed is double millimetres && millimetres > 0)
            {
                _thickness = millimetres;
                _thicknessText = Units.FormatLength(millimetres);
            }
            else
            {
                _thickness = double.NaN;
            }

            Changed();
        }
    }

    public double Thickness => _thickness;

    public bool Wraps
    {
        get => _wraps;
        set
        {
            if (value == _wraps) return;
            _wraps = value;
            Changed();
        }
    }

    /// <summary>Whether the layer is inside the core, where wrapping does not apply.</summary>
    public bool IsCore
    {
        get => _isCore;
        set
        {
            if (value == _isCore) return;
            _isCore = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanWrap));
        }
    }

    public bool CanWrap => !_isCore;

    public Brush Swatch
    {
        get
        {
            var colour = _document.FindMaterial(_materialId)?.CutColour ?? new ColourRgb(0x6A, 0x6A, 0x6A);
            var brush = new SolidColorBrush(Color.FromRgb(colour.R, colour.G, colour.B));
            brush.Freeze();
            return brush;
        }
    }

    /// <summary>The layer as the model wants it, or null while the thickness is not valid.</summary>
    public MaterialLayer? ToLayer() =>
        double.IsNaN(_thickness) ? null : new MaterialLayer(_function, _materialId, _thickness, _wraps);

    private void Changed([CallerMemberName] string? name = null)
    {
        OnPropertyChanged(name);
        Edited?.Invoke(this, EventArgs.Empty);
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
