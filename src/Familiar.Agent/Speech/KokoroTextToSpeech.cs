using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Familiar.Agent.Speech;

/// <summary>
/// Text-to-speech through Kokoro-FastAPI, on its OpenAI-compatible <c>/v1/audio/speech</c>.
/// </summary>
public sealed class KokoroTextToSpeech(HttpClient http, SpeechOptions options) : ITextToSpeech
{
    public async Task<SpeechClip> SynthesizeAsync(string text, string voice, double speed, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = options.TtsModel,
            ["input"] = text,
            ["voice"] = voice,
            ["response_format"] = options.TtsFormat,
            ["speed"] = speed,
        };

        HttpResponseMessage response;
        try
        {
            response = await http.PostAsync("v1/audio/speech", JsonContent.Create(body), ct);
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
            if (!response.IsSuccessStatusCode)
            {
                var detail = await response.Content.ReadAsStringAsync(ct);
                throw new SpeechUnavailableException($"Text-to-speech answered {(int)response.StatusCode}: {detail}");
            }

            var audio = await response.Content.ReadAsByteArrayAsync(ct);
            var type = response.Content.Headers.ContentType?.MediaType ?? ContentTypeFor(options.TtsFormat);
            return new SpeechClip(audio, type);
        }
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
                    JsonObject voice => voice["id"]?.GetValue<string>(),
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

    private static string ContentTypeFor(string format) => format switch
    {
        "mp3" => "audio/mpeg",
        "opus" => "audio/ogg",
        "aac" => "audio/aac",
        "flac" => "audio/flac",
        _ => "audio/wav",
    };
}
