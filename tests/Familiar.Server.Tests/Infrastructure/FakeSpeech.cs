using System.Collections.Concurrent;
using System.Text;
using Familiar.Agent.Speech;

namespace Familiar.Server.Tests.Infrastructure;

/// <summary>Speech-to-text that "hears" whatever the test says it will.</summary>
public sealed class FakeSpeechToText : ISpeechToText
{
    public string Transcript { get; set; } = "Hello from my microphone";

    public bool Down { get; set; }

    public byte[]? LastAudio { get; private set; }

    public string? LastContentType { get; private set; }

    public Task<string> TranscribeAsync(byte[] audio, string contentType, CancellationToken ct)
    {
        LastAudio = audio;
        LastContentType = contentType;
        return Down ? throw new SpeechUnavailableException("fake STT is down") : Task.FromResult(Transcript);
    }

    public Task<bool> EnsureModelAsync(CancellationToken ct) => Task.FromResult(false);

    public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(!Down);
}

/// <summary>Text-to-speech whose "audio" is the UTF-8 of what it was asked to say, so tests can read it back.</summary>
public sealed class FakeTextToSpeech : ITextToSpeech
{
    public bool Down { get; set; }

    public ConcurrentQueue<(string Text, string Voice, double Speed)> Requests { get; } = new();

    public Task<SpeechClip> SynthesizeAsync(string text, string voice, double speed, CancellationToken ct)
    {
        if (Down)
        {
            throw new SpeechUnavailableException("fake TTS is down");
        }

        Requests.Enqueue((text, voice, speed));

        // A word every 0.3 s, like Kokoro's captioned endpoint.
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select((w, i) => new WordTiming(w, i * 0.3, (i + 1) * 0.3))
            .ToList();
        return Task.FromResult(new SpeechClip(Encoding.UTF8.GetBytes(text), "audio/mpeg", words));
    }

    public Task<IReadOnlyList<string>> VoicesAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>(Down ? [] : ["af_heart", "am_michael"]);
}
