using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// The sections a column can be started from, ready made (specification section 16).
///
/// Drawing a column from nothing every time is work nobody should repeat: the shapes an
/// architect actually reaches for - a chamfered pier, a column with reveals down its faces, a
/// hollow box, a cruciform - are a short list, and they are all the same few booleans applied
/// to the same few generators.
///
/// Every one is built from a width and a depth rather than stored as points, so a preset is a
/// starting point that can be sized before or after it is picked, and the editor can go on
/// cutting it afterwards. Adding one is a method here, not a change anywhere else.
/// </summary>
public static class ColumnLibrary
{
    /// <summary>One section that can be started from: what it is called, and how it is built.</summary>
    public readonly record struct Preset(string Group, string Name, Func<double, double, ColumnProfile> Build)
    {
        public ColumnProfile At(double width, double depth) => Build(Math.Max(10, width), Math.Max(10, depth));

        public override string ToString() => $"{Group} — {Name}";
    }

    /// <summary>Every section on offer, in the order they are worth looking through.</summary>
    public static IReadOnlyList<Preset> All { get; } = Build();

    private static IReadOnlyList<Preset> Build()
    {
        var presets = new List<Preset>();

        void Add(string group, string name, Func<double, double, ColumnProfile> build) =>
            presets.Add(new Preset(group, name, build));

        // ---- the plain shapes --------------------------------------------------

        Add("Plain", "Square", (w, d) => ColumnProfile.Rectangle(w, d));
        Add("Plain", "Round", (w, d) => ColumnProfile.Ellipse(w, d));
        Add("Plain", "Hexagonal", (w, d) => ColumnProfile.Polygon(6, w, d));
        Add("Plain", "Octagonal", (w, d) => ColumnProfile.Polygon(8, w, d));
        Add("Plain", "Triangular", (w, d) => ColumnProfile.Polygon(3, w, d));

        // ---- the ones a corner is worked on ------------------------------------

        Add("Corners", "Chamfered", (w, d) => ColumnProfile.Rectangle(w, d).Chamfered(Math.Min(w, d) * 0.15));
        Add("Corners", "Rounded", (w, d) => ColumnProfile.Rectangle(w, d).Rounded(Math.Min(w, d) * 0.18));
        Add("Corners", "Stadium", (w, d) => ColumnProfile.Rectangle(w, d).Rounded(Math.Min(w, d) * 0.5));

        Add("Corners", "Quirked", (w, d) =>
        {
            // A small square taken out of each corner: the reveal a precast pier is cast with.
            var quirk = Math.Min(w, d) * 0.12;
            var profile = ColumnProfile.Rectangle(w, d);

            foreach (var (x, y) in Quarters(w / 2, d / 2))
                profile = Cut(profile, ColumnProfile.Rectangle(quirk * 2, quirk * 2), x, y);

            return profile;
        });

        // ---- the ones with something cut into the faces ------------------------

        Add("Reveals", "One reveal each face", (w, d) => Reveals(w, d, 1));
        Add("Reveals", "Two reveals each face", (w, d) => Reveals(w, d, 2));

        Add("Reveals", "Grooved round", (w, d) =>
            ColumnGeometry.Fluted(ColumnProfile.Ellipse(w, d),
                new ColumnShaping { Flutes = 16, FluteDepth = Math.Min(w, d) * 0.06 }));

        Add("Reveals", "Fluted round", (w, d) =>
            ColumnGeometry.Fluted(ColumnProfile.Ellipse(w, d),
                new ColumnShaping { Flutes = 24, FluteDepth = Math.Min(w, d) * 0.05 }));

        // ---- the hollow ones ---------------------------------------------------

        Add("Hollow", "Box", (w, d) =>
            Cut(ColumnProfile.Rectangle(w, d), ColumnProfile.Rectangle(w * 0.6, d * 0.6), 0, 0));

        Add("Hollow", "Tube", (w, d) =>
            Cut(ColumnProfile.Ellipse(w, d), ColumnProfile.Ellipse(w * 0.62, d * 0.62), 0, 0));

        Add("Hollow", "Box round a core", (w, d) =>
            Cut(ColumnProfile.Rectangle(w, d), ColumnProfile.Ellipse(w * 0.55, d * 0.55), 0, 0));

        // ---- the ones built out of more than one shape -------------------------

        Add("Built up", "Cruciform", (w, d) =>
        {
            var arm = Math.Min(w, d) * 0.36;
            return Join(ColumnProfile.Rectangle(w, arm), ColumnProfile.Rectangle(arm, d), 0, 0);
        });

        Add("Built up", "T", (w, d) =>
        {
            // A bar across the top with a stem hanging from the middle of it.
            var arm = Math.Min(w, d) * 0.4;
            return Join(
                ColumnProfile.Rectangle(w, arm).Transformed(p => new Point2D(p.X, p.Y + (d - arm) / 2)),
                ColumnProfile.Rectangle(arm, d), 0, 0);
        });

        Add("Built up", "L corner", (w, d) => ColumnProfile.Corner(w, d));

        Add("Built up", "Square on a round", (w, d) =>
            // The square's corners stand out of the circle, which is the whole point of it.
            Join(ColumnProfile.Ellipse(w, d), ColumnProfile.Rectangle(w * 0.82, d * 0.82), 0, 0));

        Add("Built up", "Wall pier", (w, d) =>
            // A length of wall with a deeper block in the middle of it, as a pier thickening a
            // wall is built.
            Join(ColumnProfile.Rectangle(w, d * 0.5), ColumnProfile.Rectangle(w * 0.42, d), 0, 0));

        return presets;
    }

    /// <summary>The four corners of a section, for working on each in turn.</summary>
    private static IEnumerable<(double X, double Y)> Quarters(double halfWidth, double halfDepth)
    {
        yield return (-halfWidth, -halfDepth);
        yield return (halfWidth, -halfDepth);
        yield return (halfWidth, halfDepth);
        yield return (-halfWidth, halfDepth);
    }

    /// <summary>A section with grooves cut down each of its four faces.</summary>
    private static ColumnProfile Reveals(double width, double depth, int perFace)
    {
        var profile = ColumnProfile.Rectangle(width, depth);
        var size = Math.Min(width, depth) * (perFace == 1 ? 0.14 : 0.1);

        for (var i = 0; i < perFace; i++)
        {
            // Spaced evenly along each face, symmetrically about its middle.
            var offset = perFace == 1 ? 0 : (i - (perFace - 1) / 2.0) * size * 3;

            profile = Cut(profile, ColumnProfile.Rectangle(size, size * 2), offset, depth / 2);
            profile = Cut(profile, ColumnProfile.Rectangle(size, size * 2), offset, -depth / 2);
            profile = Cut(profile, ColumnProfile.Rectangle(size * 2, size), width / 2, offset);
            profile = Cut(profile, ColumnProfile.Rectangle(size * 2, size), -width / 2, offset);
        }

        return profile;
    }

    private static ColumnProfile Cut(ColumnProfile profile, ColumnProfile shape, double x, double y) =>
        profile.Combine(Moved(shape, x, y).Outer, BooleanOperation.Difference);

    private static ColumnProfile Join(ColumnProfile profile, ColumnProfile shape, double x, double y) =>
        profile.Combine(Moved(shape, x, y).Outer, BooleanOperation.Union);

    private static ColumnProfile Moved(ColumnProfile profile, double x, double y) =>
        profile.Transformed(point => new Point2D(point.X + x, point.Y + y));
}
