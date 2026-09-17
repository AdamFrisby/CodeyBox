using CodeyBox.Core;

namespace CodeyBox.Agents.Qwen;

/// <summary>
/// Model-list probe for qwen. Returns the curated
/// <see cref="QwenKnownModels"/> seed: the model catalog is per provider
/// and server-side, so it cannot back a host-side startup probe.
/// Operator-configured ids absent from the seed surface as a startup
/// warning, not a hard reject (see
/// <see cref="QwenKnownModels.ValidateModelIdAgainstProviderList"/>).
/// Mirrors the Omp/Pi static-list probes. No quota-meter probe is
/// registered: no CLI surface reports the env-key path's remaining budget,
/// so members fall through to the <c>NullQuotaProbe</c> unknown path like
/// pi/prime/goose/omp.
/// </summary>
public sealed class QwenModelListProbe : IAgentModelListProbe
{
    public AgentKind Kind => AgentKind.Qwen;

    public Task<AgentModelListResult> GetModelListAsync(CancellationToken ct)
        => Task.FromResult(AgentModelListResult.Success(QwenKnownModels.All));
}
