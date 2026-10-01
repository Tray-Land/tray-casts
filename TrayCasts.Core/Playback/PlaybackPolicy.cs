namespace TrayCasts.Playback;

/// <summary>Rules for resuming, finishing, skipping and speed, kept apart from the media player.</summary>
public static class PlaybackPolicy
{
    public static readonly IReadOnlyList<int> SkipBackChoices = [5, 10, 15, 30];
    public static readonly IReadOnlyList<int> SkipForwardChoices = [10, 15, 30, 45, 60];
    public static readonly IReadOnlyList<double> Speeds = [1.0, 1.25, 1.5, 1.75, 2.0, 0.75];

    public const int DefaultSkipBackSeconds = 15;
    public const int DefaultSkipForwardSeconds = 30;

    /// <summary>Resuming backs up a little, so the listener picks up the thread of the sentence.</summary>
    public const double ResumeRewindSeconds = 2;

    /// <summary>Outros and ads: this close to the end counts as finished.</summary>
    public static double FinishedThresholdSeconds(double durationSeconds) => Math.Min(30, durationSeconds * 0.1);

    public static bool IsFinished(double positionSeconds, double? durationSeconds) =>
        durationSeconds is > 0 and var d && d - positionSeconds <= FinishedThresholdSeconds(d);

    /// <summary>Where to start an episode: from the top if new or finished, else just before where it was left.</summary>
    public static double ResumePositionSeconds(EpisodeProgress? progress)
    {
        if (progress is null || progress.IsPlayed || IsFinished(progress.PositionSeconds, progress.DurationSeconds))
        {
            return 0;
        }

        return Math.Max(0, progress.PositionSeconds - ResumeRewindSeconds);
    }

    /// <summary>0..1 for a list's progress bar, or null when the episode is untouched or finished.</summary>
    public static double? ProgressFraction(EpisodeProgress? progress, double? feedDurationSeconds)
    {
        if (progress is null || progress.IsPlayed || progress.PositionSeconds <= 0)
        {
            return null;
        }

        double? duration = progress.DurationSeconds is > 0 ? progress.DurationSeconds : feedDurationSeconds;
        return duration is > 0 and var d ? Math.Clamp(progress.PositionSeconds / d, 0, 1) : null;
    }

    /// <summary>Clamps a stored setting to the offered choices so a hand-edited value can't misbehave.</summary>
    public static int Sanitize(int value, IReadOnlyList<int> choices, int fallback) =>
        choices.Contains(value) ? value : fallback;

    public static double SanitizeSpeed(double value) =>
        Speeds.Any(s => Math.Abs(s - value) < 0.001) ? value : 1.0;

    /// <summary>The speed after <paramref name="current"/> in the cycle 1 → 1.25 → … → 2 → 0.75 → 1.</summary>
    public static double NextSpeed(double current)
    {
        int index = -1;
        for (int i = 0; i < Speeds.Count; i++)
        {
            if (Math.Abs(Speeds[i] - current) < 0.001)
            {
                index = i;
                break;
            }
        }

        return Speeds[(index + 1) % Speeds.Count];
    }

    /// <summary>A seek target kept inside the episode.</summary>
    public static double ClampSeek(double targetSeconds, double? durationSeconds) =>
        durationSeconds is > 0 and var d ? Math.Clamp(targetSeconds, 0, Math.Max(0, d - 1)) : Math.Max(0, targetSeconds);
}
