using CodeyBox.Core;

namespace CodeyBox.Agents.Cline;

/// <summary>
/// Model-list probe for cline. Returns the curated
/// <see cref="ClineKnownModels"/> seed: the model catalog is per provider
/// and server-side, so it cannot back a host-side startup probe.
/// Operator-configured ids absent from the seed surface as a startup
/// warning, not a hard reject (see
/// <see cref="ClineKnownModels.ValidateModelIdAgainstProviderList"/>).
/// Mirrors the Goose static-list probe. No quota-meter probe is registered:
/// cline exposes no meterable quota endpoint, so members fall through to
/// the <c>NullQuotaProbe</c> unknown path like pi/goose/autohand.
/// </summary>
public sealed class ClineModelListProbe : IAgentModelListProbe
{
    public AgentKind Kind => AgentKind.Cline;

    public Task<AgentModelListResult> GetModelListAsync(CancellationToken ct)
        => Task.FromResult(AgentModelListResult.Success(ClineKnownModels.All));
}
