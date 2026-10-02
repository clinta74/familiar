using Familiar.Domain.Familiars;
using Familiar.Server.Auth;
using Familiar.Server.Conversation;

namespace Familiar.Server.Familiars;

public sealed record FamiliarView(
    string Name,
    string? UserNickname,
    double Warmth,
    double Playfulness,
    double Verbosity,
    double Formality,
    double Expressiveness)
{
    public static FamiliarView From(FamiliarProfile p) =>
        new(p.Name, p.UserNickname, p.Warmth, p.Playfulness, p.Verbosity, p.Formality, p.Expressiveness);
}

/// <summary>Reading and changing who your familiar is (PLAN.md §5.5).</summary>
public static class FamiliarEndpoints
{
    public static void MapFamiliarEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/familiar").RequireAuthorization();
        group.MapGet("/", GetAsync);
        group.MapPut("/", UpdateAsync);
    }

    private static async Task<IResult> GetAsync(HttpContext http, ConversationStore store, CancellationToken ct) =>
        TypedResults.Ok(FamiliarView.From(await store.ProfileAsync(http.User.AccountId()!.Value, ct)));

    private static async Task<IResult> UpdateAsync(FamiliarView request, HttpContext http, ConversationStore store, CancellationToken ct)
    {
        var name = request.Name?.Trim() ?? string.Empty;
        var nickname = string.IsNullOrWhiteSpace(request.UserNickname) ? null : request.UserNickname.Trim();

        if (name.Length is 0 or > FamiliarProfile.MaxNameLength || nickname?.Length > FamiliarProfile.MaxNameLength)
        {
            return AuthEndpoints.Error(StatusCodes.Status400BadRequest, $"Names are 1-{FamiliarProfile.MaxNameLength} characters.");
        }

        var profile = await store.ProfileAsync(http.User.AccountId()!.Value, ct);
        profile.Name = name;
        profile.UserNickname = nickname;
        profile.Warmth = FamiliarProfile.Clamp(request.Warmth);
        profile.Playfulness = FamiliarProfile.Clamp(request.Playfulness);
        profile.Verbosity = FamiliarProfile.Clamp(request.Verbosity);
        profile.Formality = FamiliarProfile.Clamp(request.Formality);
        profile.Expressiveness = FamiliarProfile.Clamp(request.Expressiveness);

        return TypedResults.Ok(FamiliarView.From(await store.SaveProfileAsync(profile, ct)));
    }
}
