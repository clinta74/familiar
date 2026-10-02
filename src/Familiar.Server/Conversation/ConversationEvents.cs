using Familiar.Domain.Conversations;
using Familiar.Domain.Expression;

namespace Familiar.Server.Conversation;

// Payloads of the events on the conversation stream (PLAN.md §3.1). Serialised camelCase, enums as
// camelCase names. The event type is the SSE `event:` field, not part of the payload.

public static class EventTypes
{
    public const string Ready = "ready";
    public const string State = "state";
    public const string Message = "message";
    public const string ReplyStart = "reply.start";
    public const string ReplyDelta = "reply.delta";
    public const string ReplyEnd = "reply.end";
    public const string Expression = "expression";
    public const string Gesture = "gesture";
    public const string Error = "turn.error";
    public const string Cleared = "cleared";
    public const string Resync = "resync";
    public const string Audio = "audio";
    public const string AudioEnd = "audio.end";
    public const string ReplyTruncated = "reply.truncated";
    public const string ToolStarted = "tool.started";
    public const string ToolFinished = "tool.finished";
    public const string ToolConfirm = "tool.confirm";
    public const string ToolConfirmed = "tool.confirmed";
    public const string Jobs = "jobs";
    public const string Notify = "notify";
}

public enum FamiliarState
{
    Idle,
    Thinking,
    Speaking,

    /// <summary>The model is unreachable. The familiar still answers sign-in and history; it just can't talk.</summary>
    Resting,
}

public sealed record ReadyEvent(FamiliarSummary Familiar, bool ModelAvailable, bool SpeechAvailable);

public sealed record FamiliarSummary(string Name, double Expressiveness);

public sealed record StateEvent(FamiliarState State);

public sealed record ReplyStartEvent(Guid Id);

public sealed record ReplyDeltaEvent(Guid Id, string Text);

public sealed record ReplyEndEvent(Guid Id, string Text, Emotion Emotion, double Intensity, bool Interrupted);

public sealed record ExpressionEvent(Emotion Emotion, double Intensity);

public sealed record GestureEvent(Gesture Gesture);

public sealed record ErrorEvent(string Code, string Message);

/// <summary>A spoken sentence is ready to fetch.</summary>
/// <param name="ReplyId">The reply it belongs to.</param>
/// <param name="Seq">Its position in the reply; clips are played in this order.</param>
/// <param name="Url">Where to fetch the audio.</param>
/// <param name="Text">What it says.</param>
/// <param name="Words">When each word is spoken, if the TTS service says - for mouth shapes.</param>
public sealed record AudioEvent(Guid ReplyId, int Seq, string Url, string Text, IReadOnlyList<SpokenWord>? Words = null);

public sealed record SpokenWord(string Word, double Start, double End);

/// <summary>
/// The user talked over a reply, so it was cut to what they actually heard (the model had usually
/// finished writing it long before the familiar finished saying it).
/// </summary>
public sealed record ReplyTruncatedEvent(Guid Id, string Text);

/// <summary>The reply has no more clips coming: <c>Clips</c> were announced in total.</summary>
public sealed record AudioEndEvent(Guid ReplyId, int Clips);

public sealed record EmptyEvent;

/// <summary>A timer or reminder went off. The reply that announces it arrives as usual; this is for the chime.</summary>
/// <param name="JobId">The job that fired.</param>
/// <param name="Kind">Timer or reminder.</param>
/// <param name="Label">What it was for.</param>
/// <param name="Text">What the familiar says about it.</param>
public sealed record NotifyEvent(Guid JobId, Familiar.Domain.Scheduling.JobKind Kind, string Label, string Text);

/// <summary>A stored message, as both the history endpoint and the <c>message</c> event show it.</summary>
public sealed record MessageView(
    Guid Id,
    MessageRole Role,
    string Text,
    Emotion? Emotion,
    double? Intensity,
    bool Interrupted,
    DateTimeOffset CreatedAt)
{
    public static MessageView From(Message message) => new(
        message.Id,
        message.Role,
        message.Content,
        message.Emotion,
        message.EmotionIntensity,
        message.Interrupted,
        message.CreatedAt);
}
