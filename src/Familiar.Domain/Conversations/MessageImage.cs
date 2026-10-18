namespace Familiar.Domain.Conversations;

/// <summary>
/// A picture sent in the chat (BACKLOG item 35): a JPEG, shrunk by the client and with its camera and
/// location data stripped by the server, kept with the message it came with. Uploaded first, then
/// named by the message; one not sent within a day is removed.
/// </summary>
public sealed class MessageImage
{
    /// <summary>The most a picture may be, after shrinking on the device.</summary>
    public const int MaxBytes = 3_000_000;

    /// <summary>The longest side allowed; clients shrink to 1024.</summary>
    public const int MaxSide = 4096;

    public Guid Id { get; init; } = Guid.CreateVersion7();

    public Guid AccountId { get; init; }

    public required byte[] Bytes { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}
