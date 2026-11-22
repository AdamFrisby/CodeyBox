namespace CodeyBox.Upstream.GitHub;

/// <summary>
/// Tuning for <see cref="GitHubApiBudgetTracker"/> — the shared GitHub REST
/// request budget observed from <c>x-ratelimit-*</c>/<c>Retry-After</c>
/// response headers across every caller of the <c>github-upstream</c> HTTP
/// client.
///
/// <para>Bind under <c>CodeyBox:GitHubApiBudget</c>. Read live through
/// <c>IOptionsMonitor</c>, so edits hot-reload without a restart.</para>
/// </summary>
public sealed class GitHubApiBudgetOptions
{
    /// <summary>
    /// Number of remaining primary-rate-limit requests at or below which
    /// latency-tolerant background reads (stale-PR sweeps, redrive detection,
    /// dossier/status enrichment) defer so pipeline-critical calls (PR
    /// create, merge, push-related checks) always have budget. The reserve is
    /// per credential bucket, matching GitHub's per-token accounting.
    ///
    /// <para>Values below 0 are treated as 0 (no reserve — background reads
    /// proceed until the limit is actually exhausted). Default 500, roughly
    /// 10% of the 5,000 requests/hour primary budget.</para>
    /// </summary>
    public int BackgroundReserveRemaining { get; set; } = 500;
}
