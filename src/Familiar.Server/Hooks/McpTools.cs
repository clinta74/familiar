using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Familiar.Agent.Llm;
using Familiar.Agent.Tools;
using Familiar.Domain.Hooks;
using Familiar.Domain.Tools;
using Familiar.Persistence;
using Familiar.Server.Tools;
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
    IEmbedder embedder,
    IHttpClientFactory httpFactory,
    ILoggerFactory loggers)
{
    /// <summary>
    /// Small models choose worse among many tools, and every tool offered is read with every prompt:
    /// this many are offered at most, across servers, each in short (<see cref="Compact"/>). More can
    /// be switched on; then each turn gets those that fit it best.
    /// </summary>
    public const int MaxOfferedTools = 12;

    /// <summary>
    /// And this much of their definitions (name, description, parameters), about 1,700 tokens: the
    /// model's context is 8,192 on the NAS, and eight of GitHub's full schemas came to half of it, so
    /// the prompt filled it and the reply was cut off mid tool call.
    /// </summary>
    public const int MaxOfferedToolChars = 6000;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal IDataProtector Protector { get; } = protection.CreateProtector("Familiar.Hooks.Mcp");

    private readonly ILogger logger = loggers.CreateLogger<McpTools>();

    /// <summary>Each account's last offer, by model name: kept while it still fits (<see cref="Choose"/>).</summary>
    private readonly ConcurrentDictionary<Guid, IReadOnlyList<string>> lastOffered = new();

    /// <summary>Tools' embeddings, by the words embedded: worked out the first time a tool's in the running.</summary>
    private readonly ConcurrentDictionary<string, float[]> toolEmbeddings = new();

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

    /// <summary>Every MCP tool the account has switched on, server by server: what the familiar may run.</summary>
    public async Task<IReadOnlyList<ITool>> EnabledAsync(Guid accountId, CancellationToken ct) =>
        [.. (await EnabledByServerAsync(accountId, ct)).SelectMany(tools => tools)];

    /// <summary>
    /// The MCP tools to offer the model this turn. When more are switched on than it's offered, the
    /// ones that fit what was asked best (by embedding), kept from the last turn while they still fit
    /// (see <see cref="Choose"/>); when that can't be worked out, a share of each server's.
    /// </summary>
    public async Task<IReadOnlyList<ITool>> OfferedAsync(Guid accountId, string? asked, CancellationToken ct)
    {
        var perServer = await EnabledByServerAsync(accountId, ct);
        var enabled = perServer.SelectMany(tools => tools).ToList();
        var sizes = enabled.ToDictionary(t => t.Definition.Name, t => SizeOf(t.Definition), StringComparer.Ordinal);
        if (enabled.Count <= MaxOfferedTools && sizes.Values.Sum() <= MaxOfferedToolChars)
        {
            return enabled;
        }

        if (!string.IsNullOrWhiteSpace(asked) && await ScoreAsync(enabled, asked, ct) is { } scores)
        {
            var previous = lastOffered.GetValueOrDefault(accountId);
            var chosen = Choose(scores, sizes, previous);
            if (previous is null || !chosen.SequenceEqual(previous))
            {
                logger.McpToolsOffered(chosen.Count, enabled.Count, string.Join(", ", chosen));
            }

            lastOffered[accountId] = chosen;
            var byName = enabled.ToDictionary(t => t.Definition.Name, StringComparer.Ordinal);
            return [.. chosen.Select(name => byName[name])];
        }

        // No message (a picture alone) or no embeddings: last turn's offer if it still holds, else a
        // share of each server's, read-only first.
        if (lastOffered.GetValueOrDefault(accountId) is { } kept
            && enabled.Where(t => kept.Contains(t.Definition.Name)).ToList() is { } stillThere && stillThere.Count == kept.Count)
        {
            return [.. kept.Select(name => stillThere.First(t => t.Definition.Name == name))];
        }

        return Shares(perServer, sizes);
    }

    /// <summary>
    /// The tools to offer, by name, from how well each fits what was asked. Last turn's choice stands,
    /// in the same order, while its best fit is nearly as good as the best overall: the tools head the
    /// prompt, so a different set means the model reading the whole conversation again (about a second
    /// on the NAS), and small talk fits nothing in particular. A new choice takes the best fits that
    /// fit the budget (<see cref="MaxOfferedTools"/>, <see cref="MaxOfferedToolChars"/>), passing over
    /// one too big for what's left, and is ordered by name, so the same tools always read the same.
    /// </summary>
    internal static IReadOnlyList<string> Choose(
        IReadOnlyDictionary<string, double> scores, IReadOnlyDictionary<string, int> sizes, IReadOnlyList<string>? previous)
    {
        var best = scores.Values.Max();
        if (previous is not null && previous.All(scores.ContainsKey)
            && previous.Max(name => scores[name]) >= best - (Math.Abs(best) * StickyMargin))
        {
            return previous;
        }

        return [.. WithinBudget(scores.OrderByDescending(s => s.Value).ThenBy(s => s.Key, StringComparer.Ordinal).Select(s => s.Key), sizes)
            .Order(StringComparer.Ordinal)];
    }

    /// <summary>Names in the order given, as many as are offered and as fit in the space for them.</summary>
    private static List<string> WithinBudget(IEnumerable<string> names, IReadOnlyDictionary<string, int> sizes)
    {
        var taken = new List<string>();
        var used = 0;
        foreach (var name in names)
        {
            if (taken.Count == MaxOfferedTools)
            {
                break;
            }

            if (used + sizes[name] <= MaxOfferedToolChars)
            {
                taken.Add(name);
                used += sizes[name];
            }
        }

        return taken;
    }

    /// <summary>What a tool's definition costs the prompt, near enough: its name, description and schema.</summary>
    internal static int SizeOf(ToolDefinition definition) =>
        definition.Name.Length + definition.Description.Length + definition.Parameters.ToJsonString().Length;

    /// <summary>Last turn's tools stay while their best fit is within this fraction of the best.</summary>
    internal const double StickyMargin = 0.1;

    private async Task<List<List<ITool>>> EnabledByServerAsync(Guid accountId, CancellationToken ct)
    {
        await using var context = await db.CreateDbContextAsync(ct);
        var servers = await context.McpServers.AsNoTracking()
            .Where(s => s.AccountId == accountId)
            .OrderBy(s => s.CreatedAt)
            .ToListAsync(ct);

        // Within a server, the ones that only read first: they're what's left out last.
        return [.. servers.Select(server => ToolsOf(server)
            .Where(t => server.EnabledTools.Contains(t.Name))
            .OrderBy(t => !t.ReadOnly)
            .Select(t => (ITool)new McpTool(this, server, t))
            .ToList())];
    }

    /// <summary>Taken in turns, a tool from each server and round again, so no one server fills every place.</summary>
    private static List<ITool> Shares(List<List<ITool>> perServer, IReadOnlyDictionary<string, int> sizes)
    {
        var inTurns = new List<ITool>();
        for (var round = 0; perServer.Any(tools => tools.Count > round); round++)
        {
            inTurns.AddRange(perServer.Where(tools => tools.Count > round).Select(tools => tools[round]));
        }

        var taken = WithinBudget(inTurns.Select(t => t.Definition.Name), sizes).ToHashSet(StringComparer.Ordinal);
        return [.. inTurns.Where(t => taken.Contains(t.Definition.Name))];
    }

    /// <summary>How well each tool fits what was asked (cosine similarity), by model name; null if it can't be told.</summary>
    private async Task<Dictionary<string, double>?> ScoreAsync(List<ITool> tools, string asked, CancellationToken ct)
    {
        try
        {
            var query = await embedder.EmbedAsync(asked.Length > 1000 ? asked[..1000] : asked, EmbeddingPurpose.Query, ct);
            var scores = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var tool in tools)
            {
                // What the tool is, in words: its server, its name split into words, what it says it does.
                var definition = tool.Definition;
                var text = $"{definition.Name.Replace('_', ' ').Replace('-', ' ')}. {definition.Description}";
                if (!toolEmbeddings.TryGetValue(text, out var vector))
                {
                    vector = await embedder.EmbedAsync(text, EmbeddingPurpose.Document, ct);
                    toolEmbeddings[text] = vector;
                }

                scores[definition.Name] = Cosine(query, vector);
            }

            return scores;
        }
        catch (LlmUnavailableException exception)
        {
            logger.McpToolsUnranked(exception.Message);
            return null;
        }
    }

    private static double Cosine(float[] a, float[] b)
    {
        double dot = 0, aa = 0, bb = 0;
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            dot += a[i] * b[i];
            aa += a[i] * a[i];
            bb += b[i] * b[i];
        }

        return aa == 0 || bb == 0 ? 0 : dot / Math.Sqrt(aa * bb);
    }

    internal async Task<ToolResult> CallAsync(McpServer server, McpToolInfo tool, JsonObject arguments, CancellationToken ct)
    {
        try
        {
            var args = JsonSerializer.Deserialize<Dictionary<string, object?>>(arguments.ToJsonString(), Json) ?? [];
            var result = await UseAsync(server, client =>
            {
                // A new connection hasn't listed the tools, so the client doesn't know which arguments
                // the server wants as Mcp-Param-* headers too (x-mcp-header): told, it sends them.
                // GitHub's server turns a call away (400) without them.
                client.AddKnownTools([new Tool { Name = tool.Name, InputSchema = JsonSerializer.SerializeToElement(tool.InputSchema) }]);
                return client.CallToolAsync(tool.Name, args, cancellationToken: ct).AsTask();
            }, ct);

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
            if (result.IsError != true)
            {
                return ToolResult.Success(new JsonObject { ["ok"] = true, ["result"] = output }, summary);
            }

            // It was offered in short: with the error, what it takes in full, so the next try can be right.
            var error = $"{(output.Length > 0 ? output : "The tool reported an error.")} Its parameters: {ParameterList(Slim(tool.InputSchema))}";
            return ToolResult.Failure(error.Length > 4000 ? error[..4000] + "..." : error, $"{summary} failed");
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

    /// <summary>Schema words that only document, or that the model doesn't need to fill a call in.</summary>
    private static readonly HashSet<string> Unneeded =
        ["examples", "title", "$schema", "$comment", "additionalProperties", "default", "minLength", "maxLength", "pattern", "format", "readOnly", "deprecated"];

    /// <summary>
    /// A tool's schema as the model is shown it: every parameter, its type and whether it's required,
    /// but each description to its first sentence (80 characters at most, then "..."), no vendor extensions
    /// (<c>x-…</c>) or documentation, and "this or null" as just "this" (it's optional anyway).
    /// GitHub's came out about a third smaller. The server still checks what's sent.
    /// </summary>
    internal static JsonObject Slim(JsonObject schema) => (JsonObject)SlimNode(schema)!;

    private static JsonNode? SlimNode(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj["anyOf"] is JsonArray anyOf && anyOf.Where(alt => !IsNullType(alt)).ToList() is [JsonObject only])
                {
                    var merged = new JsonObject();
                    foreach (var (key, value) in obj.Where(p => p.Key != "anyOf").Concat(only))
                    {
                        merged[key] = value?.DeepClone();
                    }

                    return SlimNode(merged);
                }

                var slim = new JsonObject();
                foreach (var (key, value) in obj)
                {
                    if (Unneeded.Contains(key) || key.StartsWith("x-", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    slim[key] = key == "description" && value?.GetValueKind() == JsonValueKind.String
                        ? FirstSentence(value.GetValue<string>())
                        : SlimNode(value);
                }

                return slim;
            case JsonArray array:
                return new JsonArray([.. array.Select(SlimNode)]);
            default:
                return node?.DeepClone();
        }
    }

    private static bool IsNullType(JsonNode? alternative) =>
        alternative is JsonObject alt && alt["type"] is JsonValue type && type.GetValueKind() == JsonValueKind.String && type.GetValue<string>() == "null";

    private static string FirstSentence(string text, int cap = 80)
    {
        text = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var end = text.IndexOf(". ", StringComparison.Ordinal);
        var first = end < 0 ? text : text[..(end + 1)];
        if (first.Length <= cap)
        {
            return first;
        }

        var space = first.LastIndexOf(' ', cap - 1);
        return first[..(space > 0 ? space : cap)] + "...";
    }

    /// <summary>
    /// A slimmed schema in short, as the tool's offered: each parameter's name and type (a short
    /// list of choices kept), which are required, and no descriptions. The descriptions come back with
    /// a call that needs them (<see cref="McpTool.Check"/>, or the server's own error).
    /// </summary>
    internal static JsonObject Compact(JsonObject slim)
    {
        var properties = new JsonObject();
        if (slim["properties"] is JsonObject all)
        {
            foreach (var (name, value) in all)
            {
                var property = new JsonObject();
                if (value is JsonObject full)
                {
                    if (full["type"] is { } type)
                    {
                        property["type"] = type.DeepClone();
                    }

                    if (full["enum"] is JsonArray choices && choices.Count <= 6)
                    {
                        property["enum"] = choices.DeepClone();
                    }

                    if (full["items"] is JsonObject { } items && items["type"] is { } itemType)
                    {
                        property["items"] = new JsonObject { ["type"] = itemType.DeepClone() };
                    }
                }

                properties[name] = property;
            }
        }

        var compact = new JsonObject { ["type"] = "object", ["properties"] = properties };
        if (slim["required"] is JsonArray required)
        {
            compact["required"] = required.DeepClone();
        }

        return compact;
    }

    /// <summary>A schema's parameters in words, for the model: "query (string, required): Search query.; page (number)".</summary>
    internal static string ParameterList(JsonObject slim)
    {
        if (slim["properties"] is not JsonObject { Count: > 0 } properties)
        {
            return "none";
        }

        var required = RequiredOf(slim);
        return string.Join("; ", properties.Select(p =>
        {
            var description = p.Value is JsonObject { } full && full["description"] is JsonValue d && d.GetValueKind() == JsonValueKind.String
                ? $": {d.GetValue<string>()}"
                : "";
            return $"{p.Key} ({TypeOf(p.Value)}{(required.Contains(p.Key) ? ", required" : "")}){description}";
        }));
    }

    private static HashSet<string> RequiredOf(JsonObject schema) =>
        schema["required"] is JsonArray required
            ? [.. required.OfType<JsonValue>().Where(v => v.GetValueKind() == JsonValueKind.String).Select(v => v.GetValue<string>())]
            : [];

    private static string TypeOf(JsonNode? property) => property switch
    {
        JsonObject { } p when p["enum"] is JsonArray choices => "one of " + string.Join("|", choices.Select(c => c?.ToString())),
        JsonObject { } p when p["type"] is JsonValue t && t.GetValueKind() == JsonValueKind.String && t.GetValue<string>() == "array" =>
            $"list of {(p["items"] is JsonObject items ? TypeOf(items) : "values")}",
        JsonObject { } p when p["type"] is JsonValue t && t.GetValueKind() == JsonValueKind.String => t.GetValue<string>(),
        JsonObject { } p when p["type"] is JsonArray types =>
            types.OfType<JsonValue>().Select(v => v.ToString()).FirstOrDefault(v => v != "null") ?? "value",
        _ => "value",
    };

    /// <summary>
    /// One MCP tool, as the familiar's tool registry sees it. It's offered in short (<see cref="Compact"/>):
    /// a sentence of what it does, its parameters' names and types. A call missing one it needs, or
    /// naming one it hasn't got, isn't made: the model's told the parameters in full and calls again.
    /// One more round, sometimes, against a fraction of the prompt every turn.
    /// </summary>
    private sealed class McpTool(McpTools owner, McpServer server, McpToolInfo tool) : ITool, IArgumentCheck
    {
        private readonly JsonObject schema = Slim(tool.InputSchema);

        public ToolDefinition Definition { get; } = new(
            ModelName(server.Name, tool.Name),
            $"{FirstSentence(tool.Description ?? tool.Name, 160)} (from {server.Name})",
            Compact(Slim(tool.InputSchema)),
            tool.ReadOnly ? ToolRisk.Read : ToolRisk.WriteExternal);

        public ToolResult? Check(JsonObject arguments)
        {
            var missing = RequiredOf(schema).Where(name => arguments[name] is null).ToList();
            var unknown = schema["properties"] is JsonObject { Count: > 0 } properties
                ? arguments.Select(a => a.Key).Where(name => !properties.ContainsKey(name)).ToList()
                : [];
            if (missing.Count == 0 && unknown.Count == 0)
            {
                return null;
            }

            var problem = string.Join(' ', new[]
            {
                missing.Count > 0 ? $"It needs {string.Join(", ", missing)}." : null,
                unknown.Count > 0 ? $"It has no {string.Join(", ", unknown)}." : null,
            }.OfType<string>());

            // Done, as far as the chat shows it (nothing failed: it said what it needs), and not done
            // as far as the model's told: it wasn't called.
            return new ToolResult(
                true,
                new JsonObject
                {
                    ["ok"] = false,
                    ["error"] = $"{Definition.Name} wasn't called. {problem} Call it again with its parameters: {ParameterList(schema)}",
                },
                $"Checked how to use {server.Name}'s {tool.Name}");
        }

        public Task<ToolResult> InvokeAsync(ToolContext context, JsonObject arguments, CancellationToken ct) =>
            owner.CallAsync(server, tool, arguments, ct);

        public string Describe(JsonObject arguments)
        {
            var args = arguments.Count == 0 ? "" : $" with {arguments.ToJsonString()}";
            return $"Use {server.Name}'s “{tool.Name}”{(args.Length > 200 ? args[..200] + "…" : args)}";
        }
    }
}
