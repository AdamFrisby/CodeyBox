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
        var parsed = new List<(DateTimeOffset Time, string EventName, JsonElement Root)>();

        foreach (var file in files)
        {
            await foreach (var ev in ReadFileAsync(file, workItemId, ct))
                parsed.Add(ev);
        }

        parsed.Sort((a, b) => DateTimeOffset.Compare(a.Time, b.Time));
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
        List<(DateTimeOffset Time, string EventName, JsonElement Root)> events)
    {
        var result = new List<TimelineEntry>(events.Count);
        string? prevState = null;
        int pendingIteration = 1;

        foreach (var (time, eventName, root) in events)
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
                    var title = GetStr(root, "Title");
                    var summary = title is not null ? $"Created (Queued): {Truncate(title, 80)}" : "Created (Queued)";
                    var details = new { from = prevState, to = "Queued", title };
                    prevState = "Queued";
                    return new TimelineEntry(time, "state_transition", summary, details);
                }
            case "work_item.transitioned":
                {
                    var to = GetStr(root, "State") ?? "Unknown";
                    var summary = prevState is not null ? $"{prevState} → {to}" : $"→ {to}";
                    var details = new { from = prevState, to };
                    prevState = to;
                    return new TimelineEntry(time, "state_transition", summary, details);
                }
            case "work_item.picked_up":
                {
                    var worker = GetInt(root, "WorkerId");
                    var to = "Working";
                    var summary = prevState is not null
                        ? $"{prevState} → {to} (worker {worker})"
                        : $"→ {to} (worker {worker})";
                    var details = new { from = prevState, to, workerId = worker };
                    prevState = to;
                    return new TimelineEntry(time, "state_transition", summary, details);
                }
            case "work_item.cancelled":
                {
                    var summary = prevState is not null ? $"{prevState} → Cancelled" : "→ Cancelled";
                    var details = new { from = prevState, to = "Cancelled" };
                    prevState = "Cancelled";
                    return new TimelineEntry(time, "state_transition", summary, details);
                }
            case "work_item.failed":
                {
                    var error = GetStr(root, "Error");
                    var summary = $"Failed: {Truncate(error ?? "(no details)", 120)}";
                    var details = new { from = prevState, to = "Failed", error };
                    prevState = "Failed";
                    return new TimelineEntry(time, "state_transition", summary, details);
                }
            case "work_item.retried":
                {
                    var from = GetStr(root, "From") ?? "work";
                    var details = new { phase = from };
                    return new TimelineEntry(time, "state_transition", $"Retried from {from}", details);
                }
            case "work_item.resumed":
                {
                    var from = GetStr(root, "From") ?? "work";
                    var reason = GetStr(root, "Reason");
                    if (string.IsNullOrEmpty(reason)) reason = null;
                    var details = new { phase = from, reason };
                    var summary = reason is null ? $"Resumed from {from}" : $"Resumed from {from}: {Truncate(reason, 80)}";
                    return new TimelineEntry(time, "state_transition", summary, details);
                }
            case "work_item.dependent_cancelled":
                {
                    var parentId = GetStr(root, "ParentWorkItemId") ?? "?";
                    var shortParent = parentId.Length >= 8 ? parentId[..8] : parentId;
                    var summary = $"Cascade-cancelled (parent: {shortParent}…)";
                    var details = new { from = prevState, to = "Cancelled", parentWorkItemId = parentId };
                    prevState = "Cancelled";
                    return new TimelineEntry(time, "state_transition", summary, details);
                }
            case "agent.started":
                {
                    var agent = GetStr(root, "Agent") ?? "?";
                    var phase = GetStr(root, "Phase") ?? "?";
                    var sandbox = GetStr(root, "Sandbox");
                    var details = new { agent, phase, sandbox };
                    return new TimelineEntry(time, "agent_started", $"{agent} ({phase}) started", details);
                }
            case "agent.finished":
                {
                    var agent = GetStr(root, "Agent") ?? "?";
                    var success = GetBool(root, "Success");
                    var durationMs = GetLong(root, "DurationMs");
                    var exitCode = GetIntNullable(root, "ExitCode");
                    var stdoutTail = GetStr(root, "StdoutTail");
                    var stderrTail = GetStr(root, "StderrTail");
                    var sandbox = GetStr(root, "Sandbox");
                    var outcome = success ? "succeeded" : "failed";
                    var summary = $"{agent} {outcome} in {FormatDuration(durationMs)}";
                    var details = new { agent, success, exitCode, durationMs, stdoutTail, stderrTail, sandbox };
                    return new TimelineEntry(time, "agent_finished", summary, details);
                }
            case "agent.stuck_detected":
                {
                    var agent = GetStr(root, "Agent") ?? "?";
                    var phase = GetStr(root, "Phase") ?? "?";
                    var stuck = GetInt(root, "StuckSeconds");
                    var details = new { agent, phase, stuckSeconds = stuck };
                    return new TimelineEntry(time, "agent_stuck",
                        $"{agent} ({phase}) stuck — no activity for {stuck}s", details);
                }
            case "agent.killed_by_stuck_probe":
                {
                    var agent = GetStr(root, "Agent") ?? "?";
                    var phase = GetStr(root, "Phase") ?? "?";
                    var details = new { agent, phase, killedByStuckProbe = true };
                    return new TimelineEntry(time, "agent_finished",
                        $"{agent} ({phase}) killed by stuck probe", details);
                }
            case "auditor.run":
                {
                    var name = GetStr(root, "AuditorName") ?? "?";
                    var severity = GetStr(root, "WorstSeverity") ?? "None";
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
                    var endpoint = GetStr(root, "Endpoint") ?? "?";
                    var ev = GetStr(root, "WebhookEvent") ?? "?";
                    var status = GetInt(root, "StatusCode");
                    var attempt = GetInt(root, "Attempt");
                    var details = new { endpoint, @event = ev, success = true, statusCode = status, attempt };
                    return new TimelineEntry(time, "webhook_delivered",
                        $"Webhook {ev} → {endpoint}: HTTP {status}", details);
                }
            case "webhook.delivery_failed":
                {
                    var endpoint = GetStr(root, "Endpoint") ?? "?";
                    var ev = GetStr(root, "WebhookEvent") ?? "?";
                    var attempts = GetInt(root, "Attempts");
                    var failure = GetStr(root, "LastFailure") ?? "";
                    var details = new { endpoint, @event = ev, success = false, attempts, lastFailure = failure };
                    return new TimelineEntry(time, "webhook_delivered",
                        $"Webhook {ev} → {endpoint}: failed after {attempts} attempts", details);
                }
            default:
                return null;
        }
    }

    // ── CLEF property helpers ──────────────────────────────────────────────────

    private static string? GetStr(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static bool GetBool(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.True;

    private static int GetInt(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.TryGetInt32(out var i) ? i : 0;

    private static int? GetIntNullable(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind != JsonValueKind.Null
            && v.TryGetInt32(out var i) ? i : null;

    private static long GetLong(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.TryGetInt64(out var l) ? l : 0;

    private static string FormatDuration(long ms) =>
        ms < 1_000 ? $"{ms}ms" :
        ms < 60_000 ? $"{ms / 1000}s" :
        $"{ms / 60_000}m {(ms % 60_000) / 1000}s";

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}

/// <summary>A single event in a work item's audit timeline.</summary>
public sealed record TimelineEntry(
    DateTimeOffset OccurredAt,
    string Kind,
    string Summary,
    object Details);
