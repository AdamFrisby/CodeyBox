using System.Text.Json;

namespace CodeyBox.Admin.Web.Models;

/// <summary>
/// Local copy of the orchestrator's timeline response shape.
/// Intentionally separate from CodeyBox.Core — coupling is REST + JSON only.
/// </summary>
public sealed class WorkItemTimelineDto
{
    public string WorkItemId { get; set; } = "";
    public List<TimelineEntryDto> Entries { get; set; } = [];
}

public sealed class TimelineEntryDto
{
    public DateTimeOffset OccurredAt { get; set; }
    public string Kind { get; set; } = "";
    public string Summary { get; set; } = "";
    public JsonElement Details { get; set; }

    /// <summary>Human-readable relative offset from the first entry's timestamp.</summary>
    public string RelativeTime(DateTimeOffset start)
    {
        var elapsed = OccurredAt - start;
        if (elapsed.TotalSeconds < 0) return "+0s";
        if (elapsed.TotalSeconds < 60) return $"+{(int)elapsed.TotalSeconds}s";
        if (elapsed.TotalMinutes < 60)
            return $"+{(int)elapsed.TotalMinutes}m {(int)elapsed.TotalSeconds % 60}s";
        return $"+{(int)elapsed.TotalHours}h {(int)elapsed.TotalMinutes % 60}m";
    }

    /// <summary>CSS modifier class for color-coding by kind/success.</summary>
    public string CssModifier
    {
        get
        {
            if (Kind == "state_transition") return "state";
            if (Kind == "agent_started") return "agent";
            if (Kind == "agent_finished")
            {
                if (Details.ValueKind == JsonValueKind.Object &&
                    Details.TryGetProperty("success", out var s))
                    return s.ValueKind == JsonValueKind.True ? "success" : "failure";
                return Summary.Contains("failed", StringComparison.OrdinalIgnoreCase) ? "failure" : "success";
            }
            if (Kind == "auditor_run")
            {
                if (Details.ValueKind == JsonValueKind.Object &&
                    Details.TryGetProperty("severity", out var sev))
                {
                    var sv = sev.GetString() ?? "None";
                    if (sv is "Error" or "Fatal") return "failure";
                    if (sv is "Warning") return "warn";
                }
                return "muted";
            }
            if (Kind == "iteration_complete") return "iter";
            if (Kind == "webhook_delivered")
            {
                if (Details.ValueKind == JsonValueKind.Object &&
                    Details.TryGetProperty("success", out var ws))
                    return ws.ValueKind == JsonValueKind.True ? "success" : "failure";
            }
            return "";
        }
    }

    /// <summary>Iteration number for grouping, extracted from details (auditor_run/iteration_complete).</summary>
    public int? IterationNumber
    {
        get
        {
            if (Kind is not ("auditor_run" or "iteration_complete")) return null;
            if (Details.ValueKind == JsonValueKind.Object &&
                Details.TryGetProperty("iteration", out var p) &&
                p.TryGetInt32(out var i))
                return i;
            return null;
        }
    }
}
