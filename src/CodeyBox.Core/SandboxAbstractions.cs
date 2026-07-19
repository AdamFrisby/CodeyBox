using System.Text;

namespace CodeyBox.Core;

/// <summary>
/// Signals that a sandbox command could not be dispatched or observed because
/// the sandbox execution transport was unavailable. Unlike an ordinary
/// non-zero command result, callers may safely classify this as infrastructure
/// failure and retain durable recovery state.
/// </summary>
public sealed class SandboxExecutionUnavailableException : Exception
{
    public SandboxExecutionUnavailableException(int exitCode)
        : base($"Sandbox execution was unavailable (exit {exitCode}).")
    {
        ExitCode = exitCode;
    }

    public int ExitCode { get; }
}

/// <summary>
/// Lists and disposes managed sandboxes without implying the ability to create
/// new work sandboxes. Lifecycle sweepers and operator endpoints depend on this
/// narrower contract so composite lifecycle views do not masquerade as providers.
/// </summary>
public interface IManagedSandboxLifecycle
{
    /// <summary>Stable provider identifier used for diagnostics and lifecycle scoping.</summary>
    string Name { get; }

    /// <summary>
    /// Returns all sandboxes on the host that this provider verifies as owned,
    /// using its configured namespace and/or durable ownership metadata. Used by the
    /// <see cref="CodeyBox.Orchestrator.SandboxLeakReaper"/> to detect
    /// sandboxes that outlived their work item.
    ///
    /// <para>Implementations that have no persistent sandbox lifecycle
    /// (bubblewrap, process) return an empty list.</para>
    ///
    /// <para>Implementations may cache inventory briefly when repeated reads
    /// would otherwise overload an external lifecycle service.</para>
    /// </summary>
    Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct);

    /// <summary>
    /// Returns managed sandboxes together with inventory completeness metadata.
    /// Multi-host providers must override this when a sweep can partially
    /// succeed, so callers do not infer that a missing sandbox is absent on a
    /// host that was never inventoried.
    /// </summary>
    async Task<ManagedSandboxInventory> ListManagedInventoryAsync(CancellationToken ct)
    {
        var managed = await ListAllManagedAsync(ct).ConfigureAwait(false);
        return ManagedSandboxInventory.Complete(managed);
    }

    /// <summary>
    /// Best-effort dispose of a sandbox by name. Used by the
    /// <see cref="CodeyBox.Orchestrator.SandboxLeakReaper"/> when
    /// <c>AutoDispose=true</c>, and by the
    /// <c>POST /sandboxes/leaked/{name}/dispose</c> operator endpoint.
    ///
    /// <para>Implementations that have no persistent lifecycle (bubblewrap,
    /// process) are no-ops. Implementations may throw on failure; all callers
    /// must wrap invocations in try/catch and log the exception.</para>
    /// </summary>
    Task DisposeLeakedAsync(string name, CancellationToken ct);

    /// <summary>
    /// Best-effort dispose of the exact sandbox snapshot returned by
    /// <see cref="ListAllManagedAsync"/>. Composite lifecycle views use
    /// provider metadata to route disposal back to the lifecycle that reported
    /// the sandbox; multi-host providers use executor metadata to target the
    /// owning host instead of rediscovering by name across a host pool.
    /// The default implementation delegates only unscoped snapshots; providers
    /// that understand either scope dimension must override this overload.
    /// </summary>
    Task DisposeLeakedAsync(ManagedSandboxInfo sandbox, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        if (sandbox.LifecycleProviderId is not null || sandbox.HostId is not null)
        {
            throw new NotSupportedException(
                "This sandbox lifecycle cannot interpret a provider- or host-scoped disposal snapshot.");
        }
        return DisposeLeakedAsync(sandbox.Name, ct);
    }
}

/// <summary>
/// Builds and starts isolated execution sandboxes. Implementations include a
/// plain-process dev runner (UNSAFE; for local testing only), bubblewrap
/// (namespace isolation, shared kernel), and VM-backed implementations with a
/// separate guest kernel. The orchestrator selects one provider for new work;
/// a composition layer may change that choice without rerouting existing
/// sandbox handles.
/// </summary>
public interface ISandboxProvider : IManagedSandboxLifecycle
{
    /// <summary>
    /// Agent-output data plane this provider can offer for long-running CLI
    /// invocations. Providers that do not override this keep stdout/stderr on
    /// the normal <see cref="ISandbox.ExecAsync"/> pipe.
    /// </summary>
    SandboxAgentOutputTransportKind AgentOutputTransportKind => SandboxAgentOutputTransportKind.ExecPipe;

    /// <summary>
    /// Preferred launch mode for one-shot batch agent invocations. Providers
    /// that can safely supervise a detached process may advertise detached
    /// launch independently from their output transport.
    /// </summary>
    SandboxBatchLaunchMode BatchLaunchMode => SandboxBatchLaunchMode.Attached;

    /// <summary>
    /// Provisions a sandbox according to the given spec. The returned handle
    /// holds the running sandbox until disposed; disposal must tear it down
    /// regardless of state.
    /// </summary>
    Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default);
}

/// <summary>
/// Optional <see cref="ISandboxProvider"/> capability that reports whether the
/// provider captures per-VM resource metrics at teardown. When true, each
/// work-item timing phase must be kept on its own VM so a persisted per-phase resource record is
/// attributable to a single phase; when false (the default), a warm reusable VM
/// is shared across phases as before, incurring no extra teardown/rebuild churn.
/// Live-handle reuse reads <see cref="IResourceMetricsCapturingSandbox"/> so a
/// provider hot reload cannot change the policy of an existing VM. Providers
/// that never capture metrics simply do not implement this interface.
/// </summary>
public interface IResourceMetricsCapturingProvider
{
    /// <summary>
    /// True when the provider captures per-VM resource usage at teardown. Read
    /// live so a hot-reload of the capture toggle is observed on the next call.
    /// </summary>
    bool CapturesResourceMetrics { get; }
}

/// <summary>
/// Optional live-sandbox capability exposing the immutable resource-metrics
/// capture policy attached to that concrete handle. Reuse decisions must read
/// this snapshot instead of a hot-reloadable provider selection that may now
/// describe a different backend or a later options version.
/// </summary>
public interface IResourceMetricsCapturingSandbox : ISandbox
{
    bool CapturesResourceMetrics { get; }
}

/// <summary>
/// Snapshot of a sandbox that exists on the host, returned by
/// <see cref="IManagedSandboxLifecycle.ListAllManagedAsync"/>.
/// </summary>
/// <param name="Name">VM name / namespace ID.</param>
/// <param name="CreatedAt">Best-effort creation timestamp; null if not derivable.</param>
/// <param name="DiskBytes">Reported disk usage; null if not available.</param>
/// <param name="IsTrackedActive">
/// True when this sandbox is still owned by a currently-running phase in the
/// current orchestrator process. False means the sandbox exists on the host
/// but no live phase owns it; that includes sandboxes from prior processes and
/// sandboxes whose normal phase disposal failed and should be retried by the
/// leak reaper.
/// </param>
/// <param name="HasPreemptMarker">
/// True when the sandbox root carries the graceful-shutdown preempt marker.
/// Such sandboxes are intentionally preserved during the configured preempt
/// retention window and must not be treated as leaks until that window expires.
/// </param>
/// <param name="IsSuspendLifecycleOrFrozen">
/// Provider-computed flag: true when the sandbox is in a suspend lifecycle
/// state — freezing its RAM image to disk or already frozen — rather than
/// running or stopped. This abstracts the provider's own lifecycle vocabulary
/// (e.g. the multipass <c>Suspending</c>/<c>Suspended</c> states) to a single
/// boolean, the same way <see cref="HasPreemptMarker"/> abstracts a provider
/// concern. The <see cref="CodeyBox.Orchestrator.SandboxLeakReaper"/> uses it to
/// recognise a frozen VM with no live orchestrator mapping as a suspend orphan —
/// one that must not inherit the long preempt-retention grace — without Core
/// depending on any backend's CLI state strings. Always false for providers that
/// do not model a persistent suspend lifecycle.
/// </param>
/// <param name="LifecycleProviderId">
/// Optional opaque identifier for the lifecycle provider that reported this
/// snapshot. Plain providers leave this null; composite lifecycle views fill it
/// so later cleanup can target the reporting provider only.
/// </param>
/// <param name="HostId">
/// Provider-specific executor identity for multi-host providers. Null for
/// providers where the sandbox name alone is sufficient.
/// </param>
public sealed record ManagedSandboxInfo(
    string Name,
    DateTimeOffset? CreatedAt,
    long? DiskBytes,
    bool IsTrackedActive,
    bool HasPreemptMarker = false,
    bool IsSuspendLifecycleOrFrozen = false,
    string? LifecycleProviderId = null,
    string? HostId = null,
    SandboxPurpose Purpose = SandboxPurpose.WorkItem);

/// <summary>
/// Broad owner category for a sandbox. Providers persist this where possible
/// so post-restart leak reapers only sweep sandboxes they own.
/// </summary>
public enum SandboxPurpose
{
    WorkItem = 0,
    Deployment = 1,
}

public sealed class ManagedSandboxInventory : IReadOnlyList<ManagedSandboxInfo>
{
    private readonly ManagedSandboxInfo[] _items;

    public ManagedSandboxInventory(
        IReadOnlyList<ManagedSandboxInfo> items,
        bool isComplete,
        IReadOnlySet<string>? inventoriedHostIds = null)
    {
        ArgumentNullException.ThrowIfNull(items);

        _items = items.ToArray();
        IsComplete = isComplete;
        InventoriedHostIds = inventoriedHostIds is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(inventoriedHostIds, StringComparer.Ordinal);
    }

    public bool IsComplete { get; }
    public IReadOnlySet<string> InventoriedHostIds { get; }
    public int Count => _items.Length;
    public ManagedSandboxInfo this[int index] => _items[index];

    public static ManagedSandboxInventory Complete(IReadOnlyList<ManagedSandboxInfo> items) =>
        new(items, isComplete: true);

    public IEnumerator<ManagedSandboxInfo> GetEnumerator() =>
        ((IEnumerable<ManagedSandboxInfo>)_items).GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>A live sandbox. Disposing destroys it.</summary>
public interface ISandbox : IAsyncDisposable
{
    string Id { get; }

    /// <summary>
    /// Agent-output data plane this concrete sandbox can offer. The default is
    /// the historical exec pipe; providers may advertise a richer transport and
    /// still fall back per invocation when setup is unavailable.
    /// </summary>
    SandboxAgentOutputTransportKind AgentOutputTransportKind => SandboxAgentOutputTransportKind.ExecPipe;

    /// <summary>
    /// Preferred launch mode for one-shot batch agent invocations on this
    /// concrete sandbox. The default keeps the command attached to
    /// <see cref="ExecAsync"/>.
    /// </summary>
    SandboxBatchLaunchMode BatchLaunchMode => SandboxBatchLaunchMode.Attached;

    /// <summary>
    /// Executes a command inside the sandbox. The command is run with
    /// /work as the working directory unless overridden. Output streams are
    /// captured fully; for long-running commands prefer streaming variants
    /// added later.
    /// </summary>
    Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default);

    /// <summary>
    /// Flushes provider-specific mutable sandbox state back to the orchestrator
    /// host without tearing the sandbox down. Providers with remote staging use
    /// this to make writes visible before later host-side pipeline phases run.
    /// </summary>
    Task SyncStateToHostAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>
    /// Best-effort termination for commands currently running through
    /// <see cref="ExecAsync"/>. Used by watchdog paths that must make progress
    /// even when the command ignores cancellation. Providers with real process
    /// isolation should override this; the default is for lightweight test fakes.
    /// </summary>
    Task KillActiveExecsAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>
    /// Returns a PNG screenshot of the current graphical desktop. Providers
    /// that do not support graphical sandboxes throw <see cref="NotSupportedException"/>.
    /// </summary>
    Task<byte[]> GetScreenshotAsync(CancellationToken ct = default) =>
        throw new NotSupportedException("This sandbox does not expose a graphical desktop.");

    /// <summary>
    /// Synthesizes desktop input events inside a graphical sandbox. Providers
    /// that do not support graphical sandboxes throw <see cref="NotSupportedException"/>.
    /// </summary>
    Task SynthesizeInputAsync(IReadOnlyList<SandboxInputEvent> events, CancellationToken ct = default) =>
        throw new NotSupportedException("This sandbox does not expose a graphical desktop.");

    Task<SandboxAccessibilitySnapshot?> GetAccessibilityAtPointAsync(int x, int y, CancellationToken ct = default) =>
        Task.FromResult<SandboxAccessibilitySnapshot?>(null);

    Task<string?> GetAccessibilityTreeJsonAsync(CancellationToken ct = default) =>
        Task.FromResult<string?>(null);

    /// <summary>
    /// Resource metrics captured at teardown/disposal, or null if not yet captured or not supported.
    /// </summary>
    SandboxResourceMetrics? ResourceMetrics => null;
}

/// <summary>
/// Resource metrics captured at sandbox teardown for capacity planning.
/// </summary>
public sealed record SandboxResourceMetrics(
    long? PeakRamBytes,
    double? AvgCpuPercent,
    long? NetRxBytes,
    long? NetTxBytes,
    double? UptimeSeconds,
    double? LoadAvg1,
    double? LoadAvg5,
    double? LoadAvg15,
    string? BaselineRef,
    string? NetworkProfile,
    string Phase,
    DateTimeOffset CapturedAt)
{
    public long? TotalNetIoBytes => NetRxBytes.HasValue || NetTxBytes.HasValue
        ? (NetRxBytes ?? 0) + (NetTxBytes ?? 0)
        : null;
}

/// <summary>
/// Optional identity extension for sandboxes whose <see cref="ISandbox.Id"/> is
/// only unique within one executor host.
/// </summary>
public interface IHostQualifiedSandbox
{
    string HostId { get; }
}

/// <summary>
/// Optional sandbox capability for providers that can positively identify a
/// successful dispose as an execution-host loss recovery hand-off. Admission
/// wrappers may release the global sandbox slot even when a host-scoped
/// inventory is partial, because the owning work item will be replayed on a new
/// sandbox and the old host is no longer part of active capacity.
/// </summary>
public interface IReleaseAdmissionOnHostLossSandbox : ISandbox
{
    /// <summary>
    /// True after a successful <see cref="IAsyncDisposable.DisposeAsync"/> when
    /// the provider intentionally abandoned the old host-local sandbox for leak
    /// reaper cleanup after an execution-time host transport loss. False for
    /// normal cleanup failures where a live sandbox may still consume capacity.
    /// </summary>
    bool ReleaseAdmissionAfterHostLoss { get; }
}

/// <summary>
/// Optional capability for sandbox implementations that can expose an address
/// reachable from the orchestrator host. Deployment drivers use this for
/// caller-facing endpoints; sandbox-local loopback addresses stay internal.
/// </summary>
public interface IRoutableSandbox : ISandbox
{
    string? HostAddress { get; }
}


/// <summary>
/// Optional sandbox-level capability for publishing a sandbox TCP port to the
/// orchestrator host. Implementations may expose the port directly when the
/// sandbox has a host-routable address, or return a local tunnel endpoint.
/// Deployment adapters translate this sandbox capability into deployment
/// endpoint DTOs; sandbox providers do not depend on deployment APIs.
/// </summary>
public interface ISandboxPortPublisher : ISandbox
{
    bool CanPublishPort(int port);
    SandboxPublishedPort PublishPort(int port);
}

public sealed record SandboxPublishedPort(
    string Host,
    int Port,
    IReadOnlyDictionary<string, string>? Metadata = null);

/// <summary>
/// Optional sandbox capability for releasing provider-side active tracking
/// without claiming the sandbox was successfully disposed. Used by deployment
/// cleanup after a failed delete so leak reapers can retry in-process.
/// </summary>
public interface IActiveSandboxLease
{
    void ReleaseActiveTracking();
}

/// <summary>
/// Optional sandbox capability used during graceful host shutdown. A provider
/// that can preserve an interrupted sandbox should stop it and make subsequent
/// disposal a no-op so cached state can survive the orchestrator restart.
/// </summary>
public interface IPreemptibleSandbox : ISandbox
{
    Task StopAndPreserveAsync(CancellationToken ct = default);

    /// <summary>
    /// Retains a stopped sandbox specifically because infrastructure prevented
    /// publication of the normal agent-turn checkpoint. Providers that can
    /// durably reconstruct and authenticate the sandbox after a process restart
    /// return its lease; other preemptible providers return null.
    /// </summary>
    Task<SandboxRecoveryLease?> RetainForInfrastructureRecoveryAsync(
        CancellationToken ct = default) => Task.FromResult<SandboxRecoveryLease?>(null);
}

/// <summary>
/// Optional capability for sandbox implementations whose preserve/recovery
/// operations make later <see cref="IAsyncDisposable.DisposeAsync"/> a no-op.
/// Terminal cleanup calls this before disposing when it must destroy the
/// sandbox even after a previous stop/preserve.
/// </summary>
public interface IPreserveOnDisposeSandbox : ISandbox
{
    void DisablePreserveOnDispose();
}

/// <summary>
/// Optional sandbox capability for live sandboxes that participate in the
/// orchestrator's graceful shutdown teardown sweep. The marker lets the normal
/// phase runner detect that shutdown teardown has already become authoritative
/// for this sandbox and must not race it with in-VM preempt-checkpoint commands.
/// </summary>
public interface IShutdownTeardownSandbox : ISandbox
{
    /// <summary>
    /// True once the shutdown teardown handler has taken ownership of this
    /// sandbox via Suspend (RAM frozen), successful Stop (preserved), or Dispose
    /// (delete --purge). PipelineRunner reads this in its host-shutdown OCE
    /// catch block to short-circuit the in-VM preempt-checkpoint flow when that
    /// flow would hang against a frozen/stopped sandbox or fault against a
    /// deleted sandbox.
    /// </summary>
    bool IsOwnedByShutdownHandler => false;

    /// <summary>
    /// Flips <see cref="IsOwnedByShutdownHandler"/> to true. Called by
    /// <c>SandboxShutdownTeardownService</c> when lifecycle teardown has safely
    /// become authoritative. Default no-op: fakes that don't track teardown
    /// ownership keep <see cref="IsOwnedByShutdownHandler"/> false.
    /// </summary>
    void MarkOwnedByShutdownHandler() { }
}

/// <summary>
/// Marker for sandbox providers whose filesystem cannot safely host
/// agent credential files. Runners that normally materialise subscription
/// or OAuth credential bundles under <c>$HOME</c> must fail before writing
/// those files when this capability is present.
/// </summary>
public interface IRejectsFileBackedAgentCredentials : ISandbox
{
    string FileBackedAgentCredentialsUnsupportedReason { get; }
}

/// <summary>
/// Optional capability exposing the stable provider identifier that owns a
/// sandbox. Durable session references use this value to route lifecycle
/// operations back to the provider that created the sandbox, even after the
/// live provider selector changes.
/// </summary>
public interface IProviderOwnedSandbox : ISandbox
{
    string ProviderId { get; }
}

/// <summary>
/// Optional capability for sandboxes whose private guest root filesystem can
/// be safely modified by privileged setup commands. Consumers use this for
/// security tooling that must replace absolute executable paths inside a VM.
/// Sandboxes that execute against the host root, or whose root may be shared
/// with the host, must not implement this capability.
/// </summary>
public interface IPrivilegedGuestFileHardeningSandbox : ISandbox
{
}

/// <summary>
/// Implemented by sandbox wrappers/decorators (e.g. the admission-control and
/// reusable-sandbox families) that forward an inner <see cref="ISandbox"/>.
/// Marker capabilities like <see cref="IRejectsFileBackedAgentCredentials"/>
/// cannot be conditionally re-implemented by a decorator, so consumers use
/// <see cref="SandboxCapability.Find{T}(ISandbox)"/> rather than relying on
/// <c>is</c> against the outermost wrapper.
/// </summary>
public interface ISandboxDecorator : ISandbox
{
    /// <summary>The sandbox this decorator wraps.</summary>
    ISandbox InnerSandbox { get; }
}

/// <summary>
/// Resolves optional capabilities from a sandbox and any transparent decorator
/// chain around it. A malformed decorator chain fails closed instead of hiding
/// a security-relevant capability or recursing forever.
/// </summary>
public static class SandboxCapability
{
    /// <summary>
    /// Returns the first <typeparamref name="T"/> exposed by
    /// <paramref name="sandbox"/> or one of its inner sandboxes. Returns null
    /// when the well-formed chain does not expose that capability.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// A decorator returns a null inner sandbox or the chain contains a cycle.
    /// </exception>
    public static T? Find<T>(ISandbox sandbox)
        where T : class, ISandbox
    {
        ArgumentNullException.ThrowIfNull(sandbox);

        var current = sandbox;
        var visited = new HashSet<ISandbox>(ReferenceEqualityComparer.Instance);
        while (visited.Add(current))
        {
            if (current is T capability)
                return capability;
            if (current is not ISandboxDecorator decorator)
                return null;

            current = decorator.InnerSandbox
                ?? throw new InvalidOperationException(
                    "A sandbox decorator returned a null inner sandbox while resolving a capability.");
        }

        throw new InvalidOperationException(
            "A sandbox decorator cycle prevents capability resolution.");
    }
}

/// <summary>
/// Optional sandbox capability for providers that can freeze a running sandbox
/// (including its RAM state) and resume it later via
/// <see cref="ISuspendingSandboxProvider.ResumeSandboxAsync"/>. Currently
/// implemented by the multipass provider (via <c>multipass suspend</c>); the
/// process and bubblewrap providers do not implement it.
///
/// <para>Distinct from <see cref="IPreemptibleSandbox.StopAndPreserveAsync"/>:
/// preempt does an orderly stop after capturing a git checkpoint (process
/// state inside the sandbox is lost; recovery replays from the ref). Suspend
/// freezes the in-VM process state so the agent CLI can resume exactly where
/// it was on the next orchestrator start.</para>
/// </summary>
public interface ISuspendableSandbox : ISandbox
{
    /// <summary>
    /// Freeze the sandbox's RAM to disk so it can be resumed later. Marks the
    /// sandbox as preserved so the subsequent <see cref="IAsyncDisposable.DisposeAsync"/>
    /// call is a no-op rather than destroying the suspended VM.
    /// </summary>
    Task SuspendAsync(CancellationToken ct = default);

    /// <summary>
    /// True once <see cref="SuspendAsync"/> has frozen the sandbox's RAM and
    /// the provider has flipped the preserve-on-dispose flag. PipelineRunner
    /// reads this in its host-shutdown OCE catch block so the legacy
    /// preempt-checkpoint flow (git add/commit/push from inside the VM) does
    /// NOT race a suspend that already preserved the agent's in-RAM state —
    /// running a git push against a frozen VM hangs and would block the
    /// orchestrator's exit. Defaults to false for providers that don't yet
    /// model suspension state.
    /// </summary>
    bool IsSuspended => false;

    /// <summary>
    /// Best-effort RAM size of this sandbox in bytes, or null when the provider
    /// cannot report it. The shutdown teardown handler scales the per-VM
    /// suspend timeout by this value: <c>multipass suspend</c> writes the whole
    /// RAM image to disk, so a 12 GiB VM under load legitimately takes far longer
    /// than a 1 GiB idle one. Null falls back to the flat floor timeout.
    /// </summary>
    long? MemoryBytes => null;
}

/// <summary>
/// Shared policy for how long a RAM-snapshot suspend is allowed to take, scaled
/// by VM RAM size. Centralised so the shutdown suspend handler's per-VM timeout
/// (<see cref="CodeyBox.Orchestrator.SandboxShutdownTeardownService"/>), the
/// startup resume wait (how long to wait out a still-freezing VM before
/// <c>multipass start</c>), and the host shutdown grace all derive from one
/// formula and cannot drift apart. <c>multipass suspend</c> writes the whole RAM
/// image to disk, so suspend time grows ~linearly with VM size; a uniform cap
/// either truncates large VMs or wastes time waiting on small ones.
/// </summary>
public static class SuspendTimeoutPolicy
{
    /// <summary>Floor / fallback used when the VM's RAM size is unknown.</summary>
    public static readonly TimeSpan DefaultFloor = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Default cap on parallel <c>multipass suspend</c> calls during a shutdown
    /// drain. Suspend writes the VM's RAM to disk; running too many in parallel
    /// just contends on disk IO. Kept here (rather than only on the orchestrator
    /// suspend handler) so the host-shutdown ceiling math in
    /// <see cref="HostShutdownReserve"/> / <see cref="ResolveHostShutdownTimeout"/>
    /// and the handler's semaphore share one source of truth.
    /// </summary>
    public const int DefaultMaxParallelSuspends = 8;

    /// <summary>
    /// Extra budget per GiB of VM RAM. The effective budget is
    /// <c>max(floor, RAM_GiB × perGiB)</c>. 150s/GiB matches the observed
    /// ~6-minute suspend of a 4 GiB VM under load with headroom, and gives the
    /// 12 GiB default VM (see <see cref="SandboxResourceLimits.Default"/>) a
    /// 30-minute ceiling.
    /// </summary>
    public static readonly TimeSpan DefaultPerGiB = TimeSpan.FromSeconds(150);

    /// <summary>
    /// Effective suspend/resume budget for a VM of the given RAM size:
    /// <c>max(floor, RAM_GiB × perGiB)</c>. Null or non-positive
    /// <paramref name="memoryBytes"/> falls back to <paramref name="floor"/>
    /// (never a zero or negative budget that would abandon a suspend instantly).
    /// </summary>
    public static TimeSpan For(long? memoryBytes, TimeSpan? floor = null, TimeSpan? perGiB = null)
    {
        var effectiveFloor = floor ?? DefaultFloor;
        var effectivePerGiB = perGiB ?? DefaultPerGiB;
        if (memoryBytes is not { } bytes || bytes <= 0)
            return effectiveFloor;
        var gib = bytes / (double)(1024L * 1024 * 1024);
        var scaled = effectivePerGiB * gib;
        return scaled > effectiveFloor ? scaled : effectiveFloor;
    }

    /// <summary>
    /// Host-shutdown ceiling (<c>HostOptions.ShutdownTimeout</c>) that must cover
    /// the worst-case suspend drain, not just a single VM. The shutdown teardown
    /// handler fans suspends out through a semaphore capped at
    /// <paramref name="maxParallelSuspends"/> and awaits all of them, so with up to
    /// <paramref name="maxConcurrentSandboxes"/> in-flight VMs the drain runs
    /// <c>ceil(N / batch)</c> sequential waves. Each wave is bounded by the per-VM
    /// budget (<see cref="For"/>) for the largest VM the deployment provisions
    /// (<paramref name="maxVmMemoryBytes"/>). Sizing the ceiling at
    /// <c>waves × perVmBudget</c> stops the host SIGKILLing the process mid-snapshot
    /// before later waves finish — e.g. 16 VMs at the default profile need two
    /// 30-minute waves, ~60 minutes, not 30. This is a CEILING: a shutdown that
    /// suspends fewer VMs (or none) returns as soon as the handler completes.
    /// </summary>
    public static TimeSpan HostShutdownReserve(
        int maxConcurrentSandboxes,
        int maxParallelSuspends,
        long? maxVmMemoryBytes,
        TimeSpan? floor = null,
        TimeSpan? perGiB = null)
    {
        var perVm = For(maxVmMemoryBytes, floor, perGiB);
        var sandboxes = Math.Max(1, maxConcurrentSandboxes);
        var batch = Math.Max(1, maxParallelSuspends);
        var waves = (sandboxes + batch - 1) / batch;
        return perVm * waves;
    }

    /// <summary>
    /// Resolve the host's <c>HostOptions.ShutdownTimeout</c> ceiling. Deployments
    /// with a suspend-capable provider reserve enough room for a future
    /// hot-reload to Suspend mode; otherwise a healthy RAM snapshot could be
    /// truncated by SIGKILL. When <paramref name="providerSupportsSuspend"/> is set,
    /// the ceiling is the worst-case suspend drain
    /// (<see cref="HostShutdownReserve"/>:
    /// <c>ceil(maxConcurrent / maxParallelSuspends)</c> waves of the largest
    /// per-VM budget) STACKED ON TOP OF the requested <paramref name="grace"/>.
    /// The two windows are sequential, not overlapping: shutdown teardown runs
    /// in <c>IHostedLifecycleService.StoppingAsync</c> (before BackgroundService
    /// cancellation), and the preempt-checkpoint / listener-drain window still
    /// needs the full <paramref name="grace"/> AFTERWARD. Taking the max of the
    /// two would let a suspend that consumes its whole reserve leave zero room for
    /// the post-suspend drain, so the host could SIGKILL the process while
    /// PipelineRunner is still shutting down. Providers that cannot suspend keep
    /// the tighter <paramref name="grace"/> alone.
    ///
    /// <para>This is deliberately mode-and-capability-driven, not
    /// provider-name-driven: the caller folds together whether the configured
    /// provider implements <see cref="ISuspendingSandboxProvider"/> and whether
    /// the selected or hot-reloadable shutdown path can suspend. Core therefore
    /// stays provider-agnostic — a new suspend-capable backend can raise the
    /// ceiling without adding another magic string here.</para>
    ///
    /// <para>Lives on the Core policy (rather than on the orchestrator suspend
    /// handler) so the API composition root can size the ceiling without
    /// depending on a concrete hosted-service type, and so the capability guard
    /// and the max() logic stay co-located with the suspend/resume budget
    /// formula they must agree with.</para>
    /// </summary>
    /// <param name="providerSupportsSuspend">True when the host must reserve suspend budget because the provider can suspend and the shutdown mode can hot-reload to Suspend.</param>
    /// <param name="grace">Baseline shutdown grace (request-drain / preempt-checkpoint window).</param>
    /// <param name="maxConcurrentSandboxes">Upper bound on concurrently in-flight (hence suspendable) VMs.</param>
    /// <param name="maxParallelSuspends">Parallel-suspend batch size; defaults to <see cref="DefaultMaxParallelSuspends"/>.</param>
    /// <param name="maxVmMemoryBytes">Largest per-VM RAM the deployment provisions; null uses <see cref="SandboxResourceLimits.Default"/>.</param>
    public static TimeSpan ResolveHostShutdownTimeout(
        bool providerSupportsSuspend,
        TimeSpan grace,
        int maxConcurrentSandboxes,
        int maxParallelSuspends = DefaultMaxParallelSuspends,
        long? maxVmMemoryBytes = null)
    {
        if (!providerSupportsSuspend)
            return grace;
        var reserve = HostShutdownReserve(
            maxConcurrentSandboxes,
            maxParallelSuspends,
            maxVmMemoryBytes ?? SandboxResourceLimits.Default.MemoryBytes);
        return grace + reserve;
    }
}

/// <summary>
/// Lets the sandbox shutdown handler pause new work dispatch BEFORE it begins
/// freezing/stopping VMs. Without this gate the orchestrator's dispatch loop
/// keeps picking up items and creating new sandboxes while the shutdown handler
/// is mid-snapshot — the new sandboxes miss the snapshot, then get torn down
/// uncleanly when the BackgroundService cancellation token finally fires.
/// Implemented by <c>OrchestratorService</c>; injected (optionally — null is a
/// no-op for test fixtures that drive the suspend handler directly) into the
/// shutdown handler so the ordering is enforceable.
/// </summary>
public interface IShutdownDispatchGate
{
    /// <summary>
    /// True once <see cref="PauseDispatch"/> has been called. The dispatch loop
    /// reads this and stops picking up new work; in-flight workers continue
    /// their current item to completion (or until the BackgroundService token
    /// fires).
    /// </summary>
    bool IsDispatchPaused { get; }

    /// <summary>
    /// Stop accepting new work for dispatch. Idempotent. Returns immediately;
    /// in-flight sandboxes that have already been created are still in the
    /// provider's active set and will be picked up by
    /// <c>SnapshotActiveSandboxes</c>.
    /// </summary>
    void PauseDispatch();
}

/// <summary>
/// Optional provider capability for enumerating live sandboxes that need early
/// lifecycle handling on graceful host shutdown. This is intentionally separate
/// from <see cref="ISuspendingSandboxProvider"/> so Stop/Dispose teardown does
/// not depend on suspend/resume support.
/// </summary>
public interface IActiveSandboxProvider
{
    /// <summary>
    /// Snapshot of currently-active sandboxes that can participate in early
    /// shutdown lifecycle handling, paired with the work item that owns each
    /// entry. Implementations that
    /// internally use a <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey,TValue}"/>
    /// or other snapshot-safe data structure return entries that are
    /// consistent with concurrent disposals — a sandbox racing dispose may
    /// still appear here, but its suspend/dispose operation is a no-op once the
    /// sandbox is disposed. Implementations that cannot
    /// determine the owner (e.g. an in-process <c>CreateAsync</c> that did not
    /// pass <see cref="SandboxSpec.TimingWorkItemId"/>) omit those entries.
    /// </summary>
    IReadOnlyList<(WorkItemId WorkItemId, IShutdownTeardownSandbox Sandbox)> SnapshotActiveSandboxes();
}

/// <summary>
/// Lightweight projection of a provider-owned sandbox that is still active for
/// a work item. Watchdog consumers may treat stable ownership as a progress
/// signal for detached VM-local work and use <see cref="Status"/> to report a
/// richer reason when providers can expose changing activity.
/// </summary>
public sealed record ActiveSandboxProgress(WorkItemId WorkItemId, string SandboxId, string? Status = null);

/// <summary>
/// Optional provider capability for reporting active sandbox ownership.
/// Implementations should omit sandboxes whose owning work item is unknown.
/// </summary>
public interface IActiveSandboxProgressProvider
{
    /// <summary>
    /// Snapshot of currently-active sandboxes, projected to the fields progress
    /// monitoring needs.
    /// </summary>
    IReadOnlyList<ActiveSandboxProgress> SnapshotActiveSandboxProgress();
}

/// <summary>
/// Empty active-sandbox progress provider used when the configured sandbox
/// provider has no active-sandbox progress capability.
/// </summary>
public sealed class NullActiveSandboxProgressProvider : IActiveSandboxProgressProvider
{
    public static NullActiveSandboxProgressProvider Instance { get; } = new();

    private NullActiveSandboxProgressProvider() { }

    public IReadOnlyList<ActiveSandboxProgress> SnapshotActiveSandboxProgress() => [];
}

/// <summary>
/// Provider capability exposing the executor-host placement pool behind a
/// sandbox provider. Local providers return no pool; distributed providers can
/// surface per-host capacity, cordon, and health so dashboards show fan-out
/// limits instead of hiding them behind the global sandbox admission count.
/// </summary>
public interface ISandboxHostPoolSnapshot
{
    IReadOnlyList<SandboxHostPoolEntry> SnapshotHostPool();
}

public sealed record SandboxHostPoolEntry(
    string HostId,
    int Capacity,
    int Reserved,
    bool Cordoned,
    bool ConfiguredHealthy,
    bool RuntimeHealthy,
    string? RuntimeUnhealthyReason,
    DateTimeOffset? RuntimeUnhealthyUntil,
    IReadOnlyList<string> AllowedNetworkProfiles);

/// <summary>
/// Optional provider capability paired with <see cref="ISuspendableSandbox"/>.
/// The startup resume handler uses <see cref="ResumeSandboxAsync"/> to start
/// each persisted VM by name and adopt its still-running agent process.
/// </summary>
public interface ISuspendingSandboxProvider
{
    /// <summary>
    /// Best-effort resume of a previously-suspended sandbox by name. Implementations
    /// should treat "VM not found" / "already running" as non-fatal so the
    /// startup handler can clear the persisted bookkeeping for items whose
    /// suspended VM no longer exists.
    /// </summary>
    Task ResumeSandboxAsync(string name, CancellationToken ct);

    /// <summary>
    /// Resumes a scoped lifecycle snapshot. Plain providers use the name-only
    /// implementation only for unscoped snapshots; composites and multi-host
    /// providers override this overload so opaque provider or host identity can
    /// select the exact resource without ambiguous ownership rediscovery.
    /// </summary>
    Task ResumeSandboxAsync(ManagedSandboxInfo sandbox, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        if (sandbox.LifecycleProviderId is not null || sandbox.HostId is not null)
        {
            throw new NotSupportedException(
                "This sandbox provider cannot interpret a provider- or host-scoped resume snapshot.");
        }
        return ResumeSandboxAsync(sandbox.Name, ct);
    }

    /// <summary>
    /// R8-core: after <see cref="ResumeSandboxAsync"/> brings the VM back to
    /// Running, the startup resume handler asks the provider to wait for the
    /// in-VM agent process to finish, streaming what's left of
    /// <paramref name="agentLogPath"/> to <paramref name="logSink"/> as it goes.
    /// Completion is signalled by the <c>codeybox-exec</c> wrapper writing
    /// <paramref name="agentLogPath"/><c>.exit</c> containing the agent's exit
    /// code. Returns the parsed exit code on completion; returns null when
    /// the deadline elapses before the marker appears (the orchestrator falls
    /// back to the stranded-item recovery path in that case). Implementations
    /// that cannot inspect the in-VM filesystem (process / bubblewrap) return
    /// null immediately.
    /// </summary>
    /// <param name="vmName">VM whose log to tail. Must validate against the provider's name allow-list.</param>
    /// <param name="agentLogPath">Absolute in-VM path to the tee'd log file the agent wrapper writes.</param>
    /// <param name="logSink">Receives appended log bytes as the agent emits them post-resume.</param>
    /// <param name="deadline">Best-effort cap on the wait window; null lets the caller's cancellation token drive timing.</param>
    /// <param name="ct">Cancellation token. Cancelling returns immediately without throwing.</param>
    /// <returns>Agent exit code, or null if the wait timed out, the file is absent, or the provider does not support adoption.</returns>
    Task<int?> WaitForAdoptedAgentCompletionAsync(
        string vmName,
        string agentLogPath,
        Action<string>? logSink,
        TimeSpan? deadline,
        CancellationToken ct) => Task.FromResult<int?>(null);

    /// <summary>
    /// R8-core: after the resumed in-VM agent has finished (
    /// <see cref="WaitForAdoptedAgentCompletionAsync"/> returned an exit code),
    /// promote whatever the agent committed inside the VM into a real
    /// preempt-checkpoint git ref on origin so the orchestrator's standard
    /// recovery flow (see <c>DeadWorkerReaper.RecoverWorkItemAsync</c>) can
    /// re-enqueue the work item with a non-null
    /// <see cref="WorkItem.PreemptCheckpoint"/> instead of marking it Failed
    /// for "Working without a preempt checkpoint".
    ///
    /// <para>Operation, executed inside the resumed VM:</para>
    /// <list type="number">
    ///   <item><c>git add -A</c> in <paramref name="workingDir"/> to capture
    ///   any uncommitted agent output;</item>
    ///   <item>remove and positively guard historical repository-local agent
    ///   scratchpad paths; provider state is private and never belongs in Git;</item>
    ///   <item><c>git commit --allow-empty</c> so the push is non-empty even
    ///   when the agent had nothing dirty;</item>
    ///   <item><c>git push origin HEAD:<paramref name="refName"/></c>.</item>
    /// </list>
    ///
    /// <para>Returns true on a successful push; false on any in-VM failure
    /// (the resume service falls back to clearing suspend bookkeeping with no
    /// checkpoint, and the existing stranded-item path takes over).</para>
    ///
    /// <para>Implementations that cannot exec in the VM (non-VM providers)
    /// return false — those providers do not participate in suspend/resume.</para>
    /// </summary>
    /// <param name="vmName">Resumed (running) VM to operate inside.</param>
    /// <param name="workingDir">Absolute in-VM working directory containing the git repo (typically <c>/work</c>).</param>
    /// <param name="refName">Fully-qualified remote ref to push HEAD to (e.g. <c>refs/heads/codeybox/preempt/&lt;id&gt;</c>).</param>
    /// <param name="commitMessage">Commit message for the synthetic checkpoint commit. Must contain no shell metacharacters.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<bool> PushSuspendedVmCheckpointRefAsync(
        string vmName,
        string workingDir,
        string refName,
        string commitMessage,
        CancellationToken ct) => Task.FromResult(false);

    /// <summary>
    /// Startup reconciliation hook: on boot, sweep any managed sandboxes left in
    /// transitional / suspend-lifecycle state from a prior unclean shutdown and
    /// attempt to bring them back to a clean state so the standard recovery path
    /// (resume by mapping, or reaper) can run against settled state.
    ///
    /// <para>The hook is supplied the set of VM names the orchestrator still
    /// has live <c>SuspendedVmName</c> mappings for; the provider MUST NOT touch
    /// any VM in that set — those are the items the resume handler is about to
    /// reattach. Everything else in a suspend-lifecycle or unknown state is a
    /// genuine orphan from a crash mid-shutdown.</para>
    ///
    /// <para>Recovery sequence per orphaned VM (provider-specific): try a clean
    /// stop first to release any qemu disk-image write-lock the orphaned process
    /// is holding, then proceed to dispose. Surface a clear leak event for
    /// anything that still won't release after the recovery sequence.</para>
    ///
    /// <para>Returns the names of orphaned VMs the provider could not recover
    /// (still wedged, requiring operator/root attention). Empty list when there
    /// is nothing to do or recovery succeeded for every orphan. Implementations
    /// that don't model a persistent suspend lifecycle (non-VM providers) return
    /// an empty list.</para>
    /// </summary>
    Task<IReadOnlyList<string>> ReconcileStuckSandboxesAsync(
        IReadOnlySet<string> liveSuspendedNames,
        CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>([]);
}

/// <summary>
/// Optional provider capability: implementations that model a content-hashed
/// baseline image expose the current ref for a given (profile, flavor) here so
/// the orchestrator can stamp <see cref="WorkItem.BaselineImageRef"/> at pickup
/// time. Providers without baselines (process, bubblewrap) do not implement
/// this — for those, the pipeline leaves the field null and no pinning happens.
/// </summary>
public interface IBaselineImageResolver
{
    /// <summary>
    /// Returns the baseline image ref the provider would use right now for a
    /// sandbox with the given network profile and flavor, based on its live
    /// config. Returns null when the provider cannot produce a baseline for
    /// this combination (e.g. <c>UseBaselineImages=false</c>, no network
    /// profile selected, or the profile is unknown). The caller must treat
    /// null as "no pin" — the work item proceeds as before.
    /// </summary>
    string? ResolveBaselineRef(string? profileName, SandboxProfileFlavor flavor);

    /// <summary>
    /// Lists every baseline image currently present on the host that this
    /// provider considers a baseline. Used by the
    /// <see cref="CodeyBox.Orchestrator.BaselineImageReaper"/> to compute the
    /// orphan set (provider baselines minus the live-ref set from the work
    /// store). An empty list is authoritative evidence that enumeration
    /// completed and found no baselines. Implementations must throw when the
    /// inventory cannot be enumerated completely so lifecycle and admission
    /// callers do not mistake an unknown inventory for proven absence.
    /// </summary>
    Task<IReadOnlyList<BaselineImageInfo>> ListBaselineImagesAsync(CancellationToken ct);

    /// <summary>
    /// Best-effort dispose of a single baseline image by name. Invoked by the
    /// GC reaper on orphans past the grace window. Implementations may throw
    /// on failure; callers wrap in try/catch.
    /// </summary>
    Task DisposeBaselineImageAsync(string name, CancellationToken ct);
}

/// <summary>
/// Optional provider capability: eagerly ensures a clonable baseline image
/// exists for a profile/flavor before a dispatch gate asks for a sandbox. This
/// keeps callers off the provider's slow live cloud-init launch path.
/// </summary>
public interface IBaselineImageProvisioner
{
    /// <summary>
    /// Ensures the baseline image named by <paramref name="pinnedBaselineRef"/>
    /// (or the provider's active ref when null) exists for
    /// <paramref name="profileName"/> and <paramref name="flavor"/>. Returns the
    /// clonable baseline ref/name, or null when the provider cannot clone this
    /// target (for example baseline images are disabled).
    /// </summary>
    Task<string?> EnsureBaselineImageAsync(
        string profileName,
        SandboxProfileFlavor flavor,
        string? pinnedBaselineRef,
        CancellationToken ct);
}

/// <summary>
/// Null Object provisioner for sandbox providers that cannot eagerly prepare
/// clonable baseline images.
/// </summary>
public sealed class NullBaselineImageProvisioner : IBaselineImageProvisioner
{
    public static readonly NullBaselineImageProvisioner Instance = new();

    private NullBaselineImageProvisioner() { }

    public Task<string?> EnsureBaselineImageAsync(
        string profileName,
        SandboxProfileFlavor flavor,
        string? pinnedBaselineRef,
        CancellationToken ct)
    {
        _ = profileName;
        _ = flavor;
        _ = pinnedBaselineRef;
        _ = ct;
        return Task.FromResult<string?>(null);
    }
}

/// <summary>
/// Snapshot of one baseline image on the host, returned by
/// <see cref="IBaselineImageResolver.ListBaselineImagesAsync"/>.
/// </summary>
/// <param name="Name">VM / baseline name (e.g. <c>cb-baseline-abc123</c>).</param>
/// <param name="CreatedAt">Best-effort creation timestamp; null if not derivable.</param>
/// <param name="DiskBytes">Reported disk usage; null when unavailable.</param>
/// <param name="LifecycleProviderId">
/// Optional opaque identifier for the lifecycle provider that reported this
/// snapshot. Composite resolvers populate it so same-named baseline resources
/// remain distinct while admission cleanup is reconciled.
/// </param>
public sealed record BaselineImageInfo(
    string Name,
    DateTimeOffset? CreatedAt,
    long? DiskBytes,
    string? LifecycleProviderId = null);

/// <summary>
/// Null Object resolver for <see cref="IBaselineImageResolver"/>. Returned by
/// the DI factory when the registered sandbox provider does not implement
/// the capability (process / bubblewrap). Lets consumers always receive a
/// non-null resolver without having to special-case null at every call site.
/// </summary>
public sealed class NullBaselineImageResolver : IBaselineImageResolver
{
    public static readonly NullBaselineImageResolver Instance = new();
    private NullBaselineImageResolver() { }

    public string? ResolveBaselineRef(string? profileName, SandboxProfileFlavor flavor) => null;

    public Task<IReadOnlyList<BaselineImageInfo>> ListBaselineImagesAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<BaselineImageInfo>>([]);

    public Task DisposeBaselineImageAsync(string name, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>
/// Description of a sandbox to provision. Mounts and environment are the only
/// channels by which the host injects state into the sandbox.
/// </summary>
public sealed record SandboxSpec
{
    public required string ImageReference { get; init; }
    public SandboxPurpose Purpose { get; init; } = SandboxPurpose.WorkItem;
    public IReadOnlyList<SandboxMount> Mounts { get; init; } = [];
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();
    public SandboxResourceLimits Limits { get; init; } = SandboxResourceLimits.Default;
    public SandboxNetworkPolicy Network { get; init; } = SandboxNetworkPolicy.Denied;
    public SandboxProfileFlavor Flavor { get; init; } = SandboxProfileFlavor.Headless;
    public string WorkingDirectory { get; init; } = "/work";

    /// <summary>
    /// Optional timing context. When set, sandbox providers emit vm.* / bwrap.*
    /// lifecycle timing rows for this work item using ITimingStore.
    /// </summary>
    public WorkItemId? TimingWorkItemId { get; init; }
    public string? TimingPhase { get; init; }

    /// <summary>
    /// Content-hashed identifier of the sandbox baseline image to pin this
    /// sandbox to. Stamped on the work item at pickup time and threaded back
    /// through subsequent <see cref="ISandboxProvider.CreateAsync"/> calls so
    /// matching target profiles keep using the pinned baseline even when the
    /// operator edits baseline-contributing config (ExtraRuncmd,
    /// ExtraCloudInit, cloud-init contents) mid-flight. Providers whose clone
    /// source carries target-specific attachments, such as Multipass network
    /// bridges, must reject or recompute pins that do not match the requested
    /// profile/flavor.
    /// Null = provider falls back to computing the ref from live config
    /// (backward-compatible for items created before the stamping logic
    /// landed, and for providers that don't model baselines).
    /// </summary>
    public string? BaselineImageRef { get; init; }

    /// <summary>
    /// Exact provider-owned stopped sandbox to adopt instead of provisioning a
    /// fresh sandbox. Providers must reject mismatched provider ids, ownership,
    /// work-item context, specifications, or capability tokens.
    /// </summary>
    public SandboxRecoveryLease? RecoveryLease { get; init; }
}

public enum SandboxProfileFlavor
{
    Headless = 0,
    Graphical = 1,
}

public enum SandboxInputEventType
{
    Click,
    Key,
    Move,
    Scroll,
    Type,
}

public sealed record SandboxInputEvent
{
    public required SandboxInputEventType Type { get; init; }
    public int? X { get; init; }
    public int? Y { get; init; }
    public string? Key { get; init; }
    public string? Text { get; init; }
}

public sealed record SandboxAccessibilitySnapshot
{
    public string? Role { get; init; }
    public string? Name { get; init; }
    public string? Text { get; init; }
    public string? ElementType { get; init; }
}

public static class SandboxInputEventValidation
{
    public const int DefaultMaxEvents = 32;
    public const int DefaultMaxTextUtf8Bytes = 4096;
    public const int DefaultMaxKeyUtf8Bytes = 128;
    public const int DefaultMaxCoordinate = 32767;
    public const int DefaultMaxScrollMagnitude = 1000;

    public static void Validate(
        IReadOnlyList<SandboxInputEvent> events,
        int maxEvents = DefaultMaxEvents,
        int maxTextUtf8Bytes = DefaultMaxTextUtf8Bytes,
        int maxKeyUtf8Bytes = DefaultMaxKeyUtf8Bytes,
        int maxCoordinate = DefaultMaxCoordinate,
        int maxScrollMagnitude = DefaultMaxScrollMagnitude)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (events.Count == 0)
            throw new ArgumentException("Graphical input requires at least one event.", nameof(events));
        if (events.Count > maxEvents)
            throw new ArgumentOutOfRangeException(nameof(events), $"Graphical input is limited to {maxEvents} events per call.");

        for (var i = 0; i < events.Count; i++)
        {
            var inputEvent = events[i];
            if (inputEvent is null)
                throw new ArgumentException($"Graphical input event {i} is null.", nameof(events));

            switch (inputEvent.Type)
            {
                case SandboxInputEventType.Click:
                    if (inputEvent.X.HasValue != inputEvent.Y.HasValue)
                        throw new ArgumentException("Click events must provide both X and Y, or neither.", nameof(events));
                    ValidateCoordinate(inputEvent.X, maxCoordinate, nameof(SandboxInputEvent.X));
                    ValidateCoordinate(inputEvent.Y, maxCoordinate, nameof(SandboxInputEvent.Y));
                    break;

                case SandboxInputEventType.Key:
                    ValidateText(inputEvent.Key, maxKeyUtf8Bytes, "Key events require Key.", nameof(SandboxInputEvent.Key));
                    break;

                case SandboxInputEventType.Move:
                    if (inputEvent.X is null || inputEvent.Y is null)
                        throw new ArgumentException("Move events require X and Y.", nameof(events));
                    ValidateCoordinate(inputEvent.X, maxCoordinate, nameof(SandboxInputEvent.X));
                    ValidateCoordinate(inputEvent.Y, maxCoordinate, nameof(SandboxInputEvent.Y));
                    break;

                case SandboxInputEventType.Scroll:
                    ValidateScroll(inputEvent, maxScrollMagnitude);
                    break;

                case SandboxInputEventType.Type:
                    ValidateText(
                        inputEvent.Text,
                        maxTextUtf8Bytes,
                        "Type events require Text.",
                        nameof(SandboxInputEvent.Text),
                        allowWhitespace: true);
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(events), inputEvent.Type, "Unknown input event type.");
            }
        }
    }

    private static void ValidateCoordinate(int? value, int maxCoordinate, string name)
    {
        if (value is null)
            return;
        if (value.Value < 0 || value.Value > maxCoordinate)
            throw new ArgumentOutOfRangeException(name, $"{name} must be between 0 and {maxCoordinate}.");
    }

    private static void ValidateScroll(SandboxInputEvent inputEvent, int maxScrollMagnitude)
    {
        var vertical = inputEvent.Y ?? 0;
        var horizontal = inputEvent.X ?? 0;
        if (vertical == 0 && horizontal == 0)
            throw new ArgumentException("Scroll events require a non-zero X or Y amount.", nameof(inputEvent));
        if (vertical != 0 && horizontal != 0)
            throw new ArgumentException("Scroll events support one axis at a time.", nameof(inputEvent));
        if (Math.Abs((long)(vertical != 0 ? vertical : horizontal)) > maxScrollMagnitude)
            throw new ArgumentOutOfRangeException(nameof(inputEvent), $"Scroll amount must be <= {maxScrollMagnitude}.");
    }

    private static void ValidateText(
        string? value,
        int maxUtf8Bytes,
        string missingMessage,
        string fieldName,
        bool allowWhitespace = false)
    {
        if (value is null || (allowWhitespace ? value.Length == 0 : string.IsNullOrWhiteSpace(value)))
            throw new ArgumentException(missingMessage, fieldName);
        var byteCount = Encoding.UTF8.GetByteCount(value);
        if (byteCount > maxUtf8Bytes)
            throw new ArgumentOutOfRangeException(fieldName, $"{fieldName} must be <= {maxUtf8Bytes} UTF-8 bytes.");
    }
}

/// <summary>
/// Mount of a host path into the sandbox. <see cref="ReadOnly"/> mounts are
/// strongly preferred; the writable agent workspace is the only common
/// exception. <see cref="Tmpfs"/> mounts back the path with an in-memory
/// filesystem of <paramref name="SizeBytes"/> (used for credentials).
/// </summary>
public sealed record SandboxMount
{
    public required string SandboxPath { get; init; }
    public string? HostPath { get; init; }
    public bool ReadOnly { get; init; } = true;
    public bool Tmpfs { get; init; }
    public long? SizeBytes { get; init; }

    // Provider-neutral isolation hint for read-only host sources that must not
    // be shared with the sandbox by reference. Providers can satisfy it with a
    // kernel read-only bind, a staged copy, or another equivalent isolation
    // strategy; callers should treat it as "do not expose the mutable source".
    public bool SnapshotForIsolation { get; init; }
}

/// <summary>
/// Thrown by an <see cref="ISandboxProvider"/> when a bind mount fails because
/// the host source path does not exist at mount time. Carries
/// <see cref="HostPath"/> so the orchestrator can decide whether the path is
/// one it knows how to recreate (e.g. the merge-phase isolated bare clone) and
/// retry <see cref="ISandboxProvider.CreateAsync"/> after re-creating the
/// source — keeping recovery in orchestration rather than threading a
/// behavioral callback through the cross-provider mount DTO.
/// </summary>
public sealed class SandboxMountSourceMissingException : Exception
{
    public string HostPath { get; }

    public SandboxMountSourceMissingException(string hostPath, string message)
        : base(message)
    {
        HostPath = hostPath;
    }

    public SandboxMountSourceMissingException(string hostPath, string message, Exception inner)
        : base(message, inner)
    {
        HostPath = hostPath;
    }
}

public sealed record SandboxResourceLimits
{
    public int? CpuCount { get; init; }
    public long? MemoryBytes { get; init; }
    public long? DiskBytes { get; init; }
    public TimeSpan? WallClock { get; init; }

    public static SandboxResourceLimits Default { get; } = new()
    {
        CpuCount = 2,
        // 12 GiB: 2 GiB was tight enough that qemu OOM-crashed mid-task under load
        // (multipass socket then drops out, surfacing as "cannot connect to the
        // multipass socket" in CodeyBox). Real-world agent work — LLM inference
        // helpers, dotnet build/test, npm install graphs — routinely peaks well
        // above 2 GiB, especially with parallel audits in the same VM. qcow2 disk
        // stays sparse, so the per-VM cost is only paid when actually used; the
        // RAM bump matters when multiple VMs are running concurrently. Operators
        // running many concurrent workers on small hosts can override via spec.
        MemoryBytes = 12L * 1024 * 1024 * 1024,
        // A COW clone can never be smaller than its baseline, and a baseline that
        // bakes in a package-cache seed (e.g. a multi-GiB NuGet cache) plus the
        // agent toolchain runs several GiB before any work lands. 16 GiB keeps
        // clones comfortably above such baselines with room for build output;
        // qcow2/ZFS clones stay sparse, so the ceiling is only paid when used.
        DiskBytes = 16L * 1024 * 1024 * 1024,
        WallClock = TimeSpan.FromMinutes(60),
    };
}

/// <summary>
/// Sandbox network policy. Egress filtering is host-side: the provider
/// attaches the sandbox to the host bridge mapped from
/// <see cref="ProfileName"/>, and the bridge's nftables rules (set up
/// once by <c>scripts/setup-host-networks.sh</c>) drop everything not
/// on that profile's allowlist. The agent cannot disable this —
/// enforcement lives in the host kernel.
///
/// <para><see cref="AllowedHosts"/> is a documentation/intent field
/// describing what the agent expects to reach; it does not by itself
/// install any in-sandbox rule. The Bubblewrap provider uses it only
/// to gate "any network" vs "no network". The Process provider has no
/// network isolation at all.</para>
/// </summary>
public sealed record SandboxNetworkPolicy
{
    /// <summary>Hostnames the sandbox is allowed to reach. Empty = no egress.</summary>
    public IReadOnlyList<string> AllowedHosts { get; init; } = [];

    /// <summary>If non-null, sandbox can reach this host:port for git operations.</summary>
    public string? HostGitEndpoint { get; init; }

    /// <summary>
    /// Name of a pre-configured host-side network profile. When set, the
    /// provider attaches the sandbox to the matching host bridge (and its
    /// host-enforced egress rules) instead of relying on in-VM filtering.
    /// The provider's options map this name to a bridge name.
    /// </summary>
    public string? ProfileName { get; init; }

    public static SandboxNetworkPolicy Denied { get; } = new();
}

/// <summary>Provider-neutral validation for POSIX environment-variable names.</summary>
public static class SandboxEnvironmentVariableName
{
    /// <summary>Maximum characters accepted in one environment-variable name.</summary>
    public const int MaximumLength = 128;

    /// <summary>Validates a bounded ASCII POSIX environment-variable identifier.</summary>
    public static void Validate(string value, string parameterName)
    {
        if (value is null || value.Length == 0)
            throw new ArgumentException("Environment variable name must be non-empty.", parameterName);
        if (value.Length > MaximumLength)
            throw new ArgumentException("Environment variable name exceeds the size limit.", parameterName);
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Environment variable name must be non-empty.", parameterName);
        if (!IsAsciiLetter(value[0]) && value[0] != '_')
            throw new ArgumentException("Environment variable name is not a POSIX identifier.", parameterName);
        for (var index = 1; index < value.Length; index++)
        {
            var character = value[index];
            if (IsAsciiLetter(character) || character is >= '0' and <= '9' || character == '_')
                continue;
            throw new ArgumentException("Environment variable name is not a POSIX identifier.", parameterName);
        }
    }

    private static bool IsAsciiLetter(char value) =>
        value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';
}

public sealed record SandboxExec
{
    /// <summary>Maximum distinct environment-variable removals requested by one exec.</summary>
    public const int MaximumEnvironmentVariablesToUnset = 256;

    private IReadOnlyList<string> _environmentVariablesToUnset = [];

    public required IReadOnlyList<string> Argv { get; init; }
    public string? WorkingDirectory { get; init; }
    public IReadOnlyDictionary<string, string>? ExtraEnvironment { get; init; }
    /// <summary>
    /// Bounded immutable set of environment variables that must be absent from
    /// the launched process. Providers apply these removals after merging their
    /// baseline/spec environment and <see cref="ExtraEnvironment"/>. Removal
    /// therefore wins deterministically when a name is also present in
    /// <see cref="ExtraEnvironment"/>.
    /// </summary>
    public IReadOnlyList<string> EnvironmentVariablesToUnset
    {
        get => _environmentVariablesToUnset;
        init => _environmentVariablesToUnset = SnapshotEnvironmentVariablesToUnset(value);
    }
    /// <summary>
    /// Marks <see cref="ExtraEnvironment"/> as secret-bearing. Providers must
    /// deliver it without placing values in host-visible command argv and must
    /// not fall back to an inline transport if secure delivery fails.
    /// </summary>
    public bool EnvironmentContainsSecrets { get; init; }
    public string? Stdin { get; init; }
    public int? MaxStdoutBytes { get; init; }
    public int? MaxStderrBytes { get; init; }
    public bool KillOnOutputLimit { get; init; } = true;
    public SandboxAgentOutputTransportPreference AgentOutputTransport { get; init; } =
        SandboxAgentOutputTransportPreference.ExecPipe;
    public SandboxExecLaunchMode LaunchMode { get; init; } = SandboxExecLaunchMode.Attached;

    /// <summary>
    /// Optional callback invoked per stdout chunk as the process emits it.
    /// Sandbox provider best-effort: chunks may aggregate or split arbitrarily;
    /// receiver MUST not rely on line boundaries. Called from arbitrary
    /// threads; receiver is responsible for thread safety.
    /// </summary>
    public Action<string>? StdoutChunkCallback { get; init; }
    public Action<string>? StderrChunkCallback { get; init; }

    /// <summary>
    /// Applies the validated removal request at a provider's final environment
    /// sink. Call only after every provider/spec/exec environment merge and
    /// immediately before process launch or guest-environment serialization.
    /// </summary>
    public void ApplyEnvironmentRemovals(Action<string> remove)
    {
        ArgumentNullException.ThrowIfNull(remove);
        foreach (var name in _environmentVariablesToUnset)
            remove(name);
    }

    private static IReadOnlyList<string> SnapshotEnvironmentVariablesToUnset(
        IReadOnlyList<string> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Count > MaximumEnvironmentVariablesToUnset)
        {
            throw new ArgumentException(
                $"An exec cannot unset more than {MaximumEnvironmentVariablesToUnset} environment variables.",
                nameof(EnvironmentVariablesToUnset));
        }

        var snapshot = new List<string>(source.Count);
        var distinct = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in source)
        {
            if (snapshot.Count >= MaximumEnvironmentVariablesToUnset)
            {
                throw new ArgumentException(
                    $"An exec cannot unset more than {MaximumEnvironmentVariablesToUnset} environment variables.",
                    nameof(EnvironmentVariablesToUnset));
            }
            SandboxEnvironmentVariableName.Validate(name, nameof(EnvironmentVariablesToUnset));
            if (!distinct.Add(name))
            {
                throw new ArgumentException(
                    "Environment variable removal names must be unique.",
                    nameof(EnvironmentVariablesToUnset));
            }
            snapshot.Add(name);
        }
        return Array.AsReadOnly(snapshot.ToArray());
    }
}

public enum SandboxAgentOutputTransportPreference
{
    ExecPipe = 0,
    PreferHttpIngest = 1,
}

public enum SandboxAgentOutputTransportKind
{
    ExecPipe = 0,
    HttpIngest = 1,
}

public enum SandboxExecLaunchMode
{
    Attached = 0,
    DetachedBatch = 1,
}

public enum SandboxBatchLaunchMode
{
    Attached = 0,
    Detached = 1,
}

public sealed record SandboxExecResult(
    int ExitCode,
    string Stdout,
    string Stderr,
    bool StdoutLimitExceeded = false,
    bool StderrLimitExceeded = false,
    bool ExecutionUnavailable = false)
{
    public bool OutputLimitExceeded => StdoutLimitExceeded || StderrLimitExceeded;
    public bool Success => ExitCode == 0 && !OutputLimitExceeded && !ExecutionUnavailable;
}
