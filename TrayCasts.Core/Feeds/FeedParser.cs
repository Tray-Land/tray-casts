using System.Net;
using System.Xml;
using System.Xml.Linq;

namespace TrayCasts.Feeds;

/// <summary>The show-level fields of a feed plus its playable episodes, newest first.</summary>
public sealed record ParsedFeed(string Title, string? Author, string? ArtworkUrl, IReadOnlyList<Episode> Episodes);

/// <summary>Thrown when a document isn't an RSS podcast feed at all.</summary>
public sealed class FeedFormatException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Reads an RSS 2.0 podcast feed with the iTunes and Media RSS extensions.</summary>
public static class FeedParser
{
    /// <summary>Episodes kept per show. Older ones are rarely played from a tray flyout, and the
    /// whole library is loaded each time the flyout opens.</summary>
    public const int MaxEpisodes = 100;

    private static readonly XNamespace ITunes = "http://www.itunes.com/dtds/podcast-1.0.dtd";
    private static readonly XNamespace Media = "http://search.yahoo.com/mrss/";

    public static ParsedFeed Parse(string xml, string feedUrl)
    {
        using StringReader reader = new(xml);
        return Parse(XmlReader.Create(reader, ReaderSettings), feedUrl);
    }

    public static ParsedFeed Parse(Stream stream, string feedUrl) =>
        Parse(XmlReader.Create(stream, ReaderSettings), feedUrl);

    // Feeds are untrusted input: no DTDs (entity expansion), no external resolution.
    private static XmlReaderSettings ReaderSettings => new()
    {
        DtdProcessing = DtdProcessing.Ignore,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
    };

    private static ParsedFeed Parse(XmlReader reader, string feedUrl)
    {
        XDocument doc;
        try
        {
            using (reader)
            {
                doc = XDocument.Load(reader);
            }
        }
        catch (XmlException ex)
        {
            throw new FeedFormatException("This isn't a valid podcast feed.", ex);
        }

        XElement channel = doc.Root?.Name.LocalName == "rss" ? doc.Root.Element("channel")
            ?? throw new FeedFormatException("The feed has no channel.")
            : throw new FeedFormatException("This isn't an RSS podcast feed.");

        Uri.TryCreate(feedUrl, UriKind.Absolute, out Uri? baseUri);

        string title = CleanText(channel.Element("title")?.Value) ?? feedUrl;
        string? author = CleanText(channel.Element(ITunes + "author")?.Value)
            ?? CleanText(channel.Element("managingEditor")?.Value);
        string? artwork = ResolveUrl(baseUri, channel.Element(ITunes + "image")?.Attribute("href")?.Value)
            ?? ResolveUrl(baseUri, channel.Element("image")?.Element("url")?.Value);

        List<Episode> episodes = [];
        foreach (XElement item in channel.Elements("item"))
        {
            if (ParseItem(item, baseUri) is { } episode)
            {
                episodes.Add(episode);
            }
        }

        // Feeds are usually newest-first already, but not always. Undated items sink to the end.
        List<Episode> newestFirst = [.. episodes
            .Select((e, index) => (e, index))
            .OrderByDescending(x => x.e.Published ?? DateTimeOffset.MinValue)
            .ThenBy(x => x.index)
            .Select(x => x.e)
            .DistinctBy(e => e.Id)
            .Take(MaxEpisodes)];

        return new ParsedFeed(title, author, artwork, newestFirst);
    }

    private static Episode? ParseItem(XElement item, Uri? baseUri)
    {
        string? audioUrl = FindAudioUrl(item, baseUri);
        if (audioUrl is null)
        {
            return null; // A text post or a video-only item: nothing to play.
        }

        string id = CleanText(item.Element("guid")?.Value) ?? audioUrl;
        return new Episode
        {
            Id = id,
            Title = CleanText(item.Element("title")?.Value) ?? CleanText(item.Element(ITunes + "title")?.Value) ?? "Untitled episode",
            AudioUrl = audioUrl,
            Published = FeedValues.ParseDate(item.Element("pubDate")?.Value),
            DurationSeconds = FeedValues.ParseDuration(item.Element(ITunes + "duration")?.Value),
            ArtworkUrl = ResolveUrl(baseUri, item.Element(ITunes + "image")?.Attribute("href")?.Value),
        };
    }

    private static string? FindAudioUrl(XElement item, Uri? baseUri)
    {
        foreach (XElement enclosure in item.Elements("enclosure").Concat(item.Elements(Media + "content")))
        {
            string? type = enclosure.Attribute("type")?.Value;
            string? medium = enclosure.Attribute("medium")?.Value;
            bool isAudio = type?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true
                || string.Equals(medium, "audio", StringComparison.OrdinalIgnoreCase)
                || (type is null && medium is null);
            if (isAudio && ResolveUrl(baseUri, enclosure.Attribute("url")?.Value) is { } url)
            {
                return url;
            }
        }

        // Some feeds mislabel audio as video/mp4 or application/octet-stream; take the enclosure anyway.
        return ResolveUrl(baseUri, item.Element("enclosure")?.Attribute("url")?.Value);
    }

    /// <summary>An absolute http(s) URL, resolving relative ones against the feed; otherwise null.</summary>
    private static string? ResolveUrl(Uri? baseUri, string? value)
    {
        value = value?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        Uri? uri = Uri.TryCreate(value, UriKind.Absolute, out Uri? absolute) ? absolute
            : baseUri is not null && Uri.TryCreate(baseUri, value, out Uri? relative) ? relative
            : null;
        return uri is not null && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? uri.AbsoluteUri
            : null;
    }

    /// <summary>Trims, collapses whitespace, and decodes HTML entities that feeds double-encode.</summary>
    private static string? CleanText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string decoded = WebUtility.HtmlDecode(value);
        string collapsed = string.Join(' ', decoded.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length == 0 ? null : collapsed;
    }
}
