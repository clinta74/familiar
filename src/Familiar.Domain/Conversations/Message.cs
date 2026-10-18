using Familiar.Domain.Expression;

namespace Familiar.Domain.Conversations;

public enum MessageRole
{
    User = 0,
    Assistant = 1,
}

/// <summary>One turn of a conversation.</summary>
public sealed class Message
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public Guid ConversationId { get; init; }

    public MessageRole Role { get; init; }

    /// <summary>What was said, with expression tags stripped.</summary>
    public required string Content { get; set; }

    /// <summary>For assistant messages: the emotion the reply was delivered with.</summary>
    public Emotion? Emotion { get; set; }

    public double? EmotionIntensity { get; set; }

    /// <summary>
    /// The user cut the reply off. The partial reply is kept, and shown to the model as such, so
    /// it knows it didn't finish.
    /// </summary>
    public bool Interrupted { get; set; }

    /// <summary>
    /// For assistant messages: pages it handed over, as JSON (url, title), shown as cards under the
    /// reply (BACKLOG item 34). Null when there were none.
    /// </summary>
    public string? Links { get; set; }

    /// <summary>For user messages: the picture sent with it (BACKLOG item 35), if any.</summary>
    public Guid? ImageId { get; set; }

    /// <summary>
    /// For user messages: the words exactly as the model was given them, with the notes in front
    /// ("(Sunday afternoon, 3:05 PM. …) tell me a story"). Replayed so in later turns, the prompt is
    /// the same as the one the model server cached up to the end of its reply (BACKLOG item 37).
    /// </summary>
    public string? PromptText { get; set; }

    public DateTimeOffset CreatedAt { get; init; }
}
