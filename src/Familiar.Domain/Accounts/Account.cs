namespace Familiar.Domain.Accounts;

/// <summary>A login. One person, one account; their familiar and everything it knows hang off it.</summary>
public sealed class Account
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <summary>Case-insensitive (citext) and unique.</summary>
    public required string Username { get; set; }

    /// <summary>Case-insensitive (citext) and unique.</summary>
    public required string Email { get; set; }

    /// <summary>ASP.NET Identity's PBKDF2 format, including its own version and iteration count.</summary>
    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>
    /// An opaque value that changes whenever every existing session should stop working: a password
    /// change, "sign out everywhere", or the account being disabled.
    /// </summary>
    /// <remarks>
    /// Cookies carry the stamp they were issued with, and device tokens snapshot it at issue; both
    /// are refused once it no longer matches. A random string rather than a timestamp, so there is
    /// no precision mismatch between .NET's 100 ns ticks and Postgres's microseconds to get wrong.
    /// </remarks>
    public string SecurityStamp { get; set; } = NewSecurityStamp();

    public AccountRole Role { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? LastLoginAt { get; set; }

    /// <summary>A disabled account cannot sign in, and its open sessions and tokens stop working.</summary>
    public bool IsDisabled { get; set; }

    public List<AccessToken> AccessTokens { get; } = [];

    public void RotateSecurityStamp() => SecurityStamp = NewSecurityStamp();

    private static string NewSecurityStamp() => Guid.NewGuid().ToString("N");
}
