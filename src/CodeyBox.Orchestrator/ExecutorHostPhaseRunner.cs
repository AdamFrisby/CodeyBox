using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Shared executor-side phase mechanics: the default sandbox spec, the
/// staged-repo path resolution, and the sandbox teardown helper the
/// executor-host runner and the colocated transport execute around. One
/// implementation so local and remote execution cannot drift apart on
/// sandbox setup, path resolution, or teardown.
/// </summary>
internal static class ExecutorPhaseExecution
{
    /// <summary>
    /// Default sandbox spec for one phase execution. The image comes from
    /// executor-local configuration (the control plane never sends one);
    /// network stays denied by default so a phase cannot exfiltrate through
    /// an executor the operator did not open up. Timing fields carry the
    /// request's own phase (and work-item id when it parses) so provider
    /// lifecycle telemetry attributes the sandbox correctly.
    /// </summary>
    internal static SandboxSpec DefaultSandboxSpec(ExecutorPhaseRequest request, string imageReference) =>
        new()
        {
            ImageReference = imageReference ?? string.Empty,
            Purpose = SandboxPurpose.WorkItem,
            Network = SandboxNetworkPolicy.Denied,
            TimingWorkItemId = Guid.TryParse(request.WorkItemId, out var guid) ? new WorkItemId(guid) : null,
            TimingPhase = request.Phase,
        };

    /// <summary>
    /// Resolves the executor-local staged bare-repo path for
    /// <paramref name="repositoryId"/> under <paramref name="stagingRoot"/>.
    /// The repository id is untrusted dispatch input, so it never becomes a
    /// path directly: safe names are used as-is and anything else maps to a
    /// content-hashed leaf, and the result is canonicalized and contained
    /// under the root before it is returned.
    /// </summary>
    internal static string ResolveStagedRepoPath(string stagingRoot, string repositoryId) =>
        CombineLeaf(stagingRoot, ToSafeLeaf(repositoryId), repositoryId);

    /// <summary>
    /// Resolves the staged bare-repo path for one dispatch: the per-repo leaf
    /// plus a hash of the dispatch key (work item + phase + attempt), so two
    /// concurrent dispatches against the same repo stage, run and tar
    /// isolated copies instead of interleaving delete/copy/run/tar on one
    /// shared leaf. Deterministic from the request so the colocated transport
    /// and the executor-side runner agree on the leaf without extra I/O.
    /// Same containment guarantees as <see cref="ResolveStagedRepoPath"/>.
    /// </summary>
    internal static string ResolveStagedRepoPathForDispatch(string stagingRoot, ExecutorPhaseRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var dispatchKey = ExecutorPhaseProxy.BuildDispatchKey(request);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(dispatchKey));
        var suffix = Convert.ToHexString(hash).ToLowerInvariant()[..16];
        return CombineLeaf(stagingRoot, ToSafeLeaf(request.RepositoryId) + "-d-" + suffix, request.RepositoryId);
    }

    private static string CombineLeaf(string stagingRoot, string leaf, string repositoryId)
    {
        if (string.IsNullOrWhiteSpace(stagingRoot))
            throw new ExecutorPhaseException("No executor staging root is configured; cannot resolve the staged repository.");
        var rootFull = Path.GetFullPath(stagingRoot);
        var full = Path.GetFullPath(Path.Combine(rootFull, leaf));
        var prefix = rootFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.Ordinal))
            throw new ExecutorPhaseException($"Staged repo path for '{repositoryId}' escapes the staging root.");
        return full;
    }

    internal static async Task DisposeQuietAsync(ISandbox sandbox, ILogger? log)
    {
        try
        {
            await sandbox.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log?.LogWarning(ex, "Phase sandbox {SandboxId} teardown failed; the leak reaper owns the orphan", sandbox.Id);
        }
    }

    private static string ToSafeLeaf(string repositoryId)
    {
        if (!string.IsNullOrWhiteSpace(repositoryId)
            && repositoryId.Length <= 128
            && repositoryId.All(static ch =>
                (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9') || ch == '-' || ch == '_'))
            return repositoryId;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(repositoryId ?? string.Empty));
        return "repo-" + Convert.ToHexString(hash).ToLowerInvariant()[..32];
    }
}

/// <summary>
/// Executor-side phase runner: takes an <see cref="ExecutorPhaseRequest"/>,
/// provisions a sandbox, runs the phase's agent work through the injected
/// <see cref="IExecutorPhaseHandler"/>, and returns the
/// <see cref="ExecutorPhaseResult"/>. The payload stays opaque throughout —
/// the runner validates the envelope, never the pipeline semantics inside
/// <c>PayloadJson</c> — so every phase the pipeline dispatches (work,
/// rework, the audit phases, merge) executes through this one path with the
/// handler owning the per-phase agent logic.
///
/// <para>Executing-side guarantees, mirroring the control plane's dispatch
/// contract without duplicating its decisions:</para>
/// <list type="bullet">
/// <item>A redelivered phase (same dispatch key, same body hash) replays the
/// original result without provisioning a second sandbox; the same key with
/// a differing body throws <see cref="ExecutorPhaseConflictException"/> and
/// never executes. Entries expire under the configured TTL with a bounded
/// count; only the control plane's idempotency store is authoritative across
/// restarts.</item>
/// <item>A redelivery that arrives while the phase is still running attaches
/// to the in-flight execution instead of starting a duplicate — this is the
/// <see cref="ExecutorDisconnectPolicy.RetainSandboxForResume"/> resume
/// path: connection loss never cancels local execution, and the redelivery
/// observes the original result.</item>
/// <item>Admissions never exceed the executor's declared
/// <c>MaxConcurrentSandboxes</c> (null means uncapped; zero refuses fast);
/// <see cref="ActivePhaseCount"/> reports live load for heartbeat
/// placement.</item>
/// <item>Executor-environment failures (nothing staged, provisioning,
/// sandbox or handler transport loss) throw
/// <see cref="ExecutorPhaseTransportException"/> — infrastructure, retried
/// elsewhere — never an <see cref="ExecutorPhaseOutcome.AgentFailed"/>
/// verdict on the work item's diff.</item>
/// </list>
///
/// <para>Thread-safe: concurrent dispatches share one lock for the replay
/// map and the capacity gate, so concurrent writers cannot corrupt, lose, or
/// cross-contaminate entries.</para>
/// </summary>
public sealed class ExecutorHostPhaseRunner : IExecutorPhaseRunner
{
    private readonly ISandboxProvider _sandboxes;
    private readonly ExecutorSandboxTracker _tracker;
    private readonly IExecutorPhaseHandler _handler;
    private readonly Func<ExecutorOptions> _optionsAccessor;
    private readonly Func<ExecutorPhaseDispatchOptions> _dispatchOptionsAccessor;
    private readonly Func<ExecutorPhaseRequest, SandboxSpec>? _specFactory;
    private readonly TimeProvider _clock;
    private readonly ILogger<ExecutorHostPhaseRunner> _log;

    private readonly object _mutex = new();
    private readonly Dictionary<string, PhaseEntry> _entries = new(StringComparer.Ordinal);
    private readonly List<CapacityWaiter> _waiters = [];
    private int _activeCount;

    public ExecutorHostPhaseRunner(
        ISandboxProvider sandboxes,
        ExecutorSandboxTracker tracker,
        IExecutorPhaseHandler handler,
        Func<ExecutorOptions> optionsAccessor,
        Func<ExecutorPhaseDispatchOptions>? dispatchOptionsAccessor = null,
        Func<ExecutorPhaseRequest, SandboxSpec>? sandboxSpecFactory = null,
        TimeProvider? clock = null,
        ILogger<ExecutorHostPhaseRunner>? log = null)
    {
        _sandboxes = sandboxes ?? throw new ArgumentNullException(nameof(sandboxes));
        _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _optionsAccessor = optionsAccessor ?? throw new ArgumentNullException(nameof(optionsAccessor));
        _dispatchOptionsAccessor = dispatchOptionsAccessor ?? (static () => new ExecutorPhaseDispatchOptions());
        _specFactory = sandboxSpecFactory;
        _clock = clock ?? TimeProvider.System;
        _log = log ?? NullLogger<ExecutorHostPhaseRunner>.Instance;
    }

    /// <summary>
    /// Number of phases currently admitted (executing or provisioning).
    /// Reported on heartbeats so the shared placement decider's least-loaded
    /// selection observes executor-side concurrency, not just
    /// orchestrator-side dispatches.
    /// </summary>
    public int ActivePhaseCount
    {
        get { lock (_mutex) return _activeCount; }
    }

    public async Task<ExecutorPhaseResult> ExecutePhaseAsync(ExecutorPhaseRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var dispatchOptions = _dispatchOptionsAccessor();
        dispatchOptions.Validate();
        ExecutorPhaseProxy.ValidateRequest(request, dispatchOptions);

        var dispatchKey = ExecutorPhaseProxy.BuildDispatchKey(request);
        var bodyHash = ExecutorPhaseProxy.ComputeBodyHash(request);

        Task<ExecutorPhaseResult> execution;
        lock (_mutex)
        {
            PruneExpiredLocked();
            if (_entries.TryGetValue(dispatchKey, out var existing))
            {
                if (!string.Equals(existing.BodyHash, bodyHash, StringComparison.Ordinal))
                    throw new ExecutorPhaseConflictException(dispatchKey);
                if (existing.CompletedResult is not null)
                {
                    _log.LogInformation(
                        "Executor phase {DispatchKey} redelivered; replaying original result without a second sandbox",
                        dispatchKey);
                    return existing.CompletedResult;
                }
                _log.LogInformation(
                    "Executor phase {DispatchKey} redelivered while running; attaching to the in-flight execution",
                    dispatchKey);
                execution = existing.Execution!;
            }
            else
            {
                EnforceCacheLimitLocked();
                var entry = new PhaseEntry(bodyHash);
                _entries[dispatchKey] = entry;
                entry.Execution = ExecuteAndCacheAsync(request, dispatchKey, dispatchOptions, entry, ct);
                execution = entry.Execution;
            }
        }
        return await execution.ConfigureAwait(false);
    }

    private async Task<ExecutorPhaseResult> ExecuteAndCacheAsync(
        ExecutorPhaseRequest request,
        string dispatchKey,
        ExecutorPhaseDispatchOptions dispatchOptions,
        PhaseEntry entry,
        CancellationToken ct)
    {
        ExecutorPhaseResult result;
        TimeSpan resultTtl;
        try
        {
            var options = _optionsAccessor();
            options.Validate();
            resultTtl = options.PhaseResultCacheTtl;
            if (options.MaxConcurrentSandboxes is 0)
                throw new ExecutorPhaseTransportException(
                    options.HostId.Trim(),
                    "capacity",
                    "This executor declares MaxConcurrentSandboxes=0 and never accepts phases.");
            using var _ = await AcquireCapacityAsync(options.MaxConcurrentSandboxes, ct).ConfigureAwait(false);
            result = await ExecuteCoreAsync(request, dispatchKey, options, dispatchOptions, ct).ConfigureAwait(false);
        }
        catch
        {
            lock (_mutex) { RemoveIfSameLocked(dispatchKey, entry); }
            throw;
        }

        lock (_mutex)
        {
            if (ReferenceEquals(_entries.GetValueOrDefault(dispatchKey), entry))
            {
                var now = _clock.GetUtcNow();
                entry.CompletedResult = result;
                entry.CompletedAt = now;
                entry.ExpiresAt = now + resultTtl;
                entry.Execution = null;
            }
        }
        return result;
    }

    private async Task<ExecutorPhaseResult> ExecuteCoreAsync(
        ExecutorPhaseRequest request,
        string dispatchKey,
        ExecutorOptions options,
        ExecutorPhaseDispatchOptions dispatchOptions,
        CancellationToken ct)
    {
        var hostId = options.HostId.Trim();
        var stagingRoot = string.IsNullOrWhiteSpace(options.PhaseStagingRoot)
            ? Path.Combine(Path.GetTempPath(), "codeybox-executor-phases")
            : options.PhaseStagingRoot.Trim();
        var repoPath = ExecutorPhaseExecution.ResolveStagedRepoPathForDispatch(stagingRoot, request);
        if (!Directory.Exists(repoPath))
            throw new ExecutorPhaseTransportException(
                hostId,
                "stage",
                $"Staged repository for '{request.RepositoryId}' is not present on this host.");

        var imageReference = options.PhaseSandboxImageReference ?? string.Empty;
        var factory = _specFactory ?? (req => ExecutorPhaseExecution.DefaultSandboxSpec(req, imageReference));
        ISandbox sandbox;
        try
        {
            sandbox = await _sandboxes.CreateAsync(factory(request), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ExecutorPhaseTransportException(hostId, "provision-sandbox", ex.Message, ex);
        }

        try
        {
            _tracker.Track(dispatchKey, sandbox.Id, sandbox);
        }
        catch
        {
            await ExecutorPhaseExecution.DisposeQuietAsync(sandbox, _log).ConfigureAwait(false);
            throw;
        }

        try
        {
            ExecutorPhaseResult? raw;
            try
            {
                raw = await _handler.ExecuteAsync(request, repoPath, sandbox, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (ExecutorPhaseTransportException)
            {
                throw;
            }
            catch (ExecutorPhaseException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new ExecutorPhaseTransportException(hostId, "run-phase", ex.Message, ex);
            }

            if (raw is null)
                throw new ExecutorPhaseException($"Phase handler for phase '{request.Phase}' returned no result.");
            return ExecutorPhaseProxy.ValidateResult(raw, dispatchOptions);
        }
        finally
        {
            _tracker.TryUntrack(dispatchKey, out _);
            await ExecutorPhaseExecution.DisposeQuietAsync(sandbox, _log).ConfigureAwait(false);
        }
    }

    private async Task<IDisposable> AcquireCapacityAsync(int? capacity, CancellationToken ct)
    {
        CapacityWaiter? waiter = null;
        lock (_mutex)
        {
            if (!capacity.HasValue || _activeCount < capacity.Value)
            {
                _activeCount++;
                return new CapacityLease(this);
            }
            waiter = new CapacityWaiter();
            _waiters.Add(waiter);
            _log.LogDebug("Executor phase waits for capacity ({Active}/{Capacity})", _activeCount, capacity.Value);
        }

        try
        {
            using (ct.Register(static state => ((CapacityWaiter)state!).TrySetCanceled(), waiter))
            {
                await waiter.Task.ConfigureAwait(false);
            }
        }
        catch
        {
            lock (_mutex) { _waiters.Remove(waiter); }
            throw;
        }
        return new CapacityLease(this);
    }

    private void ReleaseCapacity()
    {
        CapacityWaiter? next = null;
        lock (_mutex)
        {
            _activeCount--;
            var capacity = SafeCapacity();
            if (_waiters.Count > 0 && (!capacity.HasValue || _activeCount < capacity.Value))
            {
                next = _waiters[0];
                _waiters.RemoveAt(0);
                _activeCount++;
            }
        }
        next?.TrySetResult();
    }

    private int? SafeCapacity()
    {
        // Fail-open to uncapped only when the options accessor itself is
        // broken: admission already read the same accessor (and failed the
        // dispatch there), so this path only affects already-queued waiters.
        // A throwing accessor is a host bug, not a capacity signal.
        try
        {
            return _optionsAccessor().MaxConcurrentSandboxes;
        }
        catch
        {
            return null;
        }
    }

    private void PruneExpiredLocked()
    {
        if (_entries.Count == 0)
            return;
        var now = _clock.GetUtcNow();
        foreach (var (key, entry) in _entries.ToArray())
        {
            if (entry.CompletedResult is not null && entry.ExpiresAt <= now)
                _entries.Remove(key);
        }
    }

    private void EnforceCacheLimitLocked()
    {
        int limit;
        try
        {
            limit = _optionsAccessor().MaxCachedPhaseResults;
        }
        catch
        {
            // A broken accessor must not fail dispatches; skip eviction this
            // round and enforce again on the next miss.
            return;
        }
        if (limit <= 0)
            return;
        while (true)
        {
            string? oldestKey = null;
            var oldestAt = DateTimeOffset.MaxValue;
            var completed = 0;
            foreach (var (key, entry) in _entries)
            {
                if (entry.CompletedResult is null)
                    continue;
                completed++;
                if (entry.CompletedAt < oldestAt)
                {
                    oldestAt = entry.CompletedAt ?? oldestAt;
                    oldestKey = key;
                }
            }
            if (completed < limit || oldestKey is null)
                return;
            _entries.Remove(oldestKey);
        }
    }

    private void RemoveIfSameLocked(string dispatchKey, PhaseEntry entry)
    {
        if (ReferenceEquals(_entries.GetValueOrDefault(dispatchKey), entry))
            _entries.Remove(dispatchKey);
    }

    private sealed class PhaseEntry(string bodyHash)
    {
        public string BodyHash { get; } = bodyHash;
        public Task<ExecutorPhaseResult>? Execution { get; set; }
        public ExecutorPhaseResult? CompletedResult { get; set; }
        public DateTimeOffset? CompletedAt { get; set; }
        public DateTimeOffset? ExpiresAt { get; set; }
    }

    private sealed class CapacityWaiter
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Task => _gate.Task;
        public bool TrySetResult() => _gate.TrySetResult();
        public bool TrySetCanceled() => _gate.TrySetCanceled();
    }

    private sealed class CapacityLease(ExecutorHostPhaseRunner owner) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            owner.ReleaseCapacity();
        }
    }
}
