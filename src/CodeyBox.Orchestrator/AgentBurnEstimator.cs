using System.Collections.Concurrent;
using CodeyBox.Core;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Configuration for the rolling-average per-item burn estimate used by the
/// rate-aware dispatch gate. Bind under <c>CodeyBox:AgentConcurrency:Burn</c>.
///
/// <para>
/// When historical cost data is available, the estimator divides the avg
/// token spend per item by <see cref="WindowTokenBudget"/> for that agent to
/// produce a percentage-of-window-per-item figure. When no historical data
/// (cold start) or no budget is configured, it falls back to
/// <see cref="DefaultBurnPercentPerItem"/>.
/// </para>
/// </summary>
public sealed class AgentBurnEstimatorOptions
{
    /// <summary>
    /// Default per-agent burn percent of the primary window when no historical
    /// cost data is available. Operator-tunable. Defaults reflect the observed
    /// 2026-05-17 data: codex ≈ 94%, claude ≈ 4%, gemini ≈ 10%.
    /// </summary>
    public Dictionary<string, double> DefaultBurnPercentPerItem { get; set; } =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["codex"] = 90.0,
            ["claude"] = 4.0,
            ["gemini"] = 10.0,
            ["copilot"] = 10.0,
            ["cursor"] = 90.0,
        };

    /// <summary>
    /// Per-agent token budget for the primary window. When set, recent
    /// per-item token totals are divided by this to compute the burn pct,
    /// overriding <see cref="DefaultBurnPercentPerItem"/>. Empty by default;
    /// operators opt in when they have measured numbers.
    /// </summary>
    public Dictionary<string, long> WindowTokenBudget { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How many of the most-recent Done items to include in the rolling avg. Default 10.</summary>
    public int RollingSampleSize { get; set; } = 10;

    /// <summary>How long to cache an estimate before re-querying the cost store. Default 60s.</summary>
    public TimeSpan CacheTtl { get; set; } = TimeSpan.FromSeconds(60);
}

/// <summary>
/// Default <see cref="IAgentBurnEstimator"/>: reads recent <see cref="WorkItemCost"/>
/// rows via <see cref="IWorkItemCostStore"/>, aggregates per work item, and
/// returns the rolling avg. Caches results in-process for
/// <see cref="AgentBurnEstimatorOptions.CacheTtl"/> to keep the hot dispatch
/// path cheap. Falls back to the configured defaults when historical data is
/// not yet available — the rate-aware gate sees this as "0 samples" and
/// applies the spec's "fits 2 concurrent burns" cold-start rule.
/// </summary>
public sealed class AgentBurnEstimator : IAgentBurnEstimator
{
    private readonly Func<IWorkItemCostStore> _resolveCosts;
    // Mutable + Volatile-swapped so the hot-reload coordinator can publish new
    // burn defaults / token budgets / cache TTL without a restart. Reads in
    // ComputeAsync take a single Volatile.Read into a local so a concurrent
    // swap can't tear the per-agent lookup.
    private AgentBurnEstimatorOptions _opts;
    private readonly TimeProvider _time;
    private readonly ILogger<AgentBurnEstimator> _log;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Deferred-resolution constructor preferred by DI: the cost store is
    /// resolved on the first GetEstimateAsync call so registering the burn
    /// estimator doesn't eagerly instantiate <see cref="IWorkItemCostStore"/>
    /// (and the SQLite file behind it) at container build time.
    /// </summary>
    public AgentBurnEstimator(
        Func<IWorkItemCostStore> resolveCosts,
        AgentBurnEstimatorOptions opts,
        ILogger<AgentBurnEstimator> log,
        TimeProvider? time = null)
    {
        _resolveCosts = resolveCosts;
        _opts = opts;
        _log = log;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Convenience constructor for tests and callers that already hold a store instance.</summary>
    public AgentBurnEstimator(
        IWorkItemCostStore costs,
        AgentBurnEstimatorOptions opts,
        ILogger<AgentBurnEstimator> log,
        TimeProvider? time = null)
        : this(() => costs, opts, log, time)
    {
    }

    public async Task<AgentBurnEstimate> GetEstimateAsync(AgentKind agent, CancellationToken ct = default)
    {
        var key = agent.Value;
        var now = _time.GetUtcNow();
        if (_cache.TryGetValue(key, out var entry) && entry.ExpiresAt > now)
            return entry.Estimate;

        var opts = Volatile.Read(ref _opts);
        var estimate = await ComputeAsync(opts, agent, ct);
        _cache[key] = new CacheEntry(estimate, now + opts.CacheTtl);
        return estimate;
    }

    /// <summary>
    /// Replaces the burn-estimator options with <paramref name="next"/> and
    /// drops the per-agent cache so the next read uses the new
    /// <see cref="AgentBurnEstimatorOptions.RollingSampleSize"/> /
    /// <see cref="AgentBurnEstimatorOptions.WindowTokenBudget"/>. Called by the
    /// hot-reload coordinator on a <c>CodeyBox:AgentBurnEstimator</c> change.
    /// </summary>
    public void ApplyConfigReload(AgentBurnEstimatorOptions next)
    {
        ArgumentNullException.ThrowIfNull(next);
        Volatile.Write(ref _opts, next);
        // Cached entries were computed against the prior RollingSampleSize /
        // WindowTokenBudget; drop them so the next read recomputes under the
        // new policy rather than serving a stale average for up to CacheTtl.
        _cache.Clear();
    }

    private async Task<AgentBurnEstimate> ComputeAsync(AgentBurnEstimatorOptions opts, AgentKind agent, CancellationToken ct)
    {
        long avgTokens = 0;
        int samples = 0;

        IWorkItemCostStore costs;
        try { costs = _resolveCosts(); }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "AgentBurnEstimator: failed to resolve cost store for {Agent}; using configured default",
                agent.Value);
            return new AgentBurnEstimate
            {
                AvgBurnPctPerItem = opts.DefaultBurnPercentPerItem.TryGetValue(agent.Value, out var fallback) ? fallback : -1,
                SampleCount = 0,
            };
        }

        // The store interface query is opt-in; older stores fall back to "no data".
        if (costs is IRecentCostsByAgentQueryable q)
        {
            try
            {
                (avgTokens, samples) = await q.GetAvgTokensPerItemAsync(
                    agent.Value, opts.RollingSampleSize, ct);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "AgentBurnEstimator: cost store query failed for {Agent}; using configured default",
                    agent.Value);
                samples = 0;
            }
        }

        double avgBurnPct;
        int reportedSamples;
        if (samples > 0 && opts.WindowTokenBudget.TryGetValue(agent.Value, out var budget) && budget > 0)
        {
            avgBurnPct = Math.Min(100.0, (avgTokens / (double)budget) * 100.0);
            reportedSamples = samples;
        }
        else
        {
            // Falling back to the configured default — per the AgentBurnEstimate.SampleCount
            // contract, that field counts only Done items that *contributed* to
            // AvgBurnPctPerItem. A default value is not empirical, so SampleCount must
            // be 0 so the router takes its cold-start fit fallback rather than treating
            // the default as a measured average.
            avgBurnPct = opts.DefaultBurnPercentPerItem.TryGetValue(agent.Value, out var d) ? d : -1;
            reportedSamples = 0;
        }

        return new AgentBurnEstimate
        {
            AvgBurnPctPerItem = avgBurnPct,
            SampleCount = reportedSamples,
        };
    }

    private sealed record CacheEntry(AgentBurnEstimate Estimate, DateTimeOffset ExpiresAt);
}

/// <summary>
/// Optional capability marker an <see cref="IWorkItemCostStore"/> may implement
/// so the burn estimator can query recent per-agent rows server-side without
/// loading the full cost table. Stores that don't implement this fall back to
/// the configured <see cref="AgentBurnEstimatorOptions.DefaultBurnPercentPerItem"/>.
/// </summary>
public interface IRecentCostsByAgentQueryable
{
    /// <summary>
    /// Returns (avg total tokens per work item, sample count) for the most
    /// recent <paramref name="limit"/> distinct work items that used
    /// <paramref name="agentKind"/>. "Total tokens" = input + output + cached.
    /// Returns (0, 0) when no rows match.
    /// </summary>
    Task<(long AvgTokens, int Samples)> GetAvgTokensPerItemAsync(
        string agentKind, int limit, CancellationToken ct = default);
}
