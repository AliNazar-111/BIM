using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Parameters;

namespace BIMDesigner.Core.Annotation;

/// <summary>What closes a dimension line at each witness line.</summary>
public enum DimensionTick
{
    /// <summary>A short stroke at 45° across the line: the architectural mark.</summary>
    Tick,

    /// <summary>A filled arrowhead, closed - the mark ISO 129 and most engineering drawings use.</summary>
    Arrow,

    /// <summary>A filled dot.</summary>
    Dot
}

/// <summary>How a dimension's measurement is written.</summary>
public enum DimensionUnits
{
    /// <summary>Millimetres: 2400. What UK building drawings use, without the unit.</summary>
    Millimetres,

    /// <summary>Metres: 2.400.</summary>
    Metres,

    /// <summary>Millimetres up to a metre, metres beyond: 600 mm, 2.40 m.</summary>
    Automatic
}

/// <summary>
/// A dimension style (specification section 6.3; Revit's dimension type): the mark at each end,
/// how far the witness lines stand off what is measured and run past the dimension line, the
/// size of the text and how far it sits off the line, and how the measurement is written. Sizes
/// are on the paper, so a dimension prints the same whatever the scale of the drawing.
/// </summary>
public sealed class DimensionType : ElementType
{
    public DimensionType(string name) : base(name)
    {
    }

    public override BuiltInCategory Category => BuiltInCategory.Dimensions;

    public DimensionTick Tick { get; set; } = DimensionTick.Tick;

    /// <summary>The text's height on paper, mm: 2.5 by ISO 3098 for dimensions.</summary>
    public double TextSize { get; set; } = 2.5;

    /// <summary>How far the text sits clear of the dimension line, on paper, mm.</summary>
    public double TextOffset { get; set; } = 1;

    /// <summary>The text written along the line, read from below or from the right - or always level.</summary>
    public bool TextAlongLine { get; set; } = true;

    /// <summary>The gap between a witness line and what it measures, on paper, mm.</summary>
    public double WitnessGap { get; set; } = 2;

    /// <summary>How far a witness line runs past the dimension line, on paper, mm.</summary>
    public double WitnessExtension { get; set; } = 2;

    /// <summary>How far the dimension line runs past its witness lines, on paper, mm - for ticks and dots; none behind arrows.</summary>
    public double LineExtension { get; set; } = 2;

    public DimensionUnits Units { get; set; } = DimensionUnits.Millimetres;

    /// <summary>How many places after the point.</summary>
    public int Decimals { get; set; }

    /// <summary>Whether the unit is written after the number: "mm", "m".</summary>
    public bool ShowUnits { get; set; }

    /// <summary>How many places after the point an angle is written to.</summary>
    public int AngleDecimals { get; set; }

    /// <summary>A length written as this style writes it.</summary>
    public string Format(double millimetres)
    {
        var places = Math.Clamp(Decimals, 0, 4);
        var number = Units switch
        {
            DimensionUnits.Metres => (millimetres / 1000).ToString("F" + Math.Max(places, 0)) + (ShowUnits ? " m" : string.Empty),
            DimensionUnits.Automatic => Core.Units.FormatLength(millimetres),
            _ => millimetres.ToString("F" + places) + (ShowUnits ? " mm" : string.Empty)
        };

        return number;
    }

    /// <summary>An angle, degrees, written as this style writes it.</summary>
    public string FormatAngle(double degrees) => degrees.ToString("F" + Math.Clamp(AngleDecimals, 0, 4)) + "°";

    public DimensionType Duplicate(string name) => WithCommonOf(new DimensionType(name)
    {
        Tick = Tick, TextSize = TextSize, TextOffset = TextOffset, TextAlongLine = TextAlongLine, WitnessGap = WitnessGap,
        WitnessExtension = WitnessExtension, LineExtension = LineExtension, Units = Units, Decimals = Decimals,
        ShowUnits = ShowUnits, AngleDecimals = AngleDecimals
    });

    public override IEnumerable<ParameterValue> GetTypeParameters(BimDocument? document = null)
    {
        foreach (var parameter in GetCommonTypeParameters()) yield return parameter;

        yield return ParameterValue.BindChoice(DimensionTypeParameters.Tick, () => EnumText.Humanise(Tick),
            v => { if (EnumText.TryParse<DimensionTick>(v, out var tick)) Tick = tick; }, EnumText.Choices<DimensionTick>());
        yield return ParameterValue.BindValidated(DimensionTypeParameters.TextSize, () => TextSize, v => Paper(v, 0.5, 20, x => TextSize = x));
        yield return ParameterValue.BindValidated(DimensionTypeParameters.TextOffset, () => TextOffset, v => Paper(v, 0, 10, x => TextOffset = x));
        yield return ParameterValue.Bind(DimensionTypeParameters.TextAlongLine, () => TextAlongLine, v => TextAlongLine = v);
        yield return ParameterValue.BindValidated(DimensionTypeParameters.WitnessGap, () => WitnessGap, v => Paper(v, 0, 20, x => WitnessGap = x));
        yield return ParameterValue.BindValidated(DimensionTypeParameters.WitnessExtension, () => WitnessExtension, v => Paper(v, 0, 20, x => WitnessExtension = x));
        yield return ParameterValue.BindValidated(DimensionTypeParameters.LineExtension, () => LineExtension, v => Paper(v, 0, 20, x => LineExtension = x));
        yield return ParameterValue.BindChoice(DimensionTypeParameters.Units, () => EnumText.Humanise(Units),
            v => { if (EnumText.TryParse<DimensionUnits>(v, out var units)) Units = units; }, EnumText.Choices<DimensionUnits>());
        yield return ParameterValue.BindValidated(DimensionTypeParameters.Decimals, () => Decimals, v =>
        {
            if (v is < 0 or > 4) return false;
            Decimals = v;
            return true;
        });
        yield return ParameterValue.Bind(DimensionTypeParameters.ShowUnits, () => ShowUnits, v => ShowUnits = v);
        yield return ParameterValue.BindValidated(DimensionTypeParameters.AngleDecimals, () => AngleDecimals, v =>
        {
            if (v is < 0 or > 4) return false;
            AngleDecimals = v;
            return true;
        });
    }

    private static bool Paper(double value, double least, double most, Action<double> set)
    {
        if (!double.IsFinite(value) || value < least || value > most) return false;
        set(value);
        return true;
    }
}

public static class DimensionTypeParameters
{
    public static readonly ParameterDefinition Tick = new("Tick Mark", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.Graphics);
    public static readonly ParameterDefinition TextSize = new("Text Size (paper mm)", ParameterDataType.Number, ParameterBinding.Type, ParameterGroup.Graphics);
    public static readonly ParameterDefinition TextOffset = new("Text Offset (paper mm)", ParameterDataType.Number, ParameterBinding.Type, ParameterGroup.Graphics);
    public static readonly ParameterDefinition TextAlongLine = new("Text Along Line", ParameterDataType.YesNo, ParameterBinding.Type, ParameterGroup.Graphics);
    public static readonly ParameterDefinition WitnessGap = new("Witness Line Gap to Element (paper mm)", ParameterDataType.Number, ParameterBinding.Type, ParameterGroup.Graphics);
    public static readonly ParameterDefinition WitnessExtension = new("Witness Line Extension (paper mm)", ParameterDataType.Number, ParameterBinding.Type, ParameterGroup.Graphics);
    public static readonly ParameterDefinition LineExtension = new("Dimension Line Extension (paper mm)", ParameterDataType.Number, ParameterBinding.Type, ParameterGroup.Graphics);
    public static readonly ParameterDefinition Units = new("Units", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.Other);
    public static readonly ParameterDefinition Decimals = new("Decimal Places", ParameterDataType.Integer, ParameterBinding.Type, ParameterGroup.Other);
    public static readonly ParameterDefinition ShowUnits = new("Show Unit Symbol", ParameterDataType.YesNo, ParameterBinding.Type, ParameterGroup.Other);
    public static readonly ParameterDefinition AngleDecimals = new("Angle Decimal Places", ParameterDataType.Integer, ParameterBinding.Type, ParameterGroup.Other);
}

public static class DimensionTypes
{
    /// <summary>The style a dimension is drawn in: its own type, or the project's first.</summary>
    public static DimensionType Of(BimDocument document, Element dimension) =>
        document.FindType<DimensionType>(dimension.TypeId) ?? document.TypesOf<DimensionType>().FirstOrDefault() ?? new DimensionType("Linear - 2.5 mm ticks");

    /// <summary>The styles a new project starts with: millimetres with ticks, as UK drawings are dimensioned; with arrows; in metres.</summary>
    public static IEnumerable<DimensionType> Defaults() => new[]
    {
        new DimensionType("Linear - 2.5 mm ticks"),
        new DimensionType("Linear - 2.5 mm arrows") { Tick = DimensionTick.Arrow, LineExtension = 0 },
        new DimensionType("Linear - metres") { Units = DimensionUnits.Metres, Decimals = 3 },
        new DimensionType("Linear - automatic (m above 1 m)") { Units = DimensionUnits.Automatic, TextAlongLine = false }
    };
}

/// <summary>What a radial dimension measures of an arc.</summary>
public enum RadialKind
{
    Radius,
    Diameter,

    /// <summary>The length along the arc, between its ends.</summary>
    ArcLength
}

/// <summary>
/// An angular dimension: the angle between two walls or gridlines, the arc drawn through the
/// point it was placed at, in the quarter the point is in. Measured from the model each time.
/// </summary>
public sealed class AngularDimension : Element
{
    public override BuiltInCategory Category => BuiltInCategory.Dimensions;

    public DimensionReference First { get; set; } = DimensionReference.ToPoint(default);

    public DimensionReference Second { get; set; } = DimensionReference.ToPoint(default);

    /// <summary>A point the arc passes through: which of the four angles between the lines it shows, and how far out.</summary>
    public Point2D Through { get; set; }

    public string Override { get; set; } = string.Empty;

    /// <summary>
    /// Where the two lines cross, the way each goes from there toward the arc, the angle between
    /// them in degrees, and the arc's radius; null where the lines are parallel or not lines.
    /// </summary>
    public (Point2D Centre, Vector2D From, Vector2D To, double Degrees, double Radius)? Geometry(BimDocument document)
    {
        if (First.Line(document) is not { } a || Second.Line(document) is not { } b) return null;
        var cross = a.Direction.Cross(b.Direction);
        if (Math.Abs(cross) < 1e-6) return null;

        // Where the lines cross.
        var t = (b.Point - a.Point).Cross(b.Direction) / cross;
        var centre = a.Point + a.Direction * t;
        var toward = Through - centre;
        var from = toward.Dot(a.Direction) >= 0 ? a.Direction : -a.Direction;
        var to = toward.Dot(b.Direction) >= 0 ? b.Direction : -b.Direction;
        var degrees = Math.Acos(Math.Clamp(from.Dot(to), -1, 1)) * 180 / Math.PI;
        return (centre, from, to, degrees, Math.Max(toward.Length, 1));
    }

    public double Measure(BimDocument document) => Geometry(document)?.Degrees ?? 0;

    public string DisplayText(BimDocument document) =>
        string.IsNullOrWhiteSpace(Override) ? DimensionTypes.Of(document, this).FormatAngle(Measure(document)) : Override;

    public override IEnumerable<ParameterValue> GetInstanceParameters(BimDocument document)
    {
        yield return ParameterValue.ReadOnly(DimensionParameters.Angle, () => Measure(document));
        yield return ParameterValue.Bind(DimensionParameters.Override, () => Override, v => Override = v);
        foreach (var parameter in GetCommonParameters(document)) yield return parameter;
    }

    public override string ToString() => "Angular Dimension";
}

/// <summary>
/// A radius, diameter or arc-length dimension on a curved wall: to its centreline, or the face
/// or core face it was picked by; placed where it was clicked. Measured from the model each time.
/// </summary>
public sealed class RadialDimension : Element
{
    public override BuiltInCategory Category => BuiltInCategory.Dimensions;

    public RadialKind Kind { get; set; } = RadialKind.Radius;

    /// <summary>The curved wall - and which line of it: its centreline, a face, a core face.</summary>
    public DimensionReference Target { get; set; } = DimensionReference.ToPoint(default);

    /// <summary>Where it was placed: the side of the arc the radius is drawn to, how far out an arc length runs.</summary>
    public Point2D Through { get; set; }

    public string Override { get; set; } = string.Empty;

    /// <summary>The arc measured: its centre, radius, where it starts and how far it turns (radians); null where it is not an arc.</summary>
    public (Point2D Centre, double Radius, double StartAngle, double Sweep)? Arc(BimDocument document)
    {
        if (document.FindElement(Target.ElementId) is not Wall wall || !wall.IsCurved || !wall.LocationCurve.IsArc || wall.LocationCurve.IsElliptical || wall.LocationCurve.IsSpline) return null;
        var curve = wall.LocationCurve;

        // The line picked: the centreline, or a face of it, concentric with it.
        var radius = curve.Radius;
        if (document.GetWallType(wall) is { } type && Target.IsWallLine)
        {
            var span = Target.WallLineSpan(document);
            if (span is { } line) radius = line.From.DistanceTo(curve.Centre);
        }

        return (curve.Centre, radius, curve.StartAngle, curve.Sweep);
    }

    public double Measure(BimDocument document) => Arc(document) is not { } arc ? 0 : Kind switch
    {
        RadialKind.Diameter => arc.Radius * 2,
        RadialKind.ArcLength => arc.Radius * Math.Abs(arc.Sweep),
        _ => arc.Radius
    };

    public string DisplayText(BimDocument document)
    {
        if (!string.IsNullOrWhiteSpace(Override)) return Override;
        var value = DimensionTypes.Of(document, this).Format(Measure(document));
        return Kind switch { RadialKind.Radius => "R " + value, RadialKind.Diameter => "Ø " + value, _ => "⌒ " + value };
    }

    public override IEnumerable<ParameterValue> GetInstanceParameters(BimDocument document)
    {
        yield return ParameterValue.ReadOnly(DimensionParameters.Value, () => Measure(document));
        yield return ParameterValue.BindChoice(DimensionParameters.RadialKind, () => EnumText.Humanise(Kind),
            v => { if (EnumText.TryParse<RadialKind>(v, out var kind)) Kind = kind; }, EnumText.Choices<RadialKind>());
        yield return ParameterValue.Bind(DimensionParameters.Override, () => Override, v => Override = v);
        foreach (var parameter in GetCommonParameters(document)) yield return parameter;
    }

    public override string ToString() => Kind switch { RadialKind.Diameter => "Diameter Dimension", RadialKind.ArcLength => "Arc Length Dimension", _ => "Radial Dimension" };
}
