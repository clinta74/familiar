using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Familiar.Server.Conversation;

namespace Familiar.Server.Devices;

/// <summary>What phones can do for the familiar, by name (PLAN.md §6.1).</summary>
public static class DeviceCapabilities
{
    /// <summary>Add events to the phone's own calendars - and so to Google, Outlook, whatever it syncs.</summary>
    public const string CalendarWrite = "calendar.write";

    public static readonly IReadOnlySet<string> Known = new HashSet<string>(StringComparer.Ordinal) { CalendarWrite };
}

/// <summary>A tool call for one particular phone to carry out.</summary>
/// <param name="CallId">Answer with this.</param>
/// <param name="ConnectionId">Which connection should act; every other device ignores it.</param>
/// <param name="Tool">What to do, e.g. "calendar.add".</param>
/// <param name="Arguments">Its arguments.</param>
/// <param name="ExpiresAt">After this, don't: the server has stopped waiting (a replayed invoke must not run twice).</param>
public sealed record ToolInvokeEvent(string CallId, string ConnectionId, string Tool, JsonObject Arguments, DateTimeOffset ExpiresAt);

/// <param name="CallId">The tool.invoke being answered.</param>
/// <param name="Ok">Whether it worked.</param>
/// <param name="Result">What happened, for the model (and the summary).</param>
/// <param name="Error">Why not, when it didn't.</param>
public sealed record ToolResultRequest(string? CallId, bool Ok, JsonObject? Result = null, string? Error = null);

/// <summary>What a phone answered.</summary>
public sealed record DeviceToolResult(bool Ok, JsonObject Result, string? Error);

/// <summary>A phone with the app open, and what it said it can do.</summary>
public sealed record DeviceSession(string ConnectionId, Guid AccountId, Guid DeviceId, IReadOnlySet<string> Capabilities, DateTimeOffset ConnectedAt);

/// <summary>
/// Device-side tools (PLAN.md §6.1): some things only the phone can do - write to its calendars,
/// ring an alarm. A phone lists what it can do when it opens the event stream; while it's
/// connected, the server can ask it to do one of those things with a <c>tool.invoke</c> event and
/// wait (briefly) for the <c>tool-result</c> it posts back.
/// </summary>
public sealed class DeviceTools(ConversationHub hub, TimeProvider clock, IConfiguration configuration)
{
    /// <summary>
    /// How long to wait for a phone: a backgrounded or out-of-signal phone fails the call rather
    /// than hanging the turn. Devices:ToolTimeoutSeconds.
    /// </summary>
    public TimeSpan Timeout { get; } = TimeSpan.FromSeconds(Math.Max(1, configuration.GetValue("Devices:ToolTimeoutSeconds", 15)));

    private readonly ConcurrentDictionary<string, DeviceSession> _sessions = new();
    private readonly ConcurrentDictionary<(Guid Account, string CallId), TaskCompletionSource<DeviceToolResult>> _pending = new();

    /// <summary>A phone opened the stream. Dispose the result when it closes.</summary>
    public (DeviceSession Session, IDisposable Registration) Connect(Guid accountId, Guid deviceId, IEnumerable<string> capabilities)
    {
        var session = new DeviceSession(
            Guid.NewGuid().ToString("N"),
            accountId,
            deviceId,
            capabilities.Where(DeviceCapabilities.Known.Contains).ToHashSet(StringComparer.Ordinal),
            clock.GetUtcNow());
        _sessions[session.ConnectionId] = session;
        return (session, new Disconnect(() => _sessions.TryRemove(session.ConnectionId, out _)));
    }

    /// <summary>The account's most recently connected phone that can do this, if any.</summary>
    public DeviceSession? Find(Guid accountId, string capability) =>
        _sessions.Values
            .Where(s => s.AccountId == accountId && s.Capabilities.Contains(capability))
            .MaxBy(s => s.ConnectedAt);

    /// <summary>Asks a connected phone to do something. Null if none can, or it didn't answer in time.</summary>
    public async Task<DeviceToolResult?> InvokeAsync(Guid accountId, string capability, string tool, JsonObject arguments, CancellationToken ct)
    {
        if (Find(accountId, capability) is not { } session)
        {
            return null;
        }

        var callId = $"dev_{Guid.NewGuid():N}";
        var answer = new TaskCompletionSource<DeviceToolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[(accountId, callId)] = answer;
        try
        {
            hub.For(accountId).Publish(EventTypes.ToolInvoke,
                new ToolInvokeEvent(callId, session.ConnectionId, tool, arguments, clock.GetUtcNow() + Timeout));
            return await answer.Task.WaitAsync(Timeout, clock, ct);
        }
        catch (TimeoutException)
        {
            return null;
        }
        finally
        {
            _pending.TryRemove((accountId, callId), out _);
        }
    }

    /// <summary>A phone's answer. False when nothing is waiting for it (too late, or not its call).</summary>
    public bool Answer(Guid accountId, ToolResultRequest result)
    {
        if (result.CallId is not { } callId || !_pending.TryRemove((accountId, callId), out var answer))
        {
            return false;
        }

        answer.TrySetResult(new DeviceToolResult(result.Ok, result.Result ?? [], result.Error));
        return true;
    }

    private sealed class Disconnect(Action onDispose) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                onDispose();
            }
        }
    }
}
