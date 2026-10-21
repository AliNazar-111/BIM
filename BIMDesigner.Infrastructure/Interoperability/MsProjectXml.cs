using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using BIMDesigner.Core.Coordination;
using BIMDesigner.Core.Documents;

namespace BIMDesigner.Infrastructure.Interoperability;

/// <summary>
/// The construction programme as Microsoft Project's XML (MSPDI) - which Primavera P6 and Asta
/// Powerproject read and write too: its calendar, and each task with its dates, duration in hours
/// of an eight-hour day, its finish-to-start links and their lag in tenths of a minute, its
/// earliest start, baseline and progress. Read back, a task of the same name keeps what it is
/// linked to in the model; a new one comes in with nothing linked yet.
/// </summary>
public static class MsProjectXml
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/project";
    private const int MinutesPerDay = 480;

    private static string Date(DateOnly date, bool end) => date.ToDateTime(end ? new TimeOnly(17, 0) : new TimeOnly(8, 0)).ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);

    private static string Hours(int days) => $"PT{days * 8}H0M0S";

    public static void Write(Stream output, BimDocument document)
    {
        var schedule = document.Construction;
        var times = ConstructionPlanning.Time(schedule).ToDictionary(timed => timed.Task.Id);
        var uid = schedule.Tasks.Select((task, index) => (task.Id, index + 1)).ToDictionary(entry => entry.Id, entry => entry.Item2);
        var budgets = CostPlanning.TaskBudgets(document);
        var finish = ConstructionPlanning.Finish(schedule);

        XElement E(string name, object? value) => new(Ns + name, value);

        var weekDays = new XElement(Ns + "WeekDays");
        for (var day = 1; day <= 7; day++)
        {
            var dayOfWeek = (DayOfWeek)(day - 1);
            var working = schedule.WorkingDays.Contains(dayOfWeek);
            var weekDay = new XElement(Ns + "WeekDay", E("DayType", day), E("DayWorking", working ? 1 : 0));
            if (working)
                weekDay.Add(new XElement(Ns + "WorkingTimes",
                    new XElement(Ns + "WorkingTime", E("FromTime", "08:00:00"), E("ToTime", "12:00:00")),
                    new XElement(Ns + "WorkingTime", E("FromTime", "13:00:00"), E("ToTime", "17:00:00"))));
            weekDays.Add(weekDay);
        }

        foreach (var holiday in schedule.Holidays.OrderBy(date => date))
            weekDays.Add(new XElement(Ns + "WeekDay", E("DayType", 0), E("DayWorking", 0),
                new XElement(Ns + "TimePeriod", E("FromDate", Date(holiday, false).Replace("08:00", "00:00")), E("ToDate", holiday.ToDateTime(new TimeOnly(23, 59)).ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)))));

        var tasks = new XElement(Ns + "Tasks");
        var id = 0;
        foreach (var task in schedule.Tasks)
        {
            var timed = times[task.Id];
            var element = new XElement(Ns + "Task",
                E("UID", uid[task.Id]), E("ID", ++id), E("Name", task.Name), E("Type", 1), E("IsNull", 0),
                E("OutlineNumber", id), E("OutlineLevel", 1),
                E("Start", Date(timed.Start, false)), E("Finish", Date(timed.Finish, true)),
                E("Duration", Hours(task.Duration)), E("DurationFormat", 7),
                E("Milestone", task.Duration == 0 ? 1 : 0), E("Summary", 0), E("Critical", timed.Critical ? 1 : 0),
                E("PercentComplete", (int)Math.Round(task.PercentComplete)),
                E("Cost", budgets.GetValueOrDefault(task.Id).ToString("0.00", CultureInfo.InvariantCulture)));
            if (task.ActualStart is { } began) element.Add(E("ActualStart", Date(began, false)));
            if (task.ActualFinish is { } ended) element.Add(E("ActualFinish", Date(ended, true)));
            if (task.ActualCost != 0) element.Add(E("ActualCost", task.ActualCost.ToString("0.00", CultureInfo.InvariantCulture)));
            element.Add(E("ConstraintType", task.StartNoEarlierThan is null ? 0 : 4));
            if (task.StartNoEarlierThan is { } earliest) element.Add(E("ConstraintDate", Date(earliest, false)));
            if (!string.IsNullOrWhiteSpace(task.Notes)) element.Add(E("Notes", task.Notes));
            foreach (var link in task.Predecessors.Where(link => uid.ContainsKey(link.Predecessor)))
                element.Add(new XElement(Ns + "PredecessorLink",
                    E("PredecessorUID", uid[link.Predecessor]), E("Type", 1), E("LinkLag", link.Lag * MinutesPerDay * 10), E("LagFormat", 7)));
            if (task.BaselineStart is { } baseStart && task.BaselineFinish is { } baseFinish)
                element.Add(new XElement(Ns + "Baseline", E("Number", 0), E("Start", Date(baseStart, false)), E("Finish", Date(baseFinish, true)), E("Duration", Hours(task.Duration))));
            tasks.Add(element);
        }

        var project = new XElement(Ns + "Project",
            E("SaveVersion", 14), E("Name", document.ProjectInformation.Name), E("Title", document.ProjectInformation.Name),
            E("ScheduleFromStart", 1), E("StartDate", Date(ConstructionPlanning.FirstDay(schedule), false)), E("FinishDate", Date(finish, true)),
            E("CurrencySymbol", CostPlanning.Currency(document)), E("CalendarUID", 1),
            E("DefaultStartTime", "08:00:00"), E("DefaultFinishTime", "17:00:00"), E("MinutesPerDay", MinutesPerDay),
            E("MinutesPerWeek", MinutesPerDay * Math.Max(1, schedule.WorkingDays.Count)), E("DaysPerMonth", 20), E("DurationFormat", 7));
        if (schedule.StatusDate is { } status) project.Add(E("StatusDate", Date(status, true)));
        project.Add(new XElement(Ns + "Calendars", new XElement(Ns + "Calendar", E("UID", 1), E("Name", "Standard"), E("IsBaseCalendar", 1), weekDays)));
        project.Add(tasks);

        using var writer = XmlWriter.Create(output, new XmlWriterSettings { Indent = true, Encoding = new System.Text.UTF8Encoding(false) });
        new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), project).Save(writer);
    }

    /// <summary>An MSPDI duration in working days: hours of an eight-hour day, as Project writes them.</summary>
    private static int Days(string? duration, int minutesPerDay)
    {
        if (string.IsNullOrWhiteSpace(duration)) return 0;
        try
        {
            return (int)Math.Round(XmlConvert.ToTimeSpan(duration).TotalMinutes / Math.Max(1, minutesPerDay));
        }
        catch (FormatException)
        {
            return 0;
        }
    }

    private static DateOnly? DateOf(string? text) =>
        DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? DateOnly.FromDateTime(date) : null;

    /// <summary>
    /// Reads a programme in: its start and working week, and its tasks - summary tasks left out -
    /// each laid over the task of the same name where there is one, keeping what that is linked
    /// to, or added. Every link is taken as finish to start. Returns how many tasks came in.
    /// </summary>
    public static int Read(Stream input, BimDocument document)
    {
        var xml = XDocument.Load(input);
        var root = xml.Root ?? throw new InvalidDataException("Not a Microsoft Project XML file.");
        var ns = root.Name.Namespace;
        string? Value(XElement parent, string name) => parent.Element(ns + name)?.Value;
        var minutesPerDay = int.TryParse(Value(root, "MinutesPerDay"), out var minutes) && minutes > 0 ? minutes : MinutesPerDay;
        var schedule = document.Construction;

        if (DateOf(Value(root, "StartDate")) is { } start) schedule.Start = start;
        if (DateOf(Value(root, "StatusDate")) is { } status) schedule.StatusDate = status;

        // The working week, from the project's calendar.
        var calendarUid = Value(root, "CalendarUID");
        var calendar = root.Element(ns + "Calendars")?.Elements(ns + "Calendar").FirstOrDefault(each => Value(each, "UID") == calendarUid)
                       ?? root.Element(ns + "Calendars")?.Elements(ns + "Calendar").FirstOrDefault();
        if (calendar?.Element(ns + "WeekDays") is { } days)
        {
            var working = days.Elements(ns + "WeekDay")
                .Where(day => int.TryParse(Value(day, "DayType"), out var type) && type is >= 1 and <= 7 && Value(day, "DayWorking") == "1")
                .Select(day => (DayOfWeek)(int.Parse(Value(day, "DayType")!, CultureInfo.InvariantCulture) - 1)).ToList();
            if (working.Count > 0)
            {
                schedule.WorkingDays.Clear();
                schedule.WorkingDays.AddRange(working.Distinct());
            }

            foreach (var exception in days.Elements(ns + "WeekDay").Where(day => Value(day, "DayType") == "0" && Value(day, "DayWorking") != "1"))
                if (exception.Element(ns + "TimePeriod") is { } period && DateOf(Value(period, "FromDate")) is { } from && DateOf(Value(period, "ToDate")) is { } to)
                    for (var date = from; date <= to; date = date.AddDays(1))
                        if (!schedule.Holidays.Contains(date)) schedule.Holidays.Add(date);
        }

        var byUid = new Dictionary<string, ConstructionTask>();
        var links = new List<(ConstructionTask Task, XElement Element)>();
        var read = 0;
        foreach (var element in root.Element(ns + "Tasks")?.Elements(ns + "Task") ?? Enumerable.Empty<XElement>())
        {
            if (Value(element, "Summary") == "1" || Value(element, "UID") == "0" || Value(element, "IsNull") == "1") continue;
            var name = Value(element, "Name")?.Trim();
            if (string.IsNullOrEmpty(name)) continue;

            var task = schedule.Tasks.FirstOrDefault(each => string.Equals(each.Name, name, StringComparison.OrdinalIgnoreCase));
            if (task is null)
            {
                task = new ConstructionTask { Name = name };
                schedule.Tasks.Add(task);
            }

            task.Duration = Value(element, "Milestone") == "1" ? 0 : Math.Max(0, Days(Value(element, "Duration"), minutesPerDay));
            task.PercentComplete = double.TryParse(Value(element, "PercentComplete"), NumberStyles.Float, CultureInfo.InvariantCulture, out var percent) ? Math.Clamp(percent, 0, 100) : 0;
            task.ActualStart = DateOf(Value(element, "ActualStart"));
            task.ActualFinish = DateOf(Value(element, "ActualFinish"));
            if (decimal.TryParse(Value(element, "ActualCost"), NumberStyles.Float, CultureInfo.InvariantCulture, out var cost)) task.ActualCost = cost;
            task.Notes = Value(element, "Notes") ?? task.Notes;
            task.StartNoEarlierThan = Value(element, "ConstraintType") == "4" ? DateOf(Value(element, "ConstraintDate")) : null;
            if (element.Elements(ns + "Baseline").FirstOrDefault(baseline => Value(baseline, "Number") is null or "0") is { } saved)
                (task.BaselineStart, task.BaselineFinish) = (DateOf(Value(saved, "Start")), DateOf(Value(saved, "Finish")));

            // With nothing to wait for, a task starts no earlier than Project had it.
            if (!element.Elements(ns + "PredecessorLink").Any() && task.StartNoEarlierThan is null && DateOf(Value(element, "Start")) is { } begins && begins > schedule.Start)
                task.StartNoEarlierThan = begins;

            if (Value(element, "UID") is { } id) byUid[id] = task;
            links.Add((task, element));
            read++;
        }

        foreach (var (task, element) in links)
        {
            task.Predecessors.Clear();
            foreach (var link in element.Elements(ns + "PredecessorLink"))
                if (Value(link, "PredecessorUID") is { } before && byUid.TryGetValue(before, out var predecessor) && predecessor != task)
                {
                    var lag = int.TryParse(Value(link, "LinkLag"), out var tenths) ? (int)Math.Round(tenths / 10.0 / minutesPerDay) : 0;
                    task.Predecessors.Add(new TaskLink(predecessor.Id, lag));
                }
        }

        return read;
    }
}
