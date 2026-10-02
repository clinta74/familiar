using System.Security.Cryptography;
using System.Text.Json;
using Familiar.Domain.Familiars;
using Familiar.Persistence;
using Familiar.Server.Auth;
using Familiar.Server.Conversation;
using Familiar.Server.Infrastructure;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;

namespace Familiar.Server.Familiars;

public sealed record AvatarView(Guid Id, string Name, long SizeBytes, VrmInfo Meta, DateTimeOffset CreatedAt);

/// <summary>Uploaded VRM models (PLAN.md §5.5).</summary>
public static class AvatarEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void MapAvatarEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/avatars").RequireAuthorization();
        group.MapGet("/", ListAsync);
        group.MapPost("/", UploadAsync).RequireRateLimiting(RateLimiting.Conversation);
        group.MapGet("/{id:guid}/model", ModelAsync);
        group.MapDelete("/{id:guid}", DeleteAsync);
    }

    private static async Task<List<AvatarView>> ListAsync(HttpContext http, IDbContextFactory<FamiliarDbContext> db, CancellationToken ct)
    {
        var accountId = http.User.AccountId()!.Value;
        await using var context = await db.CreateDbContextAsync(ct);
        var rows = await context.AvatarAssets.AsNoTracking()
            .Where(a => a.AccountId == accountId)
            .OrderBy(a => a.CreatedAt)
            .Select(a => new { a.Id, a.Name, a.SizeBytes, a.Meta, a.CreatedAt })
            .ToListAsync(ct);
        return [.. rows.Select(a => new AvatarView(a.Id, a.Name, a.SizeBytes, JsonSerializer.Deserialize<VrmInfo>(a.Meta, Json)!, a.CreatedAt))];
    }

    /// <summary>The .vrm file is the request body; its name comes in the query string.</summary>
    private static async Task<IResult> UploadAsync(
        HttpContext http, IDbContextFactory<FamiliarDbContext> db, TimeProvider clock, string? fileName, CancellationToken ct)
    {
        // Above Kestrel's 30 MB default, for this endpoint only.
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = AvatarAsset.MaxBytes + 1;
        }

        if (http.Request.ContentLength > AvatarAsset.MaxBytes)
        {
            return AuthEndpoints.Error(StatusCodes.Status413PayloadTooLarge, $"Models can be up to {AvatarAsset.MaxBytes / 1024 / 1024} MB.");
        }

        var accountId = http.User.AccountId()!.Value;
        await using var context = await db.CreateDbContextAsync(ct);
        if (await context.AvatarAssets.CountAsync(a => a.AccountId == accountId, ct) >= AvatarAsset.MaxPerAccount)
        {
            return AuthEndpoints.Error(StatusCodes.Status400BadRequest, $"You can keep {AvatarAsset.MaxPerAccount} models. Delete one first.");
        }

        using var buffer = new MemoryStream();
        try
        {
            await http.Request.Body.CopyToAsync(buffer, ct);
        }
        catch (BadHttpRequestException)
        {
            return AuthEndpoints.Error(StatusCodes.Status413PayloadTooLarge, $"Models can be up to {AvatarAsset.MaxBytes / 1024 / 1024} MB.");
        }

        var bytes = buffer.ToArray();
        if (bytes.Length > AvatarAsset.MaxBytes)
        {
            return AuthEndpoints.Error(StatusCodes.Status413PayloadTooLarge, $"Models can be up to {AvatarAsset.MaxBytes / 1024 / 1024} MB.");
        }

        var (info, problem) = VrmFile.Inspect(bytes);
        if (info is null)
        {
            return AuthEndpoints.Error(StatusCodes.Status400BadRequest, problem!);
        }

        var stem = Path.GetFileNameWithoutExtension(fileName ?? "");
        var name = info.Name ?? (stem.Length > 0 ? stem : "My model");
        var asset = new AvatarAsset
        {
            AccountId = accountId,
            Name = name.Length > 128 ? name[..128] : name,
            Content = bytes,
            SizeBytes = bytes.Length,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)),
            Meta = JsonSerializer.Serialize(info, Json),
            CreatedAt = clock.GetUtcNow(),
        };
        context.AvatarAssets.Add(asset);
        await context.SaveChangesAsync(ct);

        return TypedResults.Ok(new AvatarView(asset.Id, asset.Name, asset.SizeBytes, info, asset.CreatedAt));
    }

    /// <summary>The model itself. Never changes once uploaded, so the browser may keep it.</summary>
    private static async Task<IResult> ModelAsync(Guid id, HttpContext http, IDbContextFactory<FamiliarDbContext> db, CancellationToken ct)
    {
        var accountId = http.User.AccountId()!.Value;
        await using var context = await db.CreateDbContextAsync(ct);
        var asset = await context.AvatarAssets.AsNoTracking()
            .Where(a => a.Id == id && a.AccountId == accountId)
            .Select(a => new { a.Content, a.Sha256 })
            .FirstOrDefaultAsync(ct);

        if (asset is null)
        {
            return TypedResults.NotFound();
        }

        http.Response.Headers.CacheControl = "private, max-age=31536000, immutable";
        return TypedResults.File(asset.Content, "model/gltf-binary", entityTag: new Microsoft.Net.Http.Headers.EntityTagHeaderValue($"\"{asset.Sha256}\""));
    }

    /// <summary>Deleting the model in use puts the plush llama back.</summary>
    private static async Task<IResult> DeleteAsync(Guid id, HttpContext http, IDbContextFactory<FamiliarDbContext> db, ConversationStore store, CancellationToken ct)
    {
        var accountId = http.User.AccountId()!.Value;
        await using var context = await db.CreateDbContextAsync(ct);
        if (await context.AvatarAssets.Where(a => a.Id == id && a.AccountId == accountId).ExecuteDeleteAsync(ct) == 0)
        {
            return TypedResults.NotFound();
        }

        var profile = await store.ProfileAsync(accountId, ct);
        var appearance = AppearanceView.Parse(profile.Appearance);
        if (appearance.Model == "vrm" && appearance.VrmId == id)
        {
            profile.Appearance = (appearance with { Model = "llama", VrmId = null }).ToJson();
            await store.SaveProfileAsync(profile, ct);
        }

        return TypedResults.NoContent();
    }
}
