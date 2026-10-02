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
    MemoryStore memories)
{
    /// <summary>How far ahead the calendar is looked at.</summary>
    internal static readonly TimeSpan Horizon = TimeSpan.FromHours(36);

    /// <summary>Within this, an event counts as "soon" - something a greeting should mention.</summary>
    internal static readonly TimeSpan Soon = TimeSpan.FromHours(4);

    /// <summary>The most memories that go into a prompt; the newest are kept.</summary>
    internal const int MaxMemories = 60;

    /// <summary>A gap this long is worth mentioning ("You last talked 3 days ago").</summary>
    private static readonly TimeSpan NotableGap = TimeSpan.FromHours(6);

    public async Task<Situation> ForAsync(Guid accountId, TimeZoneInfo zone, DateTimeOffset now, DateTimeOffset messagesBefore, CancellationToken ct)
    {
        var notes = new List<string>();
        var soon = false;

        var events = await calendars.BetweenAsync(accountId, now, now + Horizon, ct);
        var upcoming = events
            .Where(e => !e.AllDay || e.StartsAt.UtcDateTime.Date <= TimeZoneInfo.ConvertTime(now, zone).Date.AddDays(1))
            .Take(3)
            .Select(e => $"{e.Title}, {Spoken.EventWhen(e, zone, now)}")
            .ToList();
        if (upcoming.Count > 0)
        {
            notes.Add($"On their calendar: {string.Join("; ", upcoming)}.");
            soon |= events.Any(e => !e.AllDay && e.StartsAt - now < Soon);
        }

        var pending = await scheduler.PendingAsync(accountId, null, ct);
        var running = pending
            .Where(j => j.Kind == JobKind.Timer || j.DueAt - now < Soon)
            .Take(4)
            .Select(j => j.Kind == JobKind.Timer
                ? $"{(j.Label.Length > 0 ? j.Label + " timer" : "a timer")}, {Spoken.Remaining(j.DueAt - now)} left"
                : $"a reminder to {j.Label.TrimEnd('.')} {Spoken.When(j.DueAt, zone, now)}")
            .ToList();
        if (running.Count > 0)
        {
            notes.Add($"Running: {string.Join("; ", running)}.");
            soon |= pending.Any(j => j.Kind == JobKind.Reminder && j.DueAt - now < Soon);
        }

        TimeSpan? since = await conversations.LastMessageAtAsync(accountId, messagesBefore, ct) is { } last ? now - last : null;
        if (since is null)
        {
            notes.Add("This is the first time you've talked.");
        }
        else if (since >= NotableGap)
        {
            notes.Add($"You last talked {Spoken.Ago(since.Value)} ago.");
        }

        var remembered = await memories.ListAsync(accountId, ct);
        var memoryLines = remembered.TakeLast(MaxMemories).Select(m => m.Text).ToList();

        return new Situation(notes, memoryLines, since, soon);
    }
}
