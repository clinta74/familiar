using Familiar.Domain.Accounts;
using Familiar.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Net.Http.Headers;

namespace Familiar.Server.Auth;

public static class FamiliarAuth
{
    /// <summary>The default scheme: picks cookie or device token per request.</summary>
    public const string PolicyScheme = "familiar";

    public const string CookieScheme = CookieAuthenticationDefaults.AuthenticationScheme;

    public const string TokenScheme = "device-token";

    public static IServiceCollection AddFamiliarAuth(
        this IServiceCollection services, AuthOptions options, IHostEnvironment environment)
    {
        services.AddSingleton(options);
        services.AddSingleton<IPasswordHasher<Account>, PasswordHasher<Account>>();
        services.AddSingleton<DecoyPassword>();
        services.AddSingleton<LoginThrottle>();
        services.AddScoped<CredentialVerifier>();

        // Keys in Postgres: a recreated container would otherwise mint a new key ring and every
        // session cookie in existence would stop decrypting.
        services.AddDataProtection()
            .SetApplicationName("Familiar")
            .PersistKeysToDbContext<FamiliarDbContext>();

        services.AddAuthentication(PolicyScheme)
            .AddPolicyScheme(PolicyScheme, "Cookie or device token", policy =>
                // Browsers send the cookie (a WebSocket from a browser can't set headers); the
                // phone apps send Authorization. Any Authorization header goes to the token
                // scheme, which refuses what it doesn't recognise rather than falling back.
                policy.ForwardDefaultSelector = http =>
                    http.Request.Headers.ContainsKey(HeaderNames.Authorization) ? TokenScheme : CookieScheme)
            .AddCookie(CookieScheme, cookie =>
            {
                cookie.Cookie.Name = options.CookieName;
                cookie.Cookie.HttpOnly = true;
                cookie.Cookie.SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Lax;

                // Always in production. Behind nginx the request reaches Kestrel as plain HTTP, so
                // SameAsRequest would never set Secure there even though the browser spoke HTTPS.
                cookie.Cookie.SecurePolicy = environment.IsProduction()
                    ? CookieSecurePolicy.Always
                    : CookieSecurePolicy.SameAsRequest;

                cookie.ExpireTimeSpan = options.SessionLifetime;
                cookie.SlidingExpiration = true;

                // An API answers 401/403. The default is a 302 to /Account/Login, which a fetch
                // follows to a 200 HTML page.
                cookie.Events.OnRedirectToLogin = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                };
                cookie.Events.OnRedirectToAccessDenied = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                };
                cookie.Events.OnValidatePrincipal = CookieSession.ValidateAsync;
            })
            .AddScheme<AuthenticationSchemeOptions, AccessTokenHandler>(TokenScheme, configureOptions: null);

        services.AddAuthorizationBuilder()
            .AddPolicy(Policies.Admin, policy => policy.RequireRole(nameof(AccountRole.Admin)))
            .AddPolicy(Policies.Browser, policy => policy
                .AddAuthenticationSchemes(CookieScheme)
                .RequireAuthenticatedUser());

        return services;
    }
}

public static class Policies
{
    public const string Admin = "admin";

    /// <summary>
    /// Signed in with a browser session, not a device token. For things a phone's long-lived
    /// credential shouldn't be able to do on its own, like changing the account password.
    /// </summary>
    public const string Browser = "browser";
}

public sealed record ErrorResponse(string Error);
