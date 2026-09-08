using CSweet.WorkManagement.Contracts;

namespace CSweet.Infrastructure.WorkManagement;

public static class CalendarRecurrenceEngine
{
    public static TimeZoneInfo Zone(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
        { throw new ArgumentException("Select a valid time zone.", nameof(id)); }
    }

    // A skipped wall-clock time advances to the first valid minute; a repeated time uses its earlier instant.
    public static DateTimeOffset ToInstant(DateTime local, string zoneId)
    {
        var zone = Zone(zoneId);
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(local)) local = local.AddMinutes(1);
        var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }

    public static IEnumerable<DateTime> Starts(CalendarEventInput input, DateTime through)
    {
        var start = DateTime.SpecifyKind(input.StartLocal, DateTimeKind.Unspecified);
        var rule = input.Recurrence;
        var produced = 0;
        for (var index = 0; ; index++)
        {
            DateTime next;
            try
            {
                var step = checked(index * (rule?.Interval ?? 1));
                next = rule?.Frequency switch
                {
                    "Daily" => start.AddDays(step),
                    "Weekly" => start.AddDays(checked(step * 7)),
                    "Monthly" => start.AddMonths(step),
                    "Yearly" => start.AddYears(step),
                    _ => start
                };
            }
            catch (ArgumentOutOfRangeException) { yield break; }
            catch (OverflowException) { yield break; }
            if (next > through || (rule?.Until is { } until && DateOnly.FromDateTime(next) > until)) yield break;
            // RFC-style month/year recurrence skips nonexistent dates (31st, leap day).
            if (rule?.Frequency is "Monthly" or "Yearly" && next.Day != start.Day) continue;
            if (rule?.Count is { } count && produced >= count) yield break;
            produced++;
            yield return next;
            if (rule is null) yield break;
        }
    }

    public static CalendarEventInput At(CalendarEventInput input, DateTime local) => input with
    { StartLocal = local, EndLocal = local + (input.EndLocal - input.StartLocal), Recurrence = null };

    public static void Validate(CalendarEventInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Title) || input.Title.Length > 300 || input.Description?.Length > 16000 || input.Location?.Length > 1000)
            throw new ArgumentException("A title of up to 300 characters is required; description/location are limited to 16000/1000 characters.");
        if (input.StartLocal.Kind != DateTimeKind.Unspecified || input.EndLocal.Kind != DateTimeKind.Unspecified)
            throw new ArgumentException("Start and end must be local wall-clock values without a UTC offset.");
        if (input.StartLocal.Year < 1900 || input.EndLocal.Year > 2200 || input.EndLocal <= input.StartLocal || input.EndLocal - input.StartLocal > TimeSpan.FromDays(366))
            throw new ArgumentException("Use a positive event duration of at most 366 days, between 1900 and 2200.");
        Zone(input.TimeZoneId);
        if (input.AllDay && (input.StartLocal.TimeOfDay != TimeSpan.Zero || input.EndLocal.TimeOfDay != TimeSpan.Zero))
            throw new ArgumentException("All-day events use midnight dates and an exclusive end date.");
        if (ToInstant(input.EndLocal, input.TimeZoneId) <= ToInstant(input.StartLocal, input.TimeZoneId))
            throw new ArgumentException("The event must end after it starts in its time zone.");
        if (input.Recurrence is { } r && (r.Frequency is not ("Daily" or "Weekly" or "Monthly" or "Yearly") || r.Interval is < 1 or > 1000 || r.Count is <= 0 or > 100000 || (r.Until.HasValue && r.Until.Value < DateOnly.FromDateTime(input.StartLocal))))
            throw new ArgumentException("Invalid recurrence frequency, interval, count or end date.");
        if (input.AttendeeIds?.Count > 200 || input.ReminderMinutes?.Count > 5 || input.ReminderMinutes?.Any(x => x < 0 || x > 43200) == true)
            throw new ArgumentException("Use at most 200 attendees and five reminders between zero and 43200 minutes before start.");
        if (input.Work is { } work && (work.TargetOrganizationUserId == Guid.Empty || work.Kind is not ("Instructions" or "ExistingItem") ||
            (work.Kind == "Instructions" && (string.IsNullOrWhiteSpace(work.Instructions) || work.Instructions.Length > 16000 || work.ItemId is not null)) ||
            (work.Kind == "ExistingItem" && (work.ItemId is null || input.Recurrence is not null))))
            throw new ArgumentException("Work requires a target and instructions, or a one-time existing work item.");
    }
}
