using System.Text.Json.Serialization;
using Familiar.Persistence;
using Familiar.Server;
using Familiar.Server.Auth;
using Familiar.Server.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Familiar")
    ?? throw new InvalidOperationException("ConnectionStrings:Familiar is not configured.");
var authOptions = builder.Configuration.GetSection(AuthOptions.Section).Get<AuthOptions>() ?? new AuthOptions();
var proxyOptions = ProxyOptions.From(builder.Configuration);
var migrationBudget = TimeSpan.FromSeconds(builder.Configuration.GetValue("Database:MigrationRetryBudgetSeconds", 60));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddFamiliarPersistence(connectionString);
builder.Services.AddFamiliarAuth(authOptions, builder.Environment);
builder.Services.AddFamiliarRateLimiting(builder.Configuration);
builder.Services.Configure<Microsoft.AspNetCore.Builder.ForwardedHeadersOptions>(proxyOptions.Apply);

// Enums travel as their names. As ordinals, a client comparing against "Admin" silently matches nothing.
builder.Services.ConfigureHttpJsonOptions(json => json.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddHealthChecks()
    .AddDbContextCheck<FamiliarDbContext>("database", tags: [HealthEndpoints.ReadyTag]);

var app = builder.Build();

var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Familiar.Server");
logger.Starting(BuildInfo.Current.Version, BuildInfo.Current.Revision, app.Environment.EnvironmentName);
logger.AuthConfigured(authOptions.ToString());
logger.ProxyConfigured(proxyOptions.Describe());

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

await app.RunAsync();

/// <summary>Exposed for WebApplicationFactory in the integration tests.</summary>
public partial class Program;
