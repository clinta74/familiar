using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Familiar.Persistence;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace Familiar.Server.Notifications;

public sealed class PushOptions
{
    public const string Section = "Push";

    /// <summary>
    /// Who runs this server, for the push services to contact if its pushes misbehave (RFC 8292). A
    /// mailto: or https: URL.
    /// </summary>
    public string Subject { get; init; } = "https://github.com/clinta74/familiar";

    /// <summary>
    /// The ntfy server phones push through (UnifiedPush), e.g. https://ntfy.example.lan. Endpoints on
    /// it are accepted even though it's on the local network; nothing else local is.
    /// </summary>
    public string? UnifiedPushServer { get; init; }

    /// <summary>Whether <paramref name="endpoint"/> may be pushed to.</summary>
    public bool Accepts(string endpoint) =>
        PushSender.IsAcceptableEndpoint(endpoint) || IsUnifiedPushEndpoint(endpoint);

    private bool IsUnifiedPushEndpoint(string endpoint) =>
        endpoint.Length <= 1024
        && UnifiedPushServer is { Length: > 0 } server
        && Uri.TryCreate(server, UriKind.Absolute, out var origin)
        && Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
        && uri.Scheme == origin.Scheme
        && string.Equals(uri.Host, origin.Host, StringComparison.OrdinalIgnoreCase)
        && uri.Port == origin.Port
        && uri.AbsolutePath.Length > 1;
}

/// <summary>
/// The server's VAPID key pair (RFC 8292): how push services know pushes come from the server the
/// browser subscribed to. Generated on first use and kept in the database, so subscriptions survive
/// restarts and work across server instances.
/// </summary>
public sealed class VapidKeys(IDbContextFactory<FamiliarDbContext> db, TimeProvider clock)
{
    private const string SecretName = "vapid";
    private readonly SemaphoreSlim _load = new(1, 1);
    private ECDsa? _key;
    private string? _publicKey;

    /// <summary>The public key, base64url, uncompressed point - what the browser's <c>applicationServerKey</c> wants.</summary>
    public async Task<string> PublicKeyAsync(CancellationToken ct)
    {
        await EnsureAsync(ct);
        return _publicKey!;
    }

    /// <summary>
    /// The <c>Authorization</c> header for one push service: a JWT signed with the private key whose
    /// audience is the push service's origin.
    /// </summary>
    public async Task<string> AuthorizationAsync(Uri endpoint, string subject, CancellationToken ct)
    {
        await EnsureAsync(ct);

        var header = Base64Url("""{"typ":"JWT","alg":"ES256"}"""u8.ToArray());
        var claims = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["aud"] = endpoint.GetLeftPart(UriPartial.Authority),
            ["exp"] = clock.GetUtcNow().AddHours(12).ToUnixTimeSeconds(),
            ["sub"] = subject,
        }));

        // JWS wants the raw r||s signature, not the DER .NET produces by default.
        var signature = _key!.SignData(Encoding.ASCII.GetBytes($"{header}.{claims}"), HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        return $"vapid t={header}.{claims}.{Base64Url(signature)}, k={_publicKey}";
    }

    private async Task EnsureAsync(CancellationToken ct)
    {
        if (_key is not null)
        {
            return;
        }

        await _load.WaitAsync(ct);
        try
        {
            if (_key is not null)
            {
                return;
            }

            await using var context = await db.CreateDbContextAsync(ct);
            var stored = await context.ServerSecrets.AsNoTracking().FirstOrDefaultAsync(s => s.Name == SecretName, ct);

            if (stored is null)
            {
                using var generated = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                var pkcs8 = Convert.ToBase64String(generated.ExportPkcs8PrivateKey());

                // ON CONFLICT: another instance may have just generated one too; whichever landed first wins.
                await context.Database.ExecuteSqlAsync(
                    $"INSERT INTO server_secrets (name, value, created_at) VALUES ({SecretName}, {pkcs8}, {clock.GetUtcNow()}) ON CONFLICT (name) DO NOTHING",
                    ct);
                stored = await context.ServerSecrets.AsNoTracking().FirstAsync(s => s.Name == SecretName, ct);
            }

            var key = ECDsa.Create();
            key.ImportPkcs8PrivateKey(Convert.FromBase64String(stored.Value), out _);

            var point = key.ExportParameters(false).Q;
            _publicKey = Base64Url([0x04, .. point.X!, .. point.Y!]);
            _key = key;
        }
        finally
        {
            _load.Release();
        }
    }

    private static string Base64Url(byte[] bytes) => WebEncoders.Base64UrlEncode(bytes);
}

/// <summary>
/// Web Push without content (PLAN.md §6.2): each push only says "something happened". The browser's
/// push service - Google's, Mozilla's, Apple's - so never sees a timer's label or a reminder's text;
/// the service worker fetches those from Familiar itself. That also means no payload encryption to
/// implement (RFC 8291), only VAPID.
/// </summary>
public sealed class PushSender(
    IHttpClientFactory httpFactory,
    VapidKeys keys,
    PushOptions options,
    IDbContextFactory<FamiliarDbContext> db,
    TimeProvider clock,
    ILogger<PushSender> logger)
{
    /// <summary>Something happened: every browser and phone the account has subscribed hears of it.</summary>
    /// <param name="accountId">Whose browsers and phones.</param>
    /// <param name="ct">Cancels sending.</param>
    /// <param name="devicesOnly">Just the phones: their timers changed, which browsers don't need waking for.</param>
    public async Task NotifyAsync(Guid accountId, CancellationToken ct, bool devicesOnly = false)
    {
        await using var context = await db.CreateDbContextAsync(ct);
        var now = clock.GetUtcNow();

        // A phone's only while it's still signed in (signed out some other way than here, or expired).
        var subscriptions = await context.PushSubscriptions
            .Where(s => s.AccountId == accountId && (!devicesOnly || s.DeviceId != null))
            .Where(s => s.DeviceId == null || context.AccessTokens.Any(t => t.Id == s.DeviceId && t.RevokedAt == null && t.ExpiresAt > now))
            .ToListAsync(ct);

        if (subscriptions.Count == 0)
        {
            return;
        }

        var http = httpFactory.CreateClient(nameof(PushSender));
        foreach (var subscription in subscriptions)
        {
            try
            {
                var endpoint = new Uri(subscription.Endpoint);
                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
                {
                    Content = new ByteArrayContent([]),
                };
                request.Headers.TryAddWithoutValidation("Authorization", await keys.AuthorizationAsync(endpoint, options.Subject, ct));

                // Worth delivering for a while (a phone in a tunnel), but a timer an hour late is just noise.
                request.Headers.Add("TTL", "3600");
                request.Headers.Add("Urgency", "high");

                using var response = await http.SendAsync(request, ct);
                if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
                {
                    // The browser unsubscribed, or the subscription expired.
                    context.PushSubscriptions.Remove(subscription);
                    logger.PushSubscriptionGone(endpoint.Host);
                }
                else if (!response.IsSuccessStatusCode)
                {
                    logger.PushRejected(endpoint.Host, (int)response.StatusCode, await response.Content.ReadAsStringAsync(ct));
                }
                else
                {
                    subscription.LastPushedAt = clock.GetUtcNow();
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                logger.PushFailed(subscription.Endpoint.Length > 0 ? new Uri(subscription.Endpoint).Host : "?", exception.Message);
            }
        }

        await context.SaveChangesAsync(CancellationToken.None);
    }

    /// <summary>
    /// Only real push services: https, a name rather than an address, and not this machine or the
    /// local network - otherwise subscribing would let a user make the server POST wherever they like.
    /// </summary>
    public static bool IsAcceptableEndpoint(string endpoint) =>
        endpoint.Length <= 1024
        && Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.HostNameType == UriHostNameType.Dns
        && uri.Host.Contains('.', StringComparison.Ordinal)
        && !uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
        && !uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
        && !uri.Host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase)
        && uri.IsDefaultPort;
}
