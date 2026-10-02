namespace CodeyBox.Core;

/// <summary>
/// Hot-reloadable options for post-merge per-test coverage baseline
/// production, bound from the <c>Audit:TestSelection:BaselineProduction</c>
/// configuration section via <c>IOptionsMonitor</c>. Every operational value
/// (the kill-switch, the sandboxed-job timeout, host-side retention, and the
/// guest producer binary) is a knob, not a source literal.
/// </summary>
public sealed class TestSelectionBaselineProductionOptions
{
    public const string SectionName = "Audit:TestSelection:BaselineProduction";

    /// <summary>
    /// Global kill-switch. When false, merges schedule nothing and a pending
    /// queue drains without executing. Default true; per-project opt-in
    /// (<see cref="Project.TestSelectionBaselineEnabled"/>) still applies.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Wall-clock budget for one sandboxed production job, covering sandbox
    /// creation, the base checkout, and the per-test coverlet collection.
    /// Default 1 hour; the producer itself is a post-merge host job outside
    /// any per-item audit budget.
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// How many baselines per project are retained host-side (newest first).
    /// Older commits are evicted on every store. Default 5.
    /// </summary>
    public int MaxRetainedPerProject { get; set; } = 5;

    /// <summary>
    /// Guest binary producing the baseline inside the job sandbox
    /// (<c>produce --repo &lt;checkout&gt; --output &lt;baseline.json&gt;</c>).
    /// Baked into the audit baseline image and resolved on its PATH by
    /// default; an absolute guest path pins a custom location. The job never
    /// falls back to host-side execution: building a merged checkout runs
    /// repo-authored build logic (untrusted), so production stays in a VM.
    /// </summary>
    public string ProducerBinary { get; set; } = "codeybox-test-selection-baseline";

    /// <summary>
    /// How long the production service waits before re-checking a saturated
    /// pool (global sandbox/worker caps full) for capacity. The pending job
    /// stays queued — newest commit still wins — while work-item phases keep
    /// their permits. Default 30 seconds.
    /// </summary>
    public TimeSpan SaturatedPoolRecheckDelay { get; set; } = TimeSpan.FromSeconds(30);

    public static bool IsValid(TestSelectionBaselineProductionOptions? options)
    {
        if (options is null)
            return false;
        if (options.Timeout <= TimeSpan.Zero)
            return false;
        if (options.MaxRetainedPerProject <= 0)
            return false;
        if (string.IsNullOrWhiteSpace(options.ProducerBinary)
            || options.ProducerBinary.IndexOf('\0') >= 0)
            return false;
        // A non-positive recheck delay would busy-spin the production loop
        // against a saturated pool; fail fast at config load instead.
        if (options.SaturatedPoolRecheckDelay <= TimeSpan.Zero)
            return false;
        return true;
    }
}

/// <summary>
/// One host-side stored baseline: the produced artifact plus the bookkeeping
/// the status endpoint and the audit-staging selector need. The JSON is the
/// exact bytes the producer wrote (bounded by the consumer size caps before
/// buffering); failures are recorded separately via
/// <see cref="TestSelectionBaselineFailure"/> so a failed run never evicts a
/// good baseline.
/// </summary>
public sealed record TestSelectionBaselineStored(
    string ProjectId,
    string Commit,
    DateTimeOffset ProducedAtUtc,
    string Json,
    long SizeBytes,
    int TestCount);

/// <summary>
/// The last production failure for a project: reported through the status
/// endpoint and the structured event, but never staged and never blocking
/// merges or audits (fail-safe: the selector falls back to the full suite).
/// </summary>
public sealed record TestSelectionBaselineFailure(
    string ProjectId,
    string Commit,
    DateTimeOffset OccurredAtUtc,
    string Error,
    TimeSpan Duration);

/// <summary>
/// Details payload for the <c>test-selection.baseline_produced</c> and
/// <c>test-selection.baseline_failed</c> events. Emitted when a post-merge
/// production job finishes — successfully (with size/test counts) or as a
/// reported failure that blocks nothing. Receivers track calibration
/// progress (how many fresh baselines exist) from these events.
/// </summary>
public sealed record TestSelectionBaselineProducedDetails
{
    public required string ProjectId { get; init; }
    public required string Commit { get; init; }
    public required bool Success { get; init; }
    public long SizeBytes { get; init; }
    public int TestCount { get; init; }
    public long DurationMs { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// Host-side store for produced baselines, keyed by project + commit with
/// bounded per-project retention. A production failure is recorded alongside
/// (for status/event observability) but never evicts a good baseline and
/// never throws to merge or audit callers.
/// </summary>
public interface ITestSelectionBaselineStore
{
    Task StoreAsync(
        string projectId,
        string commit,
        DateTimeOffset producedAtUtc,
        string json,
        int testCount,
        TimeSpan duration,
        int maxRetainedPerProject,
        CancellationToken ct = default);

    Task RecordFailureAsync(
        string projectId,
        string commit,
        string error,
        TimeSpan duration,
        CancellationToken ct = default);

    Task<TestSelectionBaselineStored?> GetAsync(
        string projectId,
        string commit,
        CancellationToken ct = default);

    Task<IReadOnlyList<TestSelectionBaselineStored>> ListAsync(
        string projectId,
        CancellationToken ct = default);

    Task<TestSelectionBaselineFailure?> GetLastFailureAsync(
        string projectId,
        CancellationToken ct = default);
}

/// <summary>
/// One scheduled post-merge production job: the project whose base moved,
/// the host bare-repo id captured at merge time, the base branch, and the
/// merge commit that triggered the schedule (a coalescing hint — the runner
/// re-resolves the live base tip at execution so squash-merge divergence and
/// superseding merges converge on the freshest main).
/// </summary>
public sealed record TestSelectionBaselineRequest(
    string ProjectId,
    string RepositoryId,
    string BaseBranch,
    string Commit);

/// <summary>
/// Bounded per-project production queue. Concurrency is 1 per project: at
/// most one pending request per project, and a second merge before the job
/// starts overwrites the pending entry (newest commit wins — the superseded
/// job is dropped, never executed). Thread-safe; pure coordination state, no
/// I/O, so the supersede contract is unit-testable without a host.
/// </summary>
public sealed class TestSelectionBaselineScheduler
{
    private readonly object _sync = new();
    private readonly Dictionary<string, TestSelectionBaselineRequest> _pending =
        new(StringComparer.Ordinal);
    private readonly HashSet<string> _running = new(StringComparer.Ordinal);

    /// <summary>
    /// Schedules production for a merged commit. Overwrites any still-pending
    /// request for the project (supersede); a job already executing is never
    /// interrupted — the new request waits as the single pending entry.
    /// </summary>
    public void Schedule(TestSelectionBaselineRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_sync)
        {
            _pending[request.ProjectId] = request;
        }
    }

    /// <summary>
    /// Takes the next runnable request: a pending entry whose project has no
    /// job in flight. Marks the project running. Returns false when every
    /// pending project is already running or the queue is empty.
    /// </summary>
    public bool TryTake(out TestSelectionBaselineRequest? request)
    {
        lock (_sync)
        {
            foreach (var (projectId, pending) in _pending
                .OrderBy(kvp => kvp.Key, StringComparer.Ordinal))
            {
                if (_running.Contains(projectId))
                    continue;
                _pending.Remove(projectId);
                _running.Add(projectId);
                request = pending;
                return true;
            }
        }

        request = null;
        return false;
    }

    /// <summary>Releases the per-project running slot after the job settles.</summary>
    public void Complete(string projectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        lock (_sync)
        {
            _running.Remove(projectId);
        }
    }

    public string? GetPendingCommit(string projectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        lock (_sync)
        {
            return _pending.TryGetValue(projectId, out var pending) ? pending.Commit : null;
        }
    }

    public bool IsRunning(string projectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        lock (_sync)
        {
            return _running.Contains(projectId);
        }
    }

    public int PendingCount
    {
        get
        {
            lock (_sync)
            {
                return _pending.Count;
            }
        }
    }
}

/// <summary>
/// Pure staging policy: of the stored baselines for a project, the one to
/// place at <c>BaselineSandboxPath</c> is the NEWEST (by production time)
/// whose commit is an ancestor of the item's base tip AND whose age is within
/// <c>MaxBaselineAge</c>. Anything else — no ancestor, only stale candidates —
/// stages nothing and the selector falls back to the full suite (fail-safe).
/// Ancestry (not exact-commit equality) is what survives squash-merge
/// divergence: the measured base tip stays reachable from later main tips.
/// </summary>
public static class TestSelectionBaselineStaging
{
    private static readonly TimeSpan FutureTolerance = TimeSpan.FromMinutes(5);

    public static async Task<TestSelectionBaselineStored?> SelectNewestAncestorAsync(
        IReadOnlyList<TestSelectionBaselineStored> candidates,
        string baseCommit,
        Func<string, string, CancellationToken, Task<bool>> isAncestorAsync,
        DateTimeOffset nowUtc,
        TimeSpan maxAge,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseCommit);
        ArgumentNullException.ThrowIfNull(isAncestorAsync);
        if (maxAge <= TimeSpan.Zero)
            return null;

        foreach (var candidate in candidates
            .OrderByDescending(c => c.ProducedAtUtc)
            .ThenBy(c => c.Commit, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(candidate.Commit))
                continue;
            if (candidate.ProducedAtUtc > nowUtc + FutureTolerance)
                continue;
            if (nowUtc - candidate.ProducedAtUtc > maxAge)
                continue;
            bool ancestor;
            try
            {
                ancestor = await isAncestorAsync(candidate.Commit, baseCommit, ct).ConfigureAwait(false);
            }
            catch (Exception)
            {
                continue;
            }

            if (ancestor)
                return candidate;
        }

        return null;
    }
}

/// <summary>
/// Sandbox-path guard and argv builders for staging a baseline into an audit
/// sandbox. The sink is dangerous (an arbitrary filesystem path inside a
/// privileged VM image), so the path is validated AT the sink: absolute,
/// no parent escapes, no NUL bytes. Writes travel via
/// <c>tee -- &lt;path&gt;</c> over <c>SandboxExec.Stdin</c> (an argv array
/// with a <c>--</c> separator — never a concatenated shell string, consistent
/// with the credential-materialization seam) and the file is left read-only
/// (<c>chmod 444</c>) so the audited checkout cannot rewrite its own oracle.
/// </summary>
public static class TestSelectionBaselineStagingIO
{
    public static bool IsStageableSandboxPath(string? sandboxPath)
    {
        if (string.IsNullOrWhiteSpace(sandboxPath))
            return false;
        if (sandboxPath.IndexOf('\0') >= 0)
            return false;
        if (!sandboxPath.StartsWith('/'))
            return false;
        foreach (var segment in sandboxPath.Split('/'))
        {
            if (segment == "..")
                return false;
        }

        return true;
    }

    public static string ParentDirectory(string sandboxPath)
    {
        var index = sandboxPath.LastIndexOf('/');
        return index <= 0 ? "/" : sandboxPath[..index];
    }
}
