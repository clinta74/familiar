using System.Text.Json.Nodes;
using Familiar.Agent.Tools;
using Familiar.Domain.Tools;

namespace Familiar.Server.Devices;

/// <summary>
/// The stores shop can open, and where: each one's own search page (which its app opens, when the
/// phone has it), and Amazon's Buy again. Built here from fixed patterns - the model only says which
/// store and what - and the phone opens nothing but these stores.
/// </summary>
public static class Stores
{
    public static readonly string[] Names = ["amazon", "target", "walmart", "kroger", "meijer", "costco"];

    /// <summary>Amazon's site for where they live; amazon.com otherwise.</summary>
    private static readonly Dictionary<string, string> AmazonSites = new(StringComparer.OrdinalIgnoreCase)
    {
        ["US"] = "www.amazon.com", ["GB"] = "www.amazon.co.uk", ["CA"] = "www.amazon.ca", ["AU"] = "www.amazon.com.au",
        ["DE"] = "www.amazon.de", ["FR"] = "www.amazon.fr", ["IT"] = "www.amazon.it", ["ES"] = "www.amazon.es",
        ["NL"] = "www.amazon.nl", ["SE"] = "www.amazon.se", ["PL"] = "www.amazon.pl", ["IE"] = "www.amazon.ie",
        ["JP"] = "www.amazon.co.jp", ["IN"] = "www.amazon.in", ["MX"] = "www.amazon.com.mx", ["BR"] = "www.amazon.com.br",
    };

    public static string Shown(string store) => store switch
    {
        "amazon" => "Amazon",
        "target" => "Target",
        "walmart" => "Walmart",
        "kroger" => "Kroger",
        "meijer" => "Meijer",
        "costco" => "Costco",
        _ => store,
    };

    /// <summary>The store's search page for <paramref name="terms"/>; null for a store not on the list.</summary>
    public static string? SearchUrl(string store, string terms, string? countryCode)
    {
        var q = Uri.EscapeDataString(terms);
        return store switch
        {
            "amazon" => $"https://{Amazon(countryCode)}/s?k={q}",
            "target" => $"https://www.target.com/s?searchTerm={q}",
            "walmart" => $"https://www.walmart.com/search?q={q}",
            "kroger" => $"https://www.kroger.com/search?query={q}",
            "meijer" => $"https://www.meijer.com/shopping/search.html?text={q}",
            "costco" => $"https://www.costco.com/s?keyword={q}",
            _ => null,
        };
    }

    /// <summary>Amazon's Buy again: what they've bought before (it asks them to sign in, in the app or browser).</summary>
    public static string BuyAgainUrl(string? countryCode) => $"https://{Amazon(countryCode)}/gp/buyagain";

    private static string Amazon(string? countryCode) =>
        countryCode is not null && AmazonSites.TryGetValue(countryCode, out var site) ? site : "www.amazon.com";
}

/// <summary>
/// shop: opens a store's app (or site) on the phone at a search for what they want, or Amazon's Buy
/// again, for them to look and buy (the shopping hand-off, like texts and directions). It never buys,
/// signs in or sees a basket: it gets them to the right screen.
/// </summary>
public sealed class ShopTool(DeviceTools devices) : PhoneTool(devices)
{
    public override string Capability => DeviceCapabilities.Shop;

    public override ToolDefinition Definition { get; } = new(
        "shop",
        "Opens a store's app on the user's phone at a search for what they want - or Amazon's Buy again, their past orders - " +
        "for them to look and buy themselves. You never buy anything. Stores: Amazon, Target, Walmart, Kroger, Meijer, Costco. " +
        "Use when they want to buy, order, or find something in one of them.",
        ToolSchema.Object(
            ("store", ToolSchema.Enum("Which store, as they said; Amazon if they didn't say.", Stores.Names)),
            ("search", ToolSchema.String("What to look for, in a few words: \"AA batteries\", \"oat milk\".")),
            ("buy_again", ToolSchema.Boolean("Amazon's Buy again, to reorder something they've had before, instead of a search."))),
        ToolRisk.WriteOwn);

    public override string Describe(JsonObject arguments) =>
        $"Open {Stores.Shown(ToolArgs.String(arguments, "store") ?? "amazon")}" + (ToolArgs.String(arguments, "search") is { } s ? $": {s}" : "");

    public override Task<ToolResult> InvokeAsync(ToolContext context, JsonObject arguments, CancellationToken ct)
    {
        var store = ToolArgs.String(arguments, "store")?.Trim().ToLowerInvariant() is { Length: > 0 } named ? named : "amazon";
        if (!Stores.Names.Contains(store))
        {
            return Task.FromResult(ToolResult.Failure($"That store isn't one this can open. It can open {string.Join(", ", Stores.Names.Select(Stores.Shown))}."));
        }

        var country = context.Home?.CountryCode;
        var search = ToolArgs.Label(arguments, "search", 100);
        string url;
        string title;
        if (ToolArgs.Bool(arguments, "buy_again") == true && store == "amazon")
        {
            url = Stores.BuyAgainUrl(country);
            title = "Amazon: Buy again";
        }
        else if (search is null)
        {
            return Task.FromResult(ToolResult.Failure("Ask what they're looking for."));
        }
        else
        {
            url = Stores.SearchUrl(store, search, country)!;
            title = $"{Stores.Shown(store)}: {search}";
        }

        return AskPhoneAsync(context, new JsonObject { ["url"] = url, ["title"] = title }, _ => $"Opened {title}", ct);
    }
}
