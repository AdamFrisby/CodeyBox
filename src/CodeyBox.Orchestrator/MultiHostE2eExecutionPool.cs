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

    /// <summary>
    /// Advisory recheck interval carried on the placement-refusal exception.
    /// The refusal is permanent under the current host registration (no host
    /// matches the lease requirements even with zero load), so no value here
    /// would make a retry succeed; the constant only satisfies the deferral
    /// envelope for retry-aware callers. Deliberately a constant, not a
    /// config knob: there is nothing operational to tune.
    /// </summary>
    private static readonly TimeSpan PlacementRefusalRecheckIn = TimeSpan.FromMinutes(1);

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

    public IReadOnlyList<IManagedSandboxLifecycle> ManagedSandboxProviders =>
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
            var host = TryEnterPlacedHost();
            if (host is not null)
                return host;

            // The pool keeps its 25ms poll rather than waiting on a gate:
            // ResizableConcurrencyGate.WaitAsync acquires a permit when it
            // completes, so waiting on every host gate at once would
            // over-admit. Polling with TryEnter keeps admission exact while
            // the shared decider picks which host to probe each round.
            await Task.Delay(TimeSpan.FromMilliseconds(25), ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// One placement round: projects the pool hosts to placement members and
    /// asks the shared <see cref="ExecutorPlacement"/> decider which host
    /// serves the current lease requirements. Returns null when every
    /// requirements-matching host is at capacity (the caller polls again);
    /// throws a placement refusal when no host matches the requirements even
    /// with zero load, so a mis-profiled lease fails fast instead of hanging
    /// until cancellation or landing on a host that cannot serve it.
    /// </summary>
    /// <exception cref="SandboxProvisioningDeferredException">
    /// Thrown when no registered host matches the lease requirements.
    /// </exception>
    private HostEntry? TryEnterPlacedHost()
    {
        var members = ProjectMembers();
        var requirements = BuildRequirements();
        var loads = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var host in _hosts)
            loads[host.Name] = host.Gate.CurrentInFlight;

        var decision = ExecutorPlacement.Decide(members, requirements, loads, runtimeUnhealthy: null);
        if (decision.SelectedHostId is not null)
        {
            var selected = _hosts.First(h => string.Equals(h.Name, decision.SelectedHostId, StringComparison.Ordinal));
            if (selected.Gate.TryEnter())
            {
                _logger.LogDebug(
                    "E2E multi-host pool placed lease on {Host}: {Decision}",
                    selected.Name,
                    decision.Describe());
                return selected;
            }

            return null;
        }

        // No eligible host under live load. Re-decide with zero load (the
        // same transient-vs-permanent split SandboxPlacementAcquirer uses):
        // a host the zero-load decision selects is only capacity-blocked, so
        // polling again may succeed; otherwise the requirements themselves
        // match nothing and waiting cannot help — refuse.
        var unloaded = ExecutorPlacement.Decide(members, requirements, loads: null, runtimeUnhealthy: null);
        if (unloaded.SelectedHostId is not null)
            return null;

        throw new SandboxProvisioningDeferredException(
            provider: Name,
            operation: "placement",
            errorClass: "no-eligible-host",
            detail: $"networkProfile={DisplayProfile(requirements)}; hosts={string.Join(", ", decision.Candidates.Select(static c => $"{c.HostId}={c.Reason}"))}",
            recheckIn: PlacementRefusalRecheckIn);
    }

    /// <summary>
    /// Projects pool hosts to the placement members the shared decider
    /// consumes. Capacity comes from the pool's own per-host gates (the pool
    /// keeps its own admission); network profiles come from the provider's
    /// live host-pool snapshot when it exposes one, defaulting to accept-all,
    /// normalised through the shared comparison seam so profile matching
    /// agrees with the multipass-remote path; capabilities come from the
    /// provider declaration. Cordon and health stay with the inner provider
    /// that owns the SSH state — the pool does not second-guess them, so a
    /// single-host pool behaves exactly as before for health-gated hosts
    /// (the inner placement defers).
    /// </summary>
    private IReadOnlyList<SandboxPlacementMember> ProjectMembers()
    {
        var members = new List<SandboxPlacementMember>(_hosts.Count);
        foreach (var host in _hosts)
        {
            members.Add(new SandboxPlacementMember
            {
                MemberId = host.Name,
                MaxConcurrentSandboxes = host.Gate.CurrentTarget,
                NetworkProfiles = SnapshotNetworkProfiles(host.Provider),
                Capabilities = host.Provider.DeclaredCapabilities ?? [],
            });
        }

        return members;
    }

    private static IReadOnlyList<string> SnapshotNetworkProfiles(ISandboxProvider provider)
    {
        if (provider is not ISandboxHostPoolSnapshot snapshot)
            return [];

        var profiles = new List<string>();
        foreach (var entry in snapshot.SnapshotHostPool())
        {
            foreach (var normalised in ExecutorEligibility.NormalizeNetworkProfilesForComparison(entry.AllowedNetworkProfiles))
            {
                if (!profiles.Contains(normalised, StringComparer.Ordinal))
                    profiles.Add(normalised);
            }
        }

        return profiles;
    }

    private ExecutorPlacementRequirements BuildRequirements()
    {
        var opts = _options?.CurrentValue;
        return ExecutorPlacementRequirements.FromValues(
            null,
            ExecutorEligibility.NormalizeRequiredNetworkProfile(opts?.NetworkProfile),
            requiredCapabilities: []);
    }

    private static string DisplayProfile(ExecutorPlacementRequirements requirements) =>
        string.IsNullOrWhiteSpace(requirements.RequiredNetworkProfile)
            ? "(default)"
            : requirements.RequiredNetworkProfile.Trim();

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
