using TrayCasts.Feeds;

namespace TrayCasts.Tests;

[TestClass]
public sealed class FeedValuesTests
{
    [TestMethod]
    [DataRow("Tue, 03 Sep 2024 10:00:00 +0000", 2024, 9, 3, 10, 0, 0)]
    [DataRow("Tue, 3 Sep 2024 10:00:00 GMT", 2024, 9, 3, 10, 0, 0)]
    [DataRow("03 Sep 2024 10:00 EST", 2024, 9, 3, 10, 0, -5)]
    [DataRow("Tuesday, 3 September 2024 10:00:00 -0430", 2024, 9, 3, 10, 0, -4.5)]
    [DataRow("Tue, 03 Sept. 2024 10:00:00 +02:00", 2024, 9, 3, 10, 0, 2)]
    [DataRow("Tue, 03 Sep 24 10:00:00 PDT", 2024, 9, 3, 10, 0, -7)]
    [DataRow("Tue, 03 Sep 2024 10:00:00", 2024, 9, 3, 10, 0, 0)]
    [DataRow("Tue, 03 Sep 2024 10:00:00 XYZ", 2024, 9, 3, 10, 0, 0)]
    [DataRow("2024-09-03T10:00:00Z", 2024, 9, 3, 10, 0, 0)]
    public void ParseDate_CommonFeedFormats_Parse(string text, int year, int month, int day, int hour, int minute, double offsetHours)
    {
        DateTimeOffset? parsed = FeedValues.ParseDate(text);

        Assert.AreEqual(new DateTimeOffset(year, month, day, hour, minute, 0, TimeSpan.FromHours(offsetHours)), parsed);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("sometime last week")]
    [DataRow("Tue, 31 Feb 2024 10:00:00 GMT")]
    public void ParseDate_Unreadable_ReturnsNull(string? text)
    {
        Assert.IsNull(FeedValues.ParseDate(text));
    }

    [TestMethod]
    [DataRow("3723", 3723)]
    [DataRow("45:30", 2730)]
    [DataRow("1:02:03", 3723)]
    [DataRow("01:02:03.5", 3723.5)]
    [DataRow(" 90 ", 90)]
    public void ParseDuration_Formats_ReturnSeconds(string text, double expected)
    {
        Assert.AreEqual(expected, FeedValues.ParseDuration(text));
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("0")]
    [DataRow("00:00:00")]
    [DataRow("about an hour")]
    [DataRow("1:2:3:4")]
    [DataRow("-5")]
    public void ParseDuration_UnreadableOrZero_ReturnsNull(string? text)
    {
        Assert.IsNull(FeedValues.ParseDuration(text));
    }
}
