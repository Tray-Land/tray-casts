using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using TrayCasts.Formatting;
using TrayCasts.Playback;
using TrayCasts.Services;

namespace TrayCasts.Views;

public abstract class ObservableItem : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (!EqualityComparer<T>.Default.Equals(field, value))
        {
            field = value;
            OnPropertyChanged(name);
        }
    }

    protected void OnPropertyChanged(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// Thumbnails for one flyout's lifetime. Episodes of a show usually share its art, so each URL is
/// decoded once, at display size (feeds serve 3000 px squares). Dropped with the flyout.
/// </summary>
internal sealed class ArtworkCache
{
    private readonly Dictionary<(string, int), BitmapImage> _images = [];

    public BitmapImage? Get(string? url, int size)
    {
        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {
            return null;
        }

        if (!_images.TryGetValue((url, size), out BitmapImage? image))
        {
            image = new BitmapImage
            {
                DecodePixelWidth = size,
                DecodePixelType = DecodePixelType.Logical,
                UriSource = uri,
            };
            _images[(url, size)] = image;
        }

        return image;
    }

    public void Clear() => _images.Clear();
}

/// <summary>An episode row. <see cref="Refresh"/> re-reads play state and progress in place, so
/// lists update without being rebuilt (which would lose the scroll position).</summary>
public sealed class EpisodeItem : ObservableItem
{
    public const int ArtSize = 44;

    private readonly bool _includeShowName;
    private string _meta = string.Empty;
    private double _progress;
    private Visibility _progressVisibility = Visibility.Collapsed;
    private double _titleOpacity = 1;
    private Visibility _playGlyphVisibility = Visibility.Visible;
    private Visibility _nowPlayingVisibility = Visibility.Collapsed;
    private string _nowPlayingGlyph = "";
    private string _accessibleName = string.Empty;

    internal EpisodeItem(Subscription show, Episode episode, bool includeShowName, ArtworkCache art)
    {
        Show = show;
        Episode = episode;
        _includeShowName = includeShowName;

        // In a show's own list, its art on every row is noise; only episode-specific art shows.
        string? artworkUrl = includeShowName ? episode.ArtworkUrl ?? show.ArtworkUrl : episode.ArtworkUrl;
        Artwork = art.Get(artworkUrl, ArtSize);
        ArtVisibility = includeShowName || episode.ArtworkUrl is not null ? Visibility.Visible : Visibility.Collapsed;
        Refresh();
    }

    public Subscription Show { get; }

    public Episode Episode { get; }

    public string Title => Episode.Title;

    public BitmapImage? Artwork { get; }

    public Visibility ArtVisibility { get; }

    public string Meta { get => _meta; private set => Set(ref _meta, value); }

    public double Progress { get => _progress; private set => Set(ref _progress, value); }

    public Visibility ProgressVisibility { get => _progressVisibility; private set => Set(ref _progressVisibility, value); }

    public double TitleOpacity { get => _titleOpacity; private set => Set(ref _titleOpacity, value); }

    public Visibility PlayGlyphVisibility { get => _playGlyphVisibility; private set => Set(ref _playGlyphVisibility, value); }

    public Visibility NowPlayingVisibility { get => _nowPlayingVisibility; private set => Set(ref _nowPlayingVisibility, value); }

    public string NowPlayingGlyph { get => _nowPlayingGlyph; private set => Set(ref _nowPlayingGlyph, value); }

    public string AccessibleName { get => _accessibleName; private set => Set(ref _accessibleName, value); }

    public bool IsPlayed { get; private set; }

    public bool IsCurrent { get; private set; }

    public void Refresh()
    {
        EpisodeProgress? progress = LibraryService.GetProgress(Show.FeedUrl, Episode.Id);
        IsCurrent = PlaybackService.IsCurrent(Show.FeedUrl, Episode.Id);
        IsPlayed = progress?.IsPlayed == true;
        bool playing = IsCurrent && PlaybackService.IsPlaying;

        double? fraction = PlaybackPolicy.ProgressFraction(progress, Episode.DurationSeconds);
        Progress = fraction ?? 0;
        ProgressVisibility = fraction is null ? Visibility.Collapsed : Visibility.Visible;

        string length = IsPlayed ? "Played"
            : fraction is not null && progress is not null && (progress.DurationSeconds ?? Episode.DurationSeconds) is { } total
                ? EpisodeFormat.Remaining(progress.PositionSeconds, total)
                : EpisodeFormat.Duration(Episode.DurationSeconds);
        Meta = EpisodeFormat.Meta(
            _includeShowName ? Show.Title : null,
            EpisodeFormat.Published(Episode.Published, DateTimeOffset.Now, CultureInfo.CurrentCulture),
            length);

        TitleOpacity = IsPlayed && !IsCurrent ? 0.6 : 1;
        PlayGlyphVisibility = IsCurrent ? Visibility.Collapsed : Visibility.Visible;
        NowPlayingVisibility = IsCurrent ? Visibility.Visible : Visibility.Collapsed;
        NowPlayingGlyph = playing ? "" : "";
        AccessibleName = $"{(playing ? "Playing, " : IsCurrent ? "Paused, " : string.Empty)}{Title}, {Meta}";
    }
}

/// <summary>A followed show in the Shows list.</summary>
public sealed class ShowItem : ObservableItem
{
    public const int ArtSize = 48;

    internal ShowItem(Subscription show, ArtworkCache art)
    {
        Show = show;
        Artwork = art.Get(show.ArtworkUrl, ArtSize);
        DateTimeOffset? newest = show.Episodes.Count > 0 ? show.Episodes.Max(e => e.Published) : null;
        Meta = EpisodeFormat.Meta(
            show.Author,
            newest is null ? null : "Latest " + EpisodeFormat.Published(newest, DateTimeOffset.Now, CultureInfo.CurrentCulture).ToLower(CultureInfo.CurrentCulture),
            show.Order switch
            {
                EpisodeOrder.OldestFirst => "Oldest first",
                EpisodeOrder.NewestThenCatchUp => "Catching up",
                _ => null,
            });
        AccessibleName = $"{show.Title}, {Meta}";
    }

    public Subscription Show { get; }

    public string Title => Show.Title;

    public string Meta { get; }

    public string AccessibleName { get; }

    public BitmapImage? Artwork { get; }
}

/// <summary>A search result, or the single "subscribe to this feed address" row.</summary>
public sealed class SearchItem : ObservableItem
{
    public const int ArtSize = 44;

    private bool _isSubscribed;
    private bool _isBusy;

    internal SearchItem(PodcastSearchResult result, bool isSubscribed, ArtworkCache art)
    {
        Result = result;
        Artwork = art.Get(result.ArtworkUrl, ArtSize);
        Meta = EpisodeFormat.Meta(result.Author, result.EpisodeCount is > 0 and var n ? $"{n} episodes" : null);
        _isSubscribed = isSubscribed;
    }

    public PodcastSearchResult Result { get; }

    public string Title => Result.Title;

    public string Meta { get; }

    public BitmapImage? Artwork { get; }

    public bool IsSubscribed
    {
        get => _isSubscribed;
        set
        {
            Set(ref _isSubscribed, value);
            OnStateChanged();
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            Set(ref _isBusy, value);
            OnStateChanged();
        }
    }

    public string StateGlyph => IsSubscribed ? "" : "";

    public Visibility StateGlyphVisibility => IsBusy ? Visibility.Collapsed : Visibility.Visible;

    public string AccessibleName => $"{Title}, {Meta}, {(IsSubscribed ? "following" : "follow")}";

    private void OnStateChanged()
    {
        OnPropertyChanged(nameof(StateGlyph));
        OnPropertyChanged(nameof(StateGlyphVisibility));
        OnPropertyChanged(nameof(AccessibleName));
    }
}
