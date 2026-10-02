namespace Familiar.Domain.Calendars;

public enum CalendarKind
{
    /// <summary>Familiar's own calendar, in Postgres: read and write, works offline.</summary>
    Local = 0,

    /// <summary>A subscribed iCal feed (a "secret address" from Google, Outlook, iCloud…): read-only, polled.</summary>
    Ics = 1,
}

/// <summary>A calendar the familiar can see (PLAN.md §6.4).</summary>
public sealed class CalendarSource
{
    public const int MaxNameLength = 64;

    public Guid Id { get; init; } = Guid.CreateVersion7();

    public Guid AccountId { get; init; }

    public CalendarKind Kind { get; init; }

    public required string Name { get; set; }

    /// <summary>
    /// For a feed, its URL, encrypted (ASP.NET Data Protection): a secret iCal address is as good as
    /// a password to that calendar.
    /// </summary>
    public string? ProtectedUrl { get; set; }

    public DateTimeOffset? LastSyncedAt { get; set; }

    /// <summary>Why the last sync failed, for Settings to show; null when it worked.</summary>
    public string? LastError { get; set; }

    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>
/// One event, or one occurrence of a recurring one. Feeds are expanded into occurrences over a
/// rolling window and replaced wholesale on each sync, so nothing downstream needs to know RRULE.
/// </summary>
public sealed class CalendarEvent
{
    public const int MaxTitleLength = 300;

    public Guid Id { get; init; } = Guid.CreateVersion7();

    public Guid SourceId { get; init; }

    public Guid AccountId { get; init; }

    /// <summary>The feed's UID plus the occurrence's start, for a feed; null for Familiar's own.</summary>
    public string? ExternalId { get; init; }

    public required string Title { get; set; }

    public string? Location { get; set; }

    public DateTimeOffset StartsAt { get; set; }

    public DateTimeOffset EndsAt { get; set; }

    /// <summary>A whole-day event; its times are midnight to midnight in the user's zone.</summary>
    public bool AllDay { get; set; }

    public DateTimeOffset CreatedAt { get; init; }
}
