using CodeyBox.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodeyBox.Orchestrator;

/// <summary>
/// E2E execution pool that fans clone-per-test leases out across multiple
/// remote sandbox providers. Each host has its own capacity gate, and the
/// global <see cref="E2eExecutionOptions.MaxConcurrent"/> cap remains
/// hot-reloadable.
/// </summary>
public sealed class MultiHostE2eExecutionPool : IE2eExecutionPool, IManagedSandboxProviderSource
{
    private readonly IReadOnlyList<HostEntry> _hosts;
    private readonly IOptionsMonitor<E2eExecutionOptions>? _options;
    private readonly ILogger<MultiHostE2eExecutionPool> _logger;
    private readonly ResizableConcurrencyGate _globalGate;
    private readonly Func<string?> _fallbackImageReference;
    private int _nextHost;

    public MultiHostE2eExecutionPool(
        IReadOnlyList<E2eExecutionHost> hosts,
        IOptionsMonitor<E2eExecutionOptions>? options,
        ILogger<MultiHostE2eExecutionPool> logger,
        Func<string?>? fallbackImageReference = null)
    {
        if (hosts.Count == 0)
            throw new ArgumentException("At least one E2E execution host is required.", nameof(hosts));

        _hosts = hosts
            .Select(h => new HostEntry(
                h.Name,
                h.Provider,
                new ResizableConcurrencyGate(Clamp(h.MaxConcurrent))))
            .ToArray();
        _options = options;
        _logger = logger;
        _fallbackImageReference = fallbackImageReference ?? (() => null);
        _globalGate = new ResizableConcurrencyGate(Clamp(options?.CurrentValue.MaxConcurrent ?? 4));
        _options?.OnChange(opts =>
        {
            var resized = _globalGate.Resize(Clamp(opts.MaxConcurrent));
            if (resized.OldTarget != resized.NewTarget)
            {
                _logger.LogInformation(
                    "E2E multi-host pool resized from {Old} to {New}; in-flight={InFlight}",
                    resized.OldTarget,
                    resized.NewTarget,
                    resized.InFlight);
            }
        });
    }

    public string Name => $"remote-ssh[{_hosts.Count}]";

    public int MaxConcurrent => Math.Min(
        _globalGate.CurrentTarget,
        _hosts.Sum(static h => h.Gate.CurrentTarget));

    public int InFlight => _hosts.Sum(static h => h.Gate.CurrentInFlight);

    public IReadOnlyList<ISandboxProvider> ManagedSandboxProviders =>
        _hosts.Select(static h => h.Provider).ToArray();

    public async Task<IE2eExecutionSlot> LeaseAsync(CancellationToken ct = default)
    {
        await _globalGate.WaitAsync(ct).ConfigureAwait(false);
        HostEntry? host = null;
        try
        {
            host = await WaitForHostAsync(ct).ConfigureAwait(false);
            var sandbox = await host.Provider.CreateAsync(BuildSpec(), ct).ConfigureAwait(false);
            _logger.LogDebug("E2E multi-host pool leased sandbox {SandboxId} on {Host}", sandbox.Id, host.Name);
            return new Slot(this, host, sandbox);
        }
        catch
        {
            host?.Gate.Release();
            _globalGate.Release();
            throw;
        }
    }

    private async Task<HostEntry> WaitForHostAsync(CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var host = TryEnterHost();
            if (host is not null)
                return host;

            await Task.Delay(TimeSpan.FromMilliseconds(25), ct).ConfigureAwait(false);
        }
    }

    private HostEntry? TryEnterHost()
    {
        var start = Interlocked.Increment(ref _nextHost);
        for (var i = 0; i < _hosts.Count; i++)
        {
            var host = _hosts[(start + i) % _hosts.Count];
            if (host.Gate.TryEnter())
                return host;
        }
        return null;
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

    private void Release(HostEntry host)
    {
        try
        {
            host.Gate.Release();
        }
        finally
        {
            _globalGate.Release();
        }
    }

    private static int Clamp(int value)
    {
        if (value < E2eExecutionOptions.MinimumMaxConcurrent) return E2eExecutionOptions.MinimumMaxConcurrent;
        if (value > E2eExecutionOptions.MaximumMaxConcurrent) return E2eExecutionOptions.MaximumMaxConcurrent;
        return value;
    }

    private sealed record HostEntry(string Name, ISandboxProvider Provider, ResizableConcurrencyGate Gate);

    private sealed class Slot : IE2eExecutionSlot
    {
        private readonly MultiHostE2eExecutionPool _pool;
        private readonly HostEntry _host;
        private readonly ISandbox _sandbox;
        private int _disposed;

        public Slot(MultiHostE2eExecutionPool pool, HostEntry host, ISandbox sandbox)
        {
            _pool = pool;
            _host = host;
            _sandbox = sandbox;
        }

        public ISandbox Sandbox => _sandbox;

        public string SandboxId => _sandbox.Id;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try
            {
                await _sandbox.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                _pool.Release(_host);
            }
        }
    }
}

public sealed record E2eExecutionHost(string Name, ISandboxProvider Provider, int MaxConcurrent);
