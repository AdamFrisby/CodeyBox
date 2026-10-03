using CodeyBox.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Orchestrator;

/// <summary>
/// The colocated executor: an executor host that runs inside the
/// orchestrator process and gives a single-host deployment working local
/// phase execution with no configuration. It is the same binary and the same
/// code path as a remote executor — an <see cref="ExecutorHostPhaseRunner"/>
/// running the composed <see cref="IExecutorPhaseHandler"/> — identified
/// distinctly as <c>"local"</c> and differing only in that its transport
/// crosses the local filesystem instead of a network hop. The dispatch proxy
/// always places through this host (plus any registered remote hosts), so
/// there is no "no executor registered" case and no second implementation.
/// </summary>
/// <remarks>
/// <para>Automatic, not opt-in: every option carries a safe default and the
/// host composes with whatever sandbox provider the orchestrator already
/// uses. <c>"local"</c> is a reserved host id — a remote host registering
/// under it would collide in placement, so operators must not reuse it.</para>
/// <para>Until an <see cref="IExecutorPhaseHandler"/> is composed the host
/// reports <see cref="HasRunner"/> false and phase execution fails loudly
/// (never silently degrading to another implementation); option validation
/// still fails fast at startup through the normal options path.</para>
/// </remarks>
public sealed class ColocatedExecutorHost
{
    /// <summary>
    /// Stable host id of the colocated executor. Reserved: remote hosts must
    /// not register under it.
    /// </summary>
    public const string HostId = "local";

    /// <summary>Default staging root leaf under the process temp directory.</summary>
    public const string DefaultStagingLeaf = "codeybox-colocated-phases";

    /// <summary>
    /// Placeholder satisfying the runner's absolute-URL validation. The
    /// colocated runner never performs HTTP; every load-bearing field comes
    /// from the colocated options, so this value is intentionally dummy and
    /// must never be treated as a real endpoint to configure.
    /// </summary>
    private const string UnusedOrchestratorBaseUrlPlaceholder = "http://localhost/";

    private readonly ISandboxProvider _sandboxes;
    private readonly IExecutorPhaseHandler? _handler;
    private readonly Func<ColocatedExecutorOptions> _optionsAccessor;
    private readonly Func<ExecutorPhaseDispatchOptions> _dispatchOptionsAccessor;
    private readonly Func<ExecutorPhaseRequest, SandboxSpec>? _specFactory;
    private readonly Func<string?>? _repositoriesRootAccessor;
    private readonly StagingCopyGate _copyGate;
    private readonly ExecutorSandboxTracker _tracker;
    private readonly TimeProvider _clock;
    private readonly ILoggerFactory _loggerFactory;

    private readonly ExecutorHostPhaseRunner? _runner;

    public ColocatedExecutorHost(
        ISandboxProvider sandboxes,
        Func<ColocatedExecutorOptions> optionsAccessor,
        Func<ExecutorPhaseDispatchOptions> dispatchOptionsAccessor,
        IExecutorPhaseHandler? handler = null,
        ExecutorSandboxTracker? tracker = null,
        Func<ExecutorPhaseRequest, SandboxSpec>? sandboxSpecFactory = null,
        TimeProvider? clock = null,
        ILoggerFactory? loggerFactory = null,
        Func<string?>? repositoriesRootAccessor = null,
        StagingCopyGate? copyGate = null)
    {
        _sandboxes = sandboxes ?? throw new ArgumentNullException(nameof(sandboxes));
        _optionsAccessor = optionsAccessor ?? throw new ArgumentNullException(nameof(optionsAccessor));
        _dispatchOptionsAccessor = dispatchOptionsAccessor ?? throw new ArgumentNullException(nameof(dispatchOptionsAccessor));
        _handler = handler;
        _tracker = tracker ?? new ExecutorSandboxTracker();
        _specFactory = sandboxSpecFactory;
        _repositoriesRootAccessor = repositoriesRootAccessor;
        _copyGate = copyGate ?? new StagingCopyGate();
        _clock = clock ?? TimeProvider.System;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        if (handler is not null)
        {
            _runner = new ExecutorHostPhaseRunner(
                _sandboxes,
                _tracker,
                handler,
                RunnerOptions,
                _dispatchOptionsAccessor,
                _specFactory,
                _clock,
                _loggerFactory.CreateLogger<ExecutorHostPhaseRunner>());
        }
    }

    /// <summary>True when a phase handler is composed and the host can accept phases.</summary>
    public bool HasRunner => _runner is not null;

    /// <summary>Sandbox tracker owning the colocated runner's live sandboxes.</summary>
    public ExecutorSandboxTracker Tracker => _tracker;

    /// <summary>Live phase load reported for least-loaded placement.</summary>
    public int ActivePhaseCount => _runner?.ActivePhaseCount ?? 0;

    /// <summary>
    /// Builds the colocated host's registration assertion from current
    /// options. Called per dispatch so hot-reload edits apply without
    /// restart. Validates the options, so a bad edit fails the dispatch
    /// loudly instead of placing under stale attributes.
    /// </summary>
    public ExecutorRegistration GetRegistration()
    {
        var options = _optionsAccessor();
        options.Validate();
        return new ExecutorRegistration
        {
            HostId = HostId,
            MaxConcurrentSandboxes = options.MaxConcurrentSandboxes,
            AllowedNetworkProfiles = [],
            DeclaredCredentials = [.. options.DeclaredCredentials],
            DeclaredCapabilities = [.. ExecutorCapabilityPolicy.EffectiveCapabilities(
                options.DeclaredCapabilities,
                [_sandboxes])],
            Cordoned = false,
            Healthy = true,
        };
    }

    /// <summary>
    /// Resolved staging root for the colocated transport: the configured
    /// absolute root, or a per-process subdirectory of the process temp
    /// directory reserved for the colocated host. The per-process leaf (never
    /// shared with a remote executor's staging root, nor with another
    /// orchestrator process on the same machine) keeps one process from
    /// deleting or copying through a predictable path another process owns.
    /// </summary>
    public string GetStagingRoot()
    {
        var options = _optionsAccessor();
        options.Validate();
        if (!string.IsNullOrWhiteSpace(options.StagingRoot))
            return Path.GetFullPath(options.StagingRoot.Trim());
        return Path.Combine(Path.GetTempPath(), $"{DefaultStagingLeaf}-p{Environment.ProcessId}");
    }

    /// <summary>
    /// Creates a fresh single-dispatch transport over the colocated runner.
    /// Throws <see cref="InvalidOperationException"/> when no phase handler
    /// is composed — failing loudly beats pretending to run a phase that
    /// goes nowhere.
    /// </summary>
    public ColocatedExecutorTransport CreateTransport()
    {
        var runner = _runner
            ?? throw new InvalidOperationException(
                "The colocated executor has no phase handler composed; it cannot accept phases yet. " +
                $"Compose an {nameof(IExecutorPhaseHandler)} to enable local execution through host '{HostId}'.");
        return new ColocatedExecutorTransport(
            HostId, GetStagingRoot(), runner, _repositoriesRootAccessor?.Invoke(), _copyGate);
    }

    private ExecutorOptions RunnerOptions()
    {
        // Adapts the colocated knobs to the runner's options surface. The
        // runner never performs HTTP, so OrchestratorBaseUrl is the unused
        // placeholder above; every load-bearing field (capacity, staging,
        // image, cache bounds) comes from the colocated options. The staging
        // root resolves through GetStagingRoot so transport and runner can
        // never diverge on the default leaf.
        var options = _optionsAccessor();
        options.Validate();
        return new ExecutorOptions
        {
            HostId = HostId,
            OrchestratorBaseUrl = UnusedOrchestratorBaseUrlPlaceholder,
            MaxConcurrentSandboxes = options.MaxConcurrentSandboxes,
            PhaseStagingRoot = GetStagingRoot(),
            PhaseSandboxImageReference = options.PhaseSandboxImageReference ?? string.Empty,
            MaxCachedPhaseResults = options.MaxCachedPhaseResults,
            PhaseResultCacheTtl = options.PhaseResultCacheTtl,
        };
    }
}

/// <summary>
/// Production <see cref="IExecutorPhaseTransportFactory"/>: resolves the
/// colocated host id to a fresh <see cref="ColocatedExecutorTransport"/> and
/// delegates every other host to the chained remote factory (when one is
/// composed). Registered as the dispatch transport factory so zero-config
/// local execution works with no remote transport configured.
/// </summary>
public sealed class ColocatedExecutorTransportFactory : IExecutorPhaseTransportFactory
{
    private readonly ColocatedExecutorHost _local;
    private readonly IExecutorPhaseTransportFactory? _remote;

    public ColocatedExecutorTransportFactory(
        ColocatedExecutorHost local,
        IExecutorPhaseTransportFactory? remote = null)
    {
        _local = local ?? throw new ArgumentNullException(nameof(local));
        _remote = remote;
    }

    public Task<IExecutorPhaseTransport?> ResolveAsync(string hostId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(hostId);
        var normalized = hostId.Trim();
        if (string.Equals(normalized, ColocatedExecutorHost.HostId, StringComparison.Ordinal))
            return Task.FromResult<IExecutorPhaseTransport?>(_local.CreateTransport());
        if (_remote is null)
            return Task.FromResult<IExecutorPhaseTransport?>(null);
        return _remote.ResolveAsync(normalized, ct);
    }
}
