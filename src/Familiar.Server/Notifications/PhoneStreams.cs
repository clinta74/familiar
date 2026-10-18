using System.Collections.Concurrent;
using System.Threading.Channels;
using Familiar.Persistence;
using Familiar.Server.Auth;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;

namespace Familiar.Server.Notifications;

/// <summary>
/// Push to phones without the ntfy app (BACKLOG item 38): each signed-in phone keeps one small
/// stream open from a background service, and hears "changed" on it whenever a push would go out.
/// Like Web Push it says nothing else - the phone asks what over its normal requests - and like the
/// conversation stream it's in memory: a phone that's away reconnects and catches up by asking.
/// </summary>
public sealed class PhoneStreams
{
    private readonly ConcurrentDictionary<Guid, List<Channel<bool>>> _byAccount = new();

    /// <summary>Something changed for the account: every phone listening hears of it.</summary>
    public void Changed(Guid accountId)
    {
        if (!_byAccount.TryGetValue(accountId, out var listeners))
        {
            return;
        }

        lock (listeners)
        {
            foreach (var listener in listeners)
            {
                // One waiting is enough: the phone fetches everything at once.
                listener.Writer.TryWrite(true);
            }
        }
    }

    /// <summary>How many phones of the account are listening, for tests and logs.</summary>
    public int Listening(Guid accountId) =>
        _byAccount.TryGetValue(accountId, out var listeners) ? listeners.Count : 0;

    internal (ChannelReader<bool> Reader, IDisposable Registration) Listen(Guid accountId)
    {
        var channel = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
        var listeners = _byAccount.GetOrAdd(accountId, _ => []);
        lock (listeners)
        {
            listeners.Add(channel);
        }

        return (channel.Reader, new Registration(() =>
        {
            lock (listeners)
            {
                listeners.Remove(channel);
            }
        }));
    }

    /// <summary>
    /// Well inside nginx's 60 s proxy_read_timeout - but as rare as that allows, since each ping
    /// wakes the phone's radio. The same as the ntfy app's.
    /// </summary>
    internal static TimeSpan PingInterval { get; set; } = TimeSpan.FromSeconds(45);

    /// <summary>The phone's stream: a comment to say it's open, then "changed" events and pings.</summary>
    internal static async Task StreamAsync(
        HttpContext http, PhoneStreams streams, IDbContextFactory<FamiliarDbContext> db, CancellationToken ct)
    {
        if (http.User.DeviceId() is not { } deviceId)
        {
            // Browsers have Web Push; this is for the phone app's background service.
            await Results.Problem("Only the phone app listens here.", statusCode: StatusCodes.Status400BadRequest).ExecuteAsync(http);
            return;
        }

        var accountId = http.User.AccountId()!.Value;

        // Its old way in, through ntfy, goes: this is how it hears now.
        await using (var context = await db.CreateDbContextAsync(ct))
        {
            await context.PushSubscriptions.Where(s => s.DeviceId == deviceId).ExecuteDeleteAsync(ct);
        }

        var (reader, registration) = streams.Listen(accountId);
        using var listening = registration;

        http.Response.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache, no-transform";
        http.Response.Headers["X-Accel-Buffering"] = "no";
        http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        try
        {
            await WriteAsync(http, "retry: 5000\n\n: open\n\n", ct);
            Task<bool>? waiting = null;
            while (!ct.IsCancellationRequested)
            {
                waiting ??= reader.WaitToReadAsync(ct).AsTask();
                if (await Task.WhenAny(waiting, Task.Delay(PingInterval, ct)) != waiting)
                {
                    await WriteAsync(http, ": ping\n\n", ct);
                    continue;
                }

                waiting = null;
                while (reader.TryRead(out _))
                {
                }

                await WriteAsync(http, "event: changed\ndata: {}\n\n", ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The phone went away.
        }
    }

    private static async Task WriteAsync(HttpContext http, string frame, CancellationToken ct)
    {
        await http.Response.WriteAsync(frame, ct);
        await http.Response.Body.FlushAsync(ct);
    }

    private sealed class Registration(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
