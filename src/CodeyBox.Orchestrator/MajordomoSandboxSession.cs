using CodeyBox.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Owns the single long-lived majordomo sandbox: created on demand, reused
/// across turns, torn down after a bounded idle lifetime and transparently
/// recreated on the next turn.
/// </summary>
/// <remarks>
/// <para>By construction this session never touches the work-item dispatch
/// path: when the provider gates fleet admission it creates through
/// <see cref="IInfrastructureSandboxCreator"/> without holding a permit, and it
/// never mutates <see cref="IWorkerPoolOccupancy"/> — the fleet's concurrency
/// accounting is unchanged while the majordomo sandbox is alive.</para>
/// <para>Creation fails closed: a blank profile, a profile the host does not
/// accept, or a provider kind without host-enforced egress throws instead of
/// falling back to open egress.</para>
/// <para>Thread-safe. Concurrent <see cref="GetOrCreateAsync"/> calls share one
/// creation; <see cref="NotifyIdleExpiredAsync"/> and <see cref="DisposeAsync"/>
/// serialize behind an in-flight creation through the same gate — an idle check
/// arriving mid-boot finds the fresh sandbox and declines to reap, while
/// dispose tears the completed sandbox down rather than leaking it.</para>
/// </remarks>
public sealed class MajordomoSandboxSession : IAsyncDisposable
{
    private readonly ISandboxProvider _provider;
    private readonly Func<MajordomoSandboxOptions> _optionsAccessor;
    private readonly Func<IReadOnlyList<string>> _allowedProfilesAccessor;
    private readonly TimeProvider _clock;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ISandbox? _sandbox;
    private long _lastUsedAtUtcTicks;
    private bool _disposed;

    public MajordomoSandboxSession(
        ISandboxProvider provider,
        Func<MajordomoSandboxOptions> optionsAccessor,
        Func<IReadOnlyList<string>>? allowedProfilesAccessor = null,
        TimeProvider? clock = null,
        ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(optionsAccessor);
        _provider = provider;
        _optionsAccessor = optionsAccessor;
        _allowedProfilesAccessor = allowedProfilesAccessor ?? (static () => (IReadOnlyList<string>)[]);
        _clock = clock ?? TimeProvider.System;
        _log = log ?? NullLogger.Instance;
        _lastUsedAtUtcTicks = _clock.GetUtcNow().UtcTicks;
    }

    /// <summary>True while the reusable sandbox is alive.</summary>
    public bool IsAlive => Volatile.Read(ref _sandbox) is not null && !Volatile.Read(ref _disposed);

    /// <summary>Last turn that used (or created) the sandbox, in UTC.</summary>
    public DateTimeOffset LastUsedAt =>
        new(Interlocked.Read(ref _lastUsedAtUtcTicks), TimeSpan.Zero);

    /// <summary>
    /// Returns the live sandbox, creating it on demand. Marks the turn active
    /// so the idle bound measures from the latest use.
    /// </summary>
    public async Task<ISandbox> GetOrCreateAsync(string mcpServerUrl, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mcpServerUrl);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(MajordomoSandboxSession));
            if (_sandbox is not null)
            {
                Interlocked.Exchange(ref _lastUsedAtUtcTicks, _clock.GetUtcNow().UtcTicks);
                return _sandbox;
            }
            var options = _optionsAccessor();
            var failure = MajordomoSandboxOptions.Validate(options);
            if (failure is not null)
                throw new InvalidOperationException($"Invalid {MajordomoSandboxOptions.SectionName} configuration: {failure}");

            var profile = options.NetworkProfile.Trim();
            MajordomoSandboxSpecFactory.EnsureProfileAvailable(_allowedProfilesAccessor(), profile);
            MajordomoSandboxSpecFactory.EnsureProviderEnforces(_provider.Name, profile);

            var spec = MajordomoSandboxSpecFactory.BuildSpec(options, mcpServerUrl);
            var created = _provider is IInfrastructureSandboxCreator exempt
                ? await exempt.CreateInfrastructureAsync(spec, ct).ConfigureAwait(false)
                : await _provider.CreateAsync(spec, ct).ConfigureAwait(false);
            Volatile.Write(ref _sandbox, created);
            Interlocked.Exchange(ref _lastUsedAtUtcTicks, _clock.GetUtcNow().UtcTicks);
            _log.LogInformation(
                "Majordomo sandbox created on provider '{Provider}' with network profile '{Profile}'.",
                _provider.Name, profile);
            return created;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Tears the sandbox down when it has been idle longer than the configured
    /// bound. Returns true when a teardown happened; the next turn recreates
    /// the sandbox transparently through <see cref="GetOrCreateAsync"/>.
    /// </summary>
    public async Task<bool> NotifyIdleExpiredAsync(CancellationToken ct = default)
    {
        ISandbox? victim = null;
        var idleFor = TimeSpan.Zero;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_sandbox is null || _disposed)
                return false;
            idleFor = _clock.GetUtcNow() - LastUsedAt;
            var bound = _optionsAccessor().IdleTimeout;
            if (idleFor < bound)
                return false;
            victim = _sandbox;
            Volatile.Write(ref _sandbox, null);
        }
        finally
        {
            _gate.Release();
        }

        if (victim is not null)
        {
            _log.LogInformation("Majordomo sandbox idle-torn-down after {Idle}.", idleFor);
            await victim.DisposeAsync().ConfigureAwait(false);
            return true;
        }
        return false;
    }

    /// <summary>Disposes the live sandbox, if any. Idempotent.</summary>
    public async ValueTask DisposeAsync()
    {
        ISandbox? victim = null;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
                return;
            Volatile.Write(ref _disposed, true);
            victim = _sandbox;
            Volatile.Write(ref _sandbox, null);
        }
        finally
        {
            _gate.Release();
        }
        if (victim is not null)
            await victim.DisposeAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}
