using TrayCasts.Feeds;
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

        List<ShowEpisode> latest = LibraryOps.Latest([a, b], 3);

        CollectionAssert.AreEqual(new[] { "b4", "a3", "b2" }, latest.Select(x => x.Episode.Id).ToArray());
        Assert.AreSame(b, latest[0].Show);
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
