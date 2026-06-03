using System.Text;

namespace CodeyBox.Core;

/// <summary>
/// Builds and starts isolated execution sandboxes. Implementations include a
/// plain-process dev runner (UNSAFE; for local testing only), bubblewrap
/// (namespace isolation, shared kernel), and Multipass (KVM-backed VMs with
/// a separate guest kernel — recommended for production). The orchestrator
/// picks one provider per deployment.
/// </summary>
public interface ISandboxProvider
{
    /// <summary>Stable identifier for diagnostics ("process", "bubblewrap", "multipass").</summary>
    string Name { get; }

    /// <summary>
    /// Provisions a sandbox according to the given spec. The returned handle
    /// holds the running sandbox until disposed; disposal must tear it down
    /// regardless of state.
    /// </summary>
    Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default);

    /// <summary>
    /// Returns all sandboxes on the host that belong to this provider
    /// (i.e. match the <c>codeybox-*</c> naming prefix). Used by the
    /// <see cref="CodeyBox.Orchestrator.SandboxLeakReaper"/> to detect
    /// sandboxes that outlived their work item.
    ///
    /// <para>Implementations that have no persistent sandbox lifecycle
    /// (bubblewrap, process) return an empty list.</para>
    ///
    /// <para>Implementations that shell out to an external tool (multipass)
    /// cache results for a short TTL to avoid hammering the daemon on
    /// repeated API calls.</para>
    /// </summary>
    Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct);

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
}

/// <summary>
/// Snapshot of a sandbox that exists on the host, returned by
/// <see cref="ISandboxProvider.ListAllManagedAsync"/>.
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
public sealed record ManagedSandboxInfo(
    string Name,
    DateTimeOffset? CreatedAt,
    long? DiskBytes,
    bool IsTrackedActive,
    bool HasPreemptMarker = false,
    bool IsSuspendLifecycleOrFrozen = false);

/// <summary>A live sandbox. Disposing destroys it.</summary>
public interface ISandbox : IAsyncDisposable
{
    string Id { get; }

    /// <summary>
    /// Executes a command inside the sandbox. The command is run with
    /// /work as the working directory unless overridden. Output streams are
    /// captured fully; for long-running commands prefer streaming variants
    /// added later.
    /// </summary>
    Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default);

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
}

/// <summary>
/// Optional sandbox capability used during graceful host shutdown. A provider
/// that can preserve an interrupted sandbox should stop it and make subsequent
/// disposal a no-op so cached state can survive the orchestrator restart.
/// </summary>
public interface IPreemptibleSandbox : ISandbox
{
    Task StopAndPreserveAsync(CancellationToken ct = default);
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
    /// True once the suspend-on-shutdown handler has taken ownership of this
    /// VM's teardown via Suspend (RAM frozen), Stop (clean shutdown), or Dispose
    /// (delete --purge). PipelineRunner reads this in its host-shutdown OCE
    /// catch block to short-circuit the legacy in-VM preempt-checkpoint flow
    /// when that flow would hang (Suspend) or fault against a stopped/deleted VM
    /// (Stop/Dispose). Suspend mode flips this implicitly via
    /// <see cref="IsSuspended"/>; Stop and Dispose call
    /// <see cref="MarkOwnedByShutdownHandler"/>.
    /// </summary>
    bool IsOwnedByShutdownHandler => IsSuspended;

    /// <summary>
    /// Flips <see cref="IsOwnedByShutdownHandler"/> to true. Called by
    /// <c>SandboxSuspendOnShutdownService</c> before Stop/Dispose teardown begins
    /// so PipelineRunner sees the "skip checkpoint" signal even though the
    /// suspend path was not taken. Default no-op: fakes that don't track teardown
    /// ownership keep <see cref="IsOwnedByShutdownHandler"/> at the
    /// <see cref="IsSuspended"/> fallback.
    /// </summary>
    void MarkOwnedByShutdownHandler() { }

    /// <summary>
    /// Best-effort RAM size of this sandbox in bytes, or null when the provider
    /// cannot report it. The suspend-on-shutdown handler scales the per-VM
    /// suspend timeout by this value: <c>multipass suspend</c> writes the whole
    /// RAM image to disk, so a 12 GiB VM under load legitimately takes far longer
    /// than a 1 GiB idle one. Null falls back to the flat floor timeout.
    /// </summary>
    long? MemoryBytes => null;
}

/// <summary>
/// Shared policy for how long a RAM-snapshot suspend is allowed to take, scaled
/// by VM RAM size. Centralised so the shutdown suspend handler's per-VM timeout
/// (<see cref="CodeyBox.Orchestrator.SandboxSuspendOnShutdownService"/>), the
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
    /// the worst-case suspend drain, not just a single VM. The suspend-on-shutdown
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
    /// truncated by SIGKILL. When <paramref name="suspendsOnShutdown"/> is set,
    /// the ceiling is the worst-case suspend drain
    /// (<see cref="HostShutdownReserve"/>:
    /// <c>ceil(maxConcurrent / maxParallelSuspends)</c> waves of the largest
    /// per-VM budget) STACKED ON TOP OF the requested <paramref name="grace"/>.
    /// The two windows are sequential, not overlapping: suspend-on-shutdown runs
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
    /// <param name="suspendsOnShutdown">True when the host must reserve suspend budget for a suspend-capable, hot-reloadable shutdown path.</param>
    /// <param name="grace">Baseline shutdown grace (request-drain / preempt-checkpoint window).</param>
    /// <param name="maxConcurrentSandboxes">Upper bound on concurrently in-flight (hence suspendable) VMs.</param>
    /// <param name="maxParallelSuspends">Parallel-suspend batch size; defaults to <see cref="DefaultMaxParallelSuspends"/>.</param>
    /// <param name="maxVmMemoryBytes">Largest per-VM RAM the deployment provisions; null uses <see cref="SandboxResourceLimits.Default"/>.</param>
    public static TimeSpan ResolveHostShutdownTimeout(
        bool suspendsOnShutdown,
        TimeSpan grace,
        int maxConcurrentSandboxes,
        int maxParallelSuspends = DefaultMaxParallelSuspends,
        long? maxVmMemoryBytes = null)
    {
        if (!suspendsOnShutdown)
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
    /// <c>SnapshotSuspendableActive</c>.
    /// </summary>
    void PauseDispatch();
}

/// <summary>
/// Optional provider capability paired with <see cref="ISuspendableSandbox"/>.
/// The orchestrator's suspend-on-shutdown hosted service uses
/// <see cref="SnapshotSuspendableActive"/> to enumerate sandboxes that should
/// be frozen on <c>ApplicationStopping</c>, and the startup resume handler
/// uses <see cref="ResumeSandboxAsync"/> to start each persisted VM by name.
/// </summary>
public interface ISuspendingSandboxProvider
{
    /// <summary>
    /// Snapshot of currently-active sandboxes that can be suspended, paired
    /// with the work item that owns each entry. Implementations that
    /// internally use a <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey,TValue}"/>
    /// or other snapshot-safe data structure return entries that are
    /// consistent with concurrent disposals — a sandbox racing dispose may
    /// still appear here, but its <see cref="ISuspendableSandbox.SuspendAsync"/>
    /// is a no-op once the sandbox is disposed. Implementations that cannot
    /// determine the owner (e.g. an in-process <c>CreateAsync</c> that did not
    /// pass <see cref="SandboxSpec.TimingWorkItemId"/>) omit those entries.
    /// </summary>
    IReadOnlyList<(WorkItemId WorkItemId, ISuspendableSandbox Sandbox)> SnapshotSuspendableActive();

    /// <summary>
    /// Best-effort resume of a previously-suspended sandbox by name. Implementations
    /// should treat "VM not found" / "already running" as non-fatal so the
    /// startup handler can clear the persisted bookkeeping for items whose
    /// suspended VM no longer exists.
    /// </summary>
    Task ResumeSandboxAsync(string name, CancellationToken ct);

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
    ///   <item>ensure <c>.codeybox/preempt-scratchpad.md</c> exists (so the
    ///   resumable agent runner has something to restore);</item>
    ///   <item><c>git add -A</c> in <paramref name="workingDir"/> to capture
    ///   any uncommitted agent output;</item>
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
    /// orphan set (multipass baselines minus the live-ref set from the work
    /// store). Returns an empty list when the provider has no baselines or
    /// cannot enumerate them.
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
public sealed record BaselineImageInfo(string Name, DateTimeOffset? CreatedAt, long? DiskBytes);

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
        DiskBytes = 8L * 1024 * 1024 * 1024,
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

public sealed record SandboxExec
{
    public required IReadOnlyList<string> Argv { get; init; }
    public string? WorkingDirectory { get; init; }
    public IReadOnlyDictionary<string, string>? ExtraEnvironment { get; init; }
    public string? Stdin { get; init; }

    /// <summary>
    /// Optional callback invoked per stdout chunk as the process emits it.
    /// Sandbox provider best-effort: chunks may aggregate or split arbitrarily;
    /// receiver MUST not rely on line boundaries. Called from arbitrary
    /// threads; receiver is responsible for thread safety.
    /// </summary>
    public Action<string>? StdoutChunkCallback { get; init; }
    public Action<string>? StderrChunkCallback { get; init; }
}

public sealed record SandboxExecResult(int ExitCode, string Stdout, string Stderr)
{
    public bool Success => ExitCode == 0;
}
