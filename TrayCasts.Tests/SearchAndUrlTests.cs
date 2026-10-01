using TrayCasts.Feeds;
using TrayCasts.Search;

namespace TrayCasts.Tests;

[TestClass]
public sealed class SearchAndUrlTests
{
    [TestMethod]
    [DataRow("https://example.com/feed.xml", "https://example.com/feed.xml")]
    [DataRow("  http://example.com/rss  ", "http://example.com/rss")]
    [DataRow("feed://example.com/rss", "https://example.com/rss")]
    [DataRow("feed:https://example.com/rss", "https://example.com/rss")]
    [DataRow("podcast://example.com/rss", "https://example.com/rss")]
    [DataRow("feeds.example.com/show", "https://feeds.example.com/show")]
    public void TryNormalize_Addresses_ReturnHttpUrl(string input, string expected)
    {
        Assert.IsTrue(FeedUrlPolicy.TryNormalize(input, out string url));
        Assert.AreEqual(expected, url);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("the daily")]
    [DataRow("radiolab")]
    [DataRow("ftp://example.com/feed")]
    [DataRow("file:///c:/feed.xml")]
    [DataRow(".hidden/feed")]
    public void TryNormalize_SearchTermsAndOtherSchemes_ReturnFalse(string input)
    {
        Assert.IsFalse(FeedUrlPolicy.TryNormalize(input, out _));
    }

    [TestMethod]
    public void BuildUri_EscapesTerm()
    {
        Uri uri = PodcastSearch.BuildUri(" this & that ");

        StringAssert.Contains(uri.AbsoluteUri, "term=this%20%26%20that");
        StringAssert.Contains(uri.AbsoluteUri, "media=podcast");
    }

    [TestMethod]
    public void Parse_ITunesResponse_MapsResultsAndDropsOnesWithoutFeeds()
    {
        string json = """
            {"resultCount":3,"results":[
              {"collectionName":"Show A","artistName":"Host A","feedUrl":"https://a.test/rss","artworkUrl100":"https://a.test/100.jpg","artworkUrl600":"https://a.test/600.jpg","trackCount":42},
              {"collectionName":"Apple exclusive","artistName":"Host B"},
              {"collectionName":"Show A again","feedUrl":"HTTPS://A.TEST/RSS"}
            ]}
            """;

        IReadOnlyList<PodcastSearchResult> results = PodcastSearch.Parse(json);

        Assert.HasCount(1, results);
        Assert.AreEqual(new PodcastSearchResult("Show A", "Host A", "https://a.test/rss", "https://a.test/100.jpg", 42), results[0]);
    }

    [TestMethod]
    public void Parse_Malformed_ReturnsEmpty()
    {
        Assert.IsEmpty(PodcastSearch.Parse("<html>"));
    }
}
