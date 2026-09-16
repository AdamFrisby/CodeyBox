using CodeyBox.Core;

namespace CodeyBox.Agents.Goose;

/// <summary>
/// Model-list probe for goose. Returns the curated
/// <see cref="GooseKnownModels"/> seed: the model catalog is per provider
/// and server-side, so it cannot back a host-side startup probe.
/// Operator-configured ids absent from the seed surface as a startup
/// warning, not a hard reject (see
/// <see cref="GooseKnownModels.ValidateModelIdAgainstProviderList"/>).
/// Mirrors the Pi static-list probe.
/// </summary>
public sealed class GooseModelListProbe : IAgentModelListProbe
{
    public AgentKind Kind => AgentKind.Goose;

    public Task<AgentModelListResult> GetModelListAsync(CancellationToken ct)
        => Task.FromResult(AgentModelListResult.Success(GooseKnownModels.All));
}
