using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Resolves <see cref="IAgentSessionSlotGate"/> through a lazy delegate so
/// <see cref="PipelineRunner"/> (a constructor dependency of
/// <see cref="OrchestratorService"/>) can share the orchestrator's per-agent
/// session accounting without the DI container hitting a circular
/// dependency. The delegate is invoked on every call; it should return the
/// cached singleton. Same seam as
/// <see cref="DeferredAgentRunningCounters"/>.
/// </summary>
public sealed class DeferredAgentSessionSlotGate : IAgentSessionSlotGate
{
    private readonly Func<IAgentSessionSlotGate> _resolve;

    public DeferredAgentSessionSlotGate(Func<IAgentSessionSlotGate> resolve)
    {
        _resolve = resolve;
    }

    public bool TryReserve(AgentKind agent) => _resolve().TryReserve(agent);

    public bool TryReserve(AgentMembership member) => _resolve().TryReserve(member);

    public void Release(AgentKind agent) => _resolve().Release(agent);

    public void Release(AgentMembership member) => _resolve().Release(member);

    public bool TryReserveAudit(AgentMembership member) => _resolve().TryReserveAudit(member);

    public bool TryReserveAudit(AgentKind agent) => _resolve().TryReserveAudit(agent);

    public void ReleaseAudit(AgentMembership member) => _resolve().ReleaseAudit(member);

    public void ReleaseAudit(AgentKind agent) => _resolve().ReleaseAudit(agent);

    public Task WaitForAuditSlotAsync(AgentMembership member, CancellationToken ct) =>
        _resolve().WaitForAuditSlotAsync(member, ct);

    public Task WaitForAuditSlotAsync(AgentKind agent, CancellationToken ct) =>
        _resolve().WaitForAuditSlotAsync(agent, ct);

    public bool SuspendWorkSlotForAudit(WorkItemId item) => _resolve().SuspendWorkSlotForAudit(item);

    public Task ResumeWorkSlotAfterAuditAsync(WorkItemId item, CancellationToken ct) =>
        _resolve().ResumeWorkSlotAfterAuditAsync(item, ct);

    public int GetRunningWork(AgentKind agent) => _resolve().GetRunningWork(agent);

    public int GetRunningAudit(AgentKind agent) => _resolve().GetRunningAudit(agent);

    public int GetAuditCap(AgentMembership member) => _resolve().GetAuditCap(member);

    public int GetAuditCap(AgentKind agent) => _resolve().GetAuditCap(agent);
}
