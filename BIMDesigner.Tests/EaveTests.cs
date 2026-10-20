using System.IO;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;
using BIMDesigner.Core.Views;
using BIMDesigner.Infrastructure.Serialization;

namespace BIMDesigner.Tests;

/// <summary>
/// Eaves of the five kinds - closed, open, exposed rafter, box and abbreviated - built under a
/// roof's overhang: the roof cut back to its rafters over them, the rafters put out one by one,
/// and what closes them in; and the rules they answer to - the air into a cold roof, fire-stopping
/// where a party wall meets them, and how near a lot line they come.
/// </summary>
public class EaveTests
{
    /// <summary>A house 10 m by 8 m, its walls 330 thick, under a roof at 35° picked off them, overhanging 450 all round: a hip roof, or a gable one with its eaves along the long sides.</summary>
    internal static (BimDocument Document, Roof Roof) House(bool hip, string roofType = "Roof - Slate on Timber Rafters 150mm", double overhang = 450)
    {
        var document = BimDocument.CreateDefault();
        var level = document.Levels[0].Id;
        var wallType = document.TypesOf<WallType>().First(t => t.Name.StartsWith("Exterior"));
        var (width, depth) = (10000.0, 8000.0);
        var corners = new[] { new Point2D(0, 0), new Point2D(width, 0), new Point2D(width, depth), new Point2D(0, depth) };
        var walls = new List<Wall>();
        for (var i = 0; i < 4; i++)
        {
            var wall = new Wall { Start = corners[i], End = corners[(i + 1) % 4], TypeId = wallType.Id, LevelId = level, UnconnectedHeight = 3000 };
            document.Add(wall);
            walls.Add(wall);
        }

        var out_ = wallType.Structure.TotalWidth / 2 + overhang;
        var roof = new Roof { TypeId = document.TypesOf<RoofType>().First(t => t.Name == roofType).Id, LevelId = level, HeightOffset = 3000 };
        roof.SetBoundary(new[] { new Point2D(-out_, -out_), new Point2D(width + out_, -out_), new Point2D(width + out_, depth + out_), new Point2D(-out_, depth + out_) });
        roof.SetEdges(Enumerable.Range(0, 4).Select(i => new RoofEdge
        {
            DefinesSlope = hip || i % 2 == 0, SlopeDegrees = 35, WallId = walls[i].Id, OnLeftOfWall = false, Overhang = overhang
        }));
        document.Add(roof);
        return (document, roof);
    }

    private static Eave AddEave(BimDocument document, Roof roof, string typeName)
    {
        var type = document.TypesOf<EaveType>().Single(candidate => candidate.Name == typeName);
        var eave = new Eave { RoofId = roof.Id, LevelId = roof.LevelId, TypeId = type.Id };
        eave.EdgeIds.AddRange(Eaves.EaveEdges(document, roof));
        document.Add(eave);
        return eave;
    }

    /// <summary>The roof's south eave runs along y = -615: the wall's face at -165, 450 in from it.</summary>
    private const double Edge = -615, WallFace = -165;

    [Fact]
    public void TheFiveKindsAreTheProjectsEaveTypes()
    {
        var types = BimDocument.CreateDefault().TypesOf<EaveType>().ToList();
        Assert.Equal(Enum.GetValues<EaveKind>().ToHashSet(), types.Select(type => type.Kind).ToHashSet());
        Assert.Contains(types, type => type.Bracket == EaveBracket.Bracket);
        Assert.Contains(types, type => type.Bracket == EaveBracket.Corbel);
        Assert.Contains(types, type => type.Bracket == EaveBracket.KneeBrace);

        // A box eave is made with its frieze and bed moulding; an exposed rafter eave with a shaped tail.
        var made = new EaveType("Made");
        made.ChangeKind(EaveKind.Box);
        Assert.Equal(150, made.FriezeDepth);
        Assert.Equal(EaveMoulding.Cove, made.Moulding);
        made.ChangeKind(EaveKind.ExposedRafters);
        Assert.Equal(RafterTail.Cove, made.Tail);
    }

    [Fact]
    public void AnOpenEaveCutsTheRoofBackToItsRaftersAndPutsThemOutOneByOne()
    {
        var (document, roof) = House(hip: false);
        var structure = document.FindType<SlabType>(roof.TypeId)!.Structure.Layers.First(layer => layer.Function == LayerFunction.Structure);
        bool Reaches(RoofPiece piece) => piece.Outline.Any(point => point.Y < WallFace - 1);

        Assert.Contains(RoofSolid.Pieces(document, roof), piece => ReferenceEquals(piece.Layer, structure) && Reaches(piece));
        var eave = AddEave(document, roof, "Eave - Open, Rafters 47 x 150 at 400");

        // Over the eave only the slates go on out: the rafters' layer and the ceiling under it stop at the wall.
        var pieces = RoofSolid.Pieces(document, roof);
        Assert.DoesNotContain(pieces, piece => ReferenceEquals(piece.Layer, structure) && Reaches(piece));
        Assert.Contains(pieces, piece => piece.Layer.Function == LayerFunction.Finish1 && piece.Outline.Any(point => point.Y < Edge + 1));

        // A rafter at each verge, and every 400 from the wall's corner between: 30 along each eave.
        Assert.Equal(60, Eaves.RafterCount(document, eave));
        Assert.Equal(2 * 11230, eave.Length(document), 3);

        // A section through a rafter cuts its tail out past the wall's face.
        var marker = new SectionMarker { Name = "A", Start = new Point2D(5058, -1500), End = new Point2D(5058, 1200), ViewDepth = 300 };
        var cut = SectionProjection.Build(document, marker).Pieces.Where(piece => piece.ElementId == eave.Id).ToList();
        Assert.NotEmpty(cut);
        Assert.Contains(cut, piece => piece.Bounds.Left < 1500 + WallFace - 400);
    }

    [Fact]
    public void ExposedRafterTailsAreCutToTheirShape()
    {
        double LowestAtTheEdge(string typeName)
        {
            var (document, roof) = House(hip: false);
            var eave = AddEave(document, roof, typeName);
            var rafters = Eaves.Meshes(document, eave, roof.LevelId)[0];
            return rafters.Positions.Where(point => Math.Abs(point.Y - Edge) < 1 && point.X > 1000 && point.X < 9000).Min(point => point.Z);
        }

        // A plumb end goes straight down the rafter's depth at the edge; a cove is scooped back from it.
        var plumb = LowestAtTheEdge("Eave - Open, Rafters 47 x 150 at 400");
        var cove = LowestAtTheEdge("Eave - Exposed Rafter Tails, Cove Cut");
        Assert.True(cove > plumb + 100, $"the cove's end comes down to {cove:0} at the edge, the plumb end to {plumb:0}");
    }

    [Fact]
    public void AClosedEaveSlopesWithTheRoofAndABoxEaveIsLevelWithItsFriezeMouldingAndBrackets()
    {
        var (document, roof) = House(hip: false);
        var closed = AddEave(document, roof, "Eave - Closed, Timber Soffit");
        var soffit = Eaves.Meshes(document, closed, roof.LevelId).Single(mesh => mesh.Kind == MeshKind.Ceiling);
        double Bottom(Mesh3D mesh, double y) => mesh.Positions.Where(point => Math.Abs(point.Y - y) < 1).Min(point => point.Z);

        // Under the rafters, from the edge up to the wall: 450 at 35° rises 315.
        Assert.Equal(Math.Tan(35 * Math.PI / 180) * 450, Bottom(soffit, WallFace) - Bottom(soffit, Edge), 1);

        // A vented board shows its slots, sloping with it.
        var vented = AddEave(document, roof, "Eave - Closed, Vented uPVC Soffit");
        var slots = Eaves.Meshes(document, vented, roof.LevelId).Single(mesh => mesh.Kind == MeshKind.Ceiling).Edges
            .Where(edge => edge.From.Y > Edge + 1 && edge.From.Y < WallFace - 1).ToList();
        Assert.True(slots.Count > 100, $"{slots.Count} slots");
        Assert.True(slots.Max(edge => edge.From.Z) - slots.Min(edge => edge.From.Z) > 100, "the slots slope with the board");
        document.Remove(vented);

        document.Remove(closed);
        var box = AddEave(document, roof, "Eave - Box Cornice with Brackets");
        var type = document.FindType<EaveType>(box.TypeId)!;
        var meshes = Eaves.Meshes(document, box, roof.LevelId);
        var level = meshes.Single(mesh => mesh.Kind == MeshKind.Ceiling);
        Assert.Equal(type.SoffitThickness, level.Positions.Max(point => point.Z) - level.Positions.Min(point => point.Z), 1);

        // The frieze down the wall under the soffit, its depth.
        var soffitBottom = level.Positions.Min(point => point.Z);
        Assert.Contains(meshes, mesh => mesh.Kind == MeshKind.Sweep && mesh.Positions.Any(point => Math.Abs(point.Z - (soffitBottom - type.FriezeDepth)) < 1));

        // Brackets every 900 along each wall's 10.33 m, the set centred on it: twelve an eave.
        var brackets = Eaves.Brackets(document, box);
        Assert.Equal(24, brackets.Count);
        var south = brackets.Where(bracket => bracket.Strip.Edge == 0).Select(bracket => bracket.At.X).OrderBy(x => x).ToList();
        Assert.Equal(-165 + (10330 - 11 * 900) / 2.0, south[0], 3);
        Assert.Equal(900, south[1] - south[0], 3);
    }

    [Fact]
    public void AnEaveTakesOverTheSoffitsOnItsEdgesAndAnExposedOneTheFascia()
    {
        var (document, roof) = House(hip: false);
        var soffit = new Soffit { RoofId = roof.Id, LevelId = roof.LevelId, TypeId = document.TypesOf<SoffitType>().First().Id };
        soffit.EdgeIds.AddRange(RoofEdgeSweeps.SoffitEdges(document, roof));
        document.Add(soffit);
        var fascia = new Fascia { RoofId = roof.Id, LevelId = roof.LevelId, TypeId = document.TypesOf<FasciaType>().First().Id };
        fascia.EdgeIds.AddRange(RoofEdgeSweeps.FasciaEdges(document, roof));
        document.Add(fascia);
        var eaves = Eaves.EaveEdges(document, roof);
        var verges = roof.Edges.Where(edge => !edge.DefinesSlope).Select(edge => edge.Id).ToHashSet();

        // A closed eave: the soffit keeps only the verges; the fascia stays all round.
        var history = new UndoStack();
        var steps = Eaves.TakeOver(document, roof.Id, document.TypesOf<EaveType>().First(type => type.Kind == EaveKind.Closed), eaves);
        history.Execute(new CompositeCommand("Eave", steps));
        Assert.Equal(verges, soffit.EdgeIds.ToHashSet());
        Assert.Equal(4, fascia.EdgeIds.Count);
        history.Undo();
        Assert.Equal(4, soffit.EdgeIds.Count);

        // Exposed rafter tails: the fascia gives up the eaves too, or it would hide the rafters' ends.
        steps = Eaves.TakeOver(document, roof.Id, document.TypesOf<EaveType>().First(type => type.Kind == EaveKind.ExposedRafters), eaves);
        history.Execute(new CompositeCommand("Eave", steps));
        Assert.Equal(verges, fascia.EdgeIds.ToHashSet());

        // A soffit under nothing but eaves goes altogether.
        var under = new Soffit { RoofId = roof.Id, LevelId = roof.LevelId, TypeId = soffit.TypeId };
        under.EdgeIds.AddRange(eaves);
        document.Add(under);
        history.Execute(new CompositeCommand("Eave", Eaves.TakeOver(document, roof.Id, null, eaves)));
        Assert.DoesNotContain(under, document.Elements);
    }

    [Fact]
    public void AnOpenEavesFasciaCoversTheRaftersEndsNotTheWholeRoofsDepth()
    {
        var (document, roof) = House(hip: false);
        var fascia = new Fascia { RoofId = roof.Id, LevelId = roof.LevelId, TypeId = document.TypesOf<FasciaType>().First().Id };
        fascia.EdgeIds.AddRange(Eaves.EaveEdges(document, roof));
        document.Add(fascia);
        double Depth() => -RoofEdgeSweeps.Runs(document, fascia)[0].Segments[0].Profile.Min(point => point.Y);

        var before = Depth();
        AddEave(document, roof, "Eave - Open, Rafters 47 x 150 at 400");

        // The slates and the rafters under them, not the ceiling's board under those.
        Assert.Equal(12 * Math.Sqrt(1 + Math.Pow(Math.Tan(35 * Math.PI / 180), 2)), before - Depth(), 1);
    }

    [Fact]
    public void AnEaveOverAPartyWallIsFireStoppedAsApprovedDocumentBAsks()
    {
        var (document, roof) = House(hip: false);
        var party = document.TypesOf<WallType>().Single(type => type.Separating);
        document.Add(new Wall { Start = new Point2D(5000, 0), End = new Point2D(5000, 8000), TypeId = party.Id, LevelId = roof.LevelId, UnconnectedHeight = 3000 });
        var eave = AddEave(document, roof, "Eave - Box Cornice, Frieze and Bed Moulding");

        // The party wall meets both eaves; the void over it is filled.
        Assert.Equal(2, Eaves.PartyWalls(document, eave).Count);
        var stop = Eaves.Meshes(document, eave, roof.LevelId).Single(mesh => mesh.Description == ModelMeshBuilder.FireStopMaterial);
        Assert.InRange(stop.Positions.Min(point => point.X), 5000 - party.Structure.TotalWidth / 2 - 1, 5000);
        Assert.Empty(Eaves.Problems(document, eave, ClearanceStandard.ApprovedDocumentM));
        Assert.Contains("fire-stopped over 2 party walls", Eaves.Verdict(document, eave));

        eave.FireStopped = false;
        var problems = Eaves.Problems(document, eave, ClearanceStandard.ApprovedDocumentM);
        Assert.NotEmpty(problems);
        Assert.Contains("2 party walls meet it", problems[0].Said);
        Assert.Contains("5.11b", problems[0].Rule);
        Assert.DoesNotContain(Eaves.Meshes(document, eave, roof.LevelId), mesh => mesh.Description == ModelMeshBuilder.FireStopMaterial);

        // An open eave has no void to fill.
        Assert.Empty(Eaves.Problems(document, AddEave(document, roof, "Eave - Open, Rafters 47 x 150 at 400"), ClearanceStandard.ApprovedDocumentM));
    }

    [Fact]
    public void AnEaveNearALotLineIsHeldToTheIrc()
    {
        var (document, roof) = House(hip: false);
        var eave = AddEave(document, roof, "Eave - Closed, Timber Soffit");
        var line = new PropertyLine();
        document.Add(line);
        void From(double gap) => line.SetBoundary(new[] { new Point2D(-5000, Edge - gap), new Point2D(15000, Edge - gap), new Point2D(15000, 20000), new Point2D(-5000, 20000) });

        // Under 2 ft: no projection at all.
        From(500);
        Assert.Equal(500, Eaves.DistanceToBoundary(document, eave)!.Value, 3);
        var problems = Eaves.Problems(document, eave, ClearanceStandard.Irc);
        Assert.Contains("allows no projection less than 2 ft", Assert.Single(problems.Select(problem => problem.Rule).Distinct()));

        // 2 to 5 ft: an underside fire-protected, or fireblocked at the wall.
        From(1000);
        Assert.Contains("one-hour fire-resistive construction on its underside", Assert.Single(Eaves.Problems(document, eave, ClearanceStandard.Irc).Select(problem => problem.Rule).Distinct()));
        document.FindType<EaveType>(eave.TypeId)!.Underside = EaveUnderside.OneHour;
        Assert.Empty(Eaves.Problems(document, eave, ClearanceStandard.Irc));
        document.FindType<EaveType>(eave.TypeId)!.Underside = EaveUnderside.Unprotected;
        eave.FireBlocked = true;
        Assert.Empty(Eaves.Problems(document, eave, ClearanceStandard.Irc));

        // Sprinklered throughout, 3 ft is far enough (Table R302.1(2)); 2 ft still is not.
        eave.FireBlocked = false;
        document.ProjectInformation.Sprinklered = true;
        Assert.Empty(Eaves.Problems(document, eave, ClearanceStandard.Irc));
        From(800);
        Assert.Contains("Table R302.1(2)", Eaves.Problems(document, eave, ClearanceStandard.Irc)[0].Rule);
        document.ProjectInformation.Sprinklered = false;

        // 5 ft and more: nothing asked; and in England the boundary is the walls' business, not the eave's.
        From(1600);
        Assert.Empty(Eaves.Problems(document, eave, ClearanceStandard.Irc));
        From(500);
        Assert.Empty(Eaves.Problems(document, eave, ClearanceStandard.ApprovedDocumentM));
    }

    [Fact]
    public void AnEaveUnderAColdRoofLetsAirIntoIt()
    {
        var (document, roof) = House(hip: false);
        var ceiling = new Ceiling
        {
            LevelId = roof.LevelId, HeightOffset = 3000,
            TypeId = document.TypesOf<CeilingType>().Single(type => type.Name == "Ceiling - Plasterboard under 270mm Mineral Wool").Id
        };
        ceiling.SetBoundary(new[] { new Point2D(0, 0), new Point2D(10000, 0), new Point2D(10000, 8000), new Point2D(0, 8000) });
        document.Add(ceiling);
        Assert.True(SlabRules.Cold(document, roof));

        // A solid timber soffit lets none in; a vented one 25,000 mm² a metre, past BS 5250's 10,000.
        var eave = AddEave(document, roof, "Eave - Closed, Timber Soffit");
        var problems = Eaves.Problems(document, eave, ClearanceStandard.ApprovedDocumentM);
        Assert.Contains("lets no air into the cold roof", problems[0].Said);
        Assert.Contains("10,000 mm²", problems[0].Rule);
        Assert.Contains(RoofEdgeRules.Blocked(document, roof.LevelId, ClearanceStandard.ApprovedDocumentM), blocked => blocked.Zone.Element == eave);

        eave.TypeId = document.TypesOf<EaveType>().Single(type => type.Name == "Eave - Closed, Vented uPVC Soffit").Id;
        Assert.Empty(Eaves.Problems(document, eave, ClearanceStandard.ApprovedDocumentM));
        Assert.StartsWith("Airs the cold roof over it", Eaves.Verdict(document, eave));

        // Fireblocked at the wall, its vents air nothing.
        eave.FireBlocked = true;
        Assert.Contains("fireblocked at the wall", Eaves.Problems(document, eave, ClearanceStandard.ApprovedDocumentM)[0].Said);
    }

    [Fact]
    public void AnEaveAndItsTypeAreSavedWithTheProject()
    {
        var (document, roof) = House(hip: true);
        var eave = AddEave(document, roof, "Eave - Exposed Rafter Tails, Knee Braces");
        document.ProjectInformation.Sprinklered = true;
        eave.FireStopped = false;
        eave.FireBlocked = true;
        var type = document.FindType<EaveType>(eave.TypeId)!;
        type.RafterSpacing = 600;
        type.Underside = EaveUnderside.HeavyTimber;
        type.BracketDepth = 300;

        var path = Path.Combine(Path.GetTempPath(), $"bimtest-{Guid.NewGuid():N}{ProjectFile.Extension}");
        try
        {
            ProjectFile.Save(document, path);
            var opened = ProjectFile.Load(path);
            var back = opened.Elements.OfType<Eave>().Single();
            Assert.Equal(eave.EdgeIds, back.EdgeIds);
            Assert.True(opened.ProjectInformation.Sprinklered);
            Assert.False(back.FireStopped);
            Assert.True(back.FireBlocked);
            var backType = opened.FindType<EaveType>(back.TypeId)!;
            Assert.Equal(EaveKind.ExposedRafters, backType.Kind);
            Assert.Equal(RafterTail.Scroll, backType.Tail);
            Assert.Equal(EaveBracket.KneeBrace, backType.Bracket);
            Assert.Equal(600, backType.RafterSpacing);
            Assert.Equal(300, backType.BracketDepth);
            Assert.Equal(EaveUnderside.HeavyTimber, backType.Underside);
            Assert.Equal(Eaves.RafterCount(document, eave), Eaves.RafterCount(opened, back));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
