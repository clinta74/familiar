using System.Text.Json.Nodes;
using Familiar.Agent.Tools;
using Familiar.Server.Maths;

namespace Familiar.Server.Tests;

public sealed class MathsTests
{
    private static ToolContext Context() => new(Guid.NewGuid(), Guid.NewGuid(), TimeZoneInfo.Utc, DateTimeOffset.UtcNow);

    [Theory]
    [InlineData("2 + 3 * 4", "14")]
    [InlineData("(42.50 + 18) * 1.2 / 3", "24.2")]
    [InlineData("-2^2", "-4")]
    [InlineData("2^3^2", "512")]
    [InlineData("15% of 80", "12")]
    [InlineData("80 * 115%", "92")]
    [InlineData("sqrt(2)", "1.414213562")]
    [InlineData("round(10 / 3, 2)", "3.33")]
    [InlineData("sin(30)", "0.5")]
    [InlineData("5!", "120")]
    [InlineData("17 mod 5", "2")]
    [InlineData("3 x 4", "12")]
    [InlineData("1.5e6 / 3", "500000")]
    [InlineData("0.1 + 0.2", "0.3")]
    [InlineData("max(3, 9, 4) - min(2, 1)", "8")]
    [InlineData("2 × 3 ÷ 4", "1.5")]
    [InlineData("$12.50 * 4", "50")]
    public void Sums_come_out_right(string expression, string expected) =>
        Assert.Equal(expected, Calculator.Show(Calculator.Evaluate(expression)));

    [Theory]
    [InlineData("1 / 0")]
    [InlineData("sqrt(-1)")]
    [InlineData("2 +")]
    [InlineData("(1 + 2")]
    [InlineData("System.IO.File.Delete(\"x\")")]
    [InlineData("1,200 + 3")]
    public void Nonsense_is_an_error_not_an_answer(string expression) =>
        Assert.Throws<FormatException>(() => Calculator.Evaluate(expression));

    [Fact]
    public async Task The_tool_says_what_went_wrong()
    {
        var result = await new CalculateTool().InvokeAsync(Context(), new JsonObject { ["expression"] = "2 + banana" }, default);

        Assert.False(result.Ok);
        Assert.Contains("banana", result.Data["error"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(5, "miles", "km", 8.047, "5 miles is 8.047 kilometres")]
    [InlineData(350, "°F", "°C", 176.7, "350 degrees Fahrenheit is 176.7 degrees Celsius")]
    [InlineData(-40, "C", "fahrenheit", -40, "-40 degrees Celsius is -40 degrees Fahrenheit")]
    [InlineData(2, "cups", "ml", 473.2, "2 US cups is 473.2 millilitres")]
    [InlineData(1, "UK pint", "ml", 568.3, "1 UK pint is 568.3 millilitres")]
    [InlineData(12, "stone", "kg", 76.2, "12 stone is 76.2 kilograms")]
    [InlineData(32, "psi", "bar", 2.206, "32 psi is 2.206 bar")]
    [InlineData(1, "acre", "sq. ft.", 43560, "1 acre is 43560 square feet")]
    [InlineData(1, "kilometre", "mile", 0.6214, "1 kilometre is 0.6214 miles")]
    public async Task Units_convert(double amount, string from, string to, double expected, string says)
    {
        var result = await new ConvertUnitsTool().InvokeAsync(
            Context(), new JsonObject { ["amount"] = amount, ["from"] = from, ["to"] = to }, default);

        Assert.True(result.Ok);
        Assert.Equal(expected, result.Data["result"]!.GetValue<double>());
        Assert.Equal(says, result.Data["says"]!.GetValue<string>());
    }

    [Fact]
    public async Task Units_of_different_kinds_dont_convert()
    {
        var result = await new ConvertUnitsTool().InvokeAsync(
            Context(), new JsonObject { ["amount"] = 3, ["from"] = "kg", ["to"] = "miles" }, default);

        Assert.False(result.Ok);
        Assert.Contains("weight", result.Data["error"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    private static ToolContext OnThe8thOfOctober() =>
        new(Guid.NewGuid(), Guid.NewGuid(), TimeZoneInfo.Utc, new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero));

    private static async Task<JsonObject> DatesAsync(params (string Name, JsonNode Value)[] arguments)
    {
        var result = await new DatesTool().InvokeAsync(OnThe8thOfOctober(), new JsonObject(arguments.Select(a => KeyValuePair.Create(a.Name, (JsonNode?)a.Value))), default);
        Assert.True(result.Ok, result.Data.ToJsonString());
        return result.Data;
    }

    [Theory]
    [InlineData("Christmas", 78)]
    [InlineData("25 December", 78)]
    [InlineData("25th December", 78)]
    [InlineData("2026-12-25", 78)]
    [InlineData("1 January", 85)]
    [InlineData("tomorrow", 1)]
    public async Task Days_until_a_date_are_counted_not_guessed(string date, int days) =>
        Assert.Equal(days, (await DatesAsync(("what", "until"), ("date", date)))["days"]!.GetValue<int>());

    [Fact]
    public async Task Days_since_a_date_without_a_year_count_from_the_last_one()
    {
        var since = await DatesAsync(("what", "since"), ("date", "1 January"));
        Assert.Equal(280, since["days"]!.GetValue<int>());
        Assert.Equal("Thursday 1 January 2026: 280 days ago", since["says"]!.GetValue<string>());
        Assert.Equal(7, (await DatesAsync(("what", "between"), ("from", "2026-10-01"), ("date", "2026-10-08")))["days"]!.GetValue<int>());
    }

    [Theory]
    [InlineData(100, "days", "Saturday 16 January 2027")]
    [InlineData(-2, "weeks", "Thursday 24 September 2026")]
    [InlineData(3, "months", "Friday 8 January 2027")]
    public async Task A_date_some_time_away(int amount, string unit, string expected) =>
        Assert.Equal(expected, (await DatesAsync(("what", "add"), ("amount", amount), ("unit", unit)))["date"]!.GetValue<string>());

    [Fact]
    public async Task Ages_count_whole_years_and_the_next_birthday()
    {
        var sam = await DatesAsync(("what", "age"), ("date", "14 March 1987"));
        Assert.Equal(39, sam["age"]!.GetValue<int>());
        Assert.Contains("in 157 days", sam["next_birthday"]!.GetValue<string>(), StringComparison.Ordinal);

        // A leap-day birthday is the 28th in other years.
        var leap = await DatesAsync(("what", "age"), ("date", "2000-02-29"));
        Assert.Equal(26, leap["age"]!.GetValue<int>());
        Assert.Contains("28 February 2027", leap["next_birthday"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Calculate_turns_dates_away()
    {
        var result = await new CalculateTool().InvokeAsync(Context(), new JsonObject { ["expression"] = "(2026-12-25 - 2026-10-09) / 1" }, default);
        Assert.False(result.Ok);
        Assert.Contains("dates tool", result.Data["error"]!.GetValue<string>(), StringComparison.Ordinal);
    }
}
