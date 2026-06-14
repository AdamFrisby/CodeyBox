namespace CodeyBox.Orchestrator;

/// <summary>
/// Façade that joins the live <see cref="TransitionHealthOptionsSnapshot"/>
/// to the data source and the pure classifier. The HTTP endpoint depends on
/// this single service so the orchestrator boundary, hot-reload, and the
/// pure scoring logic stay decoupled.
/// </summary>
public sealed class TransitionHealthService
{
    /// <summary>
    /// Per-source row cap. Sized to cover ~24h of activity at well over the
    /// fleet's observed peak. The classifier filters again on the wall-clock
    /// window, so a small over-fetch is harmless. <see cref="TransitionHealthOptions.MaxTransitions"/>
    /// further caps the scored set after classification.
    /// </summary>
    private const int DefaultMaxRowsPerSource = 100_000;

    private readonly ITransitionHealthDataSource _source;
    private readonly TransitionHealthOptionsSnapshot _options;
    private readonly TimeProvider _clock;

    public TransitionHealthService(
        ITransitionHealthDataSource source,
        TransitionHealthOptionsSnapshot options,
        TimeProvider? clock = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _clock = clock ?? TimeProvider.System;
    }

    public bool Enabled => _options.Enabled;

    public async Task<TransitionHealthReport> ComputeAsync(CancellationToken ct = default)
    {
        var opts = _options.Current;
        var now = _clock.GetUtcNow();
        var windowStart = now - opts.Window;
        var maxRows = opts.MaxTransitions is { } cap && cap > 0
            ? Math.Max(cap * 3, 1024)
            : DefaultMaxRowsPerSource;

        var snapshot = await _source.LoadAsync(windowStart, now, maxRows, ct);
        return TransitionHealthClassifier.Compute(snapshot, now, opts);
    }
}
