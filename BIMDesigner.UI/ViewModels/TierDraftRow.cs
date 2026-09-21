using System.ComponentModel;
using System.Runtime.CompilerServices;
using BIMDesigner.Core;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Parameters;

namespace BIMDesigner.UI.ViewModels;

/// <summary>One tier of a stacked wall type being edited. Nothing reaches the type until Apply.</summary>
public sealed class TierDraftRow : INotifyPropertyChanged
{
    private readonly BimDocument _document;
    private Guid _wallTypeId;
    private double _height;
    private string _heightText;
    private bool _isVariable;

    public TierDraftRow(BimDocument document, StackTier tier)
    {
        _document = document;
        _wallTypeId = tier.WallTypeId;
        _isVariable = tier.IsVariable;
        _height = tier.IsVariable ? 1000 : tier.Height;
        _heightText = Units.FormatLength(_height);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised when anything about the tier changes.</summary>
    public event EventHandler? Edited;

    /// <summary>Only layered types can be tiers; a stack of stacks is not a wall.</summary>
    public IReadOnlyList<WallType> WallTypes => _document.TypesOf<WallType>().OrderBy(t => t.Name).ToList();

    public Guid WallTypeId
    {
        get => _wallTypeId;
        set
        {
            if (value == _wallTypeId) return;
            _wallTypeId = value;
            Changed();
        }
    }

    public string HeightText
    {
        get => _heightText;
        set
        {
            _heightText = value;
            if (ParameterFormatter.TryParse(ParameterDataType.Length, value, out var parsed) &&
                parsed is double millimetres && millimetres > 0)
            {
                _height = millimetres;
                _heightText = Units.FormatLength(millimetres);
            }
            else
            {
                _height = double.NaN;
            }

            Changed();
        }
    }

    public bool IsVariable
    {
        get => _isVariable;
        set
        {
            if (value == _isVariable) return;
            _isVariable = value;
            Changed();
            OnPropertyChanged(nameof(CanEditHeight));
        }
    }

    /// <summary>The variable tier takes whatever height is left, so it has none of its own to type.</summary>
    public bool CanEditHeight => !_isVariable;

    public WallType? WallType => _document.FindType<WallType>(_wallTypeId);

    /// <summary>The tier as the model wants it, or null while its height is not a length.</summary>
    public StackTier? ToTier() =>
        _isVariable ? new StackTier(_wallTypeId, 0)
        : double.IsNaN(_height) ? null
        : new StackTier(_wallTypeId, _height);

    private void Changed([CallerMemberName] string? name = null)
    {
        OnPropertyChanged(name);
        Edited?.Invoke(this, EventArgs.Empty);
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
