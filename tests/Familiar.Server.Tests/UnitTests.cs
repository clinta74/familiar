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
