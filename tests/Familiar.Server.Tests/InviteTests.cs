using System.Net;
using System.Net.Http.Json;
using Familiar.Server.Auth;
using Familiar.Server.Tests.Infrastructure;

namespace Familiar.Server.Tests;

/// <summary>Invite-only registration: single-use links an admin makes, checks and cancels.</summary>
[Collection(PostgresCollection.Name)]
public sealed class InviteTests(PostgresFixture postgres)
{
    /// <summary>A clock a test moves on, to see an invite expire.</summary>
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static async Task<HttpClient> AdminAsync(FamiliarAppFactory app)
    {
        var admin = app.CreateClient();
        (await admin.RegisterAsync("boss")).EnsureSuccessStatusCode();
        return admin;
    }

    private static Task<HttpResponseMessage> RegisterAsync(FamiliarAppFactory app, string username, string? invite) =>
        app.CreateClient().PostAsJsonAsync("/api/auth/register", new RegisterRequest(username, $"{username}@example.test", "correct horse", invite));

    private static async Task<NewInviteView> InviteAsync(HttpClient admin, string? note = null, int? hours = null) =>
        await (await admin.PostAsJsonAsync("/api/admin/invites", new CreateInviteRequest(note, hours))).ReadAsync<NewInviteView>();

    private static async Task<InviteView> ListedAsync(HttpClient admin, Guid id) =>
        (await (await admin.GetAsync("/api/admin/invites")).ReadAsync<List<InviteView>>()).Single(i => i.Id == id);

    [Fact]
    public async Task Invite_only_registration_takes_a_single_use_invite()
    {
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync());
        var admin = await AdminAsync(app);

        // Open by configuration until the admin says otherwise.
        Assert.True((await (await admin.GetAsync("/api/admin/registration")).ReadAsync<RegistrationView>()).Open);
        (await admin.PutAsJsonAsync("/api/admin/registration", new RegistrationView(false))).EnsureSuccessStatusCode();
        Assert.False((await (await app.CreateClient().GetAsync("/api/auth/registration")).ReadAsync<RegistrationStatusView>()).Open);

        var refused = await RegisterAsync(app, "stranger", null);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("needs an invite", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var made = await InviteAsync(admin, "Sam", 24);
        Assert.StartsWith(Invites.Prefix, made.Code, StringComparison.Ordinal);
        Assert.Null(made.Link);
        Assert.Equal((Invites.Waiting, "Sam", "boss"), (made.Invite.Status, made.Invite.Note, made.Invite.CreatedBy));

        var check = await (await app.CreateClient().GetAsync($"/api/auth/registration?invite={made.Code}")).ReadAsync<RegistrationStatusView>();
        Assert.True(check.Invite!.Valid);

        (await RegisterAsync(app, "sam", made.Code)).EnsureSuccessStatusCode();

        // Once only.
        var again = await RegisterAsync(app, "sams_friend", made.Code);
        Assert.Equal(HttpStatusCode.Forbidden, again.StatusCode);
        Assert.Contains("used already", await again.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var used = await ListedAsync(admin, made.Invite.Id);
        Assert.Equal((Invites.Used, "sam"), (used.Status, used.UsedBy));
        Assert.NotNull(used.UsedAt);
    }

    [Fact]
    public async Task A_cancelled_or_expired_invite_lets_nobody_in()
    {
        var clock = new Clock();
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync(), new Dictionary<string, string> { ["Auth:AllowRegistration"] = "false" }, clock);
        var admin = await AdminAsync(app);

        var cancelled = await InviteAsync(admin);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/admin/invites/{cancelled.Invite.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"/api/admin/invites/{cancelled.Invite.Id}")).StatusCode);
        Assert.Equal(Invites.Cancelled, (await ListedAsync(admin, cancelled.Invite.Id)).Status);
        Assert.Contains("cancelled", await (await RegisterAsync(app, "late", cancelled.Code)).Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var expiring = await InviteAsync(admin, hours: 1);
        clock.Now += TimeSpan.FromHours(2);
        var expired = await RegisterAsync(app, "later", expiring.Code);
        Assert.Equal(HttpStatusCode.Forbidden, expired.StatusCode);
        Assert.Contains("expired", await expired.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(Invites.Expired, (await ListedAsync(admin, expiring.Invite.Id)).Status);

        // A made-up or mangled code is no invite at all.
        Assert.False((await (await app.CreateClient().GetAsync($"/api/auth/registration?invite={expiring.Code}x")).ReadAsync<RegistrationStatusView>()).Invite!.Valid);
        Assert.Equal(HttpStatusCode.Forbidden, (await RegisterAsync(app, "guess", "finv_nothing")).StatusCode);
    }

    [Fact]
    public async Task Only_an_admin_makes_invites_and_lifetimes_are_bounded()
    {
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync());
        var admin = await AdminAsync(app);
        var user = app.CreateClient();
        (await user.RegisterAsync("someone")).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.Forbidden, (await user.PostAsJsonAsync("/api/admin/invites", new CreateInviteRequest(null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.PutAsJsonAsync("/api/admin/registration", new RegistrationView(true))).StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/admin/invites", new CreateInviteRequest(null, 0))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/admin/invites", new CreateInviteRequest(null, Invites.MaxHours + 1))).StatusCode);
        var made = await InviteAsync(admin);
        Assert.Equal(TimeSpan.FromHours(Invites.DefaultHours), made.Invite.ExpiresAt - made.Invite.CreatedAt);
    }

    [Fact]
    public async Task With_a_public_address_the_server_makes_the_link()
    {
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync(),
            new Dictionary<string, string> { ["Auth:PublicUrl"] = "https://familiar.example.net/" });
        var made = await InviteAsync(await AdminAsync(app));
        Assert.Equal($"https://familiar.example.net/register?invite={made.Code}", made.Link);
    }
}
