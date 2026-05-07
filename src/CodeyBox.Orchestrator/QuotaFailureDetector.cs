using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

public static class QuotaFailureDetector
{
    private static readonly (string Pattern, QuotaFailureKind Kind)[] Patterns =
    [
        ("hit your usage limit", QuotaFailureKind.LimitReached),
        ("hit your limit", QuotaFailureKind.LimitReached),
        ("rate_limit_exceeded", QuotaFailureKind.RateLimitExceeded),
        ("API Error: 401", QuotaFailureKind.Unauthorized),
    ];

    public static QuotaFailureKind? Detect(string? stderr)
    {
        if (string.IsNullOrEmpty(stderr))
            return null;

        foreach (var (pattern, kind) in Patterns)
        {
            if (stderr.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                return kind;
        }

        return null;
    }

    public static async Task RecordIfQuotaFailureAsync(
        IQuotaFailureStore? store,
        AgentKind agent,
        string? modelId,
        string? summary,
        string? stderr,
        DateTimeOffset observedAt,
        TimeSpan retention,
        CancellationToken ct,
        ProjectId? projectId = null)
    {
        if (store is null)
            return;

        if (!string.Equals(summary?.Trim(), "agent exited 1", StringComparison.OrdinalIgnoreCase))
            return;

        var kind = Detect(stderr);
        if (kind is null)
            return;

        if (projectId is { } scopedProject)
            await store.RecordForProjectAsync(agent, modelId, scopedProject, kind.Value, observedAt, ct);
        else
            await store.RecordAsync(agent, modelId, kind.Value, observedAt, ct);

        await store.PruneOlderThanAsync(observedAt - retention, ct);
    }
}
