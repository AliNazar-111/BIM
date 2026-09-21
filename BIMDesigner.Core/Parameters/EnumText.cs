using System.Text;

namespace BIMDesigner.Core.Parameters;

/// <summary>
/// Converts enum members to and from the text shown in the property panel, so
/// <c>WallLocationLine.CoreFaceExterior</c> reads as "Core Face Exterior".
///
/// Round-tripping is exact because enum members are PascalCase with no spaces: removing the
/// spaces always recovers the member name.
/// </summary>
public static class EnumText
{
    /// <summary>"CoreFaceExterior" -> "Core Face Exterior".</summary>
    public static string Humanise(Enum value) => Humanise(value.ToString());

    public static string Humanise(string name)
    {
        var builder = new StringBuilder(name.Length + 8);

        for (var i = 0; i < name.Length; i++)
        {
            // A capital that follows a lowercase letter starts a new word.
            if (i > 0 && char.IsUpper(name[i]) && !char.IsUpper(name[i - 1]))
                builder.Append(' ');

            builder.Append(name[i]);
        }

        return builder.ToString();
    }

    /// <summary>Parses text produced by <see cref="Humanise(Enum)"/> back to its member.</summary>
    public static bool TryParse<T>(string? text, out T value) where T : struct, Enum
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        return Enum.TryParse(text.Replace(" ", string.Empty), ignoreCase: true, out value);
    }

    /// <summary>Every member of an enum, as display text - the allowed values for a parameter.</summary>
    public static IReadOnlyList<string> Choices<T>() where T : struct, Enum =>
        Enum.GetNames<T>().Select(Humanise).ToArray();
}
