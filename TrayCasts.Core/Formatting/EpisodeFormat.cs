using System.Globalization;

namespace TrayCasts.Formatting;

/// <summary>Short, human text for episode lists, the player, and the tray tooltip.</summary>
public static class EpisodeFormat
{
    /// <summary>"Today", "Yesterday", "Monday" (within a week), "Sep 3", or "Sep 3, 2024".</summary>
    public static string Published(DateTimeOffset? published, DateTimeOffset now, CultureInfo culture)
    {
        if (published is not { } date)
        {
            return string.Empty;
        }

        DateTime local = date.ToOffset(now.Offset).Date;
        DateTime today = now.Date;
        int daysAgo = (today - local).Days;
        return daysAgo switch
        {
            < 0 => local.ToString("MMM d", culture), // Scheduled ahead (clock skew or a premiere).
            0 => "Today",
            1 => "Yesterday",
            < 7 => culture.DateTimeFormat.GetDayName(local.DayOfWeek),
            _ when local.Year == today.Year => local.ToString("MMM d", culture),
            _ => local.ToString("MMM d, yyyy", culture),
        };
    }

    /// <summary>"1 hr 5 min", "45 min", "1 min", or empty when unknown.</summary>
    public static string Duration(double? seconds)
    {
        if (seconds is not { } s || s <= 0)
        {
            return string.Empty;
        }

        int minutes = (int)Math.Round(s / 60);
        if (minutes < 1)
        {
            return "1 min";
        }

        int hours = minutes / 60;
        int rest = minutes % 60;
        return hours == 0 ? $"{rest} min" : rest == 0 ? $"{hours} hr" : $"{hours} hr {rest} min";
    }

    /// <summary>"12 min left" for a started episode.</summary>
    public static string Remaining(double positionSeconds, double durationSeconds)
    {
        double left = Math.Max(0, durationSeconds - positionSeconds);
        return left < 60 ? "Almost done" : $"{Duration(left)} left";
    }

    /// <summary>"2:03" or "1:02:03".</summary>
    public static string Clock(double seconds)
    {
        TimeSpan t = TimeSpan.FromSeconds(Math.Max(0, Math.Floor(seconds)));
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes}:{t.Seconds:00}";
    }

    /// <summary>Joins the non-empty parts of a caption with " · ".</summary>
    public static string Meta(params string?[] parts) =>
        string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));

    /// <summary>
    /// The tray tooltip's second line, e.g. "Playing: Show — Episode 12". The podcast name comes
    /// first and is kept whole; a long episode title is what gets truncated.
    /// </summary>
    public static string TrayStatus(NowPlaying nowPlaying, bool isPlaying, int maxLength)
    {
        string lead = $"{(isPlaying ? "Playing" : "Paused")}: {nowPlaying.ShowTitle}";
        string text = $"{lead} — {nowPlaying.Episode.Title}";
        return text.Length <= maxLength ? text : Truncate(text, maxLength);
    }

    private static string Truncate(string text, int maxLength) =>
        text[..Math.Max(0, maxLength - 1)] + "…";
}
