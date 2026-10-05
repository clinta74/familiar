using Familiar.Domain.Memories;
using Familiar.Server.Auth;

namespace Familiar.Server.Memories;

public sealed record MemoryView(Guid Id, string Text, MemorySource Source, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    public static MemoryView From(MemoryFact m) => new(m.Id, m.Text, m.Source, m.CreatedAt, m.UpdatedAt);
}

/// <param name="Text">The memory, one short sentence.</param>
public sealed record MemoryRequest(string? Text);

/// <summary>Everything the familiar remembers, to read, correct and delete (PLAN.md §6.6).</summary>
public static class MemoryEndpoints
{
    public static void MapMemoryEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/memories").RequireAuthorization();

        group.MapGet("/", async (HttpContext http, MemoryStore store, CancellationToken ct) =>
            (await store.ListAsync(http.User.AccountId()!.Value, ct)).Select(MemoryView.From).ToList());

        group.MapPost("/", async (MemoryRequest request, HttpContext http, MemoryStore store, CancellationToken ct) =>
        {
            if (Invalid(request) is { } error)
            {
                return error;
            }

            var memory = await store.AddAsync(http.User.AccountId()!.Value, request.Text!, MemorySource.Settings, null, ct, embedLater: true);
            return memory is null
                ? AuthEndpoints.Error(StatusCodes.Status409Conflict, "That's already remembered.")
                : Results.Ok(MemoryView.From(memory));
        });

        group.MapPut("/{id:guid}", async (Guid id, MemoryRequest request, HttpContext http, MemoryStore store, CancellationToken ct) =>
        {
            if (Invalid(request) is { } error)
            {
                return error;
            }

            var memory = await store.UpdateAsync(http.User.AccountId()!.Value, id, request.Text!, ct);
            return memory is null ? Results.NotFound() : Results.Ok(MemoryView.From(memory));
        });

        group.MapDelete("/{id:guid}", async (Guid id, HttpContext http, MemoryStore store, CancellationToken ct) =>
            await store.DeleteAsync(http.User.AccountId()!.Value, id, ct) ? Results.NoContent() : Results.NotFound());

        group.MapDelete("/", async (HttpContext http, MemoryStore store, CancellationToken ct) =>
        {
            await store.DeleteAllAsync(http.User.AccountId()!.Value, ct);
            return Results.NoContent();
        });
    }

    private static IResult? Invalid(MemoryRequest request) =>
        string.IsNullOrWhiteSpace(request.Text) || request.Text.Trim().Length > MemoryFact.MaxTextLength
            ? AuthEndpoints.Error(StatusCodes.Status400BadRequest, $"A memory is 1-{MemoryFact.MaxTextLength} characters.")
            : null;
}
