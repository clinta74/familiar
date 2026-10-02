using System.Globalization;
using System.Text;

namespace Familiar.Agent.Speech;

/// <summary>
/// Cuts streamed reply text into sentences for TTS, so the familiar starts speaking after its first
/// sentence instead of after the whole reply (PLAN.md §3.2).
/// </summary>
/// <remarks>
/// A sentence ends at <c>. ! ? …</c> followed by whitespace, or at a line break. Very short ones
/// ("Oh!") are held and joined to the next, because a TTS call per two-word clip sounds choppy and
/// costs a round trip each. An over-long run without an ending is cut at a comma, so one rambling
/// sentence can't delay the voice. Common abbreviations and decimals don't end sentences.
/// </remarks>
public sealed class SentenceChunker
{
    /// <summary>Shorter sentences are joined to the next.</summary>
    public int MinLength { get; init; } = 12;

    /// <summary>Longer runs are cut at a comma (or, failing that, a space).</summary>
    public int MaxLength { get; init; } = 180;

    private static readonly string[] Abbreviations = ["mr", "mrs", "ms", "dr", "st", "vs", "etc", "e.g", "i.e", "approx", "no"];

    private readonly StringBuilder _buffer = new();

    /// <summary>Adds streamed text; returns any sentences it completed.</summary>
    public IReadOnlyList<string> Add(string text)
    {
        _buffer.Append(text);
        var sentences = new List<string>();

        while (NextBoundary() is { } end)
        {
            var sentence = _buffer.ToString(0, end).Trim();
            _buffer.Remove(0, end);
            if (sentence.Length > 0)
            {
                sentences.Add(sentence);
            }
        }

        return sentences;
    }

    /// <summary>The end of the reply: whatever is left is the last sentence.</summary>
    public string? Flush()
    {
        var rest = _buffer.ToString().Trim();
        _buffer.Clear();
        return rest.Length > 0 ? rest : null;
    }

    /// <summary>Index just past the next complete sentence, or null if there isn't one yet.</summary>
    private int? NextBoundary()
    {
        var text = _buffer.ToString();

        // The last character can't end a sentence yet: whether "3." is "3.14" depends on what follows.
        for (var i = 0; i < text.Length - 1; i++)
        {
            var c = text[i];
            if (c == '\n' && i + 1 >= MinLength)
            {
                return i + 1;
            }

            if (c is not ('.' or '!' or '?' or '…'))
            {
                continue;
            }

            // Runs of punctuation and closing quotes belong to this sentence: "Really?!" / "Yes."
            var end = i + 1;
            while (end < text.Length && text[end] is '.' or '!' or '?' or '…' or '"' or '\'' or '”' or '’' or ')')
            {
                end++;
            }

            if (end >= text.Length || !char.IsWhiteSpace(text[end]))
            {
                continue;
            }

            if (end < MinLength || (c == '.' && IsAbbreviation(text, i)))
            {
                continue;
            }

            return end;
        }

        if (text.Length > MaxLength)
        {
            var cut = text.LastIndexOfAny([',', ';', ':'], MaxLength);
            if (cut < MinLength)
            {
                cut = text.LastIndexOf(' ', MaxLength);
            }

            return cut > 0 ? cut + 1 : MaxLength;
        }

        return null;
    }

    private static bool IsAbbreviation(string text, int dot)
    {
        var start = dot;
        while (start > 0 && (char.IsLetter(text[start - 1]) || text[start - 1] == '.'))
        {
            start--;
        }

        var word = text[start..dot].ToLower(CultureInfo.InvariantCulture);
        return Abbreviations.Contains(word);
    }
}
