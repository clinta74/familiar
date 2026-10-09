using System.Globalization;
using System.Text.Json.Nodes;
using Familiar.Agent.Tools;
using Familiar.Domain.Tools;
using Familiar.Server.Scheduling;

namespace Familiar.Server.Maths;

/// <summary>
/// dates: date arithmetic worked out here (BACKLOG item 26). Asked how many days until Christmas,
/// the model passed the dates to calculate - which read them as subtraction - then counted months
/// itself and said 87 where it was 77. Days between, a date some time from another, someone's age:
/// in the user's time zone, today known.
/// </summary>
public sealed class DatesTool : ITool
{
    private static readonly string[] Whats = ["until", "since", "between", "add", "age"];
    private static readonly string[] Units = ["days", "weeks", "months", "years"];

    /// <summary>Named days, as a month and day: the next one (or the last, for "since").</summary>
    private static readonly Dictionary<string, (int Month, int Day)> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["christmas"] = (12, 25), ["christmas day"] = (12, 25), ["christmas eve"] = (12, 24), ["boxing day"] = (12, 26),
        ["new year"] = (1, 1), ["new year's day"] = (1, 1), ["new years day"] = (1, 1), ["new year's eve"] = (12, 31),
        ["new years eve"] = (12, 31), ["halloween"] = (10, 31), ["valentine's day"] = (2, 14), ["valentines day"] = (2, 14),
    };

    private static readonly string[] DayMonth = ["d MMMM", "d MMM", "MMMM d", "MMM d", "d MMMM yyyy", "d MMM yyyy", "MMMM d yyyy", "MMM d yyyy", "MMMM d, yyyy", "MMM d, yyyy"];

    public ToolDefinition Definition { get; } = new(
        "dates",
        "Works out dates exactly: how many days until a date (until) or since one (since), or between two (between); a date " +
        "some days, weeks, months or years from another (add); or someone's age from their birthday (age). Use it instead " +
        "of counting days in your head, and " +
        "never give dates to calculate. Dates as YYYY-MM-DD, \"today\", \"tomorrow\", a weekday, a day and month (\"25 " +
        "December\": the next one, or with a year), or \"Christmas\".",
        ToolSchema.Object(
            ["what"],
            ("what", ToolSchema.Enum("until: days from today to a date; since: days from a date to today; between: days from one date to another; add: a date plus some time; age: years since a birthday.", Whats)),
            ("date", ToolSchema.String("until, since: the date; between: the date to count to; add: the date to start from (leave out for today); age: the birthday.")),
            ("from", ToolSchema.String("between: the date to count from.")),
            ("amount", ToolSchema.Number("add: how many (negative to go back).")),
            ("unit", ToolSchema.Enum("add: days, weeks, months or years. Leave out for days.", Units))),
        ToolRisk.Read);

    public string Describe(JsonObject arguments) => "Work out dates";

    public Task<ToolResult> InvokeAsync(ToolContext context, JsonObject arguments, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(context.Now, context.Zone).Date);
        try
        {
            var result = (ToolArgs.String(arguments, "what")?.ToLowerInvariant()) switch
            {
                "add" => Add(arguments, context, today),
                "age" => Age(arguments, context, today),
                "since" => Between(arguments, context, today, since: true),
                _ => Between(arguments, context, today, since: false),
            };
            return Task.FromResult(result);
        }
        catch (FormatException exception)
        {
            return Task.FromResult(ToolResult.Failure(exception.Message));
        }
    }

    private static ToolResult Between(JsonObject arguments, ToolContext context, DateOnly today, bool since)
    {
        var text = ToolArgs.String(arguments, "date") ?? throw new FormatException("Say which date.");
        DateOnly from, to;
        if (since)
        {
            // A date without a year is the last one: "since 1 January" is this year's.
            (from, to) = (Parse(text, context, today, forward: false), today);
        }
        else
        {
            // The next one: "until Christmas". Between two dates, the next after the first.
            from = ToolArgs.String(arguments, "from") is { } f ? Parse(f, context, today, forward: false) : today;
            to = Parse(text, context, from, forward: true);
        }
        var days = to.DayNumber - from.DayNumber;
        var say = days switch
        {
            0 => "that's today",
            > 0 when since => $"{Days(days)} ago",
            > 0 => from == today ? $"in {Days(days)}" : $"{Days(days)} after",
            _ => from == today ? $"{Days(-days)} ago" : $"{Days(-days)} before",
        };
        return ToolResult.Success(new JsonObject
        {
            ["ok"] = true,
            ["from"] = Show(from, today),
            ["to"] = Show(to, today),
            ["days"] = days,
            ["weeks"] = $"{Math.Abs(days) / 7} weeks and {Days(Math.Abs(days) % 7)}",
            ["says"] = $"{Show(since ? from : to, today)}: {say}",
        }, $"{Show(since ? from : to, today)}: {say}");
    }

    private static ToolResult Add(JsonObject arguments, ToolContext context, DateOnly today)
    {
        var start = ToolArgs.String(arguments, "date") is { } d ? Parse(d, context, today, forward: true) : today;
        var amount = ToolArgs.Number(arguments, "amount") ?? throw new FormatException("Say how many days, weeks, months or years.");
        if (amount != Math.Floor(amount) || Math.Abs(amount) > 100_000)
        {
            throw new FormatException("Give a whole number of days, weeks, months or years.");
        }

        var n = (int)amount;
        var result = (ToolArgs.String(arguments, "unit")?.ToLowerInvariant()) switch
        {
            "weeks" or "week" => start.AddDays(n * 7),
            "months" or "month" => start.AddMonths(n),
            "years" or "year" => start.AddYears(n),
            _ => start.AddDays(n),
        };
        return ToolResult.Success(new JsonObject
        {
            ["ok"] = true,
            ["from"] = Show(start, today),
            ["date"] = Show(result, today),
            ["days_from_today"] = result.DayNumber - today.DayNumber,
        }, $"Date: {Show(result, today)}");
    }

    private static ToolResult Age(JsonObject arguments, ToolContext context, DateOnly today)
    {
        var text = ToolArgs.String(arguments, "date") ?? throw new FormatException("Say when they were born.");
        var born = Parse(text, context, today, forward: false);
        if (born > today)
        {
            throw new FormatException("That birthday is in the future. Check the year.");
        }

        var years = today.Year - born.Year - (today < Birthday(born, today.Year) ? 1 : 0);
        var next = Birthday(born, today.Year);
        if (next < today)
        {
            next = Birthday(born, today.Year + 1);
        }

        return ToolResult.Success(new JsonObject
        {
            ["ok"] = true,
            ["born"] = born.ToString("d MMMM yyyy", CultureInfo.InvariantCulture),
            ["age"] = years,
            ["next_birthday"] = next == today ? "today" : $"{Show(next, today)}, in {Days(next.DayNumber - today.DayNumber)}",
        }, $"Age: {years}");
    }

    /// <summary>The birthday in [year]: 29 February is the 28th in a year without one.</summary>
    private static DateOnly Birthday(DateOnly born, int year) =>
        new(year, born.Month, Math.Min(born.Day, DateTime.DaysInMonth(year, born.Month)));

    /// <summary>
    /// A date as said: what <see cref="LocalDates"/> knows (today, tomorrow, a weekday, YYYY-MM-DD),
    /// a named day, or a day and month with or without a year. Without a year it's the next one from
    /// [relativeTo] (<paramref name="forward"/>) or the last one before it.
    /// </summary>
    internal static DateOnly Parse(string text, ToolContext context, DateOnly relativeTo, bool forward)
    {
        var said = text.Trim().TrimEnd('.', '?', '!');
        if (LocalDates.Day(said, context) is { } day)
        {
            return DateOnly.FromDateTime(day);
        }

        int month, dayOfMonth;
        int? year = null;
        var bare = said.StartsWith("the ", StringComparison.OrdinalIgnoreCase) ? said[4..] : said;
        if (Named.TryGetValue(bare, out var named))
        {
            (month, dayOfMonth) = named;
        }
        else
        {
            // "25th December" reads as "25 December".
            var plain = System.Text.RegularExpressions.Regex.Replace(bare, @"(\d+)(st|nd|rd|th)\b", "$1", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!DateTime.TryParseExact(plain, DayMonth, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsed))
            {
                throw new FormatException($"\"{text}\" isn't a date it can read. Give it as YYYY-MM-DD.");
            }

            (month, dayOfMonth) = (parsed.Month, parsed.Day);
            if (plain.Any(char.IsDigit) && System.Text.RegularExpressions.Regex.IsMatch(plain, @"\b\d{4}\b"))
            {
                year = parsed.Year;
            }
        }

        if (year is { } y)
        {
            return new DateOnly(y, month, Math.Min(dayOfMonth, DateTime.DaysInMonth(y, month)));
        }

        var candidate = new DateOnly(relativeTo.Year, month, Math.Min(dayOfMonth, DateTime.DaysInMonth(relativeTo.Year, month)));
        if (forward && candidate < relativeTo)
        {
            candidate = new DateOnly(relativeTo.Year + 1, month, Math.Min(dayOfMonth, DateTime.DaysInMonth(relativeTo.Year + 1, month)));
        }
        else if (!forward && candidate > relativeTo)
        {
            candidate = new DateOnly(relativeTo.Year - 1, month, Math.Min(dayOfMonth, DateTime.DaysInMonth(relativeTo.Year - 1, month)));
        }

        return candidate;
    }

    private static string Show(DateOnly date, DateOnly today) =>
        date == today ? "today, " + date.ToString("dddd d MMMM yyyy", CultureInfo.InvariantCulture) : date.ToString("dddd d MMMM yyyy", CultureInfo.InvariantCulture);

    private static string Days(int n) => n == 1 ? "1 day" : $"{n} days";
}
