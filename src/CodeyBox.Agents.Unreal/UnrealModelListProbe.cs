using CodeyBox.Core;

namespace CodeyBox.Agents.Unreal;

/// <summary>
/// Model-list probe for Unreal. Returns the curated <see cref="UnrealKnownModels.All"/> seed.
/// Model discovery in unreal-agent is provider-dependent, so a static seed is served for
/// validation warnings without gating dispatch.
/// </summary>
public sealed class UnrealModelListProbe : IAgentModelListProbe
{
    public AgentKind Kind => AgentKind.Unreal;

    public Task<AgentModelListResult> GetModelListAsync(CancellationToken ct)
        => Task.FromResult(AgentModelListResult.Success(UnrealKnownModels.All));
}
