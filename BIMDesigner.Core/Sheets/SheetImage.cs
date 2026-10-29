using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Sheets;

/// <summary>
/// A picture placed on a sheet: a rendering of the model, a photograph of the site, a logo. Unlike a
/// viewport it is not a view of the model and does not change with it - it is the picture as it was put
/// there, kept in the project whole, PNG or JPEG as it came. It sits where its centre is put, as wide as
/// it is made, its height following its own proportions; its title printed under it as a view's is.
/// </summary>
public sealed class SheetImage
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>What it is called: the file it came from, or what made it.</summary>
    public string Name { get; set; } = "Image";

    /// <summary>The picture, as a PNG or a JPEG file holds it.</summary>
    public byte[] Data { get; set; } = Array.Empty<byte>();

    public int PixelWidth { get; set; }

    public int PixelHeight { get; set; }

    /// <summary>Its centre, in millimetres from the sheet's bottom-left corner.</summary>
    public Point2D Centre { get; set; }

    /// <summary>How wide it is printed, paper millimetres.</summary>
    public double Width { get; set; } = 150;

    /// <summary>How high it is printed, paper millimetres: its width in its own proportions.</summary>
    public double Height => PixelWidth > 0 && PixelHeight > 0 ? Width * PixelHeight / PixelWidth : Width;

    /// <summary>Whether its title is printed under it.</summary>
    public bool ShowTitle { get; set; } = true;

    /// <summary>A title typed for it; empty, its name without the file's extension.</summary>
    public string Title { get; set; } = string.Empty;

    public string TitleShown => string.IsNullOrWhiteSpace(Title) ? Path.GetFileNameWithoutExtension(Name) : Title;

    /// <summary>The rectangle it takes up on the paper.</summary>
    public BoundingBox2D PaperBounds => new(Centre.X - Width / 2, Centre.Y - Height / 2, Centre.X + Width / 2, Centre.Y + Height / 2);

    /// <summary>What it takes up with its title under it.</summary>
    public BoundingBox2D Footprint => ShowTitle ? new BoundingBox2D(PaperBounds.MinX, PaperBounds.MinY - Viewport.TitleBand, PaperBounds.MaxX, PaperBounds.MaxY) : PaperBounds;

    public bool Contains(Point2D paper) =>
        paper.X >= PaperBounds.MinX && paper.X <= PaperBounds.MaxX && paper.Y >= PaperBounds.MinY && paper.Y <= PaperBounds.MaxY;

    public SheetImage Copy() => new()
    {
        Id = Id, Name = Name, Data = Data, PixelWidth = PixelWidth, PixelHeight = PixelHeight, Centre = Centre, Width = Width, ShowTitle = ShowTitle, Title = Title
    };

    /// <summary>What kind of picture a file's bytes hold: "png", "jpeg", or null for neither.</summary>
    public static string? KindOf(byte[] data) =>
        data.Length > 8 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47 ? "png"
        : data.Length > 3 && data[0] == 0xFF && data[1] == 0xD8 ? "jpeg"
        : null;

    /// <summary>A PNG's or a JPEG's size in pixels, read off its header; null for anything else.</summary>
    public static (int Width, int Height)? SizeOf(byte[] data)
    {
        switch (KindOf(data))
        {
            case "png" when data.Length >= 24:
                return (Big(data, 16), Big(data, 20));

            case "jpeg":
            {
                // Through the markers to the start of a frame, which says how high and wide it is.
                var at = 2;
                while (at + 9 < data.Length)
                {
                    if (data[at] != 0xFF) return null;
                    var marker = data[at + 1];
                    if (marker == 0xFF)
                    {
                        at++;
                        continue;
                    }

                    var length = data[at + 2] << 8 | data[at + 3];
                    if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
                        return (data[at + 7] << 8 | data[at + 8], data[at + 5] << 8 | data[at + 6]);
                    at += 2 + length;
                }

                return null;
            }

            default:
                return null;
        }

        static int Big(byte[] bytes, int at) => bytes[at] << 24 | bytes[at + 1] << 16 | bytes[at + 2] << 8 | bytes[at + 3];
    }

    /// <summary>A picture of these bytes, sized from them; null where they are not a PNG or a JPEG.</summary>
    public static SheetImage? From(string name, byte[] data)
    {
        if (SizeOf(data) is not var (width, height) || width <= 0 || height <= 0) return null;
        return new SheetImage { Name = name, Data = data, PixelWidth = width, PixelHeight = height };
    }
}
