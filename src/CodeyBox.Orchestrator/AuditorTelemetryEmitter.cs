using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using CodeyBox.Agents;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Cold-tier extraction of PipelineRunner's auditor / tool-call telemetry cluster.
/// Behavior-preserving move from <see cref="PipelineRunner"/> — owns the auditor
/// sub-step parsers (build/format/test/gitleaks/semgrep) and agent tool-call /
/// thinking-aggregate timing emission. The pipeline spine holds one instance
/// and delegates at the work / audit / merge seams.
/// </summary>
internal sealed class AuditorTelemetryEmitter
{
    private readonly ITimingStore? _timings;
    private readonly IReadOnlyDictionary<AgentKind, IAgentToolCallCounter>? _toolCallCounters;
    private readonly ILogger _log;

    internal AuditorTelemetryEmitter(
        ITimingStore? timings,
        IReadOnlyDictionary<AgentKind, IAgentToolCallCounter>? toolCallCounters,
        ILogger log)
    {
        _timings = timings;
        _toolCallCounters = toolCallCounters;
        _log = log;
    }

    /// <summary>
    /// Parses well-known output patterns from shell auditors and emits sub-step timing rows.
    /// Best-effort: unknown auditors or unparsable output are silently skipped.
    /// </summary>
    internal async Task EmitAuditorSubStepsAsync(
        string auditorName, string? stdout, WorkItemId itemId, int iteration, DateTimeOffset phaseStart)
    {
        if (_timings is null || stdout is null) return;

        var subSteps = ParseAuditorSubSteps(auditorName, stdout);
        foreach (var (step, durMs, metaJson) in subSteps)
        {
            var id = Guid.NewGuid().ToString("N");
            try
            {
                await _timings.BeginAsync(new TimingRecord
                {
                    Id = id,
                    WorkItemId = itemId,
                    Phase = "audit",
                    Iteration = iteration,
                    Step = step,
                    StartedAt = phaseStart,
                    MetadataJson = metaJson,
                }, CancellationToken.None);
                await _timings.EndAsync(id, phaseStart.AddMilliseconds(durMs), durMs, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Timing: failed to emit auditor sub-step {Step}", step);
            }
        }
    }

    internal static List<(string Step, long DurationMs, string MetadataJson)> ParseAuditorSubSteps(string auditorName, string stdout)
    {
        var result = new List<(string, long, string)>();
        var auditStepPrefix = AuditTimingPrefix(auditorName);

        // Build tools such as dotnet emit: "Time Elapsed 00:00:01.234"
        if (auditorName.Contains("build", StringComparison.OrdinalIgnoreCase))
        {
            var m = Regex.Match(stdout, @"Time Elapsed (\d+):(\d+):(\d+)\.(\d+)");
            if (m.Success &&
                int.TryParse(m.Groups[1].Value, out var h) &&
                int.TryParse(m.Groups[2].Value, out var min) &&
                int.TryParse(m.Groups[3].Value, out var sec) &&
                int.TryParse(m.Groups[4].Value.PadRight(3, '0')[..3], out var ms))
            {
                result.Add(($"{auditStepPrefix}.build", (long)((h * 3600 + min * 60 + sec) * 1000 + ms), "{}"));
            }
        }

        // Format tools may emit the same "Time Elapsed" marker as build tools.
        else if (auditorName.Contains("format", StringComparison.OrdinalIgnoreCase))
        {
            var m = Regex.Match(stdout, @"Time Elapsed (\d+):(\d+):(\d+)\.(\d+)");
            if (m.Success &&
                int.TryParse(m.Groups[1].Value, out var h) &&
                int.TryParse(m.Groups[2].Value, out var min) &&
                int.TryParse(m.Groups[3].Value, out var sec) &&
                int.TryParse(m.Groups[4].Value.PadRight(3, '0')[..3], out var ms))
            {
                result.Add(($"{auditStepPrefix}.format", (long)((h * 3600 + min * 60 + sec) * 1000 + ms), "{}"));
            }
        }

        // Test tools: "Time Elapsed" for total run; "A total of N test files matched" for discovery count;
        // "Duration: X s" in Passed!/Failed! line for execution time.
        else if (auditorName.Contains("test", StringComparison.OrdinalIgnoreCase))
        {
            // Test discovery: count of matched test files (no distinct duration available)
            var discoveryMatch = Regex.Match(stdout, @"A total of (\d+) test files? matched");
            if (discoveryMatch.Success && int.TryParse(discoveryMatch.Groups[1].Value, out var fileCount))
            {
                // duration not separately measurable; count stored in metadata
                result.Add(($"{auditStepPrefix}.test_discovery", 0, $"{{\"count\":{fileCount}}}"));
            }

            // Test run duration from "Duration: X s" in Passed!/Failed! line
            var durationMatch = Regex.Match(stdout, @"Duration:\s*([\d.]+)\s*s", RegexOptions.IgnoreCase);
            if (durationMatch.Success && double.TryParse(durationMatch.Groups[1].Value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var runSecs))
            {
                result.Add(($"{auditStepPrefix}.test_run", (long)(runSecs * 1000), "{}"));
            }
            else
            {
                // Fallback: "Time Elapsed" covers the full test invocation
                var m = Regex.Match(stdout, @"Time Elapsed (\d+):(\d+):(\d+)\.(\d+)");
                if (m.Success &&
                    int.TryParse(m.Groups[1].Value, out var h) &&
                    int.TryParse(m.Groups[2].Value, out var min) &&
                    int.TryParse(m.Groups[3].Value, out var sec) &&
                    int.TryParse(m.Groups[4].Value.PadRight(3, '0')[..3], out var ms))
                {
                    result.Add(($"{auditStepPrefix}.test_run", (long)((h * 3600 + min * 60 + sec) * 1000 + ms), "{}"));
                }
            }
        }

        // gitleaks: "scan completed in 1.234s"
        if (auditorName.Contains("gitleaks", StringComparison.OrdinalIgnoreCase))
        {
            var m = Regex.Match(stdout, @"scan completed in ([\d.]+)s", RegexOptions.IgnoreCase);
            if (m.Success && double.TryParse(m.Groups[1].Value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var secs))
            {
                result.Add(("gitleaks.scan", (long)(secs * 1000), "{}"));
            }
        }

        // semgrep: JSON output with "duration" field in seconds
        if (auditorName.Contains("semgrep", StringComparison.OrdinalIgnoreCase))
        {
            var m = Regex.Match(stdout, @"""duration""\s*:\s*([\d.]+)");
            if (m.Success && double.TryParse(m.Groups[1].Value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var secs))
            {
                result.Add(("semgrep.scan", (long)(secs * 1000), "{}"));
            }
        }

        return result;
    }

    internal static string AuditTimingPrefix(string auditorName)
    {
        var separator = auditorName.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0)
            return "audit";

        var prefix = auditorName[..separator].ToLowerInvariant();
        return Regex.IsMatch(prefix, "^[a-z0-9_-]+$", RegexOptions.CultureInvariant)
            ? prefix
            : "audit";
    }

    internal async Task EmitToolCallCountsAsync(
        AgentKind agentKind,
        string? stdout, WorkItemId itemId, string phase, long agentExecDurationMs, CancellationToken ct,
        int? iteration = null)
    {
        if (_timings is null) return;
        if (_toolCallCounters is null) return;
        if (!_toolCallCounters.TryGetValue(agentKind, out var counter)) return;

        var parsed = counter.TryCount(stdout);
        if (parsed is null) return; // Not recognisable stream-json output; skip silently.

        // Compute the approximate window the agent exec occupied.
        // now ≈ agent exec end; startedAt ≈ agent exec start.
        // This ensures EndedAt - StartedAt == agentExecDurationMs for thinking_aggregate.
        var endedAt = DateTimeOffset.UtcNow;
        var startedAt = endedAt.AddMilliseconds(-agentExecDurationMs);

        // Emit one agent.tool_call.<name> row per distinct tool.
        // Per-event timestamps are unavailable in buffered stream-json output, so
        // duration_ms = 0. The invocation count is stored in metadata_json.
        foreach (var (toolName, count) in parsed.ToolCallCounts)
        {
            // Sanitize agent-controlled name: cap length, allow only safe chars.
            var safeToolName = SanitizeToolName(toolName);
            var rowId = Guid.NewGuid().ToString("N");
            var metaJson = JsonSerializer.Serialize(new Dictionary<string, object> { ["count"] = count });
            try
            {
                await _timings.BeginAsync(new TimingRecord
                {
                    Id = rowId,
                    WorkItemId = itemId,
                    Phase = phase,
                    Iteration = iteration,
                    Step = $"agent.tool_call.{safeToolName}",
                    StartedAt = startedAt,
                    MetadataJson = metaJson,
                }, CancellationToken.None);
                await _timings.EndAsync(rowId, startedAt, 0, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Timing: failed to emit agent.tool_call.{Tool}",
                    safeToolName.Replace("\n", "\\n", StringComparison.Ordinal)
                               .Replace("\r", "\\r", StringComparison.Ordinal));
            }
        }

        // Emit agent.thinking_aggregate as exec duration minus sum of tool call durations.
        // Without per-event timestamps all tool call durations are 0, so thinking_aggregate
        // equals the full agent.exec duration. IsSubStep excludes it from phase totals.
        // StartedAt/EndedAt span the actual execution window so SQL duration math is consistent.
        var thinkId = Guid.NewGuid().ToString("N");
        try
        {
            await _timings.BeginAsync(new TimingRecord
            {
                Id = thinkId,
                WorkItemId = itemId,
                Phase = phase,
                Iteration = iteration,
                Step = "agent.thinking_aggregate",
                StartedAt = startedAt,
                MetadataJson = "{}",
            }, CancellationToken.None);
            await _timings.EndAsync(thinkId, endedAt, agentExecDurationMs, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Timing: failed to emit agent.thinking_aggregate");
        }
    }

    internal static string SanitizeToolName(string name)
    {
        const int maxLen = 256;
        var s = name.Length > maxLen ? name[..maxLen] : name;
        return string.IsNullOrEmpty(s)
            ? "unknown"
            : new string(s.Select(c => char.IsLetterOrDigit(c) || c == '.' || c == '_' || c == '-' ? c : '_').ToArray());
    }
}
