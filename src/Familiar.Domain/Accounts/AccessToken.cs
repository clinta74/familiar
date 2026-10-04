namespace Familiar.Domain.Accounts;

/// <summary>
/// A bearer credential for a client that can't hold a cookie the way a browser does: the Android
/// and iOS apps, one per device.
/// </summary>
/// <remarks>
/// <para>
/// Only a hash of the secret is stored. It is SHA-256, not PBKDF2, deliberately: the secret is 256
/// random bits, so there is nothing for key stretching to protect, and stretching would add its
/// cost to every API request the phone makes.
/// </para>
/// <para>
/// Revocation is a timestamp rather than a delete, so the device list can still say when a phone
/// was signed out.
/// </para>
/// </remarks>
public sealed class AccessToken
{
    /// <summary>Minted by the server together with the secret, and embedded in the token string.</summary>
    public Guid Id { get; init; }

    public Guid AccountId { get; init; }

    public Account? Account { get; init; }

    /// <summary>What the user will recognise in their device list, e.g. "Pixel 9".</summary>
    public required string Name { get; set; }

    public required string SecretHash { get; init; }

    /// <summary>
    /// The account's <see cref="Account.SecurityStamp"/> when this token was issued, or when this
    /// device itself changed the password (which rotates the stamp for everything else).
    /// </summary>
    public required string SecurityStamp { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? LastUsedAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }

    public bool IsLive(DateTimeOffset now) => RevokedAt is null && ExpiresAt > now;
}
