using BIMDesigner.Core;

namespace BIMDesigner.UI;

/// <summary>
/// The option boxes in the project's units - millimetres, or in a US project feet and inches -
/// and the tools starting on round figures in them: a 3'-0" opening and joists at 16" in a US
/// project, where 1000 mm and 400 mm would show as 3'-3 3/8" and 1'-3 3/4".
/// </summary>
public partial class MainWindow
{
    // The units the tools' figures were last started in.
    private UnitSystem _toolFigures = UnitSystem.Metric;

    /// <summary>A length as an option box shows it: 3000, or 10'-0" in a US project.</summary>
    private static string LengthText(double mm) => Units.Imperial ? Units.FeetAndInches(mm) : mm.ToString("0");

    /// <summary>A length typed in an option box: millimetres, or feet and inches in a US project - either, with its unit.</summary>
    private static bool TryLength(string text, out double mm) => Units.TryParseLength(text, out mm);

    /// <summary>
    /// The tools' figures started over when a project in other units opens: round feet and
    /// inches for a US one, round millimetres for one in metric. A project in the same units as
    /// the last keeps what was set.
    /// </summary>
    private void StartToolFigures()
    {
        if (_toolFigures == Units.System) return;
        _toolFigures = Units.System;

        var us = Units.Imperial;
        double Pick(double millimetres, double inches) => us ? inches * Units.MillimetresPerInch : millimetres;
        Plan.NewWallHeight = Pick(3000, 120);
        Plan.NewOpeningWidth = Pick(1000, 36);
        Plan.NewOpeningHeight = Pick(1000, 36);
        Plan.NewOpeningSill = Pick(900, 36);
        Plan.OffsetDistance = Pick(1000, 36);
        Plan.JointGap = Pick(25, 1);
        Plan.ArcRadius = Pick(1000, 36);
        Plan.NewStairWidth = Pick(900, 36);
        Plan.NewRampWidth = Pick(1200, 48);
        Plan.ActivePathWidth = Pick(1200, 48);
        Plan.ActiveJoistSpacing = Pick(400, 16);
        Plan.ActiveGroundElevation = Pick(-150, -6);
        Plan.ActivePadOffset = Pick(-150, -6);
        Plan.ActiveMassHeight = Pick(9000, 360);
        Plan.NewDuctOffset = Pick(2600, 102);
    }
}
