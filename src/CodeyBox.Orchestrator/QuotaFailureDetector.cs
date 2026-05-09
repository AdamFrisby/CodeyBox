using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

public static class QuotaFailureDetector
{
    private static readonly (string Pattern, QuotaFailureKind Kind)[] Patterns =
    [
        // Codex / ChatGPT
        ("hit your usage limit", QuotaFailureKind.LimitReached),
        ("hit your limit", QuotaFailureKind.LimitReached),
        // Anthropic / OpenAI rate limits
        ("rate_limit_exceeded", QuotaFailureKind.RateLimitExceeded),
        // Google / Gemini Code Assist
        ("RESOURCE_EXHAUSTED", QuotaFailureKind.RateLimitExceeded),
        ("exceeded the rate limit", QuotaFailureKind.RateLimitExceeded),
        ("quota exceeded", QuotaFailureKind.RateLimitExceeded),
        // Auth
        ("API Error: 401", QuotaFailureKind.Unauthorized),
    ];

    public static QuotaFailureKind? Detect(string? stderr, string? stdout = null)
    {
        // Some agents (e.g. codex CLI) emit quota errors to stdout as
        // structured JSON events, not stderr. Inspect both streams.
        if (string.IsNullOrEmpty(stderr) && string.IsNullOrEmpty(stdout))
            return null;

        foreach (var (pattern, kind) in Patterns)
        {
            if (!string.IsNullOrEmpty(stderr) && stderr.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                return kind;
            if (!string.IsNullOrEmpty(stdout) && stdout.Contains(pattern, StringComparison.OrdinalIgnoreCase))
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
        ProjectId? projectId = null,
        string? stdout = null)
    {
        if (store is null)
            return;

        if (!string.Equals(summary?.Trim(), "agent exited 1", StringComparison.OrdinalIgnoreCase))
            return;

        var kind = Detect(stderr, stdout);
        if (kind is null)
            return;

        if (projectId is { } scopedProject)
            await store.RecordForProjectAsync(agent, modelId, scopedProject, kind.Value, observedAt, ct);
        else
            await store.RecordAsync(agent, modelId, kind.Value, observedAt, ct);

        await store.PruneOlderThanAsync(observedAt - retention, ct);
    }
}
