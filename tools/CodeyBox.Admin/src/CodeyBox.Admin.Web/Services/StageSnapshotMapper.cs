using System.Text.Json;
using CodeyBox.Admin.Model;
using CodeyBox.Admin.Web.Models;

namespace CodeyBox.Admin.Web.Services;

/// <summary>
/// Maps the per-item REST surfaces onto the pure stage-pipeline input at the
/// edge. A null timeline or audit-progress response stays null — the model
/// renders "unavailable" rather than assuming an empty history.
/// </summary>
public static class StageSnapshotMapper
{
    public static StageSnapshot ToStageSnapshot(
        WorkItemDto item,
        WorkItemTimelineDto? timeline,
        AuditProgressListDto? progress)
    {
        ArgumentNullException.ThrowIfNull(item);
        List<StageTransition>? transitions = null;
        if (timeline?.Entries is not null)
        {
            transitions = [];
            foreach (var entry in timeline.Entries)
            {
                if (entry is null || !string.Equals(entry.Kind, "state_transition", StringComparison.Ordinal))
                {
                    continue;
                }
                var details = entry.Details;
                if (details.ValueKind != JsonValueKind.Object
                    || !details.TryGetProperty("to", out var to)
                    || to.ValueKind != JsonValueKind.String)
                {
                    continue;
                }
                string? from = null;
                if (details.TryGetProperty("from", out var fromElement) && fromElement.ValueKind == JsonValueKind.String)
                {
                    from = fromElement.GetString();
                }
                int? workerId = null;
                if (details.TryGetProperty("workerId", out var worker) && worker.ValueKind == JsonValueKind.Number
                    && worker.TryGetInt32(out var parsed))
                {
                    workerId = parsed;
                }
                transitions.Add(new StageTransition
                {
                    At = entry.OccurredAt,
                    From = from,
                    To = to.GetString() ?? string.Empty,
                    WorkerId = workerId,
                });
                if (transitions.Count >= StageSnapshot.MaxTransitions)
                {
                    break;
                }
            }
        }

        List<StageAuditIteration>? iterations = null;
        if (progress?.Progress is not null)
        {
            iterations = progress.Progress
                .Where(r => r is not null)
                .Take(StageSnapshot.MaxTransitions)
                .Select(r => new StageAuditIteration
                {
                    Iteration = r.Iteration,
                    MaxIterations = r.MaxIterations,
                    Status = r.Status ?? "complete",
                    BlockingFindings = r.BlockingFindings,
                })
                .ToList();
        }

        return new StageSnapshot
        {
            ItemId = item.Id,
            State = item.State ?? string.Empty,
            Transitions = transitions,
            AuditIterations = iterations,
            WorkAgent = string.IsNullOrWhiteSpace(item.WorkAgent) ? item.Agent : item.WorkAgent,
        };
    }
}
