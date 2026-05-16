namespace CodeyBox.Core;

/// <summary>
/// Token usage and estimated cost for a single iteration of a work item.
/// Returned on the API and webhook surfaces as the <c>usage</c> block.
/// </summary>
public sealed record WorkItemIterationUsage(
    int Iteration,
    int TokensInput,
    int TokensOutput,
    int TokensReasoning,
    int TokensCached,
    double CostUsd,
    long ElapsedMs);

/// <summary>
/// Cumulative token usage and estimated cost across every iteration of a work item.
/// Returned on the API and webhook surfaces as the <c>usageTotal</c> block.
/// </summary>
public sealed record WorkItemUsageTotal(
    int TokensInput,
    int TokensOutput,
    int TokensReasoning,
    int TokensCached,
    double CostUsd,
    long ElapsedMs);

/// <summary>
/// Per-iteration plus aggregate split for a work item's recorded cost. Returned by
/// <see cref="IWorkItemCostStore.SummariseAsync"/>; null when no cost rows exist.
/// </summary>
public sealed record WorkItemUsageSummary(
    WorkItemIterationUsage Iteration,
    WorkItemUsageTotal Total);
