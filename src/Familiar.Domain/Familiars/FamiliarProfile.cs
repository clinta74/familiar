namespace Familiar.Domain.Familiars;

/// <summary>
/// Who an account's familiar is: what it's called, what it calls you, and how it carries itself.
/// One per account, created with defaults the first time it's needed.
/// </summary>
/// <remarks>
/// The personality traits are 0-1 sliders rather than free text, so the persona prompt they
/// render into stays short, stable (it's part of the cached prompt prefix) and impossible to use
/// as a prompt-injection channel.
/// </remarks>
public sealed class FamiliarProfile
{
    public const int MaxNameLength = 32;

    public Guid Id { get; init; } = Guid.CreateVersion7();

    public Guid AccountId { get; init; }

    public string Name { get; set; } = "Pip";

    /// <summary>What the familiar calls the user. Null means their username.</summary>
    public string? UserNickname { get; set; }

    /// <summary>0 reserved and matter-of-fact, 1 openly affectionate.</summary>
    public double Warmth { get; set; } = 0.75;

    /// <summary>0 earnest, 1 jokey.</summary>
    public double Playfulness { get; set; } = 0.5;

    /// <summary>0 terse, 1 chatty. Spoken replies stay short either way.</summary>
    public double Verbosity { get; set; } = 0.35;

    /// <summary>0 casual, 1 formal.</summary>
    public double Formality { get; set; } = 0.25;

    /// <summary>How strongly the avatar shows emotion: a multiplier on every expression's intensity.</summary>
    public double Expressiveness { get; set; } = 0.8;

    public const int MaxVoiceLength = 64;
    public const double MinVoiceSpeed = 0.7;
    public const double MaxVoiceSpeed = 1.4;

    /// <summary>A TTS voice id (Kokoro's, e.g. "af_heart"). Null means the server's default.</summary>
    public string? Voice { get; set; }

    /// <summary>Speaking rate, 1 = normal.</summary>
    public double VoiceSpeed { get; set; } = 1.0;

    /// <summary>
    /// The familiar may speak first: a greeting when the user comes back, mentioning what's coming up
    /// (PLAN.md §6.3).
    /// </summary>
    public bool Proactive { get; set; } = true;

    /// <summary>When it last spoke first, so it doesn't greet every time a tab is opened.</summary>
    public DateTimeOffset? LastGreetedAt { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; set; }

    public static double Clamp(double value) => double.IsNaN(value) ? 0.5 : Math.Clamp(value, 0, 1);
}
