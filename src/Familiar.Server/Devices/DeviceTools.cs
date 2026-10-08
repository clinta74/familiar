using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Familiar.Server.Conversation;

namespace Familiar.Server.Devices;

/// <summary>What phones can do for the familiar, by name (PLAN.md §6.1).</summary>
public static class DeviceCapabilities
{
    /// <summary>Add events to the phone's own calendars - and so to Google, Outlook, whatever it syncs.</summary>
    public const string CalendarWrite = "calendar.write";

    /// <summary>Open the phone's messages app with a text written to a contact (BACKLOG item 21).</summary>
    public const string Text = "phone.text";

    /// <summary>Open the dialler with a contact's number in.</summary>
    public const string Call = "phone.call";

    /// <summary>Open maps with directions.</summary>
    public const string Navigate = "maps.navigate";

    /// <summary>Say roughly where the phone is: the town.</summary>
    public const string Location = "location.read";

    /// <summary>Look up one contact's number or address, to say it (BACKLOG item 25).</summary>
    public const string Contacts = "contacts.read";

    public static readonly IReadOnlySet<string> Known = new HashSet<string>(StringComparer.Ordinal) { CalendarWrite, Text, Call, Navigate, Location, Contacts };

    /// <summary>What a browser may offer: where it is (BACKLOG item 24), and nothing else.</summary>
    public static readonly IReadOnlySet<string> Browser = new HashSet<string>(StringComparer.Ordinal) { Location };

    /// <summary>Where a message came from when it was a browser, not a phone (ToolContext.From).</summary>
    public const string FromBrowser = "browser";
}

/// <summary>A tool call for one particular phone to carry out.</summary>
/// <param name="CallId">Answer with this.</param>
/// <param name="ConnectionId">The connection it was meant for. Older apps act only on this.</param>
/// <param name="Tool">What to do, e.g. "calendar.add".</param>
/// <param name="Arguments">Its arguments.</param>
/// <param name="ExpiresAt">After this, don't: the server has stopped waiting (a replayed invoke must not run twice).</param>
/// <param name="DeviceId">
/// Which phone should act, on whichever connection it has: every other device ignores it. A phone
/// coming back from the background can briefly have two (one stuck while it had no network), and
/// the newest isn't always the live one.
/// </param>
public sealed record ToolInvokeEvent(string CallId, string ConnectionId, string Tool, JsonObject Arguments, DateTimeOffset ExpiresAt, Guid? DeviceId = null);

/// <param name="CallId">The tool.invoke being answered.</param>
/// <param name="Ok">Whether it worked.</param>
/// <param name="Result">What happened, for the model (and the summary).</param>
/// <param name="Error">Why not, when it didn't.</param>
public sealed record ToolResultRequest(string? CallId, bool Ok, JsonObject? Result = null, string? Error = null);

/// <summary>What the device answered.</summary>
public sealed record DeviceToolResult(bool Ok, JsonObject Result, string? Error);

/// <summary>A phone with the app open - or a browser tab, with no device - and what it said it can do.</summary>
public sealed record DeviceSession(string ConnectionId, Guid AccountId, Guid? DeviceId, IReadOnlySet<string> Capabilities, DateTimeOffset ConnectedAt)
{
    public bool IsBrowser => DeviceId is null;
}

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

    /// <summary>A phone (or, with no device, a browser) opened the stream. Dispose the result when it closes.</summary>
    public (DeviceSession Session, IDisposable Registration) Connect(Guid accountId, Guid? deviceId, IEnumerable<string> capabilities)
    {
        var allowed = deviceId is null ? DeviceCapabilities.Browser : DeviceCapabilities.Known;
        var session = new DeviceSession(
            Guid.NewGuid().ToString("N"),
            accountId,
            deviceId,
            capabilities.Where(allowed.Contains).ToHashSet(StringComparer.Ordinal),
            clock.GetUtcNow());
        _sessions[session.ConnectionId] = session;
        return (session, new Disconnect(() => _sessions.TryRemove(session.ConnectionId, out _)));
    }

    /// <summary>
    /// The connected device that should do this: the one the message came from (<paramref name="from"/>:
    /// its device id, or <see cref="DeviceCapabilities.FromBrowser"/>) if it can, otherwise the most
    /// recently connected one that can; browsers only when <paramref name="browsers"/> allows. Null if
    /// none can.
    /// </summary>
    public DeviceSession? Find(Guid accountId, string capability, string? from = null, bool browsers = true)
    {
        var able = _sessions.Values
            .Where(s => s.AccountId == accountId && s.Capabilities.Contains(capability) && (browsers || !s.IsBrowser))
            .ToList();
        var mine = from == DeviceCapabilities.FromBrowser
            ? able.Where(s => s.IsBrowser)
            : able.Where(s => s.DeviceId?.ToString() == from);
        return mine.MaxBy(s => s.ConnectedAt) ?? able.MaxBy(s => s.ConnectedAt);
    }

    /// <summary>Asks a connected device to do something (see <see cref="Find"/>). Null if none can, or it didn't answer in time.</summary>
    public async Task<DeviceToolResult?> InvokeAsync(
        Guid accountId, string capability, string tool, JsonObject arguments, CancellationToken ct, string? from = null, bool browsers = true)
    {
        if (Find(accountId, capability, from, browsers) is not { } session)
        {
            return null;
        }

        var callId = $"dev_{Guid.NewGuid():N}";
        var answer = new TaskCompletionSource<DeviceToolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[(accountId, callId)] = answer;
        try
        {
            hub.For(accountId).Publish(EventTypes.ToolInvoke,
                new ToolInvokeEvent(callId, session.ConnectionId, tool, arguments, clock.GetUtcNow() + Timeout, session.DeviceId));
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
