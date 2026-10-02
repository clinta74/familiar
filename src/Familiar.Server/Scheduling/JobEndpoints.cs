using Familiar.Server.Auth;

namespace Familiar.Server.Scheduling;

public static class JobEndpoints
{
    public static void MapJobEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/jobs").RequireAuthorization();

        group.MapGet("/", async (HttpContext http, Scheduler scheduler, CancellationToken ct) =>
            (await scheduler.PendingAsync(http.User.AccountId()!.Value, null, ct)).Select(JobView.From).ToList());

        // Cancelling from the timers list: the same as asking the familiar to.
        group.MapDelete("/{id:guid}", async (Guid id, HttpContext http, Scheduler scheduler, CancellationToken ct) =>
            await scheduler.CancelAsync(http.User.AccountId()!.Value, id, ct) ? Results.NoContent() : Results.NotFound());
    }
}
