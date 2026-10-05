namespace CodeyBox.Core;

/// <summary>
/// Host-owned gate for the per-sandbox verified path: operator opt-in plus
/// a per-kind demotion circuit breaker. The operator opts kinds in through
/// <c>CodeyBox:EgressVerification:Kinds</c> (a plugin can never add itself);
/// a failed canary demotes the kind back to
/// <see cref="EgressEnforcementLocation.NotEnforced"/> for the configured
/// cool-down, after which the kind becomes eligible for a FRESH canary — a
/// new sandbox still has to pass before it serves profiled work. A fresh
/// passing canary clears the demotion early. All reads take the current
/// options through the accessor, so edits hot-reload; the clock is injected
/// so tests drive demotion with a fake.
/// </summary>
public sealed class EgressVerificationGate
{
    private readonly Func<EgressVerificationOptions> _optionsAccessor;
    private readonly TimeProvider _clock;
    private readonly object _sync = new();
    private readonly Dictionary<string, DateTimeOffset> _demotedUntil = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="optionsAccessor">Live options reader (hot-reloadable). Null means disabled.</param>
    /// <param name="clock">Clock for cool-down expiry. Null defaults to system.</param>
    public EgressVerificationGate(Func<EgressVerificationOptions>? optionsAccessor = null, TimeProvider? clock = null)
    {
        _optionsAccessor = optionsAccessor ?? (() => new EgressVerificationOptions());
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// True when <paramref name="providerKind"/> is operator-opted-in AND not
    /// inside a failure cool-down. Opt-in alone is not enforcement — the
    /// sandbox still needs its own passing canary.
    /// </summary>
    public bool IsKindEligible(string providerKind)
    {
        if (string.IsNullOrWhiteSpace(providerKind))
            return false;
        var kind = providerKind.Trim();
        if (!_optionsAccessor().IsKindOptedIn(kind))
            return false;
        return !IsDemoted(kind);
    }

    /// <summary>True when the kind is inside a failure cool-down.</summary>
    public bool IsDemoted(string providerKind)
    {
        if (string.IsNullOrWhiteSpace(providerKind))
            return false;
        lock (_sync)
        {
            PruneExpiredLocked();
            return _demotedUntil.ContainsKey(providerKind.Trim());
        }
    }

    /// <summary>When the demotion lifts, or null when not demoted.</summary>
    public DateTimeOffset? DemotedUntil(string providerKind)
    {
        if (string.IsNullOrWhiteSpace(providerKind))
            return null;
        lock (_sync)
        {
            PruneExpiredLocked();
            return _demotedUntil.TryGetValue(providerKind.Trim(), out var until) ? until : null;
        }
    }

    /// <summary>
    /// Demotes <paramref name="providerKind"/> for the currently-configured
    /// cool-down after a failed canary. Returns when the demotion lifts.
    /// </summary>
    public DateTimeOffset RecordFailure(string providerKind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerKind);
        var until = _clock.GetUtcNow() + _optionsAccessor().Cooldown;
        lock (_sync)
        {
            _demotedUntil[providerKind.Trim()] = until;
        }
        return until;
    }

    /// <summary>Clears a demotion after a fresh passing canary.</summary>
    public void RecordSuccess(string providerKind)
    {
        if (string.IsNullOrWhiteSpace(providerKind))
            return;
        lock (_sync)
        {
            _demotedUntil.Remove(providerKind.Trim());
        }
    }

    private void PruneExpiredLocked()
    {
        var now = _clock.GetUtcNow();
        foreach (var key in _demotedUntil.Keys.ToArray())
        {
            if (_demotedUntil[key] <= now)
                _demotedUntil.Remove(key);
        }
    }
}
