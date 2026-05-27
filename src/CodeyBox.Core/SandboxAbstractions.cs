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
public sealed record ManagedSandboxInfo(
    string Name,
    DateTimeOffset? CreatedAt,
    long? DiskBytes,
    bool IsTrackedActive,
    bool HasPreemptMarker = false);

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
