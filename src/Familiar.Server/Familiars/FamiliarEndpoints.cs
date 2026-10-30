using Familiar.Agent.Speech;
using Familiar.Domain.Familiars;
using Familiar.Persistence;
using Familiar.Server.Auth;
using Familiar.Server.Conversation;
using Familiar.Server.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Familiar.Server.Familiars;

public sealed record FamiliarView(
    string Name,
    string? UserNickname,
    double Warmth,
    double Playfulness,
    double Verbosity,
    double Formality,
    double Expressiveness,
    string? Voice = null,
    double VoiceSpeed = 1.0,
    bool? Proactive = null,
    AppearanceView? Appearance = null,
    bool? LookUps = null,
    string? HomePlace = null,
    PlaceView? Home = null,
    bool? ShortGreeting = null)
{
    public static FamiliarView From(FamiliarProfile p) =>
        new(p.Name, p.UserNickname, p.Warmth, p.Playfulness, p.Verbosity, p.Formality, p.Expressiveness, p.Voice, p.VoiceSpeed, p.Proactive,
            AppearanceView.Parse(p.Appearance), p.LookUps, p.HomePlace, HomePlaces.ViewOf(p), p.ShortGreeting);
}

/// <param name="Voices">What the TTS service offers; empty if it isn't reachable.</param>
/// <param name="Default">The voice used when a familiar hasn't chosen one.</param>
public sealed record VoicesView(IReadOnlyList<string> Voices, string Default);

/// <summary>Reading and changing who your familiar is (PLAN.md §5.5).</summary>
public static class FamiliarEndpoints
{
    private const string PreviewSentence = "Hi! This is how I'll sound when we talk.";

    public static void MapFamiliarEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/familiar").RequireAuthorization();
        group.MapGet("/", GetAsync);
        group.MapPut("/", UpdateAsync);
        group.MapGet("/voices", VoicesAsync);
        group.MapGet("/voice-preview", PreviewAsync).RequireRateLimiting(RateLimiting.Conversation);
        group.MapGet("/places", SearchPlacesAsync).RequireRateLimiting(RateLimiting.Conversation);
        group.MapGet("/places/at", PlaceAtAsync).RequireRateLimiting(RateLimiting.Conversation);
    }

    /// <summary>The places called [name], to pick home from (BACKLOG item 32).</summary>
    private static async Task<IResult> SearchPlacesAsync(string? name, LookUps.Places places, CancellationToken ct)
    {
        var asked = name?.Trim() ?? "";
        if (asked.Length is < 2 or > FamiliarProfile.MaxHomePlaceLength)
        {
            return AuthEndpoints.Error(StatusCodes.Status400BadRequest, "Type the name of a town or city.");
        }

        try
        {
            return TypedResults.Ok(await places.SearchAsync(asked.Split(',')[0].Trim(), ct));
        }
        catch (HttpRequestException)
        {
            return AuthEndpoints.Error(StatusCodes.Status502BadGateway, "Couldn't search for places just now. Try again in a moment.");
        }
    }

    /// <summary>The town at a device's coordinates, for "use my current location".</summary>
    private static async Task<IResult> PlaceAtAsync(double? latitude, double? longitude, LookUps.Places places, CancellationToken ct)
    {
        if (latitude is not { } lat || longitude is not { } lon || Math.Abs(lat) > 90 || Math.Abs(lon) > 180)
        {
            return AuthEndpoints.Error(StatusCodes.Status400BadRequest, "Those aren't coordinates.");
        }

        try
        {
            return await places.AtAsync(lat, lon, ct) is { } place
                ? TypedResults.Ok(place)
                : AuthEndpoints.Error(StatusCodes.Status404NotFound, "Couldn't find a town there. Search for it by name instead.");
        }
        catch (HttpRequestException)
        {
            return AuthEndpoints.Error(StatusCodes.Status502BadGateway, "Couldn't look that up just now. Try again in a moment.");
        }
    }

    private static async Task<IResult> GetAsync(HttpContext http, ConversationStore store, CancellationToken ct) =>
        TypedResults.Ok(FamiliarView.From(await store.ProfileAsync(http.User.AccountId()!.Value, ct)));

    private static async Task<IResult> UpdateAsync(
        FamiliarView request, HttpContext http, ConversationStore store, IDbContextFactory<FamiliarDbContext> db, CancellationToken ct)
    {
        var name = request.Name?.Trim() ?? string.Empty;
        var nickname = string.IsNullOrWhiteSpace(request.UserNickname) ? null : request.UserNickname.Trim();
        var voice = string.IsNullOrWhiteSpace(request.Voice) ? null : request.Voice.Trim();

        if (name.Length is 0 or > FamiliarProfile.MaxNameLength || nickname?.Length > FamiliarProfile.MaxNameLength)
        {
            return AuthEndpoints.Error(StatusCodes.Status400BadRequest, $"Names are 1-{FamiliarProfile.MaxNameLength} characters.");
        }

        if (voice?.Length > FamiliarProfile.MaxVoiceLength)
        {
            return AuthEndpoints.Error(StatusCodes.Status400BadRequest, "That isn't a voice.");
        }

        // Null leaves it as it was (an app a version behind doesn't send it); empty clears it.
        var homePlace = request.HomePlace?.Trim();
        if (homePlace?.Length > FamiliarProfile.MaxHomePlaceLength)
        {
            return AuthEndpoints.Error(StatusCodes.Status400BadRequest, $"Where you live is at most {FamiliarProfile.MaxHomePlaceLength} characters - a town is enough.");
        }

        if (request.Home is { } picked && HomeProblem(picked) is { } homeProblem)
        {
            return AuthEndpoints.Error(StatusCodes.Status400BadRequest, homeProblem);
        }

        var accountId = http.User.AccountId()!.Value;
        if (request.Appearance is { } appearance)
        {
            await using var context = await db.CreateDbContextAsync(ct);
            var owned = await context.AvatarAssets.Where(a => a.AccountId == accountId).Select(a => a.Id).ToListAsync(ct);
            if (appearance.Problem(owned) is { } problem)
            {
                return AuthEndpoints.Error(StatusCodes.Status400BadRequest, problem);
            }
        }

        var profile = await store.ProfileAsync(accountId, ct);
        profile.Name = name;
        profile.UserNickname = nickname;
        profile.Warmth = FamiliarProfile.Clamp(request.Warmth);
        profile.Playfulness = FamiliarProfile.Clamp(request.Playfulness);
        profile.Verbosity = FamiliarProfile.Clamp(request.Verbosity);
        profile.Formality = FamiliarProfile.Clamp(request.Formality);
        profile.Expressiveness = FamiliarProfile.Clamp(request.Expressiveness);
        profile.Voice = voice;
        profile.VoiceSpeed = ClampSpeed(request.VoiceSpeed);
        profile.Proactive = request.Proactive ?? profile.Proactive;
        profile.ShortGreeting = request.ShortGreeting ?? profile.ShortGreeting;
        profile.Appearance = request.Appearance?.ToJson() ?? profile.Appearance;
        profile.LookUps = request.LookUps ?? profile.LookUps;
        SetHome(profile, request.Home, homePlace);

        return TypedResults.Ok(FamiliarView.From(await store.SaveProfileAsync(profile, ct)));
    }

    /// <summary>What's wrong with a picked home, if anything: it goes into the prompt, so it's held to a place's shape.</summary>
    private static string? HomeProblem(PlaceView home) =>
        string.IsNullOrWhiteSpace(home.Name) || home.Name.Length > 60 || home.Region?.Length > 60 || home.Country?.Length > 60
            || PlaceView.LabelOf(home.Name, home.Region, home.Country).Length > FamiliarProfile.MaxHomePlaceLength
            || home.Name.Any(char.IsControl) || home.Region?.Any(char.IsControl) == true || home.Country?.Any(char.IsControl) == true
            || home.CountryCode.Length is not (0 or 2) || !home.CountryCode.All(char.IsAsciiLetter)
            || double.IsNaN(home.Latitude) || double.IsNaN(home.Longitude) || Math.Abs(home.Latitude) > 90 || Math.Abs(home.Longitude) > 180
                ? "That isn't a place. Pick one from the list."
                : null;

    /// <summary>
    /// A picked place wins; otherwise a typed name (from an app a version behind) or empty to clear. A
    /// typed name keeps the picked place's coordinates only while it's still that place's label.
    /// </summary>
    private static void SetHome(FamiliarProfile profile, PlaceView? picked, string? typed)
    {
        if (picked is not null)
        {
            profile.HomePlace = PlaceView.LabelOf(picked.Name, picked.Region, picked.Country);
            profile.HomeLatitude = PlaceView.Round(picked.Latitude);
            profile.HomeLongitude = PlaceView.Round(picked.Longitude);
            profile.HomeCountryCode = picked.CountryCode.Length == 2 ? picked.CountryCode.ToUpperInvariant() : null;
            profile.HomeRegion = string.IsNullOrWhiteSpace(picked.Region) ? null : picked.Region.Trim();
            return;
        }

        if (typed is null || typed == profile.HomePlace)
        {
            return;
        }

        profile.HomePlace = typed.Length == 0 ? null : typed;
        profile.HomeLatitude = profile.HomeLongitude = null;
        profile.HomeCountryCode = profile.HomeRegion = null;
    }

    private static async Task<IResult> VoicesAsync(ITextToSpeech tts, SpeechOptions options, CancellationToken ct) =>
        TypedResults.Ok(new VoicesView(await tts.VoicesAsync(ct), options.DefaultVoice));

    /// <summary>A short sample in a voice and speed, so Settings can audition before saving.</summary>
    private static async Task<IResult> PreviewAsync(ITextToSpeech tts, SpeechOptions options, string? voice, double? speed, CancellationToken ct)
    {
        try
        {
            var clip = await tts.SynthesizeAsync(
                PreviewSentence,
                string.IsNullOrWhiteSpace(voice) || voice.Length > FamiliarProfile.MaxVoiceLength ? options.DefaultVoice : voice,
                ClampSpeed(speed ?? 1.0),
                ct);
            return TypedResults.File(clip.Audio, clip.ContentType);
        }
        catch (SpeechUnavailableException)
        {
            return AuthEndpoints.Error(StatusCodes.Status503ServiceUnavailable, "Text-to-speech isn't reachable right now.");
        }
    }

    private static double ClampSpeed(double speed) =>
        double.IsNaN(speed) ? 1.0 : Math.Clamp(speed, FamiliarProfile.MinVoiceSpeed, FamiliarProfile.MaxVoiceSpeed);
}
