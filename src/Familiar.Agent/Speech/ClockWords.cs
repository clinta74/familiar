using System.Text.RegularExpressions;

namespace Familiar.Agent.Speech;

/// <summary>
/// Times as people say them (BACKLOG item 16): "just after noon", "about ten past three", "in about
/// twenty minutes" - not "twelve zero two", which is how a voice reads "12:02".
/// </summary>
public static partial class ClockWords
{
    private static readonly string[] Ones =
    [
        "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten",
        "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen",
    ];

    private static readonly string[] Tens = ["", "", "twenty", "thirty", "forty", "fifty"];

    /// <summary>0-59 in words: "seven", "twenty-five".</summary>
    public static string Number(int n) =>
        n < 20 ? Ones[n] : Tens[n / 10] + (n % 10 == 0 ? "" : "-" + Ones[n % 10]);

    private static string Hour(int hour24) => Ones[hour24 % 12 == 0 ? 12 : hour24 % 12];

    /// <summary>
    /// Roughly, as you'd answer "what time is it?": "noon", "just after noon", "nearly three",
    /// "about ten past three", "quarter to four", "half past nine".
    /// </summary>
    public static string Roughly(int hour24, int minute)
    {
        var nearest = (int)Math.Round(minute / 5.0, MidpointRounding.AwayFromZero) * 5;
        var off = minute - nearest;
        var hour = hour24;
        if (nearest == 60)
        {
            nearest = 0;
            hour = (hour24 + 1) % 24;
        }

        if (nearest == 0)
        {
            var name = hour switch { 0 => "midnight", 12 => "noon", _ => Hour(hour) };
            var exact = hour is 0 or 12 ? name : $"{name} o'clock";
            return off switch { > 0 => $"just after {name}", < 0 => $"nearly {name}", _ => exact };
        }

        var phrase = nearest switch
        {
            15 => $"quarter past {Hour(hour)}",
            30 => $"half past {Hour(hour)}",
            45 => $"quarter to {Hour(hour + 1)}",
            < 30 => $"{Number(nearest)} past {Hour(hour)}",
            _ => $"{Number(60 - nearest)} to {Hour(hour + 1)}",
        };
        return off == 0 ? phrase : $"about {phrase}";
    }

    /// <summary>Exactly, but in words, as a voice should read a clock time: "twelve oh two", "three thirty", "nine o'clock".</summary>
    public static string Exactly(int hour24, int minute) =>
        minute switch
        {
            0 => $"{Hour(hour24)} o'clock",
            < 10 => $"{Hour(hour24)} oh {Ones[minute]}",
            _ => $"{Hour(hour24)} {Number(minute)}",
        };

    /// <summary>
    /// How far off something is, roughly: "in a moment", "in a few minutes", "in about twenty
    /// minutes", "in about an hour and a half", "in about 3 hours"; or as long ago ("a few minutes ago").
    /// </summary>
    public static string Relative(TimeSpan span)
    {
        var ago = span < TimeSpan.Zero;
        var length = ago ? -span : span;
        var words = length.TotalMinutes switch
        {
            < 1 => null,
            < 5 => "a few minutes",
            < 55 => $"about {(int)Math.Round(length.TotalMinutes / 5) * 5} minutes",
            < 75 => "about an hour",
            < 105 => "about an hour and a half",
            _ => $"about {(int)Math.Round(length.TotalHours)} hours",
        };

        return words is null ? (ago ? "just now" : "in a moment") : ago ? $"{words} ago" : $"in {words}";
    }

    /// <summary>
    /// Digit clock times in a reply ("12:02", "3:30pm", "9:00 AM") in words, so the voice says "twelve
    /// oh two" rather than reading digits. Anything that isn't a clock time is left alone.
    /// </summary>
    public static string SpeakTimes(string text) =>
        DigitTime().Replace(text, match =>
        {
            var hour = int.Parse(match.Groups["h"].Value, System.Globalization.CultureInfo.InvariantCulture);
            var minute = int.Parse(match.Groups["m"].Value, System.Globalization.CultureInfo.InvariantCulture);
            if (hour > 23 || minute > 59)
            {
                return match.Value;
            }

            var half = match.Groups["ampm"].Value.Replace(".", "", StringComparison.Ordinal).ToUpperInvariant();
            return Exactly(hour, minute) + (half.Length > 0 ? $" {half}" : "");
        });

    [GeneratedRegex(@"(?<![\d:.])(?<h>\d{1,2}):(?<m>\d{2})(?![\d:])(?:\s?(?<ampm>[AaPp]\.?[Mm])(?:\.(?=\s+[a-z]))?(?![A-Za-z]))?")]
    private static partial Regex DigitTime();
}
