using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Familiar.Agent.Speech;

/// <summary>
/// Text-to-speech through Kokoro-FastAPI. With word timings on, it uses Kokoro's
/// <c>/dev/captioned_speech</c> (audio plus per-word timestamps, PLAN.md §5.4); otherwise, or if that
/// endpoint isn't there, the OpenAI-compatible <c>/v1/audio/speech</c>.
/// </summary>
public sealed class KokoroTextToSpeech(HttpClient http, SpeechOptions options) : ITextToSpeech
{
    // Set once a server turns out not to have the captioned endpoint, so every later sentence
    // doesn't pay a 404 first. Static: the typed client is transient, the server isn't.
    private static volatile bool _captionsUnsupported;

    public async Task<SpeechClip> SynthesizeAsync(string text, string voice, double speed, CancellationToken ct)
    {
        if (options.TtsWordTimings && !_captionsUnsupported)
        {
            var captioned = await TryCaptionedAsync(text, voice, speed, ct);
            if (captioned is not null)
            {
                return captioned;
            }
        }

        using var response = await PostAsync("v1/audio/speech", Body(text, voice, speed), ct);
        var audio = await response.Content.ReadAsByteArrayAsync(ct);
        var type = response.Content.Headers.ContentType?.MediaType ?? ContentTypeFor(options.TtsFormat);
        return new SpeechClip(audio, type);
    }

    public async Task<IReadOnlyList<string>> VoicesAsync(CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            var reply = await http.GetFromJsonAsync<JsonObject>("v1/audio/voices", timeout.Token);

            // Older Kokoro-FastAPI lists bare ids; newer lists objects with an "id" (and quality
            // grades). Accept either.
            return reply?["voices"]?.AsArray()
                .Select(v => v switch
                {
                    JsonValue value => value.GetValue<string>(),
                    JsonObject voiceObject => voiceObject["id"]?.GetValue<string>(),
                    _ => null,
                })
                .OfType<string>()
                .Order(StringComparer.Ordinal)
                .ToList() ?? [];
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException
            or System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            return [];
        }
    }

    /// <summary>Audio plus word timings, or null if this server has no captioned endpoint.</summary>
    private async Task<SpeechClip?> TryCaptionedAsync(string text, string voice, double speed, CancellationToken ct)
    {
        var body = Body(text, voice, speed);
        body["stream"] = false;
        body["return_timestamps"] = true;

        HttpResponseMessage response;
        try
        {
            response = await http.PostAsync("dev/captioned_speech", JsonContent.Create(body), ct);
        }
        catch (HttpRequestException exception)
        {
            throw new SpeechUnavailableException($"Can't reach text-to-speech at {http.BaseAddress}.", exception);
        }
        catch (TaskCanceledException exception) when (!ct.IsCancellationRequested)
        {
            throw new SpeechUnavailableException("Text-to-speech didn't answer in time.", exception);
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
            {
                _captionsUnsupported = true;
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new SpeechUnavailableException(
                    $"Text-to-speech answered {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}");
            }

            var reply = await response.Content.ReadFromJsonAsync<JsonObject>(ct);
            if (reply?["audio"]?.GetValue<string>() is not { Length: > 0 } audio)
            {
                return null;
            }

            var words = reply["timestamps"]?.AsArray()
                .Select(t => t is JsonObject w && w["word"]?.GetValue<string>() is { } word
                    ? new WordTiming(word, w["start_time"]?.GetValue<double>() ?? 0, w["end_time"]?.GetValue<double>() ?? 0)
                    : null)
                .OfType<WordTiming>()
                .ToList();

            return new SpeechClip(Convert.FromBase64String(audio), ContentTypeFor(options.TtsFormat), words);
        }
    }

    private JsonObject Body(string text, string voice, double speed) => new()
    {
        ["model"] = options.TtsModel,
        ["input"] = text,
        ["voice"] = voice,
        ["response_format"] = options.TtsFormat,
        ["speed"] = speed,
    };

    private async Task<HttpResponseMessage> PostAsync(string path, JsonObject body, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.PostAsync(path, JsonContent.Create(body), ct);
        }
        catch (HttpRequestException exception)
        {
            throw new SpeechUnavailableException($"Can't reach text-to-speech at {http.BaseAddress}.", exception);
        }
        catch (TaskCanceledException exception) when (!ct.IsCancellationRequested)
        {
            throw new SpeechUnavailableException("Text-to-speech didn't answer in time.", exception);
        }

        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(ct);
            response.Dispose();
            throw new SpeechUnavailableException($"Text-to-speech answered {(int)response.StatusCode}: {detail}");
        }

        return response;
    }

    private static string ContentTypeFor(string format) => format switch
    {
        "mp3" => "audio/mpeg",
        "opus" => "audio/ogg",
        "aac" => "audio/aac",
        "flac" => "audio/flac",
        _ => "audio/wav",
    };
}
