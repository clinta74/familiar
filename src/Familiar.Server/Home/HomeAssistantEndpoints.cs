using System.Security.Cryptography;
using System.Text.Json;
using Familiar.Domain.Hooks;
using Familiar.Persistence;
using Familiar.Server.Auth;
using Familiar.Server.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Familiar.Server.Home;

/// <summary>A device Familiar can work, as Settings lists it.</summary>
/// <param name="Name">What Home Assistant calls it.</param>
/// <param name="Area">Its room, if it has one.</param>
public sealed record HomeDeviceView(string Name, string? Area);

/// <summary>The account's Home Assistant connection.</summary>
/// <param name="Connected">Whether there is one; the rest is empty when not.</param>
/// <param name="UrlHost">The host it talks to.</param>
/// <param name="Devices">The devices exposed to Assist, when last asked.</param>
/// <param name="RefreshedAt">When the devices were last asked for.</param>
/// <param name="LastError">Why the last check went wrong, if it did.</param>
public sealed record HomeAssistantView(bool Connected, string? UrlHost, IReadOnlyList<HomeDeviceView> Devices, DateTimeOffset? RefreshedAt, string? LastError);

/// <param name="Url">Home Assistant's address, e.g. http://homeassistant.local:8123.</param>
/// <param name="Token">A long-lived access token from the Home Assistant profile page. Left out, the saved one is kept.</param>
public sealed record HomeAssistantRequest(string? Url, string? Token);

/// <summary>Settings' Home Assistant connection (BACKLOG item 29): connect, refresh the device list, disconnect.</summary>
public static class HomeAssistantEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void Map(IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/home-assistant").RequireAuthorization();
        group.MapGet("/", GetAsync);
        group.MapPut("/", ConnectAsync);
        group.MapPost("/refresh", RefreshAsync);
        group.MapDelete("/", DisconnectAsync);
    }

    private static readonly HomeAssistantView NotConnected = new(false, null, [], null, null);

    private static HomeAssistantView View(HomeAssistantLink link, HomeAssistant home)
    {
        string host;
        try
        {
            host = new Uri(home.Protector.Unprotect(link.ProtectedUrl)).Authority;
        }
        catch (CryptographicException)
        {
            host = "(unreadable)";
        }

        return new HomeAssistantView(true, host, [.. HomeAssistant.DevicesOf(link).Select(d => new HomeDeviceView(d.Name, d.Area))], link.RefreshedAt, link.LastError);
    }

    private static async Task<HomeAssistantView> GetAsync(HttpContext http, HomeAssistant home, CancellationToken ct) =>
        await home.LinkAsync(http.User.AccountId()!.Value, ct) is { } link ? View(link, home) : NotConnected;

    /// <summary>
    /// Connects, or changes the address or token. Refused unless Home Assistant answers with the
    /// token, so a typo shows up here rather than as a familiar that can't switch the lights on.
    /// </summary>
    private static async Task<IResult> ConnectAsync(
        HomeAssistantRequest request, HttpContext http, IDbContextFactory<FamiliarDbContext> db, HomeAssistant home, TimeProvider clock, CancellationToken ct)
    {
        // The base address: a pasted .../api or profile page is trimmed back to it.
        if (OutboundUrl.Check(request.Url) is not { } given)
        {
            return AuthEndpoints.Error(StatusCodes.Status400BadRequest, "That isn't an address Familiar can connect to (http or https, not this machine).");
        }

        var url = new Uri(given.GetLeftPart(UriPartial.Authority) + "/");
        var accountId = http.User.AccountId()!.Value;
        await using var context = await db.CreateDbContextAsync(ct);
        var link = await context.HomeAssistantLinks.FirstOrDefaultAsync(h => h.AccountId == accountId, ct);
        var token = request.Token?.Trim() is { Length: > 0 } t ? t
            : link is not null ? home.Protector.Unprotect(link.ProtectedToken)
            : null;
        if (token is null)
        {
            return AuthEndpoints.Error(StatusCodes.Status400BadRequest, "Give a long-lived access token: make one at the bottom of your Home Assistant profile.");
        }

        try
        {
            await home.CheckAsync(url, token, ct);
        }
        catch (HomeAssistantException exception)
        {
            return AuthEndpoints.Error(StatusCodes.Status400BadRequest, exception.Message);
        }

        if (link is null)
        {
            link = new HomeAssistantLink
            {
                AccountId = accountId,
                ProtectedUrl = "",
                ProtectedToken = "",
                CreatedAt = clock.GetUtcNow(),
            };
            context.HomeAssistantLinks.Add(link);
        }

        link.ProtectedUrl = home.Protector.Protect(url.AbsoluteUri);
        link.ProtectedToken = home.Protector.Protect(token);
        await RefreshDevicesAsync(link, url, token, home, clock, ct);
        await context.SaveChangesAsync(ct);
        return Results.Ok(View(link, home));
    }

    private static async Task<IResult> RefreshAsync(
        HttpContext http, IDbContextFactory<FamiliarDbContext> db, HomeAssistant home, TimeProvider clock, CancellationToken ct)
    {
        var accountId = http.User.AccountId()!.Value;
        await using var context = await db.CreateDbContextAsync(ct);
        if (await context.HomeAssistantLinks.FirstOrDefaultAsync(h => h.AccountId == accountId, ct) is not { } link)
        {
            return Results.NotFound();
        }

        await RefreshDevicesAsync(link, new Uri(home.Protector.Unprotect(link.ProtectedUrl)), home.Protector.Unprotect(link.ProtectedToken), home, clock, ct);
        await context.SaveChangesAsync(ct);
        return Results.Ok(View(link, home));
    }

    private static async Task<IResult> DisconnectAsync(HttpContext http, IDbContextFactory<FamiliarDbContext> db, CancellationToken ct)
    {
        var accountId = http.User.AccountId()!.Value;
        await using var context = await db.CreateDbContextAsync(ct);
        await context.HomeAssistantLinks.Where(h => h.AccountId == accountId).ExecuteDeleteAsync(ct);
        return Results.NoContent();
    }

    /// <summary>
    /// Asks which devices are exposed. A failure is recorded, not thrown: commands still work
    /// without the list (Assist knows its devices), only "check" and the model's view of them don't.
    /// </summary>
    private static async Task RefreshDevicesAsync(HomeAssistantLink link, Uri url, string token, HomeAssistant home, TimeProvider clock, CancellationToken ct)
    {
        try
        {
            link.DevicesJson = JsonSerializer.Serialize(await home.ExposedAsync(url, token, ct), Json);
            link.RefreshedAt = clock.GetUtcNow();
            link.LastError = null;
        }
        catch (HomeAssistantException exception)
        {
            link.LastError = exception.Message.Length <= 500 ? exception.Message : exception.Message[..500];
        }
    }
}
