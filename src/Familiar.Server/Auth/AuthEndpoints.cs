using System.Globalization;
using System.Text.RegularExpressions;
using Familiar.Domain.Accounts;
using Familiar.Persistence;
using Familiar.Server.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;
using Npgsql;

namespace Familiar.Server.Auth;

public sealed record RegisterRequest(string? Username, string? Email, string? Password);

public sealed record LoginRequest(string? Username, string? Password);

public sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);

public sealed record AccountResponse(Guid Id, string Username, string Email, AccountRole Role)
{
    public static AccountResponse From(Account account) => new(account.Id, account.Username, account.Email, account.Role);
}

public static partial class AuthEndpoints
{
    /// <summary>
    /// Serialises registrations, so "is this the first account?" and the insert that acts on the
    /// answer can't interleave - two simultaneous first registrations would otherwise both become
    /// admin. Registration is rare enough that a global lock costs nothing.
    /// </summary>
    private const long RegistrationLock = 0x46_61_6D_69_6C_69_61_72; // "Familiar"

    public static void MapAuthEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/auth");

        group.MapPost("/register", RegisterAsync).RequireRateLimiting(RateLimiting.Auth);
        group.MapPost("/login", LoginAsync).RequireRateLimiting(RateLimiting.Auth);

        // Cast: a handler whose only parameter is HttpContext binds to the RequestDelegate overload,
        // which discards the IResult and answers an empty 200.
        group.MapPost("/logout", (Delegate)LogoutAsync);

        group.MapGet("/me", MeAsync).RequireAuthorization();

        // From a browser or a phone: either way the current password is required, so a device
        // token alone can't change it.
        group.MapPost("/password", ChangePasswordAsync)
            .RequireAuthorization()
            .RequireRateLimiting(RateLimiting.Auth);
    }

    private static async Task<IResult> RegisterAsync(
        RegisterRequest request,
        HttpContext http,
        FamiliarDbContext db,
        IPasswordHasher<Account> hasher,
        AuthOptions options,
        TimeProvider clock,
        ILoggerFactory loggers,
        CancellationToken ct)
    {
        var username = request.Username?.Trim() ?? string.Empty;
        var email = request.Email?.Trim() ?? string.Empty;

        if (!UsernamePattern().IsMatch(username))
        {
            return Error(StatusCodes.Status400BadRequest, "Usernames are 3-24 letters, digits or underscores.");
        }

        if (email.Length is 0 or > 320 || !email.Contains('@', StringComparison.Ordinal))
        {
            return Error(StatusCodes.Status400BadRequest, "That doesn't look like an email address.");
        }

        if (PasswordPolicy.Problem(request.Password) is { } problem)
        {
            return Error(StatusCodes.Status400BadRequest, problem);
        }

        var account = new Account { Username = username, Email = email, CreatedAt = clock.GetUtcNow() };

        // Hashed before taking the lock: PBKDF2 is the slow part, and it doesn't need serialising.
        account.PasswordHash = hasher.HashPassword(account, request.Password!);

        // Under the retrying execution strategy a transaction has to be retried whole: if the
        // connection dies part-way, the lock, the checks and the insert all happen again.
        var refused = await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({RegistrationLock})", ct);

            var isFirstAccount = !await db.Accounts.AnyAsync(ct);

            if (!isFirstAccount && !options.AllowRegistration)
            {
                return Error(StatusCodes.Status403Forbidden, "Registration is closed on this server.");
            }

            if (await db.Accounts.AnyAsync(a => a.Username == username || a.Email == email, ct))
            {
                return AlreadyRegistered();
            }

            account.Role = isFirstAccount ? AccountRole.Admin : AccountRole.User;
            db.Accounts.Add(account);

            try
            {
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
            }
            catch (DbUpdateException exception)
                when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                return AlreadyRegistered();
            }

            var logger = loggers.CreateLogger(typeof(AuthEndpoints));
            logger.Registered(account.Username, account.Role.ToString());
            if (isFirstAccount)
            {
                logger.FirstAccountPromoted(account.Username);
            }

            return (IResult?)null;
        });

        if (refused is not null)
        {
            return refused;
        }

        await CookieSession.SignInAsync(http, account, clock);
        return TypedResults.Ok(AccountResponse.From(account));
    }

    private static async Task<IResult> LoginAsync(
        LoginRequest request,
        HttpContext http,
        CredentialVerifier verifier,
        TimeProvider clock,
        CancellationToken ct)
    {
        var result = await verifier.VerifyAsync(request.Username?.Trim() ?? string.Empty, request.Password ?? string.Empty, ct);

        if (result.Outcome != CredentialOutcome.Success)
        {
            return CredentialFailure(http, result);
        }

        await CookieSession.SignInAsync(http, result.Account!, clock);
        return TypedResults.Ok(AccountResponse.From(result.Account!));
    }

    private static async Task<IResult> LogoutAsync(HttpContext http)
    {
        await http.SignOutAsync(FamiliarAuth.CookieScheme);
        return TypedResults.NoContent();
    }

    private static async Task<IResult> MeAsync(HttpContext http, FamiliarDbContext db, CancellationToken ct)
    {
        var account = http.User.AccountId() is { } id
            ? await db.Accounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, ct)
            : null;

        return account is null ? TypedResults.Unauthorized() : TypedResults.Ok(AccountResponse.From(account));
    }

    private static async Task<IResult> ChangePasswordAsync(
        ChangePasswordRequest request,
        HttpContext http,
        FamiliarDbContext db,
        IPasswordHasher<Account> hasher,
        LoginThrottle throttle,
        TimeProvider clock,
        ILoggerFactory loggers,
        CancellationToken ct)
    {
        var account = http.User.AccountId() is { } id
            ? await db.Accounts.SingleOrDefaultAsync(a => a.Id == id, ct)
            : null;

        if (account is null)
        {
            return TypedResults.Unauthorized();
        }

        // The same fuse as login: a stolen session shouldn't be a free password oracle.
        if (throttle.RetryAfter(account.Username) is { } wait)
        {
            return Throttled(http, wait);
        }

        var current = request.CurrentPassword ?? string.Empty;
        if (current.Length > PasswordPolicy.MaxLength
            || hasher.VerifyHashedPassword(account, account.PasswordHash, current) == PasswordVerificationResult.Failed)
        {
            throttle.RecordFailure(account.Username);
            return Error(StatusCodes.Status400BadRequest, "That isn't your current password.");
        }

        throttle.RecordSuccess(account.Username);

        if (PasswordPolicy.Problem(request.NewPassword) is { } problem)
        {
            return Error(StatusCodes.Status400BadRequest, problem);
        }

        account.PasswordHash = hasher.HashPassword(account, request.NewPassword!);

        // Every other browser session and every device token is now stale...
        account.RotateSecurityStamp();

        // ...except the caller's, or the person who changed the password is the first one signed
        // out by it: a phone's token takes the new stamp, a browser's cookie is re-issued with it.
        if (http.User.DeviceId() is { } deviceId)
        {
            var token = await db.AccessTokens.SingleOrDefaultAsync(t => t.Id == deviceId && t.AccountId == account.Id, ct);
            token?.SecurityStamp = account.SecurityStamp;
        }

        await db.SaveChangesAsync(ct);

        if (http.User.DeviceId() is null)
        {
            await CookieSession.SignInAsync(http, account, clock);
        }

        loggers.CreateLogger(typeof(AuthEndpoints)).PasswordChanged(account.Username);
        return TypedResults.NoContent();
    }

    /// <summary>
    /// Shared with device sign-in. Unknown user and wrong password are deliberately the same
    /// response.
    /// </summary>
    internal static IResult CredentialFailure(HttpContext http, CredentialResult result) => result.Outcome switch
    {
        CredentialOutcome.Throttled => Throttled(http, result.RetryAfter ?? TimeSpan.Zero),
        CredentialOutcome.Disabled => Error(StatusCodes.Status403Forbidden, "This account is disabled."),
        _ => Error(StatusCodes.Status401Unauthorized, "Wrong username or password."),
    };

    internal static IResult Error(int status, string message) => TypedResults.Json(new ErrorResponse(message), statusCode: status);

    private static IResult Throttled(HttpContext http, TimeSpan wait)
    {
        var seconds = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds));
        http.Response.Headers[HeaderNames.RetryAfter] = seconds.ToString(CultureInfo.InvariantCulture);
        return Error(StatusCodes.Status429TooManyRequests, $"Too many failed attempts. Try again in {seconds} seconds.");
    }

    internal static IResult AlreadyRegistered() =>
        Error(StatusCodes.Status409Conflict, "That username or email is already registered.");

    [GeneratedRegex("^[A-Za-z0-9_]{3,24}$")]
    internal static partial Regex UsernamePattern();
}
