using TrayCasts.Feeds;
using TrayCasts.Shows;

namespace TrayCasts.Storage;

/// <summary>An episode together with the show it belongs to.</summary>
public sealed record ShowEpisode(Subscription Show, Episode Episode);

/// <summary>Pure operations on the library: subscribing, merging a refresh, and the latest list.</summary>
public static class LibraryOps
{
    public static Subscription CreateSubscription(string feedUrl, FeedFetchResult fetch, DateTimeOffset now)
    {
        ParsedFeed feed = fetch.Feed ?? throw new ArgumentException("A new subscription needs the feed body.", nameof(fetch));
        Subscription subscription = new()
        {
            FeedUrl = feedUrl,
            Title = feed.Title,
            AddedUtc = now,
            EpisodeLimit = ShowPolicy.DefaultEpisodeLimit,
        };
        Apply(subscription, fetch, now);
        return subscription;
    }

    /// <summary>
    /// A show as found in its feed, to look through before following or saving anything from it.
    /// Not part of the library; every episode is listed, as for a show heard newest first.
    /// </summary>
    public static Subscription CreatePreview(string feedUrl, FeedFetchResult fetch, DateTimeOffset now)
    {
        Subscription preview = CreateSubscription(feedUrl, fetch, now);
        preview.IsFollowed = false;
        preview.EpisodeLimit = null;
        return preview;
    }

    /// <summary>Whether this one episode has been saved without following its show.</summary>
    public static bool IsEpisodeSaved(Library library, string feedUrl, string episodeId) =>
        library.Find(feedUrl) is { IsFollowed: false } show && show.Episodes.Any(e => e.Id == episodeId);

    /// <summary>
    /// Saves one episode of a show the user doesn't follow. The show is kept, with just its saved
    /// episodes, so they can be listed and played without it. A followed show already offers its
    /// episodes, so nothing changes for it.
    /// </summary>
    /// <returns>True when the library changed.</returns>
    public static bool SaveEpisode(Library library, Subscription source, Episode episode, DateTimeOffset now)
    {
        Subscription? kept = library.Find(source.FeedUrl);
        if (kept is { IsFollowed: true } || (kept is not null && kept.Episodes.Any(e => e.Id == episode.Id)))
        {
            return false;
        }

        if (kept is null)
        {
            kept = new Subscription
            {
                FeedUrl = source.FeedUrl,
                Title = source.Title,
                Author = source.Author,
                ArtworkUrl = source.ArtworkUrl,
                AddedUtc = now,
                IsFollowed = false,
            };
            library.Subscriptions.Add(kept);
        }

        // Newest first, undated last, like every stored episode list.
        kept.Episodes = [.. kept.Episodes.Append(episode).OrderByDescending(e => e.Published ?? DateTimeOffset.MinValue)];
        return true;
    }

    /// <summary>
    /// Forgets a saved episode; the show goes with its last one.
    /// </summary>
    /// <returns>True when the show itself was removed, so its progress can be dropped too.</returns>
    public static bool RemoveSavedEpisode(Library library, string feedUrl, string episodeId)
    {
        if (library.Find(feedUrl) is not { IsFollowed: false } kept)
        {
            return false;
        }

        kept.Episodes = [.. kept.Episodes.Where(e => e.Id != episodeId)];
        return kept.Episodes.Count == 0 && library.Subscriptions.Remove(kept);
    }

    /// <summary>Applies a fetch to a subscription. A 304 only updates the check time.</summary>
    /// <returns>How many episodes are new releases (see <see cref="CountNewReleases"/>).</returns>
    public static int Apply(Subscription subscription, FeedFetchResult fetch, DateTimeOffset now)
    {
        subscription.LastCheckedUtc = now;
        if (fetch.Feed is not { } feed)
        {
            return 0;
        }

        List<Episode> episodes = [.. feed.Episodes.Take(ShowPolicy.EpisodesToKeep(subscription.Order))];
        int newReleases = CountNewReleases(subscription.Episodes, episodes);
        subscription.Title = feed.Title;
        subscription.Author = feed.Author;
        subscription.ArtworkUrl = feed.ArtworkUrl;
        subscription.Episodes = episodes;
        subscription.ETag = fetch.ETag;
        subscription.LastModified = fetch.LastModified;
        return newReleases;
    }

    /// <summary>
    /// Episodes released since the last fetch: not stored before and dated after the newest stored
    /// one. The date check keeps a feed that rewrites its guids, or a back catalog fetched for a
    /// show switched to oldest first, from counting old episodes as new. A show with nothing stored
    /// has no baseline, so it counts none.
    /// </summary>
    public static int CountNewReleases(IReadOnlyList<Episode> before, IReadOnlyList<Episode> after)
    {
        DateTimeOffset? newest = before.Max(e => e.Published);
        if (newest is not { } since)
        {
            return 0;
        }

        HashSet<string> known = [.. before.Select(e => e.Id)];
        return after.Count(e => !known.Contains(e.Id) && e.Published > since);
    }

    /// <summary>
    /// What to play after an episode finishes: the next unplayed episode in the same show's queue
    /// (<see cref="ShowPolicy.UpNext"/>), so a serial carries on; otherwise the first unplayed one in
    /// the Latest list. Null when everything's been heard.
    /// </summary>
    public static ShowEpisode? NextToPlay(IEnumerable<Subscription> subscriptions, ProgressBook progress, string finishedFeedUrl, string finishedEpisodeId)
    {
        List<Subscription> shows = [.. subscriptions];
        bool IsDone(Subscription show, Episode episode) =>
            (show.FeedUrl == finishedFeedUrl && episode.Id == finishedEpisodeId)
            || progress.Get(show.FeedUrl, episode.Id)?.IsPlayed == true;

        if (shows.FirstOrDefault(s => s.FeedUrl == finishedFeedUrl) is { } sameShow
            && ShowPolicy.UpNext(sameShow, e => IsDone(sameShow, e)).FirstOrDefault(e => !IsDone(sameShow, e)) is { } next)
        {
            return new ShowEpisode(sameShow, next);
        }

        return Latest(shows, progress, int.MaxValue).FirstOrDefault(x => !IsDone(x.Show, x.Episode));
    }

    /// <summary>
    /// Changes how a show is listened to. Returns true when the show now needs episodes older than
    /// those stored, i.e. a full fetch; the validators are cleared so even a later refresh gets the
    /// whole feed rather than a 304. Going back to newest first drops the back catalog at once.
    /// </summary>
    public static bool SetOptions(Subscription show, EpisodeOrder order, int? limit)
    {
        order = ShowPolicy.SanitizeOrder(order);
        int keep = ShowPolicy.EpisodesToKeep(order);
        bool needsBacklog = keep > ShowPolicy.EpisodesToKeep(show.Order);

        show.Order = order;
        show.EpisodeLimit = ShowPolicy.SanitizeLimit(limit);
        if (show.Episodes.Count > keep)
        {
            show.Episodes = [.. show.Episodes.Take(keep)];
        }

        if (needsBacklog)
        {
            show.ETag = null;
            show.LastModified = null;
        }

        return needsBacklog;
    }

    /// <summary>What's up next across every show (see <see cref="ShowPolicy.UpNext"/>), newest activity first.</summary>
    public static List<ShowEpisode> Latest(IEnumerable<Subscription> subscriptions, ProgressBook progress, int count) =>
    [
        .. subscriptions
            .SelectMany(s => ShowPolicy.UpNext(s, e => progress.Get(s.FeedUrl, e.Id)?.IsPlayed == true)
                .Select(e => new ShowEpisode(s, e)))
            .OrderByDescending(x => ShowPolicy.LatestSortKey(x.Show, x.Episode)) // Stable: a show's queue stays in order.
            .Take(count),
    ];

    /// <summary>Shows sorted by their newest episode, so active shows come first.</summary>
    public static List<Subscription> ShowsByRecentActivity(IEnumerable<Subscription> subscriptions) =>
    [
        .. subscriptions
            .Where(s => s.IsFollowed)
            .OrderByDescending(s => s.Episodes.Count > 0 ? s.Episodes.Max(e => e.Published ?? DateTimeOffset.MinValue) : DateTimeOffset.MinValue)
            .ThenBy(s => s.Title, StringComparer.CurrentCultureIgnoreCase),
    ];

    public static NowPlaying ToNowPlaying(Subscription show, Episode episode) => new()
    {
        FeedUrl = show.FeedUrl,
        ShowTitle = show.Title,
        ShowArtworkUrl = show.ArtworkUrl,
        Episode = episode,
    };
}
