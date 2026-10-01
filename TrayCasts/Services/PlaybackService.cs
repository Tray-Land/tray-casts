using Microsoft.UI.Dispatching;
using TrayCasts.Formatting;
using TrayCasts.Playback;
using TrayCasts.Storage;
using Windows.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Streams;

namespace TrayCasts.Services;

/// <summary>
/// The one media player, app lifetime. This is the documented exception to "no work while
/// hidden": the user starts playback, and it carries on with no window open, controlled from the
/// tray menu, the media keys, and the Windows media overlay (the player's command manager).
/// </summary>
/// <remarks>
/// The <see cref="MediaPlayer"/> exists only while it's needed: it's created on play and released
/// after <see cref="ReleaseAfterPause"/> paused, with the position saved, so a paused-and-forgotten
/// episode doesn't keep the media pipeline in memory. All state is touched on the UI thread;
/// player events arrive on background threads and are marshaled, and each one checks that its
/// player is still the live one (SMTC calls on a released player fail fast natively, see Trdo).
/// </remarks>
internal static class PlaybackService
{
    private static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ReleaseAfterPause = TimeSpan.FromMinutes(15);

    private static DispatcherQueue? _dispatcher;
    private static DispatcherQueueTimer? _saveTimer;
    private static DispatcherQueueTimer? _releaseTimer;
    private static MediaPlayer? _player;

    // Where the episode will start (or resume) when there's no player to ask.
    private static double _pendingPositionSeconds;

    // True from opening a source until it first plays. The session reports a transient Paused
    // while it seeks to the resume position, which mustn't read as the listener pausing.
    private static bool _isOpening;

    /// <summary>The episode in the player, playing or not. Survives restarts.</summary>
    public static NowPlaying? Current { get; private set; }

    /// <summary>True from Play until a pause, the end, or an error, including while buffering.</summary>
    public static bool IsPlaying { get; private set; }

    public static bool IsBuffering { get; private set; }

    public static double Rate { get; private set; } = 1.0;

    /// <summary>Why the current episode can't play, or null.</summary>
    public static string? Error { get; private set; }

    /// <summary>Raised on the UI thread when the episode, play state, speed, or error changes.</summary>
    public static event EventHandler? StateChanged;

    public static double PositionSeconds =>
        _player?.Source is not null ? _player.PlaybackSession.Position.TotalSeconds : _pendingPositionSeconds;

    /// <summary>The player's duration once known, else the feed's (which is often approximate).</summary>
    public static double? DurationSeconds =>
        _player?.Source is not null && _player.PlaybackSession.NaturalDuration.TotalSeconds is > 0 and var d
            ? d
            : Current?.Episode.DurationSeconds;

    public static void Initialize(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        _saveTimer = dispatcher.CreateTimer();
        _saveTimer.Interval = SaveInterval;
        _saveTimer.Tick += (_, _) => SaveProgress();

        _releaseTimer = dispatcher.CreateTimer();
        _releaseTimer.Interval = ReleaseAfterPause;
        _releaseTimer.IsRepeating = false;
        _releaseTimer.Tick += (_, _) => ReleasePlayerIfIdle();

        Rate = SettingsService.PlaybackRate;
        Current = SettingsService.NowPlaying;
        if (Current is not null)
        {
            _pendingPositionSeconds = PlaybackPolicy.ResumePositionSeconds(
                LibraryService.GetProgress(Current.FeedUrl, Current.Episode.Id));
        }
    }

    public static bool IsCurrent(string feedUrl, string episodeId) =>
        Current is { } c && c.FeedUrl == feedUrl && c.Episode.Id == episodeId;

    /// <summary>Plays an episode from where it was left; for the current one, toggles instead.</summary>
    public static void PlayOrToggle(NowPlaying episode)
    {
        if (IsCurrent(episode.FeedUrl, episode.Episode.Id) && Error is null)
        {
            TogglePlayPause();
            return;
        }

        SaveProgress();
        Current = episode;
        SettingsService.NowPlaying = episode;
        _pendingPositionSeconds = PlaybackPolicy.ResumePositionSeconds(
            LibraryService.GetProgress(episode.FeedUrl, episode.Episode.Id));
        OpenAndPlay();
    }

    public static void TogglePlayPause()
    {
        if (Current is null)
        {
            return;
        }

        if (_player?.Source is null || Error is not null)
        {
            OpenAndPlay();
        }
        else if (IsPlaying)
        {
            _isOpening = false;
            _player.Pause();
            OnPaused();
        }
        else
        {
            _player.Play();
            OnPlaying();
        }
    }

    public static void SkipBack() => SeekTo(PositionSeconds - SettingsService.SkipBackSeconds);

    public static void SkipForward() => SeekTo(PositionSeconds + SettingsService.SkipForwardSeconds);

    public static void SeekTo(double seconds)
    {
        if (Current is null)
        {
            return;
        }

        double target = PlaybackPolicy.ClampSeek(seconds, DurationSeconds);
        if (_player?.Source is not null && _player.PlaybackSession.CanSeek)
        {
            _player.PlaybackSession.Position = TimeSpan.FromSeconds(target);
        }
        else
        {
            _pendingPositionSeconds = target;
        }

        if (!IsPlaying)
        {
            SaveProgress();
        }

        RaiseStateChanged();
    }

    public static void CycleRate()
    {
        Rate = PlaybackPolicy.NextSpeed(Rate);
        SettingsService.PlaybackRate = Rate;
        if (_player?.Source is not null)
        {
            _player.PlaybackSession.PlaybackRate = Rate;
        }

        RaiseStateChanged();
    }

    /// <summary>Exit: saves the position synchronously and releases the player.</summary>
    public static void Shutdown()
    {
        _saveTimer?.Stop();
        _releaseTimer?.Stop();
        RecordProgress();
        LibraryService.FlushProgress();
        DisposePlayer();
    }

    private static void OpenAndPlay()
    {
        if (Current is not { } current)
        {
            return;
        }

        Error = null;
        MediaPlayer player = EnsurePlayer();
        MediaPlaybackItem item = new(MediaSource.CreateFromUri(new Uri(current.Episode.AudioUrl)));
        ApplyDisplayProperties(item, current);
        _isOpening = true;
        player.Source = item;
        player.Play();
        IsBuffering = true;
        OnPlaying();
    }

    private static MediaPlayer EnsurePlayer()
    {
        if (_player is not null)
        {
            return _player;
        }

        MediaPlayer player = new()
        {
            AudioCategory = MediaPlayerAudioCategory.Media,
            AutoPlay = false,
        };

        // The command manager connects the player to the media keys and the Windows media overlay.
        // Next / previous are repurposed as skip forward / back, the way podcast apps use them.
        player.CommandManager.IsEnabled = true;
        player.CommandManager.NextBehavior.EnablingRule = MediaCommandEnablingRule.Always;
        player.CommandManager.PreviousBehavior.EnablingRule = MediaCommandEnablingRule.Always;
        player.CommandManager.NextReceived += (sender, args) =>
        {
            args.Handled = true;
            OnPlayerThread(player, SkipForward);
        };
        player.CommandManager.PreviousReceived += (sender, args) =>
        {
            args.Handled = true;
            OnPlayerThread(player, SkipBack);
        };

        player.MediaOpened += (_, _) => OnPlayerThread(player, () =>
        {
            // Resume where the listener left off, at their speed.
            if (_pendingPositionSeconds > 0 && player.PlaybackSession.CanSeek)
            {
                player.PlaybackSession.Position = TimeSpan.FromSeconds(_pendingPositionSeconds);
            }

            player.PlaybackSession.PlaybackRate = Rate;
            RaiseStateChanged();
        });
        player.PlaybackSession.PlaybackStateChanged += (session, _) => OnPlayerThread(player, () =>
        {
            MediaPlaybackState state = session.PlaybackState;
            IsBuffering = state is MediaPlaybackState.Opening or MediaPlaybackState.Buffering;
            if (state == MediaPlaybackState.Playing)
            {
                _isOpening = false;
                OnPlaying();
            }
            else if (state == MediaPlaybackState.Paused && IsPlaying && !_isOpening)
            {
                // Paused from outside the app: a media key, the overlay, or a headset.
                OnPaused();
            }
            else
            {
                RaiseStateChanged();
            }
        });
        player.MediaEnded += (_, _) => OnPlayerThread(player, OnEnded);
        player.MediaFailed += (_, args) => OnPlayerThread(player, () => OnFailed(args));

        _player = player;
        return player;
    }

    private static void ApplyDisplayProperties(MediaPlaybackItem item, NowPlaying nowPlaying)
    {
        try
        {
            MediaItemDisplayProperties props = item.GetDisplayProperties();
            props.Type = MediaPlaybackType.Music;
            props.MusicProperties.Title = nowPlaying.Episode.Title;
            props.MusicProperties.Artist = nowPlaying.ShowTitle;
            if (nowPlaying.ArtworkUrl is { } art && Uri.TryCreate(art, UriKind.Absolute, out Uri? artUri))
            {
                props.Thumbnail = RandomAccessStreamReference.CreateFromUri(artUri);
            }

            item.ApplyDisplayProperties(props);
        }
        catch (Exception)
        {
            // Overlay metadata is cosmetic; play without it.
        }
    }

    private static void OnPlaying()
    {
        IsPlaying = true;
        _releaseTimer?.Stop();
        _saveTimer?.Start();
        RaiseStateChanged();
    }

    private static void OnPaused()
    {
        IsPlaying = false;
        IsBuffering = false;
        _saveTimer?.Stop();
        SaveProgress();
        _releaseTimer?.Stop();
        _releaseTimer?.Start();
        RaiseStateChanged();
    }

    private static void OnEnded()
    {
        IsPlaying = false;
        IsBuffering = false;
        _saveTimer?.Stop();
        if (Current is { } current)
        {
            LibraryService.SetPlayed(current.FeedUrl, current.Episode.Id, played: true, DurationSeconds);
        }

        // A finished episode starts over if played again.
        _pendingPositionSeconds = 0;
        DisposePlayer();
        RaiseStateChanged();

        if (SettingsService.AutoPlayNext && Current is { } finished)
        {
            PlayNextAfter(finished);
        }
    }

    /// <summary>Carries on with what's next (<see cref="LibraryOps.NextToPlay"/>), resuming it where it was left.</summary>
    private static void PlayNextAfter(NowPlaying finished)
    {
        ShowEpisode? next = LibraryOps.NextToPlay(
            LibraryService.Library.Subscriptions, LibraryService.Progress, finished.FeedUrl, finished.Episode.Id);
        LibraryService.ReleaseIfUnviewed();
        if (next is not null)
        {
            PlayOrToggle(LibraryOps.ToNowPlaying(next.Show, next.Episode));
        }
    }

    private static void OnFailed(MediaPlayerFailedEventArgs args)
    {
        IsPlaying = false;
        IsBuffering = false;
        _saveTimer?.Stop();
        Error = args.Error switch
        {
            MediaPlayerError.SourceNotSupported => "This episode's audio format isn't supported.",
            MediaPlayerError.NetworkError when !ConnectivityService.IsInternetAvailable => "No internet connection.",
            MediaPlayerError.NetworkError => "Couldn't download this episode.",
            _ => "This episode can't be played.",
        };

        // The broken session's position can't be trusted; the next Play reopens from the last save.
        if (Current is { } current)
        {
            _pendingPositionSeconds = PlaybackPolicy.ResumePositionSeconds(
                LibraryService.GetProgress(current.FeedUrl, current.Episode.Id));
        }

        DisposePlayer();
        RaiseStateChanged();
    }

    private static void ReleasePlayerIfIdle()
    {
        if (IsPlaying || _player is null)
        {
            return;
        }

        SaveProgress();
        _pendingPositionSeconds = PositionSeconds;
        DisposePlayer();
        MemoryService.ReleaseIdle();
    }

    private static void SaveProgress()
    {
        if (RecordProgress())
        {
            LibraryService.SaveProgress();
        }
    }

    /// <summary>Copies the player's position into the progress book. False when there's nothing to record.</summary>
    private static bool RecordProgress()
    {
        if (Current is not { } current || Error is not null)
        {
            return false;
        }

        double position = PositionSeconds;
        double? duration = DurationSeconds;
        EpisodeProgress? previous = LibraryService.GetProgress(current.FeedUrl, current.Episode.Id);
        if (position <= 0 && previous is null)
        {
            return false; // Never started; don't mark it touched.
        }

        bool finished = PlaybackPolicy.IsFinished(position, duration);
        LibraryService.SetProgress(current.FeedUrl, current.Episode.Id, new EpisodeProgress
        {
            PositionSeconds = finished ? 0 : position,
            DurationSeconds = duration,
            IsPlayed = finished || (previous?.IsPlayed == true && position <= 0),
            UpdatedUtc = DateTimeOffset.UtcNow,
        });
        return true;
    }

    private static void DisposePlayer()
    {
        if (_player is not { } player)
        {
            return;
        }

        // Clear first: every deferred callback checks _player before touching it.
        _player = null;
        try
        {
            player.Pause();
            player.Source = null;
            player.Dispose();
        }
        catch (Exception)
        {
            // Already torn down.
        }
    }

    /// <summary>Runs a player callback on the UI thread, only if that player is still the live one.</summary>
    private static void OnPlayerThread(MediaPlayer player, Action action) =>
        _dispatcher?.TryEnqueue(() =>
        {
            if (ReferenceEquals(player, _player))
            {
                action();
            }
        });

    private static void RaiseStateChanged() => StateChanged?.Invoke(null, EventArgs.Empty);

    /// <summary>Tooltip text for the tray icon.</summary>
    public static string? TrayStatus(int maxLength) =>
        Current is null ? null
        : Error is not null ? "Can't play: " + Current.Episode.Title
        : _player is null && !IsPlaying ? null
        : EpisodeFormat.TrayStatus(Current, IsPlaying, maxLength);
}
