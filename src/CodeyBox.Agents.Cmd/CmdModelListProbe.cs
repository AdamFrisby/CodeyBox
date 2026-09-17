using CodeyBox.Core;

namespace CodeyBox.Agents.Cmd;

/// <summary>
/// Model-list probe for cmd. Returns the curated
/// <see cref="CmdKnownModels"/> seed: the model catalog is per provider
/// and server-side, so it cannot back a host-side startup probe.
/// Operator-configured ids absent from the seed surface as a startup
/// warning, not a hard reject (see
/// <see cref="CmdKnownModels.ValidateModelIdAgainstProviderList"/>).
/// Mirrors the Omp/Kilo static-list probes. No quota-meter probe is
/// registered: <c>cmd status</c> reports Command Code plan state, not the
/// BYOK provider balance the <c>--local-only</c> runner path spends, so
/// members fall through to the <c>NullQuotaProbe</c> unknown path like
/// omp/goose/kilo.
/// </summary>
public sealed class CmdModelListProbe : IAgentModelListProbe
{
    public AgentKind Kind => AgentKind.Cmd;

    public Task<AgentModelListResult> GetModelListAsync(CancellationToken ct)
        => Task.FromResult(AgentModelListResult.Success(CmdKnownModels.All));
}
