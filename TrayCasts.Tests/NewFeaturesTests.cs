using System.Buffers.Binary;
using System.Text;
using System.Xml;
using TrayCasts.Feeds;
using TrayCasts.Storage;
using TrayCasts.Tray;

namespace TrayCasts.Tests;

[TestClass]
public sealed class OpmlTests
{
    [TestMethod]
    public void Read_NestedOutlines_FlattensAndNormalizes()
    {
        const string xml = """
            <?xml version="1.0"?>
            <opml version="1.0"><head><title>x</title></head><body>
              <outline text="Folder">
                <outline type="rss" text="One" xmlUrl="https://a.test/one.xml" />
                <outline type="rss" title="Two" xmlUrl="feed://b.test/two" />
              </outline>
              <outline text="No feed" />
              <outline text="Dup" xmlUrl="https://a.test/one.xml" />
              <outline text="Bad" xmlUrl="ftp://c.test/rss" />
            </body></opml>
            """;

        List<OpmlFeed> feeds = Opml.Read(Stream(xml));

        CollectionAssert.AreEqual(new[] { "https://a.test/one.xml", "https://b.test/two" }, feeds.Select(f => f.FeedUrl).ToArray());
        Assert.AreEqual("One", feeds[0].Title);
        Assert.AreEqual("Two", feeds[1].Title);
    }

    [TestMethod]
    public void Read_NotOpml_Throws()
    {
        Assert.ThrowsExactly<XmlException>(() => Opml.Read(Stream("<rss><channel /></rss>")));
        Assert.ThrowsExactly<XmlException>(() => Opml.Read(Stream("not xml")));
    }

    [TestMethod]
    public void Read_Dtd_IsRejected()
    {
        const string xml = """<?xml version="1.0"?><!DOCTYPE opml [<!ENTITY e "x">]><opml><body /></opml>""";

        Assert.ThrowsExactly<XmlException>(() => Opml.Read(Stream(xml)));
    }

    [TestMethod]
    public void Write_ThenRead_RoundTrips()
    {
        Subscription[] shows =
        [
            new() { FeedUrl = "https://a.test/rss", Title = "A & B" },
            new() { FeedUrl = "https://b.test/rss?x=1&y=2", Title = "Second" },
        ];
        using MemoryStream stream = new();

        Opml.Write(stream, shows, DateTimeOffset.UnixEpoch);
        stream.Position = 0;
        List<OpmlFeed> feeds = Opml.Read(stream);

        CollectionAssert.AreEqual(shows.Select(s => s.FeedUrl).ToArray(), feeds.Select(f => f.FeedUrl).ToArray());
        Assert.AreEqual("A & B", feeds[0].Title);
    }

    private static MemoryStream Stream(string text) => new(Encoding.UTF8.GetBytes(text));
}

[TestClass]
public sealed class NewReleaseAndNextTests
{
    [TestMethod]
    public void CountNewReleases_OnlyNewerUnknownEpisodes()
    {
        Episode[] before = [Ep("2", 2), Ep("1", 1)];
        Episode[] after = [Ep("4", 4), Ep("3", 3), Ep("2", 2), Ep("old-new-id", 1)];

        Assert.AreEqual(2, LibraryOps.CountNewReleases(before, after));
    }

    [TestMethod]
    public void CountNewReleases_NoBaseline_CountsNone()
    {
        Assert.AreEqual(0, LibraryOps.CountNewReleases([], [Ep("1", 1)]));
    }

    [TestMethod]
    public void Apply_NewFeed_ReturnsNewReleaseCount()
    {
        Subscription show = Show("https://a.test/rss", ("1", 1));
        ParsedFeed feed = new("Show", null, null, [Ep("2", 2), Ep("1", 1)]);

        int count = LibraryOps.Apply(show, new FeedFetchResult(feed, null, null), DateTimeOffset.UtcNow);

        Assert.AreEqual(1, count);
    }

    [TestMethod]
    public void NextToPlay_BacklogShow_ContinuesSameShow()
    {
        Subscription serial = Show("https://serial.test/rss", ("s3", 3), ("s2", 2), ("s1", 1));
        serial.Order = EpisodeOrder.OldestFirst;
        Subscription news = Show("https://news.test/rss", ("n1", 20));

        ShowEpisode? next = LibraryOps.NextToPlay([news, serial], new ProgressBook(), serial.FeedUrl, "s1");

        Assert.AreEqual("s2", next?.Episode.Id);
    }

    [TestMethod]
    public void NextToPlay_ShowFinished_FallsBackToLatest()
    {
        Subscription a = Show("https://a.test/rss", ("a1", 5));
        Subscription b = Show("https://b.test/rss", ("b2", 4), ("b1", 3));
        ProgressBook progress = new();
        progress.Set(b.FeedUrl, "b2", new EpisodeProgress { IsPlayed = true });

        ShowEpisode? next = LibraryOps.NextToPlay([a, b], progress, a.FeedUrl, "a1");

        Assert.AreEqual("b1", next?.Episode.Id);
    }

    [TestMethod]
    public void NextToPlay_EverythingHeard_ReturnsNull()
    {
        Subscription a = Show("https://a.test/rss", ("a1", 5));

        Assert.IsNull(LibraryOps.NextToPlay([a], new ProgressBook(), a.FeedUrl, "a1"));
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

[TestClass]
public sealed class BadgeIconTests
{
    private const uint Red = 0xFFC42B1C;

    [TestMethod]
    public void Label_ClampsAtNinePlus()
    {
        Assert.AreEqual("3", BadgeIcon.Label(3));
        Assert.AreEqual("9", BadgeIcon.Label(9));
        Assert.AreEqual("9+", BadgeIcon.Label(10));
    }

    [TestMethod]
    public void TextColorFor_PicksContrast()
    {
        Assert.AreEqual(0xFFFFFFFFu, BadgeIcon.TextColorFor(Red));
        Assert.AreEqual(0xFF000000u, BadgeIcon.TextColorFor(0xFFFFE680));
    }

    [TestMethod]
    [DataRow(16)]
    [DataRow(24)]
    [DataRow(32)]
    public void Create_KeepsIconAwayFromBadge_AndPaintsBadgeCorner(int size)
    {
        uint[] icon = [.. Enumerable.Repeat(0xFF808080u, size * size)];

        byte[] ico = BadgeIcon.Create(icon, size, 12, Red);

        Assert.AreEqual(size, ico[6]);
        Assert.AreEqual(0xFF808080u, Pixel(ico, size, 0, 0), "Top-left stays the icon.");
        uint corner = Pixel(ico, size, size - 2, size / 2 + size / 4);
        Assert.AreNotEqual(0xFF808080u, corner, "Bottom-right is the badge.");
    }

    [TestMethod]
    public void Create_WrongPixelCount_Throws()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => BadgeIcon.Create(new uint[10], 16, 1, Red));
    }

    /// <summary>Reads pixel (x, y), top-down, from the bottom-up DIB.</summary>
    private static uint Pixel(byte[] ico, int size, int x, int y)
    {
        int bits = BadgeIcon.ImageOffset + 40;
        int row = size - 1 - y;
        return BinaryPrimitives.ReadUInt32LittleEndian(ico.AsSpan(bits + (((row * size) + x) * 4)));
    }
}
