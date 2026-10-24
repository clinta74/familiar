using System.ComponentModel;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Web;
using Familiar.Agent.Llm;
using Familiar.Server.Conversation;
using Familiar.Server.Hooks;
using Familiar.Server.Tests.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace Familiar.Server.Tests;

/// <summary>
/// An MCP server behind an OAuth sign-in, as Notion's or Linear's are: a fake of both in one host
/// (protected-resource and authorization-server metadata, client registration, PKCE, rotating refresh
/// tokens), so the whole flow runs without the internet.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class McpOAuthTests(PostgresFixture postgres)
{
    [McpServerToolType]
    public sealed class NoteTools
    {
        [McpServerTool(Name = "list_notes", ReadOnly = true), Description("The user's notes.")]
        public static string ListNotes() => "Buy oat milk. Call the vet.";
    }

    /// <summary>The authorization server and the MCP server it guards.</summary>
    private sealed class Fake : IAsyncDisposable
    {
        private readonly WebApplication host;
        private string? challenge;
        private int issued;

        public string? AccessToken { get; private set; }

        public string? RefreshToken { get; private set; }

        public int ExpiresIn { get; set; } = 3600;

        public List<string> RegisteredRedirects { get; } = [];

        public int Refreshes { get; private set; }

        public int Unauthorized { get; private set; }

        /// <summary>What the last sign-in asked for.</summary>
        public string? Scope { get; private set; }

        /// <summary>How the last token request carried a client secret: "form", "basic" or null.</summary>
        public string? SecretSentIn { get; private set; }

        public HttpMessageHandler Handler => host.GetTestServer().CreateHandler();

        private Fake(WebApplication host) => this.host = host;

        public static async Task<Fake> StartAsync()
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Services.AddMcpServer().WithHttpTransport(o => o.Stateless = true).WithTools<NoteTools>();
            var fake = new Fake(builder.Build());
            fake.Map();
            await fake.host.StartAsync();
            return fake;
        }

        /// <summary>Signs everyone out: the tokens it gave stop working.</summary>
        public void Revoke() => AccessToken = RefreshToken = null;

        /// <summary>The access token runs out early, as they do; the refresh token still works.</summary>
        public void Expire() => AccessToken = "expired";

        private void Map()
        {
            var app = host;
            app.Use(async (http, next) =>
            {
                if (http.Request.Path.StartsWithSegments("/mcp") && http.Request.Headers.Authorization != $"Bearer {AccessToken}")
                {
                    Unauthorized++;
                    http.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    http.Response.Headers.WWWAuthenticate = """Bearer resource_metadata="http://notes.test/.well-known/oauth-protected-resource/mcp", scope="notes" """;
                    return;
                }

                await next(http);
            });
            app.MapMcp("/mcp");

            app.MapGet("/.well-known/oauth-protected-resource/mcp", () => Results.Json(new Dictionary<string, object>
            {
                ["resource"] = "http://notes.test/mcp",
                ["authorization_servers"] = new[] { "http://auth.test" },
            }));
            app.MapGet("/.well-known/oauth-authorization-server", () => Results.Json(new Dictionary<string, object>
            {
                ["issuer"] = "http://auth.test",
                ["authorization_endpoint"] = "http://auth.test/authorize",
                ["token_endpoint"] = "http://auth.test/token",
                ["registration_endpoint"] = "http://auth.test/register",
                ["scopes_supported"] = new[] { "notes", "offline_access" },
                ["code_challenge_methods_supported"] = new[] { "S256" },
            }));
            app.MapPost("/register", async (HttpRequest request) =>
            {
                var body = (await request.ReadFromJsonAsync<Dictionary<string, System.Text.Json.JsonElement>>())!;
                RegisteredRedirects.Add(body["redirect_uris"][0].GetString()!);
                return Results.Json(new Dictionary<string, object> { ["client_id"] = $"client-{RegisteredRedirects.Count}" }, statusCode: 201);
            });

            // The user's browser, signing in and allowing it.
            app.MapGet("/authorize", (HttpRequest request) =>
            {
                var q = request.Query;
                if (q["response_type"] != "code" || q["code_challenge_method"] != "S256" || q["resource"] != "http://notes.test/mcp"
                    || !q["client_id"].ToString().StartsWith("client-", StringComparison.Ordinal))
                {
                    return Results.BadRequest();
                }

                challenge = q["code_challenge"];
                Scope = q["scope"];
                return Results.Redirect($"{q["redirect_uri"]}?code=the-code&state={Uri.EscapeDataString(q["state"]!)}");
            });

            app.MapPost("/token", async (HttpRequest request) =>
            {
                var form = await request.ReadFormAsync();
                SecretSentIn = form.ContainsKey("client_secret") ? "form" : request.Headers.Authorization.ToString().StartsWith("Basic ", StringComparison.Ordinal) ? "basic" : null;
                var refused = Results.Json(new Dictionary<string, string> { ["error"] = "invalid_grant" }, statusCode: 400);
                if (form["resource"] != "http://notes.test/mcp")
                {
                    return refused;
                }

                if (form["grant_type"] == "authorization_code")
                {
                    var verifier = SHA256.HashData(Encoding.ASCII.GetBytes(form["code_verifier"].ToString()));
                    if (form["code"] != "the-code" || Convert.ToBase64String(verifier).TrimEnd('=').Replace('+', '-').Replace('/', '_') != challenge)
                    {
                        return refused;
                    }
                }
                else if (form["grant_type"] != "refresh_token" || RefreshToken is null || form["refresh_token"] != RefreshToken)
                {
                    return refused;
                }
                else
                {
                    Refreshes++;
                }

                issued++;
                AccessToken = $"access-{issued}";
                RefreshToken = $"refresh-{issued}";
                return Results.Json(new Dictionary<string, object>
                {
                    ["access_token"] = AccessToken, ["token_type"] = "Bearer", ["expires_in"] = ExpiresIn, ["refresh_token"] = RefreshToken,
                });
            });
        }

        public ValueTask DisposeAsync() => host.DisposeAsync();
    }

    /// <summary>What the browser does between Familiar's sign-in address and its callback; gives where it ends up.</summary>
    private static async Task<string> SignInAsync(HttpClient client, FamiliarAppFactory app, Fake fake, Guid server)
    {
        var start = await (await client.PostAsync($"/api/mcp/{server}/sign-in", null)).ReadAsync<McpSignInView>();
        using var browser = new HttpClient(fake.Handler) { BaseAddress = new Uri("http://auth.test") };
        var authorized = await browser.GetAsync(start.AuthorizationUrl);
        Assert.Equal(HttpStatusCode.Redirect, authorized.StatusCode);

        var familiar = app.CreateClient(new() { AllowAutoRedirect = false });
        var back = await familiar.GetAsync(authorized.Headers.Location!.PathAndQuery);
        Assert.Equal(HttpStatusCode.Redirect, back.StatusCode);
        return back.Headers.Location!.OriginalString;
    }

    [Fact]
    public async Task A_sign_in_begun_in_the_phone_app_ends_on_a_page_saying_to_go_back_to_it()
    {
        await using var fake = await Fake.StartAsync();
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync(),
            services: s => s.AddHttpClient(nameof(McpTools)).ConfigurePrimaryHttpMessageHandler(() => fake.Handler));
        var client = app.CreateClient();
        (await client.RegisterAsync("phoner")).EnsureSuccessStatusCode();
        var added = await (await client.PostAsJsonAsync("/api/mcp", new McpServerRequest("notes", "http://notes.test/mcp", null))).ReadAsync<McpServerView>();

        var start = await (await client.PostAsync($"/api/mcp/{added.Id}/sign-in?app=true", null)).ReadAsync<McpSignInView>();
        using var browser = new HttpClient(fake.Handler) { BaseAddress = new Uri("http://auth.test") };
        var authorized = await browser.GetAsync(start.AuthorizationUrl);
        var back = await app.CreateClient(new() { AllowAutoRedirect = false }).GetAsync(authorized.Headers.Location!.PathAndQuery);

        Assert.Equal(HttpStatusCode.OK, back.StatusCode);
        Assert.Equal("text/html", back.Content.Headers.ContentType!.MediaType);
        Assert.Contains("Signed in to notes", await back.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var server = (await (await client.GetAsync("/api/mcp")).ReadAsync<List<McpServerView>>()).Single();
        Assert.False(server.SignInNeeded);
        Assert.Equal(["list_notes"], server.Tools.Select(t => t.Name));
    }

    [Fact]
    public async Task A_server_that_wants_a_sign_in_is_signed_in_to_and_its_tokens_kept_fresh()
    {
        await using var fake = await Fake.StartAsync();
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync(),
            services: s => s.AddHttpClient(nameof(McpTools)).ConfigurePrimaryHttpMessageHandler(() => fake.Handler));
        var client = app.CreateClient();
        (await client.RegisterAsync("signer")).EnsureSuccessStatusCode();

        var added = await (await client.PostAsJsonAsync("/api/mcp", new McpServerRequest("notes", "http://notes.test/mcp", null))).ReadAsync<McpServerView>();
        Assert.Equal(McpAuth.SignIn, added.Auth);
        Assert.True(added.SignInNeeded);
        Assert.Equal(McpOAuth.SignInFirst, added.LastError);
        Assert.Empty(added.Tools);

        Assert.Equal("http://localhost/settings/connections?mcp=notes", await SignInAsync(client, app, fake, added.Id));
        Assert.Equal(["http://localhost/api/mcp/oauth/callback"], fake.RegisteredRedirects);
        Assert.Equal("notes offline_access", fake.Scope);
        Assert.Null(fake.SecretSentIn);
        var server = (await (await client.GetAsync("/api/mcp")).ReadAsync<List<McpServerView>>()).Single();
        Assert.False(server.SignInNeeded);
        Assert.Null(server.LastError);
        Assert.Equal([("list_notes", true)], server.Tools.Select(t => (t.Name, t.Enabled)));

        // Used in a conversation.
        app.Llm.ThenCall("notes_list_notes", "{}").ThenSay("[relaxed:0.5] Oat milk, and the vet.");
        await using var stream = await SseClient.ConnectAsync(client);
        await stream.ReadUntilAsync(EventTypes.Ready);
        await client.PostAsJsonAsync("/api/conversation/messages", new SendMessageRequest("what's on my notes?", "UTC"));
        await stream.ReadUntilStateAsync(FamiliarState.Idle);
        Assert.Contains("Buy oat milk", app.Llm.LastPrompt!.Last(m => m.Role == ChatRole.Tool).Content, StringComparison.Ordinal);

        // The access token's turned away: refreshed, and the call goes through.
        fake.Expire();
        app.Llm.ThenCall("notes_list_notes", "{}").ThenSay("[relaxed:0.5] Same as before.");
        await client.PostAsJsonAsync("/api/conversation/messages", new SendMessageRequest("and now?", "UTC"));
        await stream.ReadUntilStateAsync(FamiliarState.Idle);
        Assert.Contains("Buy oat milk", app.Llm.LastPrompt!.Last(m => m.Role == ChatRole.Tool).Content, StringComparison.Ordinal);
        Assert.Equal(1, fake.Refreshes);

        // About to run out: refreshed before it's used, so the server never says no.
        fake.ExpiresIn = 30;
        fake.Expire();
        Assert.Null((await (await client.PostAsync($"/api/mcp/{server.Id}/refresh", null)).ReadAsync<McpServerView>()).LastError);
        var turnedAway = fake.Unauthorized;
        Assert.Null((await (await client.PostAsync($"/api/mcp/{server.Id}/refresh", null)).ReadAsync<McpServerView>()).LastError);
        Assert.Equal(turnedAway, fake.Unauthorized);
        Assert.Equal(3, fake.Refreshes);

        // Signed out at the other end: it needs signing in again, and says so.
        fake.Revoke();
        var signedOut = await (await client.PostAsync($"/api/mcp/{server.Id}/refresh", null)).ReadAsync<McpServerView>();
        Assert.True(signedOut.SignInNeeded);
        Assert.Equal(McpOAuth.SignInAgain, signedOut.LastError);
        Assert.Equal([("list_notes", true)], signedOut.Tools.Select(t => (t.Name, t.Enabled)));
        app.Llm.ThenCall("notes_list_notes", "{}").ThenSay("[concern:0.5] I need you to sign in to your notes again.");
        await client.PostAsJsonAsync("/api/conversation/messages", new SendMessageRequest("notes?", "UTC"));
        await stream.ReadUntilStateAsync(FamiliarState.Idle);
        Assert.Contains("needs signing in", app.Llm.LastPrompt!.Last(m => m.Role == ChatRole.Tool).Content, StringComparison.Ordinal);

        // Signing in again uses the client it registered the first time.
        fake.ExpiresIn = 3600;
        Assert.Equal("http://localhost/settings/connections?mcp=notes", await SignInAsync(client, app, fake, server.Id));
        var again = (await (await client.GetAsync("/api/mcp")).ReadAsync<List<McpServerView>>()).Single();
        Assert.False(again.SignInNeeded);
        Assert.Null(again.LastError);
        Assert.Single(fake.RegisteredRedirects);
    }

    [Fact]
    public async Task A_client_the_user_made_signs_in_with_its_secret_in_the_form_asking_only_what_they_said()
    {
        await using var fake = await Fake.StartAsync();
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync(),
            services: s => s.AddHttpClient(nameof(McpTools)).ConfigurePrimaryHttpMessageHandler(() => fake.Handler));
        var client = app.CreateClient();
        (await client.RegisterAsync("handmade")).EnsureSuccessStatusCode();

        var added = await (await client.PostAsJsonAsync("/api/mcp",
            new McpServerRequest("notes", "http://notes.test/mcp", null, ClientId: "client-own", ClientSecret: "s3cret", Scopes: "notes, read"))).ReadAsync<McpServerView>();
        Assert.True(added.SignInNeeded);

        Assert.Equal("http://localhost/settings/connections?mcp=notes", await SignInAsync(client, app, fake, added.Id));
        Assert.Empty(fake.RegisteredRedirects);
        Assert.Equal("notes read", fake.Scope);
        Assert.Equal("form", fake.SecretSentIn);
        Assert.False((await (await client.GetAsync("/api/mcp")).ReadAsync<List<McpServerView>>()).Single().SignInNeeded);
    }

    [Fact]
    public async Task A_sign_in_comes_back_to_the_public_address_and_only_with_the_state_it_was_given()
    {
        await using var fake = await Fake.StartAsync();
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync(),
            settings: new Dictionary<string, string> { ["Auth:PublicUrl"] = "https://familiar.example.net/" },
            services: s => s.AddHttpClient(nameof(McpTools)).ConfigurePrimaryHttpMessageHandler(() => fake.Handler));
        var client = app.CreateClient();
        (await client.RegisterAsync("stately")).EnsureSuccessStatusCode();
        var added = await (await client.PostAsJsonAsync("/api/mcp", new McpServerRequest("notes", "http://notes.test/mcp", null))).ReadAsync<McpServerView>();

        var start = await (await client.PostAsync($"/api/mcp/{added.Id}/sign-in", null)).ReadAsync<McpSignInView>();
        var query = HttpUtility.ParseQueryString(new Uri(start.AuthorizationUrl).Query);
        Assert.Equal("https://familiar.example.net/api/mcp/oauth/callback", query["redirect_uri"]);
        Assert.Equal(["https://familiar.example.net/api/mcp/oauth/callback"], fake.RegisteredRedirects);

        var familiar = app.CreateClient(new() { AllowAutoRedirect = false });
        var forged = await familiar.GetAsync($"/api/mcp/oauth/callback?code=the-code&state={added.Id:N}.not-the-state");
        Assert.StartsWith("https://familiar.example.net/settings/connections?mcpError=", forged.Headers.Location!.OriginalString, StringComparison.Ordinal);

        var cancelled = await familiar.GetAsync($"/api/mcp/oauth/callback?error=access_denied&state={Uri.EscapeDataString(query["state"]!)}");
        Assert.Equal(
            $"https://familiar.example.net/settings/connections?mcp=notes&mcpError={Uri.EscapeDataString("Signing in was cancelled.")}",
            cancelled.Headers.Location!.OriginalString);
        var server = (await (await client.GetAsync("/api/mcp")).ReadAsync<List<McpServerView>>()).Single();
        Assert.True(server.SignInNeeded);
        Assert.Null(fake.AccessToken);
    }

    [Fact]
    public async Task A_server_that_turns_familiar_away_without_saying_where_to_sign_in_still_just_wants_a_token()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        await using var closed = builder.Build();
        closed.Run(http => { http.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; });
        await closed.StartAsync();
        var handler = closed.GetTestServer().CreateHandler();

        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync(),
            services: s => s.AddHttpClient(nameof(McpTools)).ConfigurePrimaryHttpMessageHandler(() => handler));
        var client = app.CreateClient();
        (await client.RegisterAsync("tokenish")).EnsureSuccessStatusCode();

        var added = await (await client.PostAsJsonAsync("/api/mcp", new McpServerRequest("closed", "http://closed.test/mcp", null))).ReadAsync<McpServerView>();
        Assert.Equal(McpAuth.None, added.Auth);
        Assert.False(added.SignInNeeded);
        Assert.StartsWith("Couldn't list its tools", added.LastError, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync($"/api/mcp/{added.Id}/sign-in", null)).StatusCode);
    }
}
