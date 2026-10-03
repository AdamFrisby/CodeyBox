using CodeyBox.Core;

namespace CodeyBox.Majordomo;

/// <summary>
/// Self-wakeups for the majordomo: scheduled cadence ticks (by default a
/// queue-health pass) and event-triggered passes (terminal failures, stalls
/// with free capacity). A wakeup reports into the durable conversation and,
/// in <see cref="MajordomoAutonomyMode.Autonomous"/> mode, may act — every
/// mutation still passes through <see cref="MajordomoAuthorization.Decide"/>
/// with the live policy, so a wakeup is never a privilege escalation over an
/// operator-driven turn. In <see cref="MajordomoAutonomyMode.Proposed"/> mode
/// mutations become proposals and nothing executes.
/// </summary>
/// <remarks>
/// Concurrency: one flag guards operator turns and wakeups together. A wakeup
/// arriving while a turn is in flight is skipped, never queued, so no backlog
/// accumulates. A pass that finds nothing appends no conversation row.
/// History is bounded; the store truncates oversized reports.
/// </remarks>
public sealed class MajordomoWakeupCoordinator
{
    /// <summary>Most wakeup ledger rows retained.</summary>
    public const int MaxHistoryRecords = 128;

    /// <summary>Most characters of the trigger reason kept in ledger/report echoes.</summary>
    public const int MaxReasonChars = 1000;

    /// <summary>Marker appended when a wakeup report is truncated to its char cap.</summary>
    private const string TruncationSuffix = "…[truncated]";

    private readonly TimeProvider _clock;
    private readonly Func<MajordomoWakeupOptions> _wakeupOptions;
    private readonly Func<MajordomoOptions> _policy;
    private readonly IMajordomoConversationStore _conversations;
    private readonly Func<MajordomoHistoryOptions> _historyPolicy;
    private readonly object _lock = new();
    private DateTimeOffset? _lastWakeupAt;
    private readonly List<MajordomoWakeupRecord> _history = [];
    private int _inFlight;

    public MajordomoWakeupCoordinator(
        IMajordomoConversationStore conversations,
        Func<MajordomoWakeupOptions> wakeupOptions,
        Func<MajordomoOptions> policy,
        Func<MajordomoHistoryOptions>? historyPolicy = null,
        TimeProvider? clock = null)
    {
        _conversations = conversations ?? throw new ArgumentNullException(nameof(conversations));
        _wakeupOptions = wakeupOptions ?? throw new ArgumentNullException(nameof(wakeupOptions));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _historyPolicy = historyPolicy ?? (() => new MajordomoHistoryOptions());
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>When the last wakeup pass ran (reported or quiet).</summary>
    public DateTimeOffset? LastWakeupAt
    {
        get { lock (_lock) return _lastWakeupAt; }
    }

    /// <summary>Bounded ledger of every wakeup attempt, including no-ops.</summary>
    public IReadOnlyList<MajordomoWakeupRecord> History
    {
        get { lock (_lock) return _history.ToList(); }
    }

    /// <summary>
    /// Holds the turn slot for an operator-driven turn. A wakeup attempted
    /// while the lease is alive reports <see cref="MajordomoWakeupOutcome.SkippedTurnInFlight"/>.
    /// </summary>
    public IDisposable AcquireTurn()
    {
        if (Interlocked.CompareExchange(ref _inFlight, 1, 0) != 0)
            throw new InvalidOperationException("a majordomo turn is already in flight");
        return new TurnLease(this);
    }

    /// <summary>Whether a scheduled tick is due at the current clock time.</summary>
    public bool IsScheduledDue()
    {
        var options = _wakeupOptions();
        if (!options.Enabled)
            return false;
        var now = _clock.GetUtcNow();
        lock (_lock)
        {
            if (_lastWakeupAt is null)
                return true;
            if (now < _lastWakeupAt.Value)
                return false;
            return now - _lastWakeupAt.Value >= options.WakeupInterval;
        }
    }

    /// <summary>
    /// Builds the purpose prompt for a scheduled tick: the configured prompt
    /// plus the trigger context. Bounded by the options' prompt cap plus the
    /// bounded reason echo.
    /// </summary>
    public static string BuildPrompt(MajordomoWakeupOptions options, MajordomoWakeupKind kind, string reason)
    {
        ArgumentNullException.ThrowIfNull(options);
        var safeReason = TruncateReason(reason);
        return kind == MajordomoWakeupKind.Scheduled
            ? options.PurposePrompt
            : $"{options.PurposePrompt} Trigger: {kind} — {safeReason}";
    }

    /// <summary>
    /// Runs a scheduled tick when due; returns a <c>NotDue</c> result without
    /// touching the conversation or the ledger when the cadence has not elapsed.
    /// </summary>
    public Task<MajordomoWakeupResult> RunScheduledAsync(
        Func<CancellationToken, Task<MajordomoWakeupAssessment>> assess,
        Func<MajordomoWakeupMutation, CancellationToken, Task>? execute = null,
        Func<MajordomoWakeupMutation, CancellationToken, Task>? propose = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(assess);
        if (!IsScheduledDue())
        {
            var now = _clock.GetUtcNow();
            return Task.FromResult(new MajordomoWakeupResult(
                MajordomoWakeupOutcome.NotDue, MajordomoWakeupKind.Scheduled, string.Empty, 0, 0, 0, now));
        }
        return RunAsync(MajordomoWakeupKind.Scheduled, _wakeupOptions().PurposePrompt, assess, execute, propose, ct);
    }

    /// <summary>
    /// Reacts to an event worth waking for. A burst of triggers inside
    /// <see cref="MajordomoWakeupOptions.MinTriggerInterval"/> of the last
    /// pass collapses into one <c>CollapsedBurst</c> outcome — no extra pass.
    /// </summary>
    public Task<MajordomoWakeupResult> NotifyEventAsync(
        MajordomoWakeupKind kind,
        string reason,
        Func<CancellationToken, Task<MajordomoWakeupAssessment>> assess,
        Func<MajordomoWakeupMutation, CancellationToken, Task>? execute = null,
        Func<MajordomoWakeupMutation, CancellationToken, Task>? propose = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(assess);
        if (kind == MajordomoWakeupKind.Scheduled)
            throw new ArgumentException("event wakeups must carry an event kind", nameof(kind));
        var options = _wakeupOptions();
        var now = _clock.GetUtcNow();
        lock (_lock)
        {
            if (_lastWakeupAt.HasValue && now >= _lastWakeupAt.Value
                && now - _lastWakeupAt.Value < options.MinTriggerInterval)
            {
                var safeReason = TruncateReason(reason);
                var collapsed = new MajordomoWakeupResult(
                    MajordomoWakeupOutcome.CollapsedBurst, kind, safeReason, 0, 0, 0, now);
                AppendHistory(new MajordomoWakeupRecord(
                    now, kind, safeReason, MajordomoWakeupOutcome.CollapsedBurst, 0, 0, 0));
                return Task.FromResult(collapsed);
            }
        }
        return RunAsync(kind, reason, assess, execute, propose, ct);
    }

    private async Task<MajordomoWakeupResult> RunAsync(
        MajordomoWakeupKind kind,
        string reason,
        Func<CancellationToken, Task<MajordomoWakeupAssessment>> assess,
        Func<MajordomoWakeupMutation, CancellationToken, Task>? execute,
        Func<MajordomoWakeupMutation, CancellationToken, Task>? propose,
        CancellationToken ct)
    {
        var safeReason = TruncateReason(reason);
        var now = _clock.GetUtcNow();
        var wakeupOptions = _wakeupOptions();
        if (!wakeupOptions.Enabled)
            return Record(now, kind, safeReason, MajordomoWakeupOutcome.SkippedDisabled, 0, 0, 0, updateCadence: false);

        if (Interlocked.CompareExchange(ref _inFlight, 1, 0) != 0)
            return Record(now, kind, safeReason, MajordomoWakeupOutcome.SkippedTurnInFlight, 0, 0, 0, updateCadence: false);

        try
        {
            var assessment = await assess(ct).ConfigureAwait(false)
                ?? new MajordomoWakeupAssessment(false);
            now = _clock.GetUtcNow();

            if (!assessment.HasFindings || string.IsNullOrWhiteSpace(assessment.ReportText))
                return Record(now, kind, safeReason, MajordomoWakeupOutcome.QuietNoFindings, 0, 0, 0, updateCadence: true);

            var policy = _policy();
            var usage = MajordomoTurnUsage.None;
            var executed = 0;
            var proposed = 0;
            var refused = 0;

            foreach (var mutation in assessment.Mutations)
            {
                ct.ThrowIfCancellationRequested();
                var decision = MajordomoAuthorization.Decide(
                    mutation.ToolName, mutation.Arguments, policy, usage);
                switch (decision)
                {
                    case MajordomoDecision.Execute:
                        if (execute is not null)
                        {
                            await execute(mutation, ct).ConfigureAwait(false);
                            executed++;
                            if (mutation.Arguments is MajordomoMutateArgs mutateArgs)
                                usage = usage with { MutatedItems = usage.MutatedItems + Math.Max(1, mutateArgs.AffectedItemCount) };
                        }
                        else
                        {
                            refused++;
                        }
                        break;
                    case MajordomoDecision.Propose:
                        if (propose is not null)
                        {
                            await propose(mutation, ct).ConfigureAwait(false);
                            proposed++;
                        }
                        else
                        {
                            refused++;
                        }
                        break;
                    case MajordomoDecision.Refuse:
                        refused++;
                        break;
                }
            }

            var header = $"[wakeup:{kind}] {BuildPrompt(wakeupOptions, kind, safeReason)}";
            var body = TruncateReport(assessment.ReportText, wakeupOptions.MaxReportChars);
            var footer = $"Outcome: executed={executed} proposed={proposed} refused={refused} mode={policy.Mode}.";
            var text = $"{header}\n{body}\n{footer}";
            await _conversations.AppendAsync(
                MajordomoConversationRole.Majordomo, text, null, now, _historyPolicy(), ct).ConfigureAwait(false);

            return Record(now, kind, safeReason, MajordomoWakeupOutcome.Reported, executed, proposed, refused, updateCadence: true);
        }
        finally
        {
            Interlocked.Exchange(ref _inFlight, 0);
        }
    }

    private MajordomoWakeupResult Record(
        DateTimeOffset at, MajordomoWakeupKind kind, string reason,
        MajordomoWakeupOutcome outcome, int executed, int proposed, int refused,
        bool updateCadence)
    {
        lock (_lock)
        {
            if (updateCadence)
                _lastWakeupAt = at;
            AppendHistory(new MajordomoWakeupRecord(at, kind, reason, outcome, executed, proposed, refused));
        }
        return new MajordomoWakeupResult(outcome, kind, reason, executed, proposed, refused, at);
    }

    private void AppendHistory(MajordomoWakeupRecord record)
    {
        _history.Add(record);
        if (_history.Count > MaxHistoryRecords)
            _history.RemoveRange(0, _history.Count - MaxHistoryRecords);
    }

    private static string TruncateReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return "unspecified";
        var safe = Validation.DescribeUntrustedValue(reason);
        var bounded = safe.Length <= MaxReasonChars ? safe : safe[..MaxReasonChars];
        return EscapeWakeupFraming(bounded);
    }

    private static string TruncateReport(string report, int maxChars)
    {
        var safe = EscapeWakeupFraming(report);
        if (safe.Length <= maxChars)
            return safe;
        return safe[..Math.Max(0, maxChars - TruncationSuffix.Length)] + TruncationSuffix;
    }

    /// <summary>
    /// Escapes the outer conversation framing so untrusted text stored in a
    /// Majordomo-role wakeup report cannot forge a <c>[/majordomo]</c>
    /// breakout and read as surrounding instructions. Inner untrusted-data
    /// blocks keep their own closers; only the outer transcript framing is
    /// neutralized here — the excerpt author must already have escaped inner
    /// closers before wrapping the detail block.
    /// </summary>
    public static string EscapeWakeupFraming(string text) =>
        text.Replace("[/majordomo]", "[\\/majordomo]", StringComparison.Ordinal)
            .Replace("[/operator]", "[\\/operator]", StringComparison.Ordinal);

    private sealed class TurnLease(MajordomoWakeupCoordinator owner) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                Interlocked.Exchange(ref owner._inFlight, 0);
            }
        }
    }
}
