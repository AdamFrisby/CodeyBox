namespace CodeyBox.Core;

/// <summary>
/// Agent-facing pipeline phase for prompt preprocessing.
/// </summary>
public readonly record struct AgentPromptPhase(string Value)
{
    public static AgentPromptPhase Work { get; } = new("work");
    public static AgentPromptPhase Rework { get; } = new("rework");
    public static AgentPromptPhase Audit { get; } = new("audit");
    public static AgentPromptPhase Merge { get; } = new("merge");
    public static AgentPromptPhase CheckAndAct { get; } = new("check-and-act");

    public override string ToString() => Value;
}

/// <summary>
/// Ordering bands for built-in and plugin prompt preprocessors.
/// Plugin preprocessors are discovered through the normal plugin loader and
/// sorted within the plugin band by <see cref="IAgentPromptPreprocessor.Order"/>.
/// </summary>
public static class AgentPromptPreprocessorOrder
{
    public const int BuiltInFirst = 0;
    public const int Plugin = 10_000;
    public const int BuiltInLast = 20_000;
}

/// <summary>
/// Context supplied to prompt preprocessors before an agent invocation.
/// </summary>
public sealed record PromptContext(
    WorkItemId ItemId,
    AgentKind AgentKind,
    AgentPromptPhase Phase,
    int Iteration,
    Project Project,
    ISandbox Sandbox);

/// <summary>
/// Transforms an agent prompt immediately before CodeyBox invokes an agent.
/// Implementations must return the prompt that should be passed to the next
/// preprocessor in the chain, or to the agent when this is the last processor.
/// </summary>
public interface IAgentPromptPreprocessor
{
    int Order { get; }

    Task<string> ProcessAsync(PromptContext ctx, string prompt, CancellationToken ct = default);
}
