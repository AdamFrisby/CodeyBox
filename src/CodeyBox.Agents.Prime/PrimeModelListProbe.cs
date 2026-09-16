using CodeyBox.Core;

namespace CodeyBox.Agents.Prime;

/// <summary>
/// Model-list probe for prime-agent. Returns the curated
/// <see cref="PrimeKnownModels"/> seed: the live catalog
/// (<c>prime-agent model list</c>) requires an authenticated provider plus
/// network and emits a human-readable table rather than machine-readable
/// ids, so it cannot back a host-side startup probe the way the opencode
/// probe shells out to <c>opencode models</c>. Operator-configured ids
/// absent from the seed surface as a startup warning, not a hard reject
/// (see <see cref="PrimeKnownModels.ValidateModelIdAgainstProviderList"/>).
/// Mirrors the Pi/Aider static-list probes.
/// </summary>
public sealed class PrimeModelListProbe : IAgentModelListProbe
{
    public AgentKind Kind => AgentKind.Prime;

    public Task<AgentModelListResult> GetModelListAsync(CancellationToken ct)
        => Task.FromResult(AgentModelListResult.Success(PrimeKnownModels.All));
}
