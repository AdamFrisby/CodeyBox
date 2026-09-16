using CodeyBox.Core;

namespace CodeyBox.Agents.Aider;

/// <summary>
/// Model-list probe for aider. Returns the curated <see cref="AiderKnownModels"/>
/// seed: aider's live catalog (<c>aider --list-models &lt;query&gt;</c>) needs a
/// partial-match query, prints an interactive onboarding prompt when no model
/// or key is configured, and emits thousands of rows, so it cannot back a
/// host-side startup probe the way the opencode probe shells out to
/// <c>opencode models</c>. Operator-configured ids absent from the seed surface
/// as a startup warning, not a hard reject (see
/// <see cref="AiderKnownModels.ValidateModelIdAgainstProviderList"/>).
/// Mirrors the Pi/Crock/Antigravity static-list probes.
/// </summary>
public sealed class AiderModelListProbe : IAgentModelListProbe
{
    public AgentKind Kind => AgentKind.Aider;

    public Task<AgentModelListResult> GetModelListAsync(CancellationToken ct)
        => Task.FromResult(AgentModelListResult.Success(AiderKnownModels.All));
}
