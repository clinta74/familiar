using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Familiar.Agent.Tools;
using Familiar.Domain.Tools;
using Microsoft.Extensions.Caching.Memory;

namespace Familiar.Server.LookUps;

/// <summary>
/// define: what a word means, from Wiktionary's REST API - its English senses, a few short lines,
/// as plain text. A word in, definitions out; never a page.
/// </summary>
public sealed partial class DefineTool(IHttpClientFactory http, IMemoryCache cache) : ITool
{
    /// <summary>Parts of speech, and senses of each, handed back: enough to explain, short to read out.</summary>
    private const int MaxParts = 3;
    private const int MaxSenses = 2;
    private const int MaxSenseLength = 160;

    public ToolDefinition Definition { get; } = new(
        "define",
        "Look up what an English word or short phrase means. Use when the user asks what a word means or how it's used.",
        ToolSchema.Object(("word", ToolSchema.String("The word or phrase, e.g. \"petrichor\"."))),
        ToolRisk.LookUp);

    public string Describe(JsonObject arguments) => $"Look up \"{ToolArgs.String(arguments, "word")}\"";

    public async Task<ToolResult> InvokeAsync(ToolContext context, JsonObject arguments, CancellationToken ct)
    {
        var word = ToolArgs.String(arguments, "word")?.Trim() ?? string.Empty;
        if (!Word().IsMatch(word))
        {
            return ToolResult.Failure("Give one word or a short phrase to look up.");
        }

        var senses = await cache.GetOrCreateAsync($"define:{word.ToLowerInvariant()}", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromDays(1);
            // Wiktionary's titles are case-sensitive: "Paris" and "paris" are different entries.
            return await FetchAsync(word, ct) ?? (word != word.ToLowerInvariant() ? await FetchAsync(word.ToLowerInvariant(), ct) : null);
        });

        if (senses is null || senses.Count == 0)
        {
            return ToolResult.Failure($"No definition of \"{word}\" was found. Say so; don't guess.", $"No definition: {word}");
        }

        return ToolResult.Success(
            LookUpText.Result("Wiktionary", new JsonObject
            {
                ["ok"] = true,
                ["word"] = word,
                ["meanings"] = new JsonArray([.. senses.Select(s => (JsonNode?)new JsonObject { ["as"] = s.PartOfSpeech, ["meaning"] = s.Meaning })]),
            }),
            $"Looked up: {word}");
    }

    private sealed record Sense(string PartOfSpeech, string Meaning);

    private async Task<List<Sense>?> FetchAsync(string word, CancellationToken ct)
    {
        using var response = await http.CreateClient(nameof(LookUps)).GetAsync(
            $"https://en.wiktionary.org/api/rest_v1/page/definition/{Uri.EscapeDataString(word.Replace(' ', '_'))}", ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct)) as JsonObject;
        if (body?["en"] is not JsonArray english)
        {
            return [];
        }

        var senses = new List<Sense>();
        foreach (var part in english.OfType<JsonObject>().Take(MaxParts))
        {
            var partOfSpeech = LookUpText.Clean(part["partOfSpeech"]?.GetValue<string>(), 24).ToLowerInvariant();
            var definitions = (part["definitions"] as JsonArray)?.OfType<JsonObject>()
                .Select(d => LookUpText.Clean(d["definition"]?.GetValue<string>(), MaxSenseLength))
                .Where(d => d.Length > 0)
                .Take(MaxSenses) ?? [];
            senses.AddRange(definitions.Select(d => new Sense(partOfSpeech, d)));
        }

        return senses;
    }

    /// <summary>Letters, with spaces, hyphens and apostrophes between: a word or a short phrase.</summary>
    [GeneratedRegex(@"^\p{L}[\p{L}\p{M}' -]{0,39}$")]
    private static partial Regex Word();
}
