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

/// <summary>
/// send_text: Messages opens with the text written, for the user to tap Send - or, on a phone allowed
/// to send texts itself (BACKLOG item 39), for hands-free: the first call only reads it back ("Send
/// 'running late' to Mum's mobile?"), and the phone sends it when the user's next words are a yes
/// (TextDrafts) - the words read back and no others. Never by itself in a turn with someone else's
/// words in it (a look-up, a picture): then it's Messages, and Send is theirs to tap.
/// </summary>
public sealed class SendTextTool(DeviceTools devices, TextDrafts drafts) : PhoneTool(devices)
{
    internal const int MaxMessage = 600;

    private readonly DeviceTools _devices = devices;

    public override string Capability => DeviceCapabilities.Text;

    public override ToolDefinition Definition { get; } = new(
        "send_text",
        "Texts someone from the user's phone. Use when they ask you to text someone. Give the person as they'd say it " +
        "(\"Mum\", \"Sam Kim\") or a phone number; their phone finds the contact. Either Messages opens with it written and " +
        "they tap Send, or - if their phone sends texts itself - it isn't sent yet: it's read back to them, and when they say " +
        "yes, call send_text again with send true. Never say a text was sent unless it says so.",
        ToolSchema.Object(
            ["to", "message"],
            ("to", ToolSchema.String("Who to text: a contact's name as the user said it, or a phone number.")),
            ("message", ToolSchema.String("The text, in the user's voice, as they asked for it.")),
            ("send", ToolSchema.Boolean("True only when they've just said yes to the text you read back."))),
        ToolRisk.WriteOwn);

    public override string Describe(JsonObject arguments) => $"Text {ToolArgs.String(arguments, "to")}: \"{ToolArgs.String(arguments, "message")}\"";

    public override async Task<ToolResult> InvokeAsync(ToolContext context, JsonObject arguments, CancellationToken ct)
    {
        // Sending what they said yes to: the text read back, whatever this call says. Asked to send
        // with no yes behind it (the model jumping ahead, on "text Sam I'm late"), it's read back
        // like any first call - or, with nothing to read back, it says why not.
        // After a yes, the same words again without send are the same text: sent, not read back again.
        var send = ToolArgs.Bool(arguments, "send") == true;
        if (drafts.Approved(context.AccountId, context.ReplyId) is { } approved
            && (send || string.Equals(ToolArgs.String(arguments, "message")?.Trim(), approved.Message, StringComparison.Ordinal)))
        {
            return await SendAsync(context, ct);
        }

        var to = ToolArgs.Label(arguments, "to", 80);
        var message = ToolArgs.String(arguments, "message")?.Trim();
        if (to is null || string.IsNullOrEmpty(message))
        {
            return send
                ? ToolResult.Failure("Not sent: they haven't just said yes to a text read back. Call send_text without send to read it back first.", "Not sent")
                : ToolResult.Failure("Say who to text and what to say.");
        }

        if (message.Length > MaxMessage)
        {
            return ToolResult.Failure($"That's too long for a text (over {MaxMessage} characters). Make it shorter.");
        }

        if (CanSendItself(context))
        {
            return await ReadBackAsync(context, to, message, ct);
        }

        return await AskPhoneAsync(context, new JsonObject { ["to"] = to, ["message"] = message }, r => $"Text to {Said(r, "to") ?? to}", ct);
    }

    /// <summary>A phone that can send it is here, and nobody else's words are in this turn.</summary>
    private bool CanSendItself(ToolContext context) =>
        !context.Turn.OutsideText && context.Turn.LookUps == 0
        && _devices.Find(context.AccountId, DeviceCapabilities.SendText, context.From, browsers: false) is not null;

    /// <summary>The phone finds the contact; the text waits, read back, for a yes.</summary>
    private async Task<ToolResult> ReadBackAsync(ToolContext context, string to, string message, CancellationToken ct)
    {
        var answer = await _devices.InvokeAsync(context.AccountId, DeviceCapabilities.SendText, DeviceCapabilities.SendText,
            new JsonObject { ["to"] = to, ["message"] = message, ["check"] = true }, ct, context.From, browsers: false);
        if (answer is null)
        {
            return ToolResult.Failure("Their phone didn't answer: Familiar has to be open on it (it can be in the background). Say so.", "Phone didn't answer");
        }

        if (!answer.Ok)
        {
            return ToolResult.Failure(answer.Error ?? "The phone couldn't do that. Say so.", "Phone couldn't");
        }

        var name = Said(answer.Result, "to") ?? to;
        var label = Said(answer.Result, "label");
        var who = string.IsNullOrEmpty(label) || PhoneNumber(name) ? name : $"{name}'s {label.ToLowerInvariant()}";
        drafts.Hold(context.AccountId, to, name, message);
        var ask = $"Send \"{message}\" to {who}?";

        // Said as it is, not in the model's words: it left out what the text says (2026-10-18), and a
        // yes to that would send words the user never heard.
        return ToolResult.Success(new JsonObject
        {
            ["ok"] = true,
            ["sent"] = false,
            ["asked"] = ask,
            ["note"] = "Not sent yet: they've been asked. Send it only when they say yes, with send_text and send true. " +
                "If they want it changed, call send_text with the new words.",
        }, $"Text to {name}: waiting for yes") with { FinalWords = ask };
    }

    private async Task<ToolResult> SendAsync(ToolContext context, CancellationToken ct)
    {
        // Their yes, just now, in their own words, to the text read back - and nobody else's words since.
        if (drafts.Approved(context.AccountId, context.ReplyId) is not { } draft || context.Turn.OutsideText || context.Turn.LookUps > 0)
        {
            return ToolResult.Failure(
                "Not sent: they haven't just said yes to it being read back. Call send_text without send to read it back first.",
                "Not sent");
        }

        drafts.Done(context.AccountId);
        var answer = await _devices.InvokeAsync(context.AccountId, DeviceCapabilities.SendText, DeviceCapabilities.SendText,
            new JsonObject { ["to"] = draft.To, ["message"] = draft.Message, ["expect"] = draft.Name }, ct, context.From, browsers: false);
        if (answer is null)
        {
            return ToolResult.Failure("Not sent: their phone didn't answer (Familiar has to be open on it). Say so.", "Phone didn't answer");
        }

        if (!answer.Ok)
        {
            return ToolResult.Failure($"Not sent: {answer.Error ?? "the phone couldn't send it."} Say so.", "Not sent");
        }

        return ToolResult.Success(new JsonObject { ["ok"] = true, ["sent"] = true, ["to"] = draft.Name }, $"Texted {draft.Name}");
    }

    private static bool PhoneNumber(string text) =>
        text.Count(char.IsDigit) >= 3 && text.All(c => char.IsDigit(c) || "+ -().#*".Contains(c, StringComparison.Ordinal));
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
        "directions or to be taken somewhere. To go to someone they know (\"take me to Mum's\"), give that person as " +
        "to_contact: their phone finds the address in their contacts, and it never needs saying.",
        ToolSchema.Object(
            ("destination", ToolSchema.String("Where to, as they said it: \"the station\", \"Tesco in Headingley\", an address. Leave out with to_contact.")),
            ("to_contact", ToolSchema.String("Someone in their contacts whose address to go to, as they said it: \"Mum\", \"Sam Kim\".")),
            ("which", ToolSchema.String("Which of that contact's addresses, if they said: \"home\", \"work\".")),
            ("mode", ToolSchema.Enum("How they're going. Leave out for driving.", Modes))),
        ToolRisk.WriteOwn);

    public override string Describe(JsonObject arguments) =>
        $"Directions to {ToolArgs.String(arguments, "destination") ?? ToolArgs.String(arguments, "to_contact") + "'s"}";

    public override Task<ToolResult> InvokeAsync(ToolContext context, JsonObject arguments, CancellationToken ct)
    {
        var destination = ToolArgs.Label(arguments, "destination", 120);
        var contact = ToolArgs.Label(arguments, "to_contact", 80);
        if (destination is null && contact is null)
        {
            return Task.FromResult(ToolResult.Failure("Ask where they want to go."));
        }

        var mode = ToolArgs.String(arguments, "mode")?.ToLowerInvariant();
        var request = new JsonObject { ["mode"] = Modes.Contains(mode) ? mode : "driving" };
        if (contact is not null)
        {
            request["to_contact"] = contact;
            if (ToolArgs.Label(arguments, "which", 40) is { } which)
            {
                request["which"] = which;
            }
        }
        else
        {
            request["destination"] = destination;
        }

        return AskPhoneAsync(context, request, r => $"Directions to {(contact is null ? destination : (Said(r, "to") ?? contact) + "'s")}", ct);
    }
}

/// <summary>
/// contact_details: one contact's number or address, to say it (BACKLOG item 25). Unlike texting or
/// calling, the familiar has to see it to say it - the one contact detail that leaves the phone,
/// when asked for. Never remembered (the memory pass leaves contact details out).
/// </summary>
public sealed class ContactDetailsTool(DeviceTools devices) : PhoneTool(devices)
{
    private static readonly string[] Wants = ["phone", "address", "both"];

    public override string Capability => DeviceCapabilities.Contacts;

    public override ToolDefinition Definition { get; } = new(
        "contact_details",
        "Looks up someone in the user's phone contacts and gives you their phone number or address, to tell the user. Use " +
        "only when they ask what someone's number or address is - to text, call or go there, use send_text, call or " +
        "navigate instead, which don't need the details.",
        ToolSchema.Object(
            ["name"],
            ("name", ToolSchema.String("Who, as the user said it: \"Sam\", \"the dentist\".")),
            ("want", ToolSchema.Enum("What they asked for. Leave out for the phone number.", Wants)),
            ("which", ToolSchema.String("Which number or address, if they said: \"mobile\", \"work\", \"home\"."))),
        ToolRisk.Read);

    public override string Describe(JsonObject arguments) => $"Look up {ToolArgs.String(arguments, "name")} in the phone's contacts";

    public override Task<ToolResult> InvokeAsync(ToolContext context, JsonObject arguments, CancellationToken ct)
    {
        if (ToolArgs.Label(arguments, "name", 80) is not { } name)
        {
            return Task.FromResult(ToolResult.Failure("Ask who they mean."));
        }

        var want = ToolArgs.String(arguments, "want")?.ToLowerInvariant();
        var request = new JsonObject { ["name"] = name, ["want"] = Wants.Contains(want) ? want : "phone" };
        if (ToolArgs.Label(arguments, "which", 40) is { } which)
        {
            request["which"] = which;
        }

        return AskPhoneAsync(context, request, r => $"Contact: {Said(r, "name") ?? name}", ct);
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

    public override async Task<ToolResult> InvokeAsync(ToolContext context, JsonObject arguments, CancellationToken ct)
    {
        var found = await AskPhoneAsync(context, [], r => $"Location: {Said(r, "place") ?? (r["coordinates"] is null ? "found" : "from the browser")}", ct);
        if (found.Ok || string.IsNullOrWhiteSpace(context.HomePlace))
        {
            return found;
        }

        // The device wouldn't or couldn't say: home will usually do (BACKLOG item 32). Stopping there
        // left "who are the candidates for me?" with no answer when it knew where they live.
        return ToolResult.Success(new JsonObject
        {
            ["ok"] = true,
            ["home"] = context.HomePlace,
            ["note"] = $"The device couldn't say where it is ({found.Data["error"]?.GetValue<string>()}). They live in {context.HomePlace}: " +
                "go by that, and mention you're assuming home, unless they say they're somewhere else.",
        }, "Location: home");
    }
}
