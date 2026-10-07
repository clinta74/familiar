using System.Text.Json.Nodes;
using Familiar.Agent.Tools;
using Familiar.Domain.Tools;

namespace Familiar.Server.Devices;

/// <summary>A tool only a phone can carry out: offered while a phone that can do it has the app open.</summary>
public interface IPhoneTool
{
    /// <summary>What the phone has to be able to do (<see cref="DeviceCapabilities"/>).</summary>
    string Capability { get; }
}

/// <summary>
/// Things only the phone can do (BACKLOG item 21): text, call, navigate, say where it is. The phone
/// hands off rather than acting - Messages opens with the text written, the dialler with the number
/// in, Maps with the route - and the user taps Send or Call. Contacts are matched on the phone: the
/// server and the model get a name, never a number or the address book.
/// </summary>
public abstract class PhoneTool(DeviceTools devices) : ITool, IPhoneTool
{
    public abstract ToolDefinition Definition { get; }

    public abstract string Capability { get; }

    public virtual string Describe(JsonObject arguments) => Definition.Description;

    public abstract Task<ToolResult> InvokeAsync(ToolContext context, JsonObject arguments, CancellationToken ct);

    /// <summary>Asks the phone; its answer, or why there isn't one, for the model.</summary>
    protected async Task<ToolResult> AskPhoneAsync(ToolContext context, JsonObject arguments, Func<JsonObject, string> summary, CancellationToken ct)
    {
        // The device the message came from, if it can; a browser only with look-ups on (it gives
        // coordinates, which are only of use to the weather tools).
        var answer = await devices.InvokeAsync(context.AccountId, Capability, Capability, arguments, ct, context.From, browsers: context.LookUps);
        if (answer is null)
        {
            return ToolResult.Failure(
                "Their phone didn't answer: Familiar has to be open on it (it can be in the background). Say so.",
                "Phone didn't answer");
        }

        if (!answer.Ok)
        {
            return ToolResult.Failure(answer.Error ?? "The phone couldn't do that. Say so.", "Phone couldn't");
        }

        var data = answer.Result.DeepClone().AsObject();
        data["ok"] = true;
        return ToolResult.Success(data, summary(answer.Result));
    }

    protected static string? Said(JsonObject result, string name) => result[name]?.GetValue<string>();
}

public sealed class SendTextTool(DeviceTools devices) : PhoneTool(devices)
{
    internal const int MaxMessage = 600;

    public override string Capability => DeviceCapabilities.Text;

    public override ToolDefinition Definition { get; } = new(
        "send_text",
        "Writes a text message for the user to send from their phone: Messages opens with it written, and they tap Send. " +
        "Use when they ask you to text someone. Give the person as they'd say it (\"Mum\", \"Sam Kim\") or a phone number; " +
        "their phone finds the contact.",
        ToolSchema.Object(
            ["to", "message"],
            ("to", ToolSchema.String("Who to text: a contact's name as the user said it, or a phone number.")),
            ("message", ToolSchema.String("The text, in the user's voice, as they asked for it."))),
        ToolRisk.WriteOwn);

    public override string Describe(JsonObject arguments) => $"Text {ToolArgs.String(arguments, "to")}: \"{ToolArgs.String(arguments, "message")}\"";

    public override Task<ToolResult> InvokeAsync(ToolContext context, JsonObject arguments, CancellationToken ct)
    {
        var to = ToolArgs.Label(arguments, "to", 80);
        var message = ToolArgs.String(arguments, "message");
        if (to is null || string.IsNullOrWhiteSpace(message))
        {
            return Task.FromResult(ToolResult.Failure("Say who to text and what to say."));
        }

        if (message.Length > MaxMessage)
        {
            return Task.FromResult(ToolResult.Failure($"That's too long for a text (over {MaxMessage} characters). Make it shorter."));
        }

        return AskPhoneAsync(context, new JsonObject { ["to"] = to, ["message"] = message }, r => $"Text to {Said(r, "to") ?? to}", ct);
    }
}

public sealed class CallTool(DeviceTools devices) : PhoneTool(devices)
{
    public override string Capability => DeviceCapabilities.Call;

    public override ToolDefinition Definition { get; } = new(
        "call",
        "Gets a phone call ready on the user's phone: the dialler opens with the number in, and they tap Call. Use when they ask " +
        "you to call or ring someone. Give the person as they'd say it, or a number; their phone finds the contact.",
        ToolSchema.Object(["to"], ("to", ToolSchema.String("Who to call: a contact's name as the user said it, or a phone number."))),
        ToolRisk.WriteOwn);

    public override string Describe(JsonObject arguments) => $"Call {ToolArgs.String(arguments, "to")}";

    public override Task<ToolResult> InvokeAsync(ToolContext context, JsonObject arguments, CancellationToken ct) =>
        ToolArgs.Label(arguments, "to", 80) is { } to
            ? AskPhoneAsync(context, new JsonObject { ["to"] = to }, r => $"Call {Said(r, "to") ?? to}", ct)
            : Task.FromResult(ToolResult.Failure("Say who to call."));
}

public sealed class NavigateTool(DeviceTools devices) : PhoneTool(devices)
{
    private static readonly string[] Modes = ["driving", "walking", "cycling", "transit"];

    public override string Capability => DeviceCapabilities.Navigate;

    public override ToolDefinition Definition { get; } = new(
        "navigate",
        "Starts directions on the user's phone: Maps opens with the route to where they want to go. Use when they ask for " +
        "directions or to be taken somewhere.",
        ToolSchema.Object(
            ["destination"],
            ("destination", ToolSchema.String("Where to, as they said it: \"the station\", \"Tesco in Headingley\", an address.")),
            ("mode", ToolSchema.Enum("How they're going. Leave out for driving.", Modes))),
        ToolRisk.WriteOwn);

    public override string Describe(JsonObject arguments) => $"Directions to {ToolArgs.String(arguments, "destination")}";

    public override Task<ToolResult> InvokeAsync(ToolContext context, JsonObject arguments, CancellationToken ct)
    {
        if (ToolArgs.Label(arguments, "destination", 120) is not { } destination)
        {
            return Task.FromResult(ToolResult.Failure("Ask where they want to go."));
        }

        var mode = ToolArgs.String(arguments, "mode")?.ToLowerInvariant();
        var request = new JsonObject { ["destination"] = destination, ["mode"] = Modes.Contains(mode) ? mode : "driving" };
        return AskPhoneAsync(context, request, _ => $"Directions to {destination}", ct);
    }
}

public sealed class WhereAmITool(DeviceTools devices) : PhoneTool(devices)
{
    public override string Capability => DeviceCapabilities.Location;

    public override ToolDefinition Definition { get; } = new(
        "where_am_i",
        "Where the user is right now, roughly: the town or area from their phone, or coordinates from their browser. Use for " +
        "\"where am I?\", or when they ask about the weather or anything else \"here\" - then pass the place (or the " +
        "coordinates, as they are) to that tool.",
        ToolSchema.Object(),
        ToolRisk.Read);

    public override string Describe(JsonObject arguments) => "Check where the phone is";

    public override Task<ToolResult> InvokeAsync(ToolContext context, JsonObject arguments, CancellationToken ct) =>
        AskPhoneAsync(context, [], r => $"Location: {Said(r, "place") ?? (r["coordinates"] is null ? "found" : "from the browser")}", ct);
}
