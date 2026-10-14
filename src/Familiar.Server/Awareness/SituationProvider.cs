using Familiar.Domain.Scheduling;
using Familiar.Server.Calendars;
using Familiar.Server.Conversation;
using Familiar.Server.Memories;
using Familiar.Server.Scheduling;

namespace Familiar.Server.Awareness;

public sealed class AwarenessOptions
{
    public const string Section = "Awareness";

    /// <summary>Let familiars greet first at all; each user can also switch it off for theirs.</summary>
    public bool Greet { get; init; } = true;
}

/// <summary>What the familiar knows going into a turn, beyond the conversation itself.</summary>
/// <param name="Notes">Short sentences for the situation block: what's coming up, what's running, how long it's been.</param>
/// <param name="Memories">What it remembers about the user.</param>
/// <param name="SinceLastTalked">How long since the previous message, if there was one.</param>
/// <param name="SomethingSoon">An event or reminder in the next few hours - worth bringing up unprompted.</param>
public sealed record Situation(IReadOnlyList<string> Notes, IReadOnlyList<string> Memories, TimeSpan? SinceLastTalked, bool SomethingSoon);

/// <summary>
/// Assembles the situation for a turn (PLAN.md §6.3) from the calendar, the scheduler, the
/// conversation and memory. Everything is read straight from the database: none of it depends on
/// the model deciding to call a tool.
/// </summary>
public sealed class SituationProvider(
    CalendarStore calendars,
    Scheduler scheduler,
    ConversationStore conversations,
    MemoryStore memories,
    LookUps.Weather weather)
{
    /// <summary>How far ahead the calendar is looked at.</summary>
    internal static readonly TimeSpan Horizon = TimeSpan.FromHours(36);

    /// <summary>Within this, an event counts as "soon" - something a greeting should mention.</summary>
    internal static readonly TimeSpan Soon = TimeSpan.FromHours(4);

    /// <summary>The most memories that go into a prompt; the newest are kept.</summary>
    internal const int MaxMemories = 60;

    /// <summary>A gap this long is worth mentioning ("You last talked 3 days ago").</summary>
    private static readonly TimeSpan NotableGap = TimeSpan.FromHours(6);

    /// <summary>
    /// After this long apart - the greeting's gap - it's catching up: what's on the calendar and
    /// today's weather are worth knowing.
    /// </summary>
    internal static readonly TimeSpan CatchUpGap = TimeSpan.FromHours(3);

    /// <summary>Something starting within this is about to happen: worth a word even mid-conversation.</summary>
    internal static readonly TimeSpan Imminent = TimeSpan.FromMinutes(30);

    /// <param name="accountId">Whose familiar.</param>
    /// <param name="zone">Their time zone.</param>
    /// <param name="now">The turn's time.</param>
    /// <param name="messagesBefore">The message being answered, if any, so the gap is to the one before it.</param>
    /// <param name="weatherAt">
    /// Where they live, when they let it look things up: then after a while apart (a greeting, the
    /// first thing said in a morning) the notes have today's weather there.
    /// </param>
    /// <param name="ct">Cancels it.</param>
    public async Task<Situation> ForAsync(
        Guid accountId, TimeZoneInfo zone, DateTimeOffset now, DateTimeOffset messagesBefore, CancellationToken ct, string? weatherAt = null)
    {
        var notes = new List<string>();
        var soon = false;

        TimeSpan? since = await conversations.LastMessageAtAsync(accountId, messagesBefore, ct) is { } last ? now - last : null;
        var catchingUp = since is null || since >= CatchUpGap;
        DateTimeOffset? lastSpoke = since is { } gap ? now - gap : null;

        // The notes come with every message and a model reads them as news: told about the same
        // appointment each turn, it brought it up each turn. So the calendar and reminders are in them
        // when catching up after a while apart, and once more when something is about to start;
        // otherwise list_events answers questions about the calendar.
        var events = await calendars.BetweenAsync(accountId, now, now + Horizon, ct);
        soon |= events.Any(e => !e.AllDay && e.StartsAt - now < Soon);
        var upcoming = events
            .Where(e => catchingUp
                ? !e.AllDay || e.StartsAt.UtcDateTime.Date <= TimeZoneInfo.ConvertTime(now, zone).Date.AddDays(1)
                : !e.AllDay && JustImminent(e.StartsAt, now, lastSpoke))
            .Take(3)
            .Select(e => $"{e.Title}, {Spoken.EventWhen(e, zone, now)}")
            .ToList();
        if (upcoming.Count > 0)
        {
            notes.Add(catchingUp ? $"On their calendar: {string.Join("; ", upcoming)}." : $"Starting soon on their calendar: {string.Join("; ", upcoming)}.");
        }

        // And what's been and gone since this morning: an appointment talked about yesterday as
        // "tomorrow" was greeted that evening as still to come (BACKLOG item 31).
        if (catchingUp)
        {
            var today = TimeZoneInfo.ConvertTime(now, zone).Date;
            var startOfDay = new DateTimeOffset(today, zone.GetUtcOffset(today)).ToUniversalTime();
            var over = (await calendars.BetweenAsync(accountId, startOfDay, now, ct))
                .Where(e => !e.AllDay && e.StartsAt >= startOfDay && e.EndsAt <= now)
                .TakeLast(3)
                .Select(e => $"{e.Title} {Spoken.When(e.StartsAt, zone, now)}")
                .ToList();
            if (over.Count > 0)
            {
                notes.Add($"Already over today: {string.Join("; ", over)}.");
            }
        }

        var pending = await scheduler.PendingAsync(accountId, null, ct);
        var running = pending
            .Where(j => j.Kind == JobKind.Timer || (catchingUp ? j.DueAt - now < Soon : JustImminent(j.DueAt, now, lastSpoke)))
            .Take(4)
            .Select(j => j.Kind == JobKind.Timer
                ? $"{(j.Label.Length > 0 ? j.Label + " timer" : "a timer")}, {Spoken.Remaining(j.DueAt - now)} left"
                : $"a reminder to {j.Label.TrimEnd('.')} {Spoken.When(j.DueAt, zone, now)}")
            .ToList();
        soon |= pending.Any(j => j.Kind == JobKind.Reminder && j.DueAt - now < Soon);
        if (running.Count > 0)
        {
            notes.Add($"Running: {string.Join("; ", running)}.");
        }

        if (since is null)
        {
            notes.Add("This is the first time you've talked.");
        }
        else if (since >= NotableGap)
        {
            notes.Add($"You last talked {Spoken.Ago(since.Value)} ago.");
        }

        if (!string.IsNullOrWhiteSpace(weatherAt) && catchingUp
            && await weather.TodayNoteAsync(weatherAt, ct) is { } forecast)
        {
            notes.Add(forecast);
        }

        var remembered = await memories.ListAsync(accountId, ct);
        var memoryLines = remembered.TakeLast(MaxMemories).Select(m => m.Text).ToList();

        return new Situation(notes, memoryLines, since, soon);
    }

    /// <summary>
    /// Whether something at <paramref name="at"/> has come within <see cref="Imminent"/> since they
    /// last spoke: true for one turn, not every turn until it starts.
    /// </summary>
    internal static bool JustImminent(DateTimeOffset at, DateTimeOffset now, DateTimeOffset? lastSpoke) =>
        at - now <= Imminent && (lastSpoke is null || at - lastSpoke.Value > Imminent);
}
