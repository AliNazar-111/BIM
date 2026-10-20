namespace BIMDesigner.Core.Architecture;

/// <summary>How the fanlights over a window's transom open: fixed, or top-hung, opening out a little at the bottom for air.</summary>
public enum FanlightOperation
{
    Fixed,
    TopHung
}

/// <summary>
/// One light of a window: a pane in its own part of the frame, between the jambs and mullions
/// either side of it and the sill, transom or head above and below it - where it is inside the
/// window, measured from the window's start and its bottom, and how it opens.
/// </summary>
public sealed record WindowLight(double From, double To, double Bottom, double Top, WindowOperation Operation, bool Fanlight, bool HingeAtStart)
{
    public bool Opens => Operation != WindowOperation.Fixed;

    public double Width => To - From;

    public double Height => Top - Bottom;

    /// <summary>Its glass, mm².</summary>
    public double Area => Math.Max(0, Width) * Math.Max(0, Height);
}

/// <summary>
/// A window divided into lights: mullions standing up through the frame between lights side by
/// side, a transom across it with fanlights over - each light fixed or opening as the window
/// does, the fanlights fixed or top-hung. Each light is its own pane and its own way out: a
/// mullion stands in the way, so an escape window has to give its clear opening through one
/// light (Approved Document B 2.10, IRC R310.2), and a pane is sized for safety glass on its own
/// (IRC R308.4.3 counts the individual pane).
/// </summary>
public static class WindowLights
{
    /// <summary>The narrowest or shortest a light may be, mm.</summary>
    public const double LeastLight = 200;

    /// <summary>Whether a window is divided into lights at all: one light and no transom is as a window always was.</summary>
    public static bool Divided(WindowType type) => type.Operation != WindowOperation.Bay && (type.Lights > 1 || TransomAt(type) is not null);

    /// <summary>Where the transom's middle is, up from the window's bottom - none where there is no transom, or no room for the lights either side of it.</summary>
    public static double? TransomAt(WindowType type)
    {
        if (type.TransomHeight <= 0 || type.Operation == WindowOperation.Bay) return null;
        var half = type.MullionWidth / 2;
        return type.TransomHeight - half >= WindowRules.Frame + LeastLight - 1e-6 && type.TransomHeight + half <= type.Height - WindowRules.Frame - LeastLight + 1e-6
            ? type.TransomHeight
            : null;
    }

    /// <summary>The most lights a window this wide has room for, each the least a light may be.</summary>
    public static int MostLights(WindowType type) =>
        Math.Max(1, (int)Math.Floor((type.Width - 2 * WindowRules.Frame + type.MullionWidth) / (LeastLight + type.MullionWidth) + 1e-9));

    /// <summary>
    /// Which lights open and which are fixed, one letter each across - O opens, F is fixed - as
    /// the type says it, the rest the usual way: a pair with one opener, three with the outer two
    /// opening and a fixed light between; a fixed window none.
    /// </summary>
    public static string Pattern(WindowType type)
    {
        var count = Math.Max(1, type.Lights);
        if (type.Operation == WindowOperation.Fixed) return new string('F', count);
        var given = (type.LightPattern ?? string.Empty).ToUpperInvariant().Where(letter => letter is 'O' or 'F').ToArray();
        return new string(Enumerable.Range(0, count)
            .Select(i => i < given.Length ? given[i] : count == 1 || i == 0 || i == count - 1 && count > 2 ? 'O' : 'F')
            .ToArray());
    }

    /// <summary>The middle of each mullion, along the window from its start.</summary>
    public static IReadOnlyList<double> Mullions(WindowType type)
    {
        if (!Divided(type) || type.Lights <= 1) return Array.Empty<double>();
        var (each, bar) = (LightWidth(type), type.MullionWidth);
        return Enumerable.Range(1, type.Lights - 1).Select(i => WindowRules.Frame + i * each + (i - 0.5) * bar).ToList();
    }

    private static double LightWidth(WindowType type)
    {
        var count = Math.Max(1, type.Lights);
        return (type.Width - 2 * WindowRules.Frame - (count - 1) * type.MullionWidth) / count;
    }

    /// <summary>
    /// Its lights: across, between the mullions, and over the transom the fanlights - each where
    /// it is inside the frame and how it opens, a side-hung one hung on the jamb nearer it, so a
    /// pair swing apart. An undivided window is one light, the whole of it inside its frame.
    /// </summary>
    public static IReadOnlyList<WindowLight> Of(WindowType type)
    {
        var frame = WindowRules.Frame;
        var (bottom, top) = (frame, type.Height - frame);
        if (!Divided(type)) return new[] { new WindowLight(frame, type.Width - frame, bottom, top, type.Operation, false, true) };

        var count = Math.Max(1, type.Lights);
        var (each, bar) = (LightWidth(type), type.MullionWidth);
        var transom = TransomAt(type);
        var pattern = Pattern(type);
        var lights = new List<WindowLight>();
        for (var i = 0; i < count; i++)
        {
            var from = frame + i * (each + bar);
            var to = from + each;
            var operation = pattern[i] == 'O' ? type.Operation : WindowOperation.Fixed;
            lights.Add(new WindowLight(from, to, bottom, transom is { } under ? under - bar / 2 : top, operation, false, i < count / 2.0 - 1e-6));
            if (transom is { } over)
                lights.Add(new WindowLight(from, to, over + bar / 2, top, type.Fanlight == FanlightOperation.TopHung ? WindowOperation.Awning : WindowOperation.Fixed, true, true));
        }

        return lights;
    }

    /// <summary>Whether any light of it opens.</summary>
    public static bool Opens(WindowType type) => Of(type).Any(light => light.Opens);

    /// <summary>How far up from the window's bottom the lowest light that opens starts - its frame's inside; null where none opens.</summary>
    public static double? LowestOpening(WindowType type) =>
        Of(type).Where(light => light.Opens).Select(light => (double?)light.Bottom).DefaultIfEmpty(null).Min();
}
