namespace Familiar.Agent.Llm;

/// <summary>The <c>Llm</c> configuration section.</summary>
public sealed class LlmOptions
{
    public const string Section = "Llm";

    /// <summary>Familiar's own Ollama (PLAN.md §4.2). 11435 locally; http://ollama:11434 on the NAS.</summary>
    public string BaseUrl { get; set; } = "http://127.0.0.1:11435";

    /// <summary>
    /// The derived model, not its base: <c>gemma4:12b</c> would answer too, with Ollama's 4096-token
    /// default window, and silently truncate the prompt (tools/ollama/README.md).
    /// </summary>
    public string Model { get; set; } = "familiar-chat";

    public double Temperature { get; set; } = 0.7;

    /// <summary>
    /// How long to wait for the model to start answering. Generous, because the first request after
    /// a restart loads ~9 GB of weights.
    /// </summary>
    public int RequestTimeoutSeconds { get; set; } = 120;

    /// <summary>How many earlier messages go into each prompt, at most: fewer when they won't fit (<see cref="ContextTokens"/>).</summary>
    public int HistoryMessages { get; set; } = 20;

    /// <summary>
    /// The model's window: <c>num_ctx</c> in tools/ollama/Modelfile.familiar, so keep the two the same.
    /// The prompt, its tools and the reply all have to fit; the oldest history gives way.
    /// </summary>
    public int ContextTokens { get; set; } = 8192;

    /// <summary>
    /// Of the window, kept free for the reply and the turn's tool results (up to 4,000 characters
    /// each), so neither is cut off.
    /// </summary>
    public int ReplyTokens { get; set; } = 1536;

    /// <summary>For memories (PLAN.md §6.6). 768 dimensions; changing it means re-embedding every memory.</summary>
    public string EmbeddingModel { get; set; } = "nomic-embed-text";

    /// <summary>Load the models at startup and keep them loaded (<see cref="OllamaWarmUp"/>).</summary>
    public bool WarmUp { get; set; } = true;

    /// <summary>How often the warm-up checks the models are still loaded.</summary>
    public int WarmUpCheckSeconds { get; set; } = 120;

    public override string ToString() =>
        $"{Model} at {BaseUrl}, temperature {Temperature}, history {HistoryMessages}, window {ContextTokens} ({ReplyTokens} for the reply), embeddings {EmbeddingModel}, timeout {RequestTimeoutSeconds}s";
}
