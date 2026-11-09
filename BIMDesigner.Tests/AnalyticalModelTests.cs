using System.IO;
using System.IO.Compression;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Infrastructure.Interoperability;
using BIMDesigner.Infrastructure.Serialization;
using Xbim.Ifc4.Interfaces;

namespace BIMDesigner.Tests;

/// <summary>
/// The analytical model worked out from the model - members, plates, nodes joined where they meet,
/// supports where the foundations are - its loads by BS EN 1991 and ASCE 7-16, its combinations by
/// BS EN 1990 and ASCE 7-16, and it written out as IFC4's structural analysis view and as SAF.
/// </summary>
public class AnalyticalModelTests
{
    /// <summary>The 10 by 8 m house, its outside walls bearing, a strip footing under each.</summary>
    private static (BimDocument Document, List<Wall> Walls, Roof Roof) House(bool footings = true)
    {
        var (document, roof) = EaveTests.House(hip: false);
        var walls = document.Walls.ToList();
        foreach (var wall in walls) wall.StructuralUsage = StructuralUsage.Bearing;
        if (footings)
            foreach (var wall in walls)
                document.Add(Footings.Fit(document, wall));
        return (document, walls, roof);
    }

    /// <summary>A 6 by 4 m steel frame: columns up to the first floor, beams between their tops, one framing into the middle of two, a pad under each column.</summary>
    private static (BimDocument Document, List<Column> Columns, List<Beam> Beams) Frame(bool pads = true)
    {
        var document = BimDocument.CreateDefault();
        var (ground, first) = (document.Levels[0], document.Levels[1]);
        var columnType = document.TypesOf<ColumnType>().First(type => type.Structural && type.Shape == ColumnShape.IBeam);
        var beamType = document.TypesOf<BeamType>().First(type => type.Section == BeamSection.IBeam);
        var corners = new[] { new Point2D(0, 0), new Point2D(6000, 0), new Point2D(6000, 4000), new Point2D(0, 4000) };
        var columns = corners.Select(corner => new Column { TypeId = columnType.Id, LevelId = ground.Id, TopLevelId = first.Id, Location = corner, Structural = true }).ToList();
        foreach (var column in columns) document.Add(column);
        var beams = Enumerable.Range(0, 4).Select(i => new Beam { TypeId = beamType.Id, LevelId = first.Id, Start = corners[i], End = corners[(i + 1) % 4] }).ToList();
        beams.Add(new Beam { TypeId = beamType.Id, LevelId = first.Id, Start = new Point2D(3000, 0), End = new Point2D(3000, 4000) });
        foreach (var beam in beams) document.Add(beam);
        if (pads)
            foreach (var column in columns)
                document.Add(Footings.Fit(document, column));
        return (document, columns, beams);
    }

    [Fact]
    public void AHouseOfBearingWallsIsPlatesJoinedAtItsCorners()
    {
        var (document, walls, _) = House();
        var model = AnalyticalModel.Of(document);

        Assert.Equal(4, model.Panels.Count);
        Assert.All(model.Panels, panel => Assert.Equal(AnalyticalPanelKind.Wall, panel.Kind));

        // Its plate is its structure: the 140 aircrete inner leaf, masonry, at the middle of it.
        Assert.All(model.Panels, panel =>
        {
            Assert.Equal(140, panel.Thickness);
            Assert.Equal(AnalyticalMaterialKind.Masonry, panel.Material.Kind);
            Assert.Equal("Centre", panel.SystemPlane);
        });

        // Each corner one node, top and bottom, shared by the two walls meeting there.
        Assert.Equal(8, model.Nodes.Count);
        foreach (var node in model.Nodes)
            Assert.Equal(2, model.Panels.Count(panel => panel.Outline.Contains(node)));

        // Held along the foot of each wall, pinned; nothing wrong with it.
        Assert.Equal(4, model.Supports.Count);
        Assert.All(model.Supports, support => Assert.True(support.IsLine));
        Assert.Empty(model.Problems);

        // A wall set non-bearing is not in it, and what stood on it is said to stand on nothing.
        walls[0].StructuralUsage = StructuralUsage.NonBearing;
        var without = AnalyticalModel.Of(document);
        Assert.Equal(3, without.Panels.Count);
        Assert.Contains("Non-bearing", AnalyticalModel.Said(document, walls[0]));
        Assert.Contains("a plate 140 thick", AnalyticalModel.Said(document, walls[1]));
    }

    [Fact]
    public void AWallTeedIntoAnotherSplitsIt()
    {
        var (document, walls, _) = House();
        var inner = new Wall
        {
            Start = new Point2D(5000, 0), End = new Point2D(5000, 8000), LevelId = document.Levels[0].Id, UnconnectedHeight = 3000,
            TypeId = document.TypesOf<WallType>().First(type => type.Function == WallFunction.Interior).Id, StructuralUsage = StructuralUsage.Bearing
        };
        document.Add(inner);
        var model = AnalyticalModel.Of(document);

        // The south and north walls split where it meets them, the inside wall joined there, top and foot.
        Assert.Equal(2, model.Panels.Count(panel => panel.Element == walls[0]));
        Assert.Equal(2, model.Panels.Count(panel => panel.Element == walls[2]));
        var tee = model.Panels.Single(panel => panel.Element == inner);
        Assert.All(tee.Outline, node => Assert.True(model.Panels.Count(panel => panel.Outline.Contains(node)) >= 3, $"{node.Name} at the tee is shared"));
    }

    [Fact]
    public void DoorsAndWindowsAreCutOutOfTheirWallsPlate()
    {
        var (document, walls, _) = House();
        var level = document.Levels[0].Id;
        document.Add(new Door { HostWallId = walls[0].Id, TypeId = document.TypesOf<DoorType>().First().Id, LevelId = level, DistanceAlongWall = 3000 });
        document.Add(new Window { HostWallId = walls[0].Id, TypeId = document.TypesOf<WindowType>().First().Id, LevelId = level, DistanceAlongWall = 7000, SillHeight = 900 });
        var model = AnalyticalModel.Of(document);
        var south = model.Panels.Single(panel => panel.Element == walls[0]);

        // The door notched out of its foot, the window a hole in it; the foot either side of the door still held.
        Assert.Equal(8, south.Outline.Count);
        Assert.Single(south.Openings);
        var window = document.TypesOf<WindowType>().First();
        Assert.Equal(window.Width * window.Height, Polygon2D.Area(south.Openings[0].Select(south.Flat).ToList()), 0);
        Assert.Equal(2, model.Supports.Count(support => support.Panel == south));
        Assert.Empty(model.Problems);
    }

    [Fact]
    public void ARaftIsAPlateOnTheGroundTheWallsStandOn()
    {
        var (document, walls, _) = House(footings: false);
        var raft = Rafts.Under(document, document.Levels[0].Id, new Point2D(5000, 4000))!;
        document.Add(raft);
        var model = AnalyticalModel.Of(document);
        var plate = model.Panels.Single(panel => panel.Kind == AnalyticalPanelKind.Raft);

        // Its edges taken in to the walls' planes, its corners theirs; on the ground's stiffness - firm clay, 25 MN/m³.
        Assert.Equal(4, plate.Outline.Count);
        Assert.All(plate.Outline, node => Assert.Equal(3, model.Panels.Count(panel => panel.Outline.Contains(node))));
        Assert.Equal(25, plate.Subgrade);
        Assert.Equal(raft.Thickness, plate.Thickness);
        Assert.True(model.IsHeld);
        Assert.Empty(model.Supports);
        Assert.Empty(model.Problems);

        // In SAF, the ground under it.
        var ground = SafExport.Build(document).Single(sheet => sheet.Name == "StructuralSurfaceConnection");
        Assert.Equal(25.0, (double?)ground.Rows.Single()[7]);
    }

    [Fact]
    public void BeamsLandOnColumnsAndFrameIntoEachOther()
    {
        var (document, columns, beams) = Frame();
        var model = AnalyticalModel.Of(document);

        Assert.Equal(4, model.Members.Count(member => member.Kind == AnalyticalMemberKind.Column));
        Assert.Equal(5, model.Members.Count(member => member.Kind == AnalyticalMemberKind.Beam));

        // Each beam's ends on the columns' tops; the one across framing into the middle of two, a node of each.
        foreach (var beam in model.Members.Where(member => member.Element == beams[0] || member.Element == beams[1]))
            Assert.Contains(model.Members.Where(member => member.Kind == AnalyticalMemberKind.Column), column => column.End == beam.Start);
        var south = model.Members.Single(member => member.Element == beams[0]);
        var across = model.Members.Single(member => member.Element == beams[4]);
        Assert.Equal(3, south.Nodes.Count);
        Assert.Same(across.Start, south.Nodes[1]);
        Assert.Equal(new Point3D(3000, 0, 3000), across.Start.At);

        // Held at the foot of each column, pinned; each beam pinned at its ends, as a simple connection is.
        Assert.Equal(4, model.Supports.Count(support => support.Node is not null && support.Fixity == SupportFixity.Pinned));
        Assert.All(model.Members.Where(member => member.Kind == AnalyticalMemberKind.Beam), beam => Assert.Equal(MemberRelease.Pinned, beam.StartRelease));
        Assert.Empty(model.Problems);

        // Its section and material as an analysis program takes them.
        Assert.Equal("I Section", south.Section.Shape);
        Assert.Equal("S355", south.Section.Material.Quality);
        Assert.Equal(210000, south.Section.Material.Modulus);
        Assert.Equal("Top", south.SystemLine);
    }

    [Fact]
    public void WhatIsNotJoinedUpIsSaid()
    {
        var (document, columns, beams) = Frame(pads: false);
        var model = AnalyticalModel.Of(document);
        Assert.Equal(4, model.Problems.Count(problem => problem.Said.Contains("foot is joined to nothing")));
        Assert.Contains(model.Problems, problem => problem.Said.Contains("held by nothing"));
        Assert.Contains(Warnings.Of(document), warning => warning.Heading == "Analytical model" && warning.Said.Contains("foot is joined to nothing"));

        // A beam bearing on a wall that is not bearing: set it bearing.
        var (house, walls, _) = House();
        walls[0].StructuralUsage = StructuralUsage.NonBearing;
        var beam = new Beam
        {
            TypeId = house.TypesOf<BeamType>().First().Id, LevelId = house.Levels[1].Id, Start = new Point2D(5000, 0), End = new Point2D(5000, 8000)
        };
        house.Add(beam);
        var said = AnalyticalModel.Of(house).Problems.Where(problem => problem.Elements.Contains(beam)).Select(problem => problem.Said).ToList();
        Assert.Contains(said, text => text.Contains("not bearing: set the wall's Structural Usage to Bearing"));
        Assert.DoesNotContain(said, text => text.StartsWith("Its end", StringComparison.Ordinal));
    }

    [Fact]
    public void AConcreteFloorIsAPlateOnWhatCarriesItATimberOneSpansOntoTheWalls()
    {
        // A concrete floor on the frame: a plate at its top, its corners the columns' tops.
        var (frame, _, _) = Frame();
        var concrete = frame.TypesOf<FloorType>().First(type => type.Name.StartsWith("Floor - Screed", StringComparison.Ordinal));
        var slab = new Floor { TypeId = concrete.Id, LevelId = frame.Levels[1].Id };
        slab.SetBoundary(new[] { new Point2D(0, 0), new Point2D(6000, 0), new Point2D(6000, 4000), new Point2D(0, 4000) });
        frame.Add(slab);
        var model = AnalyticalModel.Of(frame);
        var plate = model.Panels.Single();
        Assert.Equal(AnalyticalPanelKind.Floor, plate.Kind);
        Assert.Equal(100, plate.Thickness);
        Assert.Equal("Top", plate.SystemPlane);
        Assert.All(model.Members.Where(member => member.Kind == AnalyticalMemberKind.Column), column => Assert.Contains(column.End, plate.Outline));
        Assert.Equal(6, plate.Outline.Count);

        // On it, 1.5 kN/m² imposed - a home's - and its screed and insulation over the self weight.
        var loading = StructuralLoading.Of(frame, model);
        var imposed = loading.SurfaceLoads.Single(load => load.Case.Nature == LoadNature.Imposed);
        Assert.Equal(-1.5, imposed.Force.Z, 6);
        var dead = loading.SurfaceLoads.Single(load => load.Case.Nature == LoadNature.Dead && load.Panel == plate);
        Assert.Equal(plate.Weight - plate.SelfWeight, -dead.Force.Z, 6);
        Assert.InRange(plate.SelfWeight, 2.3, 2.5);

        // A timber floor over the house is no plate: its load goes onto the walls it spans between, the short way.
        var (house, walls, _) = House();
        var timber = house.TypesOf<FloorType>().First(type => type.Name.StartsWith("Floor - Timber", StringComparison.Ordinal));
        var floor = new Floor { TypeId = timber.Id, LevelId = house.Levels[1].Id };
        floor.SetBoundary(new[] { new Point2D(-165, -165), new Point2D(10165, -165), new Point2D(10165, 8165), new Point2D(-165, 8165) });
        house.Add(floor);
        var houseModel = AnalyticalModel.Of(house);
        Assert.DoesNotContain(houseModel.Panels, panel => panel.Element == floor);
        Assert.Contains("spans onto what carries it", AnalyticalModel.Said(house, floor));
        var houseLoads = StructuralLoading.Of(house, houseModel);
        var onWalls = houseLoads.LineLoads.Where(load => load.Case.Nature == LoadNature.Imposed).ToList();
        Assert.All(onWalls, load => Assert.True(load.Panel!.Element == walls[0] || load.Panel.Element == walls[2], "onto the long walls, spanning the short way"));
        var share = houseLoads.Total(houseLoads.Cases.Single(each => each.Nature == LoadNature.Imposed)) / (1.5 * floor.Area / 1e6);
        Assert.InRange(share, 0.9, 1.0);
        Assert.DoesNotContain(houseLoads.Problems, problem => problem.Elements.Contains(floor));
    }

    [Fact]
    public void ARoofBearsOnItsEavesAndTheWindPushesOnItsWalls()
    {
        var (document, walls, roof) = House();
        var model = AnalyticalModel.Of(document);
        var loading = StructuralLoading.Of(document, model);
        var cases = loading.Cases.Select(each => each.Name).ToList();
        Assert.Equal(new[] { "G1", "G2", "Q2", "S1", "S2", "S3", "W1", "W2", "W3", "W4" }, cases);

        // The gable roof's slopes on the south and north walls, the whole of each: its weight up the slope on plan.
        var roofType = document.FindType<SlabType>(roof.TypeId)!;
        var dead = loading.LineLoads.Where(load => load.Case.Name == "G2").ToList();
        Assert.All(dead, load => Assert.True(load.Panel!.Element == walls[0] || load.Panel.Element == walls[2]));
        var stretch = 1 / Math.Cos(35 * Math.PI / 180);
        var expected = roof.Area / 1e6 * Loads.Weight(document, roofType.Structure) * stretch;
        Assert.InRange(-dead.Sum(load => load.Force.Z * load.Length / 1000), expected * 0.99, expected * 1.01);

        // Snow at 35°: μ1 0.8 x 25/30 on zone 1's 0.30 kN/m²; what is put on it, 0.6 x 25/30.
        var snow = loading.Total(loading.Cases.Single(each => each.Name == "S1"));
        Assert.InRange(snow, roof.Area / 1e6 * 0.2 * 0.99, roof.Area / 1e6 * 0.2 * 1.01);
        Assert.Equal(0.5, StructuralLoading.RoofImposed(document, 35), 6);

        // The wind from the west: the west wall pushed, the east one sucked, both toward +x - zones D and E for h/d 0.3, by 0.85 - and the side walls drawn off square to it.
        var west = loading.SurfaceLoads.Where(load => load.Case.Name == "W1").ToList();
        Assert.Equal(4, west.Count);
        var along = west.Where(load => Math.Abs(load.Force.X) > 1e-9).ToList();
        Assert.Equal(2, along.Count);
        Assert.All(along, load => Assert.True(load.Force.X > 0 && load.Force.Y == 0));
        var pushed = along.MaxBy(load => load.Force.X)!;
        Assert.Same(walls[3], pushed.Panel.Element);
        Assert.Equal(0.85 * (0.7 + 0.1 * (0.3 - 0.25) / 0.75) * document.ProjectInformation.WindPressure, pushed.Force.X, 4);
    }

    [Fact]
    public void TheCombinationsAreTheStandardsOwn()
    {
        // The UK: Equation NA.1, and BS EN 1990's 6.10a and 6.10b with Table NA.A1.1's ψ.
        Assert.Equal(0.30, StructuralLoading.GroundSnow(1, 50), 6);
        Assert.Equal(0.15 + 0.35 + 100 / 525.0, StructuralLoading.GroundSnow(3, 200), 6);
        Assert.Equal(0.8, StructuralLoading.ShapeCoefficient(20), 6);
        Assert.Equal(0, StructuralLoading.ShapeCoefficient(60), 6);

        var (document, _, _) = House();
        var loading = StructuralLoading.Of(document, AnalyticalModel.Of(document));
        var said = loading.Combinations.Select(each => each.Said).ToList();
        Assert.Contains("1.35 G1 + 1.35 G2 + 1.05 Q2", said);
        Assert.Contains("1.35 G1 + 1.35 G2 + 0.75 S1 + 0.75 W1", said);
        Assert.Contains("1.25 G1 + 1.25 G2 + 1.5 S1 + 0.75 W1", said);
        Assert.Contains("1.25 G1 + 1.25 G2 + 1.5 W3 + 0.75 S1", said);
        Assert.Contains("1 G1 + 1 G2 + 1.5 W2", said);
        Assert.Contains(loading.Combinations, each => each.State == LimitState.Serviceability && each.Description.Contains("6.14b"));

        // The US: ASCE 7-16 2.3.1 and 2.4.1, the snow on its roof by 7.3 and 7.4.
        var (us, _, _) = House();
        us.ProjectInformation.ClearanceStandard = ClearanceStandard.Irc;
        us.ProjectInformation.GroundSnowLoad = 30;
        Assert.Equal(0.7 * 30, StructuralLoading.RoofSnowPsf(us, 20), 6);
        Assert.Equal(21, StructuralLoading.RoofSnowPsf(us, 5), 6);
        Assert.Equal(0.7 * 30 * 30 / 40, StructuralLoading.RoofSnowPsf(us, 40), 6);
        us.ProjectInformation.GroundSnowLoad = 15;
        Assert.Equal(15, StructuralLoading.RoofSnowPsf(us, 5), 6);
        us.ProjectInformation.GroundSnowLoad = 30;
        var usLoading = StructuralLoading.Of(us, AnalyticalModel.Of(us));
        Assert.Equal("D1", usLoading.Cases[0].Name);
        var usSaid = usLoading.Combinations.Select(each => each.Said).ToList();
        Assert.Contains("1.4 D1 + 1.4 D2", usSaid);
        Assert.Contains("1.2 D1 + 1.2 D2 + 1.6 S1 + 0.5 W1", usSaid);
        Assert.Contains("0.9 D1 + 0.9 D2 + 1 W4", usSaid);
        Assert.Contains("0.6 D1 + 0.6 D2 + 0.6 W2", usSaid);
    }

    [Fact]
    public void ItIsSavedCopiedAndWrittenOutForAnalysis()
    {
        var (document, columns, beams) = Frame();
        beams[0].StartRelease = MemberRelease.Fixed;
        var pad = document.All<PadFooting>().First();
        pad.Fixity = SupportFixity.Fixed;
        document.ProjectInformation.SnowZone = 3;
        var concrete = document.TypesOf<FloorType>().First(type => type.Name.StartsWith("Floor - Screed", StringComparison.Ordinal));
        var slab = new Floor { TypeId = concrete.Id, LevelId = document.Levels[1].Id, AnalyseAs = FloorAnalysis.OneWay };
        slab.SetBoundary(new[] { new Point2D(0, 0), new Point2D(6000, 0), new Point2D(6000, 4000), new Point2D(0, 4000) });
        document.Add(slab);

        var path = Path.Combine(Path.GetTempPath(), $"analytical-{Guid.NewGuid():N}.bimx");
        try
        {
            ProjectFile.Save(document, path);
            var opened = ProjectFile.Load(path);
            Assert.Equal(MemberRelease.Fixed, opened.Find<Beam>(beams[0].Id)!.StartRelease);
            Assert.Equal(MemberRelease.Pinned, opened.Find<Beam>(beams[0].Id)!.EndRelease);
            Assert.Equal(SupportFixity.Fixed, opened.Find<PadFooting>(pad.Id)!.Fixity);
            Assert.Equal(FloorAnalysis.OneWay, opened.Find<Floor>(slab.Id)!.AnalyseAs);
            Assert.Equal(3, opened.ProjectInformation.SnowZone);
        }
        finally
        {
            File.Delete(path);
        }

        var copy = (Beam)ElementCopy.Clone(beams[0])!;
        Assert.Equal(MemberRelease.Fixed, copy.StartRelease);

        // IFC4: the analysis model, its members split at their nodes, its supports and cases.
        var model = AnalyticalModel.Of(document);
        using var ifc = IfcExport.Build(document, new IfcExportOptions { Analytical = true });
        var analysis = ifc.Instances.OfType<IIfcStructuralAnalysisModel>().Single();
        Assert.Equal(IfcAnalysisModelTypeEnum.LOADING_3D, analysis.PredefinedType);
        Assert.Equal(model.Members.Sum(member => member.Nodes.Count - 1), ifc.Instances.OfType<IIfcStructuralCurveMember>().Count());
        Assert.Equal(model.Nodes.Count, ifc.Instances.OfType<IIfcStructuralPointConnection>().Count());
        var held = ifc.Instances.OfType<IIfcStructuralPointConnection>().Where(each => each.AppliedCondition is IIfcBoundaryNodeCondition).ToList();
        Assert.Equal(4, held.Count);
        var linked = ifc.Instances.OfType<IIfcRelAssignsToProduct>().Where(relation => relation.RelatedObjects.OfType<IIfcStructuralCurveMember>().Any()).ToList();
        Assert.Equal(ifc.Instances.OfType<IIfcBeam>().Count() + columns.Count, linked.Count);
        Assert.Equal(4, ifc.Instances.OfType<IIfcRelAssignsToProduct>().Count(relation => relation.RelatingProduct is IIfcColumn));
        Assert.Contains(ifc.Instances.OfType<IIfcStructuralLoadCase>(), each => each.Name == "G1" && each.SelfWeightCoefficients.Count == 3);
        Assert.Contains(ifc.Instances.OfType<IIfcStructuralLoadGroup>(), each => each.PredefinedType == IfcLoadGroupTypeEnum.LOAD_COMBINATION);
        Assert.NotEmpty(ifc.Instances.OfType<IIfcStructuralLinearAction>());
        Assert.Contains("StructuralAnalysisView", string.Join(" ", ifc.Header.FileDescription.Description));

        // Without being asked, the export is the building alone.
        using var plain = IfcExport.Build(document);
        Assert.Empty(plain.Instances.OfType<IIfcStructuralAnalysisModel>());

        // SAF: a sheet to each kind, named as the format names them.
        var sheets = SafExport.Build(document);
        var names = sheets.Select(sheet => sheet.Name).ToList();
        foreach (var name in new[] { "Model", "Project", "StructuralMaterial", "StructuralCrossSection", "StructuralPointConnection", "StructuralCurveMember", "StructuralSurfaceMember",
                     "StructuralPointSupport", "StructuralEdgeConnection", "RelConnectsStructuralMember", "StructuralLoadGroup", "StructuralLoadCase", "StructuralLoadCombination", "StructuralCurveAction" })
            Assert.Contains(name, names);
        var model0 = sheets.Single(sheet => sheet.Name == "Model");
        Assert.Contains(model0.Rows, row => (string?)row[0] == "SAF Version" && (string?)row[1] == "2.2.0");
        Assert.Contains(model0.Rows, row => (string?)row[0] == "National code" && (string?)row[1] == "EC-BS-EN (British NA)");
        Assert.True(9 == sheets.Single(sheet => sheet.Name == "StructuralCurveMember").Rows.Count, string.Join(", ", sheets.Single(sheet => sheet.Name == "StructuralCurveMember").Rows.Select(row => row[0])) + " | " + string.Join(", ", model.Members.Select(member => member.Name + ":" + member.Kind)));
        var hinges = sheets.Single(sheet => sheet.Name == "RelConnectsStructuralMember").Rows;
        Assert.Contains(hinges, row => (string?)row[2] == "End");
        Assert.Equal(4, hinges.Count(row => (string?)row[2] == "Both"));
        var supports = sheets.Single(sheet => sheet.Name == "StructuralPointSupport").Rows;
        Assert.Single(supports, row => (string?)row[1] == "Fixed");

        var file = Path.Combine(Path.GetTempPath(), $"analytical-{Guid.NewGuid():N}.xlsx");
        try
        {
            SafExport.Save(document, file);
            using var zip = ZipFile.OpenRead(file);
            Assert.Contains(zip.Entries, entry => entry.FullName == "xl/workbook.xml");
        }
        finally
        {
            File.Delete(file);
        }
    }
}
