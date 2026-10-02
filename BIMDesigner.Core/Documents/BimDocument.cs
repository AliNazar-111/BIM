using System.Collections.ObjectModel;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;

namespace BIMDesigner.Core.Documents;

/// <summary>Project-wide information (specification section 7).</summary>
public sealed class ProjectInformation
{
    public string Name { get; set; } = "Untitled Project";
    public string Number { get; set; } = string.Empty;
    public string Client { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string BuildingType { get; set; } = string.Empty;
}

/// <summary>
/// One project: its elements, the types they are built from, the levels they sit on and the
/// materials those types are made of.
///
/// This is the single source the plan view, and later the section, elevation, 3D view,
/// schedules and IFC export all read. Nothing owns a private copy of the model.
/// </summary>
public sealed class BimDocument
{
    private readonly Dictionary<Guid, ElementType> _types = new();
    private readonly Dictionary<Guid, Material> _materials = new();
    private readonly ObservableCollection<Level> _levels = new();

    public ProjectInformation ProjectInformation { get; } = new();

    /// <summary>What each view leaves out. Views not listed show everything.</summary>
    public Views.ViewSettings ViewSettings { get; } = new();

    /// <summary>Everything placed in the project. Observable so views refresh themselves.</summary>
    public ObservableCollection<Element> Elements { get; } = new();

    public ObservableCollection<Level> Levels => _levels;

    public IReadOnlyCollection<Material> Materials => _materials.Values;

    public IReadOnlyCollection<ElementType> ElementTypes => _types.Values;

    public IEnumerable<Wall> Walls => Elements.OfType<Wall>();

    public IEnumerable<Opening> Openings => Elements.OfType<Opening>();

    // ---- lookups ---------------------------------------------------------------

    public Level? FindLevel(Guid id) => _levels.FirstOrDefault(level => level.Id == id);

    public Material? FindMaterial(Guid id) => _materials.GetValueOrDefault(id);

    public T? FindType<T>(Guid id) where T : ElementType => _types.GetValueOrDefault(id) as T;

    public IEnumerable<T> TypesOf<T>() where T : ElementType => _types.Values.OfType<T>();

    /// <summary>The wall type an element uses, or the first available type as a fallback.</summary>
    public WallType? GetWallType(Wall wall)
    {
        // A curtain wall joins, bounds rooms and is drawn round as the thin wall its mullions make.
        if (FindType<CurtainWallType>(wall.TypeId) is { } curtain) return curtain.Body;

        // A stacked wall is drawn in plan as the tier the plan cuts through.
        if (FindType<StackedWallType>(wall.TypeId) is { } stacked)
            return stacked.TierAt(this, wall.GetHeight(this), StackedWallType.PlanCutHeight)
                   ?? TypesOf<WallType>().FirstOrDefault();

        return FindType<WallType>(wall.TypeId) ?? TypesOf<WallType>().FirstOrDefault();
    }

    /// <summary>
    /// The wall type a type id is drawn as in plan: itself, or for a stacked type the tier a
    /// wall of the given height is cut through. What the wall tool previews with.
    /// </summary>
    public WallType? PlanWallType(Guid typeId, double height = 3000) =>
        FindType<StackedWallType>(typeId) is { } stacked
            ? stacked.TierAt(this, height, StackedWallType.PlanCutHeight)
            : FindType<CurtainWallType>(typeId)?.Body ?? FindType<WallType>(typeId);

    /// <summary>Whether a wall is a curtain wall.</summary>
    public bool IsCurtainWall(Wall wall) => FindType<CurtainWallType>(wall.TypeId) is not null;

    /// <summary>
    /// Every construction a wall is built of, with the elevations it runs between: one for an
    /// ordinary wall, one per tier for a stacked wall. What the 3D model, sections and
    /// quantities build from.
    /// </summary>
    public IReadOnlyList<(WallType Type, double Bottom, double Top)> GetWallTiers(Wall wall)
    {
        var bottom = wall.GetBaseElevation(this);
        var height = wall.GetHeight(this);

        if (FindType<StackedWallType>(wall.TypeId) is { } stacked)
            return stacked.Layout(this, height).Select(tier => (tier.Type, bottom + tier.Bottom, bottom + tier.Top)).ToList();

        return GetWallType(wall) is { } type
            ? new[] { (type, bottom, bottom + height) }
            : Array.Empty<(WallType, double, double)>();
    }

    // ---- content ---------------------------------------------------------------

    public void AddLevel(Level level) => _levels.Add(level);

    /// <summary>
    /// Adds a level, keeping the collection ordered by elevation.
    ///
    /// The order is what every level picker and the project browser show, and a storey list
    /// that is not in height order is one nobody can read.
    /// </summary>
    public void InsertLevel(Level level)
    {
        var index = 0;
        while (index < _levels.Count && _levels[index].Elevation <= level.Elevation) index++;

        _levels.Insert(index, level);
    }

    public void InsertLevel(int index, Level level) =>
        _levels.Insert(Math.Clamp(index, 0, _levels.Count), level);

    public bool RemoveLevel(Level level) => _levels.Remove(level);

    public int IndexOfLevel(Level level) => _levels.IndexOf(level);

    public void AddMaterial(Material material) => _materials[material.Id] = material;

    public void AddType(ElementType type) => _types[type.Id] = type;

    public bool RemoveType(ElementType type) => _types.Remove(type.Id);

    /// <summary>The elements built from a type - what editing it changes, and what stops it being deleted.</summary>
    public IEnumerable<Element> ElementsOfType(ElementType type) => Elements.Where(element => element.TypeId == type.Id);

    public void Add(Element element) => Elements.Add(element);

    public bool Remove(Element element) => Elements.Remove(element);

    /// <summary>
    /// Adds template types for any category the project has none of, along with whatever
    /// materials those types need.
    ///
    /// A project saved before a category existed would otherwise have no way to use it: the
    /// tool is enabled, the type list is empty, and nothing can be placed. Categories that
    /// already have types are left alone - this fills gaps, it does not impose a template on
    /// a project that has made its own choices.
    /// </summary>
    public void EnsureDefaultTypes()
    {
        var template = CreateDefault();
        var present = _types.Values.Select(type => type.Category).ToHashSet();

        // Curtain walls share their category with walls, so a project from before they existed
        // has walls but no curtain wall types to draw one with.
        var hasCurtain = _types.Values.OfType<CurtainWallType>().Any();

        // Doors and windows are the library a project draws from, and that library grows: a
        // project made before sliding windows or curtain wall doors existed should still be
        // able to place one. They come in by name, so a project keeps its own types and gains
        // only what it has never had.
        var known = _types.Values.OfType<OpeningType>().Select(type => type.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Component families are a library in the same way, and one that grows: a project made
        // before a family existed should still be able to place it, while keeping its own.
        var families = _types.Values.OfType<ComponentType>().Select(type => type.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var columnTypes = _types.Values.OfType<ColumnType>().Select(type => type.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Fascias, gutters and soffits are a library too, added to as it grows - and so are
        // roofs: a project made when the only roof was a flat one gains a pitched build-up.
        var roofEdgeTypes = _types.Values.Where(type => type is FasciaType or GutterType or SoffitType)
            .Select(type => type.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var roofTypes = _types.Values.OfType<RoofType>().Select(type => type.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var roofWindowTypes = _types.Values.OfType<RoofWindowType>().Select(type => type.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var type in template.ElementTypes.Where(type => type switch
                 {
                     CurtainWallType => !hasCurtain,
                     OpeningType opening => !known.Contains(opening.Name),
                     ComponentType component => !families.Contains(component.Name),
                     ColumnType column => !columnTypes.Contains(column.Name),
                     FasciaType or GutterType or SoffitType => !roofEdgeTypes.Contains(type.Name),
                     RoofType => !roofTypes.Contains(type.Name),
                     RoofWindowType => !roofWindowTypes.Contains(type.Name),
                     _ => !present.Contains(type.Category)
                 }))
        {
            foreach (var materialId in MaterialsUsedBy(type).ToList())
            {
                if (_materials.ContainsKey(materialId) || template.FindMaterial(materialId) is not { } material) continue;

                // The project's own material of that name, where it has one, rather than a second
                // "Timber, Softwood" beside it.
                if (_materials.Values.FirstOrDefault(own => string.Equals(own.Name, material.Name, StringComparison.OrdinalIgnoreCase)) is { } same &&
                    RepointMaterial(type, materialId, same.Id))
                    continue;

                AddMaterial(material);
            }

            AddType(type);
        }
    }

    /// <summary>Points a type that refers to a material by itself at another; false for a type it cannot be done for here.</summary>
    private static bool RepointMaterial(ElementType type, Guid from, Guid to)
    {
        switch (type)
        {
            case SoffitType soffit when soffit.MaterialId == from:
                soffit.MaterialId = to;
                return true;
            case FasciaType fascia when fascia.MaterialId == from:
                fascia.MaterialId = to;
                return true;
            case GutterType gutter when gutter.MaterialId == from:
                gutter.MaterialId = to;
                return true;
            case RoofWindowType roofWindow when roofWindow.FrameMaterialId == from || roofWindow.GlassMaterialId == from:
                if (roofWindow.FrameMaterialId == from) roofWindow.FrameMaterialId = to;
                if (roofWindow.GlassMaterialId == from) roofWindow.GlassMaterialId = to;
                return true;
            default:
                return false;
        }
    }

    /// <summary>The materials a type's layers refer to, if it is a layered build-up.</summary>
    private static IEnumerable<Guid> MaterialsUsedBy(ElementType type) => type switch
    {
        WallType wall => wall.Structure.Layers.Select(layer => layer.MaterialId),
        CurtainWallType curtain => new[] { curtain.GlassMaterialId, curtain.SolidMaterialId, curtain.MullionMaterialId },
        WallSweepType sweep => new[] { sweep.MaterialId },
        FasciaType fascia => new[] { fascia.MaterialId },
        GutterType gutter => new[] { gutter.MaterialId },
        SoffitType soffit => new[] { soffit.MaterialId },
        RoofWindowType roofWindow => new[] { roofWindow.FrameMaterialId, roofWindow.GlassMaterialId },
        ColumnType column => new[] { column.MaterialId },
        SlabType slab => slab.Structure.Layers.Select(layer => layer.MaterialId),
        _ => Array.Empty<Guid>()
    };

    /// <summary>
    /// A new project from the default template (specification section 12.2): a ground floor
    /// and first floor, a small material palette, and three wall types that between them
    /// exercise single-layer and multi-layer assemblies.
    /// </summary>
    public static BimDocument CreateDefault()
    {
        var document = new BimDocument();

        var ground = new Level { Name = "Ground Floor", Elevation = 0 };
        var first = new Level { Name = "First Floor", Elevation = 3000 };
        document.AddLevel(ground);
        document.AddLevel(first);

        var concrete = new Material("Concrete, Cast In Situ")
        {
            Density = 2400,
            ThermalConductivity = 2.3,
            SurfaceColour = ColourRgb.FromHex("9E9E9E"),
            CutColour = ColourRgb.FromHex("8A8A8A"),
            CostPerCubicMetre = 120m
        };
        var brick = new Material("Brick, Common")
        {
            Density = 1900,
            ThermalConductivity = 0.77,
            SurfaceColour = ColourRgb.FromHex("A8563C"),
            CutColour = ColourRgb.FromHex("9C4F37"),
            CostPerCubicMetre = 180m
        };
        var block = new Material("Concrete Block")
        {
            Density = 1400,
            ThermalConductivity = 0.51,
            SurfaceColour = ColourRgb.FromHex("9A9E93"),
            CutColour = ColourRgb.FromHex("8B8F85"),
            CostPerCubicMetre = 95m
        };
        var insulation = new Material("Rigid Insulation")
        {
            Density = 32,
            ThermalConductivity = 0.022,
            SurfaceColour = ColourRgb.FromHex("E8D24B"),
            CutColour = ColourRgb.FromHex("D8C23B"),
            CostPerCubicMetre = 210m
        };
        var cavity = new Material("Air Cavity")
        {
            Density = 1.2,
            ThermalConductivity = 0.025,
            SurfaceColour = ColourRgb.FromHex("B4BBC3"),
            CutColour = ColourRgb.FromHex("A3ABB5")
        };
        var plaster = new Material("Cement Plaster")
        {
            Density = 1900,
            ThermalConductivity = 0.72,
            SurfaceColour = ColourRgb.FromHex("D6D2C8"),
            CutColour = ColourRgb.FromHex("C6C2B8"),
            CostPerCubicMetre = 140m
        };
        var plasterboard = new Material("Gypsum Plasterboard")
        {
            Density = 800,
            ThermalConductivity = 0.25,
            SurfaceColour = ColourRgb.FromHex("E4E0D6"),
            CutColour = ColourRgb.FromHex("D4D0C6"),
            CostPerCubicMetre = 160m
        };

        foreach (var material in new[] { concrete, brick, block, insulation, cavity, plaster, plasterboard })
            document.AddMaterial(material);

        // A single structural layer - the simplest assembly.
        var generic = new WallType("Generic - 200mm", CompoundStructure.Single(concrete.Id, 200))
        {
            Function = WallFunction.Interior,
            TypeMark = "W1",
            AssemblyCode = "B2010",
            FireRating = "60 min",
            AcousticRating = 45,
            Cost = 85m,
            CoarseScaleFillColour = ColourRgb.FromHex("8A93A1")
        };

        // Exterior cavity wall: brick outer leaf, cavity, insulation, block core, plaster.
        var exterior = new WallType("Exterior - Brick on Block 330mm", new CompoundStructure(
            new MaterialLayer(LayerFunction.Finish1, brick.Id, 100),
            new MaterialLayer(LayerFunction.ThermalAir, cavity.Id, 25, wraps: false),
            new MaterialLayer(LayerFunction.ThermalAir, insulation.Id, 50, wraps: false),
            new MaterialLayer(LayerFunction.Structure, block.Id, 140),
            new MaterialLayer(LayerFunction.Finish2, plaster.Id, 15)))
        {
            Function = WallFunction.Exterior,
            TypeMark = "EW1",
            AssemblyCode = "B2010",
            FireRating = "120 min",
            AcousticRating = 53,
            ThermalResistance = 2.85,
            HeatTransferCoefficient = 0.32,
            Cost = 165m,
            CoarseScaleFillColour = ColourRgb.FromHex("A8846E"),

            // The brick returns round an exposed end, as brickwork does, rather than leaving
            // the cavity and the blockwork on show at a corner.
            WrapAtEnds = WallWrapping.Exterior
        };

        // Lightweight partition: board, stud zone, board.
        var partition = new WallType("Interior - Partition 125mm", new CompoundStructure(
            new MaterialLayer(LayerFunction.Finish2, plasterboard.Id, 12.5),
            new MaterialLayer(LayerFunction.Structure, cavity.Id, 100),
            new MaterialLayer(LayerFunction.Finish2, plasterboard.Id, 12.5)))
        {
            Function = WallFunction.Interior,
            TypeMark = "P1",
            AssemblyCode = "C1010",
            FireRating = "30 min",
            AcousticRating = 38,
            Cost = 48m,
            CoarseScaleFillColour = ColourRgb.FromHex("BFBAAE"),

            // The board returns round an end rather than showing the stud zone.
            WrapAtEnds = WallWrapping.Exterior
        };

        // Doors and windows, sized to the metric ranges used on drawings.
        var singleDoor = new DoorType("Single - 900 x 2100", 900, 2100)
        {
            TypeMark = "D1",
            AssemblyCode = "C1020",
            Operation = DoorOperation.Swing,
            LeafCount = 1,
            PanelMaterial = "Timber, Painted",
            LeafDesign = DoorLeafDesign.Panelled,
            FrameMaterial = "Timber",
            FireRating = "30 min",
            AcousticRating = 29,
            Cost = 320m
        };

        var doubleDoor = new DoorType("Double - 1800 x 2100", 1800, 2100)
        {
            TypeMark = "D2",
            AssemblyCode = "C1020",
            Operation = DoorOperation.DoubleSwing,
            LeafCount = 2,
            PanelMaterial = "Timber, Painted",
            LeafDesign = DoorLeafDesign.Panelled,
            FrameMaterial = "Timber",
            FireRating = "60 min",
            AcousticRating = 32,
            Cost = 640m
        };

        // A twin slider: one leaf fixed, the other running across it. Used exactly where
        // there is no spare wall for a panel to park on.
        var twinSlider = new DoorType("Sliding Twin - 1800 x 2100", 1800, 2100)
        {
            TypeMark = "D3",
            AssemblyCode = "C1020",
            Operation = DoorOperation.Sliding,
            LeafCount = 2,
            PanelMaterial = "Oak, Glazed",
            LeafDesign = DoorLeafDesign.Glazed,
            FrameMaterial = "Oak",
            AcousticRating = 26,
            Cost = 720m
        };

        var singleSlider = new DoorType("Sliding - 900 x 2100", 900, 2100)
        {
            TypeMark = "D4",
            AssemblyCode = "C1020",
            Operation = DoorOperation.Sliding,
            LeafCount = 1,
            PanelMaterial = "Timber, Painted",
            LeafDesign = DoorLeafDesign.Flush,
            FrameMaterial = "Timber",
            AcousticRating = 24,
            Cost = 410m
        };

        // The designs door families come in, as Revit's library shows them: a glazed French pair,
        // a single glazed door with glazing bars, a louvred bi-fold closet door, and a panelled
        // front door under an arched sunburst light.
        var frenchDoor = new DoorType("French Double - 1500 x 2100", 1500, 2100)
        {
            TypeMark = "D5",
            AssemblyCode = "C1020",
            Operation = DoorOperation.DoubleSwing,
            LeafCount = 2,
            LeafDesign = DoorLeafDesign.FrenchGlazed,
            GlazingRows = 4,
            PanelMaterial = "Pine, Glazed",
            FrameMaterial = "Pine",
            Function = DoorFunction.Exterior,
            Cost = 980m
        };

        var glazedDoor = new DoorType("Single Glazed - 800 x 2100", 800, 2100)
        {
            TypeMark = "D6",
            AssemblyCode = "C1020",
            Operation = DoorOperation.Swing,
            LeafDesign = DoorLeafDesign.FrenchGlazed,
            GlazingRows = 4,
            PanelMaterial = "Pine, Glazed",
            FrameMaterial = "Pine",
            Cost = 520m
        };

        var bifold = new DoorType("Bi-fold Louvred - 1600 x 2100", 1600, 2100)
        {
            TypeMark = "D7",
            AssemblyCode = "C1020",
            Operation = DoorOperation.Folding,
            LeafCount = 2,
            LeafDesign = DoorLeafDesign.Louvred,
            PanelMaterial = "Pine",
            FrameMaterial = "Pine",
            Thickness = 60,
            Cost = 460m
        };

        var entrance = new DoorType("Entrance Arched Light - 1000 x 2300", 1000, 2300)
        {
            TypeMark = "D8",
            AssemblyCode = "C1020",
            Operation = DoorOperation.Swing,
            LeafDesign = DoorLeafDesign.ArchedTopLight,
            PanelMaterial = "Walnut",
            FrameMaterial = "Walnut",
            Function = DoorFunction.Exterior,
            FireRating = "30 min",
            TrimWidth = 90,
            Cost = 1450m
        };

        // Curtain wall doors: the glass doors that replace a panel of a curtain wall, as Revit's
        // library has them. One clear pane in a slim anodised frame, no architrave - the
        // mullions round the panel are the frame - and full storey height.
        var curtainSingle = new DoorType("Curtain Wall Sgl Glass - 1000 x 2200", 1000, 2200)
        {
            TypeMark = "CD1",
            AssemblyCode = "C1020",
            Operation = DoorOperation.Swing,
            LeafDesign = DoorLeafDesign.FullGlass,
            CurtainPanel = true,
            PanelMaterial = "Aluminium, Anodised",
            FrameMaterial = "Aluminium, Anodised",
            Function = DoorFunction.Exterior,
            Thickness = 50,
            TrimWidth = 0,
            Cost = 1850m
        };

        var curtainDouble = new DoorType("Curtain Wall Dbl Glass - 1800 x 2200", 1800, 2200)
        {
            TypeMark = "CD2",
            AssemblyCode = "C1020",
            Operation = DoorOperation.DoubleSwing,
            LeafCount = 2,
            LeafDesign = DoorLeafDesign.FullGlass,
            CurtainPanel = true,
            PanelMaterial = "Aluminium, Anodised",
            FrameMaterial = "Aluminium, Anodised",
            Function = DoorFunction.Exterior,
            Thickness = 50,
            TrimWidth = 0,
            Cost = 3200m
        };

        // The sliding entrance of a shop or a lobby: two leaves running apart on one track.
        var curtainSlider = new DoorType("Curtain Wall Sliding Glass - 1800 x 2200", 1800, 2200)
        {
            TypeMark = "CD3",
            AssemblyCode = "C1020",
            Operation = DoorOperation.Sliding,
            LeafCount = 2,
            LeafDesign = DoorLeafDesign.FullGlass,
            CurtainPanel = true,
            PanelMaterial = "Aluminium, Anodised",
            FrameMaterial = "Aluminium, Anodised",
            Function = DoorFunction.Exterior,
            Thickness = 50,
            TrimWidth = 0,
            Cost = 4100m
        };

        var casement = new WindowType("Casement - 1200 x 1200", 1200, 1200)
        {
            TypeMark = "W1",
            AssemblyCode = "B2020",
            Operation = WindowOperation.Casement,
            GlazingType = "Double glazed, low-E",
            FrameMaterial = "uPVC",
            HeatTransferCoefficient = 1.4,
            SolarHeatGainCoefficient = 0.55,
            Cost = 410m
        };

        var picture = new WindowType("Fixed - 2400 x 1500", 2400, 1500)
        {
            TypeMark = "W2",
            AssemblyCode = "B2020",
            Operation = WindowOperation.Fixed,
            GlazingType = "Double glazed, low-E",
            FrameMaterial = "Aluminium, thermally broken",
            HeatTransferCoefficient = 1.6,
            SolarHeatGainCoefficient = 0.48,
            Cost = 890m
        };

        // The rest of the window library: one of each way a window opens, and the divided
        // lights that tell a Georgian sash from a sheet of glass.
        var casementDouble = new WindowType("Casement Double - 1800 x 1350", 1800, 1350)
        {
            TypeMark = "W3",
            AssemblyCode = "B2020",
            Operation = WindowOperation.Casement,
            GlazingType = "Double glazed, low-E",
            FrameMaterial = "Timber, Painted",
            HeatTransferCoefficient = 1.5,
            Cost = 620m
        };

        var georgian = new WindowType("Casement Georgian - 900 x 1500", 900, 1500)
        {
            TypeMark = "W4",
            AssemblyCode = "B2020",
            Operation = WindowOperation.Casement,
            GlazingRows = 3,
            GlazingColumns = 2,
            GlazingType = "Double glazed",
            FrameMaterial = "Timber, Painted",
            HeatTransferCoefficient = 1.8,
            Cost = 540m
        };

        var awning = new WindowType("Awning - 1200 x 600", 1200, 600)
        {
            TypeMark = "W5",
            AssemblyCode = "B2020",
            Operation = WindowOperation.Awning,
            GlazingType = "Double glazed",
            FrameMaterial = "uPVC",
            HeatTransferCoefficient = 1.5,
            Cost = 280m
        };

        var hopper = new WindowType("Hopper - 900 x 600", 900, 600)
        {
            TypeMark = "W6",
            AssemblyCode = "B2020",
            Operation = WindowOperation.Hopper,
            GlazingType = "Double glazed, obscured",
            FrameMaterial = "uPVC",
            HeatTransferCoefficient = 1.6,
            Cost = 240m
        };

        var slidingWindow = new WindowType("Sliding - 1800 x 1200", 1800, 1200)
        {
            TypeMark = "W7",
            AssemblyCode = "B2020",
            Operation = WindowOperation.Sliding,
            GlazingType = "Double glazed, low-E",
            FrameMaterial = "Aluminium, thermally broken",
            HeatTransferCoefficient = 1.7,
            Cost = 700m
        };

        var tiltAndTurn = new WindowType("Tilt and Turn - 1000 x 1400", 1000, 1400)
        {
            TypeMark = "W8",
            AssemblyCode = "B2020",
            Operation = WindowOperation.TiltAndTurn,
            GlazingType = "Triple glazed",
            FrameMaterial = "uPVC",
            HeatTransferCoefficient = 1.1,
            Cost = 660m
        };

        var doubleHung = new WindowType("Double Hung - 900 x 1500", 900, 1500)
        {
            TypeMark = "W9",
            AssemblyCode = "B2020",
            Operation = WindowOperation.DoubleHung,
            GlazingRows = 2,
            GlazingColumns = 2,
            GlazingType = "Double glazed",
            FrameMaterial = "Timber, Painted",
            HeatTransferCoefficient = 1.9,
            Cost = 720m
        };

        var louvred = new WindowType("Louvred - 600 x 1200", 600, 1200)
        {
            TypeMark = "W10",
            AssemblyCode = "B2020",
            Operation = WindowOperation.Louvred,
            GlazingType = "Single glazed, obscured",
            FrameMaterial = "Aluminium, Anodised",
            HeatTransferCoefficient = 4.2,
            SolarHeatGainCoefficient = 0.7,
            Cost = 310m
        };

        var bay = new WindowType("Bay - 2400 x 1500", 2400, 1500)
        {
            TypeMark = "W11",
            AssemblyCode = "B2020",
            Operation = WindowOperation.Bay,
            GlazingRows = 2,
            GlazingType = "Double glazed, low-E",
            FrameMaterial = "Timber, Painted",
            HeatTransferCoefficient = 1.8,
            Cost = 2100m
        };

        var rooflight = new WindowType("Fixed Light - 600 x 600", 600, 600)
        {
            TypeMark = "W12",
            AssemblyCode = "B2020",
            Operation = WindowOperation.Fixed,
            GlazingType = "Double glazed",
            FrameMaterial = "uPVC",
            HeatTransferCoefficient = 1.6,
            Cost = 180m
        };

        // Horizontal build-ups, layered from the upper surface downward.
        var screedFloor = new FloorType("Floor - Screed on Slab 250mm", new CompoundStructure(
            new MaterialLayer(LayerFunction.Finish1, plaster.Id, 60),
            new MaterialLayer(LayerFunction.ThermalAir, insulation.Id, 40),
            new MaterialLayer(LayerFunction.Structure, concrete.Id, 150)))
        {
            TypeMark = "F1",
            AssemblyCode = "B1010",
            Function = SlabFunction.Structural,
            FireRating = "90 min",
            AcousticRating = 52,
            HeatTransferCoefficient = 0.22,
            Cost = 145m,
            CoarseScaleFillColour = ColourRgb.FromHex("8E8E8E")
        };

        var timberFloor = new FloorType("Floor - Timber 220mm", new CompoundStructure(
            new MaterialLayer(LayerFunction.Finish1, plasterboard.Id, 20),
            new MaterialLayer(LayerFunction.Structure, cavity.Id, 180),
            new MaterialLayer(LayerFunction.Finish2, plasterboard.Id, 20)))
        {
            TypeMark = "F2",
            AssemblyCode = "B1010",
            Function = SlabFunction.Structural,
            FireRating = "30 min",
            AcousticRating = 40,
            Cost = 88m,
            CoarseScaleFillColour = ColourRgb.FromHex("A89878")
        };

        var plasterboardCeiling = new CeilingType("Ceiling - Plasterboard 25mm", new CompoundStructure(
            new MaterialLayer(LayerFunction.Finish2, plasterboard.Id, 25)))
        {
            TypeMark = "C1",
            AssemblyCode = "C3030",
            Function = SlabFunction.Architectural,
            FireRating = "30 min",
            Cost = 34m,
            CoarseScaleFillColour = ColourRgb.FromHex("D4D0C6")
        };

        // A pitched roof's build-up: slates on timber rafters, lined underneath - light enough
        // for a dormer, which the Dormer tool gives it where the main roof is heavier.
        var slate = new Material("Roof Slate, Blue-Grey")
        {
            Density = 2700,
            ThermalConductivity = 2.2,
            SurfaceColour = ColourRgb.FromHex("59616B"),
            CutColour = ColourRgb.FromHex("4B525B"),
            CostPerCubicMetre = 2600m
        };
        var softwood = new Material("Timber, Softwood")
        {
            Density = 500,
            ThermalConductivity = 0.13,
            SurfaceColour = ColourRgb.FromHex("C2A27A"),
            CutColour = ColourRgb.FromHex("B0906A"),
            CostPerCubicMetre = 650m
        };
        foreach (var material in new[] { slate, softwood }) document.AddMaterial(material);

        var pitchedRoof = new RoofType("Roof - Slate on Timber Rafters 150mm", new CompoundStructure(
            new MaterialLayer(LayerFunction.Finish1, slate.Id, 18),
            new MaterialLayer(LayerFunction.Structure, softwood.Id, 120),
            new MaterialLayer(LayerFunction.Finish2, plasterboard.Id, 12)))
        {
            TypeMark = "R2",
            AssemblyCode = "B1020",
            Function = SlabFunction.Structural,
            FireRating = "30 min",
            HeatTransferCoefficient = 0.2,
            Cost = 120m,
            CoarseScaleFillColour = ColourRgb.FromHex("7A6A58")
        };

        var flatRoof = new RoofType("Roof - Warm Flat 320mm", new CompoundStructure(
            new MaterialLayer(LayerFunction.Finish1, plaster.Id, 10),
            new MaterialLayer(LayerFunction.ThermalAir, insulation.Id, 160),
            new MaterialLayer(LayerFunction.Structure, concrete.Id, 150)))
        {
            TypeMark = "R1",
            AssemblyCode = "B1020",
            Function = SlabFunction.Structural,
            FireRating = "90 min",
            HeatTransferCoefficient = 0.15,
            Cost = 195m,
            CoarseScaleFillColour = ColourRgb.FromHex("6E7A86")
        };

        // Curtain walls: a framed storefront, and a plain glass wall to divide as needed.
        var glass = new Material("Glass, Clear")
        {
            Density = 2500,
            ThermalConductivity = 1.0,
            SurfaceColour = ColourRgb.FromHex("8CC4E0"),
            CutColour = ColourRgb.FromHex("6FA9C7"),
            CostPerCubicMetre = 900m
        };
        var aluminium = new Material("Aluminium, Anodised")
        {
            Density = 2700,
            ThermalConductivity = 160,
            SurfaceColour = ColourRgb.FromHex("B8BCC2"),
            CutColour = ColourRgb.FromHex("9DA2A9"),
            CostPerCubicMetre = 9000m
        };
        var spandrel = new Material("Spandrel Panel, Insulated")
        {
            Density = 150,
            ThermalConductivity = 0.03,
            SurfaceColour = ColourRgb.FromHex("4F5B66"),
            CutColour = ColourRgb.FromHex("434E58"),
            CostPerCubicMetre = 600m
        };
        foreach (var material in new[] { glass, aluminium, spandrel }) document.AddMaterial(material);

        // Roof edges: a painted board to cover the ends of the roof, and gutters to hang off it.
        var fasciaBoard = new Material("Timber Fascia, Painted White")
        {
            Density = 500,
            ThermalConductivity = 0.13,
            SurfaceColour = ColourRgb.FromHex("F1F0EA"),
            CutColour = ColourRgb.FromHex("D9D5C7"),
            CostPerCubicMetre = 1400m
        };
        var gutterPlastic = new Material("PVC-U, Black")
        {
            Density = 1400,
            ThermalConductivity = 0.19,
            SurfaceColour = ColourRgb.FromHex("2E3034"),
            CutColour = ColourRgb.FromHex("24262A"),
            CostPerCubicMetre = 3000m
        };
        // Dark by default, so it reads against a light roof and a light wall; white as well.
        var fasciaDark = new Material("Fascia Board, Anthracite")
        {
            Density = 500,
            ThermalConductivity = 0.13,
            SurfaceColour = ColourRgb.FromHex("3E434A"),
            CutColour = ColourRgb.FromHex("33373D"),
            CostPerCubicMetre = 1500m
        };
        foreach (var material in new[] { fasciaBoard, gutterPlastic, fasciaDark }) document.AddMaterial(material);

        // Roof windows: a frame of dark grey aluminium cladding round a double-glazed pane.
        var roofWindowFrame = new Material("Aluminium, Dark Grey")
        {
            Density = 2700,
            ThermalConductivity = 160,
            SurfaceColour = ColourRgb.FromHex("3A3F46"),
            CutColour = ColourRgb.FromHex("2F343A"),
            CostPerCubicMetre = 9000m
        };
        document.AddMaterial(roofWindowFrame);

        var roofWindow = new RoofWindowType("Roof Window - Centre Pivot 780 x 980")
        {
            Width = 780, Height = 980, Operation = RoofWindowOperation.CentrePivot,
            FrameMaterialId = roofWindowFrame.Id, GlassMaterialId = glass.Id, TypeMark = "RW1", Cost = 420m
        };
        var tallRoofWindow = new RoofWindowType("Roof Window - Centre Pivot 780 x 1400")
        {
            Width = 780, Height = 1400, Operation = RoofWindowOperation.CentrePivot,
            FrameMaterialId = roofWindowFrame.Id, GlassMaterialId = glass.Id, TypeMark = "RW2", Cost = 520m
        };
        var escapeRoofWindow = new RoofWindowType("Roof Window - Top Hung Escape 1140 x 1180")
        {
            Width = 1140, Height = 1180, Operation = RoofWindowOperation.TopHung,
            FrameMaterialId = roofWindowFrame.Id, GlassMaterialId = glass.Id, TypeMark = "RW3", Cost = 690m
        };
        var fixedRooflight = new RoofWindowType("Rooflight - Fixed 1000 x 1000")
        {
            Width = 1000, Height = 1000, Operation = RoofWindowOperation.Fixed, Upstand = 150,
            FrameMaterialId = roofWindowFrame.Id, GlassMaterialId = glass.Id, TypeMark = "RL1", Cost = 610m
        };

        var fascia = new FasciaType("Fascia - 25 mm Board, Anthracite") { Thickness = 25, MaterialId = fasciaDark.Id, TypeMark = "F1", Cost = 20m };
        var whiteFascia = new FasciaType("Fascia - 25 mm Board, White") { Thickness = 25, MaterialId = fasciaBoard.Id, TypeMark = "F2", Cost = 18m };
        var halfRound = new GutterType("Gutter - Half Round 125")
        {
            Shape = GutterShape.HalfRound, Width = 125, WallThickness = 4, MaterialId = gutterPlastic.Id, TypeMark = "G1", Cost = 14m
        };
        var soffitBoard = new SoffitType("Soffit - 12 mm Board") { Thickness = 12, MaterialId = fasciaBoard.Id, TypeMark = "S1", Cost = 22m };

        // Soffits by the air they let into the roof - solid, vented all over, vented down the
        // middle, hollow - in white uPVC, the usual board; and by what they are made of:
        // aluminium, timber, fibre cement, steel.
        var pvcWhite = new Material("PVC-U, White")
        {
            Density = 1400,
            ThermalConductivity = 0.17,
            SurfaceColour = ColourRgb.FromHex("F4F4F1"),
            CutColour = ColourRgb.FromHex("DADAD5"),
            CostPerCubicMetre = 2400m
        };
        var fibreCement = new Material("Fibre Cement Board")
        {
            Density = 1300,
            ThermalConductivity = 0.25,
            SurfaceColour = ColourRgb.FromHex("D6D3CA"),
            CutColour = ColourRgb.FromHex("B9B5AA"),
            CostPerCubicMetre = 1800m
        };
        var coatedSteel = new Material("Steel, Colour Coated")
        {
            Density = 7850,
            ThermalConductivity = 50,
            SurfaceColour = ColourRgb.FromHex("C9CDD1"),
            CutColour = ColourRgb.FromHex("9EA3A8"),
            CostPerCubicMetre = 12000m
        };
        foreach (var material in new[] { pvcWhite, fibreCement, coatedSteel }) document.AddMaterial(material);

        SoffitType Soffit(string name, string mark, double thickness, Material material, SoffitBoard board, decimal cost) =>
            new(name)
            {
                Thickness = thickness, MaterialId = material.Id, Board = board, FreeAirArea = SoffitType.TypicalFreeAirArea(board),
                TypeMark = mark, Cost = cost
            };

        var soffits = new[]
        {
            Soffit("Soffit - uPVC Solid 10 mm", "S2", 10, pvcWhite, SoffitBoard.Solid, 16m),
            Soffit("Soffit - uPVC Vented 10 mm", "S3", 10, pvcWhite, SoffitBoard.Vented, 19m),
            Soffit("Soffit - uPVC Centre Vented 10 mm", "S4", 10, pvcWhite, SoffitBoard.CentreVented, 18m),
            Soffit("Soffit - uPVC Hollow 9 mm", "S5", 9, pvcWhite, SoffitBoard.Hollow, 12m),
            Soffit("Soffit - Aluminium Vented 1 mm", "S6", 1, aluminium, SoffitBoard.Vented, 34m),
            Soffit("Soffit - Timber 18 mm", "S7", 18, softwood, SoffitBoard.Solid, 26m),
            Soffit("Soffit - Fibre Cement 9 mm", "S8", 9, fibreCement, SoffitBoard.Solid, 30m),
            Soffit("Soffit - Steel 0.7 mm, Colour Coated", "S9", 0.7, coatedSteel, SoffitBoard.Solid, 38m)
        };

        // Fascias by what they are made of - timber, uPVC, aluminium, composite, fibre cement -
        // and by the shape of their face: square, a capping board over an old fascia, ogee, round.
        var cedar = new Material("Timber, Western Red Cedar")
        {
            Density = 370, ThermalConductivity = 0.11,
            SurfaceColour = ColourRgb.FromHex("A5673F"), CutColour = ColourRgb.FromHex("8A5433"), CostPerCubicMetre = 1900m
        };
        var composite = new Material("Composite, Wood-Plastic Grey")
        {
            Density = 1300, ThermalConductivity = 0.25,
            SurfaceColour = ColourRgb.FromHex("8C857A"), CutColour = ColourRgb.FromHex("6F695F"), CostPerCubicMetre = 2600m
        };
        // What the slot between a floor's edge and a curtain wall is packed with.
        var fireStop = new Material("Fire Stop, Mineral Wool")
        {
            Density = 100, ThermalConductivity = 0.035,
            SurfaceColour = ColourRgb.FromHex("D8C98A"), CutColour = ColourRgb.FromHex("C9B874"), CostPerCubicMetre = 400m
        };
        foreach (var material in new[] { cedar, composite, fireStop }) document.AddMaterial(material);

        FasciaType Fascia(string name, string mark, FasciaProfile profile, double thickness, Material material, decimal cost) =>
            new(name) { Profile = profile, Thickness = thickness, MaterialId = material.Id, TypeMark = mark, Cost = cost };

        // Gutters by their section - K-style, half round, box, fascia gutter - and in what they
        // are made of: aluminium, copper (bright, or gone green-blue with age), vinyl,
        // galvanised steel, zinc. Each with the downpipes that go with it.
        var aluminiumWhite = new Material("Aluminium, Powder Coated White")
        {
            Density = 2700, ThermalConductivity = 160,
            SurfaceColour = ColourRgb.FromHex("F2F2EE"), CutColour = ColourRgb.FromHex("C9CBCC"), CostPerCubicMetre = 9500m
        };
        var aluminiumAnthracite = new Material("Aluminium, Powder Coated Anthracite")
        {
            Density = 2700, ThermalConductivity = 160,
            SurfaceColour = ColourRgb.FromHex("3B3F45"), CutColour = ColourRgb.FromHex("2E3136"), CostPerCubicMetre = 9500m
        };
        var copper = new Material("Copper")
        {
            Density = 8960, ThermalConductivity = 400,
            SurfaceColour = ColourRgb.FromHex("B87333"), CutColour = ColourRgb.FromHex("8E5626"), CostPerCubicMetre = 90000m
        };
        var copperPatina = new Material("Copper, Patinated")
        {
            Density = 8960, ThermalConductivity = 400,
            SurfaceColour = ColourRgb.FromHex("6FAE9C"), CutColour = ColourRgb.FromHex("8E5626"), CostPerCubicMetre = 90000m
        };
        var galvanised = new Material("Steel, Galvanised")
        {
            Density = 7850, ThermalConductivity = 50,
            SurfaceColour = ColourRgb.FromHex("A9AFB4"), CutColour = ColourRgb.FromHex("858B90"), CostPerCubicMetre = 11000m
        };
        var zinc = new Material("Zinc, Pre-Weathered")
        {
            Density = 7140, ThermalConductivity = 116,
            SurfaceColour = ColourRgb.FromHex("7F868C"), CutColour = ColourRgb.FromHex("656B70"), CostPerCubicMetre = 30000m
        };
        foreach (var material in new[] { aluminiumWhite, aluminiumAnthracite, copper, copperPatina, galvanised, zinc }) document.AddMaterial(material);

        GutterType Gutter(string name, string mark, GutterShape shape, double width, double depth, double sheet, Material material,
            DownpipeShape pipe, double pipeWidth, double pipeDepth, decimal cost) =>
            new(name)
            {
                Shape = shape, Width = width, Depth = depth, WallThickness = sheet, MaterialId = material.Id,
                DownpipeShape = pipe, DownpipeWidth = pipeWidth, DownpipeDepth = pipeDepth, TypeMark = mark, Cost = cost
            };

        var gutters = new[]
        {
            Gutter("Gutter - K-Style 125, Aluminium White", "G3", GutterShape.KStyle, 125, 100, 0.7, aluminiumWhite, DownpipeShape.Rectangular, 50, 75, 18m),
            Gutter("Gutter - K-Style 150, Aluminium White", "G4", GutterShape.KStyle, 150, 115, 0.8, aluminiumWhite, DownpipeShape.Rectangular, 75, 100, 24m),
            Gutter("Gutter - K-Style 125, Vinyl White", "G5", GutterShape.KStyle, 125, 100, 2.5, pvcWhite, DownpipeShape.Rectangular, 50, 75, 11m),
            Gutter("Gutter - K-Style 125, Galvanised Steel", "G6", GutterShape.KStyle, 125, 100, 0.8, galvanised, DownpipeShape.Rectangular, 50, 75, 20m),
            Gutter("Gutter - Half Round 150, Copper", "G7", GutterShape.HalfRound, 150, 75, 0.7, copper, DownpipeShape.Round, 76, 76, 65m),
            Gutter("Gutter - Half Round 150, Copper Patina", "G8", GutterShape.HalfRound, 150, 75, 0.7, copperPatina, DownpipeShape.Round, 76, 76, 65m),
            Gutter("Gutter - Half Round 125, Zinc", "G9", GutterShape.HalfRound, 125, 63, 0.7, zinc, DownpipeShape.Round, 76, 76, 45m),
            Gutter("Gutter - Box 200 x 150, Galvanised Steel", "G10", GutterShape.Box, 200, 150, 1.2, galvanised, DownpipeShape.Square, 100, 100, 40m),
            Gutter("Gutter - Fascia 120 x 180, Aluminium Anthracite", "G11", GutterShape.Fascia, 120, 180, 1.0, aluminiumAnthracite, DownpipeShape.Rectangular, 65, 100, 38m)
        };

        var fascias = new[]
        {
            Fascia("Fascia - Timber 25 mm, Pine", "F3", FasciaProfile.Square, 25, softwood, 14m),
            Fascia("Fascia - Timber 25 mm, Cedar", "F4", FasciaProfile.Square, 25, cedar, 24m),
            Fascia("Fascia - uPVC Square 18 mm, White", "F5", FasciaProfile.Square, 18, pvcWhite, 12m),
            Fascia("Fascia - uPVC Capping 9 mm, White", "F6", FasciaProfile.Capping, 31, pvcWhite, 9m),
            Fascia("Fascia - uPVC Ogee 18 mm, White", "F7", FasciaProfile.Ogee, 18, pvcWhite, 15m),
            Fascia("Fascia - uPVC Round 18 mm, White", "F8", FasciaProfile.Round, 18, pvcWhite, 14m),
            Fascia("Fascia - Aluminium Square, Anthracite", "F9", FasciaProfile.Square, 30, aluminiumAnthracite, 32m),
            Fascia("Fascia - Composite 25 mm, Grey", "F10", FasciaProfile.Square, 25, composite, 22m),
            Fascia("Fascia - Fibre Cement 12 mm", "F11", FasciaProfile.Square, 12, fibreCement, 18m)
        };
        var boxGutter = new GutterType("Gutter - Box 100 x 75")
        {
            Shape = GutterShape.Box, Width = 100, Depth = 75, WallThickness = 4, MaterialId = gutterPlastic.Id, TypeMark = "G2", Cost = 16m
        };

        var storefront = new CurtainWallType("Curtain Wall - Storefront 1500")
        {
            AutomaticallyEmbed = true,
            TypeMark = "CW1",
            AssemblyCode = "B2020",
            GlassMaterialId = glass.Id,
            SolidMaterialId = spandrel.Id,
            MullionMaterialId = aluminium.Id,
            Cost = 420m
        };
        var plainGlass = new CurtainWallType("Curtain Wall - Glazed")
        {
            TypeMark = "CW2",
            AssemblyCode = "B2020",
            VerticalLayout = CurtainGridLayout.None,
            HorizontalLayout = CurtainGridLayout.None,
            GlassMaterialId = glass.Id,
            SolidMaterialId = spandrel.Id,
            MullionMaterialId = aluminium.Id,
            Cost = 380m
        };

        // Sweeps and reveals to place on walls one by one, and a drawn profile for a dado rail.
        var dadoProfile = new SweepProfileType("Dado Rail");
        dadoProfile.Points.AddRange(new[]
        {
            new Point2D(0, 0), new Point2D(12, 0), new Point2D(18, 8), new Point2D(18, 22), new Point2D(26, 32),
            new Point2D(26, 48), new Point2D(18, 58), new Point2D(18, 62), new Point2D(10, 70), new Point2D(0, 70)
        });
        var skirting = new WallSweepType("Skirting - 20 x 100", SweepKind.Sweep)
        {
            Profile = SweepProfile.Skirting, Depth = 20, Height = 100, MaterialId = plaster.Id
        };
        var cornice = new WallSweepType("Cornice - 80 x 150", SweepKind.Sweep)
        {
            Profile = SweepProfile.Cornice, Depth = 80, Height = 150, MaterialId = plaster.Id, Cuttable = false
        };
        var dado = new WallSweepType("Dado Rail - 26 x 70", SweepKind.Sweep)
        {
            ProfileId = dadoProfile.Id, Depth = 26, Height = 70, MaterialId = plaster.Id
        };
        var reveal = new WallSweepType("Reveal - 20 x 20", SweepKind.Reveal) { Depth = 20, Height = 20, MaterialId = brick.Id };
        var shadowGap = new WallSweepType("Shadow Gap - 10 x 10", SweepKind.Reveal) { Depth = 10, Height = 10, MaterialId = plaster.Id };

        // Architectural columns: the box-out round a structural column, and the pier or pilaster
        // that is there to be seen. Square, round and the corner one that tucks into a room.
        var columns = new[]
        {
            new ColumnType("Column - 300 x 300", ColumnShape.Rectangular, 300, 300)
                { TypeMark = "AC1", MaterialId = block.Id, Cost = 140m },
            new ColumnType("Column - 450 x 450", ColumnShape.Rectangular, 450, 450)
                { TypeMark = "AC2", MaterialId = block.Id, Cost = 190m },
            new ColumnType("Column - 300 x 600", ColumnShape.Rectangular, 300, 600)
                { TypeMark = "AC3", MaterialId = block.Id, Cost = 210m },
            new ColumnType("Column - Round 400", ColumnShape.Round, 400, 400)
                { TypeMark = "AC4", MaterialId = concrete.Id, Cost = 230m },
            new ColumnType("Column - Corner 400", ColumnShape.LShaped, 400, 400)
                { TypeMark = "AC5", MaterialId = block.Id, Cost = 175m },
            new ColumnType("Column - Oval 500 x 350", ColumnShape.Round, 500, 350)
                { TypeMark = "AC6", MaterialId = concrete.Id, Cost = 250m },
            new ColumnType("Column - Hexagonal 400", ColumnShape.Hexagonal, 400, 400)
                { TypeMark = "AC7", MaterialId = concrete.Id, Cost = 215m },
            new ColumnType("Column - Octagonal 400", ColumnShape.Octagonal, 400, 400)
                { TypeMark = "AC8", MaterialId = concrete.Id, Cost = 225m },
            new ColumnType("Column - Triangular 450", ColumnShape.Triangular, 450, 450)
                { TypeMark = "AC9", MaterialId = block.Id, Cost = 185m },

            // Shaped columns: the same section, told what to do on the way up. Each is a set of
            // numbers rather than a shape of its own, so changing a height regenerates it.
            new ColumnType("Column - Tapered 500 to 350", ColumnShape.Round, 500, 500)
            {
                TypeMark = "AC10", MaterialId = concrete.Id, Cost = 290m,
                Shaping = { TopScale = 0.7 }
            },
            new ColumnType("Column - Twisted 400", ColumnShape.Rectangular, 400, 400)
            {
                TypeMark = "AC11", MaterialId = concrete.Id, Cost = 340m,
                Shaping = { Twist = 90 }
            },
            new ColumnType("Column - Slanted 400", ColumnShape.Rectangular, 400, 400)
            {
                TypeMark = "AC12", MaterialId = concrete.Id, Cost = 320m,
                Shaping = { SlantAcross = 600 }
            },

            // Classical orders, as parameters: a plinth, a fluted and tapered shaft, a capital.
            new ColumnType("Column - Tuscan 450", ColumnShape.Round, 450, 450)
            {
                TypeMark = "CL1", MaterialId = plaster.Id, Cost = 480m,
                Shaping = { TopScale = 0.84, BaseHeight = 220, BaseSpread = 0.2, CapitalHeight = 220, CapitalSpread = 0.24 }
            },
            new ColumnType("Column - Doric 500", ColumnShape.Round, 500, 500)
            {
                TypeMark = "CL2", MaterialId = plaster.Id, Cost = 620m,
                Shaping =
                {
                    TopScale = 0.8, Flutes = 20, FluteDepth = 26,
                    BaseHeight = 180, BaseSpread = 0.16, CapitalHeight = 260, CapitalSpread = 0.28
                }
            },
            new ColumnType("Column - Ionic 500", ColumnShape.Round, 500, 500)
            {
                TypeMark = "CL3", MaterialId = plaster.Id, Cost = 760m,
                Shaping =
                {
                    TopScale = 0.82, Flutes = 24, FluteDepth = 22,
                    BaseHeight = 300, BaseSpread = 0.22, CapitalHeight = 320, CapitalSpread = 0.34
                }
            }
        };

        foreach (var column in columns) document.AddType(column);

        // The component families a project starts with: the furniture, casework, sanitaryware
        // and planting a plan is laid out with. Most stand on a level; a wall cabinet or a wall
        // light is fixed to a face, and can be moved to another with Pick New Host.
        var timber = new ColourRgb(0xB4, 0x9A, 0x74);
        var fabric = new ColourRgb(0x7F, 0x86, 0x92);
        var white = new ColourRgb(0xEC, 0xEE, 0xF0);
        var foliage = new ColourRgb(0x5E, 0x8C, 0x4E);

        var components = new[]
        {
            new ComponentType("Desk - 1500 x 750", BuiltInCategory.Furniture, ComponentForm.Desk, 1500, 750, 750)
                { TypeMark = "F1", Colour = timber, Cost = 240m },
            new ComponentType("Table - Dining 1800 x 900", BuiltInCategory.Furniture, ComponentForm.Table, 1800, 900, 750)
                { TypeMark = "F2", Colour = timber, Cost = 380m },
            new ComponentType("Chair - Dining", BuiltInCategory.Furniture, ComponentForm.Chair, 450, 500, 900)
                { TypeMark = "F3", Colour = timber, Cost = 85m },
            new ComponentType("Chair - Desk", BuiltInCategory.Furniture, ComponentForm.Chair, 600, 600, 950)
                { TypeMark = "F4", Colour = fabric, Cost = 150m },
            new ComponentType("Sofa - Two Seat", BuiltInCategory.Furniture, ComponentForm.Sofa, 1600, 850, 800)
                { TypeMark = "F5", Colour = fabric, Cost = 620m },
            new ComponentType("Bed - Double", BuiltInCategory.Furniture, ComponentForm.Bed, 1500, 2000, 900)
                { TypeMark = "F6", Colour = fabric, Cost = 540m },
            new ComponentType("Bed - Single", BuiltInCategory.Furniture, ComponentForm.Bed, 900, 1900, 900)
                { TypeMark = "F7", Colour = fabric, Cost = 380m },
            new ComponentType("Bookcase - 900 x 1800", BuiltInCategory.Furniture, ComponentForm.Shelving, 900, 300, 1800)
                { TypeMark = "F8", Colour = timber, Cost = 210m },

            new ComponentType("Counter - Kitchen 1800", BuiltInCategory.Casework, ComponentForm.Counter, 1800, 650, 900)
                { TypeMark = "C1", Colour = timber, Cost = 460m },
            new ComponentType("Base Unit - 600", BuiltInCategory.Casework, ComponentForm.Counter, 600, 600, 900)
                { TypeMark = "C2", Colour = timber, Cost = 180m },
            new ComponentType("Wall Unit - 600", BuiltInCategory.Casework, ComponentForm.Shelving, 600, 350, 700)
            {
                TypeMark = "C3", Colour = timber, Cost = 160m,
                Placement = ComponentPlacement.WorkPlaneBased, DefaultElevation = 1400
            },

            new ComponentType("Basin - Pedestal", BuiltInCategory.PlumbingFixtures, ComponentForm.Basin, 550, 450, 850)
                { TypeMark = "P1", Colour = white, Cost = 190m },
            new ComponentType("Basin - Wall Hung", BuiltInCategory.PlumbingFixtures, ComponentForm.Basin, 550, 420, 300)
            {
                TypeMark = "P2", Colour = white, Cost = 210m,
                Placement = ComponentPlacement.FaceBased, DefaultElevation = 800
            },
            new ComponentType("WC - Close Coupled", BuiltInCategory.PlumbingFixtures, ComponentForm.Toilet, 380, 680, 780)
                { TypeMark = "P3", Colour = white, Cost = 260m },
            new ComponentType("Bath - 1700 x 700", BuiltInCategory.PlumbingFixtures, ComponentForm.Bath, 1700, 700, 550)
                { TypeMark = "P4", Colour = white, Cost = 340m },

            new ComponentType("Wall Light", BuiltInCategory.LightingFixtures, ComponentForm.WallLight, 300, 140, 320)
            {
                TypeMark = "L1", Colour = white, Cost = 75m,
                Placement = ComponentPlacement.FaceBased, DefaultElevation = 2000
            },

            new ComponentType("Tree - Small", BuiltInCategory.Planting, ComponentForm.Tree, 2400, 2400, 4000)
                { TypeMark = "T1", Colour = foliage, Cost = 260m },

            new ComponentType("Generic - 600 Cube", BuiltInCategory.GenericModels, ComponentForm.Box, 600, 600, 600)
                { TypeMark = "G1", Cost = 0m }
        };

        foreach (var component in components) document.AddType(component);

        foreach (var type in new ElementType[]
                 {
                     generic, exterior, partition,
                     dadoProfile, skirting, cornice, dado, reveal, shadowGap,
                     fascia, whiteFascia, halfRound, boxGutter, soffitBoard, soffits[0], soffits[1], soffits[2], soffits[3],
                     soffits[4], soffits[5], soffits[6], soffits[7],
                     gutters[0], gutters[1], gutters[2], gutters[3], gutters[4], gutters[5], gutters[6], gutters[7], gutters[8],
                     fascias[0], fascias[1], fascias[2], fascias[3], fascias[4], fascias[5], fascias[6], fascias[7], fascias[8],
                     roofWindow, tallRoofWindow, escapeRoofWindow, fixedRooflight,
                     storefront, plainGlass,
                     singleDoor, doubleDoor, twinSlider, singleSlider, frenchDoor, glazedDoor, bifold, entrance,
                     curtainSingle, curtainDouble, curtainSlider,
                     casement, picture, casementDouble, georgian, awning, hopper, slidingWindow,
                     tiltAndTurn, doubleHung, louvred, bay, rooflight,
                     screedFloor, timberFloor, plasterboardCeiling, flatRoof, pitchedRoof
                 })
        {
            document.AddType(type);
        }

        return document;
    }
}
