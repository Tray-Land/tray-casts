using System.Text.Json.Serialization;

namespace TrayCasts;

/// <summary>One playable episode, as read from a feed's <c>&lt;item&gt;</c>.</summary>
public sealed record Episode
{
    /// <summary>The item's <c>guid</c>, or its audio URL when the feed has none. Unique within a feed.</summary>
    public required string Id { get; init; }

    public required string Title { get; init; }

    public required string AudioUrl { get; init; }

    public DateTimeOffset? Published { get; init; }

    public double? DurationSeconds { get; init; }

    /// <summary>Episode-specific art; null means "use the show's".</summary>
    public string? ArtworkUrl { get; init; }
}

/// <summary>A followed show, its cached episodes, and what's needed for a conditional refresh.</summary>
public sealed class Subscription
{
    public required string FeedUrl { get; set; }

    public required string Title { get; set; }

    public string? Author { get; set; }

    public string? ArtworkUrl { get; set; }

    public DateTimeOffset AddedUtc { get; set; }

    public DateTimeOffset? LastCheckedUtc { get; set; }

    public string? ETag { get; set; }

    public string? LastModified { get; set; }

    /// <summary>Which episodes the Latest list offers from this show, and how the show's page sorts them.</summary>
    public EpisodeOrder Order { get; set; }

    /// <summary>How many of this show's episodes the Latest list shows at once; null for all.</summary>
    public int? EpisodeLimit { get; set; }

    /// <summary>Newest first, capped by <see cref="Shows.ShowPolicy.EpisodesToKeep"/> for <see cref="Order"/>.</summary>
    public List<Episode> Episodes { get; set; } = [];
}

/// <summary>How a show is listened to: what comes up next in the Latest list.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<EpisodeOrder>))]
public enum EpisodeOrder
{
    /// <summary>The newest episodes, played or not. For news and other shows heard as they come.</summary>
    NewestFirst,

    /// <summary>The oldest unplayed episodes, so a serial is heard from the start.</summary>
    OldestFirst,

    /// <summary>A new episode while it's unplayed; otherwise the oldest unplayed, working through the backlog.</summary>
    NewestThenCatchUp,
}

/// <summary>The saved list of shows. Stored as <c>library.json</c>.</summary>
public sealed class Library
{
    public List<Subscription> Subscriptions { get; set; } = [];

    public Subscription? Find(string feedUrl) =>
        Subscriptions.FirstOrDefault(s => string.Equals(s.FeedUrl, feedUrl, StringComparison.OrdinalIgnoreCase));
}

/// <summary>How far into an episode the listener got.</summary>
public sealed record EpisodeProgress
{
    public double PositionSeconds { get; init; }

    public double? DurationSeconds { get; init; }

    public bool IsPlayed { get; init; }

    public DateTimeOffset UpdatedUtc { get; init; }
}

/// <summary>Listening progress for every episode touched. Stored as <c>progress.json</c>, apart
/// from the library, so a feed refresh can never lose it.</summary>
public sealed class ProgressBook
{
    public Dictionary<string, EpisodeProgress> Entries { get; set; } = new(StringComparer.Ordinal);

    public static string Key(string feedUrl, string episodeId) => $"{feedUrl}\n{episodeId}";

    public EpisodeProgress? Get(string feedUrl, string episodeId) =>
        Entries.GetValueOrDefault(Key(feedUrl, episodeId));

    public void Set(string feedUrl, string episodeId, EpisodeProgress progress) =>
        Entries[Key(feedUrl, episodeId)] = progress;

    /// <summary>Forgets every episode of a show, e.g. after unsubscribing.</summary>
    public int RemoveFeed(string feedUrl)
    {
        string prefix = feedUrl + "\n";
        List<string> keys = [.. Entries.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal))];
        foreach (string key in keys)
        {
            Entries.Remove(key);
        }

        return keys.Count;
    }
}

/// <summary>The episode in the player, with enough of its show to display it without the library.</summary>
public sealed record NowPlaying
{
    public required string FeedUrl { get; init; }

    public required string ShowTitle { get; init; }

    public string? ShowArtworkUrl { get; init; }

    public required Episode Episode { get; init; }

    public string? ArtworkUrl => Episode.ArtworkUrl ?? ShowArtworkUrl;
}

/// <summary>A show found by search, not yet followed.</summary>
public sealed record PodcastSearchResult(string Title, string? Author, string FeedUrl, string? ArtworkUrl, int? EpisodeCount);

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Library))]
[JsonSerializable(typeof(ProgressBook))]
[JsonSerializable(typeof(NowPlaying))]
public sealed partial class CoreJsonContext : JsonSerializerContext;
