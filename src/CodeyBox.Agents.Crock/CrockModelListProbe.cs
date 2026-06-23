using CodeyBox.Core;

namespace CodeyBox.Agents.Crock;

/// <summary>
/// Supplies the canonical Anthropic Claude model ids CrockCode submits batches
/// for. The CrockCode CLI does not currently expose a <c>crock models</c>
/// command — verified against the public CrockCode README (2026-06) — so the
/// probe returns the static curated set in
/// <see cref="CrockKnownModels.All"/>. The agent-class startup validator
/// consults this probe via the <see cref="IAgentModelListProbe"/> seam so
/// configured <see cref="AgentMembership.ModelId"/> values either validate
/// cleanly or surface as an operator-friendly warning instead of "skipping
/// validation".
///
/// <para>If a future CrockCode release adds a CLI surface for listing the
/// gateway-accepted ids (or Anthropic's <c>/v1/models</c> response gains a
/// batch-eligibility flag we can rely on), swap this implementation for a
/// live read — the static list is acceptable now per the dependent task
/// brief.</para>
/// </summary>
public sealed class CrockModelListProbe : IAgentModelListProbe
{
    public AgentKind Kind => AgentKind.Crock;

    public Task<AgentModelListResult> GetModelListAsync(CancellationToken ct) =>
        Task.FromResult(AgentModelListResult.Success(CrockKnownModels.All));
}
