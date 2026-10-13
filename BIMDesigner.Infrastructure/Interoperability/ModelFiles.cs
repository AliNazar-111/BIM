using System.Globalization;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Infrastructure.Interoperability;

/// <summary>
/// The 3D model files a family can be loaded from: Wavefront OBJ, which most modelling programs
/// and model libraries export, and STL, text or binary, which every 3D printing and CAD program
/// does. Read for their corners and triangles only - a family is finished in its own colour or
/// material - in the file's own units, with which way is up as the format usually has it.
/// </summary>
public static class ModelFiles
{
    /// <summary>The file types offered for loading, for an open-file dialog.</summary>
    public const string Filter = "3D models (*.obj;*.stl)|*.obj;*.stl|Wavefront OBJ (*.obj)|*.obj|STL (*.stl)|*.stl";

    /// <summary>
    /// A model file's corners and triangles, and whether it is drawn Y up - as OBJ files are - or
    /// Z up, as STL files usually are. Refused, with why, for a file it cannot read.
    /// </summary>
    public static (IReadOnlyList<Point3D> Corners, IReadOnlyList<int> Triangles, bool YUp) Read(string path)
    {
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".obj":
            {
                var corners = ReadObj(File.ReadLines(path), out var triangles);
                return (corners, triangles, true);
            }

            case ".stl":
            {
                var (corners, triangles) = ReadStl(File.ReadAllBytes(path));
                return (corners, triangles, false);
            }

            default:
                throw new InvalidDataException($"{Path.GetFileName(path)} is not a model this can load: an OBJ or STL file is.");
        }
    }

    /// <summary>An OBJ file's corners and faces: each face a fan of triangles from its first corner.</summary>
    public static IReadOnlyList<Point3D> ReadObj(IEnumerable<string> lines, out IReadOnlyList<int> triangles)
    {
        var corners = new List<Point3D>();
        var faces = new List<int>();

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith("v ", StringComparison.Ordinal) || line.StartsWith("v\t", StringComparison.Ordinal))
            {
                var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 4) continue;
                corners.Add(new Point3D(Number(parts[1]), Number(parts[2]), Number(parts[3])));
            }
            else if (line.StartsWith("f ", StringComparison.Ordinal) || line.StartsWith("f\t", StringComparison.Ordinal))
            {
                // Each corner is "v", "v/vt", "v/vt/vn" or "v//vn"; counted from 1, or back from the last one read where negative.
                var face = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                    .Skip(1)
                    .Select(token => int.TryParse(token.Split('/')[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
                        ? index > 0 ? index - 1 : corners.Count + index
                        : -1)
                    .ToList();
                if (face.Count < 3 || face.Any(index => index < 0 || index >= corners.Count)) continue;

                for (var i = 1; i + 1 < face.Count; i++)
                {
                    faces.Add(face[0]);
                    faces.Add(face[i]);
                    faces.Add(face[i + 1]);
                }
            }
        }

        if (corners.Count == 0 || faces.Count == 0) throw new InvalidDataException("There are no faces in that OBJ file to make a family of.");
        triangles = faces;
        return corners;
    }

    /// <summary>
    /// An STL file's triangles, from its text or its binary form - which one it is told by its
    /// length: a binary file is an 80-byte header, a count, and 50 bytes to each triangle.
    /// </summary>
    public static (IReadOnlyList<Point3D> Corners, IReadOnlyList<int> Triangles) ReadStl(byte[] bytes)
    {
        var corners = new List<Point3D>();

        if (bytes.Length >= 84 && 84 + 50L * BitConverter.ToUInt32(bytes, 80) == bytes.Length)
        {
            var count = (int)BitConverter.ToUInt32(bytes, 80);
            for (var t = 0; t < count; t++)
            {
                var at = 84 + 50 * t + 12;
                for (var k = 0; k < 3; k++, at += 12)
                    corners.Add(new Point3D(BitConverter.ToSingle(bytes, at), BitConverter.ToSingle(bytes, at + 4), BitConverter.ToSingle(bytes, at + 8)));
            }
        }
        else
        {
            using var reader = new StringReader(System.Text.Encoding.ASCII.GetString(bytes));
            while (reader.ReadLine() is { } raw)
            {
                var parts = raw.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 4 && parts[0].Equals("vertex", StringComparison.OrdinalIgnoreCase))
                    corners.Add(new Point3D(Number(parts[1]), Number(parts[2]), Number(parts[3])));
            }

            corners.RemoveRange(corners.Count - corners.Count % 3, corners.Count % 3);
        }

        if (corners.Count < 3) throw new InvalidDataException("There are no triangles in that STL file to make a family of.");
        return (corners, Enumerable.Range(0, corners.Count).ToList());
    }

    private static double Number(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)
            ? value
            : throw new InvalidDataException($"\"{text}\" is not a number.");
}
