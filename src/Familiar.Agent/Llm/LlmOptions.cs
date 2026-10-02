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

    /// <summary>How many earlier messages go into each prompt.</summary>
    public int HistoryMessages { get; set; } = 20;

    /// <summary>For memories (PLAN.md §6.6). 768 dimensions; changing it means re-embedding every memory.</summary>
    public string EmbeddingModel { get; set; } = "nomic-embed-text";

    public override string ToString() =>
        $"{Model} at {BaseUrl}, temperature {Temperature}, history {HistoryMessages}, embeddings {EmbeddingModel}, timeout {RequestTimeoutSeconds}s";
}
