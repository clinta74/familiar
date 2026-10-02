using System.Text;
using System.Text.Json;
using Familiar.Server.Conversation;

namespace Familiar.Server.Tests.Infrastructure;

public sealed record SseEvent(string? Id, string Type, string Data)
{
    public T Payload<T>() => JsonSerializer.Deserialize<T>(Data, ConversationHub.Json)!;
}

/// <summary>Reads the conversation stream incrementally, as a browser would - not buffered to the end.</summary>
public sealed class SseClient : IAsyncDisposable
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    private readonly HttpResponseMessage _response;
    private readonly StreamReader _reader;

    private SseClient(HttpResponseMessage response, Stream stream)
    {
        _response = response;
        _reader = new StreamReader(stream, Encoding.UTF8);
    }

    public HttpResponseMessage Response => _response;

    public static async Task<SseClient> ConnectAsync(HttpClient client, string? lastEventId = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/conversation/stream");
        if (lastEventId is not null)
        {
            request.Headers.Add("Last-Event-ID", lastEventId);
        }

        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        return new SseClient(response, await response.Content.ReadAsStreamAsync());
    }

    public async Task<SseEvent> NextAsync(TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? DefaultTimeout);
        string? id = null, type = null;
        var data = new StringBuilder();

        while (true)
        {
            var line = await _reader.ReadLineAsync(cts.Token)
                ?? throw new EndOfStreamException("The event stream ended.");

            if (line.Length == 0)
            {
                if (type is not null)
                {
                    return new SseEvent(id, type, data.ToString());
                }

                continue;
            }

            if (line.StartsWith(':') || line.StartsWith("retry:", StringComparison.Ordinal))
            {
                continue;
            }

            var colon = line.IndexOf(':', StringComparison.Ordinal);
            var field = line[..colon];
            var value = line[(colon + 1)..].TrimStart();
            switch (field)
            {
                case "id": id = value; break;
                case "event": type = value; break;
                case "data": data.Append(value); break;
            }
        }
    }

    /// <summary>Reads until an event of <paramref name="type"/>, returning everything read including it.</summary>
    public async Task<List<SseEvent>> ReadUntilAsync(string type, TimeSpan? timeout = null)
    {
        var events = new List<SseEvent>();
        while (true)
        {
            var next = await NextAsync(timeout);
            events.Add(next);
            if (next.Type == type)
            {
                return events;
            }
        }
    }

    /// <summary>Reads until a <c>state</c> event with the given state.</summary>
    public async Task<List<SseEvent>> ReadUntilStateAsync(FamiliarState state, TimeSpan? timeout = null)
    {
        var events = new List<SseEvent>();
        while (true)
        {
            var next = await NextAsync(timeout);
            events.Add(next);
            if (next.Type == EventTypes.State && next.Payload<StateEvent>().State == state)
            {
                return events;
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        _reader.Dispose();
        _response.Dispose();
        return ValueTask.CompletedTask;
    }
}
