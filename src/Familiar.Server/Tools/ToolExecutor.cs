using System.Collections.Concurrent;
using System.Text.Json.Nodes;
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
    Hooks.WebhookDispatcher webhooks,
    Hooks.McpTools mcp,
    Home.HomeAssistant home,
    Devices.DeviceTools devices,
    TimeProvider clock,
    ILogger<ToolExecutor> logger) : IToolExecutor
{
    private readonly Dictionary<string, ITool> _tools = tools.ToDictionary(t => t.Definition.Name, StringComparer.Ordinal);

    /// <summary>At most this many look-ups a reply: enough to answer, not enough to go wandering.</summary>
    public const int MaxLookUpsPerReply = 2;

    /// <summary>The built-in tools, the same for everyone.</summary>
    public IReadOnlyList<ToolDefinition> BuiltIn => [.. _tools.Values.Select(t => t.Definition)];

    /// <summary>
    /// The built-in tools - the look-ups only for a user who's turned them on, the phone's only while
    /// a phone that can do them is connected (or a browser can say where it is, with look-ups on) -
    /// then their Home Assistant and MCP tools.
    /// </summary>
    public async Task<IReadOnlyList<ToolDefinition>> DefinitionsAsync(ToolContext context, CancellationToken ct) =>
    [
        .. _tools.Values
            .Where(t => t.Definition.Risk != ToolRisk.LookUp || context.LookUps)
            .Where(t => t is not Devices.IPhoneTool phone || devices.Find(context.AccountId, phone.Capability, context.From, browsers: context.LookUps) is not null)
            .Select(t => t.Definition),
        .. (await AccountToolsAsync(context.AccountId, ct)).Select(t => t.Definition),
    ];

    /// <summary>The tools that come from what the account has connected: Home Assistant, MCP servers.</summary>
    private async Task<IReadOnlyList<ITool>> AccountToolsAsync(Guid accountId, CancellationToken ct) =>
        [.. await home.ToolsForAsync(accountId, ct), .. await mcp.ToolsForAsync(accountId, ct)];

    public async Task<ToolResult> ExecuteAsync(ToolCall call, ToolContext context, CancellationToken ct)
    {
        var tool = _tools.GetValueOrDefault(call.Name)
            ?? (await AccountToolsAsync(context.AccountId, ct)).FirstOrDefault(t => t.Definition.Name == call.Name);
        if (tool is null)
        {
            logger.UnknownTool(call.Name);
            return ToolResult.Failure($"There's no tool called {call.Name}.");
        }

        var started = clock.GetTimestamp();
        bool? approved = null;
        ToolResult result;
        ToolOutcome outcome;

        var risk = tool is IRiskPerCall perCall ? perCall.RiskOf(call.Arguments) : tool.Definition.Risk;
        if (risk == ToolRisk.LookUp && !context.LookUps)
        {
            return ToolResult.Failure("Looking things up is turned off. Say you can't look that up; the user can turn it on in Settings.");
        }

        if (risk == ToolRisk.LookUp && context.Turn.LookUps >= MaxLookUpsPerReply)
        {
            return ToolResult.Failure("That's enough looking up for one reply. Answer with what you have.");
        }

        try
        {
            // After a look-up, outside text has been in the conversation: anything that changes
            // something asks first, so the text can't quietly make it act.
            if (risk == ToolRisk.WriteExternal || (risk == ToolRisk.WriteOwn && context.Turn.LookUps > 0))
            {
                approved = await confirmations.AskAsync(context, call, tool.Describe(call.Arguments), ct);
            }

            if (risk == ToolRisk.LookUp)
            {
                context.Turn.LookUps++;
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
            Risk = risk,
            Arguments = call.Arguments.ToJsonString(),
            Result = result.Data.ToJsonString(),
            Outcome = outcome,
            Approved = approved,
            DurationMs = elapsed,
            CreatedAt = clock.GetUtcNow(),
        });
        await audit.SaveChangesAsync(CancellationToken.None);

        webhooks.Raise(context.AccountId, Hooks.WebhookEvents.ToolUsed, new
        {
            tool = call.Name,
            outcome = outcome.ToString().ToLowerInvariant(),
            summary = result.Summary,
        });

        return result;
    }
}

/// <summary>A tool whose risk depends on what it's asked to do: reading the house, or unlocking its door.</summary>
public interface IRiskPerCall
{
    ToolRisk RiskOf(JsonObject arguments);
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

        // Look-ups (BACKLOG item 15): fixed outside services, and nowhere else.
        services.AddMemoryCache();
        services.AddTransient<LookUps.LookUpHosts>();
        services.AddHttpClient(nameof(LookUps), http =>
        {
            http.Timeout = TimeSpan.FromSeconds(8);
            // Wikimedia asks every client to say who it is and where to find out more; a generic
            // agent is rate-limited (429). The project, not the user: nothing personal goes out.
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Familiar/1.0 (https://github.com/clinta74/familiar; self-hosted household assistant)");
        }).AddHttpMessageHandler<LookUps.LookUpHosts>();
        services.AddSingleton<LookUps.Weather>();
        services.AddSingleton<ITool, LookUps.WeatherTool>();
        services.AddSingleton<ITool, LookUps.DefineTool>();
        services.AddSingleton<ITool, LookUps.LookUpTool>();
        services.AddSingleton<ITool, LookUps.HolidaysTool>();
        services.AddSingleton<ITool, LookUps.AirQualityTool>();
        services.AddSingleton<ITool, LookUps.ExchangeRateTool>();

        // Done by the phone, while one that can is connected (BACKLOG item 21).
        services.AddSingleton<ITool, Devices.SendTextTool>();
        services.AddSingleton<ITool, Devices.CallTool>();
        services.AddSingleton<ITool, Devices.NavigateTool>();
        services.AddSingleton<ITool, Devices.WhereAmITool>();
        services.AddSingleton<ITool, Devices.ContactDetailsTool>();

        // Worked out here, not looked up: offered whether or not look-ups are on.
        services.AddSingleton<ITool, Maths.CalculateTool>();
        services.AddSingleton<ITool, Maths.ConvertUnitsTool>();
        services.AddSingleton<ITool, Maths.DatesTool>();
        services.AddSingleton<IToolExecutor, ToolExecutor>();
        return services;
    }

    /// <summary>For the logs: what the familiar can do.</summary>
    public static string Describe(IToolExecutor executor) =>
        executor is ToolExecutor tools ? string.Join(", ", tools.BuiltIn.Select(d => $"{d.Name} ({d.Risk})")) : executor.GetType().Name;
}
