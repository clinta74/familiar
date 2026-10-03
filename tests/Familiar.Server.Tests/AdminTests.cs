using System.Net;
using System.Net.Http.Json;
using Familiar.Domain.Accounts;
using Familiar.Server.Auth;
using Familiar.Server.Tests.Infrastructure;

namespace Familiar.Server.Tests;

[Collection(PostgresCollection.Name)]
public sealed class AdminTests(PostgresFixture postgres)
{
    private static readonly Dictionary<string, string> ClosedRegistration = new() { ["Auth:AllowRegistration"] = "false" };

    /// <summary>The first account on a server is its admin, even with registration closed.</summary>
    private static async Task<HttpClient> AdminAsync(FamiliarAppFactory app)
    {
        var admin = app.CreateClient();
        (await admin.RegisterAsync("boss")).EnsureSuccessStatusCode();
        return admin;
    }

    private static async Task<AdminUserView> CreateAsync(HttpClient admin, string username, AccountRole role = AccountRole.User) =>
        await (await admin.PostAsJsonAsync("/api/admin/users",
            new CreateUserRequest(username, $"{username}@example.test", "first password", role))).ReadAsync<AdminUserView>();

    [Fact]
    public async Task With_registration_closed_an_admin_adds_the_household()
    {
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync(), ClosedRegistration);
        var admin = await AdminAsync(app);
        Assert.Equal(HttpStatusCode.Forbidden, (await app.CreateClient().RegisterAsync("stranger")).StatusCode);

        var wife = await CreateAsync(admin, "partner");
        Assert.Equal((AccountRole.User, false), (wife.Role, wife.IsDisabled));

        var her = app.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await her.LoginAsync("partner", "first password")).StatusCode);

        var users = await (await admin.GetAsync("/api/admin/users")).ReadAsync<List<AdminUserView>>();
        Assert.Equal(["boss", "partner"], users.Select(u => u.Username));
        Assert.True(users[0].IsYou);
        Assert.NotNull(users[1].LastLoginAt);

        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/admin/users", new CreateUserRequest("PARTNER", "x@example.test", "first password"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/admin/users", new CreateUserRequest("no spaces", "y@example.test", "first password"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/admin/users", new CreateUserRequest("shorty", "z@example.test", "short"))).StatusCode);
    }

    [Fact]
    public async Task Only_admins_in_a_browser_may_administer()
    {
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync());
        var admin = await AdminAsync(app);
        await CreateAsync(admin, "regular");

        var user = app.CreateClient();
        (await user.LoginAsync("regular", "first password")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync("/api/admin/users")).StatusCode);

        // An admin's phone token can't do it either (browser-only endpoints answer a token with 401,
        // as changing the password does).
        var phone = app.DeviceClient((await app.CreateClient().DeviceLoginAsync("boss")).Token);
        Assert.Equal(HttpStatusCode.Unauthorized, (await phone.GetAsync("/api/admin/users")).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await app.CreateClient().GetAsync("/api/admin/users")).StatusCode);
    }

    [Fact]
    public async Task Disabling_stops_the_account_everywhere_at_once_and_enabling_lets_it_sign_in_again()
    {
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync());
        var admin = await AdminAsync(app);
        var created = await CreateAsync(admin, "leaver");

        var browser = app.CreateClient();
        (await browser.LoginAsync("leaver", "first password")).EnsureSuccessStatusCode();
        var phone = app.DeviceClient((await app.CreateClient().DeviceLoginAsync("leaver", password: "first password")).Token);

        var disabled = await (await admin.PutAsJsonAsync($"/api/admin/users/{created.Id}", new UpdateUserRequest(IsDisabled: true))).ReadAsync<AdminUserView>();
        Assert.True(disabled.IsDisabled);

        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await phone.GetAsync("/api/auth/me")).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await app.CreateClient().LoginAsync("leaver", "first password")).StatusCode);

        await admin.PutAsJsonAsync($"/api/admin/users/{created.Id}", new UpdateUserRequest(IsDisabled: false));
        Assert.Equal(HttpStatusCode.OK, (await app.CreateClient().LoginAsync("leaver", "first password")).StatusCode);
    }

    [Fact]
    public async Task A_reset_password_works_and_signs_the_old_sessions_out()
    {
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync());
        var admin = await AdminAsync(app);
        var created = await CreateAsync(admin, "forgetful");
        var old = app.CreateClient();
        (await old.LoginAsync("forgetful", "first password")).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.NoContent,
            (await admin.PostAsJsonAsync($"/api/admin/users/{created.Id}/password", new ResetPasswordRequest("a brand new one"))).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await old.GetAsync("/api/auth/me")).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await app.CreateClient().LoginAsync("forgetful", "first password")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await app.CreateClient().LoginAsync("forgetful", "a brand new one")).StatusCode);
    }

    [Fact]
    public async Task A_new_admin_can_administer_and_a_demoted_one_loses_it_at_once()
    {
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync(), new Dictionary<string, string> { ["Auth:RevalidationIntervalSeconds"] = "3600" });
        var admin = await AdminAsync(app);
        var second = await CreateAsync(admin, "deputy", AccountRole.Admin);

        var deputy = app.CreateClient();
        (await deputy.LoginAsync("deputy", "first password")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, (await deputy.GetAsync("/api/admin/users")).StatusCode);

        await admin.PutAsJsonAsync($"/api/admin/users/{second.Id}", new UpdateUserRequest(Role: AccountRole.User));

        // Their cookie still says admin (no revalidation for an hour), but the database doesn't.
        Assert.Equal(HttpStatusCode.Forbidden, (await deputy.GetAsync("/api/admin/users")).StatusCode);
    }

    [Fact]
    public async Task An_admin_cannot_lock_themselves_out()
    {
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync());
        var admin = await AdminAsync(app);
        var me = (await (await admin.GetAsync("/api/admin/users")).ReadAsync<List<AdminUserView>>()).Single(u => u.IsYou);

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"/api/admin/users/{me.Id}", new UpdateUserRequest(Role: AccountRole.User))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"/api/admin/users/{me.Id}", new UpdateUserRequest(IsDisabled: true))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.DeleteAsync($"/api/admin/users/{me.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/admin/users/{me.Id}/password", new ResetPasswordRequest("whatever123"))).StatusCode);

        // Changing your own email is fine.
        var updated = await (await admin.PutAsJsonAsync($"/api/admin/users/{me.Id}", new UpdateUserRequest(Email: "boss@new.example"))).ReadAsync<AdminUserView>();
        Assert.Equal("boss@new.example", updated.Email);
    }

    [Fact]
    public async Task Deleting_an_account_removes_it_and_its_familiar()
    {
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync());
        var admin = await AdminAsync(app);
        var created = await CreateAsync(admin, "goner");
        var them = app.CreateClient();
        (await them.LoginAsync("goner", "first password")).EnsureSuccessStatusCode();
        (await them.PostAsJsonAsync("/api/memories", new Memories.MemoryRequest("They like trains"))).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/admin/users/{created.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync($"/api/admin/users/{created.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await them.GetAsync("/api/auth/me")).StatusCode);
        Assert.DoesNotContain(await (await admin.GetAsync("/api/admin/users")).ReadAsync<List<AdminUserView>>(), u => u.Username == "goner");
    }
}
