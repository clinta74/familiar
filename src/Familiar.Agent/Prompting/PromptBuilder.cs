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
    TimeZoneInfo TimeZone);

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
/// </remarks>
public static class PromptBuilder
{
    internal const string Rules = """
        You speak aloud through an animated avatar, so every reply is spoken. Talk the way a person
        talks: plain words, no markdown, lists, emoji, or stage directions.

        Begin each spoken reply with exactly one emotion tag, [emotion:intensity], where emotion is
        one of joy, sad, angry, surprised, relaxed, neutral, thinking and intensity is between 0 and 1.
        Choose the emotion that fits how you feel about what you're saying, not just the user's mood.
        Where it fits naturally you may add one gesture tag, [gesture:name], with name one of nod,
        shake, tilt, wave, shrug. Tags are never read aloud.
        Example: [joy:0.7] That's wonderful news! [gesture:nod] You earned it.

        Each user message begins with the current local time in parentheses. It's context for you,
        not something the user said; don't repeat it unless it matters.
        A previous reply of yours ending in "…" was cut off by the user before you finished.
        """;

    public static IReadOnlyList<ChatMessage> Build(PromptContext context)
    {
        var messages = new List<ChatMessage>(context.History.Count + 2)
        {
            new(ChatRole.System, $"{Persona(context.Profile, context.Username)}\n\n{Rules}"),
        };

        foreach (var message in context.History)
        {
            messages.Add(message.Role == MessageRole.User
                ? new ChatMessage(ChatRole.User, message.Content)
                : new ChatMessage(ChatRole.Assistant, AsTheModelWroteIt(message)));
        }

        messages.Add(new ChatMessage(ChatRole.User, $"({Situation(context.Now, context.TimeZone)}) {context.UserText}"));
        return messages;
    }

    /// <summary>
    /// The persona, from the profile's sliders. Rendered from fixed phrases, never from free text,
    /// so it can't carry instructions, and it only changes when the user changes a setting.
    /// </summary>
    internal static string Persona(FamiliarProfile profile, string username)
    {
        var you = string.IsNullOrWhiteSpace(profile.UserNickname) ? username : profile.UserNickname.Trim();
        var text = new StringBuilder();

        text.Append(CultureInfo.InvariantCulture,
            $"You are {profile.Name}, {you}'s familiar: a small, friendly companion who lives on their devices ");
        text.Append("and keeps them company, chats with them, and helps out with everyday things. ");
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

    /// <summary>"Wednesday evening, 1 October 2026, 17:30".</summary>
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
