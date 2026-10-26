using System.Windows;
using System.Windows.Media;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Mep;

namespace BIMDesigner.UI.Rendering;

/// <summary>
/// The wiring on an electrical plan, as it is drawn: each circuit's run from fitting to fitting an
/// arc in the circuit's colour, the fitting nearest its board with a home run arrow toward it and
/// the circuit's name; and the switch wire from each switch to the lights it turns on, dashed.
/// </summary>
public sealed partial class PlanRenderer
{
    /// <summary>Which of the storey's plans is drawn: its own, or one of its services plans.</summary>
    public Discipline Discipline { get; set; }

    private void DrawWiring(DrawingContext dc)
    {
        if (Document is null || Discipline != Discipline.Electrical) return;
        var document = Document;

        Point2D? Port(Component fitting, SystemKind kind) =>
            Connectors.Placed(document, fitting).Where(placed => placed.Connector.Kind == kind).Select(placed => (Point2D?)new Point2D(placed.At.X, placed.At.Y)).FirstOrDefault();

        var power = SystemKind.Power.Colour();
        var circuitBrush = new SolidColorBrush(Color.FromRgb(power.R, power.G, power.B));
        circuitBrush.Freeze();
        var circuitPen = new Pen(circuitBrush, Page(1.1));
        circuitPen.Freeze();
        foreach (var circuit in Circuits.Of(document))
        {
            var (_, stops) = Kept($"route {circuit.Id}", () => Circuits.Route(document, circuit));
            var points = new List<(Component Fitting, Point2D At)>();
            foreach (var (fitting, _) in stops)
                if (fitting.LevelId == ActiveLevelId && Port(fitting, SystemKind.Power) is { } at) points.Add((fitting, at));
            if (points.Count == 0) continue;

            for (var i = 0; i + 1 < points.Count; i++) Arc(dc, circuitPen, points[i].At, points[i + 1].At);

            // The home run: an arrow from the first fitting toward the board, and the circuit's name.
            var board = MepSystems.SourceOf(document, circuit);
            var first = points[0].At;
            var toward = board is not null && Port(board, SystemKind.Power) is { } feed ? feed : first + new Vector2D(0, -600);
            var direction = (toward - first).NormalisedOrDefault(Vector2D.UnitY);
            var reach = Math.Min(600, (toward - first).Length);
            if (reach < 1) reach = 600;
            var end = first + direction * reach;
            Arc(dc, circuitPen, first, end);
            var tip = ModelToScreen(end);
            var back = ModelToScreen(end - direction * 150);
            var side = new Vector(-(tip.Y - back.Y), tip.X - back.X) * 0.4;
            var head = new StreamGeometry();
            using (var context = head.Open())
            {
                context.BeginFigure(tip, true, true);
                context.LineTo(back + side, true, false);
                context.LineTo(back - side, true, false);
            }

            head.Freeze();
            dc.DrawGeometry(circuitBrush, null, head);
            var label = Text(circuit.Name, Page(9), circuitBrush);
            dc.DrawText(label, tip + new Vector(4, -label.Height - 2));
        }

        // Switch wires, dashed, from the switch to the lights it turns on.
        var switching = SystemKind.Switching.Colour();
        var switchBrush = new SolidColorBrush(Color.FromRgb(switching.R, switching.G, switching.B));
        switchBrush.Freeze();
        var switchPen = new Pen(switchBrush, Page(1)) { DashStyle = new DashStyle(new[] { 4.0, 3.0 }, 0) };
        switchPen.Freeze();
        foreach (var system in document.All<MepSystem>().Where(system => system.Kind == SystemKind.Switching))
        {
            var members = MepSystems.MembersOf(document, system).Where(member => member.LevelId == ActiveLevelId).ToList();
            var switches = members.Where(member => document.FindType<ComponentType>(member.TypeId)?.Form == ComponentForm.Switch).ToList();
            var lights = members.Except(switches).ToList();
            foreach (var turn in switches)
            {
                if (Port(turn, SystemKind.Switching) is not { } from) continue;
                var left = lights.Select(light => (Light: light, At: Port(light, SystemKind.Switching))).Where(entry => entry.At is not null).Select(entry => (entry.Light, At: entry.At!.Value)).ToList();
                var here = from;
                while (left.Count > 0)
                {
                    var next = left.OrderBy(entry => (entry.At - here).Length).First();
                    left.Remove(next);
                    Arc(dc, switchPen, here, next.At);
                    here = next.At;
                }
            }
        }
    }

    /// <summary>
    /// On a mechanical or plumbing plan, each pipe's and duct's supports: a short bar across the run at
    /// each clip or hanger, a small square where it holds a run going up.
    /// </summary>
    private void DrawSupports(DrawingContext dc)
    {
        if (Document is null || Discipline is not (Discipline.Mechanical or Discipline.Plumbing)) return;
        var document = Document;
        var pen = new Pen(new SolidColorBrush(Color.FromRgb(0x5A, 0x5E, 0x63)), Page(1.2));
        pen.Freeze();
        foreach (var service in document.All<Pipe>().Where(pipe => pipe.LevelId == ActiveLevelId && pipe.Kind.Discipline() == Discipline).Cast<Core.Elements.Element>()
                     .Concat(document.All<Duct>().Where(duct => duct.LevelId == ActiveLevelId && duct.Kind.Discipline() == Discipline)))
        {
            var (start, end, half) = service switch
            {
                Pipe pipe => (pipe.Start, pipe.End, pipe.Diameter / 2),
                Duct duct => (duct.Start, duct.End, duct.Width / 2),
                _ => (default(Point2D), default(Point2D), 0.0)
            };
            var along = (end - start).NormalisedOrDefault(Vector2D.UnitX);
            var across = new Vector2D(-along.Y, along.X) * (half + 60);
            foreach (var support in Kept($"supports {service.Id}", () => Supports.Of(document, service)))
            {
                var at = new Point2D(support.At.X, support.At.Y);
                if (support.Upright)
                {
                    var corner = ModelToScreen(at + new Vector2D(-half - 40, -half - 40));
                    var opposite = ModelToScreen(at + new Vector2D(half + 40, half + 40));
                    dc.DrawRectangle(null, pen, new Rect(corner, opposite));
                }
                else dc.DrawLine(pen, ModelToScreen(at + across), ModelToScreen(at - across));
            }
        }
    }

    /// <summary>A wire from one point to another, bowed a fifth of its length to one side, as wiring is drawn.</summary>
    private void Arc(DrawingContext dc, Pen pen, Point2D from, Point2D to)
    {
        var a = ModelToScreen(from);
        var b = ModelToScreen(to);
        var middle = new Point((a.X + b.X) / 2, (a.Y + b.Y) / 2);
        var bow = new Vector(-(b.Y - a.Y), b.X - a.X) * 0.2;
        var curve = new StreamGeometry();
        using (var context = curve.Open())
        {
            context.BeginFigure(a, false, false);
            context.QuadraticBezierTo(middle + bow, b, true, false);
        }

        curve.Freeze();
        dc.DrawGeometry(null, pen, curve);
    }
}
