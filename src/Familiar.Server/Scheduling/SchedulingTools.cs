using System.Globalization;
using System.Text.Json.Nodes;
using Familiar.Agent.Tools;
using Familiar.Domain.Scheduling;
using Familiar.Domain.Tools;

namespace Familiar.Server.Scheduling;

/// <summary>
/// set_timer: a countdown. Hours, minutes and seconds rather than one duration string: small models
/// fill in numbers reliably and fumble formats like "PT10M".
/// </summary>
public sealed class SetTimerTool(Scheduler scheduler) : ITool
{
    public static readonly TimeSpan MaxLength = TimeSpan.FromDays(7);

    public ToolDefinition Definition { get; } = new(
        "set_timer",
        "Start a countdown timer that rings when it's done. Give its length in hours, minutes and/or seconds.",
        ToolSchema.Object(
            ("hours", ToolSchema.Integer("Hours")),
            ("minutes", ToolSchema.Integer("Minutes")),
            ("seconds", ToolSchema.Integer("Seconds")),
            ("label", ToolSchema.String("What it's for, in a word or two, e.g. \"pasta\". Optional."))),
        ToolRisk.WriteOwn);

    public async Task<ToolResult> InvokeAsync(ToolContext context, JsonObject arguments, CancellationToken ct)
    {
        var seconds = (ToolArgs.Number(arguments, "hours") ?? 0) * 3600
            + (ToolArgs.Number(arguments, "minutes") ?? 0) * 60
            + (ToolArgs.Number(arguments, "seconds") ?? 0);

        if (seconds < 1)
        {
            return ToolResult.Failure("No length was given. Ask the user how long the timer should be.");
        }

        if (seconds > MaxLength.TotalSeconds)
        {
            return ToolResult.Failure("Timers can be at most 7 days long. Suggest a reminder instead.");
        }

        var length = (int)Math.Round(seconds);
        var job = await scheduler.AddAsync(new ScheduledJob
        {
            AccountId = context.AccountId,
            Kind = JobKind.Timer,
            Label = ToolArgs.Label(arguments, "label") ?? "",
            DueAt = context.Now.AddSeconds(length),
            DurationSeconds = length,
            TimeZone = context.Zone.Id,
            CreatedAt = context.Now,
        }, ct);

        if (job is null)
        {
            return ToolResult.Failure($"There are already {Scheduler.MaxPending} timers and reminders running.");
        }

        var duration = Spoken.Duration(length);
        return ToolResult.Success(
            new JsonObject
            {
                ["ok"] = true,
                // Just the length: given the time it rings as well, the model tried to say both and
                // got the clock arithmetic wrong.
                ["timer"] = new JsonObject { ["label"] = job.Label, ["length"] = duration },
            },
            job.Label.Length > 0 ? $"Timer: {job.Label}, {duration}" : $"Timer: {duration}");
    }
}

/// <summary>create_reminder: something to be told at a time.</summary>
public sealed class CreateReminderTool(Scheduler scheduler) : ITool
{
    public static readonly TimeSpan MaxAhead = TimeSpan.FromDays(366);

    private static readonly string[] Formats =
    [
        "yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss", "HH:mm", "H:mm",
    ];

    public ToolDefinition Definition { get; } = new(
        "create_reminder",
        "Remind the user about something at a time. Give either the local time it's due, or how many minutes from now.",
        ToolSchema.Object(
            ["text"],
            ("text", ToolSchema.String("What to remind them of, e.g. \"call Mum\".")),
            ("time", ToolSchema.String("When, in the user's local time: \"YYYY-MM-DDTHH:MM\", or \"HH:MM\" for the next time it's that time.")),
            ("in_minutes", ToolSchema.Integer("Or: how many minutes from now."))),
        ToolRisk.WriteOwn);

    public async Task<ToolResult> InvokeAsync(ToolContext context, JsonObject arguments, CancellationToken ct)
    {
        if (ToolArgs.Label(arguments, "text") is not { } text)
        {
            return ToolResult.Failure("No reminder text was given. Ask the user what to remind them of.");
        }

        DateTimeOffset due;
        if (ToolArgs.Number(arguments, "in_minutes") is { } minutes and > 0)
        {
            due = context.Now.AddMinutes(minutes);
        }
        else if (ToolArgs.String(arguments, "time") is { } time && ParseLocal(time, context) is { } at)
        {
            due = at;
        }
        else
        {
            return ToolResult.Failure("No time was given, or it couldn't be read. Ask the user when.");
        }

        if (due < context.Now.AddSeconds(-30))
        {
            return ToolResult.Failure($"That time has already passed: it's {Spoken.When(context.Now, context.Zone, context.Now)} now.");
        }

        if (due > context.Now + MaxAhead)
        {
            return ToolResult.Failure("Reminders can be at most a year ahead.");
        }

        var job = await scheduler.AddAsync(new ScheduledJob
        {
            AccountId = context.AccountId,
            Kind = JobKind.Reminder,
            Label = text,
            DueAt = due,
            TimeZone = context.Zone.Id,
            CreatedAt = context.Now,
        }, ct);

        if (job is null)
        {
            return ToolResult.Failure($"There are already {Scheduler.MaxPending} timers and reminders running.");
        }

        var when = Spoken.When(due, context.Zone, context.Now);
        return ToolResult.Success(
            new JsonObject { ["ok"] = true, ["reminder"] = new JsonObject { ["text"] = text, ["when"] = when } },
            $"Reminder: {text}, {when}");
    }

    /// <summary>
    /// The user's local time to UTC. A bare "HH:MM" means the next time the clock says that. A time
    /// skipped by a daylight-saving change is moved past the gap.
    /// </summary>
    internal static DateTimeOffset? ParseLocal(string text, ToolContext context)
    {
        if (!DateTime.TryParseExact(text.Trim(), Formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
        {
            return null;
        }

        var timeOnly = text.Trim().Length <= 5;
        if (timeOnly)
        {
            var today = TimeZoneInfo.ConvertTime(context.Now, context.Zone).Date;
            local = today + local.TimeOfDay;
        }

        var due = ToUtc(local, context.Zone);
        if (timeOnly && due < context.Now)
        {
            due = ToUtc(local.AddDays(1), context.Zone);
        }

        return due;
    }

    private static DateTimeOffset ToUtc(DateTime local, TimeZoneInfo zone)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(local))
        {
            local = local.AddMinutes(15);
        }

        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone), TimeSpan.Zero);
    }
}

/// <summary>list_scheduled: what's running.</summary>
public sealed class ListScheduledTool(Scheduler scheduler) : ITool
{
    public ToolDefinition Definition { get; } = new(
        "list_scheduled",
        "List the user's running timers and upcoming reminders.",
        ToolSchema.Object(),
        ToolRisk.Read);

    public async Task<ToolResult> InvokeAsync(ToolContext context, JsonObject arguments, CancellationToken ct)
    {
        var pending = await scheduler.PendingAsync(context.AccountId, null, ct);

        var timers = new JsonArray();
        var reminders = new JsonArray();
        foreach (var job in pending)
        {
            if (job.Kind == JobKind.Timer)
            {
                timers.Add(new JsonObject
                {
                    ["label"] = job.Label,
                    ["remaining"] = Spoken.Duration((int)Math.Max(1, (job.DueAt - context.Now).TotalSeconds)),
                    ["rings"] = Spoken.When(job.DueAt, context.Zone, context.Now),
                });
            }
            else
            {
                reminders.Add(new JsonObject { ["text"] = job.Label, ["when"] = Spoken.When(job.DueAt, context.Zone, context.Now) });
            }
        }

        return ToolResult.Success(
            new JsonObject { ["ok"] = true, ["timers"] = timers, ["reminders"] = reminders },
            pending.Count switch
            {
                0 => "Nothing scheduled",
                1 => "1 thing scheduled",
                var n => $"{n} things scheduled",
            });
    }
}

/// <summary>cancel_scheduled: stops a timer or drops a reminder, matched by what it's for.</summary>
public sealed class CancelScheduledTool(Scheduler scheduler) : ITool
{
    public ToolDefinition Definition { get; } = new(
        "cancel_scheduled",
        "Cancel a running timer or an upcoming reminder.",
        ToolSchema.Object(
            ("kind", ToolSchema.Enum("Which sort to cancel.", "timer", "reminder")),
            ("label", ToolSchema.String("Words from its label or text, to say which one. Leave out if there's only one.")),
            ("all", ToolSchema.Boolean("Cancel every one that matches, rather than just one."))),
        ToolRisk.WriteOwn);

    public async Task<ToolResult> InvokeAsync(ToolContext context, JsonObject arguments, CancellationToken ct)
    {
        JobKind? kind = ToolArgs.String(arguments, "kind")?.ToLowerInvariant() switch
        {
            "timer" => JobKind.Timer,
            "reminder" => JobKind.Reminder,
            _ => null,
        };

        var label = ToolArgs.Label(arguments, "label");
        var matches = (await scheduler.PendingAsync(context.AccountId, kind, ct))
            .Where(j => label is null || j.Label.Contains(label, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var noun = kind switch
        {
            JobKind.Timer => "timer",
            JobKind.Reminder => "reminder",
            _ => "timer or reminder",
        };

        if (matches.Count == 0)
        {
            return ToolResult.Failure(label is null ? $"There's no {noun} to cancel." : $"There's no {noun} matching \"{label}\".");
        }

        if (matches.Count > 1 && ToolArgs.Bool(arguments, "all") != true)
        {
            var labels = new JsonArray([.. matches.Select(j => (JsonNode)JsonValue.Create(Describe(j, context))!)]);
            return new ToolResult(false,
                new JsonObject { ["ok"] = false, ["error"] = "More than one matches. Ask the user which one.", ["matches"] = labels },
                null);
        }

        foreach (var job in matches)
        {
            await scheduler.CancelAsync(context.AccountId, job.Id, ct);
        }

        var cancelled = matches.Count == 1 ? Describe(matches[0], context) : $"{matches.Count} {noun}s";
        return ToolResult.Success(new JsonObject { ["ok"] = true, ["cancelled"] = cancelled }, $"Cancelled: {cancelled}");
    }

    private static string Describe(ScheduledJob job, ToolContext context)
    {
        var name = job.Kind == JobKind.Timer
            ? job.Label.Length > 0 ? $"{job.Label} timer" : $"{Spoken.Duration(job.DurationSeconds ?? 0)} timer"
            : $"reminder to {job.Label}";
        return $"{name} ({Spoken.When(job.DueAt, context.Zone, context.Now)})";
    }
}
