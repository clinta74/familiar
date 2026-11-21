namespace Familiar.Domain.Shopping;

/// <summary>
/// One thing on the account's shopping list: added by voice ("add milk"), on the website or in the
/// app, and ticked off when got - kept, struck through, until the ticked ones are cleared.
/// </summary>
public sealed class ShoppingItem
{
    public const int MaxText = 120;

    /// <summary>A sanity limit, not a feature.</summary>
    public const int MaxPerAccount = 200;

    public Guid Id { get; init; } = Guid.CreateVersion7();

    public Guid AccountId { get; init; }

    /// <summary>What to get, as said: "milk", "AA batteries", "2 tins of tomatoes".</summary>
    public required string Text { get; set; }

    public DateTimeOffset AddedAt { get; init; }

    /// <summary>When it was ticked off; null while it's still to get.</summary>
    public DateTimeOffset? GotAt { get; set; }
}
