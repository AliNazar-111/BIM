using System.ComponentModel;
using System.Runtime.CompilerServices;
using BIMDesigner.Core;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Materials;
using BIMDesigner.Core.Parameters;

namespace BIMDesigner.UI.ViewModels;

/// <summary>One sweep or reveal of a wall type being edited. Nothing reaches the type until Apply.</summary>
public sealed class SweepDraftRow : INotifyPropertyChanged
{
    private readonly BimDocument _document;
    private SweepKind _kind;
    private SweepProfile _profile;
    private Guid? _profileId;
    private WallSide _side;
    private Guid _materialId;
    private bool _fromTop;
    private bool _flip;
    private bool _cutsWall;
    private bool _cuttable;
    private bool _returns;
    private (double Value, string Text) _depth;
    private (double Value, string Text) _height;
    private (double Value, string Text) _elevation;
    private (double Value, string Text) _offset;
    private (double Value, string Text) _setback;

    public SweepDraftRow(BimDocument document, WallSweep sweep)
    {
        _document = document;
        _kind = sweep.Kind;
        _profile = sweep.Profile;
        _profileId = sweep.ProfileId;
        _side = sweep.Side;
        _materialId = sweep.MaterialId;
        _fromTop = sweep.FromTop;
        _flip = sweep.Flip;
        _cutsWall = sweep.CutsWall;
        _cuttable = sweep.Cuttable;
        _returns = sweep.Returns;
        _depth = (sweep.Depth, Units.FormatLength(sweep.Depth));
        _height = (sweep.Height, Units.FormatLength(sweep.Height));
        _elevation = (sweep.Elevation, Units.FormatLength(sweep.Elevation));
        _offset = (sweep.Offset, Units.FormatLength(sweep.Offset));
        _setback = (sweep.Setback, Units.FormatLength(sweep.Setback));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised when anything about the row changes.</summary>
    public event EventHandler? Edited;

    public static IReadOnlyList<string> KindChoices { get; } = EnumText.Choices<SweepKind>();
    public static IReadOnlyList<string> SideChoices { get; } = EnumText.Choices<WallSide>();
    public static IReadOnlyList<string> FromChoices { get; } = new[] { "Base", "Top" };

    /// <summary>The built-in shapes, then every profile drawn in the project.</summary>
    public IReadOnlyList<string> ProfileChoices =>
        EnumText.Choices<SweepProfile>()
            .Concat(_document.TypesOf<SweepProfileType>().OrderBy(p => p.Name).Select(p => p.Name))
            .ToList();

    public IReadOnlyList<Material> Materials => _document.Materials.OrderBy(m => m.Name).ToList();

    public string KindName
    {
        get => EnumText.Humanise(_kind);
        set { if (EnumText.TryParse<SweepKind>(value, out var kind) && kind != _kind) { _kind = kind; Changed(); OnPropertyChanged(nameof(HasProfile)); } }
    }

    /// <summary>Reveals are always rectangular, so only a sweep offers a profile.</summary>
    public bool HasProfile => _kind == SweepKind.Sweep;

    public string ProfileName
    {
        get => _profileId is { } id && _document.FindType<SweepProfileType>(id) is { } drawn ? drawn.Name : EnumText.Humanise(_profile);
        set
        {
            if (_document.TypesOf<SweepProfileType>().FirstOrDefault(p => p.Name == value) is { } drawn)
            {
                if (_profileId == drawn.Id) return;
                _profileId = drawn.Id;

                // A drawn profile comes at its own size.
                _depth = (drawn.Depth, Units.FormatLength(drawn.Depth));
                _height = (drawn.Height, Units.FormatLength(drawn.Height));
                OnPropertyChanged(nameof(DepthText));
                OnPropertyChanged(nameof(HeightText));
                Changed();
            }
            else if (EnumText.TryParse<SweepProfile>(value, out var profile) && (profile != _profile || _profileId is not null))
            {
                _profile = profile;
                _profileId = null;
                Changed();
            }
        }
    }

    public string SideName
    {
        get => EnumText.Humanise(_side);
        set { if (EnumText.TryParse<WallSide>(value, out var side) && side != _side) { _side = side; Changed(); } }
    }

    public string FromName
    {
        get => _fromTop ? "Top" : "Base";
        set { var top = value == "Top"; if (top != _fromTop) { _fromTop = top; Changed(); } }
    }

    public Guid MaterialId
    {
        get => _materialId;
        set { if (value != _materialId) { _materialId = value; Changed(); } }
    }

    public string DepthText { get => _depth.Text; set { _depth = Parse(value, positive: true); Changed(); } }

    public string HeightText { get => _height.Text; set { _height = Parse(value, positive: true); Changed(); } }

    public string ElevationText { get => _elevation.Text; set { _elevation = Parse(value, positive: false); Changed(); } }

    /// <summary>Off the face, or into the wall when negative.</summary>
    public string OffsetText { get => _offset.Text; set { _offset = Parse(value, positive: false, signed: true); Changed(); } }

    public string SetbackText { get => _setback.Text; set { _setback = Parse(value, positive: false); Changed(); } }

    public bool Flip { get => _flip; set { if (value != _flip) { _flip = value; Changed(); } } }

    public bool CutsWall { get => _cutsWall; set { if (value != _cutsWall) { _cutsWall = value; Changed(); } } }

    public bool Cuttable { get => _cuttable; set { if (value != _cuttable) { _cuttable = value; Changed(); } } }

    public bool Returns { get => _returns; set { if (value != _returns) { _returns = value; Changed(); } } }

    /// <summary>The sweep as the model wants it, or null while a size is not a length.</summary>
    public WallSweep? ToSweep() =>
        new[] { _depth.Value, _height.Value, _elevation.Value, _offset.Value, _setback.Value }.Any(double.IsNaN)
            ? null
            : new WallSweep(_kind, _profile, _side, _depth.Value, _height.Value, _elevation.Value, _fromTop, _materialId,
                _offset.Value, _flip, _setback.Value, _cutsWall, _cuttable, _kind == SweepKind.Sweep ? _profileId : null, _returns);

    private static (double, string) Parse(string text, bool positive, bool signed = false) =>
        ParameterFormatter.TryParse(ParameterDataType.Length, text, out var parsed) &&
        parsed is double millimetres && (signed || (positive ? millimetres > 0 : millimetres >= 0))
            ? (millimetres, Units.FormatLength(millimetres))
            : (double.NaN, text);

    private void Changed([CallerMemberName] string? name = null)
    {
        OnPropertyChanged(name);
        Edited?.Invoke(this, EventArgs.Empty);
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
