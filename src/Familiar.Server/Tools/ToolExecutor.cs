using System.Collections.Concurrent;
using Familiar.Agent.Tools;
using Familiar.Domain.Tools;
using Familiar.Persistence;
using Familiar.Server.Conversation;
using Microsoft.EntityFrameworkCore;

namespace Familiar.Server.Tools;

/// <summary>The familiar wants to do something outside Familiar and is asking first.</summary>
/// <param name="ReplyId">The reply it's part of.</param>
/// <param name="CallId">Answer with this.</param>
/// <param name="Tool">The tool's name.</param>
/// <param name="Description">What it will do, for the user to allow or not.</param>
/// <param name="ExpiresAt">After this it counts as declined.</param>
public sealed record ToolConfirmEvent(Guid ReplyId, string CallId, string Tool, string Description, DateTimeOffset ExpiresAt);

public sealed record ToolStartedEvent(Guid ReplyId, string CallId, string Tool);

public sealed record ToolFinishedEvent(Guid ReplyId, string CallId, string Tool, bool Ok, string? Summary);

/// <summary>A confirmation request was answered (by any device), so the others can put theirs away.</summary>
public sealed record ToolConfirmedEvent(string CallId, bool Approved);

/// <summary>
/// Confirmations in flight: a turn waits here while the user decides (PLAN.md §6.1). Answered from
/// any of the account's devices; unanswered within <see cref="Timeout"/> means no.
/// </summary>
public sealed class ToolConfirmations(ConversationHub hub, TimeProvider clock)
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private readonly ConcurrentDictionary<(Guid Account, string CallId), TaskCompletionSource<bool>> _pending = new();

    public async Task<bool> AskAsync(ToolContext context, ToolCall call, string description, CancellationToken ct)
    {
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var key = (context.AccountId, call.Id);
        _pending[key] = answer;

        var channel = hub.For(context.AccountId);
        channel.Publish(EventTypes.ToolConfirm,
            new ToolConfirmEvent(context.ReplyId, call.Id, call.Name, description, clock.GetUtcNow() + Timeout));

        try
        {
            return await answer.Task.WaitAsync(Timeout, clock, ct);
        }
        catch (TimeoutException)
        {
            channel.Publish(EventTypes.ToolConfirmed, new ToolConfirmedEvent(call.Id, false));
            return false;
        }
        finally
        {
            _pending.TryRemove(key, out _);
        }
    }

    /// <summary>False when there's no such question waiting (already answered, or expired).</summary>
    public bool Answer(Guid accountId, string callId, bool approve)
    {
        if (!_pending.TryRemove((accountId, callId), out var answer))
        {
            return false;
        }

        answer.TrySetResult(approve);
        hub.For(accountId).Publish(EventTypes.ToolConfirmed, new ToolConfirmedEvent(callId, approve));
        return true;
    }
}

/// <summary>
/// Runs the familiar's tools for a turn: looks the tool up, asks the user first if it reaches outside
/// Familiar, turns failures into something the model can explain, and records every call.
/// </summary>
public sealed class ToolExecutor(
    IEnumerable<ITool> tools,
    ToolConfirmations confirmations,
    IDbContextFactory<FamiliarDbContext> db,
    TimeProvider clock,
    ILogger<ToolExecutor> logger) : IToolExecutor
{
    private readonly Dictionary<string, ITool> _tools = tools.ToDictionary(t => t.Definition.Name, StringComparer.Ordinal);

    public IReadOnlyList<ToolDefinition> Definitions => [.. _tools.Values.Select(t => t.Definition)];

    public async Task<ToolResult> ExecuteAsync(ToolCall call, ToolContext context, CancellationToken ct)
    {
        if (!_tools.TryGetValue(call.Name, out var tool))
        {
            logger.UnknownTool(call.Name);
            return ToolResult.Failure($"There's no tool called {call.Name}.");
        }

        var started = clock.GetTimestamp();
        bool? approved = null;
        ToolResult result;
        ToolOutcome outcome;

        try
        {
            if (tool.Definition.Risk == ToolRisk.WriteExternal)
            {
                approved = await confirmations.AskAsync(context, call, tool.Describe(call.Arguments), ct);
            }

            if (approved == false)
            {
                result = ToolResult.Failure("The user didn't allow it. Don't try again unless they ask.", "Not allowed");
                outcome = ToolOutcome.Declined;
            }
            else
            {
                result = await tool.InvokeAsync(context, call.Arguments, ct);
                outcome = result.Ok ? ToolOutcome.Succeeded : ToolOutcome.Failed;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.ToolFailed(exception, call.Name);
            result = ToolResult.Failure("Something went wrong running that. Apologise and suggest trying again.", "Didn't work");
            outcome = ToolOutcome.Failed;
        }

        var elapsed = (long)clock.GetElapsedTime(started).TotalMilliseconds;
        logger.ToolRan(call.Name, outcome, elapsed);

        // The audit record survives an interrupted turn: the tool ran whether or not the reply finished.
        await using var audit = await db.CreateDbContextAsync(CancellationToken.None);
        audit.ToolInvocations.Add(new ToolInvocation
        {
            AccountId = context.AccountId,
            ReplyId = context.ReplyId,
            Tool = call.Name,
            Risk = tool.Definition.Risk,
            Arguments = call.Arguments.ToJsonString(),
            Result = result.Data.ToJsonString(),
            Outcome = outcome,
            Approved = approved,
            DurationMs = elapsed,
            CreatedAt = clock.GetUtcNow(),
        });
        await audit.SaveChangesAsync(CancellationToken.None);

        return result;
    }
}

/// <summary>Registration of the tools and what runs them.</summary>
public static class ToolRegistration
{
    public static IServiceCollection AddFamiliarTools(this IServiceCollection services)
    {
        services.AddSingleton<ToolConfirmations>();
        services.AddSingleton<ITool, Scheduling.SetTimerTool>();
        services.AddSingleton<ITool, Scheduling.CreateReminderTool>();
        services.AddSingleton<ITool, Scheduling.ListScheduledTool>();
        services.AddSingleton<ITool, Scheduling.CancelScheduledTool>();
        services.AddSingleton<ITool, Calendars.ListEventsTool>();
        services.AddSingleton<ITool, Calendars.CreateEventTool>();
        services.AddSingleton<ITool, Memories.RememberTool>();
        services.AddSingleton<ITool, Memories.ForgetTool>();
        services.AddSingleton<IToolExecutor, ToolExecutor>();
        return services;
    }

    /// <summary>For the logs: what the familiar can do.</summary>
    public static string Describe(IToolExecutor executor) =>
        string.Join(", ", executor.Definitions.Select(d => $"{d.Name} ({d.Risk})"));
}
