using Familiar.Persistence;
using Familiar.Server.Auth;

namespace Familiar.Server.Tests;

public sealed class AccessTokenSecretTests
{
    [Fact]
    public void A_minted_token_parses_back_to_its_id_and_matches_its_hash()
    {
        var id = Guid.CreateVersion7();
        var (token, hash) = AccessTokenSecret.Mint(id);

        Assert.True(AccessTokenSecret.TryParse(token, out var parsedId, out var secret));
        Assert.Equal(id, parsedId);
        Assert.True(AccessTokenSecret.Matches(secret, hash));
        Assert.False(AccessTokenSecret.Matches(secret + "x", hash));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("fam_")]
    [InlineData("fam_not-a-guid-at-all-not-a-guid-at-al_secret")]
    [InlineData("pat_0123456789abcdef0123456789abcdef_secret")]
    public void Malformed_tokens_do_not_parse(string? token) =>
        Assert.False(AccessTokenSecret.TryParse(token, out _, out _));
}

public sealed class LoginThrottleTests
{
    [Fact]
    public void Backoff_starts_at_the_threshold_doubles_and_caps()
    {
        var clock = new ManualClock();
        var throttle = new LoginThrottle(
            new AuthOptions { LoginFailuresBeforeBackoff = 2, LoginBackoffSeconds = 10, LoginBackoffMaxSeconds = 30 },
            clock);

        Assert.False(throttle.RecordFailure("x"));
        Assert.Null(throttle.RetryAfter("x"));

        Assert.True(throttle.RecordFailure("X"));
        Assert.Equal(TimeSpan.FromSeconds(10), throttle.RetryAfter("x"));

        throttle.RecordFailure("x");
        Assert.Equal(TimeSpan.FromSeconds(20), throttle.RetryAfter("x"));

        throttle.RecordFailure("x");
        Assert.Equal(TimeSpan.FromSeconds(30), throttle.RetryAfter("x"));

        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.Null(throttle.RetryAfter("x"));

        throttle.RecordSuccess("x");
        Assert.False(throttle.RecordFailure("x"));
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}

public sealed class SnakeCaseNamingTests
{
    [Theory]
    [InlineData("SecretHash", "secret_hash")]
    [InlineData("IPAddress", "ip_address")]
    [InlineData("IX_Accounts_Email", "ix_accounts_email")]
    [InlineData("PK_AccessTokens", "pk_access_tokens")]
    [InlineData("Id", "id")]
    [InlineData("already_snake", "already_snake")]
    public void Converts(string input, string expected) =>
        Assert.Equal(expected, SnakeCaseNaming.ToSnakeCase(input));
}

public sealed class McpToolChoiceTests
{
    private static Dictionary<string, double> Scores(params (string Name, double Score)[] scores) =>
        scores.ToDictionary(s => s.Name, s => s.Score);

    private static Dictionary<string, int> Small(Dictionary<string, double> scores) => scores.Keys.ToDictionary(k => k, _ => 100);

    private static IReadOnlyList<string> Choose(Dictionary<string, double> scores, IReadOnlyList<string>? previous, Dictionary<string, int>? sizes = null) =>
        Familiar.Server.Hooks.McpTools.Choose(scores, sizes ?? Small(scores), previous);

    [Fact]
    public void The_best_fits_are_offered_in_name_order()
    {
        var scores = Scores([.. Enumerable.Range(0, 10).Select(i => ($"t{i}", i / 10.0))]);
        Assert.Equal(["t2", "t3", "t4", "t5", "t6", "t7", "t8", "t9"], Choose(scores, previous: null));
    }

    [Fact]
    public void Last_turns_offer_stands_while_it_still_nearly_fits_best()
    {
        var previous = new[] { "c", "a" };

        // Its best (c, 0.75) is within a tenth of the best (d, 0.8): unchanged, so the prompt's head is.
        Assert.Same(previous, Choose(Scores(("a", 0.2), ("b", 0.1), ("c", 0.75), ("d", 0.8)), previous));

        // Small talk fits nothing: unchanged too.
        Assert.Same(previous, Choose(Scores(("a", 0), ("b", 0), ("c", 0), ("d", 0)), previous));
    }

    [Fact]
    public void A_new_subject_or_a_tool_switched_off_makes_a_new_offer()
    {
        Assert.Equal(["a", "b", "c", "d"], Choose(Scores(("a", 0.2), ("b", 0.6), ("c", 0.3), ("d", 0.9)), ["c", "a"]));
        Assert.Equal(["b", "c"], Choose(Scores(("b", 0.5), ("c", 0.4)), ["c", "a"]));
    }

    [Fact]
    public void The_offer_keeps_to_its_size_passing_over_a_tool_too_big_for_whats_left()
    {
        var scores = Scores(("huge", 0.9), ("big", 0.8), ("small", 0.7), ("tiny", 0.1));
        var sizes = new Dictionary<string, int> { ["huge"] = 3500, ["big"] = 2600, ["small"] = 500, ["tiny"] = 100 };
        Assert.Equal(["huge", "small", "tiny"], Choose(scores, previous: null, sizes));
    }

    [Fact]
    public void A_schema_is_slimmed_to_what_a_call_needs()
    {
        var schema = (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse("""
            {
              "$schema": "https://json-schema.org/draft/2020-12/schema",
              "type": "object",
              "required": ["repo"],
              "properties": {
                "repo": { "type": "string", "description": "Repository name. Owner is separate; see owner.", "x-mcp-header": "repo", "minLength": 1 },
                "type": { "anyOf": [{ "type": "string" }, { "type": "null" }], "description": "Type of this issue" },
                "query": { "type": "string", "description": "A search query in the GitHub search syntax, which can use qualifiers such as is:open and label:bug and much else" }
              }
            }
            """)!;

        var slim = Familiar.Server.Hooks.McpTools.Slim(schema).ToJsonString();

        Assert.Equal(
            """{"type":"object","required":["repo"],"properties":{"repo":{"type":"string","description":"Repository name."},"type":{"description":"Type of this issue","type":"string"},"query":{"type":"string","description":"A search query in the GitHub search syntax, which can use qualifiers such as..."}}}""",
            slim);
    }
}

