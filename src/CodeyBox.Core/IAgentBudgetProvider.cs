namespace CodeyBox.Core;

/// <summary>Per-window usage detail surfaced by <see cref="IAgentBudgetProvider.SummariseAllAsync"/>.</summary>
public sealed record BudgetWindowUsage(
    string Kind,
    int? Hours,
    long UsedCents,
    long LimitCents,
    double PercentRemaining,
    DateTimeOffset? ResetAt);

/// <summary>All windows for one configured (agent, model) budget.</summary>
public sealed record AgentBudgetUsageView(
    string Agent,
    string Model,
    IReadOnlyList<BudgetWindowUsage> Windows);

/// <summary>
/// Computes synthetic quota from locally-accounted spend so the router can gate
/// proactively on operator budgets, the same way it gates on real probe
/// snapshots. Lives in Core alongside <see cref="IAgentQuotaProbe"/> and
/// <see cref="IAgentBurnEstimator"/> so host surfaces (e.g. the /quota endpoint)
/// depend on a Core abstraction rather than an Orchestrator implementation type.
/// </summary>
public interface IAgentBudgetProvider
{
    /// <summary>
    /// Returns a synthetic <see cref="AgentQuotaSnapshot"/> for the (agent, model)
    /// budget — <c>AvailablePct</c> = MIN(percent remaining) across windows,
    /// <c>ResetAt</c> = earliest window reset.
    /// <para>
    /// Returns <c>null</c> only when no budget is configured for that pair (the
    /// router then ignores the budget gate). When a budget <i>is</i> configured
    /// but spend cannot be verified (usage store unavailable or a window query
    /// fails), the snapshot fails closed — <c>AvailablePct</c> = 0 — so a
    /// configured spend cap gates dispatch rather than silently disabling
    /// protection during an infrastructure failure.
    /// </para>
    /// </summary>
    Task<AgentQuotaSnapshot?> GetBudgetSnapshotAsync(AgentKind agent, string? modelId, CancellationToken ct = default);

    /// <summary>Per-window usage for every configured budget, for the /quota endpoint.</summary>
    Task<IReadOnlyList<AgentBudgetUsageView>> SummariseAllAsync(CancellationToken ct = default);
}

/// <summary>
/// Companion to <see cref="IAgentBudgetProvider"/> for hot-reloading the budget
/// configuration. Kept separate so the host's reload coordinator depends on a
/// Core abstraction rather than the concrete Orchestrator calculator, and so the
/// many read-only <see cref="IAgentBudgetProvider"/> implementations (test fakes,
/// stubs) are not forced to carry a reload method they never use.
/// </summary>
public interface IAgentBudgetConfigReloadable
{
    /// <summary>Swaps the active budget options. Called by the hot-reload coordinator.</summary>
    void ApplyConfigReload(AgentBudgetOptions next);
}
