using System;
using System.Threading;
using System.Threading.Tasks;
using CodeyBox.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodeyBox.Orchestrator;

/// <summary>
/// E2E execution pool backed by a sandbox provider chosen specifically for
/// replay work. Production composition should pass the remote cheap-CPU
/// provider; local development can pass an independent unadmitted provider.
/// Clone-per-test: each lease produces a fresh sandbox from the pre-baked
/// baseline image (when the provider supports baseline images) and disposes it
/// on release.
///
/// <para>The pool itself does not contain a queue — the
/// <see cref="E2eRunDispatcher"/> hosted service owns queue draining and calls
/// <see cref="LeaseAsync"/> once a slot is free.</para>
/// </summary>
public sealed class LocalE2eExecutionPool : IE2eExecutionPool, IManagedSandboxProviderSource
{
    private readonly ISandboxProvider _provider;
    private readonly IOptionsMonitor<E2eExecutionOptions>? _options;
    private readonly ILogger<LocalE2eExecutionPool> _logger;
    private readonly ResizableConcurrencyGate _gate;
    private readonly Func<string?> _fallbackImageReference;
    private readonly string _name;

    public LocalE2eExecutionPool(
        ISandboxProvider provider,
        IOptionsMonitor<E2eExecutionOptions>? options,
        ILogger<LocalE2eExecutionPool> logger,
        Func<string?>? fallbackImageReference = null,
        string? name = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _options = options;
        _logger = logger;
        _fallbackImageReference = fallbackImageReference ?? (() => null);
        _name = string.IsNullOrWhiteSpace(name) ? "local" : name;
        _gate = new ResizableConcurrencyGate(Clamp(options?.CurrentValue.MaxConcurrent ?? 4));
        _options?.OnChange(opts =>
        {
            var resized = _gate.Resize(Clamp(opts.MaxConcurrent));
            if (resized.OldTarget != resized.NewTarget)
            {
                _logger.LogInformation(
                    "E2E pool {Pool} resized from {Old} to {New}; in-flight={InFlight}",
                    _name,
                    resized.OldTarget,
                    resized.NewTarget,
                    resized.InFlight);
            }
        });
    }

    public string Name => _name;

    public int MaxConcurrent => _gate.CurrentTarget;

    public int InFlight => _gate.CurrentInFlight;

    public IReadOnlyList<IManagedSandboxLifecycle> ManagedSandboxProviders => [_provider];

    public async Task<IE2eExecutionSlot> LeaseAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var spec = BuildSpec();
            var sandbox = await _provider.CreateAsync(spec, ct);
            _logger.LogDebug("E2E pool leased sandbox {SandboxId}", sandbox.Id);
            return new Slot(this, sandbox);
        }
        catch
        {
            _gate.Release();
            throw;
        }
    }

    private SandboxSpec BuildSpec()
    {
        var opts = _options?.CurrentValue ?? new E2eExecutionOptions();
        return new SandboxSpec
        {
            ImageReference = opts.SandboxImageReference ?? _fallbackImageReference() ?? string.Empty,
            BaselineImageRef = opts.BaselineImageRef,
            Network = string.IsNullOrEmpty(opts.NetworkProfile)
                ? SandboxNetworkPolicy.Denied
                : new SandboxNetworkPolicy { ProfileName = opts.NetworkProfile },
        };
    }

    private void ReleaseSlot()
    {
        try
        {
            _gate.Release();
        }
        catch (InvalidOperationException)
        {
            // Defensive: a double-release would be a bug elsewhere, log rather than crash.
            _logger.LogWarning("E2E pool gate over-released; ignoring.");
        }
    }

    private static int Clamp(int value)
    {
        if (value < E2eExecutionOptions.MinimumMaxConcurrent) return E2eExecutionOptions.MinimumMaxConcurrent;
        if (value > E2eExecutionOptions.MaximumMaxConcurrent) return E2eExecutionOptions.MaximumMaxConcurrent;
        return value;
    }

    private sealed class Slot : IE2eExecutionSlot
    {
        private readonly LocalE2eExecutionPool _pool;
        private readonly ISandbox _sandbox;
        private int _disposed;

        public Slot(LocalE2eExecutionPool pool, ISandbox sandbox)
        {
            _pool = pool;
            _sandbox = sandbox;
        }

        public ISandbox Sandbox => _sandbox;

        public string SandboxId => _sandbox.Id;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try
            {
                await _sandbox.DisposeAsync();
            }
            finally
            {
                _pool.ReleaseSlot();
            }
        }
    }
}
