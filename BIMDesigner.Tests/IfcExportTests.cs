using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Infrastructure.Interoperability;
using Xbim.Ifc;
using Xbim.Ifc4.GeometricConstraintResource;
using Xbim.Ifc4.GeometricModelResource;
using Xbim.Ifc4.GeometryResource;
using Xbim.Ifc4.Interfaces;
using Xbim.Ifc4.Kernel;
using Xbim.Ifc4.MaterialResource;
using Xbim.Ifc4.MeasureResource;
using Xbim.Ifc4.ProductExtension;
using Xbim.Ifc4.PropertyResource;
using Xbim.Ifc4.SharedBldgElements;
using Xbim.Ifc4.UtilityResource;

using CoreWindow = BIMDesigner.Core.Architecture.Window;

namespace BIMDesigner.Tests;

/// <summary>
/// Tests over the IFC export (specification section 8).
///
/// Export is where the model leaves us for software we do not control, so these check the
/// things a receiving application depends on: that the spatial tree is connected, that a
/// wall is a wall with its layers intact, that a door leaves a real hole, and that the same
/// project exported twice says the same thing.
/// </summary>
public class IfcExportTests
{
    private const double Tolerance = 1e-6;

    /// <summary>A two-storey project: four walls, a door, a window, a floor and a room.</summary>
    private static (BimDocument Document, Wall Wall, Door Door, CoreWindow Window, Floor Floor, Room Room) Project()
    {
        var document = BimDocument.CreateDefault();
        document.ProjectInformation.Name = "Riverside House";
        document.ProjectInformation.Address = "1 River Lane";

        var type = document.TypesOf<WallType>().Single(t => t.Name.StartsWith("Exterior"));
        var ground = document.Levels[0].Id;

        Point2D[] corners = { new(0, 0), new(6000, 0), new(6000, 4000), new(0, 4000) };
        var walls = new List<Wall>();

        for (var i = 0; i < corners.Length; i++)
        {
            var wall = new Wall
            {
                Start = corners[i],
                End = corners[(i + 1) % corners.Length],
                TypeId = type.Id,
                LevelId = ground,
                UnconnectedHeight = 3000,
                Mark = $"W{i + 1}"
            };

            document.Add(wall);
            walls.Add(wall);
        }

        var door = new Door
        {
            TypeId = document.TypesOf<DoorType>().First(t => t.Width == 900).Id,
            LevelId = ground,
            HostWallId = walls[0].Id,
            DistanceAlongWall = 2000,
            Mark = "D-01"
        };
        document.Add(door);

        var window = new CoreWindow
        {
            TypeId = document.TypesOf<WindowType>().First(t => t.Width == 1200).Id,
            LevelId = ground,
            HostWallId = walls[0].Id,
            DistanceAlongWall = 4500,
            SillHeight = 900
        };
        document.Add(window);

        var floor = new Floor { TypeId = document.TypesOf<FloorType>().First().Id, LevelId = ground };
        floor.SetBoundary(corners);
        document.Add(floor);

        var room = new Room { Location = new Point2D(3000, 2000), LevelId = ground, Name = "Living", Number = "01" };
        document.Add(room);

        return (document, walls[0], door, window, floor, room);
    }

    // ---- the file itself ---------------------------------------------------------

    [Fact]
    public void AnExportedProjectIsAWellFormedIfcStepFile()
    {
        var (document, _, _, _, _, _) = Project();
        var path = Path.Combine(Path.GetTempPath(), $"bimdesigner-{Guid.NewGuid():N}.ifc");

        try
        {
            IfcExport.Save(document, path);

            var text = File.ReadAllText(path);

            Assert.StartsWith("ISO-10303-21;", text);
            Assert.Contains("FILE_SCHEMA (('IFC4'))", text);
            Assert.Contains("IFCWALL", text);
            Assert.EndsWith("END-ISO-10303-21;", text.TrimEnd());

            // The header declares what the file is. A blank one is treated with suspicion by
            // the applications that read it.
            Assert.Contains("ViewDefinition [CoordinationView]", text);
            Assert.Contains("BIMDesigner", text);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void AnEmptyProjectExportsAProjectRatherThanThrowing()
    {
        using var model = IfcExport.Build(new BimDocument());

        Assert.Single(model.Instances.OfType<IfcProject>());
        Assert.Empty(model.Instances.OfType<IfcWall>());
    }

    // ---- units and the spatial tree ----------------------------------------------

    [Fact]
    public void TheFileIsInMillimetresSoNothingIsScaledOnTheWayOut()
    {
        var (document, _, _, _, _, _) = Project();
        using var model = IfcExport.Build(document);

        var length = model.Instances.OfType<IfcSIUnit>().Single(unit => unit.UnitType == IfcUnitEnum.LENGTHUNIT);

        Assert.Equal(IfcSIUnitName.METRE, length.Name);
        Assert.Equal(IfcSIPrefix.MILLI, length.Prefix);
    }

    [Fact]
    public void ProjectSiteBuildingAndStoreysFormOneConnectedTree()
    {
        var (document, _, _, _, _, _) = Project();
        using var model = IfcExport.Build(document);

        var project = Assert.Single(model.Instances.OfType<IfcProject>());
        var site = Assert.Single(model.Instances.OfType<IfcSite>());
        var building = Assert.Single(model.Instances.OfType<IfcBuilding>());
        var storeys = model.Instances.OfType<IfcBuildingStorey>().ToList();

        Assert.Equal(document.Levels.Count, storeys.Count);

        // A storey that cannot be reached from the project is a storey most viewers will not
        // show - the trap FreeCAD's exporter warns about in its own source.
        Assert.True(IsAggregated(model, project, site));
        Assert.True(IsAggregated(model, site, building));
        Assert.All(storeys, storey => Assert.True(IsAggregated(model, building, storey)));
    }

    [Fact]
    public void EachStoreyCarriesItsOwnElevation()
    {
        var (document, _, _, _, _, _) = Project();
        using var model = IfcExport.Build(document);

        foreach (var level in document.Levels)
        {
            var storey = model.Instances.OfType<IfcBuildingStorey>().Single(s => s.Name == level.Name);
            Assert.Equal(level.Elevation, (double)storey.Elevation!.Value, Tolerance);
        }
    }

    [Fact]
    public void ElementsAreContainedInTheStoreyTheyStandOn()
    {
        var (document, wall, _, _, _, _) = Project();
        using var model = IfcExport.Build(document);

        var ground = document.Levels[0];
        var exported = Find<IfcWall>(model, wall.Id);

        var containment = model.Instances.OfType<IfcRelContainedInSpatialStructure>()
            .Single(relation => relation.RelatedElements.Contains(exported));

        Assert.Equal(ground.Name, containment.RelatingStructure.Name);
    }

    // ---- walls -------------------------------------------------------------------

    [Fact]
    public void EveryWallIsExportedAsAWallOfTheRightHeight()
    {
        var (document, _, _, _, _, _) = Project();
        using var model = IfcExport.Build(document);

        var walls = model.Instances.OfType<IfcWall>().ToList();
        Assert.Equal(document.Walls.Count(), walls.Count);

        foreach (var solid in walls.Select(BodyOf))
            Assert.Equal(3000, (double)solid.Depth, Tolerance);
    }

    [Fact]
    public void AWallCarriesItsLayersRatherThanArrivingAsAnAnonymousLump()
    {
        var (document, wall, _, _, _, _) = Project();
        using var model = IfcExport.Build(document);

        var type = document.GetWallType(wall)!;
        var exported = Find<IfcWall>(model, wall.Id);

        var association = model.Instances.OfType<IfcRelAssociatesMaterial>()
            .Single(relation => relation.RelatedObjects.Contains(exported));

        var usage = Assert.IsAssignableFrom<IfcMaterialLayerSetUsage>(association.RelatingMaterial);
        var layers = usage.ForLayerSet.MaterialLayers.ToList();

        // This is the part most exporters lose: a layered wall flattened into one solid takes
        // its fire rating, U-value and takeoff with it.
        Assert.Equal(type.Structure.Layers.Count, layers.Count);

        Assert.Equal(
            type.Structure.Layers.Select(layer => Math.Round(layer.Thickness, 6)),
            layers.Select(layer => Math.Round((double)layer.LayerThickness, 6)));

        Assert.Equal(
            type.Structure.Layers.Select(layer => document.FindMaterial(layer.MaterialId)!.Name),
            layers.Select(layer => layer.Material?.Name.ToString()));
    }

    [Fact]
    public void WallsOfOneTypeShareOneTypeObjectAndOneLayerSet()
    {
        var (document, _, _, _, _, _) = Project();
        using var model = IfcExport.Build(document);

        // Four walls, all of the same type: one IfcWallType, and one layer set between them.
        // Repeating the build-up per wall would bloat the file and let four copies of one
        // construction drift apart in whatever opens it.
        Assert.Single(model.Instances.OfType<IfcWallType>());

        var wallLayerSets = model.Instances.OfType<IfcRelAssociatesMaterial>()
            .Where(relation => relation.RelatedObjects.OfType<IfcWall>().Any())
            .Select(relation => relation.RelatingMaterial)
            .OfType<IfcMaterialLayerSetUsage>()
            .Select(usage => usage.ForLayerSet)
            .Distinct()
            .ToList();

        Assert.Single(wallLayerSets);
        Assert.Equal(4, model.Instances.OfType<IfcWall>().Count());
    }

    [Fact]
    public void AWallGetsItsStandardPropertySet()
    {
        var (document, wall, _, _, _, _) = Project();
        using var model = IfcExport.Build(document);

        var exported = Find<IfcWall>(model, wall.Id);
        var common = PropertySet(model, exported, "Pset_WallCommon");

        var external = common.HasProperties.OfType<IfcPropertySingleValue>().Single(p => p.Name == "IsExternal");
        Assert.Equal("true", external.NominalValue!.Value!.ToString()!.ToLowerInvariant());

        // And our own parameters travel alongside the standard ones.
        var ours = PropertySet(model, exported, "BIMDesigner_Parameters");
        Assert.Contains(ours.HasProperties.OfType<IfcPropertySingleValue>(), p => p.Name == "Length");
    }

    [Fact]
    public void NumbersAreWrittenAsTheMeasuresTheyActuallyAre()
    {
        var (document, wall, _, _, _, room) = Project();

        // Give the wall type a U-value so there is a thermal transmittance to write.
        document.GetWallType(wall)!.HeatTransferCoefficient = 0.28;

        using var model = IfcExport.Build(document);
        var exported = Find<IfcWall>(model, wall.Id);

        var common = PropertySet(model, exported, "Pset_WallCommon")
            .HasProperties.OfType<IfcPropertySingleValue>()
            .ToDictionary(property => property.Name.ToString(), property => property.NominalValue);

        // A bare real is a number with no idea what it measures: the receiving application
        // cannot convert its units, schedule it, or check it against anything.
        Assert.IsType<IfcThermalTransmittanceMeasure>(common["ThermalTransmittance"]);
        Assert.IsType<IfcBoolean>(common["IsExternal"]);
        Assert.IsType<IfcBoolean>(common["LoadBearing"]);
        Assert.IsType<IfcLabel>(common["FireRating"]);
        Assert.IsType<IfcIdentifier>(common["Reference"]);

        // And the same for the parameters we carry alongside, which already know their kind.
        var ours = PropertySet(model, exported, "BIMDesigner_Parameters")
            .HasProperties.OfType<IfcPropertySingleValue>()
            .ToDictionary(property => property.Name.ToString(), property => property.NominalValue);

        Assert.IsType<IfcLengthMeasure>(ours["Length"]);
        Assert.IsType<IfcAreaMeasure>(ours["Area"]);
        Assert.IsType<IfcVolumeMeasure>(ours["Volume"]);
        Assert.IsType<IfcBoolean>(ours["Room Bounding"]);

        var space = Find<IfcSpace>(model, room.Id);
        var spaceProperties = PropertySet(model, space, "Pset_SpaceCommon")
            .HasProperties.OfType<IfcPropertySingleValue>()
            .ToDictionary(property => property.Name.ToString(), property => property.NominalValue);

        Assert.IsType<IfcAreaMeasure>(spaceProperties["GrossPlannedArea"]);
    }

    [Fact]
    public void ACeilingGetsTheCoveringPropertySetRatherThanTheSlabOne()
    {
        var (document, _, _, _, floor, _) = Project();

        var ceiling = new Ceiling
        {
            TypeId = document.TypesOf<CeilingType>().First().Id,
            LevelId = document.Levels[0].Id,
            HeightOffset = 2700
        };
        ceiling.SetBoundary(floor.Boundary);
        document.Add(ceiling);

        using var model = IfcExport.Build(document);

        var covering = Find<IfcCovering>(model, ceiling.Id);
        Assert.NotNull(PropertySet(model, covering, "Pset_CoveringCommon"));
    }

    // ---- openings ----------------------------------------------------------------

    [Fact]
    public void ADoorLeavesARealHoleInTheWallAndFillsIt()
    {
        var (document, wall, door, _, _, _) = Project();
        using var model = IfcExport.Build(document);

        var exportedWall = Find<IfcWall>(model, wall.Id);
        var exportedDoor = Find<IfcDoor>(model, door.Id);

        var voids = model.Instances.OfType<IfcRelVoidsElement>()
            .Where(relation => relation.RelatingBuildingElement == exportedWall)
            .ToList();

        // Two openings in this wall: the door and the window.
        Assert.Equal(2, voids.Count);

        var fills = model.Instances.OfType<IfcRelFillsElement>()
            .Single(relation => relation.RelatedBuildingElement == exportedDoor);

        // The hole and the thing in it have to be linked, or the receiving application cannot
        // take the door out and leave the opening.
        Assert.Contains(fills.RelatingOpeningElement, voids.Select(v => v.RelatedOpeningElement));

        var doorType = document.FindType<DoorType>(door.TypeId)!;
        Assert.Equal(doorType.Width, (double)exportedDoor.OverallWidth!.Value, Tolerance);
        Assert.Equal(doorType.Height, (double)exportedDoor.OverallHeight!.Value, Tolerance);
    }

    [Fact]
    public void AWindowIsAWindowAtItsSillHeight()
    {
        var (document, _, _, window, _, _) = Project();
        using var model = IfcExport.Build(document);

        var exported = Find<IfcWindow>(model, window.Id);
        var type = document.FindType<WindowType>(window.TypeId)!;

        Assert.Equal(type.Width, (double)exported.OverallWidth!.Value, Tolerance);
        Assert.Equal(type.Height, (double)exported.OverallHeight!.Value, Tolerance);

        var common = PropertySet(model, exported, "Pset_WindowCommon");
        var sill = common.HasProperties.OfType<IfcPropertySingleValue>().Single(p => p.Name == "SillHeight");

        Assert.Equal(900, Convert.ToDouble(sill.NominalValue!.Value), Tolerance);
    }

    // ---- slabs and spaces --------------------------------------------------------

    [Fact]
    public void FloorsRoofsAndCeilingsExportAsWhatTheyAre()
    {
        var (document, _, _, _, floor, _) = Project();

        var level = document.Levels[0].Id;
        var outline = floor.Boundary.ToList();

        var roof = new Roof { TypeId = document.TypesOf<RoofType>().First().Id, LevelId = level, HeightOffset = 3300 };
        roof.SetBoundary(outline);
        document.Add(roof);

        var ceiling = new Ceiling { TypeId = document.TypesOf<CeilingType>().First().Id, LevelId = level, HeightOffset = 2700 };
        ceiling.SetBoundary(outline);
        document.Add(ceiling);

        using var model = IfcExport.Build(document);

        Assert.Equal(IfcSlabTypeEnum.FLOOR, Find<IfcSlab>(model, floor.Id).PredefinedType);

        // A roof is an IfcRoof, not a slab that happens to be on top: IFC keeps the entity
        // apart, and a flat roof is one of its predefined kinds rather than a different thing.
        Assert.Equal(IfcRoofTypeEnum.FLAT_ROOF, Find<IfcRoof>(model, roof.Id).PredefinedType);

        // A ceiling is a finish, not a structural slab, and IFC has a separate entity for it.
        Assert.Equal(IfcCoveringTypeEnum.CEILING, Find<IfcCovering>(model, ceiling.Id).PredefinedType);
    }

    [Fact]
    public void APitchedRoofExportsAsAnAssemblyOfItsFaces()
    {
        var (document, _, _, _, floor, _) = Project();

        var roof = new Roof { TypeId = document.TypesOf<RoofType>().First().Id, LevelId = document.Levels[0].Id, HeightOffset = 3300 };
        roof.SetBoundary(floor.Boundary.ToList());
        roof.SetShape(RoofForm.Gable);
        document.Add(roof);

        using var model = IfcExport.Build(document);

        var exported = Find<IfcRoof>(model, roof.Id);
        Assert.Equal(IfcRoofTypeEnum.GABLE_ROOF, exported.PredefinedType);

        // The two slopes come across as the parts the roof is made of. IFC says the roof's
        // body is then the sum of theirs, so the roof itself carries no shape of its own.
        var faces = model.Instances.OfType<IfcRelAggregates>()
            .Where(relation => relation.RelatingObject == exported)
            .SelectMany(relation => relation.RelatedObjects)
            .OfType<IfcSlab>()
            .ToList();

        Assert.Equal(2, faces.Count);
        Assert.All(faces, face => Assert.Equal(IfcSlabTypeEnum.ROOF, face.PredefinedType));
        Assert.Null(exported.Representation);

        // Each face lies in its own plane, tilted out of horizontal - that is what makes it a
        // pitched roof rather than two flat slabs at different heights.
        foreach (var face in faces)
        {
            var axis = ((IfcLocalPlacement)face.ObjectPlacement!).RelativePlacement as IfcAxis2Placement3D;
            Assert.NotNull(axis!.Axis);
            Assert.True(axis.Axis!.Z < 0.999, "The face was placed flat rather than on its slope.");
        }
    }

    [Fact]
    public void ARoomBecomesASpaceInsideItsStorey()
    {
        var (document, _, _, _, _, room) = Project();
        using var model = IfcExport.Build(document);

        var space = Find<IfcSpace>(model, room.Id);

        Assert.Equal("Living", space.LongName);
        Assert.Equal("01", space.Name);

        // A space is a division of the storey, not an object standing on it.
        var storey = model.Instances.OfType<IfcBuildingStorey>().Single(s => s.Name == document.Levels[0].Name);
        Assert.True(IsAggregated(model, storey, space));
    }

    [Fact]
    public void AnUnenclosedRoomIsLeftOutRatherThanExportedAsNothing()
    {
        var document = BimDocument.CreateDefault();

        // A room floating in the open, with no walls round it.
        document.Add(new Room { Location = new Point2D(0, 0), LevelId = document.Levels[0].Id, Name = "Nowhere" });

        using var model = IfcExport.Build(document);

        // A zero-area space would turn up in someone else's area schedule as a real room.
        Assert.Empty(model.Instances.OfType<IfcSpace>());
    }

    // ---- identity ----------------------------------------------------------------

    [Fact]
    public void ExportingTheSameProjectTwiceGivesTheSameIdentifiers()
    {
        var (document, wall, _, _, _, _) = Project();

        using var first = IfcExport.Build(document);
        using var second = IfcExport.Build(document);

        var a = Find<IfcWall>(first, wall.Id);
        var b = Find<IfcWall>(second, wall.Id);

        // Stable identifiers are what let the receiving end tell an edited wall from a new
        // one. Freshly generated ones would make every re-issue look like a new building.
        Assert.Equal(a.GlobalId.ToString(), b.GlobalId.ToString());
    }

    [Fact]
    public void TheProjectAndBuildingAreNamedAfterTheProjectInformation()
    {
        var (document, _, _, _, _, _) = Project();
        using var model = IfcExport.Build(document);

        Assert.Equal("Riverside House", model.Instances.OfType<IfcProject>().Single().Name);
        Assert.Equal("1 River Lane", model.Instances.OfType<IfcSite>().Single().Name);
    }

    // ---- helpers -----------------------------------------------------------------

    private static T Find<T>(IfcStore model, Guid id) where T : IIfcRoot
    {
        var expected = IfcGloballyUniqueId.ConvertToBase64(id).ToString();
        return model.Instances.OfType<T>().Single(entity => entity.GlobalId.ToString() == expected);
    }

    private static bool IsAggregated(IfcStore model, IIfcObjectDefinition whole, IIfcObjectDefinition part) =>
        model.Instances.OfType<IfcRelAggregates>()
            .Any(relation => relation.RelatingObject == whole && relation.RelatedObjects.Contains(part));

    private static IfcPropertySet PropertySet(IfcStore model, IfcObject element, string name) =>
        model.Instances.OfType<IfcRelDefinesByProperties>()
            .Where(relation => relation.RelatedObjects.Contains(element))
            .Select(relation => relation.RelatingPropertyDefinition)
            .OfType<IfcPropertySet>()
            .Single(set => set.Name == name);

    private static IfcExtrudedAreaSolid BodyOf(IfcProduct product) =>
        product.Representation!.Representations
            .SelectMany(representation => representation.Items)
            .OfType<IfcExtrudedAreaSolid>()
            .First();
}
