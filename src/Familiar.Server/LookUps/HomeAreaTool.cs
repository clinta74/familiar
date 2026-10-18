using System.Globalization;
using System.Text.Json.Nodes;
using Familiar.Agent.Tools;
using Familiar.Domain.Tools;
using Microsoft.Extensions.Caching.Memory;

namespace Familiar.Server.LookUps;

/// <summary>
/// home_area: the official areas where the user lives, from the US Census Bureau's geocoder (BACKLOG
/// item 33) - county, city or township, congressional and state legislative districts, school
/// district, ZIP area. Anything that depends on those starts here: who represents them, who's
/// standing for their seat, which school district, which county's services. Asked who the candidates
/// were, the model named a district from memory, and the wrong one.
/// </summary>
/// <remarks>
/// For home's coordinates, kept to about a kilometre: near a boundary, a district can be the
/// neighbouring one, and the result says so. The US only: elsewhere there's no such service to ask.
/// </remarks>
public sealed class HomeAreaTool(IHttpClientFactory http, IMemoryCache cache, Weather weather) : ITool
{
    /// <summary>The Census layers worth saying, by the end of their name (the start changes with each Congress or year).</summary>
    private static readonly (string Layer, string Key)[] Layers =
    [
        ("States", "state"),
        ("Counties", "county"),
        ("Incorporated Places", "city"),
        ("County Subdivisions", "township_or_city"),
        ("Congressional Districts", "congressional_district"),
        ("State Legislative Districts - Upper", "state_senate_district"),
        ("State Legislative Districts - Lower", "state_house_district"),
        ("Unified School Districts", "school_district"),
        ("Elementary School Districts", "elementary_school_district"),
        ("Secondary School Districts", "secondary_school_district"),
        ("ZIP Code Tabulation Areas", "zip_area"),
    ];

    public ToolDefinition Definition { get; } = new(
        "home_area",
        "The official areas where the user lives, in the US: their county, city or township, congressional district, state senate " +
        "and state house districts, school district and ZIP area, from the Census Bureau. Use it first for anything that depends on " +
        "those - who represents them, who's standing for their seat, their school district, county services - then look_up what you " +
        "need by those names (\"Michigan's 10th congressional district\"). Never guess a district.",
        ToolSchema.Object(),
        ToolRisk.LookUp);

    public string Describe(JsonObject arguments) => "Look up the areas where you live";

    public async Task<ToolResult> InvokeAsync(ToolContext context, JsonObject arguments, CancellationToken ct)
    {
        if (await weather.HomeAsync(context, ct) is not { } home)
        {
            return ToolResult.Failure("You don't know where the user lives. Ask; they can also set it in Settings, under Your familiar.");
        }

        if (!home.CountryCode.Equals("US", StringComparison.OrdinalIgnoreCase))
        {
            return ToolResult.Failure($"This only works in the US; they live in {home.Label}. Use look_up with their town or region instead.");
        }

        JsonObject? areas;
        try
        {
            areas = await cache.GetOrCreateAsync(FormattableString.Invariant($"home-area:{home.Latitude:F2},{home.Longitude:F2}"), async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromDays(7);
                using var response = await http.CreateClient(nameof(LookUps)).GetAsync(string.Create(CultureInfo.InvariantCulture,
                    $"https://geocoding.geo.census.gov/geocoder/geographies/coordinates?x={home.Longitude:F2}&y={home.Latitude:F2}&benchmark=Public_AR_Current&vintage=Current_Current&layers=all&format=json"), ct);
                response.EnsureSuccessStatusCode();
                return Areas(JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))?["result"]?["geographies"] as JsonObject);
            });
        }
        catch (HttpRequestException)
        {
            return ToolResult.Failure("The Census Bureau's service didn't answer. Say you couldn't look it up just now.", "Census unavailable");
        }

        if (areas is null || areas.Count == 0)
        {
            return ToolResult.Failure("The Census Bureau found nothing at their home. Say you couldn't find their districts.", "Nothing found");
        }

        var result = areas.DeepClone().AsObject();
        result["ok"] = true;
        result["home"] = home.Label;
        result["accuracy"] = "For the area around their home, to about a kilometre: near a boundary a district may be the neighbouring one.";
        return ToolResult.Success(LookUpText.Result("the US Census Bureau", result),
            $"Areas: {areas["county"]?.GetValue<string>() ?? home.Label}{(areas["congressional_district"] is { } d ? $", {d}" : "")}");
    }

    /// <summary>The layers we say, named for looking up: "Michigan's 10th congressional district", "Macomb County".</summary>
    internal static JsonObject? Areas(JsonObject? geographies)
    {
        if (geographies is null)
        {
            return null;
        }

        string? Name(string layer) =>
            geographies.FirstOrDefault(g => g.Key.EndsWith(layer, StringComparison.OrdinalIgnoreCase)).Value is JsonArray { Count: > 0 } found
                ? LookUpText.Clean(found[0]?["NAME"]?.GetValue<string>(), 100) is { Length: > 0 } name ? name : null
                : null;

        var state = Name("States");
        var areas = new JsonObject();
        foreach (var (layer, key) in Layers)
        {
            if (Name(layer) is not { } name)
            {
                continue;
            }

            areas[key] = key switch
            {
                // "Congressional District 10" in Michigan is "Michigan's 10th congressional district", as Wikipedia names it.
                "congressional_district" when state is not null && Number(name) is { } n =>
                    n == 0 ? $"{state}'s at-large congressional district" : $"{state}'s {Ordinal(n)} congressional district",
                // And "Michigan's 11th Senate district", "California's 10th State Assembly district": what
                // Wikipedia's search finds ("Michigan Senate District 11" found the US Senate election).
                "state_senate_district" when state is not null && Number(name) is { } n => $"{state}'s {Ordinal(n)} Senate district",
                "state_house_district" when state is not null && Number(name) is { } n => $"{state}'s {Ordinal(n)} {LowerHouse(state)} district",
                "zip_area" => name.Replace("ZCTA5 ", "", StringComparison.Ordinal),
                _ => name,
            };
        }

        // A city's subdivision is the city again; say it once.
        if (areas["township_or_city"]?.GetValue<string>() == areas["city"]?.GetValue<string>())
        {
            areas.Remove("township_or_city");
        }

        return areas;
    }

    /// <summary>What a state calls its lower house, as its district articles do.</summary>
    private static string LowerHouse(string state) => state switch
    {
        "California" or "New York" or "Wisconsin" => "State Assembly",
        "Nevada" => "Assembly",
        "Maryland" or "Virginia" or "West Virginia" => "House of Delegates",
        _ => "House of Representatives",
    };

    private static int? Number(string name) =>
        name.Split(' ').LastOrDefault() is { } last && int.TryParse(last, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n
        : name.Contains("at Large", StringComparison.OrdinalIgnoreCase) ? 0 : null;

    private static string Ordinal(int n) =>
        n + ((n % 100) is 11 or 12 or 13 ? "th" : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" });
}
