using System.Globalization;
using System.Text;

namespace Familiar.Agent.Speech;

/// <summary>Text as it should be spoken: no emoji, no markdown, nothing a voice would read out literally.</summary>
public static class SpeechText
{
    public static string Clean(string text)
    {
        // "12:02" would be read digit by digit ("twelve zero two"): in words, as a person says it.
        text = ClockWords.SpeakTimes(text);
        var builder = new StringBuilder(text.Length);
        var enumerator = StringInfo.GetTextElementEnumerator(text);

        while (enumerator.MoveNext())
        {
            var element = enumerator.GetTextElement();
            var category = CharUnicodeInfo.GetUnicodeCategory(element, 0);

            // Emoji and pictographs (OtherSymbol, and anything outside the BMP), markdown emphasis
            // and code markers. The model is told not to use them; this is for when it does.
            if (category is UnicodeCategory.OtherSymbol or UnicodeCategory.Surrogate
                || char.IsSurrogate(element[0])
                || element is "*" or "_" or "`" or "#" or "~")
            {
                continue;
            }

            builder.Append(element);
        }

        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim();
    }
}
