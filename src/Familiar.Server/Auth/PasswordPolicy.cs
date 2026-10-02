namespace Familiar.Server.Auth;

/// <summary>One policy for registration and password changes alike.</summary>
public static class PasswordPolicy
{
    public const int MinLength = 8;

    /// <summary>
    /// Not a security rule but a denial-of-service guard: PBKDF2 hashes whatever it is given, so an
    /// unbounded password is an unbounded amount of CPU per request.
    /// </summary>
    public const int MaxLength = 256;

    /// <summary>Length only. Composition rules push people towards worse passwords, not better ones.</summary>
    public static string? Problem(string? password) => password switch
    {
        null or { Length: < MinLength } => $"Passwords must be at least {MinLength} characters.",
        { Length: > MaxLength } => $"Passwords can be at most {MaxLength} characters.",
        _ => null,
    };
}
