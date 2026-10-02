using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

namespace Familiar.Server.Auth;

/// <summary>
/// Token strings: <c>fam_&lt;id&gt;_&lt;secret&gt;</c> for devices, and the same shape with another
/// prefix for narrower tokens (<c>fhk_</c>: announce-only hook tokens).
/// </summary>
/// <remarks>
/// The prefix makes a leaked token greppable and recognisable to secret scanners. The id is the
/// row's primary key, so validating a token is one indexed lookup, not a scan of every hash. The
/// secret is 32 bytes from the CSPRNG, base64url.
/// </remarks>
public static class AccessTokenSecret
{
    public const string Prefix = "fam_";

    private const int IdLength = 32;

    public static (string Token, string SecretHash) Mint(Guid id) => Mint(id, Prefix);

    public static (string Token, string SecretHash) Mint(Guid id, string prefix)
    {
        var secret = Base64Url(RandomNumberGenerator.GetBytes(32));
        return ($"{prefix}{id:N}_{secret}", Hash(secret));
    }

    /// <summary>Splits a token into its id and secret. No I/O; says nothing about validity.</summary>
    public static bool TryParse(string? token, out Guid id, [NotNullWhen(true)] out string? secret) =>
        TryParse(token, Prefix, out id, out secret);

    public static bool TryParse(string? token, string prefix, out Guid id, [NotNullWhen(true)] out string? secret)
    {
        id = Guid.Empty;
        secret = null;

        if (token is null
            || token.Length < prefix.Length + IdLength + 2
            || !token.StartsWith(prefix, StringComparison.Ordinal)
            || token[prefix.Length + IdLength] != '_'
            || !Guid.TryParseExact(token.AsSpan(prefix.Length, IdLength), "N", out id))
        {
            return false;
        }

        secret = token[(prefix.Length + IdLength + 1)..];
        return true;
    }

    public static string Hash(string secret) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    public static bool Matches(string secret, string storedHash) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(Hash(secret)),
            Encoding.ASCII.GetBytes(storedHash));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
