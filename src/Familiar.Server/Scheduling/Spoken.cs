using System.Globalization;
using Familiar.Domain.Calendars;
using Familiar.Domain.Scheduling;

namespace Familiar.Server.Scheduling;

/// <summary>Times and durations the way a person would say them - these go to the model and to speech.</summary>
public static class Spoken
{
    public static string Duration(int seconds)
    {
        if (seconds < 60)
        {
            return Plural(seconds, "second");
        }

        var span = TimeSpan.FromSeconds(seconds);
        var parts = new List<string>();
        if (span.Days > 0)
        {
            parts.Add(Plural(span.Days, "day"));
        }

        if (span.Hours > 0)
        {
            parts.Add(Plural(span.Hours, "hour"));
        }

        if (span.Minutes > 0)
        {
            parts.Add(Plural(span.Minutes, "minute"));
        }

        if (span.Seconds > 0 && span.TotalMinutes < 10)
        {
            parts.Add(Plural(span.Seconds, "second"));
        }

        return string.Join(" ", parts);
    }

    /// <summary>
    /// "at 6:40 PM", "tomorrow at 9:00 AM", "on Saturday 10 October at 9:00 AM" - in the user's time
    /// zone. A 12-hour clock: given "23:55" the model said "eleven minutes past".
    /// </summary>
    public static string When(DateTimeOffset utc, TimeZoneInfo zone, DateTimeOffset now)
    {
        var local = TimeZoneInfo.ConvertTime(utc, zone);
        var today = TimeZoneInfo.ConvertTime(now, zone).Date;
        var time = local.ToString("h:mm tt", CultureInfo.InvariantCulture);

        return (local.Date - today).Days switch
        {
            0 => $"at {time}",
            1 => $"tomorrow at {time}",
            > 1 and < 7 => $"on {local:dddd} at {time}",
            _ => string.Create(CultureInfo.InvariantCulture, $"on {local:dddd d MMMM} at {time}"),
        };
    }

    /// <summary>
    /// When an event is, relative to now: "in 25 minutes", "now, until 3:00 PM", "tomorrow at 10:00 AM",
    /// "today (all day)".
    /// </summary>
    public static string EventWhen(CalendarEvent e, TimeZoneInfo zone, DateTimeOffset now)
    {
        if (e.AllDay)
        {
            // Stored as UTC midnight of its date: the date is what matters, not an instant.
            var today = TimeZoneInfo.ConvertTime(now, zone).Date;
            var days = (e.StartsAt.UtcDateTime.Date - today).Days;
            var lasts = (e.EndsAt - e.StartsAt).TotalDays >= 2 ? $", for {(int)(e.EndsAt - e.StartsAt).TotalDays} days" : "";
            return days switch
            {
                < 0 when e.EndsAt.UtcDateTime.Date > today => $"all day, until {e.EndsAt.UtcDateTime.AddDays(-1):dddd}",
                0 => $"today (all day{lasts})",
                1 => $"tomorrow (all day{lasts})",
                > 1 and < 7 => $"on {e.StartsAt.UtcDateTime:dddd} (all day{lasts})",
                _ => string.Create(CultureInfo.InvariantCulture, $"on {e.StartsAt.UtcDateTime:dddd d MMMM} (all day{lasts})"),
            };
        }

        if (e.StartsAt <= now && e.EndsAt > now)
        {
            return $"now, until {TimeZoneInfo.ConvertTime(e.EndsAt, zone).ToString("h:mm tt", CultureInfo.InvariantCulture)}";
        }

        var until = e.StartsAt - now;
        if (until > TimeSpan.Zero && until < TimeSpan.FromMinutes(90))
        {
            return $"in {Duration((int)Math.Max(60, Math.Round(until.TotalMinutes) * 60))}";
        }

        return When(e.StartsAt, zone, now);
    }

    /// <summary>Time left, rounded the way it's said: "40 seconds", "10 minutes", "1 hour 5 minutes".</summary>
    public static string Remaining(TimeSpan left) =>
        left.TotalSeconds < 90
            ? Duration((int)Math.Max(1, Math.Round(left.TotalSeconds)))
            : Duration((int)Math.Round(left.TotalMinutes) * 60);

    /// <summary>Midnight at the start of a local date, as an instant.</summary>
    public static DateTimeOffset StartOfDay(DateTime date, TimeZoneInfo zone)
    {
        var local = DateTime.SpecifyKind(date.Date, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(local))
        {
            local = local.AddMinutes(30);
        }

        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone), TimeSpan.Zero);
    }

    /// <summary>"3 days", "yesterday"-ish: how long ago, roughly, as a person would say it.</summary>
    public static string Ago(TimeSpan span) => span switch
    {
        { TotalHours: < 1 } => "a few minutes",
        { TotalHours: < 2 } => "about an hour",
        { TotalHours: < 24 } => $"about {(int)span.TotalHours} hours",
        { TotalDays: < 2 } => "a day",
        { TotalDays: < 14 } => $"{(int)span.TotalDays} days",
        { TotalDays: < 60 } => $"{(int)(span.TotalDays / 7)} weeks",
        _ => $"{(int)(span.TotalDays / 30)} months",
    };

    /// <summary>What the familiar says when a job fires. Late jobs (fired after a restart) admit it.</summary>
    public static string Announcement(ScheduledJob job, DateTimeOffset now)
    {
        var late = now - job.DueAt;
        var lateBy = late > TimeSpan.FromMinutes(1) ? Duration((int)late.TotalSeconds) : null;

        return job.Kind switch
        {
            JobKind.Timer when lateBy is null => $"Your {TimerName(job)} is done!",
            JobKind.Timer => $"Your {TimerName(job)} went off while I was away, about {lateBy} ago.",
            _ when lateBy is null => $"Here's your reminder: {job.Label}.",
            _ => $"Sorry, I was away when this was due {lateBy} ago - here's your reminder: {job.Label}.",
        };
    }

    /// <summary>The time zone the job was set in, or UTC if it's unknown on this machine.</summary>
    public static TimeZoneInfo ZoneOf(ScheduledJob job) =>
        job.TimeZone is { } id && TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone) ? zone : TimeZoneInfo.Utc;

    /// <summary>"pasta timer", "10 minute timer", or just "timer".</summary>
    private static string TimerName(ScheduledJob job)
    {
        var label = job.Label.Trim();
        if (label.Length > 0 && !label.Equals("timer", StringComparison.OrdinalIgnoreCase))
        {
            return label.EndsWith("timer", StringComparison.OrdinalIgnoreCase) ? label : $"{label} timer";
        }

        if (job.DurationSeconds is not { } seconds)
        {
            return "timer";
        }

        // "10 minutes" → "10 minute timer".
        var length = Duration(seconds);
        foreach (var unit in new[] { "day", "hour", "minute", "second" })
        {
            length = length.Replace(unit + "s", unit, StringComparison.Ordinal);
        }

        return $"{length} timer";
    }

    private static string Plural(int n, string unit) =>
        string.Create(CultureInfo.InvariantCulture, $"{n} {unit}{(n == 1 ? "" : "s")}");
}
