using System.Threading.Channels;
using Familiar.Agent.Speech;

namespace Familiar.Server.Conversation;

/// <summary>
/// One reply's voice: sentences go in as the model writes them, and come out as audio clips
/// announced on the event stream, in order (PLAN.md §3.2 step 6).
/// </summary>
/// <remarks>
/// <para>
/// A single worker synthesises one sentence at a time. Kokoro on the GPU is many times faster than
/// real time, so sequential synthesis stays ahead of playback, and order needs no reassembly.
/// </para>
/// <para>
/// If TTS fails, the reply carries on as text: one <c>turn.error</c> says the voice is unavailable
/// and the rest of the sentences are dropped rather than retried into a dead service.
/// </para>
/// </remarks>
public sealed class VoicePipeline
{
    private readonly SentenceChunker _chunker = new();
    private readonly Channel<string> _sentences = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _worker;

    public VoicePipeline(
        ITextToSpeech tts,
        SpeechClipStore clips,
        AccountChannel channel,
        Guid accountId,
        Guid replyId,
        string voice,
        double speed,
        TimeProvider clock,
        ILogger logger,
        CancellationToken ct)
    {
        _worker = Task.Run(async () =>
        {
            var sequence = 0;
            var failed = false;

            await foreach (var sentence in _sentences.Reader.ReadAllAsync(ct))
            {
                var text = SpeechText.Clean(sentence);
                if (failed || text.Length == 0)
                {
                    continue;
                }

                try
                {
                    var started = clock.GetTimestamp();
                    var clip = await tts.SynthesizeAsync(text, voice, speed, ct);
                    var clipId = clips.Add(accountId, clip);

                    FirstAudioAt ??= clock.GetTimestamp();
                    logger.SentenceSynthesized(text.Length, (long)clock.GetElapsedTime(started).TotalMilliseconds);

                    channel.Publish(EventTypes.Audio, new AudioEvent(replyId, sequence++, $"/api/conversation/audio/{clipId}", text));
                }
                catch (SpeechUnavailableException exception)
                {
                    failed = true;
                    logger.SpeechUnavailable(exception.Message);
                    channel.Publish(EventTypes.Error, new ErrorEvent("voice", "My voice isn't working right now, so I'll answer in text."));
                }
            }

            if (sequence > 0)
            {
                channel.Publish(EventTypes.AudioEnd, new AudioEndEvent(replyId, sequence));
            }
        }, CancellationToken.None);
    }

    /// <summary>When the first clip was ready, as a <see cref="TimeProvider"/> timestamp; null until then.</summary>
    public long? FirstAudioAt { get; private set; }

    public void Add(string text)
    {
        foreach (var sentence in _chunker.Add(text))
        {
            _sentences.Writer.TryWrite(sentence);
        }
    }

    /// <summary>The reply is complete: speak what's left, and wait until every clip is announced.</summary>
    public async Task CompleteAsync()
    {
        if (_chunker.Flush() is { } last)
        {
            _sentences.Writer.TryWrite(last);
        }

        _sentences.Writer.TryComplete();

        try
        {
            await _worker;
        }
        catch (OperationCanceledException)
        {
            // Interrupted: the remaining sentences are simply not spoken.
        }
    }
}
