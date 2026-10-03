using CodeyBox.Core;
using CodeyBox.Majordomo;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CodeyBox.Api.Majordomo;

/// <summary>
/// Lets the majordomo wake itself to watch the queue instead of only
/// answering when spoken to. Each scheduled tick carries the configured
/// purpose prompt (by default a queue-health pass); the tick also detects
/// the event conditions worth reacting to — a queue stalled while dispatch
/// capacity sits free — and fires those as debounced event wakeups rather
/// than extra scheduled passes. Direct push triggers (for example a work
/// item entering a terminal failure) arrive through
/// <see cref="NotifyTerminalFailureAsync"/>; a burst of triggers inside the
/// configured floor collapses into one considered pass.
/// </summary>
/// <remarks>
/// A wakeup reports into the durable conversation and never escalates
/// privilege: mutations, when an assessment carries any, pass through
/// <see cref="MajordomoAuthorization"/> with the live policy exactly like an
/// operator-driven turn. The default assessment is report-only. Wakeups are
/// skipped, not queued, while a turn is in flight, and a pass that finds
/// nothing stores no conversation row. Every attempt — including skips and
/// quiet passes — lands in <see cref="MajordomoWakeupCoordinator.History"/>.
/// </remarks>
internal sealed class MajordomoWakeupService : BackgroundService
{
    /// <summary>Upper cap for the derived poll interval.</summary>
    public static readonly TimeSpan MaxPollInterval = TimeSpan.FromMinutes(5);

    /// <summary>Lower floor for the derived poll interval.</summary>
    public static readonly TimeSpan MinPollInterval = TimeSpan.FromSeconds(30);

    private readonly MajordomoWakeupCoordinator _coordinator;
    private readonly IOptionsMonitor<MajordomoServerOptions> _options;
    private readonly MajordomoReadBackend _reads;
    private readonly TimeProvider _clock;
    private readonly ILogger _log;

    public MajordomoWakeupService(
        MajordomoWakeupCoordinator coordinator,
        IOptionsMonitor<MajordomoServerOptions> options,
        MajordomoReadBackend reads,
        TimeProvider? clock = null,
        ILogger? log = null)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _reads = reads ?? throw new ArgumentNullException(nameof(reads));
        _clock = clock ?? TimeProvider.System;
        _log = log ?? NullLogger.Instance;
    }

    /// <summary>
    /// Pure derivation of the poll interval from the live wakeup cadence: one
    /// quarter of the cadence so expiry is noticed promptly, floored so a
    /// short cadence does not hot-loop and capped so a day-long cadence still
    /// notices a config change within minutes.
    /// </summary>
    public static TimeSpan ComputePollInterval(TimeSpan cadence)
    {
        if (cadence <= TimeSpan.Zero)
            return MaxPollInterval;
        var quarter = TimeSpan.FromTicks(cadence.Ticks / 4);
        if (quarter < MinPollInterval)
            return MinPollInterval;
        return quarter > MaxPollInterval ? MaxPollInterval : quarter;
    }

    /// <summary>
    /// Pure stall predicate: items wait in <c>Queued</c> while the dispatcher
    /// has free slots on a running queue — capacity the queue is not using.
    /// </summary>
    public static bool IsStalled(QueueStatusResult queue, DispatchStatusResult dispatch)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(dispatch);
        return queue.State == QueueState.Running
            && dispatch.QueuedCount > 0
            && dispatch.CurrentlyRunning < dispatch.MaxConcurrent;
    }

    /// <summary>
    /// One watch pass: fires a debounced event wakeup when the queue is
    /// stalled with free capacity, otherwise runs the scheduled tick when
    /// due. Returns the wakeup result, or null when there was nothing due.
    /// A failed pass never returns null: the error is logged and rethrown so
    /// a failure (for example a conversation-store write failing after the
    /// assessment) cannot read as 'not due'.
    /// </summary>
    public async Task<MajordomoWakeupResult?> CheckOnceAsync(CancellationToken ct = default)
    {
        var phase = "queue read";
        try
        {
            var queue = await _reads.GetQueueStatusAsync(ct).ConfigureAwait(false);
            var dispatch = await _reads.GetDispatchStatusAsync(ct).ConfigureAwait(false);

            if (IsStalled(queue, dispatch))
            {
                phase = "stalled-queue pass";
                var detail = $"queued={dispatch.QueuedCount} running={dispatch.CurrentlyRunning}/{dispatch.MaxConcurrent}";
                return await _coordinator.NotifyEventAsync(
                    MajordomoWakeupKind.QueueStalled,
                    detail,
                    innerCt => AssessAsync(queue, dispatch, $"Queue stalled with free capacity ({detail}).", innerCt),
                    ct: ct).ConfigureAwait(false);
            }

            if (!_coordinator.IsScheduledDue())
                return null;

            phase = "scheduled pass";
            return await _coordinator.RunScheduledAsync(
                innerCt => AssessAsync(queue, dispatch, null, innerCt),
                ct: ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Majordomo wakeup: {Phase} failed; will retry next tick.", phase);
            throw;
        }
    }

    /// <summary>
    /// Push trigger for a work item entering a terminal failure. Debounced by
    /// the coordinator: a burst of failures inside the trigger floor produces
    /// one wakeup, not one per failure. The work-item id and failure text are
    /// untrusted (agent stdout / failure detail): the id is allowlisted to the
    /// system GUID alphabet (anything else becomes 'unspecified') so it can
    /// safely echo in the trigger reason, and the failure excerpt is
    /// sanitized, bounded, and replayed only inside a demarcated
    /// untrusted-data block — never as Majordomo-role prose — so a payload
    /// carrying instructions or a forged <c>[/majordomo]</c> marker cannot
    /// read as the majordomo's own reasoning.
    /// </summary>
    public Task<MajordomoWakeupResult> NotifyTerminalFailureAsync(
        string workItemId, string? failureText, CancellationToken ct = default)
    {
        var (reason, report) = BuildTerminalFailureReport(workItemId, failureText);
        return _coordinator.NotifyEventAsync(
            MajordomoWakeupKind.TerminalFailure,
            reason,
            innerCt => Task.FromResult(new MajordomoWakeupAssessment(true, report)),
            ct: ct);
    }

    /// <summary>
    /// Pure construction of the terminal-failure trigger reason and its
    /// findings report from untrusted caller input, so the sanitization and
    /// untrusted-data framing are testable without a read backend.
    /// </summary>
    internal static (string Reason, string Report) BuildTerminalFailureReport(
        string? workItemId, string? failureText)
    {
        var safeId = SanitizeWorkItemId(workItemId);
        var reason = $"work item {safeId} entered a terminal failure";
        string report;
        if (string.IsNullOrWhiteSpace(failureText))
        {
            report = $"{reason}; see get_work_item for detail.";
        }
        else
        {
            var excerpt = SanitizeFailureExcerpt(failureText);
            report = $"{reason}.\n{MajordomoContextAssembler.UntrustedDataNotice}\n" +
                $"[untrusted_tool_result failure-detail]\n{excerpt}\n[/untrusted_tool_result]\n" +
                "See get_work_item for full detail.";
        }
        return (reason, report);
    }

    /// <summary>Longest failure excerpt replayed inside the untrusted-data block.</summary>
    internal const int MaxFailureExcerptChars = 2000;

    internal static string SanitizeWorkItemId(string? workItemId)
    {
        if (string.IsNullOrWhiteSpace(workItemId))
            return "unspecified";
        var trimmed = workItemId.Trim();
        // Work-item ids are system GUIDs (WorkItemId.ToString("N")); only that
        // alphabet may echo in Majordomo-role prose. Anything else — including
        // instruction-shaped free text — collapses to 'unspecified' so the
        // trigger reason can never carry instructions into the stored report.
        if (Guid.TryParseExact(trimmed, "N", out var id)
            || Guid.TryParseExact(trimmed, "D", out id))
            return id.ToString("N");
        return "unspecified";
    }

    /// <summary>
    /// Sanitizes untrusted failure detail for the demarcated block: strips the
    /// same non-echoable class <see cref="Validation.DescribeUntrustedValue"/>
    /// drops (terminal escapes, bidi overrides, zero-width and private-use
    /// characters, lone surrogates) while keeping CR/LF/TAB prose whitespace,
    /// bounds the excerpt, and escapes embedded closing markers so the detail
    /// cannot break out of its block or forge outer transcript framing.
    /// </summary>
    internal static string SanitizeFailureExcerpt(string failureText)
    {
        ArgumentNullException.ThrowIfNull(failureText);
        var length = Math.Min(failureText.Length, MaxFailureExcerptChars);
        var builder = new System.Text.StringBuilder(length);
        for (var i = 0; i < length;)
        {
            var c = failureText[i];
            if (c is '\r' or '\n' or '\t')
            {
                builder.Append(c);
                i++;
                continue;
            }
            var width = char.IsHighSurrogate(c)
                && i + 1 < length
                && char.IsLowSurrogate(failureText[i + 1])
                ? 2 : 1;
            var category = width == 2
                ? char.GetUnicodeCategory(failureText, i)
                : char.GetUnicodeCategory(c);
            var echoable = category is not (
                System.Globalization.UnicodeCategory.Control
                or System.Globalization.UnicodeCategory.Format
                or System.Globalization.UnicodeCategory.Surrogate
                or System.Globalization.UnicodeCategory.PrivateUse
                or System.Globalization.UnicodeCategory.OtherNotAssigned
                or System.Globalization.UnicodeCategory.LineSeparator
                or System.Globalization.UnicodeCategory.ParagraphSeparator);
            if (echoable)
                builder.Append(failureText, i, width);
            i += width;
        }
        if (failureText.Length > MaxFailureExcerptChars)
            builder.Append('…');
        return builder.ToString()
            .Replace("[/untrusted_tool_result]", "[\\/untrusted_tool_result]", StringComparison.Ordinal)
            .Replace("[/majordomo]", "[\\/majordomo]", StringComparison.Ordinal)
            .Replace("[/operator]", "[\\/operator]", StringComparison.Ordinal);
    }

    private static Task<MajordomoWakeupAssessment> AssessAsync(
        QueueStatusResult queue, DispatchStatusResult dispatch, string? prefix, CancellationToken ct)
    {
        var lines = new List<string>();
        if (queue.State != QueueState.Running)
            lines.Add($"Queue is {queue.State} (paused).");
        var failures = queue.ItemCountsByState
            .Where(kv => kv.Key is WorkItemState.Failed or WorkItemState.AuditFailed
                or WorkItemState.MergeConflictResolutionFailed or WorkItemState.AbandonedAfterRecoveryAttempts
                && kv.Value > 0)
            .Select(kv => $"{kv.Key}: {kv.Value}")
            .ToList();
        if (failures.Count > 0)
            lines.Add("Terminal failures: " + string.Join(", ", failures) + ".");
        if (IsStalled(queue, dispatch))
            lines.Add($"Queue stalled: {dispatch.QueuedCount} queued with {dispatch.CurrentlyRunning}/{dispatch.MaxConcurrent} slots occupied.");
        var waitingQuota = queue.ItemCountsByState.TryGetValue(WorkItemState.WaitingForQuotaReset, out var quota) ? quota : 0;
        if (waitingQuota > 0)
            lines.Add($"Quota-blocked: {waitingQuota} items waiting for quota reset.");
        var needsInput = queue.ItemCountsByState.TryGetValue(WorkItemState.NeedsOperatorInput, out var input) ? input : 0;
        if (needsInput > 0)
            lines.Add($"Needs operator: {needsInput} items parked for input.");

        if (lines.Count == 0)
            return Task.FromResult(new MajordomoWakeupAssessment(false));

        var body = (prefix is null ? string.Empty : prefix + " ") + string.Join(" ", lines);
        return Task.FromResult(new MajordomoWakeupAssessment(true, body));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan delay;
            try
            {
                delay = ComputePollInterval(TimeSpan.FromSeconds(
                    _options.CurrentValue.Wakeup.WakeupIntervalSeconds));
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Majordomo wakeup service: failed to read options; retrying later.");
                delay = MaxPollInterval;
            }

            try
            {
                await Task.Delay(delay, _clock, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }

            try
            {
                var result = await CheckOnceAsync(stoppingToken).ConfigureAwait(false);
                if (result is not null)
                    _log.LogInformation(
                        "Majordomo wakeup: {Kind} concluded {Outcome}.",
                        result.Kind, result.Outcome);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Majordomo wakeup service: watch pass failed; will retry.");
            }
        }
    }
}
