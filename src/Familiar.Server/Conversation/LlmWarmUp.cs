using Familiar.Agent.Llm;

namespace Familiar.Server.Conversation;

/// <summary>
/// Keeps the models loaded (PLAN.md §4.2): at startup, and then every couple of minutes, because
/// Ollama can restart on its own (a container update, <c>restart: always</c> after a crash) and
/// <c>create-models.sh</c> evicts what's loaded. Each check is one cheap <c>/api/ps</c>; a load only
/// happens when something isn't there.
/// </summary>
/// <remarks>Never blocks or fails startup: a model server that isn't up yet is just checked again later.</remarks>
public sealed class LlmWarmUp(OllamaWarmUp warmUp, LlmOptions options, ILogger<LlmWarmUp> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.WarmUp)
        {
            return;
        }

        // Logged when it changes, not every check: a missing model is worth one clear warning, not
        // one every two minutes.
        WarmUpOutcome? last = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            var outcome = await warmUp.EnsureLoadedAsync(stoppingToken);
            if (outcome != last || outcome == WarmUpOutcome.Loaded)
            {
                switch (outcome)
                {
                    case WarmUpOutcome.Loaded:
                        logger.ModelsWarmed(options.Model, options.EmbeddingModel);
                        break;
                    case WarmUpOutcome.Missing:
                        logger.ModelMissing(warmUp.MissingModel ?? options.Model);
                        break;
                    case WarmUpOutcome.Unreachable:
                        logger.ModelUnavailable($"warm-up couldn't reach {options.BaseUrl}");
                        break;
                }
            }

            last = outcome;

            // Sooner while it isn't ready, so a model server that comes up late is warmed promptly.
            var wait = outcome is WarmUpOutcome.Loaded or WarmUpOutcome.AlreadyLoaded
                ? TimeSpan.FromSeconds(Math.Max(10, options.WarmUpCheckSeconds))
                : TimeSpan.FromSeconds(15);
            await Task.Delay(wait, stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }
}
