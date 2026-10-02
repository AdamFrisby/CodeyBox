namespace CodeyBox.Core;

/// <summary>
/// Hot-reloadable tuning for host-level provider-transient correlation: when
/// the same transient <em>signature</em> (not free text — the detector-owned
/// label such as <c>websocket-close-1006</c>) is observed from several agents
/// inside <see cref="Window"/>, the host network (not any one provider) is
/// the likely cause and dispatch pauses for <see cref="PauseDuration"/>
/// instead of burning every item's retry budget at once.
/// </summary>
public sealed record ProviderTransientCorrelationSettings(
    TimeSpan Window,
    int DistinctAgentThreshold,
    TimeSpan PauseDuration)
{
    /// <summary>Window default: five minutes — the September 2026 blip hit two agents at the same instant.</summary>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(5);

    /// <summary>Pause default: two minutes — long enough for a host blip to clear, short enough to stay useful.</summary>
    public static readonly TimeSpan DefaultPauseDuration = TimeSpan.FromMinutes(2);

    /// <summary>Largest accepted correlation window (bounds retained event volume).</summary>
    public static readonly TimeSpan MaxWindow = TimeSpan.FromHours(1);

    /// <summary>Longest accepted dispatch pause.</summary>
    public static readonly TimeSpan MaxPauseDuration = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Builds settings, clamping each field into its valid range rather than
    /// throwing: the values are operator-supplied and hot-reloaded, so a bad
    /// value must degrade to a safe default instead of crashing dispatch.
    /// The threshold floors at two — a single agent's failure is never
    /// host-level evidence.
    /// </summary>
    public static ProviderTransientCorrelationSettings Create(
        TimeSpan window,
        int distinctAgentThreshold,
        TimeSpan pauseDuration) =>
        new(
            window <= TimeSpan.Zero ? DefaultWindow : (window > MaxWindow ? MaxWindow : window),
            distinctAgentThreshold < 2 ? 2 : (distinctAgentThreshold > 16 ? 16 : distinctAgentThreshold),
            pauseDuration < TimeSpan.Zero ? TimeSpan.Zero : (pauseDuration > MaxPauseDuration ? MaxPauseDuration : pauseDuration));
}

/// <summary>
/// Tracks provider-transient signature observations across agents and trips a
/// bounded dispatch pause when the same signature hits several agents inside
/// the configured window (host/network-level evidence). Observations carry
/// only detector-owned signature labels — never raw agent output, which may
/// contain secrets. Bounded memory: per-signature event lists are pruned to
/// the live window and the table evicts the stalest signature past the cap.
/// Thread-safe; total functions of their inputs (an empty signature observes
/// nothing and never trips).
/// </summary>
public sealed class ProviderTransientCorrelationTracker
{
    /// <summary>Most distinct signatures retained (bounds the table).</summary>
    public const int MaxTrackedSignatures = 64;

    /// <summary>Most observations retained per signature (bounds each list).</summary>
    public const int MaxObservationsPerSignature = 32;

    private readonly Func<ProviderTransientCorrelationSettings> _settingsAccessor;
    private readonly object _gate = new();
    private readonly Dictionary<string, List<Observation>> _observations =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _pauses =
        new(StringComparer.OrdinalIgnoreCase);

    public ProviderTransientCorrelationTracker(
        Func<ProviderTransientCorrelationSettings>? settingsAccessor = null)
    {
        _settingsAccessor = settingsAccessor
            ?? (() => ProviderTransientCorrelationSettings.Create(
                ProviderTransientCorrelationSettings.DefaultWindow,
                2,
                ProviderTransientCorrelationSettings.DefaultPauseDuration));
    }

    /// <summary>
    /// Records one transient observation. Returns true exactly when this
    /// observation newly trips a host-level pause for
    /// <paramref name="signature"/> (threshold distinct agents inside the
    /// window, no pause already active); the caller pauses dispatch briefly.
    /// Returns false for empty signatures and while a pause is active.
    /// </summary>
    public bool Observe(string? signature, AgentKind agent, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(signature))
            return false;

        var settings = ReadSettings();
        var key = signature.Trim();
        lock (_gate)
        {
            PruneLocked(settings, now);
            if (IsPausedLocked(now))
            {
                RecordLocked(key, agent.Value, now);
                return false;
            }

            RecordLocked(key, agent.Value, now);
            if (!_observations.TryGetValue(key, out var events))
                return false;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in events)
            {
                if (now - entry.At > settings.Window)
                    continue;
                seen.Add(entry.Agent);
            }

            if (seen.Count < settings.DistinctAgentThreshold)
                return false;

            _pauses[key] = now + settings.PauseDuration;
            return settings.PauseDuration > TimeSpan.Zero;
        }
    }

    /// <summary>True while any tripped signature pause is still active.</summary>
    public bool IsPaused(DateTimeOffset now)
    {
        var settings = ReadSettings();
        lock (_gate)
        {
            PruneLocked(settings, now);
            return IsPausedLocked(now);
        }
    }

    private ProviderTransientCorrelationSettings ReadSettings()
    {
        try
        {
            return _settingsAccessor();
        }
        catch (Exception)
        {
            return ProviderTransientCorrelationSettings.Create(
                ProviderTransientCorrelationSettings.DefaultWindow,
                2,
                ProviderTransientCorrelationSettings.DefaultPauseDuration);
        }
    }

    private void RecordLocked(string signature, string agent, DateTimeOffset now)
    {
        if (!_observations.TryGetValue(signature, out var events))
        {
            if (_observations.Count >= MaxTrackedSignatures)
                EvictStalestLocked();
            events = [];
            _observations[signature] = events;
        }

        events.Add(new Observation(now, agent));
        if (events.Count > MaxObservationsPerSignature)
            events.RemoveRange(0, events.Count - MaxObservationsPerSignature);
    }

    private void PruneLocked(ProviderTransientCorrelationSettings settings, DateTimeOffset now)
    {
        foreach (var (signature, until) in _pauses.ToArray())
        {
            if (until <= now)
                _pauses.Remove(signature);
        }

        foreach (var (signature, events) in _observations.ToArray())
        {
            events.RemoveAll(entry => now - entry.At > settings.Window);
            if (events.Count == 0 && !_pauses.ContainsKey(signature))
                _observations.Remove(signature);
        }
    }

    private void EvictStalestLocked()
    {
        string? stalest = null;
        var stalestAt = DateTimeOffset.MaxValue;
        foreach (var (signature, events) in _observations)
        {
            if (_pauses.ContainsKey(signature))
                continue;
            var latest = events.Count == 0 ? DateTimeOffset.MinValue : events[^1].At;
            if (latest < stalestAt)
            {
                stalestAt = latest;
                stalest = signature;
            }
        }

        if (stalest is not null)
            _observations.Remove(stalest);
    }

    private bool IsPausedLocked(DateTimeOffset now)
    {
        foreach (var until in _pauses.Values)
        {
            if (until > now)
                return true;
        }

        return false;
    }

    private sealed record Observation(DateTimeOffset At, string Agent);
}
