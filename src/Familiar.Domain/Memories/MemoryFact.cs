namespace Familiar.Domain.Memories;

public enum MemorySource
{
    /// <summary>Picked out of a conversation by the background pass.</summary>
    Conversation = 0,

    /// <summary>The user asked the familiar to remember it.</summary>
    Told = 1,

    /// <summary>Written or edited by the user in Settings.</summary>
    Settings = 2,
}

/// <summary>
/// Something the familiar remembers about its user, durably: "their daughter is called Maya",
/// "they take their tea without sugar" (PLAN.md §6.6). The user can see, edit and delete every one.
/// </summary>
public sealed class MemoryFact
{
    public const int MaxTextLength = 300;

    /// <summary>nomic-embed-text's dimensions.</summary>
    public const int EmbeddingDimensions = 768;

    public Guid Id { get; init; } = Guid.CreateVersion7();

    public Guid AccountId { get; init; }

    /// <summary>One short sentence, in the third person: "Their name is Sam."</summary>
    public required string Text { get; set; }

    /// <summary>For finding near-duplicates and what "forget that" refers to. Null if it couldn't be embedded.</summary>
    public float[]? Embedding { get; set; }

    public MemorySource Source { get; set; }

    /// <summary>The last message of the stretch of conversation it came from.</summary>
    public Guid? SourceMessageId { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; set; }
}
