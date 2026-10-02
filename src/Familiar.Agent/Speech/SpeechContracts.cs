namespace Familiar.Agent.Speech;

/// <summary>The <c>Speech</c> configuration section: the STT and TTS sidecars (PLAN.md §8).</summary>
public sealed class SpeechOptions
{
    public const string Section = "Speech";

    /// <summary>faster-whisper behind Speaches. 8001 locally; http://stt:8000 on the NAS.</summary>
    public string SttBaseUrl { get; set; } = "http://127.0.0.1:8001";

    /// <summary>
    /// Small and English-only by default: on the GPU it transcribes an utterance in a few hundred
    /// milliseconds and needs well under 1 GB of VRAM, which the NAS budget depends on (§10.3).
    /// </summary>
    public string SttModel { get; set; } = "Systran/faster-distil-whisper-small.en";

    /// <summary>ISO 639-1, or empty to let Whisper detect it (slower, and wrong on short clips).</summary>
    public string SttLanguage { get; set; } = "en";

    /// <summary>Kokoro behind Kokoro-FastAPI. 8002 locally; http://tts:8880 on the NAS.</summary>
    public string TtsBaseUrl { get; set; } = "http://127.0.0.1:8002";

    public string TtsModel { get; set; } = "kokoro";

    public string DefaultVoice { get; set; } = "af_heart";

    /// <summary>MP3: every browser and both phone platforms decode it.</summary>
    public string TtsFormat { get; set; } = "mp3";

    public int RequestTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// At startup, in the background: make sure the STT model is downloaded and load the TTS model,
    /// so the first spoken turn doesn't pay for either.
    /// </summary>
    public bool WarmUpOnStart { get; set; } = true;

    public override string ToString() =>
        $"STT {SttModel} at {SttBaseUrl}; TTS {TtsModel}/{DefaultVoice} ({TtsFormat}) at {TtsBaseUrl}";
}

/// <summary>Synthesised speech, ready to serve.</summary>
public sealed record SpeechClip(byte[] Audio, string ContentType);

/// <summary>A speech service can't be used right now. Voice degrades to text; nothing else breaks.</summary>
public sealed class SpeechUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

public interface ISpeechToText
{
    /// <summary>The words in an utterance, or an empty string if there were none.</summary>
    /// <exception cref="SpeechUnavailableException">The STT service can't be used.</exception>
    Task<string> TranscribeAsync(byte[] audio, string contentType, CancellationToken ct);

    /// <summary>Makes sure the configured model is installed, downloading it if not.</summary>
    /// <returns>True if it had to be downloaded.</returns>
    /// <exception cref="SpeechUnavailableException">The STT service can't be used.</exception>
    Task<bool> EnsureModelAsync(CancellationToken ct);

    Task<bool> IsAvailableAsync(CancellationToken ct);
}

public interface ITextToSpeech
{
    /// <exception cref="SpeechUnavailableException">The TTS service can't be used.</exception>
    Task<SpeechClip> SynthesizeAsync(string text, string voice, double speed, CancellationToken ct);

    /// <summary>The voices the TTS service offers; empty if it can't be reached.</summary>
    Task<IReadOnlyList<string>> VoicesAsync(CancellationToken ct);
}
