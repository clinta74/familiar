namespace Familiar.Domain.Accounts;

/// <summary>
/// A single-use link that lets one person register while registration is invite only: made by an
/// admin, good until <see cref="ExpiresAt"/>, used once or cancelled. Kept afterwards, so the admin
/// can see who came in on which invite.
/// </summary>
/// <remarks>
/// Like a device token, only a hash of its secret is stored, and the id is part of the code
/// (<c>finv_&lt;id&gt;_&lt;secret&gt;</c>): the link is shown once, when it's made.
/// </remarks>
public sealed class Invite
{
    /// <summary>Minted by the server together with the secret, and embedded in the code.</summary>
    public Guid Id { get; init; }

    public required string SecretHash { get; init; }

    /// <summary>Who it's for, as the admin put it ("Mum", "Sam's tablet"); optional.</summary>
    public string? Note { get; set; }

    /// <summary>The admin who made it; null once their account is gone.</summary>
    public Guid? CreatedBy { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset ExpiresAt { get; init; }

    public DateTimeOffset? UsedAt { get; set; }

    /// <summary>The account registered with it; null if unused, or once that account is gone.</summary>
    public Guid? UsedBy { get; set; }

    /// <summary>Who registered with it, kept even if the account is later removed.</summary>
    public string? UsedByUsername { get; set; }

    public DateTimeOffset? CancelledAt { get; set; }
}
