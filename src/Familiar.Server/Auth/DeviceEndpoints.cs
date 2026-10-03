using Familiar.Domain.Accounts;
using Familiar.Persistence;
using Familiar.Server.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Familiar.Server.Auth;

public sealed record DeviceLoginRequest(string? Username, string? Password, string? DeviceName);

public sealed record DeviceSummary(
    Guid Id,
    string Name,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? LastUsedAt,
    bool IsCurrent);

/// <summary>Returned once, at sign-in. The token is never retrievable again.</summary>
public sealed record DeviceLoginResponse(string Token, DeviceSummary Device, AccountResponse Account);

/// <summary>
/// Sign-in for the phone apps, and the signed-in devices list. A device signs in with a username
/// and password like a browser, and gets a long-lived bearer token instead of a cookie.
/// </summary>
public static class DeviceEndpoints
{
    public const int MaxDeviceNameLength = 64;

    public static void MapDeviceEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/auth/devices");

        group.MapPost("/login", LoginAsync).RequireRateLimiting(RateLimiting.Auth);
        group.MapGet("/", ListAsync).RequireAuthorization();

        // A phone signing itself out is DELETE on its own id; the web settings page uses the
        // same route to sign out a lost phone.
        group.MapDelete("/{id:guid}", RevokeAsync).RequireAuthorization();
    }

    private static async Task<IResult> LoginAsync(
        DeviceLoginRequest request,
        HttpContext http,
        CredentialVerifier verifier,
        FamiliarDbContext db,
        AuthOptions options,
        TimeProvider clock,
        ILoggerFactory loggers,
        CancellationToken ct)
    {
        var deviceName = request.DeviceName?.Trim() ?? string.Empty;
        if (deviceName.Length is 0 or > MaxDeviceNameLength)
        {
            return AuthEndpoints.Error(StatusCodes.Status400BadRequest, $"Device names are 1-{MaxDeviceNameLength} characters.");
        }

        var result = await verifier.VerifyAsync(request.Username?.Trim() ?? string.Empty, request.Password ?? string.Empty, ct);
        if (result.Outcome != CredentialOutcome.Success)
        {
            return AuthEndpoints.CredentialFailure(http, result);
        }

        var account = result.Account!;
        var now = clock.GetUtcNow();

        var live = await db.AccessTokens.CountAsync(t => t.AccountId == account.Id && t.RevokedAt == null && t.ExpiresAt > now, ct);
        if (live >= options.DeviceTokensPerAccount)
        {
            return AuthEndpoints.Error(
                StatusCodes.Status409Conflict,
                $"You're signed in on {live} devices already. Sign one out from Settings first.");
        }

        var id = Guid.CreateVersion7();
        var (token, secretHash) = AccessTokenSecret.Mint(id);
        var device = new AccessToken
        {
            Id = id,
            AccountId = account.Id,
            Name = deviceName,
            SecretHash = secretHash,
            SecurityStamp = account.SecurityStamp,
            CreatedAt = now,
            ExpiresAt = now + options.DeviceTokenLifetime,
        };

        db.AccessTokens.Add(device);
        await db.SaveChangesAsync(ct);

        loggers.CreateLogger(typeof(DeviceEndpoints)).DeviceSignedIn(deviceName, account.Username);
        return TypedResults.Ok(new DeviceLoginResponse(token, Summarise(device, currentId: id), AccountResponse.From(account)));
    }

    private static async Task<IResult> ListAsync(HttpContext http, FamiliarDbContext db, CancellationToken ct)
    {
        if (http.User.AccountId() is not { } accountId)
        {
            return TypedResults.Unauthorized();
        }

        var devices = await db.AccessTokens.AsNoTracking()
            .Where(t => t.AccountId == accountId && t.RevokedAt == null)
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync(ct);

        var currentId = http.User.DeviceId();
        return TypedResults.Ok(devices.Select(d => Summarise(d, currentId)).ToList());
    }

    private static async Task<IResult> RevokeAsync(
        Guid id,
        HttpContext http,
        FamiliarDbContext db,
        TimeProvider clock,
        ILoggerFactory loggers,
        Calendars.CalendarStore calendars,
        CancellationToken ct)
    {
        if (http.User.AccountId() is not { } accountId)
        {
            return TypedResults.Unauthorized();
        }

        // Someone else's device is "not found", not "forbidden": its existence isn't the caller's business.
        var device = await db.AccessTokens.Include(t => t.Account)
            .SingleOrDefaultAsync(t => t.Id == id && t.AccountId == accountId, ct);

        if (device is null)
        {
            return TypedResults.NotFound();
        }

        if (device.RevokedAt is null)
        {
            device.RevokedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
            await calendars.RemoveDeviceAsync(id, ct);

            // Nothing more is pushed to a phone that's signed out.
            await db.PushSubscriptions.Where(s => s.DeviceId == id).ExecuteDeleteAsync(ct);
            loggers.CreateLogger(typeof(DeviceEndpoints)).DeviceSignedOut(id, device.Account!.Username);
        }

        return TypedResults.NoContent();
    }

    private static DeviceSummary Summarise(AccessToken device, Guid? currentId) =>
        new(device.Id, device.Name, device.CreatedAt, device.ExpiresAt, device.LastUsedAt, device.Id == currentId);
}
