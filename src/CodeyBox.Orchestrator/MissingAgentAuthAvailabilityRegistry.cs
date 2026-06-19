using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

// Legacy tests and small embedders may still construct PipelineRunner directly
// without the production DI graph. Keep the dependency non-null, but fail
// loudly if an auth-required side effect is attempted without the real registry.
internal sealed class MissingAgentAuthAvailabilityRegistry : IAgentAuthAvailabilityRegistry
{
    public static MissingAgentAuthAvailabilityRegistry Instance { get; } = new();

    private MissingAgentAuthAvailabilityRegistry()
    {
    }

    public AvailabilityTransition MarkAuthRequired(AgentKind kind, string reason)
        => throw new InvalidOperationException(
            "IAgentAuthAvailabilityRegistry is not wired; auth-required agent output cannot be benched.");
}
