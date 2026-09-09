using CodeyBox.Core;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Default <see cref="IAgentBudgetProvider"/>: sums recent
/// <see cref="AgentUsageEvent"/> spend per window via <see cref="IAgentUsageStore"/>
/// and derives a percent-remaining figure.
/// <para>
/// Both the dispatch gate (<see cref="GetBudgetSnapshotAsync"/>) and the
/// visibility summary (<see cref="SummariseAllAsync"/>, behind the /quota
/// endpoint) recompute on every call against the live store — neither serves a
/// cached snapshot. A cache would let either path report a stale "healthy"
/// percent for the cache lifetime after an accounting outage begins, masking the
/// store failure and contradicting the fail-closed contract in
/// <c>docs/operating/budgets.md</c> (degraded accounting must read as exhausted on
/// /quota). Recomputing is cheap — one indexed SUM per window — and neither path
/// is a tight loop (dispatch and dashboard polling).
/// </para>
/// </summary>
public sealed class AgentBudgetCalculator : IAgentBudgetProvider, IAgentBudgetConfigReloadable
{
    private readonly Func<IAgentUsageStore> _resolveStore;
    private AgentBudgetOptions _opts;
    private readonly TimeProvider _time;
    private readonly ILogger<AgentBudgetCalculator> _log;

    public AgentBudgetCalculator(
        Func<IAgentUsageStore> resolveStore,
        AgentBudgetOptions opts,
        ILogger<AgentBudgetCalculator> log,
        TimeProvider? time = null)
    {
        _resolveStore = resolveStore;
        _opts = opts;
        _log = log;
        _time = time ?? TimeProvider.System;
    }

    public AgentBudgetCalculator(
        IAgentUsageStore store,
        AgentBudgetOptions opts,
        ILogger<AgentBudgetCalculator> log,
        TimeProvider? time = null)
        : this(() => store, opts, log, time)
    {
    }

    /// <summary>Swaps options. Called by the hot-reload coordinator.</summary>
    public void ApplyConfigReload(AgentBudgetOptions next)
    {
        ArgumentNullException.ThrowIfNull(next);
        Volatile.Write(ref _opts, next);
    }

    public async Task<AgentQuotaSnapshot?> GetBudgetSnapshotAsync(
        AgentKind agent, string? modelId, CancellationToken ct = default)
    {
        var opts = Volatile.Read(ref _opts);
        var model = ResolveModelOptions(opts, agent.Value, modelId);
        if (model is null || model.Windows.Count == 0) return null;

        var computation = await ComputeAsync(agent.Value, modelId ?? string.Empty, model, ct);
        return computation.Snapshot;
    }

    public async Task<IReadOnlyList<AgentBudgetUsageView>> SummariseAllAsync(CancellationToken ct = default)
    {
        var opts = Volatile.Read(ref _opts);
        var views = new List<AgentBudgetUsageView>();
        foreach (var (agentKind, member) in opts.Members)
        {
            foreach (var (modelKey, model) in member.Models)
            {
                if (model.Windows.Count == 0) continue;
                // Visibility path (/quota): recompute against the live store so an
                // accounting outage reads as exhausted here too, rather than serving
                // a stale healthy snapshot.
                var computation = await ComputeAsync(agentKind, modelKey, model, ct);
                views.Add(new AgentBudgetUsageView(agentKind, modelKey, computation.Windows));
            }
        }
        return views;
    }

    private static AgentBudgetModelOptions? ResolveModelOptions(AgentBudgetOptions opts, string agentKind, string? modelId)
    {
        if (string.IsNullOrEmpty(modelId)) return null;
        if (!opts.Members.TryGetValue(agentKind, out var member)) return null;
        return member.Models.TryGetValue(modelId, out var model) ? model : null;
    }

    private async Task<BudgetComputation> ComputeAsync(
        string agentKind, string modelKey, AgentBudgetModelOptions model, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        // The budget is configured (callers only reach here with >= 1 window), so
        // a store/query failure must NOT silently disable the gate. We fail closed
        // (treat the affected window as fully exhausted, 0% remaining) so a
        // configured spend cap keeps gating dispatch even while accounting is
        // unavailable. Every call recomputes against the live store, so the gate
        // recovers on the next call as soon as the store comes back.
        IAgentUsageStore? store = null;
        bool storeUnavailable = false;
        try { store = _resolveStore(); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex,
                "AgentBudgetCalculator: failed to resolve usage store for {Agent}/{Model}; failing closed",
                agentKind, modelKey);
            storeUnavailable = true;
        }

        // A null model key in config is not supported (windows are per-model), but
        // the usage rows we query carry the same model id, so map "" → null when
        // the configured key represents the default-model bucket.
        string? queryModel = string.IsNullOrEmpty(modelKey) ? null : modelKey;

        var windows = new List<BudgetWindowUsage>(model.Windows.Count);
        double minRemaining = 100.0;
        DateTimeOffset? earliestReset = null;
        bool degraded = storeUnavailable;

        foreach (var w in model.Windows)
        {
            var limitCostMicroCents = CentsToCostMicroCents(w.LimitCents);
            var limitCents = (long)decimal.Round((decimal)w.LimitCents, 0, MidpointRounding.AwayFromZero);

            // An unrecognised window kind (e.g. a new enum value left unhandled or
            // an out-of-range bound value) must NOT fall through to a zero-width
            // query that reports 100% remaining and silently disables the gate.
            // Fail closed and mark degraded so the misconfiguration is visible.
            if (w.Kind is not (BudgetWindowKind.Rolling or BudgetWindowKind.Weekly or BudgetWindowKind.Monthly))
            {
                _log.LogWarning(
                    "AgentBudgetCalculator: unrecognised budget window kind {Kind} for {Agent}/{Model}; failing closed",
                    w.Kind, agentKind, modelKey);
                degraded = true;
                minRemaining = 0.0;
                windows.Add(new BudgetWindowUsage(
                    Kind: w.Kind.ToString(), Hours: null, UsedCents: 0,
                    LimitCents: limitCents, PercentRemaining: 0.0, ResetAt: null));
                continue;
            }

            // A Rolling window with no positive Hours is a misconfiguration. We
            // must NOT silently collapse it to a 1-hour window (a far narrower
            // cap than intended, which would over-report remaining budget).
            // Fail closed and mark degraded — consistent with the handling of
            // unrecognised kinds and zero/negative limits — so the gate keeps
            // blocking until the operator corrects the config.
            if (w.Kind == BudgetWindowKind.Rolling && w.Hours is not > 0)
            {
                _log.LogWarning(
                    "AgentBudgetCalculator: Rolling budget window for {Agent}/{Model} has missing/non-positive Hours ({Hours}); failing closed",
                    agentKind, modelKey, w.Hours);
                degraded = true;
                minRemaining = 0.0;
                windows.Add(new BudgetWindowUsage(
                    Kind: w.Kind.ToString(), Hours: w.Hours, UsedCents: 0,
                    LimitCents: limitCents, PercentRemaining: 0.0, ResetAt: null));
                continue;
            }

            var (fromUtc, toUtc, resetAtCalendar) = WindowBounds(w, now);

            AgentUsageWindowAggregate? agg = null;
            if (!storeUnavailable)
            {
                try
                {
                    agg = await store!.SumWindowAsync(agentKind, queryModel, fromUtc, toUtc, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A cancelled token (shutdown/abort) is not an accounting
                    // outage — let it propagate so the caller unwinds cleanly
                    // rather than parking dispatch as fail-closed/exhausted.
                    _log.LogWarning(ex,
                        "AgentBudgetCalculator: usage query failed for {Agent}/{Model} window {Kind}; failing closed",
                        agentKind, modelKey, w.Kind);
                    degraded = true;
                }
            }

            double percentRemaining;
            long usedCents;
            DateTimeOffset? resetAt;

            if (agg is not { } a)
            {
                // Store unavailable or this window's query failed → fail closed.
                // The window still participates in MIN so the constraint is not
                // dropped; we do not know spend, so report it as exhausted.
                percentRemaining = 0.0;
                usedCents = 0;
                resetAt = w.Kind == BudgetWindowKind.Rolling ? null : resetAtCalendar;
            }
            else if (limitCostMicroCents <= 0)
            {
                // Misconfigured (zero/negative cap). A zero budget means nothing is
                // available, so fail closed rather than disable the gate on a typo.
                percentRemaining = 0.0;
                usedCents = CostMicroCentsToRoundedCents(a.SumMicroCents);
                resetAt = w.Kind == BudgetWindowKind.Rolling
                    ? (a.EarliestUtc is { } e0 && w.Hours is { } h0 ? e0.AddHours(h0) : null)
                    : resetAtCalendar;
            }
            else
            {
                var percentUsed = a.SumMicroCents / (double)limitCostMicroCents * 100.0;
                percentRemaining = Math.Clamp(100.0 - percentUsed, 0.0, 100.0);
                usedCents = CostMicroCentsToRoundedCents(a.SumMicroCents);
                // Rolling reset = when the oldest contributing event ages out of
                // the window (soonest the window's usage drops); null when the
                // window is empty. Calendar reset = the next window boundary.
                resetAt = w.Kind == BudgetWindowKind.Rolling
                    ? (a.EarliestUtc is { } e && w.Hours is { } h ? e.AddHours(h) : null)
                    : resetAtCalendar;
            }

            if (resetAt is { } r && (earliestReset is null || r < earliestReset))
                earliestReset = r;

            minRemaining = Math.Min(minRemaining, percentRemaining);

            windows.Add(new BudgetWindowUsage(
                Kind: w.Kind.ToString(),
                Hours: w.Kind == BudgetWindowKind.Rolling ? w.Hours : null,
                UsedCents: usedCents,
                LimitCents: limitCents,
                PercentRemaining: percentRemaining,
                ResetAt: resetAt));
        }

        var snapshot = new AgentQuotaSnapshot
        {
            AvailablePct = minRemaining,
            ResetAt = earliestReset,
            Notes = degraded ? "local budget (degraded: usage accounting unavailable)" : "local budget",
            Windows = windows
                .Select(u => new WindowQuota
                {
                    Name = u.Hours is { } h ? $"budget:{u.Kind}:{h}h" : $"budget:{u.Kind}",
                    AvailablePct = u.PercentRemaining,
                    ResetAt = u.ResetAt,
                })
                .ToList(),
        };

        return new BudgetComputation(snapshot, windows);
    }

    /// <summary>
    /// Computes the [from, to) span and calendar reset for a window. Callers
    /// (<see cref="ComputeAsync"/>) guard against unrecognised kinds and
    /// non-positive Rolling Hours BEFORE reaching here and fail closed on them.
    /// This method throws on those same misconfigurations rather than silently
    /// substituting a narrower span (e.g. a 1-hour Rolling window or a zero-width
    /// default), so a future caller that skips the guards fails loudly instead of
    /// undercounting usage and loosening the gate — consistent with the
    /// fail-closed contract in <c>docs/operating/budgets.md</c>.
    /// </summary>
    private static (DateTimeOffset From, DateTimeOffset To, DateTimeOffset? Reset) WindowBounds(
        AgentBudgetWindowOptions w, DateTimeOffset now)
    {
        switch (w.Kind)
        {
            case BudgetWindowKind.Rolling:
                {
                    if (w.Hours is not { } h || h <= 0)
                        throw new ArgumentException(
                            $"Rolling window requires positive Hours; got {w.Hours?.ToString() ?? "null"}.", nameof(w));
                    return (now.AddHours(-h), now, null);
                }
            case BudgetWindowKind.Weekly:
                {
                    var date = now.UtcDateTime.Date;
                    var daysSinceMonday = ((int)date.DayOfWeek + 6) % 7;
                    var start = new DateTimeOffset(date.AddDays(-daysSinceMonday), TimeSpan.Zero);
                    var end = start.AddDays(7);
                    return (start, end, end);
                }
            case BudgetWindowKind.Monthly:
                {
                    var d = now.UtcDateTime;
                    var start = new DateTimeOffset(new DateTime(d.Year, d.Month, 1, 0, 0, 0, DateTimeKind.Utc), TimeSpan.Zero);
                    var end = start.AddMonths(1);
                    return (start, end, end);
                }
            default:
                throw new ArgumentOutOfRangeException(nameof(w), w.Kind, "Unrecognised budget window kind.");
        }
    }

    private static long CentsToCostMicroCents(double cents) =>
        (long)decimal.Round((decimal)cents * AgentUsageEvent.CostMicroCentsPerCent, 0, MidpointRounding.AwayFromZero);

    private static long CostMicroCentsToRoundedCents(long costMicroCents) =>
        (long)decimal.Round((decimal)costMicroCents / AgentUsageEvent.CostMicroCentsPerCent, 0, MidpointRounding.AwayFromZero);

    private sealed record BudgetComputation(AgentQuotaSnapshot Snapshot, IReadOnlyList<BudgetWindowUsage> Windows);
}
