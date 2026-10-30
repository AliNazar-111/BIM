using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Sheets;

namespace BIMDesigner.Core.Views;

/// <summary>How a property line is edged on planning drawings.</summary>
public enum PlanningEdge
{
    None,

    /// <summary>The application site: edged in red - all the land the development needs, out to the road it is reached from.</summary>
    ApplicationSite,

    /// <summary>Other land the applicant owns, close to or next to the site: edged in blue.</summary>
    OtherLand
}

/// <summary>Which planning drawing a view is: the location plan, or the block plan - in the US the vicinity map and the site plan.</summary>
public enum PlanningDrawing
{
    None,
    Location,
    Block
}

/// <summary>One thing a planning drawing is asked for: whether it is met, or not - or can only be checked by eye.</summary>
public sealed record PlanningCheck(bool? Met, string Said);

/// <summary>
/// The plans a planning application goes in with. In England the Town and Country Planning
/// (Development Management Procedure) (England) Order 2015, article 7(1)(c), asks for a plan
/// identifying the land, drawn to an identified scale and showing the direction of north - the
/// location plan, which the Planning Portal says is at 1:1250 or 1:2500 on up-to-date mapping, with
/// at least two named roads and the buildings round it, the site edged in red out to the road and
/// any other land the applicant owns near it in blue; and most councils' validation lists ask for a
/// block plan, at 1:500 or 1:200, of the development within its boundaries. Each is a view of the
/// ground floor plan cropped to the site at its scale, drawing only what such a plan shows - the
/// map under it, the building, the boundaries, the ground - on a sheet of its own with a north point
/// and a scale bar, and the mapping's copyright line under it. In the US, a vicinity map and a site
/// plan the same way.
/// </summary>
public static class PlanningDrawings
{
    /// <summary>What a location plan draws: the map, the buildings, the boundaries and the ground.</summary>
    private static readonly HashSet<BuiltInCategory> OnLocationPlans = new()
    {
        BuiltInCategory.Walls, BuiltInCategory.Floors, BuiltInCategory.Roofs, BuiltInCategory.PropertyLines, BuiltInCategory.Topography,
        BuiltInCategory.Pads, BuiltInCategory.ImportedDrawings, BuiltInCategory.LinkedModels, BuiltInCategory.CurtainPanels, BuiltInCategory.CurtainWallMullions
    };

    /// <summary>What a block plan draws as well: the doors and windows, the roof's edges, steps, ramps, railings, trees, parking.</summary>
    private static readonly HashSet<BuiltInCategory> OnBlockPlans = new(OnLocationPlans)
    {
        BuiltInCategory.Doors, BuiltInCategory.Windows, BuiltInCategory.Fascias, BuiltInCategory.Gutters, BuiltInCategory.Eaves, BuiltInCategory.RoofSoffits,
        BuiltInCategory.Stairs, BuiltInCategory.Ramps, BuiltInCategory.Railings, BuiltInCategory.Planting, BuiltInCategory.Parking, BuiltInCategory.SlabEdges
    };

    /// <summary>Which planning drawing a view is, or none.</summary>
    public static PlanningDrawing Of(BimDocument document, ViewReference view) =>
        view.Kind == ViewKind.Callout && document.Find<Callout>(view.TargetId) is { } callout ? callout.Planning : PlanningDrawing.None;

    /// <summary>Whether a planning drawing draws an element.</summary>
    public static bool Shows(PlanningDrawing drawing, Element element) =>
        drawing == PlanningDrawing.None || (drawing == PlanningDrawing.Location ? OnLocationPlans : OnBlockPlans).Contains(element.Category);

    /// <summary>What a planning drawing is called: the location plan and the block plan; in the US the vicinity map and the site plan.</summary>
    public static string Name(PlanningDrawing drawing, bool us) => (drawing, us) switch
    {
        (PlanningDrawing.Location, false) => "Location Plan",
        (PlanningDrawing.Location, true) => "Vicinity Map",
        (PlanningDrawing.Block, false) => "Block Plan",
        (PlanningDrawing.Block, true) => "Site Plan",
        _ => string.Empty
    };

    /// <summary>The scales each is drawn at: the location plan at 1:1250 or 1:2500, the block plan at 1:500 or 1:200; in the US 1" = 200' and 1" = 20'.</summary>
    public static IReadOnlyList<double> Scales(PlanningDrawing drawing, bool us) => (drawing, us) switch
    {
        (PlanningDrawing.Location, false) => new double[] { 1250, 2500 },
        (PlanningDrawing.Block, false) => new double[] { 500, 200 },
        (PlanningDrawing.Location, true) => new double[] { 2400, 4800 },
        _ => new double[] { 240, 120 }
    };

    /// <summary>The paper each goes on: the location plan A4, the block plan A3 - both across, the title block taking their right-hand side.</summary>
    public static (PaperSize Size, PaperOrientation Orientation) Paper(PlanningDrawing drawing) =>
        drawing == PlanningDrawing.Location ? (PaperSize.A4, PaperOrientation.Landscape) : (PaperSize.A3, PaperOrientation.Landscape);

    /// <summary>The planning drawing of a kind, where one has been made.</summary>
    public static Callout? Drawing(BimDocument document, PlanningDrawing drawing) =>
        document.All<Callout>().FirstOrDefault(callout => callout.Planning == drawing);

    /// <summary>The land edged in red, where one property line is.</summary>
    public static PropertyLine? Site(BimDocument document) =>
        document.All<PropertyLine>().FirstOrDefault(line => line.Planning == PlanningEdge.ApplicationSite);

    /// <summary>
    /// The location plan and the block plan made, or brought up to date: each a view of the
    /// storey the red line is on, cropped round it to fill its sheet at its scale, coarse, with a
    /// north point, a scale bar and the mapping's note - a sheet of its own each. One step to undo.
    /// </summary>
    public static IUndoableCommand Make(BimDocument document, double locationScale, double blockScale, string mapNote)
    {
        var steps = new List<IUndoableCommand>();
        var info = document.ProjectInformation;
        var (noteBefore, noteAfter) = (info.PlanningMapNote, mapNote.Trim());
        if (noteBefore != noteAfter) steps.Add(new DelegateCommand("Map Note", () => info.PlanningMapNote = noteAfter, () => info.PlanningMapNote = noteBefore));

        foreach (var (drawing, scale) in new[] { (PlanningDrawing.Location, locationScale), (PlanningDrawing.Block, blockScale) })
            steps.AddRange(MakeOne(document, drawing, scale, noteAfter));

        return new CompositeCommand("Planning Drawings", steps);
    }

    private static IEnumerable<IUndoableCommand> MakeOne(BimDocument document, PlanningDrawing drawing, double scale, string mapNote)
    {
        var us = document.ProjectInformation.ClearanceStandard.IsUs();
        var name = Name(drawing, us);
        var site = Site(document);
        var level = site?.LevelId ?? document.Levels.OrderBy(each => Math.Abs(each.Elevation)).First().Id;
        var parent = ViewReference.FloorPlan(level);

        // Round the red line, or the building where there is none yet.
        var middle = site is not null ? BoundingBox2D.Around(site.Boundary).Centre
            : document.Walls.Where(wall => wall.LevelId == level).Aggregate(BoundingBox2D.Empty, (box, wall) => box.Include(wall.Start).Include(wall.End)) is { IsEmpty: false } built ? built.Centre : default;

        // The box filling the sheet's drawing area at the scale, the title and note under it.
        var (size, orientation) = Paper(drawing);
        var sheet = document.All<Sheet>().FirstOrDefault(each => each.Viewports.Any(viewport => viewport.View.Kind == ViewKind.Callout && document.Find<Callout>(viewport.View.TargetId)?.Planning == drawing));
        var paper = sheet ?? new Sheet { PaperSize = size, Orientation = orientation };
        var area = paper.DrawingArea;
        var (paperWidth, paperHeight) = (area.Width - 10, area.Height - 10 - Viewport.TitleBand - Viewport.NoteBand);
        var region = new BoundingBox2D(middle.X - paperWidth * scale / 2, middle.Y - paperHeight * scale / 2, middle.X + paperWidth * scale / 2, middle.Y + paperHeight * scale / 2);

        if (Drawing(document, drawing) is { } callout)
        {
            var (before, beforeLevel, beforeParent) = (callout.Region, callout.LevelId, callout.Parent);
            yield return new DelegateCommand(name, () => (callout.Region, callout.LevelId, callout.Parent) = (region, level, parent),
                () => (callout.Region, callout.LevelId, callout.Parent) = (before, beforeLevel, beforeParent));
        }
        else
        {
            callout = new Callout { Name = name, Parent = parent, Region = region, LevelId = level, Planning = drawing };
            yield return new AddElementCommand(document, callout, name);
        }

        var view = Callouts.Reference(callout);
        yield return new SetViewScaleCommand(document, view, scale);
        yield return new SetViewDetailLevelCommand(document, view, DetailLevel.Coarse);

        var centre = new Point2D(area.MinX + area.Width / 2, area.MinY + 5 + Viewport.TitleBand + Viewport.NoteBand + paperHeight / 2);
        if (sheet is null)
        {
            var made = new Sheet
            {
                Number = NextNumber(document, drawing), Name = name, PaperSize = size, Orientation = orientation, DrawnBy = Environment.UserName
            };
            made.Add(new Viewport { View = view, Centre = centre, Scale = new ViewScale(scale), ShowNorth = true, ShowScaleBar = true, Note = mapNote });
            yield return new AddElementCommand(document, made, $"Add Sheet {made.Number}");
        }
        else if (sheet.Viewports.First(viewport => viewport.View == view) is var placed)
        {
            var before = (placed.Centre, placed.Scale, placed.ShowNorth, placed.ShowScaleBar, placed.Note);
            var after = (centre, new ViewScale(scale), true, true, mapNote);
            yield return new DelegateCommand($"{name} Sheet",
                () => (placed.Centre, placed.Scale, placed.ShowNorth, placed.ShowScaleBar, placed.Note) = after,
                () => (placed.Centre, placed.Scale, placed.ShowNorth, placed.ShowScaleBar, placed.Note) = before);
        }
    }

    /// <summary>A planning drawing's sheet number: PL-001 for the location plan, PL-002 the block plan - or the next free.</summary>
    private static string NextNumber(BimDocument document, PlanningDrawing drawing)
    {
        var taken = document.All<Sheet>().Select(sheet => sheet.Number).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var number = drawing == PlanningDrawing.Location ? 1 : 2; ; number++)
            if (!taken.Contains($"PL-{number:000}")) return $"PL-{number:000}";
    }

    /// <summary>The sheet a planning drawing is on, or null.</summary>
    public static Sheet? SheetOf(BimDocument document, PlanningDrawing drawing) =>
        Drawing(document, drawing) is { } callout
            ? document.All<Sheet>().FirstOrDefault(sheet => sheet.Viewports.Any(viewport => viewport.View == Callouts.Reference(callout)))
            : null;

    /// <summary>
    /// What the planning drawings are asked for, each met or not: the red line, the blue line kept
    /// off it, each drawing at a scale it is asked for, round the whole site, with mapping under
    /// the location plan and its copyright line, a north point and a scale bar - and, to check by
    /// eye, the red line run out to the road.
    /// </summary>
    public static IReadOnlyList<PlanningCheck> Checks(BimDocument document)
    {
        var us = document.ProjectInformation.ClearanceStandard.IsUs();
        var checks = new List<PlanningCheck>();
        var reds = document.All<PropertyLine>().Where(line => line.Planning == PlanningEdge.ApplicationSite).ToList();
        var blues = document.All<PropertyLine>().Where(line => line.Planning == PlanningEdge.OtherLand).ToList();

        checks.Add(reds.Count switch
        {
            0 => new PlanningCheck(false, us
                ? "No lot line is marked as the site: pick the property line round it and set its Planning Edge to the application site."
                : "No red line: pick the property line round the application site and set its Planning Edge to Red line - the site edged in red is what a location plan is for (DMPO 2015, art. 7(1)(c)(i))."),
            1 => new PlanningCheck(true, $"The application site {(us ? "is outlined" : "is edged in red")}: {Units.SquareMetres(Polygon2D.Area(reds[0].Boundary), "#,0")}."),
            _ => new PlanningCheck(false, $"{reds.Count} property lines are edged in red: an application has one site - mark the others blue, or none.")
        });

        if (reds.Count == 1 && blues.FirstOrDefault(blue => Overlap(reds[0].Boundary, blue.Boundary)) is { } overlapping)
            checks.Add(new PlanningCheck(false, "Land edged in blue overlaps the red line: blue is the applicant's other land, outside the site."));
        else if (blues.Count > 0)
            checks.Add(new PlanningCheck(true, $"Other land the applicant owns edged in blue: {blues.Count} {(blues.Count == 1 ? "piece" : "pieces")}."));

        foreach (var drawing in new[] { PlanningDrawing.Location, PlanningDrawing.Block })
        {
            var name = Name(drawing, us);
            if (Drawing(document, drawing) is not { } callout)
            {
                checks.Add(new PlanningCheck(false, $"No {name.ToLowerInvariant()} yet: Make Drawings makes it."));
                continue;
            }

            var view = Callouts.Reference(callout);
            var scale = document.ViewSettings.ScaleOf(view);
            var asked = Scales(drawing, us);
            checks.Add(asked.Any(each => Math.Abs(each - scale) < 0.5)
                ? new PlanningCheck(true, $"The {name.ToLowerInvariant()} is at {new ViewScale(scale)}.")
                : new PlanningCheck(false, $"The {name.ToLowerInvariant()} is at {new ViewScale(scale)}: {(us ? "it is" : "the Planning Portal asks for it")} at {string.Join(" or ", asked.Select(each => new ViewScale(each).ToString()))}."));

            if (reds.Count == 1 && !Within(callout.Region, BoundingBox2D.Around(reds[0].Boundary)))
                checks.Add(new PlanningCheck(false, $"The {name.ToLowerInvariant()} does not take in the whole site: Make Drawings again to centre it, or draw it at the smaller scale."));

            if (SheetOf(document, drawing) is not { } sheet || sheet.Viewports.FirstOrDefault(each => each.View == view) is not { } placed)
                checks.Add(new PlanningCheck(false, $"The {name.ToLowerInvariant()} is on no sheet."));
            else if (!placed.ShowNorth || !placed.ShowScaleBar)
                checks.Add(new PlanningCheck(false, $"The {name.ToLowerInvariant()} on {sheet.Number} has no {(!placed.ShowNorth ? "north point" : "scale bar")}: a plan is drawn to an identified scale, showing north (DMPO 2015, art. 7(1)(c)(i))."));

            if (drawing == PlanningDrawing.Location)
            {
                var maps = document.All<CadUnderlay>().Where(map => map.LevelId == callout.LevelId && Meets(map.Bounds, callout.Region)).ToList();
                checks.Add(maps.Count > 0
                    ? new PlanningCheck(true, $"Drawn over {string.Join(", ", maps.Select(map => System.IO.Path.GetFileName(map.Source)))}.")
                    : new PlanningCheck(false, us
                        ? "No map under the vicinity map: import one - the streets round the site - as a DWG or DXF (Manage → Import CAD)."
                        : "No map under the location plan: it shows the site among its surroundings - at least two named roads and the buildings round it - on up-to-date Ordnance Survey mapping, bought as DWG or DXF from a map reseller (Manage → Import CAD)."));
                if (!us)
                    checks.Add(string.IsNullOrWhiteSpace(document.ProjectInformation.PlanningMapNote)
                        ? new PlanningCheck(false, "No copyright line: Ordnance Survey mapping carries its notice and licence - \"© Crown copyright and database rights 2026 OS AC0000000000\", as the reseller gives it.")
                        : new PlanningCheck(true, $"Its mapping credited: {document.ProjectInformation.PlanningMapNote}"));
            }
        }

        if (!us) checks.Add(new PlanningCheck(null, "To check by eye: the red line runs out to the road the site is reached from, and takes in all the land the work needs - access, parking, landscaping, visibility splays."));
        return checks;
    }

    private static bool Within(BoundingBox2D outer, BoundingBox2D inner) =>
        inner.MinX >= outer.MinX && inner.MinY >= outer.MinY && inner.MaxX <= outer.MaxX && inner.MaxY <= outer.MaxY;

    private static bool Meets(BoundingBox2D a, BoundingBox2D b) =>
        !a.IsEmpty && !b.IsEmpty && a.MinX <= b.MaxX && b.MinX <= a.MaxX && a.MinY <= b.MaxY && b.MinY <= a.MaxY;

    /// <summary>Whether two pieces of land overlap rather than meet along a side: either's middle, or a corner well in from it, inside the other.</summary>
    private static bool Overlap(IReadOnlyList<Point2D> red, IReadOnlyList<Point2D> blue)
    {
        static IEnumerable<Point2D> Inside(IReadOnlyList<Point2D> outline)
        {
            var middle = Polygon2D.Centroid(outline, outline[0]);
            yield return middle;
            foreach (var corner in outline) yield return corner + (middle - corner) * 0.05;
        }

        return Inside(blue).Any(point => Polygon2D.Contains(red, point)) || Inside(red).Any(point => Polygon2D.Contains(blue, point));
    }
}
