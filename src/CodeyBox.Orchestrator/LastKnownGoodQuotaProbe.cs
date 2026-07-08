using CodeyBox.Core;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Decorator over any <see cref="IAgentQuotaProbe"/> that retains the last
/// <em>real</em> reading per <c>(RouteKey, ModelId)</c> and substitutes it when
/// the inner probe returns a <see cref="QuotaUnknownReason.Transient"/> unknown
/// (or throws). This means a momentary probe blip no longer collapses an agent
/// to "unknown" and lets the router fall open — the router keeps gating on the
/// last good number instead.
///
/// <para><b>Retain vs discard</b> is driven by the unknown reason the inner
/// probe reports:
/// <list type="bullet">
///   <item><description><see cref="QuotaUnknownReason.Transient"/> (network/5xx/timeout,
///   or an inner exception) — the account state is unchanged, so a recent good
///   reading is served stale (with a note), bounded by <c>MaxStaleness</c> (age
///   since capture) and the reading's own <see cref="AgentQuotaSnapshot.ResetAt"/>
///   (a stale "0%" past its reset is dropped so a recovered window isn't gated
///   forever). Age is used rather than a failure count because probes cache
///   unknowns, so a single blip can be observed many times.</description></item>
///   <item><description><see cref="QuotaUnknownReason.Permanent"/> /
///   <see cref="QuotaUnknownReason.NoCredential"/> — the prior reading can no
///   longer be trusted (revoked token, contract failure, no creds), so the
///   retained value is discarded and the unknown is passed through.</description></item>
/// </list></para>
///
/// <para>Keyed by <c>(RouteKey, ModelId)</c> — <c>RouteKey</c> is account-scoped
/// (<c>agent/instanceId</c>), so distinct accounts never share a retained value.
/// A within-account token rotation needs no special handling: the rotated read
/// either succeeds (overwrites the retained value) or fails Permanent (drops it),
/// and the account's quota is unchanged in the meantime.</para>
/// </summary>
public sealed class LastKnownGoodQuotaProbe : IAgentQuotaProbe, IAgentQuotaCacheInvalidator, IAgentQuotaRecoveryStateInvalidator
{
    private readonly IAgentQuotaProbe _inner;
    private readonly Func<LastKnownGoodQuotaOptions> _optionsProvider;
    private readonly ILogger? _log;
    private readonly TimeProvider _time;

    private readonly object _lock = new();
    private readonly Dictionary<(string RouteKey, string ModelKey), (AgentQuotaSnapshot Snapshot, DateTimeOffset CapturedAt)> _retained = new();

    public AgentKind Kind => _inner.Kind;

    public LastKnownGoodQuotaProbe(
        IAgentQuotaProbe inner,
        Func<LastKnownGoodQuotaOptions> optionsProvider,
        ILogger? log = null,
        TimeProvider? timeProvider = null)
    {
        _inner = inner;
        _optionsProvider = optionsProvider;
        _log = log;
        _time = timeProvider ?? TimeProvider.System;
    }

    public async Task<AgentQuotaSnapshot> GetAvailabilityAsync(AgentMembership member, CancellationToken ct)
    {
        var key = KeyFor(member);
        AgentQuotaSnapshot snapshot;
        try
        {
            snapshot = await _inner.GetAvailabilityAsync(member, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // An inner throw is a transient fault (the router would otherwise
            // turn it into a context-free unknown). Treat it as transient so the
            // last-known-good can stand in.
            _log?.LogDebug(ex, "Quota probe {Kind} threw; treating as transient unknown", Kind.Value);
            snapshot = AgentQuotaSnapshot.UnknownSnapshot(
                QuotaUnknownReason.Transient, $"probe threw: {ex.GetType().Name}");
        }

        var now = _time.GetUtcNow();

        lock (_lock)
        {
            if (snapshot.IsKnown)
            {
                _retained[key] = (snapshot, now);
                return snapshot;
            }

            // Not a real reading. Only an explicit Transient reason may stand in
            // with a retained value; Permanent / NoCredential (and any
            // unclassified negative) discard it.
            if (snapshot.Unknown != QuotaUnknownReason.Transient)
            {
                _retained.Remove(key);
                return snapshot;
            }

            // Transient unknown: serve the retained reading while it's within
            // MaxStaleness and hasn't passed its own reset.
            if (_retained.TryGetValue(key, out var r))
            {
                var age = now - r.CapturedAt;
                var resetPassed = r.Snapshot.ResetAt is { } reset && reset <= now;

                if (!resetPassed && age <= _optionsProvider().MaxStaleness)
                {
                    var ageSeconds = (long)Math.Round(age.TotalSeconds);
                    _log?.LogDebug(
                        "Quota probe {Kind}: transient unknown; retaining last-known-good (age {Age}s)",
                        Kind.Value, ageSeconds);
                    return r.Snapshot with
                    {
                        Notes = $"stale (age {ageSeconds}s, {snapshot.Notes})",
                    };
                }

                _retained.Remove(key);
                _log?.LogWarning(
                    "Quota probe {Kind}: dropping last-known-good (age {Age}s, resetPassed={Reset}); falling to unknown",
                    Kind.Value, (long)Math.Round(age.TotalSeconds), resetPassed);
            }

            return snapshot;
        }
    }

    public async Task MarkExhaustedAsync(
        AgentMembership member,
        TimeSpan ttl,
        DateTimeOffset? resetAt = null,
        CancellationToken ct = default)
    {
        await _inner.MarkExhaustedAsync(member, ttl, resetAt, ct).ConfigureAwait(false);
        if (_inner is IAgentQuotaCacheInvalidator invalidator)
            invalidator.InvalidateResponseCache();

        var key = KeyFor(member);
        lock (_lock)
            _retained.Remove(key);
    }

    public void InvalidateCache() => InvalidateResponseCache();

    public void InvalidateResponseCache()
    {
        if (_inner is IAgentQuotaCacheInvalidator invalidator)
            invalidator.InvalidateResponseCache();
    }

    public void InvalidateCredentialState()
    {
        if (_inner is IAgentQuotaCacheInvalidator invalidator)
            invalidator.InvalidateCredentialState();

        lock (_lock)
            _retained.Clear();
    }

    public void InvalidateRecoveryState(AgentMembership member)
    {
        if (_inner is IAgentQuotaRecoveryStateInvalidator recoveryInvalidator)
        {
            recoveryInvalidator.InvalidateRecoveryState(member);
            return;
        }

        if (_inner is IAgentQuotaCacheInvalidator invalidator)
            invalidator.InvalidateResponseCache();
    }

    private static (string RouteKey, string ModelKey) KeyFor(AgentMembership member) =>
        (member.RouteKey, string.IsNullOrWhiteSpace(member.ModelId) ? "" : member.ModelId!);
}

/// <summary>
/// Bounds for <see cref="LastKnownGoodQuotaProbe"/>. Read on every probe call so
/// values bound from <c>CodeyBox:QuotaRouter</c> hot-reload without restart.
/// </summary>
public sealed record LastKnownGoodQuotaOptions
{
    /// <summary>Maximum age of a retained reading before it is dropped and the
    /// unknown passes through. Default 5 minutes.</summary>
    public TimeSpan MaxStaleness { get; init; } = TimeSpan.FromMinutes(5);
}
