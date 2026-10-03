using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Familiar.Agent.Speech;
using Familiar.Server.Conversation;
using Familiar.Server.Familiars;
using Familiar.Server.Tests.Infrastructure;

namespace Familiar.Server.Tests;

public sealed class SentenceChunkerTests
{
    private static List<string> Chunk(params string[] fragments)
    {
        var chunker = new SentenceChunker();
        var sentences = fragments.SelectMany(chunker.Add).ToList();
        if (chunker.Flush() is { } last)
        {
            sentences.Add(last);
        }

        return sentences;
    }

    [Fact]
    public void A_sentence_is_released_once_the_next_one_starts()
    {
        var chunker = new SentenceChunker();

        Assert.Empty(chunker.Add("That's wonderful news."));
        Assert.Equal(["That's wonderful news."], chunker.Add(" Tell me"));
        Assert.Equal("Tell me", chunker.Flush());
    }

    [Fact]
    public void Streamed_fragments_become_whole_sentences()
    {
        Assert.Equal(
            ["Oh my gosh, that's amazing!", "I'm so happy for you.", "When's the big day?"],
            Chunk("Oh my", " gosh, that's", " amazing! I'm so", " happy for you. When's", " the big day?"));
    }

    [Fact]
    public void Very_short_sentences_are_joined_to_the_next()
    {
        Assert.Equal(["Oh! That's lovely to hear."], Chunk("Oh! That's lovely to hear."));
    }

    [Theory]
    [InlineData("Dr. Smith said it's fine.")]
    [InlineData("It costs 3.50 at the shop.")]
    [InlineData("Bring snacks, drinks, etc. for everyone.")]
    public void Abbreviations_and_decimals_do_not_end_a_sentence(string text) =>
        Assert.Equal([text], Chunk(text));

    [Fact]
    public void A_long_run_without_an_ending_is_cut_at_a_comma()
    {
        var clause = "and then we walked a little further along the river";
        var sentences = Chunk(string.Join(", ", Enumerable.Repeat(clause, 6)) + ".");

        Assert.True(sentences.Count > 1);
        Assert.All(sentences, s => Assert.True(s.Length <= 181, s));
    }

    [Fact]
    public void Emoji_and_markdown_are_not_spoken() =>
        Assert.Equal("That's great! Well done.", SpeechText.Clean("That's *great*! 🎉 Well done."));
}

[Collection(PostgresCollection.Name)]
public sealed class VoiceTests(PostgresFixture postgres)
{
    private static async Task<HttpClient> SignedInAsync(FamiliarAppFactory app)
    {
        var client = app.CreateClient();
        (await client.RegisterAsync("speaker")).EnsureSuccessStatusCode();
        return client;
    }

    private static Task<HttpResponseMessage> SpeakAsync(HttpClient client, string contentType = "audio/webm;codecs=opus")
    {
        var body = new ByteArrayContent(Encoding.UTF8.GetBytes("pretend this is opus"));
        body.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        return client.PostAsync("/api/conversation/audio?timeZone=Europe/London", body);
    }

    [Fact]
    public async Task A_spoken_message_is_transcribed_and_answered_aloud_in_order()
    {
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync());
        app.Llm.Fragments = ["[joy:0.8] What a lovely", " thing to hear!", " Tell me everything", " about it."];
        app.Stt.Transcript = "I got the job";
        var client = await SignedInAsync(app);
        await using var stream = await SseClient.ConnectAsync(client);
        Assert.True((await stream.ReadUntilAsync(EventTypes.Ready))[^1].Payload<ReadyEvent>().SpeechAvailable);

        var response = await SpeakAsync(client);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("audio/webm; codecs=opus", app.Stt.LastContentType);

        var events = await stream.ReadUntilAsync(EventTypes.AudioEnd);
        Assert.Equal("I got the job", events.First(e => e.Type == EventTypes.Message).Payload<MessageView>().Text);

        var clips = events.Where(e => e.Type == EventTypes.Audio).Select(e => e.Payload<AudioEvent>()).ToList();
        Assert.Equal(["What a lovely thing to hear!", "Tell me everything about it."], clips.Select(c => c.Text));
        Assert.Equal([0, 1], clips.Select(c => c.Seq));
        Assert.Equal(2, events[^1].Payload<AudioEndEvent>().Clips);

        // Each clip is fetchable, and is what it says.
        var audio = await client.GetAsync(clips[1].Url);
        Assert.Equal("audio/mpeg", audio.Content.Headers.ContentType?.MediaType);
        Assert.Equal("Tell me everything about it.", await audio.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Typed_messages_are_only_spoken_when_asked()
    {
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync());
        var client = await SignedInAsync(app);
        await using var stream = await SseClient.ConnectAsync(client);
        await stream.ReadUntilAsync(EventTypes.Ready);

        await client.PostAsJsonAsync("/api/conversation/messages", new SendMessageRequest("Quietly", null));
        var silent = await stream.ReadUntilStateAsync(FamiliarState.Idle);
        Assert.DoesNotContain(silent, e => e.Type == EventTypes.Audio);

        await client.PostAsJsonAsync("/api/conversation/messages", new SendMessageRequest("Out loud", null, Speak: true));
        var spoken = await stream.ReadUntilStateAsync(FamiliarState.Idle);
        Assert.Contains(spoken, e => e.Type == EventTypes.Audio);
    }

    [Fact]
    public async Task The_familiars_voice_and_speed_are_used()
    {
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync());
        var client = await SignedInAsync(app);
        (await client.PutAsJsonAsync("/api/familiar", new FamiliarView("Pip", null, 0.5, 0.5, 0.5, 0.5, 0.5, "am_michael", 9)))
            .EnsureSuccessStatusCode();
        await using var stream = await SseClient.ConnectAsync(client);
        await stream.ReadUntilAsync(EventTypes.Ready);

        await SpeakAsync(client);
        await stream.ReadUntilAsync(EventTypes.AudioEnd);

        Assert.All(app.Tts.Requests, r => Assert.Equal(("am_michael", 1.4), (r.Voice, r.Speed)));
    }

    [Fact]
    public async Task Silence_is_not_a_message()
    {
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync());
        app.Stt.Transcript = "";
        var client = await SignedInAsync(app);
        await using var stream = await SseClient.ConnectAsync(client);
        await stream.ReadUntilAsync(EventTypes.Ready);

        Assert.Equal(HttpStatusCode.NoContent, (await SpeakAsync(client)).StatusCode);

        var events = await stream.ReadUntilStateAsync(FamiliarState.Idle);
        Assert.Equal("no-speech", events.Single(e => e.Type == EventTypes.Error).Payload<ErrorEvent>().Code);
        Assert.Empty(await (await client.GetAsync("/api/conversation/messages")).ReadAsync<List<MessageView>>());
    }

    [Fact]
    public async Task When_tts_is_down_the_reply_still_arrives_as_text()
    {
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync());
        app.Tts.Down = true;
        var client = await SignedInAsync(app);
        await using var stream = await SseClient.ConnectAsync(client);
        Assert.False((await stream.ReadUntilAsync(EventTypes.Ready))[^1].Payload<ReadyEvent>().SpeechAvailable);

        await SpeakAsync(client);
        var events = await stream.ReadUntilStateAsync(FamiliarState.Idle);

        Assert.Equal("voice", events.Single(e => e.Type == EventTypes.Error).Payload<ErrorEvent>().Code);
        Assert.Contains(events, e => e.Type == EventTypes.ReplyEnd);
        Assert.DoesNotContain(events, e => e.Type == EventTypes.Audio);
    }

    [Fact]
    public async Task When_stt_is_down_the_upload_says_so()
    {
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync());
        app.Stt.Down = true;
        var client = await SignedInAsync(app);
        await using var stream = await SseClient.ConnectAsync(client);
        await stream.ReadUntilAsync(EventTypes.Ready);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await SpeakAsync(client)).StatusCode);

        // It went "thinking" when the recording arrived; it mustn't stay that way.
        var events = await stream.ReadUntilStateAsync(FamiliarState.Idle);
        Assert.Contains(events, e => e.Type == EventTypes.State && e.Payload<StateEvent>().State == FamiliarState.Thinking);
    }

    [Fact]
    public async Task Only_audio_bodies_are_accepted()
    {
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync());
        var client = await SignedInAsync(app);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await SpeakAsync(client, "text/plain")).StatusCode);
    }

    [Fact]
    public async Task Clips_belong_to_their_account()
    {
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync());
        var owner = await SignedInAsync(app);
        var stranger = app.CreateClient();
        (await stranger.RegisterAsync("stranger")).EnsureSuccessStatusCode();

        await using var stream = await SseClient.ConnectAsync(owner);
        await stream.ReadUntilAsync(EventTypes.Ready);
        await SpeakAsync(owner);
        var clip = (await stream.ReadUntilAsync(EventTypes.Audio))[^1].Payload<AudioEvent>();

        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync(clip.Url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync(clip.Url)).StatusCode);
    }

    [Fact]
    public async Task Voices_are_listed_and_previewable()
    {
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync());
        var client = await SignedInAsync(app);

        var voices = await (await client.GetAsync("/api/familiar/voices")).ReadAsync<VoicesView>();
        Assert.Equal(["af_heart", "am_michael"], voices.Voices);

        var preview = await client.GetAsync("/api/familiar/voice-preview?voice=am_michael&speed=1.2");
        Assert.Equal("audio/mpeg", preview.Content.Headers.ContentType?.MediaType);
        Assert.Contains(app.Tts.Requests, r => r is { Voice: "am_michael", Speed: 1.2 });
    }
}

public sealed class KokoroClientTests
{
    private sealed class Stub(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
    }

    private static Task<IReadOnlyList<string>> VoicesFrom(string json) =>
        new KokoroTextToSpeech(new HttpClient(new Stub(json)) { BaseAddress = new Uri("http://tts/") }, new SpeechOptions())
            .VoicesAsync(CancellationToken.None);

    [Fact]
    public async Task Voices_are_read_from_bare_ids() =>
        Assert.Equal(["af_heart", "bm_george"], await VoicesFrom("""{"voices":["bm_george","af_heart"]}"""));

    [Fact]
    public async Task Voices_are_read_from_voice_objects() =>
        Assert.Equal(["af_alloy", "af_heart"], await VoicesFrom("""{"voices":[{"id":"af_heart","overall_grade":"A"},{"id":"af_alloy"}]}"""));

    [Fact]
    public async Task An_unexpected_shape_means_no_voices_not_an_exception() =>
        Assert.Empty(await VoicesFrom("""{"voices":[42]}"""));
}

public sealed class EchoDetectorTests
{
    private const string Reply = "Oh my gosh, that is the best news ever! I'm so happy for you.";

    [Theory]
    [InlineData("that is the best news ever")]
    [InlineData("Oh my gosh, that is the best news ever.")]
    [InlineData("I'm so happy for you")]
    public void Its_own_words_are_echo(string transcript) => Assert.True(EchoDetector.IsEcho(transcript, Reply));

    [Theory]
    [InlineData("What's the weather like tomorrow?")]
    [InlineData("thanks")]
    [InlineData("is that the best you've got though")]
    public void The_users_words_are_not(string transcript) => Assert.False(EchoDetector.IsEcho(transcript, Reply));

    [Fact]
    public void Nothing_said_yet_means_nothing_to_echo() => Assert.False(EchoDetector.IsEcho("hello there friend", null));
}

[Collection(PostgresCollection.Name)]
public sealed class BargeInTests(PostgresFixture postgres)
{
    private static async Task<HttpClient> SignedInAsync(FamiliarAppFactory app, string name = "barger")
    {
        var client = app.CreateClient();
        (await client.RegisterAsync(name)).EnsureSuccessStatusCode();
        return client;
    }

    [Fact]
    public async Task Audio_events_carry_word_timings()
    {
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync());
        var client = await SignedInAsync(app);
        await using var stream = await SseClient.ConnectAsync(client);
        await stream.ReadUntilAsync(EventTypes.Ready);

        await client.PostAsJsonAsync("/api/conversation/messages", new SendMessageRequest("Hi", null, Speak: true));
        var clip = (await stream.ReadUntilAsync(EventTypes.Audio))[^1].Payload<AudioEvent>();

        Assert.Equal(["Hello", "there!"], clip.Words!.Select(w => w.Word));
        Assert.Equal(0.3, clip.Words![1].Start, 3);
    }

    [Fact]
    public async Task Talking_over_a_reply_cuts_it_to_what_was_heard()
    {
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync());
        app.Llm.Fragments = ["[joy:0.7] Llamas hum to each other.", " They also spit when annoyed.", " And they're great guards."];
        var client = await SignedInAsync(app);
        await using var stream = await SseClient.ConnectAsync(client);
        await stream.ReadUntilAsync(EventTypes.Ready);

        await client.PostAsJsonAsync("/api/conversation/messages", new SendMessageRequest("Llama facts?", null, Speak: true));
        var replyId = (await stream.ReadUntilAsync(EventTypes.ReplyEnd))[^1].Payload<ReplyEndEvent>().Id;
        await stream.ReadUntilStateAsync(FamiliarState.Idle);

        // The reply is fully written and stored, but the user only heard the first sentence and a bit.
        (await client.PostAsJsonAsync("/api/conversation/interrupt", new InterruptRequest(replyId, "Llamas hum to each other. They also")))
            .EnsureSuccessStatusCode();

        var truncated = (await stream.ReadUntilAsync(EventTypes.ReplyTruncated))[^1].Payload<ReplyTruncatedEvent>();
        Assert.Equal("Llamas hum to each other. They also", truncated.Text);

        var stored = (await (await client.GetAsync("/api/conversation/messages")).ReadAsync<List<MessageView>>())[^1];
        Assert.Equal(("Llamas hum to each other. They also", true), (stored.Text, stored.Interrupted));
    }

    [Fact]
    public async Task An_interrupt_without_a_body_still_just_stops()
    {
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync());
        var client = await SignedInAsync(app);

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/conversation/interrupt", null)).StatusCode);
    }

    [Fact]
    public async Task Someone_elses_reply_cannot_be_truncated()
    {
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync());
        var owner = await SignedInAsync(app, "owner");
        var other = await SignedInAsync(app, "other");
        await using var stream = await SseClient.ConnectAsync(owner);
        await stream.ReadUntilAsync(EventTypes.Ready);
        await owner.PostAsJsonAsync("/api/conversation/messages", new SendMessageRequest("Hi", null));
        var replyId = (await stream.ReadUntilAsync(EventTypes.ReplyEnd))[^1].Payload<ReplyEndEvent>().Id;
        await stream.ReadUntilStateAsync(FamiliarState.Idle);

        await other.PostAsJsonAsync("/api/conversation/interrupt", new InterruptRequest(replyId, "He"));

        var stored = (await (await owner.GetAsync("/api/conversation/messages")).ReadAsync<List<MessageView>>())[^1];
        Assert.Equal("Hello there!", stored.Text);
    }

    [Fact]
    public async Task The_familiar_ignores_its_own_voice()
    {
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync());
        app.Llm.Fragments = ["[joy:0.8] That is wonderful news, I'm so happy for you!"];
        var client = await SignedInAsync(app);
        await using var stream = await SseClient.ConnectAsync(client);
        await stream.ReadUntilAsync(EventTypes.Ready);
        await client.PostAsJsonAsync("/api/conversation/messages", new SendMessageRequest("I passed!", null));
        await stream.ReadUntilStateAsync(FamiliarState.Idle);

        // The mic picked up the speakers.
        app.Stt.Transcript = "that is wonderful news I'm so happy for you";
        var body = new ByteArrayContent([1, 2, 3]);
        body.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        var response = await client.PostAsync("/api/conversation/audio", body);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(2, (await (await client.GetAsync("/api/conversation/messages")).ReadAsync<List<MessageView>>()).Count);
    }
}
