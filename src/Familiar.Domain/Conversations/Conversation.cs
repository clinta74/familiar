namespace Familiar.Domain.Conversations;

/// <summary>A stretch of talk between an account and its familiar.</summary>
public sealed class Conversation
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public Guid AccountId { get; init; }

    public DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset LastMessageAt { get; set; }

    public List<Message> Messages { get; } = [];
}
