using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Tuning knobs for the colocated executor — the in-process executor host
/// that gives a single-host deployment working local phase execution with no
/// configuration. Bound under <c>CodeyBox:ColocatedExecutor</c>. Every value
/// carries a safe default so an operator who configures nothing still gets
/// working local execution; the whole record is hot-reloadable through a
/// delegate accessor so an edit lands on the next dispatch without restart.
/// </summary>
public sealed class ColocatedExecutorOptions
{
    /// <summary>Config section path.</summary>
    public const string SectionName = "CodeyBox:ColocatedExecutor";

    /// <summary>
    /// Host-local sandbox capacity, mirroring
    /// <c>ExecutorOptions.MaxConcurrentSandboxes</c>. Null means uncapped.
    /// Zero means the colocated host registers but is never selected — local
    /// dispatches then defer under backoff rather than fail.
    /// </summary>
    public int? MaxConcurrentSandboxes { get; set; }

    /// <summary>
    /// Root under which the colocated transport stages the phase's single
    /// bare repo, one leaf per <c>RepositoryId</c>. Empty means a process-temp
    /// subdirectory reserved for the colocated host (never shared with a
    /// remote executor's staging root on the same machine). Must be absolute
    /// when set.
    /// </summary>
    public string StagingRoot { get; set; } = string.Empty;

    /// <summary>
    /// Agent credential sets the colocated host declares. The default
    /// <c>"*"</c> means it holds whatever the orchestrator itself holds —
    /// the colocated host runs with the orchestrator's own credential chain,
    /// so narrowing this only ever refuses work. Entries are opaque names
    /// matched by exact ordinal equality (see
    /// <see cref="ExecutorEligibility.HoldsCredential"/>).
    /// </summary>
    public List<string> DeclaredCredentials { get; set; } = ["*"];

    /// <summary>
    /// Clearance tags the colocated host is trusted to handle, in the same
    /// vocabulary as <c>WorkItem.RequiredCapabilities</c>. Well-known tags
    /// the local provider does not implement are dropped at registration
    /// (see <see cref="ExecutorCapabilityPolicy"/>); non-well-known tags
    /// pass through. Empty means the colocated host takes only phases with
    /// no required capabilities — capability-bearing work then waits for a
    /// remote host declaring the tag.
    /// </summary>
    public List<string> DeclaredCapabilities { get; set; } = [];

    /// <summary>
    /// Image reference stamped on sandbox specs the colocated runner
    /// provisions. Empty means the provider default.
    /// </summary>
    public string PhaseSandboxImageReference { get; set; } = string.Empty;

    /// <summary>
    /// Maximum completed phase results the colocated runner's replay guard
    /// keeps; oldest-completed evicted first, in-flight never evicted.
    /// </summary>
    public int MaxCachedPhaseResults { get; set; } = 1024;

    /// <summary>How long the colocated runner replays a completed phase result on redelivery.</summary>
    public TimeSpan PhaseResultCacheTtl { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Fails fast on misconfiguration so a bad bound surfaces at startup or
    /// on the next dispatch instead of silently admitting unbounded work.
    /// </summary>
    public void Validate()
    {
        if (MaxConcurrentSandboxes is { } cap
            && (cap < 0 || cap > ExecutorRegistration.MaxDeclaredCapacity))
            throw new InvalidOperationException(
                $"CodeyBox:ColocatedExecutor:MaxConcurrentSandboxes must be between 0 and {ExecutorRegistration.MaxDeclaredCapacity}.");
        ValidateEntries(DeclaredCredentials, nameof(DeclaredCredentials));
        ValidateEntries(DeclaredCapabilities, nameof(DeclaredCapabilities));
        if (!string.IsNullOrWhiteSpace(StagingRoot))
        {
            if (StagingRoot.Trim().Length > ExecutorOptions.MaxPhaseStagingRootLength)
                throw new InvalidOperationException(
                    $"CodeyBox:ColocatedExecutor:StagingRoot must be at most {ExecutorOptions.MaxPhaseStagingRootLength} characters.");
            if (StagingRoot.Any(char.IsControl))
                throw new InvalidOperationException("CodeyBox:ColocatedExecutor:StagingRoot must not contain control characters.");
            if (!Path.IsPathFullyQualified(StagingRoot.Trim()))
                throw new InvalidOperationException("CodeyBox:ColocatedExecutor:StagingRoot must be an absolute path.");
        }
        if (!string.IsNullOrEmpty(PhaseSandboxImageReference))
        {
            if (PhaseSandboxImageReference.Length > ExecutorOptions.MaxPhaseSandboxImageReferenceLength)
                throw new InvalidOperationException(
                    $"CodeyBox:ColocatedExecutor:PhaseSandboxImageReference must be at most {ExecutorOptions.MaxPhaseSandboxImageReferenceLength} characters.");
            if (PhaseSandboxImageReference.Any(char.IsControl))
                throw new InvalidOperationException("CodeyBox:ColocatedExecutor:PhaseSandboxImageReference must not contain control characters.");
        }
        if (MaxCachedPhaseResults < 1 || MaxCachedPhaseResults > ExecutorOptions.MaxCachedPhaseResultsLimit)
            throw new InvalidOperationException(
                $"CodeyBox:ColocatedExecutor:MaxCachedPhaseResults must be between 1 and {ExecutorOptions.MaxCachedPhaseResultsLimit}.");
        if (PhaseResultCacheTtl <= TimeSpan.Zero)
            throw new InvalidOperationException("CodeyBox:ColocatedExecutor:PhaseResultCacheTtl must be positive.");
    }

    private static void ValidateEntries(List<string> entries, string fieldName)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count > ExecutorRegistration.MaxDeclaredEntries)
            throw new InvalidOperationException(
                $"CodeyBox:ColocatedExecutor:{fieldName} may contain at most {ExecutorRegistration.MaxDeclaredEntries} entries.");
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry))
                throw new InvalidOperationException($"CodeyBox:ColocatedExecutor:{fieldName} entries must be non-empty.");
            if (entry.Trim().Length > ExecutorRegistration.MaxDeclaredEntryLength)
                throw new InvalidOperationException(
                    $"CodeyBox:ColocatedExecutor:{fieldName} entries must be at most {ExecutorRegistration.MaxDeclaredEntryLength} characters.");
        }
    }
}
