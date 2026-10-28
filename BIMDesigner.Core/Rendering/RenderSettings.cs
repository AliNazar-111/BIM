using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;

namespace BIMDesigner.Core.Rendering;

/// <summary>The sky a rendering is lit by.</summary>
public enum RenderSky
{
    /// <summary>The sun at the place, date and time set, under a blue sky.</summary>
    Clear,

    /// <summary>No sun: a grey sky three times as bright overhead as at the horizon (the CIE overcast sky).</summary>
    Overcast
}

/// <summary>How many paths each pixel is worked out from: more, smoother and slower.</summary>
public enum RenderQuality
{
    Draft,
    Medium,
    High,
    Best
}

/// <summary>
/// How the project's renderings are made: the place the sun is worked out for - its latitude and
/// longitude, its clocks - the date and time, the sky, the picture's size and quality, and how bright.
/// Kept with the project as one step to undo.
/// </summary>
public sealed class RenderSettings
{
    public DateOnly Date { get; set; } = new(2026, 6, 21);

    /// <summary>The time on the clocks there.</summary>
    public TimeOnly Time { get; set; } = new(10, 30);

    /// <summary>The site's latitude, degrees north, and longitude, degrees east; none set, the country's own.</summary>
    public double? Latitude { get; set; }

    public double? Longitude { get; set; }

    /// <summary>Its standard time's hours from Greenwich, and its summer time; none set, the country's own.</summary>
    public double? UtcOffset { get; set; }

    public SummerTime? Summer { get; set; }

    public RenderSky Sky { get; set; }

    public RenderQuality Quality { get; set; } = RenderQuality.Medium;

    /// <summary>The picture's size, pixels.</summary>
    public int Width { get; set; } = 1600;

    public int Height { get; set; } = 900;

    /// <summary>Brighter or darker than the rendering would make it, in stops.</summary>
    public double Exposure { get; set; }

    /// <summary>The paths a pixel is worked out from at a quality.</summary>
    public static int Samples(RenderQuality quality) => quality switch
    {
        RenderQuality.Draft => 4,
        RenderQuality.Medium => 24,
        RenderQuality.High => 96,
        _ => 384
    };

    /// <summary>Where the sun is worked out for: as set, or the country's - London's for England, the plan's latitude in the middle of the US.</summary>
    public (double Latitude, double Longitude, double UtcOffset, SummerTime Summer) Place(BimDocument document)
    {
        var us = document.ProjectInformation.ClearanceStandard.IsUs();
        return (Latitude ?? (us ? document.ProjectInformation.Latitude : 51.5),
            Longitude ?? (us ? -95 : -0.13),
            UtcOffset ?? (us ? -6 : 0),
            Summer ?? (us ? SummerTime.NorthAmerican : SummerTime.European));
    }

    /// <summary>The sun's altitude and azimuth at the date and time set, at the place.</summary>
    public (double Altitude, double Azimuth) Sun(BimDocument document)
    {
        var (latitude, longitude, offset, summer) = Place(document);
        return SunPosition.Of(latitude, longitude, SunPosition.ToUtc(Date, Time, offset, summer));
    }

    public RenderSettings Copy() => new()
    {
        Date = Date, Time = Time, Latitude = Latitude, Longitude = Longitude, UtcOffset = UtcOffset, Summer = Summer,
        Sky = Sky, Quality = Quality, Width = Width, Height = Height, Exposure = Exposure
    };

    public bool SameAs(RenderSettings other) =>
        Date == other.Date && Time == other.Time && Latitude == other.Latitude && Longitude == other.Longitude && UtcOffset == other.UtcOffset &&
        Summer == other.Summer && Sky == other.Sky && Quality == other.Quality && Width == other.Width && Height == other.Height && Exposure.Equals(other.Exposure);

    public void CopyFrom(RenderSettings other)
    {
        (Date, Time, Latitude, Longitude, UtcOffset, Summer) = (other.Date, other.Time, other.Latitude, other.Longitude, other.UtcOffset, other.Summer);
        (Sky, Quality, Width, Height, Exposure) = (other.Sky, other.Quality, other.Width, other.Height, other.Exposure);
    }
}

/// <summary>The rendering settings changed as one step to undo.</summary>
public sealed class SetRenderSettingsCommand : Documents.Commands.IUndoableCommand
{
    private readonly RenderSettings _settings;
    private readonly RenderSettings _before;
    private readonly RenderSettings _after;

    public SetRenderSettingsCommand(RenderSettings settings, RenderSettings after)
    {
        _settings = settings;
        _before = settings.Copy();
        _after = after.Copy();
    }

    public string Name => "Rendering Settings";

    public void Redo() => _settings.CopyFrom(_after);

    public void Undo() => _settings.CopyFrom(_before);
}
