using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace CodeyBox.Api;

/// <summary>
/// Reads audit-tier log files (NDJSON CLEF format) and builds a timeline of
/// events for a single work item. Terminal items are cached in memory;
/// in-flight items are re-read on every request.
///
/// Files are read line-by-line so a multi-GB log file never loads into RAM.
/// </summary>
internal sealed class AuditLogTimelineReader
{
    private readonly AuditLogOptions _opts;
    private readonly ConcurrentDictionary<string, IReadOnlyList<TimelineEntry>> _cache = new();

    public AuditLogTimelineReader(AuditLogOptions opts) => _opts = opts;

    public async ValueTask<IReadOnlyList<TimelineEntry>> GetTimelineAsync(
        string workItemId, bool isTerminal, DateTimeOffset createdAt, CancellationToken ct)
    {
        if (isTerminal && _cache.TryGetValue(workItemId, out var cached))
            return cached;

        var entries = await BuildTimelineAsync(workItemId, createdAt, ct);

        if (isTerminal)
            _cache.TryAdd(workItemId, entries);

        return entries;
    }

    private async Task<IReadOnlyList<TimelineEntry>> BuildTimelineAsync(
        string workItemId, DateTimeOffset createdAt, CancellationToken ct)
    {
        var files = FindAuditFiles(createdAt);
        var parsed = new List<(DateTimeOffset Time, int Seq, string EventName, JsonElement Root)>();

        // Seq is the read order (files are already date-ordered, lines are in
        // file order). It breaks timestamp ties deterministically so equal-@t
        // events always render in the same order.
        var seq = 0;
        foreach (var file in files)
        {
            await foreach (var (eventTime, eventName, eventRoot) in ReadFileAsync(file, workItemId, ct))
                parsed.Add((eventTime, seq++, eventName, eventRoot));
        }

        parsed.Sort((a, b) =>
        {
            var c = DateTimeOffset.Compare(a.Time, b.Time);
            return c != 0 ? c : a.Seq.CompareTo(b.Seq);
        });
        return MapEntries(parsed);
    }

    private IEnumerable<string> FindAuditFiles(DateTimeOffset createdAt)
    {
        var auditPath = Path.GetFullPath(_opts.AuditPath);
        var dir = Path.GetDirectoryName(auditPath);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            return [];

        // e.g. AuditPath="logs/audit-.json" → stem="audit-", ext=".json"
        var stem = Path.GetFileNameWithoutExtension(auditPath);
        var ext = Path.GetExtension(auditPath);
        var pattern = stem + "*" + ext;

        var startDate = DateOnly.FromDateTime(createdAt.UtcDateTime);

        return Directory
            .GetFiles(dir, pattern, SearchOption.TopDirectoryOnly)
            .Where(f => GetFileDate(f, stem) >= startDate)
            .OrderBy(f => GetFileSortKey(f, stem));
    }

    // Comparer key: (dateStr, sequence) — sorts size-rolled files correctly.
    private static (string Date, int Seq) GetFileSortKey(string filePath, string prefix)
    {
        var name = Path.GetFileNameWithoutExtension(filePath); // "audit-20260501" or "audit-20260501_001"
        if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return ("zzzzzzzz", 999);
        var rest = name[prefix.Length..]; // "20260501" or "20260501_001"
        var ui = rest.IndexOf('_');
        if (ui < 0) return (rest, 0);
        return (rest[..ui], int.TryParse(rest[(ui + 1)..], out var s) ? s : 0);
    }

    private static DateOnly GetFileDate(string filePath, string prefix)
    {
        var (date, _) = GetFileSortKey(filePath, prefix);
        return DateOnly.TryParseExact(date, "yyyyMMdd", out var d) ? d : DateOnly.MaxValue;
    }

    private static async IAsyncEnumerable<(DateTimeOffset, string, JsonElement)> ReadFileAsync(
        string path, string workItemId, [EnumeratorCancellation] CancellationToken ct)
    {
        FileStream? fs;
        try
        {
            fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        }
        catch (IOException)
        {
            yield break;
        }

        await using (fs)
        using (var reader = new StreamReader(fs, Encoding.UTF8))
        {
            string? line;
            while ((line = await reader.ReadLineAsync(ct)) is not null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                JsonDocument doc;
                try { doc = JsonDocument.Parse(line); }
                catch (JsonException) { continue; }

                using (doc)
                {
                    var root = doc.RootElement;

                    if (!root.TryGetProperty("WorkItemId", out var widProp) ||
                        widProp.GetString() != workItemId)
                        continue;

                    if (!root.TryGetProperty("EventName", out var evProp))
                        continue;
                    var eventName = evProp.GetString();
                    if (string.IsNullOrEmpty(eventName)) continue;

                    if (!root.TryGetProperty("@t", out var tProp) ||
                        !DateTimeOffset.TryParse(
                            tProp.GetString(), null,
                            System.Globalization.DateTimeStyles.RoundtripKind, out var time))
                        continue;

                    // Clone before the document is disposed.
                    yield return (time, eventName, root.Clone());
                }
            }
        }
    }

    // ── Entry mapping ──────────────────────────────────────────────────────────

    private static IReadOnlyList<TimelineEntry> MapEntries(
        List<(DateTimeOffset Time, int Seq, string EventName, JsonElement Root)> events)
    {
        var result = new List<TimelineEntry>(events.Count);
        string? prevState = null;
        int pendingIteration = 1;

        foreach (var (time, _, eventName, root) in events)
        {
            var entry = MapEvent(time, eventName, root, ref prevState, pendingIteration);
            if (entry is null) continue;

            if (entry.Kind == "iteration_complete" &&
                root.TryGetProperty("Iteration", out var ip) && ip.TryGetInt32(out var i))
                pendingIteration = i + 1;

            result.Add(entry);
        }

        return result;
    }

    private static TimelineEntry? MapEvent(
        DateTimeOffset time, string eventName, JsonElement root,
        ref string? prevState, int pendingIteration)
    {
        switch (eventName)
        {
            case "work_item.created":
                {
                    var title = SafeText(GetStr(root, "Title"), 256);
                    var summary = title is not null ? $"Created (Queued): {SafeText(title, 80)}" : "Created (Queued)";
                    var details = new { from = prevState, to = "Queued", title };
                    prevState = "Queued";
                    return new TimelineEntry(time, "state_transition", summary, details);
                }
            case "work_item.transitioned":
                {
                    var to = SafeText(GetStr(root, "State"), 64) ?? "Unknown";
                    var summary = prevState is not null ? $"{prevState} → {to}" : $"→ {to}";
                    var details = new { from = prevState, to };
                    prevState = to;
                    return new TimelineEntry(time, "state_transition", summary, details);
                }
            case "work_item.picked_up":
                {
                    // A pickup is NOT a state transition: the dispatcher hands
                    // the item to a worker without changing its lifecycle
                    // state (a watchdog-recovered WorkComplete item is picked
                    // up still in WorkComplete). Rendering this as
                    // "X → Working" fabricated a transition the log never
                    // recorded, e.g. "Working → Working". The pickup keeps its
                    // own kind and never touches prevState.
                    var worker = GetWorkerLabel(root);
                    var state = SafeText(GetStr(root, "State"), 64);
                    var attempt = GetIntNullable(root, "Attempt");
                    if (state is null)
                    {
                        var details = new { workerId = worker, state = (string?)null, attempt = attempt, legacy = true };
                        return new TimelineEntry(time, "pickup",
                            $"Picked up by worker {worker} (state unknown — legacy record; not a state transition)",
                            details);
                    }
                    var summary = attempt.HasValue
                        ? $"Picked up by worker {worker} in {state} (attempt {attempt})"
                        : $"Picked up by worker {worker} in {state}";
                    var knownDetails = new { workerId = worker, state, attempt };
                    return new TimelineEntry(time, "pickup", summary, knownDetails);
                }
            case "work_item.cancelled":
                {
                    var summary = prevState is not null ? $"{prevState} → Cancelled" : "→ Cancelled";
                    var details = new { from = prevState, to = "Cancelled" };
                    prevState = "Cancelled";
                    return new TimelineEntry(time, "state_transition", summary, details);
                }
            case "work_item.no_action_required":
                {
                    // Terminal resolution recorded by the agent itself; the
                    // item genuinely left its prior state for NoActionRequired.
                    var reason = SafeText(NonEmptyOrNull(GetStr(root, "Reason")), 120);
                    var summary = reason is null
                        ? "Resolved with no action required (no recorded reason)"
                        : $"Resolved with no action required: {reason}";
                    var details = new { from = prevState, to = "NoActionRequired", reason };
                    prevState = "NoActionRequired";
                    return new TimelineEntry(time, "state_transition", summary, details);
                }
            case "work_item.recovered":
                {
                    var attempt = GetIntNullable(root, "Attempt");
                    var (from, to, summary) = RecoveryCore(root, "Recovered", worker: null, attempt);
                    return AdvanceRecovery(time, ref prevState, to, summary, new { from, to, workerId = (string?)null, attempt });
                }
            case "work_item.watchdog_recovered":
                {
                    var worker = GetWorkerLabelOrNull(root);
                    var (from, to, summary) = RecoveryCore(root, "Watchdog recovery", worker, attempt: null);
                    var dependentsRestored = GetIntNullable(root, "DependentsRestored");
                    return AdvanceRecovery(time, ref prevState, to, summary, new { from, to, workerId = worker, attempt = (int?)null, dependentsRestored });
                }
            case "work_item.worker_dead_recovered":
                {
                    var worker = GetWorkerLabelOrNull(root);
                    var attempt = GetIntNullable(root, "Attempt");
                    var (from, to, summary) = RecoveryCore(root, "Dead-worker recovery", worker, attempt);
                    return AdvanceRecovery(time, ref prevState, to, summary, new { from, to, workerId = worker, attempt });
                }
            case "work_item.item_stale_recovered":
                {
                    var worker = GetWorkerLabelOrNull(root);
                    var attempt = GetIntNullable(root, "Attempt");
                    var trigger = SafeText(GetStr(root, "Trigger"), 64);
                    var branchPreserved = GetBoolNullable(root, "BranchPreserved");
                    var (from, to, core) = RecoveryCore(root, "Item-stale recovery", worker, attempt);
                    var summary = trigger is null ? core : $"{core} (trigger: {trigger})";
                    return AdvanceRecovery(time, ref prevState, to, summary, new { from, to, workerId = worker, attempt, trigger, branchPreserved });
                }
            case "work_item.dependent_restored":
                {
                    var parentId = SafeText(GetStr(root, "ParentWorkItemId"), 64) ?? "unknown";
                    var shortParent = parentId.Length >= 8 ? parentId[..8] : parentId;
                    var summary = $"Restored to Queued (parent {shortParent}… recovered)";
                    var details = new { from = prevState, to = "Queued", parentWorkItemId = parentId };
                    prevState = "Queued";
                    return new TimelineEntry(time, "recovery", summary, details);
                }
            case "work_item.transient_cancel_retried":
                {
                    var phase = SafeText(GetStr(root, "Phase"), 64) ?? "unknown phase";
                    var source = SafeText(GetStr(root, "CancellationSource"), 128);
                    var attempt = GetIntNullable(root, "Attempt");
                    var maxAttempts = GetIntNullable(root, "MaxAttempts");
                    var summary = $"Auto-retried after transient cancellation in {phase}" +
                        (attempt.HasValue && maxAttempts.HasValue ? $" (attempt {attempt}/{maxAttempts})" : "") +
                        (source is null ? " (cancellation source not recorded)" : $": {source}");
                    var details = new { phase, cancellationSource = source, attempt, maxAttempts };
                    return new TimelineEntry(time, "recovery", summary, details);
                }
            case "work_item.interrupted_by_host_shutdown":
                {
                    var (from, to, summary) = RecoveryCore(root, "Host-shutdown checkpoint", worker: null, attempt: null);
                    return AdvanceRecovery(time, ref prevState, to, summary, new { from, to });
                }
            case "work_item.watchdog_stuck":
                {
                    var worker = GetWorkerLabel(root);
                    var state = SafeText(GetStr(root, "State"), 64) ?? "unknown";
                    var seconds = GetLongNullable(root, "SinceProgressSeconds");
                    var lastStream = SafeText(GetStr(root, "LastStreamEvent"), 128);
                    var summary = seconds.HasValue
                        ? $"Watchdog: no progress for {seconds}s in {state} (worker {worker})"
                        : $"Watchdog: no progress in {state} (worker {worker}; duration not recorded)";
                    if (lastStream is not null) summary += $"; last stream event: {lastStream}";
                    var details = new { workerId = worker, state, sinceProgressSeconds = seconds, lastStreamEvent = lastStream };
                    return new TimelineEntry(time, "watchdog_stuck", summary, details);
                }
            case "work_item.item_stale_detected":
                {
                    var worker = GetWorkerLabel(root);
                    var state = SafeText(GetStr(root, "State"), 64) ?? "unknown";
                    var seconds = GetLongNullable(root, "SinceUpdatedSeconds");
                    var trigger = SafeText(GetStr(root, "Trigger"), 64);
                    var summary = seconds.HasValue
                        ? $"Item-stale: no update for {seconds}s in {state} (worker {worker})"
                        : $"Item-stale: no update in {state} (worker {worker}; duration not recorded)";
                    if (trigger is not null) summary += $" (trigger: {trigger})";
                    var details = new { workerId = worker, state, sinceUpdatedSeconds = seconds, trigger };
                    return new TimelineEntry(time, "watchdog_stuck", summary, details);
                }
            case "work_item.watchdog_parked":
                {
                    var worker = GetWorkerLabel(root);
                    var from = SafeText(GetStr(root, "FromState"), 64);
                    var summary = $"Parked for operator triage (worker {worker}; auto-recover disabled)" +
                        (from is null ? " (prior state not recorded)" : $" — was {from}");
                    var details = new { from = from ?? prevState, to = "NeedsOperatorInput", workerId = worker };
                    prevState = "NeedsOperatorInput";
                    return new TimelineEntry(time, "state_transition", summary, details);
                }
            case "work_item.worker_dead_failed_terminal":
                {
                    var worker = GetWorkerLabel(root);
                    var attempt = GetIntNullable(root, "Attempt");
                    var summary = attempt.HasValue
                        ? $"Abandoned after {attempt} recovery attempts (worker {worker} dead)"
                        : $"Abandoned after recovery attempts exhausted (worker {worker} dead; attempt not recorded)";
                    var details = new { from = prevState, to = "AbandonedAfterRecoveryAttempts", workerId = worker, attempt };
                    prevState = "AbandonedAfterRecoveryAttempts";
                    return new TimelineEntry(time, "state_transition", summary, details);
                }
            case "work_item.abandoned_after_recovery":
                {
                    var maxAttempts = GetIntNullable(root, "MaxAttempts");
                    var summary = maxAttempts.HasValue
                        ? $"Abandoned after {maxAttempts} recovery attempts; operator intervention required"
                        : "Abandoned after recovery attempts; operator intervention required (limit not recorded)";
                    var details = new { from = prevState, to = "AbandonedAfterRecoveryAttempts", maxAttempts };
                    prevState = "AbandonedAfterRecoveryAttempts";
                    return new TimelineEntry(time, "state_transition", summary, details);
                }
            case "work_item.terminal_failure_classified":
                {
                    var failureClass = SafeText(GetStr(root, "FailureClass"), 64) ?? "unknown";
                    var state = SafeText(GetStr(root, "State"), 64);
                    var action = SafeText(GetStr(root, "Action"), 64) ?? "unknown";
                    var attempt = GetIntNullable(root, "Attempt");
                    var maxAttempts = GetIntNullable(root, "MaxAttempts");
                    var reason = SafeText(NonEmptyOrNull(GetStr(root, "Reason")), 256);
                    var nextRetryAt = SafeText(GetStr(root, "NextRetryAt"), 64);
                    if (string.IsNullOrEmpty(nextRetryAt)) nextRetryAt = null;
                    var summary = $"Terminal failure classified: {failureClass} → {action}" +
                        (state is null ? "" : $" (state {state})") +
                        (attempt.HasValue && maxAttempts.HasValue ? $" (attempt {attempt}/{maxAttempts})" : "") +
                        (reason is null ? " — no recorded cause; not inferred" : $": {reason}");
                    var details = new { failureClass, state, action, attempt, maxAttempts, reason, nextRetryAt };
                    return new TimelineEntry(time, "failure_classified", summary, details);
                }
            case "work_item.post_agent_timeout":
                {
                    var phase = SafeText(GetStr(root, "Phase"), 64) ?? "unknown phase";
                    var timeout = GetLongNullable(root, "TimeoutSeconds");
                    var summary = timeout.HasValue
                        ? $"Post-agent step '{phase}' exceeded {timeout}s; item failed to release pool slot"
                        : $"Post-agent step '{phase}' timed out (timeout not recorded); item failed to release pool slot";
                    var details = new { phase, timeoutSeconds = timeout };
                    return new TimelineEntry(time, "infra_failure", summary, details);
                }
            case "sandbox.agent_infra_failure":
                {
                    var agent = SafeText(GetStr(root, "Agent"), 64) ?? "unknown agent";
                    var sandbox = SafeText(GetStr(root, "Sandbox"), 128);
                    var phase = SafeText(GetStr(root, "Phase"), 64) ?? "unknown phase";
                    var summaryText = SafeText(GetStr(root, "Summary"), 256);
                    var reason = SafeText(NonEmptyOrNull(GetStr(root, "Reason")), 256);
                    var summary = $"Agent infrastructure failure in {phase} ({agent})" +
                        (summaryText is null ? " — no recorded summary" : $": {summaryText}") +
                        (reason is null ? "" : $" (reason: {reason})");
                    var details = new { agent, sandbox, phase, summary = summaryText, reason };
                    return new TimelineEntry(time, "infra_failure", summary, details);
                }
            case "sandbox.provisioning_deferred":
                {
                    var provider = SafeText(GetStr(root, "Provider"), 64);
                    var operation = SafeText(GetStr(root, "Operation"), 64);
                    var errorClass = SafeText(GetStr(root, "ErrorClass"), 128);
                    var resumeState = SafeText(GetStr(root, "ResumeState"), 64);
                    var recheck = GetLongNullable(root, "RecheckSeconds");
                    var detail = SafeText(GetStr(root, "Detail"), 256);
                    if (string.IsNullOrEmpty(detail)) detail = null;
                    var what = (provider ?? "unknown provider") + "/" + (operation ?? "unknown operation");
                    var summary = $"Sandbox provisioning deferred: {what}" +
                        (errorClass is null ? " (error class not recorded)" : $" ({errorClass})") +
                        (resumeState is null ? "; resume state not recorded" : $"; resumes from {resumeState}") +
                        (recheck.HasValue ? $" in {recheck}s" : "") +
                        (detail is null ? " — no recorded cause; not inferred" : $": {detail}");
                    var details = new { provider, operation, errorClass, resumeState, recheckSeconds = recheck, detail };
                    return new TimelineEntry(time, "deferral", summary, details);
                }
            case "durable_resume.deferred":
                {
                    var agent = SafeText(GetStr(root, "PinnedAgent"), 64);
                    var reason = SafeText(NonEmptyOrNull(GetStr(root, "Reason")), 256);
                    var summary = $"Durable resume deferred (pinned agent {agent ?? "unknown"})" +
                        (reason is null ? " — no recorded cause; not inferred" : $": {reason}");
                    var details = new { pinnedAgent = agent, reason };
                    return new TimelineEntry(time, "deferral", summary, details);
                }
            case "durable_resume.attempt_refunded":
                {
                    var agent = SafeText(GetStr(root, "Agent"), 64) ?? "unknown agent";
                    var reason = SafeText(NonEmptyOrNull(GetStr(root, "Reason")), 256);
                    var summary = $"Durable resume dispatch attempt refunded ({agent})" +
                        (reason is null ? "" : $": {reason}");
                    var details = new { agent, reason };
                    return new TimelineEntry(time, "recovery", summary, details);
                }
            case "durable_resume.rerouted":
                {
                    var from = SafeText(GetStr(root, "RejectedAgent"), 64) ?? "unknown";
                    var to = SafeText(GetStr(root, "ChosenAgent"), 64) ?? "unknown";
                    var reason = SafeText(NonEmptyOrNull(GetStr(root, "Reason")), 256);
                    var summary = $"Durable resume rerouted {from} → {to}" +
                        (reason is null ? " (reason not recorded)" : $": {reason}");
                    var details = new { rejectedAgent = from, chosenAgent = to, reason };
                    return new TimelineEntry(time, "recovery", summary, details);
                }
            case "agent.pause_dispatch_deferred":
                {
                    var retryFrom = SafeText(GetStr(root, "RetryFrom"), 64);
                    var reason = SafeText(NonEmptyOrNull(GetStr(root, "Reason")), 256);
                    var summary = "Dispatch deferred: agent paused" +
                        (retryFrom is null ? "" : $" (resumes from {retryFrom})") +
                        (reason is null ? " — no recorded cause; not inferred" : $": {reason}");
                    var details = new { retryFrom, reason };
                    return new TimelineEntry(time, "deferral", summary, details);
                }
            case "agent.pause_waiting_item_resumed":
                {
                    var source = SafeText(GetStr(root, "Source"), 64);
                    var retryFrom = SafeText(GetStr(root, "RetryFrom"), 64);
                    var summary = "Re-enqueued after agent pause change" +
                        (source is null ? "" : $" (source: {source})") +
                        (retryFrom is null ? "" : $" from {retryFrom}");
                    var details = new { source, retryFrom };
                    return new TimelineEntry(time, "recovery", summary, details);
                }
            case "budget.deferred":
            case "refactor.exclusivity_deferred":
                {
                    var project = SafeText(GetStr(root, "ProjectId"), 64);
                    var reason = SafeText(NonEmptyOrNull(GetStr(root, "Reason")), 256);
                    var gate = eventName == "budget.deferred" ? "Budget cap" : "Refactor exclusivity";
                    var summary = $"{gate} deferred" +
                        (reason is null ? " — no recorded cause; not inferred" : $": {reason}");
                    var details = new { projectId = project, reason };
                    return new TimelineEntry(time, "deferral", summary, details);
                }
            case "disk.deferred":
                {
                    var mount = SafeText(GetStr(root, "MountPath"), 128) ?? "unknown path";
                    var free = GetLongNullable(root, "FreeBytes");
                    var threshold = GetLongNullable(root, "ThresholdBytes");
                    var summary = free.HasValue && threshold.HasValue
                        ? $"Deferred: only {free} bytes free on {mount} (threshold {threshold})"
                        : $"Deferred: low disk on {mount} (sizes not recorded)";
                    var details = new { mountPath = mount, freeBytes = free, thresholdBytes = threshold };
                    return new TimelineEntry(time, "deferral", summary, details);
                }
            case "quota_router.deferred":
                {
                    var recheck = GetLongNullable(root, "RecheckMs");
                    var summary = recheck.HasValue
                        ? $"Quota router deferred: re-enqueue in {recheck}ms"
                        : "Quota router deferred (recheck not recorded)";
                    var details = new { recheckMs = recheck };
                    return new TimelineEntry(time, "deferral", summary, details);
                }
            case "quota_router.waiting":
                {
                    var classId = SafeText(GetStr(root, "ClassId"), 64) ?? "unknown class";
                    var recheck = GetLongNullable(root, "RecheckMs");
                    var summary = recheck.HasValue
                        ? $"Waiting on quota for class '{classId}'; recheck in {recheck}ms"
                        : $"Waiting on quota for class '{classId}' (recheck not recorded)";
                    var details = new { classId, recheckMs = recheck };
                    return new TimelineEntry(time, "deferral", summary, details);
                }
            case "quota_router.all_exhausted":
                {
                    var classId = SafeText(GetStr(root, "ClassId"), 64) ?? "unknown class";
                    var phase = SafeText(GetStr(root, "Phase"), 64);
                    var members = GetIntNullable(root, "MemberCount");
                    var summary = $"All members of class '{classId}' exhausted" +
                        (phase is null ? "" : $" in {phase}") +
                        (members.HasValue ? $" ({members} members)" : "") +
                        "; parked for quota reset";
                    var details = new { classId, phase, memberCount = members };
                    return new TimelineEntry(time, "deferral", summary, details);
                }
            case "provider_transient_parked":
                {
                    var agent = SafeText(GetStr(root, "Agent"), 64) ?? "unknown agent";
                    var kind = SafeText(GetStr(root, "Kind"), 64);
                    var signature = SafeText(GetStr(root, "Signature"), 128);
                    var phase = SafeText(GetStr(root, "Phase"), 64);
                    var model = SafeText(GetStr(root, "Model"), 128);
                    var summary = $"Provider-transient retry parked ({agent})" +
                        (kind is null ? "" : $": {kind}") +
                        (signature is null ? " (signature not recorded)" : $" [{signature}]") +
                        (string.IsNullOrEmpty(phase) || phase == "(unknown)" ? "" : $" in {phase}");
                    var details = new { agent, model, kind, signature, phase };
                    return new TimelineEntry(time, "deferral", summary, details);
                }
            case "agent.attempt_timeout_fallback":
            case "agent.resume_exhausted_fallback":
            case "quota_router.agent_fallback":
                {
                    var phase = SafeText(GetStr(root, "Phase"), 64) ?? "unknown phase";
                    var iteration = GetIntNullable(root, "Iteration");
                    var fromAgent = SafeText(GetStr(root, "FromAgent"), 64) ?? "unknown";
                    var fromModel = SafeText(GetStr(root, "FromModel"), 128);
                    var toAgent = SafeText(GetStr(root, "ToAgent"), 64) ?? "unknown";
                    var toModel = SafeText(GetStr(root, "ToModel"), 128);
                    var reason = SafeText(NonEmptyOrNull(GetStr(root, "Reason")), 256);
                    var label = eventName == "agent.attempt_timeout_fallback" ? "Attempt-timeout fallback"
                        : eventName == "agent.resume_exhausted_fallback" ? "Resume-exhausted fallback"
                        : "Quota fallback";
                    var summary = $"{label} in {phase}: {fromAgent} → {toAgent}" +
                        (iteration.HasValue ? $" (iteration {iteration})" : "") +
                        (reason is null ? " (reason not recorded)" : $": {reason}");
                    var details = new { phase, iteration, fromAgent, fromModel, toAgent, toModel, reason };
                    return new TimelineEntry(time, "recovery", summary, details);
                }
            case "agent.restore_requeue_item":
                {
                    var agent = SafeText(GetStr(root, "Agent"), 64) ?? "unknown agent";
                    var failureKind = SafeText(GetStr(root, "FailureKind"), 128);
                    var from = SafeText(GetStr(root, "From"), 64);
                    var summary = $"Re-enqueued after {agent} restore" +
                        (from is null ? "" : $" from {from}") +
                        (string.IsNullOrEmpty(failureKind) || failureKind == "(null)" ? " (prior failure kind not recorded)" : $": {failureKind}");
                    var details = new { agent, failureKind, from };
                    return new TimelineEntry(time, "recovery", summary, details);
                }
            case "transient_retry_attempted":
            case "quota_retry_attempted":
                {
                    var source = SafeText(GetStr(root, "Source"), 64) ?? "unknown source";
                    var outcome = SafeText(GetStr(root, "Outcome"), 64) ?? "unknown outcome";
                    var state = SafeText(GetStr(root, "State"), 64);
                    var reason = SafeText(NonEmptyOrNull(GetStr(root, "Reason")), 256);
                    var summary = $"Retry attempted ({source}): {outcome}" +
                        (state is null ? "" : $" in {state}") +
                        (reason is null ? " (reason not recorded)" : $": {reason}");
                    var details = new { source, outcome, state, reason };
                    return new TimelineEntry(time, "recovery", summary, details);
                }
            case "work_item.failed":
                {
                    var error = SafeText(GetStr(root, "Error"), 500);
                    var summary = $"Failed: {SafeText(GetStr(root, "Error"), 120) ?? "(no details)"}";
                    var details = new { from = prevState, to = "Failed", error };
                    prevState = "Failed";
                    return new TimelineEntry(time, "state_transition", summary, details);
                }
            case "work_item.retried":
                {
                    var from = SafeText(GetStr(root, "From"), 64) ?? "work";
                    var details = new { phase = from };
                    return new TimelineEntry(time, "state_transition", $"Retried from {from}", details);
                }
            case "work_item.resumed":
                {
                    var from = SafeText(GetStr(root, "From"), 64) ?? "work";
                    var reason = GetStr(root, "Reason");
                    // Reason is round-tripped through the empty-string sentinel
                    // because Serilog drops null properties; see
                    // AuditLog.WorkItemResumed at src/CodeyBox.Core/AuditLog.cs.
                    if (string.IsNullOrEmpty(reason)) reason = null;
                    reason = SafeText(reason, 256);
                    var details = new { phase = from, reason };
                    var summary = reason is null ? $"Resumed from {from}" : $"Resumed from {from}: {SafeText(reason, 80)}";
                    // Advance prevState so the next state_transition shows
                    // "Queued → Working" (etc.) instead of stale "Cancelled → ...".
                    prevState = from switch
                    {
                        "audit" => "WorkComplete",
                        "merge" => "AuditPassed",
                        _ => "Queued",
                    };
                    return new TimelineEntry(time, "state_transition", summary, details);
                }
            case "work_item.dependent_cancelled":
                {
                    var parentId = SafeText(GetStr(root, "ParentWorkItemId"), 64) ?? "?";
                    var shortParent = parentId.Length >= 8 ? parentId[..8] : parentId;
                    var summary = $"Cascade-cancelled (parent: {shortParent}…)";
                    var details = new { from = prevState, to = "Cancelled", parentWorkItemId = parentId };
                    prevState = "Cancelled";
                    return new TimelineEntry(time, "state_transition", summary, details);
                }
            case "agent.started":
                {
                    var agent = SafeText(GetStr(root, "Agent"), 64) ?? "?";
                    var phase = SafeText(GetStr(root, "Phase"), 64) ?? "?";
                    var sandbox = SafeText(GetStr(root, "Sandbox"), 128);
                    var details = new { agent, phase, sandbox };
                    return new TimelineEntry(time, "agent_started", $"{agent} ({phase}) started", details);
                }
            case "agent.finished":
                {
                    // Raw agent-process exit, not a phase verdict: the pipeline
                    // may still fail the phase afterwards (infra check,
                    // empty diff, stuck probe). The summary says so explicitly
                    // so a green agent exit is never mistaken for phase success.
                    var agent = SafeText(GetStr(root, "Agent"), 64) ?? "?";
                    var success = GetBool(root, "Success");
                    var durationMs = GetLong(root, "DurationMs");
                    var exitCode = GetIntNullable(root, "ExitCode");
                    var stdoutTail = SafeText(GetStr(root, "StdoutTail"), 500);
                    var stderrTail = SafeText(GetStr(root, "StderrTail"), 500);
                    var sandbox = SafeText(GetStr(root, "Sandbox"), 128);
                    var outcome = success ? "succeeded" : "failed";
                    var summary = $"{agent} agent run {outcome} in {FormatDuration(durationMs)} (raw agent exit — not a phase verdict)";
                    var details = new { agent, success, exitCode, durationMs, stdoutTail, stderrTail, sandbox };
                    return new TimelineEntry(time, "agent_finished", summary, details);
                }
            case "agent.stuck_detected":
                {
                    var agent = SafeText(GetStr(root, "Agent"), 64) ?? "?";
                    var phase = SafeText(GetStr(root, "Phase"), 64) ?? "?";
                    var stuck = GetInt(root, "StuckSeconds");
                    var details = new { agent, phase, stuckSeconds = stuck };
                    return new TimelineEntry(time, "agent_stuck",
                        $"{agent} ({phase}) stuck — no activity for {stuck}s", details);
                }
            case "agent.killed_by_stuck_probe":
                {
                    var agent = SafeText(GetStr(root, "Agent"), 64) ?? "?";
                    var phase = SafeText(GetStr(root, "Phase"), 64) ?? "?";
                    var details = new { agent, phase, killedByStuckProbe = true };
                    return new TimelineEntry(time, "agent_finished",
                        $"{agent} ({phase}) killed by stuck probe (raw agent exit — not a phase verdict)", details);
                }
            case "auditor.run":
                {
                    var name = SafeText(GetStr(root, "AuditorName"), 128) ?? "?";
                    var severity = SafeText(GetStr(root, "WorstSeverity"), 32) ?? "None";
                    var durationMs = GetLong(root, "DurationMs");
                    var findings = string.Equals(severity, "None", StringComparison.OrdinalIgnoreCase)
                        ? "0 findings" : $"{severity} findings";
                    var summary = $"{name} (iter {pendingIteration}) — {findings}";
                    var details = new { name, iteration = pendingIteration, severity, durationMs };
                    return new TimelineEntry(time, "auditor_run", summary, details);
                }
            case "audit.iteration_complete":
                {
                    var iter = GetInt(root, "Iteration");
                    var maxIter = GetInt(root, "MaxIterations");
                    var blocking = GetInt(root, "BlockingCount");
                    var nonBlocking = GetInt(root, "NonBlockingCount");
                    var summary = $"Audit iteration {iter} of {maxIter}: {blocking} blocking, {nonBlocking} non-blocking";
                    var details = new { iteration = iter, totalIterations = maxIter, blocking, nonBlocking };
                    return new TimelineEntry(time, "iteration_complete", summary, details);
                }
            case "audit.passed":
                {
                    var iter = GetInt(root, "Iteration");
                    var details = new { iteration = iter, passed = true };
                    return new TimelineEntry(time, "iteration_complete",
                        $"Audit passed on iteration {iter}", details);
                }
            case "audit.failed":
                {
                    var iter = GetInt(root, "Iteration");
                    var blocking = GetInt(root, "BlockingCount");
                    var details = new { iteration = iter, blocking, passed = false };
                    return new TimelineEntry(time, "iteration_complete",
                        $"Audit failed after {iter} iterations: {blocking} blocking findings", details);
                }
            case "webhook.delivered":
                {
                    var endpoint = SafeText(GetStr(root, "Endpoint"), 256) ?? "?";
                    var ev = SafeText(GetStr(root, "WebhookEvent"), 64) ?? "?";
                    var status = GetInt(root, "StatusCode");
                    var attempt = GetInt(root, "Attempt");
                    var details = new { endpoint, @event = ev, success = true, statusCode = status, attempt };
                    return new TimelineEntry(time, "webhook_delivered",
                        $"Webhook {ev} → {endpoint}: HTTP {status}", details);
                }
            case "webhook.delivery_failed":
                {
                    var endpoint = SafeText(GetStr(root, "Endpoint"), 256) ?? "?";
                    var ev = SafeText(GetStr(root, "WebhookEvent"), 64) ?? "?";
                    var attempts = GetInt(root, "Attempts");
                    var failure = SafeText(GetStr(root, "LastFailure"), 256) ?? "";
                    var details = new { endpoint, @event = ev, success = false, attempts, lastFailure = failure };
                    return new TimelineEntry(time, "webhook_delivered",
                        $"Webhook {ev} → {endpoint}: failed after {attempts} attempts", details);
                }
            default:
                return null;
        }
    }

    private static (string? From, string? To, string Summary) RecoveryCore(
        JsonElement root, string label, string? worker, int? attempt)
    {
        var from = SafeText(GetStr(root, "FromState"), 64);
        var to = SafeText(GetStr(root, "ToState"), 64);
        string summary;
        if (from is null || to is null)
        {
            summary = $"{label}: prior/target state not recorded" +
                (worker is null ? "" : $" (worker {worker})") +
                " — cause and outcome not inferred";
        }
        else if (string.Equals(from, to, StringComparison.Ordinal))
        {
            summary = $"{label} in place in {to} (not a phase transition)" +
                (worker is null ? "" : $" (worker {worker})") +
                (attempt.HasValue ? $" (attempt {attempt})" : "");
        }
        else
        {
            summary = $"{label}: {from} → {to}" +
                (worker is null ? "" : $" (worker {worker})") +
                (attempt.HasValue ? $" (attempt {attempt})" : "");
        }
        return (from, to, summary);
    }

    private static TimelineEntry AdvanceRecovery(
        DateTimeOffset time, ref string? prevState, string? to, string summary, object details)
    {
        if (to is not null)
            prevState = to;
        return new TimelineEntry(time, "recovery", summary, details);
    }

    // ── CLEF property helpers ──────────────────────────────────────────────────

    private static string? GetStr(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static bool GetBool(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.True;

    private static bool? GetBoolNullable(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.ValueKind == JsonValueKind.True : null;

    private static int GetInt(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.TryGetInt32(out var i) ? i : 0;

    private static int? GetIntNullable(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind != JsonValueKind.Null
            && v.TryGetInt32(out var i) ? i : null;

    private static long GetLong(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.TryGetInt64(out var l) ? l : 0;

    private static long? GetLongNullable(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind != JsonValueKind.Null
            && v.TryGetInt64(out var l) ? l : null;

    // Worker identity is an int pool slot in work_item.picked_up but an
    // opaque string (registry id, sometimes "<no-live-worker>") in the
    // watchdog / dead-worker / stale-detector events. Accept either shape so
    // older and newer records both render instead of dropping to "0".
    private static string GetWorkerLabel(JsonElement el)
    {
        if (el.TryGetProperty("WorkerId", out var v))
        {
            if (v.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(v.GetString()))
                return SafeText(v.GetString(), 64)!;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i))
                return i.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        return "unknown";
    }

    private static string? GetWorkerLabelOrNull(JsonElement el)
    {
        var label = GetWorkerLabel(el);
        return label == "unknown" ? null : label;
    }

    private static string? NonEmptyOrNull(string? s) =>
        string.IsNullOrEmpty(s) ? null : s;

    // Sink-side guard for every untrusted string the timeline renders or
    // returns: control characters (including terminal escapes) become spaces
    // so a hostile log line cannot inject escapes into a consumer's terminal,
    // newlines are flattened, and the value is capped at max characters.
    // AuditLog already truncates at emission; this cap is defense in depth at
    // the sink for hand-written or legacy log lines. Never returns secrets —
    // callers only pass display fields (states, reasons, tails), never tokens.
    private static string? SafeText(string? s, int max)
    {
        if (s is null)
            return null;
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s)
            sb.Append(char.IsControl(c) ? ' ' : c);
        var cleaned = sb.ToString().Trim();
        if (cleaned.Length == 0)
            return null;
        return cleaned.Length <= max ? cleaned : cleaned[..max] + "…";
    }

    private static string FormatDuration(long ms) =>
        ms < 1_000 ? $"{ms}ms" :
        ms < 60_000 ? $"{ms / 1000}s" :
        $"{ms / 60_000}m {(ms % 60_000) / 1000}s";
}

/// <summary>A single event in a work item's audit timeline.</summary>
public sealed record TimelineEntry(
    DateTimeOffset OccurredAt,
    string Kind,
    string Summary,
    object Details);
