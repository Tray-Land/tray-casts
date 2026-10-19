namespace TrayCasts.Shows;

/// <summary>
/// Per-show listening rules: which episodes come up next (<see cref="Subscription.Order"/>), how
/// many of them the Latest list shows (<see cref="Subscription.EpisodeLimit"/>), and how many
/// episodes are kept on disk for each order.
/// </summary>
public static class ShowPolicy
{
    /// <summary>The episode limits offered; null (all) is offered too.</summary>
    public static readonly IReadOnlyList<int> LimitChoices = [1, 2, 3, 5, 10];

    /// <summary>The limit a newly followed show starts with, so one busy show doesn't fill the Latest list.</summary>
    public const int DefaultEpisodeLimit = 3;

    /// <summary>Episodes kept for a show heard newest first. Older ones are rarely played from a
    /// tray flyout, and the whole library is loaded each time the flyout opens.</summary>
    public const int RecentEpisodesKept = 100;

    /// <summary>The limit a show gets when it switches to an order that works through a backlog
    /// with no limit set, so hundreds of old episodes don't flood the Latest list.</summary>
    public const int DefaultBacklogLimit = 1;

    /// <summary>A show heard from the start needs its whole back catalog, up to the parser's cap.</summary>
    public static int EpisodesToKeep(EpisodeOrder order) =>
        order == EpisodeOrder.NewestFirst ? RecentEpisodesKept : Feeds.FeedParser.MaxEpisodes;

    public static bool WorksThroughBacklog(EpisodeOrder order) => order != EpisodeOrder.NewestFirst;

    /// <summary>Clamps a stored limit to the offered choices so a hand-edited value can't misbehave.</summary>
    public static int? SanitizeLimit(int? limit) => limit is { } n && LimitChoices.Contains(n) ? n : null;

    public static EpisodeOrder SanitizeOrder(EpisodeOrder order) =>
        Enum.IsDefined(order) ? order : EpisodeOrder.NewestFirst;

    /// <summary>
    /// What the Latest list offers from this show, in the order it should be heard:
    /// <list type="bullet">
    /// <item>Newest first: the newest episodes, played ones included (they show as played).</item>
    /// <item>Oldest first: the oldest unplayed episodes.</item>
    /// <item>Newest, then catch up: the newest episode while it's unplayed, then the oldest unplayed.</item>
    /// </list>
    /// </summary>
    public static IEnumerable<Episode> UpNext(Subscription show, Func<Episode, bool> isPlayed)
    {
        // A show kept for saved episodes offers exactly those, whatever their order or limit.
        if (!show.IsFollowed)
        {
            return show.Episodes;
        }

        // Stored newest first; undated episodes are at the end, so they count as the oldest.
        IEnumerable<Episode> backlog = show.Episodes.AsEnumerable().Reverse().Where(e => !isPlayed(e));
        IEnumerable<Episode> queue = SanitizeOrder(show.Order) switch
        {
            EpisodeOrder.OldestFirst => backlog,
            EpisodeOrder.NewestThenCatchUp when show.Episodes is [var newest, ..] && !isPlayed(newest) =>
                backlog.Where(e => e != newest).Prepend(newest),
            EpisodeOrder.NewestThenCatchUp => backlog,
            _ => show.Episodes,
        };

        return SanitizeLimit(show.EpisodeLimit) is { } limit ? queue.Take(limit) : queue;
    }

    /// <summary>The show's own page: every episode, oldest at the top for a show heard from the start.</summary>
    public static IEnumerable<Episode> InPageOrder(Subscription show) =>
        SanitizeOrder(show.Order) == EpisodeOrder.OldestFirst ? show.Episodes.AsEnumerable().Reverse() : show.Episodes;

    /// <summary>
    /// Where a show's up-next episodes sit in the Latest list. Newest-first episodes sort by their own
    /// date; a backlog episode sorts by the show's newest release, so an old episode that's next in
    /// line appears with the show's recent activity instead of sinking below every other show.
    /// </summary>
    public static DateTimeOffset LatestSortKey(Subscription show, Episode episode) =>
        !show.IsFollowed || SanitizeOrder(show.Order) == EpisodeOrder.NewestFirst || show.Episodes.Count == 0
            ? episode.Published ?? DateTimeOffset.MinValue
            : show.Episodes[0].Published ?? DateTimeOffset.MinValue;
}
