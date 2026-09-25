using System.Windows.Media;

namespace BIMDesigner.UI.Rendering;

/// <summary>
/// Every colour a drawing is made of, in one place, so the same drawing code can produce a
/// dark editing canvas and a white sheet.
///
/// The alternative - branching on "am I on paper?" inside each draw call - would put that
/// question in fifty places and guarantee that some of them eventually answered it
/// differently. A palette makes the question something the surface answers once.
///
/// Material colours are deliberately not here. A brick is the same colour on screen and on
/// paper, because it is a property of the brick rather than of the drawing.
/// </summary>
public sealed class DrawingPalette
{
    public required Color WallOutline { get; init; }
    public required Color LayerSeparator { get; init; }
    public required Color Selected { get; init; }
    public required Color Preview { get; init; }
    public required Color LocationLine { get; init; }

    public required Color Opening { get; init; }
    public required Color Swing { get; init; }
    public required Color Glass { get; init; }

    /// <summary>The line a component is drawn with in plan, and the fill inside its footprint.</summary>
    public required Color Component { get; init; }
    public required Color ComponentFill { get; init; }

    public required Color RoomOutline { get; init; }
    public required Color RoomFill { get; init; }
    public required Color RoomFillSelected { get; init; }
    public required Color RoomTagText { get; init; }
    public required Color SlabOutline { get; init; }

    public required Color GridLine { get; init; }
    public required Color BubbleFill { get; init; }
    public required Color GridText { get; init; }

    public required Color SectionLine { get; init; }
    public required Color SectionText { get; init; }

    public required Color Dimension { get; init; }
    public required Color DimensionText { get; init; }
    public required Color LooseDimension { get; init; }
    public required Color LooseDimensionText { get; init; }

    public required Color Leader { get; init; }
    public required Color TagFill { get; init; }
    public required Color TagText { get; init; }
    public required Color LabelBackdrop { get; init; }
    public required Color WallLabel { get; init; }
    public required Color Unenclosed { get; init; }

    public required Color SectionCut { get; init; }
    public required Color SectionSeen { get; init; }
    public required Color LevelLine { get; init; }
    public required Color GroundLine { get; init; }
    public required Color LevelText { get; init; }

    /// <summary>The dark editing canvas: light lines on a near-black ground.</summary>
    public static readonly DrawingPalette Screen = new()
    {
        WallOutline = Color.FromRgb(0xE6, 0xE9, 0xEE),
        LayerSeparator = Color.FromRgb(0x4A, 0x50, 0x5A),
        Selected = Color.FromRgb(0x5A, 0xAB, 0xFF),
        Preview = Color.FromRgb(0x5A, 0xAB, 0xFF),
        LocationLine = Color.FromRgb(0xFF, 0xC4, 0x4D),

        Opening = Color.FromRgb(0xE6, 0xE9, 0xEE),
        Swing = Color.FromRgb(0xB6, 0xBE, 0xC9),
        Glass = Color.FromRgb(0x7A, 0xC8, 0xE8),
        Component = Color.FromRgb(0x8A, 0x93, 0xA1),
        ComponentFill = Color.FromArgb(0x26, 0x8A, 0x93, 0xA1),

        RoomOutline = Color.FromRgb(0x5A, 0xAB, 0xFF),
        RoomFill = Color.FromArgb(0x16, 0x5A, 0xAB, 0xFF),
        RoomFillSelected = Color.FromArgb(0x30, 0x5A, 0xAB, 0xFF),
        RoomTagText = Color.FromRgb(0xC8, 0xDA, 0xEE),
        SlabOutline = Color.FromRgb(0x6E, 0x78, 0x86),

        GridLine = Color.FromRgb(0x9B, 0x8F, 0xB8),
        BubbleFill = Color.FromRgb(0x1B, 0x1E, 0x26),
        GridText = Color.FromRgb(0xCB, 0xBF, 0xE4),

        SectionLine = Color.FromRgb(0xE8, 0x8B, 0x5A),
        SectionText = Color.FromRgb(0xF2, 0xC0, 0x9E),

        Dimension = Color.FromRgb(0x7F, 0xD4, 0xA8),
        DimensionText = Color.FromRgb(0xA8, 0xE8, 0xC6),
        LooseDimension = Color.FromRgb(0xD8, 0xA6, 0x5C),
        LooseDimensionText = Color.FromRgb(0xE8, 0xC2, 0x8A),

        Leader = Color.FromRgb(0x9A, 0xA4, 0xB2),
        TagFill = Color.FromRgb(0x25, 0x2B, 0x36),
        TagText = Color.FromRgb(0xE6, 0xE9, 0xEE),
        LabelBackdrop = Color.FromArgb(0xCC, 0x17, 0x1A, 0x21),
        WallLabel = Colors.White,
        Unenclosed = Colors.OrangeRed,

        SectionCut = Color.FromRgb(0xE6, 0xE9, 0xEE),
        SectionSeen = Color.FromRgb(0x5E, 0x68, 0x76),
        LevelLine = Color.FromRgb(0x6E, 0x7C, 0x8C),
        GroundLine = Color.FromRgb(0x4A, 0x52, 0x5E),
        LevelText = Color.FromRgb(0xA8, 0xB4, 0xC2)
    };

    /// <summary>
    /// The storey below, shown faintly under the one being drawn.
    ///
    /// An underlay has to be present enough to set out against and quiet enough that it is
    /// never mistaken for the drawing. Everything is a low-contrast blue-grey: near enough to
    /// the background to recede, far enough from the drawing's own ink to be obviously not it.
    /// </summary>
    public static readonly DrawingPalette Underlay = new()
    {
        WallOutline = Color.FromRgb(0x3E, 0x4A, 0x5C),
        LayerSeparator = Color.FromRgb(0x2A, 0x32, 0x3E),
        Selected = Color.FromRgb(0x3E, 0x4A, 0x5C),
        Preview = Color.FromRgb(0x3E, 0x4A, 0x5C),
        LocationLine = Color.FromRgb(0x3E, 0x4A, 0x5C),

        Opening = Color.FromRgb(0x38, 0x43, 0x53),
        Swing = Color.FromRgb(0x2E, 0x38, 0x46),
        Glass = Color.FromRgb(0x30, 0x44, 0x52),
        Component = Color.FromRgb(0x4A, 0x56, 0x66),
        ComponentFill = Color.FromArgb(0x2A, 0x4A, 0x56, 0x66),

        RoomOutline = Color.FromRgb(0x2C, 0x36, 0x44),
        RoomFill = Color.FromArgb(0x00, 0, 0, 0),
        RoomFillSelected = Color.FromArgb(0x00, 0, 0, 0),
        RoomTagText = Color.FromRgb(0x3A, 0x45, 0x54),
        SlabOutline = Color.FromRgb(0x2C, 0x36, 0x44),

        GridLine = Color.FromRgb(0x33, 0x2E, 0x40),
        BubbleFill = Color.FromRgb(0x17, 0x1A, 0x21),
        GridText = Color.FromRgb(0x3A, 0x35, 0x48),

        SectionLine = Color.FromRgb(0x4A, 0x36, 0x2A),
        SectionText = Color.FromRgb(0x4A, 0x36, 0x2A),

        Dimension = Color.FromRgb(0x2C, 0x40, 0x36),
        DimensionText = Color.FromRgb(0x2C, 0x40, 0x36),
        LooseDimension = Color.FromRgb(0x40, 0x36, 0x28),
        LooseDimensionText = Color.FromRgb(0x40, 0x36, 0x28),

        Leader = Color.FromRgb(0x30, 0x36, 0x3E),
        TagFill = Color.FromRgb(0x17, 0x1A, 0x21),
        TagText = Color.FromRgb(0x3A, 0x43, 0x50),
        LabelBackdrop = Color.FromArgb(0x00, 0, 0, 0),
        WallLabel = Color.FromRgb(0x30, 0x38, 0x44),
        Unenclosed = Color.FromRgb(0x50, 0x34, 0x2A),

        SectionCut = Color.FromRgb(0x3E, 0x4A, 0x5C),
        SectionSeen = Color.FromRgb(0x2A, 0x32, 0x3E),
        LevelLine = Color.FromRgb(0x2C, 0x34, 0x3E),
        GroundLine = Color.FromRgb(0x2C, 0x34, 0x3E),
        LevelText = Color.FromRgb(0x33, 0x3B, 0x46)
    };

    /// <summary>The storey below on a white drawing area: pale grey-blue, receding into the white.</summary>
    public static readonly DrawingPalette UnderlayLight = new()
    {
        WallOutline = Color.FromRgb(0xB4, 0xBE, 0xCB),
        LayerSeparator = Color.FromRgb(0xD4, 0xDA, 0xE2),
        Selected = Color.FromRgb(0xB4, 0xBE, 0xCB),
        Preview = Color.FromRgb(0xB4, 0xBE, 0xCB),
        LocationLine = Color.FromRgb(0xB4, 0xBE, 0xCB),

        Opening = Color.FromRgb(0xBC, 0xC5, 0xD0),
        Swing = Color.FromRgb(0xCC, 0xD3, 0xDC),
        Glass = Color.FromRgb(0xBC, 0xD2, 0xE0),
        Component = Color.FromRgb(0x9A, 0xA4, 0xB0),
        ComponentFill = Color.FromArgb(0x1E, 0x9A, 0xA4, 0xB0),

        RoomOutline = Color.FromRgb(0xCC, 0xD4, 0xDE),
        RoomFill = Color.FromArgb(0x00, 0, 0, 0),
        RoomFillSelected = Color.FromArgb(0x00, 0, 0, 0),
        RoomTagText = Color.FromRgb(0xBC, 0xC5, 0xD0),
        SlabOutline = Color.FromRgb(0xCC, 0xD4, 0xDE),

        GridLine = Color.FromRgb(0xD0, 0xCA, 0xDE),
        BubbleFill = Colors.White,
        GridText = Color.FromRgb(0xC0, 0xB8, 0xD2),

        SectionLine = Color.FromRgb(0xE0, 0xC8, 0xBA),
        SectionText = Color.FromRgb(0xE0, 0xC8, 0xBA),

        Dimension = Color.FromRgb(0xBC, 0xD4, 0xC8),
        DimensionText = Color.FromRgb(0xBC, 0xD4, 0xC8),
        LooseDimension = Color.FromRgb(0xDC, 0xCC, 0xB0),
        LooseDimensionText = Color.FromRgb(0xDC, 0xCC, 0xB0),

        Leader = Color.FromRgb(0xC8, 0xCE, 0xD6),
        TagFill = Colors.White,
        TagText = Color.FromRgb(0xBC, 0xC5, 0xD0),
        LabelBackdrop = Color.FromArgb(0x00, 0, 0, 0),
        WallLabel = Color.FromRgb(0xC0, 0xC8, 0xD2),
        Unenclosed = Color.FromRgb(0xE4, 0xC4, 0xB8),

        SectionCut = Color.FromRgb(0xB4, 0xBE, 0xCB),
        SectionSeen = Color.FromRgb(0xD4, 0xDA, 0xE2),
        LevelLine = Color.FromRgb(0xCC, 0xD4, 0xDE),
        GroundLine = Color.FromRgb(0xCC, 0xD4, 0xDE),
        LevelText = Color.FromRgb(0xC4, 0xCC, 0xD6)
    };

    /// <summary>
    /// Paper: dark lines on white, as a drawing prints.
    ///
    /// The weights are the same as on screen - what changes is only the ink. A drawing that
    /// looked different on the sheet from the way it looked in the editor would make the
    /// editor a poor place to check a drawing, which is most of what an editor is for.
    /// </summary>
    public static readonly DrawingPalette Paper = new()
    {
        WallOutline = Color.FromRgb(0x1A, 0x1A, 0x1A),
        LayerSeparator = Color.FromRgb(0x77, 0x77, 0x77),
        Selected = Color.FromRgb(0x1D, 0x74, 0xC8),
        Preview = Color.FromRgb(0x1D, 0x74, 0xC8),
        LocationLine = Color.FromRgb(0xB0, 0x7A, 0x00),

        Opening = Color.FromRgb(0x1A, 0x1A, 0x1A),
        Swing = Color.FromRgb(0x5A, 0x5A, 0x5A),
        Glass = Color.FromRgb(0x24, 0x6E, 0x94),

        Component = Color.FromRgb(0x33, 0x33, 0x33),
        ComponentFill = Color.FromArgb(0x12, 0x33, 0x33, 0x33),

        RoomOutline = Color.FromRgb(0x1D, 0x74, 0xC8),
        RoomFill = Color.FromArgb(0x14, 0x1D, 0x74, 0xC8),
        RoomFillSelected = Color.FromArgb(0x2E, 0x1D, 0x74, 0xC8),
        RoomTagText = Color.FromRgb(0x25, 0x2C, 0x36),
        SlabOutline = Color.FromRgb(0x8A, 0x92, 0x9C),

        GridLine = Color.FromRgb(0x6B, 0x5B, 0x8E),
        BubbleFill = Colors.White,
        GridText = Color.FromRgb(0x46, 0x39, 0x63),

        SectionLine = Color.FromRgb(0xB4, 0x5A, 0x22),
        SectionText = Color.FromRgb(0x8A, 0x42, 0x16),

        Dimension = Color.FromRgb(0x1D, 0x5E, 0x40),
        DimensionText = Color.FromRgb(0x16, 0x4A, 0x32),
        LooseDimension = Color.FromRgb(0x9A, 0x6A, 0x1E),
        LooseDimensionText = Color.FromRgb(0x7E, 0x55, 0x13),

        Leader = Color.FromRgb(0x5A, 0x60, 0x68),
        TagFill = Colors.White,
        TagText = Color.FromRgb(0x1A, 0x1A, 0x1A),
        LabelBackdrop = Color.FromArgb(0xD8, 0xFF, 0xFF, 0xFF),
        WallLabel = Color.FromRgb(0x2A, 0x2A, 0x2A),
        Unenclosed = Color.FromRgb(0xB3, 0x2D, 0x12),

        SectionCut = Color.FromRgb(0x1A, 0x1A, 0x1A),
        SectionSeen = Color.FromRgb(0x94, 0x9C, 0xA6),
        LevelLine = Color.FromRgb(0x6A, 0x74, 0x80),
        GroundLine = Color.FromRgb(0x3A, 0x40, 0x48),
        LevelText = Color.FromRgb(0x3A, 0x42, 0x4C)
    };
}
