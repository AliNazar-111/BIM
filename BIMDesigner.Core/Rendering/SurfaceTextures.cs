using System.Collections.Concurrent;
using BIMDesigner.Core.Materials;

namespace BIMDesigner.Core.Rendering;

/// <summary>
/// A pattern drawn as a picture that repeats across a face: so many millimetres of it across and up,
/// its colours, and how far each point of it stands proud - the joints sunk, a slate's tail thick -
/// for a rendering to light. Its top row is the highest up the face.
/// </summary>
public sealed class SurfaceTexture
{
    internal SurfaceTexture(int width, int height, double length, double rise, byte[] pixels, byte[] relief)
    {
        (Width, Height, Length, Rise, Pixels, Relief) = (width, height, length, rise, pixels, relief);
        long r = 0, g = 0, b = 0;
        for (var i = 0; i < pixels.Length; i += 3) (r, g, b) = (r + pixels[i], g + pixels[i + 1], b + pixels[i + 2]);
        var count = Math.Max(1, pixels.Length / 3);
        Average = new ColourRgb((byte)(r / count), (byte)(g / count), (byte)(b / count));
    }

    /// <summary>Its size in pixels.</summary>
    public int Width { get; }

    public int Height { get; }

    /// <summary>How much of the face it covers before it repeats, mm across and up.</summary>
    public double Length { get; }

    public double Rise { get; }

    /// <summary>Red, green and blue a pixel, row by row from the top.</summary>
    public byte[] Pixels { get; }

    /// <summary>How far each pixel stands proud, 0 to 255.</summary>
    public byte[] Relief { get; }

    public ColourRgb Average { get; }

    /// <summary>The pixel at a point of the face, mm across and up.</summary>
    public int IndexAt(double u, double v)
    {
        var fx = u / Length;
        var fy = v / Rise;
        fx -= Math.Floor(fx);
        fy -= Math.Floor(fy);
        var x = Math.Min(Width - 1, (int)(fx * Width));
        var y = Math.Min(Height - 1, (int)((1 - fy) * Height));
        return y * Width + x;
    }

    public ColourRgb At(double u, double v)
    {
        var i = IndexAt(u, v) * 3;
        return new ColourRgb(Pixels[i], Pixels[i + 1], Pixels[i + 2]);
    }

    /// <summary>How far the face stands proud at a point, 0 to 1.</summary>
    public double ReliefAt(double u, double v) => Relief[IndexAt(u, v)] / 255.0;
}

/// <summary>
/// The pictures of the patterns, drawn here rather than loaded so nothing is borrowed: brick and
/// block in stretcher bond, coursed stone, boards with their grain, tiles and paving on a grid,
/// slates and tiles in their courses up a roof, standing seams, and render, concrete, metal,
/// grass, gravel and planting as their grain. Each brick, board and slate a little different, as
/// they are. Drawn once for each finish and kept.
/// </summary>
public static class SurfaceTextures
{
    private static readonly ConcurrentDictionary<SurfaceFinish, SurfaceTexture> Drawn = new();

    /// <summary>The picture of a finish; none for a plain colour.</summary>
    public static SurfaceTexture? Of(SurfaceFinish finish)
    {
        if (finish.Pattern == SurfacePattern.None) return null;
        var (length, height, joint, _) = SurfacePatterns.Units(finish.Pattern);
        var settled = finish with
        {
            UnitLength = finish.UnitLength > 0 ? finish.UnitLength : length,
            UnitHeight = finish.UnitHeight > 0 ? finish.UnitHeight : height,
            JointWidth = finish.JointWidth >= 0 ? finish.JointWidth : joint,
            Reflectance = 0
        };

        if (Drawn.Count > 256) Drawn.Clear();
        return Drawn.GetOrAdd(settled, Draw);
    }

    private static SurfaceTexture Draw(SurfaceFinish finish) => finish.Pattern switch
    {
        SurfacePattern.Brick => Masonry(finish, 4, 6, 0.11, Bond.Half, 1),
        SurfacePattern.Block => Masonry(finish, 3, 4, 0.04, Bond.Half, 2),
        SurfacePattern.Stone => Masonry(finish, 4, 5, 0.10, Bond.Random, 3),
        SurfacePattern.Tiles => Masonry(finish, 4, 4, 0.025, Bond.Stack, 4),
        SurfacePattern.Paving => Masonry(finish, 3, 3, 0.06, Bond.Stack, 5),
        SurfacePattern.Boards => Boards(finish),
        SurfacePattern.Slate => Roofing(finish, 0.07, 6),
        SurfacePattern.PlainTiles => Roofing(finish, 0.13, 7),
        SurfacePattern.InterlockingTiles => Roofing(finish, 0.05, 8),
        SurfacePattern.StandingSeam => Seams(finish),
        SurfacePattern.Grass => Grass(finish),
        SurfacePattern.Gravel => Gravel(finish),
        SurfacePattern.Planting => Planting(finish),
        SurfacePattern.BoardAndBatten => BoardAndBatten(finish),
        SurfacePattern.HalfTimbered => HalfTimbered(finish),
        SurfacePattern.BarrelTiles => Barrel(finish),
        SurfacePattern.Thatch => Thatch(finish),
        SurfacePattern.Shingles => Shingles(finish),
        SurfacePattern.Stucco => Grain(finish, 800, 0.8, 0.07, 0.045, 0.012, 13),
        SurfacePattern.Concrete => Grain(finish, 1200, 0.5, 0.03, 0.05, 0.004, 10),
        SurfacePattern.Metal => Grain(finish, 1000, 0.5, 0.025, 0.015, 0, 11, brushed: true),
        _ => Grain(finish, 1000, 0.6, 0.035, 0.025, 0, 12)
    };

    private enum Bond { Half, Stack, Random }

    // ---- the patterns -------------------------------------------------------------------------

    /// <summary>Units in courses with joints between: brick and block in half bond, tiles and paving stacked, stone in random lengths.</summary>
    private static SurfaceTexture Masonry(SurfaceFinish finish, int across, int courses, double variation, Bond bond, int seed)
    {
        var (length, height, joint) = (finish.UnitLength, finish.UnitHeight, Math.Max(0, finish.JointWidth));
        var (unitW, unitH) = (length + joint, height + joint);
        var (tileW, tileH) = (across * unitW, courses * unitH);
        var canvas = new Canvas(tileW, tileH, Scale(tileW, tileH, joint, 1024));
        var fine = new Noise(Cells(tileW, 6), Cells(tileH, 6), seed);
        var blotch = new Noise(Cells(tileW, 140), Cells(tileH, 140), seed + 100);

        // Stone: each course cut into lengths of its own, set along by a distance of its own.
        var cuts = new List<double>[courses];
        var shifts = new double[courses];
        for (var c = 0; c < courses; c++)
        {
            cuts[c] = new List<double> { 0 };
            shifts[c] = Hash(c, 7, seed) * tileW;
            if (bond != Bond.Random) continue;
            var at = 0.0;
            var k = 0;
            while (true)
            {
                at += unitW * (0.55 + 0.9 * Hash(c, k++, seed + 1));
                if (at >= tileW - 0.45 * unitW) break;
                cuts[c].Add(at);
            }
        }

        for (var y = 0; y < canvas.H; y++)
        for (var x = 0; x < canvas.W; x++)
        {
            var (px, py) = canvas.Mm(x, y);
            var c = Math.Min(courses - 1, (int)(py / unitH));
            var yy = py - c * unitH;
            int k;
            double xx, unitLength;
            if (bond == Bond.Random)
            {
                var along = Wrap(px - shifts[c], tileW);
                k = cuts[c].FindLastIndex(cut => cut <= along);
                var end = k + 1 < cuts[c].Count ? cuts[c][k + 1] : tileW;
                (xx, unitLength) = (along - cuts[c][k], end - cuts[c][k] - joint);
            }
            else
            {
                var along = Wrap(px - (bond == Bond.Half && c % 2 == 1 ? unitW / 2 : 0), tileW);
                k = (int)(along / unitW);
                (xx, unitLength) = (along - k * unitW, length);
            }

            var (fx, fy) = (px / tileW, py / tileH);
            var grain = fine.At(fx, fy);
            var edge = Math.Min(Math.Min(xx - joint / 2, joint / 2 + unitLength - xx), Math.Min(yy - joint / 2, joint / 2 + height - yy));
            if (joint > 0 && edge < 0)
            {
                canvas.Set(x, y, Times(finish.JointColour, 1 + 0.05 * grain), 0.2);
                continue;
            }

            var tint = 1 + variation * (2 * Hash(k, c, seed) - 1);
            var warm = finish.Pattern == SurfacePattern.Brick ? 0.05 * (2 * Hash(k, c, seed + 2) - 1) : 0;
            var speck = finish.Pattern is SurfacePattern.Brick or SurfacePattern.Block && Hash(x, y, seed + 3) < 0.015 ? 0.8 : 1;
            var shade = tint * speck * (1 + 0.05 * grain + 0.03 * blotch.At(fx, fy));
            canvas.Set(x, y, (finish.Colour.R * shade * (1 + warm), finish.Colour.G * shade, finish.Colour.B * shade * (1 - warm)), 0.65 + 0.35 * Math.Min(1, edge / 2));
        }

        return canvas.Done(tileW, tileH);
    }

    /// <summary>Boards laid along, each with its grain and a tone of its own, their ends staggered, a shadow gap between.</summary>
    private static SurfaceTexture Boards(SurfaceFinish finish)
    {
        var (length, width, gap) = (finish.UnitLength, finish.UnitHeight, Math.Max(0, finish.JointWidth));
        var rows = Math.Max(4, (int)Math.Ceiling(600 / width));
        var (tileW, tileH) = (length, rows * width);
        var canvas = new Canvas(tileW, tileH, Scale(tileW, tileH, gap, 2048));
        var streaks = new Noise(Cells(tileW, 700), Cells(tileH, 2.5), 20);
        var knots = new Noise(Cells(tileW, 90), Cells(tileH, 25), 21);
        var fine = new Noise(Cells(tileW, 8), Cells(tileH, 2), 22);

        for (var y = 0; y < canvas.H; y++)
        for (var x = 0; x < canvas.W; x++)
        {
            var (px, py) = canvas.Mm(x, y);
            var row = Math.Min(rows - 1, (int)(py / width));
            var yy = py - row * width;
            var butt = Hash(row, 0, 23) * tileW;
            var fromButt = Math.Abs(Wrap(px - butt + tileW / 2, tileW) - tileW / 2);
            if (yy < gap || fromButt < gap / 2)
            {
                canvas.Set(x, y, Times(finish.JointColour, 1), 0);
                continue;
            }

            var (fx, fy) = (px / tileW, py / tileH);
            var tint = 1 + 0.09 * (2 * Hash(row, 1, 23) - 1);
            var shade = tint * (1 + 0.10 * streaks.At(fx, fy) + 0.05 * knots.At(fx, fy) + 0.025 * fine.At(fx, fy));
            canvas.Set(x, y, Times(finish.Colour, shade), 0.6 + 0.4 * Math.Min(1, Math.Min(yy - gap, width - yy) / 3));
        }

        return canvas.Done(tileW, tileH);
    }

    /// <summary>
    /// Boards stood upright side by side, the grain running up them, each a tone of its own, and a batten over each joint standing proud: lit
    /// along its left edge, shaded along its right, casting a band of shadow on the board to its right (round 141).
    /// </summary>
    private static SurfaceTexture BoardAndBatten(SurfaceFinish finish)
    {
        var (spacing, batten, shadow) = (finish.UnitLength, Math.Max(10, Math.Min(finish.UnitHeight, finish.UnitLength / 2)), Math.Max(2, finish.JointWidth));
        const int across = 4;
        var (tileW, tileH) = (across * spacing, 2400.0);
        var canvas = new Canvas(tileW, tileH, Scale(tileW, tileH, 0, 1536));
        var streaks = new Noise(Cells(tileW, 2.5), Cells(tileH, 700), 70);
        var knots = new Noise(Cells(tileW, 25), Cells(tileH, 90), 71);
        var fine = new Noise(Cells(tileW, 2), Cells(tileH, 8), 72);
        for (var y = 0; y < canvas.H; y++)
        for (var x = 0; x < canvas.W; x++)
        {
            var (px, py) = canvas.Mm(x, y);
            var (fx, fy) = (px / tileW, py / tileH);
            var grain = 0.08 * streaks.At(fx, fy) + 0.04 * knots.At(fx, fy) + 0.02 * fine.At(fx, fy);
            var xx = Wrap(px, spacing);

            // The batten over the joint at xx 0: across it from its left edge.
            var onBatten = xx < spacing / 2 ? batten / 2 + xx : batten / 2 - (spacing - xx);
            if (onBatten >= 0 && onBatten <= batten)
            {
                var joint = (int)Math.Round(px / spacing) % across;
                var edge = onBatten < 3 ? 1.14 : onBatten > batten - 3 ? 0.78 : 1;
                canvas.Set(x, y, Times(finish.Colour, (1 + 0.06 * (2 * Hash(joint, 3, 73) - 1)) * edge * (1 + grain)), 1);
                continue;
            }

            var board = (int)(px / spacing) % across;
            var shade = (1 + 0.07 * (2 * Hash(board, 1, 74) - 1)) * (1 + grain);
            var fromBatten = xx - batten / 2;
            if (fromBatten < shadow) shade *= 0.6 + 0.4 * (fromBatten / shadow);
            canvas.Set(x, y, Times(finish.Colour, shade), 0.3 + 0.1 * grain);
        }

        return canvas.Done(tileW, tileH);
    }

    /// <summary>
    /// Half-timbering a storey high, four panels across: a sole plate along the bottom and a top plate along the top, a rail across the middle,
    /// a post at every panel's edge, and braces - up from the sole plate to the rail in the outer panels' lower halves, leaning in, and in the
    /// inner panels' upper halves leaning out - the timbers standing proud of the render between, each a tone of its own (round 142).
    /// </summary>
    private static SurfaceTexture HalfTimbered(SurfaceFinish finish)
    {
        var (panel, storey, timber) = (finish.UnitLength, finish.UnitHeight, Math.Max(40, finish.JointWidth));
        const int across = 4;
        var tileW = across * panel;
        var canvas = new Canvas(tileW, storey, Scale(tileW, storey, 0, 1536));
        var grain = new Noise(Cells(tileW, 3), Cells(storey, 60), 90);
        var render = new Noise(Cells(tileW, 4), Cells(storey, 4), 91);
        var blotch = new Noise(Cells(tileW, 200), Cells(storey, 200), 92);
        var (rail, half) = (storey / 2, timber / 2);

        // A brace: from one corner of half a panel to the other, as a band of the timber's width.
        static double FromLine(double x, double y, double x0, double y0, double x1, double y1)
        {
            var (dx, dy) = (x1 - x0, y1 - y0);
            var t = Math.Clamp(((x - x0) * dx + (y - y0) * dy) / (dx * dx + dy * dy), 0, 1);
            return Math.Sqrt(Math.Pow(x - x0 - t * dx, 2) + Math.Pow(y - y0 - t * dy, 2));
        }

        for (var y = 0; y < canvas.H; y++)
        for (var x = 0; x < canvas.W; x++)
        {
            var (px, py) = canvas.Mm(x, y);
            var up = storey - py;
            var k = Math.Min(across - 1, (int)(px / panel));
            var xx = px - k * panel;
            var (left, right) = (k * panel, (k + 1) * panel);
            bool Brace()
            {
                if (k is 0 or across - 1 && up < rail)
                    return k == 0 ? FromLine(px, up, left + half, timber, right - half, rail - half) < half * 0.8
                                  : FromLine(px, up, right - half, timber, left + half, rail - half) < half * 0.8;
                if (k is 1 or 2 && up > rail)
                    return k == 1 ? FromLine(px, up, right - half, rail + half, left + half, storey - timber) < half * 0.8
                                  : FromLine(px, up, left + half, rail + half, right - half, storey - timber) < half * 0.8;
                return false;
            }

            var (fx, fy) = (px / tileW, py / storey);
            var plate = up < timber || up > storey - timber;
            var post = xx < half || xx > panel - half;
            var middle = Math.Abs(up - rail) < half * 0.85;
            if (plate || post || middle || Brace())
            {
                var piece = plate ? (up < timber ? 0 : 1) : post ? 2 + (int)Math.Round(px / panel) % across : middle ? 7 + k : 11 + k;
                var tone = (1 + 0.12 * (2 * Hash(piece, 1, 93) - 1)) * (1 + 0.10 * grain.At(fx, fy));
                canvas.Set(x, y, Times(finish.JointColour, tone), 1);
                continue;
            }

            canvas.Set(x, y, Times(finish.Colour, 1 + 0.035 * render.At(fx, fy) + 0.03 * blotch.At(fx, fy)), 0.35);
        }

        return canvas.Done(tileW, storey);
    }

    /// <summary>
    /// Curved clay tiles in rows down the slope, a pan and a cover across each unit: the pan a trough, low and shaded at its middle, the cover a
    /// roll standing up over the pans either side, lit on one flank and dark on the other; each course's covers overlapping the course below,
    /// a line of shadow under their ends; terracotta tile by tile a tone of its own (round 142).
    /// </summary>
    private static SurfaceTexture Barrel(SurfaceFinish finish)
    {
        var (width, gauge) = (finish.UnitLength, finish.UnitHeight);
        const int across = 6, courses = 6;
        var (tileW, tileH) = (across * width, courses * gauge);
        var canvas = new Canvas(tileW, tileH, Scale(tileW, tileH, 0, 1536));
        var fine = new Noise(Cells(tileW, 6), Cells(tileH, 6), 95);
        var pan = width / 2;
        for (var y = 0; y < canvas.H; y++)
        for (var x = 0; x < canvas.W; x++)
        {
            var (px, py) = canvas.Mm(x, y);
            var c = Math.Min(courses - 1, (int)(py / gauge));
            var yy = py - c * gauge;
            var k = (int)(px / width) % across;
            var xx = Wrap(px, width);
            var tint = 1 + 0.07 * (2 * Hash(k, c, 96) - 1);
            double shade, relief;

            // The cover, a half round over the joint between pans: across it from its left flank, 0 to pi.
            var onCover = xx < pan / 2 ? xx + pan / 2 : xx > width - pan / 2 ? xx - (width - pan / 2) : -1;
            if (onCover >= 0)
            {
                var angle = Math.PI * onCover / pan;
                (shade, relief) = (0.8 + 0.38 * Math.Cos(angle) + 0.1 * Math.Sin(angle), 0.55 + 0.45 * Math.Sin(angle));
            }
            else
            {
                var across_ = (xx - pan / 2) / pan;
                (shade, relief) = (0.82 + 0.1 * Math.Abs(2 * across_ - 1), 0.1 + 0.25 * Math.Abs(2 * across_ - 1));
            }

            // The course above's ends over the top of this one, and their shadow.
            var shadow = Math.Min(25, gauge * 0.08);
            if (yy < shadow) shade *= 0.5 + 0.5 * (yy / shadow);
            canvas.Set(x, y, Times(finish.Colour, tint * shade * (1 + 0.04 * fine.At(px / tileW, py / tileH))), relief);
        }

        return canvas.Done(tileW, tileH);
    }

    /// <summary>
    /// Shingles in courses, each of a width and a tone of its own, the joints of one course off those of the course below, a gap between: each
    /// course's butts standing proud of the course below and casting a line of shadow on it - cedar shingles on a wall, asphalt ones on a roof
    /// (round 141).
    /// </summary>
    private static SurfaceTexture Shingles(SurfaceFinish finish)
    {
        var (width, exposure, gap) = (finish.UnitLength, finish.UnitHeight, Math.Max(0, finish.JointWidth));
        const int courses = 8;
        var (tileW, tileH) = (Math.Max(1200, 6 * width), courses * exposure);
        var canvas = new Canvas(tileW, tileH, Scale(tileW, tileH, gap, 1536));
        var streaks = new Noise(Cells(tileW, 2), Cells(tileH, 60), 80);
        var cuts = new List<double>[courses];
        var shifts = new double[courses];
        for (var c = 0; c < courses; c++)
        {
            (cuts[c], shifts[c]) = (new List<double> { 0 }, Hash(c, 7, 81) * tileW);
            var at = 0.0;
            for (var k = 0; ; k++)
            {
                at += width * (0.45 + 1.1 * Hash(c, k, 82));
                if (at >= tileW - 0.4 * width) break;
                cuts[c].Add(at);
            }
        }

        var shadow = Math.Min(12, exposure * 0.1);
        for (var y = 0; y < canvas.H; y++)
        for (var x = 0; x < canvas.W; x++)
        {
            var (px, py) = canvas.Mm(x, y);
            var c = Math.Min(courses - 1, (int)(py / exposure));
            var yy = py - c * exposure;
            var along = Wrap(px - shifts[c], tileW);
            var k = cuts[c].FindLastIndex(cut => cut <= along);
            if (along - cuts[c][k] < gap)
            {
                canvas.Set(x, y, Times(finish.JointColour, 0.8), 0.1);
                continue;
            }

            var shade = (1 + 0.12 * (2 * Hash(k, c, 83) - 1)) * (1 + 0.06 * streaks.At(px / tileW, py / tileH));
            if (yy < shadow) shade *= 0.55 + 0.45 * (yy / shadow);
            canvas.Set(x, y, Times(finish.Colour, shade), 0.15 + 0.75 * (yy / exposure));
        }

        return canvas.Done(tileW, tileH);
    }

    /// <summary>
    /// Slates and tiles in courses up a roof, each course half a unit along from the one below: each
    /// unit's tail lying on the course below, thick enough to cast a line of shadow on it, the gaps
    /// between units dark; an interlocking tile's roll across it.
    /// </summary>
    private static SurfaceTexture Roofing(SurfaceFinish finish, double variation, int seed)
    {
        var (width, gauge, gap) = (finish.UnitLength, finish.UnitHeight, Math.Max(0, finish.JointWidth));
        const int across = 6, courses = 8;
        var (tileW, tileH) = (across * width, courses * gauge);
        var canvas = new Canvas(tileW, tileH, Scale(tileW, tileH, gap, 1024));
        var fine = new Noise(Cells(tileW, 5), Cells(tileH, 5), seed);
        var shadow = finish.Pattern switch { SurfacePattern.Slate => 7.0, SurfacePattern.PlainTiles => 9.0, _ => 14.0 };
        var interlocking = finish.Pattern == SurfacePattern.InterlockingTiles;

        for (var y = 0; y < canvas.H; y++)
        for (var x = 0; x < canvas.W; x++)
        {
            var (px, py) = canvas.Mm(x, y);
            var c = Math.Min(courses - 1, (int)(py / gauge));
            var yy = py - c * gauge;
            var along = Wrap(px - (c % 2 == 1 ? width / 2 : 0), tileW);
            var k = (int)(along / width);
            var xx = along - k * width;
            if (!interlocking && xx < gap)
            {
                canvas.Set(x, y, Times(finish.JointColour, 1), 0.1);
                continue;
            }

            var tint = 1 + variation * (2 * Hash(k, c, seed) - 1);
            var roll = interlocking ? Math.Sin(2 * Math.PI * xx / width) : 0;
            var shade = tint * (1 + 0.04 * fine.At(px / tileW, py / tileH) + 0.12 * roll);

            // The shadow of the course above's tail across the top of this one.
            if (yy < shadow) shade *= 0.55 + 0.45 * (yy / shadow);
            canvas.Set(x, y, Times(finish.Colour, shade), 0.15 + 0.7 * (yy / gauge) + 0.15 * roll);
        }

        return canvas.Done(tileW, tileH);
    }

    /// <summary>Standing seams up the slope at their centres, lit one side and shaded the other, the pans between nearly flat.</summary>
    private static SurfaceTexture Seams(SurfaceFinish finish)
    {
        var (centres, seam) = (finish.UnitLength, Math.Max(4, finish.JointWidth));
        var (tileW, tileH) = (4 * centres, 1000.0);
        var canvas = new Canvas(tileW, tileH, Scale(tileW, tileH, seam, 1024));
        var canning = new Noise(Cells(tileW, 250), Cells(tileH, 400), 30);
        var fine = new Noise(Cells(tileW, 3), Cells(tileH, 60), 31);
        for (var y = 0; y < canvas.H; y++)
        for (var x = 0; x < canvas.W; x++)
        {
            var (px, py) = canvas.Mm(x, y);
            var xx = Wrap(px, centres);
            var (fx, fy) = (px / tileW, py / tileH);
            if (xx < seam)
                canvas.Set(x, y, Times(finish.Colour, xx < seam / 2 ? 1.18 : 0.72), 1);
            // The seam standing an inch proud throws a shadow on the pan beside it, which is what shows it from a distance (round 154).
            else if (xx < seam + 2.5 * seam)
                canvas.Set(x, y, Times(finish.Colour, 0.72 + 0.28 * (xx - seam) / (2.5 * seam)), 0.35);
            else
                canvas.Set(x, y, Times(finish.Colour, 1 + 0.03 * canning.At(fx, fy) + 0.015 * fine.At(fx, fy)), 0.35);
        }

        return canvas.Done(tileW, tileH);
    }

    /// <summary>A grain without units: render's and plaster's fine texture, concrete's blotches and pores, metal brushed along.</summary>
    private static SurfaceTexture Grain(SurfaceFinish finish, double size, double scale, double fineAmount, double blotchAmount, double pores, int seed, bool brushed = false)
    {
        var canvas = new Canvas(size, size, scale);
        var fine = brushed ? new Noise(Cells(size, 300), Cells(size, 2), seed) : new Noise(Cells(size, 3), Cells(size, 3), seed);
        var blotch = new Noise(Cells(size, 180), Cells(size, 180), seed + 1);
        for (var y = 0; y < canvas.H; y++)
        for (var x = 0; x < canvas.W; x++)
        {
            var (px, py) = canvas.Mm(x, y);
            var (fx, fy) = (px / size, py / size);
            var grain = fine.At(fx, fy);
            var pore = pores > 0 && Hash(x, y, seed + 2) < pores;
            canvas.Set(x, y, Times(finish.Colour, (1 + fineAmount * grain + blotchAmount * blotch.At(fx, fy)) * (pore ? 0.62 : 1)), pore ? 0.1 : 0.5 + 0.3 * grain);
        }

        return canvas.Done(size, size);
    }

    /// <summary>Grass: blade by blade lighter and darker, in patches, a little yellower here and bluer there.</summary>
    private static SurfaceTexture Grass(SurfaceFinish finish)
    {
        // Three metres of it before it repeats, its patches big and soft, so the repeat is not seen across a lawn.
        const double size = 3000;
        var canvas = new Canvas(size, size, 0.5);
        var patches = new Noise(Cells(size, 700), Cells(size, 700), 40);
        var tufts = new Noise(Cells(size, 40), Cells(size, 40), 41);
        for (var y = 0; y < canvas.H; y++)
        for (var x = 0; x < canvas.W; x++)
        {
            var (px, py) = canvas.Mm(x, y);
            var (fx, fy) = (px / size, py / size);
            var blade = 2 * Hash(x, y, 42) - 1;
            var shade = 1 + 0.14 * blade + 0.07 * patches.At(fx, fy) + 0.06 * tufts.At(fx, fy);
            var yellow = 0.06 * patches.At(fy, fx);
            canvas.Set(x, y, (finish.Colour.R * shade * (1 + yellow), finish.Colour.G * shade, finish.Colour.B * shade * (1 - yellow)), 0.5 + 0.5 * blade);
        }

        return canvas.Done(size, size);
    }

    /// <summary>
    /// Thatch (round 151): reed or straw dressed into courses up the slope - its stems seen as fine streaks running down it, darker in the
    /// hollows between them, each course a faint shadow under the one over it, weathered grey in broad patches. A stem's width and a
    /// course's height are its units.
    /// </summary>
    private static SurfaceTexture Thatch(SurfaceFinish finish)
    {
        var (stem, course) = (Math.Max(3, finish.UnitLength), Math.Max(150, finish.UnitHeight));
        var (tileW, tileH) = (1200.0, Math.Max(1, Math.Round(1200 / course)) * course);
        var canvas = new Canvas(tileW, tileH, Scale(tileW, tileH, stem, 1536));
        var patches = new Noise(Cells(tileW, 450), Cells(tileH, 450), 93);
        var stems = new Noise(Cells(tileW, stem), Cells(tileH, stem * 14), 94);
        var shadow = course * 0.12;
        for (var y = 0; y < canvas.H; y++)
        for (var x = 0; x < canvas.W; x++)
        {
            var (px, py) = canvas.Mm(x, y);
            var (fx, fy) = (px / tileW, py / tileH);
            var fibre = stems.At(fx, fy);
            var shade = (1 + 0.22 * fibre) * (1 + 0.08 * patches.At(fx, fy));
            var yy = Wrap(py, course);
            if (yy < shadow) shade *= 0.78 + 0.22 * (yy / shadow);
            var (r, g, b) = Times(finish.Colour, shade);
            var grey = 0.12 * Math.Max(0, patches.At(fy, fx));
            var mean = (r + g + b) / 3;
            canvas.Set(x, y, (r + (mean - r) * grey, g + (mean - g) * grey, b + (mean - b) * grey), 0.2 + 0.3 * (1 + fibre));
        }

        return canvas.Done(tileW, tileH);
    }

    /// <summary>Gravel: stones of their own size and shade, rounded, lying close, the gaps between dark.</summary>
    private static SurfaceTexture Gravel(SurfaceFinish finish)
    {
        const double size = 600;
        var canvas = new Canvas(size, size, 1.2);
        var under = Times(finish.Colour, 0.5);
        for (var i = 0; i < canvas.R.Length; i++) (canvas.R[i], canvas.G[i], canvas.B[i], canvas.Z[i]) = (under.R, under.G, under.B, 0);

        var stones = (int)(size * size / (Math.PI * 6.5 * 6.5) * 1.6);
        for (var s = 0; s < stones; s++)
        {
            var (cx, cy, radius) = (Hash(s, 0, 50) * size, Hash(s, 1, 50) * size, 3.5 + 5.5 * Hash(s, 2, 50));
            var shade = 0.72 + 0.5 * Hash(s, 3, 50);
            var warm = 0.08 * (2 * Hash(s, 4, 50) - 1);
            canvas.Disc(cx, cy, radius * 1.15, radius, (dx, dy, d) =>
            {
                var dome = Math.Sqrt(Math.Max(0, 1 - d * d));
                var lit = shade * (0.85 + 0.15 * dome + 0.08 * (-dx - dy));
                return ((finish.Colour.R * lit * (1 + warm), finish.Colour.G * lit, finish.Colour.B * lit * (1 - warm)), 0.3 + 0.7 * dome);
            });
        }

        return canvas.Done(size, size);
    }

    /// <summary>Planting: leaves in clumps of their own shade over dark soil.</summary>
    private static SurfaceTexture Planting(SurfaceFinish finish)
    {
        const double size = 1500;
        var canvas = new Canvas(size, size, 0.4);
        var soil = new Noise(Cells(size, 8), Cells(size, 8), 60);
        for (var y = 0; y < canvas.H; y++)
        for (var x = 0; x < canvas.W; x++)
        {
            var (px, py) = canvas.Mm(x, y);
            canvas.Set(x, y, Times(new ColourRgb(0x4E, 0x3D, 0x2D), 1 + 0.15 * soil.At(px / size, py / size)), 0);
        }

        for (var s = 0; s < 70; s++)
        {
            var (cx, cy, radius) = (Hash(s, 0, 61) * size, Hash(s, 1, 61) * size, 50 + 110 * Hash(s, 2, 61));
            var shade = 0.65 + 0.5 * Hash(s, 3, 61);
            canvas.Disc(cx, cy, radius, radius, (dx, dy, d) =>
            {
                var leaf = 1 + 0.25 * (2 * Hash((int)(cx + dx * 40), (int)(cy + dy * 40), 62) - 1);
                return (Times(finish.Colour, shade * leaf * (0.75 + 0.25 * (1 - d))), 0.4 + 0.6 * (1 - d));
            });
        }

        return canvas.Done(size, size);
    }

    // ---- drawing ------------------------------------------------------------------------------

    /// <summary>Pixels a millimetre: about a thousand across the picture, enough that a joint is a pixel and a half.</summary>
    private static double Scale(double width, double height, double joint, int most)
    {
        var scale = most / Math.Max(width, height);
        if (joint > 0) scale = Math.Max(scale, 1.5 / joint);
        return Math.Clamp(Math.Min(scale, 2048 / Math.Max(width, height)), 0.05, 4);
    }

    private static int Cells(double size, double feature) => Math.Max(2, (int)Math.Round(size / feature));

    private static double Wrap(double value, double period) => value - Math.Floor(value / period) * period;

    private static (double R, double G, double B) Times(ColourRgb colour, double factor) => (colour.R * factor, colour.G * factor, colour.B * factor);

    /// <summary>A number from 0 to 1 that is always the same for the same three.</summary>
    internal static double Hash(int a, int b, int seed)
    {
        unchecked
        {
            var h = (uint)a * 374761393u + (uint)b * 668265263u + (uint)seed * 2246822519u;
            h = (h ^ (h >> 13)) * 1274126177u;
            h ^= h >> 16;
            return h / 4294967296.0;
        }
    }

    /// <summary>Value noise that repeats across the picture: a lattice of numbers, smoothly between.</summary>
    private sealed class Noise(int cellsX, int cellsY, int seed)
    {
        public double At(double fx, double fy)
        {
            var (x, y) = (fx * cellsX, fy * cellsY);
            var (x0, y0) = ((int)Math.Floor(x), (int)Math.Floor(y));
            var (tx, ty) = (Smooth(x - x0), Smooth(y - y0));
            double V(int i, int j) => 2 * Hash(Mod(i, cellsX), Mod(j, cellsY), seed) - 1;
            var top = V(x0, y0) + (V(x0 + 1, y0) - V(x0, y0)) * tx;
            var bottom = V(x0, y0 + 1) + (V(x0 + 1, y0 + 1) - V(x0, y0 + 1)) * tx;
            return top + (bottom - top) * ty;
        }

        private static double Smooth(double t) => t * t * (3 - 2 * t);

        private static int Mod(int value, int period) => ((value % period) + period) % period;
    }

    private sealed class Canvas
    {
        public readonly int W, H;
        private readonly double _sx, _sy;
        public readonly double[] R, G, B, Z;

        public Canvas(double width, double height, double scale)
        {
            (W, H) = (Math.Max(8, (int)Math.Round(width * scale)), Math.Max(8, (int)Math.Round(height * scale)));
            (_sx, _sy) = (W / width, H / height);
            (R, G, B, Z) = (new double[W * H], new double[W * H], new double[W * H], new double[W * H]);
        }

        /// <summary>The middle of a pixel, mm across from the left and down from the top.</summary>
        public (double X, double Y) Mm(int x, int y) => ((x + 0.5) / _sx, (y + 0.5) / _sy);

        public void Set(int x, int y, (double R, double G, double B) colour, double relief)
        {
            var i = y * W + x;
            (R[i], G[i], B[i], Z[i]) = (colour.R, colour.G, colour.B, relief);
        }

        /// <summary>A round thing at a point, mm, wrapping round the picture's edges, each pixel of it as it says.</summary>
        public void Disc(double cx, double cy, double reach, double radius, Func<double, double, double, ((double R, double G, double B) Colour, double Relief)> paint)
        {
            var (x0, x1) = ((int)Math.Floor((cx - reach) * _sx), (int)Math.Ceiling((cx + reach) * _sx));
            var (y0, y1) = ((int)Math.Floor((cy - reach) * _sy), (int)Math.Ceiling((cy + reach) * _sy));
            for (var y = y0; y <= y1; y++)
            for (var x = x0; x <= x1; x++)
            {
                var (dx, dy) = (((x + 0.5) / _sx - cx) / radius, ((y + 0.5) / _sy - cy) / radius);
                var d = Math.Sqrt(dx * dx + dy * dy);
                if (d > 1) continue;
                var (wx, wy) = ((x % W + W) % W, (y % H + H) % H);
                var (colour, relief) = paint(dx, dy, d);
                if (relief < Z[wy * W + wx]) continue;
                Set(wx, wy, colour, relief);
            }
        }

        public SurfaceTexture Done(double length, double rise)
        {
            var pixels = new byte[W * H * 3];
            var relief = new byte[W * H];
            for (var i = 0; i < W * H; i++)
            {
                pixels[3 * i] = (byte)Math.Clamp(Math.Round(R[i]), 0, 255);
                pixels[3 * i + 1] = (byte)Math.Clamp(Math.Round(G[i]), 0, 255);
                pixels[3 * i + 2] = (byte)Math.Clamp(Math.Round(B[i]), 0, 255);
                relief[i] = (byte)Math.Clamp(Math.Round(Z[i] * 255), 0, 255);
            }

            return new SurfaceTexture(W, H, length, rise, pixels, relief);
        }
    }
}
