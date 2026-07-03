using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

public static class QuotaFailureClassifierStoreExtensions
{
    public static async Task RecordIfQuotaFailureAsync(
        this IQuotaFailureClassifier classifier,
        IQuotaFailureStore? store,
        AgentKind agent,
        string? modelId,
        string? summary,
        string? stderr,
        DateTimeOffset observedAt,
        TimeSpan retention,
        CancellationToken ct,
        ProjectId? projectId = null,
        string? stdout = null,
        bool bypassExitedSummaryGuard = false)
    {
        ArgumentNullException.ThrowIfNull(classifier);

        if (store is null)
            return;

        // The summary guard exists so an infrastructure exit-1 (e.g. failed auth
        // materialisation) is never recorded as a provider quota signal. Callers
        // that have already positively confirmed a quota block from a side-channel
        // (the exit-0 give-up where the run "succeeded" with summary "ok", so the
        // guard would otherwise drop a real 429) opt out via bypassExitedSummaryGuard.
        if (!bypassExitedSummaryGuard && !IsAgentExited1Summary(summary))
            return;

        var detection = classifier.Detect(agent, stderr, stdout);
        if (detection is null)
            return;

        if (projectId is { } scopedProject)
            await store.RecordForProjectAsync(agent, modelId, scopedProject, detection.Kind, observedAt, ct);
        else
            await store.RecordAsync(agent, modelId, detection.Kind, observedAt, ct);

        await store.PruneOlderThanAsync(observedAt - retention, ct);
    }

    /// <summary>
    /// Matches the "agent exited 1" guard shape, allowing a single appended
    /// diagnostic tail of the form <c>"agent exited 1: &lt;stderr-fragment&gt;"</c>.
    /// Provider runners (notably <c>GeminiAgentRunner</c>) now enrich the
    /// summary so operators can tell quota from auth from transport without
    /// reading the audit log; the persistent observed-failure store still
    /// needs to recognise those summaries as exit-1 failures, otherwise the
    /// next pickup wouldn't skip a Gemini member that just exhausted quota
    /// and the iteration would be burned re-discovering exhaustion.
    /// Other failure shapes (e.g. <c>"failed to materialise gemini auth: exit 1"</c>)
    /// remain excluded - they are infrastructure failures, not provider quota
    /// signals.
    /// </summary>
    internal static bool IsAgentExited1Summary(string? summary)
    {
        if (string.IsNullOrEmpty(summary)) return false;
        var trimmed = summary.Trim();
        const string Base = "agent exited 1";
        if (string.Equals(trimmed, Base, StringComparison.OrdinalIgnoreCase))
            return true;
        return trimmed.Length > Base.Length
            && trimmed.StartsWith(Base, StringComparison.OrdinalIgnoreCase)
            && trimmed[Base.Length] == ':';
    }
}
