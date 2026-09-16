using CodeyBox.Core;

namespace CodeyBox.Agents.Autohand;

/// <summary>
/// Model-list probe for autohand. Returns the curated
/// <see cref="AutohandKnownModels"/> seed: the model catalog is per provider
/// and server-side, so it cannot back a host-side startup probe.
/// Operator-configured ids absent from the seed surface as a startup
/// warning, not a hard reject (see
/// <see cref="AutohandKnownModels.ValidateModelIdAgainstProviderList"/>).
/// Mirrors the Goose static-list probe. No quota-meter probe is registered:
/// autohand exposes no meterable quota endpoint, so members fall through to
/// the <c>NullQuotaProbe</c> unknown path like pi/goose.
/// </summary>
public sealed class AutohandModelListProbe : IAgentModelListProbe
{
    public AgentKind Kind => AgentKind.Autohand;

    public Task<AgentModelListResult> GetModelListAsync(CancellationToken ct)
        => Task.FromResult(AgentModelListResult.Success(AutohandKnownModels.All));
}
