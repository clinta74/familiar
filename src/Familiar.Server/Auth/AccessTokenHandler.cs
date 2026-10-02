using System.Text.Encodings.Web;
using Familiar.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Familiar.Server.Auth;

/// <summary>
/// Authenticates <c>Authorization: Bearer fam_…</c> device tokens (the Android and iOS apps).
/// </summary>
/// <remarks>
/// Reads the token and its account on every request. Unlike the cookie there's no revalidation
/// interval to wait out: a revoked device, a disabled account or a password change takes effect on
/// the phone's very next call. The role comes from the account row, never from the token.
/// </remarks>
public sealed class AccessTokenHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    IDbContextFactory<FamiliarDbContext> dbFactory,
    AuthOptions authOptions,
    TimeProvider clock)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)
{
    private const string BearerPrefix = "Bearer ";
    private const string RefusalItem = "familiar.token.refusal";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? header = Request.Headers.Authorization;
        if (header is null || !header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        if (!AccessTokenSecret.TryParse(header[BearerPrefix.Length..].Trim(), out var id, out var secret))
        {
            return Refuse("That is not a Familiar device token.");
        }

        var ct = Context.RequestAborted;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var token = await db.AccessTokens.Include(t => t.Account).SingleOrDefaultAsync(t => t.Id == id, ct);

        if (token?.Account is not { } account || !AccessTokenSecret.Matches(secret, token.SecretHash))
        {
            return Refuse("Unknown device token.");
        }

        var now = clock.GetUtcNow();

        // A revoked device, an expired one, and one signed out by a password change all read the
        // same to the phone: sign in again.
        if (!token.IsLive(now) || !string.Equals(token.SecurityStamp, account.SecurityStamp, StringComparison.Ordinal))
        {
            return Refuse("This device has been signed out.");
        }

        if (account.IsDisabled)
        {
            return Refuse("This account is disabled.");
        }

        if (now - (token.LastUsedAt ?? token.CreatedAt) >= authOptions.DeviceTokenTouchInterval)
        {
            // Sliding expiry: a phone in regular use never has to sign in again.
            token.LastUsedAt = now;
            token.ExpiresAt = now + authOptions.DeviceTokenLifetime;
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException exception)
            {
                // Bookkeeping; never worth failing the request over.
                Logger.DeviceTouchFailed(exception, token.Id);
            }
        }

        var principal = Principals.Build(account, Scheme.Name, token.Id);
        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers[HeaderNames.WWWAuthenticate] = "Bearer";
        var message = Context.Items[RefusalItem] as string ?? "Sign in required.";
        await Response.WriteAsJsonAsync(new ErrorResponse(message));
    }

    protected override async Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        await Response.WriteAsJsonAsync(new ErrorResponse("This device can't do that."));
    }

    private AuthenticateResult Refuse(string message)
    {
        Context.Items[RefusalItem] = message;
        return AuthenticateResult.Fail(message);
    }
}
