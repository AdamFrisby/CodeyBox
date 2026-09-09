using CodeyBox.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Background sweep and lifecycle reaper for per-work-item bare git repository clones.
/// Reaps the clone for a work item when it enters a terminal state (respecting the configured
/// retention grace period), and sweeps already-terminal clones left behind at startup
/// or across host restarts.
/// </summary>
public sealed class WorkItemRepoReaper : BackgroundService
{
    private readonly IGitHost _gitHost;
    private readonly IWorkItemStore _store;
    private readonly Func<RepoRetentionOptions> _optionsAccessor;
    private readonly ILogger<WorkItemRepoReaper> _log;
    private readonly TimeProvider _time;
    private readonly IWorkerRegistry? _workerRegistry;
    private readonly string? _repositoriesRoot;
    private readonly List<Func<WorkItemId, bool>> _activeItemChecks = [];
    private readonly SemaphoreSlim _sweepGate = new(1, 1);

    public WorkItemRepoReaper(
        IGitHost gitHost,
        IWorkItemStore store,
        RepoRetentionOptions options,
        ILogger<WorkItemRepoReaper> log,
        TimeProvider? time = null,
        IWorkerRegistry? workerRegistry = null,
        Func<WorkItemId, bool>? isItemActive = null,
        string? repositoriesRoot = null)
        : this(gitHost, store, () => options, log, time, workerRegistry, isItemActive, repositoriesRoot)
    {
    }

    public WorkItemRepoReaper(
        IGitHost gitHost,
        IWorkItemStore store,
        Func<RepoRetentionOptions> optionsAccessor,
        ILogger<WorkItemRepoReaper> log,
        TimeProvider? time = null,
        IWorkerRegistry? workerRegistry = null,
        Func<WorkItemId, bool>? isItemActive = null,
        string? repositoriesRoot = null)
    {
        _gitHost = gitHost ?? throw new ArgumentNullException(nameof(gitHost));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _optionsAccessor = optionsAccessor ?? throw new ArgumentNullException(nameof(optionsAccessor));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _time = time ?? TimeProvider.System;
        _workerRegistry = workerRegistry;
        _repositoriesRoot = repositoriesRoot;

        if (isItemActive is not null)
        {
            RegisterActiveItemCheck(isItemActive);
        }
    }

    /// <summary>
    /// Registers a delegate to verify if a work item is currently active on an in-process worker.
    /// Used by the orchestrator service to prevent sweeps from racing active workers.
    /// </summary>
    public void RegisterActiveItemCheck(Func<WorkItemId, bool> isItemActive)
    {
        ArgumentNullException.ThrowIfNull(isItemActive);
        lock (_activeItemChecks)
        {
            _activeItemChecks.Add(isItemActive);
        }
    }

    private bool IsItemActive(WorkItemId id)
    {
        lock (_activeItemChecks)
        {
            for (var i = 0; i < _activeItemChecks.Count; i++)
            {
                if (_activeItemChecks[i](id))
                    return true;
            }
        }
        return false;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var initialOpts = _optionsAccessor();
        if (initialOpts.Enabled)
        {
            try
            {
                _log.LogInformation("WorkItemRepoReaper: running initial startup sweep");
                await RunSweepAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "WorkItemRepoReaper: initial startup sweep failed");
            }
        }
        else
        {
            _log.LogInformation("WorkItemRepoReaper disabled via initial configuration; startup sweep skipped");
        }

        var interval = initialOpts.CheckInterval < TimeSpan.FromSeconds(1)
            ? TimeSpan.FromSeconds(1)
            : initialOpts.CheckInterval;

        using var timer = new PeriodicTimer(interval, _time);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            var opts = _optionsAccessor();
            if (!opts.Enabled)
                continue;

            var desiredInterval = opts.CheckInterval < TimeSpan.FromSeconds(1)
                ? TimeSpan.FromSeconds(1)
                : opts.CheckInterval;
            if (timer.Period != desiredInterval)
            {
                timer.Period = desiredInterval;
            }

            try
            {
                await RunSweepAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "WorkItemRepoReaper: periodic sweep failed");
            }
        }
    }

    /// <summary>
    /// Reaps the repository clone for a work item if it is in a terminal state,
    /// its retention grace period has elapsed, and it is not currently active on a worker.
    /// </summary>
    public async Task<bool> ReapWorkItemAsync(WorkItemId id, bool force = false, CancellationToken ct = default)
    {
        var opts = _optionsAccessor();
        if (!opts.Enabled)
            return false;

        if (IsItemActive(id))
            return false;

        if (_workerRegistry is not null)
        {
            var activeWorkers = await _workerRegistry.ListAsync(ct).ConfigureAwait(false);
            if (IsOwnedByRegisteredWorker(activeWorkers, id))
                return false;
        }

        var item = await _store.GetAsync(id, ct).ConfigureAwait(false);
        if (item is null)
            return false;

        if (!WorkItemStates.IsTerminal(item.State))
            return false;

        var now = _time.GetUtcNow();
        var grace = opts.GracePeriod < TimeSpan.Zero ? TimeSpan.Zero : opts.GracePeriod;
        var age = now - item.UpdatedAt;
        if (!force && grace > TimeSpan.Zero && age < grace)
            return false;

        if (IsItemActive(id))
            return false;

        if (_workerRegistry is not null)
        {
            var activeWorkers = await _workerRegistry.ListAsync(ct).ConfigureAwait(false);
            if (IsOwnedByRegisteredWorker(activeWorkers, id))
                return false;
        }

        await _gitHost.DisposeRepositoryAsync(id.ToString(), ct).ConfigureAwait(false);
        _log.LogInformation(
            "WorkItemRepoReaper: reaped clone for terminal work item {WorkItemId} (state={State}, age={Age}, grace={Grace})",
            id, item.State, age, grace);
        return true;
    }

    /// <summary>
    /// Best-effort reap used by worker completion and API close-out paths.
    /// Failures are logged and do not propagate to the caller.
    /// </summary>
    public async Task TryReapWorkItemAsync(WorkItemId id, CancellationToken ct = default)
    {
        try
        {
            await ReapWorkItemAsync(id, force: false, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "WorkItemRepoReaper: failed to reap clone for work item {WorkItemId}", id);
        }
    }

    /// <summary>
    /// Executes a full sweep over the repository root directory, reaping terminal work-item clones
    /// whose grace period has expired.
    /// </summary>
    public async Task<RepoReapSummary> RunSweepAsync(CancellationToken ct = default)
    {
        var opts = _optionsAccessor();
        if (!opts.Enabled)
        {
            return new RepoReapSummary();
        }

        if (!await _sweepGate.WaitAsync(0, ct).ConfigureAwait(false))
        {
            return new RepoReapSummary();
        }

        try
        {
            return await RunSweepCoreAsync(opts, ct).ConfigureAwait(false);
        }
        finally
        {
            _sweepGate.Release();
        }
    }

    private async Task<RepoReapSummary> RunSweepCoreAsync(RepoRetentionOptions opts, CancellationToken ct)
    {
        var summary = new RepoReapSummary();
        var rootDir = _repositoriesRoot ?? _gitHost.RepositoriesRootDirectory;
        if (string.IsNullOrWhiteSpace(rootDir) || !Directory.Exists(rootDir))
        {
            return summary;
        }

        string[] candidateDirs;
        try
        {
            candidateDirs = Directory.GetDirectories(rootDir);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "WorkItemRepoReaper: failed to enumerate repository root {RootDir}", rootDir);
            summary.Errors++;
            return summary;
        }

        var activeWorkers = _workerRegistry is not null
            ? await _workerRegistry.ListAsync(ct).ConfigureAwait(false)
            : [];
        var activeWorkItemIds = CollectRegisteredWorkItemIds(activeWorkers);

        var now = _time.GetUtcNow();
        var grace = opts.GracePeriod < TimeSpan.Zero ? TimeSpan.Zero : opts.GracePeriod;

        foreach (var dir in candidateDirs)
        {
            ct.ThrowIfCancellationRequested();
            WorkItemId? parsedId = null;
            try
            {
                summary.Scanned++;
                var dirName = Path.GetFileName(dir);
                if (!TryParseWorkItemCloneDirectory(dirName, out var workItemId, out var repositoryId))
                {
                    // Non-work-item directory (e.g. _upstream-mirror, merge staging, disabled hooks)
                    continue;
                }

                parsedId = workItemId;

                if (IsReparsePoint(dir))
                {
                    summary.Errors++;
                    _log.LogWarning(
                        "WorkItemRepoReaper: refusing to reap work item {WorkItemId} because its clone path is a reparse point",
                        workItemId);
                    continue;
                }

                // Compare parsed work-item ids so D-format / N-format GUID strings match.
                if (IsItemActive(workItemId) || activeWorkItemIds.Contains(workItemId))
                {
                    summary.SkippedActive++;
                    _log.LogDebug("WorkItemRepoReaper: work item {WorkItemId} clone is owned by an active worker; skipping", workItemId);
                    continue;
                }

                WorkItem? item;
                try
                {
                    item = await _store.GetAsync(workItemId, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    summary.Errors++;
                    _log.LogWarning(ex, "WorkItemRepoReaper: failed to query work item store for {WorkItemId}; skipping", workItemId);
                    continue;
                }

                if (item is null)
                {
                    // Tolerates an entry whose work item no longer exists in DB without aborting the sweep
                    summary.SkippedUnknown++;
                    _log.LogWarning("WorkItemRepoReaper: work item {WorkItemId} was not found in database; skipping", workItemId);
                    continue;
                }

                if (!WorkItemStates.IsTerminal(item.State))
                {
                    // Never touch a clone belonging to a non-terminal item
                    summary.SkippedNonTerminal++;
                    _log.LogDebug("WorkItemRepoReaper: work item {WorkItemId} is in non-terminal state {State}; clone survives", workItemId, item.State);
                    continue;
                }

                var age = now - item.UpdatedAt;
                if (grace > TimeSpan.Zero && age < grace)
                {
                    // Honour configured retention grace period
                    summary.SkippedWithinGrace++;
                    _log.LogDebug(
                        "WorkItemRepoReaper: work item {WorkItemId} is terminal ({State}) but within retention grace period ({Age} < {Grace}); skipping",
                        workItemId, item.State, age, grace);
                    continue;
                }

                // TOCTOU guard: re-verify not active and still terminal
                if (IsItemActive(workItemId) || activeWorkItemIds.Contains(workItemId))
                {
                    summary.SkippedActive++;
                    continue;
                }

                var refreshed = await _store.GetAsync(workItemId, ct).ConfigureAwait(false);
                if (refreshed is null || !WorkItemStates.IsTerminal(refreshed.State))
                {
                    summary.SkippedNonTerminal++;
                    continue;
                }

                await _gitHost.DisposeRepositoryAsync(repositoryId, ct).ConfigureAwait(false);
                summary.Reaped++;
                _log.LogInformation(
                    "WorkItemRepoReaper: reaped clone for terminal work item {WorkItemId} (state={State}, age={Age}, grace={Grace})",
                    workItemId, item.State, age, grace);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                summary.Errors++;
                if (parsedId is { } workItemId)
                {
                    _log.LogWarning(ex, "WorkItemRepoReaper: failed to inspect/reap work item {WorkItemId}; continuing sweep", workItemId);
                }
                else
                {
                    _log.LogWarning(ex, "WorkItemRepoReaper: failed to inspect/reap a repository entry; continuing sweep");
                }
            }
        }

        if (summary.Reaped > 0)
        {
            _log.LogInformation(
                "WorkItemRepoReaper: sweep completed — {Reaped} reaped, {Scanned} scanned, {SkippedWithinGrace} in grace, {SkippedActive} active, {SkippedNonTerminal} non-terminal, {SkippedUnknown} unknown",
                summary.Reaped, summary.Scanned, summary.SkippedWithinGrace, summary.SkippedActive, summary.SkippedNonTerminal, summary.SkippedUnknown);
        }

        return summary;
    }

    private static bool IsOwnedByRegisteredWorker(IReadOnlyList<WorkerRegistration> workers, WorkItemId id)
    {
        for (var i = 0; i < workers.Count; i++)
        {
            if (TryParseWorkItemId(workers[i].CurrentWorkItemId, out var owned) && owned == id)
                return true;
        }
        return false;
    }

    private static HashSet<WorkItemId> CollectRegisteredWorkItemIds(IReadOnlyList<WorkerRegistration> workers)
    {
        var ids = new HashSet<WorkItemId>();
        for (var i = 0; i < workers.Count; i++)
        {
            if (TryParseWorkItemId(workers[i].CurrentWorkItemId, out var owned))
                ids.Add(owned);
        }
        return ids;
    }

    private static bool TryParseWorkItemCloneDirectory(string dirName, out WorkItemId id, out string repositoryId)
    {
        id = default;
        repositoryId = string.Empty;
        if (!dirName.EndsWith(".git", StringComparison.OrdinalIgnoreCase) || dirName.Length <= 4)
            return false;

        repositoryId = dirName[..^4];
        return TryParseWorkItemId(repositoryId, out id);
    }

    private static bool TryParseWorkItemId(string? value, out WorkItemId id)
    {
        id = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;
        if (!Guid.TryParse(value.AsSpan(), out var guid))
            return false;
        id = new WorkItemId(guid);
        return true;
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>
/// Summary of work-item repository reaping results for a single sweep.
/// </summary>
public sealed class RepoReapSummary
{
    public int Scanned { get; set; }
    public int Reaped { get; set; }
    public int SkippedActive { get; set; }
    public int SkippedNonTerminal { get; set; }
    public int SkippedWithinGrace { get; set; }
    public int SkippedUnknown { get; set; }
    public int Errors { get; set; }
}
