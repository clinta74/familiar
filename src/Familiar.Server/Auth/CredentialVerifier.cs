using Familiar.Domain.Accounts;
using Familiar.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Familiar.Server.Auth;

public enum CredentialOutcome
{
    Success,
    Invalid,
    Throttled,
    Disabled,
}

public sealed record CredentialResult(CredentialOutcome Outcome, Account? Account = null, TimeSpan? RetryAfter = null);

/// <summary>
/// A password hash for a user who doesn't exist, so an unknown username costs the same PBKDF2 work
/// as a wrong password.
/// </summary>
/// <remarks>
/// Without it, "no such user" returns before hashing and "wrong password" after, and the two are
/// distinguishable by the clock even though the responses are identical. Minted once by the same
/// hasher as every real hash, so it carries the same iteration count; the password is random and
/// discarded.
/// </remarks>
public sealed class DecoyPassword(IPasswordHasher<Account> hasher)
{
    private static readonly Account Decoy = new() { Username = "decoy", Email = "decoy@invalid" };

    private readonly string _hash = hasher.HashPassword(Decoy, Guid.NewGuid().ToString("N"));

    public void Verify(string password) => hasher.VerifyHashedPassword(Decoy, _hash, password);
}

/// <summary>Username + password → account, shared by browser login and device sign-in.</summary>
public sealed class CredentialVerifier(
    FamiliarDbContext db,
    IPasswordHasher<Account> hasher,
    DecoyPassword decoy,
    LoginThrottle throttle,
    TimeProvider clock,
    ILogger<CredentialVerifier> logger)
{
    public async Task<CredentialResult> VerifyAsync(string username, string password, CancellationToken ct)
    {
        // Before the lookup, so unknown names are throttled exactly like real ones.
        if (throttle.RetryAfter(username) is { } wait)
        {
            return new CredentialResult(CredentialOutcome.Throttled, RetryAfter: wait);
        }

        // Over-long input is refused without hashing it (PasswordPolicy.MaxLength). No password that
        // long was ever accepted, so this reveals nothing about the account.
        if (password.Length > PasswordPolicy.MaxLength)
        {
            RecordFailure(username);
            return new CredentialResult(CredentialOutcome.Invalid);
        }

        var account = await db.Accounts.SingleOrDefaultAsync(a => a.Username == username, ct);

        if (account is null)
        {
            decoy.Verify(password);
            RecordFailure(username);
            return new CredentialResult(CredentialOutcome.Invalid);
        }

        var result = hasher.VerifyHashedPassword(account, account.PasswordHash, password);
        if (result == PasswordVerificationResult.Failed)
        {
            RecordFailure(username);
            return new CredentialResult(CredentialOutcome.Invalid);
        }

        throttle.RecordSuccess(username);

        // Only disclosed to someone who knows the password.
        if (account.IsDisabled)
        {
            return new CredentialResult(CredentialOutcome.Disabled, account);
        }

        if (result == PasswordVerificationResult.SuccessRehashNeeded)
        {
            account.PasswordHash = hasher.HashPassword(account, password);
        }

        account.LastLoginAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);

        return new CredentialResult(CredentialOutcome.Success, account);
    }

    private void RecordFailure(string username)
    {
        if (throttle.RecordFailure(username))
        {
            logger.LoginBackoffStarted(username);
        }
    }
}
