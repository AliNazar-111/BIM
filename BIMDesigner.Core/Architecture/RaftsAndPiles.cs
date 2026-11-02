using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;
using BIMDesigner.Core.Views;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// Rafts: one made under a building from the outside of its walls, a little beyond them; its solids -
/// the slab, the thickening round its edge and under the load-bearing walls inside - and how deep its
/// edge goes under the ground.
/// </summary>
public static class Rafts
{
    /// <summary>How far a raft is carried out past the outside face of the walls on it, mm: enough to seat the outer leaf whole.</summary>
    public const double Projection = 150;

    /// <summary>A raft under the building on a storey round a point: its walls' outside faces, carried out <see cref="Projection"/>; null where no building is there.</summary>
    public static RaftFoundation? Under(BimDocument document, Guid levelId, Point2D at)
    {
        var outline = AreaPlans.Frame(document, levelId, AreaScheme.GrossExternal).FirstOrDefault(each => Polygon2D.Contains(each, at));
        if (outline is null) return null;
        var counter = Polygon2D.SignedArea(outline) < 0 ? outline.Reverse().ToList() : outline.ToList();
        var raft = new RaftFoundation { LevelId = levelId };
        raft.SetOutline(SlabEdgeRuns.Inset(counter, closed: true, -Projection));
        // Its edge down out of the frost below the lowest ground round it: 0.45 m; in the US 12 in, or the frost line.
        var project = document.ProjectInformation;
        var level = document.FindLevel(levelId)?.Elevation ?? 0;
        var lowest = raft.Outline.Min(point => Footings.GroundAt(document, point, level));
        if (project.ClearanceStandard.IsUs())
        {
            (raft.Thickness, raft.EdgeWidth) = (101.6, 304.8);
            raft.EdgeDepth = Math.Max(304.8, Math.Ceiling((level - lowest + Math.Max(304.8, project.FrostDepth)) / 25.4) * 25.4);
            (raft.TopReinforcement, raft.BottomReinforcement, raft.EdgeBars, raft.EdgeLinks) = (string.Empty, string.Empty, "2 #4", string.Empty);
        }
        else raft.EdgeDepth = Math.Max(raft.EdgeDepth, Math.Ceiling((level - lowest + 450) / 50) * 50);

        return raft;
    }

    /// <summary>A raft with the same outline and settings, not yet in the project.</summary>
    public static RaftFoundation Copy(RaftFoundation raft)
    {
        var copy = new RaftFoundation
        {
            Thickness = raft.Thickness, EdgeWidth = raft.EdgeWidth, EdgeDepth = raft.EdgeDepth, TopDepth = raft.TopDepth, ThickenUnderWalls = raft.ThickenUnderWalls,
            TopReinforcement = raft.TopReinforcement, BottomReinforcement = raft.BottomReinforcement, EdgeBars = raft.EdgeBars, EdgeLinks = raft.EdgeLinks
        };
        copy.SetOutline(raft.Outline);
        return copy;
    }

    /// <summary>A raft's top, mm.</summary>
    public static double Top(BimDocument document, RaftFoundation raft) => (document.FindLevel(raft.LevelId)?.Elevation ?? 0) - raft.TopDepth;

    /// <summary>The load-bearing walls on its storey inside it - not round its edge - each as the strip it is thickened along.</summary>
    public static IReadOnlyList<IReadOnlyList<Point2D>> Thickenings(BimDocument document, RaftFoundation raft)
    {
        var strips = new List<IReadOnlyList<Point2D>>();
        if (!raft.ThickenUnderWalls || raft.Outline.Count < 3) return strips;
        var inner = SlabEdgeRuns.Inset(raft.Outline, closed: true, raft.EdgeWidth);
        foreach (var wall in document.Walls.Where(wall => wall.LevelId == raft.LevelId && !wall.IsCurved && wall.StructuralUsage != StructuralUsage.NonBearing))
        {
            if (!Polygon2D.Contains(inner, wall.LocationCurve.PointAt(wall.Length / 2))) continue;
            var across = wall.Direction.PerpendicularLeft() * (raft.EdgeWidth / 2);
            strips.Add(new[] { wall.Start - across, wall.End - across, wall.End + across, wall.Start + across });
        }

        return strips;
    }

    /// <summary>A raft's solids: its slab, then each stretch of its thickened edge and each thickening under a wall, each its own.</summary>
    public static IReadOnlyList<Mesh3D> Meshes(BimDocument document, RaftFoundation raft)
    {
        var meshes = new List<Mesh3D>();
        if (raft.Outline.Count < 3) return meshes;
        var concrete = document.Materials.FirstOrDefault(material => material.Name.Contains("Concrete", StringComparison.OrdinalIgnoreCase));
        var colour = concrete?.SurfaceColour ?? ColourRgb.FromHex("B5B3AE");
        var (top, name) = (Top(document, raft), concrete?.Name ?? "Raft");
        Mesh3D Piece() => new(raft.Id, raft.LevelId, MeshKind.Floor, colour, name);

        var slab = Piece();
        slab.AddExtrusion(raft.Outline, top - raft.Thickness, top);
        meshes.Add(slab);
        if (raft.EdgeDepth <= raft.Thickness + 1) return meshes;

        var inner = SlabEdgeRuns.Inset(raft.Outline, closed: true, raft.EdgeWidth);
        for (var i = 0; i < raft.Outline.Count; i++)
        {
            var j = (i + 1) % raft.Outline.Count;
            var band = Piece();
            band.AddExtrusion(new[] { raft.Outline[i], raft.Outline[j], inner[j], inner[i] }, top - raft.EdgeDepth, top - raft.Thickness);
            meshes.Add(band);
        }

        foreach (var strip in Thickenings(document, raft))
        {
            var under = Piece();
            under.AddExtrusion(strip, top - raft.EdgeDepth, top - raft.Thickness);
            meshes.Add(under);
        }

        return meshes;
    }

    /// <summary>The concrete in a raft, mm³: its slab, its edge and its thickenings below it.</summary>
    public static double Volume(BimDocument document, RaftFoundation raft)
    {
        if (raft.Outline.Count < 3) return 0;
        var below = Math.Max(0, raft.EdgeDepth - raft.Thickness);
        var inner = SlabEdgeRuns.Inset(raft.Outline, closed: true, raft.EdgeWidth);
        return Polygon2D.Area(raft.Outline) * raft.Thickness
               + Math.Max(0, Polygon2D.Area(raft.Outline) - Polygon2D.Area(inner)) * below
               + Thickenings(document, raft).Sum(strip => Polygon2D.Area(strip)) * below;
    }

    /// <summary>How far the underside of a raft's edge is below the ground round it, mm: at its lowest corner.</summary>
    public static double EdgeBelowGround(BimDocument document, RaftFoundation raft)
    {
        if (raft.Outline.Count < 3) return 0;
        var level = document.FindLevel(raft.LevelId)?.Elevation ?? 0;
        var bottom = Top(document, raft) - raft.EdgeDepth;
        return raft.Outline.Min(point => Footings.GroundAt(document, point, level)) - bottom;
    }
}

/// <summary>
/// Piles and ground beams: a beam under a wall, its top just under the ground, carried by bored piles
/// at its ends and between at no more than a spacing; each pile sized for its share of the wall's
/// load - in clay by the skin friction down its shaft and the bearing under its toe (the alpha method:
/// 0.5 cu over its shaft, 9 cu under its base, a factor of safety of 2.5) - and taken below the clay's
/// movement.
/// </summary>
public static class Piles
{
    /// <summary>The most piles under a ground beam are apart, mm.</summary>
    public const double MaxSpacing = 3000;

    /// <summary>The adhesion factor on a bored pile's shaft in clay, its base bearing factor, and the factor of safety on the two together.</summary>
    public const double Alpha = 0.5, BearingFactor = 9, SafetyFactor = 2.5;

    /// <summary>The clay's undrained shear strength the ground is taken to have, kPa: the middle of each band BS 5930:1999 gave a clay's consistency - very soft under 20, soft 20 to 40, firm 40 to 75, stiff 75 to 150, the bounds BS EN ISO 14688-2 keeps; null for ground that is not clay.</summary>
    public static double? Strength(GroundType ground) => ground switch
    {
        GroundType.StiffClay => 100,
        GroundType.FirmClay => 55,
        GroundType.SoftClay => 30,
        GroundType.VerySoft => 15,
        _ => null
    };

    /// <summary>The beam's outline in plan: along its wall, its width wide - run on past the wall's ends by half its width.</summary>
    public static IReadOnlyList<Point2D>? Outline(BimDocument document, GroundBeam beam)
    {
        if (document.Find<Wall>(beam.WallId) is not { } wall || wall.IsCurved || wall.Length < 1 || document.GetWallType(wall) is not { } wallType) return null;
        var along = (wall.End - wall.Start).NormalisedOrDefault(Vector2D.UnitX);
        var middle = wall.PointAt(wallType.Structure, 0, 0);
        var across = (wall.PointAt(wallType.Structure, 0, 1) - middle).NormalisedOrDefault(along.PerpendicularLeft());
        var (start, end) = (middle - along * (beam.Width / 2), wall.PointAt(wallType.Structure, wall.Length, 0) + along * (beam.Width / 2));
        var box = new List<Point2D> { start - across * (beam.Width / 2), end - across * (beam.Width / 2), end + across * (beam.Width / 2), start + across * (beam.Width / 2) };
        if (Polygon2D.SignedArea(box) < 0) box.Reverse();
        return box;
    }

    /// <summary>A beam's underside and top, mm.</summary>
    public static (double Bottom, double Top) Heights(BimDocument document, GroundBeam beam)
    {
        var level = document.Find<Wall>(beam.WallId) is { } wall ? document.FindLevel(wall.LevelId)?.Elevation ?? 0 : 0;
        var top = level - beam.TopDepth;
        return (top - beam.Depth, top);
    }

    public static GroundBeam? Under(BimDocument document, Wall wall) => document.All<GroundBeam>().FirstOrDefault(beam => beam.WallId == wall.Id);

    /// <summary>The piles a beam stands on.</summary>
    public static IReadOnlyList<Pile> On(BimDocument document, GroundBeam beam) => document.All<Pile>().Where(pile => pile.BeamId == beam.Id).ToList();

    /// <summary>Where a pile stands: so far along its beam's wall, on the middle of the wall's structure - or where it was put.</summary>
    public static Point2D Position(BimDocument document, Pile pile) =>
        pile.BeamId is { } id && document.Find<GroundBeam>(id) is { } beam && document.Find<Wall>(beam.WallId) is { IsCurved: false } wall && document.GetWallType(wall) is { } type
            ? wall.PointAt(type.Structure, Math.Clamp(pile.Along, 0, wall.Length), 0)
            : pile.Location;

    /// <summary>A pile's head, mm: under its beam, or so far below its level.</summary>
    public static double Head(BimDocument document, Pile pile) =>
        pile.BeamId is { } id && document.Find<GroundBeam>(id) is { } beam ? Heights(document, beam).Bottom : (document.FindLevel(pile.LevelId)?.Elevation ?? 0) - pile.TopDepth;

    /// <summary>The load a pile carries, kN: its share of its beam's wall - half the span each side - its share of the beam, and itself.</summary>
    public static double Load(BimDocument document, Pile pile)
    {
        var own = Math.PI * pile.Diameter * pile.Diameter / 4 / 1e6 * pile.Length / 1000 * 24;
        if (pile.BeamId is not { } id || document.Find<GroundBeam>(id) is not { } beam || document.Find<Wall>(beam.WallId) is not { } wall) return own;
        double Along(Pile each) => (Position(document, each) - wall.Start).Dot(wall.Direction);

        // The piles under it, with those its ends share with the walls it meets.
        var under = document.All<Pile>().Where(each => each.BeamId == beam.Id || Line2D.DistanceFromSegment(Position(document, each), wall.Start, wall.End) < each.Diameter)
            .OrderBy(Along).ToList();
        var at = under.FindIndex(each => ReferenceEquals(each, pile));
        if (at < 0) return own;
        var left = at > 0 ? (Along(pile) - Along(under[at - 1])) / 2 : 0;
        var right = at + 1 < under.Count ? (Along(under[at + 1]) - Along(pile)) / 2 : 0;
        var span = (left + right) / 1000;
        var beamWeight = beam.Width * beam.Depth / 1e6 * 24;
        return (Loads.OnWall(document, wall) + beamWeight) * span + own;
    }

    /// <summary>What a pile can carry in the ground, kN, by the alpha method; null where the ground is not clay.</summary>
    public static double? Capacity(BimDocument document, Pile pile)
    {
        if (Strength(document.ProjectInformation.Ground) is not { } cu) return null;
        var (diameter, length) = (pile.Diameter / 1000, pile.Length / 1000);
        var shaft = Alpha * cu * Math.PI * diameter * length;
        var toe = BearingFactor * cu * Math.PI * diameter * diameter / 4;
        return (shaft + toe) / SafetyFactor;
    }

    /// <summary>How long a pile must be to carry a load in the ground, mm, rounded up to 250; null where the ground is not clay.</summary>
    public static double? LengthFor(BimDocument document, double load, double diameter)
    {
        if (Strength(document.ProjectInformation.Ground) is not { } cu) return null;
        var d = diameter / 1000;
        var toe = BearingFactor * cu * Math.PI * d * d / 4;
        var shaft = Math.Max(0, load * SafetyFactor - toe);
        return Math.Ceiling(shaft / (Alpha * cu * Math.PI * d) * 1000 / 250) * 250;
    }

    public static string CapacitySaid(BimDocument document, Pile pile) =>
        Capacity(document, pile) is { } capacity
            ? $"{capacity:0} kN safe in {StructureRules.Describe(document.ProjectInformation.Ground)} (cu {Strength(document.ProjectInformation.Ground):0} kPa), carrying {Load(document, pile):0} kN"
            : $"Not worked out in {StructureRules.Describe(document.ProjectInformation.Ground)}: a pile there is the piling engineer's to design";

    /// <summary>
    /// A ground beam under a wall and the piles under it - at its ends and between at no more than
    /// <see cref="MaxSpacing"/> apart, none where one already stands. Their lengths are set afterwards,
    /// once all the piles are in and each one's share of the load is known. Not yet in the project.
    /// </summary>
    public static (GroundBeam Beam, IReadOnlyList<Pile> Piles) Fit(BimDocument document, Wall wall, double diameter, double leastLength, IEnumerable<Pile>? alsoPlaced = null)
    {
        var us = document.ProjectInformation.ClearanceStandard.IsUs();
        var beam = new GroundBeam { WallId = wall.Id, LevelId = wall.LevelId };
        if (us) (beam.Width, beam.Depth, beam.TopBars, beam.BottomBars, beam.Links) = (406.4, 609.6, "2 #5", "3 #5", "#3 @ 12 in");
        beam.Void = HeaveVoid(document.ProjectInformation);

        var count = Math.Max(2, (int)Math.Ceiling(wall.Length / MaxSpacing) + 1);
        var existing = document.All<Pile>().Select(pile => (Position(document, pile), pile.Diameter))
            .Concat((alsoPlaced ?? Enumerable.Empty<Pile>()).Select(pile => (pile.Location, pile.Diameter))).ToList();
        var structure = document.GetWallType(wall)?.Structure;
        var piles = new List<Pile>();
        for (var i = 0; i < count; i++)
        {
            var along = wall.Length * i / (count - 1);
            var at = structure is null ? wall.Start + wall.Direction * along : wall.PointAt(structure, along, 0);
            if (existing.Any(pile => pile.Item1.DistanceTo(at) < pile.Item2)) continue;
            piles.Add(new Pile { Location = at, Along = along, Diameter = diameter, Length = leastLength, BeamId = beam.Id, LevelId = wall.LevelId, Bars = us ? "4 #4" : "4 H12" });
        }

        // Each as long as its share of the wall and beam asks - half the span to the piles each side,
        // those already under the wall's line counted - with its own weight, and no shorter than the least.
        var stations = piles.Select(pile => pile.Along)
            .Concat(existing.Where(pile => Line2D.DistanceFromSegment(pile.Item1, wall.Start, wall.End) < pile.Item2).Select(pile => (pile.Item1 - wall.Start).Dot(wall.Direction)))
            .Distinct().OrderBy(along => along).ToList();
        var perMetre = Loads.OnWall(document, wall) + beam.Width * beam.Depth / 1e6 * 24;
        foreach (var pile in piles)
        {
            var at = stations.IndexOf(pile.Along);
            var span = ((at > 0 ? pile.Along - stations[at - 1] : 0) + (at + 1 < stations.Count ? stations[at + 1] - pile.Along : 0)) / 2 / 1000;
            for (var pass = 0; pass < 2; pass++)
            {
                var own = Math.PI * diameter * diameter / 4 / 1e6 * pile.Length / 1000 * 24;
                pile.Length = Math.Max(leastLength, LengthFor(document, perMetre * span + own, diameter) ?? leastLength);
            }
        }

        return (beam, piles);
    }

    /// <summary>A pile on its own at a point, as long as the least it is to be, its head so far below the level.</summary>
    public static Pile Single(BimDocument document, Guid levelId, Point2D at, double diameter, double leastLength) =>
        new() { Location = at, LevelId = levelId, Diameter = diameter, Length = leastLength, Bars = document.ProjectInformation.ClearanceStandard.IsUs() ? "4 #4" : "4 H12" };

    /// <summary>The least a pile is made, mm: 3 m, to reach below the ground that moves with the seasons; 10 ft in the US.</summary>
    public static double LeastLength(BimDocument document) => document.ProjectInformation.ClearanceStandard.IsUs() ? 3048 : 3000;

    /// <summary>The diameter a pile is made, mm: 300 - 12 in in the US.</summary>
    public static double UsualDiameter(BimDocument document) => document.ProjectInformation.ClearanceStandard.IsUs() ? 304.8 : 300;

    /// <summary>
    /// The void under a ground beam in a shrinkable clay, so the clay can swell without lifting it, mm:
    /// 150 in a clay of high shrinkage, 100 of medium, 50 of low (NHBC Standards 4.2.10, its heave precautions).
    /// </summary>
    public static double HeaveVoid(ProjectInformation project) => project.ClayShrinkage switch
    {
        ClayShrinkage.High => 150,
        ClayShrinkage.Medium => 100,
        ClayShrinkage.Low => 50,
        _ => 0
    };

    /// <summary>The solids of a ground beam - with the masonry on it - or a pile.</summary>
    public static IReadOnlyList<Mesh3D> Meshes(BimDocument document, Element element)
    {
        var concrete = document.Materials.FirstOrDefault(material => material.Name.Contains("Concrete", StringComparison.OrdinalIgnoreCase));
        var colour = concrete?.SurfaceColour ?? ColourRgb.FromHex("B5B3AE");
        var meshes = new List<Mesh3D>();
        switch (element)
        {
            case GroundBeam beam when Outline(document, beam) is { } outline:
            {
                var (bottom, top) = Heights(document, beam);
                var body = new Mesh3D(beam.Id, beam.LevelId, MeshKind.Floor, colour, concrete?.Name ?? "Ground Beam");
                body.AddExtrusion(outline, bottom, top);
                meshes.Add(body);
                if (document.Find<Wall>(beam.WallId) is { } wall && document.GetWallType(wall) is { } wallType && wall.GetBaseElevation(document) > top + 1)
                {
                    var across = wall.Direction.PerpendicularLeft() * (wallType.Structure.TotalWidth / 2);
                    var (from, to) = (wall.PointAt(wallType.Structure, 0, 0), wall.PointAt(wallType.Structure, wall.Length, 0));
                    var stem = new Mesh3D(beam.Id, beam.LevelId, MeshKind.Wall, ColourRgb.FromHex("A8A49C"), "Foundation Wall");
                    stem.AddExtrusion(new[] { from - across, to - across, to + across, from + across }, top, wall.GetBaseElevation(document));
                    meshes.Add(stem);
                }

                break;
            }

            case Pile pile:
            {
                var head = Head(document, pile);
                var body = new Mesh3D(pile.Id, pile.LevelId, MeshKind.Wall, colour, concrete?.Name ?? "Pile");
                body.AddExtrusion(Circle(Position(document, pile), pile.Diameter / 2), head - pile.Length, head);
                meshes.Add(body);
                break;
            }
        }

        return meshes;
    }

    /// <summary>A circle as a polygon of 24 sides.</summary>
    public static IReadOnlyList<Point2D> Circle(Point2D centre, double radius) =>
        Enumerable.Range(0, 24).Select(i => centre + new Vector2D(Math.Cos(i * Math.PI / 12), Math.Sin(i * Math.PI / 12)) * radius).ToList();

    /// <summary>The concrete in a ground beam, mm³.</summary>
    public static double Volume(BimDocument document, GroundBeam beam) =>
        Outline(document, beam) is { } outline ? Polygon2D.Area(outline) * beam.Depth : 0;
}
