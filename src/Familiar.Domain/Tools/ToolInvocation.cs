namespace Familiar.Domain.Tools;

/// <summary>How much a tool can change, and so how carefully it's run (PLAN.md §6.1).</summary>
public enum ToolRisk
{
    /// <summary>Only looks: runs immediately.</summary>
    Read = 0,

    /// <summary>Changes the user's own Familiar data (timers, reminders): runs, announced, undoable.</summary>
    WriteOwn = 1,

    /// <summary>Reaches outside Familiar (a calendar, a webhook): asks the user first.</summary>
    WriteExternal = 2,
}

public enum ToolOutcome
{
    Succeeded = 0,
    Failed = 1,

    /// <summary>The user said no (or didn't answer) when asked to confirm.</summary>
    Declined = 2,
}

/// <summary>The audit record of one tool call: what the model asked for and what happened.</summary>
public sealed class ToolInvocation
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public Guid AccountId { get; init; }

    /// <summary>The reply it was part of.</summary>
    public Guid? ReplyId { get; init; }

    public required string Tool { get; init; }

    public ToolRisk Risk { get; init; }

    /// <summary>The arguments as the model sent them (JSON).</summary>
    public required string Arguments { get; init; }

    /// <summary>What the tool returned to the model (JSON).</summary>
    public string? Result { get; set; }

    public ToolOutcome Outcome { get; set; }

    /// <summary>For confirmed tools: whether the user approved, and when.</summary>
    public bool? Approved { get; set; }

    public long DurationMs { get; set; }

    public DateTimeOffset CreatedAt { get; init; }
}
