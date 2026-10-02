using System.Globalization;
using Familiar.Domain.Accounts;
using Familiar.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;

namespace Familiar.Server.Auth;

/// <summary>
/// Signing a browser in, and keeping an open cookie session honest about the account behind it.
/// </summary>
/// <remarks>
/// <para>
/// The cookie carries the account's security stamp from sign-in. Once a minute (by default) the
/// session re-reads the account and is rejected if the stamp moved, the account was disabled or
/// deleted; a changed role is picked up in place. That's how a password change reaches sessions
/// that are already open.
/// </para>
/// <para>
/// The stamp lives in the ticket's properties, not in its issue time: sliding renewal re-issues
/// the ticket with a fresh issue time, which would let a pre-change session outlive the change.
/// Property items survive renewal.
/// </para>
/// </remarks>
public static class CookieSession
{
    private const string StampItem = "familiar.stamp";
    private const string RevalidatedItem = "familiar.revalidated";

    public static Task SignInAsync(HttpContext http, Account account, TimeProvider clock)
    {
        var properties = new AuthenticationProperties { IsPersistent = true };
        properties.Items[StampItem] = account.SecurityStamp;
        properties.Items[RevalidatedItem] = clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);

        return http.SignInAsync(
            FamiliarAuth.CookieScheme,
            Principals.Build(account, FamiliarAuth.CookieScheme),
            properties);
    }

    /// <summary>The cookie scheme's <c>OnValidatePrincipal</c>.</summary>
    public static async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        var services = context.HttpContext.RequestServices;
        var options = services.GetRequiredService<AuthOptions>();
        var now = services.GetRequiredService<TimeProvider>().GetUtcNow();

        if (!IsDue(context.Properties, options.RevalidationInterval, now))
        {
            return;
        }

        // From the context, not HttpContext.User: authentication is still in progress, so User
        // isn't populated yet.
        if (context.Principal?.AccountId() is not { } accountId)
        {
            await RejectAsync(context);
            return;
        }

        var factory = services.GetRequiredService<IDbContextFactory<FamiliarDbContext>>();
        await using var db = await factory.CreateDbContextAsync(context.HttpContext.RequestAborted);
        var account = await db.Accounts.AsNoTracking()
            .SingleOrDefaultAsync(a => a.Id == accountId, context.HttpContext.RequestAborted);

        if (account is null
            || account.IsDisabled
            || !context.Properties.Items.TryGetValue(StampItem, out var stamp)
            || !string.Equals(stamp, account.SecurityStamp, StringComparison.Ordinal))
        {
            await RejectAsync(context);
            return;
        }

        context.Properties.Items[RevalidatedItem] = now.ToString("O", CultureInfo.InvariantCulture);

        if (!context.Principal.IsInRole(account.Role.ToString()))
        {
            context.ReplacePrincipal(Principals.Build(account, FamiliarAuth.CookieScheme));
        }

        // Without this the new revalidation time is discarded with the ticket, and every request
        // after the first interval goes to the database.
        context.ShouldRenew = true;
    }

    private static bool IsDue(AuthenticationProperties properties, TimeSpan interval, DateTimeOffset now) =>
        interval <= TimeSpan.Zero
        || !properties.Items.TryGetValue(RevalidatedItem, out var raw)
        || !DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var last)
        || now - last >= interval;

    private static async Task RejectAsync(CookieValidatePrincipalContext context)
    {
        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(FamiliarAuth.CookieScheme);
    }
}
