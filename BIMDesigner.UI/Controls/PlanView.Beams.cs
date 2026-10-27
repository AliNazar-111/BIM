using System.Windows.Media;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.UI.Controls;

/// <summary>
/// Beams on the plan: click where one starts and where it ends - it hangs from this level, its
/// top there, under the floor it carries - and the next starts where it ended, a chain of them,
/// until Esc.
/// </summary>
public partial class PlanView
{
    /// <summary>The beam type the Beam tool puts in; none for the first there is.</summary>
    public Guid ActiveBeamTypeId { get; set; }

    /// <summary>How far above this level the next beam's line is, mm: 0 for its top at the level.</summary>
    public double ActiveBeamOffset { get; set; }

    private Point2D? _beamStart;
    private Point2D? _beamHover;

    private void BeamHover(Point2D at)
    {
        _beamHover = at;
        InvalidateVisual();
    }

    /// <summary>
    /// A click with the Beam tool: where a beam starts, then where it ends - and the next one
    /// starts there. Public so it can be driven from tests.
    /// </summary>
    public bool ClickBeam(Point2D at)
    {
        if (Document is null || Document.FindLevel(ActiveLevelId) is null) return false;

        if (_beamStart is not { } start)
        {
            _beamStart = at;
            HintChanged?.Invoke(this, "Now click where it ends: the next beam starts there. Esc to stop.");
            InvalidateVisual();
            return true;
        }

        if ((at - start).Length < 100) return false;

        var type = Document.FindType<BeamType>(ActiveBeamTypeId) ?? Document.TypesOf<BeamType>().FirstOrDefault();
        var beam = new Beam
        {
            LevelId = ActiveLevelId, TypeId = type?.Id ?? Guid.Empty, Start = start, End = at,
            StartOffset = ActiveBeamOffset, EndOffset = ActiveBeamOffset
        };
        Apply(new AddElementCommand(Document, beam, "Beam"));
        _beamStart = at;

        var clearances = Beams.Verdict(Document, beam);
        HintChanged?.Invoke(this, $"Beam in: {Beams.TypeOf(Document, beam).Name}, {Core.Units.Metres(beam.Length, "0.00")}, its underside at {Core.Units.FormatMm(Beams.Bottom(Document, beam, 0))}. {(clearances == "Clear" ? "" : clearances + " ")}Click where the next ends; Esc to stop.");
        ModelChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
        return true;
    }

    /// <summary>Esc with a beam begun: its start forgotten.</summary>
    private bool CancelBeam()
    {
        if (_beamStart is null) return false;
        _beamStart = null;
        InvalidateVisual();
        return true;
    }

    /// <summary>The beam the next click would put in, from where it starts to the cursor.</summary>
    private void DrawBeamPreview(DrawingContext dc)
    {
        if (ActiveTool != PlanTool.Beam || Document is null || _beamStart is not { } start || _beamHover is not { } hover || (hover - start).Length < 1) return;

        var type = Document.FindType<BeamType>(ActiveBeamTypeId) ?? Document.TypesOf<BeamType>().FirstOrDefault();
        var half = (type?.Width ?? 100) / 2;
        var across = (hover - start).NormalisedOrDefault(Vector2D.UnitX).PerpendicularLeft() * half;
        dc.DrawGeometry(null, SketchPreviewPen, Outline(new[] { start - across, hover - across, hover + across, start + across }));
    }

    /// <summary>The beams under a point of this plan: hung from its level, the point over them.</summary>
    private IEnumerable<Beam> BeamsAt(Point2D model)
    {
        if (Document is null) yield break;

        foreach (var beam in OnActiveLevel<Beam>())
        {
            var reach = Math.Max(Beams.TypeOf(Document, beam).Width / 2, 5 / PixelsPerMm);
            if (Line2D.DistanceFromSegment(model, beam.Start, beam.End) <= reach) yield return beam;
        }
    }
}
