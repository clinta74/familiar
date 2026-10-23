using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Familiar.Agent.Tools;
using Familiar.Domain.Hooks;
using Familiar.Domain.Tools;
using Familiar.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Familiar.Server.Hooks;

/// <summary>A tool an MCP server offers, as remembered between refreshes.</summary>
/// <param name="Name">Its name on the server.</param>
/// <param name="Description">What the server says it does.</param>
/// <param name="InputSchema">Its JSON-schema parameters.</param>
/// <param name="ReadOnly">The server says it only reads (<c>readOnlyHint</c>).</param>
public sealed record McpToolInfo(string Name, string? Description, JsonObject InputSchema, bool ReadOnly);

/// <summary>
/// The MCP client (PLAN.md §6.5): connects to the user's MCP servers over streamable HTTP, lists
/// their tools, and runs them for the familiar. A server's tools are offered to the model as
/// <c>&lt;server&gt;_&lt;tool&gt;</c>; any that doesn't say it's read-only asks the user first.
/// </summary>
/// <remarks>
/// A connection per use, closed straight after: a turn calls a tool or two at most, and holding
/// sessions open to servers that may restart isn't worth the bookkeeping.
/// </remarks>
public sealed partial class McpTools(
    IDbContextFactory<FamiliarDbContext> db,
    IDataProtectionProvider protection,
    McpOAuth oauth,
    IHttpClientFactory httpFactory,
    ILoggerFactory loggers)
{
    /// <summary>Small models choose worse among many tools; this many are offered at most, across servers.</summary>
    public const int MaxOfferedTools = 8;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal IDataProtector Protector { get; } = protection.CreateProtector("Familiar.Hooks.Mcp");

    /// <summary>The name the model sees: the server's name, then the tool's, reduced to what tool names allow.</summary>
    public static string ModelName(string server, string tool)
    {
        var name = $"{server}_{Unsafe().Replace(tool, "_")}";
        return name.Length <= 64 ? name : name[..64];
    }

    public static IReadOnlyList<McpToolInfo> ToolsOf(McpServer server) =>
        JsonSerializer.Deserialize<List<McpToolInfo>>(server.ToolsJson, Json) ?? [];

    /// <summary>Connects and asks what tools there are. Throws if it can't.</summary>
    public Task<IReadOnlyList<McpToolInfo>> ListAsync(McpServer server, CancellationToken ct) => UseAsync<IReadOnlyList<McpToolInfo>>(server, async client =>
    {
        var tools = await client.ListToolsAsync(cancellationToken: ct);
        return [.. tools.Take(50).Select(t => new McpToolInfo(
            t.ProtocolTool.Name,
            t.ProtocolTool.Description is { Length: > 500 } d ? d[..500] : t.ProtocolTool.Description,
            JsonNode.Parse(t.ProtocolTool.InputSchema.GetRawText()) as JsonObject ?? ToolSchema.Object(),
            t.ProtocolTool.Annotations?.ReadOnlyHint == true))];
    }, ct);

    /// <summary>The enabled MCP tools for an account, ready to offer the model.</summary>
    public async Task<IReadOnlyList<ITool>> ToolsForAsync(Guid accountId, CancellationToken ct)
    {
        await using var context = await db.CreateDbContextAsync(ct);
        var servers = await context.McpServers.AsNoTracking()
            .Where(s => s.AccountId == accountId)
            .OrderBy(s => s.CreatedAt)
            .ToListAsync(ct);

        return [.. servers
            .SelectMany(server => ToolsOf(server)
                .Where(t => server.EnabledTools.Contains(t.Name))
                .Select(t => (ITool)new McpTool(this, server, t)))
            .Take(MaxOfferedTools)];
    }

    internal async Task<ToolResult> CallAsync(McpServer server, McpToolInfo tool, JsonObject arguments, CancellationToken ct)
    {
        try
        {
            var args = JsonSerializer.Deserialize<Dictionary<string, object?>>(arguments.ToJsonString(), Json) ?? [];
            var result = await UseAsync(server, client => client.CallToolAsync(tool.Name, args, cancellationToken: ct).AsTask(), ct);

            var text = new StringBuilder();
            foreach (var block in result.Content)
            {
                if (block is TextContentBlock textBlock)
                {
                    text.AppendLine(textBlock.Text);
                }
            }

            var output = text.Length > 4000 ? text.ToString(0, 4000) + "…" : text.ToString().Trim();
            var summary = $"{server.Name}: {tool.Name}";
            return result.IsError == true
                ? ToolResult.Failure(output.Length > 0 ? output : "The tool reported an error.", $"{summary} failed")
                : ToolResult.Success(new JsonObject { ["ok"] = true, ["result"] = output }, summary);
        }
        catch (McpOAuthException exception) when (exception.SignInNeeded)
        {
            return ToolResult.Failure($"{server.Name} needs signing in: the user can do it in Settings → Connections.", $"{server.Name} needs signing in");
        }
        catch (Exception exception) when (exception is HttpRequestException or ModelContextProtocol.McpException or McpOAuthException or TimeoutException
            || (exception is TaskCanceledException && !ct.IsCancellationRequested))
        {
            return ToolResult.Failure($"Couldn't reach {server.Name}: {exception.Message}", $"{server.Name} unreachable");
        }
    }

    /// <summary>
    /// Connects, does something, disconnects. A server signed in to with OAuth gets a fresh access
    /// token, and if it turns that away (401) the token's refreshed and it's tried once more: the 401
    /// comes before anything has run, so trying again can't do anything twice.
    /// </summary>
    private async Task<T> UseAsync<T>(McpServer server, Func<McpClient, Task<T>> use, CancellationToken ct)
    {
        if (server.ProtectedToken is not null || server.ProtectedOAuth is null)
        {
            await using var plain = await ConnectAsync(server, server.ProtectedToken is { } token ? Protector.Unprotect(token) : null, ct);
            return await use(plain);
        }

        var bearer = await oauth.AccessTokenAsync(server.Id, rejected: null, ct);
        try
        {
            await using var client = await ConnectAsync(server, bearer, ct);
            return await use(client);
        }
        catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.Unauthorized)
        {
            bearer = await oauth.AccessTokenAsync(server.Id, rejected: bearer, ct);
            await using var client = await ConnectAsync(server, bearer, ct);
            return await use(client);
        }
    }

    private async Task<McpClient> ConnectAsync(McpServer server, string? bearer, CancellationToken ct)
    {
        var headers = new Dictionary<string, string>();
        if (bearer is not null)
        {
            headers["Authorization"] = $"Bearer {bearer}";
        }

        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(Protector.Unprotect(server.ProtectedUrl)),
                Name = server.Name,
                TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = headers,
            },
            httpFactory.CreateClient(nameof(McpTools)),
            loggers,
            ownsHttpClient: false);

        return await McpClient.CreateAsync(transport, new McpClientOptions
        {
            ClientInfo = new Implementation { Name = "Familiar", Version = Infrastructure.BuildInfo.Current.Version },
        }, loggers, ct);
    }

    [GeneratedRegex("[^A-Za-z0-9_-]")]
    private static partial Regex Unsafe();

    /// <summary>One MCP tool, as the familiar's tool registry sees it.</summary>
    private sealed class McpTool(McpTools owner, McpServer server, McpToolInfo tool) : ITool
    {
        public ToolDefinition Definition { get; } = new(
            ModelName(server.Name, tool.Name),
            $"{tool.Description ?? tool.Name} (from {server.Name})",
            tool.InputSchema,
            tool.ReadOnly ? ToolRisk.Read : ToolRisk.WriteExternal);

        public Task<ToolResult> InvokeAsync(ToolContext context, JsonObject arguments, CancellationToken ct) =>
            owner.CallAsync(server, tool, arguments, ct);

        public string Describe(JsonObject arguments)
        {
            var args = arguments.Count == 0 ? "" : $" with {arguments.ToJsonString()}";
            return $"Use {server.Name}'s “{tool.Name}”{(args.Length > 200 ? args[..200] + "…" : args)}";
        }
    }
}
