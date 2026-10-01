using TrayCasts.Feeds;

namespace TrayCasts.Tests;

[TestClass]
public sealed class FeedParserTests
{
    private const string FeedUrl = "https://example.com/podcast/feed.xml";

    private const string SampleFeed = """
        <?xml version="1.0" encoding="UTF-8"?>
        <rss version="2.0" xmlns:itunes="http://www.itunes.com/dtds/podcast-1.0.dtd" xmlns:media="http://search.yahoo.com/mrss/">
          <channel>
            <title>  The   Sample &amp;amp; Show </title>
            <itunes:author>Sample Author</itunes:author>
            <itunes:image href="https://example.com/art.jpg" />
            <item>
              <title>Older episode</title>
              <guid isPermaLink="false">ep-1</guid>
              <pubDate>Mon, 02 Sep 2024 10:00:00 +0000</pubDate>
              <itunes:duration>45:30</itunes:duration>
              <enclosure url="https://cdn.example.com/ep1.mp3" length="1" type="audio/mpeg" />
            </item>
            <item>
              <title>Newest episode</title>
              <guid>ep-2</guid>
              <pubDate>Tue, 03 Sep 2024 10:00:00 GMT</pubDate>
              <itunes:duration>3723</itunes:duration>
              <itunes:image href="https://example.com/ep2.jpg" />
              <enclosure url="media/ep2.m4a" type="audio/x-m4a" />
            </item>
            <item>
              <title>Text-only post</title>
              <guid>post</guid>
            </item>
            <item>
              <title>No guid</title>
              <pubDate>Sun, 01 Sep 2024 10:00:00 PDT</pubDate>
              <media:content url="https://cdn.example.com/ep0.mp3" medium="audio" />
            </item>
          </channel>
        </rss>
        """;

    [TestMethod]
    public void Parse_SampleFeed_ReadsShowFields()
    {
        ParsedFeed feed = FeedParser.Parse(SampleFeed, FeedUrl);

        Assert.AreEqual("The Sample & Show", feed.Title);
        Assert.AreEqual("Sample Author", feed.Author);
        Assert.AreEqual("https://example.com/art.jpg", feed.ArtworkUrl);
    }

    [TestMethod]
    public void Parse_SampleFeed_KeepsOnlyPlayableItemsNewestFirst()
    {
        ParsedFeed feed = FeedParser.Parse(SampleFeed, FeedUrl);

        CollectionAssert.AreEqual(new[] { "ep-2", "ep-1", "https://cdn.example.com/ep0.mp3" }, feed.Episodes.Select(e => e.Id).ToArray());
    }

    [TestMethod]
    public void Parse_RelativeEnclosure_ResolvesAgainstFeedUrl()
    {
        Episode newest = FeedParser.Parse(SampleFeed, FeedUrl).Episodes[0];

        Assert.AreEqual("https://example.com/podcast/media/ep2.m4a", newest.AudioUrl);
        Assert.AreEqual("https://example.com/ep2.jpg", newest.ArtworkUrl);
        Assert.AreEqual(3723, newest.DurationSeconds);
    }

    [TestMethod]
    public void Parse_ItemWithoutGuid_UsesAudioUrlAsId()
    {
        Episode episode = FeedParser.Parse(SampleFeed, FeedUrl).Episodes[^1];

        Assert.AreEqual("No guid", episode.Title);
        Assert.AreEqual(new DateTimeOffset(2024, 9, 1, 10, 0, 0, TimeSpan.FromHours(-7)), episode.Published);
    }

    [TestMethod]
    public void Parse_DuplicateGuids_KeepsFirst()
    {
        string xml = """
            <rss version="2.0"><channel><title>Dupes</title>
              <item><title>A</title><guid>same</guid><enclosure url="https://x.test/a.mp3" type="audio/mpeg"/></item>
              <item><title>B</title><guid>same</guid><enclosure url="https://x.test/b.mp3" type="audio/mpeg"/></item>
            </channel></rss>
            """;

        ParsedFeed feed = FeedParser.Parse(xml, FeedUrl);

        Assert.HasCount(1, feed.Episodes);
        Assert.AreEqual("A", feed.Episodes[0].Title);
    }

    [TestMethod]
    public void Parse_MoreThanMax_IsCapped()
    {
        string items = string.Concat(Enumerable.Range(0, FeedParser.MaxEpisodes + 20)
            .Select(i => $"<item><guid>{i}</guid><enclosure url=\"https://x.test/{i}.mp3\" type=\"audio/mpeg\"/></item>"));

        ParsedFeed feed = FeedParser.Parse($"<rss><channel><title>Big</title>{items}</channel></rss>", FeedUrl);

        Assert.HasCount(FeedParser.MaxEpisodes, feed.Episodes);
    }

    [TestMethod]
    public void Parse_NonHttpEnclosure_IsSkipped()
    {
        string xml = """<rss><channel><title>T</title><item><guid>1</guid><enclosure url="file:///c:/secret.mp3" type="audio/mpeg"/></item></channel></rss>""";

        Assert.IsEmpty(FeedParser.Parse(xml, FeedUrl).Episodes);
    }

    [TestMethod]
    public void Parse_HtmlPage_ThrowsFeedFormatException()
    {
        Assert.ThrowsExactly<FeedFormatException>(() => FeedParser.Parse("<html><body>Not a feed</body></html>", FeedUrl));
    }

    [TestMethod]
    public void Parse_Garbage_ThrowsFeedFormatException()
    {
        Assert.ThrowsExactly<FeedFormatException>(() => FeedParser.Parse("{ \"json\": true }", FeedUrl));
    }

    [TestMethod]
    public void Parse_DtdWithEntities_DoesNotExpandThem()
    {
        string xml = """
            <?xml version="1.0"?>
            <!DOCTYPE rss [<!ENTITY boom "BOOM">]>
            <rss><channel><title>Safe</title></channel></rss>
            """;

        Assert.AreEqual("Safe", FeedParser.Parse(xml, FeedUrl).Title);
    }
}
