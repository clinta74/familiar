namespace Familiar.Domain.Scheduling;

public enum JobKind
{
    Timer = 0,
    Reminder = 1,
}

public enum JobStatus
{
    Pending = 0,
    Fired = 1,
    Cancelled = 2,
}

/// <summary>
/// Something the familiar has promised to do at a time: ring a timer, give a reminder (PLAN.md §6.2).
/// Stored, so it survives a restart; a job that came due while the server was down fires on startup,
/// late, and says so.
/// </summary>
public sealed class ScheduledJob
{
    public const int MaxLabelLength = 200;

    public Guid Id { get; init; } = Guid.CreateVersion7();

    public Guid AccountId { get; init; }

    public JobKind Kind { get; init; }

    /// <summary>What it's for: "pasta", "call Mum".</summary>
    public required string Label { get; set; }

    public DateTimeOffset DueAt { get; set; }

    /// <summary>For timers: how long it was set for, so it can be announced as "your 10-minute timer".</summary>
    public int? DurationSeconds { get; init; }

    /// <summary>
    /// A reminder that rings like an alarm until it's dismissed ("wake me at 7"), rather than a
    /// notification. Timers always ring.
    /// </summary>
    public bool Ring { get; init; }

    /// <summary>The user's time zone when it was set, for speaking times back in their terms.</summary>
    public string? TimeZone { get; init; }

    public JobStatus Status { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? FiredAt { get; set; }
}
