namespace Familiar.Agent.Llm;

public enum ChatRole
{
    System,
    User,
    Assistant,
}

public sealed record ChatMessage(ChatRole Role, string Content);

/// <summary>A streaming chat model.</summary>
public interface ILlmClient
{
    /// <summary>Streams the reply as text fragments, in order. Cancelling stops generation.</summary>
    /// <exception cref="LlmUnavailableException">The model server can't be reached or refused the request.</exception>
    IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct);

    /// <summary>Whether the model server answers at all. Cheap; never throws.</summary>
    Task<bool> IsAvailableAsync(CancellationToken ct);
}

/// <summary>
/// The model can't be used right now: the server is down, still loading, or rejected the request.
/// The familiar shows this as "resting" rather than as a failure.
/// </summary>
public sealed class LlmUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
