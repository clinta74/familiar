using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace Familiar.Agent.Llm;

/// <summary>
/// Ollama's native chat API, streamed. Native rather than OpenAI-compatible because only the
/// native API takes <c>think</c> and <c>keep_alive</c>.
/// </summary>
/// <remarks>
/// <c>think: false</c> on every request: familiar-chat's base is a thinking model, and reasoning
/// before every spoken reply costs seconds a conversation doesn't have (bake-off, 2026-10-01).
/// </remarks>
public sealed class OllamaClient(HttpClient http, LlmOptions options) : ILlmClient
{
    public async IAsyncEnumerable<string> StreamAsync(
        IReadOnlyList<ChatMessage> messages, [EnumeratorCancellation] CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = options.Model,
            ["stream"] = true,
            ["think"] = false,
            ["keep_alive"] = -1,
            ["options"] = new JsonObject { ["temperature"] = options.Temperature },
            ["messages"] = new JsonArray([.. messages.Select(m => (JsonNode)new JsonObject
            {
                ["role"] = m.Role switch
                {
                    ChatRole.System => "system",
                    ChatRole.Assistant => "assistant",
                    _ => "user",
                },
                ["content"] = m.Content,
            })]),
        };

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

                if (chunk?["message"]?["content"]?.GetValue<string>() is { Length: > 0 } content)
                {
                    yield return content;
                }

                if (chunk?["done"]?.GetValue<bool>() == true)
                {
                    yield break;
                }
            }
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
