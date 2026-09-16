using CodeyBox.Core;

namespace CodeyBox.Agents.Vibe;

/// <summary>
/// Model-list probe for vibe. Returns the curated <see cref="VibeKnownModels"/>
/// seed: vibe's model catalog is guest-config <c>[[models]]</c> aliases
/// resolved inside the sandbox, so it cannot back a host-side startup probe
/// the way the opencode probe shells out to <c>opencode models</c>.
/// Operator-configured aliases absent from the seed surface as a startup
/// warning, not a hard reject (see
/// <see cref="VibeKnownModels.ValidateModelIdAgainstProviderList"/>).
/// Mirrors the Goose static-list probe.
/// </summary>
public sealed class VibeModelListProbe : IAgentModelListProbe
{
    public AgentKind Kind => AgentKind.Vibe;

    public Task<AgentModelListResult> GetModelListAsync(CancellationToken ct)
        => Task.FromResult(AgentModelListResult.Success(VibeKnownModels.All));
}
