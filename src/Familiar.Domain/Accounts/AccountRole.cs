namespace Familiar.Domain.Accounts;

/// <summary>What an account may do on this server.</summary>
/// <remarks>
/// Stored as its name, not its ordinal, so inserting a member can never silently remap existing
/// rows. Familiar is a personal assistant rather than a shared world, so there are only two: the
/// person who administers the installation, and everyone else.
/// </remarks>
public enum AccountRole
{
    User = 0,
    Admin = 1,
}
