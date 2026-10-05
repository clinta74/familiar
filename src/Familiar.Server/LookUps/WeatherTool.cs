using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Familiar.Agent.Tools;
using Familiar.Domain.Tools;

namespace Familiar.Server.LookUps;

/// <summary>
/// get_weather: the forecast from Open-Meteo (free, no key). A place and a day in, numbers out -
/// nothing in a forecast can carry instructions. Without a place, the user's home from Settings.
/// </summary>
public sealed partial class WeatherTool(Weather weather) : ITool
{
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

        var found = await weather.GeocodeAsync(place, ct);
        if (found is null)
        {
            return ToolResult.Failure($"Couldn't find a place called \"{place}\". Ask the user to say it another way.", $"Weather: no {place}");
        }

        var imperial = Weather.IsImperial(found.CountryCode);
        var forecast = await weather.ForecastAsync(found, imperial, ct);
        var dates = (forecast["daily"]?["time"] as JsonArray)?.Select(d => DateOnly.Parse(d!.GetValue<string>(), CultureInfo.InvariantCulture)).ToList() ?? [];
        if (dates.Count == 0)
        {
            return ToolResult.Failure("The forecast didn't come back. Say the weather isn't available just now.");
        }

        var index = DayIndex(ToolArgs.String(arguments, "day"), dates);
        if (index is null)
        {
            return ToolResult.Failure($"Forecasts go {Weather.ForecastDays} days ahead, from today ({dates[0]:yyyy-MM-dd}). Ask which day they mean.");
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
                ["conditions"] = Weather.Conditions((int)Daily("weather_code")),
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
                ["conditions"] = Weather.Conditions(current["weather_code"]?.GetValue<int>() ?? -1),
                ["wind"] = Math.Round(current["wind_speed_10m"]?.GetValue<double>() ?? double.NaN),
            };
        }

        var summary = index == 0 && facts["now"] is JsonObject now
            ? $"Weather: {found.Name}, {now["temperature"]}{units.Item1}, {now["conditions"]}"
            : $"Weather: {found.Name}, {day:ddd}, {Math.Round(Daily("temperature_2m_min"))}-{Math.Round(Daily("temperature_2m_max"))}{units.Item1}";
        return ToolResult.Success(LookUpText.Result("Open-Meteo", facts), summary);
    }

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

    /// <summary>A place name: letters, digits, spaces and a little punctuation; no addresses or links.</summary>
    [GeneratedRegex(@"^[\p{L}\p{M}0-9][\p{L}\p{M}0-9 ,.'-]{0,79}$")]
    private static partial Regex Place();
}
