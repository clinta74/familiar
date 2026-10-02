using Familiar.Domain.Expression;

namespace Familiar.Agent.Expression;

/// <summary>
/// An emotion for a reply the model forgot to tag, so a model that drifts from the tag grammar
/// degrades to "less expressive" rather than to a blank face (PLAN.md §4.4).
/// </summary>
/// <remarks>Deliberately crude: a few cue words. Only used when there's no tag at all.</remarks>
public static class EmotionFallback
{
    private static readonly string[] SadCues = ["sorry", "unfortunately", "sad", "miss you", "that's hard", "worried"];
    private static readonly string[] JoyCues = ["congrat", "amazing", "wonderful", "great", "yay", "love", "awesome", "glad"];
    private static readonly string[] SurpriseCues = ["wow", "whoa", "really?", "no way"];

    public static (Emotion Emotion, double Intensity) For(string reply)
    {
        var text = reply.ToLowerInvariant();

        if (SadCues.Any(text.Contains))
        {
            return (Emotion.Sad, 0.5);
        }

        if (SurpriseCues.Any(text.Contains))
        {
            return (Emotion.Surprised, 0.5);
        }

        if (JoyCues.Any(text.Contains) || text.Contains('!', StringComparison.Ordinal))
        {
            return (Emotion.Joy, 0.5);
        }

        return (Emotion.Neutral, 0.3);
    }
}
