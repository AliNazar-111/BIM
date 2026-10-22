using System.Windows;
using System.Windows.Media;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Mep;
using BIMDesigner.Core.Views;

namespace BIMDesigner.UI.Controls;

/// <summary>
/// The services on the plan: which discipline's plan it is - the storey's own, or its
/// mechanical, electrical or plumbing plan - the connectors of the fittings selected, each a
/// dot in its kind's colour and ringed red where it is not connected; and a system being edited,
/// its fittings clicked on or off it, and the equipment feeding it picked.
/// </summary>
public partial class PlanView
{
    private Discipline _discipline;
    private MepSystem? _editingSystem;

    /// <summary>Which of the storey's plans this is: its own, or one of its services plans.</summary>
    public Discipline ActiveDiscipline
    {
        get => _discipline;
        set
        {
            if (_discipline == value) return;
            _discipline = value;
            if (value != Discipline.Architectural) _activeCallout = null;
            ViewChanged?.Invoke(this, EventArgs.Empty);
            InvalidateVisual();
        }
    }

    /// <summary>The system whose fittings the Edit System tool clicks on and off, and whose source Pick Source picks.</summary>
    public MepSystem? EditingSystem
    {
        get => _editingSystem;
        private set
        {
            _editingSystem = value;
            InvalidateVisual();
        }
    }

    /// <summary>Starts clicking fittings on and off a system.</summary>
    public void EditSystem(MepSystem system)
    {
        SetTool(PlanTool.EditSystem);
        EditingSystem = system;
        HintChanged?.Invoke(this, $"Editing {system.Name}: click a fitting to put it on or take it off - {MepSystems.MembersOf(Document!, system).Count} on it now. Esc when done.");
    }

    /// <summary>Starts picking the equipment that feeds a system.</summary>
    public void PickSystemSource(MepSystem system)
    {
        SetTool(PlanTool.PickSource);
        EditingSystem = system;
        HintChanged?.Invoke(this, $"Click what feeds {system.Name}: {system.Kind.Fed() ?? "nothing needs to"}.");
    }

    /// <summary>
    /// A click with Edit System: the fitting there put on the system, or taken off it if it is on
    /// it. Public so it can be driven from tests.
    /// </summary>
    public bool ClickSystemMember(Point2D at)
    {
        if (Document is null || EditingSystem is not { } system || ComponentAt(at) is not { } fitting) return false;

        var on = system.Members.Contains(fitting.Id);
        if (!on && MepSystems.WhyNotMember(Document, fitting, system) is { } why)
        {
            HintChanged?.Invoke(this, $"{why}.");
            return false;
        }

        var name = Document.FindType<ComponentType>(fitting.TypeId)?.Name ?? "Fitting";
        Apply(new EditSystemCommand(system, on ? $"Remove from {system.Name}" : $"Add to {system.Name}", edited =>
        {
            if (on) edited.Members.Remove(fitting.Id);
            else edited.Members.Add(fitting.Id);
        }));
        HintChanged?.Invoke(this, $"{name} {(on ? "taken off" : "put on")} {system.Name} - {MepSystems.MembersOf(Document, system).Count} on it. Click another, or Esc when done.");
        ModelChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
        return true;
    }

    /// <summary>A click with Pick Source: the equipment there made what feeds the system. Public so it can be driven from tests.</summary>
    public bool ClickSystemSource(Point2D at)
    {
        if (Document is null || EditingSystem is not { } system || ComponentAt(at) is not { } equipment) return false;

        if (MepSystems.WhyNotSource(Document, equipment, system) is { } why)
        {
            HintChanged?.Invoke(this, $"{why}.");
            return false;
        }

        Apply(new EditSystemCommand(system, $"Source of {system.Name}", edited => edited.SourceId = equipment.Id));
        HintChanged?.Invoke(this, $"{Document.FindType<ComponentType>(equipment.TypeId)?.Name ?? "It"} feeds {system.Name}.");
        ModelChanged?.Invoke(this, EventArgs.Empty);
        SetTool(PlanTool.Select);
        return true;
    }

    private static readonly Pen ConnectorRing = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0x1C, 0x21, 0x28))), 1));
    private static readonly Pen OpenRing = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0xE0, 0x30, 0x1E))), 2));

    private static Color ColourOf(SystemKind kind)
    {
        var colour = kind.Colour();
        return Color.FromRgb(colour.R, colour.G, colour.B);
    }

    /// <summary>
    /// The connectors of the fittings selected on this storey, and a system being edited: its
    /// fittings outlined in its colour, the equipment feeding it dashed.
    /// </summary>
    private void DrawSystems(DrawingContext dc)
    {
        if (Document is null) return;

        if (EditingSystem is { } system && ActiveTool is PlanTool.EditSystem or PlanTool.PickSource)
        {
            var colour = ColourOf(system.Kind);
            var member = new Pen(new SolidColorBrush(colour), 3);
            var source = new Pen(new SolidColorBrush(colour), 2) { DashStyle = DashStyles.Dash };
            foreach (var fitting in MepSystems.MembersOf(Document, system).Where(fitting => fitting.LevelId == ActiveLevelId))
                DrawFootprint(dc, fitting, member);
            if (MepSystems.SourceOf(Document, system) is { } feeding && feeding.LevelId == ActiveLevelId)
                DrawFootprint(dc, feeding, source);
        }

        foreach (var fitting in _selection.OfType<Component>().Where(fitting => fitting.LevelId == ActiveLevelId))
        {
            var placed = Connectors.Placed(Document, fitting);
            if (placed.Count == 0) continue;
            var open = MepSystems.OpenKinds(Document, fitting);
            foreach (var connector in placed)
            {
                var at = ModelToScreen(new Point2D(connector.At.X, connector.At.Y));
                var fill = new SolidColorBrush(ColourOf(connector.Connector.Kind));
                dc.DrawEllipse(fill, open.Contains(connector.Connector.Kind) && !connector.Connector.Optional ? OpenRing : ConnectorRing, at, 4, 4);
            }
        }
    }

    private void DrawFootprint(DrawingContext dc, Component fitting, Pen pen)
    {
        if (Document!.FindType<ComponentType>(fitting.TypeId) is not { } type) return;
        var outline = ComponentModel.Footprint(type, ComponentModel.FrameOf(fitting, 0));
        if (outline.Count < 3) return;

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(ModelToScreen(outline[0]), false, true);
            context.PolyLineTo(outline.Skip(1).Select(ModelToScreen).ToList(), true, false);
        }

        geometry.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }
}
