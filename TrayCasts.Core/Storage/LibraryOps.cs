using TrayCasts.Feeds;

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
        };
        Apply(subscription, fetch, now);
        return subscription;
    }

    /// <summary>Applies a fetch to a subscription. A 304 only updates the check time.</summary>
    public static void Apply(Subscription subscription, FeedFetchResult fetch, DateTimeOffset now)
    {
        subscription.LastCheckedUtc = now;
        if (fetch.Feed is not { } feed)
        {
            return;
        }

        subscription.Title = feed.Title;
        subscription.Author = feed.Author;
        subscription.ArtworkUrl = feed.ArtworkUrl;
        subscription.Episodes = [.. feed.Episodes];
        subscription.ETag = fetch.ETag;
        subscription.LastModified = fetch.LastModified;
    }

    /// <summary>The newest episodes across every show, newest first.</summary>
    public static List<ShowEpisode> Latest(IEnumerable<Subscription> subscriptions, int count) =>
    [
        .. subscriptions
            .SelectMany(s => s.Episodes.Select(e => new ShowEpisode(s, e)))
            .OrderByDescending(x => x.Episode.Published ?? DateTimeOffset.MinValue)
            .Take(count),
    ];

    /// <summary>Shows sorted by their newest episode, so active shows come first.</summary>
    public static List<Subscription> ShowsByRecentActivity(IEnumerable<Subscription> subscriptions) =>
    [
        .. subscriptions
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
