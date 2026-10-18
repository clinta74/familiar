using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Familiar.Agent.Tools;
using Familiar.Domain.Tools;
using Microsoft.Extensions.Caching.Memory;

namespace Familiar.Server.LookUps;

/// <summary>
/// look_up: facts about a person, place, thing or event - the start of its Wikipedia article, and
/// the key facts from its Wikidata entry (each article names its entry, so it's one tool for both).
/// A topic in, short plain text and dated facts out; never a page. For anything that may have
/// changed since the model learned it: it said a bridge "should open in 2025" a year after it opened.
/// With "find", one part of the article instead (BACKLOG item 33): a district's candidates, a city's
/// climate, a band's albums - what the opening paragraph never has.
/// </summary>
public sealed partial class LookUpTool(IHttpClientFactory http, IMemoryCache cache) : ITool
{
    private const int MaxSummary = 700;

    /// <summary>
    /// The Wikidata properties worth reading out, in the order they're given. Others (identifiers,
    /// images, links) are left out: they're noise to the model, and a link is never handed over.
    /// </summary>
    private static readonly (string Id, string Name)[] Properties =
    [
        ("P31", "is a"),
        ("P17", "country"),
        ("P131", "located in"),
        ("P571", "founded or begun"),
        ("P1619", "officially opened"),
        ("P729", "in service since"),
        ("P730", "out of service since"),
        ("P576", "ended or demolished"),
        ("P580", "start"),
        ("P582", "end"),
        ("P569", "born"),
        ("P19", "place of birth"),
        ("P570", "died"),
        ("P106", "occupation"),
        ("P27", "citizen of"),
        ("P39", "position held"),
        ("P1308", "held now by"),
        ("P36", "capital"),
        ("P35", "head of state"),
        ("P6", "head of government"),
        ("P1082", "population"),
        ("P2046", "area"),
        ("P2043", "length"),
        ("P2048", "height"),
        ("P2044", "elevation"),
        ("P38", "currency"),
        ("P37", "official language"),
        ("P577", "published"),
        ("P50", "author"),
        ("P57", "director"),
        ("P175", "performer"),
        ("P84", "architect"),
        ("P127", "owned by"),
        ("P137", "operated by"),
        ("P176", "made by"),
        ("P1448", "official name"),
    ];

    /// <summary>At most this many values per property (a country's many languages, say).</summary>
    private const int MaxValues = 3;

    public ToolDefinition Definition { get; } = new(
        "look_up",
        "Facts about a person, place, thing or event, from Wikipedia and Wikidata. Your own knowledge may be out of date: " +
        "use this for anything that could have changed - openings, completions, records, who holds an office, recent events - " +
        "and for figures like populations, heights and dates. With find, it reads that part of the article instead: who's " +
        "standing in a district, a list, a table of results or a climate - anything past its first paragraph. Not for news, " +
        "events, opening hours or prices: say you can't look those up.",
        ToolSchema.Object(
            ("topic", ToolSchema.String(
                "What to look up. Its exact name if you're sure of it (\"Mount Everest\", \"2026 United States House of " +
                "Representatives elections in Michigan\"); otherwise the user's own words (\"new bridge from Canada to Michigan\") " +
                "- a guessed name finds the wrong thing.")),
            ("find", ToolSchema.String(
                "Optional: the part of the article to read, in a few words - \"District 10\", \"Results\", \"Climate\", " +
                "\"Discography\". Leave out for the summary."))),
        ToolRisk.LookUp);

    public string Describe(JsonObject arguments) => $"Look up {ToolArgs.String(arguments, "topic")}";

    public async Task<ToolResult> InvokeAsync(ToolContext context, JsonObject arguments, CancellationToken ct)
    {
        var topic = ToolArgs.String(arguments, "topic")?.Trim() ?? string.Empty;
        if (!Topic().IsMatch(topic))
        {
            return ToolResult.Failure("Say what to look up in a few words.");
        }

        Found? found;
        try
        {
            found = await cache.GetOrCreateAsync($"lookup:{topic.ToLowerInvariant()}", async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(12);
                return await FindAsync(topic, ct);
            });
        }
        catch (HttpRequestException exception) when (exception.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
        {
            return ToolResult.Failure("Wikipedia is busy just now. Say you couldn't look it up and they could ask again in a minute.", "Wikipedia busy");
        }

        if (found is null)
        {
            return ToolResult.Failure($"Nothing was found about \"{topic}\". Say so; don't guess.", $"Nothing found: {topic}");
        }

        if (found.Ambiguous)
        {
            return ToolResult.Failure($"\"{found.Title}\" could mean several things. Ask the user which one they mean.", $"Looked up: {found.Title}");
        }

        if (ToolArgs.String(arguments, "find") is { Length: > 0 } find)
        {
            // The topic's words beyond the article's title say which part too: asked for "Candidates" in
            // "... elections in Michigan's 10th congressional district", it's District 10 that's meant.
            var extra = WikipediaSections.Words(topic).Except(WikipediaSections.Words(found.Title));
            return await SectionAsync(found, find.Length > 100 ? find[..100] : find, extra, ct);
        }

        var facts = new JsonObject
        {
            ["ok"] = true,
            ["title"] = found.Title,
            ["summary"] = found.Summary,
            ["as_of"] = found.Edited,
        };
        if (found.Description.Length > 0)
        {
            facts["description"] = found.Description;
        }

        if (found.Facts.Count > 0)
        {
            facts["facts"] = new JsonObject(found.Facts.Select(f => KeyValuePair.Create(f.Key, (JsonNode?)f.Value)));
        }

        return ToolResult.Success(LookUpText.Result("Wikipedia and Wikidata", facts), $"Looked up: {found.Title}");
    }

    private sealed record Found(string Title, string Description, string Summary, string Edited, bool Ambiguous, IReadOnlyDictionary<string, string> Facts);

    /// <summary>The part of [found]'s article that [find] names; or, when no heading fits, what parts there are.</summary>
    private async Task<ToolResult> SectionAsync(Found found, string find, IEnumerable<string> extra, CancellationToken ct)
    {
        var page = Uri.EscapeDataString(found.Title);
        IReadOnlyList<WikipediaSections.Section> outline;
        try
        {
            outline = await cache.GetOrCreateAsync($"lookup-sections:{found.Title}", async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(6);
                var parsed = await GetJsonAsync($"https://en.wikipedia.org/w/api.php?action=parse&page={page}&prop=sections&redirects=1&format=json", ct);
                return parsed?["parse"]?["sections"] is JsonArray sections ? WikipediaSections.Outline(sections) : [];
            }) ?? [];
        }
        catch (HttpRequestException exception) when (exception.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
        {
            return ToolResult.Failure("Wikipedia is busy just now. Say you couldn't look it up and they could ask again in a minute.", "Wikipedia busy");
        }

        if ((WikipediaSections.Best(outline, find + " " + string.Join(' ', extra)) ?? WikipediaSections.Best(outline, find)) is not { } section)
        {
            var parts = string.Join("; ", outline.Where(s => s.Level <= 3).Select(s => s.Heading).Distinct().Take(40));
            // Its opening paragraph too: it often says what was asked ("currently represented by ...").
            return ToolResult.Failure(
                $"\"{found.Title}\" has no part about \"{find}\". It begins: \"{found.Summary}\" Its parts are: {parts}. If that " +
                "doesn't answer it, call look_up again with one of those parts as find.", $"Looked up: {found.Title}");
        }

        var text = await cache.GetOrCreateAsync($"lookup-section:{found.Title}:{section.Index}", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(6);
            var parsed = await GetJsonAsync(
                $"https://en.wikipedia.org/w/api.php?action=parse&page={page}&section={section.Index}&prop=text&redirects=1&disableeditsection=1&disablelimitreport=1&format=json", ct);
            return parsed?["parse"]?["text"]?["*"]?.GetValue<string>() is { } html ? WikipediaSections.Text(html) : "";
        }) ?? "";

        return ToolResult.Success(LookUpText.Result("Wikipedia", new JsonObject
        {
            ["ok"] = true,
            ["title"] = found.Title,
            ["part"] = section.Path,
            ["text"] = text,
            ["as_of"] = found.Edited,
        }), $"Looked up: {found.Title} › {section.Heading}");
    }

    private HttpClient Client() => http.CreateClient(nameof(LookUps));

    private async Task<Found?> FindAsync(string topic, CancellationToken ct)
    {
        // The best-matching article (the search understands "the new bridge from Canada to Michigan").
        var search = await GetJsonAsync($"https://en.wikipedia.org/w/rest.php/v1/search/page?q={Uri.EscapeDataString(topic)}&limit=1", ct);
        if ((search?["pages"] as JsonArray)?.FirstOrDefault()?["key"]?.GetValue<string>() is not { } key)
        {
            return null;
        }

        var page = await GetJsonAsync($"https://en.wikipedia.org/api/rest_v1/page/summary/{Uri.EscapeDataString(key)}", ct);
        if (page is null)
        {
            return null;
        }

        var title = LookUpText.Clean(page["title"]?.GetValue<string>(), 120);
        var edited = DateTimeOffset.TryParse(page["timestamp"]?.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
            ? at.ToString("d MMMM yyyy", CultureInfo.InvariantCulture)
            : string.Empty;
        var ambiguous = page["type"]?.GetValue<string>() == "disambiguation";
        var facts = !ambiguous && page["wikibase_item"]?.GetValue<string>() is { } item && EntityId().IsMatch(item)
            ? await WikidataAsync(item, ct)
            : new Dictionary<string, string>();

        return new Found(
            title,
            LookUpText.Clean(page["description"]?.GetValue<string>(), 160),
            LookUpText.Clean(page["extract"]?.GetValue<string>(), MaxSummary),
            edited,
            ambiguous,
            facts);
    }

    /// <summary>The chosen properties of a Wikidata entry, in words: dates by their precision, amounts with units, items by name.</summary>
    private async Task<Dictionary<string, string>> WikidataAsync(string item, CancellationToken ct)
    {
        var entity = (await GetJsonAsync($"https://www.wikidata.org/w/api.php?action=wbgetentities&ids={item}&props=claims&format=json", ct))?["entities"]?[item];
        if (entity?["claims"] is not JsonObject claims)
        {
            return [];
        }

        var chosen = new List<(string Name, List<JsonObject> Values)>();
        foreach (var (id, name) in Properties)
        {
            var statements = (claims[id] as JsonArray)?.OfType<JsonObject>()
                .Where(s => s["rank"]?.GetValue<string>() != "deprecated" && s["mainsnak"]?["snaktype"]?.GetValue<string>() == "value")
                .ToList() ?? [];
            // The current ones: marked preferred (today's population), or else those that haven't ended
            // (an office's holders each have an end time but the sitting one).
            var preferred = statements.Where(s => s["rank"]?.GetValue<string>() == "preferred").ToList();
            var current = statements.Where(s => s["qualifiers"]?["P582"] is null).ToList();
            var values = (preferred.Count > 0 ? preferred : current.Count > 0 ? current : statements)
                .Take(MaxValues).Select(s => (JsonObject)s["mainsnak"]!["datavalue"]!).ToList();
            if (values.Count > 0)
            {
                chosen.Add((name, values));
            }
        }

        // Items and units are ids ("Q30"): one more request for all their names.
        var ids = chosen.SelectMany(c => c.Values)
            .Select(v => v["type"]?.GetValue<string>() switch
            {
                "wikibase-entityid" => v["value"]?["id"]?.GetValue<string>(),
                "quantity" => UnitId(v["value"]?["unit"]?.GetValue<string>()),
                _ => null,
            })
            .OfType<string>().Where(i => EntityId().IsMatch(i)).Distinct().Take(50).ToList();
        var names = new Dictionary<string, string>();
        if (ids.Count > 0)
        {
            var labels = (await GetJsonAsync($"https://www.wikidata.org/w/api.php?action=wbgetentities&ids={string.Join('|', ids)}&props=labels&languages=en&format=json", ct))?["entities"] as JsonObject;
            foreach (var (id, labelled) in labels ?? [])
            {
                if (labelled?["labels"]?["en"]?["value"]?.GetValue<string>() is { } label)
                {
                    names[id] = LookUpText.Clean(label, 80);
                }
            }
        }

        var facts = new Dictionary<string, string>();
        foreach (var (name, values) in chosen)
        {
            var words = values.Select(v => Words(v, names)).OfType<string>().Where(w => w.Length > 0).Distinct().ToList();
            if (words.Count > 0)
            {
                facts[name] = string.Join(", ", words);
            }
        }

        return facts;
    }

    private static string? Words(JsonObject datavalue, Dictionary<string, string> names)
    {
        var value = datavalue["value"];
        switch (datavalue["type"]?.GetValue<string>())
        {
            case "wikibase-entityid":
                return value?["id"]?.GetValue<string>() is { } id ? names.GetValueOrDefault(id) : null;
            case "time":
                return Date(value?["time"]?.GetValue<string>(), value?["precision"]?.GetValue<int>() ?? 11);
            case "quantity":
                var amount = value?["amount"]?.GetValue<string>()?.TrimStart('+');
                if (!decimal.TryParse(amount, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                {
                    return null;
                }

                var unit = UnitId(value?["unit"]?.GetValue<string>()) is { } u ? names.GetValueOrDefault(u) : null;
                return number.ToString("#,0.##", CultureInfo.InvariantCulture) + (unit is null ? "" : $" {unit}");
            case "monolingualtext":
                return LookUpText.Clean(value?["text"]?.GetValue<string>(), 120);
            case "string":
                return LookUpText.Clean(value?.GetValue<string>(), 120);
            default:
                return null;
        }
    }

    /// <summary>"+2026-03-15T00:00:00Z" at its precision: a year (9), a month (10) or a day (11).</summary>
    internal static string? Date(string? time, int precision)
    {
        var match = time is null ? null : WikidataTime().Match(time);
        if (match is null || !match.Success)
        {
            return null;
        }

        var bce = match.Groups["sign"].Value == "-";
        var year = int.Parse(match.Groups["y"].Value, CultureInfo.InvariantCulture);
        var month = int.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture);
        var day = int.Parse(match.Groups["d"].Value, CultureInfo.InvariantCulture);
        var era = bce ? " BC" : "";
        return precision switch
        {
            >= 11 when month > 0 && day > 0 => $"{day} {CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(month)} {year}{era}",
            >= 10 when month > 0 => $"{CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(month)} {year}{era}",
            9 => $"{year}{era}",
            8 => $"the {year / 10 * 10}s{era}",
            7 => $"the {OrdinalCentury(year)} century{era}",
            _ => $"{year}{era}",
        };
    }

    private static string OrdinalCentury(int year)
    {
        var century = (year - 1) / 100 + 1;
        var suffix = (century % 100) is 11 or 12 or 13 ? "th" : (century % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };
        return $"{century}{suffix}";
    }

    /// <summary>A unit is given as an entity URL ("http://www.wikidata.org/entity/Q11573"); "1" means none.</summary>
    private static string? UnitId(string? unit) =>
        unit is null || unit == "1" ? null : unit[(unit.LastIndexOf('/') + 1)..];

    private async Task<JsonNode?> GetJsonAsync(string url, CancellationToken ct)
    {
        using var response = await Client().GetAsync(url, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    /// <summary>A few words: letters, digits, spaces and a little punctuation - no links or code.</summary>
    [GeneratedRegex(@"^[\p{L}\p{M}0-9][\p{L}\p{M}0-9 ,.'()&-]{0,99}$")]
    private static partial Regex Topic();

    [GeneratedRegex("^Q[0-9]{1,12}$")]
    private static partial Regex EntityId();

    [GeneratedRegex(@"^(?<sign>[+-])(?<y>\d{1,11})-(?<m>\d{2})-(?<d>\d{2})T")]
    private static partial Regex WikidataTime();
}
