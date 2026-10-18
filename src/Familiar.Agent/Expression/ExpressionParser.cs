using System.Globalization;
using System.Text;
using Familiar.Domain.Expression;

namespace Familiar.Agent.Expression;

public abstract record ExpressionToken;

public sealed record TextToken(string Text) : ExpressionToken;

public sealed record EmotionToken(Emotion Emotion, double Intensity) : ExpressionToken;

public sealed record GestureToken(Gesture Gesture) : ExpressionToken;

public sealed record SoundToken(SoundEffect Sound) : ExpressionToken;

/// <summary>"[link:2]": show the user the second page this reply's tools found (BACKLOG item 34).</summary>
public sealed record LinkToken(int Number) : ExpressionToken;

/// <summary>A tag that is clearly a tag but means nothing we can show. Neither spoken nor acted.</summary>
public sealed record IgnoredTag(string Tag) : ExpressionToken;

/// <summary>
/// Splits the model's streamed output into speech and the expression tags it's asked to write
/// (PLAN.md §4.4): <c>[joy:0.8]</c>, <c>[sad]</c>, <c>[gesture:nod]</c>, <c>[sound:pop]</c>.
/// </summary>
/// <remarks>
/// <para>
/// Streaming is the whole difficulty: Ollama sends a tag as several fragments ("[", "joy", ":",
/// "1", ".", "0", "]"), so a fragment ending inside a possible tag is held back until the tag
/// resolves. A bracket that turns out not to be a known tag - "[sic]", a long aside, a stray
/// bracket - comes out as ordinary text, so nothing the model says is ever lost.
/// </para>
/// <para>
/// Tags are never spoken: they're removed from the text, along with the space they leave behind.
/// </para>
/// </remarks>
public sealed class ExpressionParser
{
    /// <summary>Longer than any real tag ("[surprised:0.75]" is 16). Past this it's not a tag.</summary>
    private const int MaxTagLength = 24;

    public const double DefaultIntensity = 0.6;

    private readonly StringBuilder _pending = new();
    private bool _trimLeadingSpace = true;

    public IReadOnlyList<ExpressionToken> Feed(string fragment)
    {
        var tokens = new List<ExpressionToken>();
        var text = new StringBuilder();

        foreach (var c in fragment)
        {
            if (_pending.Length == 0)
            {
                if (c == '[')
                {
                    _pending.Append(c);
                }
                else
                {
                    text.Append(c);
                }

                continue;
            }

            if (c == '[')
            {
                // A second bracket before the first closed: the first wasn't a tag.
                text.Append(_pending);
                _pending.Clear().Append(c);
                continue;
            }

            _pending.Append(c);

            if (c == ']')
            {
                if (TryParseTag(_pending.ToString()) is { } tag)
                {
                    EmitText(text, tokens);
                    tokens.Add(tag);
                    _trimLeadingSpace = true;
                }
                else
                {
                    text.Append(_pending);
                }

                _pending.Clear();
            }
            else if (_pending.Length > MaxTagLength || c == '\n')
            {
                text.Append(_pending);
                _pending.Clear();
            }
        }

        EmitText(text, tokens);
        return tokens;
    }

    /// <summary>End of the reply: anything still held back was never a tag.</summary>
    public IReadOnlyList<ExpressionToken> Flush()
    {
        var tokens = new List<ExpressionToken>();
        var text = new StringBuilder().Append(_pending);
        _pending.Clear();
        EmitText(text, tokens);
        return tokens;
    }

    private void EmitText(StringBuilder text, List<ExpressionToken> tokens)
    {
        if (text.Length == 0)
        {
            return;
        }

        var value = text.ToString();
        text.Clear();

        // The tag was removed; don't leave its space behind ("Great! [gesture:nod] So" → "Great! So").
        if (_trimLeadingSpace)
        {
            value = value.TrimStart();
            if (value.Length == 0)
            {
                return;
            }

            _trimLeadingSpace = false;
        }

        tokens.Add(new TextToken(value));
    }

    internal static ExpressionToken? TryParseTag(string tag)
    {
        // "[name]" or "[name:value]"
        var inner = tag.AsSpan(1, tag.Length - 2).Trim();
        var colon = inner.IndexOf(':');
        var name = (colon < 0 ? inner : inner[..colon]).Trim().ToString();
        var value = colon < 0 ? string.Empty : inner[(colon + 1)..].Trim().ToString();

        // "[gesture:x]" is unambiguously a tag, so it's never spoken: a gesture we can't perform
        // ("[gesture:dance]") is dropped rather than read out.
        if (name.Equals("gesture", StringComparison.OrdinalIgnoreCase))
        {
            return TryParseGesture(value, out var gesture) ? new GestureToken(gesture) : new IgnoredTag(tag);
        }

        // Likewise "[sound:x]": a sound we can't make is dropped, not read out.
        if (name.Equals("sound", StringComparison.OrdinalIgnoreCase))
        {
            return Enum.TryParse<SoundEffect>(value.Replace("-", string.Empty, StringComparison.Ordinal), ignoreCase: true, out var sound)
                && Enum.IsDefined(sound) && !int.TryParse(value, out _)
                ? new SoundToken(sound)
                : new IgnoredTag(tag);
        }

        // "[link:n]": a page a tool found, shown as a card; never read out, whatever n is.
        if (name.Equals("link", StringComparison.OrdinalIgnoreCase))
        {
            return int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number) && number is > 0 and < 100
                ? new LinkToken(number)
                : new IgnoredTag(tag);
        }

        // Stage directions the model writes despite being asked not to: "[laughs]", "[nods]".
        if (value.Length == 0 && TryParseGesture(name, out var direction))
        {
            return new GestureToken(direction);
        }

        // A sound written without "sound:", "[drumroll]": asked for a drumroll, the model wrote just that.
        if (value.Length == 0 && TryParseSound(name, out var bare))
        {
            return new SoundToken(bare);
        }

        if (!TryParseEmotion(name, out var emotion))
        {
            return null;
        }

        // A known emotion with a garbled intensity is still that emotion.
        return value.Length > 0 && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var intensity)
            ? new EmotionToken(emotion, Math.Clamp(intensity, 0, 1))
            : new EmotionToken(emotion, DefaultIntensity);
    }

    private static bool TryParseSound(string name, out SoundEffect sound)
    {
        var key = name.Trim().ToLowerInvariant().Replace("-", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal);
        key = key switch
        {
            "sadtrombone" => "womp",
            "fanfare" => "tada",
            _ => key,
        };
        return Enum.TryParse(key, ignoreCase: true, out sound) && Enum.IsDefined(sound) && !int.TryParse(key, out _);
    }

    private static bool TryParseGesture(string name, out Gesture gesture)
    {
        gesture = name.Trim().ToLowerInvariant() switch
        {
            "nod" or "nods" or "nodding" or "agree" => Gesture.Nod,
            "shake" or "shakes" or "headshake" or "shakes head" or "shake head" => Gesture.Shake,
            "tilt" or "tilts" or "head tilt" or "tilts head" or "tilt head" or "ponder" => Gesture.Tilt,
            "wave" or "waves" or "waving" => Gesture.Wave,
            "shrug" or "shrugs" or "shrugging" => Gesture.Shrug,
            "laugh" or "laughs" or "laughing" or "giggle" or "giggles" or "chuckle" or "chuckles" or "haha" => Gesture.Laugh,
            "wink" or "winks" or "winking" => Gesture.Wink,
            "sigh" or "sighs" or "sighing" => Gesture.Sigh,
            "yawn" or "yawns" or "yawning" => Gesture.Yawn,
            "excited" or "bounce" or "bounces" or "hop" or "hops" or "jump" or "jumps" or "cheer" or "cheers" => Gesture.Excited,
            _ => (Gesture)(-1),
        };

        return Enum.IsDefined(gesture);
    }

    private static bool TryParseEmotion(string name, out Emotion emotion)
    {
        // Names only: Enum.TryParse would also accept "3" as Angry.
        foreach (var candidate in Enum.GetValues<Emotion>())
        {
            if (name.Equals(candidate.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                emotion = candidate;
                return true;
            }
        }

        // Words the model reaches for that mean one of ours.
        emotion = name.ToLowerInvariant() switch
        {
            "happy" or "excited" => Emotion.Joy,
            "sadness" or "sorry" => Emotion.Sad,
            "calm" => Emotion.Relaxed,
            "curious" => Emotion.Thinking,
            _ => (Emotion)(-1),
        };

        return Enum.IsDefined(emotion);
    }
}
