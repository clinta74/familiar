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
}
