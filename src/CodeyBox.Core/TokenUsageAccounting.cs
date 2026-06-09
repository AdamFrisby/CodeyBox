namespace CodeyBox.Core;

/// <summary>
/// Shared helpers for accounting across the split between normal-rate input
/// and cached input.
/// </summary>
public static class TokenUsageAccounting
{
    public static int FreshInputTokens(int totalInputTokens, int cachedInputTokens)
    {
        var total = Math.Max(0, totalInputTokens);
        var cached = Math.Max(0, cachedInputTokens);
        return Math.Max(0, total - cached);
    }

    public static long TotalInputTokens(long inputTokens, long cachedInputTokens)
        => Math.Max(0, inputTokens) + Math.Max(0, cachedInputTokens);

    public static long TotalInputTokens(WorkItemCost row)
        => TotalInputTokens(row.InputTokens, row.CachedInputTokens);
}
