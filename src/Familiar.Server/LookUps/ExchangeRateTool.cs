using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using Familiar.Agent.Tools;
using Familiar.Domain.Tools;
using Microsoft.Extensions.Caching.Memory;

namespace Familiar.Server.LookUps;

/// <summary>
/// exchange_rate: today's rate between two currencies, and an amount converted, from Frankfurter
/// (the European Central Bank's daily reference rates; free, no key). Codes in, numbers out.
/// </summary>
public sealed class ExchangeRateTool(IHttpClientFactory http, IMemoryCache cache) : ITool
{
    private const string Api = "https://api.frankfurter.dev/v1";

    /// <summary>What people call them, for the codes the ECB publishes.</summary>
    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["dollar"] = "USD", ["dollars"] = "USD", ["us dollar"] = "USD", ["us dollars"] = "USD", ["buck"] = "USD", ["bucks"] = "USD",
        ["pound"] = "GBP", ["pounds"] = "GBP", ["sterling"] = "GBP", ["quid"] = "GBP",
        ["euro"] = "EUR", ["euros"] = "EUR",
        ["yen"] = "JPY",
        ["canadian dollar"] = "CAD", ["canadian dollars"] = "CAD",
        ["australian dollar"] = "AUD", ["australian dollars"] = "AUD",
        ["franc"] = "CHF", ["swiss franc"] = "CHF", ["swiss francs"] = "CHF",
        ["yuan"] = "CNY", ["renminbi"] = "CNY",
        ["rupee"] = "INR", ["rupees"] = "INR",
        ["peso"] = "MXN", ["pesos"] = "MXN",
        ["krona"] = "SEK", ["kronor"] = "SEK",
        ["krone"] = "NOK", ["kroner"] = "NOK",
        ["zloty"] = "PLN",
        ["won"] = "KRW",
        ["rand"] = "ZAR",
        ["real"] = "BRL", ["reais"] = "BRL",
        ["lira"] = "TRY",
    };

    public ToolDefinition Definition { get; } = new(
        "exchange_rate",
        "Today's exchange rate between two currencies, and an amount converted. Use when the user asks what something is worth " +
        "in another currency. About 30 major currencies (the European Central Bank's reference rates, set once each working day).",
        ToolSchema.Object(
            ["from", "to"],
            ("from", ToolSchema.String("The currency to convert from: a code like \"USD\" or a name like \"pounds\".")),
            ("to", ToolSchema.String("The currency to convert to.")),
            ("amount", ToolSchema.Number("How much of the first currency. Leave out for 1."))),
        ToolRisk.LookUp);

    public string Describe(JsonObject arguments) =>
        $"Check the exchange rate from {ToolArgs.String(arguments, "from")} to {ToolArgs.String(arguments, "to")}";

    public async Task<ToolResult> InvokeAsync(ToolContext context, JsonObject arguments, CancellationToken ct)
    {
        var known = await CurrenciesAsync(ct);
        var from = Code(ToolArgs.String(arguments, "from"), known);
        var to = Code(ToolArgs.String(arguments, "to"), known);
        if (from is null || to is null)
        {
            var which = from is null ? ToolArgs.String(arguments, "from") : ToolArgs.String(arguments, "to");
            return ToolResult.Failure(
                $"\"{which}\" isn't one of the currencies there's a rate for (about 30 major ones). Say so, or ask which currency they mean.",
                "Rate: unknown currency");
        }

        var amount = ToolArgs.Number(arguments, "amount") ?? 1;
        if (amount is <= 0 or > 1e12)
        {
            return ToolResult.Failure("That amount doesn't make sense. Ask how much they mean.");
        }

        var rates = await RatesAsync(from, ct);
        var rate = from == to ? 1 : rates["rates"]?[to]?.GetValue<double>();
        if (rate is null)
        {
            return ToolResult.Failure("The rate didn't come back. Say exchange rates aren't available just now.");
        }

        var date = DateOnly.TryParse(rates["date"]?.GetValue<string>(), CultureInfo.InvariantCulture, out var d) ? d : (DateOnly?)null;
        var facts = new JsonObject
        {
            ["ok"] = true,
            ["from"] = $"{from} ({known[from]})",
            ["to"] = $"{to} ({known[to]})",
            ["rate"] = Math.Round(rate.Value, 4),
            ["amount"] = amount,
            ["converted"] = Math.Round(amount * rate.Value, amount * rate.Value >= 100 ? 0 : 2),
            ["rate_set_on"] = date?.ToString("d MMMM yyyy", CultureInfo.InvariantCulture),
            ["about"] = "A reference rate: banks and card companies charge a little more.",
        };
        return ToolResult.Success(
            LookUpText.Result("Frankfurter (European Central Bank rates)", facts),
            FormattableString.Invariant($"Rate: 1 {from} = {Math.Round(rate.Value, 4)} {to}"));
    }

    /// <summary>A currency's code from its code or a common name; null if there's no rate for it.</summary>
    internal static string? Code(string? asked, IReadOnlyDictionary<string, string> known)
    {
        var text = asked?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        if (text.Length == 3 && known.ContainsKey(text.ToUpperInvariant()))
        {
            return text.ToUpperInvariant();
        }

        if (Names.TryGetValue(text, out var named) && known.ContainsKey(named))
        {
            return named;
        }

        // "British Pound", "Japanese yen": the ECB's own names.
        return known.FirstOrDefault(k => string.Equals(k.Value, text, StringComparison.OrdinalIgnoreCase)).Key;
    }

    /// <summary>The currencies there are rates for, code to name; cached for a week.</summary>
    private async Task<IReadOnlyDictionary<string, string>> CurrenciesAsync(CancellationToken ct) =>
        (await cache.GetOrCreateAsync("currencies", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromDays(7);
            using var response = await http.CreateClient(nameof(LookUps)).GetAsync($"{Api}/currencies", ct);
            response.EnsureSuccessStatusCode();
            var list = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct)) as JsonObject ?? [];
            return (IReadOnlyDictionary<string, string>)list.ToDictionary(p => p.Key, p => LookUpText.Clean(p.Value?.GetValue<string>(), 40), StringComparer.OrdinalIgnoreCase);
        }))!;

    /// <summary>Every rate from one currency; the ECB sets them once a working day, so an hour's cache is plenty.</summary>
    private async Task<JsonObject> RatesAsync(string from, CancellationToken ct) =>
        (await cache.GetOrCreateAsync($"rates:{from}", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1);
            using var response = await http.CreateClient(nameof(LookUps)).GetAsync($"{Api}/latest?base={WebUtility.UrlEncode(from)}", ct);
            response.EnsureSuccessStatusCode();
            return JsonNode.Parse(await response.Content.ReadAsStringAsync(ct)) as JsonObject ?? [];
        }))!;
}
