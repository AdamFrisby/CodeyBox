namespace CodeyBox.Core;

/// <summary>
/// Token usage and estimated cost for a single iteration of a work item.
/// Returned on the API and webhook surfaces as the <c>usage</c> block.
/// Token counts are <see langword="long"/> so multi-row aggregates can exceed
/// <see cref="int.MaxValue"/> without silent overflow (a single agent CLI call
/// already returns up to ~10⁷ tokens, and busy work items accumulate many calls).
/// </summary>
public sealed record WorkItemIterationUsage(
    int Iteration,
    long TokensInput,
    long TokensOutput,
    long TokensReasoning,
    long TokensCached,
    double CostUsd,
    long ElapsedMs);

/// <summary>
/// Cumulative token usage and estimated cost across every iteration of a work item.
/// Returned on the API and webhook surfaces as the <c>usageTotal</c> block.
/// Token counts are <see langword="long"/>; see <see cref="WorkItemIterationUsage"/>.
/// </summary>
public sealed record WorkItemUsageTotal(
    long TokensInput,
    long TokensOutput,
    long TokensReasoning,
    long TokensCached,
    double CostUsd,
    long ElapsedMs);

/// <summary>
/// Per-iteration plus aggregate split for a work item's recorded cost. Returned by
/// <see cref="IWorkItemCostStore.SummariseAsync"/>; null when no cost rows exist.
/// </summary>
public sealed record WorkItemUsageSummary(
    WorkItemIterationUsage Iteration,
    WorkItemUsageTotal Total);
