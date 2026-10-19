using TrayCasts.Feeds;
using TrayCasts.Search;
using TrayCasts.Storage;
using Windows.Storage;

namespace TrayCasts.Services;

/// <summary>Outcome of refreshing every show: how many feeds couldn't be read, and how many new episodes came out.</summary>
internal sealed record RefreshOutcome(int Checked, int Failed, Exception? LastError, int NewReleases = 0);

/// <summary>Outcome of an OPML import.</summary>
internal sealed record ImportOutcome(int Added, int AlreadyFollowed, int Failed);

/// <summary>
/// The followed shows and listening progress. The library is loaded when the flyout needs it and
/// dropped when the flyout closes (<see cref="Unload"/>), so the idle app doesn't hold every
/// show's episode list. UI thread only; network and parsing run on the thread pool.
/// </summary>
internal static class LibraryService
{
    private const int MaxConcurrentFetches = 4;

    private static readonly HttpClient Http = FeedClient.CreateHttpClient($"TrayCasts/{AppVersion.Current} (Windows)");
    private static readonly FeedClient Feeds = new(Http);

    private static Library? _library;
    private static ProgressBook? _progress;

    /// <summary>Raised on the UI thread when shows are added, removed, or refreshed, or progress
    /// changes in a way a list should show (played / unplayed).</summary>
    public static event EventHandler? Changed;

    /// <summary>Bumped on every <see cref="Changed"/>, so a view can tell whether what it shows is current.</summary>
    public static int Version { get; private set; }

    public static Library Library => _library ??= LibraryStore.Load(LibraryPath, CoreJsonContext.Default.Library);

    /// <summary>Small and needed by the player even with no window open, so it can stay loaded.</summary>
    public static ProgressBook Progress => _progress ??= LibraryStore.Load(ProgressPath, CoreJsonContext.Default.ProgressBook);

    private static string LibraryPath => Path.Combine(ApplicationData.Current.LocalFolder.Path, "library.json");

    private static string ProgressPath => Path.Combine(ApplicationData.Current.LocalFolder.Path, "progress.json");

    public static Task<IReadOnlyList<PodcastSearchResult>> SearchAsync(string term, CancellationToken cancellationToken) =>
        PodcastSearch.SearchAsync(Http, term, cancellationToken);

    /// <summary>Follows a show, fetching it first so a bad URL fails here rather than later.</summary>
    public static async Task<Subscription> SubscribeAsync(string feedUrl, CancellationToken cancellationToken)
    {
        if (Library.Find(feedUrl) is { IsFollowed: true } existing)
        {
            return existing;
        }

        FeedFetchResult fetch = await Feeds.FetchAsync(feedUrl, null, null, cancellationToken);

        // Another add of the same show may have finished during the await.
        if (Library.Find(feedUrl) is { IsFollowed: true } raced)
        {
            return raced;
        }

        Subscription subscription = LibraryOps.CreateSubscription(feedUrl, fetch, DateTimeOffset.UtcNow);
        if (Library.Find(feedUrl) is { } saved)
        {
            // Following a show kept for saved episodes replaces it; its progress stays.
            Library.Subscriptions.Remove(saved);
        }

        Library.Subscriptions.Add(subscription);
        await SaveLibraryAsync();
        RaiseChanged();
        return subscription;
    }

    /// <summary>
    /// Reads a show's feed to look through it without following it. A followed show is returned as
    /// it is. The preview isn't kept anywhere, so closing its page frees it.
    /// </summary>
    public static async Task<Subscription> PreviewAsync(string feedUrl, CancellationToken cancellationToken)
    {
        if (Library.Find(feedUrl) is { IsFollowed: true } followed)
        {
            return followed;
        }

        FeedFetchResult fetch = await Feeds.FetchAsync(feedUrl, null, null, cancellationToken);
        return LibraryOps.CreatePreview(feedUrl, fetch, DateTimeOffset.UtcNow);
    }

    public static bool IsEpisodeSaved(string feedUrl, string episodeId) =>
        LibraryOps.IsEpisodeSaved(Library, feedUrl, episodeId);

    /// <summary>Saves one episode of a show that isn't followed, or forgets it when it already is saved.</summary>
    public static void ToggleSavedEpisode(Subscription source, Episode episode)
    {
        if (IsEpisodeSaved(source.FeedUrl, episode.Id))
        {
            if (LibraryOps.RemoveSavedEpisode(Library, source.FeedUrl, episode.Id)
                && PlaybackService.Current?.FeedUrl != source.FeedUrl
                && Progress.RemoveFeed(source.FeedUrl) > 0)
            {
                _ = LibraryStore.SaveAsync(ProgressPath, Progress, CoreJsonContext.Default.ProgressBook);
            }
        }
        else if (!LibraryOps.SaveEpisode(Library, source, episode, DateTimeOffset.UtcNow))
        {
            return;
        }

        _ = SaveLibraryAsync();
        RaiseChanged();
    }

    public static void Unsubscribe(Subscription subscription)
    {
        if (!Library.Subscriptions.Remove(subscription))
        {
            return;
        }

        // Keep the progress of whatever is in the player, so it can still resume.
        if (PlaybackService.Current?.FeedUrl != subscription.FeedUrl && Progress.RemoveFeed(subscription.FeedUrl) > 0)
        {
            _ = LibraryStore.SaveAsync(ProgressPath, Progress, CoreJsonContext.Default.ProgressBook);
        }

        _ = SaveLibraryAsync();
        RaiseChanged();
    }

    /// <summary>
    /// Changes a show's order and limit, saved and shown at once. Switching to an order that works
    /// through the backlog then fetches the whole feed, since only the newest episodes were kept;
    /// if that fails, the next refresh fetches it instead.
    /// </summary>
    public static async Task SetShowOptionsAsync(Subscription show, EpisodeOrder order, int? limit, CancellationToken cancellationToken)
    {
        bool needsBacklog = LibraryOps.SetOptions(show, order, limit);
        await SaveLibraryAsync();
        RaiseChanged();

        if (needsBacklog)
        {
            FeedFetchResult fetch = await Feeds.FetchAsync(show.FeedUrl, null, null, cancellationToken);
            _ = LibraryOps.Apply(show, fetch, DateTimeOffset.UtcNow);
            await SaveLibraryAsync();
            RaiseChanged();
        }
    }

    /// <summary>
    /// Checks every show, a few at a time, with conditional requests. Throws only when every feed
    /// failed (so the poller backs off); partial failures are reported in the outcome.
    /// </summary>
    public static async Task<RefreshOutcome> RefreshAllAsync(CancellationToken cancellationToken)
    {
        List<Subscription> shows = [.. Library.Subscriptions.Where(s => s.IsFollowed)];
        if (shows.Count == 0)
        {
            return new RefreshOutcome(0, 0, null);
        }

        using SemaphoreSlim gate = new(MaxConcurrentFetches);
        int failed = 0;
        int newReleases = 0;
        Exception? lastError = null;
        bool changed = false;

        // Each task resumes on the UI thread after its fetch, so the merges never race each other.
        await Task.WhenAll(shows.Select(async show =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                FeedFetchResult fetch = await Feeds.FetchAsync(show.FeedUrl, show.ETag, show.LastModified, cancellationToken);
                newReleases += LibraryOps.Apply(show, fetch, DateTimeOffset.UtcNow);
                changed |= !fetch.NotModified;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                failed++;
                lastError = ex;
            }
            finally
            {
                gate.Release();
            }
        }));

        cancellationToken.ThrowIfCancellationRequested();
        if (failed == shows.Count)
        {
            throw lastError!;
        }

        SettingsService.LastRefreshedUtc = DateTimeOffset.UtcNow;
        await SaveLibraryAsync();
        if (changed)
        {
            RaiseChanged();
        }

        return new RefreshOutcome(shows.Count, failed, lastError, newReleases);
    }

    /// <summary>
    /// Follows every show in an OPML file that isn't followed yet, a few fetches at a time, saving
    /// once at the end (also when cancelled, keeping the shows added so far). A feed that can't be
    /// read is skipped and counted.
    /// </summary>
    public static async Task<ImportOutcome> ImportOpmlAsync(string path, Action<int> checkedCount, CancellationToken cancellationToken)
    {
        List<OpmlFeed> feeds;
        await using (FileStream stream = File.OpenRead(path))
        {
            feeds = await Task.Run(() => Opml.Read(stream), cancellationToken);
        }

        List<OpmlFeed> missing = [.. feeds.Where(f => Library.Find(f.FeedUrl) is not { IsFollowed: true })];
        using SemaphoreSlim gate = new(MaxConcurrentFetches);
        int added = 0;
        int failed = 0;
        int done = 0;
        try
        {
            // Each task resumes on the UI thread after its fetch, so the adds never race each other.
            await Task.WhenAll(missing.Select(async feed =>
            {
                await gate.WaitAsync(cancellationToken);
                try
                {
                    FeedFetchResult fetch = await Feeds.FetchAsync(feed.FeedUrl, null, null, cancellationToken);
                    if (Library.Find(feed.FeedUrl) is not { IsFollowed: true })
                    {
                        if (Library.Find(feed.FeedUrl) is { } saved)
                        {
                            Library.Subscriptions.Remove(saved);
                        }

                        Library.Subscriptions.Add(LibraryOps.CreateSubscription(feed.FeedUrl, fetch, DateTimeOffset.UtcNow));
                        added++;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    failed++;
                }
                finally
                {
                    gate.Release();
                    checkedCount(++done);
                }
            }));
        }
        finally
        {
            if (added > 0)
            {
                await SaveLibraryAsync();
                RaiseChanged();
            }
        }

        return new ImportOutcome(added, feeds.Count - missing.Count, failed);
    }

    /// <summary>Writes the followed shows to an OPML file, for another podcast app or a backup.</summary>
    public static async Task<int> ExportOpmlAsync(string path)
    {
        List<Subscription> shows = [.. Library.Subscriptions.Where(s => s.IsFollowed).OrderBy(s => s.Title, StringComparer.CurrentCultureIgnoreCase)];
        await Task.Run(() =>
        {
            using FileStream stream = File.Create(path);
            Opml.Write(stream, shows, DateTimeOffset.UtcNow);
        });
        return shows.Count;
    }

    public static EpisodeProgress? GetProgress(string feedUrl, string episodeId) => Progress.Get(feedUrl, episodeId);

    /// <summary>Records the player's position. Written to disk by the caller's schedule (see
    /// <see cref="SaveProgress"/>), not on every call.</summary>
    public static void SetProgress(string feedUrl, string episodeId, EpisodeProgress progress) =>
        Progress.Set(feedUrl, episodeId, progress);

    public static void SetPlayed(string feedUrl, string episodeId, bool played, double? durationSeconds)
    {
        Progress.Set(feedUrl, episodeId, new EpisodeProgress
        {
            PositionSeconds = 0,
            DurationSeconds = durationSeconds,
            IsPlayed = played,
            UpdatedUtc = DateTimeOffset.UtcNow,
        });
        SaveProgress();
        RaiseChanged();
    }

    public static void SaveProgress() =>
        _ = LibraryStore.SaveAsync(ProgressPath, Progress, CoreJsonContext.Default.ProgressBook);

    /// <summary>For exit: written synchronously, since the process is about to end.</summary>
    public static void FlushProgress()
    {
        if (_progress is not null)
        {
            LibraryStore.Save(ProgressPath, _progress, CoreJsonContext.Default.ProgressBook);
        }
    }

    /// <summary>True while a flyout shows the library; work done without one should <see cref="ReleaseIfUnviewed"/> after.</summary>
    public static bool IsViewed { get; set; }

    /// <summary>Drops the in-memory library when the flyout closes; it reloads from disk next time.</summary>
    public static void Unload() => _library = null;

    /// <summary>For background work (the new-episode check, auto-play): unloads unless a flyout is using the library.</summary>
    public static void ReleaseIfUnviewed()
    {
        if (!IsViewed)
        {
            Unload();
        }
    }

    private static void RaiseChanged()
    {
        Version++;
        Changed?.Invoke(null, EventArgs.Empty);
    }

    private static Task SaveLibraryAsync() =>
        _library is null ? Task.CompletedTask : LibraryStore.SaveAsync(LibraryPath, _library, CoreJsonContext.Default.Library);
}
