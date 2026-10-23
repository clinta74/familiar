using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Familiar.Domain.Hooks;
using Familiar.Persistence;
using Familiar.Server.Auth;
using Familiar.Server.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Familiar.Server.Hooks;

public sealed record McpToolView(string Name, string ModelName, string? Description, bool ReadOnly, bool Enabled);

/// <summary>How Familiar proves itself to an MCP server.</summary>
public enum McpAuth
{
    /// <summary>It doesn't: the server's open (on the local network, say).</summary>
    None,

    /// <summary>A bearer token the user gave.</summary>
    Token,

    /// <summary>The user signed in to it in the browser (OAuth).</summary>
    SignIn,
}

/// <summary>An MCP server as Settings shows it. <c>SignInNeeded</c>: its tools can't be used until the user signs in (again), with <see cref="McpSignInView"/>.</summary>
public sealed record McpServerView(
    Guid Id, string Name, string UrlHost, IReadOnlyList<McpToolView> Tools, DateTimeOffset? RefreshedAt, string? LastError, McpAuth Auth, bool SignInNeeded);

/// <param name="Name">Short and lower-case: it prefixes the tool names the model sees.</param>
/// <param name="Url">The server's streamable-HTTP endpoint.</param>
/// <param name="Token">A bearer token, if the server needs one.</param>
/// <param name="ClientId">For a server that wants a sign-in but doesn't let apps register themselves: the OAuth client made for Familiar in its developer settings.</param>
/// <param name="ClientSecret">That client's secret, if it has one.</param>
public sealed record McpServerRequest(string? Name, string? Url, string? Token, string? ClientId = null, string? ClientSecret = null);

/// <param name="AuthorizationUrl">Where to send the browser to sign in; it comes back to Settings → Connections.</param>
public sealed record McpSignInView(string AuthorizationUrl);

/// <param name="Enabled">The tool names (as the server calls them) to switch on.</param>
public sealed record McpToolsRequest(IReadOnlyList<string>? Enabled);

public static partial class McpEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void MapMcpEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/mcp").RequireAuthorization();
        group.MapGet("/", ListAsync);
        group.MapPost("/", AddAsync);
        group.MapPost("/{id:guid}/refresh", RefreshAsync);
        group.MapPost("/{id:guid}/sign-in", SignInAsync);
        group.MapPut("/{id:guid}/tools", SetToolsAsync);
        group.MapDelete("/{id:guid}", DeleteAsync);

        // The browser comes back here from signing in, with no Familiar credentials it can be trusted
        // with: the state it brings, which only the sign-in that began it knows, says whose server it is.
        routes.MapGet(CallbackPath, CallbackAsync).AllowAnonymous();
    }

    public const string CallbackPath = "/api/mcp/oauth/callback";

    /// <summary>Where Familiar is, as the user's browser sees it: Auth:PublicUrl, else the address this request came to (through the proxy).</summary>
    internal static string PublicRoot(HttpRequest request, AuthOptions options) =>
        Uri.TryCreate(options.PublicUrl, UriKind.Absolute, out var root) && (root.Scheme == Uri.UriSchemeHttps || root.Scheme == Uri.UriSchemeHttp)
            ? root.GetLeftPart(UriPartial.Path).TrimEnd('/')
            : $"{request.Scheme}://{request.Host}{request.PathBase}";

    private static McpServerView View(McpServer server, McpTools mcp)
    {
        string host;
        try
        {
            host = new Uri(mcp.Protector.Unprotect(server.ProtectedUrl)).Host;
        }
        catch (CryptographicException)
        {
            host = "(unreadable)";
        }

        var tools = McpTools.ToolsOf(server)
            .Select(t => new McpToolView(t.Name, McpTools.ModelName(server.Name, t.Name), t.Description, t.ReadOnly, server.EnabledTools.Contains(t.Name)))
            .ToList();
        var auth = server.ProtectedToken is not null ? McpAuth.Token : server.ProtectedOAuth is not null ? McpAuth.SignIn : McpAuth.None;
        return new McpServerView(server.Id, server.Name, host, tools, server.RefreshedAt, server.LastError, auth, server.SignInNeeded);
    }

    private static async Task<List<McpServerView>> ListAsync(HttpContext http, IDbContextFactory<FamiliarDbContext> db, McpTools mcp, CancellationToken ct)
    {
        var accountId = http.User.AccountId()!.Value;
        await using var context = await db.CreateDbContextAsync(ct);
        var servers = await context.McpServers.AsNoTracking().Where(s => s.AccountId == accountId).OrderBy(s => s.CreatedAt).ToListAsync(ct);
        return [.. servers.Select(s => View(s, mcp))];
    }

    private static async Task<IResult> AddAsync(
        McpServerRequest request, HttpContext http, IDbContextFactory<FamiliarDbContext> db, McpTools mcp, McpOAuth oauth, TimeProvider clock, CancellationToken ct)
    {
        var name = request.Name?.Trim().ToLowerInvariant() ?? "";
        if (!ServerName().IsMatch(name))
        {
            return AuthEndpoints.Error(StatusCodes.Status400BadRequest, "Name it in lower-case letters and digits, starting with a letter (up to 16), e.g. homeassistant.");
        }

        if (OutboundUrl.Check(request.Url) is not { } url)
        {
            return AuthEndpoints.Error(StatusCodes.Status400BadRequest, "That isn't an address Familiar can connect to (http or https, not this machine).");
        }

        var accountId = http.User.AccountId()!.Value;
        await using var context = await db.CreateDbContextAsync(ct);
        var existing = await context.McpServers.Where(s => s.AccountId == accountId).Select(s => s.Name).ToListAsync(ct);
        if (existing.Count >= McpServer.MaxPerAccount)
        {
            return AuthEndpoints.Error(StatusCodes.Status400BadRequest, $"You can connect {McpServer.MaxPerAccount} MCP servers. Remove one first.");
        }

        if (existing.Contains(name))
        {
            return AuthEndpoints.Error(StatusCodes.Status409Conflict, "You already have a server with that name.");
        }

        var server = new McpServer
        {
            AccountId = accountId,
            Name = name,
            ProtectedUrl = mcp.Protector.Protect(url.AbsoluteUri),
            ProtectedToken = string.IsNullOrWhiteSpace(request.Token) ? null : mcp.Protector.Protect(request.Token.Trim()),
            CreatedAt = clock.GetUtcNow(),
        };

        await RefreshToolsAsync(server, mcp, clock, enableAll: true, ct);

        // Turned away with no token to give: if it says where to sign in, that's the next step.
        if (server.LastError is not null && server.ProtectedToken is null && await DiscoverAsync(url, oauth, ct) is { } grant)
        {
            if (!string.IsNullOrWhiteSpace(request.ClientId))
            {
                grant.ClientId = request.ClientId.Trim();
                grant.ClientSecret = string.IsNullOrWhiteSpace(request.ClientSecret) ? null : request.ClientSecret.Trim();
            }

            oauth.Write(server, grant);
            server.SignInNeeded = true;
            server.LastError = McpOAuth.SignInFirst;
        }

        context.McpServers.Add(server);
        await context.SaveChangesAsync(ct);
        return Results.Ok(View(server, mcp));
    }

    private static async Task<IResult> RefreshAsync(
        Guid id, HttpContext http, IDbContextFactory<FamiliarDbContext> db, McpTools mcp, TimeProvider clock, CancellationToken ct)
    {
        var accountId = http.User.AccountId()!.Value;
        await using var context = await db.CreateDbContextAsync(ct);
        var server = await context.McpServers.FirstOrDefaultAsync(s => s.Id == id && s.AccountId == accountId, ct);
        if (server is null)
        {
            return Results.NotFound();
        }

        await RefreshToolsAsync(server, mcp, clock, enableAll: false, ct);
        await context.SaveChangesAsync(ct);
        return Results.Ok(View(server, mcp));
    }

    /// <summary>
    /// Begins signing in to a server (the first time, or again): the browser's sent to the address
    /// this gives, and comes back to <see cref="CallbackAsync"/>.
    /// </summary>
    private static async Task<IResult> SignInAsync(
        Guid id, HttpContext http, IDbContextFactory<FamiliarDbContext> db, McpTools mcp, McpOAuth oauth, AuthOptions options, CancellationToken ct)
    {
        var accountId = http.User.AccountId()!.Value;
        await using var context = await db.CreateDbContextAsync(ct);
        var server = await context.McpServers.FirstOrDefaultAsync(s => s.Id == id && s.AccountId == accountId, ct);
        if (server is null)
        {
            return Results.NotFound();
        }

        if (server.ProtectedToken is not null)
        {
            return AuthEndpoints.Error(StatusCodes.Status400BadRequest, "This server uses an access token. Remove it and add it again without one to sign in instead.");
        }

        // One added before it wanted signing in to (or before Familiar could) finds out now.
        var grant = oauth.Read(server) ?? await DiscoverAsync(new Uri(mcp.Protector.Unprotect(server.ProtectedUrl)), oauth, ct);
        if (grant is null)
        {
            return AuthEndpoints.Error(StatusCodes.Status400BadRequest, "This server doesn't offer signing in.");
        }

        try
        {
            var address = await oauth.BeginAsync(grant, server.Id, PublicRoot(http.Request, options) + CallbackPath, ct);
            oauth.Write(server, grant);
            await context.SaveChangesAsync(ct);
            return Results.Ok(new McpSignInView(address.AbsoluteUri));
        }
        catch (Exception exception) when (exception is McpOAuthException or HttpRequestException
            || (exception is TaskCanceledException && !ct.IsCancellationRequested))
        {
            return AuthEndpoints.Error(StatusCodes.Status502BadGateway, exception is McpOAuthException ? exception.Message : $"Couldn't reach its sign-in: {exception.Message}");
        }
    }

    /// <summary>
    /// The browser, back from signing in: the code's swapped for tokens, the tools are listed, and the
    /// browser goes back to Settings → Connections, told what went wrong if anything did.
    /// </summary>
    private static async Task<IResult> CallbackAsync(
        string? code, string? state, string? error, [FromQuery(Name = "error_description")] string? errorDescription, string? iss,
        HttpContext http, IDbContextFactory<FamiliarDbContext> db, McpTools mcp, McpOAuth oauth, AuthOptions options, TimeProvider clock, CancellationToken ct)
    {
        var back = PublicRoot(http.Request, options) + "/settings/connections";
        await using var context = await db.CreateDbContextAsync(ct);
        var server = McpOAuth.ServerOf(state) is { } id ? await context.McpServers.FirstOrDefaultAsync(s => s.Id == id, ct) : null;
        var grant = server is null ? null : oauth.Read(server);
        if (server is null || grant is null || !oauth.IsPending(grant, state))
        {
            return Results.Redirect($"{back}?mcpError={Uri.EscapeDataString("That sign-in had run out, or wasn't one Familiar began. Try again from here.")}");
        }

        string? problem;
        try
        {
            if (error is not null)
            {
                McpOAuth.Abandon(grant);
                oauth.Write(server, grant);
                throw new McpOAuthException(error == "access_denied" ? "Signing in was cancelled." : $"The sign-in didn't work ({errorDescription ?? error}).", true);
            }

            await oauth.CompleteAsync(grant, state, code, iss, ct);
            oauth.Write(server, grant);
            server.SignInNeeded = false;
            server.LastError = null;

            // Saved first: listing the tools reads the tokens back.
            await context.SaveChangesAsync(ct);
            await RefreshToolsAsync(server, mcp, clock, enableAll: server.RefreshedAt is null, ct);
            problem = server.LastError;
        }
        catch (Exception exception) when (exception is McpOAuthException or HttpRequestException
            || (exception is TaskCanceledException && !ct.IsCancellationRequested))
        {
            problem = exception is McpOAuthException ? exception.Message : $"Couldn't reach its sign-in: {exception.Message}";
        }

        await context.SaveChangesAsync(CancellationToken.None);
        var outcome = problem is null ? "" : $"&mcpError={Uri.EscapeDataString(problem)}";
        return Results.Redirect($"{back}?mcp={Uri.EscapeDataString(server.Name)}{outcome}");
    }

    /// <summary>Whether the server wants an OAuth sign-in; not being able to tell counts as no.</summary>
    private static async Task<McpOAuthGrant?> DiscoverAsync(Uri url, McpOAuth oauth, CancellationToken ct)
    {
        try
        {
            return await oauth.DiscoverAsync(url, ct);
        }
        catch (Exception exception) when (exception is HttpRequestException || (exception is TaskCanceledException && !ct.IsCancellationRequested))
        {
            return null;
        }
    }

    private static async Task<IResult> SetToolsAsync(
        Guid id, McpToolsRequest request, HttpContext http, IDbContextFactory<FamiliarDbContext> db, McpTools mcp, CancellationToken ct)
    {
        var accountId = http.User.AccountId()!.Value;
        await using var context = await db.CreateDbContextAsync(ct);
        var server = await context.McpServers.FirstOrDefaultAsync(s => s.Id == id && s.AccountId == accountId, ct);
        if (server is null)
        {
            return Results.NotFound();
        }

        var known = McpTools.ToolsOf(server).Select(t => t.Name).ToHashSet();
        server.EnabledTools = [.. (request.Enabled ?? []).Where(known.Contains).Distinct()];
        await context.SaveChangesAsync(ct);
        return Results.Ok(View(server, mcp));
    }

    private static async Task<IResult> DeleteAsync(Guid id, HttpContext http, IDbContextFactory<FamiliarDbContext> db, CancellationToken ct)
    {
        var accountId = http.User.AccountId()!.Value;
        await using var context = await db.CreateDbContextAsync(ct);
        return await context.McpServers.Where(s => s.Id == id && s.AccountId == accountId).ExecuteDeleteAsync(ct) > 0
            ? Results.NoContent()
            : Results.NotFound();
    }

    /// <summary>
    /// Asks the server for its tools. A failure is recorded rather than thrown, so a server that's
    /// down can still be added, fixed and refreshed. New tools start switched on only when the server
    /// is first added, and then only as many as the model is offered.
    /// </summary>
    private static async Task RefreshToolsAsync(McpServer server, McpTools mcp, TimeProvider clock, bool enableAll, CancellationToken ct)
    {
        try
        {
            var tools = await mcp.ListAsync(server, ct);
            server.ToolsJson = JsonSerializer.Serialize(tools, Json);
            server.EnabledTools = enableAll
                ? [.. tools.Take(McpTools.MaxOfferedTools).Select(t => t.Name)]
                : [.. server.EnabledTools.Where(name => tools.Any(t => t.Name == name))];
            server.RefreshedAt = clock.GetUtcNow();
            server.LastError = null;
            server.SignInNeeded = false;
        }
        catch (McpOAuthException exception)
        {
            server.LastError = exception.Message;
            server.SignInNeeded = exception.SignInNeeded;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var message = $"Couldn't list its tools: {exception.Message}";
            server.LastError = message.Length <= 500 ? message : message[..500];
        }
    }

    [GeneratedRegex("^[a-z][a-z0-9]{0,15}$")]
    private static partial Regex ServerName();
}
