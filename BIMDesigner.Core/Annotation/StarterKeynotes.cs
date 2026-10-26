using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;

namespace BIMDesigner.Core.Annotation;

/// <summary>
/// The starter keynotes put on the template's types, so that a keynote placed on a fresh
/// project reads something rather than "?": a door by its kind, a window, a wall, floor, roof or
/// ceiling by its make-up, a stair, a beam or column by what it is made of, a fitting by what it
/// is. Only where the starter set's words fit the type - a glazed or louvred door is not a
/// flush painted one, a clay tile roof not concrete tiles - and never over a key already given.
/// </summary>
public static class StarterKeynotes
{
    public static void Apply(BimDocument document)
    {
        foreach (var type in document.ElementTypes.Where(type => string.IsNullOrEmpty(type.Keynote)))
            if (KeyOf(type) is { } key && document.Keynotes.Find(key) is not null)
                type.Keynote = key;

        // A material's own note only where it means the same wherever the material is: mineral
        // wool is in partitions, lofts and cavities alike, and is left for the office to say.
        foreach (var material in document.Materials.Where(material => string.IsNullOrEmpty(material.Keynote)))
        {
            var key = material.Name switch
            {
                var name when name.StartsWith("Brick") => "2.5.01",
                "Aircrete Block" => "2.5.03",
                "Gypsum Plasterboard" => "3.1.01",
                var name when name.StartsWith("Roof Slate") => "2.3.02",
                "Roof Tile, Concrete Interlocking" => "2.3.01",
                _ => null
            };
            if (key is not null && document.Keynotes.Find(key) is not null) material.Keynote = key;
        }
    }

    /// <summary>The starter set's key for a type of the template, or null where none of its notes says what the type is.</summary>
    public static string? KeyOf(ElementType type)
    {
        bool Named(params string[] words) => words.Any(word => type.Name.Contains(word, StringComparison.OrdinalIgnoreCase));
        bool Steel() => type.Name.StartsWith("UB ") || type.Name.StartsWith("UC ") || type.Name.StartsWith("SHS ") || type.Name.StartsWith("CHS ")
                        || type.Name.Length > 1 && type.Name[0] == 'W' && char.IsDigit(type.Name[1]);

        return type switch
        {
            DoorType when Named("Fire Door") => "2.8.02",
            DoorType { Function: DoorFunction.Exterior } => "2.6.02",
            DoorType when Named("Single -", "Double -", "Sliding") => "2.8.01",
            WindowType when !Named("Louvred") => "2.6.01",
            WallType when Named("Brick") => "2.5.01",
            WallType when Named("Partition", "Stud Wall") => "2.7.01",
            FloorType when Named("Slab") => "1.1.03",
            FloorType when Named("Timber") => "2.2.01",
            RoofType when Named("Concrete Interlocking") => "2.3.01",
            RoofType when Named("Slate") => "2.3.02",
            RoofType when Named("Flat") => "2.3.03",
            CeilingType when Named("Soffit") => "2.3.06",
            CeilingType when Named("Plasterboard") => "3.3.01",
            StairType when Named("Timber") => "2.4.01",
            FasciaType or SoffitType => "2.3.06",
            GutterType => "2.3.07",
            RailingType when Named("Close Board") => "8.4.01",
            BeamType when Steel() => "2.1.02",
            BeamType when Named("Joist") => "2.2.01",
            ColumnType when Steel() => "2.1.01",
            ColumnType when Named("Timber Post") => "2.1.03",
            ComponentType component => component.Form switch
            {
                ComponentForm.Toilet or ComponentForm.WallHungToilet => "5.1.01",
                ComponentForm.Basin => "5.1.02",
                ComponentForm.Bath or ComponentForm.Shower => "5.1.03",
                ComponentForm.Radiator => "5.6.01",
                ComponentForm.ExtractFan => "5.7.01",
                ComponentForm.Socket => "5.8.01",
                ComponentForm.CeilingLight or ComponentForm.WallLight => "5.8.02",
                ComponentForm.SmokeAlarm => "5.11.01",
                ComponentForm.Counter or ComponentForm.WallUnit or ComponentForm.TallUnit or ComponentForm.OvenHousing => "4.1.01",
                ComponentForm.Wardrobe => "4.1.02",
                _ => null
            },
            _ => null
        };
    }
}
