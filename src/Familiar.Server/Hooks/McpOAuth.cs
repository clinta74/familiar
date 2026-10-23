using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Familiar.Domain.Hooks;
using Familiar.Persistence;
using Familiar.Server.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Familiar.Server.Hooks;

/// <summary>An MCP server's OAuth sign-in, kept encrypted on its row (<see cref="McpServer.ProtectedOAuth"/>).</summary>
public sealed class McpOAuthGrant
{
    public string? Issuer { get; set; }

    public required string AuthorizationEndpoint { get; set; }

    public required string TokenEndpoint { get; set; }

    public string? RegistrationEndpoint { get; set; }

    /// <summary>The resource indicator (RFC 8707) tokens are asked for: the MCP server itself.</summary>
    public required string Resource { get; set; }

    public string? Scope { get; set; }

    /// <summary>The authorization server names itself in its redirects (RFC 9207), so the name is checked.</summary>
    public bool IssInRedirect { get; set; }

    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }

    /// <summary><c>none</c>, <c>client_secret_basic</c> or <c>client_secret_post</c>.</summary>
    public string TokenAuthMethod { get; set; } = "none";

    /// <summary>Where the client Familiar registered for itself sends people back; null for one the user gave.</summary>
    public string? RegisteredRedirectUri { get; set; }

    public string? AccessToken { get; set; }

    public string? RefreshToken { get; set; }

    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>A sign-in under way: what the callback must bring back, and the PKCE secret for it.</summary>
    public string? PendingState { get; set; }

    public string? PendingVerifier { get; set; }

    public string? PendingRedirectUri { get; set; }

    public DateTimeOffset? PendingUntil { get; set; }
}

/// <summary>Signing in to an MCP server didn't work; <see cref="SignInNeeded"/> when only the user signing in again will fix it.</summary>
public sealed class McpOAuthException(string message, bool signInNeeded) : Exception(message)
{
    public bool SignInNeeded { get; } = signInNeeded;
}

/// <summary>
/// The MCP authorization flow (spec 2025-06-18 and later), for the hosted MCP servers that want a
/// sign-in rather than a token: discovery from the server's 401 (protected-resource metadata, then
/// the authorization server's), dynamic client registration, the browser sign-in with PKCE, and
/// tokens refreshed before they run out or when they're turned away.
/// </summary>
/// <remarks>
/// The SDK's own OAuth provider waits inside the request for the code to come back, as a desktop app
/// would; here the sign-in happens in the user's browser and comes back to an endpoint later, maybe
/// after a restart, so the flow's state lives on the server's row.
/// </remarks>
public sealed partial class McpOAuth(
    IDbContextFactory<FamiliarDbContext> db,
    IDataProtectionProvider protection,
    IHttpClientFactory httpFactory,
    TimeProvider clock)
{
    /// <summary>What a server's error says when its sign-in has stopped working.</summary>
    public const string SignInAgain = "Needs signing in again.";

    /// <summary>What a newly added server's error says until it's signed in to.</summary>
    public const string SignInFirst = "Sign in to connect it.";

    /// <summary>How long the browser has to come back from the sign-in.</summary>
    public static readonly TimeSpan SignInWindow = TimeSpan.FromMinutes(15);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private const string Initialize =
        """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"Familiar","version":"1"}}}""";

    private readonly IDataProtector protector = protection.CreateProtector("Familiar.Hooks.Mcp.OAuth");

    /// <summary>One refresh at a time per server: refresh tokens are often single-use.</summary>
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> gates = new();

    public McpOAuthGrant? Read(McpServer server) =>
        server.ProtectedOAuth is { } text ? JsonSerializer.Deserialize<McpOAuthGrant>(protector.Unprotect(text), Json) : null;

    public void Write(McpServer server, McpOAuthGrant grant) =>
        server.ProtectedOAuth = protector.Protect(JsonSerializer.Serialize(grant, Json));

    /// <summary>
    /// Asks the server, unsigned, to start a session. A 401 that leads to an authorization server
    /// means it wants an OAuth sign-in: the grant comes back, with nobody signed in yet. Anything else
    /// (it answers, or it wants a token it doesn't say how to get) is null.
    /// </summary>
    public async Task<McpOAuthGrant?> DiscoverAsync(Uri endpoint, CancellationToken ct)
    {
        var http = Http();
        using var probe = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new StringContent(Initialize, Encoding.UTF8, "application/json") };
        probe.Headers.Accept.ParseAdd("application/json");
        probe.Headers.Accept.ParseAdd("text/event-stream");
        using var response = await http.SendAsync(probe, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            return null;
        }

        var challenge = ChallengeOf(response);

        // Protected-resource metadata (RFC 9728): where the challenge says, else where it's usually kept.
        var origin = endpoint.GetLeftPart(UriPartial.Authority);
        var path = endpoint.AbsolutePath.TrimEnd('/');
        List<string?> places = [challenge.GetValueOrDefault("resource_metadata"), $"{origin}/.well-known/oauth-protected-resource{path}", $"{origin}/.well-known/oauth-protected-resource"];
        ResourceMetadata? resource = null;
        foreach (var place in places.Distinct())
        {
            if (await GetAsync<ResourceMetadata>(http, place, ct) is { AuthorizationServers: [_, ..] } found)
            {
                resource = found;
                break;
            }
        }

        // Servers from before protected-resource metadata are their own authorization server.
        var issuer = resource is null ? new Uri(origin) : OutboundUrl.Check(resource.AuthorizationServers![0]);
        if (issuer is null || await AuthorizationServerAsync(http, issuer, ct) is not { } server
            || OutboundUrl.Check(server.AuthorizationEndpoint) is null || OutboundUrl.Check(server.TokenEndpoint) is null)
        {
            return null;
        }

        var scope = challenge.GetValueOrDefault("scope")
            ?? (resource?.ScopesSupported is [_, ..] scopes ? string.Join(' ', scopes) : null);

        // Without offline_access some servers give no refresh token, and the sign-in lasts an hour.
        if (server.ScopesSupported?.Contains("offline_access") == true && scope?.Split(' ').Contains("offline_access") != true)
        {
            scope = scope is null ? "offline_access" : $"{scope} offline_access";
        }

        return new McpOAuthGrant
        {
            Issuer = server.Issuer,
            AuthorizationEndpoint = server.AuthorizationEndpoint!,
            TokenEndpoint = server.TokenEndpoint!,
            RegistrationEndpoint = OutboundUrl.Check(server.RegistrationEndpoint)?.AbsoluteUri,
            Resource = resource?.Resource ?? endpoint.GetLeftPart(UriPartial.Query),
            Scope = scope,
            IssInRedirect = server.IssInRedirect == true,
            TokenAuthMethod = server.TokenAuthMethods is { } methods && !methods.Contains("client_secret_basic") && methods.Contains("client_secret_post")
                ? "client_secret_post"
                : "client_secret_basic",
        };
    }

    /// <summary>
    /// Readies a sign-in: registers Familiar with the authorization server if it hasn't (or did for a
    /// different address), and gives the address to send the user's browser to.
    /// </summary>
    public async Task<Uri> BeginAsync(McpOAuthGrant grant, Guid serverId, string redirectUri, CancellationToken ct)
    {
        if (grant.ClientId is null || (grant.RegisteredRedirectUri is not null && grant.RegisteredRedirectUri != redirectUri))
        {
            await RegisterAsync(grant, redirectUri, ct);
        }

        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        grant.PendingState = $"{serverId:N}.{Base64Url(RandomNumberGenerator.GetBytes(24))}";
        grant.PendingVerifier = verifier;
        grant.PendingRedirectUri = redirectUri;
        grant.PendingUntil = clock.GetUtcNow() + SignInWindow;

        var query = new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = grant.ClientId,
            ["redirect_uri"] = redirectUri,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["state"] = grant.PendingState,
            ["scope"] = grant.Scope,
            ["resource"] = grant.Resource,
        };
        var encoded = string.Join('&', query.Where(p => p.Value is not null).Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value!)}"));
        return new Uri($"{grant.AuthorizationEndpoint}{(grant.AuthorizationEndpoint.Contains('?', StringComparison.Ordinal) ? '&' : '?')}{encoded}");
    }

    /// <summary>The server a callback's state belongs to, if it's one of ours.</summary>
    public static Guid? ServerOf(string? state) =>
        state is { Length: > 33 } && state[32] == '.' && Guid.TryParseExact(state[..32], "N", out var id) ? id : null;

    /// <summary>The browser's back with a code: checks it's the sign-in begun, and swaps the code for tokens.</summary>
    public async Task CompleteAsync(McpOAuthGrant grant, string? state, string? code, string? iss, CancellationToken ct)
    {
        if (!IsPending(grant, state))
        {
            throw new McpOAuthException("That sign-in had expired. Try again.", true);
        }

        if (grant.IssInRedirect && grant.Issuer is not null && iss != grant.Issuer)
        {
            throw new McpOAuthException("The sign-in came back from somewhere else. Try again.", true);
        }

        if (string.IsNullOrEmpty(code))
        {
            throw new McpOAuthException("The sign-in didn't finish. Try again.", true);
        }

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = grant.PendingRedirectUri!,
            ["code_verifier"] = grant.PendingVerifier!,
        };
        Abandon(grant);
        Apply(grant, await TokenAsync(grant, form, ct));
    }

    /// <summary>The state is the sign-in under way, and it hasn't run out.</summary>
    public bool IsPending(McpOAuthGrant grant, string? state) =>
        grant.PendingState is not null && state is not null && grant.PendingUntil >= clock.GetUtcNow()
        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(grant.PendingState), Encoding.UTF8.GetBytes(state));

    public static void Abandon(McpOAuthGrant grant)
    {
        grant.PendingState = grant.PendingVerifier = grant.PendingRedirectUri = null;
        grant.PendingUntil = null;
    }

    /// <summary>
    /// The access token for a signed-in server, refreshed first if it's about to run out or was just
    /// turned away (<paramref name="rejected"/>). When it can't be, the server's marked as needing a
    /// new sign-in and this throws.
    /// </summary>
    public async Task<string> AccessTokenAsync(Guid serverId, string? rejected, CancellationToken ct)
    {
        var gate = gates.GetOrAdd(serverId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            await using var context = await db.CreateDbContextAsync(ct);
            var server = await context.McpServers.FirstOrDefaultAsync(s => s.Id == serverId, ct)
                ?? throw new McpOAuthException("It's been removed.", true);
            var grant = Read(server);
            if (grant?.AccessToken is null)
            {
                throw new McpOAuthException(server.SignInNeeded && server.RefreshedAt is not null ? SignInAgain : SignInFirst, true);
            }

            // Another call may have refreshed it while this one waited.
            if (grant.AccessToken != rejected && (grant.ExpiresAt is null || grant.ExpiresAt > clock.GetUtcNow().AddMinutes(1)))
            {
                return grant.AccessToken;
            }

            try
            {
                if (grant.RefreshToken is null)
                {
                    throw new McpOAuthException(SignInAgain, true);
                }

                Apply(grant, await TokenAsync(grant, new() { ["grant_type"] = "refresh_token", ["refresh_token"] = grant.RefreshToken }, ct));
            }
            catch (McpOAuthException exception) when (exception.SignInNeeded)
            {
                grant.AccessToken = grant.RefreshToken = null;
                grant.ExpiresAt = null;
                Write(server, grant);
                server.SignInNeeded = true;
                server.LastError = SignInAgain;
                await context.SaveChangesAsync(CancellationToken.None);
                throw new McpOAuthException(SignInAgain, true);
            }

            Write(server, grant);
            server.SignInNeeded = false;
            await context.SaveChangesAsync(CancellationToken.None);
            return grant.AccessToken!;
        }
        finally
        {
            gate.Release();
        }
    }

    private HttpClient Http() => httpFactory.CreateClient(nameof(McpTools));

    /// <summary>Dynamic client registration (RFC 7591): Familiar introduces itself, as a public client using PKCE.</summary>
    private async Task RegisterAsync(McpOAuthGrant grant, string redirectUri, CancellationToken ct)
    {
        if (grant.RegistrationEndpoint is null)
        {
            throw new McpOAuthException(
                "This server doesn't let apps register themselves. Remove it and add it again with a client ID from its developer settings.", false);
        }

        using var response = await Http().PostAsJsonAsync(grant.RegistrationEndpoint, new RegistrationRequest(
            "Familiar", [redirectUri], ["authorization_code", "refresh_token"], ["code"], "none", grant.Scope), Json, ct);
        if (!response.IsSuccessStatusCode || await ReadAsync<RegistrationResponse>(response, ct) is not { ClientId: { } clientId } registered)
        {
            throw new McpOAuthException($"The server wouldn't register Familiar ({await ErrorOfAsync(response, ct)}).", false);
        }

        grant.ClientId = clientId;
        grant.ClientSecret = registered.ClientSecret;
        grant.TokenAuthMethod = registered.ClientSecret is null ? "none" : registered.TokenEndpointAuthMethod ?? "client_secret_basic";
        grant.RegisteredRedirectUri = redirectUri;
    }

    private async Task<TokenResponse> TokenAsync(McpOAuthGrant grant, Dictionary<string, string> form, CancellationToken ct)
    {
        form["client_id"] = grant.ClientId!;
        form["resource"] = grant.Resource;
        using var request = new HttpRequestMessage(HttpMethod.Post, grant.TokenEndpoint);
        request.Headers.Accept.ParseAdd("application/json");
        if (grant.ClientSecret is { } secret)
        {
            if (grant.TokenAuthMethod == "client_secret_post")
            {
                form["client_secret"] = secret;
            }
            else
            {
                var basic = $"{Uri.EscapeDataString(grant.ClientId!)}:{Uri.EscapeDataString(secret)}";
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(basic)));
            }
        }

        request.Content = new FormUrlEncodedContent(form);
        using var response = await Http().SendAsync(request, ct);
        if (response.IsSuccessStatusCode && await ReadAsync<TokenResponse>(response, ct) is { AccessToken: { Length: > 0 } } tokens)
        {
            return tokens;
        }

        // 400 is the authorization server saying no (invalid_grant: the code or refresh token is
        // spent); a 5xx might pass, so the sign-in's kept for next time.
        var refused = response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized || response.IsSuccessStatusCode;
        throw new McpOAuthException($"The sign-in was turned down ({await ErrorOfAsync(response, ct)}).", refused);
    }

    private void Apply(McpOAuthGrant grant, TokenResponse tokens)
    {
        grant.AccessToken = tokens.AccessToken;
        grant.RefreshToken = tokens.RefreshToken ?? grant.RefreshToken;
        grant.ExpiresAt = tokens.ExpiresIn is > 0 and var seconds ? clock.GetUtcNow().AddSeconds(seconds) : null;
    }

    /// <summary>Authorization-server metadata (RFC 8414, or OpenID's), from the places the MCP spec says to look, in order.</summary>
    private static async Task<ServerMetadata?> AuthorizationServerAsync(HttpClient http, Uri issuer, CancellationToken ct)
    {
        var origin = issuer.GetLeftPart(UriPartial.Authority);
        var path = issuer.AbsolutePath.TrimEnd('/');
        string[] places = path.Length == 0
            ? [$"{origin}/.well-known/oauth-authorization-server", $"{origin}/.well-known/openid-configuration"]
            : [$"{origin}/.well-known/oauth-authorization-server{path}", $"{origin}/.well-known/openid-configuration{path}", $"{origin}{path}/.well-known/openid-configuration"];
        foreach (var place in places)
        {
            if (await GetAsync<ServerMetadata>(http, place, ct) is { AuthorizationEndpoint: not null, TokenEndpoint: not null } found)
            {
                return found;
            }
        }

        return null;
    }

    private static async Task<T?> GetAsync<T>(HttpClient http, string? place, CancellationToken ct) where T : class
    {
        if (OutboundUrl.Check(place) is not { } url)
        {
            return null;
        }

        try
        {
            using var response = await http.GetAsync(url, ct);
            return response.IsSuccessStatusCode ? await ReadAsync<T>(response, ct) : null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    private static async Task<T?> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct) where T : class
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(Json, ct);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            return null;
        }
    }

    private static async Task<string> ErrorOfAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var error = await ReadAsync<ErrorResponse>(response, ct);
        var text = error?.ErrorDescription ?? error?.Error ?? $"{(int)response.StatusCode} {response.ReasonPhrase}";
        return text.Length <= 200 ? text : text[..200];
    }

    /// <summary>The parameters of a <c>WWW-Authenticate: Bearer …</c> challenge.</summary>
    private static Dictionary<string, string> ChallengeOf(HttpResponseMessage response)
    {
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var challenge in response.Headers.WwwAuthenticate.Where(h => h.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (Match match in ChallengeParameter().Matches(challenge.Parameter ?? ""))
            {
                parameters.TryAdd(match.Groups[1].Value, match.Groups[2].Success ? Regex.Unescape(match.Groups[2].Value) : match.Groups[3].Value);
            }
        }

        return parameters;
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [GeneratedRegex("""([A-Za-z0-9_-]+)\s*=\s*(?:"((?:[^"\\]|\\.)*)"|([^\s,]+))""")]
    private static partial Regex ChallengeParameter();

    private sealed record ResourceMetadata(
        [property: JsonPropertyName("resource")] string? Resource,
        [property: JsonPropertyName("authorization_servers")] string[]? AuthorizationServers,
        [property: JsonPropertyName("scopes_supported")] string[]? ScopesSupported);

    private sealed record ServerMetadata(
        [property: JsonPropertyName("issuer")] string? Issuer,
        [property: JsonPropertyName("authorization_endpoint")] string? AuthorizationEndpoint,
        [property: JsonPropertyName("token_endpoint")] string? TokenEndpoint,
        [property: JsonPropertyName("registration_endpoint")] string? RegistrationEndpoint,
        [property: JsonPropertyName("scopes_supported")] string[]? ScopesSupported,
        [property: JsonPropertyName("token_endpoint_auth_methods_supported")] string[]? TokenAuthMethods,
        [property: JsonPropertyName("authorization_response_iss_parameter_supported")] bool? IssInRedirect);

    private sealed record RegistrationRequest(
        [property: JsonPropertyName("client_name")] string ClientName,
        [property: JsonPropertyName("redirect_uris")] string[] RedirectUris,
        [property: JsonPropertyName("grant_types")] string[] GrantTypes,
        [property: JsonPropertyName("response_types")] string[] ResponseTypes,
        [property: JsonPropertyName("token_endpoint_auth_method")] string TokenEndpointAuthMethod,
        [property: JsonPropertyName("scope"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Scope);

    private sealed record RegistrationResponse(
        [property: JsonPropertyName("client_id")] string? ClientId,
        [property: JsonPropertyName("client_secret")] string? ClientSecret,
        [property: JsonPropertyName("token_endpoint_auth_method")] string? TokenEndpointAuthMethod);

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("expires_in")] long? ExpiresIn);

    private sealed record ErrorResponse(
        [property: JsonPropertyName("error")] string? Error,
        [property: JsonPropertyName("error_description")] string? ErrorDescription);
}
