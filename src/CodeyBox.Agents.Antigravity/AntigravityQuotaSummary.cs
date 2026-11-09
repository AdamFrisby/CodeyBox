using System.Globalization;
using System.Text.Json;

namespace CodeyBox.Agents.Antigravity;

/// <summary>One quota window for a model group, as reported by the gateway's quota summary.</summary>
/// <param name="BucketId">Provider's stable machine id, e.g. <c>gemini-weekly</c>, <c>3p-5h</c>.
/// Preferred over the display name for matching, which is prose and localisable.</param>
/// <param name="Window">Normalised window name (<c>five_hour</c> / <c>seven_day</c>).</param>
/// <param name="RemainingFraction">0.0-1.0 remaining, as sent.</param>
/// <param name="DisplayName">Bucket display name, e.g. <c>Weekly Limit Remaining</c> or
/// <c>Gemini 4 Argon Weekly Limit</c>. Retained so per-model rows (agy 1.5.2+) that share the
/// pool bucket-id prefix — or use a new one — still match the configured model/family.</param>
/// <param name="GroupDisplayName">Owning group display name, e.g. <c>Gemini Models</c> or
/// <c>Gemini 4 Argon</c>. Per-model limits arrive as their own group.</param>
internal sealed record AntigravityQuotaBucket(
    string BucketId,
    string Window,
    double RemainingFraction,
    DateTimeOffset? ResetAt,
    string DisplayName = "",
    string GroupDisplayName = "")
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
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return [];

            var parsed = new List<AntigravityQuotaGroup>();
            if (doc.RootElement.TryGetProperty("groups", out var groups)
                && groups.ValueKind == JsonValueKind.Array)
            {
                foreach (var group in groups.EnumerateArray())
                {
                    if (group.ValueKind != JsonValueKind.Object)
                        continue;

                    var groupDisplay = String(group, "displayName") ?? "";
                    var buckets = new List<AntigravityQuotaBucket>();
                    if (group.TryGetProperty("buckets", out var bucketArray)
                        && bucketArray.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var bucket in bucketArray.EnumerateArray())
                        {
                            if (TryParseBucket(bucket, groupDisplay) is { } parsedBucket)
                                buckets.Add(parsedBucket);
                        }
                    }

                    if (buckets.Count > 0)
                        parsed.Add(new AntigravityQuotaGroup(groupDisplay, buckets));
                }
            }

            // agy 1.5.2+ surfaces per-model limits alongside the pool rows. When a future
            // payload moves those rows outside "groups" (e.g. a top-level "models",
            // "limits", or "buckets" array), still surface any bucket-shaped object so the
            // router sees the most restrictive row instead of only the pool.
            if (parsed.Count > 0)
                return parsed;

            var loose = new List<AntigravityQuotaBucket>();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var item in prop.Value.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                        continue;
                    if (TryParseBucket(item, "") is { } bucket
                        && item.TryGetProperty("remainingFraction", out _))
                    {
                        loose.Add(bucket);
                    }
                    else if (item.ValueKind == JsonValueKind.Object
                        && item.TryGetProperty("buckets", out var nested)
                        && nested.ValueKind == JsonValueKind.Array)
                    {
                        var nestedDisplay = String(item, "displayName") ?? "";
                        foreach (var n in nested.EnumerateArray())
                        {
                            if (TryParseBucket(n, nestedDisplay) is { } nb)
                                loose.Add(nb);
                        }
                    }
                }
            }

            if (loose.Count > 0)
                parsed.Add(new AntigravityQuotaGroup("", loose));

            return parsed;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static AntigravityQuotaBucket? TryParseBucket(JsonElement bucket, string groupDisplayName)
    {
        if (bucket.ValueKind != JsonValueKind.Object)
            return null;

        var bucketId = String(bucket, "bucketId");
        if (string.IsNullOrEmpty(bucketId))
        {
            // Per-model rows may omit a machine id and carry only a display name such as
            // "Gemini 4 Argon Weekly Limit". Synthesise a stable id from it so the row is
            // still matchable and surfaced instead of silently dropped.
            var fallbackDisplay = String(bucket, "displayName");
            if (string.IsNullOrWhiteSpace(fallbackDisplay))
                return null;
            bucketId = "display:" + fallbackDisplay;
        }

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
            resetAt,
            String(bucket, "displayName") ?? "",
            groupDisplayName);
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
    /// <para>agy 1.5.2+ additionally reports per-model (or per-family) limit rows such as a
    /// <c>Gemini 4 Argon</c> group with a <c>Gemini 4 Argon Weekly Limit</c> bucket. When the
    /// configured model (or its family, e.g. <c>gemini-4-argon-high</c> → <c>gemini 4 argon</c>)
    /// has its own row, that row plus the family pool rows are returned so the caller — which
    /// aggregates to the scarcest window — uses the most restrictive applicable row for both
    /// availability and reset time.</para>
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

        var pool = all
            .Where(b => b.BucketId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (pool.Length == 0)
            pool = all;

        if (string.IsNullOrWhiteSpace(modelId))
            return pool;

        var specific = all.Where(b => MatchesModelOrFamily(b, modelId)).ToArray();
        if (specific.Length == 0)
            return pool;

        // Union of pool + specific so the scarcest-window aggregation sees the most
        // restrictive applicable row. Deduplicate by bucket id.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var union = new List<AntigravityQuotaBucket>(pool.Length + specific.Length);
        foreach (var b in pool.Concat(specific))
        {
            if (seen.Add(b.BucketId))
                union.Add(b);
        }
        return union;
    }

    /// <summary>
    /// True when a bucket's id, display name, or group display name names
    /// <paramref name="modelId"/> or its family. Matching is on normalised tokens
    /// (lowercase, non-alphanumeric → space) so <c>gemini-4-argon-high</c> matches a
    /// <c>Gemini 4 Argon</c> group and a <c>Gemini 4 Argon Weekly Limit</c> bucket,
    /// while a bare pool row (<c>Gemini Models</c> / <c>Weekly Limit Remaining</c>)
    /// does not claim a per-model identity.
    /// </summary>
    internal static bool MatchesModelOrFamily(AntigravityQuotaBucket bucket, string modelId)
    {
        var modelNorm = NormaliseName(modelId);
        if (string.IsNullOrEmpty(modelNorm))
            return false;
        var familyNorm = NormaliseName(StripThinkingLevelSuffix(modelId));
        var candidates = new[] { bucket.BucketId, bucket.DisplayName, bucket.GroupDisplayName };
        foreach (var candidate in candidates)
        {
            var norm = NormaliseName(candidate);
            if (string.IsNullOrEmpty(norm))
                continue;
            // Skip generic pool labels: they contain the family word (e.g. "gemini") but
            // must not count as a per-model row on their own. Require the family-or-model
            // match to be more specific than the bare "gemini models" / "3p" pool label,
            // which the pool-prefix path already covers.
            if (IsGenericPoolLabel(norm))
                continue;
            if (norm.Contains(modelNorm, StringComparison.Ordinal)
                || norm.Contains(familyNorm, StringComparison.Ordinal)
                || (!string.IsNullOrEmpty(familyNorm) && familyNorm.Contains(norm, StringComparison.Ordinal) && norm.Length >= 8))
                return true;
        }
        return false;
    }

    private static bool IsGenericPoolLabel(string normalised)
        => string.Equals(normalised, "gemini models", StringComparison.Ordinal)
            || string.Equals(normalised, "claude and gpt models", StringComparison.Ordinal)
            || string.Equals(normalised, "weekly limit remaining", StringComparison.Ordinal)
            || string.Equals(normalised, "five hour limit remaining", StringComparison.Ordinal);

    internal static string NormaliseName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";
        var chars = new char[value.Length];
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            chars[i] = char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ';
        }
        return string.Join(" ", new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Strips a trailing thinking-level suffix (<c>-high</c>/<c>-medium</c>/<c>-low</c>)
    /// so <c>gemini-4-argon-high</c> yields family <c>gemini-4-argon</c>.</summary>
    internal static string StripThinkingLevelSuffix(string modelId)
    {
        foreach (var suffix in new[] { "-high", "-medium", "-low" })
        {
            if (modelId.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return modelId[..^suffix.Length];
        }
        return modelId;
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
