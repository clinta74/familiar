using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Familiar.Server.Auth;
using Familiar.Server.Tests.Infrastructure;

namespace Familiar.Server.Tests;

[Collection(PostgresCollection.Name)]
public sealed class DeviceTokenTests(PostgresFixture postgres)
{
    [Fact]
    public async Task A_device_token_authenticates_without_a_cookie()
    {
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync());
        (await app.CreateClient().RegisterAsync("hana")).EnsureSuccessStatusCode();

        var login = await app.CreateClient().DeviceLoginAsync("hana", "Pixel 9");
        var phone = app.DeviceClient(login.Token);

        Assert.StartsWith(AccessTokenSecret.Prefix, login.Token, StringComparison.Ordinal);
        var me = await (await phone.GetAsync("/api/auth/me")).ReadAsync<AccountResponse>();
        Assert.Equal("hana", me.Username);

        var devices = await (await phone.GetAsync("/api/auth/devices")).ReadAsync<List<DeviceSummary>>();
        var device = Assert.Single(devices);
        Assert.Equal("Pixel 9", device.Name);
        Assert.True(device.IsCurrent);
    }

    [Fact]
    public async Task A_device_that_signs_itself_out_stops_working()
    {
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync());
        (await app.CreateClient().RegisterAsync("ivan")).EnsureSuccessStatusCode();
        var login = await app.CreateClient().DeviceLoginAsync("ivan");
        var phone = app.DeviceClient(login.Token);

        Assert.Equal(HttpStatusCode.NoContent, (await phone.DeleteAsync($"/api/auth/devices/{login.Device.Id}")).StatusCode);

        var after = await phone.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
        Assert.Contains("signed out", (await after.ReadAsync<ErrorResponse>()).Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Changing_the_password_signs_out_every_device()
    {
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync());
        var browser = app.CreateClient();
        (await browser.RegisterAsync("judy")).EnsureSuccessStatusCode();
        var phone = app.DeviceClient((await app.CreateClient().DeviceLoginAsync("judy")).Token);

        (await browser.PostAsJsonAsync("/api/auth/password", new ChangePasswordRequest("correct horse", "battery staple")))
            .EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.Unauthorized, (await phone.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task A_phone_can_change_the_password_and_stays_signed_in()
    {
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync());
        var browser = app.CreateClient();
        (await browser.RegisterAsync("kim")).EnsureSuccessStatusCode();
        var phone = app.DeviceClient((await app.CreateClient().DeviceLoginAsync("kim")).Token);
        var otherPhone = app.DeviceClient((await app.CreateClient().DeviceLoginAsync("kim")).Token);

        var response = await phone.PostAsJsonAsync("/api/auth/password", new ChangePasswordRequest("correct horse", "battery staple"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await phone.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await otherPhone.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/api/auth/me")).StatusCode);
        (await app.CreateClient().LoginAsync("kim", "battery staple")).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task A_phone_still_needs_the_current_password()
    {
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync());
        (await app.CreateClient().RegisterAsync("kit")).EnsureSuccessStatusCode();
        var phone = app.DeviceClient((await app.CreateClient().DeviceLoginAsync("kit")).Token);

        var response = await phone.PostAsJsonAsync("/api/auth/password", new ChangePasswordRequest("a guess", "battery staple"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await phone.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task A_bad_bearer_token_is_refused_even_alongside_a_good_cookie()
    {
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync());
        var client = app.CreateClient();
        (await client.RegisterAsync("liam")).EnsureSuccessStatusCode();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "fam_00000000000000000000000000000000_nope");

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task Someone_elses_device_is_not_found()
    {
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync());
        (await app.CreateClient().RegisterAsync("mia")).EnsureSuccessStatusCode();
        (await app.CreateClient().RegisterAsync("noah")).EnsureSuccessStatusCode();
        var miasPhone = await app.CreateClient().DeviceLoginAsync("mia");
        var noah = app.DeviceClient((await app.CreateClient().DeviceLoginAsync("noah")).Token);

        var response = await noah.DeleteAsync($"/api/auth/devices/{miasPhone.Device.Id}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Device_count_is_capped()
    {
        await using var app = new FamiliarAppFactory(
            await postgres.CreateDatabaseAsync(),
            new Dictionary<string, string> { ["Auth:DeviceTokensPerAccount"] = "2" });
        (await app.CreateClient().RegisterAsync("olga")).EnsureSuccessStatusCode();

        await app.CreateClient().DeviceLoginAsync("olga", "one");
        await app.CreateClient().DeviceLoginAsync("olga", "two");
        var third = await app.CreateClient().PostAsJsonAsync(
            "/api/auth/devices/login", new DeviceLoginRequest("olga", "correct horse", "three"));

        Assert.Equal(HttpStatusCode.Conflict, third.StatusCode);
    }
}
