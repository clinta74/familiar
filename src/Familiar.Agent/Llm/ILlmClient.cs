using System.Text.Json.Nodes;
using Familiar.Agent.Tools;

namespace Familiar.Agent.Llm;

public enum ChatRole
{
    System,
    User,
    Assistant,

    /// <summary>A tool's result, answering the assistant's tool call.</summary>
    Tool,
}

/// <param name="Role">Who's speaking.</param>
/// <param name="Content">What was said (for a tool message, its JSON result).</param>
/// <param name="ToolCalls">For an assistant message: the tools it asked for.</param>
/// <param name="ToolName">For a tool message: which tool answered.</param>
/// <param name="ToolCallId">For a tool message: which call it answers.</param>
/// <param name="Images">Pictures with a user message (BACKLOG item 35), as JPEG bytes.</param>
public sealed record ChatMessage(
    ChatRole Role,
    string Content,
    IReadOnlyList<ToolCall>? ToolCalls = null,
    string? ToolName = null,
    string? ToolCallId = null,
    IReadOnlyList<byte[]>? Images = null);

/// <summary>A piece of a streamed reply.</summary>
public abstract record LlmChunk;

public sealed record TextChunk(string Text) : LlmChunk;

/// <summary>The model wants tools run before it carries on.</summary>
public sealed record ToolCallsChunk(IReadOnlyList<ToolCall> Calls) : LlmChunk;

/// <summary>
/// What a round cost, as the model server reports it at the end (BACKLOG item 37): the prompt's
/// tokens, how many it actually read (the rest came from its cache), and how long that took.
/// </summary>
public sealed record UsageChunk(int PromptTokens, int PromptRead, double PromptMs, int OutputTokens, double OutputMs) : LlmChunk;

/// <summary>A streaming chat model that can call tools.</summary>
public interface ILlmClient
{
    /// <summary>Streams the reply, in order. Cancelling stops generation.</summary>
    /// <param name="messages">The conversation so far.</param>
    /// <param name="tools">Tools the model may call; null or empty for none.</param>
    /// <param name="ct">Cancels the stream.</param>
    /// <exception cref="LlmUnavailableException">The model server can't be reached or refused the request.</exception>
    IAsyncEnumerable<LlmChunk> StreamAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolDefinition>? tools, CancellationToken ct);

    /// <summary>
    /// One whole answer constrained to a JSON schema, for background work (picking out memories)
    /// rather than conversation.
    /// </summary>
    /// <param name="messages">The prompt.</param>
    /// <param name="schema">The JSON schema the answer must match.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <exception cref="LlmUnavailableException">The model server can't be reached or refused the request.</exception>
    Task<JsonObject> CompleteJsonAsync(IReadOnlyList<ChatMessage> messages, JsonObject schema, CancellationToken ct);

    /// <summary>Whether the model server answers at all. Cheap; never throws.</summary>
    Task<bool> IsAvailableAsync(CancellationToken ct);
}

/// <summary>
/// The model can't be used right now: the server is down, still loading, or rejected the request.
/// The familiar shows this as "resting" rather than as a failure.
/// </summary>
public sealed class LlmUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>What a text is being embedded for: nomic-embed-text embeds the two differently.</summary>
public enum EmbeddingPurpose
{
    /// <summary>Something being stored, to be found later.</summary>
    Document,

    /// <summary>Something being looked for.</summary>
    Query,
}

/// <summary>Turns text into vectors, for finding memories by meaning rather than wording.</summary>
public interface IEmbedder
{
    /// <exception cref="LlmUnavailableException">The model server can't be reached or refused the request.</exception>
    Task<float[]> EmbedAsync(string text, EmbeddingPurpose purpose, CancellationToken ct);
}
