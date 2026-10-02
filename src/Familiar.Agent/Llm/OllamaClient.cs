using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Familiar.Agent.Tools;

namespace Familiar.Agent.Llm;

/// <summary>
/// Ollama's native chat API, streamed, with tool calling. Native rather than OpenAI-compatible because
/// only the native API takes <c>think</c> and <c>keep_alive</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>think: false</c> on every request: familiar-chat's base is a thinking model, and reasoning
/// before every spoken reply costs seconds a conversation doesn't have (bake-off, 2026-10-01).
/// </para>
/// <para>
/// Tool calls arrive whole, in a single chunk, each with an id; the results go back as <c>tool</c>
/// messages naming the tool and the call.
/// </para>
/// </remarks>
public sealed class OllamaClient(HttpClient http, LlmOptions options) : ILlmClient
{
    public async IAsyncEnumerable<LlmChunk> StreamAsync(
        IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolDefinition>? tools, [EnumeratorCancellation] CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = options.Model,
            ["stream"] = true,
            ["think"] = false,
            ["keep_alive"] = -1,
            ["options"] = new JsonObject { ["temperature"] = options.Temperature },
            ["messages"] = new JsonArray([.. messages.Select(ToJson)]),
        };

        if (tools is { Count: > 0 })
        {
            body["tools"] = new JsonArray([.. tools.Select(t => (JsonNode)new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["parameters"] = t.Parameters.DeepClone(),
                },
            })]);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "api/chat") { Content = JsonContent.Create(body) };
        var response = await SendAsync(request, ct);

        using (response)
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);

            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (line.Length == 0)
                {
                    continue;
                }

                var chunk = JsonNode.Parse(line);
                if (chunk?["error"] is { } error)
                {
                    throw new LlmUnavailableException($"Ollama: {error}");
                }

                if (chunk?["message"]?["tool_calls"] is JsonArray { Count: > 0 } calls)
                {
                    yield return new ToolCallsChunk([.. calls.Select(ParseCall).OfType<ToolCall>()]);
                }

                if (chunk?["message"]?["content"]?.GetValue<string>() is { Length: > 0 } content)
                {
                    yield return new TextChunk(content);
                }

                if (chunk?["done"]?.GetValue<bool>() == true)
                {
                    yield break;
                }
            }
        }
    }

    public async Task<JsonObject> CompleteJsonAsync(IReadOnlyList<ChatMessage> messages, JsonObject schema, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = options.Model,
            ["stream"] = false,
            ["think"] = false,
            ["keep_alive"] = -1,
            ["format"] = schema.DeepClone(),

            // Low: this is bookkeeping, not conversation.
            ["options"] = new JsonObject { ["temperature"] = 0.2 },
            ["messages"] = new JsonArray([.. messages.Select(ToJson)]),
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "api/chat") { Content = JsonContent.Create(body) };
        using var response = await SendAsync(request, ct);
        var answer = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
        var content = answer?["message"]?["content"]?.GetValue<string>() ?? "";

        try
        {
            return JsonNode.Parse(content) as JsonObject
                ?? throw new LlmUnavailableException("The model's answer wasn't a JSON object.");
        }
        catch (System.Text.Json.JsonException exception)
        {
            throw new LlmUnavailableException("The model's answer wasn't valid JSON.", exception);
        }
    }

    public async Task<bool> IsAvailableAsync(CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            using var response = await http.GetAsync("api/version", timeout.Token);
            return response.IsSuccessStatusCode;
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
        {
            return false;
        }
    }

    private static JsonNode ToJson(ChatMessage message)
    {
        var json = new JsonObject
        {
            ["role"] = message.Role switch
            {
                ChatRole.System => "system",
                ChatRole.Assistant => "assistant",
                ChatRole.Tool => "tool",
                _ => "user",
            },
            ["content"] = message.Content,
        };

        if (message.ToolCalls is { Count: > 0 } calls)
        {
            json["tool_calls"] = new JsonArray([.. calls.Select(c => (JsonNode)new JsonObject
            {
                ["id"] = c.Id,
                ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.Arguments.DeepClone() },
            })]);
        }

        if (message.ToolName is { } name)
        {
            json["tool_name"] = name;
        }

        if (message.ToolCallId is { } id)
        {
            json["tool_call_id"] = id;
        }

        return json;
    }

    private static ToolCall? ParseCall(JsonNode? node)
    {
        var function = node?["function"];
        if (function?["name"]?.GetValue<string>() is not { Length: > 0 } name)
        {
            return null;
        }

        // Arguments normally arrive as an object; some models send a JSON string instead.
        var arguments = function["arguments"] switch
        {
            JsonObject o => (JsonObject)o.DeepClone(),
            JsonValue v when v.TryGetValue<string>(out var text) && JsonNode.Parse(text) is JsonObject parsed => parsed,
            _ => new JsonObject(),
        };

        var id = node?["id"]?.GetValue<string>() is { Length: > 0 } given ? given : $"call_{Guid.NewGuid():N}";
        return new ToolCall(id, name, arguments);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException exception)
        {
            throw new LlmUnavailableException($"Can't reach the model server at {http.BaseAddress}.", exception);
        }
        catch (TaskCanceledException exception) when (!ct.IsCancellationRequested)
        {
            throw new LlmUnavailableException("The model server didn't answer in time.", exception);
        }

        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(ct);
            response.Dispose();
            throw new LlmUnavailableException($"The model server answered {(int)response.StatusCode}: {detail}");
        }

        return response;
    }
}
