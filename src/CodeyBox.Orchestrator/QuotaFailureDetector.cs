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
        string? stderr,
        DateTimeOffset observedAt,
        TimeSpan retention,
        CancellationToken ct)
    {
        if (store is null)
            return;

        var kind = Detect(stderr);
        if (kind is null)
            return;

        await store.RecordAsync(agent, modelId, kind.Value, observedAt, ct);
        await store.PruneOlderThanAsync(observedAt - retention, ct);
    }
}
