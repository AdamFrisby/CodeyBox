using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Periodically walks terminal-failure rows (Failed, AuditFailed,
/// MergeConflictResolutionFailed), classifies each via
/// <see cref="ITerminalFailureClassifier"/>, and routes by class:
/// <list type="bullet">
///   <item><c>Transient</c> → schedule auto-retry with exponential backoff + jitter, capped per item, then dead-letter to <see cref="WorkItemState.NeedsOperatorInput"/>.</item>
///   <item><c>Deterministic</c> → no auto-retry; leave parked and emit a single audit-log line so the operator can see why.</item>
///   <item><c>PolicyQuota</c> → delegated to <c>QuotaRetryScheduler</c>; no-op here.</item>
///   <item><c>Unknown</c> → fail-closed: park (no retry).</item>
/// </list>
/// This replaces the external operator chaperone's blunt
/// "requeue every terminal failure" reflex: TRANSIENT failures still
/// recover automatically (the chaperone's only legitimate function), but
/// DETERMINISTIC failures stop looping.
/// </summary>
public sealed class TerminalFailureRecoveryService : BackgroundService
{
    // Mirrors QuotaRetryScheduler's accessor-poll cadence: while the
    // service is disabled, check the live accessor at this interval so
    // hot-enable does not require a restart or an unrelated wake-up.
    private static readonly TimeSpan OptionsReloadPollInterval = TimeSpan.FromSeconds(1);

    private readonly IWorkItemStore _store;
    private readonly WorkItemRetrier _retrier;
    private readonly ITerminalFailureClassifier _classifier;
    private readonly Func<TerminalFailureRecoveryOptions> _optionsAccessor;
    private readonly TimeProvider _time;
    private readonly Func<int, int> _jitter;
    private readonly ILogger<TerminalFailureRecoveryService> _log;

    private static readonly WorkItemState[] TerminalStatesUnderRecovery =
    {
        WorkItemState.Failed,
        WorkItemState.AuditFailed,
        WorkItemState.MergeConflictResolutionFailed,
    };

    public TerminalFailureRecoveryService(
        IWorkItemStore store,
        WorkItemRetrier retrier,
        ITerminalFailureClassifier classifier,
        Func<TerminalFailureRecoveryOptions> optionsAccessor,
        ILogger<TerminalFailureRecoveryService> log,
        TimeProvider? timeProvider = null,
        Func<int, int>? jitter = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _retrier = retrier ?? throw new ArgumentNullException(nameof(retrier));
        _classifier = classifier ?? throw new ArgumentNullException(nameof(classifier));
        _optionsAccessor = optionsAccessor ?? throw new ArgumentNullException(nameof(optionsAccessor));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _time = timeProvider ?? TimeProvider.System;
        // Random is non-deterministic; tests inject a fixed jitter to keep
        // backoff windows reproducible.
        _jitter = jitter ?? DefaultJitter;
    }

    private TerminalFailureRecoveryOptions CurrentOptions
    {
        get
        {
            try { return _optionsAccessor(); }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to read live terminal-failure recovery options; using defaults");
                return new TerminalFailureRecoveryOptions();
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var wasEnabled = false;
        var loggedDisabled = false;
        var lastSweepAt = _time.GetUtcNow();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var opts = CurrentOptions;
                if (!opts.Enabled)
                {
                    if (!loggedDisabled)
                    {
                        _log.LogInformation("Terminal-failure recovery is disabled");
                        loggedDisabled = true;
                    }
                    wasEnabled = false;
                    await Task.Delay(OptionsReloadPollInterval, stoppingToken);
                    continue;
                }

                loggedDisabled = false;
                if (!wasEnabled)
                {
                    _log.LogInformation(
                        "Terminal-failure recovery enabled. Interval={Interval} baseBackoff={BaseBackoff} maxBackoff={MaxBackoff} jitter={Jitter} maxRetries={MaxRetries}",
                        opts.PeriodicCheckInterval, opts.BaseBackoff, opts.MaxBackoff, opts.JitterFraction, opts.MaxAutoRetriesPerWorkItem);
                    wasEnabled = true;
                    lastSweepAt = _time.GetUtcNow() - opts.PeriodicCheckInterval; // immediate sweep on enable
                }

                var interval = opts.PeriodicCheckInterval;
                if (interval <= TimeSpan.Zero)
                {
                    _log.LogWarning(
                        "Terminal-failure recovery interval {Interval} is invalid; using {Fallback}",
                        interval, OptionsReloadPollInterval);
                    interval = OptionsReloadPollInterval;
                }

                var now = _time.GetUtcNow();
                var nextSweepAt = lastSweepAt + interval;
                if (now < nextSweepAt)
                {
                    var delay = nextSweepAt - now;
                    if (delay > OptionsReloadPollInterval)
                        delay = OptionsReloadPollInterval;
                    await Task.Delay(delay, stoppingToken);
                    continue;
                }

                lastSweepAt = now;
                await RunPeriodicSweepAsync(opts, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Error during terminal-failure recovery sweep");
            }
        }
    }

    /// <summary>
    /// Test hook: walks the terminal-state rows once and applies the
    /// classifier verdict to each. Public so the orchestrator can wire it
    /// to a webhook / signal in a future change without changing the sweep
    /// cadence.
    /// </summary>
    internal async Task RunPeriodicSweepAsync(TerminalFailureRecoveryOptions opts, CancellationToken ct)
    {
        _log.LogDebug("Starting terminal-failure recovery sweep");
        foreach (var state in TerminalStatesUnderRecovery)
        {
            await foreach (var item in _store.ListByStateAsync(state, ct))
            {
                try
                {
                    await EvaluateAsync(item, opts, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Error processing terminal failure for work item {Id}; continuing sweep", item.Id);
                }
            }
        }
    }

    /// <summary>
    /// Apply the classifier verdict to a single work item. Visible to
    /// tests so they can exercise individual classifications without
    /// driving the sweep loop.
    /// </summary>
    internal async Task EvaluateAsync(WorkItem item, TerminalFailureRecoveryOptions opts, CancellationToken ct)
    {
        var verdict = _classifier.Classify(item);

        switch (verdict.Class)
        {
            case TerminalFailureClass.PolicyQuota:
                // Owned by QuotaRetryScheduler. Surface a single audit line so
                // the failure-class taxonomy is complete in the audit log,
                // then no-op — competing with the quota scheduler's targeted
                // timer would just double-fire retries.
                AuditLog.TerminalFailureClassified(
                    item.Id,
                    failureClass: nameof(TerminalFailureClass.PolicyQuota),
                    reason: verdict.Reason,
                    state: item.State.ToString(),
                    action: "delegated",
                    attempt: item.TerminalRetryAttempts,
                    maxAttempts: opts.MaxAutoRetriesPerWorkItem,
                    nextRetryAt: item.NextQuotaRetryAt);
                break;

            case TerminalFailureClass.Deterministic:
            case TerminalFailureClass.Unknown:
                await HandleNonRetryableAsync(item, opts, verdict, ct);
                break;

            case TerminalFailureClass.Transient:
                await HandleTransientAsync(item, opts, verdict, ct);
                break;

            default:
                _log.LogWarning(
                    "Unrecognised TerminalFailureClass {Class} for work item {Id}; treating as Unknown (park)",
                    verdict.Class, item.Id);
                await HandleNonRetryableAsync(item, opts, verdict with { Class = TerminalFailureClass.Unknown }, ct);
                break;
        }
    }

    private Task HandleNonRetryableAsync(
        WorkItem item,
        TerminalFailureRecoveryOptions opts,
        TerminalFailureClassification verdict,
        CancellationToken ct)
    {
        // No state mutation: the item is already in its terminal state.
        // Single audit-log line per sweep so operators can see why nothing
        // is happening. The log row is emitted on EVERY sweep so the
        // verdict stays observable — the alternative (one-shot logging
        // via a "classified" flag) hides the verdict from any operator
        // who picks up the dashboard later.
        AuditLog.TerminalFailureClassified(
            item.Id,
            failureClass: verdict.Class.ToString(),
            reason: verdict.Reason,
            state: item.State.ToString(),
            action: "parked",
            attempt: item.TerminalRetryAttempts,
            maxAttempts: opts.MaxAutoRetriesPerWorkItem,
            nextRetryAt: null);
        _ = item;
        _ = ct;
        return Task.CompletedTask;
    }

    private async Task HandleTransientAsync(
        WorkItem item,
        TerminalFailureRecoveryOptions opts,
        TerminalFailureClassification verdict,
        CancellationToken ct)
    {
        if (item.TerminalRetryAttempts >= opts.MaxAutoRetriesPerWorkItem)
        {
            await DeadLetterAsync(item, opts, verdict, ct);
            return;
        }

        var now = _time.GetUtcNow();
        if (item.NextTerminalRetryAt is { } scheduled && scheduled > now)
        {
            // Backoff window still open. Re-arm the audit log so an
            // operator watching the dashboard sees the next scheduled
            // attempt.
            AuditLog.TerminalFailureClassified(
                item.Id,
                failureClass: nameof(TerminalFailureClass.Transient),
                reason: verdict.Reason,
                state: item.State.ToString(),
                action: "scheduled",
                attempt: item.TerminalRetryAttempts,
                maxAttempts: opts.MaxAutoRetriesPerWorkItem,
                nextRetryAt: scheduled);
            return;
        }

        if (item.NextTerminalRetryAt is null)
        {
            // First time we've seen this failure: arm the backoff timer
            // and wait for the next sweep to actually retry. Splitting
            // arming from retry means jitter is observable on the first
            // sweep and the operator sees a scheduled wakeup before any
            // sandbox burns.
            var armed = await ArmBackoffAsync(item, opts, verdict, ct);
            if (armed is null) return;
            return; // logged inside ArmBackoffAsync
        }

        await ExecuteRetryAsync(item, opts, verdict, ct);
    }

    /// <summary>
    /// Stamp the next scheduled retry time on the row and persist. Returns
    /// the post-arm WorkItem so the caller can chain; returns null when
    /// the row's state changed concurrently (in which case the next sweep
    /// will pick up the new state).
    /// </summary>
    private async Task<WorkItem?> ArmBackoffAsync(
        WorkItem item,
        TerminalFailureRecoveryOptions opts,
        TerminalFailureClassification verdict,
        CancellationToken ct)
    {
        var delay = ComputeBackoff(opts, item.TerminalRetryAttempts);
        var nextAt = _time.GetUtcNow() + delay;
        var withSchedule = item with { NextTerminalRetryAt = nextAt };
        var updated = await _store.TryUpdateIfStateAsync(withSchedule, item.State, ct);
        if (!updated)
        {
            _log.LogDebug(
                "Work item {Id} state changed while arming terminal-failure backoff; skipping",
                item.Id);
            return null;
        }

        AuditLog.TerminalFailureClassified(
            item.Id,
            failureClass: nameof(TerminalFailureClass.Transient),
            reason: verdict.Reason,
            state: item.State.ToString(),
            action: "scheduled",
            attempt: item.TerminalRetryAttempts,
            maxAttempts: opts.MaxAutoRetriesPerWorkItem,
            nextRetryAt: nextAt);
        return withSchedule;
    }

    private async Task ExecuteRetryAsync(
        WorkItem item,
        TerminalFailureRecoveryOptions opts,
        TerminalFailureClassification verdict,
        CancellationToken ct)
    {
        // Bump the persisted counter BEFORE handing off to the retrier so
        // a crash mid-retry cannot loop the cap.
        var nextAttempt = item.TerminalRetryAttempts + 1;
        var counted = item with
        {
            TerminalRetryAttempts = nextAttempt,
            NextTerminalRetryAt = null,
        };
        var counterPersisted = await _store.TryUpdateIfStateAsync(counted, item.State, ct);
        if (!counterPersisted)
        {
            _log.LogDebug(
                "Work item {Id} state changed before terminal-failure retry attempt could be persisted; skipping",
                item.Id);
            return;
        }

        var (success, error, _, actualFrom, _) = await _retrier.RetryAsync(
            counted, from: null, trigger: "terminal-failure-recovery", ct);

        if (!success)
        {
            _log.LogWarning(
                "Terminal-failure recovery retry failed for work item {Id}: {Error}",
                item.Id, error);
            AuditLog.TerminalFailureClassified(
                item.Id,
                failureClass: nameof(TerminalFailureClass.Transient),
                reason: $"retry-failed: {error}",
                state: counted.State.ToString(),
                action: "retried",
                attempt: nextAttempt,
                maxAttempts: opts.MaxAutoRetriesPerWorkItem,
                nextRetryAt: null);
            return;
        }

        AuditLog.TerminalFailureClassified(
            item.Id,
            failureClass: nameof(TerminalFailureClass.Transient),
            reason: verdict.Reason,
            state: item.State.ToString(),
            action: "retried",
            attempt: nextAttempt,
            maxAttempts: opts.MaxAutoRetriesPerWorkItem,
            nextRetryAt: null);
        _ = actualFrom;
    }

    private async Task DeadLetterAsync(
        WorkItem item,
        TerminalFailureRecoveryOptions opts,
        TerminalFailureClassification verdict,
        CancellationToken ct)
    {
        var lastError =
            $"Transient terminal-failure auto-retry reached max attempts ({opts.MaxAutoRetriesPerWorkItem}). " +
            $"Previous error: {item.LastError ?? "(none)"}. Operator intervention required.";

        // NeedsOperatorInput is the operator-visible park state — the
        // pipeline already treats it as a "yes I see it" inbox and the
        // recovery service's job here is simply to flip the row off
        // Failed so it shows up in that inbox. The work-item retrier
        // exposes the manual retry path; we DON'T loop here.
        var parked = item.With(WorkItemState.NeedsOperatorInput, lastError) with
        {
            NextTerminalRetryAt = null,
        };

        var updated = await _store.TryUpdateIfStateAsync(parked, item.State, ct);
        if (!updated)
        {
            _log.LogDebug(
                "Work item {Id} state changed before terminal-failure dead-letter could land; skipping",
                item.Id);
            return;
        }

        AuditLog.TerminalFailureClassified(
            item.Id,
            failureClass: nameof(TerminalFailureClass.Transient),
            reason: verdict.Reason,
            state: item.State.ToString(),
            action: "dead-lettered",
            attempt: item.TerminalRetryAttempts,
            maxAttempts: opts.MaxAutoRetriesPerWorkItem,
            nextRetryAt: null);
    }

    private TimeSpan ComputeBackoff(TerminalFailureRecoveryOptions opts, int attemptsSoFar)
    {
        // Exponential: attempt 0 → base, attempt 1 → 2x, attempt 2 → 4x …
        // Clamped to MaxBackoff so a high MaxAutoRetriesPerWorkItem can't
        // produce an unbounded wait window.
        var shift = Math.Min(attemptsSoFar, 30); // protect against int overflow at extreme caps
        long baseTicks = opts.BaseBackoff.Ticks <= 0 ? TimeSpan.FromSeconds(1).Ticks : opts.BaseBackoff.Ticks;
        long targetTicks;
        try
        {
            targetTicks = baseTicks << shift;
            if (targetTicks <= 0 || targetTicks > opts.MaxBackoff.Ticks)
                targetTicks = opts.MaxBackoff.Ticks;
        }
        catch (OverflowException)
        {
            targetTicks = opts.MaxBackoff.Ticks;
        }
        var deterministic = TimeSpan.FromTicks(targetTicks);

        if (opts.JitterFraction <= 0)
            return deterministic;

        // Bucketed jitter: scale the deterministic delay by ±jitterFraction.
        // _jitter() returns the bucket index in [0, 1000]; we map it into
        // the [-jitterFraction, +jitterFraction] band so tests can pin a
        // single bucket (e.g. 500 = no offset).
        var bucket = _jitter(1001);
        if (bucket < 0) bucket = 0;
        if (bucket > 1000) bucket = 1000;
        var scaled = ((bucket - 500) / 500.0) * opts.JitterFraction;
        var jittered = deterministic.Ticks + (long)(deterministic.Ticks * scaled);
        if (jittered <= 0) jittered = 1;
        return TimeSpan.FromTicks(jittered);
    }

    private static int DefaultJitter(int exclusiveUpperBound)
        => Random.Shared.Next(exclusiveUpperBound);
}
