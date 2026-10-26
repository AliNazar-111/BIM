using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BIMDesigner.Core.Documents;

namespace BIMDesigner.UI.StartUp;

/// <summary>
/// What the home screen, or the New Project steps, hand to the project window: the project to
/// work on - read, made or brought back - where it is kept, and what to say about it.
/// </summary>
public sealed record StartChoice(BimDocument Document, string? Path, string Said)
{
    /// <summary>The example house: walls drawn into it once it is open.</summary>
    public bool Example { get; init; }

    /// <summary>Work brought back after BIMDesigner stopped: changed, and in no file yet.</summary>
    public bool Unsaved { get; init; }

    /// <summary>A new project to be saved here as soon as it is open.</summary>
    public string? SaveTo { get; init; }

    /// <summary>What did not come in as itself from an IFC file, to be said once the project is open.</summary>
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}

/// <summary>The logo, at the size wanted, from the program's own icon.</summary>
internal static class Logo
{
    public static ImageSource? Image(int size)
    {
        try
        {
            var decoder = new IconBitmapDecoder(new Uri("pack://application:,,,/BIMDesigner;component/Assets/BIMDesigner.ico"), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            return decoder.Frames.OrderBy(frame => Math.Abs(frame.PixelWidth - size)).ThenByDescending(frame => frame.PixelWidth).FirstOrDefault();
        }
        catch (Exception exception) when (exception is IOException or NotSupportedException or UriFormatException or FileFormatException)
        {
            return null;
        }
    }
}
