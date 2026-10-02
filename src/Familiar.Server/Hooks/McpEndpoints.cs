using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Familiar.Domain.Hooks;
using Familiar.Persistence;
using Familiar.Server.Auth;
using Familiar.Server.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Familiar.Server.Hooks;

public sealed record McpToolView(string Name, string ModelName, string? Description, bool ReadOnly, bool Enabled);

public sealed record McpServerView(Guid Id, string Name, string UrlHost, IReadOnlyList<McpToolView> Tools, DateTimeOffset? RefreshedAt, string? LastError);

/// <param name="Name">Short and lower-case: it prefixes the tool names the model sees.</param>
/// <param name="Url">The server's streamable-HTTP endpoint.</param>
/// <param name="Token">A bearer token, if the server needs one.</param>
public sealed record McpServerRequest(string? Name, string? Url, string? Token);

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
        group.MapPut("/{id:guid}/tools", SetToolsAsync);
        group.MapDelete("/{id:guid}", DeleteAsync);
    }

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
        return new McpServerView(server.Id, server.Name, host, tools, server.RefreshedAt, server.LastError);
    }

    private static async Task<List<McpServerView>> ListAsync(HttpContext http, IDbContextFactory<FamiliarDbContext> db, McpTools mcp, CancellationToken ct)
    {
        var accountId = http.User.AccountId()!.Value;
        await using var context = await db.CreateDbContextAsync(ct);
        var servers = await context.McpServers.AsNoTracking().Where(s => s.AccountId == accountId).OrderBy(s => s.CreatedAt).ToListAsync(ct);
        return [.. servers.Select(s => View(s, mcp))];
    }

    private static async Task<IResult> AddAsync(
        McpServerRequest request, HttpContext http, IDbContextFactory<FamiliarDbContext> db, McpTools mcp, TimeProvider clock, CancellationToken ct)
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
