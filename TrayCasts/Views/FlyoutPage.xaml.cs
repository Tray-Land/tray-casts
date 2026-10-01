using System.Collections.ObjectModel;
using System.Globalization;
using System.Numerics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using TrayCasts.Feeds;
using TrayCasts.Formatting;
using TrayCasts.Services;
using TrayCasts.Shows;
using TrayCasts.Storage;
using Windows.Foundation;

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
    private const int FullArtSize = 280;
    private const string ArtFlightKey = "NowPlayingArt";
    private const float SlideDistance = 48;

    /// <summary>Paused art sits back a little, the way a record stops spinning.</summary>
    private const float PausedArtScale = 0.88f;

    /// <summary>Pointer speed, in px/ms, past which a release counts as a flick.</summary>
    private const double FlickVelocity = 0.4;

    /// <summary>How long after a drag on the card a click is still taken as part of the drag.</summary>
    private const long ClickAfterDragMs = 300;

    private readonly ForegroundPoller _poller;
    private readonly DispatcherQueueTimer _positionTimer;
    private readonly DispatcherQueueTimer _searchTimer;
    private readonly ArtworkCache _art = new();
    private readonly ImplicitMotion _motion;
    private readonly ArtExpandTransition _expand;
    private readonly PlayerSurface _surface;
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
        NowPlaying,
    }

    private View _view = View.Browse;
    private Subscription? _openShow;
    private EpisodeOrder _openShowOrder;
    private bool _updatingShowOptions;
    private int _renderedVersion = -1;
    private bool _isVisible;
    private CancellationTokenSource? _searchCts;
    private string _lastSearch = string.Empty;
    private bool _updatingSlider;
    private bool _isDraggingSlider;
    private bool _headerDragging;
    private long _headerDragEndedMs;
    private float _artDragDistance;
    private bool _artDragFromPage;
    private Vector3Transition? _artScaleTransition;

    public FlyoutPage()
    {
        InitializeComponent();
        LibraryService.IsViewed = true;
        LatestList.ItemsSource = _latest;
        ShowsList.ItemsSource = _shows;
        ShowEpisodesList.ItemsSource = _showEpisodes;
        SearchList.ItemsSource = _results;

        LimitBox.Items.Add("All episodes");
        foreach (int limit in ShowPolicy.LimitChoices)
        {
            LimitBox.Items.Add(limit == 1 ? "1 episode" : $"{limit} episodes");
        }

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

        _surface = new PlayerSurface(PlayerCardSurface);
        NowPlayingCard.SizeChanged += (_, _) =>
        {
            FitFullPageAboveControls();
            _surface.FollowCard(NowPlayingCard.ActualHeight);
        };

        _motion = new ImplicitMotion(this);
        _expand = new ArtExpandTransition(this);
        SetUpMotion();
    }

    private void SetUpMotion()
    {
        if (!_motion.IsEnabled)
        {
            return;
        }

        Vector3 fade = Vector3.Zero;
        Vector3 below = new(0, SlideDistance / 2, 0);
        _motion.SetEnterExit(NowPlayingCard, below, below);
        _motion.SetEnterExit(PlayerCardSurface, below, below);
        _motion.SetEnterExit(NowPlayingHeader, fade, fade);
        _motion.SetEnterExit(EmptyPanel, below, fade);
        foreach (UIElement button in new UIElement[] { BackButton, AddButton, RefreshButton })
        {
            _motion.SetEnterExit(button, fade, fade);
        }

        _artScaleTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(450) };
        FullArtHost.ScaleTransition = _artScaleTransition;
        FullArtHost.SizeChanged += (_, e) => FullArtHost.CenterPoint = new Vector3((float)e.NewSize.Width / 2, (float)e.NewSize.Height / 2, 0);

        // Offsets animate only after the first layout, so nothing flies in from the corner on open.
        Loaded += OnFirstLoaded;

        void OnFirstLoaded(object sender, RoutedEventArgs e)
        {
            Loaded -= OnFirstLoaded;

            // The InfoBar opening, the player card arriving, and the card trading its header for
            // the full page all move things; let them glide. The card and its controls move by
            // equal and opposite amounts when the header goes, so the controls hold still.
            _motion.AnimateMoves(BrowsePanel, ShowPanel, AddPanel, NowPlayingPanel, NowPlayingCard, TitleText);
            _motion.AnimateMoves([.. NowPlayingCardContent.Children.Where(c => c != NowPlayingHeader)]);
        }
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
        SetMarqueesActive(true);
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
        _expand.Cancel();
        LibraryService.Changed -= OnLibraryChanged;
        PlaybackService.StateChanged -= OnPlaybackStateChanged;
        _poller.Stop();
        _positionTimer.Stop();
        _searchTimer.Stop();
        _searchCts?.Cancel();
        SetMarqueesActive(false);
    }

    /// <summary>Clipped player text scrolls only while someone can see it.</summary>
    private void SetMarqueesActive(bool active)
    {
        NowPlayingTitle.IsActive = active;
        NowPlayingShow.IsActive = active;
        FullTitle.IsActive = active;
        FullShowText.IsActive = active;
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
        LibraryService.IsViewed = false;
        LibraryService.Unload();
    }

    // ---- Library -------------------------------------------------------------------------

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (LibraryService.Library.Subscriptions.Count == 0)
        {
            return;
        }

        SetLoading(true);
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
        }
        finally
        {
            SetLoading(false);
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
        foreach (ShowEpisode item in LibraryOps.Latest(library.Subscriptions, LibraryService.Progress, LatestCount))
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
        else if (_view == View.NowPlaying)
        {
            UpdateNowPlaying(); // The show may have been followed or unfollowed.
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

    /// <summary>Swaps the refresh button's icon for a spinner while feeds are being fetched.</summary>
    private void SetLoading(bool active)
    {
        LoadingRing.IsActive = active;
        RefreshIcon.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
    }

    // ---- Views ---------------------------------------------------------------------------

    private void ShowBrowse(bool animate = true)
    {
        _view = View.Browse;
        _openShow = null;
        _showEpisodes.Clear();
        _searchTimer.Stop();
        _searchCts?.Cancel();
        SetView(BrowsePanel, App.DisplayName, animate);
    }

    private void OpenShow(Subscription show)
    {
        bool sameShow = _view == View.Show && _openShow?.FeedUrl == show.FeedUrl && _openShowOrder == show.Order;
        _view = View.Show;
        _openShow = show;
        _openShowOrder = show.Order;

        _showEpisodes.Clear();
        foreach (Episode episode in ShowPolicy.InPageOrder(show))
        {
            _showEpisodes.Add(new EpisodeItem(show, episode, includeShowName: false, _art));
        }

        UpdateShowOptions(show);

        ShowArt.Source = _art.Get(show.ArtworkUrl, 56);
        ShowAuthorText.Text = show.Author ?? string.Empty;
        ShowAuthorText.Visibility = show.Author is null ? Visibility.Collapsed : Visibility.Visible;
        ShowCountText.Text = show.Episodes.Count == 1 ? "1 episode" : $"{show.Episodes.Count} episodes";
        UnfollowPrompt.Text = $"Unfollow {show.Title}?";
        SetView(ShowPanel, show.Title);

        // Open at what's up next: for a show heard from the start, that's far down the list.
        if (!sameShow && _showEpisodes.Count > 0)
        {
            Episode? next = ShowPolicy.UpNext(show, IsPlayed(show)).FirstOrDefault();
            EpisodeItem target = _showEpisodes.FirstOrDefault(i => i.Episode == next) ?? _showEpisodes[0];
            ShowEpisodesList.ScrollIntoView(target, ScrollIntoViewAlignment.Leading);
        }
    }

    private static Func<Episode, bool> IsPlayed(Subscription show) =>
        e => LibraryService.GetProgress(show.FeedUrl, e.Id)?.IsPlayed == true;

    // ---- Show options --------------------------------------------------------------------

    private void UpdateShowOptions(Subscription show)
    {
        _updatingShowOptions = true;
        EpisodeOrder order = ShowPolicy.SanitizeOrder(show.Order);
        OrderRadios.SelectedIndex = (int)order;
        OrderHintText.Text = order switch
        {
            EpisodeOrder.OldestFirst => "Latest offers the oldest unplayed episodes, to hear the show from the start.",
            EpisodeOrder.NewestThenCatchUp => "Latest offers a new episode first. Once it's played, the oldest unplayed ones, to catch up.",
            _ => "Latest offers the newest episodes as they come out.",
        };
        LimitBox.SelectedIndex = ShowPolicy.SanitizeLimit(show.EpisodeLimit) is { } limit
            ? ShowPolicy.LimitChoices.ToList().IndexOf(limit) + 1
            : 0;
        _updatingShowOptions = false;
    }

    private void ShowOptions_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingShowOptions || _openShow is not { } show || OrderRadios.SelectedIndex < 0)
        {
            return;
        }

        EpisodeOrder order = (EpisodeOrder)OrderRadios.SelectedIndex;
        int? limit = LimitBox.SelectedIndex > 0 ? ShowPolicy.LimitChoices[LimitBox.SelectedIndex - 1] : null;

        // A backlog can be hundreds of episodes; don't let it flood Latest unless asked to.
        if (ReferenceEquals(sender, OrderRadios) && limit is null && ShowPolicy.WorksThroughBacklog(order) && !ShowPolicy.WorksThroughBacklog(show.Order))
        {
            limit = ShowPolicy.DefaultBacklogLimit;
        }

        _ = SetShowOptionsAsync(show, order, limit);
    }

    private async Task SetShowOptionsAsync(Subscription show, EpisodeOrder order, int? limit)
    {
        bool fetching = ShowPolicy.EpisodesToKeep(order) > ShowPolicy.EpisodesToKeep(show.Order);
        if (fetching)
        {
            SetLoading(true);
        }

        try
        {
            await LibraryService.SetShowOptionsAsync(show, order, limit, _lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ShowNetworkProblem($"Can't load older episodes of {show.Title}", ex);
        }
        finally
        {
            if (fetching)
            {
                SetLoading(false);
            }
        }
    }

    private void OpenNowPlaying()
    {
        if (PlaybackService.Current is null)
        {
            return;
        }

        _view = View.NowPlaying;
        _openShow = null;

        // Fill the page before it shows, and settle the paused scale at once: the art flies in to
        // where it will rest, so it mustn't still be shrinking when it lands.
        FullArtHost.ScaleTransition = null;
        UpdateNowPlaying();
        FullArtHost.ScaleTransition = _artScaleTransition;
        SetView(NowPlayingPanel, "Now playing");
    }

    /// <summary>Keeps the full page's bottom edge where the controls start once the header is gone,
    /// whether or not the header is there now (see the panel's comment in XAML).</summary>
    private void FitFullPageAboveControls()
    {
        double header = NowPlayingHeader.Visibility == Visibility.Visible ? HeaderExtent() : 0;
        double gap = ((Grid)NowPlayingPanel.Parent).RowSpacing;
        NowPlayingPanel.Margin = new Thickness(0, 0, 0, Math.Max(0, NowPlayingCard.ActualHeight - header) + gap);
    }

    private double HeaderExtent() =>
        NowPlayingHeader.ActualHeight + NowPlayingHeader.Margin.Top + NowPlayingHeader.Margin.Bottom;

    // ---- Drag between the player card and the full page ----------------------------------

    private void NowPlayingHeader_ManipulationStarted(object sender, ManipulationStartedRoutedEventArgs e)
    {
        _headerDragging = true;
        if (_motion.IsEnabled && !_expand.IsActive && _view != View.NowPlaying && PlaybackService.Current is { } current)
        {
            FrameworkElement leaving = new[] { BrowsePanel, ShowPanel, AddPanel }.First(p => p.Visibility == Visibility.Visible);
            BeginArtDrag(current, leaving, fromPage: false);
        }
    }

    private void FullArt_ManipulationStarted(object sender, ManipulationStartedRoutedEventArgs e)
    {
        if (_motion.IsEnabled && !_expand.IsActive && _view == View.NowPlaying && PlaybackService.Current is { } current)
        {
            // Down from the page goes home, whichever view it was opened from.
            BeginArtDrag(current, BrowsePanel, fromPage: true);
        }
    }

    private void ArtDrag_ManipulationDelta(object sender, ManipulationDeltaRoutedEventArgs e)
    {
        if (_expand.IsTracking)
        {
            // The art moves exactly as far as the pointer: it stays under the finger.
            float moved = (float)-e.Cumulative.Translation.Y / _artDragDistance;
            _expand.Track(_artDragFromPage ? 1 + moved : moved);
        }
    }

    private void ArtDrag_ManipulationCompleted(object sender, ManipulationCompletedRoutedEventArgs e)
    {
        if (ReferenceEquals(sender, NowPlayingHeader))
        {
            _headerDragging = false;
            _headerDragEndedMs = Environment.TickCount64;
        }

        if (_expand.IsTracking)
        {
            // A flick decides it; otherwise a third of the way decides it.
            double velocity = e.Velocities.Linear.Y;
            bool open = velocity < -FlickVelocity
                || (velocity <= FlickVelocity && _expand.Progress > (_artDragFromPage ? 2 / 3f : 1 / 3f));
            _expand.Settle(open);
        }
        else if (!_motion.IsEnabled)
        {
            // No motion: a swipe still switches, just without following the pointer.
            double moved = e.Cumulative.Translation.Y;
            if (ReferenceEquals(sender, NowPlayingHeader) && moved < -HeaderExtent())
            {
                OpenNowPlaying();
            }
            else if (ReferenceEquals(sender, FullArtHost) && moved > HeaderExtent())
            {
                ShowBrowse();
            }
        }
    }

    /// <summary>Lays out both ends at once (the full page, and the card with its header over the
    /// view underneath), measures where the art travels, and hands the visuals to the transition
    /// at whichever end the drag starts from.</summary>
    private void BeginArtDrag(NowPlaying current, FrameworkElement leaving, bool fromPage)
    {
        _artDragFromPage = fromPage;
        UpdateFullPage(current);
        FullArtHost.ScaleTransition = null;
        FullArtHost.Scale = Vector3.One; // The art travels at full size; a paused scale settles after.

        // The header coming back must not grow the card on screen, nor glide its controls: the
        // transition draws the card's shape itself until it settles.
        ImplicitMotion.SuspendMoves(CardMovers());
        foreach (UIElement element in new UIElement[] { NowPlayingPanel, leaving, NowPlayingHeader })
        {
            ImplicitMotion.ClearEnterExit(element);
            element.Visibility = Visibility.Visible;
        }

        _surface.LayOutFull();
        UpdateLayout();

        Rect surface = BoundsOnPage(PlayerCardSurface);
        float cardInset = (float)Math.Max(0, BoundsOnPage(NowPlayingCard).Top - surface.Top);
        Rect thumb = BoundsOnPage(MiniArtTile);
        Rect art = BoundsOnPage(FullArtTile);
        float viewbox = (float)(art.Width / FullArtTile.ActualWidth);
        _artDragDistance = (float)Math.Max(1, thumb.Y - art.Y);

        _expand.Begin(
            new ArtExpandTransition.Parts(
                Art: FullArtTile,
                ArtOffsetFrom: new Vector3((float)(thumb.X - art.X) / viewbox, (float)(thumb.Y - art.Y) / viewbox, 0),
                ArtScaleFrom: (float)(thumb.Width / art.Width),
                Thumbnail: MiniArtTile,
                Details: FullDetails,
                Header: NowPlayingHeader,
                SurfaceEdge: _surface.ClipAt(fromPage ? 0 : cardInset, under: leaving),
                SurfaceSize: new Vector2((float)surface.Width, (float)surface.Height),
                SurfaceInset: cardInset,
                Leaving: leaving),
            fromPage ? 1 : 0,
            open => FinishArtDrag(open, leaving));
    }

    /// <summary>Commits the layout for the end the drag settled at; the screen already shows it.</summary>
    private void FinishArtDrag(bool open, FrameworkElement leaving)
    {
        FullArtHost.ScaleTransition = _artScaleTransition;
        if (open)
        {
            _view = View.NowPlaying;
            _openShow = null;
            SetView(NowPlayingPanel, "Now playing", animate: false);
            UpdateNowPlaying(); // Settles a paused episode's art to its smaller size.
        }
        else if (_artDragFromPage)
        {
            ShowBrowse(animate: false);
        }
        else
        {
            NowPlayingPanel.Visibility = Visibility.Collapsed;
            _surface.Rest(full: false, NowPlayingCard.ActualHeight);
        }

        // Once the new layout is on screen, moves and the header's fades animate again.
        UIElement[] movers = CardMovers();
        AfterFrames(2, () =>
        {
            _motion.AnimateMoves(movers);
            _motion.SetEnterExit(NowPlayingHeader, Vector3.Zero, Vector3.Zero);
        });
    }

    /// <summary>The card and what's in it below the header: what moves when the header comes and goes.</summary>
    private UIElement[] CardMovers() =>
        [NowPlayingCard, .. NowPlayingCardContent.Children.Where(c => c != NowPlayingHeader)];

    private Rect BoundsOnPage(FrameworkElement element) =>
        element.TransformToVisual(this).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));

    /// <summary>Runs <paramref name="action"/> once XAML has rendered <paramref name="frames"/>
    /// more frames, i.e. after the layout just changed has reached the screen.</summary>
    private static void AfterFrames(int frames, Action action)
    {
        CompositionTarget.Rendering += OnRendering;

        void OnRendering(object? sender, object e)
        {
            if (--frames <= 0)
            {
                CompositionTarget.Rendering -= OnRendering;
                action();
            }
        }
    }

    private void OpenAdd()
    {
        _view = View.Add;
        SetView(AddPanel, "Add a podcast");
        SearchBox.SelectAll();
        DispatcherQueue.TryEnqueue(() => SearchBox.Focus(FocusState.Programmatic));
    }

    /// <param name="animate">False when the change is already on screen, e.g. after a drag.</param>
    private void SetView(FrameworkElement panel, string title, bool animate = true)
    {
        FrameworkElement[] panels = [BrowsePanel, ShowPanel, AddPanel, NowPlayingPanel];
        FrameworkElement? current = panels.FirstOrDefault(p => p.Visibility == Visibility.Visible && p != panel);
        ConnectedAnimation? artFlight = null;
        double cardTopBefore = 0;
        if (animate && current is not null)
        {
            PrepareViewTransition(current, panel);
            // No card means playback ended and there is nowhere for the art to land.
            if (_motion.IsEnabled && NowPlayingCard.Visibility == Visibility.Visible && (current == NowPlayingPanel || panel == NowPlayingPanel))
            {
                artFlight = PrepareArtFlight(toFullPage: panel == NowPlayingPanel);
                cardTopBefore = BoundsOnPage(NowPlayingCard).Top;
            }
        }

        BrowsePanel.Visibility = panel == BrowsePanel ? Visibility.Visible : Visibility.Collapsed;
        ShowPanel.Visibility = panel == ShowPanel ? Visibility.Visible : Visibility.Collapsed;
        AddPanel.Visibility = panel == AddPanel ? Visibility.Visible : Visibility.Collapsed;
        NowPlayingPanel.Visibility = panel == NowPlayingPanel ? Visibility.Visible : Visibility.Collapsed;

        // The full page shows the art and details itself; the card below keeps only the controls.
        NowPlayingHeader.Visibility = panel == NowPlayingPanel ? Visibility.Collapsed : Visibility.Visible;
        BackButton.Visibility = _view == View.Browse ? Visibility.Collapsed : Visibility.Visible;
        AddButton.Visibility = _view == View.Browse ? Visibility.Visible : Visibility.Collapsed;
        TitleText.Text = title;
        ToolTipService.SetToolTip(TitleText, _view == View.Show ? title : null);

        bool full = panel == NowPlayingPanel;
        if (artFlight is null)
        {
            _surface.Rest(full, NowPlayingCard.ActualHeight);
            return;
        }

        // The art's landing spot, and where the surface's edge goes, must be measured first.
        _surface.LayOutFull();
        UpdateLayout();
        artFlight.TryStart(full ? FullArtTile : MiniArtTile);

        // The card's surface grows up into the page, or shrinks back down around the card.
        double surfaceTop = BoundsOnPage(PlayerCardSurface).Top;
        double cardTop = full ? cardTopBefore : BoundsOnPage(NowPlayingCard).Top;
        float cardInset = (float)Math.Max(0, cardTop - surfaceTop);
        _surface.Glide(full ? cardInset : 0, full ? 0 : cardInset, full, NowPlayingCard.ActualHeight, under: full ? current! : panel);
    }

    /// <summary>The album art is the same picture in the player card and on the full page, so it
    /// flies between them: up and out of the card with a lift, straight back down when leaving.</summary>
    private ConnectedAnimation PrepareArtFlight(bool toFullPage)
    {
        ConnectedAnimation flight = ConnectedAnimationService.GetForCurrentView()
            .PrepareToAnimate(ArtFlightKey, toFullPage ? MiniArtTile : FullArtTile);
        flight.Configuration = toFullPage
            ? new GravityConnectedAnimationConfiguration()
            : new DirectConnectedAnimationConfiguration();
        return flight;
    }

    /// <summary>Drilling in slides the new view in from the right, going back from the left, and
    /// now playing rises up out of the player card.</summary>
    private void PrepareViewTransition(FrameworkElement from, FrameworkElement to)
    {
        static int Depth(FrameworkElement panel, FlyoutPage page) => panel == page.BrowsePanel ? 0 : 1;

        bool back = Depth(to, this) < Depth(from, this);
        Vector3 enter = to == NowPlayingPanel ? new(0, SlideDistance, 0)
            : new(back ? -SlideDistance : SlideDistance, 0, 0);
        Vector3 exit = from == NowPlayingPanel ? new(0, SlideDistance / 2, 0)
            : to == NowPlayingPanel ? new(0, -SlideDistance / 4, 0)
            : new(back ? SlideDistance / 2 : -SlideDistance / 2, 0, 0);
        _motion.SetEnterExit(to, enter, Vector3.Zero);
        _motion.SetEnterExit(from, Vector3.Zero, exit);
    }

    private void ShowBrowseTab()
    {
        bool shows = BrowseTabs.SelectedItem == ShowsTab;
        if (shows != (ShowsList.Visibility == Visibility.Visible))
        {
            // The list slides toward the tab that was picked.
            float direction = shows ? 1 : -1;
            _motion.SetEnterExit(shows ? ShowsList : LatestList, new(direction * SlideDistance, 0, 0), Vector3.Zero);
            _motion.SetEnterExit(shows ? LatestList : ShowsList, Vector3.Zero, new(-direction * SlideDistance / 2, 0, 0));
        }

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
            _expand.Cancel();
            NowPlayingCard.Visibility = Visibility.Collapsed;
            PlayerCardSurface.Visibility = Visibility.Collapsed;
            if (_view == View.NowPlaying)
            {
                ShowBrowse();
            }

            return;
        }

        if (_view == View.NowPlaying)
        {
            UpdateFullPage(current);
        }

        NowPlayingCard.Visibility = Visibility.Visible;
        PlayerCardSurface.Visibility = Visibility.Visible;
        NowPlayingTitle.Text = current.Episode.Title;
        ToolTipService.SetToolTip(NowPlayingTitle, current.Episode.Title);
        NowPlayingShow.Text = current.ShowTitle;
        // The card shares the full page's decode, so the art is already sharp when it flies up.
        NowPlayingArt.Source = _art.Get(current.ArtworkUrl, FullArtSize);

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

    private void UpdateFullPage(NowPlaying current)
    {
        Episode episode = current.Episode;
        FullArt.Source = _art.Get(current.ArtworkUrl, FullArtSize);
        float scale = PlaybackService.IsPlaying || !_motion.IsEnabled ? 1f : PausedArtScale;
        FullArtHost.Scale = new Vector3(scale, scale, 1);
        FullTitle.Text = episode.Title;
        ToolTipService.SetToolTip(FullTitle, episode.Title);

        // Only a followed show has a page to go to; an episode can outlive its unfollowed show.
        FullShowText.Text = current.ShowTitle;
        Subscription? show = LibraryService.Library.Find(current.FeedUrl);
        FullShowLink.IsEnabled = show is not null;
        AutomationPropertiesHelper.SetNameAndTip(FullShowLink, show is null ? current.ShowTitle : $"Go to {current.ShowTitle}");

        FullMeta.Text = EpisodeFormat.Meta(
            show?.Author,
            EpisodeFormat.Published(episode.Published, DateTimeOffset.Now, CultureInfo.CurrentCulture),
            EpisodeFormat.Duration(episode.DurationSeconds ?? PlaybackService.DurationSeconds));
    }

    private void NowPlayingHeader_Click(object sender, RoutedEventArgs e)
    {
        // The release that ends a drag can also click the button; that click isn't meant.
        bool justDragged = _headerDragging || Environment.TickCount64 - _headerDragEndedMs < ClickAfterDragMs;
        if (!justDragged && !_expand.IsActive)
        {
            OpenNowPlaying();
        }
    }

    private void FullShowLink_Click(object sender, RoutedEventArgs e)
    {
        if (PlaybackService.Current is { } current && LibraryService.Library.Find(current.FeedUrl) is { } show)
        {
            OpenShow(show);
        }
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
