using Familiar.Agent.Llm;
using Familiar.Agent.Speech;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Familiar.Server.Tests.Infrastructure;

/// <summary>Boots the real server against a test database. Nothing is stubbed except the clock, on request.</summary>
public sealed class FamiliarAppFactory(
    string connectionString,
    IReadOnlyDictionary<string, string>? settings = null,
    TimeProvider? clock = null,
    FakeLlm? llm = null,
    Action<IServiceCollection>? services = null) : WebApplicationFactory<Program>
{
    /// <summary>
    /// The model every test talks to. Never the real one: a test must not depend on Ollama being up,
    /// and the scripted replies are what make the assertions exact.
    /// </summary>
    public FakeLlm Llm { get; } = llm ?? new FakeLlm();

    public FakeSpeechToText Stt { get; } = new();

    public FakeTextToSpeech Tts { get; } = new();

    public FakeEmbedder Embedder { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // "Testing", so appsettings.Development.json (the compose database) can't leak in.
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Familiar", connectionString);

        // Revalidate on every request, so a test of "password change signs other sessions out"
        // asserts the behaviour rather than a one-minute cache.
        builder.UseSetting("Auth:RevalidationIntervalSeconds", "0");

        // Fail fast against a bad database rather than riding out a minute of retries.
        builder.UseSetting("Database:MigrationRetryBudgetSeconds", "0");

        // Every test request comes from one address, so the shipped limit would trip during
        // setup. Tests of the limit itself set it back down.
        builder.UseSetting("RateLimits:AuthAttemptsPerMinute", "100000");
        builder.UseSetting("RateLimits:MessagesPerMinute", "100000");

        // The fakes need no warming, and a warm-up synthesis would show up in tests that count TTS calls.
        builder.UseSetting("Speech:WarmUpOnStart", "false");

        // A greeting on connect would interleave with what most tests assert; greeting tests turn it on.
        builder.UseSetting("Awareness:Greet", "false");

        foreach (var (key, value) in settings ?? new Dictionary<string, string>())
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureTestServices(collection =>
        {
            collection.AddSingleton<ILlmClient>(Llm);
            collection.AddSingleton<ISpeechToText>(Stt);
            collection.AddSingleton<ITextToSpeech>(Tts);
            collection.AddSingleton<IEmbedder>(Embedder);
            if (clock is not null)
            {
                collection.Replace(ServiceDescriptor.Singleton(clock));
            }

            services?.Invoke(collection);
        });

        // The Windows event log provider's handle is shared across hosts in one process and closed
        // by the first to be disposed; later hosts then throw from inside ILogger.Log.
        builder.ConfigureLogging(logging => logging.ClearProviders());
    }
}
