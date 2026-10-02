using Ical.Net.DataTypes;
using IcalCalendar = Ical.Net.Calendar;
using IcalEvent = Ical.Net.CalendarComponents.CalendarEvent;

namespace Familiar.Server.Calendars;

/// <summary>One occurrence read from a feed, before it's stored.</summary>
/// <param name="ExternalId">The event's UID and this occurrence's start: stable across syncs.</param>
/// <param name="Title">The event's summary.</param>
/// <param name="Location">Where, if the feed says.</param>
/// <param name="StartsAt">When it starts (for an all-day event, UTC midnight of its date).</param>
/// <param name="EndsAt">When it ends.</param>
/// <param name="AllDay">A whole-day event.</param>
public sealed record FeedOccurrence(string ExternalId, string Title, string? Location, DateTimeOffset StartsAt, DateTimeOffset EndsAt, bool AllDay);

/// <summary>
/// Reads an iCalendar feed into occurrences over a window: recurring events are expanded (RRULE,
/// EXDATE, moved instances), time zones resolved, so what's stored is just "this, then".
/// </summary>
public static class IcsFeed
{
    /// <summary>A runaway RRULE ("every minute") stops here rather than filling the table.</summary>
    public const int MaxOccurrences = 2000;

    /// <exception cref="FormatException">It isn't an iCalendar file.</exception>
    public static IReadOnlyList<FeedOccurrence> Read(string text, DateTimeOffset from, DateTimeOffset to)
    {
        IcalCalendar? calendar;
        try
        {
            calendar = IcalCalendar.Load(text);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw new FormatException("That isn't an iCalendar (.ics) feed.", exception);
        }

        if (calendar is null)
        {
            throw new FormatException("That isn't an iCalendar (.ics) feed.");
        }

        var occurrences = new List<FeedOccurrence>();
        foreach (var occurrence in calendar.GetOccurrences<IcalEvent>(new CalDateTime(from.UtcDateTime, CalDateTime.UtcTzId)))
        {
            var start = occurrence.Period.StartTime;
            if (start.AsUtc >= to.UtcDateTime || occurrences.Count >= MaxOccurrences)
            {
                break;
            }

            if (occurrence.Source is not IcalEvent source)
            {
                continue;
            }

            var allDay = !start.HasTime;
            var startsAt = allDay
                ? new DateTimeOffset(start.Value.Date, TimeSpan.Zero)
                : new DateTimeOffset(start.AsUtc, TimeSpan.Zero);

            var end = occurrence.Period.EffectiveEndTime;
            var endsAt = end is null
                ? startsAt + (allDay ? TimeSpan.FromDays(1) : TimeSpan.FromHours(1))
                : allDay ? new DateTimeOffset(end.Value.Date, TimeSpan.Zero) : new DateTimeOffset(end.AsUtc, TimeSpan.Zero);

            // Ended before the window: GetOccurrences starts from the start time, so a long event
            // already under way is included, but one that's over isn't wanted.
            if (endsAt <= from)
            {
                continue;
            }

            var title = string.IsNullOrWhiteSpace(source.Summary) ? "(untitled)" : source.Summary.Trim();
            occurrences.Add(new FeedOccurrence(
                $"{source.Uid}|{startsAt:O}",
                Cap(title, Domain.Calendars.CalendarEvent.MaxTitleLength)!,
                Cap(string.IsNullOrWhiteSpace(source.Location) ? null : source.Location.Trim(), Domain.Calendars.CalendarEvent.MaxTitleLength),
                startsAt,
                endsAt < startsAt ? startsAt : endsAt,
                allDay));
        }

        return occurrences;
    }

    private static string? Cap(string? text, int max) => text is null || text.Length <= max ? text : text[..max];
}
