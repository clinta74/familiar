using System.Text.RegularExpressions;

namespace Familiar.Agent.Tools;

/// <summary>Web addresses in what someone wrote (BACKLOG item 34): http and https only, without the sentence's last full stop.</summary>
public static partial class WebAddresses
{
    /// <summary>The http(s) addresses in [text], in order, as they'd be fetched.</summary>
    public static IEnumerable<string> In(string text) =>
        Address().Matches(text)
            .Select(m => m.Value.TrimEnd('.', ',', ';', ':', '!', '?', ')', ']', '\'', '"'))
            .Where(a => Uri.TryCreate(a, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
            .Select(a => new Uri(a).AbsoluteUri);

    [GeneratedRegex(@"https?://[^\s<>""'`]+", RegexOptions.IgnoreCase)]
    private static partial Regex Address();
}
