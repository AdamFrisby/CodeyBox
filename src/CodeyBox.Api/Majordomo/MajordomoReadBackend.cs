using CodeyBox.Core;
using CodeyBox.Majordomo;
using CodeyBox.Orchestrator;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Api.Majordomo;

/// <summary>
/// The READ half of the majordomo surface: each method composes the same
/// stores and status services the operator endpoints read from — the MCP
/// layer adds no queries of its own.
/// </summary>
internal sealed class MajordomoReadBackend
{
    private readonly IWorkItemStore _store;
    private readonly IQueueController _queue;
    private readonly OrchestratorService _orchestrator;
    private readonly IAgentRegistry _agents;
    private readonly IAgentDispatchAvailability? _dispatchAvailability;
    private readonly IAgentRunningCounters _runningCounters;
    private readonly AgentConcurrencySnapshot? _concurrency;
    private readonly IReadOnlyList<IAgentQuotaProbe> _subscriptionProbes;
    private readonly AgentClassRouter? _router;
    private readonly IAgentQuotaGate? _quotaGate;
    private readonly IQuotaFailureStore? _quotaFailures;
    private readonly QuotaRouterOptions _quotaOptions;
    private readonly IAuditReportStore? _auditReports;
    private readonly ILogger _log;
    private readonly TimeProvider _time;

    public MajordomoReadBackend(
        IWorkItemStore store,
        IQueueController queue,
        OrchestratorService orchestrator,
        IAgentRegistry agents,
        IAgentRunningCounters runningCounters,
        IEnumerable<IAgentQuotaProbe> quotaProbes,
        QuotaRouterOptions quotaOptions,
        ILoggerFactory loggerFactory,
        IAgentDispatchAvailability? dispatchAvailability = null,
        AgentConcurrencySnapshot? concurrency = null,
        AgentClassRouter? router = null,
        IAgentQuotaGate? quotaGate = null,
        IQuotaFailureStore? quotaFailures = null,
        IAuditReportStore? auditReports = null,
        TimeProvider? time = null)
    {
        _store = store;
        _queue = queue;
        _orchestrator = orchestrator;
        _agents = agents;
        _runningCounters = runningCounters;
        _subscriptionProbes = AgentQuotaProbeCatalog.BuildSubscriptionProbes(quotaProbes);
        _quotaOptions = quotaOptions;
        _dispatchAvailability = dispatchAvailability;
        _concurrency = concurrency;
        _router = router;
        _quotaGate = quotaGate;
        _quotaFailures = quotaFailures;
        _auditReports = auditReports;
        _time = time ?? TimeProvider.System;
        _log = loggerFactory.CreateLogger("MajordomoReadBackend");
    }

    public async Task<QueueStatusResult> GetQueueStatusAsync(CancellationToken ct)
    {
        var counts = new Dictionary<WorkItemState, int>();
        foreach (var state in Enum.GetValues<WorkItemState>())
            counts[state] = await _store.CountByStateAsync(state, ct).ConfigureAwait(false);
        return new QueueStatusResult(_queue.State, _queue.PausedAt, _queue.PausedReason, counts);
    }

    public async Task<DispatchStatusResult> GetDispatchStatusAsync(CancellationToken ct)
    {
        var status = await _orchestrator.GetStatusAsync(ct).ConfigureAwait(false);
        var slots = (status.OccupiedSlots ?? [])
            .Select(s => Guid.TryParse(s.WorkItemId, out var g)
                ? new OccupiedDispatchSlot(s.WorkerIndex, new WorkItemId(g), s.AcquiredAt)
                : null)
            .OfType<OccupiedDispatchSlot>()
            .ToList();
        return new DispatchStatusResult(
            status.MaxConcurrent,
            status.CurrentlyRunning,
            status.QueuedCount,
            status.LastSpawnAt,
            slots);
    }

    /// <summary>
    /// Per-agent rollup of the same signals the operator's /quota and
    /// /concurrency surfaces compose: the dispatch availability verdict, the
    /// running-counters occupancy, the operator concurrency cap, and the
    /// subscription-probe quota readings gated through
    /// <see cref="IAgentQuotaGate"/>.
    /// </summary>
    public async Task<(AgentCapacityResult? Result, MajordomoRefusal? Refusal)> GetAgentCapacityAsync(
        GetAgentCapacityArgs args, CancellationToken ct)
    {
        IReadOnlyList<AgentKind> kinds;
        if (args.Agent is { } agent)
        {
            if (!_agents.TryGet(agent, out _))
                return (null, new MajordomoRefusal(
                    MajordomoRefusalReasons.UnknownAgent,
                    $"unknown agent '{Validation.DescribeUntrustedValue(agent.Value)}'; registered agents: {string.Join(", ", _agents.Available.Select(a => a.Value))}",
                    Field: "agent"));
            kinds = [agent];
        }
        else
        {
            kinds = _agents.Available.ToList();
        }

        var membersByKind = new Dictionary<AgentKind, List<AgentMembership>>();
        if (_router is not null)
        {
            foreach (var entry in _router.SnapshotConfiguredMembers())
            {
                if (entry.Member.Billing != AgentBilling.Subscription)
                    continue;
                if (!membersByKind.TryGetValue(entry.Member.Agent, out var list))
                    membersByKind[entry.Member.Agent] = list = [];
                if (!list.Contains(entry.Member))
                    list.Add(entry.Member);
            }
        }

        var caps = _concurrency?.Current.Members;
        var now = _time.GetUtcNow();
        var entries = new List<AgentCapacityEntry>(kinds.Count);
        foreach (var kind in kinds)
        {
            var availability = _dispatchAvailability?.GetAvailability(kind);
            var inFlight = _runningCounters.GetRunning(kind);
            var maxConcurrent = caps is not null && caps.TryGetValue(kind.Value, out var cap)
                ? cap.MaxConcurrent
                : (int?)null;

            var (quotaExhausted, quotaResetsAt, quotaReason) = await ProbeQuotaAsync(kind, membersByKind, now, ct)
                .ConfigureAwait(false);

            var routable = availability?.Available != false && !quotaExhausted;
            var exclusionReason = availability?.Reason
                ?? quotaReason
                ?? (maxConcurrent is { } mc && inFlight >= mc
                    ? $"at concurrency cap ({inFlight}/{mc})"
                    : null);

            entries.Add(new AgentCapacityEntry(
                kind,
                routable,
                routable ? null : exclusionReason,
                inFlight,
                maxConcurrent,
                quotaExhausted,
                quotaResetsAt));
        }

        return (new AgentCapacityResult(entries), null);
    }

    /// <summary>
    /// The quota verdict for one agent kind: every subscription-serving member
    /// is probed and gated exactly as the /quota endpoint does; the kind is
    /// exhausted only when no member can take work. <c>resetsAt</c> is the
    /// earliest recovery among exhausted members, or null when no member
    /// reports a reset time.
    /// </summary>
    private async Task<(bool Exhausted, DateTimeOffset? ResetsAt, string? Reason)> ProbeQuotaAsync(
        AgentKind kind,
        IReadOnlyDictionary<AgentKind, List<AgentMembership>> membersByKind,
        DateTimeOffset now,
        CancellationToken ct)
    {
        if (_quotaGate is null)
            return (false, null, null);

        var members = membersByKind.TryGetValue(kind, out var m) ? m : [];
        if (members.Count == 0)
        {
            // A kind with no class membership is metered directly when a probe
            // claims it — same fallback the /quota endpoint applies.
            members =
            [
                new AgentMembership
                {
                    Agent = kind,
                    Billing = AgentBilling.Subscription,
                    QualityScore = 100,
                },
            ];
        }

        var anyMetered = false;
        var allBlocked = true;
        DateTimeOffset? earliestReset = null;
        string? reason = null;
        foreach (var member in members)
        {
            var resolution = AgentQuotaProbeCatalog.ResolveSubscriptionProbe(_subscriptionProbes, member, _log);
            if (resolution.Probe is null)
                continue; // unmetered member — quota can never exhaust it
            anyMetered = true;

            AgentQuotaSnapshot snapshot;
            try
            {
                snapshot = await resolution.Probe.GetAvailabilityAsync(member, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Majordomo capacity read: quota probe failed for {Agent}", kind.Value);
                snapshot = AgentQuotaSnapshot.UnknownSnapshot(QuotaUnknownReason.Transient);
            }

            var recentFailure = _quotaFailures is not null
                && await _quotaFailures.HasRecentAsync(
                    member.Agent, member.ModelId, _quotaOptions.ObservedFailureWindow, now, ct).ConfigureAwait(false);
            if (_quotaGate.Allows(member, snapshot, now, recentFailure))
            {
                allBlocked = false;
                break;
            }

            reason = snapshot.IsKnown
                ? $"quota exhausted ({Math.Round(snapshot.AvailablePct, 1)}% remaining)"
                : $"quota unknown ({snapshot.Unknown?.ToString() ?? "no reading"})"
                  + (recentFailure ? "; recent quota failure observed" : string.Empty);
            if (snapshot.ResetAt is { } reset && (earliestReset is null || reset < earliestReset))
                earliestReset = reset;
        }

        if (!anyMetered || !allBlocked)
            return (false, null, null);
        return (true, earliestReset, reason);
    }

    public async Task<ListWorkItemsResult> ListWorkItemsAsync(ListWorkItemsArgs args, CancellationToken ct)
    {
        var items = new List<WorkItem>();
        await foreach (var item in _store.ListAsync(ct).ConfigureAwait(false))
        {
            if (args.ProjectId is { } pid && item.ProjectId != pid)
                continue;
            if (args.States is { } states && !states.Contains(item.State))
                continue;
            items.Add(item);
        }

        var ordered = items.OrderBy(i => i.QueuePosition).ToList();
        return new ListWorkItemsResult(
            ordered.Take(args.Limit).Select(ToSummary).ToList(),
            ordered.Count);
    }

    public async Task<WorkItemDetailResult> GetWorkItemAsync(GetWorkItemArgs args, CancellationToken ct)
    {
        var item = await _store.GetAsync(args.Id, ct).ConfigureAwait(false);
        return new WorkItemDetailResult(item is null ? null : ToDetail(item));
    }

    public async Task<WorkItemAuditResult> GetWorkItemAuditAsync(GetWorkItemAuditArgs args, CancellationToken ct)
    {
        IReadOnlyList<AuditReport> reports = _auditReports is null
            ? []
            : await _auditReports.GetByWorkItemAsync(args.Id.ToString(), ct).ConfigureAwait(false);
        var summaries = reports
            .Where(r => args.Iteration is null || r.Iteration == args.Iteration)
            .Select(r => new AuditReportSummary(
                r.Iteration, r.Target, r.AuditorName, r.WorstSeverity, r.Findings, r.StartedAt, r.EndedAt))
            .ToList();
        return new WorkItemAuditResult(args.Id, summaries);
    }

    private static WorkItemSummary ToSummary(WorkItem item) => new(
        item.Id,
        item.ProjectId,
        item.Title,
        item.State,
        item.Priority,
        item.Agent,
        item.AgentClassId,
        item.FailureKind,
        item.DependsOn,
        item.CreatedAt,
        item.UpdatedAt);

    private static WorkItemDetail ToDetail(WorkItem item) => new(
        item.Id,
        item.ProjectId,
        item.Title,
        item.Prompt,
        item.State,
        item.Priority,
        item.Agent,
        item.AgentClassId,
        item.AuditorProfile,
        item.BaseBranch,
        item.WorkBranch,
        item.DependsOn,
        item.LastError,
        item.FailureKind,
        item.CancellationReason,
        item.QuotaResetAt,
        item.CreatedAt,
        item.UpdatedAt);
}
