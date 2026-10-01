using TrayCasts.Shows;

namespace TrayCasts.Tests;

[TestClass]
public sealed class ShowPolicyTests
{
    [TestMethod]
    public void UpNext_NewestFirst_KeepsPlayedEpisodes()
    {
        Subscription show = Show(EpisodeOrder.NewestFirst, null, "e3", "e2", "e1");

        CollectionAssert.AreEqual(new[] { "e3", "e2", "e1" }, UpNext(show, "e3"));
    }

    [TestMethod]
    public void UpNext_NewestFirst_WithLimit_TakesNewest()
    {
        Subscription show = Show(EpisodeOrder.NewestFirst, 2, "e3", "e2", "e1");

        CollectionAssert.AreEqual(new[] { "e3", "e2" }, UpNext(show));
    }

    [TestMethod]
    public void UpNext_OldestFirst_SkipsPlayed()
    {
        Subscription show = Show(EpisodeOrder.OldestFirst, 2, "e4", "e3", "e2", "e1");

        CollectionAssert.AreEqual(new[] { "e2", "e3" }, UpNext(show, "e1"));
    }

    [TestMethod]
    public void UpNext_OldestFirst_AllPlayed_IsEmpty()
    {
        Subscription show = Show(EpisodeOrder.OldestFirst, null, "e2", "e1");

        Assert.IsEmpty(UpNext(show, "e1", "e2"));
    }

    [TestMethod]
    public void UpNext_CatchUp_NewestUnplayed_ComesFirstThenBacklog()
    {
        Subscription show = Show(EpisodeOrder.NewestThenCatchUp, 3, "e4", "e3", "e2", "e1");

        CollectionAssert.AreEqual(new[] { "e4", "e1", "e2" }, UpNext(show));
    }

    [TestMethod]
    public void UpNext_CatchUp_NewestPlayed_LoadsOldestUnplayed()
    {
        Subscription show = Show(EpisodeOrder.NewestThenCatchUp, 1, "e4", "e3", "e2", "e1");

        CollectionAssert.AreEqual(new[] { "e2" }, UpNext(show, "e4", "e1"));
    }

    [TestMethod]
    public void UpNext_UnofferedLimit_MeansAll()
    {
        Subscription show = Show(EpisodeOrder.NewestFirst, 7, "e3", "e2", "e1");

        Assert.HasCount(3, UpNext(show));
    }

    [TestMethod]
    public void InPageOrder_OldestFirst_Reverses()
    {
        Subscription show = Show(EpisodeOrder.OldestFirst, null, "e2", "e1");

        CollectionAssert.AreEqual(new[] { "e1", "e2" }, ShowPolicy.InPageOrder(show).Select(e => e.Id).ToArray());
    }

    [TestMethod]
    public void InPageOrder_CatchUp_StaysNewestFirst()
    {
        Subscription show = Show(EpisodeOrder.NewestThenCatchUp, null, "e2", "e1");

        CollectionAssert.AreEqual(new[] { "e2", "e1" }, ShowPolicy.InPageOrder(show).Select(e => e.Id).ToArray());
    }

    [TestMethod]
    public void EpisodesToKeep_BacklogOrders_KeepMore()
    {
        Assert.AreEqual(ShowPolicy.RecentEpisodesKept, ShowPolicy.EpisodesToKeep(EpisodeOrder.NewestFirst));
        Assert.IsGreaterThan(ShowPolicy.RecentEpisodesKept, ShowPolicy.EpisodesToKeep(EpisodeOrder.OldestFirst));
        Assert.IsGreaterThan(ShowPolicy.RecentEpisodesKept, ShowPolicy.EpisodesToKeep(EpisodeOrder.NewestThenCatchUp));
    }

    [TestMethod]
    public void SanitizeOrder_UnknownValue_IsNewestFirst()
    {
        Assert.AreEqual(EpisodeOrder.NewestFirst, ShowPolicy.SanitizeOrder((EpisodeOrder)42));
    }

    private static string[] UpNext(Subscription show, params string[] played) =>
        [.. ShowPolicy.UpNext(show, e => played.Contains(e.Id)).Select(e => e.Id)];

    /// <summary>A show whose episodes are given newest first, one day apart.</summary>
    private static Subscription Show(EpisodeOrder order, int? limit, params string[] newestFirst) => new()
    {
        FeedUrl = "https://a.test/rss",
        Title = "Show",
        Order = order,
        EpisodeLimit = limit,
        Episodes = [.. newestFirst.Select((id, i) => new Episode
        {
            Id = id,
            Title = id,
            AudioUrl = $"https://x.test/{id}.mp3",
            Published = new DateTimeOffset(2026, 9, 28 - i, 0, 0, 0, TimeSpan.Zero),
        })],
    };
}
