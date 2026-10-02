using System.Net;
using System.Net.Sockets;

namespace Familiar.Server.Infrastructure;

/// <summary>
/// Checks a URL a user asked Familiar to fetch or POST to (a calendar feed, a webhook, an MCP
/// server): http(s), and not this machine or a link-local address such as cloud metadata. The local
/// network is allowed on purpose - Home Assistant and Nextcloud usually live there - so this guards
/// against mistakes and the obvious abuses rather than being a sandbox.
/// </summary>
public static class OutboundUrl
{
    public static Uri? Check(string? text, bool allowWebcal = false)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 2048 || !Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri))
        {
            return null;
        }

        // webcal:// is just "subscribe to this with your calendar app" - it's https underneath.
        if (allowWebcal && uri.Scheme.Equals("webcal", StringComparison.OrdinalIgnoreCase))
        {
            uri = new UriBuilder(uri) { Scheme = Uri.UriSchemeHttps, Port = uri.IsDefaultPort ? -1 : uri.Port }.Uri;
        }

        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
        {
            return null;
        }

        if (uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address)
            && (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)
                || (address.AddressFamily == AddressFamily.InterNetwork && address.GetAddressBytes() is [169, 254, ..])
                || address.IsIPv6LinkLocal))
        {
            return null;
        }

        return uri;
    }
}
