using CodeyBox.Admin.Model;
using CodeyBox.Admin.Web.Models;

namespace CodeyBox.Admin.Web.Services;

/// <summary>
/// Maps the orchestrator's REST DTOs onto the pure admin model at the edge.
/// Pure and total: null-tolerant, bounded, never throws on odd input. This is
/// the only place that knows both shapes, so the map package derives
/// everything from package 2's model instead of re-gathering state.
/// </summary>
public static class FleetMapSnapshotMapper
{
    /// <summary>
    /// Builds the model snapshot the projection layer reasons about.
    /// Only non-terminal items are mapped — the map shows live work, and
    /// terminal history stays on the queue page.
    /// </summary>
    public static FleetSnapshot ToSnapshot(
        IReadOnlyList<WorkItemDto>? items,
        IReadOnlyList<AgentPauseStateDto>? pausedAgents,
        QuotaReportDto? quota,
        ConcurrencyDto? concurrency,
        WorkersStatusDto? workersFallback,
        DateTimeOffset now) =>
        ToSnapshot(items, pausedAgents, quota, concurrency, workersFallback, now, terminal: null);

    /// <summary>
    /// As above, but terminal items are admitted by <paramref name="terminal"/>
    /// (<see cref="TerminalVisibility"/>): settled work stays while something
    /// in flight builds on it or while inside the operator's horizon, and
    /// failures that need a decision always stay. Null keeps the legacy
    /// behaviour of dropping every terminal item.
    /// </summary>
    public static FleetSnapshot ToSnapshot(
        IReadOnlyList<WorkItemDto>? items,
        IReadOnlyList<AgentPauseStateDto>? pausedAgents,
        QuotaReportDto? quota,
        ConcurrencyDto? concurrency,
        WorkersStatusDto? workersFallback,
        DateTimeOffset now,
        TerminalVisibilityOptions? terminal)
    {
        var mapped = new List<AdminWorkItem>();
        if (items is not null)
        {
            foreach (var item in items)
            {
                if (item is null || string.IsNullOrEmpty(item.Id))
                {
                    continue;
                }
                if (terminal is null && ItemStates.IsTerminal(item.State))
                {
                    continue;
                }
                mapped.Add(new AdminWorkItem
                {
                    Id = item.Id,
                    Title = item.Title ?? string.Empty,
                    State = item.State ?? string.Empty,
                    Agent = item.Agent ?? string.Empty,
                    CreatedAt = item.CreatedAt,
                    UpdatedAt = item.UpdatedAt,
                    DependsOn = (item.DependsOn ?? [])
                        .Where(d => !string.IsNullOrEmpty(d))
                        .Distinct(StringComparer.Ordinal)
                        .Take(64)
                        .ToList(),
                    DependsOnSatisfied = item.DependsOnSatisfied,
                    ReleaseId = string.IsNullOrWhiteSpace(item.ReleaseId) ? null : item.ReleaseId,
                    QueuePosition = item.QueuePosition,
                    AttemptCount = Math.Max(item.AuditIterations ?? 0, item.UpstreamPushAttempts),
                });
                if (mapped.Count >= FleetSnapshot.MaxItems)
                {
                    break;
                }
            }
        }

        return new FleetSnapshot
        {
            Now = now,
            Items = terminal is null ? mapped : TerminalVisibility.Filter(mapped, now, terminal),
            Agents = ToAgentStatuses(pausedAgents, quota),
            Workers = ToWorkerCapacity(concurrency, workersFallback),
        };
    }

    private static IReadOnlyList<AdminAgentStatus> ToAgentStatuses(
        IReadOnlyList<AgentPauseStateDto>? pausedAgents,
        QuotaReportDto? quota)
    {
        var byAgent = new Dictionary<string, AdminAgentStatus>(StringComparer.OrdinalIgnoreCase);
        if (pausedAgents is not null)
        {
            foreach (var paused in pausedAgents)
            {
                if (paused is null || string.IsNullOrWhiteSpace(paused.Agent) || !paused.Paused)
                {
                    continue;
                }
                byAgent[paused.Agent] = new AdminAgentStatus
                {
                    Agent = paused.Agent,
                    Paused = true,
                    PauseReason = paused.PausedReason,
                };
            }
        }
        if (quota?.Probes is not null)
        {
            foreach (var probe in quota.Probes)
            {
                var snapshot = probe?.LatestSnapshot;
                if (probe is null || string.IsNullOrWhiteSpace(probe.Agent)
                    || snapshot is null || snapshot.IsKnown == false)
                {
                    continue;
                }
                // Worst instance wins: one exhausted credential benches the agent.
                if (byAgent.TryGetValue(probe.Agent, out var existing)
                    && existing.QuotaAvailablePct.HasValue
                    && existing.QuotaAvailablePct.Value <= snapshot.AvailablePct)
                {
                    continue;
                }
                byAgent[probe.Agent] = (existing ?? new AdminAgentStatus { Agent = probe.Agent }) with
                {
                    QuotaAvailablePct = snapshot.AvailablePct,
                    QuotaResetAt = snapshot.ResetAt,
                };
            }
        }
        return byAgent.Values.ToList();
    }

    private static AdminWorkerCapacity ToWorkerCapacity(
        ConcurrencyDto? concurrency, WorkersStatusDto? fallback)
    {
        if (concurrency is not null)
        {
            return new AdminWorkerCapacity
            {
                GlobalMaxConcurrent = Math.Max(0, concurrency.GlobalMaxConcurrent),
                GlobalRunning = Math.Max(0, concurrency.CurrentlyRunningTotal),
                PerAgentCaps = new Dictionary<string, int>(
                    concurrency.PerAgentCaps ?? new Dictionary<string, int>(),
                    StringComparer.OrdinalIgnoreCase),
                PerAgentRunning = new Dictionary<string, int>(
                    concurrency.CurrentlyRunningPerAgent ?? new Dictionary<string, int>(),
                    StringComparer.OrdinalIgnoreCase),
            };
        }
        if (fallback is not null)
        {
            return new AdminWorkerCapacity
            {
                GlobalMaxConcurrent = Math.Max(0, fallback.MaxConcurrent),
                GlobalRunning = Math.Max(0, fallback.CurrentlyRunning),
            };
        }
        return new AdminWorkerCapacity();
    }
}
