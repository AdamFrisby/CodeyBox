using CodeyBox.Core;

namespace CodeyBox.Agents.Pi;

/// <summary>
/// Model-list probe for pi. Returns the curated <see cref="PiKnownModels"/>
/// seed: pi's live catalog (<c>pi --list-models</c>) requires an authenticated
/// provider plus network, so it cannot back a host-side startup probe the way
/// the opencode probe shells out to <c>opencode models</c>. Operator-configured
/// ids absent from the seed surface as a startup warning, not a hard reject
/// (see <see cref="PiKnownModels.ValidateModelIdAgainstProviderList"/>).
/// Mirrors the Crock/Antigravity static-list probes.
/// </summary>
public sealed class PiModelListProbe : IAgentModelListProbe
{
    public AgentKind Kind => AgentKind.Pi;

    public Task<AgentModelListResult> GetModelListAsync(CancellationToken ct)
        => Task.FromResult(AgentModelListResult.Success(PiKnownModels.All));
}
