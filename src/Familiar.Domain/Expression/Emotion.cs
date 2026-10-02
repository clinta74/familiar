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
}
