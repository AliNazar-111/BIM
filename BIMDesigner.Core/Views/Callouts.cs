using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Parameters;
using BIMDesigner.Core.Sheets;

namespace BIMDesigner.Core.Views;

/// <summary>
/// A callout (specification section 6.1): a box drawn on a plan or a section round a part of it,
/// which is a view of its own - that part, at a larger scale and finer detail, cropped to the
/// box. Its tag on the view it is drawn on carries its detail number and the sheet it is on, as
/// Revit's does, once it is placed on one.
/// </summary>
public sealed class Callout : Element
{
    public override BuiltInCategory Category => BuiltInCategory.Callouts;

    public string Name { get; set; } = "Callout 1";

    /// <summary>The view it is drawn on: a floor plan or a section.</summary>
    public ViewReference Parent { get; set; }

    /// <summary>The box, in the view it is drawn on: plan millimetres, or along a section and up it.</summary>
    public BoundingBox2D Region { get; set; }

    public override IEnumerable<ParameterValue> GetInstanceParameters(BimDocument document)
    {
        yield return ParameterValue.Bind(CalloutParameters.Name, () => Name, value => { if (!string.IsNullOrWhiteSpace(value)) Name = value.Trim(); });
        yield return ParameterValue.ReadOnly(CalloutParameters.Parent, () => Parent.TitleIn(document));
        yield return ParameterValue.ReadOnly(CalloutParameters.Width, () => Region.Width);
        yield return ParameterValue.ReadOnly(CalloutParameters.Height, () => Region.Height);
        yield return ParameterValue.ReadOnly(CalloutParameters.Sheet, () => Callouts.PlacedOn(document, Callouts.Reference(this)) is { } placed ? $"{placed.Detail} / {placed.Sheet}" : "Not placed");
        foreach (var common in GetCommonParameters(document)) yield return common;
    }
}

public static class CalloutParameters
{
    public static readonly ParameterDefinition Name = new("View Name", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.IdentityData);
    public static readonly ParameterDefinition Parent = new("Parent View", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.IdentityData);
    public static readonly ParameterDefinition Width = new("Crop Width", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Extents);
    public static readonly ParameterDefinition Height = new("Crop Height", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Extents);
    public static readonly ParameterDefinition Sheet = new("Detail / Sheet Number", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.IdentityData);
}

public static class Callouts
{
    /// <summary>A plan callout's scale: an enlarged plan, 1:50.</summary>
    public const double PlanScale = 50;

    /// <summary>A section callout's scale: a detail, 1:20.</summary>
    public const double SectionScale = 20;

    /// <summary>The smallest a callout's box is, mm each way.</summary>
    public const double Smallest = 200;

    public static ViewReference Reference(Callout callout) => ViewReference.Callout(callout.Id);

    public static string NextName(BimDocument document)
    {
        var names = document.All<Callout>().Select(callout => callout.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; ; i++)
            if (!names.Contains($"Callout {i}")) return $"Callout {i}";
    }

    /// <summary>The sheet a view is placed on and its detail number there, or null where it is on none.</summary>
    public static (string Detail, string Sheet)? PlacedOn(BimDocument document, ViewReference view)
    {
        foreach (var sheet in document.All<Sheet>())
            if (sheet.Viewports.FirstOrDefault(viewport => viewport.View == view) is { } placed)
                return (placed.DetailNumber, sheet.Number);
        return null;
    }

    /// <summary>The callouts drawn on a view.</summary>
    public static IEnumerable<Callout> On(BimDocument document, ViewReference view) =>
        document.All<Callout>().Where(callout => callout.Parent == view);

    /// <summary>
    /// A callout drawn on a view round a box: named, its view at the scale callouts of its kind are
    /// drawn at - 1:50 off a plan, 1:20 off a section - and in fine detail. One step to undo.
    /// </summary>
    public static (Callout Callout, IUndoableCommand Command) Draw(BimDocument document, ViewReference parent, Guid levelId, Point2D corner, Point2D other)
    {
        var region = new BoundingBox2D(Math.Min(corner.X, other.X), Math.Min(corner.Y, other.Y), Math.Max(corner.X, other.X), Math.Max(corner.Y, other.Y));
        var callout = new Callout { Name = NextName(document), Parent = parent, Region = region, LevelId = levelId };
        var view = Reference(callout);
        var scale = parent.Kind == ViewKind.Section ? SectionScale : PlanScale;
        return (callout, new CompositeCommand("Callout", new IUndoableCommand[]
        {
            new AddElementCommand(document, callout, "Callout"),
            new SetViewScaleCommand(document, view, scale),
            new SetViewDetailLevelCommand(document, view, DetailLevel.Fine)
        }));
    }
}

/// <summary>Sets how much detail a view is drawn in.</summary>
public sealed class SetViewDetailLevelCommand : IUndoableCommand
{
    private readonly BimDocument _document;
    private readonly ViewReference _view;
    private readonly DetailLevel? _old;
    private readonly DetailLevel? _new;

    public SetViewDetailLevelCommand(BimDocument document, ViewReference view, DetailLevel? level)
    {
        (_document, _view, _new) = (document, view, level);
        _old = document.ViewSettings.DetailLevelOf(view);
    }

    public string Name => "Detail Level";

    public void Redo() => _document.ViewSettings.SetDetailLevel(_view, _new);

    public void Undo() => _document.ViewSettings.SetDetailLevel(_view, _old);
}
