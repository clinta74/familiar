using System.Text.Json.Serialization;
using Familiar.Agent.Llm;
using Familiar.Agent.Speech;
using Familiar.Agent.Turns;
using Familiar.Persistence;
using Familiar.Server;
using Familiar.Server.Auth;
using Familiar.Server.Conversation;
using Familiar.Server.Familiars;
using Familiar.Server.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Familiar")
    ?? throw new InvalidOperationException("ConnectionStrings:Familiar is not configured.");
var authOptions = builder.Configuration.GetSection(AuthOptions.Section).Get<AuthOptions>() ?? new AuthOptions();
var proxyOptions = ProxyOptions.From(builder.Configuration);
var migrationBudget = TimeSpan.FromSeconds(builder.Configuration.GetValue("Database:MigrationRetryBudgetSeconds", 60));

var llmOptions = builder.Configuration.GetSection(LlmOptions.Section).Get<LlmOptions>() ?? new LlmOptions();
var speechOptions = builder.Configuration.GetSection(SpeechOptions.Section).Get<SpeechOptions>() ?? new SpeechOptions();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddFamiliarPersistence(connectionString);

// The conversation (PLAN.md §3). The model client is a typed HttpClient, resolved per turn rather
// than captured by a singleton, so the handler pool can rotate connections.
builder.Services.AddSingleton(llmOptions);
builder.Services.AddHttpClient<ILlmClient, OllamaClient>(http =>
{
    http.BaseAddress = new Uri(llmOptions.BaseUrl.TrimEnd('/') + "/");
    http.Timeout = TimeSpan.FromSeconds(Math.Max(5, llmOptions.RequestTimeoutSeconds));
});
builder.Services.AddTransient<TurnRunner>();

// Voice (PLAN.md §3.2): the STT and TTS sidecars, both OpenAI-compatible HTTP.
builder.Services.AddSingleton(speechOptions);
builder.Services.AddHttpClient<ISpeechToText, SpeachesSpeechToText>(http =>
{
    http.BaseAddress = new Uri(speechOptions.SttBaseUrl.TrimEnd('/') + "/");
    http.Timeout = TimeSpan.FromSeconds(Math.Max(5, speechOptions.RequestTimeoutSeconds));
});
builder.Services.AddHttpClient<ITextToSpeech, KokoroTextToSpeech>(http =>
{
    http.BaseAddress = new Uri(speechOptions.TtsBaseUrl.TrimEnd('/') + "/");
    http.Timeout = TimeSpan.FromSeconds(Math.Max(5, speechOptions.RequestTimeoutSeconds));
});
builder.Services.AddSingleton<SpeechClipStore>();
builder.Services.AddHostedService<SpeechWarmUp>();

builder.Services.AddSingleton<ConversationHub>();
builder.Services.AddSingleton<ConversationStore>();
builder.Services.AddSingleton<ConversationTurns>();
builder.Services.AddFamiliarAuth(authOptions, builder.Environment);
builder.Services.AddFamiliarRateLimiting(builder.Configuration);
builder.Services.Configure<Microsoft.AspNetCore.Builder.ForwardedHeadersOptions>(proxyOptions.Apply);

// Enums travel as camelCase names, exactly as on the event stream (ConversationHub.Json). As ordinals a
// client comparing against "admin" silently matches nothing; and when this was PascalCase while the
// stream was camelCase, messages loaded from history lost their styling and emotion labels.
builder.Services.ConfigureHttpJsonOptions(json =>
    json.SerializerOptions.Converters.Add(new JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase)));

builder.Services.AddHealthChecks()
    .AddDbContextCheck<FamiliarDbContext>("database", tags: [HealthEndpoints.ReadyTag]);

var app = builder.Build();

var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Familiar.Server");
logger.Starting(BuildInfo.Current.Version, BuildInfo.Current.Revision, app.Environment.EnvironmentName);
logger.AuthConfigured(authOptions.ToString());
logger.ProxyConfigured(proxyOptions.Describe());
logger.LlmConfigured(llmOptions.ToString());
logger.SpeechConfigured(speechOptions.ToString());

await StartupMigrator.MigrateAsync(app.Services, migrationBudget, logger, app.Lifetime.ApplicationStopping);

// Order matters: forwarded headers first so the cookie's Secure decision and the rate limiter see
// the real client; authentication before the rate limiter so later per-account policies can
// partition on the user.
app.UseForwardedHeaders();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapHealthEndpoints();
app.MapAuthEndpoints();
app.MapDeviceEndpoints();
app.MapFamiliarEndpoints();
app.MapConversationEndpoints();

await app.RunAsync();

/// <summary>Exposed for WebApplicationFactory in the integration tests.</summary>
public partial class Program;
