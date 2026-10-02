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
    public async Task<IReadOnlyList<McpToolInfo>> ListAsync(McpServer server, CancellationToken ct)
    {
        await using var client = await ConnectAsync(server, ct);
        var tools = await client.ListToolsAsync(cancellationToken: ct);
        return [.. tools.Take(50).Select(t => new McpToolInfo(
            t.ProtocolTool.Name,
            t.ProtocolTool.Description is { Length: > 500 } d ? d[..500] : t.ProtocolTool.Description,
            JsonNode.Parse(t.ProtocolTool.InputSchema.GetRawText()) as JsonObject ?? ToolSchema.Object(),
            t.ProtocolTool.Annotations?.ReadOnlyHint == true))];
    }

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
            await using var client = await ConnectAsync(server, ct);
            var args = JsonSerializer.Deserialize<Dictionary<string, object?>>(arguments.ToJsonString(), Json) ?? [];
            var result = await client.CallToolAsync(tool.Name, args, cancellationToken: ct);

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
        catch (Exception exception) when (exception is HttpRequestException or ModelContextProtocol.McpException or TimeoutException
            || (exception is TaskCanceledException && !ct.IsCancellationRequested))
        {
            return ToolResult.Failure($"Couldn't reach {server.Name}: {exception.Message}", $"{server.Name} unreachable");
        }
    }

    private async Task<McpClient> ConnectAsync(McpServer server, CancellationToken ct)
    {
        var headers = new Dictionary<string, string>();
        if (server.ProtectedToken is { } token)
        {
            headers["Authorization"] = $"Bearer {Protector.Unprotect(token)}";
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
