using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Familiar.Agent.Tools;
using Familiar.Domain.Tools;
using Microsoft.Extensions.Caching.Memory;

namespace Familiar.Server.LookUps;

/// <summary>
/// public_holidays: a country's public holidays, from Nager.Date (free, no key) - one request per
/// country and year, kept for the day, so it's barely ever asked. Wikidata can't stand in: it
/// describes most holidays by rule ("the first Monday in May"), not by date. Without a country, the
/// one where the user lives (from Settings, found through the weather's place search).
/// </summary>
public sealed partial class HolidaysTool(IHttpClientFactory http, IMemoryCache cache, Weather weather) : ITool
{
    /// <summary>How many coming holidays are listed when no day is asked about.</summary>
    private const int Upcoming = 5;

    public ToolDefinition Definition { get; } = new(
        "public_holidays",
        "A country's public holidays: whether a day is one, or which are coming up. Use for bank holidays, days off, long weekends.",
        ToolSchema.Object(
            ("country", ToolSchema.String("A country name or two-letter code, e.g. \"Canada\" or \"GB\". Leave out for where the user lives.")),
            ("day", ToolSchema.String("A day to check: \"today\", \"tomorrow\", a weekday, or a date (YYYY-MM-DD). Leave out to list the next few."))),
        ToolRisk.LookUp);

    public string Describe(JsonObject arguments) => $"Check public holidays in {ToolArgs.String(arguments, "country") ?? "their country"}";

    public async Task<ToolResult> InvokeAsync(ToolContext context, JsonObject arguments, CancellationToken ct)
    {
        var country = await CountryAsync(ToolArgs.String(arguments, "country")?.Trim(), context, ct);
        if (country is null)
        {
            return ToolResult.Failure("You don't know which country. Ask the user; they can also set where they live in Settings, under Your familiar.");
        }

        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(context.Now, context.Zone).DateTime);
        var asked = ToolArgs.String(arguments, "day")?.Trim();
        var holidays = new List<Holiday>();
        foreach (var year in new[] { today.Year, today.Year + 1 })
        {
            holidays.AddRange(await YearAsync(country.Value.Code, year, ct));
        }

        // Where they live, if it's in this country: then holidays only some regions have are kept to
        // theirs ("Saint Andrew's Day" is Scotland's, not Leeds').
        var region = await HomeRegionAsync(country.Value.Code, context, ct);
        bool Applies(Holiday h) => h.Nationwide || (region is not null && h.Regions.Contains(region));
        if (region is not null)
        {
            holidays = [.. holidays.Where(Applies)];
        }

        var facts = new JsonObject { ["ok"] = true, ["country"] = country.Value.Name };
        if (region is not null)
        {
            facts["their_region"] = Regions[region];
        }

        if (!string.IsNullOrEmpty(asked))
        {
            if (Day(asked, today) is not { } day)
            {
                return ToolResult.Failure("Give the day as today, tomorrow, a weekday or a date (YYYY-MM-DD).");
            }

            var on = holidays.Where(h => h.Date == day).ToList();
            facts["day"] = day.ToString("dddd d MMMM yyyy", CultureInfo.InvariantCulture);
            facts["is_a_public_holiday"] = on.Any(Applies);
            if (on.Count > 0)
            {
                facts["holidays"] = List(on);
            }
        }
        else
        {
            facts["coming_up"] = List(holidays.Where(h => h.Date >= today).Take(Upcoming));
        }

        return ToolResult.Success(LookUpText.Result("Nager.Date", facts), $"Holidays: {country.Value.Name}");
    }

    /// <summary>A holiday; a regional one says where (ISO 3166-2 codes: "GB-SCT", "US-MI").</summary>
    private sealed record Holiday(DateOnly Date, string Name, bool Nationwide, IReadOnlyList<string> Regions);

    private static JsonArray List(IEnumerable<Holiday> holidays) =>
        new([.. holidays.Select(h => (JsonNode?)new JsonObject
        {
            ["date"] = h.Date.ToString("dddd d MMMM yyyy", CultureInfo.InvariantCulture),
            ["name"] = h.Name,
            ["where"] = h.Nationwide ? "nationwide" : "only in " + string.Join(", ", h.Regions.Select(r => Regions.GetValueOrDefault(r, r))),
        })]);

    /// <summary>
    /// Regions by their ISO 3166-2 codes, as Nager.Date gives them, for the countries most likely here;
    /// matched against the region the place search gives for where the user lives.
    /// </summary>
    private static readonly Dictionary<string, string> Regions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["GB-ENG"] = "England", ["GB-SCT"] = "Scotland", ["GB-WLS"] = "Wales", ["GB-NIR"] = "Northern Ireland",
        ["CA-AB"] = "Alberta", ["CA-BC"] = "British Columbia", ["CA-MB"] = "Manitoba", ["CA-NB"] = "New Brunswick",
        ["CA-NL"] = "Newfoundland and Labrador", ["CA-NS"] = "Nova Scotia", ["CA-NT"] = "Northwest Territories",
        ["CA-NU"] = "Nunavut", ["CA-ON"] = "Ontario", ["CA-PE"] = "Prince Edward Island", ["CA-QC"] = "Quebec",
        ["CA-SK"] = "Saskatchewan", ["CA-YT"] = "Yukon",
        ["AU-ACT"] = "Australian Capital Territory", ["AU-NSW"] = "New South Wales", ["AU-NT"] = "Northern Territory",
        ["AU-QLD"] = "Queensland", ["AU-SA"] = "South Australia", ["AU-TAS"] = "Tasmania", ["AU-VIC"] = "Victoria",
        ["AU-WA"] = "Western Australia",
        ["US-AL"] = "Alabama", ["US-AK"] = "Alaska", ["US-AZ"] = "Arizona", ["US-AR"] = "Arkansas", ["US-CA"] = "California",
        ["US-CO"] = "Colorado", ["US-CT"] = "Connecticut", ["US-DE"] = "Delaware", ["US-DC"] = "District of Columbia",
        ["US-FL"] = "Florida", ["US-GA"] = "Georgia", ["US-HI"] = "Hawaii", ["US-ID"] = "Idaho", ["US-IL"] = "Illinois",
        ["US-IN"] = "Indiana", ["US-IA"] = "Iowa", ["US-KS"] = "Kansas", ["US-KY"] = "Kentucky", ["US-LA"] = "Louisiana",
        ["US-ME"] = "Maine", ["US-MD"] = "Maryland", ["US-MA"] = "Massachusetts", ["US-MI"] = "Michigan", ["US-MN"] = "Minnesota",
        ["US-MS"] = "Mississippi", ["US-MO"] = "Missouri", ["US-MT"] = "Montana", ["US-NE"] = "Nebraska", ["US-NV"] = "Nevada",
        ["US-NH"] = "New Hampshire", ["US-NJ"] = "New Jersey", ["US-NM"] = "New Mexico", ["US-NY"] = "New York",
        ["US-NC"] = "North Carolina", ["US-ND"] = "North Dakota", ["US-OH"] = "Ohio", ["US-OK"] = "Oklahoma", ["US-OR"] = "Oregon",
        ["US-PA"] = "Pennsylvania", ["US-RI"] = "Rhode Island", ["US-SC"] = "South Carolina", ["US-SD"] = "South Dakota",
        ["US-TN"] = "Tennessee", ["US-TX"] = "Texas", ["US-UT"] = "Utah", ["US-VT"] = "Vermont", ["US-VA"] = "Virginia",
        ["US-WA"] = "Washington", ["US-WV"] = "West Virginia", ["US-WI"] = "Wisconsin", ["US-WY"] = "Wyoming",
    };

    /// <summary>The region code of where the user lives, if that's in [country] and a region we know.</summary>
    private async Task<string?> HomeRegionAsync(string country, ToolContext context, CancellationToken ct)
    {
        if (await weather.HomeAsync(context, ct) is not { } home || !home.CountryCode.Equals(country, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return Regions.FirstOrDefault(r => r.Key.StartsWith(country + "-", StringComparison.OrdinalIgnoreCase)
            && r.Value.Equals(home.Region, StringComparison.OrdinalIgnoreCase)).Key;
    }

    private async Task<IReadOnlyList<Holiday>> YearAsync(string code, int year, CancellationToken ct) =>
        (await cache.GetOrCreateAsync(FormattableString.Invariant($"holidays:{code}:{year}"), async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromDays(1);
            using var response = await http.CreateClient(nameof(LookUps)).GetAsync(
                FormattableString.Invariant($"https://date.nager.at/api/v3/PublicHolidays/{year}/{code}"), ct);
            if (!response.IsSuccessStatusCode)
            {
                return (IReadOnlyList<Holiday>)[];
            }

            var list = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct)) as JsonArray ?? [];
            return [.. list.OfType<JsonObject>()
                .Select(h => DateOnly.TryParseExact(h["date"]?.GetValue<string>(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                    ? new Holiday(
                        date,
                        LookUpText.Clean(h["name"]?.GetValue<string>(), 80),
                        h["global"]?.GetValue<bool>() ?? true,
                        (h["counties"] as JsonArray)?.Select(c => c?.GetValue<string>()).OfType<string>().ToList() ?? [])
                    : null)
                .OfType<Holiday>()
                .OrderBy(h => h.Date)];
        }))!;

    /// <summary>The country asked about, by code or name; or the one where the user lives.</summary>
    private async Task<(string Code, string Name)?> CountryAsync(string? asked, ToolContext context, CancellationToken ct)
    {
        var countries = await CountriesAsync(ct);
        if (string.IsNullOrEmpty(asked))
        {
            if (await weather.HomeAsync(context, ct) is not { } home)
            {
                return null;
            }

            asked = home.CountryCode;
        }

        if (!Country().IsMatch(asked))
        {
            return null;
        }

        (string Code, string Name)? Find(Func<(string Code, string Name), bool> test)
        {
            foreach (var country in countries)
            {
                if (test(country))
                {
                    return country;
                }
            }

            return null;
        }

        // Common names Nager.Date spells differently.
        var code = asked.ToLowerInvariant() switch
        {
            "uk" or "england" or "scotland" or "wales" or "northern ireland" or "britain" or "great britain" => "GB",
            "usa" or "america" or "the us" or "united states of america" => "US",
            _ => asked,
        };
        var name = asked;
        return Find(c => c.Code.Equals(code, StringComparison.OrdinalIgnoreCase))
            ?? Find(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? (name.Length > 3 ? Find(c => c.Name.Contains(name, StringComparison.OrdinalIgnoreCase)) : null);
    }

    private async Task<IReadOnlyList<(string Code, string Name)>> CountriesAsync(CancellationToken ct) =>
        (await cache.GetOrCreateAsync("holidays:countries", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromDays(7);
            using var response = await http.CreateClient(nameof(LookUps)).GetAsync("https://date.nager.at/api/v3/AvailableCountries", ct);
            response.EnsureSuccessStatusCode();
            var list = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct)) as JsonArray ?? [];
            return (IReadOnlyList<(string, string)>)[.. list.OfType<JsonObject>()
                .Select(c => (c["countryCode"]?.GetValue<string>() ?? "", LookUpText.Clean(c["name"]?.GetValue<string>(), 60)))
                .Where(c => c.Item1.Length == 2)];
        }))!;

    /// <summary>"today", "tomorrow", a weekday (the next one, today included) or a date.</summary>
    internal static DateOnly? Day(string asked, DateOnly today)
    {
        var text = asked.ToLowerInvariant();
        if (text == "today")
        {
            return today;
        }

        if (text == "tomorrow")
        {
            return today.AddDays(1);
        }

        if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return date;
        }

        foreach (var weekday in Enum.GetValues<DayOfWeek>())
        {
            if (text.StartsWith(weekday.ToString()[..3], StringComparison.OrdinalIgnoreCase))
            {
                return today.AddDays(((int)weekday - (int)today.DayOfWeek + 7) % 7);
            }
        }

        return null;
    }

    [GeneratedRegex(@"^[\p{L} .'-]{2,60}$")]
    private static partial Regex Country();
}
