using System.Globalization;
using System.Text.RegularExpressions;

namespace TrayCasts.Feeds;

/// <summary>
/// Lenient readers for the values podcast feeds get wrong most often: RFC 822 dates (with named
/// zones, missing seconds, two-digit years, full month names) and <c>itunes:duration</c>
/// (seconds, mm:ss, or h:mm:ss).
/// </summary>
public static partial class FeedValues
{
    private static readonly string[] Months = ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"];

    private static readonly Dictionary<string, int> ZoneHours = new(StringComparer.OrdinalIgnoreCase)
    {
        ["GMT"] = 0, ["UT"] = 0, ["UTC"] = 0, ["Z"] = 0,
        ["EST"] = -5, ["EDT"] = -4, ["CST"] = -6, ["CDT"] = -5,
        ["MST"] = -7, ["MDT"] = -6, ["PST"] = -8, ["PDT"] = -7,
        ["BST"] = 1, ["CET"] = 1, ["CEST"] = 2, ["EET"] = 2, ["EEST"] = 3,
        ["IST"] = 5, ["JST"] = 9, ["AEST"] = 10, ["AEDT"] = 11,
    };

    /// <summary>Parses a feed date, or returns null. Unknown or missing zones are treated as UTC.</summary>
    public static DateTimeOffset? ParseDate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        Match m = Rfc822Regex().Match(text.Trim());
        if (m.Success && TryBuildRfc822(m, out DateTimeOffset parsed))
        {
            return parsed;
        }

        // ISO 8601 and other machine formats some generators emit.
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal, out DateTimeOffset fallback)
            ? fallback
            : null;
    }

    /// <summary>Parses <c>itunes:duration</c> into seconds, or null when absent, zero, or unreadable.</summary>
    public static double? ParseDuration(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string[] parts = text.Trim().Split(':');
        if (parts.Length > 3)
        {
            return null;
        }

        double total = 0;
        foreach (string part in parts)
        {
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || value < 0)
            {
                return null;
            }

            total = (total * 60) + value;
        }

        return total > 0 ? total : null;
    }

    private static bool TryBuildRfc822(Match m, out DateTimeOffset result)
    {
        result = default;
        int day = int.Parse(m.Groups["day"].Value, CultureInfo.InvariantCulture);
        string monthText = m.Groups["month"].Value;
        int month = Array.IndexOf(Months, monthText[..3].ToLowerInvariant()) + 1;
        int year = int.Parse(m.Groups["year"].Value, CultureInfo.InvariantCulture);
        if (year < 100)
        {
            year += year < 70 ? 2000 : 1900;
        }

        int hour = int.Parse(m.Groups["hour"].Value, CultureInfo.InvariantCulture);
        int minute = int.Parse(m.Groups["minute"].Value, CultureInfo.InvariantCulture);
        int second = m.Groups["second"].Success ? int.Parse(m.Groups["second"].Value, CultureInfo.InvariantCulture) : 0;

        if (month == 0 || !TryParseZone(m.Groups["zone"].Value, out TimeSpan offset))
        {
            return false;
        }

        try
        {
            result = new DateTimeOffset(year, month, day, hour, minute, Math.Min(second, 59), offset);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static bool TryParseZone(string zone, out TimeSpan offset)
    {
        offset = TimeSpan.Zero;
        zone = zone.Trim();
        if (zone.Length == 0)
        {
            return true;
        }

        if (zone[0] is '+' or '-')
        {
            string digits = zone[1..].Replace(":", "", StringComparison.Ordinal);
            if (digits.Length != 4 || !int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out int hhmm))
            {
                return false;
            }

            offset = new TimeSpan(hhmm / 100, hhmm % 100, 0);
            if (zone[0] == '-')
            {
                offset = -offset;
            }

            return offset.Duration() <= TimeSpan.FromHours(14);
        }

        if (ZoneHours.TryGetValue(zone, out int hours))
        {
            offset = TimeSpan.FromHours(hours);
        }

        // An unknown name: keep the date, assume UTC. Off by hours beats dropping the episode's date.
        return true;
    }

    // "Tue, 03 Sep 2024 10:00:00 +0000", "3 Sept. 2024 10:00 EST", "Tuesday, 3 September 24 10:00:00 GMT"
    [GeneratedRegex(@"^(?:[A-Za-z]+,?\s*)?(?<day>\d{1,2})\s+(?<month>[A-Za-z]{3,})\.?\s+(?<year>\d{2}|\d{4})\s+(?<hour>\d{1,2}):(?<minute>\d{2})(?::(?<second>\d{2}))?(?:\.\d+)?\s*(?<zone>[+-]\d{2}:?\d{2}|[A-Za-z]{1,5})?\s*$")]
    private static partial Regex Rfc822Regex();
}
