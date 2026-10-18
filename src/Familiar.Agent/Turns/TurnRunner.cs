using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Familiar.Agent.Expression;
using Familiar.Agent.Llm;
using Familiar.Agent.Tools;
using Familiar.Domain.Expression;

namespace Familiar.Agent.Turns;

public abstract record TurnEvent;

/// <summary>Spoken text, tags removed.</summary>
public sealed record SpeechDelta(string Text) : TurnEvent;

public sealed record ExpressionChanged(Emotion Emotion, double Intensity) : TurnEvent;

public sealed record GestureMade(Gesture Gesture) : TurnEvent;

/// <summary>A sound effect, at most one per reply: more than that stops being charming.</summary>
public sealed record SoundMade(SoundEffect Sound) : TurnEvent;

/// <summary>
/// A tag the model wrote that means nothing we can show (an unknown gesture). Dropped from the reply;
/// reported so the gestures the model keeps reaching for can be added.
/// </summary>
public sealed record TagIgnored(string Tag) : TurnEvent;

/// <summary>The reply shows the user a page its tools found, by its number (BACKLOG item 34).</summary>
public sealed record LinkShared(int Number) : TurnEvent;

/// <summary>A tool is about to run.</summary>
public sealed record ToolStarted(string CallId, string Tool) : TurnEvent;

/// <summary>A tool has run; <c>Summary</c> is what the user sees ("Timer: pasta, 10 minutes").</summary>
public sealed record ToolFinished(string CallId, string Tool, bool Ok, string? Summary) : TurnEvent;

/// <summary>How a turn ended.</summary>
/// <param name="Text">Everything that was said, tags removed.</param>
/// <param name="Emotion">The reply's emotion: its first tag, or the fallback if it had none.</param>
/// <param name="Intensity">0-1, before the user's expressiveness setting is applied.</param>
/// <param name="EmotionWasTagged">False when <paramref name="Emotion"/> came from the fallback.</param>
/// <param name="Interrupted">The turn was cancelled before the model finished.</param>
public sealed record TurnResult(string Text, Emotion Emotion, double Intensity, bool EmotionWasTagged, bool Interrupted);

/// <summary>
/// Runs one reply: streams it from the model, splits speech from expression, runs any tools the model
/// calls and gives it their results, and reports all of it as it happens.
/// </summary>
/// <remarks>
/// <para>
/// Cancellation is how barge-in works (PLAN.md §3.2): the turn stops where it is and returns what was
/// said so far, marked interrupted, rather than throwing - an interrupted reply is a normal outcome,
/// and the caller still has to store it.
/// </para>
/// <para>
/// Tools (PLAN.md §6.1): when the model asks for tools, they're run, their results appended, and the
/// model streams again - up to <see cref="MaxToolRounds"/> times, so a confused model can't loop.
/// Anything it said before calling a tool ("Let me set that…") is part of the reply.
/// </para>
/// <para>
/// A reply that says it's doing something with no tool called ("I'm setting the lamp to fifty
/// percent") gets one more round, told nothing happened: the model does that now and then however
/// the prompt words it (BACKLOG item 30), and the user would believe it. By then the claim has been
/// spoken; what follows is the tool really running, or the model owning up.
/// </para>
/// </remarks>
public sealed partial class TurnRunner(ILlmClient llm)
{
    public const int MaxToolRounds = 4;

    /// <summary>What the model is told when it said it did something without calling a tool.</summary>
    public const string ClaimedNote =
        "You just said you'd do that, but you didn't call a tool, so nothing has happened. Call the tool now. " +
        "If none of your tools can do it, say so plainly instead.";

    // Results are read by the model, not a browser: "didn't", not "didn't".
    private static readonly JsonSerializerOptions ToolJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <exception cref="LlmUnavailableException">The model couldn't be used.</exception>
    public async Task<TurnResult> RunAsync(
        IReadOnlyList<ChatMessage> prompt,
        Func<TurnEvent, ValueTask> emit,
        CancellationToken ct,
        IToolExecutor? tools = null,
        ToolContext? toolContext = null)
    {
        var parser = new ExpressionParser();
        var spoken = new StringBuilder();
        (Emotion Emotion, double Intensity)? first = null;
        var interrupted = false;
        var sounded = false;

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
                    case SoundToken sound when !sounded:
                        sounded = true;
                        await emit(new SoundMade(sound.Sound));
                        break;
                    case SoundToken:
                        break;
                    case IgnoredTag ignored:
                        await emit(new TagIgnored(ignored.Tag));
                        break;
                    case LinkToken link:
                        await emit(new LinkShared(link.Number));
                        break;
                }
            }
        }

        var messages = prompt.ToList();
        var toolsRan = false;
        var nudged = false;

        try
        {
            var definitions = tools is not null && toolContext is not null ? await tools.DefinitionsAsync(toolContext, ct) : null;

            for (var round = 0; round <= MaxToolRounds; round++)
            {
                var calls = new List<ToolCall>();
                var raw = new StringBuilder();

                // The last round gets no tools: the model has to say something.
                var offered = round < MaxToolRounds ? definitions : null;

                await foreach (var chunk in llm.StreamAsync(messages, offered, ct))
                {
                    switch (chunk)
                    {
                        case TextChunk fragment:
                            raw.Append(fragment.Text);
                            await Handle(parser.Feed(fragment.Text));
                            break;
                        case ToolCallsChunk requested:
                            calls.AddRange(requested.Calls);
                            break;
                    }
                }

                // Said it did something, called nothing: once, it's told so and goes again.
                if (calls.Count == 0 && offered is not null && !toolsRan && !nudged && ClaimsToAct(raw.ToString()))
                {
                    nudged = true;
                    messages.Add(new ChatMessage(ChatRole.Assistant, raw.ToString()));
                    messages.Add(new ChatMessage(ChatRole.System, ClaimedNote));
                    if (spoken.Length > 0 && !char.IsWhiteSpace(spoken[^1]))
                    {
                        await Handle(parser.Feed(" "));
                    }

                    continue;
                }

                // Calls on the last round (offered nothing) are ignored: it's past time to answer.
                if (calls.Count == 0 || offered is null || tools is null || toolContext is null)
                {
                    break;
                }

                toolsRan = true;

                messages.Add(new ChatMessage(ChatRole.Assistant, raw.ToString(), calls));
                foreach (var call in calls)
                {
                    await emit(new ToolStarted(call.Id, call.Name));
                    var result = await tools.ExecuteAsync(call, toolContext, ct);
                    await emit(new ToolFinished(call.Id, call.Name, result.Ok, result.Summary));
                    messages.Add(new ChatMessage(ChatRole.Tool, result.Data.ToJsonString(ToolJson), ToolName: call.Name, ToolCallId: call.Id));
                }

                // A sentence before the tool call and one after shouldn't run together.
                if (spoken.Length > 0 && !char.IsWhiteSpace(spoken[^1]))
                {
                    await Handle(parser.Feed(" "));
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            interrupted = true;
        }

        await Handle(parser.Flush());

        var text = spoken.ToString().Trim();

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

    /// <summary>
    /// Whether a reply says the familiar is doing, or has done, something only a tool could: "I've
    /// turned on", "I'm setting", "I'll remind you". Not "I can't", nor what the user did.
    /// </summary>
    public static bool ClaimsToAct(string reply) => Claim().IsMatch(reply);

    [GeneratedRegex(
        @"\bI(?:'ve|’ve| have|'m|’m| am|'ll|’ll| will)\s+(?:just\s+|now\s+|gone\s+and\s+|also\s+)?" +
        @"(?:turn|switch|set|chang|dim|brighten|light|lock|unlock|open|clos|shut|start|stop|paus|play|resum|" +
        @"add|creat|put|schedul|remind|cancel|delet|remov|send|text|call|book|mov|adjust|rais|lower|boil)",
        RegexOptions.IgnoreCase)]
    private static partial Regex Claim();
}
