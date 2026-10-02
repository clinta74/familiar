using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Familiar.Agent.Llm;

/// <summary>
/// Embeddings from Ollama's <c>/api/embed</c>, with nomic-embed-text's task prefixes: it's trained to
/// embed "search_document:" and "search_query:" text differently, and matches noticeably worse
/// without them.
/// </summary>
public sealed class OllamaEmbedder(HttpClient http, LlmOptions options) : IEmbedder
{
    public async Task<float[]> EmbedAsync(string text, EmbeddingPurpose purpose, CancellationToken ct)
    {
        var prefix = purpose == EmbeddingPurpose.Query ? "search_query: " : "search_document: ";
        var body = new JsonObject
        {
            ["model"] = options.EmbeddingModel,
            ["input"] = prefix + text,
            ["keep_alive"] = -1,
        };

        HttpResponseMessage response;
        try
        {
            response = await http.PostAsJsonAsync("api/embed", body, ct);
        }
        catch (HttpRequestException exception)
        {
            throw new LlmUnavailableException($"Can't reach the model server at {http.BaseAddress}.", exception);
        }
        catch (TaskCanceledException exception) when (!ct.IsCancellationRequested)
        {
            throw new LlmUnavailableException("The model server didn't answer in time.", exception);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new LlmUnavailableException(
                    $"The model server answered {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}");
            }

            var answer = await response.Content.ReadFromJsonAsync<JsonObject>(ct);
            if (answer?["embeddings"] is not JsonArray { Count: > 0 } embeddings || embeddings[0] is not JsonArray vector)
            {
                throw new LlmUnavailableException("The model server returned no embedding.");
            }

            return [.. vector.Select(v => v!.GetValue<float>())];
        }
    }
}

/// <summary>Cosine similarity, for comparing embeddings: 1 is the same meaning, 0 unrelated.</summary>
public static class Similarity
{
    public static double Cosine(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length || a.Length == 0)
        {
            return 0;
        }

        double dot = 0, normA = 0, normB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        return normA == 0 || normB == 0 ? 0 : dot / (Math.Sqrt(normA) * Math.Sqrt(normB));
    }
}
