using System.Net;
using System.Net.Http.Json;
using Familiar.Domain.Accounts;
using Familiar.Server.Auth;
using Familiar.Server.Tests.Infrastructure;

namespace Familiar.Server.Tests;

[Collection(PostgresCollection.Name)]
public sealed class AuthFlowTests(PostgresFixture postgres)
{
    [Fact]
    public async Task First_account_is_admin_and_later_ones_are_users()
    {
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync());
        var client = app.CreateClient();

        var first = await (await client.RegisterAsync("alice")).ReadAsync<AccountResponse>();
        var second = await (await app.CreateClient().RegisterAsync("bob")).ReadAsync<AccountResponse>();

        Assert.Equal(AccountRole.Admin, first.Role);
        Assert.Equal(AccountRole.User, second.Role);
    }

    [Fact]
    public async Task Simultaneous_first_registrations_make_exactly_one_admin()
    {
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync());

        var responses = await Task.WhenAll(
            Enumerable.Range(0, 6).Select(i => app.CreateClient().RegisterAsync($"racer{i}")));

        var accounts = await Task.WhenAll(responses.Select(r => r.ReadAsync<AccountResponse>()));
        Assert.Single(accounts, a => a.Role == AccountRole.Admin);
    }

    [Fact]
    public async Task Usernames_are_unique_regardless_of_case()
    {
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync());

        (await app.CreateClient().RegisterAsync("Carol")).EnsureSuccessStatusCode();
        var duplicate = await app.CreateClient().RegisterAsync("carol");

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
    }

    [Fact]
    public async Task Closed_registration_still_lets_the_first_account_in()
    {
        await using var app = new FamiliarAppFactory(
            await postgres.CreateDatabaseAsync(),
            new Dictionary<string, string> { ["Auth:AllowRegistration"] = "false" });

        var first = await app.CreateClient().RegisterAsync("owner");
        var second = await app.CreateClient().RegisterAsync("stranger");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, second.StatusCode);
    }

    [Fact]
    public async Task Unknown_user_and_wrong_password_get_the_same_answer()
    {
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync());
        (await app.CreateClient().RegisterAsync("dave")).EnsureSuccessStatusCode();

        var wrongPassword = await app.CreateClient().LoginAsync("dave", "not the password");
        var unknownUser = await app.CreateClient().LoginAsync("nobody", "not the password");

        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknownUser.StatusCode);
        Assert.Equal(await wrongPassword.Content.ReadAsStringAsync(), await unknownUser.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Signed_out_api_calls_get_401_not_a_login_redirect()
    {
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync());
        var client = app.CreateClient(new() { AllowAutoRedirect = false });

        var response = await client.GetAsync("/api/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Login_me_and_logout_round_trip()
    {
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync());
        (await app.CreateClient().RegisterAsync("erin")).EnsureSuccessStatusCode();

        var client = app.CreateClient();
        (await client.LoginAsync("ERIN")).EnsureSuccessStatusCode();

        var me = await (await client.GetAsync("/api/auth/me")).ReadAsync<AccountResponse>();
        Assert.Equal("erin", me.Username);

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/auth/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Changing_the_password_signs_out_other_sessions_but_not_the_caller()
    {
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync());
        var laptop = app.CreateClient();
        (await laptop.RegisterAsync("frank")).EnsureSuccessStatusCode();

        var desktop = app.CreateClient();
        (await desktop.LoginAsync("frank")).EnsureSuccessStatusCode();

        var change = await laptop.PostAsJsonAsync("/api/auth/password", new ChangePasswordRequest("correct horse", "battery staple"));
        Assert.Equal(HttpStatusCode.NoContent, change.StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await laptop.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await desktop.GetAsync("/api/auth/me")).StatusCode);
        (await app.CreateClient().LoginAsync("frank", "battery staple")).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Repeated_failures_for_one_name_back_off_with_retry_after()
    {
        await using var app = new FamiliarAppFactory(
            await postgres.CreateDatabaseAsync(),
            new Dictionary<string, string> { ["Auth:LoginFailuresBeforeBackoff"] = "3" });
        (await app.CreateClient().RegisterAsync("grace")).EnsureSuccessStatusCode();

        var client = app.CreateClient();
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.LoginAsync("grace", "guess")).StatusCode);
        }

        // Paused even for the right password: the throttle doesn't reveal which guess was right.
        var paused = await client.LoginAsync("grace");
        Assert.Equal(HttpStatusCode.TooManyRequests, paused.StatusCode);
        Assert.NotNull(paused.Headers.RetryAfter);
    }

    [Fact]
    public async Task The_auth_rate_limit_applies_per_address()
    {
        await using var app = new FamiliarAppFactory(
            await postgres.CreateDatabaseAsync(),
            new Dictionary<string, string> { ["RateLimits:AuthAttemptsPerMinute"] = "2" });
        var client = app.CreateClient();

        await client.LoginAsync("nobody", "x");
        await client.LoginAsync("nobody", "x");
        var third = await client.LoginAsync("nobody", "x");

        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
    }

    [Theory]
    [InlineData("ab", "long enough")]
    [InlineData("has space", "long enough")]
    [InlineData("valid_name", "short")]
    public async Task Bad_registrations_are_400(string username, string password)
    {
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync());

        var response = await app.CreateClient().RegisterAsync(username, password);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace((await response.ReadAsync<ErrorResponse>()).Error));
    }
}
