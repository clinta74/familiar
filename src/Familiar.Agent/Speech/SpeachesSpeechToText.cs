using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Familiar.Agent.Speech;

/// <summary>
/// Speech-to-text through Speaches (faster-whisper), on its OpenAI-compatible
/// <c>/v1/audio/transcriptions</c>, so any compatible server can stand in.
/// </summary>
public sealed class SpeachesSpeechToText(HttpClient http, SpeechOptions options) : ISpeechToText
{
    public async Task<string> TranscribeAsync(byte[] audio, string contentType, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(audio);
        file.Headers.ContentType = MediaTypeHeaderValue.TryParse(contentType, out var type)
            ? type
            : new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", $"utterance{Extension(contentType)}");
        form.Add(new StringContent(options.SttModel), "model");
        form.Add(new StringContent("json"), "response_format");
        if (!string.IsNullOrWhiteSpace(options.SttLanguage))
        {
            form.Add(new StringContent(options.SttLanguage), "language");
        }

        HttpResponseMessage response;
        try
        {
            response = await http.PostAsync("v1/audio/transcriptions", form, ct);
        }
        catch (HttpRequestException exception)
        {
            throw new SpeechUnavailableException($"Can't reach speech-to-text at {http.BaseAddress}.", exception);
        }
        catch (TaskCanceledException exception) when (!ct.IsCancellationRequested)
        {
            throw new SpeechUnavailableException("Speech-to-text didn't answer in time.", exception);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new SpeechUnavailableException($"Speech-to-text answered {(int)response.StatusCode}: {body}");
            }

            return (JsonNode.Parse(body)?["text"]?.GetValue<string>() ?? string.Empty).Trim();
        }
    }

    /// <summary>
    /// Speaches serves only models it has downloaded, and downloads one on POST. Checked first, so a
    /// restart with the model already present is a single GET.
    /// </summary>
    public async Task<bool> EnsureModelAsync(CancellationToken ct)
    {
        try
        {
            var installed = await http.GetFromJsonAsync<JsonObject>("v1/models", ct);
            if (installed?["data"]?.AsArray().Any(m => m?["id"]?.GetValue<string>() == options.SttModel) == true)
            {
                return false;
            }

            // Downloads can take a while; this runs in the background at startup, not on a request.
            using var download = new HttpRequestMessage(HttpMethod.Post, $"v1/models/{options.SttModel}");
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cancel.CancelAfter(TimeSpan.FromMinutes(10));
            using var response = await http.SendAsync(download, cancel.Token);
            if (!response.IsSuccessStatusCode)
            {
                throw new SpeechUnavailableException(
                    $"Downloading {options.SttModel} failed: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(ct)}");
            }

            return true;
        }
        catch (HttpRequestException exception)
        {
            throw new SpeechUnavailableException($"Can't reach speech-to-text at {http.BaseAddress}.", exception);
        }
    }

    public async Task<bool> IsAvailableAsync(CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            using var response = await http.GetAsync("health", timeout.Token);
            return response.IsSuccessStatusCode;
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
        {
            return false;
        }
    }

    private static string Extension(string contentType) => contentType switch
    {
        _ when contentType.Contains("webm", StringComparison.OrdinalIgnoreCase) => ".webm",
        _ when contentType.Contains("ogg", StringComparison.OrdinalIgnoreCase) => ".ogg",
        _ when contentType.Contains("mp4", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("m4a", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("aac", StringComparison.OrdinalIgnoreCase) => ".m4a",
        _ when contentType.Contains("mpeg", StringComparison.OrdinalIgnoreCase) => ".mp3",
        _ => ".wav",
    };
}
