using System.Globalization;
using System.Text.Json.Nodes;
using Familiar.Agent.Tools;
using Familiar.Domain.Tools;
using Microsoft.Extensions.Caching.Memory;

namespace Familiar.Server.LookUps;

/// <summary>
/// air_quality: Open-Meteo's air-quality forecast - the air quality index, the UV index and, in
/// Europe, pollen - for a place and today or the next few days. Numbers in, words out ("good",
/// "high"); like the forecast, nothing in it can carry instructions.
/// </summary>
public sealed class AirQualityTool(Weather weather, IHttpClientFactory http, IMemoryCache cache) : ITool
{
    /// <summary>How far ahead the air-quality forecast runs (today included).</summary>
    internal const int Days = 4;

    private static readonly string[] Pollens = ["alder", "birch", "grass", "mugwort", "olive", "ragweed"];

    public ToolDefinition Definition { get; } = new(
        "air_quality",
        "The air quality, the UV index and (in Europe) the pollen count, for today or the next few days. Use for questions about " +
        "hay fever, pollen, smog or smoke, sunburn or sun cream, or whether the air is fine for a run.",
        ToolSchema.Object(
            ("place", ToolSchema.String("A town or city. Leave out for where the user lives.")),
            ("day", ToolSchema.String("\"today\", \"tomorrow\", a weekday or a date (YYYY-MM-DD). Leave out for today."))),
        ToolRisk.LookUp);

    public string Describe(JsonObject arguments) => $"Check the air quality for {ToolArgs.String(arguments, "place") ?? "home"}";

    public async Task<ToolResult> InvokeAsync(ToolContext context, JsonObject arguments, CancellationToken ct)
    {
        var asked = ToolArgs.String(arguments, "place")?.Trim();
        var place = string.IsNullOrEmpty(asked) ? context.HomePlace : asked;
        if (string.IsNullOrWhiteSpace(place))
        {
            return ToolResult.Failure(
                "You don't know where the user is. Ask which town; they can also set where they live in Settings, under Your familiar.");
        }

        if (!WeatherTool.IsPlace(place))
        {
            return ToolResult.Failure("That doesn't look like a place. Ask the user for a town or city.");
        }

        var found = await weather.GeocodeAsync(place, ct);
        if (found is null)
        {
            return ToolResult.Failure($"Couldn't find a place called \"{place}\". Ask the user to say it another way.", $"Air: no {place}");
        }

        var forecast = await ForecastAsync(found, ct);
        var times = (forecast["hourly"]?["time"] as JsonArray)?.Select(t => t!.GetValue<string>()).ToList() ?? [];
        var dates = times.Select(t => DateOnly.Parse(t[..10], CultureInfo.InvariantCulture)).Distinct().ToList();
        if (dates.Count == 0)
        {
            return ToolResult.Failure("The air-quality forecast didn't come back. Say it isn't available just now.");
        }

        if (WeatherTool.DayIndex(ToolArgs.String(arguments, "day"), dates) is not { } index)
        {
            return ToolResult.Failure($"The air-quality forecast goes {Days} days ahead, from today ({dates[0]:yyyy-MM-dd}). Ask which day they mean.");
        }

        var day = dates[index];
        var hours = times.Select((t, i) => (t, i)).Where(h => h.t.StartsWith(day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), StringComparison.Ordinal))
            .Select(h => h.i).ToList();
        double? Peak(string field) => hours.Select(i => (forecast["hourly"]?[field] as JsonArray)?[i]?.GetValue<double>()).Max();

        // The US index in the US, the European one elsewhere: the scale people there know.
        var us = string.Equals(found.CountryCode, "US", StringComparison.OrdinalIgnoreCase);
        var (aqiField, scale) = us ? ("us_aqi", "US AQI") : ("european_aqi", "European AQI");

        var facts = new JsonObject
        {
            ["ok"] = true,
            ["place"] = found.Label,
            ["day"] = (index == 0 ? "today, " : index == 1 ? "tomorrow, " : "") + day.ToString("dddd d MMMM", CultureInfo.InvariantCulture),
        };

        if (Peak(aqiField) is { } worst)
        {
            facts["air_quality"] = new JsonObject
            {
                ["worst"] = Math.Round(worst),
                ["level"] = us ? UsLevel(worst) : EuropeanLevel(worst),
                ["scale"] = scale,
            };
            if (index == 0 && forecast["current"]?[aqiField]?.GetValue<double>() is { } now)
            {
                facts["air_quality"]!["now"] = Math.Round(now);
            }
        }

        if (Peak("uv_index") is { } uv)
        {
            facts["uv_index"] = new JsonObject { ["highest"] = Math.Round(uv, 1), ["level"] = UvLevel(uv) };
        }

        var pollen = new JsonObject();
        foreach (var kind in Pollens)
        {
            if (Peak($"{kind}_pollen") is { } count && PollenLevel(kind, count) is { } level)
            {
                pollen[kind] = level;
            }
        }

        var measured = Pollens.Any(kind => Peak($"{kind}_pollen") is not null);
        facts["pollen"] = !measured ? "No pollen forecast for this area (it covers Europe only)."
            : pollen.Count == 0 ? "None to speak of."
            : pollen;

        var summary = facts["air_quality"] is JsonObject air ? $"Air: {found.Name}, {air["level"]}" : $"Air: {found.Name}";
        return ToolResult.Success(LookUpText.Result("Open-Meteo (air quality)", facts), summary);
    }

    private async Task<JsonObject> ForecastAsync(Weather.Location place, CancellationToken ct) =>
        (await cache.GetOrCreateAsync(FormattableString.Invariant($"air:{place.Latitude:F2},{place.Longitude:F2}"), async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1);
            var pollen = string.Join(",", Pollens.Select(p => $"{p}_pollen"));
            var url = FormattableString.Invariant(
                $"https://air-quality-api.open-meteo.com/v1/air-quality?latitude={place.Latitude:F4}&longitude={place.Longitude:F4}") +
                $"&current=us_aqi,european_aqi&hourly=us_aqi,european_aqi,uv_index,{pollen}&forecast_days={Days}&timezone=auto";
            using var response = await http.CreateClient(nameof(LookUps)).GetAsync(url, ct);
            response.EnsureSuccessStatusCode();
            return JsonNode.Parse(await response.Content.ReadAsStringAsync(ct)) as JsonObject ?? [];
        }))!;

    /// <summary>The US EPA's bands.</summary>
    internal static string UsLevel(double aqi) => aqi switch
    {
        <= 50 => "good",
        <= 100 => "moderate",
        <= 150 => "unhealthy for sensitive groups",
        <= 200 => "unhealthy",
        <= 300 => "very unhealthy",
        _ => "hazardous",
    };

    /// <summary>The European Environment Agency's bands.</summary>
    internal static string EuropeanLevel(double aqi) => aqi switch
    {
        <= 20 => "good",
        <= 40 => "fair",
        <= 60 => "moderate",
        <= 80 => "poor",
        <= 100 => "very poor",
        _ => "extremely poor",
    };

    /// <summary>The WHO's bands.</summary>
    internal static string UvLevel(double uv) => uv switch
    {
        < 3 => "low",
        < 6 => "moderate",
        < 8 => "high",
        < 11 => "very high",
        _ => "extreme",
    };

    /// <summary>
    /// Grains per cubic metre as a level, roughly as pollen forecasts give them (the Met Office's for
    /// grass and birch; ragweed sets people off at far fewer). Null for none at all.
    /// </summary>
    internal static string? PollenLevel(string kind, double count)
    {
        if (count < 1)
        {
            return null;
        }

        var (moderate, high, veryHigh) = kind switch
        {
            "grass" => (30, 50, 150),
            "birch" => (40, 80, 200),
            "ragweed" => (5, 20, 50),
            _ => (10, 50, 200),
        };
        return count < moderate ? "low" : count < high ? "moderate" : count < veryHigh ? "high" : "very high";
    }
}
