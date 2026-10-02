using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace Familiar.Server.Infrastructure;

/// <summary>
/// Which proxies may tell the server the real client address and scheme (the <c>Proxy</c> section).
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing configured means forwarded headers are off</b>, not on. If both trust lists are empty
/// the framework trusts the headers from anyone, so any client could claim any address and walk
/// around the per-address rate limits.
/// </para>
/// <para>
/// When configured, the framework's default loopback trust is cleared so that only what is listed
/// is trusted, and the forward limit is lifted so the walk can cross both hops of a typical NAS
/// deployment (front TLS proxy → client nginx → server), still bounded by the trust list.
/// </para>
/// </remarks>
public sealed class ProxyOptions
{
    public const string Section = "Proxy";

    /// <summary>Comma-separated CIDRs, e.g. the compose network: <c>172.30.0.0/16</c>.</summary>
    public string KnownNetworks { get; set; } = string.Empty;

    /// <summary>Comma-separated addresses.</summary>
    public string KnownProxies { get; set; } = string.Empty;

    public static ProxyOptions From(IConfiguration configuration) =>
        configuration.GetSection(Section).Get<ProxyOptions>() ?? new ProxyOptions();

    public void Apply(ForwardedHeadersOptions options)
    {
        var networks = Split(KnownNetworks).Select(ParseNetwork).ToList();
        var proxies = Split(KnownProxies).Select(ParseAddress).ToList();

        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();

        if (networks.Count == 0 && proxies.Count == 0)
        {
            options.ForwardedHeaders = ForwardedHeaders.None;
            return;
        }

        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.ForwardLimit = null;

        foreach (var network in networks)
        {
            options.KnownIPNetworks.Add(network);
        }

        foreach (var proxy in proxies)
        {
            options.KnownProxies.Add(proxy);
        }
    }

    public string Describe() =>
        string.IsNullOrWhiteSpace(KnownNetworks) && string.IsNullOrWhiteSpace(KnownProxies)
            ? "off (no Proxy:KnownNetworks or Proxy:KnownProxies)"
            : $"trusting networks [{KnownNetworks}] proxies [{KnownProxies}]";

    private static IEnumerable<string> Split(string value) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // A typo here fails startup rather than quietly trusting nothing (or everything).
    private static System.Net.IPNetwork ParseNetwork(string value) =>
        System.Net.IPNetwork.TryParse(value, out var network)
            ? network
            : throw new InvalidOperationException($"Proxy:KnownNetworks entry '{value}' is not a CIDR network.");

    private static IPAddress ParseAddress(string value) =>
        IPAddress.TryParse(value, out var address)
            ? address
            : throw new InvalidOperationException($"Proxy:KnownProxies entry '{value}' is not an IP address.");
}
