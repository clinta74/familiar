using System.Diagnostics;
using Familiar.Agent.Speech;

namespace Familiar.Server.Conversation;

/// <summary>
/// Gets the speech sidecars ready at startup, in the background: downloads the STT model if Speaches
/// doesn't have it, and has Kokoro load its model by saying one word. Without this the first person
/// to talk after a deploy waits for both - a download on a fresh volume.
/// </summary>
/// <remarks>
/// Never blocks startup and never fails it: speech is optional (PLAN.md §10.3). The sidecars are
/// slower to start than the server - on the NAS everything comes up together - so each is tried
/// again for a while before it's reported and left for the first request to find.
/// </remarks>
public sealed class SpeechWarmUp(IServiceScopeFactory scopes, SpeechOptions options, ILogger<SpeechWarmUp> logger) : BackgroundService
{
    private static readonly TimeSpan RetryEvery = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan GiveUpAfter = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.WarmUpOnStart)
        {
            return;
        }

        await using var scope = scopes.CreateAsyncScope();
        var stt = scope.ServiceProvider.GetRequiredService<ISpeechToText>();
        var tts = scope.ServiceProvider.GetRequiredService<ITextToSpeech>();

        await Task.WhenAll(
            KeepTryingAsync(async ct =>
            {
                if (await stt.EnsureModelAsync(ct))
                {
                    logger.SttModelDownloaded(options.SttModel);
                }
            }, stoppingToken),
            KeepTryingAsync(ct => tts.SynthesizeAsync("Hello.", options.DefaultVoice, 1.0, ct), stoppingToken));
    }

    /// <summary>Runs the warm-up until it succeeds, giving up after a while with a warning.</summary>
    private async Task KeepTryingAsync(Func<CancellationToken, Task> warmUp, CancellationToken ct)
    {
        var waited = Stopwatch.StartNew();
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await warmUp(ct);
                return;
            }
            catch (SpeechUnavailableException exception)
            {
                if (waited.Elapsed >= GiveUpAfter)
                {
                    logger.SpeechUnavailable(exception.Message);
                    return;
                }

                logger.SpeechNotReady(exception.Message, (int)RetryEvery.TotalSeconds);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }

            try
            {
                await Task.Delay(RetryEvery, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
