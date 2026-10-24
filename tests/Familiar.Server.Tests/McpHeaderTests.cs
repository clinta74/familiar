using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Familiar.Agent.Llm;
using Familiar.Server.Conversation;
using Familiar.Server.Hooks;
using Familiar.Server.Tests.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Familiar.Server.Tests;

/// <summary>
/// An MCP server that wants some arguments as headers too (<c>x-mcp-header</c>), as GitHub's does,
/// and turns a call away without them. Written by hand: the SDK's server doesn't declare them.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class McpHeaderTests(PostgresFixture postgres)
{
    private const string ListCommits = """
        {
          "name": "list_commits",
          "description": "Get list of commits of a branch in a GitHub repository.",
          "annotations": { "readOnlyHint": true },
          "inputSchema": {
            "type": "object",
            "required": ["owner", "repo"],
            "properties": {
              "owner": { "type": "string", "description": "Repository owner", "x-mcp-header": "owner" },
              "repo": { "type": "string", "description": "Repository name", "x-mcp-header": "repo" }
            }
          }
        }
        """;

    [Fact]
    public async Task Arguments_the_server_wants_as_headers_are_sent_as_headers()
    {
        var headers = new List<string>();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        await using var github = builder.Build();
        github.MapPost("/mcp", async (HttpContext http) =>
        {
            var request = (await JsonNode.ParseAsync(http.Request.Body))!.AsObject();
            if (request["id"] is not { } id)
            {
                return Results.Accepted();
            }

            JsonNode result;
            switch ((string?)request["method"])
            {
                case "initialize":
                    result = new JsonObject
                    {
                        ["protocolVersion"] = request["params"]!["protocolVersion"]!.DeepClone(),
                        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                        ["serverInfo"] = new JsonObject { ["name"] = "fake-github", ["version"] = "1" },
                    };
                    break;
                case "tools/list":
                    result = new JsonObject { ["tools"] = new JsonArray(JsonNode.Parse(ListCommits)) };
                    break;
                case "tools/call":
                    var sent = http.Request.Headers
                        .Where(h => h.Key.StartsWith("Mcp-Param-", StringComparison.OrdinalIgnoreCase))
                        .Select(h => $"{h.Key}={h.Value}")
                        .ToList();
                    lock (headers)
                    {
                        headers.AddRange(sent);
                    }

                    if (sent.Count == 0)
                    {
                        return Results.BadRequest();
                    }

                    result = new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "1,234 commits" }) };
                    break;
                default:
                    return Results.Json(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["error"] = new JsonObject { ["code"] = -32601, ["message"] = "no" } });
            }

            return Results.Json(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = result });
        });
        await github.StartAsync();
        var handler = github.GetTestServer().CreateHandler();

        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync(),
            services: s => s.AddHttpClient(nameof(McpTools)).ConfigurePrimaryHttpMessageHandler(() => handler));
        app.Llm.ThenCall("github_list_commits", """{"owner": "clinta74", "repo": "familiar"}""").ThenSay("[relaxed:0.5] Lots.");
        var client = app.CreateClient();
        (await client.RegisterAsync("headed")).EnsureSuccessStatusCode();
        var server = await (await client.PostAsJsonAsync("/api/mcp", new McpServerRequest("github", "http://github.test/mcp", null))).ReadAsync<McpServerView>();
        Assert.Null(server.LastError);

        await using var stream = await SseClient.ConnectAsync(client);
        await stream.ReadUntilAsync(EventTypes.Ready);
        await client.PostAsJsonAsync("/api/conversation/messages", new SendMessageRequest("how many commits on familiar?", "UTC"));
        await stream.ReadUntilStateAsync(FamiliarState.Idle);

        Assert.Contains("1,234 commits", app.Llm.LastPrompt!.Last(m => m.Role == ChatRole.Tool).Content, StringComparison.Ordinal);
        Assert.Contains(headers, h => h.Contains("familiar", StringComparison.Ordinal));
        Assert.Contains(headers, h => h.Contains("clinta74", StringComparison.Ordinal));
    }
}
