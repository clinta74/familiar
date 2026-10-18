using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Familiar.Server.LookUps;

/// <summary>
/// One part of a Wikipedia article, as text (BACKLOG item 33): look_up's summary is an article's
/// opening paragraph, and what's asked is often further down - a district's candidates, a city's
/// climate, an album list - much of it in tables. The part is chosen by its headings, every heading
/// beneath it is kept, and each gets a short share of the room, so a long section (a district's
/// endorsements, fundraising and polls) still shows its whole shape and its first lines.
/// </summary>
public static partial class WikipediaSections
{
    /// <summary>The most text handed back for a section.</summary>
    public const int MaxText = 3500;

    /// <summary>The most for any one part under its heading: long lists and tables are cut, not the parts after them.</summary>
    public const int MaxPart = 450;

    /// <summary>
    /// Of a part that's cut, this much of its start is kept and the rest of the room goes to its end:
    /// lists of office-holders run oldest first, results tables end in their totals - the current
    /// senator was the one row cut off.
    /// </summary>
    public const int KeptStart = 150;

    private static readonly HashSet<string> Small = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "of", "in", "on", "for", "to", "and", "my", "our", "their", "s", "about", "what", "who", "is", "are",
    };

    /// <summary>Parts that are only ever pointers elsewhere.</summary>
    private static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase)
    {
        "References", "Notes", "External links", "See also", "Further reading", "Citations", "Sources", "Bibliography", "Gallery", "Footnotes",
    };

    /// <summary>A section: its index in the article, its heading, and the headings above it ("District 10 › General election › Results").</summary>
    public sealed record Section(string Index, string Heading, int Level, string Path);

    /// <summary>The article's sections with their paths, from the parse API's list.</summary>
    public static IReadOnlyList<Section> Outline(JsonArray sections)
    {
        var outline = new List<Section>();
        var above = new List<(int Level, string Heading)>();
        foreach (var s in sections.OfType<JsonObject>())
        {
            var heading = LookUpText.Clean(Strip(s["line"]?.GetValue<string>() ?? ""), 120);
            var level = int.TryParse(s["level"]?.GetValue<string>(), out var l) ? l : 2;
            var index = s["index"]?.GetValue<string>() ?? "";
            above.RemoveAll(a => a.Level >= level);
            above.Add((level, heading));
            if (heading.Length > 0 && index.All(char.IsAsciiDigit) && index.Length > 0)
            {
                outline.Add(new Section(index, heading, level, string.Join(" › ", above.Select(a => a.Heading))));
            }
        }

        return outline;
    }

    /// <summary>
    /// The section that best matches [find], by the words of its heading and the headings above it:
    /// "Michigan's 10th district" finds "District 10"; ties go to the larger part (the district, not
    /// one of its results). Null when no heading shares a word with it.
    /// </summary>
    public static Section? Best(IReadOnlyList<Section> outline, string find)
    {
        var wanted = Words(find);
        if (wanted.Count == 0)
        {
            return null;
        }

        return outline
            .Where(s => !Skipped.Contains(s.Heading))
            .Select(s => (Section: s, Score: wanted.Count(w => Words(s.Path).Contains(w)), Own: wanted.Count(w => Words(s.Heading).Contains(w))))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Own)
            .ThenBy(x => x.Section.Level)
            .Select(x => x.Section)
            .FirstOrDefault();
    }

    /// <summary>
    /// A section's HTML as text: headings as "## Heading", list items as "- item", a table a row to a
    /// line with its cells between " | "; references, styles and edit links gone; each part under a
    /// heading cut to <see cref="MaxPart"/> and the whole to <see cref="MaxText"/>.
    /// </summary>
    public static string Text(string html)
    {
        var text = Noise().Replace(html, "");
        text = Row().Replace(text, m => "\n" + string.Join(" | ",
            Cell().Split(m.Groups[1].Value).Select(c => Collapse(Strip(c))).Where(c => c.Length > 0)) + "\n");
        text = Heading().Replace(text, m => $"\n{new string('#', int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))} {Collapse(Strip(m.Groups[2].Value))}\n");
        text = ListItem().Replace(text, "\n- ");
        text = LineBreak().Replace(text, ", ");
        text = Block().Replace(text, "\n");
        text = WebUtility.HtmlDecode(Tag().Replace(text, ""));

        // Parts under each heading, each kept short; the pointer-only ones left out.
        var result = new StringBuilder();
        var part = new StringBuilder();
        var skipping = false;
        void Flush()
        {
            if (part.Length > 0 && !skipping)
            {
                result.Append(Shorten(part.ToString().TrimEnd())).Append('\n');
            }

            part.Clear();
        }

        foreach (var raw in text.Split('\n'))
        {
            var line = Collapse(raw);
            if (line.Length == 0 || line is "|" or "-")
            {
                continue;
            }

            if (line.StartsWith('#'))
            {
                Flush();
                skipping = Skipped.Contains(line.TrimStart('#', ' '));
                if (!skipping)
                {
                    result.Append(line).Append('\n');
                }

                continue;
            }

            part.Append(line).Append('\n');
        }

        Flush();
        var all = UrlLike().Replace(result.ToString(), "");
        return all.Length > MaxText ? all[..MaxText].TrimEnd() + " …" : all.TrimEnd();
    }

    /// <summary>A part as it's kept: whole if it fits, else its first lines and its last, a line apart.</summary>
    internal static string Shorten(string part)
    {
        if (part.Length <= MaxPart)
        {
            return part;
        }

        var head = part[..KeptStart];
        head = head.Contains('\n') ? head[..head.LastIndexOf('\n')] : head;
        var tail = part[^(MaxPart - KeptStart)..];
        tail = tail.Contains('\n') ? tail[(tail.IndexOf('\n') + 1)..] : tail;
        return $"{head.TrimEnd()}\n…\n{tail.TrimStart()}";
    }

    /// <summary>The words that matter for matching: lower case, "10th" as "10", "Michigan's" as "michigan", small words gone.</summary>
    internal static HashSet<string> Words(string text) =>
        [.. WordPattern().Matches(text.ToLowerInvariant())
            .Select(m => Ordinal().Replace(m.Value, "$1"))
            .Where(w => !Small.Contains(w))];

    private static string Strip(string html) => WebUtility.HtmlDecode(Tag().Replace(html, ""));

    private static string Collapse(string text) => Spaces().Replace(text, " ").Trim();

    [GeneratedRegex(@"<sup\b[^>]*>.*?</sup>|<style\b[^>]*>.*?</style>|<script\b[^>]*>.*?</script>|<span class=""mw-editsection"">.*?</span>|<!--.*?-->", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex Noise();

    [GeneratedRegex(@"<tr\b[^>]*>(.*?)</tr>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex Row();

    [GeneratedRegex(@"</t[dh]>", RegexOptions.IgnoreCase)]
    private static partial Regex Cell();

    [GeneratedRegex(@"<h([2-6])\b[^>]*>(.*?)</h\1>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex Heading();

    [GeneratedRegex(@"<li\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ListItem();

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreak();

    [GeneratedRegex(@"</(p|li|div|ul|ol|dl|dd|dt|table|caption)>|<(dd|dt)\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex Block();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tag();

    [GeneratedRegex(@"[ \t ]+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex WordPattern();

    [GeneratedRegex(@"^(\d+)(st|nd|rd|th)$")]
    private static partial Regex Ordinal();

    [GeneratedRegex(@"https?://\S+|www\.\S+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlLike();
}
