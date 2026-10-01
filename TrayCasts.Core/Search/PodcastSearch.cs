using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TrayCasts.Search;

/// <summary>
/// Finds shows through the iTunes Search API: free, keyless, and it returns each show's RSS URL,
/// which is all the app needs. Nothing is sent but the search term.
/// </summary>
public static class PodcastSearch
{
    public const int DefaultLimit = 25;

    public static Uri BuildUri(string term, int limit = DefaultLimit) => new(
        "https://itunes.apple.com/search?media=podcast&entity=podcast"
        + $"&limit={limit.ToString(CultureInfo.InvariantCulture)}&term={Uri.EscapeDataString(term.Trim())}");

    public static async Task<IReadOnlyList<PodcastSearchResult>> SearchAsync(HttpClient http, string term, CancellationToken cancellationToken)
    {
        await using Stream json = await http.GetStreamAsync(BuildUri(term), cancellationToken).ConfigureAwait(false);
        ITunesResponse? response = await JsonSerializer
            .DeserializeAsync(json, SearchJsonContext.Default.ITunesResponse, cancellationToken)
            .ConfigureAwait(false);
        return ToResults(response);
    }

    /// <summary>Parses a response body; results without a feed URL (Apple-exclusive shows) are dropped.</summary>
    public static IReadOnlyList<PodcastSearchResult> Parse(string json)
    {
        try
        {
            return ToResults(JsonSerializer.Deserialize(json, SearchJsonContext.Default.ITunesResponse));
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static List<PodcastSearchResult> ToResults(ITunesResponse? response) =>
    [
        .. (response?.Results ?? [])
            .Where(r => !string.IsNullOrWhiteSpace(r.FeedUrl) && !string.IsNullOrWhiteSpace(r.CollectionName))
            .DistinctBy(r => r.FeedUrl, StringComparer.OrdinalIgnoreCase)
            .Select(r => new PodcastSearchResult(
                r.CollectionName!.Trim(),
                string.IsNullOrWhiteSpace(r.ArtistName) ? null : r.ArtistName.Trim(),
                r.FeedUrl!.Trim(),
                r.ArtworkUrl100 ?? r.ArtworkUrl600,
                r.TrackCount)),
    ];
}

internal sealed class ITunesResponse
{
    [JsonPropertyName("results")]
    public List<ITunesResult>? Results { get; set; }
}

internal sealed class ITunesResult
{
    [JsonPropertyName("collectionName")]
    public string? CollectionName { get; set; }

    [JsonPropertyName("artistName")]
    public string? ArtistName { get; set; }

    [JsonPropertyName("feedUrl")]
    public string? FeedUrl { get; set; }

    [JsonPropertyName("artworkUrl100")]
    public string? ArtworkUrl100 { get; set; }

    [JsonPropertyName("artworkUrl600")]
    public string? ArtworkUrl600 { get; set; }

    [JsonPropertyName("trackCount")]
    public int? TrackCount { get; set; }
}

[JsonSerializable(typeof(ITunesResponse))]
internal sealed partial class SearchJsonContext : JsonSerializerContext;
