using TrayCasts.Feeds;
using TrayCasts.Shows;
using TrayCasts.Storage;

namespace TrayCasts.Tests;

[TestClass]
public sealed class StorageTests
{
    private string _folder = null!;

    [TestInitialize]
    public void Setup()
    {
        _folder = Path.Combine(Path.GetTempPath(), "TrayCastsTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [TestMethod]
    public async Task SaveAsync_ThenLoad_RoundTrips()
    {
        string path = Path.Combine(_folder, "library.json");
        Library library = new();
        library.Subscriptions.Add(Show("https://a.test/rss", ("1", 2)));

        await LibraryStore.SaveAsync(path, library, CoreJsonContext.Default.Library);
        Library loaded = LibraryStore.Load(path, CoreJsonContext.Default.Library);

        Assert.HasCount(1, loaded.Subscriptions);
        Assert.AreEqual("1", loaded.Subscriptions[0].Episodes[0].Id);
        Assert.IsFalse(File.Exists(path + ".tmp"));
    }

    [TestMethod]
    public void Load_MissingOrCorrupt_ReturnsEmpty()
    {
        string path = Path.Combine(_folder, "progress.json");
        Assert.IsEmpty(LibraryStore.Load(path, CoreJsonContext.Default.ProgressBook).Entries);

        File.WriteAllText(path, "{ not json");
        Assert.IsEmpty(LibraryStore.Load(path, CoreJsonContext.Default.ProgressBook).Entries);
    }

    [TestMethod]
    public void ProgressBook_RemoveFeed_RemovesOnlyThatShow()
    {
        ProgressBook book = new();
        book.Set("https://a.test/rss", "1", new EpisodeProgress { PositionSeconds = 5 });
        book.Set("https://a.test/rss", "2", new EpisodeProgress { PositionSeconds = 6 });
        book.Set("https://a.test/rss2", "1", new EpisodeProgress { PositionSeconds = 7 });

        Assert.AreEqual(2, book.RemoveFeed("https://a.test/rss"));
        Assert.IsNotNull(book.Get("https://a.test/rss2", "1"));
    }

    [TestMethod]
    public void CreateSubscription_NewShow_StartsWithDefaultLimit()
    {
        ParsedFeed feed = new("Show", null, null, [Ep("1", 1)]);

        Subscription show = LibraryOps.CreateSubscription("https://a.test/rss", new FeedFetchResult(feed, null, null), DateTimeOffset.UtcNow);

        Assert.AreEqual(ShowPolicy.DefaultEpisodeLimit, show.EpisodeLimit);
    }

    [TestMethod]
    public void Apply_NotModified_OnlyUpdatesCheckTime()
    {
        Subscription show = Show("https://a.test/rss", ("1", 1));
        show.ETag = "\"v1\"";
        DateTimeOffset now = DateTimeOffset.UtcNow;

        LibraryOps.Apply(show, new FeedFetchResult(null, "\"v1\"", null), now);

        Assert.AreEqual(now, show.LastCheckedUtc);
        Assert.HasCount(1, show.Episodes);
        Assert.AreEqual("\"v1\"", show.ETag);
    }

    [TestMethod]
    public void Apply_NewFeed_ReplacesEpisodesAndValidators()
    {
        Subscription show = Show("https://a.test/rss", ("1", 1));
        ParsedFeed feed = new("Renamed", "Host", "https://a.test/art.jpg", [Ep("2", 2), Ep("1", 1)]);

        LibraryOps.Apply(show, new FeedFetchResult(feed, "\"v2\"", "Tue, 03 Sep 2024 10:00:00 GMT"), DateTimeOffset.UtcNow);

        Assert.AreEqual("Renamed", show.Title);
        Assert.HasCount(2, show.Episodes);
        Assert.AreEqual("\"v2\"", show.ETag);
    }

    [TestMethod]
    public void Latest_MergesShowsNewestFirst()
    {
        Subscription a = Show("https://a.test/rss", ("a1", 1), ("a3", 3));
        Subscription b = Show("https://b.test/rss", ("b2", 2), ("b4", 4));

        List<ShowEpisode> latest = LibraryOps.Latest([a, b], new ProgressBook(), 3);

        CollectionAssert.AreEqual(new[] { "b4", "a3", "b2" }, latest.Select(x => x.Episode.Id).ToArray());
        Assert.AreSame(b, latest[0].Show);
    }

    [TestMethod]
    public void Latest_BacklogShow_SortsWithItsNewestRelease()
    {
        Subscription news = Show("https://n.test/rss", ("n5", 5));
        Subscription serial = Show("https://s.test/rss", ("s9", 9), ("s2", 2), ("s1", 1));
        serial.Order = EpisodeOrder.OldestFirst;
        serial.EpisodeLimit = 2;

        List<ShowEpisode> latest = LibraryOps.Latest([news, serial], new ProgressBook(), 10);

        CollectionAssert.AreEqual(new[] { "s1", "s2", "n5" }, latest.Select(x => x.Episode.Id).ToArray());
    }

    [TestMethod]
    public void Apply_NewestFirst_KeepsOnlyRecentEpisodes()
    {
        Subscription show = Show("https://a.test/rss");
        ParsedFeed feed = new("Show", null, null, [.. Enumerable.Range(0, ShowPolicy.RecentEpisodesKept + 5).Select(i => Ep($"e{i}", 1))]);

        LibraryOps.Apply(show, new FeedFetchResult(feed, null, null), DateTimeOffset.UtcNow);
        Assert.HasCount(ShowPolicy.RecentEpisodesKept, show.Episodes);

        show.Order = EpisodeOrder.OldestFirst;
        LibraryOps.Apply(show, new FeedFetchResult(feed, null, null), DateTimeOffset.UtcNow);
        Assert.HasCount(ShowPolicy.RecentEpisodesKept + 5, show.Episodes);
    }

    [TestMethod]
    public void SetOptions_ToBacklogOrder_NeedsFullFeedAndClearsValidators()
    {
        Subscription show = Show("https://a.test/rss", ("1", 1));
        show.ETag = "\"v1\"";
        show.LastModified = "yesterday";

        bool needsBacklog = LibraryOps.SetOptions(show, EpisodeOrder.NewestThenCatchUp, 3);

        Assert.IsTrue(needsBacklog);
        Assert.IsNull(show.ETag);
        Assert.IsNull(show.LastModified);
        Assert.AreEqual(EpisodeOrder.NewestThenCatchUp, show.Order);
        Assert.AreEqual(3, show.EpisodeLimit);
    }

    [TestMethod]
    public void SetOptions_BackToNewestFirst_TrimsBacklog()
    {
        Subscription show = Show("https://a.test/rss");
        show.Order = EpisodeOrder.OldestFirst;
        show.ETag = "\"v1\"";
        show.Episodes = [.. Enumerable.Range(0, ShowPolicy.RecentEpisodesKept + 5).Select(i => Ep($"e{i}", 1))];

        bool needsBacklog = LibraryOps.SetOptions(show, EpisodeOrder.NewestFirst, 4);

        Assert.IsFalse(needsBacklog);
        Assert.HasCount(ShowPolicy.RecentEpisodesKept, show.Episodes);
        Assert.AreEqual("\"v1\"", show.ETag);
    }

    [TestMethod]
    public void SetOptions_UnofferedLimit_MeansAll()
    {
        Subscription show = Show("https://a.test/rss");

        LibraryOps.SetOptions(show, EpisodeOrder.NewestFirst, 7);

        Assert.IsNull(show.EpisodeLimit);
    }

    [TestMethod]
    public async Task SaveAsync_ShowOptions_RoundTrip()
    {
        string path = Path.Combine(_folder, "library.json");
        Library library = new();
        Subscription show = Show("https://a.test/rss");
        show.Order = EpisodeOrder.NewestThenCatchUp;
        show.EpisodeLimit = 2;
        library.Subscriptions.Add(show);

        await LibraryStore.SaveAsync(path, library, CoreJsonContext.Default.Library);
        Subscription loaded = LibraryStore.Load(path, CoreJsonContext.Default.Library).Subscriptions[0];

        StringAssert.Contains(File.ReadAllText(path), "\"NewestThenCatchUp\"");
        Assert.AreEqual(EpisodeOrder.NewestThenCatchUp, loaded.Order);
        Assert.AreEqual(2, loaded.EpisodeLimit);
    }

    [TestMethod]
    public void ShowsByRecentActivity_EmptyShowsLast()
    {
        Subscription empty = Show("https://e.test/rss");
        Subscription old = Show("https://o.test/rss", ("o", 1));
        Subscription fresh = Show("https://f.test/rss", ("f", 9));

        List<Subscription> sorted = LibraryOps.ShowsByRecentActivity([empty, old, fresh]);

        CollectionAssert.AreEqual(new[] { fresh, old, empty }, sorted);
    }

    private static Subscription Show(string feedUrl, params (string Id, int Day)[] episodes) => new()
    {
        FeedUrl = feedUrl,
        Title = feedUrl,
        Episodes = [.. episodes.Select(e => Ep(e.Id, e.Day))],
    };

    private static Episode Ep(string id, int day) => new()
    {
        Id = id,
        Title = id,
        AudioUrl = $"https://x.test/{id}.mp3",
        Published = new DateTimeOffset(2026, 9, day, 0, 0, 0, TimeSpan.Zero),
    };
}
