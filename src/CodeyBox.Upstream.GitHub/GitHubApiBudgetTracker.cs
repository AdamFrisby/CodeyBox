using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using CodeyBox.Core;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Upstream.GitHub;

/// <summary>How a GitHub API call is prioritised against the shared request budget.</summary>
public enum GitHubRequestClass
{
    /// <summary>
    /// Pipeline-critical work (PR create/merge, push-related checks, operator
    /// actions). Always attempted unless the budget is provably exhausted —
    /// the reserve exists precisely so these calls keep budget.
    /// </summary>
    Pipeline,

    /// <summary>
    /// Latency-tolerant background reads (stale-PR sweeps, redrive detection,
    /// dossier/status enrichment). Deferred while remaining budget is at or
    /// below <see cref="GitHubApiBudgetOptions.BackgroundReserveRemaining"/>.
    /// </summary>
    Background,
}

/// <summary>
/// Point-in-time view of one credential's GitHub REST budget, as last
/// reported by response headers. Exposed through the admin status endpoint;
/// <see cref="BudgetKey"/> is a truncated SHA-256 of the token — never the
/// token itself.
/// </summary>
public sealed record GitHubApiBudgetSnapshot(
    string BudgetKey,
    long RequestsObserved,
    int? Remaining,
    DateTimeOffset? ResetAt,
    DateTimeOffset? BlockedUntil,
    string? BlockedReason,
    IReadOnlyList<KeyValuePair<string, int>> TopConsumers);

/// <summary>
/// Shared GitHub REST request-budget tracker. Every response on the
/// <c>github-upstream</c> HTTP client flows through
/// <see cref="GitHubRateLimitHandler"/>, which feeds
/// <see cref="ObserveResponse"/>; <see cref="GitHubUpstreamRemote"/> consults
/// <see cref="CheckForSend"/> before transmitting so background sweeps back
/// off before the primary limit runs out and no caller retries into a 403
/// inside a known rate-limit window.
///
/// <para>Budgets are keyed by a truncated hash of the bearer token, matching
/// GitHub's per-token accounting. Buckets are bounded and evicted oldest-first;
/// state is per-process and rebuilt from the next response after a restart —
/// deliberately optimistic: an unknown budget never blocks a call, so sends
/// may 403 until the next response supplies headers.</para>
/// </summary>
public sealed class GitHubApiBudgetTracker
{
    private const int MaxBuckets = 128;
    private const int MaxTopPaths = 16;
    private const int MaxPathLength = 120;
    private const int MaxCredentialKeys = 1024;

    private readonly Func<GitHubApiBudgetOptions> _optionsAccessor;
    private readonly TimeProvider _time;
    private readonly ILogger<GitHubApiBudgetTracker> _log;
    private readonly object _gate = new();
    private readonly Dictionary<string, Bucket> _buckets = new(StringComparer.Ordinal);

    // Credential fingerprint → current budget key. Remote instances are
    // created per call, so the background-read gate cannot re-derive the
    // token hash cheaply; sends register the mapping here after resolving
    // the token, and the gate reuses it without any network I/O.
    private readonly ConcurrentDictionary<string, string> _credentialBudgetKeys =
        new(StringComparer.Ordinal);

    public GitHubApiBudgetTracker(
        Func<GitHubApiBudgetOptions>? optionsAccessor = null,
        TimeProvider? time = null,
        ILogger<GitHubApiBudgetTracker>? log = null)
    {
        _optionsAccessor = optionsAccessor ?? (() => new GitHubApiBudgetOptions());
        _time = time ?? TimeProvider.System;
        _log = log ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<GitHubApiBudgetTracker>.Instance;
    }

    /// <summary>
    /// Stable, non-reversible bucket key for a credential. Tokens never leave
    /// the process; only this hash is stored, logged, or exposed.
    /// </summary>
    public static string BudgetKeyForToken(string? token)
    {
        if (string.IsNullOrEmpty(token))
            return "unknown";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return "sha256:" + Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    /// <summary>
    /// Records which budget bucket a credential currently maps to. Called by
    /// senders after resolving their token; the fingerprint comes from
    /// <see cref="IGitHubTokenProvider.CredentialFingerprint"/> and is never
    /// the token itself.
    /// </summary>
    public void RegisterCredentialToken(string credentialFingerprint, string token)
    {
        if (string.IsNullOrEmpty(credentialFingerprint) || string.IsNullOrEmpty(token))
            return;
        if (_credentialBudgetKeys.Count >= MaxCredentialKeys
            && !_credentialBudgetKeys.ContainsKey(credentialFingerprint))
        {
            // Bound the map: drop an arbitrary stale entry rather than grow forever.
            _credentialBudgetKeys.TryRemove(_credentialBudgetKeys.Keys.First(), out _);
        }
        _credentialBudgetKeys[credentialFingerprint] = BudgetKeyForToken(token);
    }

    /// <summary>
    /// Budget key last registered for <paramref name="credentialFingerprint"/>,
    /// or <c>null</c> when no send has resolved that credential yet. Lookup
    /// only — never triggers credential resolution.
    /// </summary>
    public string? BudgetKeyForCredential(string credentialFingerprint) =>
        string.IsNullOrEmpty(credentialFingerprint)
            ? null
            : _credentialBudgetKeys.TryGetValue(credentialFingerprint, out var key) ? key : null;

    /// <summary>
    /// Records the rate-limit evidence carried by one response. Safe to call
    /// for every status code; only <c>x-ratelimit-*</c>, <c>Retry-After</c>,
    /// and 403/429 statuses influence blocking state. Never throws — tracking
    /// must not break the call it observed.
    /// </summary>
    public void ObserveResponse(string budgetKey, HttpResponseMessage response, string? path = null)
    {
        try
        {
            ObserveResponseCore(budgetKey, response, path);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "GitHub API budget tracking failed for a response; ignoring");
        }
    }

    private void ObserveResponseCore(string budgetKey, HttpResponseMessage response, string? path)
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            var bucket = GetOrCreateBucket(budgetKey, now);
            bucket.LastSeenUtc = now;
            bucket.RequestsObserved++;
            TrackPath(bucket, path);

            if (TryGetHeader(response, "x-ratelimit-remaining", out var remainingRaw)
                && int.TryParse(remainingRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var remaining))
            {
                bucket.Remaining = remaining;
            }
            if (TryGetHeader(response, "x-ratelimit-reset", out var resetRaw)
                && long.TryParse(resetRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var resetEpoch)
                && resetEpoch > 0)
            {
                bucket.ResetAt = DateTimeOffset.FromUnixTimeSeconds(resetEpoch);
            }

            // A reported exhaustion blocks every subsequent send until the
            // reset, on any status — the next call would 403 anyway.
            DateTimeOffset? exhaustedUntil =
                bucket.Remaining == 0 && bucket.ResetAt is { } reset && reset > now
                    ? reset
                    : null;

            // 403/429 with an explicit cooldown (Retry-After) is a secondary
            // or primary rejection: honor the server's wait. A bare 403
            // (permissions, SSO) carries no rate-limit signal and must not
            // block the budget.
            DateTimeOffset? rejectedUntil = null;
            string? rejectedReason = null;
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            {
                if (response.Headers.RetryAfter?.Delta is { } delta && delta > TimeSpan.Zero)
                {
                    rejectedUntil = now + delta;
                    rejectedReason = "retry-after";
                }
                else if (response.Headers.RetryAfter?.Date is { } date && date > now)
                {
                    rejectedUntil = date;
                    rejectedReason = "retry-after-date";
                }
                else if (exhaustedUntil is not null)
                {
                    rejectedUntil = exhaustedUntil;
                    rejectedReason = "primary-limit-exhausted";
                }
            }

            var candidate = Latest(exhaustedUntil, rejectedUntil);
            if (candidate is { } until && until > now)
            {
                if (bucket.BlockedUntil is null || until > bucket.BlockedUntil)
                {
                    bucket.BlockedUntil = until;
                    bucket.BlockedReason = rejectedReason ?? "primary-limit-exhausted";
                }
            }

            WarnIfDegraded(bucket, now);
        }
    }

    /// <summary>
    /// Returns the deferral a send with <paramref name="requestClass"/> must
    /// observe, or <c>null</c> when the call may proceed. Hard blocks
    /// (exhausted primary window, Retry-After cooldown) apply to every class;
    /// the reserve applies only to <see cref="GitHubRequestClass.Background"/>.
    /// </summary>
    public UpstreamApiDeferredException? CheckForSend(string budgetKey, GitHubRequestClass requestClass)
    {
        lock (_gate)
        {
            if (!_buckets.TryGetValue(budgetKey, out var bucket))
                return null;
            var now = _time.GetUtcNow();
            var blockedUntil = EffectiveBlockedUntil(bucket, now);
            if (blockedUntil is { } until)
            {
                return new UpstreamApiDeferredException(
                    $"GitHub API budget for credential {budgetKey} is blocked until {until:o} " +
                    $"({bucket.BlockedReason ?? "rate limit"}); refusing to send into a known rate-limit window",
                    until);
            }

            if (requestClass == GitHubRequestClass.Background)
            {
                var reserve = Math.Max(0, _optionsAccessor().BackgroundReserveRemaining);
                // Remaining/ResetAt describe the window they were observed in.
                // Once ResetAt has passed the stored Remaining is stale — the
                // real budget is unknown until the next response — so it must
                // not keep deferring reads (matching EffectiveBlockedUntil,
                // which only honors a block while its reset is still ahead).
                var windowLive = bucket.ResetAt is not { } observedReset || observedReset > now;
                if (reserve > 0 && windowLive
                    && bucket.Remaining is { } remaining && remaining <= reserve)
                {
                    var reset = bucket.ResetAt is { } r && r > now ? r : (DateTimeOffset?)null;
                    return new UpstreamApiDeferredException(
                        $"GitHub API budget for credential {budgetKey} is low: {remaining} requests remaining " +
                        $"(background reserve {reserve}), resets {(reset?.ToString("o") ?? "unknown")}; " +
                        "deferring latency-tolerant read",
                        reset);
                }
            }
            return null;
        }
    }

    /// <summary>
    /// Background-read gate for <see cref="IUpstreamBackgroundReadGate"/>:
    /// reports the same deferral <see cref="CheckForSend"/> would raise for a
    /// background call, as data rather than an exception.
    /// </summary>
    public UpstreamReadDeferral? CheckBackgroundRead(string budgetKey)
    {
        var deferred = CheckForSend(budgetKey, GitHubRequestClass.Background);
        return deferred is null
            ? null
            : new UpstreamReadDeferral(deferred.DeferredUntil, deferred.Message);
    }

    /// <summary>Current per-credential budget view for the admin status endpoint.</summary>
    public IReadOnlyList<GitHubApiBudgetSnapshot> Snapshot()
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            return _buckets.Values
                .OrderByDescending(b => b.LastSeenUtc)
                .Select(b => new GitHubApiBudgetSnapshot(
                    b.Key,
                    b.RequestsObserved,
                    b.Remaining,
                    b.ResetAt,
                    EffectiveBlockedUntil(b, now),
                    b.BlockedReason,
                    b.TopPaths
                        .OrderByDescending(kv => kv.Value)
                        .Take(5)
                        .ToList()))
                .ToList();
        }
    }

    private Bucket GetOrCreateBucket(string key, DateTimeOffset now)
    {
        if (_buckets.TryGetValue(key, out var existing))
            return existing;
        if (_buckets.Count >= MaxBuckets)
        {
            var oldest = _buckets.Values.MinBy(b => b.LastSeenUtc);
            if (oldest is not null)
                _buckets.Remove(oldest.Key);
        }
        var bucket = new Bucket { Key = key, LastSeenUtc = now };
        _buckets[key] = bucket;
        return bucket;
    }

    private void WarnIfDegraded(Bucket bucket, DateTimeOffset now)
    {
        var windowLive = bucket.ResetAt is not { } observedReset || observedReset > now;
        var degraded = EffectiveBlockedUntil(bucket, now) is not null
            || (windowLive
                && bucket.Remaining is { } remaining
                && remaining <= Math.Max(0, _optionsAccessor().BackgroundReserveRemaining));
        if (!degraded)
        {
            bucket.DegradedWarned = false;
            return;
        }
        if (bucket.DegradedWarned)
            return;
        bucket.DegradedWarned = true;
        var top = bucket.TopPaths
            .OrderByDescending(kv => kv.Value)
            .Take(5)
            .Select(kv => $"{kv.Key}×{kv.Value}");
        _log.LogWarning(
            "GitHub API budget low for credential {BudgetKey}: {Remaining} requests remaining, " +
            "resets at {ResetAt}, blocked until {BlockedUntil} ({BlockedReason}); " +
            "background reads are deferred — top consumers: {TopConsumers}",
            bucket.Key,
            bucket.Remaining?.ToString(CultureInfo.InvariantCulture) ?? "unknown",
            bucket.ResetAt?.ToString("o") ?? "unknown",
            EffectiveBlockedUntil(bucket, now)?.ToString("o") ?? "no",
            bucket.BlockedReason ?? "reserve",
            string.Join(", ", top));
    }

    private static DateTimeOffset? EffectiveBlockedUntil(Bucket bucket, DateTimeOffset now)
    {
        var until = bucket.BlockedUntil;
        if (bucket.Remaining == 0 && bucket.ResetAt is { } reset && reset > now
            && (until is null || reset > until))
            until = reset;
        return until is { } u && u > now ? u : null;
    }

    private static DateTimeOffset? Latest(DateTimeOffset? a, DateTimeOffset? b) =>
        a is null ? b : b is null ? a : a > b ? a : b;

    private static bool TryGetHeader(HttpResponseMessage response, string name, out string? value)
    {
        value = null;
        return response.Headers.TryGetValues(name, out var values)
            && (value = values.FirstOrDefault()) is not null;
    }

    private static void TrackPath(Bucket bucket, string? path)
    {
        if (string.IsNullOrEmpty(path))
            return;
        var normalized = NormalizePath(path);
        if (bucket.TopPaths.TryGetValue(normalized, out var count))
        {
            bucket.TopPaths[normalized] = count + 1;
            return;
        }
        if (bucket.TopPaths.Count >= MaxTopPaths)
        {
            // Evict the least-used entry so a flood of distinct paths cannot
            // freeze the consumer list shown in the low-budget warning.
            var coldest = bucket.TopPaths.MinBy(kv => kv.Value);
            bucket.TopPaths.Remove(coldest.Key);
        }
        bucket.TopPaths[normalized] = 1;
    }

    /// <summary>
    /// Reduces a request path to a bounded, low-cardinality endpoint label.
    /// High-cardinality segments — numeric ids (PR numbers, installation
    /// ids), hex shas, manifest/branch tokens, and anything id-like —
    /// collapse to <c>#</c> so per-path consumer stats stay bounded, readable,
    /// and free of credential-bearing values (e.g. the
    /// <c>/app-manifests/{code}/conversions</c> exchange code).
    /// </summary>
    private static string NormalizePath(string path)
    {
        var builder = new StringBuilder(Math.Min(path.Length, MaxPathLength));
        var segments = path.Split('/');
        for (var i = 0; i < segments.Length && builder.Length < MaxPathLength; i++)
        {
            if (i > 0 && builder.Length < MaxPathLength)
                builder.Append('/');
            var segment = segments[i];
            if (IsHighCardinalitySegment(segment))
            {
                if (builder.Length < MaxPathLength)
                    builder.Append('#');
                continue;
            }
            foreach (var c in segment)
            {
                if (builder.Length >= MaxPathLength)
                    break;
                builder.Append(c);
            }
        }
        return builder.ToString();
    }

    private static bool IsHighCardinalitySegment(string segment)
    {
        if (segment.Length == 0)
            return false;
        // Pure-numeric ids: PR numbers, installation ids.
        if (segment.All(char.IsDigit))
            return true;
        // Endpoint names are short words; anything this long is a token,
        // sha, or percent-escaped branch ref — never a route segment.
        if (segment.Length >= 24)
            return true;
        // Mixed letter/digit ids at id-like lengths (hex shas, app-manifest
        // codes, owner/repo names containing digits, escaped branch refs).
        return segment.Length >= 8
            && segment.Any(char.IsLetter)
            && segment.Any(char.IsDigit);
    }

    private sealed class Bucket
    {
        public required string Key { get; init; }
        public DateTimeOffset LastSeenUtc { get; set; }
        public long RequestsObserved { get; set; }
        public int? Remaining { get; set; }
        public DateTimeOffset? ResetAt { get; set; }
        public DateTimeOffset? BlockedUntil { get; set; }
        public string? BlockedReason { get; set; }
        public bool DegradedWarned { get; set; }
        public readonly Dictionary<string, int> TopPaths = new(StringComparer.Ordinal);
    }
}
