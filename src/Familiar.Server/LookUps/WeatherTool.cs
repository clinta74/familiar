using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Familiar.Agent.Tools;
using Familiar.Domain.Tools;
using Microsoft.Extensions.Caching.Memory;

namespace Familiar.Server.LookUps;

/// <summary>
/// get_weather: the forecast from Open-Meteo (free, no key). A place and a day in, numbers out -
/// nothing in a forecast can carry instructions. Without a place, the user's home from Settings.
/// </summary>
public sealed partial class WeatherTool(IHttpClientFactory http, IMemoryCache cache) : ITool
{
    /// <summary>Open-Meteo's forecasts run this far ahead (today included).</summary>
    public const int ForecastDays = 7;

    /// <summary>Countries that think in Fahrenheit and miles.</summary>
    private static readonly HashSet<string> Imperial = new(StringComparer.OrdinalIgnoreCase) { "US", "LR", "MM" };

    public ToolDefinition Definition { get; } = new(
        "get_weather",
        "The weather now and the forecast for up to a week ahead. Use when the user asks about the weather or what to wear.",
        ToolSchema.Object(
            ("place", ToolSchema.String("A town or city, e.g. \"Leeds\" or \"Portland, Oregon\". Leave out for where the user lives.")),
            ("day", ToolSchema.String("\"today\", \"tomorrow\", a weekday like \"saturday\", or a date (YYYY-MM-DD). Leave out for today."))),
        ToolRisk.LookUp);

    public string Describe(JsonObject arguments) => $"Check the weather for {ToolArgs.String(arguments, "place") ?? "home"}";

    public async Task<ToolResult> InvokeAsync(ToolContext context, JsonObject arguments, CancellationToken ct)
    {
        var asked = ToolArgs.String(arguments, "place")?.Trim();
        var place = string.IsNullOrEmpty(asked) ? context.HomePlace : asked;
        if (string.IsNullOrWhiteSpace(place))
        {
            return ToolResult.Failure(
                "You don't know where the user is. Ask which town; they can also set where they live in Settings, under Your familiar.");
        }

        if (!Place().IsMatch(place))
        {
            return ToolResult.Failure("That doesn't look like a place. Ask the user for a town or city.");
        }

        var found = await GeocodeAsync(place, ct);
        if (found is null)
        {
            return ToolResult.Failure($"Couldn't find a place called \"{place}\". Ask the user to say it another way.", $"Weather: no {place}");
        }

        var imperial = Imperial.Contains(found.CountryCode);
        var forecast = await ForecastAsync(found, imperial, ct);
        var dates = (forecast["daily"]?["time"] as JsonArray)?.Select(d => DateOnly.Parse(d!.GetValue<string>(), CultureInfo.InvariantCulture)).ToList() ?? [];
        if (dates.Count == 0)
        {
            return ToolResult.Failure("The forecast didn't come back. Say the weather isn't available just now.");
        }

        var index = DayIndex(ToolArgs.String(arguments, "day"), dates);
        if (index is null)
        {
            return ToolResult.Failure($"Forecasts go {ForecastDays} days ahead, from today ({dates[0]:yyyy-MM-dd}). Ask which day they mean.");
        }

        var units = imperial ? ("°F", "mph", "in") : ("°C", "km/h", "mm");
        double Daily(string field) => (forecast["daily"]?[field] as JsonArray)?[index.Value]?.GetValue<double>() ?? double.NaN;
        var day = dates[index.Value];
        var facts = new JsonObject
        {
            ["ok"] = true,
            ["place"] = found.Label,
            ["day"] = (index == 0 ? "today, " : index == 1 ? "tomorrow, " : "") + day.ToString("dddd d MMMM", CultureInfo.InvariantCulture),
            ["forecast"] = new JsonObject
            {
                ["conditions"] = Conditions((int)Daily("weather_code")),
                ["high"] = Math.Round(Daily("temperature_2m_max")),
                ["low"] = Math.Round(Daily("temperature_2m_min")),
                ["chance_of_rain_percent"] = Math.Round(Daily("precipitation_probability_max")),
                ["rain"] = Math.Round(Daily("precipitation_sum"), 1),
            },
            ["units"] = new JsonObject { ["temperature"] = units.Item1, ["wind"] = units.Item2, ["rain"] = units.Item3 },
        };

        if (index == 0 && forecast["current"] is JsonObject current)
        {
            facts["now"] = new JsonObject
            {
                ["temperature"] = Math.Round(current["temperature_2m"]?.GetValue<double>() ?? double.NaN),
                ["feels_like"] = Math.Round(current["apparent_temperature"]?.GetValue<double>() ?? double.NaN),
                ["conditions"] = Conditions(current["weather_code"]?.GetValue<int>() ?? -1),
                ["wind"] = Math.Round(current["wind_speed_10m"]?.GetValue<double>() ?? double.NaN),
            };
        }

        var summary = index == 0 && facts["now"] is JsonObject now
            ? $"Weather: {found.Name}, {now["temperature"]}{units.Item1}, {now["conditions"]}"
            : $"Weather: {found.Name}, {day:ddd}, {Math.Round(Daily("temperature_2m_min"))}-{Math.Round(Daily("temperature_2m_max"))}{units.Item1}";
        return ToolResult.Success(LookUpText.Result("Open-Meteo", facts), summary);
    }

    private sealed record Location(string Name, string Label, double Latitude, double Longitude, string CountryCode);

    /// <summary>"Portland, Oregon": search for Portland, prefer the one whose region or country says Oregon.</summary>
    private async Task<Location?> GeocodeAsync(string place, CancellationToken ct) =>
        await cache.GetOrCreateAsync($"geocode:{place.ToLowerInvariant()}", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromDays(1);
            var parts = place.Split(',', 2, StringSplitOptions.TrimEntries);
            using var response = await http.CreateClient(nameof(LookUps)).GetAsync(
                $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(parts[0])}&count=10&language=en&format=json", ct);
            response.EnsureSuccessStatusCode();
            var results = (JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))?["results"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];

            static string Text(JsonObject o, string key) => o[key]?.GetValue<string>() ?? string.Empty;
            var hint = parts.Length > 1 ? parts[1] : null;
            var best = (hint is null ? null : results.FirstOrDefault(r =>
                    Text(r, "admin1").Contains(hint, StringComparison.OrdinalIgnoreCase)
                    || Text(r, "country").Contains(hint, StringComparison.OrdinalIgnoreCase)
                    || Text(r, "country_code").Equals(hint, StringComparison.OrdinalIgnoreCase)))
                ?? results.FirstOrDefault();
            if (best is null)
            {
                return null;
            }

            var name = LookUpText.Clean(Text(best, "name"), 60);
            var label = string.Join(", ", new[] { name, LookUpText.Clean(Text(best, "admin1"), 60), LookUpText.Clean(Text(best, "country"), 60) }
                .Where(s => s.Length > 0).Distinct());
            return new Location(name, label, best["latitude"]!.GetValue<double>(), best["longitude"]!.GetValue<double>(), Text(best, "country_code"));
        });

    private async Task<JsonObject> ForecastAsync(Location at, bool imperial, CancellationToken ct) =>
        (await cache.GetOrCreateAsync(FormattableString.Invariant($"forecast:{at.Latitude:F2},{at.Longitude:F2},{imperial}"), async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(15);
            var units = imperial ? "&temperature_unit=fahrenheit&wind_speed_unit=mph&precipitation_unit=inch" : string.Empty;
            var url = FormattableString.Invariant(
                $"https://api.open-meteo.com/v1/forecast?latitude={at.Latitude}&longitude={at.Longitude}") +
                "&current=temperature_2m,apparent_temperature,weather_code,wind_speed_10m" +
                "&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_probability_max,precipitation_sum" +
                $"&timezone=auto&forecast_days={ForecastDays}{units}";
            using var response = await http.CreateClient(nameof(LookUps)).GetAsync(url, ct);
            response.EnsureSuccessStatusCode();
            return JsonNode.Parse(await response.Content.ReadAsStringAsync(ct)) as JsonObject ?? [];
        }))!;

    /// <summary>Which of the forecast's days is meant; null when it's past the forecast's end.</summary>
    internal static int? DayIndex(string? day, IReadOnlyList<DateOnly> dates)
    {
        var asked = day?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(asked) || asked is "today" or "now" or "tonight")
        {
            return 0;
        }

        if (asked == "tomorrow")
        {
            return dates.Count > 1 ? 1 : null;
        }

        if (DateOnly.TryParseExact(asked, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            var at = dates.ToList().IndexOf(date);
            return at < 0 ? null : at;
        }

        var weekday = Enum.GetValues<DayOfWeek>().Cast<DayOfWeek?>()
            .FirstOrDefault(d => asked.StartsWith(d!.Value.ToString()[..3], StringComparison.OrdinalIgnoreCase));
        if (weekday is { } wanted)
        {
            var at = dates.ToList().FindIndex(d => d.DayOfWeek == wanted);
            return at < 0 ? null : at;
        }

        return asked.Contains("weekend", StringComparison.Ordinal) ? DayIndex("saturday", dates) : 0;
    }

    /// <summary>WMO weather codes, as Open-Meteo reports them, in words.</summary>
    internal static string Conditions(int code) => code switch
    {
        0 => "clear sky",
        1 => "mainly clear",
        2 => "partly cloudy",
        3 => "overcast",
        45 or 48 => "fog",
        51 or 53 or 55 => "drizzle",
        56 or 57 => "freezing drizzle",
        61 => "light rain",
        63 => "rain",
        65 => "heavy rain",
        66 or 67 => "freezing rain",
        71 => "light snow",
        73 => "snow",
        75 => "heavy snow",
        77 => "snow grains",
        80 => "light showers",
        81 => "showers",
        82 => "heavy showers",
        85 or 86 => "snow showers",
        95 => "thunderstorms",
        96 or 99 => "thunderstorms with hail",
        _ => "unknown",
    };

    /// <summary>A place name: letters, digits, spaces and a little punctuation; no addresses or links.</summary>
    [GeneratedRegex(@"^[\p{L}\p{M}0-9][\p{L}\p{M}0-9 ,.'-]{0,79}$")]
    private static partial Regex Place();
}
