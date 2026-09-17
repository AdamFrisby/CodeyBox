using CodeyBox.Core;

namespace CodeyBox.Agents.Continue;

/// <summary>
/// Model-list probe for Continue. Returns the curated
/// <see cref="ContinueKnownModels"/> seed: the model catalog is per provider
/// and server-side, so it cannot back a host-side startup probe.
/// Operator-configured ids absent from the seed surface as a startup
/// warning, not a hard reject (see
/// <see cref="ContinueKnownModels.ValidateModelIdAgainstProviderList"/>).
/// Mirrors the Kilo/Goose/Autohand static-list probes. No quota-meter probe
/// is registered: <c>cn</c> exposes no meterable quota endpoint, so members
/// fall through to the <c>NullQuotaProbe</c> unknown path like
/// pi/kilo/autohand.
/// </summary>
public sealed class ContinueModelListProbe : IAgentModelListProbe
{
    public AgentKind Kind => AgentKind.Continue;

    public Task<AgentModelListResult> GetModelListAsync(CancellationToken ct)
        => Task.FromResult(AgentModelListResult.Success(ContinueKnownModels.All));
}
