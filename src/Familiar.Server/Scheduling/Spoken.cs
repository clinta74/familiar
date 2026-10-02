using System.Globalization;
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
