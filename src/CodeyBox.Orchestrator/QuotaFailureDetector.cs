using System.Text.RegularExpressions;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

public sealed record QuotaDetection(QuotaFailureKind Kind, DateTimeOffset? ResetAt = null);

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

    private static readonly Regex ResetAfterRegex = new(@"reset after\s+(?:(\d+)\s*h)?\s*(?:(\d+)\s*m)?\s*(?:(\d+)\s*s)?", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static QuotaDetection? Detect(string? stderr, string? stdout = null)
    {
        // Some agents (e.g. codex CLI) emit quota errors to stdout as
        // structured JSON events, not stderr. Inspect both streams.
        if (string.IsNullOrEmpty(stderr) && string.IsNullOrEmpty(stdout))
            return null;

        foreach (var (pattern, kind) in Patterns)
        {
            var inStderr = !string.IsNullOrEmpty(stderr) && stderr.Contains(pattern, StringComparison.OrdinalIgnoreCase);
            var inStdout = !string.IsNullOrEmpty(stdout) && stdout.Contains(pattern, StringComparison.OrdinalIgnoreCase);

            if (inStderr || inStdout)
            {
                DateTimeOffset? resetAt = null;
                // Prefer stderr for reset time as most agents use it, but fall back to stdout.
                var source = !string.IsNullOrEmpty(stderr) ? stderr : stdout;
                if (!string.IsNullOrEmpty(source))
                {
                    var match = ResetAfterRegex.Match(source);
                    if (match.Success)
                    {
                        var h = 0;
                        var m = 0;
                        var s = 0;

                        if (match.Groups[1].Success && int.TryParse(match.Groups[1].Value, out var hv)) h = Math.Min(hv, 10_000);
                        if (match.Groups[2].Success && int.TryParse(match.Groups[2].Value, out var mv)) m = Math.Min(mv, 10_000);
                        if (match.Groups[3].Success && int.TryParse(match.Groups[3].Value, out var sv)) s = Math.Min(sv, 10_000);

                        if (h > 0 || m > 0 || s > 0)
                        {
                            resetAt = DateTimeOffset.UtcNow.Add(new TimeSpan(h, m, s));
                        }
                    }
                }
                return new QuotaDetection(kind, resetAt);
            }
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

        var detection = Detect(stderr, stdout);
        if (detection is null)
            return;

        if (projectId is { } scopedProject)
            await store.RecordForProjectAsync(agent, modelId, scopedProject, detection.Kind, observedAt, ct);
        else
            await store.RecordAsync(agent, modelId, detection.Kind, observedAt, ct);

        await store.PruneOlderThanAsync(observedAt - retention, ct);
    }
}
