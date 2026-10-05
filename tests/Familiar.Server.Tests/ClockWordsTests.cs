using Familiar.Agent.Speech;
using Familiar.Server.Scheduling;

namespace Familiar.Server.Tests;

public sealed class ClockWordsTests
{
    [Theory]
    [InlineData(12, 0, "noon")]
    [InlineData(12, 2, "just after noon")]
    [InlineData(11, 58, "nearly noon")]
    [InlineData(0, 1, "just after midnight")]
    [InlineData(15, 0, "three o'clock")]
    [InlineData(15, 10, "ten past three")]
    [InlineData(15, 8, "about ten past three")]
    [InlineData(9, 30, "half past nine")]
    [InlineData(15, 45, "quarter to four")]
    [InlineData(15, 51, "about ten to four")]
    [InlineData(23, 45, "quarter to twelve")]
    [InlineData(23, 59, "nearly midnight")]
    [InlineData(18, 25, "twenty-five past six")]
    public void Roughly_as_you_would_answer_what_time_it_is(int hour, int minute, string expected) =>
        Assert.Equal(expected, ClockWords.Roughly(hour, minute));

    [Theory]
    [InlineData(-10, "about 10 minutes ago")]
    [InlineData(-0.5, "just now")]
    [InlineData(0.5, "in a moment")]
    [InlineData(3, "in a few minutes")]
    [InlineData(22, "in about 20 minutes")]
    [InlineData(60, "in about an hour")]
    [InlineData(95, "in about an hour and a half")]
    [InlineData(200, "in about 3 hours")]
    public void Relative_times_are_rough(double minutes, string expected) =>
        Assert.Equal(expected, ClockWords.Relative(TimeSpan.FromMinutes(minutes)));

    [Theory]
    [InlineData("It's 12:02 right now.", "It's twelve oh two right now.")]
    [InlineData("Your reminder is at 3:30pm.", "Your reminder is at three thirty PM.")]
    [InlineData("See you at 9:00 A.M. sharp", "See you at nine o'clock AM sharp")]
    [InlineData("The score was 3:2 and 10:15:30 is a timestamp", "The score was 3:2 and 10:15:30 is a timestamp")]
    [InlineData("Version 1.12:30 stays", "Version 1.12:30 stays")]
    public void Digit_times_are_spoken_in_words(string text, string expected) =>
        Assert.Equal(expected, ClockWords.SpeakTimes(text));

    [Fact]
    public void A_reminder_later_today_says_how_long_until_it()
    {
        var now = new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal("at 3:00 PM (in about 3 hours)", Spoken.When(now.AddHours(3), TimeZoneInfo.Utc, now));
        Assert.Equal("tomorrow at 9:00 AM", Spoken.When(now.AddHours(21), TimeZoneInfo.Utc, now));
    }
}
