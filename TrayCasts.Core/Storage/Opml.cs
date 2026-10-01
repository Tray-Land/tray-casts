using System.Xml;
using System.Xml.Linq;
using TrayCasts.Feeds;

namespace TrayCasts.Storage;

/// <summary>A show listed in an OPML file.</summary>
public sealed record OpmlFeed(string FeedUrl, string? Title);

/// <summary>
/// Reads and writes OPML, the subscription list format every podcast app imports and exports.
/// Only <c>outline</c> elements with an <c>xmlUrl</c> matter; folders (nested outlines) are flattened.
/// </summary>
public static class Opml
{
    private static readonly XmlReaderSettings ReaderSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
    };

    /// <summary>The feeds in an OPML document, de-duplicated, in file order. Throws <see cref="XmlException"/>
    /// when the file isn't XML or isn't OPML.</summary>
    public static List<OpmlFeed> Read(Stream stream)
    {
        XDocument document;
        using (XmlReader reader = XmlReader.Create(stream, ReaderSettings))
        {
            document = XDocument.Load(reader);
        }

        if (document.Root is not { } root || !root.Name.LocalName.Equals("opml", StringComparison.OrdinalIgnoreCase))
        {
            throw new XmlException("This isn't an OPML file.");
        }

        List<OpmlFeed> feeds = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (XElement outline in root.Descendants().Where(e => e.Name.LocalName == "outline"))
        {
            // Some exporters write xmlURL or url; accept them all.
            string? raw = outline.Attributes()
                .FirstOrDefault(a => a.Name.LocalName.Equals("xmlUrl", StringComparison.OrdinalIgnoreCase)
                    || a.Name.LocalName.Equals("url", StringComparison.OrdinalIgnoreCase))?.Value;
            if (!FeedUrlPolicy.TryNormalize(raw, out string feedUrl) || !seen.Add(feedUrl))
            {
                continue;
            }

            string? title = (string?)outline.Attribute("title") ?? (string?)outline.Attribute("text");
            feeds.Add(new OpmlFeed(feedUrl, string.IsNullOrWhiteSpace(title) ? null : title.Trim()));
        }

        return feeds;
    }

    /// <summary>Writes the shows as an OPML 2.0 document.</summary>
    public static void Write(Stream stream, IEnumerable<Subscription> shows, DateTimeOffset now)
    {
        XDocument document = new(
            new XDeclaration("1.0", "utf-8", null),
            new XElement("opml",
                new XAttribute("version", "2.0"),
                new XElement("head",
                    new XElement("title", "Tray Casts subscriptions"),
                    new XElement("dateCreated", now.ToString("r", System.Globalization.CultureInfo.InvariantCulture))),
                new XElement("body",
                    shows.Select(s => new XElement("outline",
                        new XAttribute("type", "rss"),
                        new XAttribute("text", s.Title),
                        new XAttribute("title", s.Title),
                        new XAttribute("xmlUrl", s.FeedUrl))))));

        using XmlWriter writer = XmlWriter.Create(stream, new XmlWriterSettings { Indent = true, CloseOutput = false, Encoding = new System.Text.UTF8Encoding(false) });
        document.Save(writer);
    }
}
