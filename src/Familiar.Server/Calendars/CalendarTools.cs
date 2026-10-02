using System.Text.Json.Nodes;
using Familiar.Agent.Tools;
using Familiar.Domain.Calendars;
using Familiar.Domain.Tools;
using Familiar.Server.Scheduling;

namespace Familiar.Server.Calendars;

/// <summary>list_events: what's on the user's calendars - all of them, merged.</summary>
public sealed class ListEventsTool(CalendarStore store) : ITool
{
    public ToolDefinition Definition { get; } = new(
        "list_events",
        "Look at the user's calendar. Without a date, shows what's coming up today and tomorrow.",
        ToolSchema.Object(
            ("date", ToolSchema.String("The first day to look at: \"today\", \"tomorrow\", a weekday like \"Tuesday\", or \"YYYY-MM-DD\". Optional.")),
            ("days", ToolSchema.Integer("How many days to look at, 1 to 14. Optional."))),
        ToolRisk.Read);

    public async Task<ToolResult> InvokeAsync(ToolContext context, JsonObject arguments, CancellationToken ct)
    {
        var today = TimeZoneInfo.ConvertTime(context.Now, context.Zone).Date;
        var days = (int)Math.Clamp(ToolArgs.Number(arguments, "days") ?? 0, 0, 14);

        // From now (or the start of the day asked about) to the end of the last day wanted.
        DateTime firstDay;
        DateTimeOffset from;
        if (ToolArgs.String(arguments, "date") is { } text && LocalDates.Day(text, context) is { } date)
        {
            firstDay = date;
            from = Spoken.StartOfDay(date, context.Zone);
            days = days == 0 ? 1 : days;
        }
        else
        {
            firstDay = today;
            from = context.Now;
            days = days == 0 ? 2 : days;
        }

        var to = Spoken.StartOfDay(firstDay.AddDays(days), context.Zone);

        var events = await store.BetweenAsync(context.AccountId, from, to, ct);
        var list = new JsonArray([.. events.Take(30).Select(e => (JsonNode)Describe(e, context))]);

        return ToolResult.Success(
            new JsonObject { ["ok"] = true, ["events"] = list, ["note"] = events.Count == 0 ? "Nothing on the calendar then." : null },
            events.Count switch
            {
                0 => "Calendar: nothing",
                1 => "Calendar: 1 event",
                var n => $"Calendar: {n} events",
            });
    }

    internal static JsonObject Describe(CalendarEvent e, ToolContext context)
    {
        var json = new JsonObject { ["title"] = e.Title, ["when"] = Spoken.EventWhen(e, context.Zone, context.Now) };
        if (e.Location is { } location)
        {
            json["where"] = location;
        }

        return json;
    }
}

/// <summary>create_event: puts something on Familiar's own calendar.</summary>
public sealed class CreateEventTool(CalendarStore store) : ITool
{
    public ToolDefinition Definition { get; } = new(
        "create_event",
        "Add an event to the user's Familiar calendar.",
        ToolSchema.Object(["title", "day"],
            ("title", ToolSchema.String("What it is, e.g. \"Dentist\".")),
            ("day", ToolSchema.String("Which day: \"today\", \"tomorrow\", a weekday like \"Tuesday\", or \"YYYY-MM-DD\".")),
            ("time", ToolSchema.String("When it starts, \"HH:MM\" in the user's local time. Leave out for an all-day event.")),
            ("duration_minutes", ToolSchema.Integer("How long it lasts. Optional; an hour if not given."))),
        ToolRisk.WriteOwn);

    /// <remarks>
    /// Day and time apart, and the day as a word if the user said one: given a single "start", the
    /// model worked out "Tuesday" from a Thursday as the Monday's date (2026-10-02). Split, it passes
    /// "Tuesday" and the server does the arithmetic.
    /// </remarks>
    public async Task<ToolResult> InvokeAsync(ToolContext context, JsonObject arguments, CancellationToken ct)
    {
        if (ToolArgs.Label(arguments, "title", CalendarEvent.MaxTitleLength) is not { } title)
        {
            return ToolResult.Failure("No title was given. Ask the user what the event is.");
        }

        // Older shape, and models that put it all in one: "Tuesday 15:00" or "2026-10-06T15:00".
        var dayText = ToolArgs.String(arguments, "day") ?? ToolArgs.String(arguments, "start");
        var timeText = ToolArgs.String(arguments, "time");
        if (dayText is null)
        {
            return ToolResult.Failure("No day was given. Ask the user when it is.");
        }

        DateTimeOffset startsAt, endsAt;
        bool allDay;
        if (timeText is null && LocalDates.Day(dayText, context) is { } day)
        {
            // All-day events are stored as UTC midnight of their date, like a feed's.
            allDay = true;
            startsAt = new DateTimeOffset(day, TimeSpan.Zero);
            endsAt = startsAt.AddDays(1);
        }
        else if (CreateReminderTool.ParseLocal(timeText is null ? dayText : $"{dayText} {timeText}", context) is { } at)
        {
            allDay = false;
            startsAt = at;
            var minutes = Math.Clamp(ToolArgs.Number(arguments, "duration_minutes") ?? 60, 5, 24 * 60);
            endsAt = startsAt.AddMinutes(minutes);
        }
        else
        {
            return ToolResult.Failure("The day or time couldn't be read. Ask the user when it is.");
        }

        var created = await store.AddLocalEventAsync(context.AccountId, title, startsAt, endsAt, allDay, ct);
        var when = Spoken.EventWhen(created, context.Zone, context.Now);
        return ToolResult.Success(
            new JsonObject { ["ok"] = true, ["event"] = new JsonObject { ["title"] = title, ["when"] = when } },
            $"Calendar: {title}, {when}");
    }
}
