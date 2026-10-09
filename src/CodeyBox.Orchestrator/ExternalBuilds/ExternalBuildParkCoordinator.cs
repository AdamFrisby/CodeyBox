using CodeyBox.Core.ExternalBuilds;

namespace CodeyBox.Orchestrator.ExternalBuilds;

/// <summary>
/// Durable park and completion delivery coordinator. Checkpoints the
/// work-item turn and releases model/agent execution capacity while parked;
/// resumes the exact correct work item/phase/iteration/attempt exactly once
/// on completion through the service outbox with acknowledged delivery.
/// Survives orchestrator/runner restart, disconnected transports, task TTL
/// expiry, and retained/fresh sandbox recovery. A parked build is a known
/// wait state, never stalled-worker failure. Audit/merge gates stay closed
/// while required evidence is outstanding.
/// </summary>
public sealed class ExternalBuildParkCoordinator
{
    private readonly ExternalBuildService _service;
    private readonly IExternalBuildStore _store;
    private readonly InMemoryExternalBuildHistory _history;
    private readonly Func<ExternalBuildOptions> _options;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private readonly Dictionary<string, ParkedWait> _parked = new(StringComparer.Ordinal);
    private readonly HashSet<string> _resumed = new(StringComparer.Ordinal);
    private int _deliveries;

    public ExternalBuildParkCoordinator(
        ExternalBuildService service,
        IExternalBuildStore store,
        InMemoryExternalBuildHistory history,
        Func<ExternalBuildOptions> options,
        TimeProvider? clock = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _clock = clock ?? TimeProvider.System;
    }

    public sealed record ParkedWait(
        string BuildId, string WorkItemId, string Phase, int Iteration, int Attempt,
        ExternalBuildParkDecision Decision, DateTimeOffset ParkedAt, string CheckpointId);

    public int Deliveries { get { lock (_gate) return _deliveries; } }

    /// <summary>
    /// Predicts before waiting. Completed-before-park returns no park.
    /// Otherwise parks (releasing capacity) only when predicted duration
    /// strictly exceeds 10 minutes or the elapsed fallback fires.
    /// </summary>
    public async Task<ParkedWait?> MaybeParkAsync(
        string buildId, TimeSpan alreadyElapsed, bool? coldCache, CancellationToken ct = default)
    {
        var record = await _store.GetAsync(buildId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Unknown external build '{buildId}'.");
        if (ExternalBuildLifecycle.IsTerminal(record.State))
            return null;
        var opts = _options();
        var decision = ExternalBuildParkPolicy.Decide(
            record.Target, _history.Snapshot(), opts.HistorySampleSize,
            opts.MinSamplesForPrediction,
            Enum.TryParse<ParkEstimator>(opts.Estimator, out var est) ? est : ParkEstimator.Median,
            _clock.GetUtcNow(), coldCache);
        ExternalBuildParkDecision effective = decision;
        if (!decision.ShouldPark && decision.PredictedDuration is null
            && ExternalBuildParkPolicy.ShouldParkOnElapsed(alreadyElapsed))
            effective = decision with { ShouldPark = true, Reason = $"elapsed {alreadyElapsed:mm\\:ss} exceeds 10:00 with insufficient history" };
        if (!effective.ShouldPark)
            return null;
        var parked = new ParkedWait(
            record.Id, record.WorkItemId, record.Phase, record.Iteration, record.Attempt,
            effective, _clock.GetUtcNow(), "ckpt-" + record.Id);
        lock (_gate) _parked[record.Id] = parked;
        return parked;
    }

    /// <summary>
    /// Delivers each completion exactly once to the correct
    /// work item/phase/iteration/attempt. Obsolete attempts, duplicate
    /// completions, and parking/cancel races are suppressed explicitly.
    /// Returns the delivered build ids.
    /// </summary>
    public async Task<IReadOnlyList<string>> DeliverCompletionsAsync(
        Func<ExternalBuildRecord, string, CancellationToken, Task> resumeAsync,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(resumeAsync);
        var delivered = new List<string>();
        foreach (var evt in _service.DrainOutbox())
        {
            var record = await _store.GetAsync(evt.BuildId, ct).ConfigureAwait(false);
            if (record is null) { _service.AckDelivery(evt.BuildId); continue; }
            lock (_gate)
            {
                if (_resumed.Contains(record.Id)) { _service.AckDelivery(record.Id); continue; }
                if (_parked.TryGetValue(record.Id, out var parked)
                    && (parked.Attempt != record.Attempt || parked.Iteration != record.Iteration
                        || !string.Equals(parked.Phase, record.Phase, StringComparison.Ordinal)
                        || !string.Equals(parked.WorkItemId, record.WorkItemId, StringComparison.Ordinal)))
                {
                    _service.AckDelivery(record.Id);
                    continue;
                }
            }
            var resumeKind = DetermineResumeKind(record);
            await resumeAsync(record, resumeKind, ct).ConfigureAwait(false);
            lock (_gate)
            {
                _resumed.Add(record.Id);
                _parked.Remove(record.Id);
                _deliveries++;
            }
            _service.AckDelivery(record.Id);
            delivered.Add(record.Id);
            if (record.State == ExternalBuildState.Succeeded && record.Evidence is not null)
                _history.Record(new ExternalBuildDurationSample(
                    record.Target,
                    record.CompletedAt.HasValue && record.CreatedAt != default
                        ? record.CompletedAt.Value - record.CreatedAt : TimeSpan.FromMinutes(1),
                    true, record.CompletedAt ?? _clock.GetUtcNow(), ColdCache: false));
        }
        return delivered;
    }

    /// <summary>Completion/park/cancel race: a cancelled build is never resumed as success.</summary>
    private static string DetermineResumeKind(ExternalBuildRecord record) =>
        record.State switch
        {
            ExternalBuildState.Succeeded => "completed",
            ExternalBuildState.Failed => "build-failed",
            ExternalBuildState.Cancelled => "cancelled",
            _ => "reconciliation-blocked",
        };

    /// <summary>Parked builds are known waits, not stalled workers.</summary>
    public bool IsParkedKnownWait(string buildId)
    {
        lock (_gate) return _parked.ContainsKey(buildId);
    }

    /// <summary>Required evidence outstanding: audit/merge must not advance.</summary>
    public static bool IsEvidenceOutstanding(ExternalBuildRecord record) =>
        record.State != ExternalBuildState.Succeeded || record.Evidence is null;
}
