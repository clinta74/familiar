using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace Familiar.Server.Conversation;

/// <summary>One server-sent event, already framed for the wire.</summary>
public sealed record ServerSentEvent(string Id, string Type, string Frame);

/// <summary>
/// Every open event stream, grouped by account (PLAN.md §3.1). A turn publishes to its account's
/// channel once, and every device the account has open receives it.
/// </summary>
public sealed class ConversationHub
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly ConcurrentDictionary<Guid, AccountChannel> _accounts = new();

    // Event ids carry the process's epoch, so a Last-Event-ID from before a restart is recognised
    // as unknown (and the client told to resync) instead of being mistaken for a recent one.
    private readonly string _epoch = Guid.NewGuid().ToString("N")[..8];

    public AccountChannel For(Guid accountId) => _accounts.GetOrAdd(accountId, _ => new AccountChannel(_epoch));

    /// <summary>Frames an event that belongs to one connection only (no id, so it's never replayed).</summary>
    public static string Frame(string type, object payload) =>
        $"event: {type}\ndata: {JsonSerializer.Serialize(payload, Json)}\n\n";
}

/// <summary>
/// One account's live conversation: its subscribers, a replay buffer for reconnects, and the turn
/// that's currently running.
/// </summary>
public sealed class AccountChannel(string epoch)
{
    /// <summary>
    /// Events kept for replay. A reply is a few hundred deltas, so this covers a reconnect in the
    /// middle of a long one; anything further behind gets a resync.
    /// </summary>
    private const int ReplayCapacity = 1024;

    /// <summary>
    /// A subscriber this far behind isn't reading; it's dropped and its client reconnects and
    /// replays, rather than the server buffering without bound.
    /// </summary>
    private const int SubscriberBuffer = 2048;

    private readonly Lock _gate = new();
    private readonly Queue<ServerSentEvent> _replay = new();
    private readonly List<Channel<ServerSentEvent>> _subscribers = [];
    private long _sequence;

    /// <summary>Serialises turn starts and interrupts for this account.</summary>
    internal SemaphoreSlim TurnGate { get; } = new(1, 1);

    internal CancellationTokenSource? TurnCancellation { get; set; }

    internal Task? TurnTask { get; set; }

    public void Publish(string type, object payload)
    {
        var data = JsonSerializer.Serialize(payload, ConversationHub.Json);

        lock (_gate)
        {
            var id = $"{epoch}-{++_sequence}";
            var item = new ServerSentEvent(id, type, $"id: {id}\nevent: {type}\ndata: {data}\n\n");

            _replay.Enqueue(item);
            if (_replay.Count > ReplayCapacity)
            {
                _replay.Dequeue();
            }

            for (var i = _subscribers.Count - 1; i >= 0; i--)
            {
                if (!_subscribers[i].Writer.TryWrite(item))
                {
                    _subscribers[i].Writer.TryComplete();
                    _subscribers.RemoveAt(i);
                }
            }
        }
    }

    /// <summary>
    /// Starts receiving events. With a <paramref name="lastEventId"/> (a reconnect), the events
    /// after it are replayed first - or, if they're no longer held, the subscription asks for a resync.
    /// </summary>
    public Subscription Subscribe(string? lastEventId)
    {
        var channel = Channel.CreateBounded<ServerSentEvent>(new BoundedChannelOptions(SubscriberBuffer)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

        lock (_gate)
        {
            List<ServerSentEvent> replay = [];
            var resync = false;

            if (!string.IsNullOrEmpty(lastEventId))
            {
                if (!TryParseId(lastEventId, out var sequence))
                {
                    // From before a restart, or not ours: nothing it refers to is still held.
                    resync = true;
                }
                else if (sequence < _sequence)
                {
                    // Missed something. Replayable only if the buffer still reaches back that far.
                    if (_replay.Count > 0 && SequenceOf(_replay.Peek()) <= sequence + 1)
                    {
                        replay.AddRange(_replay.Where(e => SequenceOf(e) > sequence));
                    }
                    else
                    {
                        resync = true;
                    }
                }
            }

            _subscribers.Add(channel);
            return new Subscription(this, channel, replay, resync);
        }
    }

    internal void Unsubscribe(Channel<ServerSentEvent> channel)
    {
        lock (_gate)
        {
            _subscribers.Remove(channel);
        }

        channel.Writer.TryComplete();
    }

    private bool TryParseId(string id, out long sequence)
    {
        sequence = 0;
        var dash = id.IndexOf('-', StringComparison.Ordinal);
        return dash > 0
            && id.AsSpan(0, dash).SequenceEqual(epoch)
            && long.TryParse(id.AsSpan(dash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out sequence);
    }

    private static long SequenceOf(ServerSentEvent e) =>
        long.Parse(e.Id.AsSpan(e.Id.IndexOf('-', StringComparison.Ordinal) + 1), CultureInfo.InvariantCulture);
}

public sealed class Subscription(
    AccountChannel owner,
    Channel<ServerSentEvent> channel,
    IReadOnlyList<ServerSentEvent> replay,
    bool resync) : IDisposable
{
    public ChannelReader<ServerSentEvent> Reader => channel.Reader;

    /// <summary>Events the client missed while disconnected, to send before anything new.</summary>
    public IReadOnlyList<ServerSentEvent> Replay { get; } = replay;

    /// <summary>The client missed more than can be replayed; it should reload history.</summary>
    public bool Resync { get; } = resync;

    public void Dispose() => owner.Unsubscribe(channel);
}
