using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using CodeyBox.Core;
using CodeyBox.Sandbox;

namespace CodeyBox.Orchestrator;

public sealed class WorkSandboxContext : IAsyncDisposable
{
    private static readonly AsyncLocal<WorkSandboxContext?> _current = new();

    public static WorkSandboxContext? Current => _current.Value;

    private readonly ISandboxProvider _provider;
    private readonly PipelineTuningSnapshot _tuning;
    private readonly ILogger _log;
    private readonly WorkSandboxContext? _previous;

    private ISandbox? _activeSandbox;
    private string? _activeBaselineImageRef;
    private string? _activeTimingPhase;
    private int _reuseCount;
    private DateTimeOffset _createdAt;

    public WorkSandboxContext(ISandboxProvider provider, PipelineTuningSnapshot tuning, ILogger log)
    {
        _provider = provider;
        _tuning = tuning;
        _log = log;
        _previous = _current.Value;
        _current.Value = this;
        _createdAt = DateTimeOffset.UtcNow;
    }

    public async Task<ISandbox> GetOrCreateSandboxAsync(SandboxSpec spec, CancellationToken ct)
    {
        var options = _tuning.Current;
        if (!options.EnableSandboxReuse)
        {
            _log.LogDebug("Sandbox reuse is disabled; creating fresh sandbox.");
            return await _provider.CreateAsync(spec, ct);
        }

        var requestedTimingPhase = string.IsNullOrWhiteSpace(spec.TimingPhase)
            ? "work"
            : spec.TimingPhase!;

        // Per-phase VM isolation exists ONLY to keep each phase's teardown
        // resource record attributable to a single phase. It is gated on the
        // provider's resource-metrics capture toggle so that with the feature
        // off (the default) a warm VM is still reused across work<->rework,
        // exactly as before — no hidden VM churn for operators who never opted
        // into capture.
        var isolatePhasesForMetrics =
            _provider is IResourceMetricsCapturingProvider capturing
            && capturing.CapturesResourceMetrics;

        // Check pressure threshold
        if (_provider is ISandboxAdmissionSnapshot snapshot)
        {
            var max = snapshot.MaxConcurrentSandboxes;
            var current = snapshot.CurrentAdmittedSandboxes;
            if (max > 0 && ((double)current / max) >= options.SandboxPressureThreshold)
            {
                _log.LogInformation("Sandbox pressure threshold reached ({Current}/{Max} >= {Threshold}); recreating sandbox.", current, max, options.SandboxPressureThreshold);
                await DisposeActiveSandboxAsync();
            }
        }

        // Check lifetime and reuse limit
        if (_activeSandbox != null)
        {
            var age = DateTimeOffset.UtcNow - _createdAt;
            if (age >= options.MaxSandboxLifetime)
            {
                _log.LogInformation("Active sandbox exceeded max lifetime ({Age} >= {Limit}); recreating sandbox.", age, options.MaxSandboxLifetime);
                await DisposeActiveSandboxAsync();
            }
            else if (_reuseCount >= options.MaxSandboxReuses)
            {
                _log.LogInformation("Active sandbox exceeded max reuse count ({Count} >= {Limit}); recreating sandbox.", _reuseCount, options.MaxSandboxReuses);
                await DisposeActiveSandboxAsync();
            }
            // Check if the baseline image matches. If not, we cannot reuse.
            else if (_activeBaselineImageRef != spec.BaselineImageRef)
            {
                _log.LogInformation("Active sandbox baseline image mismatch ('{Active}' != '{Request}'); recreating sandbox.", _activeBaselineImageRef, spec.BaselineImageRef);
                await DisposeActiveSandboxAsync();
            }
            else if (isolatePhasesForMetrics
                && !string.Equals(_activeTimingPhase, requestedTimingPhase, StringComparison.Ordinal))
            {
                _log.LogInformation("Active sandbox timing phase changed ('{Active}' != '{Request}') and resource-metrics capture is on; recreating sandbox for an accurate per-phase record.", _activeTimingPhase, requestedTimingPhase);
                await DisposeActiveSandboxAsync();
            }
        }

        if (_activeSandbox == null)
        {
            _log.LogInformation("Creating fresh sandbox for reuse (BaselineImageRef: {Image}).", spec.BaselineImageRef);
            _activeSandbox = await _provider.CreateAsync(spec, ct);
            _activeBaselineImageRef = spec.BaselineImageRef;
            _activeTimingPhase = requestedTimingPhase;
            _createdAt = DateTimeOffset.UtcNow;
            _reuseCount = 0;
            return Wrap(_activeSandbox, this);
        }
        else
        {
            _reuseCount++;
            _log.LogInformation("Reusing existing warm sandbox (Reuse count: {Count}/{Limit}).", _reuseCount, options.MaxSandboxReuses);
            // Clean the work directory of the reused sandbox before passing it back
            try
            {
                await _activeSandbox.ExecAsync(new SandboxExec
                {
                    Argv = ["rm", "-rf", SandboxConventions.WorkDir]
                }, ct);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to clean work directory in reused sandbox; recreating sandbox instead.");
                await DisposeActiveSandboxAsync();
                _activeSandbox = await _provider.CreateAsync(spec, ct);
                _activeBaselineImageRef = spec.BaselineImageRef;
                _activeTimingPhase = requestedTimingPhase;
                _createdAt = DateTimeOffset.UtcNow;
                _reuseCount = 0;
                return Wrap(_activeSandbox, this);
            }

            return Wrap(_activeSandbox, this);
        }
    }

    private async Task DisposeActiveSandboxAsync()
    {
        if (_activeSandbox != null)
        {
            _log.LogDebug("Disposing active reusable sandbox.");
            try
            {
                await _activeSandbox.DisposeAsync();
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Error disposing reusable sandbox.");
            }
            _activeSandbox = null;
            _activeBaselineImageRef = null;
            _activeTimingPhase = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await DisposeActiveSandboxAsync();
        }
        finally
        {
            _current.Value = _previous;
        }
    }

    [Flags]
    private enum SandboxCapabilities
    {
        None = 0,
        Preemptible = 1 << 0,
        Suspendable = 1 << 1,
        Shutdown = 1 << 2
    }

    private static ISandbox Wrap(ISandbox inner, WorkSandboxContext context)
    {
        var capabilities = SandboxCapabilities.None;
        var preemptible = inner as IPreemptibleSandbox;
        var suspendable = inner as ISuspendableSandbox;
        var shutdown = inner as IShutdownTeardownSandbox;
        if (preemptible is not null) capabilities |= SandboxCapabilities.Preemptible;
        if (suspendable is not null) capabilities |= SandboxCapabilities.Suspendable;
        if (shutdown is not null) capabilities |= SandboxCapabilities.Shutdown;

        return capabilities switch
        {
            SandboxCapabilities.None => new ReusableSandbox(inner, context),
            SandboxCapabilities.Preemptible => new ReusablePreemptibleSandbox(inner, preemptible!, context),
            SandboxCapabilities.Suspendable => new ReusableSuspendableSandbox(inner, suspendable!, context),
            SandboxCapabilities.Shutdown => new ReusableShutdownSandbox(inner, shutdown!, context),
            SandboxCapabilities.Preemptible | SandboxCapabilities.Suspendable => new ReusablePreemptibleSuspendableSandbox(inner, preemptible!, suspendable!, context),
            SandboxCapabilities.Preemptible | SandboxCapabilities.Shutdown => new ReusablePreemptibleShutdownSandbox(inner, preemptible!, shutdown!, context),
            SandboxCapabilities.Suspendable | SandboxCapabilities.Shutdown => new ReusableSuspendableShutdownSandbox(inner, suspendable!, shutdown!, context),
            SandboxCapabilities.Preemptible | SandboxCapabilities.Suspendable | SandboxCapabilities.Shutdown => new ReusableFullSandbox(inner, preemptible!, suspendable!, shutdown!, context),
            _ => throw new InvalidOperationException($"Unhandled sandbox capability set: {capabilities}"),
        };
    }

    private class ReusableSandbox : ISandbox
    {
        protected readonly ISandbox _inner;
        protected readonly WorkSandboxContext _context;

        public ReusableSandbox(ISandbox inner, WorkSandboxContext context)
        {
            _inner = inner;
            _context = context;
        }

        public string Id => _inner.Id;
        public SandboxAgentOutputTransportKind AgentOutputTransportKind => _inner.AgentOutputTransportKind;
        public SandboxBatchLaunchMode BatchLaunchMode => _inner.BatchLaunchMode;
        public SandboxResourceMetrics? ResourceMetrics => _inner.ResourceMetrics;

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            return _inner.ExecAsync(exec, ct);
        }

        public Task KillActiveExecsAsync(CancellationToken ct = default)
        {
            return _inner.KillActiveExecsAsync(ct);
        }

        public Task<byte[]> GetScreenshotAsync(CancellationToken ct = default)
        {
            return _inner.GetScreenshotAsync(ct);
        }

        public Task SynthesizeInputAsync(IReadOnlyList<SandboxInputEvent> events, CancellationToken ct = default)
        {
            return _inner.SynthesizeInputAsync(events, ct);
        }

        public Task<SandboxAccessibilitySnapshot?> GetAccessibilityAtPointAsync(int x, int y, CancellationToken ct = default)
        {
            return _inner.GetAccessibilityAtPointAsync(x, y, ct);
        }

        public Task<string?> GetAccessibilityTreeJsonAsync(CancellationToken ct = default)
        {
            return _inner.GetAccessibilityTreeJsonAsync(ct);
        }

        public ValueTask DisposeAsync()
        {
            // Do not dispose the underlying sandbox.
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ReusablePreemptibleSandbox : ReusableSandbox, IPreemptibleSandbox
    {
        private readonly IPreemptibleSandbox _preemptible;
        public ReusablePreemptibleSandbox(ISandbox inner, IPreemptibleSandbox preemptible, WorkSandboxContext context)
            : base(inner, context)
        {
            _preemptible = preemptible;
        }
        public Task StopAndPreserveAsync(CancellationToken ct = default) => _preemptible.StopAndPreserveAsync(ct);
    }

    private sealed class ReusableSuspendableSandbox : ReusableSandbox, ISuspendableSandbox
    {
        private readonly ISuspendableSandbox _suspendable;
        public ReusableSuspendableSandbox(ISandbox inner, ISuspendableSandbox suspendable, WorkSandboxContext context)
            : base(inner, context)
        {
            _suspendable = suspendable;
        }
        public Task SuspendAsync(CancellationToken ct = default) => _suspendable.SuspendAsync(ct);
        public bool IsSuspended => _suspendable.IsSuspended;
        public long? MemoryBytes => _suspendable.MemoryBytes;
    }

    private sealed class ReusableShutdownSandbox : ReusableSandbox, IShutdownTeardownSandbox
    {
        private readonly IShutdownTeardownSandbox _shutdown;
        public ReusableShutdownSandbox(ISandbox inner, IShutdownTeardownSandbox shutdown, WorkSandboxContext context)
            : base(inner, context)
        {
            _shutdown = shutdown;
        }
        public bool IsOwnedByShutdownHandler => _shutdown.IsOwnedByShutdownHandler;
        public void MarkOwnedByShutdownHandler() => _shutdown.MarkOwnedByShutdownHandler();
    }

    private sealed class ReusablePreemptibleSuspendableSandbox : ReusableSandbox, IPreemptibleSandbox, ISuspendableSandbox
    {
        private readonly IPreemptibleSandbox _preemptible;
        private readonly ISuspendableSandbox _suspendable;
        public ReusablePreemptibleSuspendableSandbox(ISandbox inner, IPreemptibleSandbox preemptible, ISuspendableSandbox suspendable, WorkSandboxContext context)
            : base(inner, context)
        {
            _preemptible = preemptible;
            _suspendable = suspendable;
        }
        public Task StopAndPreserveAsync(CancellationToken ct = default) => _preemptible.StopAndPreserveAsync(ct);
        public Task SuspendAsync(CancellationToken ct = default) => _suspendable.SuspendAsync(ct);
        public bool IsSuspended => _suspendable.IsSuspended;
        public long? MemoryBytes => _suspendable.MemoryBytes;
    }

    private sealed class ReusablePreemptibleShutdownSandbox : ReusableSandbox, IPreemptibleSandbox, IShutdownTeardownSandbox
    {
        private readonly IPreemptibleSandbox _preemptible;
        private readonly IShutdownTeardownSandbox _shutdown;
        public ReusablePreemptibleShutdownSandbox(ISandbox inner, IPreemptibleSandbox preemptible, IShutdownTeardownSandbox shutdown, WorkSandboxContext context)
            : base(inner, context)
        {
            _preemptible = preemptible;
            _shutdown = shutdown;
        }
        public Task StopAndPreserveAsync(CancellationToken ct = default) => _preemptible.StopAndPreserveAsync(ct);
        public bool IsOwnedByShutdownHandler => _shutdown.IsOwnedByShutdownHandler;
        public void MarkOwnedByShutdownHandler() => _shutdown.MarkOwnedByShutdownHandler();
    }

    private sealed class ReusableSuspendableShutdownSandbox : ReusableSandbox, ISuspendableSandbox, IShutdownTeardownSandbox
    {
        private readonly ISuspendableSandbox _suspendable;
        private readonly IShutdownTeardownSandbox _shutdown;
        public ReusableSuspendableShutdownSandbox(ISandbox inner, ISuspendableSandbox suspendable, IShutdownTeardownSandbox shutdown, WorkSandboxContext context)
            : base(inner, context)
        {
            _suspendable = suspendable;
            _shutdown = shutdown;
        }
        public Task SuspendAsync(CancellationToken ct = default) => _suspendable.SuspendAsync(ct);
        public bool IsSuspended => _suspendable.IsSuspended;
        public long? MemoryBytes => _suspendable.MemoryBytes;
        public bool IsOwnedByShutdownHandler => _shutdown.IsOwnedByShutdownHandler;
        public void MarkOwnedByShutdownHandler() => _shutdown.MarkOwnedByShutdownHandler();
    }

    private sealed class ReusableFullSandbox : ReusableSandbox, IPreemptibleSandbox, ISuspendableSandbox, IShutdownTeardownSandbox
    {
        private readonly IPreemptibleSandbox _preemptible;
        private readonly ISuspendableSandbox _suspendable;
        private readonly IShutdownTeardownSandbox _shutdown;
        public ReusableFullSandbox(ISandbox inner, IPreemptibleSandbox preemptible, ISuspendableSandbox suspendable, IShutdownTeardownSandbox shutdown, WorkSandboxContext context)
            : base(inner, context)
        {
            _preemptible = preemptible;
            _suspendable = suspendable;
            _shutdown = shutdown;
        }
        public Task StopAndPreserveAsync(CancellationToken ct = default) => _preemptible.StopAndPreserveAsync(ct);
        public Task SuspendAsync(CancellationToken ct = default) => _suspendable.SuspendAsync(ct);
        public bool IsSuspended => _suspendable.IsSuspended;
        public long? MemoryBytes => _suspendable.MemoryBytes;
        public bool IsOwnedByShutdownHandler => _shutdown.IsOwnedByShutdownHandler;
        public void MarkOwnedByShutdownHandler() => _shutdown.MarkOwnedByShutdownHandler();
    }
}
