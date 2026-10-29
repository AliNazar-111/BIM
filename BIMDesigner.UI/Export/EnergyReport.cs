using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Energy;
using BIMDesigner.Core.Parameters;

namespace BIMDesigner.UI.Export;

/// <summary>
/// The home's energy and overheating report: a PDF to hand on - to the client, to the energy assessor, to
/// building control as the design stands - setting out what the Energy Rating window works out. Its SAP 10.3
/// and environmental impact ratings on their bands; its emission, primary energy and fabric energy efficiency
/// rates against the notional home's targets and each other thing Approved Document L 2026 asks; the home as
/// SAP takes it, its fabric, its thermal bridges and its solar arrays; what was taken that the model does not
/// say; its overheating by Approved Document O's simplified method, room by room and opening by opening; and,
/// as appendices, the worksheets box by box. It says plainly that it is a design-stage calculation, not an
/// accredited assessor's: the as-designed and as-built SAP calculations and their BREL reports are theirs.
/// </summary>
public static class EnergyReport
{
    private static string F(double value, string format) => value.ToString(format, CultureInfo.CurrentCulture);

    /// <summary>An enum's words as a sentence has them: "Point of use", not "Point Of Use".</summary>
    private static string Said<T>(T value) where T : struct, Enum
    {
        var words = EnumText.Humanise(value);
        return words.Length == 0 ? words : char.ToUpperInvariant(words[0]) + words[1..].ToLowerInvariant();
    }

    private static ReportCell Tick(bool met) => new(met ? "Met" : "Not met", met ? ReportTone.Met : ReportTone.Unmet);

    /// <summary>Why a project has no report: a US one, or no home to rate; null where it has one.</summary>
    public static string? WhyNot(BimDocument document) =>
        document.ProjectInformation.ClearanceStandard.IsUs() ? "SAP and the Future Homes Standard rate new homes in England; this is a US project."
        : FutureHomesStandard.Of(document) is null ? SapSurvey.Of(document).Notes.FirstOrDefault() ?? "There is no heated room enclosed by walls to rate yet."
        : null;

    /// <summary>The report laid out, or null where the project has none.</summary>
    public static ReportDocument? Build(BimDocument document, string version, DateTime on)
    {
        if (WhyNot(document) is not null || FutureHomesStandard.Of(document) is not { } assessment) return null;
        var overheating = Overheating.Of(document);
        var project = document.ProjectInformation;
        var home = assessment.Model.Home;
        var rating = assessment.Rating;
        var report = new ReportDocument($"{project.Name} - energy and overheating report, {on.ToString("d MMMM yyyy", CultureInfo.CurrentCulture)} - BIMDesigner {version}");

        // ---- what it is ----------------------------------------------------------------
        report.Title("Energy and Overheating Report", $"{project.Name}{(string.IsNullOrWhiteSpace(project.Address) ? string.Empty : $", {project.Address}")}");
        var postcode = string.IsNullOrWhiteSpace(project.Energy.Postcode) ? Overheating.District(project.Address) : project.Energy.Postcode;
        report.Facts(new[]
        {
            ("Project", string.IsNullOrWhiteSpace(project.Number) ? project.Name : $"{project.Number} - {project.Name}"),
            ("Client", string.IsNullOrWhiteSpace(project.Client) ? "-" : project.Client),
            ("Address", string.IsNullOrWhiteSpace(project.Address) ? "-" : project.Address),
            ("Postcode", string.IsNullOrWhiteSpace(postcode) ? "-" : postcode),
            ("The home", $"{(home.Flat ? "A flat" : "A house")} of {home.Storeys} storey{(home.Storeys == 1 ? string.Empty : "s")}, {F(home.FloorArea, "0.0")} m² of floor"),
            ("Date", on.ToString("d MMMM yyyy", CultureInfo.CurrentCulture)),
            ("Worked out by", $"BIMDesigner {version}, from the building model"),
            ("Method", "SAP 10.3 (12 June 2026), UK average climate; Approved Document L, Volume 1, 2026 edition (the Future Homes Standard, in force from 24 March 2027); Approved Document O, 2021 edition, simplified method")
        });
        report.Paragraph(
            "This is a design-stage calculation, worked from the building model as it stands. It is not an accredited SAP assessment: for building control, " +
            "an accredited energy assessor's as-designed and as-built SAP calculations, with their Building Regulations England Part L (BREL) reports, are needed. " +
            "This report sets out the same ratings, rates and checks, so the design can be checked before then and handed to them with what was assumed.", ReportTone.Muted);

        // ---- the verdict and the ratings ---------------------------------------------------
        report.Heading("Summary");
        var unmet = assessment.Checks.Count(check => !check.Met);
        report.Paragraph(assessment.Complies
                ? "The home meets the Future Homes Standard: each thing Approved Document L 2026 asks below is met."
                : $"The home does not meet the Future Homes Standard: {unmet} of the {assessment.Checks.Count} things Approved Document L 2026 asks below {(unmet == 1 ? "is" : "are")} not met.",
            assessment.Complies ? ReportTone.Met : ReportTone.Unmet, 10.5);
        if (overheating is not null)
            report.Paragraph(overheating.Complies
                    ? "It meets requirement O1 by Approved Document O's simplified method."
                    : $"It does not meet requirement O1 by Approved Document O's simplified method: {overheating.Checks.Count(check => !check.Met)} of its {overheating.Checks.Count} checks are not met.",
                overheating.Complies ? ReportTone.Met : ReportTone.Unmet, 10.5);

        report.Block(150, (dc, area) =>
        {
            Bands(dc, new Rect(area.X, area.Y, 220, area.Height), "Energy efficiency rating (SAP)", rating.SapRating, EnergyRatingWindow.CostColours);
            Bands(dc, new Rect(area.X + 245, area.Y, 220, area.Height), "Environmental impact rating", rating.EnvironmentalImpact, EnergyRatingWindow.ImpactColours);
        });
        report.Facts(new[]
        {
            ("Running costs", $"£{F(rating.Cost, "#,0")} a year at SAP's fuel prices, for the home's typical occupancy"),
            ("Space heating", $"{F(rating.SpaceHeating, "#,0")} kWh a year, {F(rating.SpaceHeating / Math.Max(rating.FloorArea, 1), "0")} kWh/m²"),
            ("Hot water", $"{F(rating.WaterHeating, "#,0")} kWh a year"),
            ("Solar electricity", home.Photovoltaics.Count == 0 ? "None" : $"{F(home.Photovoltaics.Sum(array => array.PeakPower), "0.00")} kWp, {F(rating.PhotovoltaicGenerated, "#,0")} kWh a year")
        });

        // ---- Part L ------------------------------------------------------------------------
        report.Heading("Approved Document L 2026 - the Future Homes Standard");
        report.Paragraph("The home's rates against the targets its notional twin sets (SAP 10.3 Appendix R): the same size and shape, built and serviced as the notional home specification has it.");
        report.Table(new[] { new ReportColumn("Rate", 3.2), new ReportColumn("The home", 1.4, Right: true), new ReportColumn("Target", 1.4, Right: true), new ReportColumn("", 1.1) }, new[]
        {
            Rate("Dwelling emission rate (DER), kgCO2e/m² a year", assessment.DwellingEmissionRate, assessment.TargetEmissionRate, "0.00"),
            Rate("Dwelling primary energy rate (DPER), kWh/m² a year", assessment.DwellingPrimaryEnergyRate, assessment.TargetPrimaryEnergyRate, "0.0"),
            Rate("Dwelling fabric energy efficiency (DFEE), kWh/m² a year", assessment.DwellingFabricEnergyEfficiency, assessment.TargetFabricEnergyEfficiency, "0.0")
        });
        report.Heading("What it asks, each in turn", 2);
        report.Table(new[] { new ReportColumn("Requirement", 2.6), new ReportColumn("", 0.8), new ReportColumn("How the home stands", 5) },
            assessment.Checks.Select(check => (IReadOnlyList<ReportCell>)new[] { (ReportCell)check.Requirement, Tick(check.Met), check.Detail }));

        // ---- the home ----------------------------------------------------------------------
        report.Heading("The home as SAP takes it");
        report.Facts(new[]
        {
            ("Floor area", $"{F(home.FloorArea, "0.0")} m² over {home.Storeys} storey{(home.Storeys == 1 ? string.Empty : "s")}; {F(home.GroundFloorArea, "0.0")} m² on the ground floor; living area {F(home.LivingArea, "0.0")} m²"),
            ("Volume", $"{F(home.Volume, "0")} m³"),
            ("Air permeability", $"{F(home.AirPermeability, "0.0#")} m³/h·m² at 50 Pa"),
            ("Ventilation", $"{Said(home.Ventilation)}{(home.SpecificFanPower > 0 ? $", specific fan power {F(home.SpecificFanPower, "0.00")} W/(l/s)" : string.Empty)}{(home.HeatRecovery > 0 ? $", heat recovery {F(home.HeatRecovery * 100, "0")}%" : string.Empty)}"),
            ("Heating", $"{home.Heating.Name}: {F(home.Heating.SpaceEfficiency, "0")}% seasonal efficiency heating, {F(home.Heating.WaterEfficiency, "0")}% hot water; {home.Heating.Controls.ToLowerInvariant()}"),
            ("Hot water", home.HotWater.CylinderVolume > 0 ? $"{Said(home.HotWater.Heater)}, a cylinder of {F(home.HotWater.CylinderVolume, "0")} litres" : Said(home.HotWater.Heater)),
            ("Showers and baths", $"{home.Showers.Count} shower{(home.Showers.Count == 1 ? string.Empty : "s")}, {home.Baths} bath{(home.Baths == 1 ? string.Empty : "s")}{(home.WasteWaterRecovery > 0 ? $"; waste water heat recovery {F(home.WasteWaterRecovery * 100, "0")}%" : string.Empty)}"),
            ("Lighting", home.LightingWatts > 0 ? $"{F(home.LightingLumens, "#,0")} lm from {F(home.LightingWatts, "#,0")} W, {F(home.LightingLumens / home.LightingWatts, "0")} lm/W" : "Not given - SAP's default taken"),
            ("Thermal mass", $"{F(home.ThermalMass, "0")} kJ/m²K"),
            ("Shelter and shading", $"{home.ShelteredSides} sheltered side{(home.ShelteredSides == 1 ? string.Empty : "s")}; overshading {Said(home.Overshading).ToLowerInvariant()}")
        });

        report.Heading("Its fabric", 2);
        report.Paragraph($"Each surface losing heat, its U-value as its layers or its maker give it. ΣAU {F(home.Elements.Sum(element => element.Area * element.U), "0.0")} W/K.");
        report.Table(new[] { new ReportColumn("Surface", 3.6), new ReportColumn("Kind", 1.4), new ReportColumn("Area, m²", 1.0, Right: true), new ReportColumn("U, W/m²K", 1.0, Right: true), new ReportColumn("Glazing", 2.4) },
            home.Elements.OrderBy(element => element.Kind).ThenBy(element => element.Name, StringComparer.CurrentCultureIgnoreCase).Select(element => (IReadOnlyList<ReportCell>)new ReportCell[]
            {
                element.Name, Said(element.Kind), F(element.Area, "0.00"), F(element.U, "0.00"),
                element.Glazed ? $"facing {EnergyRatingWindow.Compass(element.Orientation)}{(element.Kind == SapSurface.Window ? string.Empty : $" at {F(element.Tilt, "0")}°")}, g {F(element.SolarTransmittance, "0.00")}, frame {F(element.FrameFactor, "0.0")}" : string.Empty
            }));

        report.Heading("Its thermal bridges", 2);
        if (home.Junctions.Count == 0)
            report.Paragraph($"Taken as a y-value of {F(home.YValue, "0.00")} W/m²K over the area facing outside.");
        else
        {
            report.Paragraph($"Each junction measured round the rooms, its ψ as set or as the setting gives it: {F(home.Junctions.Sum(junction => junction.Length * junction.Psi), "0.0")} W/K in all. A junction's own ψ is to come from its calculated detail.");
            report.Table(new[] { new ReportColumn("Junction", 4.4), new ReportColumn("Length, m", 1.1, Right: true), new ReportColumn("ψ, W/m·K", 1.1, Right: true), new ReportColumn("W/K", 1.0, Right: true) },
                home.Junctions.Select(junction => (IReadOnlyList<ReportCell>)new ReportCell[] { $"{junction.Code} {junction.What}", F(junction.Length, "0.00"), F(junction.Psi, "0.000"), F(junction.Length * junction.Psi, "0.00") }));
        }

        if (home.Photovoltaics.Count > 0)
        {
            report.Heading("Its solar arrays", 2);
            report.Table(new[] { new ReportColumn("Array", 3), new ReportColumn("kWp", 1, Right: true), new ReportColumn("Facing", 1.2), new ReportColumn("Tilt", 0.8, Right: true), new ReportColumn("Overshading", 1.2, Right: true) },
                home.Photovoltaics.Select(array => (IReadOnlyList<ReportCell>)new ReportCell[] { array.Name, F(array.PeakPower, "0.00"), EnergyRatingWindow.Compass(array.Orientation), $"{F(array.Tilt, "0")}°", F(array.Shading, "0.00") }));
        }

        if (assessment.Model.Notes.Count > 0)
        {
            report.Heading("What was taken that the model does not say", 2);
            report.Paragraph("To be confirmed with the products chosen, or changed in the Energy Rating window and the types' properties:", ReportTone.Muted);
            foreach (var note in assessment.Model.Notes) report.Paragraph($"•  {note}");
        }

        // ---- Part O ------------------------------------------------------------------------
        if (overheating is not null)
        {
            report.Heading("Overheating - Approved Document O, simplified method");
            report.Facts(new[]
            {
                ("Location", $"The home is {overheating.Location}"),
                ("Cross-ventilation", overheating.CrossVentilation),
                ("Floor and glazing", $"{F(overheating.FloorArea, "0.0")} m² of floor; {F(overheating.Glazing, "0.00")} m² of glazing, {F(overheating.Glazing / Math.Max(overheating.FloorArea, 1e-9) * 100, "0.#")}%, most of it facing {overheating.Facade}"),
                ("Free area", $"{F(overheating.TotalFree, "0.00")} m² of equivalent area against {F(overheating.TotalFreeRequired, "0.00")} m²")
            });
            report.Table(new[] { new ReportColumn("Requirement", 2.6), new ReportColumn("", 0.8), new ReportColumn("How the home stands", 5) },
                overheating.Checks.Select(check => (IReadOnlyList<ReportCell>)new[] { (ReportCell)check.Requirement, Tick(check.Met), check.Detail }));
            report.Heading("Its rooms", 2);
            report.Table(new[] { new ReportColumn("Room", 2.4), new ReportColumn("Floor, m²", 1, Right: true), new ReportColumn("Glazing, m²", 1, Right: true), new ReportColumn("Share", 0.8, Right: true), new ReportColumn("Facing", 1), new ReportColumn("At night, m²", 1.1, Right: true) },
                overheating.Rooms.OrderByDescending(room => room.Share).Select(room => (IReadOnlyList<ReportCell>)new ReportCell[]
                {
                    room.Name, F(room.FloorArea, "0.0"), F(room.Glazing, "0.00"), $"{F(room.Share * 100, "0.#")}%", room.Facade, room.Bedroom ? F(room.NightFree, "0.00") : "-"
                }));
            report.Heading("Its openings - their equivalent areas by Appendix D", 2);
            report.Table(new[] { new ReportColumn("Opening", 2.2), new ReportColumn("Room", 1.6), new ReportColumn("Glass, m²", 0.9, Right: true), new ReportColumn("By day, m²", 0.9, Right: true), new ReportColumn("At night, m²", 0.9, Right: true), new ReportColumn("How it opens", 3) },
                overheating.Openings.Select(opening => (IReadOnlyList<ReportCell>)new ReportCell[]
                {
                    $"{opening.Name}{(opening.Bearing is { } way ? $", facing {EnergyRatingWindow.Compass(way)}" : string.Empty)}", opening.Room.Name, F(opening.Glazing, "0.00"), F(opening.Equivalent, "0.00"), F(opening.NightEquivalent, "0.00"), opening.Note
                }));
        }

        // ---- the worksheets -----------------------------------------------------------------
        var appendices = new[]
        {
            ("Appendix A - the home's worksheet, for its emission and primary energy rates", assessment.Dwelling),
            ("Appendix B - the notional home's worksheet, for the targets", assessment.Target),
            ("Appendix C - the home's worksheet for its ratings, at typical occupancy", assessment.Rating),
            ("Appendix D - the home's fabric energy efficiency", assessment.DwellingFabric),
            ("Appendix E - the notional home's fabric energy efficiency", assessment.TargetFabric)
        };
        foreach (var (title, result) in appendices)
        {
            report.NewPage();
            report.Heading(title);
            report.Table(new[] { new ReportColumn("Box", 0.7), new ReportColumn("What", 5), new ReportColumn("Value", 1.3, Right: true), new ReportColumn("Unit", 1.2) },
                result.Lines.Select(line => (IReadOnlyList<ReportCell>)new ReportCell[]
                {
                    $"({line.Box})", line.What, line.Value.ToString(Math.Abs(line.Value) >= 100 ? "#,0" : "0.###", CultureInfo.CurrentCulture), line.Unit
                }));
        }

        return report;
    }

    /// <summary>A rate of the home against its target: the lower is the better, met at or under it.</summary>
    private static IReadOnlyList<ReportCell> Rate(string what, double home, double target, string format) =>
        new[] { (ReportCell)what, F(home, format), F(target, format), Tick(home <= target + 1e-9) };

    /// <summary>A rating on its A to G bands, as an energy performance certificate draws it: each band longer than the one above, the rating marked against its own.</summary>
    private static void Bands(DrawingContext dc, Rect area, string title, int rating, Color[] colours)
    {
        var bands = new[] { ('A', "92+"), ('B', "81-91"), ('C', "69-80"), ('D', "55-68"), ('E', "39-54"), ('F', "21-38"), ('G', "1-20") };
        var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        FormattedText Text(string text, double size, Brush brush) => new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, size, brush, 1);
        dc.DrawText(Text(title, 9.5, Brushes.Black), new Point(area.X, area.Y));
        var band = SapCalculation.Band(rating);
        var height = (area.Height - 22) / bands.Length;
        for (var i = 0; i < bands.Length; i++)
        {
            var (letter, range) = bands[i];
            var y = area.Y + 18 + i * height;
            var width = 80 + i * 14;
            var fill = new SolidColorBrush(colours[i]);
            var ink = i is 2 or 3 or 4 ? Brushes.Black : Brushes.White;
            dc.DrawRectangle(fill, null, new Rect(area.X, y, width, height - 2));
            var label = Text($"{range}   {letter}", 8.5, ink);
            dc.DrawText(label, new Point(area.X + width - label.Width - 5, y + (height - 2 - label.Height) / 2));
            if (letter != band) continue;
            var mark = new Rect(area.Right - 46, y, 46, height - 2);
            dc.DrawRectangle(fill, null, mark);
            var said = Text($"{rating} {letter}", 10, ink);
            dc.DrawText(said, new Point(mark.X + (mark.Width - said.Width) / 2, y + (height - 2 - said.Height) / 2));
        }
    }

    /// <summary>The report written to a PDF file.</summary>
    public static void ToPdf(ReportDocument report, string path) => PdfWriter.Write(path, report.Pages());

    /// <summary>A suggested file name: the project's, as an energy report.</summary>
    public static string SuggestedFileName(BimDocument document)
    {
        var name = new string(document.ProjectInformation.Name.Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray()).Trim();
        return $"{(name.Length == 0 ? "Home" : name)} - Energy and Overheating Report.pdf";
    }
}
