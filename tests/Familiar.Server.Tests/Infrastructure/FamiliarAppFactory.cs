using Familiar.Agent.Llm;
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
    FakeLlm? llm = null) : WebApplicationFactory<Program>
{
    /// <summary>
    /// The model every test talks to. Never the real one: a test must not depend on Ollama being up,
    /// and the scripted replies are what make the assertions exact.
    /// </summary>
    public FakeLlm Llm { get; } = llm ?? new FakeLlm();

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

        foreach (var (key, value) in settings ?? new Dictionary<string, string>())
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<ILlmClient>(Llm);
            if (clock is not null)
            {
                services.Replace(ServiceDescriptor.Singleton(clock));
            }
        });

        // The Windows event log provider's handle is shared across hosts in one process and closed
        // by the first to be disposed; later hosts then throw from inside ILogger.Log.
        builder.ConfigureLogging(logging => logging.ClearProviders());
    }
}
