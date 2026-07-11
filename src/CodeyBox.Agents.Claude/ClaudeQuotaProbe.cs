using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using CodeyBox.Agents;
using CodeyBox.Core;

namespace CodeyBox.Agents.Claude;

/// <summary>
/// Probes the Anthropic OAuth usage endpoint to estimate available Claude
/// subscription quota. Uses the <c>agent-quota</c> named HTTP client.
///
/// Transient probe failures (network errors, timeouts, 5xx, 408, 429) retry
/// with backoff before being recorded as a failure. On continued failure the
/// last-known-good snapshot is RETAINED (with a staleness note carrying the
/// age) until either <c>MaxConsecutiveFailures</c> end-to-end probes have
/// failed or the retained snapshot age exceeds <c>MaxStaleness</c>; only then
/// does the snapshot fall to <see cref="AgentQuotaSnapshot.AvailablePct"/> = -1
/// so the router's unknown policy decides what to do. This means a one-off
/// network blip cannot silently disable the <c>MinQuotaPct</c> floor.
///
/// Thread-safe; results are cached for <c>cacheTtl</c> to avoid hammering
/// the endpoint when several work items pick up close together.
/// </summary>
public sealed class ClaudeQuotaProbe : IAgentQuotaProbe, IAgentQuotaCacheInvalidator
{
    internal const string UsageEndpoint = "https://api.anthropic.com/api/oauth/usage";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly Func<AgentMembership, AgentQuotaCredentials> _credentialsProvider;
    private readonly TimeSpan _cacheTtl;
    private readonly Func<ClaudeQuotaProbeResilienceOptions> _resilienceProvider;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ClaudeQuotaProbe> _log;

    // Single-entry cache: (route key, token, snapshot, expiry). Protected by _lock.
    private (string RouteKey, string AccessToken, AgentQuotaSnapshot Snapshot, DateTimeOffset ExpiresAt)? _cache;
    // Last successful fetch's underlying snapshot + when it was captured.
    // Surfaced as a stale-but-retained reading on transient failures.
    private readonly SemaphoreSlim _lock = new(1, 1);

    // Dedupes the Information log line that fires when a configured model is
    // absent from the probe response. Keyed by (token, modelId).
    private readonly HashSet<(string Token, string ModelId)> _loggedMissingModels = new();
    private readonly object _loggedMissingModelsLock = new();

    public AgentKind Kind => AgentKind.Claude;

    public ClaudeQuotaProbe(
        IHttpClientFactory httpClientFactory,
        string? token,
        TimeSpan cacheTtl,
        ILogger<ClaudeQuotaProbe> log)
        : this(httpClientFactory, () => new AgentQuotaCredentials(token), cacheTtl, log)
    {
    }

    public ClaudeQuotaProbe(
        IHttpClientFactory httpClientFactory,
        Func<AgentQuotaCredentials> credentialsProvider,
        TimeSpan cacheTtl,
        ILogger<ClaudeQuotaProbe> log)
        : this(httpClientFactory, _ => credentialsProvider(), cacheTtl, log)
    {
    }

    public ClaudeQuotaProbe(
        IHttpClientFactory httpClientFactory,
        Func<AgentMembership, AgentQuotaCredentials> credentialsProvider,
        TimeSpan cacheTtl,
        ILogger<ClaudeQuotaProbe> log)
        : this(httpClientFactory, credentialsProvider, cacheTtl, log,
               resilienceProvider: null, timeProvider: null)
    {
    }

    public ClaudeQuotaProbe(
        IHttpClientFactory httpClientFactory,
        Func<AgentQuotaCredentials> credentialsProvider,
        TimeSpan cacheTtl,
        ILogger<ClaudeQuotaProbe> log,
        Func<ClaudeQuotaProbeResilienceOptions>? resilienceProvider,
        TimeProvider? timeProvider)
        : this(httpClientFactory, _ => credentialsProvider(), cacheTtl, log, resilienceProvider, timeProvider)
    {
    }

    public ClaudeQuotaProbe(
        IHttpClientFactory httpClientFactory,
        Func<AgentMembership, AgentQuotaCredentials> credentialsProvider,
        TimeSpan cacheTtl,
        ILogger<ClaudeQuotaProbe> log,
        Func<ClaudeQuotaProbeResilienceOptions>? resilienceProvider,
        TimeProvider? timeProvider)
    {
        _httpClientFactory = httpClientFactory;
        _credentialsProvider = credentialsProvider;
        _cacheTtl = cacheTtl;
        _resilienceProvider = resilienceProvider ?? (static () => new ClaudeQuotaProbeResilienceOptions());
        _timeProvider = timeProvider ?? TimeProvider.System;
        _log = log;
    }

    public async Task<AgentQuotaSnapshot> GetAvailabilityAsync(AgentMembership member, CancellationToken ct)
    {
        var credentials = _credentialsProvider(member);
        var token = credentials.AccessToken;
        if (string.IsNullOrEmpty(token))
            return Unknown(QuotaUnknownReason.NoCredential, "no token configured");
        var routeKey = member.RouteKey;

        AgentQuotaSnapshot snapshot;
        await _lock.WaitAsync(ct);
        try
        {
            var now = _timeProvider.GetUtcNow();
            if (_cache is { } entry
                && string.Equals(entry.RouteKey, routeKey, StringComparison.OrdinalIgnoreCase)
                && string.Equals(entry.AccessToken, token, StringComparison.Ordinal)
                && now < entry.ExpiresAt)
            {
                snapshot = entry.Snapshot;
            }
            else
            {
                snapshot = await FetchWithResilienceAsync(token, ct);
                _cache = (routeKey, token, snapshot, _timeProvider.GetUtcNow() + _cacheTtl);
            }
        }
        finally
        {
            _lock.Release();
        }

        return ApplyMemberGate(snapshot, member, token);
    }

    /// <summary>
    /// When the configured <see cref="AgentMembership.ModelId"/> is not present
    /// in the parsed response's per-model buckets, attempt to resolve it to a
    /// family/overall bucket before degrading to unknown. The Claude usage API
    /// keys its per-model buckets by family names (e.g. "seven_day_opus"), not
    /// by the full configured model id (e.g. "claude-opus-4-8"), so an exact
    /// <see cref="AgentQuotaSnapshot.PerModel"/> key match is the exception
    /// rather than the rule. When a family bucket or a usable overall/window
    /// reading is available, return a KNOWN snapshot (with the resolved model
    /// id added to <see cref="AgentQuotaSnapshot.PerModel"/> so
    /// <see cref="QuotaGatePolicy.ResolveMemberQuota"/> finds it) rather than
    /// Unknown — the floor is then enforced normally. Only degrade to Unknown
    /// when neither a family bucket nor an overall reading is available. Logs
    /// once per (token, modelId) when even the resolution falls through so
    /// operators can spot typos in configured model ids.
    /// </summary>
    private AgentQuotaSnapshot ApplyMemberGate(AgentQuotaSnapshot snapshot, AgentMembership member, string token)
    {
        if (!snapshot.IsKnown) return snapshot;
        if (string.IsNullOrWhiteSpace(member.ModelId)) return snapshot;
        if (snapshot.PerModel.ContainsKey(member.ModelId)) return snapshot;

        if (TryResolveModelQuota(snapshot, member.ModelId) is { } resolved)
        {
            // Add the resolved reading under the configured model id so
            // QuotaGatePolicy.ResolveMemberQuota finds it via TryGetValue and
            // the floor is enforced on a KNOWN reading rather than degrading
            // to Unknown.
            var perModel = new Dictionary<string, ModelQuota>(snapshot.PerModel, StringComparer.OrdinalIgnoreCase)
            {
                [member.ModelId] = resolved.Quota,
            };
            _log.LogDebug(
                "Claude quota probe: configured model {ModelId} resolved to {Resolution}",
                member.ModelId, resolved.Note);
            return new AgentQuotaSnapshot
            {
                AvailablePct = snapshot.AvailablePct,
                ResetAt = snapshot.ResetAt,
                Notes = snapshot.Notes,
                PerModel = perModel,
                Windows = snapshot.Windows,
            };
        }

        var modelList = snapshot.PerModel.Count == 0
            ? "(none)"
            : string.Join(", ", snapshot.PerModel.Keys.OrderBy(k => k, StringComparer.Ordinal));
        var notes = $"configured model '{member.ModelId}' not in quota response (have: {modelList})";

        bool firstTime;
        lock (_loggedMissingModelsLock)
            firstTime = _loggedMissingModels.Add((token, member.ModelId));
        if (firstTime)
        {
            _log.LogInformation(
                "Claude quota probe: configured model {ModelId} not in response buckets ({BucketList}); reporting unknown so the router can apply its unknown policy",
                member.ModelId, modelList);
        }

        return new AgentQuotaSnapshot
        {
            AvailablePct = -1,
            Unknown = QuotaUnknownReason.Permanent,
            ResetAt = snapshot.ResetAt,
            Notes = notes,
            PerModel = snapshot.PerModel,
        };
    }

    /// <summary>
    /// Resolves a configured model id that is not an exact
    /// <see cref="AgentQuotaSnapshot.PerModel"/> key to a family bucket (by
    /// substring match against the opus/sonnet/haiku families) or, failing
    /// that, to the overall/binding-window reading. Returns a
    /// <see cref="ResolvedModelQuota"/> (with a KNOWN <see cref="ModelQuota"/>)
    /// when a usable reading is available; null only when the snapshot carries
    /// no usable reading at all.
    /// </summary>
    private static ResolvedModelQuota? TryResolveModelQuota(
        AgentQuotaSnapshot snapshot,
        string modelId)
    {
        // 1. Family bucket match by substring. The configured model id (e.g.
        // "claude-opus-4-8") carries a family token ("opus"); the API's
        // per-model buckets are keyed by family suffix (e.g.
        // "seven_day_opus"). Match the family, then read the suffix bucket
        // (already capped by overall in the parser).
        foreach (var (suffix, modelMatch) in ModelSuffixes)
        {
            if (!modelId.Contains(modelMatch, StringComparison.OrdinalIgnoreCase)) continue;

            if (snapshot.PerModel.TryGetValue(suffix, out var familyQuota))
            {
                return new ResolvedModelQuota(familyQuota, $"family bucket '{suffix}'");
            }

            // Fall back to any per-model key carrying the same family token
            // (covers legacy shapes and probes that surface configured-model
            // keys alongside the family suffix).
            foreach (var (key, quota) in snapshot.PerModel)
            {
                if (key.Contains(modelMatch, StringComparison.OrdinalIgnoreCase))
                {
                    return new ResolvedModelQuota(quota, $"family bucket '{key}'");
                }
            }
        }

        // 2. Fall back to the overall/binding-window reading. The snapshot is
        // known (checked by the caller), so this is always available when we
        // reach here.
        if (snapshot.IsKnown)
        {
            return new ResolvedModelQuota(
                new ModelQuota
                {
                    AvailablePct = snapshot.AvailablePct,
                    ResetAt = snapshot.ResetAt,
                    Window = "overall (model-specific bucket unavailable)",
                },
                "overall reading (no model-specific bucket)");
        }

        return null;
    }

    private sealed record ResolvedModelQuota(ModelQuota Quota, string Note);

    /// <summary>
    /// Drops the in-process snapshot so the next
    /// <see cref="GetAvailabilityAsync"/> call refetches against the upstream
    /// usage endpoint. Wire to <see cref="CodeyBox.Orchestrator.CredentialFileSource.TokenUpdated"/>
    /// so an out-of-band host token rotation (operator running the CLI, child
    /// sandbox writeback, scripted refresh) doesn't leave a stale 401 pinned
    /// for the full cache TTL.
    /// </summary>
    public void InvalidateCache()
    {
        _lock.Wait();
        try
        {
            _cache = null;
        }
        finally { _lock.Release(); }
    }

    private const int MaxResponseChars = 64 * 1024; // 64 KiB

    /// <summary>
    /// Single end-to-end probe attempt: retries transient failures with
    /// exponential backoff. On terminal failure, retains a stale last-known-good
    /// snapshot until either too many consecutive failures or staleness expiry.
    /// </summary>
    private async Task<AgentQuotaSnapshot> FetchWithResilienceAsync(string token, CancellationToken ct)
    {
        var opts = _resilienceProvider();
        var totalAttempts = Math.Max(1, opts.MaxRetries + 1);
        ProbeAttemptResult last = default;

        for (int attempt = 0; attempt < totalAttempts; attempt++)
        {
            if (attempt > 0)
            {
                var exponential = ComputeExponentialBackoff(opts.RetryInitialDelay, attempt - 1);
                var backoff = HttpQuotaRetryPolicy.ComputeRetryDelay(
                    exponential,
                    last.RetryAfterDelay,
                    opts.MaxRetryDelay > TimeSpan.Zero
                        ? opts.MaxRetryDelay
                        : ClaudeQuotaProbeResilienceOptions.DefaultMaxRetryDelay);
                await Task.Delay(backoff, _timeProvider, ct);
            }

            last = await ProbeOnceAsync(token, ct);
            if (last.Outcome is ProbeOutcome.Success or ProbeOutcome.PermanentFailure)
            {
                // Success returns the parsed snapshot (a real reading, or a
                // Permanent unknown for an unparseable 200); PermanentFailure
                // returns the Unknown(Permanent) the attempt built. Staleness is
                // owned by the LastKnownGoodQuotaProbe decorator from here.
                return last.Snapshot!;
            }
        }

        // All attempts failed transiently. Report a transient unknown; the
        // LastKnownGoodQuotaProbe decorator decides whether to substitute a
        // recent reading.
        return Unknown(QuotaUnknownReason.Transient, last.Reason ?? "network error");
    }

    private async Task<ProbeAttemptResult> ProbeOnceAsync(string token, CancellationToken ct)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("agent-quota");
            using var request = new HttpRequestMessage(HttpMethod.Get, UsageEndpoint);
            // Do NOT log the Authorization header — it contains the OAuth token.
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                _log.LogDebug("Claude quota endpoint returned {StatusCode}; treating quota as unknown",
                    status);
                var reason = $"HTTP {status}";
                return IsTransientStatus(response.StatusCode)
                    ? ProbeAttemptResult.Transient(
                        reason,
                        HttpQuotaRetryPolicy.TryGetRetryAfterDelay(
                            response.Headers,
                            _timeProvider.GetUtcNow()))
                    : ProbeAttemptResult.Permanent(Unknown(QuotaUnknownReason.Permanent, reason), reason);
            }

            // Do NOT log the response body — it may contain account identifiers.
            var body = await ReadCappedAsync(response.Content, ct);
            if (body is null)
                return ProbeAttemptResult.Permanent(Unknown(QuotaUnknownReason.Permanent, "response too large"), "response too large");
            return ProbeAttemptResult.Success(ParseResponse(body));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Claude quota probe failed; treating as transient");
            return ProbeAttemptResult.Transient("network error");
        }
    }

    private static bool IsTransientStatus(HttpStatusCode status)
    {
        var code = (int)status;
        if (code >= 500 && code <= 599) return true;
        return status == HttpStatusCode.RequestTimeout
            || status == HttpStatusCode.TooManyRequests;
    }

    private static TimeSpan ComputeExponentialBackoff(TimeSpan initialDelay, int completedRetries)
    {
        if (initialDelay <= TimeSpan.Zero)
            return TimeSpan.Zero;

        var multiplier = Math.Pow(2, completedRetries);
        var ticks = initialDelay.Ticks * multiplier;
        return ticks >= TimeSpan.MaxValue.Ticks
            ? TimeSpan.MaxValue
            : TimeSpan.FromTicks((long)ticks);
    }

    private enum ProbeOutcome { Success, TransientFailure, PermanentFailure }

    private readonly record struct ProbeAttemptResult(
        ProbeOutcome Outcome,
        AgentQuotaSnapshot? Snapshot,
        string? Reason,
        TimeSpan? RetryAfterDelay = null)
    {
        public static ProbeAttemptResult Success(AgentQuotaSnapshot snapshot)
            => new(ProbeOutcome.Success, snapshot, null);

        public static ProbeAttemptResult Transient(string reason, TimeSpan? retryAfterDelay = null)
            => new(ProbeOutcome.TransientFailure, null, reason, retryAfterDelay);

        public static ProbeAttemptResult Permanent(AgentQuotaSnapshot snapshot, string reason)
            => new(ProbeOutcome.PermanentFailure, snapshot, reason);
    }

    private static async Task<string?> ReadCappedAsync(HttpContent content, CancellationToken ct)
    {
        // Allocate one extra char so we can detect bodies that exceed the cap.
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        var buffer = new char[MaxResponseChars + 1];
        int totalRead = 0, chunk;
        do
        {
            chunk = await reader.ReadAsync(buffer.AsMemory(totalRead, buffer.Length - totalRead), ct);
            totalRead += chunk;
        }
        while (chunk > 0 && totalRead < buffer.Length);
        if (totalRead > MaxResponseChars) return null;
        return new string(buffer, 0, totalRead);
    }

    // Claude's OAuth usage endpoint returns a flat object with named buckets
    // like `five_hour`, `seven_day`, `seven_day_opus`, `seven_day_sonnet`.
    // Each bucket has `utilization` (0-100, where 100 means capped) and
    // `resets_at`. Global buckets (no `_<model>` suffix beyond the window
    // name) constrain ALL models; `_<model>` suffixes constrain only that
    // family. Effective availability is min(global) globally, and
    // min(global, model-specific) per model.
    private static readonly string[] GlobalBuckets = ["five_hour", "seven_day"];

    // Maps a model bucket suffix to model-id substrings it constrains.
    // E.g. `seven_day_opus` constrains any model id containing "opus".
    private static readonly (string Suffix, string ModelMatch)[] ModelSuffixes =
    [
        ("seven_day_opus", "opus"),
        ("seven_day_sonnet", "sonnet"),
        ("seven_day_haiku", "haiku"),
    ];

    internal static AgentQuotaSnapshot ParseResponse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Try the new flat-bucket shape first.
            var flat = TryParseFlatShape(root);
            if (flat is not null) return flat;

            // Fallback: the older `rate_limit` + `additional_rate_limits` shape
            // (kept for backwards compatibility / future-proofing).
            var overall = TryParseRateLimit(root.TryGetProperty("rate_limit", out var rateLimit) ? rateLimit : root);
            var perModel = ParsePerModel(root);

            if (overall is not null && perModel.Count > 0)
            {
                var capPct = overall.AvailablePct;
                foreach (var key in perModel.Keys.ToList())
                {
                    var v = perModel[key];
                    if (v.AvailablePct > capPct)
                        perModel[key] = new ModelQuota
                        {
                            AvailablePct = capPct,
                            ResetAt = overall.ResetAt ?? v.ResetAt,
                            Window = $"{v.Window} (capped by overall)",
                            Windows = v.Windows,
                        };
                }
            }

            if (overall is not null || perModel.Count > 0)
                return new AgentQuotaSnapshot
                {
                    AvailablePct = overall?.AvailablePct ?? -1,
                    ResetAt = overall?.ResetAt,
                    Notes = overall is null ? "overall quota unknown; parsed per-model rollups" : null,
                    PerModel = perModel,
                    Windows = overall?.Windows ?? Array.Empty<WindowQuota>(),
                };

            return Unknown(QuotaUnknownReason.Permanent, "unexpected response shape");
        }
        catch (JsonException)
        {
            return Unknown(QuotaUnknownReason.Permanent, "invalid JSON");
        }
    }

    private static AgentQuotaSnapshot? TryParseFlatShape(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;

        // Detect flat shape: at least one global bucket present with `utilization`.
        // Collect every global window's raw reading so /quota can expose the
        // breakdown — without this, an operator sees only the aggregated min
        // and can't tell whether the gate was the 5h or the 7d cap.
        ModelQuota? overall = null;
        var globalWindows = new List<WindowQuota>(GlobalBuckets.Length);
        foreach (var bucket in GlobalBuckets)
        {
            if (!root.TryGetProperty(bucket, out var el) || el.ValueKind != JsonValueKind.Object) continue;
            var quota = ParseFlatBucket(el, bucket);
            if (quota is null) continue;
            globalWindows.Add(new WindowQuota
            {
                Name = bucket,
                AvailablePct = quota.AvailablePct,
                ResetAt = quota.ResetAt,
            });
            if (overall is null || quota.AvailablePct < overall.AvailablePct)
                overall = quota with { Window = bucket };
        }

        if (overall is null) return null; // not the flat shape
        overall = overall with { Windows = globalWindows };

        // Collect model-specific buckets.
        var modelBuckets = new Dictionary<string, ModelQuota>(StringComparer.OrdinalIgnoreCase);
        foreach (var (suffix, _) in ModelSuffixes)
        {
            if (!root.TryGetProperty(suffix, out var el) || el.ValueKind != JsonValueKind.Object) continue;
            var quota = ParseFlatBucket(el, suffix);
            if (quota is not null)
                modelBuckets[suffix] = quota;
        }

        // Build per-model dict: every model_id matched by a suffix gets
        // min(overall, model-specific). Keep the suffix as a synthetic key
        // too so callers that route by suffix still work. Each per-model
        // ModelQuota carries the global windows plus its own model-specific
        // window so the breakdown stays complete in /quota.
        var perModel = new Dictionary<string, ModelQuota>(StringComparer.OrdinalIgnoreCase);
        foreach (var (suffix, _) in ModelSuffixes)
        {
            if (!modelBuckets.TryGetValue(suffix, out var bucket)) continue;
            var windowsForModel = BuildPerModelWindows(globalWindows, suffix, bucket);
            var capped = bucket.AvailablePct < overall.AvailablePct
                ? bucket with { Window = suffix, Windows = windowsForModel }
                : new ModelQuota
                {
                    AvailablePct = overall.AvailablePct,
                    ResetAt = overall.ResetAt ?? bucket.ResetAt,
                    Window = $"{suffix} (capped by overall)",
                    Windows = windowsForModel,
                };
            perModel[suffix] = capped;
        }

        // Map any client-known model ids by substring match.
        var configuredModels = new[] { "claude-opus-4-7", "claude-sonnet-4-6", "claude-haiku-4-5" };
        foreach (var modelId in configuredModels)
        {
            ModelQuota? best = overall; // default to global cap
            List<WindowQuota> windowsForModel = globalWindows;
            foreach (var (suffix, modelMatch) in ModelSuffixes)
            {
                if (!modelId.Contains(modelMatch, StringComparison.OrdinalIgnoreCase)) continue;
                if (!modelBuckets.TryGetValue(suffix, out var modelBucket)) continue;
                windowsForModel = BuildPerModelWindows(globalWindows, suffix, modelBucket);
                var effective = modelBucket.AvailablePct < overall.AvailablePct
                    ? modelBucket
                    : overall;
                if (best is null || effective.AvailablePct < best.AvailablePct)
                    best = effective with { Window = suffix };
            }
            if (best is not null)
                perModel[modelId] = best with { Windows = windowsForModel };
        }

        return new AgentQuotaSnapshot
        {
            AvailablePct = overall.AvailablePct,
            ResetAt = overall.ResetAt,
            Notes = null,
            PerModel = perModel,
            Windows = globalWindows,
        };
    }

    private static List<WindowQuota> BuildPerModelWindows(
        List<WindowQuota> globalWindows,
        string modelSuffix,
        ModelQuota modelBucket)
    {
        var result = new List<WindowQuota>(globalWindows.Count + 1);
        result.AddRange(globalWindows);
        result.Add(new WindowQuota
        {
            Name = modelSuffix,
            AvailablePct = modelBucket.AvailablePct,
            ResetAt = modelBucket.ResetAt,
        });
        return result;
    }

    private static ModelQuota? ParseFlatBucket(JsonElement el, string window)
    {
        if (!TryGetDoubleProperty(el, "utilization", out var utilPct)) return null;
        return new ModelQuota
        {
            AvailablePct = ClampAvailable(100.0 - utilPct),
            ResetAt = TryGetResetAt(el),
            Window = window,
        };
    }

    private static Dictionary<string, ModelQuota> ParsePerModel(JsonElement root)
    {
        var perModel = new Dictionary<string, ModelQuota>(StringComparer.OrdinalIgnoreCase);

        if (root.TryGetProperty("additional_rate_limits", out var additional) &&
            additional.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in additional.EnumerateArray())
                AddModelQuota(perModel, item);
        }

        if (root.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in models.EnumerateObject())
            {
                var quota = TryParseRateLimit(prop.Value.TryGetProperty("rate_limit", out var rateLimit) ? rateLimit : prop.Value);
                if (quota is not null)
                    perModel[prop.Name] = quota;
            }
        }

        if (root.TryGetProperty("model_rate_limits", out var modelLimits) &&
            modelLimits.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in modelLimits.EnumerateArray())
                AddModelQuota(perModel, item);
        }

        return perModel;
    }

    private static void AddModelQuota(Dictionary<string, ModelQuota> perModel, JsonElement item)
    {
        var modelId = TryGetString(item, "model_id")
            ?? TryGetString(item, "model")
            ?? TryGetString(item, "limit_name")
            ?? TryGetString(item, "limitName");
        if (string.IsNullOrWhiteSpace(modelId))
            return;

        var quotaSource = item.TryGetProperty("rate_limit", out var rateLimit) ? rateLimit : item;
        var quota = TryParseRateLimit(quotaSource);
        if (quota is not null)
            perModel[modelId] = quota;
    }

    private static ModelQuota? TryParseRateLimit(JsonElement el)
    {
        var windowSources = new[]
        {
            ("5h-rolling", TryGetProperty(el, "primary_window")),
            ("weekly", TryGetProperty(el, "secondary_window")),
            ("overall", (JsonElement?)el),
        };

        // Two passes: collect every named window for /quota exposure, then
        // pick the min (most-constrained) for the aggregated AvailablePct.
        var windows = new List<WindowQuota>(windowSources.Length);
        ModelQuota? mostConstrained = null;
        foreach (var (windowName, window) in windowSources)
        {
            if (window is null)
                continue;

            var quota = TryParseWindow(window.Value, windowName);
            if (quota is null)
                continue;

            // Skip the synthetic "overall" entry when it duplicates the named
            // windows — it carries no extra signal in the breakdown.
            if (windowName != "overall")
                windows.Add(new WindowQuota
                {
                    Name = windowName,
                    AvailablePct = quota.AvailablePct,
                    ResetAt = quota.ResetAt,
                });

            if (mostConstrained is null || quota.AvailablePct < mostConstrained.AvailablePct)
                mostConstrained = quota;
        }

        return mostConstrained is null
            ? null
            : mostConstrained with { Windows = windows };
    }

    private static ModelQuota? TryParseWindow(JsonElement el, string window)
    {
        // Explicit deny flags trump usage percentages.
        var explicitDeny = (TryGetBoolProperty(el, "allowed", out var allowed) && !allowed)
                        || (TryGetBoolProperty(el, "limit_reached", out var lim) && lim);

        if (!TryGetDoubleProperty(el, "used_percent", out var usedPct) &&
            !TryGetDoubleProperty(el, "usedPercent", out usedPct))
        {
            if (TryGetDoubleProperty(el, "available_percent", out var availablePct) ||
                TryGetDoubleProperty(el, "availablePercent", out availablePct))
            {
                var pct = ClampAvailable(availablePct);
                if (explicitDeny) pct = 0;
                return new ModelQuota
                {
                    AvailablePct = pct,
                    ResetAt = TryGetResetAt(el),
                    Window = window,
                };
            }

            if (TryGetDoubleProperty(el, "used", out var used) &&
                TryGetDoubleProperty(el, "limit", out var limit) &&
                limit > 0)
            {
                var pct = ClampAvailable(100.0 * (1.0 - used / limit));
                if (explicitDeny) pct = 0;
                return new ModelQuota
                {
                    AvailablePct = pct,
                    ResetAt = TryGetResetAt(el),
                    Window = window,
                };
            }

            if (explicitDeny)
                return new ModelQuota { AvailablePct = 0, ResetAt = TryGetResetAt(el), Window = window };
            return null;
        }

        var availPct = ClampAvailable(100.0 - usedPct);
        if (explicitDeny) availPct = 0;
        return new ModelQuota
        {
            AvailablePct = availPct,
            ResetAt = TryGetResetAt(el),
            Window = window,
        };
    }

    private static bool TryGetBoolProperty(JsonElement el, string name, out bool value)
    {
        value = false;
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out var prop))
            return false;
        if (prop.ValueKind == JsonValueKind.True) { value = true; return true; }
        if (prop.ValueKind == JsonValueKind.False) { value = false; return true; }
        return false;
    }

    private static JsonElement? TryGetProperty(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var value) ? value : null;

    private static string? TryGetString(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object &&
        el.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool TryGetDoubleProperty(JsonElement el, string name, out double value)
    {
        value = 0;
        return el.ValueKind == JsonValueKind.Object &&
               el.TryGetProperty(name, out var prop) &&
               TryGetDouble(prop, out value);
    }

    private static bool TryGetDouble(JsonElement el, out double value)
    {
        value = 0;
        return el.ValueKind switch
        {
            JsonValueKind.Number => el.TryGetDouble(out value),
            JsonValueKind.String => double.TryParse(el.GetString(), out value),
            _ => false,
        };
    }

    private static DateTimeOffset? TryGetResetAt(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Object)
        {
            if (el.TryGetProperty("reset_at", out var snake))
                return TryGetResetAt(snake);
            if (el.TryGetProperty("resets_at", out var snakeS))
                return TryGetResetAt(snakeS);
            if (el.TryGetProperty("resetAt", out var camel))
                return TryGetResetAt(camel);
        }

        return el.ValueKind switch
        {
            JsonValueKind.Number when el.TryGetInt64(out var seconds) =>
                DateTimeOffset.FromUnixTimeSeconds(seconds),
            JsonValueKind.String when DateTimeOffset.TryParse(el.GetString(), out var parsed) =>
                parsed,
            _ => null,
        };
    }

    private static double ClampAvailable(double pct) => Math.Clamp(pct, 0.0, 100.0);

    private static AgentQuotaSnapshot Unknown(QuotaUnknownReason reason, string notes) =>
        AgentQuotaSnapshot.UnknownSnapshot(reason, notes);
}
