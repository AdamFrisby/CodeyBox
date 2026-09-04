using System.Globalization;
using System.Text.Json;

namespace CodeyBox.Agents.Antigravity;

/// <summary>One quota window for a model group, as reported by the gateway's quota summary.</summary>
/// <param name="BucketId">Provider's stable machine id, e.g. <c>gemini-weekly</c>, <c>3p-5h</c>.
/// Preferred over the display name for matching, which is prose and localisable.</param>
/// <param name="Window">Normalised window name (<c>five_hour</c> / <c>seven_day</c>).</param>
/// <param name="RemainingFraction">0.0-1.0 remaining, as sent.</param>
internal sealed record AntigravityQuotaBucket(
    string BucketId,
    string Window,
    double RemainingFraction,
    DateTimeOffset? ResetAt)
{
    /// <summary>The fraction expressed as the 0-100 percentage the quota model uses.</summary>
    public double AvailablePct => Math.Clamp(RemainingFraction * 100.0, 0.0, 100.0);
}

/// <summary>
/// A family of models sharing one pair of limits, e.g. "Gemini Models" or "Claude and GPT models".
/// The gateway meters per family, not per model id.
/// </summary>
internal sealed record AntigravityQuotaGroup(
    string DisplayName,
    IReadOnlyList<AntigravityQuotaBucket> Buckets);

/// <summary>
/// Parses the gateway's <c>:retrieveUserQuotaSummary</c> payload into per-window readings.
///
/// <para>Shape (verified against agy 1.1.26):</para>
/// <code>
/// {"groups":[{"displayName":"Gemini Models","buckets":[
///   {"bucketId":"gemini-weekly","window":"weekly","remainingFraction":0.998,"resetTime":"…Z"},
///   {"bucketId":"gemini-5h","window":"5h","remainingFraction":0.989,"resetTime":"…Z"}]}]}
/// </code>
/// </summary>
internal static class AntigravityQuotaSummaryParser
{
    /// <summary>Gateway window name → the name CodeyBox's per-window floors key on.</summary>
    internal const string FiveHourWindow = "five_hour";
    internal const string SevenDayWindow = "seven_day";

    /// <summary>Bucket-id prefix for Google's own models.</summary>
    private const string GeminiBucketPrefix = "gemini-";

    /// <summary>Bucket-id prefix for the third-party models fronted by the same subscription.</summary>
    private const string ThirdPartyBucketPrefix = "3p-";

    public static IReadOnlyList<AntigravityQuotaGroup> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("groups", out var groups)
                || groups.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var parsed = new List<AntigravityQuotaGroup>();
            foreach (var group in groups.EnumerateArray())
            {
                if (group.ValueKind != JsonValueKind.Object)
                    continue;

                var buckets = new List<AntigravityQuotaBucket>();
                if (group.TryGetProperty("buckets", out var bucketArray)
                    && bucketArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (var bucket in bucketArray.EnumerateArray())
                    {
                        if (TryParseBucket(bucket) is { } parsedBucket)
                            buckets.Add(parsedBucket);
                    }
                }

                if (buckets.Count > 0)
                    parsed.Add(new AntigravityQuotaGroup(String(group, "displayName") ?? "", buckets));
            }

            return parsed;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static AntigravityQuotaBucket? TryParseBucket(JsonElement bucket)
    {
        if (bucket.ValueKind != JsonValueKind.Object)
            return null;

        var bucketId = String(bucket, "bucketId");
        if (string.IsNullOrEmpty(bucketId))
            return null;

        // remainingFraction arrives as either a float (0.998) or an integer (1) — GetDouble covers both.
        if (!bucket.TryGetProperty("remainingFraction", out var fractionEl)
            || fractionEl.ValueKind != JsonValueKind.Number
            || !fractionEl.TryGetDouble(out var fraction)
            || double.IsNaN(fraction))
        {
            return null;
        }

        DateTimeOffset? resetAt = null;
        if (String(bucket, "resetTime") is { Length: > 0 } reset
            && DateTimeOffset.TryParse(
                reset, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var parsedReset))
        {
            resetAt = parsedReset;
        }

        return new AntigravityQuotaBucket(
            bucketId,
            NormaliseWindow(String(bucket, "window"), bucketId),
            fraction,
            resetAt);
    }

    /// <summary>
    /// Maps the gateway's window token onto CodeyBox's canonical names so
    /// <c>QuotaRouter.MinQuotaPctByWindow</c> floors (keyed <c>five_hour</c>/<c>seven_day</c>) apply.
    /// Falls back to the bucket-id suffix, then to the raw token, so an unrecognised window is still
    /// surfaced rather than silently dropped.
    /// </summary>
    private static string NormaliseWindow(string? window, string bucketId)
    {
        var token = window;
        if (string.IsNullOrWhiteSpace(token))
        {
            var dash = bucketId.LastIndexOf('-');
            token = dash >= 0 && dash < bucketId.Length - 1 ? bucketId[(dash + 1)..] : bucketId;
        }

        return token.ToLowerInvariant() switch
        {
            "5h" or "5h-rolling" or "five_hour" or "fivehour" => FiveHourWindow,
            "weekly" or "7d" or "seven_day" or "sevenday" => SevenDayWindow,
            _ => token,
        };
    }

    /// <summary>
    /// The buckets that meter <paramref name="modelId"/>. The gateway groups Google's own models
    /// separately from the third-party (Claude / GPT-OSS) models fronted by the same subscription, so a
    /// gemini member must not be gated on the Claude group's consumption or vice versa.
    ///
    /// <para>An unrecognised model id falls back to EVERY bucket, which aggregates to the most
    /// constrained window across all groups. That is deliberately conservative: guessing the wrong group
    /// would report a fresh window for an exhausted one and dispatch into a 429.</para>
    /// </summary>
    public static IReadOnlyList<AntigravityQuotaBucket> BucketsForModel(
        IReadOnlyList<AntigravityQuotaGroup> groups, string? modelId)
    {
        var all = groups.SelectMany(g => g.Buckets).ToArray();
        if (all.Length == 0)
            return [];

        var prefix = BucketPrefixForModel(modelId);
        if (prefix is null)
            return all;

        var matched = all
            .Where(b => b.BucketId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        return matched.Length > 0 ? matched : all;
    }

    /// <summary>Which bucket family meters a model id, or null when it is not recognised.</summary>
    private static string? BucketPrefixForModel(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            return null;

        if (modelId.StartsWith("gemini", StringComparison.OrdinalIgnoreCase))
            return GeminiBucketPrefix;

        // Anthropic and OpenAI models ride the same subscription through the gateway's "3p" buckets.
        if (modelId.StartsWith("claude", StringComparison.OrdinalIgnoreCase)
            || modelId.StartsWith("gpt", StringComparison.OrdinalIgnoreCase))
        {
            return ThirdPartyBucketPrefix;
        }

        return null;
    }

    private static string? String(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
