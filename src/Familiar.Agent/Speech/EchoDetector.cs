using System.Text;

namespace Familiar.Agent.Speech;

/// <summary>
/// Recognises the familiar hearing itself. Hands-free listening keeps the microphone open while the
/// familiar talks (that's what lets you interrupt it), and on speakers some of its own voice can
/// leak past the browser's echo cancellation and be transcribed. Answering its own words back would
/// start a loop, so a transcript that is mostly what it just said is treated as echo.
/// </summary>
public static class EchoDetector
{
    /// <summary>Shorter transcripts are too little to judge ("yes", "okay") - and are rarely echo.</summary>
    private const int MinWords = 3;

    /// <summary>The share of the transcript's words that must come from the reply.</summary>
    private const double Threshold = 0.8;

    public static bool IsEcho(string transcript, string? lastReply)
    {
        if (string.IsNullOrWhiteSpace(lastReply))
        {
            return false;
        }

        var heard = Words(transcript);
        if (heard.Count < MinWords)
        {
            return false;
        }

        var said = Words(lastReply).ToHashSet(StringComparer.Ordinal);
        var shared = heard.Count(said.Contains);
        return (double)shared / heard.Count >= Threshold;
    }

    private static List<string> Words(string text)
    {
        var words = new List<string>();
        var current = new StringBuilder();

        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c) || c == '\'')
            {
                current.Append(char.ToLowerInvariant(c));
            }
            else if (current.Length > 0)
            {
                words.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0)
        {
            words.Add(current.ToString());
        }

        return words;
    }
}
