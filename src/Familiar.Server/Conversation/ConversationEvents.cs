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
}

public enum FamiliarState
{
    Idle,
    Thinking,
    Speaking,

    /// <summary>The model is unreachable. The familiar still answers sign-in and history; it just can't talk.</summary>
    Resting,
}

public sealed record ReadyEvent(FamiliarSummary Familiar, bool ModelAvailable);

public sealed record FamiliarSummary(string Name, double Expressiveness);

public sealed record StateEvent(FamiliarState State);

public sealed record ReplyStartEvent(Guid Id);

public sealed record ReplyDeltaEvent(Guid Id, string Text);

public sealed record ReplyEndEvent(Guid Id, string Text, Emotion Emotion, double Intensity, bool Interrupted);

public sealed record ExpressionEvent(Emotion Emotion, double Intensity);

public sealed record GestureEvent(Gesture Gesture);

public sealed record ErrorEvent(string Code, string Message);

public sealed record EmptyEvent;

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
