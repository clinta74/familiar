using System.Text.Json.Nodes;
using Microsoft.Extensions.Caching.Memory;

namespace Familiar.Server.LookUps;

/// <summary>
/// Open-Meteo, for the weather tool and the greeting's notes: a place found by name, and its forecast,
/// both cached (a place for a day, a forecast for a quarter of an hour).
/// </summary>
public sealed class Weather(IHttpClientFactory http, IMemoryCache cache, ILogger<Weather> logger)
{
    /// <summary>Open-Meteo's forecasts run this far ahead (today included).</summary>
    public const int ForecastDays = 7;

    /// <summary>The most the greeting waits for a forecast: it's a nicety, never a delay.</summary>
    internal static readonly TimeSpan NoteTimeout = TimeSpan.FromSeconds(3);

    /// <summary>Countries that think in Fahrenheit and miles.</summary>
    private static readonly HashSet<string> Imperial = new(StringComparer.OrdinalIgnoreCase) { "US", "LR", "MM" };

    public static bool IsImperial(string countryCode) => Imperial.Contains(countryCode);

    /// <summary>
    /// Today's weather at [place] as a note for the familiar ("Weather where they live today: light
    /// rain, 9-14°C, 80% chance of rain; 11°C now."), not naming the place: the user's home stays out
    /// of the prompt. Null if it can't be had quickly.
    /// </summary>
    public async Task<string?> TodayNoteAsync(string place, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(NoteTimeout);
        try
        {
            if (await GeocodeAsync(place, timeout.Token) is not { } found)
            {
                return null;
            }

            var unit = IsImperial(found.CountryCode) ? "°F" : "°C";
            var forecast = await ForecastAsync(found, IsImperial(found.CountryCode), timeout.Token);
            double Today(string field) => (forecast["daily"]?[field] as JsonArray)?[0]?.GetValue<double>() ?? double.NaN;
            var low = Math.Round(Today("temperature_2m_min"));
            var high = Math.Round(Today("temperature_2m_max"));
            if (double.IsNaN(low) || double.IsNaN(high))
            {
                return null;
            }

            var rain = Today("precipitation_probability_max");
            var now = forecast["current"]?["temperature_2m"]?.GetValue<double>();
            var note = FormattableString.Invariant($"Weather where they live today: {Conditions((int)Today("weather_code"))}, {low}-{high}{unit}");
            if (!double.IsNaN(rain))
            {
                note += FormattableString.Invariant($", {Math.Round(rain)}% chance of rain");
            }

            return note + (now is { } t ? FormattableString.Invariant($"; {Math.Round(t)}{unit} now.") : ".");
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException
            && !ct.IsCancellationRequested)
        {
            logger.WeatherNoteFailed(exception.Message);
            return null;
        }
    }

    public sealed record Location(string Name, string Label, double Latitude, double Longitude, string CountryCode);

    /// <summary>"Portland, Oregon": search for Portland, prefer the one whose region or country says Oregon.</summary>
    public async Task<Location?> GeocodeAsync(string place, CancellationToken ct) =>
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

    public async Task<JsonObject> ForecastAsync(Location at, bool imperial, CancellationToken ct) =>
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

    /// <summary>WMO weather codes, as Open-Meteo reports them, in words.</summary>
    public static string Conditions(int code) => code switch
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
}
