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
    AppearanceView? Appearance = null)
{
    public static FamiliarView From(FamiliarProfile p) =>
        new(p.Name, p.UserNickname, p.Warmth, p.Playfulness, p.Verbosity, p.Formality, p.Expressiveness, p.Voice, p.VoiceSpeed, p.Proactive,
            AppearanceView.Parse(p.Appearance));
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
        profile.Appearance = request.Appearance?.ToJson() ?? profile.Appearance;

        return TypedResults.Ok(FamiliarView.From(await store.SaveProfileAsync(profile, ct)));
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
