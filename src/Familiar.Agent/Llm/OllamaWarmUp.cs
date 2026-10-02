using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Familiar.Agent.Llm;

/// <summary>What a warm-up check found.</summary>
public enum WarmUpOutcome
{
    /// <summary>Both models were already in memory.</summary>
    AlreadyLoaded,

    /// <summary>One or both had to be loaded; now they are.</summary>
    Loaded,

    /// <summary>Ollama answered, but a model doesn't exist there (familiar-chat not created yet).</summary>
    Missing,

    /// <summary>Ollama isn't reachable.</summary>
    Unreachable,
}

/// <summary>
/// Gets the chat and embedding models into memory before anyone needs them. Loading familiar-chat
/// takes ~11 s on the dev box (load_duration, cold, 2026-10-02), against 0.4 s for a reply once it's
/// resident - without this, whoever speaks first after an Ollama restart waits out the difference.
/// </summary>
/// <remarks>
/// Ollama-specific: <c>/api/ps</c> says what's loaded; a <c>/api/generate</c> with no prompt loads a
/// chat model without generating anything; an embedding model is loaded by embedding a word.
/// <c>keep_alive: -1</c> keeps both there.
/// </remarks>
public sealed class OllamaWarmUp(HttpClient http, LlmOptions options)
{
    /// <summary>The model that wasn't found, when the outcome is <see cref="WarmUpOutcome.Missing"/>.</summary>
    public string? MissingModel { get; private set; }

    public async Task<WarmUpOutcome> EnsureLoadedAsync(CancellationToken ct)
    {
        MissingModel = null;
        try
        {
            var loaded = await LoadedAsync(ct);
            var chatLoaded = loaded.Contains(Tagged(options.Model));
            var embedLoaded = loaded.Contains(Tagged(options.EmbeddingModel));
            if (chatLoaded && embedLoaded)
            {
                return WarmUpOutcome.AlreadyLoaded;
            }

            if (!chatLoaded && !await LoadAsync("api/generate", new JsonObject { ["model"] = options.Model, ["keep_alive"] = -1 }, ct))
            {
                MissingModel = options.Model;
                return WarmUpOutcome.Missing;
            }

            if (!embedLoaded && !await LoadAsync("api/embed", new JsonObject { ["model"] = options.EmbeddingModel, ["input"] = "warm", ["keep_alive"] = -1 }, ct))
            {
                MissingModel = options.EmbeddingModel;
                return WarmUpOutcome.Missing;
            }

            return WarmUpOutcome.Loaded;
        }
        catch (Exception exception) when (exception is HttpRequestException || (exception is TaskCanceledException && !ct.IsCancellationRequested))
        {
            return WarmUpOutcome.Unreachable;
        }
    }

    private async Task<HashSet<string>> LoadedAsync(CancellationToken ct)
    {
        var ps = await http.GetFromJsonAsync<JsonObject>("api/ps", ct);
        return [.. (ps?["models"] as JsonArray ?? []).Select(m => m?["name"]?.GetValue<string>()).OfType<string>()];
    }

    /// <summary>False when the model doesn't exist; throws if Ollama can't be reached or fails otherwise.</summary>
    private async Task<bool> LoadAsync(string path, JsonObject body, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(path, body, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        response.EnsureSuccessStatusCode();
        return true;
    }

    /// <summary>/api/ps names models with their tag: "familiar-chat" is listed as "familiar-chat:latest".</summary>
    private static string Tagged(string model) => model.Contains(':', StringComparison.Ordinal) ? model : $"{model}:latest";
}
