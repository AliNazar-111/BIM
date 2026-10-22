using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;
using BIMDesigner.Core.Parameters;

namespace BIMDesigner.Core.Architecture;

/// <summary>What a region of the ground is laid as.</summary>
public enum GroundSurface
{
    Lawn,
    Path,
    Paving,
    Gravel,
    Planting
}

/// <summary>
/// A region of the ground laid as something of its own - a lawn, a path, paving, gravel, a
/// planting bed: Revit's subregion. It lies on the ground, following it; a path to a door is
/// checked for how steep it is.
/// </summary>
public sealed class GroundRegion : Element
{
    private readonly List<Point2D> _boundary = new();

    public override BuiltInCategory Category => BuiltInCategory.Topography;

    /// <summary>Its outline in plan, anticlockwise.</summary>
    public IReadOnlyList<Point2D> Boundary => _boundary;

    public void SetBoundary(IEnumerable<Point2D> points)
    {
        var replacement = points.ToList();
        _boundary.Clear();
        _boundary.AddRange(replacement);
        if (Polygon2D.SignedArea(_boundary) < 0) _boundary.Reverse();
    }

    public GroundSurface Surface { get; set; } = GroundSurface.Lawn;

    /// <summary>How far it is raised over the ground round it - a planting bed - or, below 0, sunk into it - a sunken patio, mm.</summary>
    public double Offset { get; set; }

    /// <summary>A material of the project's for it; none for how its surface usually reads.</summary>
    public Guid MaterialId { get; set; }

    /// <summary>
    /// Whether it is the way up to a door of the building - its approach - which Approved
    /// Document M holds to 1:20, or a ramp no steeper than 1:12; the ADA, an accessible route.
    /// </summary>
    public bool Approach { get; set; }

    private readonly List<Point2D> _pathLine = new();

    /// <summary>A path's middle, as it was drawn along - empty for a region drawn by its outline.</summary>
    public IReadOnlyList<Point2D> PathLine => _pathLine;

    /// <summary>How wide a path drawn along its middle is, mm.</summary>
    public double PathWidth { get; private set; }

    /// <summary>Makes it a path along a line, so wide: its outline the band either side of the line.</summary>
    public void SetPath(IEnumerable<Point2D> line, double width)
    {
        var points = line.ToList();
        _pathLine.Clear();
        _pathLine.AddRange(points);
        PathWidth = Math.Max(300, width);
        if (_pathLine.Count >= 2) SetBoundary(GroundRegions.Band(_pathLine, PathWidth));
    }

    /// <summary>A path's middle moved as the region is - by a move, a turn, a mirror.</summary>
    public void TransformPath(Func<Point2D, Point2D> map)
    {
        var moved = _pathLine.Select(map).ToList();
        _pathLine.Clear();
        _pathLine.AddRange(moved);
    }

    public override IEnumerable<ParameterValue> GetInstanceParameters(BimDocument document)
    {
        yield return ParameterValue.BindChoice(GroundRegionParameters.Surface,
            () => EnumText.Humanise(Surface),
            v => { if (EnumText.TryParse<GroundSurface>(v, out var surface)) Surface = surface; },
            EnumText.Choices<GroundSurface>());
        var names = document.Materials.Select(material => material.Name).OrderBy(name => name).ToArray();
        yield return ParameterValue.BindChoice(GroundRegionParameters.Material,
            () => document.FindMaterial(MaterialId)?.Name ?? "As its surface",
            v => MaterialId = document.Materials.FirstOrDefault(material => material.Name == v)?.Id ?? Guid.Empty,
            names.Prepend("As its surface").ToArray());
        yield return ParameterValue.BindValidated(GroundRegionParameters.Offset, () => Offset, (double v) =>
        {
            if (!double.IsFinite(v) || Math.Abs(v) > 3000) return false;
            Offset = v;
            return true;
        });
        yield return ParameterValue.Bind(GroundRegionParameters.Approach, () => Approach, v => Approach = v);
        if (_pathLine.Count >= 2)
        {
            yield return ParameterValue.BindValidated(GroundRegionParameters.PathWidth, () => PathWidth, (double v) =>
            {
                if (!double.IsFinite(v) || v < 300 || v > 20000) return false;
                SetPath(_pathLine.ToList(), v);
                return true;
            });
            yield return ParameterValue.ReadOnly(GroundRegionParameters.PathLength, () => Approaches.Length(_pathLine));
            yield return ParameterValue.ReadOnly(GroundRegionParameters.CrossFall,
                () => Approaches.CrossFall(document, this) is { } fall && fall > 1e-6 ? $"1:{1 / fall:0.#}" : "Level");
        }
        yield return ParameterValue.ReadOnly(GroundRegionParameters.Area, () => Polygon2D.Area(Boundary));
        yield return ParameterValue.ReadOnly(GroundRegionParameters.Gradient, () => GroundRegions.Steepest(document, this) is { } steepest && steepest > 1e-6 ? $"1:{1 / steepest:0.#}" : "Level");
        yield return ParameterValue.ReadOnly(GroundRegionParameters.Rule, () => GroundRegions.Verdict(document, this));

        foreach (var parameter in GetCommonParameters(document)) yield return parameter;
    }
}

public static class GroundRegionParameters
{
    public static readonly ParameterDefinition Surface = new("Surface", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Construction);
    public static readonly ParameterDefinition Material = new("Material", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.MaterialsAndFinishes);
    public static readonly ParameterDefinition Approach = new("Approach to Entrance", ParameterDataType.YesNo, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition PathWidth = new("Path Width", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition PathLength = new("Path Length", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition CrossFall = new("Steepest Cross-fall", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Offset = new("Raised By (minus for sunk)", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition Area = new("Area", ParameterDataType.Area, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Gradient = new("Steepest Gradient", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Rule = new("Regulations", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.IdentityData);
}

/// <summary>A region's surface, where it lies on the ground, how steep it is, and whether a path to a door is gentle enough.</summary>
public static class GroundRegions
{
    public static ColourRgb Colour(GroundSurface surface) => surface switch
    {
        GroundSurface.Path => new ColourRgb(0xC9, 0xB9, 0x9A),
        GroundSurface.Paving => new ColourRgb(0xA8, 0xA4, 0x9E),
        GroundSurface.Gravel => new ColourRgb(0xB8, 0xAE, 0x98),
        GroundSurface.Planting => new ColourRgb(0x6B, 0x52, 0x38),
        _ => new ColourRgb(0x6E, 0x9B, 0x48)
    };

    /// <summary>The ground it lies on: the surface its middle is over, on its level.</summary>
    public static Toposurface? Host(BimDocument document, GroundRegion region)
    {
        if (region.Boundary.Count < 3) return null;
        var middle = Polygon2D.Centroid(region.Boundary, region.Boundary[0]);
        return document.All<Toposurface>().Where(ground => ground.LevelId == region.LevelId)
            .FirstOrDefault(ground => Terrains.HeightAt(document, ground, middle) is not null);
    }

    /// <summary>Its height at a point: the ground's there, or its level's.</summary>
    public static double HeightAt(BimDocument document, GroundRegion region, Point2D at) =>
        Host(document, region) is { } ground && Terrains.HeightAt(document, ground, at) is { } height
            ? height
            : document.FindLevel(region.LevelId)?.Elevation ?? 0;

    /// <summary>
    /// How steep it is at its steepest, as a rise over a run: the steepest of the ground's
    /// triangles it lies over. None off the ground.
    /// </summary>
    public static double? Steepest(BimDocument document, GroundRegion region)
    {
        if (Host(document, region) is not { } ground) return null;
        var outline = region.Boundary;
        var (minX, maxX, minY, maxY) = (outline.Min(point => point.X), outline.Max(point => point.X), outline.Min(point => point.Y), outline.Max(point => point.Y));
        double? steepest = null;
        foreach (var (a, b, c) in Terrains.Faces(document, ground))
        {
            // Each of the ground's triangles it lies over, however little of it.
            if (Math.Max(a.X, Math.Max(b.X, c.X)) < minX || Math.Min(a.X, Math.Min(b.X, c.X)) > maxX
                || Math.Max(a.Y, Math.Max(b.Y, c.Y)) < minY || Math.Min(a.Y, Math.Min(b.Y, c.Y)) > maxY) continue;
            var triangle = new[] { new Point2D(a.X, a.Y), new Point2D(b.X, b.Y), new Point2D(c.X, c.Y) };
            if (PolygonBoolean.Combine(triangle, outline, BooleanOperation.Intersection).Sum(piece => piece.Area) < 1) continue;

            // The plane through the three: its fall, as its normal leans.
            var (ux, uy, uz) = (b.X - a.X, b.Y - a.Y, b.Z - a.Z);
            var (vx, vy, vz) = (c.X - a.X, c.Y - a.Y, c.Z - a.Z);
            var (nx, ny, nz) = (uy * vz - uz * vy, uz * vx - ux * vz, ux * vy - uy * vx);
            if (Math.Abs(nz) < 1e-9) continue;
            var slope = Math.Sqrt(nx * nx + ny * ny) / Math.Abs(nz);
            if (steepest is not { } known || slope > known) steepest = slope;
        }

        return steepest;
    }

    /// <summary>
    /// What about a region falls short. A path, or paving, that is the approach to a door:
    /// Approved Document M takes 1:20 or gentler as gently sloping; steeper is a ramp - its
    /// flights 2 m at 1:12, 5 m at 1:15, 10 m at 1:20 (M4(2) 2.10, Diagram 2.1) - and steeper
    /// than 1:12 is no approach without steps; a wheelchair user's (M4(3) 3.10) no steeper than
    /// 1:15. Loose gravel is no suitable surface for one (2.9e, 3.9e). The ADA: an accessible
    /// route at 1:20, a ramp at 1:12 at most, its surface firm and stable (302.1).
    /// </summary>
    public static IReadOnlyList<string> Problems(BimDocument document, GroundRegion region)
    {
        var problems = new List<string>();

        // A lawn steeper than 1 in 3 is not mown safely: plant it, or step it with a retaining wall.
        if (region.Surface == GroundSurface.Lawn && Steepest(document, region) is { } lawn && lawn > 1 / 3.0 + 1e-6)
            problems.Add($"It falls 1:{1 / lawn:0.#} at its steepest: a lawn steeper than 1 in 3 is not mown safely - plant it, or step it with a retaining wall.");

        var us = document.ProjectInformation.ClearanceStandard.IsUs();
        if (region.Approach && region.Surface == GroundSurface.Gravel)
            problems.Add(us
                ? "It is laid in gravel: the ADA asks an accessible route for a firm, stable surface (302.1) - loose gravel is not."
                : "It is laid in gravel: Approved Document M asks an approach for a suitable ground surface (2.9e) - firm and even, which loose gravel is not.");
        if (!region.Approach || region.Surface is not (GroundSurface.Path or GroundSurface.Paving)) return problems;
        problems.AddRange(Approaches.PathProblems(document, region));
        if (Steepest(document, region) is not { } steepest || steepest <= 1 / 20.0 + 1e-6) return problems;

        // A ramp built over it takes the slope, and is checked as a ramp.
        if (RampOver(document, region) is not null) return problems;

        var said = $"1:{1 / steepest:0.#}";
        if (us)
        {
            if (steepest > 1 / 12.0 + 1e-6)
                problems.Add($"It falls {said} at its steepest: the ADA allows a ramp no steeper than 1:12 on an accessible route.");
            else problems.Add($"It falls {said} at its steepest: steeper than 1:20, the ADA takes it as a ramp - landings and handrails asked.");
            return problems;
        }

        if (document.ProjectInformation.ClearanceStandard == ClearanceStandard.ApprovedDocumentM3)
        {
            problems.Add(steepest > 1 / 15.0 + 1e-6
                ? $"It falls {said} at its steepest: Approved Document M allows a wheelchair user's approach no steeper than 1:15 (M4(3) 3.10a) - a ramp built over it, or a gentler way."
                : $"It falls {said} at its steepest: Approved Document M takes it as a ramp (M4(3) 3.10, Diagram 3.1) - its flights 5 m long at 1:15 and 10 m at 1:20 at most, 1200 wide, with level landings.");
            return problems;
        }

        problems.Add(steepest > 1 / 12.0 + 1e-6
            ? $"It falls {said} at its steepest: Approved Document M allows an approach no steeper than 1:12 (M4(2) 2.10a) - a ramp built over it, or steps to another way."
            : $"It falls {said} at its steepest: Approved Document M takes it as a ramp (M4(2) 2.10, Diagram 2.1) - its flights 2 m long at 1:12, 5 m at 1:15 and 10 m at 1:20 at most, with landings.");
        return problems;
    }

    public static string Verdict(BimDocument document, GroundRegion region)
    {
        var problems = Problems(document, region);
        if (problems.Count > 0) return string.Join(" ", problems);
        if (region.Approach && RampOver(document, region) is not null && Steepest(document, region) is > 1 / 20.0 + 1e-6) return "Too steep for an approach alone: ramped, the ramp checked as one";
        return region.Approach ? "Gently sloping enough for an approach"
            : region.Surface == GroundSurface.Lawn ? "Gentle enough to mow"
            : "Not an approach to a door: not checked";
    }

    /// <summary>A ramp built over a region - a fair part of it under the ramp - if there is one.</summary>
    public static Ramp? RampOver(BimDocument document, GroundRegion region)
    {
        if (region.Boundary.Count < 3) return null;
        foreach (var ramp in document.All<Ramp>())
        {
            var footprint = Ramps.Layout(document, ramp).Footprint;
            if (footprint.Count < 3) continue;
            var shared = PolygonBoolean.Combine(footprint, region.Boundary, BooleanOperation.Intersection).Sum(piece => piece.Area);
            if (shared > 0.25 * Math.Min(Polygon2D.Area(footprint), Polygon2D.Area(region.Boundary))) return ramp;
        }

        return null;
    }

    /// <summary>
    /// A path drawn along a line, so wide: the outline of a band that wide either side of it,
    /// its corners mitred.
    /// </summary>
    public static IReadOnlyList<Point2D> Band(IReadOnlyList<Point2D> line, double width)
    {
        if (line.Count < 2) return Array.Empty<Point2D>();
        var left = Railings.SetIn(line, false, width / 2);
        var right = Railings.SetIn(line, false, -width / 2);
        return left.Concat(right.Reverse()).ToList();
    }
}
