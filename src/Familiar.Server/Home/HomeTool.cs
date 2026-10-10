using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Familiar.Agent.Tools;
using Familiar.Domain.Hooks;
using Familiar.Domain.Tools;
using Familiar.Server.Tools;

namespace Familiar.Server.Home;

/// <summary>
/// home: the user's smart home, through Home Assistant's Assist (BACKLOG item 29). "do" hands over
/// a command in plain words; "check" reads how the exposed devices are now. The description names
/// the devices, so the model knows the kettle is something it can switch.
/// </summary>
/// <remarks>
/// Doing doesn't ask first - "lights off" shouldn't need a yes - except for what lets someone in:
/// unlocking, opening a door or the garage, disarming an alarm. And like the familiar's other
/// changes, it asks after a look-up, when outside text has been in the conversation.
/// </remarks>
public sealed partial class HomeTool(HomeAssistant home, HomeAssistantLink link) : ITool, IRiskPerCall
{
    /// <summary>At most this much of the device list goes into the description, which every turn pays for.</summary>
    public const int MaxListed = 1200;

    private static readonly string[] Whats = ["do", "check"];

    private readonly IReadOnlyList<HomeDevice> _devices = HomeAssistant.DevicesOf(link);

    public ToolDefinition Definition => new(
        "home",
        "Works the user's smart home through Home Assistant: switches lights and plugs on and off, sets a light's " +
        "brightness or colour, sets the heating, opens and closes blinds, locks doors, plays and pauses media, and " +
        "says how things are. Never guess how anything in the house is - a temperature, whether something's on - ask it. " +
        "do: say the command simply, as you would to a smart speaker, naming the device or room (\"turn off the " +
        "kitchen light\", \"set the desk lamp brightness to 50%\", \"set the desk lamp to green\", \"is the garage door open\"); " +
        "each change is a new call, and say the device's name, never \"it\". check: how things are " +
        "now, in a room or the whole house." + Listed(),
        ToolSchema.Object(
            ["what"],
            ("what", ToolSchema.Enum("do: a command or question for the home; check: how the devices are now.", Whats)),
            ("command", ToolSchema.String("do: the command, in plain words, with the device or room by name.")),
            ("room", ToolSchema.String("check: a room or device; leave out for everything."))),
        ToolRisk.WriteOwn);

    public ToolRisk RiskOf(JsonObject arguments) => Read(arguments) switch
    {
        (true, _) => ToolRisk.Read,
        (false, var command) when LetsSomeoneIn().IsMatch(command ?? "") => ToolRisk.WriteExternal,
        _ => ToolRisk.WriteOwn,
    };

    public string Describe(JsonObject arguments) => Read(arguments) switch
    {
        (true, _) => "Check the house",
        (false, var command) => $"Tell Home Assistant: “{command}”",
    };

    public async Task<ToolResult> InvokeAsync(ToolContext context, JsonObject arguments, CancellationToken ct)
    {
        try
        {
            var (check, text) = Read(arguments);
            return check ? await CheckAsync(text, ct) : await DoAsync(text, ct);
        }
        catch (HomeAssistantException exception)
        {
            return ToolResult.Failure($"{exception.Message} Tell the user it didn't work.", "Home Assistant didn't answer");
        }
    }

    private async Task<ToolResult> DoAsync(string? command, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(command))
        {
            return ToolResult.Failure("Say the command, e.g. \"turn off the kitchen light\".");
        }

        var answer = await home.AssistAsync(link, command, ct);
        if (!answer.Ok)
        {
            // A name it didn't know, or wording it doesn't: say what there is and how Assist likes to
            // be asked, so one more try can get it right.
            var hint = answer.Code switch
            {
                "no_valid_targets" when _devices.Count > 0 =>
                    $" It knows these: {string.Join("; ", Rooms(_devices))}. If one of them is what the user meant, call home again now with its name; if none is, tell the user.",
                "no_intent_match" =>
                    " It didn't follow the wording. Call home again now, worded its way: \"turn on the <device>\", " +
                    "\"set the <device> brightness to 50%\", \"set the <device> to green\", \"set the temperature in the <room> to 21\", " +
                    "\"is the <device> on\"" + (_devices.Count > 0 ? $". Its devices: {string.Join("; ", Rooms(_devices))}." : "."),
                _ => " Tell the user it didn't work.",
            };
            return ToolResult.Failure($"Home Assistant said: “{answer.Speech}”.{hint}", "Home Assistant didn't understand");
        }

        var data = new JsonObject { ["ok"] = true, ["said"] = answer.Speech };
        if (answer.Done.Count > 0)
        {
            data["done"] = new JsonArray([.. answer.Done.Select(n => JsonValue.Create(n))]);
        }

        if (answer.Failed.Count > 0)
        {
            data["failed"] = new JsonArray([.. answer.Failed.Select(n => JsonValue.Create(n))]);
        }

        return ToolResult.Success(data, $"Home: {answer.Speech}");
    }

    private async Task<ToolResult> CheckAsync(string? room, CancellationToken ct)
    {
        if (_devices.Count == 0)
        {
            return ToolResult.Failure("Familiar can't see which devices are exposed. Use do with a question instead, e.g. \"is the kitchen light on\".");
        }

        var devices = string.IsNullOrEmpty(room) || Everywhere().IsMatch(room)
            ? _devices
            : [.. _devices.Where(d => Mentions(d.Area, room) || Mentions(d.Name, room))];
        if (devices.Count == 0)
        {
            return ToolResult.Failure($"No room or device called “{room}”. The rooms are: {string.Join(", ", _devices.Select(d => d.Area ?? "no room").Distinct())}.");
        }

        var states = (await home.StatesAsync(link, devices, ct)).ToDictionary(s => s["id"]!.GetValue<string>());
        var lines = new JsonArray();
        foreach (var device in devices)
        {
            if (states.TryGetValue(device.Id, out var state))
            {
                lines.Add($"{device.Name}{(device.Area is { } area ? $" ({area})" : "")}: {Say(state)}");
            }
        }

        return ToolResult.Success(new JsonObject { ["ok"] = true, ["devices"] = lines }, $"Checked {Count(lines.Count)}");
    }

    /// <summary>A device's state as said: "on, 40%", "21 °C", "heat, set to 20, 19.5 now".</summary>
    private static string Say(JsonObject state)
    {
        // "playing The News", not "playing, playing The News".
        var playing = state["playing"]?.GetValue<string>();
        var now = state["state"]?.GetValue<string>() ?? "unknown";
        var say = new StringBuilder(playing is not null && now == "playing" ? $"playing {playing}" : now);
        if (state["unit"]?.GetValue<string>() is { } unit)
        {
            say.Append(' ').Append(unit);
        }

        if (state["brightness"] is JsonValue brightness && brightness.TryGetValue<double>(out var b))
        {
            say.Append($", {Math.Round(b / 255 * 100)}%");
        }

        if (state["target"] is JsonValue target)
        {
            say.Append($", set to {target}");
        }

        if (state["current"] is JsonValue current)
        {
            say.Append($", {current} now");
        }

        if (state["position"] is JsonValue position)
        {
            say.Append($", {position}% open");
        }

        if (playing is not null && now != "playing")
        {
            say.Append($", {playing}");
        }

        return say.ToString();
    }

    /// <summary>
    /// Whether it's a check, and the command or room. The model often puts it all in "what" -
    /// "check TV", "do, is the TV on?" - so what follows do or check is read as the rest.
    /// </summary>
    private static (bool Check, string? Text) Read(JsonObject arguments)
    {
        var what = ToolArgs.String(arguments, "what") ?? "";
        var command = ToolArgs.String(arguments, "command");
        var room = ToolArgs.String(arguments, "room");
        var said = DoOrCheck().Match(what);
        var check = said.Success
            ? said.Groups[1].Value.Equals("check", StringComparison.OrdinalIgnoreCase)
            : command is null && room is not null;
        var rest = said.Success ? said.Groups[2].Value.Trim() : what;
        return check
            ? (true, room ?? (rest.Length > 0 ? rest : null))
            : (false, command ?? (rest.Length > 0 ? rest : null));
    }

    private static bool Mentions(string? name, string said) =>
        name is not null && (name.Contains(said, StringComparison.OrdinalIgnoreCase) || said.Contains(name, StringComparison.OrdinalIgnoreCase));

    private static string Count(int n) => n == 1 ? "1 device" : $"{n} devices";

    /// <summary>The devices by room: "Kitchen: Kitchen light, Kettle".</summary>
    private static IEnumerable<string> Rooms(IReadOnlyList<HomeDevice> devices) =>
        devices.GroupBy(d => d.Area).Select(g => $"{g.Key ?? "Elsewhere"}: {string.Join(", ", g.Select(d => d.Name))}");

    private string Listed()
    {
        if (_devices.Count == 0)
        {
            return "";
        }

        var listed = new StringBuilder(" Its devices: ");
        foreach (var room in Rooms(_devices))
        {
            if (listed.Length + room.Length > MaxListed)
            {
                listed.Append("and more. ");
                break;
            }

            listed.Append(room).Append(". ");
        }

        return listed.ToString().TrimEnd();
    }

    [GeneratedRegex(@"^\s*(do|check)\b[\s,:;-]*(.*)$", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex DoOrCheck();

    [GeneratedRegex(@"^(the )?(everything|everywhere|all|any|anything|anywhere|whole house|house|home|all rooms)$", RegexOptions.IgnoreCase)]
    private static partial Regex Everywhere();

    [GeneratedRegex(@"\b(unlock|disarm)|\bopen\b.*\b(door|garage|gate)|\b(door|garage|gate)\b.*\bopen\b", RegexOptions.IgnoreCase)]
    private static partial Regex LetsSomeoneIn();
}
