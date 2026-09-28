using System.Windows;
using System.Windows.Controls;
using BIMDesigner.Core;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Parameters;

namespace BIMDesigner.UI;

/// <summary>
/// Draws the profile of a roof by extrusion (Revit's Edit Profile): square-on, over the span
/// clicked in plan, starting from a gable, gambrel, vault, shed or butterfly and moved from
/// there point by point.
/// </summary>
public partial class ExtrusionProfileWindow : System.Windows.Window
{
    private readonly double _spanStart;
    private readonly double _span;
    private readonly double _levelElevation;
    private readonly IReadOnlyList<(string Name, double Elevation)> _levels;

    /// <param name="spanStart">Where the building under the roof starts, along the profile's line.</param>
    /// <param name="span">How wide the building under the roof is.</param>
    /// <param name="profile">The profile to start from; a gable over the span when there is none.</param>
    /// <param name="baseOffset">How far above its level the roof's base is.</param>
    /// <param name="levelElevation">The elevation of the roof's level.</param>
    /// <param name="levels">The project's levels, to set the profile out against.</param>
    public ExtrusionProfileWindow(
        double spanStart, double span, RoofProfile? profile, double baseOffset,
        double levelElevation, IReadOnlyList<(string Name, double Elevation)> levels)
    {
        InitializeComponent();

        _spanStart = spanStart;
        _span = span;
        _levelElevation = levelElevation;
        _levels = levels;

        Editor.SpanStart = spanStart;
        Editor.Span = span;

        // An existing profile gives its own numbers back: the rise above its eaves (or the base,
        // where the eaves hang below it) and how far it runs out past the building.
        var drawn = profile is { Points.Count: >= 2 } ? new RoofExtrusion(new Point2D(0, 0), Vector2D.UnitX, profile, 0, 1).Points : null;
        var rise = drawn is not null
            ? drawn.Max(point => point.Y) - Math.Max(0, drawn.Min(point => point.Y))
            : Math.Round(span / 2 * Math.Tan(30 * Math.PI / 180) / 10) * 10;
        var overhang = drawn is not null ? Math.Max(0, spanStart - drawn[0].X) : 0;

        RiseBox.Text = Units.FormatLength(Math.Round(rise / 10) * 10);
        OverhangBox.Text = Units.FormatLength(Math.Round(overhang / 10) * 10);
        BaseBox.Text = Units.FormatLength(baseOffset);
        BaseBox.TextChanged += (_, _) => ShowLevels();
        ShowLevels();

        Editor.Profile = profile is { Points.Count: >= 2 } ? profile : Preset(RoofForm.Gable);
        Editor.ProfileChanged += (_, _) => Report();

        Loaded += (_, _) =>
        {
            Editor.Fit();
            Report();
        };
    }

    /// <summary>The profile drawn, when the window was finished.</summary>
    public RoofProfile Profile => Editor.Profile;

    /// <summary>The base the roof sits at, above its level.</summary>
    public double BaseOffset { get; private set; }

    private double? Base() =>
        ParameterFormatter.TryParse(ParameterDataType.Length, BaseBox.Text, out var value) && value is double offset
            ? offset
            : null;

    private double Rise() =>
        ParameterFormatter.TryParse(ParameterDataType.Length, RiseBox.Text, out var value) && value is double rise && rise >= 0
            ? rise
            : _span / 2 * Math.Tan(30 * Math.PI / 180);

    private double Overhang() =>
        ParameterFormatter.TryParse(ParameterDataType.Length, OverhangBox.Text, out var value) && value is double overhang && overhang >= 0
            ? overhang
            : 0;

    /// <summary>A starting shape over the span, where the span is.</summary>
    private RoofProfile Preset(RoofForm form) =>
        RoofExtrusion.Preset(form, _span, Rise(), Overhang()).Shifted(_spanStart);

    /// <summary>The levels as heights above the base - which moves them when the base does.</summary>
    private void ShowLevels()
    {
        if (Base() is not { } offset) return;

        Editor.Levels = _levels
            .Select(level => (level.Name, level.Elevation - _levelElevation - offset))
            .ToList();
    }

    private void OnPreset(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string name } || !Enum.TryParse<RoofForm>(name, out var form)) return;

        Editor.Profile = Preset(form);
        Editor.Fit();
        Report();
    }

    /// <summary>Says what the profile makes, or what is wrong with it - and only lets it finish when it makes a roof.</summary>
    private void Report()
    {
        var extrusion = new RoofExtrusion(new Point2D(0, 0), Vector2D.UnitX, Editor.Profile, 0, 1000);

        if (extrusion.Problem() is { } problem)
        {
            StatusText.Text = problem;
            OkButton.IsEnabled = false;
            return;
        }

        var profile = extrusion.Profile;
        var drawn = extrusion.Points;
        var straight = Enumerable.Range(0, profile.Count - 1).Where(i => extrusion.Sagittas[i] == 0).ToList();
        var arcs = Enumerable.Range(0, profile.Count - 1)
            .Select(i => RoofExtrusion.Circle(profile[i], profile[i + 1], extrusion.Sagittas[i]))
            .OfType<(Point2D Centre, double Radius)>()
            .Select(circle => Math.Round(circle.Radius / 10) * 10)
            .Distinct()
            .ToList();

        var pitches = straight
            .Select(i => Math.Round(Math.Abs(Math.Atan2(profile[i + 1].Y - profile[i].Y, profile[i + 1].X - profile[i].X) * 180 / Math.PI), 1))
            .Distinct()
            .ToList();

        var parts = new List<string>();
        if (pitches.Count > 0)
            parts.Add($"{(pitches.Count == 1 ? "pitch" : "pitches")} {string.Join(", ", pitches.Take(6).Select(pitch => $"{pitch:0.#}°"))}" +
                      (pitches.Count > 6 ? " and more" : ""));
        if (arcs.Count > 0)
            parts.Add($"{(arcs.Count == 1 ? "an arc of radius" : "arcs of radius")} {string.Join(", ", arcs.Take(4).Select(Units.FormatLength))}");

        StatusText.Text =
            $"{EnumText.Humanise(extrusion.Form())}: {Units.FormatLength(drawn[^1].X - drawn[0].X)} wide, " +
            $"rising {Units.FormatLength(drawn.Max(point => point.Y) - Math.Max(0, drawn.Min(point => point.Y)))}; " +
            $"{string.Join("; ", parts)}.";
        OkButton.IsEnabled = true;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (Base() is not { } offset)
        {
            StatusText.Text = "The base needs a height above the level, such as 3000 mm.";
            return;
        }

        BaseOffset = offset;
        DialogResult = true;
    }
}
