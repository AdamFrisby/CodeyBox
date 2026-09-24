using CodeyBox.Core;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Agents.Unreal;

/// <summary>
/// Credential viability check for Unreal Labs' unreal-agent.
/// Validates that a pay-per-API key is available (<c>OPENROUTER_API_KEY</c>,
/// <c>OPENAI_API_KEY</c>, <c>FIREWORKS_API_KEY</c>, or <c>UNREAL_HARNESS_LLM_API_KEY</c>)
/// and verifies that Codex subscription credentials are NOT present.
/// </summary>
public sealed class UnrealSmokeProbe : IAgentSmokeProbe
{
    private readonly ILogger<UnrealSmokeProbe>? _log;

    public UnrealSmokeProbe(ILogger<UnrealSmokeProbe>? log = null)
    {
        _log = log;
    }

    public AgentKind Kind => AgentKind.Unreal;

    public Task<AgentSmokeResult> SmokeTestAsync(AgentCredential credential, CancellationToken ct)
    {
        if (UnrealAgentRunner.IsCodexSubscriptionCredential(credential))
        {
            _log?.LogWarning("Unreal smoke probe rejected Codex subscription credentials");
            return Task.FromResult(new AgentSmokeResult(
                Ok: false,
                FailureReason: UnrealAgentRunner.ProhibitedCodexCredentialMarker,
                Duration: TimeSpan.Zero,
                Category: SmokeFailureCategory.Persistent));
        }

        var hasApiKey = credential.EnvironmentVariables.TryGetValue("OPENROUTER_API_KEY", out var routerKey) && !string.IsNullOrWhiteSpace(routerKey)
            || credential.EnvironmentVariables.TryGetValue("OPENAI_API_KEY", out var openaiKey) && !string.IsNullOrWhiteSpace(openaiKey)
            || credential.EnvironmentVariables.TryGetValue("FIREWORKS_API_KEY", out var fireworksKey) && !string.IsNullOrWhiteSpace(fireworksKey)
            || credential.EnvironmentVariables.TryGetValue("UNREAL_HARNESS_LLM_API_KEY", out var harnessKey) && !string.IsNullOrWhiteSpace(harnessKey);

        if (!hasApiKey)
        {
            _log?.LogDebug("Unreal smoke probe found no usable API key in credential bundle");
            return Task.FromResult(new AgentSmokeResult(
                Ok: false,
                FailureReason: UnrealAgentRunner.MissingCredentialMarker,
                Duration: TimeSpan.Zero,
                Category: SmokeFailureCategory.Persistent));
        }

        return Task.FromResult(new AgentSmokeResult(
            Ok: true,
            FailureReason: null,
            Duration: TimeSpan.Zero,
            Category: SmokeFailureCategory.None));
    }
}
