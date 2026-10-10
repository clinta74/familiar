using System.Text.Json.Serialization;
using Familiar.Agent.Llm;
using Familiar.Agent.Speech;
using Familiar.Agent.Turns;
using Familiar.Persistence;
using Familiar.Server;
using Familiar.Server.Auth;
using Familiar.Server.Awareness;
using Familiar.Server.Calendars;
using Familiar.Server.Hooks;
using Familiar.Server.Conversation;
using Familiar.Server.Familiars;
using Familiar.Server.Infrastructure;
using Familiar.Server.Memories;
using Familiar.Server.Notifications;
using Familiar.Server.Scheduling;
using Familiar.Server.Tools;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Familiar")
    ?? throw new InvalidOperationException("ConnectionStrings:Familiar is not configured.");
var authOptions = builder.Configuration.GetSection(AuthOptions.Section).Get<AuthOptions>() ?? new AuthOptions();
var proxyOptions = ProxyOptions.From(builder.Configuration);
var migrationBudget = TimeSpan.FromSeconds(builder.Configuration.GetValue("Database:MigrationRetryBudgetSeconds", 60));

var llmOptions = builder.Configuration.GetSection(LlmOptions.Section).Get<LlmOptions>() ?? new LlmOptions();
var speechOptions = builder.Configuration.GetSection(SpeechOptions.Section).Get<SpeechOptions>() ?? new SpeechOptions();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddFamiliarPersistence(connectionString, new DatabaseRetry(
    builder.Configuration.GetValue("Database:MaxRetryCount", DatabaseRetry.Default.MaxRetryCount),
    TimeSpan.FromSeconds(builder.Configuration.GetValue("Database:MaxRetryDelaySeconds", DatabaseRetry.Default.MaxRetryDelay.TotalSeconds))));

// The conversation (PLAN.md §3). The model client is a typed HttpClient, resolved per turn rather
// than captured by a singleton, so the handler pool can rotate connections.
builder.Services.AddSingleton(llmOptions);
builder.Services.AddHttpClient<ILlmClient, OllamaClient>(http =>
{
    http.BaseAddress = new Uri(llmOptions.BaseUrl.TrimEnd('/') + "/");
    http.Timeout = TimeSpan.FromSeconds(Math.Max(5, llmOptions.RequestTimeoutSeconds));
});
builder.Services.AddTransient<TurnRunner>();
builder.Services.AddHttpClient<OllamaWarmUp>(http =>
{
    http.BaseAddress = new Uri(llmOptions.BaseUrl.TrimEnd('/') + "/");
    http.Timeout = TimeSpan.FromSeconds(Math.Max(5, llmOptions.RequestTimeoutSeconds));
});
builder.Services.AddHostedService<LlmWarmUp>();
builder.Services.AddHttpClient<IEmbedder, OllamaEmbedder>(http =>
{
    http.BaseAddress = new Uri(llmOptions.BaseUrl.TrimEnd('/') + "/");
    http.Timeout = TimeSpan.FromSeconds(Math.Max(5, llmOptions.RequestTimeoutSeconds));
});

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

// Tools and the scheduler (PLAN.md §6): timers and reminders that fire with the tab closed, by
// content-free Web Push.
var pushOptions = builder.Configuration.GetSection(PushOptions.Section).Get<PushOptions>() ?? new PushOptions();
builder.Services.AddSingleton(pushOptions);
builder.Services.AddSingleton<SchedulerStore>();
builder.Services.AddSingleton<SchedulerSignal>();
builder.Services.AddSingleton<Scheduler>();
builder.Services.AddFamiliarTools();
builder.Services.AddSingleton<VapidKeys>();
builder.Services.AddHttpClient(nameof(PushSender), http => http.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddSingleton<PushSender>();
builder.Services.AddHostedService<SchedulerLoop>();

// Awareness and memory (PLAN.md §6.3, §6.4, §6.6).
builder.Services.AddSingleton(builder.Configuration.GetSection(MemoryOptions.Section).Get<MemoryOptions>() ?? new MemoryOptions());
builder.Services.AddSingleton(builder.Configuration.GetSection(CalendarOptions.Section).Get<CalendarOptions>() ?? new CalendarOptions());
builder.Services.AddSingleton<MemoryStore>();
builder.Services.AddHostedService<MemoryExtractor>();
builder.Services.AddSingleton<CalendarStore>();
builder.Services.AddHttpClient(nameof(FeedSync), http =>
{
    http.Timeout = TimeSpan.FromSeconds(20);
    http.DefaultRequestHeaders.UserAgent.ParseAdd("Familiar/1.0 (calendar subscription)");
});
builder.Services.AddSingleton<FeedSync>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<FeedSync>());
builder.Services.AddSingleton<SituationProvider>();
builder.Services.AddSingleton<Familiar.Server.Devices.DeviceTools>();
builder.Services.AddSingleton(builder.Configuration.GetSection(AwarenessOptions.Section).Get<AwarenessOptions>() ?? new AwarenessOptions());

// Hooks (PLAN.md §6.5): announcements in, webhooks out.
builder.Services.AddSingleton<RecentAnnouncements>();
builder.Services.AddHttpClient(nameof(WebhookDispatcher), http => http.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddSingleton<WebhookDispatcher>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<WebhookDispatcher>());
builder.Services.AddHttpClient(nameof(McpTools), http => http.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddSingleton<McpTools>();
builder.Services.AddHttpClient(nameof(Familiar.Server.Home.HomeAssistant), http => http.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddSingleton<Familiar.Server.Home.HomeAssistantSockets>();
builder.Services.AddSingleton<Familiar.Server.Home.HomeAssistant>();

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
logger.ToolsConfigured(ToolRegistration.Describe(app.Services.GetRequiredService<Familiar.Agent.Tools.IToolExecutor>()));

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
app.MapAdminEndpoints();
app.MapFamiliarEndpoints();
app.MapAvatarEndpoints();
app.MapConversationEndpoints();
app.MapJobEndpoints();
app.MapNotificationEndpoints();
app.MapMemoryEndpoints();
app.MapCalendarEndpoints();
app.MapHookEndpoints();
app.MapMcpEndpoints();
Familiar.Server.Home.HomeAssistantEndpoints.Map(app);

await app.RunAsync();

/// <summary>Exposed for WebApplicationFactory in the integration tests.</summary>
public partial class Program;
