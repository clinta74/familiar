using Familiar.Domain.Accounts;
using Familiar.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Familiar.Server.Auth;

/// <param name="Id">The account.</param>
/// <param name="Username">Its username.</param>
/// <param name="Email">Its email.</param>
/// <param name="Role">user or admin.</param>
/// <param name="CreatedAt">When it was made.</param>
/// <param name="LastLoginAt">When it last signed in, if ever.</param>
/// <param name="IsDisabled">It can't sign in, and its sessions and devices have stopped working.</param>
/// <param name="Devices">How many phones it has signed in.</param>
/// <param name="IsYou">The admin looking at the list.</param>
public sealed record AdminUserView(
    Guid Id,
    string Username,
    string Email,
    AccountRole Role,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastLoginAt,
    bool IsDisabled,
    int Devices,
    bool IsYou);

/// <param name="Username">3-24 letters, digits or underscores.</param>
/// <param name="Email">Their email.</param>
/// <param name="Password">A first password, for them to change.</param>
/// <param name="Role">user (the default) or admin.</param>
public sealed record CreateUserRequest(string? Username, string? Email, string? Password, AccountRole? Role = null);

/// <param name="Email">A new email, or null to leave it.</param>
/// <param name="Role">A new role, or null to leave it.</param>
/// <param name="IsDisabled">Disable or re-enable, or null to leave it.</param>
public sealed record UpdateUserRequest(string? Email = null, AccountRole? Role = null, bool? IsDisabled = null);

/// <param name="Password">The new password.</param>
public sealed record ResetPasswordRequest(string? Password);

/// <summary>
/// User administration, for the server's admins (PLAN.md §9): add the household's accounts, reset a
/// forgotten password, disable or remove an account, make someone else an admin. With these,
/// registration can stay closed on the NAS for good.
/// </summary>
/// <remarks>
/// <para>
/// From a browser only, like changing your own password: a phone's long-lived token shouldn't be
/// able to administer the server on its own.
/// </para>
/// <para>
/// Admins can't disable, demote or delete themselves, so the server always keeps an admin who can
/// sign in. The caller's role is checked against the database on every request, not just the
/// session: someone demoted loses these powers at once rather than at their next revalidation.
/// </para>
/// </remarks>
public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/admin/users")
            .RequireAuthorization(Policies.Admin, Policies.Browser)
            .AddEndpointFilter(async (context, next) =>
                await CallerIsAdminAsync(context.HttpContext) ? await next(context) : Results.Forbid());

        group.MapGet("/", ListAsync);
        group.MapPost("/", CreateAsync).RequireRateLimiting(Infrastructure.RateLimiting.Auth);
        group.MapPut("/{id:guid}", UpdateAsync);
        group.MapPost("/{id:guid}/password", ResetPasswordAsync).RequireRateLimiting(Infrastructure.RateLimiting.Auth);
        group.MapDelete("/{id:guid}", DeleteAsync);
    }

    internal static async Task<bool> CallerIsAdminAsync(HttpContext http)
    {
        var db = http.RequestServices.GetRequiredService<FamiliarDbContext>();
        var id = http.User.AccountId();
        return id is { } accountId && await db.Accounts.AnyAsync(
            a => a.Id == accountId && a.Role == AccountRole.Admin && !a.IsDisabled, http.RequestAborted);
    }

    private static async Task<List<AdminUserView>> ListAsync(HttpContext http, FamiliarDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var me = http.User.AccountId()!.Value;
        var now = clock.GetUtcNow();
        return await db.Accounts.AsNoTracking()
            .OrderBy(a => a.CreatedAt)
            .Select(a => new AdminUserView(
                a.Id,
                a.Username,
                a.Email,
                a.Role,
                a.CreatedAt,
                a.LastLoginAt,
                a.IsDisabled,
                a.AccessTokens.Count(t => t.RevokedAt == null && t.ExpiresAt > now),
                a.Id == me))
            .ToListAsync(ct);
    }

    private static async Task<IResult> CreateAsync(
        CreateUserRequest request,
        HttpContext http,
        FamiliarDbContext db,
        IPasswordHasher<Account> hasher,
        TimeProvider clock,
        ILoggerFactory loggers,
        CancellationToken ct)
    {
        var username = request.Username?.Trim() ?? string.Empty;
        var email = request.Email?.Trim() ?? string.Empty;

        if (!AuthEndpoints.UsernamePattern().IsMatch(username))
        {
            return AuthEndpoints.Error(StatusCodes.Status400BadRequest, "Usernames are 3-24 letters, digits or underscores.");
        }

        if (!IsEmail(email))
        {
            return AuthEndpoints.Error(StatusCodes.Status400BadRequest, "That doesn't look like an email address.");
        }

        if (PasswordPolicy.Problem(request.Password) is { } problem)
        {
            return AuthEndpoints.Error(StatusCodes.Status400BadRequest, problem);
        }

        var account = new Account
        {
            Username = username,
            Email = email,
            Role = request.Role ?? AccountRole.User,
            CreatedAt = clock.GetUtcNow(),
        };
        account.PasswordHash = hasher.HashPassword(account, request.Password!);

        if (await db.Accounts.AnyAsync(a => a.Username == username || a.Email == email, ct))
        {
            return AuthEndpoints.AlreadyRegistered();
        }

        db.Accounts.Add(account);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return AuthEndpoints.AlreadyRegistered();
        }

        loggers.CreateLogger(typeof(AdminEndpoints)).AdminCreatedUser(http.User.Identity?.Name ?? "?", account.Username, account.Role.ToString());
        return TypedResults.Ok(new AdminUserView(account.Id, account.Username, account.Email, account.Role, account.CreatedAt, null, false, 0, false));
    }

    private static async Task<IResult> UpdateAsync(
        Guid id, UpdateUserRequest request, HttpContext http, FamiliarDbContext db, TimeProvider clock, ILoggerFactory loggers, CancellationToken ct)
    {
        var account = await db.Accounts.SingleOrDefaultAsync(a => a.Id == id, ct);
        if (account is null)
        {
            return TypedResults.NotFound();
        }

        var self = account.Id == http.User.AccountId();
        if (self && ((request.Role is { } role && role != account.Role) || request.IsDisabled == true))
        {
            return AuthEndpoints.Error(StatusCodes.Status400BadRequest,
                "You can't demote or disable yourself - make someone else an admin and ask them.");
        }

        if (request.Email is { } emailText)
        {
            var email = emailText.Trim();
            if (!IsEmail(email))
            {
                return AuthEndpoints.Error(StatusCodes.Status400BadRequest, "That doesn't look like an email address.");
            }

            if (await db.Accounts.AnyAsync(a => a.Id != id && a.Email == email, ct))
            {
                return AuthEndpoints.Error(StatusCodes.Status409Conflict, "Another account already has that email.");
            }

            account.Email = email;
        }

        var log = loggers.CreateLogger(typeof(AdminEndpoints));
        var admin = http.User.Identity?.Name ?? "?";

        if (request.Role is { } newRole && newRole != account.Role)
        {
            account.Role = newRole;
            log.AdminChangedRole(admin, account.Username, newRole.ToString());
        }

        if (request.IsDisabled is { } disabled && disabled != account.IsDisabled)
        {
            account.IsDisabled = disabled;

            // Disabling ends every session and device at once; re-enabling doesn't bring them back.
            if (disabled)
            {
                account.RotateSecurityStamp();
            }

            log.AdminSetDisabled(admin, account.Username, disabled);
        }

        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(await ViewAsync(db, account, http, clock.GetUtcNow(), ct));
    }

    /// <summary>Sets a new password and signs the account out everywhere, the same as changing it yourself.</summary>
    private static async Task<IResult> ResetPasswordAsync(
        Guid id, ResetPasswordRequest request, HttpContext http, FamiliarDbContext db, IPasswordHasher<Account> hasher,
        ILoggerFactory loggers, CancellationToken ct)
    {
        var account = await db.Accounts.SingleOrDefaultAsync(a => a.Id == id, ct);
        if (account is null)
        {
            return TypedResults.NotFound();
        }

        if (account.Id == http.User.AccountId())
        {
            return AuthEndpoints.Error(StatusCodes.Status400BadRequest, "Change your own password in Settings › Password.");
        }

        if (PasswordPolicy.Problem(request.Password) is { } problem)
        {
            return AuthEndpoints.Error(StatusCodes.Status400BadRequest, problem);
        }

        account.PasswordHash = hasher.HashPassword(account, request.Password!);
        account.RotateSecurityStamp();
        await db.SaveChangesAsync(ct);

        loggers.CreateLogger(typeof(AdminEndpoints)).AdminResetPassword(http.User.Identity?.Name ?? "?", account.Username);
        return TypedResults.NoContent();
    }

    /// <summary>Removes the account and everything of theirs: familiar, conversations, memories, calendars, devices.</summary>
    private static async Task<IResult> DeleteAsync(Guid id, HttpContext http, FamiliarDbContext db, ILoggerFactory loggers, CancellationToken ct)
    {
        if (id == http.User.AccountId())
        {
            return AuthEndpoints.Error(StatusCodes.Status400BadRequest, "You can't delete your own account here.");
        }

        var username = await db.Accounts.Where(a => a.Id == id).Select(a => a.Username).SingleOrDefaultAsync(ct);
        if (username is null)
        {
            return TypedResults.NotFound();
        }

        await db.Accounts.Where(a => a.Id == id).ExecuteDeleteAsync(ct);
        loggers.CreateLogger(typeof(AdminEndpoints)).AdminDeletedUser(http.User.Identity?.Name ?? "?", username);
        return TypedResults.NoContent();
    }

    private static async Task<AdminUserView> ViewAsync(FamiliarDbContext db, Account a, HttpContext http, DateTimeOffset now, CancellationToken ct)
    {
        var devices = await db.AccessTokens.CountAsync(t => t.AccountId == a.Id && t.RevokedAt == null && t.ExpiresAt > now, ct);
        return new AdminUserView(a.Id, a.Username, a.Email, a.Role, a.CreatedAt, a.LastLoginAt, a.IsDisabled, devices, a.Id == http.User.AccountId());
    }

    private static bool IsEmail(string email) => email.Length is > 0 and <= 320 && email.Contains('@', StringComparison.Ordinal);
}
