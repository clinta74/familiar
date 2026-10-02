using Familiar.Agent.Speech;

namespace Familiar.Server.Conversation;

/// <summary>
/// Gets the speech sidecars ready at startup, in the background: downloads the STT model if Speaches
/// doesn't have it, and has Kokoro load its model by saying one word. Without this the first person
/// to talk after a deploy waits for both - a download on a fresh volume.
/// </summary>
/// <remarks>
/// Never blocks startup and never fails it: speech is optional (PLAN.md §10.3), and a sidecar that
/// isn't up yet is simply reported and left for the first request to find.
/// </remarks>
public sealed class SpeechWarmUp(IServiceScopeFactory scopes, SpeechOptions options, ILogger<SpeechWarmUp> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.WarmUpOnStart)
        {
            return;
        }

        await using var scope = scopes.CreateAsyncScope();

        try
        {
            if (await scope.ServiceProvider.GetRequiredService<ISpeechToText>().EnsureModelAsync(stoppingToken))
            {
                logger.SttModelDownloaded(options.SttModel);
            }
        }
        catch (SpeechUnavailableException exception)
        {
            logger.SpeechUnavailable(exception.Message);
        }

        try
        {
            await scope.ServiceProvider.GetRequiredService<ITextToSpeech>()
                .SynthesizeAsync("Hello.", options.DefaultVoice, 1.0, stoppingToken);
        }
        catch (SpeechUnavailableException exception)
        {
            logger.SpeechUnavailable(exception.Message);
        }
    }
}
