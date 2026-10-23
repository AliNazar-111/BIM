using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Materials;
using BIMDesigner.Core.Sheets;
using BIMDesigner.Core.Views;

namespace BIMDesigner.Core.Mep;

/// <summary>
/// A storey's services plans: its mechanical, electrical and plumbing plans, each a view of its
/// own beside the storey's plan - on sheets of their own, at scales of their own. One draws the
/// building faint, as an engineer's layout does, and its own discipline's fittings in its colour -
/// plumbing blue, mechanical orange, electrical magenta - any not connected yet in red.
/// </summary>
public static class ServicesPlans
{
    /// <summary>What a fitting not connected yet is drawn in on its services plan.</summary>
    public static readonly ColourRgb Unconnected = new(0xE0, 0x30, 0x1E);

    /// <summary>The plan of a storey in a discipline: the storey's own plan for the building itself.</summary>
    public static ViewReference Plan(Guid levelId, Discipline discipline) => ViewReference.FloorPlan(levelId) with { Index = (int)discipline };

    /// <summary>The discipline a view is a plan of; the building itself for any other view.</summary>
    public static Discipline Of(ViewReference view) =>
        view.Kind == ViewKind.FloorPlan && view.Index is >= 1 and <= 3 ? (Discipline)view.Index : Discipline.Architectural;

    /// <summary>What a storey's plan in a discipline is called after the storey: "Plan", "Electrical Plan".</summary>
    public static string Title(Discipline discipline) => discipline == Discipline.Architectural ? "Plan" : $"{discipline.Title()} Plan";

    /// <summary>The colour a discipline's fittings are drawn in on its plan.</summary>
    public static ColourRgb Colour(Discipline discipline) => discipline switch
    {
        Discipline.Plumbing => new ColourRgb(0x3A, 0x8E, 0xF0),
        Discipline.Mechanical => new ColourRgb(0xF0, 0x9A, 0x3C),
        Discipline.Electrical => new ColourRgb(0xD8, 0x4C, 0xC8),
        _ => new ColourRgb(0x80, 0x80, 0x80)
    };

    /// <summary>The services plans there are of a storey, its own plan first.</summary>
    public static IReadOnlyList<Discipline> All { get; } = Enum.GetValues<Discipline>();

    /// <summary>
    /// How a services plan draws each element: the building faint, its own discipline's fittings
    /// in its colour, and red where one is not connected yet. Null for the storey's own plan.
    /// </summary>
    public static Func<Element, ElementStyle>? StyleFor(BimDocument document, Discipline discipline)
    {
        var faint = new ElementStyle(Halftone: true);

        // The building's own plan shows the services' pipes faint, under the building.
        if (discipline == Discipline.Architectural)
            return document.All<Pipe>().Any() || document.All<Duct>().Any() ? element => element is Pipe or Duct ? faint : default : null;

        var open = new ElementStyle(Colour: Unconnected);
        var connected = new ElementStyle(Colour: Colour(discipline));
        return element =>
        {
            if (Phasing.IsAnnotation(element.Category)) return default;

            // A pipe in its own colour on its discipline's plan; faint on the others.
            if (element is Pipe pipe) return pipe.Kind.Discipline() == discipline ? default : faint;
            if (element is Duct duct) return duct.Kind.Discipline() == discipline ? default : faint;

            // The drains' chambers, outfalls and soakaways, and air admittance valves, are the plumbing's.
            if (element is Component { Category: BuiltInCategory.Drainage } || element is Component valve && document.FindType<ComponentType>(valve.TypeId)?.Form == ComponentForm.AirAdmittanceValve)
                return discipline == Discipline.Plumbing ? connected : faint;
            if (element is not Component component || !MepSystems.ConnectorsOf(document, component).Any(connector => connector.Kind.Discipline() == discipline))
                return faint;
            return MepSystems.OpenKinds(document, component).Any(kind => kind.Discipline() == discipline) ? open : connected;
        };
    }
}
