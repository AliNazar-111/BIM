namespace BIMDesigner.Core.Sheets;

/// <summary>ISO 216 paper sizes (specification section 6.5).</summary>
public enum PaperSize
{
    A0,
    A1,
    A2,
    A3,
    A4
}

public enum PaperOrientation
{
    Landscape,
    Portrait
}

/// <summary>
/// Paper dimensions, in millimetres - the same unit as the rest of the model.
///
/// A sheet is the one place in the application where millimetres mean paper rather than
/// building, which is exactly what a drawing scale converts between.
/// </summary>
public static class Paper
{
    /// <summary>Short and long edge of each size, in millimetres.</summary>
    public static (double Short, double Long) Dimensions(PaperSize size) => size switch
    {
        PaperSize.A0 => (841, 1189),
        PaperSize.A1 => (594, 841),
        PaperSize.A2 => (420, 594),
        PaperSize.A3 => (297, 420),
        _ => (210, 297)
    };

    public static double Width(PaperSize size, PaperOrientation orientation)
    {
        var (shortEdge, longEdge) = Dimensions(size);
        return orientation == PaperOrientation.Landscape ? longEdge : shortEdge;
    }

    public static double Height(PaperSize size, PaperOrientation orientation)
    {
        var (shortEdge, longEdge) = Dimensions(size);
        return orientation == PaperOrientation.Landscape ? shortEdge : longEdge;
    }
}

/// <summary>
/// A drawing scale, held as its denominator: 50 means 1:50, one millimetre of paper to fifty
/// of building.
///
/// Scale is stored on the viewport rather than on the view, because the same plan is
/// legitimately shown at 1:100 on a general arrangement and at 1:20 on a detail sheet. It is
/// a property of how a drawing is presented, not of what it contains.
/// </summary>
public readonly record struct ViewScale(double Denominator)
{
    /// <summary>The scales that appear on architectural drawings, largest drawing first.</summary>
    public static readonly double[] Common = { 5, 10, 20, 25, 50, 100, 200, 500, 1000 };

    public static readonly ViewScale OneToOneHundred = new(100);

    public bool IsValid => Denominator > 0;

    /// <summary>Millimetres of building to millimetres of paper.</summary>
    public double ToPaper(double modelMillimetres) =>
        IsValid ? modelMillimetres / Denominator : modelMillimetres;

    /// <summary>Millimetres of paper back to millimetres of building.</summary>
    public double ToModel(double paperMillimetres) =>
        IsValid ? paperMillimetres * Denominator : paperMillimetres;

    /// <summary>
    /// The largest conventional scale that fits <paramref name="modelSize"/> into
    /// <paramref name="paperSize"/>. Drawings are read with a scale rule, so fitting a view
    /// means choosing from the scales on one rather than computing an arbitrary ratio - a
    /// drawing at 1:87 cannot be measured.
    /// </summary>
    public static ViewScale FittingInto(double modelSize, double paperSize)
    {
        if (modelSize <= 0 || paperSize <= 0) return OneToOneHundred;

        foreach (var denominator in Common)
            if (modelSize / denominator <= paperSize) return new ViewScale(denominator);

        return new ViewScale(Common[^1]);
    }

    /// <summary>
    /// The largest conventional scale at which a drawing this size fits the space available
    /// both ways.
    ///
    /// Fitting the width alone is not enough: a tall, narrow building would be scaled to the
    /// width of the sheet and then run off the top and bottom of it.
    /// </summary>
    public static ViewScale FittingInto(
        double modelWidth, double modelHeight, double paperWidth, double paperHeight)
    {
        var forWidth = FittingInto(modelWidth, paperWidth);
        var forHeight = FittingInto(modelHeight, paperHeight);

        // The larger denominator is the smaller drawing, and it is the one that fits both.
        return forWidth.Denominator >= forHeight.Denominator ? forWidth : forHeight;
    }

    public override string ToString() => IsValid ? $"1 : {Denominator:0.###}" : "1 : 1";
}
