using Familiar.Agent.Expression;
using Familiar.Agent.Llm;
using Familiar.Agent.Prompting;
using Familiar.Agent.Turns;
using Familiar.Domain.Conversations;
using Familiar.Domain.Expression;
using Familiar.Domain.Familiars;
using Familiar.Server.Tests.Infrastructure;

namespace Familiar.Server.Tests;

public sealed class ExpressionParserTests
{
    private static List<ExpressionToken> Parse(params string[] fragments)
    {
        var parser = new ExpressionParser();
        var tokens = fragments.SelectMany(parser.Feed).ToList();
        tokens.AddRange(parser.Flush());
        return tokens;
    }

    private static string Spoken(IEnumerable<ExpressionToken> tokens) =>
        string.Concat(tokens.OfType<TextToken>().Select(t => t.Text));

    [Fact]
    public void A_tag_split_across_fragments_is_reassembled_and_never_spoken()
    {
        // Exactly how Ollama streamed it while this was being written.
        var tokens = Parse("[", "joy", ":", "1", ".", "0", "]", " I", " got", " it!");

        Assert.Equal(new EmotionToken(Emotion.Joy, 1.0), tokens[0]);
        Assert.Equal("I got it!", Spoken(tokens));
    }

    [Fact]
    public void Gestures_mid_sentence_are_removed_without_leaving_a_double_space()
    {
        var tokens = Parse("[relaxed:0.4] Sure thing! [gesture:nod] Let's do it.");

        Assert.Contains(new GestureToken(Gesture.Nod), tokens);
        Assert.Equal("Sure thing! Let's do it.", Spoken(tokens));
    }

    [Theory]
    [InlineData("I said [sic] there.")]
    [InlineData("An array [1, 2, 3] of numbers.")]
    [InlineData("A [very long bracketed aside that goes on and on] here.")]
    [InlineData("Unclosed [joy:0.5 never ends")]
    public void Brackets_that_are_not_known_tags_are_spoken_unchanged(string text)
    {
        var tokens = Parse(text);

        Assert.Equal(text, Spoken(tokens));
        Assert.DoesNotContain(tokens, t => t is EmotionToken or GestureToken);
    }

    [Theory]
    [InlineData("[surprised:7]", Emotion.Surprised, 1.0)]
    [InlineData("[sad]", Emotion.Sad, ExpressionParser.DefaultIntensity)]
    [InlineData("[Happy:0.5]", Emotion.Joy, 0.5)]
    [InlineData("[ thinking : 0.3 ]", Emotion.Thinking, 0.3)]
    public void Emotion_tags_are_normalised(string tag, Emotion emotion, double intensity) =>
        Assert.Equal(new EmotionToken(emotion, intensity), ExpressionParser.TryParseTag(tag));

    [Fact]
    public void An_unknown_gesture_is_dropped_never_spoken()
    {
        var tokens = Parse("Ha! [gesture:dance] That's funny.");

        Assert.Equal("Ha! That's funny.", Spoken(tokens));
        Assert.Contains(tokens, t => t is IgnoredTag);
    }

    [Theory]
    [InlineData("[gesture:laugh]", Gesture.Laugh)]
    [InlineData("[gesture:Giggles]", Gesture.Laugh)]
    [InlineData("[laughs]", Gesture.Laugh)]
    [InlineData("[nods]", Gesture.Nod)]
    [InlineData("[gesture:shakes head]", Gesture.Shake)]
    [InlineData("[waves]", Gesture.Wave)]
    [InlineData("[gesture:wink]", Gesture.Wink)]
    [InlineData("[sighs]", Gesture.Sigh)]
    [InlineData("[gesture:yawning]", Gesture.Yawn)]
    [InlineData("[gesture:excited]", Gesture.Excited)]
    [InlineData("[bounces]", Gesture.Excited)]
    public void Gesture_words_the_model_reaches_for_are_understood(string tag, Gesture gesture) =>
        Assert.Equal(new GestureToken(gesture), ExpressionParser.TryParseTag(tag));

    [Fact]
    public void A_known_emotion_with_a_garbled_intensity_is_still_that_emotion() =>
        Assert.Equal(new EmotionToken(Emotion.Joy, ExpressionParser.DefaultIntensity), ExpressionParser.TryParseTag("[joy:lots]"));

    [Fact]
    public void A_numeric_emotion_is_not_mistaken_for_an_enum_value() =>
        Assert.Null(ExpressionParser.TryParseTag("[3:0.5]"));
}

public sealed class PromptBuilderTests
{
    private static readonly FamiliarProfile Profile = new() { Name = "Pip", AccountId = Guid.NewGuid() };

    private static PromptContext Context(IReadOnlyList<Message> history) => new(
        Profile,
        "alice",
        history,
        "What's up?",
        new DateTimeOffset(2026, 10, 1, 22, 30, 0, TimeSpan.Zero),
        TimeZoneInfo.Utc);

    [Fact]
    public void System_then_history_then_the_new_message_with_the_time()
    {
        Message[] history =
        [
            new() { Role = MessageRole.User, Content = "Hi", CreatedAt = default },
            new() { Role = MessageRole.Assistant, Content = "Hello!", Emotion = Emotion.Joy, EmotionIntensity = 0.8, CreatedAt = default },
            new() { Role = MessageRole.Assistant, Content = "As I was say", Interrupted = true, CreatedAt = default },
        ];

        var prompt = PromptBuilder.Build(Context(history));

        Assert.Equal([ChatRole.System, ChatRole.User, ChatRole.Assistant, ChatRole.Assistant, ChatRole.User], prompt.Select(m => m.Role));
        Assert.Contains("You are Pip, alice's familiar", prompt[0].Content, StringComparison.Ordinal);
        Assert.Equal("[joy:0.8] Hello!", prompt[2].Content);
        Assert.Equal("[neutral:0.5] As I was say…", prompt[3].Content);
        Assert.Equal("(Thursday night, 1 October 2026, 22:30) What's up?", prompt[4].Content);
    }

    [Fact]
    public void The_system_message_does_not_change_from_turn_to_turn()
    {
        // The cached prompt prefix depends on it (PLAN.md §4.3).
        var first = PromptBuilder.Build(Context([]))[0];
        var later = PromptBuilder.Build(Context([]) with { Now = DateTimeOffset.UnixEpoch, UserText = "Something else" })[0];

        Assert.Equal(first, later);
    }

    [Fact]
    public void The_nickname_is_used_when_set()
    {
        var persona = PromptBuilder.Persona(new FamiliarProfile { Name = "Pip", UserNickname = "Captain" }, "alice");

        Assert.Contains("Call them Captain.", persona, StringComparison.Ordinal);
        Assert.DoesNotContain("alice", persona, StringComparison.Ordinal);
    }
}

public sealed class TurnRunnerTests
{
    private static async Task<(TurnResult Result, List<TurnEvent> Events)> Run(FakeLlm llm, CancellationToken ct = default)
    {
        var events = new List<TurnEvent>();
        var result = await new TurnRunner(llm).RunAsync([], e =>
        {
            events.Add(e);
            return ValueTask.CompletedTask;
        }, ct);
        return (result, events);
    }

    [Fact]
    public async Task The_first_tag_is_the_replys_emotion()
    {
        var (result, events) = await Run(new FakeLlm { Fragments = ["[joy:0.7] Yes!", " [sad:0.2] Well, mostly."] });

        Assert.Equal((Emotion.Joy, 0.7, true), (result.Emotion, result.Intensity, result.EmotionWasTagged));
        Assert.Equal("Yes! Well, mostly.", result.Text);
        Assert.Equal(2, events.OfType<ExpressionChanged>().Count());
    }

    [Fact]
    public async Task An_untagged_reply_still_gets_an_expression()
    {
        var (result, events) = await Run(new FakeLlm { Fragments = ["I'm so sorry to hear that."] });

        Assert.False(result.EmotionWasTagged);
        Assert.Equal(Emotion.Sad, result.Emotion);
        Assert.Contains(new ExpressionChanged(Emotion.Sad, 0.5), events);
    }

    [Fact]
    public async Task Cancelling_returns_what_was_said_so_far_as_interrupted()
    {
        using var cancel = new CancellationTokenSource();
        var llm = new FakeLlm { Fragments = ["[relaxed:0.5] One.", " Two.", " Three."], FragmentDelay = TimeSpan.FromMilliseconds(50) };
        var events = new List<TurnEvent>();

        var result = await new TurnRunner(llm).RunAsync([], async e =>
        {
            events.Add(e);
            if (e is SpeechDelta)
            {
                await cancel.CancelAsync();
            }
        }, cancel.Token);

        Assert.True(result.Interrupted);
        Assert.Equal("One.", result.Text);
    }

    [Fact]
    public async Task Tags_it_cannot_act_out_are_reported_not_spoken()
    {
        var (result, events) = await Run(new FakeLlm { Fragments = ["[joy:0.6] Ta-da! [gesture:dance] Fun."] });

        Assert.Equal("Ta-da! Fun.", result.Text);
        Assert.Contains(new TagIgnored("[gesture:dance]"), events);
    }

    [Fact]
    public async Task A_model_that_is_down_surfaces_as_unavailable() =>
        await Assert.ThrowsAsync<LlmUnavailableException>(() => Run(new FakeLlm { Down = true }));
}

public sealed class PersonaTests
{
    [Fact]
    public void The_familiar_knows_what_it_looks_like() =>
        Assert.Contains("plush llama", PromptBuilder.Persona(new FamiliarProfile { Name = "Pip" }, "alice"), StringComparison.Ordinal);
}
