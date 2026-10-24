using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Familiar.Agent.Llm;
using Familiar.Agent.Prompting;
using Familiar.Agent.Tools;
using Familiar.Domain.Tools;
using Familiar.Server.Conversation;
using Familiar.Server.Hooks;
using Familiar.Server.Tests.Infrastructure;

namespace Familiar.Server.Tests;

public sealed class PromptBudgetTests
{
    private static IReadOnlyList<ChatMessage> Build(IReadOnlyList<string> history) =>
        [new(ChatRole.System, "You are a familiar."), .. history.Select(h => new ChatMessage(ChatRole.User, h)), new(ChatRole.User, "now")];

    [Fact]
    public void A_history_that_fits_is_kept_whole()
    {
        string[] history = ["one", "two", "three"];
        var (kept, _, dropped) = PromptBudget.Fit(history, Build, tools: null, budget: 1000, step: 6);
        Assert.Equal(history, kept);
        Assert.Equal(0, dropped);
    }

    [Fact]
    public void The_oldest_history_gives_way_a_step_at_a_time_and_the_newest_message_stays()
    {
        // Fourteen messages of 300 characters, about 105 tokens each with formatting: room for nine.
        var history = Enumerable.Range(0, 14).Select(i => $"{i:D2}" + new string('x', 298)).ToList();
        var (kept, prompt, dropped) = PromptBudget.Fit(history, Build, tools: null, budget: 1000, step: 6);

        // Fourteen is ~1,450 tokens; six out (one step) leaves eight, ~840: enough, and the start is a step boundary.
        Assert.Equal(6, dropped);
        Assert.Equal(history.Skip(6), kept);
        Assert.Equal("now", prompt[^1].Content);
        Assert.True(PromptBudget.Estimate(prompt, null) <= 1000);
    }

    [Fact]
    public void Tools_count_against_the_window_too()
    {
        var history = Enumerable.Range(0, 6).Select(i => new string('x', 300)).ToList();
        var tool = new ToolDefinition("big_tool", new string('d', 2000), new JsonObject(), ToolRisk.Read);
        Assert.Equal(0, PromptBudget.Fit(history, Build, tools: null, budget: 1000, step: 6).Dropped);
        Assert.Equal(6, PromptBudget.Fit(history, Build, tools: [tool], budget: 1000, step: 6).Dropped);
    }

    [Fact]
    public void Parameters_named_like_schema_words_are_kept()
    {
        // GitHub's issue_write: its title went with the schema keyword "title", so no issue could have one.
        var schema = (JsonObject)JsonNode.Parse("""
            {
              "type": "object",
              "title": "IssueWrite",
              "required": ["method"],
              "properties": {
                "method": { "type": "string", "enum": ["create", "update"] },
                "title": { "type": "string", "description": "Issue title", "title": "Title" },
                "default": { "type": "boolean" },
                "format": { "type": "string" }
              }
            }
            """)!;

        var slim = McpTools.Slim(schema);

        Assert.Null(slim["title"]);
        Assert.Equal(["method", "title", "default", "format"], slim["properties"]!.AsObject().Select(p => p.Key));
        Assert.Equal("""{"type":"string","description":"Issue title"}""", slim["properties"]!["title"]!.ToJsonString());
        Assert.Contains("title (string): Issue title", McpTools.ParameterList(slim), StringComparison.Ordinal);
        Assert.Equal(4, McpTools.Compact(slim)["properties"]!.AsObject().Count);
    }

    [Fact]
    public void An_mcp_schema_in_short_and_in_words()
    {
        var slim = (JsonObject)JsonNode.Parse("""
            {
              "type": "object",
              "required": ["query"],
              "properties": {
                "query": { "type": "string", "description": "Search query." },
                "state": { "type": "string", "enum": ["open", "closed"], "description": "Only issues in this state." },
                "labels": { "type": "array", "items": { "type": "string" } },
                "page": { "type": ["number", "null"] }
              }
            }
            """)!;

        Assert.Equal(
            """{"type":"object","properties":{"query":{"type":"string"},"state":{"type":"string","enum":["open","closed"]},"labels":{"type":"array","items":{"type":"string"}},"page":{"type":["number","null"]}},"required":["query"]}""",
            McpTools.Compact(slim).ToJsonString());
        Assert.Equal(
            "query (string, required): Search query.; state (one of open|closed): Only issues in this state.; labels (list of string); page (number)",
            McpTools.ParameterList(slim));
    }
}

[Collection(PostgresCollection.Name)]
public sealed class PromptWindowTests(PostgresFixture postgres)
{
    [Fact]
    public async Task In_a_small_window_the_history_gives_way_and_the_message_still_gets_its_reply()
    {
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync(),
            settings: new Dictionary<string, string> { ["Llm:ContextTokens"] = "1200", ["Llm:ReplyTokens"] = "100" });
        app.Llm.ThenSay("[relaxed:0.5] Noted.").ThenSay("[relaxed:0.5] Hello again.");
        var client = app.CreateClient();
        (await client.RegisterAsync("windowed")).EnsureSuccessStatusCode();

        await using var stream = await SseClient.ConnectAsync(client);
        await stream.ReadUntilAsync(EventTypes.Ready);
        await client.PostAsJsonAsync("/api/conversation/messages", new SendMessageRequest("the first thing I said " + new string('z', 400), "UTC"));
        await stream.ReadUntilStateAsync(FamiliarState.Idle);
        await client.PostAsJsonAsync("/api/conversation/messages", new SendMessageRequest("and the second", "UTC"));
        await stream.ReadUntilStateAsync(FamiliarState.Idle);

        Assert.DoesNotContain(app.Llm.LastPrompt!, m => m.Content.Contains("the first thing I said", StringComparison.Ordinal));
        Assert.Contains("and the second", app.Llm.LastPrompt![^1].Content, StringComparison.Ordinal);
    }
}
