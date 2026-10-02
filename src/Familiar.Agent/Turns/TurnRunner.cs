using System.Text;
using Familiar.Agent.Expression;
using Familiar.Agent.Llm;
using Familiar.Domain.Expression;

namespace Familiar.Agent.Turns;

public abstract record TurnEvent;

/// <summary>Spoken text, tags removed.</summary>
public sealed record SpeechDelta(string Text) : TurnEvent;

public sealed record ExpressionChanged(Emotion Emotion, double Intensity) : TurnEvent;

public sealed record GestureMade(Gesture Gesture) : TurnEvent;

/// <summary>How a turn ended.</summary>
/// <param name="Text">Everything that was said, tags removed.</param>
/// <param name="Emotion">The reply's emotion: its first tag, or the fallback if it had none.</param>
/// <param name="Intensity">0-1, before the user's expressiveness setting is applied.</param>
/// <param name="EmotionWasTagged">False when <paramref name="Emotion"/> came from the fallback.</param>
/// <param name="Interrupted">The turn was cancelled before the model finished.</param>
public sealed record TurnResult(string Text, Emotion Emotion, double Intensity, bool EmotionWasTagged, bool Interrupted);

/// <summary>
/// Runs one reply: streams it from the model, splits speech from expression, and reports both as
/// they happen.
/// </summary>
/// <remarks>
/// Cancellation is how barge-in works (PLAN.md §3.2): the turn stops where it is and returns what was
/// said so far, marked interrupted, rather than throwing - an interrupted reply is a normal outcome,
/// and the caller still has to store it.
/// </remarks>
public sealed class TurnRunner(ILlmClient llm)
{
    /// <exception cref="LlmUnavailableException">The model couldn't be used.</exception>
    public async Task<TurnResult> RunAsync(
        IReadOnlyList<ChatMessage> prompt, Func<TurnEvent, ValueTask> emit, CancellationToken ct)
    {
        var parser = new ExpressionParser();
        var spoken = new StringBuilder();
        (Emotion Emotion, double Intensity)? first = null;
        var interrupted = false;

        async ValueTask Handle(IReadOnlyList<ExpressionToken> tokens)
        {
            foreach (var token in tokens)
            {
                switch (token)
                {
                    case TextToken text:
                        spoken.Append(text.Text);
                        await emit(new SpeechDelta(text.Text));
                        break;
                    case EmotionToken emotion:
                        first ??= (emotion.Emotion, emotion.Intensity);
                        await emit(new ExpressionChanged(emotion.Emotion, emotion.Intensity));
                        break;
                    case GestureToken gesture:
                        await emit(new GestureMade(gesture.Gesture));
                        break;
                }
            }
        }

        try
        {
            await foreach (var fragment in llm.StreamAsync(prompt, ct))
            {
                await Handle(parser.Feed(fragment));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            interrupted = true;
        }

        await Handle(parser.Flush());

        var text = spoken.ToString().TrimEnd();

        if (first is { } tagged)
        {
            return new TurnResult(text, tagged.Emotion, tagged.Intensity, EmotionWasTagged: true, interrupted);
        }

        var (emotion, intensity) = EmotionFallback.For(text);
        if (!interrupted)
        {
            await emit(new ExpressionChanged(emotion, intensity));
        }

        return new TurnResult(text, emotion, intensity, EmotionWasTagged: false, interrupted);
    }
}
