using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Familiar.Server.Infrastructure;

public static class HealthEndpoints
{
    public const string ReadyTag = "ready";

    public static void MapHealthEndpoints(this IEndpointRouteBuilder routes)
    {
        // Liveness: the process answers. Deliberately checks nothing, so a database outage never
        // gets the server container restarted - restarting it wouldn't bring Postgres back.
        routes.MapHealthChecks("/health", new HealthCheckOptions { Predicate = _ => false });

        // Readiness: the database is reachable.
        routes.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains(ReadyTag),
            ResponseWriter = WriteAsync,
        });

        routes.MapGet("/api/version", () => TypedResults.Ok(BuildInfo.Current));
    }

    // Names and statuses only. Exception text from a failed check can carry connection details.
    private static Task WriteAsync(HttpContext http, HealthReport report) =>
        http.Response.WriteAsJsonAsync(new
        {
            status = report.Status.ToString(),
            durationMs = (long)report.TotalDuration.TotalMilliseconds,
            checks = report.Entries.Select(e => new { name = e.Key, status = e.Value.Status.ToString() }),
        });
}
