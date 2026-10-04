namespace Familiar.Domain.Expression;

/// <summary>
/// What the familiar is feeling about what it's saying. Maps one-to-one onto VRM's preset
/// expressions (happy, sad, angry, surprised, relaxed) plus neutral and thinking (PLAN.md §4.4).
/// </summary>
public enum Emotion
{
    Neutral = 0,
    Joy = 1,
    Sad = 2,
    Angry = 3,
    Surprised = 4,
    Relaxed = 5,
    Thinking = 6,
}

/// <summary>A one-off body movement the familiar makes mid-sentence.</summary>
public enum Gesture
{
    Nod = 0,
    Shake = 1,
    Tilt = 2,
    Wave = 3,
    Shrug = 4,
    Laugh = 5,
    Wink = 6,
    Sigh = 7,
    Yawn = 8,

    /// <summary>A big happy hop - more than joy's gentle bounce.</summary>
    Excited = 9,
}

/// <summary>
/// A sound effect, not a voice: made by the clients themselves (synthesised, so nothing to fetch),
/// the same whichever voice the familiar has. Vocal sounds (a giggle, a gasp) come later, in its voice.
/// </summary>
public enum SoundEffect
{
    /// <summary>A bubbly pop: an idea, a surprise, something appearing.</summary>
    Pop = 0,

    /// <summary>A springy boing: something silly, a bounce.</summary>
    Boing = 1,

    /// <summary>A twinkle of bright notes: magic, delight, something nice.</summary>
    Sparkle = 2,

    /// <summary>A little fanfare: done it, a reveal, a celebration.</summary>
    Tada = 3,

    /// <summary>A rush of air: off it goes, quick as anything.</summary>
    Whoosh = 4,

    /// <summary>A sad trombone: a gentle, comic "oh dear".</summary>
    Womp = 5,
}
