using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;

namespace Familiar.Server.LookUps;

/// <summary>A page couldn't be read; the message says why, for the model to pass on.</summary>
public sealed class LinkException(string message) : Exception(message);

/// <summary>
/// Reads a public web page as plain text, for read_link (BACKLOG item 34). Only the public internet:
/// the address is checked as written and again when connecting, against every address its name
/// resolves to - so neither "http://192.168.1.10" nor a public name that points at the house reaches
/// the household network or this server. Redirects are followed by hand and checked the same way.
/// Text only, cut short: scripts, styles, menus and footers go; a page's article or main part is kept.
/// </summary>
public sealed partial class LinkReader(IHttpClientFactory http, IMemoryCache cache)
{
    public const int MaxBytes = 2_000_000;

    public const int MaxText = 4000;

    public const int MaxRedirects = 3;

    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>A page as read: where it ended up, its title and its text.</summary>
    public sealed record Page(Uri Url, string Title, string Text);

    public async Task<Page> ReadAsync(Uri url, CancellationToken ct) =>
        (await cache.GetOrCreateAsync($"link:{url.AbsoluteUri}", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(15);
            return await FetchAsync(url, ct);
        }))!;

    private async Task<Page> FetchAsync(Uri url, CancellationToken ct)
    {
        var client = http.CreateClient(nameof(LinkReader));
        var current = url;
        for (var hop = 0; ; hop++)
        {
            if (!IsPublic(current))
            {
                throw new LinkException("Only public web pages can be read: http or https on the internet, not this network.");
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,text/plain;q=0.9");
            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (Exception exception) when (exception is HttpRequestException or SocketException || (exception is TaskCanceledException && !ct.IsCancellationRequested))
            {
                throw new LinkException($"Couldn't reach {current.Host}: {exception.Message}");
            }

            using (response)
            {
                if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
                {
                    if (hop >= MaxRedirects)
                    {
                        throw new LinkException("The page kept sending it elsewhere.");
                    }

                    current = new Uri(current, location);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw new LinkException($"{current.Host} answered {(int)response.StatusCode} - the page may be gone or private.");
                }

                var type = response.Content.Headers.ContentType?.MediaType ?? "";
                if (type is not ("text/html" or "application/xhtml+xml" or "text/plain"))
                {
                    throw new LinkException($"That isn't a page it can read{(type.Length > 0 ? $" (it's {type})" : "")}.");
                }

                var body = await ReadCappedAsync(response.Content, ct);
                return type == "text/plain"
                    ? new Page(current, current.Host, Cap(Collapse(body)))
                    : FromHtml(current, body);
            }
        }
    }

    /// <summary>At most <see cref="MaxBytes"/>, in the page's own character set.</summary>
    private static async Task<string> ReadCappedAsync(HttpContent content, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        var buffer = new byte[MaxBytes];
        var total = 0;
        int read;
        while (total < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(total), ct)) > 0)
        {
            total += read;
        }

        Encoding encoding;
        try
        {
            encoding = content.Headers.ContentType?.CharSet is { Length: > 0 } charset ? Encoding.GetEncoding(charset.Trim('"')) : Encoding.UTF8;
        }
        catch (ArgumentException)
        {
            encoding = Encoding.UTF8;
        }

        return encoding.GetString(buffer, 0, total);
    }

    /// <summary>A page's title and readable text: its article or main part if it marks one, else its body.</summary>
    internal static Page FromHtml(Uri url, string html)
    {
        var title = Collapse(WebUtility.HtmlDecode(Tag().Replace(TitleTag().Match(html).Groups[1].Value, "")));
        var body = Furniture().Replace(html, "");
        var main = Article().Match(body) is { Success: true } article ? article.Value
            : MainTag().Match(body) is { Success: true } mainPart ? mainPart.Value
            : body;
        var text = Heading().Replace(main, m => $"\n## {Tag().Replace(m.Groups[2].Value, "")}\n");
        text = ListItem().Replace(text, "\n- ");
        text = LineBreak().Replace(text, "\n");
        text = Block().Replace(text, "\n");
        text = WebUtility.HtmlDecode(Tag().Replace(text, ""));
        text = Url().Replace(text, "");
        return new Page(url, title.Length > 0 ? (title.Length > 120 ? title[..120] : title) : url.Host, Cap(Collapse(text)));
    }

    /// <summary>
    /// Whether an address may be read: http or https on the usual ports, no user name or password in
    /// it, and not a name or number that's plainly this machine or this network. The connection checks
    /// again, after resolving the name (<see cref="ConnectPublicAsync"/>).
    /// </summary>
    public static bool IsPublic(Uri url) =>
        url.IsAbsoluteUri
        && (url.Scheme == Uri.UriSchemeHttps || url.Scheme == Uri.UriSchemeHttp)
        && (url.IsDefaultPort || url.Port is 80 or 443)
        && url.UserInfo.Length == 0
        && !url.IsLoopback
        && !url.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        && !LocalName().IsMatch(url.Host)
        && (!IPAddress.TryParse(url.Host.Trim('[', ']'), out var address) || IsPublic(address));

    /// <summary>An internet address, not this machine, this network, or one reserved or private.</summary>
    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)
            || address.Equals(IPAddress.Broadcast) || address.Equals(IPAddress.IPv6None))
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return bytes switch
            {
                [0, ..] or [10, ..] or [127, ..] => false,
                [100, var b, ..] when b is >= 64 and <= 127 => false, // Carrier-grade NAT.
                [169, 254, ..] => false,
                [172, var b, ..] when b is >= 16 and <= 31 => false,
                [192, 0, 0, _] or [192, 0, 2, _] or [192, 168, ..] => false,
                [198, 18 or 19, ..] or [198, 51, 100, _] or [203, 0, 113, _] => false,
                [var a, ..] when a >= 224 => false, // Multicast and reserved.
                _ => true,
            };
        }

        return !(address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast || address.IsIPv6UniqueLocal
            || bytes is [0x00, 0x64, 0xff, 0x9b, ..] // NAT64: could stand for a private IPv4 address.
            || bytes is [0x20, 0x01, 0x0d, 0xb8, ..]);
    }

    /// <summary>
    /// The connection itself: the name resolved here and only its public addresses used, so a name
    /// that resolves to the household network is refused however it's spelled.
    /// </summary>
    public static async ValueTask<Stream> ConnectPublicAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
        var allowed = addresses.Where(IsPublic).ToArray();
        if (allowed.Length == 0)
        {
            throw new HttpRequestException("that address isn't on the public internet");
        }

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>The client read_link uses: redirects by hand, every connection through <see cref="ConnectPublicAsync"/>.</summary>
    public static void Register(IServiceCollection services) =>
        services.AddHttpClient(nameof(LinkReader), client =>
        {
            client.Timeout = Timeout;
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Familiar/1.0 (https://github.com/clinta74/familiar; self-hosted household assistant)");
        }).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectCallback = ConnectPublicAsync,
            MaxResponseHeadersLength = 64,
        });

    private static string Cap(string text) => text.Length > MaxText ? text[..MaxText].TrimEnd() + " …" : text;

    private static string Collapse(string text) =>
        string.Join('\n', text.Split('\n').Select(l => Spaces().Replace(l, " ").Trim()).Where(l => l.Length > 0 && l != "-"));

    [GeneratedRegex(@"<title\b[^>]*>(.*?)</title>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex TitleTag();

    [GeneratedRegex(@"<(script|style|noscript|svg|template|iframe|nav|header|footer|aside|form|head)\b[^>]*>.*?</\1>|<!--.*?-->", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex Furniture();

    [GeneratedRegex(@"<article\b[^>]*>.*?</article>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex Article();

    [GeneratedRegex(@"<main\b[^>]*>.*?</main>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex MainTag();

    [GeneratedRegex(@"<h([1-6])\b[^>]*>(.*?)</h\1>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex Heading();

    [GeneratedRegex(@"<li\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ListItem();

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreak();

    [GeneratedRegex(@"</(p|div|li|ul|ol|tr|table|section|blockquote|pre|dd|dt)>", RegexOptions.IgnoreCase)]
    private static partial Regex Block();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tag();

    [GeneratedRegex(@"https?://\S+|www\.\S+", RegexOptions.IgnoreCase)]
    private static partial Regex Url();

    [GeneratedRegex(@"[ \t ]+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"(\.local|\.lan|\.home|\.internal|\.localdomain|\.arpa)$|^[^.]+$", RegexOptions.IgnoreCase)]
    private static partial Regex LocalName();
}
