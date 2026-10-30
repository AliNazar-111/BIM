using System.Windows;
using System.Windows.Media;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Mep;

namespace BIMDesigner.UI.Rendering;

/// <summary>
/// What each piece of a drawing was drawn for, kept on the drawing it was drawn into - so a DWG or a
/// DXF puts each element on the layer a CAD user looks for it on, named as the US National CAD
/// Standard's layer guidelines (AIA) name them: walls A-WALL, doors A-DOOR, windows A-GLAZ,
/// dimensions A-ANNO-DIMS, cold water P-DOMW-CWTR; and its fills beside it on its -PATT layer.
/// </summary>
public static class CadLayers
{
    /// <summary>The element a drawing was drawn for.</summary>
    public static readonly DependencyProperty ElementProperty =
        DependencyProperty.RegisterAttached("Element", typeof(Element), typeof(CadLayers), new PropertyMetadata(null));

    public static Element? GetElement(DependencyObject drawing) => (Element?)drawing.GetValue(ElementProperty);

    public static void SetElement(DependencyObject drawing, Element? element) => drawing.SetValue(ElementProperty, element);

    /// <summary>The layer a drawing goes on where it is not its element's own - a room's tag, not its fill.</summary>
    public static readonly DependencyProperty LayerProperty =
        DependencyProperty.RegisterAttached("Layer", typeof(string), typeof(CadLayers), new PropertyMetadata(null));

    public static string? GetLayer(DependencyObject drawing) => (string?)drawing.GetValue(LayerProperty);

    /// <summary>A drawing of one element: drawn into a group of its own and the element kept on it.</summary>
    public static void Draw(DrawingContext dc, Element element, Action<DrawingContext> draw, string? layer = null)
    {
        var group = new DrawingGroup();
        using (var inner = group.Open()) draw(inner);
        SetElement(group, element);
        if (layer is not null) group.SetValue(LayerProperty, layer);
        dc.DrawDrawing(group);
    }

    /// <summary>The layer an element is drawn on.</summary>
    public static string Of(Element element) => element switch
    {
        Pipe pipe => pipe.Kind switch
        {
            SystemKind.ColdWater => "P-DOMW-CWTR",
            SystemKind.HotWater => "P-DOMW-HWTR",
            SystemKind.Heating => "M-HYDR-PIPE",
            SystemKind.Foul => "P-SANR-PIPE",
            SystemKind.Rainwater => "P-STRM-PIPE",
            SystemKind.Gas => "P-GAS",
            SystemKind.Sprinkler => "F-SPRN-PIPE",
            _ => "P-PIPE"
        },
        Column { Structural: true } => "S-COLS",
        _ => element.Category switch
        {
            BuiltInCategory.Walls or BuiltInCategory.Parts or BuiltInCategory.WallOpenings => "A-WALL",
            BuiltInCategory.WallSweeps or BuiltInCategory.WallReveals => "A-WALL-OTLN",
            BuiltInCategory.CurtainPanels or BuiltInCategory.CurtainWallMullions => "A-GLAZ-CWMG",
            BuiltInCategory.Doors => "A-DOOR",
            BuiltInCategory.Windows => "A-GLAZ",
            BuiltInCategory.Floors or BuiltInCategory.SlabEdges => "A-FLOR",
            BuiltInCategory.ShaftOpenings => "A-FLOR-OTLN",
            BuiltInCategory.Roofs or BuiltInCategory.Fascias or BuiltInCategory.Gutters or BuiltInCategory.RoofSoffits or BuiltInCategory.Eaves => "A-ROOF",
            BuiltInCategory.RoofWindows => "A-ROOF-SKYL",
            BuiltInCategory.Chimneys => "A-ROOF-CHIM",
            BuiltInCategory.Ceilings => "A-CLNG",
            BuiltInCategory.Columns => "A-COLS",
            BuiltInCategory.StructuralColumns => "S-COLS",
            BuiltInCategory.StructuralFraming => "S-BEAM",
            BuiltInCategory.StructuralFoundations => "S-FNDN",
            BuiltInCategory.Grids => "S-GRID",
            BuiltInCategory.Stairs => "A-FLOR-STRS",
            BuiltInCategory.Ramps => "A-FLOR-LEVL",
            BuiltInCategory.Railings => "A-FLOR-HRAL",
            BuiltInCategory.Casework => "A-FLOR-CASE",
            BuiltInCategory.Rooms or BuiltInCategory.RoomSeparationLines => "A-AREA",
            BuiltInCategory.Furniture => "A-FURN",
            BuiltInCategory.SpecialtyEquipment => "A-EQPM",
            BuiltInCategory.Masses => "A-MASS",
            BuiltInCategory.Entourage => "A-ENTO",
            BuiltInCategory.PlumbingFixtures => "P-FIXT",
            BuiltInCategory.Downpipes => "P-STRM-RISR",
            BuiltInCategory.RoofDrains => "P-STRM-DRAN",
            BuiltInCategory.Drainage => "P-SANR-EQPM",
            BuiltInCategory.Pipes or BuiltInCategory.PipingSystems => "P-PIPE",
            BuiltInCategory.MechanicalEquipment => "M-EQPM",
            BuiltInCategory.Ducts or BuiltInCategory.DuctSystems => "M-HVAC-DUCT",
            BuiltInCategory.AirTerminals => "M-HVAC-DIFF",
            BuiltInCategory.LightingFixtures => "E-LITE",
            BuiltInCategory.LightingDevices => "E-LITE-SWCH",
            BuiltInCategory.ElectricalFixtures => "E-POWR",
            BuiltInCategory.ElectricalEquipment => "E-POWR-EQPM",
            BuiltInCategory.ElectricalCircuits => "E-POWR-CIRC",
            BuiltInCategory.FireAlarmDevices => "E-FIRE",
            BuiltInCategory.Sprinklers => "F-SPRN",
            BuiltInCategory.Topography or BuiltInCategory.Pads => "C-TOPO",
            BuiltInCategory.SpotElevations => "C-TOPO-SPOT",
            BuiltInCategory.PropertyLines => "C-PROP-LINE",
            BuiltInCategory.Parking => "C-PRKG",
            BuiltInCategory.Planting => "L-PLNT",
            BuiltInCategory.Dimensions => "A-ANNO-DIMS",
            BuiltInCategory.Tags or BuiltInCategory.TextNotes or BuiltInCategory.KeynoteTags => "A-ANNO-TEXT",
            BuiltInCategory.Sections or BuiltInCategory.Elevations or BuiltInCategory.Callouts or BuiltInCategory.Views or BuiltInCategory.ModelGroups => "A-ANNO-SYMB",
            BuiltInCategory.RevisionClouds => "A-ANNO-REVS",
            BuiltInCategory.DetailLines => "A-DETL",
            BuiltInCategory.FilledRegions => "A-DETL-PATT",
            _ => "A-GENM"
        }
    };

    /// <summary>A room's name and area, on the layer rooms are identified on.</summary>
    public const string RoomTags = "A-AREA-IDEN";
}
