using System.Globalization;
using TrayCasts.Formatting;
using TrayCasts.Playback;

namespace TrayCasts.Tests;

[TestClass]
public sealed class PlaybackPolicyTests
{
    [TestMethod]
    public void ResumePosition_NoProgress_StartsAtZero()
    {
        Assert.AreEqual(0, PlaybackPolicy.ResumePositionSeconds(null));
    }

    [TestMethod]
    public void ResumePosition_MidEpisode_BacksUpTwoSeconds()
    {
        EpisodeProgress progress = new() { PositionSeconds = 600, DurationSeconds = 3600 };

        Assert.AreEqual(598, PlaybackPolicy.ResumePositionSeconds(progress));
    }

    [TestMethod]
    public void ResumePosition_NearEndOrPlayed_StartsOver()
    {
        Assert.AreEqual(0, PlaybackPolicy.ResumePositionSeconds(new EpisodeProgress { PositionSeconds = 3580, DurationSeconds = 3600 }));
        Assert.AreEqual(0, PlaybackPolicy.ResumePositionSeconds(new EpisodeProgress { PositionSeconds = 100, DurationSeconds = 3600, IsPlayed = true }));
    }

    [TestMethod]
    public void IsFinished_ShortEpisode_UsesTenPercent()
    {
        // 2 minutes: the threshold is 12 s, not 30.
        Assert.IsFalse(PlaybackPolicy.IsFinished(100, 120));
        Assert.IsTrue(PlaybackPolicy.IsFinished(110, 120));
    }

    [TestMethod]
    public void IsFinished_UnknownDuration_IsFalse()
    {
        Assert.IsFalse(PlaybackPolicy.IsFinished(5000, null));
    }

    [TestMethod]
    public void ProgressFraction_PrefersPlayerDurationOverFeed()
    {
        EpisodeProgress progress = new() { PositionSeconds = 300, DurationSeconds = 1200 };

        Assert.AreEqual(0.25, PlaybackPolicy.ProgressFraction(progress, 3600));
        Assert.AreEqual(0.5, PlaybackPolicy.ProgressFraction(progress with { DurationSeconds = null }, 600));
        Assert.IsNull(PlaybackPolicy.ProgressFraction(progress with { IsPlayed = true }, 600));
    }

    [TestMethod]
    public void NextSpeed_CyclesAndRecoversFromOddValues()
    {
        Assert.AreEqual(1.25, PlaybackPolicy.NextSpeed(1.0));
        Assert.AreEqual(0.75, PlaybackPolicy.NextSpeed(2.0));
        Assert.AreEqual(1.0, PlaybackPolicy.NextSpeed(0.75));
        Assert.AreEqual(1.0, PlaybackPolicy.NextSpeed(3.3));
    }

    [TestMethod]
    public void Sanitize_UnknownChoice_FallsBack()
    {
        Assert.AreEqual(30, PlaybackPolicy.Sanitize(30, PlaybackPolicy.SkipForwardChoices, 15));
        Assert.AreEqual(15, PlaybackPolicy.Sanitize(1000, PlaybackPolicy.SkipForwardChoices, 15));
        Assert.AreEqual(1.0, PlaybackPolicy.SanitizeSpeed(9));
    }

    [TestMethod]
    public void ClampSeek_StaysInsideEpisode()
    {
        Assert.AreEqual(0, PlaybackPolicy.ClampSeek(-10, 100));
        Assert.AreEqual(99, PlaybackPolicy.ClampSeek(500, 100));
        Assert.AreEqual(500, PlaybackPolicy.ClampSeek(500, null));
    }
}

[TestClass]
public sealed class EpisodeFormatTests
{
    private static readonly CultureInfo EnUs = CultureInfo.GetCultureInfo("en-US");
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 15, 0, 0, TimeSpan.FromHours(-5)); // Wednesday

    [TestMethod]
    [DataRow(0, "Today")]
    [DataRow(1, "Yesterday")]
    [DataRow(2, "Monday")]
    [DataRow(6, "Thursday")]
    [DataRow(10, "Sep 20")]
    [DataRow(400, "Aug 26, 2025")]
    public void Published_RelativeToNow(int daysAgo, string expected)
    {
        Assert.AreEqual(expected, EpisodeFormat.Published(Now.AddDays(-daysAgo), Now, EnUs));
    }

    [TestMethod]
    public void Published_UsesLocalDayNotUtcDay()
    {
        // 02:00 UTC on Oct 1 is still Sep 30 at UTC-5.
        DateTimeOffset utc = new(2026, 10, 1, 2, 0, 0, TimeSpan.Zero);

        Assert.AreEqual("Today", EpisodeFormat.Published(utc, Now, EnUs));
    }

    [TestMethod]
    [DataRow(null, "")]
    [DataRow(20.0, "1 min")]
    [DataRow(2730.0, "46 min")]
    [DataRow(3600.0, "1 hr")]
    [DataRow(3900.0, "1 hr 5 min")]
    public void Duration_Formats(double? seconds, string expected)
    {
        Assert.AreEqual(expected, EpisodeFormat.Duration(seconds));
    }

    [TestMethod]
    public void Clock_Formats()
    {
        Assert.AreEqual("0:00", EpisodeFormat.Clock(-3));
        Assert.AreEqual("2:03", EpisodeFormat.Clock(123.9));
        Assert.AreEqual("1:02:03", EpisodeFormat.Clock(3723));
    }

    [TestMethod]
    public void Remaining_Formats()
    {
        Assert.AreEqual("12 min left", EpisodeFormat.Remaining(600, 1320));
        Assert.AreEqual("Almost done", EpisodeFormat.Remaining(1300, 1320));
    }

    [TestMethod]
    public void Meta_SkipsEmptyParts()
    {
        Assert.AreEqual("Show · Today", EpisodeFormat.Meta("Show", "", null, "Today"));
    }

    [TestMethod]
    public void TrayStatus_TruncatesWithEllipsis()
    {
        NowPlaying np = new()
        {
            FeedUrl = "f",
            ShowTitle = "Show",
            Episode = new Episode { Id = "1", Title = new string('x', 200), AudioUrl = "https://x.test/a.mp3" },
        };

        string status = EpisodeFormat.TrayStatus(np, isPlaying: true, maxLength: 100);

        Assert.HasCount(100, status);
        Assert.StartsWith("Playing: ", status);
        Assert.EndsWith("…", status);
    }

    [TestMethod]
    public void TrayStatus_ShowsPodcastNameFirst()
    {
        NowPlaying np = new()
        {
            FeedUrl = "f",
            ShowTitle = "My Show",
            Episode = new Episode { Id = "1", Title = new string('x', 200), AudioUrl = "https://x.test/a.mp3" },
        };

        string status = EpisodeFormat.TrayStatus(np, isPlaying: true, maxLength: 100);

        Assert.StartsWith("Playing: My Show — ", status);
    }
}
