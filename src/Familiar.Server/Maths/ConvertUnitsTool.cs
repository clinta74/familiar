using System.Globalization;
using System.Text.Json.Nodes;
using Familiar.Agent.Tools;
using Familiar.Domain.Tools;

namespace Familiar.Server.Maths;

/// <summary>
/// convert_units: lengths, weights, volumes (cooking ones too), temperatures, speeds, areas,
/// durations, energy and pressure, worked out here from exact factors. Nothing leaves the house.
/// </summary>
public sealed class ConvertUnitsTool : ITool
{
    public ToolDefinition Definition { get; } = new(
        "convert_units",
        "Converts an amount between units: length, weight, volume (cups, tablespoons, pints), temperature, speed, area, time, " +
        "energy (calories) and pressure (psi, bar). Use instead of converting in your head. Cups, pints and gallons are US ones " +
        "unless you say \"UK pint\" or \"imperial gallon\".",
        ToolSchema.Object(
            ["amount", "from", "to"],
            ("amount", ToolSchema.Number("How much, e.g. 5.")),
            ("from", ToolSchema.String("The unit it's in, e.g. \"miles\", \"°F\", \"cups\".")),
            ("to", ToolSchema.String("The unit wanted, e.g. \"km\", \"°C\", \"ml\"."))),
        ToolRisk.Read);

    public string Describe(JsonObject arguments) =>
        $"Convert {ToolArgs.String(arguments, "amount")} {ToolArgs.String(arguments, "from")} to {ToolArgs.String(arguments, "to")}";

    public Task<ToolResult> InvokeAsync(ToolContext context, JsonObject arguments, CancellationToken ct)
    {
        var amount = ToolArgs.Number(arguments, "amount");
        var from = Units.Find(ToolArgs.String(arguments, "from"));
        var to = Units.Find(ToolArgs.String(arguments, "to"));
        if (amount is not { } value || !double.IsFinite(value))
        {
            return Task.FromResult(ToolResult.Failure("Give the amount as a number."));
        }

        if (from is null || to is null)
        {
            var which = from is null ? ToolArgs.String(arguments, "from") : ToolArgs.String(arguments, "to");
            return Task.FromResult(ToolResult.Failure($"\"{which}\" isn't a unit it knows. Try a common name or symbol, like \"kg\" or \"pounds\"."));
        }

        if (from.Kind != to.Kind)
        {
            return Task.FromResult(ToolResult.Failure($"{from.Plural} are {from.Kind} and {to.Plural} are {to.Kind}: one can't be turned into the other."));
        }

        var result = Units.Convert(value, from, to);
        var shown = Units.Round(result);
        var says = string.Create(CultureInfo.InvariantCulture, $"{value} {(value == 1 ? from.Name : from.Plural)} is {shown} {(shown == 1 ? to.Name : to.Plural)}");
        return Task.FromResult(ToolResult.Success(
            new JsonObject { ["ok"] = true, ["result"] = shown, ["unit"] = to.Plural, ["says"] = says },
            says));
    }
}

/// <summary>A unit: its kind, and how many of the kind's base unit one of it is (or a formula, for temperature).</summary>
public sealed record Unit(string Name, string Plural, string Kind, double Factor, double Offset = 0);

public static class Units
{
    private static readonly Dictionary<string, Unit> ByName = new(StringComparer.OrdinalIgnoreCase);

    static Units()
    {
        // Length, in metres.
        Add("millimetre", "millimetres", "length", 0.001, "mm", "millimeter", "millimeters");
        Add("centimetre", "centimetres", "length", 0.01, "cm", "centimeter", "centimeters");
        Add("metre", "metres", "length", 1, "m", "meter", "meters");
        Add("kilometre", "kilometres", "length", 1000, "km", "kilometer", "kilometers");
        Add("inch", "inches", "length", 0.0254, "in", "\"");
        Add("foot", "feet", "length", 0.3048, "ft", "'");
        Add("yard", "yards", "length", 0.9144, "yd");
        Add("mile", "miles", "length", 1609.344, "mi");
        Add("nautical mile", "nautical miles", "length", 1852, "nmi");

        // Weight, in kilograms.
        Add("milligram", "milligrams", "weight", 1e-6, "mg");
        Add("gram", "grams", "weight", 0.001, "g", "gramme", "grammes");
        Add("kilogram", "kilograms", "weight", 1, "kg", "kilo", "kilos");
        Add("tonne", "tonnes", "weight", 1000, "t", "metric ton", "metric tons");
        Add("ounce", "ounces", "weight", 0.028349523125, "oz");
        Add("pound", "pounds", "weight", 0.45359237, "lb", "lbs");
        Add("stone", "stone", "weight", 6.35029318, "st", "stones");
        Add("US ton", "US tons", "weight", 907.18474, "ton", "tons", "short ton", "short tons");

        // Volume, in litres. US kitchen measures unless it says UK or imperial.
        Add("millilitre", "millilitres", "volume", 0.001, "ml", "milliliter", "milliliters", "cc");
        Add("centilitre", "centilitres", "volume", 0.01, "cl", "centiliter", "centiliters");
        Add("decilitre", "decilitres", "volume", 0.1, "dl", "deciliter", "deciliters");
        Add("litre", "litres", "volume", 1, "l", "liter", "liters");
        Add("cubic metre", "cubic metres", "volume", 1000, "m3", "m³", "cubic meter", "cubic meters");
        Add("teaspoon", "teaspoons", "volume", 0.00492892159375, "tsp");
        Add("tablespoon", "tablespoons", "volume", 0.01478676478125, "tbsp", "tbs");
        Add("US fluid ounce", "US fluid ounces", "volume", 0.0295735295625, "fl oz", "floz", "fluid ounce", "fluid ounces");
        Add("US cup", "US cups", "volume", 0.2365882365, "cup", "cups");
        Add("US pint", "US pints", "volume", 0.473176473, "pint", "pints", "pt");
        Add("US quart", "US quarts", "volume", 0.946352946, "quart", "quarts", "qt");
        Add("US gallon", "US gallons", "volume", 3.785411784, "gallon", "gallons", "gal");
        Add("UK fluid ounce", "UK fluid ounces", "volume", 0.0284130625, "imperial fluid ounce", "imperial fluid ounces");
        Add("UK pint", "UK pints", "volume", 0.56826125, "imperial pint", "imperial pints");
        Add("UK gallon", "UK gallons", "volume", 4.54609, "imperial gallon", "imperial gallons");

        // Temperature: to kelvin, K = value * factor + offset.
        Add("degree Celsius", "degrees Celsius", "temperature", 1, 273.15, "c", "°c", "celsius", "centigrade");
        Add("degree Fahrenheit", "degrees Fahrenheit", "temperature", 5.0 / 9, 459.67 * 5 / 9, "f", "°f", "fahrenheit");
        Add("kelvin", "kelvin", "temperature", 1, 0, "k");

        // Speed, in metres a second.
        Add("metre per second", "metres per second", "speed", 1, "m/s", "meters per second");
        Add("kilometre per hour", "kilometres per hour", "speed", 1 / 3.6, "km/h", "kph", "kmh", "kilometers per hour");
        Add("mile per hour", "miles per hour", "speed", 0.44704, "mph");
        Add("knot", "knots", "speed", 1852 / 3600.0, "kn", "kt");
        Add("foot per second", "feet per second", "speed", 0.3048, "ft/s", "fps");

        // Area, in square metres.
        Add("square centimetre", "square centimetres", "area", 1e-4, "cm2", "cm²", "sq cm");
        Add("square metre", "square metres", "area", 1, "m2", "m²", "sq m", "square meter", "square meters");
        Add("square kilometre", "square kilometres", "area", 1e6, "km2", "km²", "sq km", "square kilometer", "square kilometers");
        Add("hectare", "hectares", "area", 1e4, "ha");
        Add("acre", "acres", "area", 4046.8564224, "ac");
        Add("square inch", "square inches", "area", 0.00064516, "sq in", "in2", "in²");
        Add("square foot", "square feet", "area", 0.09290304, "sq ft", "ft2", "ft²");
        Add("square yard", "square yards", "area", 0.83612736, "sq yd", "yd2", "yd²");
        Add("square mile", "square miles", "area", 2589988.110336, "sq mi", "mi2", "mi²");

        // Time, in seconds.
        Add("second", "seconds", "time", 1, "s", "sec", "secs");
        Add("minute", "minutes", "time", 60, "min", "mins");
        Add("hour", "hours", "time", 3600, "h", "hr", "hrs");
        Add("day", "days", "time", 86400, "d");
        Add("week", "weeks", "time", 604800, "wk", "wks");
        Add("year", "years", "time", 31557600, "yr", "yrs");

        // Energy, in joules. "Calories" on food are kilocalories.
        Add("joule", "joules", "energy", 1, "j");
        Add("kilojoule", "kilojoules", "energy", 1000, "kj");
        Add("calorie (food)", "calories (food)", "energy", 4184, "kcal", "calorie", "calories", "cal", "kilocalorie", "kilocalories");
        Add("kilowatt-hour", "kilowatt-hours", "energy", 3.6e6, "kwh", "kilowatt hour", "kilowatt hours");

        // Pressure, in pascals.
        Add("pascal", "pascals", "pressure", 1, "pa");
        Add("kilopascal", "kilopascals", "pressure", 1000, "kpa");
        Add("bar", "bar", "pressure", 1e5, "bars");
        Add("millibar", "millibars", "pressure", 100, "mbar", "mb", "hpa", "hectopascal", "hectopascals");
        Add("psi", "psi", "pressure", 6894.757293168, "pounds per square inch");
        Add("atmosphere", "atmospheres", "pressure", 101325, "atm");
    }

    private static void Add(string name, string plural, string kind, double factor, params string[] aliases) =>
        Add(name, plural, kind, factor, 0, aliases);

    private static void Add(string name, string plural, string kind, double factor, double offset, params string[] aliases)
    {
        var unit = new Unit(name, plural, kind, factor, offset);
        foreach (var alias in aliases.Prepend(name).Prepend(plural))
        {
            ByName[alias] = unit;
        }
    }

    /// <summary>A unit by any of its names or symbols; "degrees F", "sq. ft." and "Kilometres" too.</summary>
    public static Unit? Find(string? asked)
    {
        var text = asked?.Trim().TrimEnd('.').Replace(". ", " ", StringComparison.Ordinal);
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        if (ByName.TryGetValue(text, out var unit))
        {
            return unit;
        }

        foreach (var prefix in new[] { "degrees ", "degree ", "deg " })
        {
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && ByName.TryGetValue(text[prefix.Length..], out var degrees))
            {
                return degrees;
            }
        }

        return null;
    }

    public static double Convert(double value, Unit from, Unit to) => ((value * from.Factor) + from.Offset - to.Offset) / to.Factor;

    /// <summary>Four significant figures, as anyone would say it: 8.047 km, 12,346 m, 0.0254 m.</summary>
    public static double Round(double value)
    {
        if (value == 0 || !double.IsFinite(value))
        {
            return value;
        }

        var digits = (int)Math.Clamp(3 - Math.Floor(Math.Log10(Math.Abs(value))), 0, 10);
        return Math.Round(value, digits, MidpointRounding.AwayFromZero);
    }
}
