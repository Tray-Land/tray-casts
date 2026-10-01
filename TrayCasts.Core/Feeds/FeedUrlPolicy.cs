namespace TrayCasts.Feeds;

/// <summary>Decides whether text typed in the add box is a feed address or a search.</summary>
public static class FeedUrlPolicy
{
    /// <summary>
    /// Returns an absolute http(s) URL when <paramref name="input"/> looks like an address:
    /// "https://…", "feed://…" / "podcast://…" (rewritten to https), or a bare "host.tld/path".
    /// Anything with spaces, or without a dot in the host, is a search term.
    /// </summary>
    public static bool TryNormalize(string? input, out string feedUrl)
    {
        feedUrl = string.Empty;
        string text = input?.Trim() ?? string.Empty;
        if (text.Length == 0 || text.Any(char.IsWhiteSpace))
        {
            return false;
        }

        foreach (string scheme in (string[])["feed://", "podcast://", "pcast://", "itpc://"])
        {
            if (text.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
            {
                text = "https://" + text[scheme.Length..];
                break;
            }
        }

        // "feed:https://…" is another form some sites link with.
        if (text.StartsWith("feed:", StringComparison.OrdinalIgnoreCase))
        {
            text = text["feed:".Length..];
        }

        bool hasScheme = text.Contains("://", StringComparison.Ordinal);
        if (!hasScheme)
        {
            string host = text.Split('/', 2)[0];
            if (!host.Contains('.', StringComparison.Ordinal) || host.StartsWith('.') || host.EndsWith('.'))
            {
                return false;
            }

            text = "https://" + text;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || string.IsNullOrEmpty(uri.Host))
        {
            return false;
        }

        feedUrl = uri.AbsoluteUri;
        return true;
    }
}
