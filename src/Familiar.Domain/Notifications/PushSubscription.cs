namespace Familiar.Domain.Notifications;

/// <summary>
/// A browser's Web Push subscription, so a timer can ring with the tab closed (PLAN.md §6.2). Pushes
/// carry no content - the browser's push service (Google's, Mozilla's, Apple's) only learns that
/// something happened; the service worker fetches what over Familiar's own connection.
/// </summary>
public sealed class PushSubscription
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public Guid AccountId { get; init; }

    /// <summary>The push service URL for this browser. Unique: a browser re-subscribing replaces itself.</summary>
    public required string Endpoint { get; set; }

    /// <summary>The browser's public key and auth secret - stored for completeness; content-free pushes don't use them.</summary>
    public string? P256dh { get; set; }

    public string? Auth { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? LastPushedAt { get; set; }
}
