using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Energy;
using BIMDesigner.Core.Parameters;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// Whether a window or roof window is a smoke vent, and how it is opened (Approved Document B Volume 1, 3.28e, 3.50 to 3.53, Diagram 3.9):
/// not one; opened by hand where it is; opened from a switch elsewhere - the fire and rescue service's at their access level, or a control
/// system's; or opened on detecting smoke - an automatic opening vent, which a switch can open too; or never shut - permanent ventilation, a
/// louvre or an open grille (Approved Document B Volume 2 3.35, 5.44).
/// </summary>
public enum SmokeVentKind
{
    None,
    Manual,
    Remote,
    Automatic,
    Permanent
}

/// <summary>
/// Smoke vents: windows and roof windows whose types say they are one, and their free area as Appendix D5 measures it - the declared
/// aerodynamic free area where the type gives one (BS EN 12101-2), else the geometric: each light that opens, opened as far as it is taken
/// to open, the least area the air passes through at right angles to it (Diagram D7).
/// </summary>
public static class SmokeVents
{
    /// <summary>A smoke vent: the window or roof window, how it is named, how it is opened, its free area, m², and its bottom and top, mm above the project's base.</summary>
    public sealed record Vent(Element Element, string Name, SmokeVentKind Kind, double FreeArea, double Bottom, double Top);

    /// <summary>Whether a vent is opened from elsewhere - from a switch, or on detecting smoke.</summary>
    public static bool Remotely(SmokeVentKind kind) => kind is SmokeVentKind.Remote or SmokeVentKind.Automatic;

    /// <summary>
    /// A hinged light's geometric free area, m² (Appendix D5 b, Diagram D7): the light's own opening - its hinge's length by its width
    /// across, m - or, where less, the gap round it opened so many degrees: along its free edge the chord it has swung through, and the
    /// triangle at each end between the frame and the light. A light opened square to its frame gives its whole opening.
    /// </summary>
    public static double Geometric(double hinge, double across, double angle)
    {
        if (hinge <= 0 || across <= 0 || angle <= 0) return 0;
        var turned = Math.Min(angle, 90) * Math.PI / 180;
        var gap = hinge * 2 * across * Math.Sin(turned / 2) + across * across * Math.Sin(turned);
        return Math.Min(hinge * across, gap);
    }

    /// <summary>A window as a smoke vent - null where its type is not one.</summary>
    public static Vent? Of(BimDocument document, Window window)
    {
        if (document.FindType<WindowType>(window.TypeId) is not { SmokeVent: not SmokeVentKind.None } type) return null;
        var floor = document.FindLevel(window.LevelId)?.Elevation ?? 0;
        var (sill, height) = window.Placed(document, type, document.Find<Wall>(window.HostWallId));
        var area = type.SmokeVentArea > 0 ? type.SmokeVentArea : Overheating.Lights(document, window).Sum(light => Geometric(light.Hinge, light.Across, light.Angle));
        return new Vent(window, $"the {type.Name} window", type.SmokeVent, area, floor + sill, floor + sill + height);
    }

    /// <summary>A roof window as a smoke vent - null where its type is not one.</summary>
    public static Vent? Of(BimDocument document, RoofWindow window)
    {
        if (document.FindType<RoofWindowType>(window.TypeId) is not { SmokeVent: not SmokeVentKind.None } type || RoofWindows.Frame(document, window) is not { } frame) return null;
        var area = type.SmokeVentArea > 0 ? type.SmokeVentArea : Overheating.Lights(document, window).Sum(light => Geometric(light.Hinge, light.Across, light.Angle));
        return new Vent(window, $"the {type.Name} roof window", type.SmokeVent, area, frame.Corners.Min(frame.Top), frame.Corners.Max(frame.Top));
    }

    /// <summary>The Edit Type parameters of a window or roof window type as a smoke vent: how it opens, and its declared free area once it is one.</summary>
    internal static IEnumerable<ParameterValue> Parameters(Func<SmokeVentKind> kind, Action<SmokeVentKind> setKind, Func<double> area, Action<double> setArea)
    {
        yield return ParameterValue.BindChoice(SmokeVentParameters.Kind, () => Said(kind()),
            v => { if (Choices.FirstOrDefault(choice => choice.Said == v) is { Said: not null } chosen) setKind(chosen.Kind); }, Choices.Select(choice => choice.Said).ToArray());
        if (kind() != SmokeVentKind.None)
            yield return ParameterValue.BindValidated(SmokeVentParameters.FreeArea, area, (double v) =>
            {
                if (!double.IsFinite(v) || v is < 0 or >= 100) return false;
                setArea(v);
                return true;
            });
    }

    private static readonly (SmokeVentKind Kind, string Said)[] Choices =
    {
        (SmokeVentKind.None, "No"),
        (SmokeVentKind.Manual, "Opened by hand"),
        (SmokeVentKind.Remote, "Opened from a switch"),
        (SmokeVentKind.Automatic, "Automatic, on smoke (AOV)"),
        (SmokeVentKind.Permanent, "Permanent ventilation, always open")
    };

    /// <summary>How a kind of vent is said in Edit Type.</summary>
    public static string Said(SmokeVentKind kind) => Choices.First(choice => choice.Kind == kind).Said;
}

public static class SmokeVentParameters
{
    public static readonly ParameterDefinition Kind = new("Smoke Vent", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.Construction);

    public static readonly ParameterDefinition FreeArea = new("Smoke Vent Free Area (m², 0 = from its lights)", ParameterDataType.Number, ParameterBinding.Type, ParameterGroup.Construction);
}
