using System.Globalization;
using System.Windows;
using System.Windows.Media;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Energy;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Handover;
using BIMDesigner.Core.Mep;
using BIMDesigner.Core.Sheets;
using BIMDesigner.Core.Views;
using BIMDesigner.UI.Rendering;
using Point = System.Windows.Point;

namespace BIMDesigner.UI.Export;

/// <summary>
/// The home's handover documents, written from its model as PDFs to give to the owner. The operation and
/// maintenance manual Approved Document L, Volume 1 (2026 edition), Section 9 asks for under regulations
/// 40, 40A and 40C - specific to the home and the models put in it: each fixed building service, what it
/// is and is for, where it is on a floor plan, how to work it and how to keep it up (9.2); the Home User
/// Guide within it, on heating and hot water, any electricity made on site, ventilation and staying cool
/// in hot weather (9.3, and Approved Document O, 4.1 to 4.3); a heat pump's design figures, to be fixed to
/// it (9.4); and what else to keep with it - the makers' manuals, the commissioning sheets, the BREL report
/// and the energy performance certificate's recommendations (9.2(b), (c), 9.5, 9.6). In the US, the
/// maintenance instructions the IECC asks to be furnished for equipment that needs preventive maintenance,
/// its regular maintenance on a label (R303.3). Then the asset register, the maintenance plan and its
/// calendar for the first year, the replacements the assets' lives bring, the warranties and the work
/// recorded as done.
/// </summary>
public static class HandoverReport
{
    private static readonly Typeface Regular = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface Bold = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    private static string F(double value, string format) => value.ToString(format, CultureInfo.CurrentCulture);

    private static string Day(DateOnly day) => day.ToString("d MMMM yyyy", CultureInfo.CurrentCulture);

    private static string Short(DateOnly? day) => day is { } on ? on.ToString("MMM yyyy", CultureInfo.CurrentCulture) : "-";

    private static string Capital(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    /// <summary>A name begun in lower case, as it reads in a sentence - "test the RCDs", not "test the rcds".</summary>
    private static string Lower(string text) => text.Length == 0 || text.Length > 1 && char.IsUpper(text[1]) ? text : char.ToLowerInvariant(text[0]) + text[1..];

    /// <summary>Why a project has no handover documents yet; null where it has.</summary>
    public static string? WhyNot(BimDocument document) =>
        Assets.Of(document).Count == 0 ? "There is nothing in the model to keep up yet: no boiler, alarm, appliance, sanitaryware, door or window." : null;

    /// <summary>The groups the services are set out in, each with what it is for and how it is worked.</summary>
    private static readonly (string Heading, AssetKind[] Kinds, string For, string Operate)[] Groups =
    {
        ("Heating and hot water",
            new[] { AssetKind.GasBoiler, AssetKind.OilBoiler, AssetKind.ElectricBoiler, AssetKind.HeatPump, AssetKind.HotWaterCylinder, AssetKind.Radiator, AssetKind.TowelRail, AssetKind.UnderfloorHeating, AssetKind.Thermostat },
            "They keep the home warm and give it hot water.",
            "The heating comes on and goes off by its programmer or timer and holds each zone at its thermostat's setting; radiators' thermostatic valves hold each room a little lower where it is set so. See Heating and hot water in the Home User Guide."),
        ("Ventilation",
            new[] { AssetKind.HeatRecoveryUnit, AssetKind.ExtractUnit, AssetKind.ExtractFan, AssetKind.CookerHood, AssetKind.BackgroundVentilator },
            "They change the home's air, taking out the damp and the stale and bringing in fresh.",
            "See Ventilation in the Home User Guide: what runs all the time, what to boost, and what to keep open."),
        ("Fire and gas safety",
            new[] { AssetKind.SmokeAlarm, AssetKind.HeatAlarm, AssetKind.CarbonMonoxideAlarm, AssetKind.DuctSmokeDetector, AssetKind.SprinklerValve, AssetKind.Sprinkler, AssetKind.FireDoor },
            "They warn of fire and of carbon monoxide, and hold a fire back while you get out.",
            "An alarm sounding means leave the home at once and call the fire service from outside. A chirp now and then means its battery is low or it has reached the end of its life. Never cover an alarm or a sprinkler, and never wedge a fire door open."),
        ("Electricity",
            new[] { AssetKind.ConsumerUnit },
            "It takes the supply to each circuit, and cuts a circuit off when it is overloaded or a fault lets current leak to earth.",
            "To turn the power off, switch off the main switch. A circuit that has switched itself off is reset by its switch once whatever was faulty is unplugged; one that will not reset wants an electrician."),
        ("Gas and water",
            new[] { AssetKind.GasMeter, AssetKind.StopValve, AssetKind.GasFire },
            "Where the gas and the water come in, and the gas fire.",
            "The gas is turned off by the lever by the meter, a quarter turn so it is across the pipe; the water by the stop valve, turned clockwise."),
        ("Kitchen and laundry",
            new[] { AssetKind.Cooker, AssetKind.Fridge, AssetKind.Dishwasher, AssetKind.WashingMachine, AssetKind.TumbleDryer },
            "The appliances fitted with the home.",
            "Each as its maker's instructions say: keep them with this manual."),
        ("Bathrooms and sanitaryware",
            new[] { AssetKind.Toilet, AssetKind.Basin, AssetKind.Sink, AssetKind.Bath, AssetKind.Shower },
            "The WCs, basins, sinks, baths and showers.",
            "A WC whose cistern keeps running is turned off at the isolating valve on its supply until its valve is renewed."),
        ("Drainage",
            new[] { AssetKind.DrainAccess, AssetKind.Soakaway, AssetKind.AirAdmittanceValve },
            "Where the drains can be got at and cleared, and where the rainwater goes.",
            "Put nothing down a drain but what it is for: no fat, wipes or nappies."),
        ("Doors, windows and roof",
            new[] { AssetKind.Door, AssetKind.Window, AssetKind.RoofWindow, AssetKind.Gutter, AssetKind.Chimney },
            "The doors and windows, the roof windows, the gutters and the chimneys.",
            "Windows open on their handles; restrictors are released by their own catch. Keep the background ventilators in the frames open."),
        ("Other equipment",
            new[] { AssetKind.Equipment },
            "Equipment of the home's own.",
            "As its maker's instructions say.")
    };

    /// <summary>The operation and maintenance manual, with the Home User Guide in it; null where the project has nothing to keep up.</summary>
    public static ReportDocument? Manual(BimDocument document, string version, DateTime on) => Build(document, version, on, guideOnly: false);

    /// <summary>The Home User Guide on its own; null where the project has nothing to keep up.</summary>
    public static ReportDocument? Guide(BimDocument document, string version, DateTime on) => Build(document, version, on, guideOnly: true);

    public static void ToPdf(ReportDocument report, string path) => PdfWriter.Write(path, report.Pages());

    /// <summary>A suggested file name: the project's, as its manual or its guide.</summary>
    public static string SuggestedFileName(BimDocument document, bool guide)
    {
        var name = new string(document.ProjectInformation.Name.Where(c => !System.IO.Path.GetInvalidFileNameChars().Contains(c)).ToArray()).Trim();
        return $"{(name.Length == 0 ? "Home" : name)} - {(guide ? "Home User Guide" : "Operation and Maintenance Manual")}.pdf";
    }

    private static ReportDocument? Build(BimDocument document, string version, DateTime on, bool guideOnly)
    {
        if (WhyNot(document) is not null) return null;
        using var cache = ModelCache.Begin(document);
        var project = document.ProjectInformation;
        var us = project.ClearanceStandard.IsUs();
        var today = DateOnly.FromDateTime(on);
        var assets = Assets.Of(document);
        var title = guideOnly ? "Home User Guide" : "Operation and Maintenance Manual";
        var report = new ReportDocument($"{project.Name} - {title.ToLowerInvariant()}, {on.ToString("d MMMM yyyy", CultureInfo.CurrentCulture)} - BIMDesigner {version}");

        report.Title(title, $"{project.Name}{(string.IsNullOrWhiteSpace(project.Address) ? string.Empty : $", {project.Address}")}");
        report.Facts(new[]
        {
            ("Project", string.IsNullOrWhiteSpace(project.Number) ? project.Name : $"{project.Number} - {project.Name}"),
            ("Address", string.IsNullOrWhiteSpace(project.Address) ? "-" : project.Address),
            ("Handed over", document.Handover.HandedOver is { } handed ? Day(handed) : "-"),
            ("Date", Day(today)),
            ("Written by", $"BIMDesigner {version}, from the building model"),
            ("Written to", us
                ? "The International Energy Conservation Code, R303.3: maintenance instructions for equipment that needs preventive maintenance"
                : "Approved Document L, Volume 1 (2026 edition), Section 9, for regulations 40, 40A and 40C; Approved Document O (2021 edition), Section 4")
        });
        report.Paragraph(guideOnly
                ? "Plain advice on running this home in a healthy and energy efficient way: its heating and hot water, its ventilation" + (us ? "." : ", any electricity it makes and keeping it cool in hot weather.") +
                  " Keep it with the home's operation and maintenance manual, and pass both on when the home changes hands."
                : "This manual is written for this home, from its model: what each of its services is and is for, where it is, how to work it and how to keep it up, with the makers and models put in. " +
                  "The makers' own instructions for each appliance come first where they say otherwise: keep them with this manual, and pass both on when the home changes hands.",
            ReportTone.Muted);

        HomeUserGuide(report, document, assets, us, today);
        if (guideOnly)
        {
            WhatYouDo(report, document, assets, us, today);
            return report;
        }

        Services(report, document, assets, us);
        Plans(report, document, assets);
        Register(report, document, assets);
        Plan(report, document, assets, today);
        Replacements(report, document, today);
        Warranties(report, document, assets);
        Labels(report, document, assets, us, today);
        Records(report, document, assets);
        Keep(report, document, assets, us);
        return report;
    }

    private static string Where(Asset asset) =>
        string.Join(", ", new[] { asset.Room, asset.Level?.Name ?? string.Empty }.Where(part => part.Length > 0)).DefaultIfEmptyText("-");

    private static string DefaultIfEmptyText(this string text, string empty) => text.Length == 0 ? empty : text;

    /// <summary>A temperature as the project's country says it: °C, or °F in the US.</summary>
    private static string Degrees(double celsius, bool us) => us ? $"{F(celsius * 9 / 5 + 32, "0")} °F" : $"{F(celsius, "0.#")} °C";

    private static string Tags(IEnumerable<Asset> assets) => string.Join(", ", assets.Select(asset => asset.Tag));

    private static string Named(IReadOnlyList<Asset> assets, bool us)
    {
        if (assets.Count == 0) return string.Empty;
        var kind = Assets.Describe(assets[0].Kind, us).ToLowerInvariant();
        return assets.Count == 1 ? $"the {kind} ({assets[0].Tag}, {Where(assets[0])})" : $"{assets.Count} {kind}s ({Tags(assets)})";
    }

    // ---- the Home User Guide ----------------------------------------------------------------

    private static void HomeUserGuide(ReportDocument report, BimDocument document, IReadOnlyList<Asset> assets, bool us, DateOnly today)
    {
        IReadOnlyList<Asset> Of(params AssetKind[] kinds) => assets.Where(asset => kinds.Contains(asset.Kind)).ToList();

        report.Heading(us ? "Using your home" : "Home User Guide");

        // Heating and hot water.
        report.Heading("Heating and hot water", 2);
        var sources = Of(AssetKind.GasBoiler, AssetKind.OilBoiler, AssetKind.ElectricBoiler, AssetKind.HeatPump);
        var emitters = Of(AssetKind.Radiator, AssetKind.TowelRail, AssetKind.UnderfloorHeating);
        var thermostats = Of(AssetKind.Thermostat);
        var cylinders = Of(AssetKind.HotWaterCylinder);
        if (sources.Count == 0 && emitters.Count == 0)
            report.Paragraph("The model shows no heating yet.", ReportTone.Muted);
        else
        {
            var through = emitters.GroupBy(asset => asset.Kind).Select(group => $"{group.Count()} {Assets.Describe(group.Key, us).ToLowerInvariant()}{(group.Count() == 1 ? string.Empty : "s")}");
            report.Paragraph($"The home is heated by {(sources.Count == 0 ? "its heating" : Named(sources, us))}{(emitters.Count == 0 ? string.Empty : $", through {string.Join(" and ", through)}")}." +
                             (thermostats.Count == 0 ? string.Empty : $" It is controlled by {Named(thermostats, us)}."));
            var combi = sources.Any(source => source.Type is ComponentType { Form: ComponentForm.Boiler, HotWater: BoilerHotWater.Combi });
            report.Paragraph(cylinders.Count > 0
                ? $"Hot water is stored in {Named(cylinders, us)}. " + (us
                    ? "Its maker sets its temperature: 120 °F is the Consumer Product Safety Commission's advice against scalding."
                    : "Keep it stored at 60 °C or more - the HSE's guidance against legionella (HSG274 Part 2) - and run taps seldom used for a minute each week.")
                : combi ? "Hot water comes straight from the combination boiler as a tap is run: nothing is stored." : "The model shows no hot water storage.");

            if (HeatLoss.Of(document) is { } loss)
            {
                var temperatures = loss.Rooms.Select(room => room.Temperature).OfType<double>().ToList();
                if (temperatures.Count > 0)
                    report.Paragraph($"The heating was designed to hold the rooms at {(Math.Round(temperatures.Min()) == Math.Round(temperatures.Max()) ? Degrees(temperatures.Min(), us) : $"{Degrees(temperatures.Min(), us)} to {Degrees(temperatures.Max(), us)}")} when it is {Degrees(loss.Outside, us)} outside. " +
                                     "Set the thermostat to the lowest temperature you are comfortable at: each degree lower uses less energy.");
            }

            if (sources.Any(source => source.Kind == AssetKind.HeatPump))
            {
                var flow = HeatPumpSystems(document).Select(system => Heating.Design(document, system).Flow).DefaultIfEmpty(0).Max();
                report.Paragraph("A heat pump works best left on, holding the home steadily at its temperature" + (flow > 0 ? $" with water at {F(flow, "0")} °C going round, as designed" : string.Empty) +
                                 ", rather than turned up and down as a boiler is. Turning the flow temperature up costs more to run.");
            }
            else if (sources.Any(source => source.Kind == AssetKind.GasBoiler))
                report.Paragraph("A condensing boiler is most efficient with the water coming back to it cool: radiators turned down by their valves, not off and on.");
        }

        // Ventilation.
        report.Heading("Ventilation", 2);
        var (rate, _, bedrooms) = Ventilation.WholeDwelling(document);
        var units = Of(AssetKind.HeatRecoveryUnit, AssetKind.ExtractUnit);
        var fans = Of(AssetKind.ExtractFan, AssetKind.CookerHood);
        var vents = Of(AssetKind.BackgroundVentilator);
        switch (Ventilation.TypeOf(document))
        {
            case VentilationType.HeatRecovery:
                report.Paragraph($"The home is ventilated with heat recovery: {Named(units.Where(unit => unit.Kind == AssetKind.HeatRecoveryUnit).ToList(), us)} supplies fresh air to the living rooms and bedrooms and draws stale air from the kitchen and bathrooms, passing the heat of the one to the other. " +
                                 "Leave it on all the time; boost it while cooking, bathing or drying clothes. Clean or replace its filters as its upkeep below says - blocked filters cut the air it moves.");
                break;
            case VentilationType.ContinuousExtract:
                report.Paragraph($"The home is ventilated by continuous extract: {Named(units, us)} draws air from the kitchen and bathrooms all the time, and fresh air comes in through the background ventilators. " +
                                 "Leave it on all the time and the background ventilators open; boost it while cooking, bathing or drying clothes.");
                break;
            default:
                if (us)
                {
                    report.Paragraph("The home is ventilated naturally: air comes in through its windows, and " + (fans.Count == 0 ? "the windows" : Named(fans, us)) +
                                     (fans.Count == 1 ? " takes" : " take") + " the damp air out of the kitchen and bathrooms. Run the exhaust fans while cooking or bathing, and leave them to run on.");
                    break;
                }

                report.Paragraph("The home is ventilated naturally: background ventilators in its windows and walls let in a little fresh air all the time, and " +
                                 (fans.Count == 0 ? "the windows" : Named(fans, us)) + (fans.Count == 1 ? " takes" : " take") + " the damp air out of the kitchen and bathrooms. " +
                                 "Keep the background ventilators open, and use the extract fans while cooking or bathing, leaving them to run on.");
                break;
        }

        if (rate > 0)
            report.Paragraph($"The home was designed for {(us ? $"{F(rate * 2.119, "0")} cfm" : $"{F(rate, "0")} l/s")} of fresh air all the time{(bedrooms > 0 ? $", for its {bedrooms} bedroom{(bedrooms == 1 ? string.Empty : "s")}" : string.Empty)}. " +
                             "To clear smells or condensation quickly, open windows wide for a few minutes." + (vents.Count > 0 ? $" Its background ventilators: {Tags(vents)}." : string.Empty), ReportTone.Muted);

        if (us) return;

        // Electricity made on site.
        var arrays = SapSurvey.Of(document).Home.Photovoltaics;
        if (arrays.Count > 0)
        {
            report.Heading("Electricity made on site", 2);
            report.Paragraph($"Solar panels on the home - {string.Join(", ", arrays.Select(array => $"{F(array.PeakPower, "0.0#")} kWp facing {F(array.Orientation, "0")}° from north"))} - make electricity whenever it is light. " +
                             "It is used in the home first and what is left goes to the grid. Run the washing machine and dishwasher in the day to use more of it; keep the panels clear of shade from growing trees.");
        }

        // Staying cool in hot weather.
        report.Heading("Staying cool in hot weather", 2);
        if (Overheating.Of(document) is { } cool)
        {
            report.Paragraph((cool.CrossVentilated
                    ? "The home is kept from overheating by its windows, on opposite sides of it so that air can blow through"
                    : "The home is kept from overheating by opening its windows") +
                (cool.HighRisk ? ", and its sunny windows are shaded" : string.Empty) + ".");
            report.Paragraph("In the day, when it is hotter outside than in, keep the windows shut and draw blinds or curtains on the sunny side. In the evening, at night and early in the morning, " +
                             "when it is cooler outside, open the windows wide - on opposite sides of the home where you can, to let the air through. Do this all through the summer, from May to September, or whenever it is hot. " +
                             "Bedroom windows can be left open at night where it is safe and quiet - on the ground floor only as far as a lockable restrictor lets them.");
            var openings = cool.Openings.GroupBy(opening => opening.Room).Select(group => (IReadOnlyList<ReportCell>)new ReportCell[]
            {
                group.First().Room.Name, string.Join(", ", group.Select(opening => opening.Name)), F(group.Sum(opening => opening.Equivalent), "0.00"), F(group.Sum(opening => opening.NightEquivalent), "0.00")
            }).ToList();
            if (openings.Count > 0)
                report.Table(new[] { new ReportColumn("Room", 1.2), new ReportColumn("Windows to open", 2.4), new ReportColumn("Open by day, m²", 0.9, true), new ReportColumn("At night, m²", 0.9, true) }, openings);
            report.Paragraph("There are no automatic controls or sensors for cooling in this home" +
                             (Of(AssetKind.HeatRecoveryUnit).Count > 0 ? ": the heat recovery unit's summer bypass, where it has one, lets the cool night air in without heating it." : ".") +
                             " Keep the windows' hinges, stays and restrictors working as their upkeep below says.", ReportTone.Muted);
        }
        else report.Paragraph("Open the windows wide when it is cooler outside than in - in the evening and at night - and keep them shut with blinds drawn in the heat of the day.");

        // Where to turn things off.
        report.Heading("In an emergency", 2);
        var meter = Of(AssetKind.GasMeter);
        var stop = Of(AssetKind.StopValve);
        var board = Of(AssetKind.ConsumerUnit);
        var lines = new List<(string, string)>();
        if (meter.Count > 0) lines.Add(("If you smell gas", $"Turn the gas off at {Named(meter, us)}, open the windows, use no switches or flames, and call the National Gas Emergency Service on 0800 111 999."));
        if (stop.Count > 0) lines.Add(("A leak of water", $"Turn the water off at {Named(stop, us)}."));
        if (board.Count > 0) lines.Add(("The electricity", $"Switch off at the main switch of {Named(board, us)}."));
        lines.Add(("A fire", "Get everyone out, close the doors behind you, and call 999 from outside."));
        report.Facts(lines);
    }

    private static IEnumerable<MepSystem> HeatPumpSystems(BimDocument document) =>
        document.All<MepSystem>().Where(system => system.Kind == SystemKind.Heating && Heating.FromHeatPump(document, system));

    /// <summary>What the occupier does: the tasks for the occupier, each with the assets it is for.</summary>
    private static void WhatYouDo(ReportDocument report, BimDocument document, IReadOnlyList<Asset> assets, bool us, DateOnly today)
    {
        var yours = MaintenancePlan.Of(document, today).Where(item => item.Task.By.StartsWith("the occupier", StringComparison.Ordinal)).ToList();
        if (yours.Count == 0) return;
        report.Heading("Looking after it yourself");
        report.Table(new[] { new ReportColumn("What to do", 2.6), new ReportColumn("How often", 1), new ReportColumn("For", 1.6) },
            yours.GroupBy(item => (item.Task.Id, item.Task.Name)).Select(group => (IReadOnlyList<ReportCell>)new ReportCell[]
            {
                $"{group.Key.Name}: {group.First().Task.Description}", Capital(group.First().Task.Often), Tags(group.Select(item => item.Asset))
            }));
    }

    // ---- the services, system by system ---------------------------------------------------

    private static void Services(ReportDocument report, BimDocument document, IReadOnlyList<Asset> assets, bool us)
    {
        report.NewPage();
        report.Heading("The home's services and fittings");
        report.Paragraph("Each of the home's services and fittings: what it is, what it is for, where it is - its tag is on the floor plans that follow - how to work it and how it is kept up.", ReportTone.Muted);
        foreach (var (heading, kinds, purpose, operate) in Groups)
        {
            var these = assets.Where(asset => kinds.Contains(asset.Kind)).ToList();
            if (these.Count == 0) continue;
            report.Heading(heading, 2);
            report.Paragraph($"What they are for: {purpose}");
            report.Paragraph($"How to work them: {operate}");
            report.Table(new[] { new ReportColumn("Tag", 0.7), new ReportColumn("What it is", 1.5), new ReportColumn("Where", 1.4), new ReportColumn("Maker and model", 1.3), new ReportColumn("Kept up by", 2.2) },
                these.Select(asset => (IReadOnlyList<ReportCell>)new ReportCell[]
                {
                    asset.Tag, $"{Assets.Describe(asset.Kind, us)}: {asset.Name}", Where(asset),
                    asset.Maker.Length + asset.Model.Length == 0 ? new ReportCell("not yet given", ReportTone.Muted) : $"{asset.Maker} {asset.Model}".Trim(),
                    string.Join("; ", MaintenanceLibrary.For(document, asset).Select(task => $"{Lower(task.Name)}, {task.Often}")).DefaultIfEmptyText("-")
                }));
        }
    }

    // ---- where they are -----------------------------------------------------------------

    private static void Plans(ReportDocument report, BimDocument document, IReadOnlyList<Asset> assets)
    {
        var storeys = assets.Where(asset => asset.Level is not null).GroupBy(asset => asset.Level!).OrderBy(group => group.Key.Elevation).ToList();
        if (storeys.Count == 0) return;
        report.NewPage();
        report.Heading("Where they are");
        foreach (var storey in storeys)
        {
            report.Heading(storey.Key.Name, 2);
            PlanOf(report, document, storey.Key, storey.ToList());
        }
    }

    /// <summary>A storey's plan, fitted to the page, each asset's tag marked where it is.</summary>
    private static void PlanOf(ReportDocument report, BimDocument document, Level level, IReadOnlyList<Asset> assets)
    {
        var points = document.Elements.Where(element => element.LevelId == level.Id && element is Wall)
            .SelectMany(ElementTransforms.PlanPoints).Concat(assets.Select(asset => asset.At)).ToList();
        if (points.Count < 2) return;
        var minX = points.Min(point => point.X) - 600;
        var maxX = points.Max(point => point.X) + 600;
        var minY = points.Min(point => point.Y) - 600;
        var maxY = points.Max(point => point.Y) + 600;
        var width = ReportDocument.PageWidth - 100;
        var height = Math.Clamp(width * (maxY - minY) / Math.Max(1, maxX - minX), 160, 520);
        report.Block(height, (dc, area) =>
        {
            var scale = Math.Min(area.Width / (maxX - minX), area.Height / (maxY - minY));
            var left = area.Left + (area.Width - (maxX - minX) * scale) / 2;
            var top = area.Top + (area.Height - (maxY - minY) * scale) / 2;
            Point At(Point2D point) => new(left + (point.X - minX) * scale, top + (maxY - point.Y) * scale);

            var view = ViewReference.FloorPlan(level.Id);
            var renderer = new PlanRenderer(DrawingPalette.Paper)
            {
                Document = document,
                ActiveLevelId = level.Id,
                Filter = document.ViewSettings.FilterFor(document, view),
                Style = document.ViewSettings.StyleFor(document, view),
                DetailLevel = DetailLevel.Coarse,
                ShowWallLabels = false,
                View = view,
                PixelsPerDip = 1,
                PixelsPerPaperMm = 72 / 25.4 * 0.8
            };
            renderer.SetSelection(null);
            renderer.SetTransform(At, scale);
            dc.PushClip(new RectangleGeometry(area));
            renderer.DrawContent(dc);

            var ink = new SolidColorBrush(Color.FromRgb(0xB8, 0x2E, 0x22));
            ink.Freeze();
            var pen = new Pen(ink, 0.6);
            pen.Freeze();
            foreach (var asset in assets)
            {
                var text = new FormattedText(asset.Tag, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Bold, 5.2, ink, 1);
                var at = At(asset.At);
                var box = new Rect(at.X - text.Width / 2 - 1.5, at.Y - text.Height / 2 - 0.5, text.Width + 3, text.Height + 1);
                dc.DrawRoundedRectangle(Brushes.White, pen, box, 1.5, 1.5);
                dc.DrawText(text, new Point(box.Left + 1.5, box.Top + 0.5));
            }

            dc.Pop();
        });
    }

    // ---- the register, the plan, replacements, warranties ---------------------------------

    private static void Register(ReportDocument report, BimDocument document, IReadOnlyList<Asset> assets)
    {
        var us = document.ProjectInformation.ClearanceStandard.IsUs();
        report.NewPage();
        report.Heading("Asset register");
        var missing = assets.Count(asset => Assets.Missing(document, asset).Count > 0);
        if (missing > 0)
            report.Paragraph($"{missing} of the {assets.Count} assets still want their maker, model, serial number or installation date: fill them in from the labels on them and their documents.", ReportTone.Muted);
        report.Table(new[]
            {
                new ReportColumn("Tag", 0.7), new ReportColumn("What it is", 1.6), new ReportColumn("Where", 1.3), new ReportColumn("Maker and model", 1.3),
                new ReportColumn("Serial", 0.9), new ReportColumn("Installed", 0.75), new ReportColumn("Warranty to", 0.75), new ReportColumn("Life", 1.1)
            },
            assets.Select(asset =>
            {
                var life = Assets.LifeOf(document, asset);
                return (IReadOnlyList<ReportCell>)new ReportCell[]
                {
                    asset.Tag, $"{Assets.Describe(asset.Kind, us)}: {asset.Name}", Where(asset), $"{asset.Maker} {asset.Model}".Trim().DefaultIfEmptyText("-"),
                    asset.Record.SerialNumber.DefaultIfEmptyText(Assets.HasSerial(asset.Kind) ? "-" : "n/a"), Short(Assets.InstalledOn(document, asset)),
                    Short(Assets.WarrantyEnds(document, asset)),
                    life.Years is null ? life.Said : $"{life.Said}{(Assets.ReplaceBy(document, asset) is { } by ? $", to {by.Year}" : string.Empty)}"
                };
            }));
        report.Paragraph("Lives are each type's Expected Life where it is given, else the National Association of Home Builders' Study of Life Expectancy of Home Components (2007), Table 1, the shorter end of a range taken.", ReportTone.Muted, 7.5);
    }

    private static void Plan(ReportDocument report, BimDocument document, IReadOnlyList<Asset> assets, DateOnly today)
    {
        var due = MaintenancePlan.Of(document, today);
        if (due.Count == 0) return;
        report.NewPage();
        report.Heading("Maintenance plan");
        report.Paragraph("Each task that keeps the home's services and fittings up, how often and by whom, and what it rests on: the law, a standard, the maker's instructions, or common practice where none says. " +
                         "Where the maker's instructions for the model put in say otherwise, follow them.", ReportTone.Muted);
        report.Table(new[] { new ReportColumn("Task", 2.2), new ReportColumn("For", 1.2), new ReportColumn("How often", 0.9), new ReportColumn("By", 1.2), new ReportColumn("Rests on", 1.9) },
            due.GroupBy(item => (item.Task.Id, item.Task.Name, item.Task.Source)).Select(group =>
            {
                var task = group.First().Task;
                return (IReadOnlyList<ReportCell>)new ReportCell[]
                {
                    $"{task.Name}: {task.Description}", Tags(group.Select(item => item.Asset)), Capital(task.Often), Capital(task.By),
                    new($"{task.Basis switch { MaintenanceBasis.Law => "The law", MaintenanceBasis.Standard => "Standard", MaintenanceBasis.Maker => "Maker", _ => "Practice" }}: {task.Source}",
                        task.Basis == MaintenanceBasis.Law ? ReportTone.Plain : ReportTone.Muted)
                };
            }));

        // The first year, month by month.
        var from = document.Handover.HandedOver is { } handed && handed > today.AddYears(-1) ? handed : today;
        var to = from.AddYears(1).AddDays(-1);
        report.Heading($"The year from {Day(from)}", 2);
        var months = MaintenancePlan.Between(document, from, to, today).GroupBy(entry => (entry.Due.Year, entry.Due.Month)).ToList();
        report.Table(new[] { new ReportColumn("Month", 0.8), new ReportColumn("What falls due", 4.2) },
            months.Select(month => (IReadOnlyList<ReportCell>)new ReportCell[]
            {
                new DateOnly(month.Key.Year, month.Key.Month, 1).ToString("MMM yyyy", CultureInfo.CurrentCulture),
                string.Join("; ", month.GroupBy(entry => entry.Item.Task.Name).Select(task =>
                    $"{task.Key}{(task.First().Item.Task.Every is { Unit: IntervalUnit.Week } every ? $" ({every.Said})" : string.Empty)}: {Tags(task.Select(entry => entry.Item.Asset).Distinct())}"))
            }));
        var events = due.Where(item => item.Task.Every is null).GroupBy(item => item.Task.Name).ToList();
        if (events.Count > 0)
            report.Paragraph("And as they come: " + string.Join("; ", events.Select(group => $"{Lower(group.Key)} {group.First().Task.When} ({Tags(group.Select(item => item.Asset))})")) + ".", ReportTone.Muted);
    }

    private static void Replacements(ReportDocument report, BimDocument document, DateOnly today)
    {
        var years = MaintenancePlan.Replacements(document, today.Year, 30);
        if (years.Count == 0) return;
        report.Heading("Replacements over the next 30 years");
        var currency = document.ProjectInformation.ClearanceStandard.IsUs() ? "$" : "£";
        report.Table(new[] { new ReportColumn("Year", 0.5), new ReportColumn("What comes to the end of its life", 4), new ReportColumn("At today's cost", 0.9, true) },
            years.Select(year => (IReadOnlyList<ReportCell>)new ReportCell[]
            {
                year.Year.ToString(CultureInfo.CurrentCulture), string.Join(", ", year.Assets.Select(entry => $"{entry.Asset.Tag} {Lower(Assets.Describe(entry.Asset.Kind, document.ProjectInformation.ClearanceStandard.IsUs()))}")),
                year.Assets.Sum(entry => entry.Cost) is var cost && cost > 0 ? $"{currency}{cost.ToString("#,0", CultureInfo.CurrentCulture)}" : "-"
            }));
        report.Paragraph("From each asset's installation date and life; costs are its type's cost, where one is given.", ReportTone.Muted, 7.5);
    }

    private static void Warranties(ReportDocument report, BimDocument document, IReadOnlyList<Asset> assets)
    {
        var warranted = assets.Where(asset => asset.Type is { PartsWarranty: > 0 } or { LabourWarranty: > 0 }).ToList();
        if (warranted.Count == 0) return;
        report.Heading("Warranties");
        report.Table(new[] { new ReportColumn("Tag", 0.7), new ReportColumn("What it is", 1.8), new ReportColumn("Parts to", 0.9), new ReportColumn("Labour to", 0.9), new ReportColumn("From", 1.6) },
            warranted.Select(asset =>
            {
                var start = asset.Record.WarrantyStart ?? Assets.InstalledOn(document, asset);
                var type = asset.Type!;
                return (IReadOnlyList<ReportCell>)new ReportCell[]
                {
                    asset.Tag, asset.Name, Short(Assets.WarrantyEnds(document, asset)),
                    type.LabourWarranty > 0 && start is { } began ? Short(began.AddMonths((int)Math.Round(type.LabourWarranty * 12))) : "-",
                    type.WarrantyGuarantor.DefaultIfEmptyText(asset.Maker.DefaultIfEmptyText("-"))
                };
            }));
        report.Paragraph("Most makers' warranties ask for the servicing in the maintenance plan, recorded: keep the records.", ReportTone.Muted, 7.5);
    }

    // ---- labels to fix to the equipment ----------------------------------------------------

    private static void Labels(ReportDocument report, BimDocument document, IReadOnlyList<Asset> assets, bool us, DateOnly today)
    {
        var equipment = assets.Where(asset => asset.Kind is AssetKind.GasBoiler or AssetKind.OilBoiler or AssetKind.ElectricBoiler or AssetKind.HeatPump or AssetKind.HotWaterCylinder
            or AssetKind.HeatRecoveryUnit or AssetKind.ExtractUnit or AssetKind.ConsumerUnit or AssetKind.SprinklerValve or AssetKind.Equipment).ToList();
        if (equipment.Count == 0) return;
        report.NewPage();
        report.Heading("Labels to fix to the equipment");
        report.Paragraph(us
                ? "Each piece of equipment's regular maintenance, to cut out and fix to it where it can be seen, as the IECC asks (R303.3)."
                : "Each piece of equipment's regular maintenance, to cut out and fix to it. A heat pump's carries the design figures Approved Document L asks to be fixed to the unit or the hot water cylinder - outside, behind it, out of the weather (9.4).",
            ReportTone.Muted);

        var loss = HeatLoss.Of(document);
        foreach (var asset in equipment)
        {
            var lines = new List<(string Text, bool Strong)>
            {
                ($"{asset.Tag}  {Assets.Describe(asset.Kind, us)}", true),
                ($"{asset.Name}{(asset.Maker.Length + asset.Model.Length > 0 ? $" - {asset.Maker} {asset.Model}".TrimEnd() : string.Empty)}{(asset.Record.SerialNumber.Length > 0 ? $", serial {asset.Record.SerialNumber}" : string.Empty)}", false)
            };
            foreach (var task in MaintenanceLibrary.For(document, asset))
                lines.Add(($"{Capital(task.Often)}: {Lower(task.Name)} - {task.By}", false));

            if (asset.Kind == AssetKind.HeatPump && !us)
            {
                var systems = HeatPumpSystems(document).ToList();
                var temperatures = loss?.Rooms.Select(room => room.Temperature).OfType<double>().ToList() ?? new List<double>();
                var flow = systems.Select(system => Heating.Design(document, system).Flow).DefaultIfEmpty(0).Max();
                var outputs = systems.SelectMany(system => MepSystems.MembersOf(document, system)).OfType<Component>()
                    .Select(component => Heating.OutputOf(document, component)).OfType<EmitterOutput>().ToList();
                lines.Add(("Heat loss calculation: " + (loss is null || temperatures.Count == 0 ? "-" :
                    $"rooms {F(temperatures.Min(), "0")} to {F(temperatures.Max(), "0")} °C inside, {F(loss.Outside, "0.#")} °C outside; worked out {Day(today)}"), false));
                lines.Add(($"Design flow temperature: {(flow > 0 ? $"{F(flow, "0")} °C" : "-")}", false));
                lines.Add(($"Emitter circuit: {(outputs.Count == 0 ? "-" : $"{outputs.Count} emitters, {F(outputs.Sum(output => output.Watts) / 1000, "0.0")} kW at the design flow")}", false));
                lines.Add(("Minimum setback temperature: ............     Commissioned by: ............     Competent person scheme: ............", false));
            }

            var texts = lines.Select(line => new FormattedText(line.Text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, line.Strong ? Bold : Regular, line.Strong ? 9.5 : 7.8, Brushes.Black, 1)
            {
                MaxTextWidth = ReportDocument.PageWidth - 100 - 20
            }).ToList();
            var height = texts.Sum(text => text.Height + 1.5) + 14;
            report.Block(height, (dc, area) =>
            {
                var pen = new Pen(Brushes.Black, 0.8) { DashStyle = DashStyles.Dash };
                dc.DrawRectangle(null, pen, new Rect(area.Left, area.Top, area.Width, height));
                var y = area.Top + 7;
                foreach (var text in texts)
                {
                    dc.DrawText(text, new Point(area.Left + 10, y));
                    y += text.Height + 1.5;
                }
            });
        }
    }

    // ---- what has been done, and what to keep with this ----------------------------------

    private static void Records(ReportDocument report, BimDocument document, IReadOnlyList<Asset> assets)
    {
        var done = document.Handover.Done.OrderByDescending(record => record.On).ToList();
        if (done.Count == 0) return;
        var byId = assets.ToDictionary(asset => asset.Element.Id);
        report.Heading("Work recorded");
        report.Table(new[] { new ReportColumn("Done", 0.8), new ReportColumn("Asset", 1.4), new ReportColumn("Task", 1.6), new ReportColumn("By", 1.1), new ReportColumn("Notes", 1.6) },
            done.Select(record =>
            {
                var asset = byId.GetValueOrDefault(record.ElementId);
                var task = asset is null ? null : MaintenanceLibrary.For(document, asset).FirstOrDefault(each => each.Id == record.TaskId);
                return (IReadOnlyList<ReportCell>)new ReportCell[]
                {
                    MaintenancePlan.Said(record.On), asset is null ? "-" : $"{asset.Tag} {asset.Name}", task?.Name ?? record.TaskId, record.By.DefaultIfEmptyText("-"), record.Notes.DefaultIfEmptyText("-")
                };
            }));
    }

    private static void Keep(ReportDocument report, BimDocument document, IReadOnlyList<Asset> assets, bool us)
    {
        report.Heading("To keep with this manual");
        var manuals = assets.Where(asset => asset.Type is { Url.Length: > 0 }).GroupBy(asset => asset.Type!.Url).Select(group => $"{group.First().Name}: {group.Key}").ToList();
        var keep = new List<string> { "Each appliance's and each control's own instructions from its maker" + (manuals.Count > 0 ? $" - online: {string.Join("; ", manuals)}" : string.Empty) + "." };
        bool Has(params AssetKind[] kinds) => assets.Any(asset => kinds.Contains(asset.Kind));
        if (!us)
        {
            keep.Add("The commissioning sheets for the heating, hot water and ventilation, completed (Approved Document L 9.2(c)) - the Benchmark checklist for a boiler or heat pump, and the ventilation's commissioning record (Approved Document F).");
            keep.Add("The signed Building Regulations England Part L (BREL) report, as built, and the photographs of the build quality (Approved Document L 9.5, 9.6).");
            keep.Add("The recommendations report of the on-construction energy performance certificate (Approved Document L 9.6(b)).");
            keep.Add("The Electrical Installation Certificate (BS 7671), and the building control completion certificate.");
            if (Has(AssetKind.GasBoiler, AssetKind.GasFire, AssetKind.GasMeter)) keep.Add("The Gas Safe registered engineer's Building Regulations compliance certificate for the gas appliances.");
            if (Has(AssetKind.HotWaterCylinder)) keep.Add("The unvented hot water cylinder's installation and commissioning record (Approved Document G, G3).");
            if (Has(AssetKind.HeatPump)) keep.Add("The heat pump's MCS installation certificate and its heat loss calculation.");
            if (Has(AssetKind.SprinklerValve, AssetKind.Sprinkler)) keep.Add("The sprinkler system's commissioning certificate and its design (BS 9251:2021).");
        }
        else
        {
            keep.Add("The energy certificate posted in or on the electrical panel, as the IECC asks (R401.3).");
            keep.Add("The equipment's warranties, registered with their makers, and the building department's certificate of occupancy.");
            if (Has(AssetKind.SprinklerValve, AssetKind.Sprinkler)) keep.Add("The sprinkler system's design and its acceptance test (NFPA 13D).");
        }

        foreach (var line in keep) report.Paragraph("•  " + line);
    }
}
