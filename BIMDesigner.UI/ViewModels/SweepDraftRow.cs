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
    private WallSide _side;
    private Guid _materialId;
    private bool _fromTop;
    private (double Value, string Text) _depth;
    private (double Value, string Text) _height;
    private (double Value, string Text) _elevation;

    public SweepDraftRow(BimDocument document, WallSweep sweep)
    {
        _document = document;
        _kind = sweep.Kind;
        _profile = sweep.Profile;
        _side = sweep.Side;
        _materialId = sweep.MaterialId;
        _fromTop = sweep.FromTop;
        _depth = (sweep.Depth, Units.FormatLength(sweep.Depth));
        _height = (sweep.Height, Units.FormatLength(sweep.Height));
        _elevation = (sweep.Elevation, Units.FormatLength(sweep.Elevation));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised when anything about the row changes.</summary>
    public event EventHandler? Edited;

    public static IReadOnlyList<string> KindChoices { get; } = EnumText.Choices<SweepKind>();
    public static IReadOnlyList<string> ProfileChoices { get; } = EnumText.Choices<SweepProfile>();
    public static IReadOnlyList<string> SideChoices { get; } = EnumText.Choices<WallSide>();
    public static IReadOnlyList<string> FromChoices { get; } = new[] { "Base", "Top" };

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
        get => EnumText.Humanise(_profile);
        set { if (EnumText.TryParse<SweepProfile>(value, out var profile) && profile != _profile) { _profile = profile; Changed(); } }
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

    /// <summary>The sweep as the model wants it, or null while a size is not a length.</summary>
    public WallSweep? ToSweep() =>
        double.IsNaN(_depth.Value) || double.IsNaN(_height.Value) || double.IsNaN(_elevation.Value)
            ? null
            : new WallSweep(_kind, _profile, _side, _depth.Value, _height.Value, _elevation.Value, _fromTop, _materialId);

    private static (double, string) Parse(string text, bool positive) =>
        ParameterFormatter.TryParse(ParameterDataType.Length, text, out var parsed) &&
        parsed is double millimetres && (positive ? millimetres > 0 : millimetres >= 0)
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
