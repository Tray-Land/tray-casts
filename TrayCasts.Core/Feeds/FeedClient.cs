using System.Net;
using System.Net.Http.Headers;

namespace TrayCasts.Feeds;

/// <summary>The result of a fetch: <see cref="Feed"/> is null when the server said "not modified".</summary>
public sealed record FeedFetchResult(ParsedFeed? Feed, string? ETag, string? LastModified)
{
    public bool NotModified => Feed is null;
}

/// <summary>
/// Downloads and parses feeds. Sends the last ETag / Last-Modified so an unchanged feed costs one
/// small 304, and parses on the thread pool so a large feed never stalls the UI thread.
/// </summary>
public sealed class FeedClient(HttpClient http)
{
    /// <summary>Feeds with a decade of episodes and inline show notes reach a few MB; this is far past that.</summary>
    public const long MaxFeedBytes = 32L * 1024 * 1024;

    /// <summary>One shared client: short timeout, compression, and an identifying User-Agent
    /// (some hosts reject requests without one).</summary>
    public static HttpClient CreateHttpClient(string userAgent)
    {
        HttpClient client = new(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
        })
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        return client;
    }

    public async Task<FeedFetchResult> FetchAsync(string feedUrl, string? etag, string? lastModified, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, feedUrl);
        request.Headers.Accept.ParseAdd("application/rss+xml, application/xml;q=0.9, text/xml;q=0.8, */*;q=0.5");
        if (!string.IsNullOrEmpty(etag))
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", etag);
        }

        if (!string.IsNullOrEmpty(lastModified))
        {
            request.Headers.TryAddWithoutValidation("If-Modified-Since", lastModified);
        }

        using HttpResponseMessage response = await http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            return new FeedFetchResult(null, etag, lastModified);
        }

        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxFeedBytes)
        {
            throw new FeedFormatException("The feed is too large.");
        }

        using MemoryStream buffer = new();
        await using (Stream body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        {
            await CopyWithLimitAsync(body, buffer, MaxFeedBytes, cancellationToken).ConfigureAwait(false);
        }

        buffer.Position = 0;
        ParsedFeed feed = FeedParser.Parse(buffer, feedUrl);
        return new FeedFetchResult(feed, response.Headers.ETag?.ToString(), FormatLastModified(response.Content.Headers));
    }

    private static string? FormatLastModified(HttpContentHeaders headers) =>
        headers.LastModified?.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

    private static async Task CopyWithLimitAsync(Stream source, Stream destination, long limit, CancellationToken cancellationToken)
    {
        byte[] chunk = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > limit)
            {
                throw new FeedFormatException("The feed is too large.");
            }

            await destination.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }
}
