using CodeyBox.Core;

namespace CodeyBox.Agents.Opencode;

/// <summary>
/// Placeholder model-list probe for opencode. Always returns
/// <see cref="AgentModelListResult.Failed"/>.
///
/// <para>opencode does enumerate available models (e.g. <c>opencode models</c>
/// on the CLI), but neither the CLI output format nor a corresponding HTTP
/// endpoint has been verified against the live subscription tier in this
/// environment. Per <c>feedback-vendor-api-drift</c> the probe ships in the
/// "skip validation, log a warning" shape that <see cref="AgentClassConfigValidator"/>
/// already handles — operators can configure any opencode <c>ModelId</c>
/// they like and confirm correctness by observing the first dispatched
/// item, rather than at startup.</para>
/// </summary>
public sealed class OpencodeModelListProbe : IAgentModelListProbe
{
    public AgentKind Kind => AgentKind.Opencode;

    public Task<AgentModelListResult> GetModelListAsync(CancellationToken ct)
        => Task.FromResult(AgentModelListResult.Failed("no probe shape verified"));
}
