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
public sealed record ChatMessage(
    ChatRole Role,
    string Content,
    IReadOnlyList<ToolCall>? ToolCalls = null,
    string? ToolName = null,
    string? ToolCallId = null);

/// <summary>A piece of a streamed reply.</summary>
public abstract record LlmChunk;

public sealed record TextChunk(string Text) : LlmChunk;

/// <summary>The model wants tools run before it carries on.</summary>
public sealed record ToolCallsChunk(IReadOnlyList<ToolCall> Calls) : LlmChunk;

/// <summary>A streaming chat model that can call tools.</summary>
public interface ILlmClient
{
    /// <summary>Streams the reply, in order. Cancelling stops generation.</summary>
    /// <param name="messages">The conversation so far.</param>
    /// <param name="tools">Tools the model may call; null or empty for none.</param>
    /// <param name="ct">Cancels the stream.</param>
    /// <exception cref="LlmUnavailableException">The model server can't be reached or refused the request.</exception>
    IAsyncEnumerable<LlmChunk> StreamAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolDefinition>? tools, CancellationToken ct);

    /// <summary>Whether the model server answers at all. Cheap; never throws.</summary>
    Task<bool> IsAvailableAsync(CancellationToken ct);
}

/// <summary>
/// The model can't be used right now: the server is down, still loading, or rejected the request.
/// The familiar shows this as "resting" rather than as a failure.
/// </summary>
public sealed class LlmUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
