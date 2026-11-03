using System.Globalization;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;

namespace BIMDesigner.Core.Handover;

/// <summary>How often a task comes round: every so many weeks, months or years.</summary>
public enum IntervalUnit
{
    Week,
    Month,
    Year
}

/// <summary>An interval: every 6 months, every year, every 10 years.</summary>
public sealed record Interval(int Count, IntervalUnit Unit)
{
    public static Interval Weeks(int count) => new(count, IntervalUnit.Week);

    public static Interval Months(int count) => new(count, IntervalUnit.Month);

    public static Interval Years(int count) => new(count, IntervalUnit.Year);

    /// <summary>The day it comes round next after a day.</summary>
    public DateOnly After(DateOnly day) => Unit switch
    {
        IntervalUnit.Week => day.AddDays(7 * Count),
        IntervalUnit.Month => day.AddMonths(Count),
        _ => day.AddYears(Count)
    };

    /// <summary>"every week", "every 6 months", "every year".</summary>
    public string Said => Count == 1
        ? $"every {Unit.ToString().ToLowerInvariant()}"
        : $"every {Count} {Unit.ToString().ToLowerInvariant()}s";

    /// <summary>As an ISO 8601 duration, as IFC and COBie write one: "P6M", "P1Y", "P1W".</summary>
    public string Iso => $"P{Count}{Unit switch { IntervalUnit.Week => "W", IntervalUnit.Month => "M", _ => "Y" }}";

    /// <summary>About how many times a year it comes round.</summary>
    public double PerYear => Unit switch
    {
        IntervalUnit.Week => 52.0 / Count,
        IntervalUnit.Month => 12.0 / Count,
        _ => 1.0 / Count
    };
}

/// <summary>What kind of work a task is.</summary>
public enum MaintenanceCategory
{
    Test,
    Inspection,
    Service,
    Cleaning,
    Replacement,
    SafetyCheck
}

/// <summary>What a task rests on: a law, a standard or code of practice, the maker's instructions, or common practice.</summary>
public enum MaintenanceBasis
{
    Law,
    Standard,
    Maker,
    Practice
}

/// <summary>
/// A task that keeps an asset up: what it is, what is done, how often - or on what occasion, where it
/// comes with an event rather than the calendar - who does it, about how long it takes, and what it
/// rests on, cited.
/// </summary>
public sealed record MaintenanceTask(
    string Id, string Name, string Description, MaintenanceCategory Category, Interval? Every, string When,
    string By, double Hours, MaintenanceBasis Basis, string Source)
{
    /// <summary>How often, as the plan says it: its interval, or its occasion.</summary>
    public string Often => Every?.Said ?? When;
}

/// <summary>Work done on an asset: which task, on what day, by whom, and anything noted.</summary>
public sealed record MaintenanceRecord(Guid Id, Guid ElementId, string TaskId, DateOnly On, string By, string Notes);

/// <summary>
/// The project's handover: the day the home was handed over - what installation dates and the
/// maintenance plan run from where nothing else says - whether it is let to tenants, and whether it is
/// a building of flats with common parts, which bring the landlord's and the responsible person's
/// duties; and the work done on each asset since, as recorded.
/// </summary>
public sealed class HandoverSettings
{
    public DateOnly? HandedOver { get; set; }

    public bool Let { get; set; }

    public bool Flats { get; set; }

    public List<MaintenanceRecord> Done { get; } = new();
}

/// <summary>
/// The tasks that keep each kind of asset up, in England and in the US, each cited - what the law
/// asks (the Gas Safety (Installation and Use) Regulations 1998, the Electrical Safety Standards in the
/// Private Rented Sector (England) Regulations 2020, the Smoke and Carbon Monoxide Alarm (England)
/// Regulations 2015 as amended, the Fire Safety (England) Regulations 2022), what the standards ask
/// (BS 7671, BS 5839-6, BS 9251, NFPA 72, NFPA 13D, NFPA 211), and where neither says, what the makers'
/// instructions do or common practice is - said as such, for the makers' own instructions for the
/// model installed to be put first where they say otherwise.
/// </summary>
public static class MaintenanceLibrary
{
    private const string Occupier = "the occupier";
    private const string MakerSaid = "The maker's instructions";
    private const string Practice = "Common practice";

    private static MaintenanceTask Task(string id, string name, string description, MaintenanceCategory category, Interval? every, string by, double hours,
        MaintenanceBasis basis, string source, string when = "") =>
        new(id, name, description, category, every, when, by, hours, basis, source);

    /// <summary>The tasks for an asset, as the project's standard and its circumstances - let, flats - have them.</summary>
    public static IReadOnlyList<MaintenanceTask> For(BimDocument document, Asset asset)
    {
        var us = document.ProjectInformation.ClearanceStandard.IsUs();
        var handover = document.Handover;
        var let = handover.Let && !us;
        var tasks = new List<MaintenanceTask>();
        var gas = asset.Type is ComponentType { GasInput: > 0 };
        var gasSafe = "a Gas Safe registered engineer";

        void GasSafetyCheck()
        {
            if (let)
                tasks.Add(Task("gas-safety-check", "Gas safety check", "Have every gas appliance and flue checked for safety and the record kept; a copy given to the tenants within 28 days.",
                    MaintenanceCategory.SafetyCheck, Interval.Years(1), gasSafe, 1, MaintenanceBasis.Law,
                    "The Gas Safety (Installation and Use) Regulations 1998, reg. 36(3) and (6): within 12 months of installation, then at intervals of no more than 12 months"));
        }

        void AlarmAtTenancy()
        {
            if (let)
                tasks.Add(Task("alarm-tenancy", "Check alarm at new tenancy", "Check the alarm is in working order on the day a new tenancy begins; repair or replace it when a tenant reports it faulty.",
                    MaintenanceCategory.Test, null, "the landlord", 0.1, MaintenanceBasis.Law,
                    "The Smoke and Carbon Monoxide Alarm (England) Regulations 2015, as amended in 2022", "on the day each new tenancy begins"));
        }

        switch (asset.Kind)
        {
            case AssetKind.GasBoiler:
                tasks.Add(Task("boiler-service", "Service the boiler", "Have the boiler serviced: the burner, heat exchanger, flue and seals checked, the combustion analysed, the condensate trap cleaned.",
                    MaintenanceCategory.Service, Interval.Years(1), us ? "a qualified heating contractor" : gasSafe, 1.5, MaintenanceBasis.Maker,
                    MakerSaid + ", kept to for its warranty"));
                tasks.Add(Task("boiler-pressure", "Check the system pressure", "Check the pressure on the boiler's gauge is within the band its maker marks, and top it up at the filling loop where it has fallen.",
                    MaintenanceCategory.Inspection, Interval.Months(1), Occupier, 0.1, MaintenanceBasis.Maker, MakerSaid));
                GasSafetyCheck();
                break;

            case AssetKind.OilBoiler:
                tasks.Add(Task("boiler-service", "Service the boiler", "Have the boiler serviced: the burner and nozzle, the heat exchanger, the flue and the oil supply and filter checked, the combustion analysed.",
                    MaintenanceCategory.Service, Interval.Years(1), us ? "a qualified oil heating technician" : "an OFTEC registered technician", 2, MaintenanceBasis.Maker,
                    MakerSaid + ", kept to for its warranty"));
                break;

            case AssetKind.ElectricBoiler:
                tasks.Add(Task("boiler-service", "Service the boiler", "Have the boiler, its controls and the system's pressure and expansion vessel checked.",
                    MaintenanceCategory.Service, Interval.Years(1), "a heating engineer", 1, MaintenanceBasis.Maker, MakerSaid));
                break;

            case AssetKind.HeatPump:
                tasks.Add(Task("heat-pump-service", "Service the heat pump", "Have the heat pump serviced: the refrigerant circuit, the outdoor coil and fan, the water side's pressure, filter and flow, the controls and settings checked.",
                    MaintenanceCategory.Service, Interval.Years(1), us ? "a qualified HVAC technician (EPA 608 certified for the refrigerant)" : "a heat pump engineer (F-gas certified for work on the refrigerant)", 2,
                    MaintenanceBasis.Maker, MakerSaid + ", kept to for its warranty" + (us ? string.Empty : "; the Heat Pump Association's annual service checklist (HPAdoc0003)")));
                tasks.Add(Task("heat-pump-clear", "Keep the outdoor unit clear", "Keep leaves, snow and anything stacked away from the outdoor unit, clear of the clearances its maker gives, its grille and drain unblocked.",
                    MaintenanceCategory.Inspection, Interval.Months(1), Occupier, 0.1, MaintenanceBasis.Maker, MakerSaid));
                break;

            case AssetKind.HotWaterCylinder when us:
                tasks.Add(Task("water-heater-flush", "Flush the water heater", "Drain water from the drain valve until it runs clear, flushing out the sediment.",
                    MaintenanceCategory.Cleaning, Interval.Years(1), Occupier, 0.5, MaintenanceBasis.Maker, MakerSaid));
                tasks.Add(Task("relief-valve", "Test the relief valve", "Lift the temperature and pressure relief valve's lever briefly: water should flow and stop as it closes; its discharge pipe clear to its drain.",
                    MaintenanceCategory.Test, Interval.Years(1), Occupier, 0.1, MaintenanceBasis.Maker, MakerSaid));
                break;

            case AssetKind.HotWaterCylinder:
                tasks.Add(Task("cylinder-service", "Service the unvented cylinder", "Have the cylinder serviced: the expansion vessel's charge, the pressure reducing valve and strainer, the temperature and pressure relief valves operated, the tundish and discharge pipe checked.",
                    MaintenanceCategory.Service, Interval.Years(1), "a competent person qualified in unvented hot water systems", 1, MaintenanceBasis.Maker,
                    MakerSaid + "; Approved Document G (2015), G3: work on an unvented system by a competent person"));
                tasks.Add(Task("tundish-check", "Look at the tundish", "Look at the tundish by the cylinder: water running through it means a relief valve is letting go - call the installer.",
                    MaintenanceCategory.Inspection, Interval.Months(1), Occupier, 0.05, MaintenanceBasis.Maker, MakerSaid));
                break;

            case AssetKind.Radiator or AssetKind.TowelRail:
                tasks.Add(Task("bleed", "Bleed of air", "Before the heating season, bleed the air from it at its bleed valve - cold at the top while warm below means air in it - then check the system's pressure.",
                    MaintenanceCategory.Service, Interval.Years(1), Occupier, 0.1, MaintenanceBasis.Practice, Practice));
                break;

            case AssetKind.HeatRecoveryUnit or AssetKind.ExtractUnit:
                tasks.Add(Task("ventilation-filters", "Clean or replace the filters", "Clean or replace the unit's filters; a blocked filter cuts the air it moves and raises the power it takes.",
                    MaintenanceCategory.Cleaning, Interval.Months(6), Occupier, 0.25, MaintenanceBasis.Maker, MakerSaid + " (makers give 6 to 12 months)"));
                tasks.Add(Task("ventilation-service", "Service the ventilation", "Have the unit serviced, its fans and condensate drain checked, its valves and grilles cleaned and its airflows measured against those it was commissioned at.",
                    MaintenanceCategory.Service, Interval.Years(1), "a ventilation engineer", 1.5, MaintenanceBasis.Maker,
                    MakerSaid + (us ? string.Empty : "; Approved Document F, Volume 1 (2021): the airflows it was commissioned to")));
                break;

            case AssetKind.ExtractFan:
                tasks.Add(Task("fan-clean", "Clean the fan", "Clean the grille and the impeller; check it runs, and that its run-on timer or humidity control still works.",
                    MaintenanceCategory.Cleaning, Interval.Years(1), Occupier, 0.25, MaintenanceBasis.Maker, MakerSaid));
                break;

            case AssetKind.CookerHood:
                tasks.Add(Task("hood-filters", "Clean the grease filters", "Clean the metal grease filters - in a dishwasher will do; a recirculating hood's charcoal filter replaced every 3 to 6 months.",
                    MaintenanceCategory.Cleaning, Interval.Months(1), Occupier, 0.1, MaintenanceBasis.Maker, MakerSaid));
                break;

            case AssetKind.BackgroundVentilator:
                tasks.Add(Task("vent-clean", "Clean the vent", "Clean the vent, check it opens and closes, and leave it open.",
                    MaintenanceCategory.Cleaning, Interval.Years(1), Occupier, 0.05, MaintenanceBasis.Practice,
                    us ? Practice : "Approved Document F, Volume 1 (2021): background ventilators kept open; cleaning by common practice"));
                break;

            case AssetKind.SmokeAlarm or AssetKind.HeatAlarm:
                tasks.Add(Task("alarm-test", "Test the alarm", "Press the test button and hear it sound - and the others with it, where they are interlinked.",
                    MaintenanceCategory.Test, Interval.Months(1), Occupier, 0.05, us ? MaintenanceBasis.Maker : MaintenanceBasis.Standard,
                    us ? "NFPA's guidance and the maker's instructions: test monthly" : "BS 5839-6:2019, Table 3: at least monthly (weekly for a Grade A system)"));
                tasks.Add(Task("alarm-replace", "Replace the alarm", "Replace the alarm by the date marked on it - 10 years from when it was made, unless its maker says otherwise; a mains alarm by an electrician.",
                    MaintenanceCategory.Replacement, Interval.Years(10), us ? "the occupier or an electrician" : "the occupier, or an electrician for a mains alarm", 0.5,
                    us ? MaintenanceBasis.Standard : MaintenanceBasis.Maker,
                    us ? "NFPA 72, 10.4.6: no longer than 10 years from the date of manufacture" : "The makers' replace-by date on the alarm"));
                AlarmAtTenancy();
                break;

            case AssetKind.CarbonMonoxideAlarm:
                tasks.Add(Task("co-test", "Test the alarm", "Press the test button and hear it sound.",
                    MaintenanceCategory.Test, Interval.Weeks(1), Occupier, 0.02, MaintenanceBasis.Maker, MakerSaid + (us ? string.Empty : " (BS EN 50292 is the guide to their use)")));
                tasks.Add(Task("co-replace", "Replace the alarm", "Replace it when its end-of-life warning sounds, or by the replace-by date on it - 7 or 10 years, by the unit.",
                    MaintenanceCategory.Replacement, Interval.Years(7), Occupier, 0.25, MaintenanceBasis.Standard, "BS EN 50291-1: an end-of-life warning, and a replace-by date"));
                AlarmAtTenancy();
                break;

            case AssetKind.DuctSmokeDetector:
                tasks.Add(Task("duct-detector-test", "Test the duct detector", "Test it, and the fire and smoke dampers it closes, with the fire alarm system.",
                    MaintenanceCategory.Test, Interval.Years(1), "a fire alarm engineer", 1, MaintenanceBasis.Maker, MakerSaid));
                break;

            case AssetKind.ConsumerUnit when us:
                tasks.Add(Task("gfci-test", "Test GFCI and AFCI devices", "Press the TEST button on each GFCI and AFCI breaker and receptacle: it should trip; then RESET it.",
                    MaintenanceCategory.Test, Interval.Months(1), Occupier, 0.25, MaintenanceBasis.Maker, "The CPSC and UL: test monthly; the maker's instructions"));
                break;

            case AssetKind.ConsumerUnit:
                tasks.Add(Task("rcd-test", "Test the RCDs", "Press each RCD's and RCBO's test button: it should switch off; switch it back on.",
                    MaintenanceCategory.Test, Interval.Months(6), Occupier, 0.1, MaintenanceBasis.Standard, "BS 7671:2018+A4:2026, 514.12.2: the notice to test six-monthly"));
                tasks.Add(let
                    ? Task("eicr", "Electrical inspection and test", "Have the electrical installation inspected and tested, and the Electrical Installation Condition Report given to the tenants within 28 days.",
                        MaintenanceCategory.SafetyCheck, Interval.Years(5), "a qualified electrician", 4, MaintenanceBasis.Law,
                        "The Electrical Safety Standards in the Private Rented Sector (England) Regulations 2020, reg. 3: at intervals of no more than 5 years")
                    : Task("eicr", "Electrical inspection and test", "Have the electrical installation inspected and tested, and an Electrical Installation Condition Report written - and when the home changes hands.",
                        MaintenanceCategory.Inspection, Interval.Years(10), "a registered electrician", 4, MaintenanceBasis.Standard,
                        "IET Guidance Note 3, Table 3.2: 10 years for a home, or at a change of occupancy"));
                break;

            case AssetKind.SprinklerValve:
                tasks.Add(Task("sprinkler-valve-check", "Check the valve and gauge", "Check the stop valve on the sprinkler supply is locked open and the pressure gauge reads its normal pressure.",
                    MaintenanceCategory.Inspection, Interval.Months(1), Occupier, 0.05, MaintenanceBasis.Standard,
                    us ? "NFPA 13D (2022), A.12.2: valves and gauges monthly" : "The installer's instructions under BS 9251:2021"));
                if (us)
                    tasks.Add(Task("waterflow-test", "Test the waterflow alarm", "Open the test and drain valve: the waterflow alarm should sound; close it again.",
                        MaintenanceCategory.Test, Interval.Months(6), Occupier, 0.25, MaintenanceBasis.Standard, "NFPA 13D (2022), A.12.2 (advisory)"));
                tasks.Add(Task("sprinkler-service", "Inspect and test the sprinklers", "Have the system inspected, tested and serviced, and the record kept.",
                    MaintenanceCategory.Service, Interval.Years(1), us ? "a fire sprinkler contractor" : "a sprinkler contractor, the installer or one third-party certificated", 2,
                    MaintenanceBasis.Standard, us ? "NFPA 13D (2022), 12.2: inspected and tested periodically" : "BS 9251:2021: a full inspection and test once a year"));
                break;

            case AssetKind.Sprinkler:
                tasks.Add(Task("sprinkler-heads", "Look over the sprinkler", "Check nothing hangs from or covers it, nothing is stacked close under it, and it has not been painted or damaged.",
                    MaintenanceCategory.Inspection, Interval.Years(1), Occupier, 0.02, MaintenanceBasis.Standard,
                    us ? "NFPA 13D (2022), A.12.2" : "The installer's instructions under BS 9251:2021"));
                break;

            case AssetKind.StopValve:
                tasks.Add(Task("stop-valve-turn", "Turn the stop valve", "Turn it off and on again, so it turns freely when it is needed.",
                    MaintenanceCategory.Inspection, Interval.Years(1), Occupier, 0.05, MaintenanceBasis.Practice, Practice));
                break;

            case AssetKind.GasFire:
                tasks.Add(Task("gas-fire-service", "Service the gas fire", "Have the fire serviced and its flue checked.",
                    MaintenanceCategory.Service, Interval.Years(1), us ? "a qualified gas technician" : gasSafe, 1, MaintenanceBasis.Maker, MakerSaid));
                if (us)
                    tasks.Add(Task("vent-inspect", "Inspect the vent", "Have the fireplace's vent inspected.",
                        MaintenanceCategory.Inspection, Interval.Years(1), "a certified chimney sweep", 1, MaintenanceBasis.Standard, "NFPA 211: chimneys, fireplaces and vents inspected at least once a year"));
                GasSafetyCheck();
                break;

            case AssetKind.Cooker when gas:
                GasSafetyCheck();
                break;

            case AssetKind.HotWaterCylinder or AssetKind.Cooker:
                break;

            case AssetKind.Fridge:
                tasks.Add(Task("fridge-coils", "Clean the coils and seals", "Vacuum the condenser coils at the back or underneath, and clean the door seals.",
                    MaintenanceCategory.Cleaning, Interval.Years(1), Occupier, 0.25, MaintenanceBasis.Maker, MakerSaid));
                break;

            case AssetKind.Dishwasher:
                tasks.Add(Task("dishwasher-filter", "Clean the filter", "Take out and rinse the filter in the base of the tub.",
                    MaintenanceCategory.Cleaning, Interval.Months(1), Occupier, 0.1, MaintenanceBasis.Maker, MakerSaid));
                break;

            case AssetKind.WashingMachine:
                tasks.Add(Task("washer-filter", "Clean the filter", "Clean the drain pump filter and the detergent drawer, and run an empty hot wash.",
                    MaintenanceCategory.Cleaning, Interval.Months(3), Occupier, 0.25, MaintenanceBasis.Maker, MakerSaid));
                break;

            case AssetKind.TumbleDryer:
                tasks.Add(Task("dryer-lint", "Clean the lint filter", "Clean the lint filter.", MaintenanceCategory.Cleaning, null, Occupier, 0.02,
                    MaintenanceBasis.Maker, us ? "The US Fire Administration; the maker's instructions" : MakerSaid, "after every load"));
                tasks.Add(Task("dryer-vent", "Clean the vent duct", "Clean the lint out of the vent duct to outside, or a condenser dryer's condenser.",
                    MaintenanceCategory.Cleaning, Interval.Years(1), us ? "the occupier or a dryer vent cleaner" : Occupier, 0.5, MaintenanceBasis.Maker,
                    us ? "The US Fire Administration: at least once a year" : MakerSaid));
                break;

            case AssetKind.Toilet:
                tasks.Add(Task("cistern-check", "Check the cistern", "Check water is not running on into the pan after it fills; renew the fill or flush valve's washer where it is.",
                    MaintenanceCategory.Inspection, Interval.Years(1), Occupier, 0.1, MaintenanceBasis.Practice, Practice));
                break;

            case AssetKind.Shower:
                tasks.Add(Task("shower-descale", "Descale the head", "Descale the shower head, and check the seal round the tray or enclosure.",
                    MaintenanceCategory.Cleaning, Interval.Months(6), Occupier, 0.2, MaintenanceBasis.Practice, Practice));
                break;

            case AssetKind.Basin or AssetKind.Sink or AssetKind.Bath:
                tasks.Add(Task("seal-check", "Check the seal", "Check the sealant round it, and renew it where it has split or lifted.",
                    MaintenanceCategory.Inspection, Interval.Years(1), Occupier, 0.1, MaintenanceBasis.Practice, Practice));
                break;

            case AssetKind.DrainAccess:
                tasks.Add(Task("drain-inspect", "Look into the drain", "Lift the cover and check the drain runs freely; have it rodded or jetted where it has silted.",
                    MaintenanceCategory.Inspection, Interval.Years(1), "the occupier, or a drainage contractor", 0.25, MaintenanceBasis.Practice, Practice));
                break;

            case AssetKind.Soakaway:
                tasks.Add(Task("soakaway-inspect", "Check the soakaway", "After heavy rain, check it drains away; clear its silt trap or catchpit.",
                    MaintenanceCategory.Inspection, Interval.Years(1), "the occupier, or a drainage contractor", 0.5, MaintenanceBasis.Practice, Practice));
                break;

            case AssetKind.AirAdmittanceValve:
                tasks.Add(Task("aav-check", "Check the valve", "Check it opens freely and is not stuck or blocked; keep its cover clear.",
                    MaintenanceCategory.Inspection, Interval.Years(1), Occupier, 0.05, MaintenanceBasis.Maker, MakerSaid));
                break;

            case AssetKind.Door when asset.Type is DoorType { Function: DoorFunction.Exterior }:
                tasks.Add(Task("door-ironmongery", "Oil the locks and hinges", "Oil the locks, hinges and multipoint gear, and check the seals and the threshold's drainage.",
                    MaintenanceCategory.Service, Interval.Years(1), Occupier, 0.25, MaintenanceBasis.Practice, Practice));
                break;

            case AssetKind.FireDoor:
                var closes = asset.Type is DoorType { SelfClosing: true }
                    ? "Check it closes fully onto its latch from any angle on its self-closer"
                    : "Check it shuts fully onto its latch - keep it shut at night";
                tasks.Add(Task("fire-door-check", "Check the fire door", $"{closes}, its gaps even, its intumescent strips and smoke seals whole, its hinges tight and its glass and signs in place - never wedged open.",
                    MaintenanceCategory.Inspection, us ? Interval.Years(1) : Interval.Months(6), us ? Occupier : "the occupier, or the responsible person", 0.25, MaintenanceBasis.Standard,
                    us ? "NFPA 80: fire door assemblies inspected and tested at least once a year" : "BS 8214:2016 and the BWF-CERTIFIRE best practice guide: every six months, as widely quoted"));
                if (!us && handover.Flats && Over11Metres(document))
                    tasks.Add(Task("fire-door-reg10", "Fire door check (regulation 10)", "Check the building's fire doors: those in the common parts at least every 3 months, each flat's entrance door at least every 12 months, on a best endeavours basis.",
                        MaintenanceCategory.SafetyCheck, Interval.Months(3), "the responsible person", 0.25, MaintenanceBasis.Law,
                        "The Fire Safety (England) Regulations 2022, reg. 10: buildings of flats with a storey over 11 m"));
                break;

            case AssetKind.Window:
                tasks.Add(Task("window-maintain", "Clean and oil the window", "Clean the drainage slots and the seals; oil the hinges, stays and locks; check the restrictors work.",
                    MaintenanceCategory.Service, Interval.Years(1), Occupier, 0.2, MaintenanceBasis.Maker, MakerSaid));
                break;

            case AssetKind.RoofWindow:
                tasks.Add(Task("roof-window-maintain", "Clean and check the roof window", "Clean the flashings and gutters round it and its ventilation filter; check it opens and closes.",
                    MaintenanceCategory.Service, Interval.Years(1), Occupier, 0.3, MaintenanceBasis.Maker, MakerSaid));
                break;

            case AssetKind.Gutter:
                tasks.Add(Task("gutter-clear", "Clear the gutters", "Clear the gutters and their outlets of leaves and moss, and check the joints do not drip.",
                    MaintenanceCategory.Cleaning, asset.Type is GutterType { LeafGuard: true } ? Interval.Years(1) : Interval.Months(6),
                    "the occupier, or a roofer for high work", 1, MaintenanceBasis.Practice, Practice));
                break;

            case AssetKind.Chimney:
                var chimney = (Chimney)asset.Element;
                var fired = chimney.Fireplace != ChimneyFireplace.None || chimney.FireplacesAbove.Values.Any(fire => fire != ChimneyFireplace.None);
                if (fired)
                    tasks.Add(us
                        ? Task("chimney-inspect", "Inspect the chimney", "Have the chimney and flue inspected, and swept as it needs.",
                            MaintenanceCategory.Inspection, Interval.Years(1), "a certified chimney sweep", 1.5, MaintenanceBasis.Standard,
                            "NFPA 211: chimneys, fireplaces and vents inspected at least once a year")
                        : Task("chimney-sweep", "Sweep the chimney", "Have the chimney swept: at least once a year burning smokeless fuel, at least twice a year burning wood or house coal.",
                            MaintenanceCategory.Cleaning, Interval.Years(1), "a registered chimney sweep (HETAS, NACS, APICS or the Guild)", 1.5, MaintenanceBasis.Practice,
                            "HETAS: at least once a year for smokeless fuels, twice a year for wood or bituminous coal"));
                tasks.Add(Task("chimney-stack", "Look over the stack", "From the ground, look over the stack, its pots, flaunching and flashings for cracks and slipped pieces.",
                    MaintenanceCategory.Inspection, Interval.Years(1), Occupier, 0.1, MaintenanceBasis.Practice, Practice));
                break;

            case AssetKind.Equipment:
                tasks.Add(Task("equipment-service", "Service as its maker says", "Service it as its maker's instructions say.",
                    MaintenanceCategory.Service, Interval.Years(1), "a competent person", 1, MaintenanceBasis.Maker, MakerSaid));
                break;
        }

        return tasks;
    }

    /// <summary>Whether a storey of the building is more than 11 m above the lowest - as the Fire Safety (England) Regulations 2022 count height.</summary>
    public static bool Over11Metres(BimDocument document)
    {
        var levels = document.Levels.Where(level => document.All<Room>().Any(room => room.LevelId == level.Id)).ToList();
        if (levels.Count == 0) return false;
        var lowest = document.Levels.Min(level => level.Elevation);
        return levels.Max(level => level.Elevation) - lowest > 11000;
    }
}

/// <summary>A task on an asset and when it is next due: from when it was last done, or from when the asset was installed.</summary>
public sealed record DueTask(Asset Asset, MaintenanceTask Task, DateOnly? Last, DateOnly? Next)
{
    public bool OverdueOn(DateOnly today) => Next is { } next && next < today;
}

/// <summary>
/// The maintenance plan: every task on every asset, when it was last done and when it is due next -
/// its interval on from the day it was last done, or from the day the asset was installed, or the home
/// handed over, or today where neither is known; what falls due between two days, each time it comes
/// round; what is overdue; and the replacements its assets' lives bring, year by year, at their types'
/// costs.
/// </summary>
public static class MaintenancePlan
{
    /// <summary>Each task on each asset, with when it was last done and is next due.</summary>
    public static IReadOnlyList<DueTask> Of(BimDocument document, DateOnly today)
    {
        var done = document.Handover.Done
            .GroupBy(record => (record.ElementId, record.TaskId))
            .ToDictionary(group => group.Key, group => group.Max(record => record.On));
        var due = new List<DueTask>();
        foreach (var asset in Assets.Of(document))
        foreach (var task in MaintenanceLibrary.For(document, asset))
        {
            DateOnly? last = done.TryGetValue((asset.Element.Id, task.Id), out var on) ? on : null;
            var from = last ?? Assets.InstalledOn(document, asset) ?? today;
            due.Add(new DueTask(asset, task, last, task.Every?.After(from)));
        }

        return due;
    }

    /// <summary>What falls due from one day to another, each time it comes round, in order.</summary>
    public static IReadOnlyList<(DateOnly Due, DueTask Item)> Between(BimDocument document, DateOnly from, DateOnly to, DateOnly today)
    {
        var occurrences = new List<(DateOnly, DueTask)>();
        foreach (var item in Of(document, today))
        {
            if (item.Next is not { } next || item.Task.Every is not { } every) continue;
            for (var day = next; day <= to && occurrences.Count < 100_000; day = every.After(day))
                if (day >= from) occurrences.Add((day, item));
        }

        return occurrences.OrderBy(entry => entry.Item1).ThenBy(entry => entry.Item2.Asset.Tag, StringComparer.Ordinal).ToList();
    }

    /// <summary>What is overdue on a day: due before it and not done.</summary>
    public static IReadOnlyList<DueTask> Overdue(BimDocument document, DateOnly today) =>
        Of(document, today).Where(item => item.OverdueOn(today)).OrderBy(item => item.Next).ToList();

    /// <summary>Records tasks done on a day, as one step to undo.</summary>
    public static IUndoableCommand Record(BimDocument document, IReadOnlyList<DueTask> items, DateOnly on, string by, string notes) =>
        HandoverEditCommand.Make(document, items.Count == 1 ? $"Record {items[0].Task.Name}" : $"Record {items.Count} Tasks Done", () =>
        {
            foreach (var item in items)
                document.Handover.Done.Add(new MaintenanceRecord(Guid.NewGuid(), item.Asset.Element.Id, item.Task.Id, on, by.Trim(), notes.Trim()));
        });

    /// <summary>The replacements due year by year from one year for so many: each asset whose life ends in it, with its type's cost.</summary>
    public static IReadOnlyList<(int Year, IReadOnlyList<(Asset Asset, decimal Cost)> Assets)> Replacements(BimDocument document, int fromYear, int years)
    {
        var due = new List<(int, Asset, decimal)>();
        foreach (var asset in Assets.Of(document))
        {
            if (Assets.LifeOf(document, asset) is not { Years: { } life } || life <= 0 || Assets.InstalledOn(document, asset) is not { } installed) continue;
            for (var next = installed.AddMonths((int)Math.Round(life * 12)); next.Year < fromYear + years; next = next.AddMonths((int)Math.Round(life * 12)))
                if (next.Year >= fromYear) due.Add((next.Year, asset, asset.Type?.Cost ?? 0));
        }

        return due.GroupBy(entry => entry.Item1).OrderBy(group => group.Key)
            .Select(group => (group.Key, (IReadOnlyList<(Asset, decimal)>)group.Select(entry => (entry.Item2, entry.Item3)).ToList()))
            .ToList();
    }

    /// <summary>A day as the plan says it.</summary>
    public static string Said(DateOnly day) => day.ToString("d MMM yyyy", CultureInfo.CurrentCulture);
}

/// <summary>A change to the handover - its date, whether it is let or flats, the work recorded - as one step to undo.</summary>
public sealed class HandoverEditCommand : IUndoableCommand
{
    private readonly BimDocument _document;
    private readonly State _before;
    private State _after;

    private HandoverEditCommand(BimDocument document, string name)
    {
        _document = document;
        Name = name;
        _before = State.Of(document.Handover);
        _after = _before;
    }

    /// <summary>Makes a change as one step: taken before, made, taken after.</summary>
    public static HandoverEditCommand Make(BimDocument document, string name, Action change)
    {
        var command = new HandoverEditCommand(document, name);
        change();
        command._after = State.Of(document.Handover);
        return command;
    }

    public string Name { get; }

    public void Redo() => _after.Restore(_document.Handover);

    public void Undo() => _before.Restore(_document.Handover);

    private sealed record State(DateOnly? HandedOver, bool Let, bool Flats, IReadOnlyList<MaintenanceRecord> Done)
    {
        public static State Of(HandoverSettings handover) => new(handover.HandedOver, handover.Let, handover.Flats, handover.Done.ToList());

        public void Restore(HandoverSettings handover)
        {
            handover.HandedOver = HandedOver;
            handover.Let = Let;
            handover.Flats = Flats;
            handover.Done.Clear();
            handover.Done.AddRange(Done);
        }
    }
}
