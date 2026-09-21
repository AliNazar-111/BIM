using System.Collections.ObjectModel;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Elements;
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
    public WallType? GetWallType(Wall wall) => FindType<WallType>(wall.TypeId) ?? TypesOf<WallType>().FirstOrDefault();

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

        foreach (var type in template.ElementTypes.Where(type => !present.Contains(type.Category)))
        {
            foreach (var materialId in MaterialsUsedBy(type))
            {
                if (_materials.ContainsKey(materialId)) continue;
                if (template.FindMaterial(materialId) is { } material) AddMaterial(material);
            }

            AddType(type);
        }
    }

    /// <summary>The materials a type's layers refer to, if it is a layered build-up.</summary>
    private static IEnumerable<Guid> MaterialsUsedBy(ElementType type) => type switch
    {
        WallType wall => wall.Structure.Layers.Select(layer => layer.MaterialId),
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
            SurfaceColour = ColourRgb.FromHex("2A2F37"),
            CutColour = ColourRgb.FromHex("22262C")
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
            new MaterialLayer(LayerFunction.FinishExterior, brick.Id, 100),
            new MaterialLayer(LayerFunction.ThermalOrAir, cavity.Id, 25, wraps: false),
            new MaterialLayer(LayerFunction.ThermalOrAir, insulation.Id, 50, wraps: false),
            new MaterialLayer(LayerFunction.Structure, block.Id, 140),
            new MaterialLayer(LayerFunction.FinishInterior, plaster.Id, 15)))
        {
            Function = WallFunction.Exterior,
            TypeMark = "EW1",
            AssemblyCode = "B2010",
            FireRating = "120 min",
            AcousticRating = 53,
            ThermalResistance = 2.85,
            HeatTransferCoefficient = 0.32,
            Cost = 165m,
            CoarseScaleFillColour = ColourRgb.FromHex("A8846E")
        };

        // Lightweight partition: board, stud zone, board.
        var partition = new WallType("Interior - Partition 125mm", new CompoundStructure(
            new MaterialLayer(LayerFunction.FinishExterior, plasterboard.Id, 12.5),
            new MaterialLayer(LayerFunction.Structure, cavity.Id, 100),
            new MaterialLayer(LayerFunction.FinishInterior, plasterboard.Id, 12.5)))
        {
            Function = WallFunction.Interior,
            TypeMark = "P1",
            AssemblyCode = "C1010",
            FireRating = "30 min",
            AcousticRating = 38,
            Cost = 48m,
            CoarseScaleFillColour = ColourRgb.FromHex("BFBAAE")
        };

        // Doors and windows, sized to the metric ranges used on drawings.
        var singleDoor = new DoorType("Single - 900 x 2100", 900, 2100)
        {
            TypeMark = "D1",
            AssemblyCode = "C1020",
            Operation = DoorOperation.Swing,
            LeafCount = 1,
            PanelMaterial = "Timber, Painted",
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
            FrameMaterial = "Timber",
            AcousticRating = 24,
            Cost = 410m
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

        // Horizontal build-ups, layered from the upper surface downward.
        var screedFloor = new FloorType("Floor - Screed on Slab 250mm", new CompoundStructure(
            new MaterialLayer(LayerFunction.FinishExterior, plaster.Id, 60),
            new MaterialLayer(LayerFunction.ThermalOrAir, insulation.Id, 40),
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
            new MaterialLayer(LayerFunction.FinishExterior, plasterboard.Id, 20),
            new MaterialLayer(LayerFunction.Structure, cavity.Id, 180),
            new MaterialLayer(LayerFunction.FinishInterior, plasterboard.Id, 20)))
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
            new MaterialLayer(LayerFunction.FinishInterior, plasterboard.Id, 25)))
        {
            TypeMark = "C1",
            AssemblyCode = "C3030",
            Function = SlabFunction.Architectural,
            FireRating = "30 min",
            Cost = 34m,
            CoarseScaleFillColour = ColourRgb.FromHex("D4D0C6")
        };

        var flatRoof = new RoofType("Roof - Warm Flat 320mm", new CompoundStructure(
            new MaterialLayer(LayerFunction.Membrane, plaster.Id, 10),
            new MaterialLayer(LayerFunction.ThermalOrAir, insulation.Id, 160),
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

        foreach (var type in new ElementType[]
                 {
                     generic, exterior, partition,
                     singleDoor, doubleDoor, twinSlider, singleSlider, casement, picture,
                     screedFloor, timberFloor, plasterboardCeiling, flatRoof
                 })
        {
            document.AddType(type);
        }

        return document;
    }
}
