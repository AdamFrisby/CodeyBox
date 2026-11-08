using CodeyBox.Core;
using CodeyBox.Sandbox;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Hot-reloadable knobs for <see cref="BaselineMigrationService"/>.
/// </summary>
public sealed class BaselineMigrationOptions
{
    /// <summary>
    /// Maximum number of pinned work items inspected in a single migrate call.
    /// Bounds the scan and the write transaction so a large backlog cannot
    /// buffer unboundedly or hold the shared write gate for an unbounded span.
    /// When more candidates exist than this cap, the call migrates the first
    /// <see cref="MaxItemsPerScan"/> (ordered by created time) and reports
    /// <c>truncated=true</c>; because the operation is idempotent, the operator
    /// simply re-runs to continue. Must be &gt;= 1.
    /// </summary>
    public int MaxItemsPerScan { get; set; } = 5000;
}

/// <summary>
/// Migrates in-flight work items onto the current-config baseline by clearing
/// their per-item baseline pin (<see cref="WorkItem.BaselineImageRef"/>), so
/// their next pickup recomputes the ref from live config (new CLI/model). The
/// clear is performed through <see cref="IWorkItemStore.ClearBaselinePinsAsync"/>
/// — one bounded transaction under the shared SQLite write gate — so it
/// serializes with the dispatch loop and can neither corrupt state nor deadlock.
///
/// <para>Actively-running items are unaffected until their next pickup: clearing
/// the pin does not disturb the current run. Items already on the current-config
/// baseline are left untouched, making the operation idempotent.</para>
///
/// <para>Terminal items are deliberately NOT migrated: a terminal pin is the
/// historical record of the baseline the finished attempt ran on, and the
/// retry path (<see cref="WorkItemRetrier"/>) re-validates the pin against the
/// item's current route before re-queueing, so a stale terminal pin self-heals
/// at exactly the moment it could do harm. Excluding them also keeps this
/// scan proportional to the live backlog instead of every item that ever
/// pinned. They are still counted and reported as
/// <see cref="BaselineMigrationResult.SkippedTerminalCount"/> so the excluded
/// set stays legible to the operator.</para>
///
/// <para>The current-config ref is resolved exactly as
/// <c>OrchestratorService</c> does at pickup — via
/// <see cref="IBaselineImageResolver.ResolveBaselineRef"/> with the project's
/// work profile and <see cref="SandboxProfileFlavor.Headless"/> — so the
/// "already current" comparison and the reported recompute target match what a
/// real pickup would produce.</para>
/// </summary>
public sealed class BaselineMigrationService
{
    private readonly IWorkItemStore _store;
    private readonly IBaselineImageResolver _resolver;
    private readonly IProjectRepository _projects;
    private readonly TimeProvider _time;
    private readonly Func<BaselineMigrationOptions> _optsAccessor;
    private readonly Func<BaselineStalePinOptions> _stalePinOptions;
    private readonly ILogger<BaselineMigrationService> _log;

    public BaselineMigrationService(
        IWorkItemStore store,
        IBaselineImageResolver resolver,
        IProjectRepository projects,
        TimeProvider time,
        Func<BaselineMigrationOptions> optsAccessor,
        ILogger<BaselineMigrationService> log,
        Func<BaselineStalePinOptions>? stalePinOptions = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _projects = projects ?? throw new ArgumentNullException(nameof(projects));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _optsAccessor = optsAccessor ?? throw new ArgumentNullException(nameof(optsAccessor));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _stalePinOptions = stalePinOptions ?? (static () => new BaselineStalePinOptions());
    }

    /// <summary>
    /// Clears the baseline pin for every non-terminal work item that matches
    /// <paramref name="filter"/> and is not already on its project's
    /// current-config baseline. Returns a summary of how many items were
    /// migrated and which refs they will recompute to.
    /// </summary>
    public async Task<BaselineMigrationResult> MigrateAsync(
        BaselineMigrationFilter filter,
        CancellationToken ct = default)
    {
        var max = Math.Max(1, _optsAccessor().MaxItemsPerScan);

        // Request one extra row so we can tell "exactly at the cap" from
        // "more remain" without a second COUNT query.
        var candidates = await _store.ListNonTerminalBaselinePinnedAsync(
            filter.ProjectId, filter.BaselineImageRef, max + 1, ct).ConfigureAwait(false);

        var truncated = candidates.Count > max;
        if (truncated)
            candidates = candidates.Take(max).ToList();

        var currentRefByProject = await BuildCurrentRefMapAsync(candidates, ct).ConfigureAwait(false);
        var plan = BaselineMigrationPlanner.Plan(candidates, filter, currentRefByProject);

        // Terminal pinned items are excluded from the candidate scan above by
        // design (see class docstring), but they are exactly the delta an
        // operator needs to explain "scanned" vs the visible backlog — count
        // them under the same scope so the result can report why the
        // difference exists.
        var skippedTerminal = await _store.CountTerminalBaselinePinnedAsync(
            filter.ProjectId, filter.BaselineImageRef, ct).ConfigureAwait(false);

        var migrated = plan.ItemIdsToClear.Count == 0
            ? 0
            : await _store.ClearBaselinePinsAsync(plan.ItemIdsToClear, _time.GetUtcNow(), ct).ConfigureAwait(false);

        AuditLog.BaselineMigrated(
            filter.ProjectId?.Value,
            filter.BaselineImageRef,
            candidates.Count,
            migrated,
            plan.AlreadyCurrentCount,
            skippedTerminal,
            truncated);

        // A config change that moves the toolchain under pinned items is the
        // incident shape (old baked CLI + new model): name it explicitly so
        // the operator sees WHY the migration mattered, not just the counts.
        // Compares the migrated pins' toolchain fingerprints against the live
        // refs they will recompute to; legacy pins with no fingerprint count
        // as drifted when their ref differs (provenance unverifiable).
        var toolchainDrifted = CountToolchainDrift(candidates, plan.ItemIdsToClear, currentRefByProject);
        if (toolchainDrifted > 0)
        {
            _log.LogWarning(
                "Baseline migration: {Drifted} of {Migrated} migrated pin(s) bake a stale toolchain (CLI upgrade under in-flight items). " +
                "Items whose dispatch model also changed were running a model their baked CLI may not know; " +
                "the pickup-time stale-pin policy ({Mode}) now migrates that combination automatically.",
                toolchainDrifted, migrated, _stalePinOptions().Mode);
        }

        if (truncated)
        {
            _log.LogInformation(
                "Baseline migration hit the per-scan cap of {Max}; {Migrated} item(s) migrated this pass. Re-run to continue (idempotent).",
                max, migrated);
        }

        return new BaselineMigrationResult(
            migrated,
            candidates.Count,
            plan.AlreadyCurrentCount,
            skippedTerminal,
            truncated,
            plan.RecomputeTargets);
    }

    /// <summary>
    /// Advisory check behind <c>GET /baselines</c> and
    /// <c>GET /admin/baseline-images</c>: scans the same non-terminal pinned
    /// candidate set as <see cref="MigrateAsync"/> (same per-scan cap) and
    /// reports the pins whose baked toolchain differs from the live config's.
    /// Those are the items whose CLI/model combination may be inconsistent
    /// after a config change — the pickup-time stale-pin policy migrates the
    /// dangerous ones automatically, but when the policy is
    /// <see cref="BaselineStalePinMode.Disabled"/> (or the model has not
    /// changed) this warning is the operator's only signal to run a manual
    /// migrate. Read-only: never clears anything. Bounded by
    /// <see cref="BaselineMigrationOptions.MaxItemsPerScan"/> like the write
    /// path.
    /// </summary>
    public async Task<BaselinePinConsistencyReport> CheckConsistencyAsync(CancellationToken ct = default)
    {
        var max = Math.Max(1, _optsAccessor().MaxItemsPerScan);
        var candidates = await _store.ListNonTerminalBaselinePinnedAsync(null, null, max + 1, ct).ConfigureAwait(false);

        var truncated = candidates.Count > max;
        if (truncated)
            candidates = candidates.Take(max).ToList();

        var currentRefByProject = await BuildCurrentRefMapAsync(candidates, ct).ConfigureAwait(false);
        var mode = _stalePinOptions().Mode;

        var stale = new List<BaselineToolchainStalePin>();
        foreach (var candidate in candidates)
        {
            var live = currentRefByProject.GetValueOrDefault(candidate.ProjectId);
            if (!IsToolchainStale(candidate.BaselineImageRef, live))
                continue;
            stale.Add(new BaselineToolchainStalePin(
                candidate.Id, candidate.ProjectId, candidate.BaselineImageRef, candidate.BaselineImageModelId, live));
        }

        string? warning = stale.Count == 0
            ? null
            : $"{stale.Count} pinned work item(s) are stale: their baseline ref differs from the live config's " +
              "(stale toolchain, or a legacy pin whose provenance is unverifiable). A config change such as an " +
              "agent CLI upgrade moved the baseline under them; items whose dispatch model also changed run a " +
              "combination their baked CLI may not know. Pickup-time auto-migration is " +
              $"{DescribeMode(mode)}; run POST /baselines/migrate to move them now.";

        return new BaselinePinConsistencyReport(candidates.Count, stale.Count, truncated, warning, stale);
    }

    private static string DescribeMode(BaselineStalePinMode mode) => mode switch
    {
        BaselineStalePinMode.Disabled => "DISABLED",
        BaselineStalePinMode.MigrateOnToolchainChange => "on (migrates any toolchain drift at pickup)",
        _ => "on (migrates toolchain drift combined with a model change at pickup)",
    };

    /// <summary>
    /// True when <paramref name="pinnedRef"/> was baked from different
    /// toolchain inputs than <paramref name="liveRef"/> resolves now. Scoped
    /// pins compare fingerprints; legacy pins (or a missing live ref) degrade
    /// to full-string comparison — an unverifiable pin counts as stale when
    /// its ref differs.
    /// </summary>
    internal static bool IsToolchainStale(string pinnedRef, string? liveRef)
    {
        if (string.IsNullOrEmpty(pinnedRef))
            return false;
        if (liveRef is null)
            return true;
        if (string.Equals(pinnedRef, liveRef, StringComparison.Ordinal))
            return false;
        var pinnedHash = BaselinePin.TryExtractToolchainHash(pinnedRef);
        var liveHash = BaselinePin.TryExtractToolchainHash(liveRef);
        if (pinnedHash is null || liveHash is null)
            return true;
        return !string.Equals(pinnedHash, liveHash, StringComparison.Ordinal);
    }

    private static int CountToolchainDrift(
        IReadOnlyList<BaselinePinnedWorkItem> candidates,
        IReadOnlyList<WorkItemId> migratedIds,
        IReadOnlyDictionary<ProjectId, string?> currentRefByProject)
    {
        var migrated = new HashSet<WorkItemId>(migratedIds);
        var drifted = 0;
        foreach (var candidate in candidates)
        {
            if (!migrated.Contains(candidate.Id))
                continue;
            if (IsToolchainStale(candidate.BaselineImageRef, currentRefByProject.GetValueOrDefault(candidate.ProjectId)))
                drifted++;
        }
        return drifted;
    }

    private async Task<IReadOnlyDictionary<ProjectId, string?>> BuildCurrentRefMapAsync(
        IReadOnlyList<BaselinePinnedWorkItem> candidates,
        CancellationToken ct)
    {
        var map = new Dictionary<ProjectId, string?>();
        foreach (var projectId in candidates.Select(c => c.ProjectId).Distinct())
        {
            map[projectId] = SafeResolveForProject(await SafeGetProjectAsync(projectId, ct).ConfigureAwait(false));
        }
        return map;
    }

    private async Task<Project?> SafeGetProjectAsync(ProjectId projectId, CancellationToken ct)
    {
        try
        {
            return await _projects.GetAsync(projectId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Mirror SafeResolve: a throwing project lookup (e.g. config
            // validation failing while items still reference a removed
            // project) must not abort the operation. Treat as "no current
            // baseline" — affected items migrate to no-pin / report stale,
            // exactly as a failing pickup resolve would produce.
            _log.LogDebug(ex,
                "Project lookup threw while planning baseline migration for project {ProjectId}; treating as no current baseline",
                projectId.Value);
            return null;
        }
    }

    private string? SafeResolveForProject(Project? project) =>
        project is null ? null : SafeResolve(project.NetworkProfiles.Work);

    private string? SafeResolve(string? workProfile)
    {
        try
        {
            return _resolver.ResolveBaselineRef(workProfile, SandboxProfileFlavor.Headless);
        }
        catch (Exception ex)
        {
            // Mirror OrchestratorService.ResolveBaselineRefForPickup: a throwing
            // resolver must not abort the operation. Treat as "no current
            // baseline" — the affected items then migrate to no-pin, which is
            // exactly what a failing pickup resolve would also produce.
            _log.LogDebug(ex,
                "Baseline-ref resolver threw while planning migration for profile {Profile}; treating as no current baseline",
                workProfile ?? "(none)");
            return null;
        }
    }
}
