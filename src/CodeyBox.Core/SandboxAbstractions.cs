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
/// True when this sandbox was created by the current orchestrator process
/// and has not yet been disposed. False means the sandbox exists on the
/// host but the current process has no record of creating it — the primary
/// indicator of a leak.
/// </param>
/// <param name="HasPreemptMarker">
/// True when the sandbox root carries the graceful-shutdown preempt marker.
/// Such sandboxes are intentionally preserved and must not be treated as leaks.
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
