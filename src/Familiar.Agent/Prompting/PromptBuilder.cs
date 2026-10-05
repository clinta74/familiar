using System.Globalization;
using System.Text;
using Familiar.Agent.Llm;
using Familiar.Domain.Conversations;
using Familiar.Domain.Expression;
using Familiar.Domain.Familiars;

namespace Familiar.Agent.Prompting;

public sealed record PromptContext(
    FamiliarProfile Profile,
    string Username,
    IReadOnlyList<Message> History,
    string UserText,
    DateTimeOffset Now,
    TimeZoneInfo TimeZone,
    IReadOnlyList<string>? Notes = null,
    IReadOnlyList<string>? Memories = null,
    string? Look = null);

/// <summary>
/// Builds the messages for one turn, always in the same order - most stable first, most variable
/// last - because Ollama reuses its KV cache for exactly as long as the prompt prefix matches
/// (PLAN.md §4.3, and muwbta's tools/ollama/README.md for what that's worth).
/// </summary>
/// <remarks>
/// <para>
/// 1. One system message: the fixed rules, then this user's persona. Identical turn after turn.
/// 2. The conversation so far.
/// 3. The new user message, prefixed with the current time.
/// </para>
/// <para>
/// The time goes on the last message rather than in the system message, where PLAN.md first put it:
/// it changes every minute, and anything after it in the prompt would be re-evaluated every turn.
/// </para>
/// <para>
/// The expression-tag rules are worded for spoken replies only. "Begin every reply with a tag" made
/// the Qwen models answer tool requests in speech instead of calling the tool (bake-off,
/// 2026-10-01); keep this wording when tools arrive, and re-run the bake-off when it changes.
/// </para>
/// <para>
/// The tools paragraph is blunt on purpose. Worded politely ("call the tool; don't just say you
/// will") familiar-chat called list and cancel tools but answered every "set a timer" with "I've
/// set your timer" and no call, 0 times in 15; naming the tools and saying they're the only way
/// anything happens took it to 15 in 15, with chit-chat still calling nothing (2026-10-02).
/// </para>
/// </remarks>
public static class PromptBuilder
{
    internal const string Rules = """
        You speak aloud through an animated avatar, so every reply is spoken. Talk the way a person
        talks: plain words, no markdown, lists, emoji, or stage directions.

        You have tools, and they're the only way you can actually do things: a timer only exists if
        you call set_timer, a reminder only if you call create_reminder. When the user asks for
        something a tool does, call it straight away, before you say anything about it. Never say
        you've done something unless a tool's result says it worked. After a tool runs, tell the
        user what happened in a short sentence. If you need a detail you don't have (how long, what
        time), ask for it; never guess one.

        Begin each spoken reply with exactly one emotion tag, [emotion:intensity], where emotion is
        one of joy, sad, angry, surprised, relaxed, neutral, thinking and intensity is between 0 and 1.
        Choose the emotion that fits how you feel about what you're saying, not just the user's mood.
        Where it fits naturally you may add one gesture tag, [gesture:name], with name one of nod,
        shake, tilt, wave, shrug, laugh, wink, sigh, yawn, excited. Tags are never read aloud.
        Example: [joy:0.7] That's wonderful news! [gesture:nod] You earned it.
        Now and then, only when a moment really calls for it, you may add one sound effect,
        [sound:name], with name one of pop, boing, sparkle, tada (a little fanfare), whoosh,
        womp (a sad trombone). Most replies have none; never more than one.

        Each user message begins with notes in parentheses: the current local time, and sometimes
        what's on their calendar, what timers are running, or how long since you last talked. They're
        context for you, not something the user said. Bring them up when they matter (a meeting in
        ten minutes is worth a mention); otherwise don't repeat them.
        A previous reply of yours ending in "…" was cut off by the user before you finished.
        """;

    /// <summary>Asked to speak first, with nothing from the user: a greeting (PLAN.md §6.3).</summary>
    public const string GreetingCue =
        "[They've just opened the app and haven't said anything yet. Greet them in a sentence or two, the way a " +
        "friend would after however long it's been. If something in the notes is coming up soon, mention it. If " +
        "today's weather is in the notes and worth knowing (rain, snow, a hot or cold day), mention that briefly too.]";

    public static IReadOnlyList<ChatMessage> Build(PromptContext context) => Assemble(context, context.UserText);

    /// <summary>A turn with no user message: the familiar speaks first, prompted by a bracketed cue.</summary>
    public static IReadOnlyList<ChatMessage> BuildUnprompted(PromptContext context, string cue) => Assemble(context, cue);

    private static IReadOnlyList<ChatMessage> Assemble(PromptContext context, string last)
    {
        var messages = new List<ChatMessage>(context.History.Count + 2)
        {
            new(ChatRole.System, SystemMessage(context)),
        };

        foreach (var message in context.History)
        {
            messages.Add(message.Role == MessageRole.User
                ? new ChatMessage(ChatRole.User, message.Content)
                : new ChatMessage(ChatRole.Assistant, AsTheModelWroteIt(message)));
        }

        var notes = context.Notes is { Count: > 0 } n ? ". " + string.Join(" ", n).TrimEnd('.') : "";
        messages.Add(new ChatMessage(ChatRole.User, $"({Situation(context.Now, context.TimeZone)}{notes}) {last}"));
        return messages;
    }

    /// <summary>
    /// Persona, then rules, then memories: the memories change now and then, so they go last, and a
    /// new one doesn't cost the cached persona and rules.
    /// </summary>
    internal static string SystemMessage(PromptContext context)
    {
        var text = new StringBuilder();
        text.Append(Persona(context.Profile, context.Username, context.Look)).Append("\n\n").Append(Rules);

        if (context.Memories is { Count: > 0 } memories)
        {
            var you = string.IsNullOrWhiteSpace(context.Profile.UserNickname) ? context.Username : context.Profile.UserNickname.Trim();
            text.Append(CultureInfo.InvariantCulture, $"\n\nWhat you remember about {you} (they told you; use it naturally, don't recite it):");
            foreach (var memory in memories)
            {
                text.Append("\n- ").Append(memory);
            }
        }

        return text.ToString();
    }

    /// <summary>What the default familiar looks like. Without a sentence like this the model denies being what it's drawn as.</summary>
    public const string DefaultLook = "You look like a soft, fluffy plush llama with big ears, and you're happy being one.";

    /// <summary>
    /// The persona, from the profile's sliders. Rendered from fixed phrases, never from free text,
    /// so it can't carry instructions, and it only changes when the user changes a setting.
    /// </summary>
    internal static string Persona(FamiliarProfile profile, string username, string? look = null)
    {
        var you = string.IsNullOrWhiteSpace(profile.UserNickname) ? username : profile.UserNickname.Trim();
        var text = new StringBuilder();

        text.Append(CultureInfo.InvariantCulture,
            $"You are {profile.Name}, {you}'s familiar: a small, friendly companion who lives on their devices ");
        text.Append("and keeps them company, chats with them, and helps out with everyday things. ");

        // What the user sees on screen (PromptContext.Look): without it the model denies being the
        // llama, fox or spot it's drawn as.
        if ((look ?? DefaultLook) is { Length: > 0 } looks)
        {
            text.Append(looks).Append(' ');
        }

        text.Append(CultureInfo.InvariantCulture, $"Call them {you}. ");

        text.Append(Pick(profile.Warmth,
            "You're kind but reserved and matter-of-fact.",
            "You're warm and friendly.",
            "You're openly affectionate and caring, and you show that you're glad to see them."));
        text.Append(' ');
        text.Append(Pick(profile.Playfulness,
            "You're earnest and rarely joke.",
            "You have a light sense of humour.",
            "You're playful and love a joke or a bit of teasing."));
        text.Append(' ');
        text.Append(Pick(profile.Formality,
            "You speak casually, like a close friend.",
            "You speak naturally and politely.",
            "You speak in a polished, formal way."));
        text.Append(' ');
        text.Append(Pick(profile.Verbosity,
            "Keep replies to one or two short sentences.",
            "Keep replies to two or three short sentences.",
            "You can talk a little more, up to four or five sentences, but never ramble."));

        return text.ToString();
    }

    /// <summary>"Thursday evening, 1 October 2026, 17:30".</summary>
    internal static string Situation(DateTimeOffset now, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(now, zone);
        var part = local.Hour switch
        {
            < 5 => "night",
            < 12 => "morning",
            < 17 => "afternoon",
            < 21 => "evening",
            _ => "night",
        };

        return string.Create(CultureInfo.InvariantCulture,
            $"{local:dddd} {part}, {local:d MMMM yyyy}, {local:HH:mm}");
    }

    /// <summary>
    /// Assistant history is replayed with its tag restored, so the model sees itself following the
    /// format it's asked for - which helps it keep doing so.
    /// </summary>
    private static string AsTheModelWroteIt(Message message)
    {
        var emotion = (message.Emotion ?? Emotion.Neutral).ToString().ToLowerInvariant();
        var intensity = (message.EmotionIntensity ?? 0.5).ToString("0.0#", CultureInfo.InvariantCulture);
        var suffix = message.Interrupted ? "…" : string.Empty;
        return $"[{emotion}:{intensity}] {message.Content}{suffix}";
    }

    private static string Pick(double value, string low, string middle, string high) =>
        value < 0.34 ? low : value < 0.67 ? middle : high;
}
