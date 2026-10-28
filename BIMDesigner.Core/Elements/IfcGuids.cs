using System.Numerics;

namespace BIMDesigner.Core.Elements;

/// <summary>
/// An element's GUID as IFC writes it: the 128-bit number its 32 hex digits make, in 22 digits of
/// IFC's own base 64 (0-9, A-Z, a-z, _ and $) - its GlobalId in an IFC file, and how a BCF viewpoint
/// names the elements it is about.
/// </summary>
public static class IfcGuids
{
    private const string Digits = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz_$";

    public static string Compress(Guid id)
    {
        var value = new BigInteger(Ordered(id.ToByteArray()), isUnsigned: true, isBigEndian: true);
        var digits = new char[22];
        for (var i = 21; i >= 0; i--)
        {
            digits[i] = Digits[(int)(value % 64)];
            value /= 64;
        }

        return new string(digits);
    }

    /// <summary>The GUID an IFC GlobalId stands for, if it is one.</summary>
    public static bool TryExpand(string? text, out Guid id)
    {
        id = Guid.Empty;
        if (text is not { Length: 22 } || Digits.IndexOf(text[0]) is < 0 or > 3) return false;
        var value = BigInteger.Zero;
        foreach (var digit in text)
        {
            var at = Digits.IndexOf(digit);
            if (at < 0) return false;
            value = value * 64 + at;
        }

        var bytes = value.ToByteArray(isUnsigned: true, isBigEndian: true);
        var padded = new byte[16];
        bytes.CopyTo(padded, 16 - bytes.Length);
        id = new Guid(Ordered(padded));
        return true;
    }

    /// <summary>A GUID's bytes in the order it is written, its first three fields big end first - or back again.</summary>
    private static byte[] Ordered(byte[] b) => new[] { b[3], b[2], b[1], b[0], b[5], b[4], b[7], b[6], b[8], b[9], b[10], b[11], b[12], b[13], b[14], b[15] };
}
