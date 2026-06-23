using System;
using System.Threading;
using System.Threading.Tasks;
using CodeyBox.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodeyBox.Orchestrator;

/// <summary>
/// The default E2E pool. Wraps the orchestrator's configured
/// <see cref="ISandboxProvider"/> (Multipass in production) with its OWN
/// concurrency gate so that E2E load never competes with the coding fleet for
/// <see cref="WorkerPool"/> slots. Clone-per-test: each lease produces a fresh
/// sandbox from the pre-baked baseline image (when the provider supports
/// baseline images) and disposes it on release.
///
/// <para>The pool itself does not contain a queue — the
/// <see cref="E2eRunDispatcher"/> hosted service owns queue draining and calls
/// <see cref="LeaseAsync"/> once a slot is free.</para>
/// </summary>
public sealed class LocalE2eExecutionPool : IE2eExecutionPool
{
    private readonly ISandboxProvider _provider;
    private readonly IOptionsMonitor<E2eExecutionOptions>? _options;
    private readonly ILogger<LocalE2eExecutionPool> _logger;
    private readonly SemaphoreSlim _gate;
    private readonly int _initialMaxConcurrent;
    private int _inFlight;

    public LocalE2eExecutionPool(
        ISandboxProvider provider,
        IOptionsMonitor<E2eExecutionOptions>? options,
        ILogger<LocalE2eExecutionPool> logger)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _options = options;
        _logger = logger;
        _initialMaxConcurrent = Clamp(options?.CurrentValue.MaxConcurrent ?? 4);
        _gate = new SemaphoreSlim(_initialMaxConcurrent, E2eExecutionOptions.MaximumMaxConcurrent);
    }

    public string Name => "local";

    public int MaxConcurrent => Clamp(_options?.CurrentValue.MaxConcurrent ?? _initialMaxConcurrent);

    public int InFlight => Volatile.Read(ref _inFlight);

    public async Task<IE2eExecutionSlot> LeaseAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        Interlocked.Increment(ref _inFlight);
        try
        {
            var spec = BuildSpec();
            var sandbox = await _provider.CreateAsync(spec, ct);
            _logger.LogDebug("E2E pool leased sandbox {SandboxId}", sandbox.Id);
            return new Slot(this, sandbox);
        }
        catch
        {
            Interlocked.Decrement(ref _inFlight);
            _gate.Release();
            throw;
        }
    }

    private SandboxSpec BuildSpec()
    {
        var opts = _options?.CurrentValue ?? new E2eExecutionOptions();
        return new SandboxSpec
        {
            ImageReference = opts.SandboxImageReference ?? string.Empty,
            BaselineImageRef = opts.BaselineImageRef,
            Network = string.IsNullOrEmpty(opts.NetworkProfile)
                ? SandboxNetworkPolicy.Denied
                : new SandboxNetworkPolicy { ProfileName = opts.NetworkProfile },
        };
    }

    private void ReleaseSlot()
    {
        Interlocked.Decrement(ref _inFlight);
        try
        {
            _gate.Release();
        }
        catch (SemaphoreFullException)
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
