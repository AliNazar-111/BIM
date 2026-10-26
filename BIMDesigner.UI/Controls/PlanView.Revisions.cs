using System.Windows.Media;
using BIMDesigner.Core.Annotation;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;
using BIMDesigner.UI.Rendering;

namespace BIMDesigner.UI.Controls;

/// <summary>
/// Revision clouds and keynotes on the plan. A cloud is clicked round what changed - corner by
/// corner, double-clicking the last; two corners make a rectangle - for the revision picked on
/// the options bar, its tag beside it. A keynote is clicked on what it notes, then where its
/// box goes: an element's type's keynote, the material there, or a key picked from the table.
/// </summary>
public partial class PlanView
{
    /// <summary>The revision new clouds are of; the project's latest not yet issued where none is picked.</summary>
    public Guid ActiveRevisionId { get; set; }

    /// <summary>Whether a new cloud has a tag with its revision's number.</summary>
    public bool CloudTagged { get; set; } = true;

    /// <summary>Whose keynote the Keynote tool puts down.</summary>
    public KeynoteSource ActiveKeynoteSource { get; set; } = KeynoteSource.Element;

    /// <summary>The key a keynote of its own carries.</summary>
    public string ActiveKeynoteKey { get; set; } = string.Empty;

    private readonly List<Point2D> _cloudPoints = new();
    private (Point2D At, Element? Target, Guid MaterialId)? _keynotePoint;

    /// <summary>The revision a cloud would be of now: the one picked, unless issued; else the latest still open.</summary>
    public Revision? CloudRevision()
    {
        if (Document is null) return null;
        if (Document.FindRevision(ActiveRevisionId) is { Issued: false } picked) return picked;
        return Document.Revisions.LastOrDefault(revision => !revision.Issued);
    }

    /// <summary>Whether an element cannot be changed: pinned, or a cloud of an issued revision.</summary>
    public bool IsLocked(Element element) => element.Pinned || (Document is not null && element is RevisionCloud cloud && cloud.IsLocked(Document));

    /// <summary>A click with the Revision Cloud tool: the next corner. Public so it can be driven from tests.</summary>
    public bool ClickCloud(Point2D raw)
    {
        if (Document is null) return false;

        // With no revisions yet, the first cloud makes the first; with every one issued, a new one has to be added.
        if (CloudRevision() is null && Document.Revisions.Count > 0)
        {
            HintChanged?.Invoke(this, "Every revision has been issued, and an issued revision cannot be added to: add a new one in Annotate → Revisions.");
            return false;
        }

        var point = SnapToGrid(raw);

        // Back at the first corner: the cloud closes there.
        if (_cloudPoints.Count >= 3 && point.DistanceTo(_cloudPoints[0]) <= 8 / PixelsPerMm) return FinishCloud();

        _cloudPoints.Add(point);
        HintChanged?.Invoke(this, _cloudPoints.Count == 1
            ? "Click the next corner - or double-click the opposite corner for a rectangle. Esc cancels."
            : "Click the next corner; double-click the last, or click the first, to close the cloud.");
        InvalidateVisual();
        return true;
    }

    /// <summary>
    /// The cloud clicked round, closed and made - two corners a rectangle - with its tag at its
    /// top right. Public so it can be driven from tests.
    /// </summary>
    public bool FinishCloud()
    {
        if (Document is null) return false;
        var revision = CloudRevision();
        var first = revision is null && Document.Revisions.Count == 0;
        if (revision is null && !first) return false;
        var outline = _cloudPoints.Count == 2 ? PlanRenderer.Rectangle(_cloudPoints[0], _cloudPoints[1]).ToList() : _cloudPoints.ToList();
        _cloudPoints.Clear();

        var box = BoundingBox2D.Around(outline);
        if (outline.Count < 3 || box.Width < 1 || box.Height < 1)
        {
            HintChanged?.Invoke(this, "A cloud has to go round something: click three corners or more, or two opposite ones.");
            InvalidateVisual();
            return false;
        }

        // The project's first cloud: the first revision made for it, dated today, as the same step.
        var document = Document;
        revision ??= new Revision { Date = DateTime.Today };
        var scale = document.ViewSettings.ScaleOf(CurrentView);
        var cloud = new RevisionCloud
        {
            RevisionId = revision.Id, LevelId = ActiveLevelId,
            TagAt = CloudTagged ? new Point2D(box.MaxX + 8 * scale, box.MaxY + 8 * scale) : null
        };
        cloud.Outline.AddRange(outline);
        var made = revision;
        IUndoableCommand add = new AddElementCommand(document, cloud, "Revision Cloud");
        if (first)
            add = new CompositeCommand("Revision Cloud", new[]
            {
                new DelegateCommand("Add Revision", () => document.Revisions.Add(made), () => document.Revisions.Remove(made)), add
            });
        Apply(add);
        ActiveRevisionId = revision.Id;
        Select(cloud);
        HintChanged?.Invoke(this, first
            ? $"Clouded for {Revisions.Describe(document, revision)}, the project's first revision, made for it and dated today - describe it in Annotate → Revisions. Every sheet showing this plan now lists it."
            : $"Clouded for {Revisions.Describe(document, revision)}: every sheet showing this plan now lists it.");
        InvalidateVisual();
        return true;
    }

    /// <summary>
    /// A click with the Keynote tool: on what it notes, then where its box goes. Public so it can
    /// be driven from tests.
    /// </summary>
    public bool ClickKeynote(Point2D raw)
    {
        if (Document is null) return false;

        if (_keynotePoint is not { } first)
        {
            var target = ActiveKeynoteSource == KeynoteSource.User ? null : HitCandidates(raw).FirstOrDefault(element => element is not (KeynoteTag or RevisionCloud));
            var material = Guid.Empty;
            switch (ActiveKeynoteSource)
            {
                case KeynoteSource.Element when target is null:
                    HintChanged?.Invoke(this, "Click the element to note: its type's keynote is what the tag reads.");
                    return false;

                case KeynoteSource.Material:
                    if (target is null || Keynotes.MaterialAt(Document, target, raw) is not { } found)
                    {
                        HintChanged?.Invoke(this, "Click a wall, floor, roof or ceiling: the keynote is its material's where it is clicked.");
                        return false;
                    }

                    material = found.Id;
                    break;

                case KeynoteSource.User when string.IsNullOrWhiteSpace(ActiveKeynoteKey):
                    HintChanged?.Invoke(this, "Pick the key on the options bar first.");
                    return false;
            }

            _keynotePoint = (raw, target, material);
            HintChanged?.Invoke(this, "Now click where the keynote's box goes.");
            InvalidateVisual();
            return true;
        }

        _keynotePoint = null;
        var keynote = new KeynoteTag
        {
            Source = ActiveKeynoteSource, TargetId = first.Target?.Id ?? Guid.Empty, MaterialId = first.MaterialId,
            Key = ActiveKeynoteSource == KeynoteSource.User ? ActiveKeynoteKey : string.Empty,
            Position = raw, LeaderEnd = first.At.DistanceTo(raw) < 1 ? null : first.At, LevelId = ActiveLevelId
        };
        Apply(new AddElementCommand(Document, keynote, "Keynote"));
        Select(keynote);

        var key = keynote.KeyIn(Document);
        HintChanged?.Invoke(this, key.Length == 0
            ? ActiveKeynoteSource == KeynoteSource.Material
                ? "The material there has no keynote yet: give it one in Keynotes on the Annotate tab, and the tag will read it."
                : "Its type has no keynote yet: give it one in Type Properties, and the tag will read it."
            : $"{key}: {Document.Keynotes.TextOf(key)}");
        InvalidateVisual();
        return true;
    }

    private bool CancelRevisionTools()
    {
        var was = _cloudPoints.Count > 0 || _keynotePoint is not null;
        _cloudPoints.Clear();
        _keynotePoint = null;
        if (was) InvalidateVisual();
        return was;
    }

    private void DrawPendingRevisionTools(DrawingContext dc)
    {
        if (ActiveTool == PlanTool.RevisionCloud && _cloudPoints.Count > 0)
            _renderer.DrawPendingCloud(dc, _cloudPoints.Append(SnapToGrid(_cursorModel)).ToList());

        if (ActiveTool == PlanTool.Keynote && _keynotePoint is { } first)
            dc.DrawLine(_selectedPen, ModelToScreen(first.At), ModelToScreen(_cursorModel));
    }

    /// <summary>Clouds and keynotes near a point: on a cloud's line or its tag, in a keynote's box or on its leader.</summary>
    private IEnumerable<Element> RevisionMarksAt(Point2D model)
    {
        if (Document is null) yield break;
        var reach = 6 / PixelsPerMm;
        var screen = ModelToScreen(model);

        foreach (var keynote in OnActiveLevel<KeynoteTag>())
        {
            var box = _renderer.KeynoteBox(keynote);
            box.Inflate(3, 3);
            if (box.Contains(screen) || (keynote.LeaderEnd is { } end && DistanceToSegment(model, keynote.Position, end) <= reach)) yield return keynote;
        }

        foreach (var cloud in OnActiveLevel<RevisionCloud>())
        {
            var near = cloud.Outline.Select((point, i) => DistanceToSegment(model, point, cloud.Outline[(i + 1) % cloud.Outline.Count])).DefaultIfEmpty(double.MaxValue).Min() <= reach * 2
                       || (cloud.TagAt is { } tag && tag.DistanceTo(model) <= 14 / PixelsPerMm);
            if (near) yield return cloud;
        }
    }
}
