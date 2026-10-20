using System.Collections.ObjectModel;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;
using BIMDesigner.Core.Schedules;

namespace BIMDesigner.Core.Documents;

/// <summary>Project-wide information (specification section 7).</summary>
public sealed class ProjectInformation
{
    public string Name { get; set; } = "Untitled Project";
    public string Number { get; set; } = string.Empty;
    public string Client { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string BuildingType { get; set; } = string.Empty;

    /// <summary>Whose rules the clear space round fittings is checked against: Approved Document M, or the ADA.</summary>
    public ClearanceStandard ClearanceStandard { get; set; } = ClearanceStandard.ApprovedDocumentM;

    /// <summary>
    /// Whether the building has a sprinkler system throughout - BS 9251 in England, IRC P2904 in
    /// the US. It lets a side near the boundary have more windows (Approved Document B 11.21), and
    /// a US house be built nearer its lot line (IRC Table R302.1(2)).
    /// </summary>
    public bool Sprinklered { get; set; }

    /// <summary>What the ground under the building is - Approved Document A's type of ground and the IRC's class of soil in one - what its footings are sized for.</summary>
    public GroundType Ground { get; set; } = GroundType.FirmClay;

    /// <summary>How much a clay ground shrinks as it dries: what takes Approved Document A's footings down from 0.45 m to 0.75, 0.9 or 1.0 m.</summary>
    public ClayShrinkage ClayShrinkage { get; set; }

    /// <summary>The frost line for a US house, mm below the ground - its jurisdiction's, from Table R301.2; none set at 0.</summary>
    public double FrostDepth { get; set; }

    /// <summary>The ground snow load for a US house, psf - its jurisdiction's, from Table R301.2: what its roof and footings are sized for.</summary>
    public double GroundSnowLoad { get; set; } = 20;

    /// <summary>
    /// The wind pressure on the cladding, kPa, where it is given rather than worked out from the
    /// site: what a curtain wall's mullions are checked to with no basic wind speed set.
    /// </summary>
    public double WindPressure { get; set; } = 1.0;

    /// <summary>
    /// The site's basic wind speed: in England the map's, m/s (BS EN 1991-1-4 UK National Annex
    /// Figure NA.1); in the US the design wind speed, mph (ASCE 7 Figure 26.5-1 for its risk
    /// category). None set at 0, and the wind pressure is as given.
    /// </summary>
    public double BasicWindSpeed { get; set; }

    /// <summary>How high the site is above sea level, m.</summary>
    public double SiteAltitude { get; set; }

    /// <summary>What lies upwind of the site.</summary>
    public WindTerrain Terrain { get; set; } = WindTerrain.OpenCountry;
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

    /// <summary>Every element by its id, kept in step with <see cref="Elements"/>: a level asks where its floor is far too often to search.</summary>
    private readonly Dictionary<Guid, Element> _byId = new();

    public BimDocument()
    {
        Elements.CollectionChanged += (_, change) =>
        {
            if (change.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
            {
                _byId.Clear();
                foreach (var element in Elements) _byId[element.Id] = element;
                return;
            }

            foreach (Element gone in change.OldItems ?? Array.Empty<Element>()) _byId.Remove(gone.Id);
            foreach (Element added in change.NewItems ?? Array.Empty<Element>())
            {
                _byId[added.Id] = added;

                // Drawn while a design option is being worked on, it is part of that option - the
                // first time it comes in, not when an undo brings back what was there already.
                if (_seen.Add(added.Id) && EditingDesignOption is { } option && added.DesignOptionId is null && !Views.Phasing.IsAnnotation(added.Category))
                    added.DesignOptionId = option;
            }
        };

        // A level standing on a floor is told where that floor's top is.
        _levels.CollectionChanged += (_, change) =>
        {
            foreach (Level added in change.NewItems ?? Array.Empty<Level>()) added.FloorTop = FloorTop;
        };
    }

    /// <summary>The top of a floor or flat roof a level stands on, or null where it is gone.</summary>
    private double? FloorTop(Guid id) => _byId.GetValueOrDefault(id) is Slab slab ? slab.GetTopElevation(this) : null;

    /// <summary>An element by its id, or null.</summary>
    public Element? FindElement(Guid id) => _byId.GetValueOrDefault(id);

    public ProjectInformation ProjectInformation { get; } = new();

    /// <summary>What each view leaves out. Views not listed show everything.</summary>
    public Views.ViewSettings ViewSettings { get; } = new();

    /// <summary>The {3D} view every project has: where the 3D window opens, with its camera, style and section box.</summary>
    public Views.View3D Default3D { get; } = new() { Name = "{3D}" };

    /// <summary>Everything placed in the project. Observable so views refresh themselves.</summary>
    public ObservableCollection<Element> Elements { get; } = new();

    /// <summary>The project's schedules: those it starts with, changed or not, and those made in it.</summary>
    public List<ScheduleDefinition> Schedules { get; } = new();

    /// <summary>A schedule by its id, or null.</summary>
    public ScheduleDefinition? FindSchedule(Guid id) => Schedules.FirstOrDefault(schedule => schedule.Id == id);

    /// <summary>Every element that has ever been in the project, by its id: what tells a new one from one an undo brings back.</summary>
    private readonly HashSet<Guid> _seen = new();

    /// <summary>The design option new elements go into while it is being worked on; null for the main model.</summary>
    public Guid? EditingDesignOption { get; set; }

    /// <summary>The project's view filters: what each view can hide, colour or fade by its parameters.</summary>
    public List<Views.ParameterFilter> ParameterFilters { get; } = new();

    /// <summary>The project's sets of design options.</summary>
    public List<Views.DesignOptionSet> DesignOptionSets { get; } = new();

    /// <summary>The project's own parameters, and the shared ones it has taken in.</summary>
    public List<Parameters.ProjectParameter> ProjectParameters { get; } = new();

    /// <summary>The classification its types and rooms are coded to.</summary>
    public Parameters.ClassificationTable Classification { get; } = new();

    /// <summary>The revisions of the drawings, in the order they were made: what numbers them.</summary>
    public List<Annotation.Revision> Revisions { get; } = new();

    /// <summary>A revision by its id, or null.</summary>
    public Annotation.Revision? FindRevision(Guid id) => Revisions.FirstOrDefault(revision => revision.Id == id);

    /// <summary>The project's keynotes: the keys keynote tags carry, and what they say.</summary>
    public Annotation.KeynoteTable Keynotes { get; } = new();

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
    /// <summary>Library families saved under an older name: the old name, the new, and the size it had then.</summary>
    private static readonly (string Old, string Renamed, double Width, double Depth)[] RenamedFamilies =
    {
        ("Bed - Double", "Bed - King 1500 x 2000", 1500, 2000),
        ("Bed - Single", "Bed - Single 900 x 1900", 900, 1900)
    };

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

        // Families the library has renamed since: the old "Bed - Double" was a king's 1500 x 2000.
        // One left at the size it was saved at takes its new name, so it is not added again beside itself.
        foreach (var (old, renamed, width, depth) in RenamedFamilies)
            if (_types.Values.OfType<ComponentType>().FirstOrDefault(type => string.Equals(type.Name, old, StringComparison.OrdinalIgnoreCase)) is { } family &&
                Math.Abs(family.Width - width) < 0.5 && Math.Abs(family.Depth - depth) < 0.5 &&
                !_types.Values.OfType<ComponentType>().Any(type => string.Equals(type.Name, renamed, StringComparison.OrdinalIgnoreCase)))
                family.Name = renamed;

        // Component families are a library in the same way, and one that grows: a project made
        // before a family existed should still be able to place it, while keeping its own.
        var families =_types.Values.OfType<ComponentType>().Select(type => type.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var columnTypes = _types.Values.OfType<ColumnType>().Select(type => type.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Fascias, gutters and soffits are a library too, added to as it grows - and so are
        // roofs: a project made when the only roof was a flat one gains a pitched build-up.
        var roofEdgeTypes = _types.Values.Where(type => type is FasciaType or GutterType or SoffitType or EaveType)
            .Select(type => type.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var roofTypes = _types.Values.OfType<RoofType>().Select(type => type.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var wallTypeNames = _types.Values.OfType<WallType>().Select(type => type.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var roofWindowTypes = _types.Values.OfType<RoofWindowType>().Select(type => type.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var footingTypes = _types.Values.OfType<FootingType>().Select(type => type.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var mullionTypes = _types.Values.OfType<MullionType>().Select(type => type.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var curtainTypes = _types.Values.OfType<CurtainWallType>().Select(type => type.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var chimneyTypes = _types.Values.OfType<ChimneyType>().Select(type => type.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var type in template.ElementTypes.Where(type => type switch
                 {
                     // A curtain wall type framed in mullion types comes by name, as they do.
                     CurtainWallType curtain => !hasCurtain || curtain.InteriorVertical is not null && !curtainTypes.Contains(curtain.Name),
                     MullionType => !mullionTypes.Contains(type.Name),
                     OpeningType opening => !known.Contains(opening.Name),
                     ComponentType component => !families.Contains(component.Name),
                     ColumnType column => !columnTypes.Contains(column.Name),
                     FasciaType or GutterType or SoffitType or EaveType => !roofEdgeTypes.Contains(type.Name),
                     RoofType => !roofTypes.Contains(type.Name),
                     CeilingType { Name: "Ceiling - Plasterboard under 270mm Mineral Wool" or "Ceiling - Soffit Board, uPVC 10mm" } => !_types.Values.Any(own => string.Equals(own.Name, type.Name, StringComparison.OrdinalIgnoreCase)),
                     WallType { Log: not null } or WallType { Name: "Stud Wall - Acoustic 125mm" or "Timber Frame - Brick Clad 315mm" or "Party Wall - Cavity Blockwork 300mm" } => !wallTypeNames.Contains(type.Name),
                     RoofWindowType => !roofWindowTypes.Contains(type.Name),
                     FootingType => !footingTypes.Contains(type.Name),
                     ChimneyType => !chimneyTypes.Contains(type.Name),
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

        // A curtain wall type come from the library puts the project's own mullion types of the
        // names it framed in on its lines, where the project had them already.
        foreach (var curtain in _types.Values.OfType<CurtainWallType>())
        {
            Guid? Own(Guid? slot) =>
                slot is { } id && id != Guid.Empty && FindType<MullionType>(id) is null &&
                template.FindType<MullionType>(id) is { } library &&
                _types.Values.OfType<MullionType>().FirstOrDefault(own => string.Equals(own.Name, library.Name, StringComparison.OrdinalIgnoreCase)) is { } same
                    ? same.Id
                    : slot;
            (curtain.InteriorVertical, curtain.BorderVerticalStart, curtain.BorderVerticalEnd) =
                (Own(curtain.InteriorVertical), Own(curtain.BorderVerticalStart), Own(curtain.BorderVerticalEnd));
            (curtain.InteriorHorizontal, curtain.BorderHorizontalBottom, curtain.BorderHorizontalTop) =
                (Own(curtain.InteriorHorizontal), Own(curtain.BorderHorizontalBottom), Own(curtain.BorderHorizontalTop));
            curtain.TakeDepth(this);
        }

        // A library family saved while it was still a plain box - the wardrobe was one - or as
        // something it is not - a wall unit drawn as open shelves - takes the shape the library
        // has given it since, keeping its size, material and everything else.
        foreach (var own in _types.Values.OfType<ComponentType>().Where(type => type.Form is ComponentForm.Box or ComponentForm.Shelving))
            if (template.TypesOf<ComponentType>().FirstOrDefault(type => string.Equals(type.Name, own.Name, StringComparison.OrdinalIgnoreCase)) is { } shaped &&
                shaped.Form != own.Form && (own.Form == ComponentForm.Box || shaped.Form == ComponentForm.WallUnit))
                own.Form = shaped.Form;

        // Finishes to paint with: a project made before Paint gains them, by name.
        foreach (var finish in FinishMaterials())
            if (!_materials.Values.Any(own => string.Equals(own.Name, finish.Name, StringComparison.OrdinalIgnoreCase)))
                AddMaterial(finish);
    }

    /// <summary>
    /// Paints and tiles, for Paint to put on a wall's face: a white emulsion and three colours, a
    /// masonry paint for outside, wall tiles plain and mosaic.
    /// </summary>
    private static IEnumerable<Material> FinishMaterials()
    {
        Material Finish(string name, string colour, decimal cost) => new(name)
        {
            Density = 1300, ThermalConductivity = 0.5, SurfaceColour = ColourRgb.FromHex(colour), CutColour = ColourRgb.FromHex(colour),
            CostPerCubicMetre = cost
        };

        yield return Finish("Paint, White Emulsion", "F2F0EA", 0m);
        yield return Finish("Paint, Terracotta", "B5573A", 0m);
        yield return Finish("Paint, Sage Green", "9AAE8F", 0m);
        yield return Finish("Paint, Charcoal", "3C3F44", 0m);
        yield return Finish("Paint, Masonry, Off-White", "E8E2D2", 0m);
        yield return Finish("Ceramic Tile, White Gloss", "F4F6F7", 0m);
        yield return Finish("Ceramic Tile, Blue Mosaic", "3E6E9E", 0m);
    }

    /// <summary>Points a type that refers to a material by itself at another; false for a type it cannot be done for here.</summary>
    private static bool RepointMaterial(ElementType type, Guid from, Guid to)
    {
        switch (type)
        {
            case SoffitType soffit when soffit.MaterialId == from:
                soffit.MaterialId = to;
                return true;
            case FootingType footing when footing.MaterialId == from || footing.StemMaterialId == from:
                if (footing.MaterialId == from) footing.MaterialId = to;
                if (footing.StemMaterialId == from) footing.StemMaterialId = to;
                return true;
            case ColumnType column when column.MaterialId == from:
                column.MaterialId = to;
                return true;
            case EaveType eave when eave.MaterialId == from || eave.SoffitMaterialId == from:
                if (eave.MaterialId == from) eave.MaterialId = to;
                if (eave.SoffitMaterialId == from) eave.SoffitMaterialId = to;
                return true;
            case MullionType mullion when mullion.MaterialId == from:
                mullion.MaterialId = to;
                return true;
            case FasciaType fascia when fascia.MaterialId == from:
                fascia.MaterialId = to;
                return true;
            case GutterType gutter when gutter.MaterialId == from:
                gutter.MaterialId = to;
                return true;
            case SlabEdgeType slabEdge when slabEdge.MaterialId == from:
                slabEdge.MaterialId = to;
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
        SlabEdgeType slabEdge => new[] { slabEdge.MaterialId },
        SoffitType soffit => new[] { soffit.MaterialId },
        EaveType eave => new[] { eave.MaterialId, eave.SoffitMaterialId },
        MullionType mullion => new[] { mullion.MaterialId },
        FootingType footing => new[] { footing.MaterialId, footing.StemMaterialId },
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

        // Aircrete block, light and warm, for the inner leaf of an outside wall; mineral wool for
        // a stud wall's insulation and its sound.
        var aircrete = new Material("Aircrete Block")
        {
            Density = 600,
            ThermalConductivity = 0.11,
            SurfaceColour = ColourRgb.FromHex("C9CBC4"),
            CutColour = ColourRgb.FromHex("B9BBB4"),
            CostPerCubicMetre = 110m
        };
        var mineralWool = new Material("Mineral Wool Insulation")
        {
            Density = 30,
            ThermalConductivity = 0.035,
            SurfaceColour = ColourRgb.FromHex("D9C38A"),
            CutColour = ColourRgb.FromHex("C9B37A"),
            CostPerCubicMetre = 60m
        };

        foreach (var material in new[] { concrete, brick, block, insulation, cavity, plaster, plasterboard, aircrete, mineralWool })
            document.AddMaterial(material);
        foreach (var finish in FinishMaterials()) document.AddMaterial(finish);

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

        // Exterior cavity wall: brick outer leaf, cavity, insulation, an aircrete block inner leaf,
        // plaster - 0.25 W/m²K, inside the 0.26 Approved Document L allows a new dwelling's wall.
        var exterior = new WallType("Exterior - Brick on Block 330mm", new CompoundStructure(
            new MaterialLayer(LayerFunction.Finish1, brick.Id, 100),
            new MaterialLayer(LayerFunction.ThermalAir, cavity.Id, 25, wraps: false),
            new MaterialLayer(LayerFunction.ThermalAir, insulation.Id, 50, wraps: false),
            new MaterialLayer(LayerFunction.Structure, aircrete.Id, 140),
            new MaterialLayer(LayerFunction.Finish2, plaster.Id, 15)))
        {
            Function = WallFunction.Exterior,
            TypeMark = "EW1",
            AssemblyCode = "B2010",
            FireRating = "120 min",
            AcousticRating = 53,
            ThermalResistance = 4.05,
            HeatTransferCoefficient = 0.25,
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

        // Doors the rules ask for: a front door giving the 850 clear Approved Document M asks of an
        // external door (M4(2) 2.20); a fire door, FD30S and self-closing, for between a house and
        // its garage (Approved Document B 5.6); a wide internal door giving a wheelchair user's 850
        // (M4(3) 3.24); a sectional garage door; and the US's 36 x 80 in exterior door, 32 in clear
        // by 78 in, its egress door (IRC R311.2).
        var frontDoor = new DoorType("Front Door - 1000 x 2100", 1000, 2100)
        {
            TypeMark = "D9", AssemblyCode = "C1020", Operation = DoorOperation.Swing, LeafDesign = DoorLeafDesign.HalfGlazed,
            PanelMaterial = "Composite", FrameMaterial = "Composite", Function = DoorFunction.Exterior, HeatTransferCoefficient = 1.4,
            Cost = 1100m, Description = "850 clear and more, for Approved Document M's principal entrance"
        };
        var fireDoor = new DoorType("Fire Door FD30S - 900 x 2100", 900, 2100)
        {
            TypeMark = "D10", AssemblyCode = "C1020", Operation = DoorOperation.Swing, LeafDesign = DoorLeafDesign.Flush,
            PanelMaterial = "Timber, Painted", FrameMaterial = "Timber", FireRating = "30 min (FD30S)", SelfClosing = true,
            HardwareSet = "Overhead closer, intumescent strips and smoke seals", Cost = 480m
        };
        var wideDoor = new DoorType("Single - 1000 x 2100", 1000, 2100)
        {
            TypeMark = "D11", AssemblyCode = "C1020", Operation = DoorOperation.Swing, LeafDesign = DoorLeafDesign.Panelled,
            PanelMaterial = "Timber, Painted", FrameMaterial = "Timber", FireRating = "30 min", Cost = 360m,
            Description = "850 clear and more, for a wheelchair user's dwelling"
        };
        var garageDoor = new DoorType("Garage Door - Sectional 2400 x 2100", 2400, 2100)
        {
            TypeMark = "D12", AssemblyCode = "C1020", Operation = DoorOperation.Overhead, LeafDesign = DoorLeafDesign.Flush,
            PanelMaterial = "Steel, Insulated", FrameMaterial = "Steel", Function = DoorFunction.Exterior, Thickness = 60, Cost = 1600m
        };
        var usExterior = new DoorType("Exterior Door - 36 x 80 in", 965, 2083)
        {
            TypeMark = "D13", AssemblyCode = "C1020", Operation = DoorOperation.Swing, LeafDesign = DoorLeafDesign.HalfGlazed,
            PanelMaterial = "Fiberglass", FrameMaterial = "Timber", Function = DoorFunction.Exterior, HeatTransferCoefficient = 1.7,
            Cost = 900m, Description = "32 in clear by 78 in: the IRC's egress door"
        };

        // Glass in a door is safety glass, as it is sold: Approved Document K and the IRC ask it.
        foreach (var door in new[] { singleDoor, doubleDoor, twinSlider, singleSlider, frenchDoor, glazedDoor, bifold, entrance, curtainSingle, curtainDouble, curtainSlider, frontDoor, usExterior })
            door.SafetyGlazing = Doors.Glazed(door);

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
            GlazingType = "Double glazed, low-E, toughened",
            FrameMaterial = "Aluminium, thermally broken",
            HeatTransferCoefficient = 1.6,
            SolarHeatGainCoefficient = 0.48,
            Cost = 890m,
            SafetyGlazing = true
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
            HeatTransferCoefficient = 1.4,
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
            HeatTransferCoefficient = 1.4,
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
            HeatTransferCoefficient = 1.4,
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
            Cost = 310m,
            Description = "Single glazed louvres: for a store or a porch, not a heated room"
        };

        var bay = new WindowType("Bay - 2400 x 1500", 2400, 1500)
        {
            TypeMark = "W11",
            AssemblyCode = "B2020",
            Operation = WindowOperation.Bay,
            GlazingRows = 2,
            GlazingType = "Double glazed, low-E, toughened",
            FrameMaterial = "Timber, Painted",
            HeatTransferCoefficient = 1.4,
            Cost = 2100m,
            SafetyGlazing = true
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

        // Windows divided into lights by mullions and a transom: a three-light casement with its
        // outer lights side-hung, a pair with top-hung fanlights over, a fixed light with a fanlight
        // to open for air, and two double-hung windows mulled side by side as the US has them.
        var threeLight = new WindowType("Casement 3-Light - 1770 x 1200", 1770, 1200)
        {
            TypeMark = "W13",
            AssemblyCode = "B2020",
            Operation = WindowOperation.Casement,
            Lights = 3,
            LightPattern = "OFO",
            GlazingType = "Double glazed, low-E",
            FrameMaterial = "uPVC",
            HeatTransferCoefficient = 1.4,
            Cost = 690m
        };
        var fanlights = new WindowType("Casement 2-Light, Top-Hung Fanlights - 1200 x 1350", 1200, 1350)
        {
            TypeMark = "W14",
            AssemblyCode = "B2020",
            Operation = WindowOperation.Casement,
            Lights = 2,
            LightPattern = "OF",
            TransomHeight = 950,
            Fanlight = FanlightOperation.TopHung,
            GlazingType = "Double glazed, low-E",
            FrameMaterial = "uPVC",
            HeatTransferCoefficient = 1.4,
            Cost = 640m
        };
        var fixedFanlight = new WindowType("Fixed with Top-Hung Fanlight - 900 x 1500", 900, 1500)
        {
            TypeMark = "W15",
            AssemblyCode = "B2020",
            Operation = WindowOperation.Fixed,
            TransomHeight = 1100,
            Fanlight = FanlightOperation.TopHung,
            GlazingType = "Double glazed, low-E",
            FrameMaterial = "uPVC",
            HeatTransferCoefficient = 1.4,
            Cost = 420m
        };
        var twinDoubleHung = new WindowType("Double Hung Twin - 60 x 60 in", 1524, 1524)
        {
            TypeMark = "W16",
            AssemblyCode = "B2020",
            Operation = WindowOperation.DoubleHung,
            Lights = 2,
            LightPattern = "OO",
            MullionWidth = 76,
            GlazingType = "Double glazed, low-E",
            FrameMaterial = "Vinyl",
            HeatTransferCoefficient = 1.4,
            Cost = 980m
        };

        // Horizontal build-ups, layered from the upper surface downward. The ground floor: a
        // flowing screed on 100 of PIR on the slab - 0.17 W/m²K on the ground under a house, inside
        // the 0.18 Approved Document L allows.
        var screedFloor = new FloorType("Floor - Screed on Slab 250mm", new CompoundStructure(
            new MaterialLayer(LayerFunction.Finish1, plaster.Id, 50),
            new MaterialLayer(LayerFunction.ThermalAir, insulation.Id, 100),
            new MaterialLayer(LayerFunction.Structure, concrete.Id, 100)))
        {
            TypeMark = "F1",
            AssemblyCode = "B1010",
            Function = SlabFunction.Structural,
            FireRating = "90 min",
            AcousticRating = 52,
            HeatTransferCoefficient = 0.17,
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

        // A warm pitched roof: slate on rafters with PIR between them and under them - 0.16 W/m²K,
        // as Approved Document L allows a new dwelling's roof; and a loft's ceiling with mineral
        // wool laid on it, for a cold roof whose slates and rafters keep out only the weather.
        var insulatedRafters = new Material("Timber Rafters with PIR Between")
        {
            Density = 150,
            ThermalConductivity = 0.038,
            SurfaceColour = ColourRgb.FromHex("D8C690"),
            CutColour = ColourRgb.FromHex("C8B680"),
            CostPerCubicMetre = 420m
        };
        document.AddMaterial(insulatedRafters);
        var warmRoof = new RoofType("Roof - Insulated Slate on Rafters 230mm", new CompoundStructure(
            new MaterialLayer(LayerFunction.Finish1, slate.Id, 18),
            new MaterialLayer(LayerFunction.Structure, insulatedRafters.Id, 150),
            new MaterialLayer(LayerFunction.ThermalAir, insulation.Id, 50),
            new MaterialLayer(LayerFunction.Finish2, plasterboard.Id, 12.5)))
        {
            TypeMark = "R3", AssemblyCode = "B1020", Function = SlabFunction.Structural, FireRating = "30 min",
            HeatTransferCoefficient = 0.16, Cost = 175m, CoarseScaleFillColour = ColourRgb.FromHex("7A6A58")
        };
        // Tiled roofs, warm as the slate one: clay plain tiles, laid at 35 degrees at the least, and
        // concrete interlocking tiles, at 15 (BS 5534).
        var plainTile = new Material("Roof Tile, Clay Plain")
        {
            Density = 2000, ThermalConductivity = 1.0,
            SurfaceColour = ColourRgb.FromHex("9C4E36"), CutColour = ColourRgb.FromHex("8C442E"), CostPerCubicMetre = 2200m
        };
        var interlockingTile = new Material("Roof Tile, Concrete Interlocking")
        {
            Density = 2100, ThermalConductivity = 1.5,
            SurfaceColour = ColourRgb.FromHex("5E5A57"), CutColour = ColourRgb.FromHex("504C49"), CostPerCubicMetre = 900m
        };
        document.AddMaterial(plainTile);
        document.AddMaterial(interlockingTile);
        var plainTileRoof = new RoofType("Roof - Plain Clay Tiles on Insulated Rafters 240mm", new CompoundStructure(
            new MaterialLayer(LayerFunction.Finish1, plainTile.Id, 28),
            new MaterialLayer(LayerFunction.Structure, insulatedRafters.Id, 150),
            new MaterialLayer(LayerFunction.ThermalAir, insulation.Id, 50),
            new MaterialLayer(LayerFunction.Finish2, plasterboard.Id, 12.5)))
        {
            TypeMark = "R4", AssemblyCode = "B1020", Function = SlabFunction.Structural, FireRating = "30 min",
            HeatTransferCoefficient = 0.16, Cost = 185m, CoarseScaleFillColour = ColourRgb.FromHex("8C5A44")
        };
        var interlockingRoof = new RoofType("Roof - Concrete Interlocking Tiles on Insulated Rafters 240mm", new CompoundStructure(
            new MaterialLayer(LayerFunction.Finish1, interlockingTile.Id, 28),
            new MaterialLayer(LayerFunction.Structure, insulatedRafters.Id, 150),
            new MaterialLayer(LayerFunction.ThermalAir, insulation.Id, 50),
            new MaterialLayer(LayerFunction.Finish2, plasterboard.Id, 12.5)))
        {
            TypeMark = "R5", AssemblyCode = "B1020", Function = SlabFunction.Structural, FireRating = "30 min",
            HeatTransferCoefficient = 0.16, Cost = 150m, CoarseScaleFillColour = ColourRgb.FromHex("66615D")
        };

        var loftCeiling = new CeilingType("Ceiling - Plasterboard under 270mm Mineral Wool", new CompoundStructure(
            new MaterialLayer(LayerFunction.ThermalAir, mineralWool.Id, 270),
            new MaterialLayer(LayerFunction.Finish2, plasterboard.Id, 12.5)))
        {
            TypeMark = "C2", AssemblyCode = "C3030", Function = SlabFunction.Architectural, FireRating = "30 min", Cost = 42m,
            CoarseScaleFillColour = ColourRgb.FromHex("D9C38A")
        };

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
        // Eaves of each kind - closed, open, exposed rafter, box, abbreviated - in softwood, their
        // soffits in timber, uPVC or fibre cement: rafters 47 x 150 at 400 centres, as a cut roof
        // is framed, the boarding over open ones 18 mm.
        EaveType Eave(string name, string mark, EaveKind kind, Material soffit, SoffitBoard board, decimal cost, Action<EaveType>? more = null)
        {
            var eave = new EaveType(name)
            {
                MaterialId = softwood.Id, SoffitMaterialId = soffit.Id, Board = board, TypeMark = mark, Cost = cost,
                FreeAirArea = kind is EaveKind.Open or EaveKind.ExposedRafters ? 10000 : SoffitType.TypicalFreeAirArea(board)
            };
            eave.ChangeKind(kind);
            more?.Invoke(eave);
            return eave;
        }

        var eaves = new[]
        {
            Eave("Eave - Closed, Vented uPVC Soffit", "E1", EaveKind.Closed, pvcWhite, SoffitBoard.Vented, 38m),
            Eave("Eave - Closed, Timber Soffit", "E2", EaveKind.Closed, softwood, SoffitBoard.Solid, 44m),
            Eave("Eave - Closed, 1-Hour Fire-Rated Underside", "E3", EaveKind.Closed, fibreCement, SoffitBoard.Solid, 58m,
                eave => eave.Underside = EaveUnderside.OneHour),
            Eave("Eave - Open, Rafters 47 x 150 at 400", "E4", EaveKind.Open, softwood, SoffitBoard.Solid, 32m),
            Eave("Eave - Exposed Rafter Tails, Cove Cut", "E5", EaveKind.ExposedRafters, softwood, SoffitBoard.Solid, 46m),
            Eave("Eave - Exposed Rafter Tails, Knee Braces", "E6", EaveKind.ExposedRafters, softwood, SoffitBoard.Solid, 72m, eave =>
            {
                eave.Tail = RafterTail.Scroll;
                eave.Bracket = EaveBracket.KneeBrace;
                eave.BracketSpacing = 2400;
                eave.BracketHeight = 600;
                eave.BracketWidth = 90;
            }),
            Eave("Eave - Box Cornice, Frieze and Bed Moulding", "E7", EaveKind.Box, softwood, SoffitBoard.CentreVented, 64m),
            Eave("Eave - Box Cornice with Brackets", "E8", EaveKind.Box, softwood, SoffitBoard.CentreVented, 96m, eave =>
            {
                eave.Moulding = EaveMoulding.Ogee;
                eave.FriezeDepth = 200;
                eave.Bracket = EaveBracket.Bracket;
                eave.BracketSpacing = 900;
                eave.BracketHeight = 350;
            }),
            Eave("Eave - Box Cornice with Corbels", "E9", EaveKind.Box, softwood, SoffitBoard.CentreVented, 88m, eave =>
            {
                eave.Bracket = EaveBracket.Corbel;
                eave.BracketSpacing = 1200;
                eave.BracketHeight = 240;
                eave.BracketWidth = 100;
            }),
            Eave("Eave - Abbreviated, Frieze Board", "E10", EaveKind.Abbreviated, softwood, SoffitBoard.Solid, 30m, eave => eave.MouldingSize = 30)
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

        // Mullion types to frame a curtain wall's lines with: aluminium boxes at a storefront's and
        // a stick system's sizes, one with a cover cap on its pressure plate, a round tube, steel
        // and timber, and the four corner sections for where two curtain walls meet.
        MullionType Mullion(string name, string mark, MullionShape shape, double width, double thickness, Material finish, decimal cost,
            MullionMaterial made = MullionMaterial.Aluminium, double wall = 3, double cap = 0, double leg = 0) => new(name)
        {
            Shape = shape, Width1 = width > 0 ? width / 2 : 25, Width2 = width > 0 ? width / 2 : 25, Thickness = thickness, CapDepth = cap, WallThickness = made == MullionMaterial.Timber ? 0 : wall,
            Material = made, MaterialId = finish.Id, Leg1 = leg > 0 ? leg : 100, Leg2 = leg > 0 ? leg : 100, TypeMark = mark, AssemblyCode = "B2020", Cost = cost
        };
        var mullions = new[]
        {
            Mullion("Mullion - Rectangular 50 x 100", "MU1", MullionShape.Rectangular, 50, 100, aluminium, 38m),
            Mullion("Mullion - Rectangular 50 x 150", "MU2", MullionShape.Rectangular, 50, 150, aluminium, 46m),
            Mullion("Mullion - Rectangular 65 x 200", "MU3", MullionShape.Rectangular, 65, 200, aluminium, 64m, wall: 4),
            Mullion("Mullion - Capped 50 x 150, Cover Cap 20", "MU4", MullionShape.Rectangular, 50, 130, aluminiumAnthracite, 58m, cap: 20),
            Mullion("Mullion - Circular 50", "MU5", MullionShape.Circular, 50, 50, aluminium, 42m),
            Mullion("Mullion - Steel 60 x 120", "MU6", MullionShape.Rectangular, 60, 120, galvanised, 72m, MullionMaterial.Steel, wall: 5),
            Mullion("Mullion - Timber 60 x 140", "MU7", MullionShape.Rectangular, 60, 140, softwood, 40m, MullionMaterial.Timber),
            Mullion("Mullion - L Corner 175 x 175", "MU8", MullionShape.CornerL, 0, 150, aluminium, 88m, leg: 175),
            Mullion("Mullion - V Corner 175 x 175", "MU9", MullionShape.CornerV, 0, 150, aluminium, 92m, leg: 175),
            Mullion("Mullion - Trapezoid Corner 175 x 175", "MU10", MullionShape.CornerTrapezoid, 0, 150, aluminium, 92m, leg: 175),
            Mullion("Mullion - Quad Corner 150 x 150", "MU11", MullionShape.CornerQuad, 0, 150, aluminium, 84m, leg: 150)
        };

        // A stick system: capped mullions and transoms between panels, plain boxes round its edges.
        var stickSystem = new CurtainWallType("Curtain Wall - Stick System 1500, Capped")
        {
            TypeMark = "CW3",
            AssemblyCode = "B2020",
            HorizontalSpacing = 1350,
            GlassMaterialId = glass.Id,
            SolidMaterialId = spandrel.Id,
            MullionMaterialId = aluminium.Id,
            InteriorVertical = mullions[3].Id,
            InteriorHorizontal = mullions[3].Id,
            BorderVerticalStart = mullions[1].Id,
            BorderVerticalEnd = mullions[1].Id,
            BorderHorizontalBottom = mullions[1].Id,
            BorderHorizontalTop = mullions[1].Id,
            MullionAnchors = true,
            Splices = MullionSplices.EveryFloor,
            StackJoint = 25,
            Cost = 520m
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

        // Structural columns: steel universal columns at their BS EN 10365 sizes and a W8x31, a
        // square hollow section and 3 in Schedule 40 pipe - the least the IRC takes in steel -
        // timber posts, glulam and concrete.
        var structuralSteel = new Material("Steel, Structural (Primed)")
        {
            Density = 7850, ThermalConductivity = 50,
            SurfaceColour = ColourRgb.FromHex("8B3A2F"), CutColour = ColourRgb.FromHex("6E2D25"), CostPerCubicMetre = 14000m
        };
        var glulam = new Material("Timber, Glulam GL24h")
        {
            Density = 420, ThermalConductivity = 0.13,
            SurfaceColour = ColourRgb.FromHex("D8B886"), CutColour = ColourRgb.FromHex("C4A06C"), CostPerCubicMetre = 1500m
        };
        document.AddMaterial(structuralSteel);
        document.AddMaterial(glulam);

        ColumnType Structural(string name, string mark, ColumnShape shape, double width, double depth, BeamMaterial made, Material finish, decimal cost,
            double flange = 0, double web = 0, double wall = 0, string fire = "", bool treated = false) =>
            new(name, shape, width, depth)
            {
                Structural = true, StructuralMaterial = made, MaterialId = finish.Id, FlangeThickness = flange, WebThickness = web,
                WallThickness = wall, FireRating = fire, PreservativeTreated = treated, TypeMark = mark, AssemblyCode = "B1010", Cost = cost
            };

        var structuralColumns = new[]
        {
            Structural("UC 152 x 152 x 23", "SC1", ColumnShape.IBeam, 152.2, 152.4, BeamMaterial.Steel, structuralSteel, 95m, 6.8, 5.8),
            Structural("UC 152 x 152 x 30", "SC2", ColumnShape.IBeam, 152.9, 157.6, BeamMaterial.Steel, structuralSteel, 120m, 9.4, 6.5),
            Structural("UC 203 x 203 x 46", "SC3", ColumnShape.IBeam, 203.6, 203.2, BeamMaterial.Steel, structuralSteel, 180m, 11.0, 7.2),
            Structural("UC 203 x 203 x 46, Intumescent 30 min", "SC4", ColumnShape.IBeam, 203.6, 203.2, BeamMaterial.Steel, structuralSteel, 230m, 11.0, 7.2, fire: "30 min"),
            Structural("SHS 100 x 100 x 5", "SC5", ColumnShape.HollowRectangular, 100, 100, BeamMaterial.Steel, structuralSteel, 70m, wall: 5),
            Structural("CHS 88.9 x 5.5 - 3 in Schedule 40 Pipe", "SC6", ColumnShape.HollowRound, 88.9, 88.9, BeamMaterial.Steel, structuralSteel, 55m, wall: 5.49),
            Structural("W8x31", "SC7", ColumnShape.IBeam, 203.1, 203.2, BeamMaterial.Steel, structuralSteel, 170m, 11.05, 7.24),
            Structural("Timber Post C24 100 x 100", "SC8", ColumnShape.Rectangular, 100, 100, BeamMaterial.Timber, softwood, 25m),
            Structural("Timber Post C24 150 x 150", "SC9", ColumnShape.Rectangular, 150, 150, BeamMaterial.Timber, softwood, 45m),
            Structural("Timber Post 4x4, Treated", "SC10", ColumnShape.Rectangular, 89, 89, BeamMaterial.Timber, softwood, 20m, treated: true),
            Structural("Timber Post 6x6, Treated", "SC11", ColumnShape.Rectangular, 140, 140, BeamMaterial.Timber, softwood, 38m, treated: true),
            Structural("Glulam GL24h 140 x 140", "SC12", ColumnShape.Rectangular, 140, 140, BeamMaterial.Glulam, glulam, 60m),
            Structural("Concrete 300 x 300", "SC13", ColumnShape.Rectangular, 300, 300, BeamMaterial.Concrete, concrete, 140m, fire: "60 min"),
            Structural("Concrete Round 300", "SC14", ColumnShape.Round, 300, 300, BeamMaterial.Concrete, concrete, 150m, fire: "60 min")
        };
        foreach (var column in structuralColumns) document.AddType(column);

        // Footings: strips of concrete under walls, blockwork on them up to the walls; pads under
        // columns, a concrete pier on each up to its column.
        FootingType Strip(string name, string mark, double width, double thickness, decimal cost) => new(name, FootingKind.Strip)
        {
            Width = width, Thickness = thickness, MaterialId = concrete.Id, StemMaterialId = block.Id, TypeMark = mark, AssemblyCode = "A1010", Cost = cost
        };
        FootingType Pad(string name, string mark, double width, double length, double thickness, decimal cost) => new(name, FootingKind.Pad)
        {
            Width = width, Length = length, Thickness = thickness, MaterialId = concrete.Id, StemMaterialId = concrete.Id, TypeMark = mark, AssemblyCode = "A1010", Cost = cost
        };
        foreach (var footing in new[]
                 {
                     Strip("Strip Footing - 450 x 225", "SF1", 450, 225, 55m),
                     Strip("Strip Footing - 600 x 225", "SF2", 600, 225, 68m),
                     Strip("Strip Footing - 750 x 300", "SF3", 750, 300, 92m),
                     Strip("Strip Footing - 900 x 300", "SF4", 900, 300, 110m),
                     Strip("Strip Footing - 1100 x 400", "SF5", 1100, 400, 150m),
                     Strip("Footing - 16 x 8 in", "SF6", 406.4, 203.2, 50m),
                     Strip("Footing - 24 x 10 in", "SF7", 609.6, 254, 70m),
                     Pad("Pad Footing - 600 x 600 x 300", "PF1", 600, 600, 300, 60m),
                     Pad("Pad Footing - 900 x 900 x 350", "PF2", 900, 900, 350, 110m),
                     Pad("Pad Footing - 1200 x 1200 x 450", "PF3", 1200, 1200, 450, 190m),
                     Pad("Pad Footing - 1500 x 1500 x 600", "PF4", 1500, 1500, 600, 290m),
                     Pad("Pad Footing - 24 x 24 x 12 in", "PF5", 609.6, 609.6, 304.8, 62m)
                 })
            document.AddType(footing);

        // The component families a project starts with: the furniture, casework, sanitaryware
        // and planting a plan is laid out with. Most stand on a level; a wall cabinet or a wall
        // light is fixed to a face, and can be moved to another with Pick New Host.
        var timber = new ColourRgb(0xB4, 0x9A, 0x74);
        var fabric = new ColourRgb(0x7F, 0x86, 0x92);
        var white = new ColourRgb(0xEC, 0xEE, 0xF0);
        var foliage = new ColourRgb(0x5E, 0x8C, 0x4E);
        var stainless = new ColourRgb(0xB8, 0xBC, 0xC0);
        var linen = new ColourRgb(0xE8, 0xDC, 0xC4);
        var enamel = new ColourRgb(0x3A, 0x3D, 0x42);

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

            // Beds at the sizes their mattresses are sold in - the UK's, and Approved Document M's
            // furniture schedule, which sizes the principal bedroom's bed a king - their headboards
            // a metre high and the tops of their mattresses at 550.
            new ComponentType("Bed - Single 900 x 1900", BuiltInCategory.Furniture, ComponentForm.Bed, 900, 1900, 1000)
                { TypeMark = "F7", Colour = fabric, Cost = 380m, Description = "UK single: mattress 900 x 1900" },
            new ComponentType("Bed - Small Double 1200 x 1900", BuiltInCategory.Furniture, ComponentForm.Bed, 1200, 1900, 1000)
                { TypeMark = "F14", Colour = fabric, Cost = 420m, Description = "UK small double: mattress 1200 x 1900" },
            new ComponentType("Bed - Double 1350 x 1900", BuiltInCategory.Furniture, ComponentForm.Bed, 1350, 1900, 1000)
                { TypeMark = "F6", Colour = fabric, Cost = 480m, Description = "UK double: mattress 1350 x 1900" },
            new ComponentType("Bed - King 1500 x 2000", BuiltInCategory.Furniture, ComponentForm.Bed, 1500, 2000, 1000)
                { TypeMark = "F15", Colour = fabric, Cost = 540m, Description = "UK king: mattress 1500 x 2000 - the principal bedroom's in Approved Document M" },
            new ComponentType("Bed - Super King 1800 x 2000", BuiltInCategory.Furniture, ComponentForm.Bed, 1800, 2000, 1000)
                { TypeMark = "F16", Colour = fabric, Cost = 690m, Description = "UK super king: mattress 1800 x 2000" },

            // And the US's, in inches: a twin, a full, a queen, a king and a California king.
            new ComponentType("Bed - Twin 38 x 75 in", BuiltInCategory.Furniture, ComponentForm.Bed, 965, 1905, 1100)
                { TypeMark = "F17", Colour = fabric, Cost = 380m, Description = "US twin: mattress 38 x 75 in" },
            new ComponentType("Bed - Full 54 x 75 in", BuiltInCategory.Furniture, ComponentForm.Bed, 1372, 1905, 1100)
                { TypeMark = "F18", Colour = fabric, Cost = 460m, Description = "US full: mattress 54 x 75 in" },
            new ComponentType("Bed - Queen 60 x 80 in", BuiltInCategory.Furniture, ComponentForm.Bed, 1524, 2032, 1100)
                { TypeMark = "F19", Colour = fabric, Cost = 540m, Description = "US queen: mattress 60 x 80 in" },
            new ComponentType("Bed - King 76 x 80 in", BuiltInCategory.Furniture, ComponentForm.Bed, 1930, 2032, 1100)
                { TypeMark = "F20", Colour = fabric, Cost = 720m, Description = "US king: mattress 76 x 80 in" },
            new ComponentType("Bed - California King 72 x 84 in", BuiltInCategory.Furniture, ComponentForm.Bed, 1829, 2134, 1100)
                { TypeMark = "F21", Colour = fabric, Cost = 760m, Description = "US California king: mattress 72 x 84 in" },

            // A bunk bed on 900 x 1900 mattresses, its upper bed 1000 up and guarded as EN 747
            // asks; a cot on a 600 x 1200 mattress, its rails 900 high.
            new ComponentType("Bunk Bed - 900 x 1900", BuiltInCategory.Furniture, ComponentForm.BunkBed, 1020, 2020, 1600)
                { TypeMark = "F22", Colour = timber, Cost = 450m, Description = "Two 900 x 1900 mattresses; upper bed guarded to EN 747-1" },
            new ComponentType("Cot - 600 x 1200", BuiltInCategory.Furniture, ComponentForm.Cot, 680, 1280, 900)
                { TypeMark = "F23", Colour = white, Cost = 220m, Description = "Cot to BS EN 716: mattress 600 x 1200" },
            new ComponentType("Wardrobe - 1000 x 600", BuiltInCategory.Furniture, ComponentForm.Wardrobe, 1000, 600, 2100)
                { TypeMark = "F9", Colour = timber, Cost = 420m },
            new ComponentType("Bookcase - 900 x 1800", BuiltInCategory.Furniture, ComponentForm.Shelving, 900, 300, 1800)
                { TypeMark = "F8", Colour = timber, Cost = 210m },

            // The bedroom and the living room: a triple wardrobe, 600 deep for hanging; an armchair
            // with its seat at 450; a coffee table at 420, a little under the seat; a TV unit.
            new ComponentType("Wardrobe - 1500 x 600", BuiltInCategory.Furniture, ComponentForm.Wardrobe, 1500, 600, 2100)
                { TypeMark = "F10", Colour = timber, Cost = 560m },
            new ComponentType("Armchair", BuiltInCategory.Furniture, ComponentForm.Armchair, 850, 850, 950)
                { TypeMark = "F11", Colour = fabric, Cost = 420m },
            new ComponentType("Coffee Table - 1100 x 600", BuiltInCategory.Furniture, ComponentForm.CoffeeTable, 1100, 600, 420)
                { TypeMark = "F12", Colour = timber, Cost = 180m },
            new ComponentType("TV Unit - 1600", BuiltInCategory.Furniture, ComponentForm.TelevisionUnit, 1600, 450, 500)
                { TypeMark = "F13", Colour = timber, Cost = 280m },

            // A three seat sofa and a corner one, their seats at 450; a table for four - 1200 by
            // 800, as Approved Document M sets one out - and round ones for four and for six, on
            // pedestals; a bedside table, a chest of drawers and a sideboard; a single wardrobe.
            new ComponentType("Sofa - Three Seat", BuiltInCategory.Furniture, ComponentForm.Sofa, 2100, 900, 850)
                { TypeMark = "F24", Colour = fabric, Cost = 820m },
            new ComponentType("Sofa - Corner 2600 x 1700", BuiltInCategory.Furniture, ComponentForm.CornerSofa, 2600, 1700, 850)
                { TypeMark = "F25", Colour = fabric, Cost = 1250m },
            new ComponentType("Table - Dining 1200 x 800", BuiltInCategory.Furniture, ComponentForm.Table, 1200, 800, 750)
                { TypeMark = "F26", Colour = timber, Cost = 240m, Description = "Seats four" },
            new ComponentType("Table - Round 900", BuiltInCategory.Furniture, ComponentForm.RoundTable, 900, 900, 750)
                { TypeMark = "F27", Colour = timber, Cost = 220m, Description = "Seats two to four" },
            new ComponentType("Table - Round 1200", BuiltInCategory.Furniture, ComponentForm.RoundTable, 1200, 1200, 750)
                { TypeMark = "F28", Colour = timber, Cost = 340m, Description = "Seats four to six" },
            new ComponentType("Bedside Table", BuiltInCategory.Furniture, ComponentForm.Drawers, 400, 400, 550)
                { TypeMark = "F29", Colour = timber, Cost = 90m },
            new ComponentType("Chest of Drawers - 750", BuiltInCategory.Furniture, ComponentForm.Drawers, 750, 450, 800)
                { TypeMark = "F30", Colour = timber, Cost = 260m },
            new ComponentType("Sideboard - 1600", BuiltInCategory.Furniture, ComponentForm.Drawers, 1600, 450, 850)
                { TypeMark = "F31", Colour = timber, Cost = 420m },
            new ComponentType("Wardrobe - Single 600 x 600", BuiltInCategory.Furniture, ComponentForm.Wardrobe, 600, 600, 2100)
                { TypeMark = "F32", Colour = timber, Cost = 260m },

            new ComponentType("Counter - Kitchen 1800", BuiltInCategory.Casework, ComponentForm.Counter, 1800, 650, 900)
                { TypeMark = "C1", Colour = timber, Cost = 460m },
            new ComponentType("Base Unit - 600", BuiltInCategory.Casework, ComponentForm.Counter, 600, 600, 900)
                { TypeMark = "C2", Colour = timber, Cost = 180m },
            // Wall units 720 high and 350 deep with their doors, their undersides 500 over the
            // worktop; a tall larder and a tall housing with the oven at eye level, their tops
            // with the wall units'; and an island, which stands where it is put.
            new ComponentType("Wall Unit - 600", BuiltInCategory.Casework, ComponentForm.WallUnit, 600, 350, 720)
            {
                TypeMark = "C3", Colour = timber, Cost = 160m,
                Placement = ComponentPlacement.WorkPlaneBased, DefaultElevation = 1400
            },
            new ComponentType("Wall Unit - 1000", BuiltInCategory.Casework, ComponentForm.WallUnit, 1000, 350, 720)
            {
                TypeMark = "C4", Colour = timber, Cost = 220m,
                Placement = ComponentPlacement.WorkPlaneBased, DefaultElevation = 1400
            },
            new ComponentType("Tall Unit - Larder 600", BuiltInCategory.Casework, ComponentForm.TallUnit, 600, 600, 2120)
                { TypeMark = "C5", Colour = timber, Cost = 420m },
            new ComponentType("Tall Unit - Oven Housing 600", BuiltInCategory.Casework, ComponentForm.OvenHousing, 600, 600, 2120)
                { TypeMark = "C6", Colour = timber, Cost = 1350m, Description = "Built-in oven and microwave at eye level" },
            new ComponentType("Island - 2400 x 1000", BuiltInCategory.Casework, ComponentForm.Counter, 2400, 1000, 900)
                { TypeMark = "C7", Colour = timber, Cost = 1600m, AgainstWall = false },

            // The kitchen's appliances at the sizes UK ones come in: 600 wide to take the place of
            // a 600 unit in a run, the dishwasher, washing machine and under-counter fridge under
            // the 900 worktop - its colour or material is the worktop's - and a fridge-freezer
            // standing tall at the end of the run.
            new ComponentType("Fridge Freezer - 600", BuiltInCategory.SpecialtyEquipment, ComponentForm.Fridge, 600, 650, 1850)
                { TypeMark = "A1", Colour = white, Cost = 450m },
            new ComponentType("Fridge Freezer - American 910", BuiltInCategory.SpecialtyEquipment, ComponentForm.Fridge, 910, 740, 1790)
                { TypeMark = "A2", Colour = stainless, Cost = 1100m },
            new ComponentType("Fridge - Under Counter 600", BuiltInCategory.SpecialtyEquipment, ComponentForm.Fridge, 600, 600, 900)
                { TypeMark = "A3", Colour = timber, Cost = 260m },
            new ComponentType("Oven and Hob - 600", BuiltInCategory.SpecialtyEquipment, ComponentForm.Cooker, 600, 600, 900)
                { TypeMark = "A4", Colour = timber, Cost = 650m },
            new ComponentType("Range Cooker - 1000", BuiltInCategory.SpecialtyEquipment, ComponentForm.Cooker, 1000, 600, 900)
                { TypeMark = "A5", Colour = enamel, Cost = 1200m },
            new ComponentType("Dishwasher - 600", BuiltInCategory.SpecialtyEquipment, ComponentForm.Dishwasher, 600, 600, 900)
                { TypeMark = "A6", Colour = timber, Cost = 380m },
            new ComponentType("Dishwasher - Slimline 450", BuiltInCategory.SpecialtyEquipment, ComponentForm.Dishwasher, 450, 600, 900)
                { TypeMark = "A7", Colour = timber, Cost = 330m },
            new ComponentType("Washing Machine - 600", BuiltInCategory.SpecialtyEquipment, ComponentForm.WashingMachine, 600, 600, 900)
                { TypeMark = "A8", Colour = timber, Cost = 400m },
            new ComponentType("Tumble Dryer - 600", BuiltInCategory.SpecialtyEquipment, ComponentForm.TumbleDryer, 600, 600, 900)
                { TypeMark = "A9", Colour = timber, Cost = 420m },

            // Chimney cooker hoods over the hob, at least as wide as it as Approved Document F
            // asks, hung 700 over it - the middle of its 650 to 750 - by the wall they are fixed to.
            new ComponentType("Cooker Hood - Chimney 600", BuiltInCategory.SpecialtyEquipment, ComponentForm.CookerHood, 600, 500, 850)
            {
                TypeMark = "A10", Colour = stainless, Cost = 280m, ExtractRate = 110,
                Placement = ComponentPlacement.WorkPlaneBased, DefaultElevation = 1600
            },
            new ComponentType("Cooker Hood - Chimney 1000", BuiltInCategory.SpecialtyEquipment, ComponentForm.CookerHood, 1000, 500, 850)
            {
                TypeMark = "A11", Colour = stainless, Cost = 450m, ExtractRate = 170,
                Placement = ComponentPlacement.WorkPlaneBased, DefaultElevation = 1600
            },

            new ComponentType("Basin - Pedestal", BuiltInCategory.PlumbingFixtures, ComponentForm.Basin, 550, 450, 850)
                { TypeMark = "P1", Colour = white, Cost = 190m },
            // Basins on the wall with their rims at 850, as a pedestal basin's is; a hand-rinse
            // basin for a cloakroom; WCs with their seats at 420, close coupled and wall hung.
            new ComponentType("Basin - Wall Hung", BuiltInCategory.PlumbingFixtures, ComponentForm.Basin, 550, 420, 300)
            {
                TypeMark = "P2", Colour = white, Cost = 210m,
                Placement = ComponentPlacement.FaceBased, DefaultElevation = 550
            },
            new ComponentType("Basin - Hand Rinse 400", BuiltInCategory.PlumbingFixtures, ComponentForm.Basin, 400, 250, 250)
            {
                TypeMark = "P11", Colour = white, Cost = 120m,
                Placement = ComponentPlacement.FaceBased, DefaultElevation = 600
            },
            new ComponentType("WC - Close Coupled", BuiltInCategory.PlumbingFixtures, ComponentForm.Toilet, 380, 680, 780)
                { TypeMark = "P3", Colour = white, Cost = 260m },
            new ComponentType("WC - Wall Hung", BuiltInCategory.PlumbingFixtures, ComponentForm.WallHungToilet, 360, 540, 1000)
                { TypeMark = "P12", Colour = white, Cost = 420m, Description = "Concealed cistern in a frame in the wall" },
            new ComponentType("Bath - 1700 x 700", BuiltInCategory.PlumbingFixtures, ComponentForm.Bath, 1700, 700, 550)
                { TypeMark = "P4", Colour = white, Cost = 340m },
            new ComponentType("Bath - 1800 x 800", BuiltInCategory.PlumbingFixtures, ComponentForm.Bath, 1800, 800, 560)
                { TypeMark = "P13", Colour = white, Cost = 480m },

            // Sinks set into the worktop of their unit: a bowl and drainer in a 1000 unit, a bowl in a 600.
            new ComponentType("Sink Unit - 1000", BuiltInCategory.PlumbingFixtures, ComponentForm.Sink, 1000, 600, 900)
                { TypeMark = "P5", Colour = timber, Cost = 320m },
            new ComponentType("Sink Unit - 600", BuiltInCategory.PlumbingFixtures, ComponentForm.Sink, 600, 600, 900)
                { TypeMark = "P6", Colour = timber, Cost = 240m },

            // Showers on a low 45 tray, an enclosure's 8 mm glass 1900 high on it: a tray on its
            // own, a corner one, a sliding door across an alcove, and a walk-in's fixed panel.
            new ComponentType("Shower Tray - 900 x 900", BuiltInCategory.PlumbingFixtures, ComponentForm.Shower, 900, 900, 45)
                { TypeMark = "P7", Colour = white, Cost = 180m },
            new ComponentType("Shower Enclosure - Corner 900 x 900", BuiltInCategory.PlumbingFixtures, ComponentForm.Shower, 900, 900, 1945)
                { TypeMark = "P8", Colour = white, Cost = 520m, Enclosure = ShowerEnclosure.Corner },
            new ComponentType("Shower Enclosure - Alcove 1200 x 800", BuiltInCategory.PlumbingFixtures, ComponentForm.Shower, 1200, 800, 1945)
                { TypeMark = "P9", Colour = white, Cost = 480m, Enclosure = ShowerEnclosure.Alcove },
            new ComponentType("Walk-in Shower - 1400 x 900", BuiltInCategory.PlumbingFixtures, ComponentForm.Shower, 1400, 900, 1945)
                { TypeMark = "P10", Colour = white, Cost = 560m, Enclosure = ShowerEnclosure.WalkIn },

            // A level-access shower, its former flush with the floor: 1200 x 1200, as Approved
            // Document M asks of a wheelchair user's bathroom.
            new ComponentType("Shower - Level Access 1200 x 1200", BuiltInCategory.PlumbingFixtures, ComponentForm.Shower, 1200, 1200, 10)
                { TypeMark = "P14", Colour = new ColourRgb(0xC8, 0xCC, 0xD0), Cost = 650m, Description = "Wet-room former, laid to fall to its drain" },

            // A wall light with its middle at 1700, as lights are put up - 1.6 to 1.8 m - and LED
            // lamps in all of them, past the 75 lumens a watt Approved Document L asks for.
            new ComponentType("Wall Light", BuiltInCategory.LightingFixtures, ComponentForm.WallLight, 300, 140, 320)
            {
                TypeMark = "L1", Colour = white, Cost = 75m, Efficacy = 90,
                Placement = ComponentPlacement.FaceBased, DefaultElevation = 1550
            },

            // Ceiling lights hang from the ceiling over where they are put; with no ceiling, their
            // top at 2400 - the height given is their underside's.
            new ComponentType("Ceiling Light - Flush", BuiltInCategory.LightingFixtures, ComponentForm.CeilingLight, 350, 350, 100)
            {
                TypeMark = "L2", Colour = white, Cost = 45m, Efficacy = 90,
                Placement = ComponentPlacement.WorkPlaneBased, DefaultElevation = 2300
            },
            new ComponentType("Ceiling Light - Pendant", BuiltInCategory.LightingFixtures, ComponentForm.CeilingLight, 400, 400, 700)
            {
                TypeMark = "L3", Colour = linen, Cost = 60m, Efficacy = 80,
                Placement = ComponentPlacement.WorkPlaneBased, DefaultElevation = 1700
            },
            new ComponentType("Downlight - Recessed", BuiltInCategory.LightingFixtures, ComponentForm.CeilingLight, 90, 90, 60)
            {
                TypeMark = "L4", Colour = white, Cost = 25m, Efficacy = 95,
                Placement = ComponentPlacement.WorkPlaneBased, DefaultElevation = 2340
            },

            // Radiators on the wall, 150 off the floor on their valves; sockets and switches on it
            // at the heights Approved Document M sets out - between 450 and 1200 above the floor.
            new ComponentType("Radiator - 600 x 1000", BuiltInCategory.MechanicalEquipment, ComponentForm.Radiator, 1000, 100, 650)
            {
                TypeMark = "M1", Colour = white, Cost = 120m,
                Placement = ComponentPlacement.FaceBased, DefaultElevation = 100
            },
            new ComponentType("Radiator - 600 x 1600", BuiltInCategory.MechanicalEquipment, ComponentForm.Radiator, 1600, 100, 650)
            {
                TypeMark = "M2", Colour = white, Cost = 170m,
                Placement = ComponentPlacement.FaceBased, DefaultElevation = 100
            },
            new ComponentType("Socket - Double", BuiltInCategory.ElectricalFixtures, ComponentForm.Socket, 146, 12, 86)
            {
                TypeMark = "E1", Colour = white, Cost = 18m,
                Placement = ComponentPlacement.FaceBased, DefaultElevation = 450
            },
            new ComponentType("Switch - One Gang", BuiltInCategory.LightingDevices, ComponentForm.Switch, 86, 12, 86)
            {
                TypeMark = "E2", Colour = white, Cost = 8m,
                Placement = ComponentPlacement.FaceBased, DefaultElevation = 1050
            },

            // A shaver supply unit, the one socket BS 7671 lets into a bathroom's zone 2; the
            // consumer unit, its switches at 1400 - Approved Document M's 1350 to 1450; a room
            // thermostat at 1100, in M4(2)'s 900 to 1200 for heating controls.
            new ComponentType("Shaver Socket", BuiltInCategory.ElectricalFixtures, ComponentForm.ShaverSocket, 146, 40, 86)
            {
                TypeMark = "E3", Colour = white, Cost = 45m,
                Placement = ComponentPlacement.FaceBased, DefaultElevation = 1100
            },
            new ComponentType("Consumer Unit - 12 Way", BuiltInCategory.ElectricalEquipment, ComponentForm.ConsumerUnit, 380, 120, 300)
            {
                TypeMark = "E4", Colour = white, Cost = 180m,
                Placement = ComponentPlacement.FaceBased, DefaultElevation = 1250
            },
            new ComponentType("Room Thermostat", BuiltInCategory.MechanicalEquipment, ComponentForm.Thermostat, 86, 25, 86)
            {
                TypeMark = "M3", Colour = white, Cost = 60m,
                Placement = ComponentPlacement.FaceBased, DefaultElevation = 1100
            },

            // A heated towel rail; extract fans within 400 of a 2400 ceiling, as Approved Document
            // F asks (1.20), at a bathroom's 15 l/s and a kitchen's 60 l/s.
            new ComponentType("Towel Rail - 500 x 1200", BuiltInCategory.MechanicalEquipment, ComponentForm.TowelRail, 500, 100, 1200)
            {
                TypeMark = "M4", Colour = new ColourRgb(0xC8, 0xCC, 0xD0), Cost = 140m,
                Placement = ComponentPlacement.FaceBased, DefaultElevation = 150
            },
            new ComponentType("Extract Fan - 100 (15 l/s)", BuiltInCategory.MechanicalEquipment, ComponentForm.ExtractFan, 150, 40, 150)
            {
                TypeMark = "M5", Colour = white, Cost = 55m, ExtractRate = 15,
                Placement = ComponentPlacement.FaceBased, DefaultElevation = 2150
            },
            new ComponentType("Extract Fan - 150 (60 l/s)", BuiltInCategory.MechanicalEquipment, ComponentForm.ExtractFan, 190, 50, 190)
            {
                TypeMark = "M6", Colour = white, Cost = 95m, ExtractRate = 60,
                Placement = ComponentPlacement.FaceBased, DefaultElevation = 2110
            },

            // A gas combi boiler on the wall, its controls at 1150 or so.
            new ComponentType("Boiler - Gas Combi", BuiltInCategory.MechanicalEquipment, ComponentForm.Boiler, 440, 340, 700)
            {
                TypeMark = "M7", Colour = white, Cost = 1800m,
                Placement = ComponentPlacement.FaceBased, DefaultElevation = 1100
            },

            // Alarms on the ceiling: smoke, heat - for a kitchen - and carbon monoxide.
            new ComponentType("Smoke Alarm", BuiltInCategory.FireAlarmDevices, ComponentForm.SmokeAlarm, 110, 110, 45)
            {
                TypeMark = "FA1", Colour = white, Cost = 35m,
                Placement = ComponentPlacement.WorkPlaneBased, DefaultElevation = 2355
            },
            new ComponentType("Heat Alarm", BuiltInCategory.FireAlarmDevices, ComponentForm.HeatAlarm, 110, 110, 45)
            {
                TypeMark = "FA2", Colour = white, Cost = 35m,
                Placement = ComponentPlacement.WorkPlaneBased, DefaultElevation = 2355
            },
            new ComponentType("Carbon Monoxide Alarm", BuiltInCategory.FireAlarmDevices, ComponentForm.CarbonMonoxideAlarm, 110, 110, 45)
            {
                TypeMark = "FA3", Colour = white, Cost = 30m,
                Placement = ComponentPlacement.WorkPlaneBased, DefaultElevation = 2355
            },

            // Trees at their mature heights, with how much water they draw as NHBC Standards
            // Table 3 sorts them - which is how far round them a clay soil is dried - and their
            // stems' diameters, which their root protection areas are measured from (BS 5837).
            // A tree of no named species is taken as high demand, as NHBC asks.
            new ComponentType("Tree - Small", BuiltInCategory.Planting, ComponentForm.Tree, 2400, 2400, 4000)
                { TypeMark = "T1", Colour = foliage, Cost = 260m, StemDiameter = 150 },
            new ComponentType("Tree - Large", BuiltInCategory.Planting, ComponentForm.Tree, 6000, 6000, 9000)
                { TypeMark = "T2", Colour = foliage, Cost = 650m, StemDiameter = 400 },
            new ComponentType("Tree - Oak, English", BuiltInCategory.Planting, ComponentForm.Tree, 14000, 14000, 20000)
                { TypeMark = "T5", Colour = foliage, Cost = 900m, WaterDemand = TreeWaterDemand.High, StemDiameter = 800 },
            new ComponentType("Tree - Willow, Weeping", BuiltInCategory.Planting, ComponentForm.Tree, 12000, 12000, 16000)
                { TypeMark = "T6", Colour = new ColourRgb(0x7E, 0x9E, 0x4E), Cost = 450m, WaterDemand = TreeWaterDemand.High, StemDiameter = 600 },
            new ComponentType("Tree - Poplar, Lombardy", BuiltInCategory.Planting, ComponentForm.Tree, 4000, 4000, 25000)
                { TypeMark = "T7", Colour = foliage, Cost = 380m, WaterDemand = TreeWaterDemand.High, StemDiameter = 500 },
            new ComponentType("Tree - Ash", BuiltInCategory.Planting, ComponentForm.Tree, 15000, 15000, 23000)
                { TypeMark = "T8", Colour = foliage, Cost = 700m, WaterDemand = TreeWaterDemand.Moderate, StemDiameter = 700 },
            new ComponentType("Tree - Beech", BuiltInCategory.Planting, ComponentForm.Tree, 14000, 14000, 20000)
                { TypeMark = "T9", Colour = new ColourRgb(0x5A, 0x7A, 0x3C), Cost = 750m, WaterDemand = TreeWaterDemand.Moderate, StemDiameter = 700 },
            new ComponentType("Tree - Cherry, Japanese", BuiltInCategory.Planting, ComponentForm.Tree, 8000, 8000, 9000)
                { TypeMark = "T10", Colour = new ColourRgb(0xD8, 0x9A, 0xB0), Cost = 320m, WaterDemand = TreeWaterDemand.Moderate, StemDiameter = 300 },
            new ComponentType("Tree - Apple", BuiltInCategory.Planting, ComponentForm.Tree, 6000, 6000, 10000)
                { TypeMark = "T11", Colour = foliage, Cost = 180m, WaterDemand = TreeWaterDemand.Moderate, StemDiameter = 250 },
            new ComponentType("Tree - Birch, Silver", BuiltInCategory.Planting, ComponentForm.Tree, 6000, 6000, 14000)
                { TypeMark = "T12", Colour = new ColourRgb(0x8A, 0xAA, 0x5A), Cost = 240m, WaterDemand = TreeWaterDemand.Low, StemDiameter = 300 },
            new ComponentType("Tree - Holly", BuiltInCategory.Planting, ComponentForm.Tree, 5000, 5000, 12000)
                { TypeMark = "T13", Colour = new ColourRgb(0x2F, 0x5A, 0x30), Cost = 200m, WaterDemand = TreeWaterDemand.Low, StemDiameter = 250 },
            new ComponentType("Conifer - Leyland Cypress", BuiltInCategory.Planting, ComponentForm.Conifer, 5000, 5000, 20000)
                { TypeMark = "T14", Colour = new ColourRgb(0x3F, 0x6B, 0x3A), Cost = 150m, WaterDemand = TreeWaterDemand.High, StemDiameter = 450 },
            new ComponentType("Conifer - 7 m", BuiltInCategory.Planting, ComponentForm.Conifer, 2500, 2500, 7000)
                { TypeMark = "T3", Colour = new ColourRgb(0x3F, 0x6B, 0x3A), Cost = 300m },
            new ComponentType("Shrub - 1200", BuiltInCategory.Planting, ComponentForm.Shrub, 1200, 1200, 1000)
                { TypeMark = "T4", Colour = new ColourRgb(0x6B, 0x8F, 0x4E), Cost = 40m },

            // Outside: cars a parking bay's length - 2400 x 4800 - takes, and people to give a sense of scale.
            new ComponentType("Car - Hatchback", BuiltInCategory.Entourage, ComponentForm.Car, 4100, 1800, 1500)
                { TypeMark = "V1", Colour = new ColourRgb(0x9E, 0x2B, 0x25), Cost = 0m },
            new ComponentType("Car - Estate", BuiltInCategory.Entourage, ComponentForm.Car, 4700, 1850, 1500)
                { TypeMark = "V2", Colour = new ColourRgb(0x3E, 0x52, 0x66), Cost = 0m },
            new ComponentType("Car - SUV", BuiltInCategory.Entourage, ComponentForm.Car, 4600, 1900, 1700)
                { TypeMark = "V3", Colour = new ColourRgb(0x55, 0x5A, 0x60), Cost = 0m },
            new ComponentType("Van - Panel", BuiltInCategory.Entourage, ComponentForm.Car, 5050, 2000, 1980)
                { TypeMark = "V4", Colour = white, Cost = 0m },

            // Parking bays marked out: the UK's 2.4 x 4.8, and an accessible one with 1.2 m zones
            // to one side and behind (M4(3) 3.12); the US's 9 x 18 ft, and an accessible one with
            // its 60 in aisle (ADA 502).
            new ComponentType("Parking Bay - 2400 x 4800", BuiltInCategory.Parking, ComponentForm.ParkingBay, 2400, 4800, 5)
                { TypeMark = "PB1", Colour = new ColourRgb(0x45, 0x48, 0x4D), Cost = 0m },
            new ComponentType("Parking Bay - Accessible 3600 x 6000", BuiltInCategory.Parking, ComponentForm.AccessibleParkingBay, 3600, 6000, 5)
                { TypeMark = "PB2", Colour = new ColourRgb(0x45, 0x48, 0x4D), Cost = 0m },
            new ComponentType("Parking Space - 9 x 18 ft", BuiltInCategory.Parking, ComponentForm.ParkingBay, 2743, 5486, 5)
                { TypeMark = "PB3", Colour = new ColourRgb(0x45, 0x48, 0x4D), Cost = 0m },
            new ComponentType("Parking Space - Accessible 96 + 60 in", BuiltInCategory.Parking, ComponentForm.AccessibleParkingBay, 3962, 5486, 5)
                { TypeMark = "PB4", Colour = new ColourRgb(0x45, 0x48, 0x4D), Cost = 0m },
            new ComponentType("Person - Adult", BuiltInCategory.Entourage, ComponentForm.Person, 500, 300, 1750)
                { TypeMark = "H1", Colour = new ColourRgb(0x4A, 0x6F, 0xA5), Cost = 0m },
            new ComponentType("Person - Child", BuiltInCategory.Entourage, ComponentForm.Person, 380, 230, 1200)
                { TypeMark = "H2", Colour = new ColourRgb(0xC0, 0x50, 0x3A), Cost = 0m },

            // A wheelchair user, at the UK's reference wheelchair's 700 x 1200 x 1350; and turning
            // circles to try a room with - Approved Document M's 1500, the ADA's 60 in.
            new ComponentType("Person - Wheelchair User", BuiltInCategory.Entourage, ComponentForm.WheelchairUser, 700, 1200, 1350)
                { TypeMark = "H3", Colour = new ColourRgb(0x4A, 0x6F, 0xA5), Cost = 0m },
            new ComponentType("Turning Circle - 1500", BuiltInCategory.GenericModels, ComponentForm.TurningCircle, 1500, 1500, 10)
                { TypeMark = "G2", Colour = new ColourRgb(0xB8, 0xE0, 0xC0), Cost = 0m },
            new ComponentType("Turning Circle - 60 in", BuiltInCategory.GenericModels, ComponentForm.TurningCircle, 1525, 1525, 10)
                { TypeMark = "G3", Colour = new ColourRgb(0xB8, 0xE0, 0xC0), Cost = 0m },

            new ComponentType("Generic - 600 Cube", BuiltInCategory.GenericModels, ComponentForm.Box, 600, 600, 600)
                { TypeMark = "G1", Cost = 0m }
        };

        foreach (var component in components) document.AddType(component);
        foreach (var chimney in ChimneyType.Library()) document.AddType(chimney);
        foreach (var stair in StairType.Library()) document.AddType(stair);
        foreach (var ramp in RampType.Library()) document.AddType(ramp);
        foreach (var railing in RailingType.Library()) document.AddType(railing);
        foreach (var beam in BeamType.Library()) document.AddType(beam);

        // Log walls, as Archicad builds them: round logs, D-logs flat inside, squared logs.
        // A stud partition with mineral wool in it, for round a bedroom or a bathroom - Rw 43, past
        // the 40 Approved Document E asks there; a timber frame outside wall behind a brick skin,
        // 0.22 W/m²K.
        var acousticPartition = new WallType("Stud Wall - Acoustic 125mm", new CompoundStructure(
            new MaterialLayer(LayerFunction.Finish2, plasterboard.Id, 12.5),
            new MaterialLayer(LayerFunction.Structure, mineralWool.Id, 100),
            new MaterialLayer(LayerFunction.Finish2, plasterboard.Id, 12.5)))
        {
            Function = WallFunction.Interior, TypeMark = "P2", AssemblyCode = "C1010", FireRating = "30 min", AcousticRating = 43, Cost = 62m,
            CoarseScaleFillColour = ColourRgb.FromHex("C9C1A8"), WrapAtEnds = WallWrapping.Exterior
        };
        // A party wall between houses: two leaves of dense block with a cavity between, plastered both sides.
        var partyWall = new WallType("Party Wall - Cavity Blockwork 300mm", new CompoundStructure(
            new MaterialLayer(LayerFunction.Finish2, plaster.Id, 13),
            new MaterialLayer(LayerFunction.Structure, block.Id, 100),
            new MaterialLayer(LayerFunction.ThermalAir, cavity.Id, 75, wraps: false),
            new MaterialLayer(LayerFunction.Structure, block.Id, 100),
            new MaterialLayer(LayerFunction.Finish2, plaster.Id, 13)))
        {
            Function = WallFunction.Interior, Separating = true, TypeMark = "PW1", AssemblyCode = "C1010", FireRating = "60 min", AcousticRating = 55, Cost = 118m,
            CoarseScaleFillColour = ColourRgb.FromHex("B9B2A4"), WrapAtEnds = WallWrapping.Exterior
        };
        var timberFrame = new WallType("Timber Frame - Brick Clad 315mm", new CompoundStructure(
            new MaterialLayer(LayerFunction.Finish1, brick.Id, 100),
            new MaterialLayer(LayerFunction.ThermalAir, cavity.Id, 50, wraps: false),
            new MaterialLayer(LayerFunction.Substrate, softwood.Id, 9),
            new MaterialLayer(LayerFunction.Structure, mineralWool.Id, 140),
            new MaterialLayer(LayerFunction.Finish2, plasterboard.Id, 12.5)))
        {
            Function = WallFunction.Exterior, TypeMark = "EW2", AssemblyCode = "B2010", FireRating = "60 min", AcousticRating = 45, Cost = 150m,
            HeatTransferCoefficient = 0.22, CoarseScaleFillColour = ColourRgb.FromHex("B59A7A"), WrapAtEnds = WallWrapping.Exterior
        };

        // A soffit of any shape - a porch's, a carport's - sketched as a ceiling of soffit board.
        var soffitCeiling = new CeilingType("Ceiling - Soffit Board, uPVC 10mm", new CompoundStructure(
            new MaterialLayer(LayerFunction.Finish1, pvcWhite.Id, 10)))
        {
            TypeMark = "C3", AssemblyCode = "B2010", Function = SlabFunction.Architectural, Cost = 28m,
            CoarseScaleFillColour = ColourRgb.FromHex("F2F2EE")
        };

        // Slab edges: a thickened edge to a ground-bearing slab, an edge beam, a kerb along a floor's edge.
        var thickenedEdge = new SlabEdgeType("Slab Edge - Thickened 450 x 450")
            { Shape = SlabEdgeShape.Thickened, Width = 450, Depth = 450, FootWidth = 250, MaterialId = concrete.Id, TypeMark = "SE1", Cost = 85m };
        var edgeBeam = new SlabEdgeType("Slab Edge - Edge Beam 300 x 600")
            { Shape = SlabEdgeShape.EdgeBeam, Width = 300, Depth = 600, MaterialId = concrete.Id, TypeMark = "SE2", Cost = 110m };
        var kerb = new SlabEdgeType("Slab Edge - Upstand Kerb 150 x 150")
            { Shape = SlabEdgeShape.Upstand, Width = 150, Depth = 150, MaterialId = concrete.Id, TypeMark = "SE3", Cost = 40m };

        var logRound = new WallType("Log Wall - Round 220", new CompoundStructure(new MaterialLayer(LayerFunction.Structure, softwood.Id, 220)))
            { Function = WallFunction.Exterior, Log = new LogWall(LogShape.Round, 190, 300), WrapAtEnds = WallWrapping.None, TypeMark = "L1", Cost = 210m };
        var logD = new WallType("Log Wall - D-Log 200", new CompoundStructure(new MaterialLayer(LayerFunction.Structure, softwood.Id, 200)))
            { Function = WallFunction.Exterior, Log = new LogWall(LogShape.DLog, 180, 250), WrapAtEnds = WallWrapping.None, TypeMark = "L2", Cost = 180m };
        var logSquare = new WallType("Log Wall - Square 180", new CompoundStructure(new MaterialLayer(LayerFunction.Structure, softwood.Id, 180)))
            { Function = WallFunction.Exterior, Log = new LogWall(LogShape.Square, 180, 200), WrapAtEnds = WallWrapping.None, TypeMark = "L3", Cost = 160m };

        foreach (var type in new ElementType[]
                 {
                     generic, exterior, partition,
                     dadoProfile, skirting, cornice, dado, reveal, shadowGap,
                     fascia, whiteFascia, halfRound, boxGutter, soffitBoard, soffits[0], soffits[1], soffits[2], soffits[3],
                     soffits[4], soffits[5], soffits[6], soffits[7],
                     gutters[0], gutters[1], gutters[2], gutters[3], gutters[4], gutters[5], gutters[6], gutters[7], gutters[8],
                     fascias[0], fascias[1], fascias[2], fascias[3], fascias[4], fascias[5], fascias[6], fascias[7], fascias[8],
                     eaves[0], eaves[1], eaves[2], eaves[3], eaves[4], eaves[5], eaves[6], eaves[7], eaves[8], eaves[9],
                     roofWindow, tallRoofWindow, escapeRoofWindow, fixedRooflight,
                     storefront, plainGlass,
                     mullions[0], mullions[1], mullions[2], mullions[3], mullions[4], mullions[5], mullions[6], mullions[7], mullions[8],
                     mullions[9], mullions[10], stickSystem,
                     singleDoor, doubleDoor, twinSlider, singleSlider, frenchDoor, glazedDoor, bifold, entrance,
                     curtainSingle, curtainDouble, curtainSlider, frontDoor, fireDoor, wideDoor, garageDoor, usExterior,
                     casement, picture, casementDouble, georgian, awning, hopper, slidingWindow,
                     tiltAndTurn, doubleHung, louvred, bay, rooflight, threeLight, fanlights, fixedFanlight, twinDoubleHung,
                     screedFloor, timberFloor, plasterboardCeiling, flatRoof, pitchedRoof,
                     logRound, logD, logSquare, acousticPartition, timberFrame, warmRoof, loftCeiling, plainTileRoof, interlockingRoof,
                     thickenedEdge, edgeBeam, kerb, partyWall, soffitCeiling
                 })
        {
            document.AddType(type);
        }

        foreach (var curtain in document.TypesOf<CurtainWallType>()) curtain.TakeDepth(document);

        // Dimension styles: millimetres with ticks, as UK building drawings are dimensioned, first.
        foreach (var type in Annotation.DimensionTypes.Defaults()) document.AddType(type);

        // The schedules a drawing set has, to change, copy or add to.
        document.Schedules.AddRange(ScheduleDefinition.Defaults());

        // Keynotes to start from, until an office's own file is loaded; and Uniclass's codes for what is drawn here.
        document.Keynotes.CopyFrom(Annotation.KeynoteTable.Starter());
        document.Classification.CopyFrom(Parameters.ClassificationTable.Starter());

        return document;
    }
}
