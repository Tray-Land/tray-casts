using System.Collections.ObjectModel;
using System.Globalization;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using TrayCasts.Feeds;
using TrayCasts.Formatting;
using TrayCasts.Services;
using TrayCasts.Storage;

namespace TrayCasts.Views;

/// <summary>
/// The flyout's content: latest episodes, followed shows, one show's episodes, search, and the
/// now-playing card. Feeds refresh only while the flyout is on screen (see
/// <see cref="ForegroundPoller"/>); playback carries on without it.
/// </summary>
public sealed partial class FlyoutPage : Page, IDisposable
{
    /// <summary>Podcasts publish a few times a week at most; this is plenty while the flyout is open.</summary>
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan SearchDelay = TimeSpan.FromMilliseconds(400);
    private const int LatestCount = 50;
    private const int NowPlayingArtSize = 40;

    private readonly ForegroundPoller _poller;
    private readonly DispatcherQueueTimer _positionTimer;
    private readonly DispatcherQueueTimer _searchTimer;
    private readonly ArtworkCache _art = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ObservableCollection<EpisodeItem> _latest = [];
    private readonly ObservableCollection<ShowItem> _shows = [];
    private readonly ObservableCollection<EpisodeItem> _showEpisodes = [];
    private readonly ObservableCollection<SearchItem> _results = [];

    private enum View
    {
        Browse,
        Show,
        Add,
    }

    private View _view = View.Browse;
    private Subscription? _openShow;
    private int _renderedVersion = -1;
    private bool _isVisible;
    private CancellationTokenSource? _searchCts;
    private string _lastSearch = string.Empty;
    private bool _updatingSlider;
    private bool _isDraggingSlider;

    public FlyoutPage()
    {
        InitializeComponent();
        LatestList.ItemsSource = _latest;
        ShowsList.ItemsSource = _shows;
        ShowEpisodesList.ItemsSource = _showEpisodes;
        SearchList.ItemsSource = _results;

        _poller = new ForegroundPoller(DispatcherQueue, RefreshInterval, RefreshAsync, StaleAfter, SettingsService.LastRefreshedUtc);
        _poller.Failed += (_, ex) => ShowNetworkProblem("Can't check for new episodes", ex);

        _positionTimer = DispatcherQueue.CreateTimer();
        _positionTimer.Interval = TimeSpan.FromMilliseconds(500);
        _positionTimer.Tick += (_, _) => UpdatePosition();

        _searchTimer = DispatcherQueue.CreateTimer();
        _searchTimer.Interval = SearchDelay;
        _searchTimer.IsRepeating = false;
        _searchTimer.Tick += (_, _) => _ = SearchAsync(SearchBox.Text);

        // The slider's thumb handles pointer events itself, so listen to handled ones too: a drag
        // should seek once, when released, not on every pixel.
        PositionSlider.AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) => _isDraggingSlider = true), true);
        PositionSlider.AddHandler(PointerReleasedEvent, new PointerEventHandler((_, _) => EndSliderDrag()), true);
        PositionSlider.AddHandler(PointerCaptureLostEvent, new PointerEventHandler((_, _) => EndSliderDrag()), true);
    }

    /// <summary>The flyout opened: show what's saved at once, then refresh if it's stale.</summary>
    public void OnShown()
    {
        if (_isVisible)
        {
            return;
        }

        _isVisible = true;
        LibraryService.Changed += OnLibraryChanged;
        PlaybackService.StateChanged += OnPlaybackStateChanged;

        if (_renderedVersion != LibraryService.Version || _renderedVersion < 0)
        {
            RenderLibrary();
        }
        else
        {
            RefreshEpisodeStates();
        }

        UpdateNowPlaying();
        UpdateFooter();
        _positionTimer.Start();
        _poller.Start();

        if (_view == View.Add)
        {
            DispatcherQueue.TryEnqueue(() => SearchBox.Focus(FocusState.Programmatic));
        }
    }

    /// <summary>The flyout hid: stop refreshing and searching, and let go of app-lifetime events.</summary>
    public void OnHidden()
    {
        if (!_isVisible)
        {
            return;
        }

        _isVisible = false;
        LibraryService.Changed -= OnLibraryChanged;
        PlaybackService.StateChanged -= OnPlaybackStateChanged;
        _poller.Stop();
        _positionTimer.Stop();
        _searchTimer.Stop();
        _searchCts?.Cancel();
    }

    /// <summary>Back within the page (a show or search to the lists). False when already at the top.</summary>
    public bool TryGoBack()
    {
        if (_view == View.Browse)
        {
            return false;
        }

        ShowBrowse();
        return true;
    }

    public void Dispose()
    {
        OnHidden();
        _lifetime.Cancel();
        _latest.Clear();
        _shows.Clear();
        _showEpisodes.Clear();
        _results.Clear();
        _art.Clear();
        LibraryService.Unload();
    }

    // ---- Library -------------------------------------------------------------------------

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (LibraryService.Library.Subscriptions.Count == 0)
        {
            return;
        }

        LoadingRing.IsActive = true;
        RefreshButton.IsEnabled = false;
        try
        {
            RefreshOutcome outcome = await LibraryService.RefreshAllAsync(cancellationToken);
            if (outcome.Failed > 0)
            {
                ShowStatus(
                    outcome.Failed == 1 ? "Couldn't update 1 show" : $"Couldn't update {outcome.Failed} shows",
                    "It'll be checked again next time.");
            }
            else
            {
                StatusBar.IsOpen = false;
            }

            UpdateFooter();
        }
        finally
        {
            LoadingRing.IsActive = false;
            RefreshButton.IsEnabled = true;
        }
    }

    private void OnLibraryChanged(object? sender, EventArgs e) => RenderLibrary();

    private void OnPlaybackStateChanged(object? sender, EventArgs e)
    {
        UpdateNowPlaying();
        RefreshEpisodeStates();
    }

    /// <summary>Rebuilds the lists from the library.</summary>
    private void RenderLibrary()
    {
        Library library = LibraryService.Library;
        _renderedVersion = LibraryService.Version;

        _latest.Clear();
        foreach (ShowEpisode item in LibraryOps.Latest(library.Subscriptions, LatestCount))
        {
            _latest.Add(new EpisodeItem(item.Show, item.Episode, includeShowName: true, _art));
        }

        _shows.Clear();
        foreach (Subscription show in LibraryOps.ShowsByRecentActivity(library.Subscriptions))
        {
            _shows.Add(new ShowItem(show, _art));
        }

        bool empty = library.Subscriptions.Count == 0;
        EmptyPanel.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        BrowseTabs.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        RefreshButton.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        ShowBrowseTab();

        if (_view == View.Show)
        {
            // The open show may have been refreshed (a new object) or unfollowed elsewhere.
            if (_openShow is not null && library.Find(_openShow.FeedUrl) is { } show)
            {
                OpenShow(show);
            }
            else
            {
                ShowBrowse();
            }
        }
    }

    private void RefreshEpisodeStates()
    {
        foreach (EpisodeItem item in _latest)
        {
            item.Refresh();
        }

        foreach (EpisodeItem item in _showEpisodes)
        {
            item.Refresh();
        }
    }

    private void UpdateFooter()
    {
        DateTimeOffset? last = SettingsService.LastRefreshedUtc;
        UpdatedText.Text = last is { } t && LibraryService.Library.Subscriptions.Count > 0
            ? t.ToLocalTime().Date == DateTime.Today
                ? $"Updated {t.ToLocalTime():t}"
                : $"Updated {EpisodeFormat.Published(t, DateTimeOffset.Now, CultureInfo.CurrentCulture)}"
            : string.Empty;
    }

    // ---- Views ---------------------------------------------------------------------------

    private void ShowBrowse()
    {
        _view = View.Browse;
        _openShow = null;
        _showEpisodes.Clear();
        _searchTimer.Stop();
        _searchCts?.Cancel();
        SetView(BrowsePanel, App.DisplayName);
    }

    private void OpenShow(Subscription show)
    {
        bool sameShow = _view == View.Show && _openShow?.FeedUrl == show.FeedUrl;
        _view = View.Show;
        _openShow = show;

        _showEpisodes.Clear();
        foreach (Episode episode in show.Episodes)
        {
            _showEpisodes.Add(new EpisodeItem(show, episode, includeShowName: false, _art));
        }

        ShowArt.Source = _art.Get(show.ArtworkUrl, 56);
        ShowAuthorText.Text = show.Author ?? string.Empty;
        ShowAuthorText.Visibility = show.Author is null ? Visibility.Collapsed : Visibility.Visible;
        ShowCountText.Text = show.Episodes.Count == 1 ? "1 episode" : $"{show.Episodes.Count} episodes";
        UnfollowPrompt.Text = $"Unfollow {show.Title}?";
        SetView(ShowPanel, show.Title);

        if (!sameShow && _showEpisodes.Count > 0)
        {
            ShowEpisodesList.ScrollIntoView(_showEpisodes[0]);
        }
    }

    private void OpenAdd()
    {
        _view = View.Add;
        SetView(AddPanel, "Add a podcast");
        SearchBox.SelectAll();
        DispatcherQueue.TryEnqueue(() => SearchBox.Focus(FocusState.Programmatic));
    }

    private void SetView(FrameworkElement panel, string title)
    {
        BrowsePanel.Visibility = panel == BrowsePanel ? Visibility.Visible : Visibility.Collapsed;
        ShowPanel.Visibility = panel == ShowPanel ? Visibility.Visible : Visibility.Collapsed;
        AddPanel.Visibility = panel == AddPanel ? Visibility.Visible : Visibility.Collapsed;
        BackButton.Visibility = _view == View.Browse ? Visibility.Collapsed : Visibility.Visible;
        AddButton.Visibility = _view == View.Browse ? Visibility.Visible : Visibility.Collapsed;
        TitleText.Text = title;
        ToolTipService.SetToolTip(TitleText, _view == View.Show ? title : null);
    }

    private void ShowBrowseTab()
    {
        bool shows = BrowseTabs.SelectedItem == ShowsTab;
        bool empty = _shows.Count == 0;
        LatestList.Visibility = !shows && !empty ? Visibility.Visible : Visibility.Collapsed;
        ShowsList.Visibility = shows && !empty ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BrowseTabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args) => ShowBrowseTab();

    private void BackButton_Click(object sender, RoutedEventArgs e) => TryGoBack();

    private void AddButton_Click(object sender, RoutedEventArgs e) => OpenAdd();

    private void RefreshButton_Click(object sender, RoutedEventArgs e) => _ = _poller.RefreshNowAsync();

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => App.Current.ShowSettings();

    private void ShowsList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ShowItem item)
        {
            OpenShow(item.Show);
        }
    }

    // ---- Episodes ------------------------------------------------------------------------

    private void EpisodeList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is EpisodeItem item)
        {
            PlaybackService.PlayOrToggle(LibraryOps.ToNowPlaying(item.Show, item.Episode));
        }
    }

    private void EpisodeList_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        EpisodeItem? item = args.OriginalSource switch
        {
            ListViewItem container => container.Content as EpisodeItem,
            FrameworkElement element => element.DataContext as EpisodeItem,
            _ => null,
        };
        if (item is null)
        {
            return;
        }

        args.Handled = true;
        MenuFlyout menu = new();
        bool playing = item.IsCurrent && PlaybackService.IsPlaying;
        MenuFlyoutItem play = new()
        {
            Text = playing ? "Pause" : "Play",
            Icon = new FontIcon { Glyph = playing ? "" : "" },
        };
        play.Click += (_, _) => PlaybackService.PlayOrToggle(LibraryOps.ToNowPlaying(item.Show, item.Episode));
        menu.Items.Add(play);

        MenuFlyoutItem played = new()
        {
            Text = item.IsPlayed ? "Mark as unplayed" : "Mark as played",
            Icon = new FontIcon { Glyph = item.IsPlayed ? "" : "" },
        };
        played.Click += (_, _) => LibraryService.SetPlayed(item.Show.FeedUrl, item.Episode.Id, !item.IsPlayed, item.Episode.DurationSeconds);
        menu.Items.Add(played);

        if (_view == View.Browse)
        {
            MenuFlyoutItem show = new() { Text = "Go to show", Icon = new FontIcon { Glyph = "" } };
            show.Click += (_, _) => OpenShow(item.Show);
            menu.Items.Add(show);
        }

        if (args.TryGetPosition(sender, out Windows.Foundation.Point point))
        {
            menu.ShowAt(sender, new FlyoutShowOptions { Position = point });
        }
        else
        {
            menu.ShowAt(args.OriginalSource as FrameworkElement ?? (FrameworkElement)sender);
        }
    }

    private void ConfirmUnfollow_Click(object sender, RoutedEventArgs e)
    {
        UnfollowFlyout.Hide();
        if (_openShow is { } show)
        {
            ShowBrowse();
            LibraryService.Unsubscribe(show);
        }
    }

    // ---- Search --------------------------------------------------------------------------

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            _searchTimer.Stop();
            if (_results.Count == 1 && FeedUrlPolicy.TryNormalize(SearchBox.Text, out _))
            {
                _ = FollowAsync(_results[0]);
            }
            else
            {
                _ = SearchAsync(SearchBox.Text, force: true);
            }
        }
    }

    private async Task SearchAsync(string text, bool force = false)
    {
        string term = text.Trim();
        if (!force && term == _lastSearch)
        {
            return;
        }

        _lastSearch = term;
        _searchCts?.Cancel();
        _results.Clear();
        SearchRing.IsActive = false;

        if (term.Length == 0)
        {
            SearchStatusText.Text = "Results come from the Apple Podcasts directory.";
            return;
        }

        // A pasted address needs no search: offer to follow it directly.
        if (FeedUrlPolicy.TryNormalize(term, out string feedUrl))
        {
            PodcastSearchResult direct = new(feedUrl, "RSS feed", feedUrl, null, null);
            _results.Add(new SearchItem(direct, LibraryService.Library.Find(feedUrl) is not null, _art));
            SearchStatusText.Text = "Press Enter or select the feed to follow it.";
            return;
        }

        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _searchCts = cts;
        SearchRing.IsActive = true;
        SearchStatusText.Text = "Searching…";
        try
        {
            IReadOnlyList<PodcastSearchResult> found = await LibraryService.SearchAsync(term, cts.Token);
            if (cts.IsCancellationRequested)
            {
                return; // A newer search replaced this one.
            }

            foreach (PodcastSearchResult result in found)
            {
                _results.Add(new SearchItem(result, LibraryService.Library.Find(result.FeedUrl) is not null, _art));
            }

            SearchStatusText.Text = found.Count == 0
                ? "No podcasts found. Try other words, or paste the show's RSS feed address."
                : "Select a show to follow it.";
        }
        catch (Exception) when (cts.IsCancellationRequested)
        {
            // Superseded or hidden.
        }
        catch (Exception)
        {
            _lastSearch = string.Empty; // Let the same term retry.
            SearchStatusText.Text = ConnectivityService.IsInternetAvailable
                ? "Search isn't responding. Try again, or paste the show's RSS feed address."
                : "No internet connection.";
        }
        finally
        {
            if (_searchCts == cts)
            {
                _searchCts = null;
                SearchRing.IsActive = false;
            }
        }
    }

    private void SearchList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SearchItem item)
        {
            _ = FollowAsync(item);
        }
    }

    private async Task FollowAsync(SearchItem item)
    {
        if (item.IsBusy)
        {
            return;
        }

        if (LibraryService.Library.Find(item.Result.FeedUrl) is { } existing)
        {
            OpenShow(existing);
            return;
        }

        item.IsBusy = true;
        try
        {
            Subscription show = await LibraryService.SubscribeAsync(item.Result.FeedUrl, _lifetime.Token);
            item.IsSubscribed = true;
            StatusBar.IsOpen = false;
            if (_view == View.Add)
            {
                OpenShow(show);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ShowNetworkProblem($"Can't follow {item.Title}", ex);
        }
        finally
        {
            item.IsBusy = false;
        }
    }

    // ---- Now playing ---------------------------------------------------------------------

    private void UpdateNowPlaying()
    {
        if (PlaybackService.Current is not { } current)
        {
            NowPlayingCard.Visibility = Visibility.Collapsed;
            return;
        }

        NowPlayingCard.Visibility = Visibility.Visible;
        NowPlayingTitle.Text = current.Episode.Title;
        ToolTipService.SetToolTip(NowPlayingTitle, current.Episode.Title);
        NowPlayingShow.Text = current.ShowTitle;
        NowPlayingArt.Source = _art.Get(current.ArtworkUrl, NowPlayingArtSize);

        bool playing = PlaybackService.IsPlaying;
        PlayPauseIcon.Glyph = playing ? "" : "";
        string playLabel = playing ? "Pause" : "Play";
        AutomationPropertiesHelper.SetNameAndTip(PlayPauseButton, playLabel);
        AutomationPropertiesHelper.SetNameAndTip(SkipBackButton, $"Back {SettingsService.SkipBackSeconds} seconds");
        AutomationPropertiesHelper.SetNameAndTip(SkipForwardButton, $"Forward {SettingsService.SkipForwardSeconds} seconds");

        string rate = PlaybackService.Rate.ToString("0.##", CultureInfo.CurrentCulture) + "×";
        SpeedText.Text = rate;
        AutomationPropertiesHelper.SetNameAndTip(SpeedButton, $"Playback speed {rate}");

        BufferingRing.IsActive = PlaybackService.IsBuffering && playing;
        PlaybackErrorText.Text = PlaybackService.Error ?? string.Empty;
        PlaybackErrorText.Visibility = PlaybackService.Error is null ? Visibility.Collapsed : Visibility.Visible;
        UpdatePosition();
    }

    private void UpdatePosition()
    {
        if (PlaybackService.Current is null || _isDraggingSlider)
        {
            return;
        }

        double position = PlaybackService.PositionSeconds;
        double? duration = PlaybackService.DurationSeconds;

        _updatingSlider = true;
        PositionSlider.Maximum = Math.Max(1, duration ?? position);
        PositionSlider.Value = Math.Min(position, PositionSlider.Maximum);
        _updatingSlider = false;
        ShowTimes(position, duration);
    }

    private void ShowTimes(double position, double? duration)
    {
        ElapsedText.Text = EpisodeFormat.Clock(position);
        RemainingText.Text = duration is > 0 and var d ? "-" + EpisodeFormat.Clock(d - position) : string.Empty;
    }

    private void PositionSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updatingSlider)
        {
            return;
        }

        if (_isDraggingSlider)
        {
            ShowTimes(e.NewValue, PlaybackService.DurationSeconds);
        }
        else
        {
            PlaybackService.SeekTo(e.NewValue); // Keyboard arrows.
        }
    }

    private void EndSliderDrag()
    {
        if (!_isDraggingSlider)
        {
            return;
        }

        _isDraggingSlider = false;
        PlaybackService.SeekTo(PositionSlider.Value);
    }

    private void PlayPauseButton_Click(object sender, RoutedEventArgs e) => PlaybackService.TogglePlayPause();

    private void SkipBackButton_Click(object sender, RoutedEventArgs e) => PlaybackService.SkipBack();

    private void SkipForwardButton_Click(object sender, RoutedEventArgs e) => PlaybackService.SkipForward();

    private void SpeedButton_Click(object sender, RoutedEventArgs e) => PlaybackService.CycleRate();

    // ---- Status --------------------------------------------------------------------------

    private void ShowNetworkProblem(string title, Exception ex)
    {
        bool online = ConnectivityService.IsInternetAvailable;
        string message = !online ? "You're offline. This will update when you're back online."
            : ex is FeedFormatException ? ex.Message
            : ex is HttpRequestException { StatusCode: { } code } ? $"The server answered {(int)code}. Retrying shortly."
            : "The server isn't responding. Retrying shortly.";
        ShowStatus(title, message);
    }

    private void ShowStatus(string title, string message)
    {
        StatusBar.Title = title;
        StatusBar.Message = message;
        StatusBar.IsOpen = true;
    }
}

internal static class AutomationPropertiesHelper
{
    /// <summary>Icon-only buttons need a spoken name and a matching tooltip.</summary>
    public static void SetNameAndTip(FrameworkElement element, string text)
    {
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(element, text);
        ToolTipService.SetToolTip(element, text);
    }
}
