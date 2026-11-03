using System.Globalization;
using BIMDesigner.Core.Handover;
using Xbim.Ifc4.DateTimeResource;
using Xbim.Ifc4.Interfaces;
using Xbim.Ifc4.Kernel;
using Xbim.Ifc4.MeasureResource;
using Xbim.Ifc4.ProcessExtension;
using Xbim.Ifc4.SharedFacilitiesElements;

namespace BIMDesigner.Infrastructure.Interoperability;

public static partial class IfcExport
{
    private sealed partial class Exporter
    {
        /// <summary>
        /// The handover as IFC4 carries it. Each asset's maker and model (Pset_ManufacturerTypeInformation),
        /// serial number, bar code and the day it was put in (Pset_ManufacturerOccurrence), its warranty
        /// (Pset_Warranty) and its life (Pset_ServiceLife) on its product, and the product grouped into an
        /// IfcAsset by its tag. The maintenance plan as an IfcWorkSchedule controlling an IfcTask for each
        /// task on each type - the assets it keeps up assigned to it by IfcRelAssignsToProcess - that recurs
        /// by an IfcTaskTimeRecurring at its interval from the day the home was handed over.
        /// </summary>
        private void ExportHandover()
        {
            var assets = Assets.Of(_document).Where(asset => _products.ContainsKey(asset.Element.Id)).ToList();
            if (assets.Count == 0) return;
            static string Day(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            static string Months(double years) => $"P{Math.Max(1, (int)Math.Round(years * 12))}M";

            foreach (var asset in assets)
            {
                var product = _products[asset.Element.Id];
                var installed = Assets.InstalledOn(_document, asset);
                if (asset.Maker.Length > 0 || asset.Model.Length > 0)
                    WriteSet(product, "Pset_ManufacturerTypeInformation", new Dictionary<string, IfcValue?>
                    {
                        ["Manufacturer"] = asset.Maker.Length > 0 ? new IfcLabel(asset.Maker) : null,
                        ["ModelReference"] = asset.Model.Length > 0 ? new IfcLabel(asset.Model) : null,
                        ["ModelLabel"] = asset.Type is { } type ? new IfcLabel(type.Name) : null
                    });

                WriteSet(product, "Pset_ManufacturerOccurrence", new Dictionary<string, IfcValue?>
                {
                    ["SerialNumber"] = asset.Record.SerialNumber.Length > 0 ? new IfcIdentifier(asset.Record.SerialNumber) : null,
                    ["BarCode"] = asset.Record.BarCode.Length > 0 ? new IfcIdentifier(asset.Record.BarCode) : null,
                    ["AcquisitionDate"] = installed is { } on ? new IfcDate(Day(on)) : null
                });

                if (asset.Type is { PartsWarranty: > 0 } warranted)
                    WriteSet(product, "Pset_Warranty", new Dictionary<string, IfcValue?>
                    {
                        ["WarrantyStartDate"] = (asset.Record.WarrantyStart ?? installed) is { } start ? new IfcDate(Day(start)) : null,
                        ["WarrantyEndDate"] = Assets.WarrantyEnds(_document, asset) is { } end ? new IfcDate(Day(end)) : null,
                        ["WarrantyPeriod"] = new IfcDuration(Months(warranted.PartsWarranty)),
                        ["WarrantyContent"] = new IfcText($"Parts {warranted.PartsWarranty:0.#} years, labour {warranted.LabourWarranty:0.#} years"),
                        ["PointOfContact"] = warranted.WarrantyGuarantor.Length > 0 ? new IfcLabel(warranted.WarrantyGuarantor) : null
                    });

                if (Assets.LifeOf(_document, asset) is { Years: { } years })
                    WriteSet(product, "Pset_ServiceLife", new Dictionary<string, IfcValue?> { ["ServiceLifeDuration"] = new IfcDuration(Months(years)) });

                var group = New<IfcAsset>(each =>
                {
                    each.GlobalId = PartId(asset.Element.Id, 0xA5, 0x5E).ToIfc();
                    each.Name = $"{asset.Tag} {asset.Name}";
                    each.Identification = asset.Tag;
                    if (installed is { } day) each.IncorporationDate = new IfcDate(Day(day));
                });
                New<IfcRelAssignsToGroup>(relation =>
                {
                    relation.RelatingGroup = group;
                    relation.RelatedObjects.Add(product);
                });
            }

            // The maintenance plan: a task for each task on each type, recurring from the handover.
            var from = _document.Handover.HandedOver ?? DateOnly.FromDateTime(DateTime.Today);
            var plan = New<IfcWorkSchedule>(schedule =>
            {
                schedule.GlobalId = PartId(assets[0].Element.Id, 0x6D, 0xD6).ToIfc();
                schedule.Name = "Maintenance plan";
                schedule.CreationDate = new IfcDateTime(DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture));
                schedule.StartTime = new IfcDateTime($"{Day(from)}T08:00:00");
                schedule.PredefinedType = IfcWorkScheduleTypeEnum.PLANNED;
            });

            var tasks = new List<IfcTask>();
            foreach (var kind in assets.GroupBy(asset => (Type: asset.Type?.Id ?? Guid.Empty, asset.Kind)))
            {
                var first = kind.First();
                foreach (var task in MaintenanceLibrary.For(_document, first))
                {
                    var products = kind.Select(asset => (IfcProduct)_products[asset.Element.Id]).ToList();
                    var hours = TimeSpan.FromHours(task.Hours);
                    var ifcTask = New<IfcTask>(each =>
                    {
                        each.GlobalId = PartId(first.Element.Id, (byte)(0x10 + tasks.Count % 200), 0x7A).ToIfc();
                        each.Name = task.Name;
                        each.Description = $"{task.Description} By {task.By}. {task.Source}.";
                        each.ObjectType = task.Category.ToString();
                        each.Identification = task.Id;
                        each.LongDescription = task.Every is null ? $"Done {task.When}." : $"Done {task.Every.Said}.";
                        each.Status = "Not started";
                        each.WorkMethod = task.By;
                        each.IsMilestone = false;
                        each.PredefinedType = IfcTaskTypeEnum.MAINTENANCE;
                        if (task.Every is { } every)
                            each.TaskTime = Recurring(every, from, $"PT{(int)hours.TotalHours}H{hours.Minutes}M");
                    });
                    New<IfcRelAssignsToProcess>(relation =>
                    {
                        relation.RelatingProcess = ifcTask;
                        relation.RelatedObjects.AddRange(products);
                    });
                    tasks.Add(ifcTask);
                }
            }

            if (tasks.Count > 0)
                New<IfcRelAssignsToControl>(relation =>
                {
                    relation.RelatingControl = plan;
                    relation.RelatedObjects.AddRange(tasks);
                });
        }

        /// <summary>When a task comes round, as IFC4 has it: its interval from a day, by the week, the month or the year, on that day of it.</summary>
        private IfcTaskTimeRecurring Recurring(Interval every, DateOnly from, string duration)
        {
            var pattern = New<IfcRecurrencePattern>(recurrence =>
            {
                recurrence.Interval = every.Count;
                switch (every.Unit)
                {
                    case IntervalUnit.Week:
                        recurrence.RecurrenceType = IfcRecurrenceTypeEnum.WEEKLY;
                        recurrence.WeekdayComponent.Add(new IfcDayInWeekNumber(((int)from.DayOfWeek + 6) % 7 + 1));
                        break;
                    case IntervalUnit.Month:
                        recurrence.RecurrenceType = IfcRecurrenceTypeEnum.MONTHLY_BY_DAY_OF_MONTH;
                        recurrence.DayComponent.Add(new IfcDayInMonthNumber(Math.Min(from.Day, 28)));
                        break;
                    default:
                        recurrence.RecurrenceType = IfcRecurrenceTypeEnum.YEARLY_BY_DAY_OF_MONTH;
                        recurrence.DayComponent.Add(new IfcDayInMonthNumber(Math.Min(from.Day, 28)));
                        recurrence.MonthComponent.Add(new IfcMonthInYearNumber(from.Month));
                        break;
                }
            });

            var next = every.After(from);
            return New<IfcTaskTimeRecurring>(time =>
            {
                time.DurationType = IfcTaskDurationEnum.WORKTIME;
                time.ScheduleDuration = new IfcDuration(duration);
                time.ScheduleStart = new IfcDateTime($"{next:yyyy-MM-dd}T08:00:00");
                time.Recurrence = pattern;
            });
        }
    }
}
